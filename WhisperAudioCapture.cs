using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Diagnostics;

namespace MusicPlayer
{
    public sealed class WhisperAudioCapture : IDisposable
    {
        private readonly object _gate = new();

        private WaveInEvent? _waveIn;
        private Timer? _exportTimer;

        private readonly List<byte> _pcmBuffer = new();

        private short _latestPeak;
        private DateTime _latestPeakUtc = DateTime.MinValue;

        private DateTime? _manualSegmentStartUtc;
        private DateTime _lastManualExportUtc = DateTime.MinValue;

        private DateTime _lastLoudAudioUtc = DateTime.MinValue;
        private DateTime _lastSpeechEndExportUtc = DateTime.MinValue;

        public event Action<string, bool>? ChunkReady;

        public int SampleRate { get; set; } = 16000;
        public int Channels { get; set; } = 1;

        // Window length: how much recent audio Whisper sees each time.
        public int ChunkMilliseconds { get; set; } = 3500;

        // Step length: how often we export a new overlapping window.
        public int StepMilliseconds { get; set; } = 1250;

        // Keep enough audio for overlap plus a little safety margin.
        public int MaxBufferedMilliseconds { get; set; } = 10000;

        // Cheap "is there enough audio energy to be worth transcribing?" gate.
        public short MinExportPeak { get; set; } = 700;

        // Speech-end auto-export tuning.
        public short SpeechActivityPeakThreshold { get; set; } = 900;
        public int SpeechEndQuietMs { get; set; } = 450;
        public int SpeechEndMinIntervalMs { get; set; } = 1200;

        // Host can decide whether transcription is armed right now.
        public Func<bool>? ShouldExport { get; set; }

        // Manual Ctrl-capture settings.
        public int ManualSegmentPreRollMs { get; set; } = 400;
        public int ManualSegmentMinHoldMs { get; set; } = 180;
        public int ManualSegmentMinGapMs { get; set; } = 700;

        private int BytesPerSecond => SampleRate * Channels * 2; // 16-bit PCM
        private int MaxBufferedBytes => Math.Max(BytesPerSecond, (BytesPerSecond * MaxBufferedMilliseconds) / 1000);
        private int WindowBytes => Math.Max(BytesPerSecond / 2, (BytesPerSecond * ChunkMilliseconds) / 1000);

        public void Start()
        {
            Stop();

            string debugDir = Path.Combine(Path.GetTempPath(), "GMA", "whisper_debug");
            Directory.CreateDirectory(debugDir);

            CleanupOldTempFiles(debugDir);

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, 16, Channels),
                BufferMilliseconds = 100
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();

            int firstDueMs = Math.Max(1600, StepMilliseconds);

            _exportTimer = new Timer(
                _ => ExportCurrentWindow(),
                null,
                firstDueMs,
                StepMilliseconds);
        }

        public void Stop()
        {
            try { _exportTimer?.Dispose(); } catch { }
            _exportTimer = null;

            try { _waveIn?.StopRecording(); } catch { }

            if (_waveIn != null)
            {
                try { _waveIn.DataAvailable -= OnDataAvailable; } catch { }
                try { _waveIn.RecordingStopped -= OnRecordingStopped; } catch { }
                try { _waveIn.Dispose(); } catch { }
                _waveIn = null;
            }

            lock (_gate)
            {
                _pcmBuffer.Clear();
                _latestPeak = 0;
                _latestPeakUtc = DateTime.MinValue;
                _manualSegmentStartUtc = null;
                _lastManualExportUtc = DateTime.MinValue;
                _lastLoudAudioUtc = DateTime.MinValue;
                _lastSpeechEndExportUtc = DateTime.MinValue;
            }
        }

        private static void CleanupOldTempFiles(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;

                var nowUtc = DateTime.UtcNow;

                foreach (var file in Directory.GetFiles(directory))
                {
                    try
                    {
                        var createdUtc = File.GetCreationTimeUtc(file);
                        var age = nowUtc - createdUtc;

                        // Delete files older than 1 hour
                        if (age.TotalHours > 1)
                            File.Delete(file);
                    }
                    catch
                    {
                        // Ignore individual file failures
                    }
                }
            }
            catch
            {
                // Ignore directory-level failures
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            short peak = 0;

            for (int i = 0; i < e.BytesRecorded - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                short abs = (short)Math.Abs(sample);
                if (abs > peak)
                    peak = abs;
            }

            AppendMicDebugLog($"{DateTime.Now:HH:mm:ss.fff} Bytes={e.BytesRecorded}, Peak={peak}");

            DateTime nowUtc = DateTime.UtcNow;

            lock (_gate)
            {
                _pcmBuffer.AddRange(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());

                int overflow = _pcmBuffer.Count - MaxBufferedBytes;
                if (overflow > 0)
                    _pcmBuffer.RemoveRange(0, overflow);

                _latestPeak = peak;
                _latestPeakUtc = nowUtc;

                if (peak >= SpeechActivityPeakThreshold)
                    _lastLoudAudioUtc = nowUtc;
            }

            TrySpeechEndAutoExport();
        }

        private void TrySpeechEndAutoExport()
        {
            bool shouldExport = false;
            DateTime nowUtc = DateTime.UtcNow;

            lock (_gate)
            {
                if (_manualSegmentStartUtc != null)
                    return;

                if (_lastLoudAudioUtc == DateTime.MinValue)
                    return;

                double quietForMs = (nowUtc - _lastLoudAudioUtc).TotalMilliseconds;
                double sinceLastExportMs = (nowUtc - _lastSpeechEndExportUtc).TotalMilliseconds;

                if (quietForMs >= SpeechEndQuietMs &&
                    sinceLastExportMs >= SpeechEndMinIntervalMs)
                {
                    shouldExport = true;
                    _lastSpeechEndExportUtc = nowUtc;
                    _lastLoudAudioUtc = DateTime.MinValue;
                }
            }

            if (shouldExport)
            {
                AppendMicDebugLog($"{DateTime.Now:HH:mm:ss.fff} speechEndAutoExport=true");

                ExportCurrentWindow();
            }
        }

        private void ExportCurrentWindow()
        {
            ExportCurrentWindowCore(
                ignoreShouldExport: false,
                ignoreQuietGate: false,
                minimumBufferDivisor: 3,
                isWarmup: false);
        }

        public void ExportWarmupWindow()
        {
            ExportCurrentWindowCore(
                ignoreShouldExport: true,
                ignoreQuietGate: true,
                minimumBufferDivisor: 3,
                isWarmup: true);
        }

        public void ExportStartupProbeWindow()
        {
            ExportCurrentWindowCore(
                ignoreShouldExport: true,
                ignoreQuietGate: false,
                minimumBufferDivisor: 4,
                isWarmup: false);
        }

        private void ExportCurrentWindowCore(bool ignoreShouldExport, bool ignoreQuietGate, int minimumBufferDivisor, bool isWarmup)
        {
            if (!ignoreShouldExport && ShouldExport != null && !ShouldExport())
                return;

            byte[] snapshot;
            short recentPeak;
            DateTime recentPeakUtc;

            lock (_gate)
            {
                int minimumBytes = Math.Max(WindowBytes / Math.Max(1, minimumBufferDivisor), BytesPerSecond / 4);
                if (_pcmBuffer.Count < minimumBytes)
                    return;

                recentPeak = _latestPeak;
                recentPeakUtc = _latestPeakUtc;

                int take = Math.Min(WindowBytes, _pcmBuffer.Count);
                int start = _pcmBuffer.Count - take;

                snapshot = _pcmBuffer.GetRange(start, take).ToArray();
            }

            if (!ignoreQuietGate)
            {
                // Only skip if VERY quiet AND no recent speech
                if (recentPeak < MinExportPeak &&
                    (DateTime.UtcNow - recentPeakUtc).TotalMilliseconds <= 500)
                {
                    return;
                }
            }

            WriteSnapshotToWavAndRaise(snapshot, isWarmup);
        }

        private bool WriteSnapshotToWavAndRaise(byte[] snapshot, bool isWarmup)
        {
            try
            {
                string debugDir = Path.Combine(Path.GetTempPath(), "GMA", "whisper_debug");
                Directory.CreateDirectory(debugDir);

                string wavPath = Path.Combine(
                    debugDir,
                    $"whisper_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.wav");

                using var writer = new WaveFileWriter(wavPath, new WaveFormat(SampleRate, 16, Channels));
                writer.Write(snapshot, 0, snapshot.Length);

                ChunkReady?.Invoke(wavPath, isWarmup);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void BeginManualSegment()
        {
            lock (_gate)
            {
                _manualSegmentStartUtc ??= DateTime.UtcNow;
            }
        }

        public bool EndManualSegmentAndExport()
        {
            byte[] snapshot;
            DateTime nowUtc = DateTime.UtcNow;

            lock (_gate)
            {
                if (_manualSegmentStartUtc == null)
                    return false;

                DateTime segmentStartUtc = _manualSegmentStartUtc.Value;
                _manualSegmentStartUtc = null;

                if ((nowUtc - segmentStartUtc).TotalMilliseconds < ManualSegmentMinHoldMs)
                    return false;

                if ((nowUtc - _lastManualExportUtc).TotalMilliseconds < ManualSegmentMinGapMs)
                    return false;

                int desiredMs =
                    (int)Math.Ceiling((nowUtc - segmentStartUtc).TotalMilliseconds) + ManualSegmentPreRollMs;

                desiredMs = Math.Clamp(desiredMs, 300, MaxBufferedMilliseconds);

                int bytesToTake = Math.Min((BytesPerSecond * desiredMs) / 1000, _pcmBuffer.Count);
                if (bytesToTake <= 0)
                    return false;

                int start = _pcmBuffer.Count - bytesToTake;
                snapshot = _pcmBuffer.GetRange(start, bytesToTake).ToArray();

                _lastManualExportUtc = nowUtc;
            }

            return WriteSnapshotToWavAndRaise(snapshot, isWarmup: false);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
        }

        [Conditional("DEBUG")]
        private static void AppendMicDebugLog(string line)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "mic_debug.log"),
                    line + Environment.NewLine);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}