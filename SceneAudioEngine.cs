using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using NAudio.Vorbis;

namespace MusicPlayer
{
    /// <summary>
    /// Parallel ambience engine for Scene Mode.
    ///
    /// Design goals:
    /// - Completely isolated from AudioPlayer.cs so music lane crossfade / EOF logic stays untouched.
    /// - Owns its own output and timing; UI should only call Play/Stop/SetVolume.
    /// - Supports up to 3 ambience lanes (logical lanes 1..3; maps to scene UI lanes 2..4).
    ///
    /// Notes:
    /// - This first-pass implementation uses AudioFileReader, so it is best with formats NAudio can open
    ///   directly on the user's machine (commonly mp3 / wav; other formats depend on installed support).
    /// - Each lane loops independently.
    /// - The mixer output is stereo IEEE float at 44.1kHz.
    /// </summary>
    public sealed class SceneAudioEngine : IDisposable
    {
        private const int LaneCount = 3; // ambience-only lanes
        private static readonly WaveFormat MixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        // Track-level loudness normalization for ambience only.
        // One-time analysis at load; no ongoing gain riding.
        // Analyze only the first portion of the track so the logic works
        // across all supported formats without requiring seek support.
        private const float LoudnessAnalysisSeconds = 30.0f;
        private const float TargetRms = 0.025f;
        private const float SilenceFloorRms = 0.0010f;
        private const float SilenceGateSampleAbs = 0.0030f;
        private const float MinNormalizationGain = 0.05f;
        private const float MaxNormalizationGain = 4.00f;
        private const float PeakCeiling = 0.95f;
        private const float MaxEffectiveLaneVolume = 1.75f;

        private readonly object _gate = new();
        private readonly MixingSampleProvider _mixer;
        private IWavePlayer _output;
        private readonly AudioDeviceManager _deviceManager;
        private readonly LaneState[] _lanes = new LaneState[LaneCount];
        private readonly Dictionary<string, float> _normalizationGainCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public SceneAudioEngine()
        {
            _mixer = new MixingSampleProvider(MixerFormat)
            {
                ReadFully = true
            };

            _deviceManager = new AudioDeviceManager();
            _deviceManager.DefaultRenderDeviceChanged += OnDefaultRenderDeviceChanged;

            _output = CreateOutputForCurrentDefaultDevice();
            _output.Init(_mixer);
            _output.Play();
        }

        /// <summary>
        /// Plays or replaces an ambience lane.
        /// laneIndex must be 0..2 and corresponds to ambience lanes only.
        /// </summary>
        public void PlayLane(int laneIndex, string filePath, bool loop = true, float volume = 1.0f)
        {
            if (laneIndex < 0 || laneIndex >= LaneCount)
                throw new ArgumentOutOfRangeException(nameof(laneIndex));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("filePath is empty.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Audio file not found.", filePath);

            // Analyze outside the main lock so file I/O does not block lane operations.
            float normalizationGain = GetOrAnalyzeNormalizationGain(filePath);
            float userVolume = Math.Clamp(volume, 0f, 1f);

            lock (_gate)
            {
                ThrowIfDisposed();

                RemoveLane_NoLock(laneIndex);

                SceneSourceHandle sourceHandle = CreateSceneSource(filePath);
                ISampleProvider source = sourceHandle.SampleProvider;

                if (!source.WaveFormat.Equals(MixerFormat))
                    source = ConvertToMixerFormat(source, MixerFormat);

                ISampleProvider playbackSource;

                if (loop)
                {
                    playbackSource = new LoopingSampleProvider(() =>
                    {
                        sourceHandle.Dispose();
                        sourceHandle = CreateSceneSource(filePath);

                        ISampleProvider newSource = sourceHandle.SampleProvider;

                        if (!newSource.WaveFormat.Equals(MixerFormat))
                            newSource = ConvertToMixerFormat(newSource, MixerFormat);

                        return newSource;
                    });
                }
                else
                {
                    playbackSource = source;
                }

                var volumeProvider = new VolumeSampleProvider(playbackSource)
                {
                    Volume = ComputeEffectiveLaneVolume(userVolume, normalizationGain)
                };

                _lanes[laneIndex] = new LaneState(
                    filePath: filePath,
                    sourceHandle: sourceHandle,
                    volumeProvider: volumeProvider,
                    mixerInput: volumeProvider,
                    userVolume: userVolume,
                    normalizationGain: normalizationGain);

                _mixer.AddMixerInput(volumeProvider);
            }
        }

        public void StopLane(int laneIndex)
        {
            if (laneIndex < 0 || laneIndex >= LaneCount)
                return;

            lock (_gate)
            {
                if (_disposed)
                    return;

                RemoveLane_NoLock(laneIndex);
            }
        }

        public void SetLaneVolume(int laneIndex, float volume)
        {
            if (laneIndex < 0 || laneIndex >= LaneCount)
                return;

            lock (_gate)
            {
                if (_disposed)
                    return;

                var lane = _lanes[laneIndex];
                if (lane == null)
                    return;

                lane.UserVolume = Math.Clamp(volume, 0f, 1f);
                lane.VolumeProvider.Volume = ComputeEffectiveLaneVolume(lane.UserVolume, lane.NormalizationGain);
            }
        }

        public bool IsLaneActive(int laneIndex)
        {
            if (laneIndex < 0 || laneIndex >= LaneCount)
                return false;

            lock (_gate)
            {
                return !_disposed && _lanes[laneIndex] != null;
            }
        }

        public string? GetLanePath(int laneIndex)
        {
            if (laneIndex < 0 || laneIndex >= LaneCount)
                return null;

            lock (_gate)
            {
                return _disposed ? null : _lanes[laneIndex]?.FilePath;
            }
        }

        public void StopAll()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                for (int i = 0; i < LaneCount; i++)
                    RemoveLane_NoLock(i);
            }
        }

        private void RemoveLane_NoLock(int laneIndex)
        {
            var lane = _lanes[laneIndex];
            if (lane == null)
                return;

            try { _mixer.RemoveMixerInput(lane.MixerInput); } catch { }
            try { lane.SourceHandle.Dispose(); } catch { }

            _lanes[laneIndex] = null!;
        }

        private float GetOrAnalyzeNormalizationGain(string filePath)
        {
            lock (_gate)
            {
                if (_normalizationGainCache.TryGetValue(filePath, out float cached))
                    return cached;
            }

            float analyzed = AnalyzeNormalizationGain(filePath);

            lock (_gate)
            {
                if (_normalizationGainCache.TryGetValue(filePath, out float cached))
                    return cached;

                _normalizationGainCache[filePath] = analyzed;
                return analyzed;
            }
        }

        private static float AnalyzeNormalizationGain(string filePath)
        {
            try
            {
                using var sourceHandle = CreateSceneSource(filePath);
                ISampleProvider source = sourceHandle.SampleProvider;

                if (!source.WaveFormat.Equals(MixerFormat))
                    source = ConvertToMixerFormat(source, MixerFormat);

                int samplesToAnalyze = Math.Max(
                    1,
                    (int)(MixerFormat.SampleRate * MixerFormat.Channels * LoudnessAnalysisSeconds));

                float[] buffer = new float[4096];

                double totalSumSquares = 0.0;
                int totalCountedSamples = 0;
                float globalPeak = 0.0f;

                int remaining = samplesToAnalyze;

                while (remaining > 0)
                {
                    int wanted = Math.Min(buffer.Length, remaining);
                    int read = source.Read(buffer, 0, wanted);
                    if (read <= 0)
                        break;

                    for (int i = 0; i < read; i++)
                    {
                        float sample = buffer[i];
                        float abs = Math.Abs(sample);

                        if (abs > globalPeak)
                            globalPeak = abs;

                        // Ignore very tiny samples so silence does not make a track
                        // seem quieter than it feels in practice.
                        if (abs < SilenceGateSampleAbs)
                            continue;

                        totalSumSquares += sample * sample;
                        totalCountedSamples++;
                    }

                    remaining -= read;
                }

                if (totalCountedSamples <= 0)
                    return 1.0f;

                float rms = (float)Math.Sqrt(totalSumSquares / totalCountedSamples);

                if (rms < SilenceFloorRms)
                    return 1.0f;

                float desiredGain = TargetRms / rms;

                // Push quiet tracks up a bit more, and loud tracks down a bit more.
                if (desiredGain > 1.0f)
                    desiredGain *= 1.35f;
                else
                    desiredGain *= 0.75f;

                // Dense sounds like rain tend to feel louder than sparse sounds.
                float crest = globalPeak / Math.Max(rms, 1e-6f);

                float densityPenalty =
                    crest < 2.0f ? 0.50f :
                    crest < 3.0f ? 0.65f :
                    crest < 4.0f ? 0.80f :
                    1.0f;

                desiredGain *= densityPenalty;

                float peakSafeGain = globalPeak > 0.0f
                    ? PeakCeiling / globalPeak
                    : MaxNormalizationGain;

                float gain = Math.Min(desiredGain, peakSafeGain);
                gain = Math.Clamp(gain, MinNormalizationGain, MaxNormalizationGain);

                return gain;
            }
            catch
            {
                // If analysis fails for any reason, fall back to neutral gain.
                return 1.0f;
            }
        }

        private static float ComputeEffectiveLaneVolume(float userVolume, float normalizationGain)
        {
            return Math.Clamp(userVolume * normalizationGain, 0f, MaxEffectiveLaneVolume);
        }

        private static ISampleProvider ConvertToMixerFormat(ISampleProvider source, WaveFormat targetFormat)
        {
            ISampleProvider current = source;

            if (current.WaveFormat.Channels == 1 && targetFormat.Channels == 2)
                current = new MonoToStereoSampleProvider(current);
            else if (current.WaveFormat.Channels == 2 && targetFormat.Channels == 1)
                current = new StereoToMonoSampleProvider(current);
            else if (current.WaveFormat.Channels != targetFormat.Channels)
                throw new InvalidOperationException(
                    $"Unsupported channel conversion: {current.WaveFormat.Channels} -> {targetFormat.Channels}");

            if (current.WaveFormat.SampleRate != targetFormat.SampleRate)
                current = new WdlResamplingSampleProvider(current, targetFormat.SampleRate);

            return current;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SceneAudioEngine));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                for (int i = 0; i < LaneCount; i++)
                    RemoveLane_NoLock(i);

                try { _output.Stop(); } catch { }
                try { _output.Dispose(); } catch { }
                try { _deviceManager.Dispose(); } catch { }

                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private sealed class SceneSourceHandle : IDisposable
        {
            public SceneSourceHandle(ISampleProvider sampleProvider, IDisposable disposable)
            {
                SampleProvider = sampleProvider;
                _disposable = disposable;
            }

            private readonly IDisposable _disposable;
            public ISampleProvider SampleProvider { get; }

            public void Dispose()
            {
                try { _disposable.Dispose(); } catch { }
            }
        }

        private static SceneSourceHandle CreateSceneSource(string filePath)
        {
            string ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";

            if (ext == ".ogg")
            {
                // Requires NAudio.Vorbis
                var vorbis = new VorbisWaveReader(filePath);
                return new SceneSourceHandle(vorbis.ToSampleProvider(), vorbis);
            }

            var reader = new AudioFileReader(filePath);
            return new SceneSourceHandle(reader, reader);
        }

        private IWavePlayer CreateOutputForCurrentDefaultDevice()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                return new WasapiOut(dev, AudioClientShareMode.Shared, false, 250);
            }
            catch
            {
                return new WaveOutEvent
                {
                    DesiredLatency = 400,
                    NumberOfBuffers = 8
                };
            }
        }

        private void OnDefaultRenderDeviceChanged(string defaultDeviceId)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                try
                {
                    var newOutput = CreateOutputForCurrentDefaultDevice();
                    newOutput.Init(_mixer);
                    newOutput.Play();

                    var oldOutput = _output;
                    _output = newOutput;

                    try { oldOutput.Stop(); } catch { }
                    try { oldOutput.Dispose(); } catch { }
                }
                catch
                {
                    // Keep the current output alive if rebind fails.
                }
            }
        }

        private sealed class LaneState
        {
            public LaneState(
                string filePath,
                IDisposable sourceHandle,
                VolumeSampleProvider volumeProvider,
                ISampleProvider mixerInput,
                float userVolume,
                float normalizationGain)
            {
                FilePath = filePath;
                SourceHandle = sourceHandle;
                VolumeProvider = volumeProvider;
                MixerInput = mixerInput;
                UserVolume = userVolume;
                NormalizationGain = normalizationGain;
            }

            public string FilePath { get; }
            public IDisposable SourceHandle { get; }
            public VolumeSampleProvider VolumeProvider { get; }
            public ISampleProvider MixerInput { get; }
            public float UserVolume { get; set; }
            public float NormalizationGain { get; }
        }

        private sealed class LoopingSampleProvider : ISampleProvider
        {
            private readonly Func<ISampleProvider> _createSource;
            private ISampleProvider _current;

            public LoopingSampleProvider(Func<ISampleProvider> createSource)
            {
                _createSource = createSource;
                _current = createSource();
            }

            public WaveFormat WaveFormat => _current.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int totalRead = 0;

                while (totalRead < count)
                {
                    int read = _current.Read(buffer, offset + totalRead, count - totalRead);
                    if (read > 0)
                    {
                        totalRead += read;
                        continue;
                    }

                    // recreate source on loop
                    _current = _createSource();

                    read = _current.Read(buffer, offset + totalRead, count - totalRead);
                    if (read <= 0)
                        break;

                    totalRead += read;
                }

                return totalRead;
            }
        }
    }
}
