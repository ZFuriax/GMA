//AudioPlayer.cs
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Dsp;

namespace MusicPlayer
{
    public sealed class AudioPlayer : IDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _pipelineSem = new(1, 1);

        // UI can subscribe to this to feed RainVisualizerOverlay.SetBands(...)
        public event Action<float[]>? SpectrumAvailable;

        // Lightweight FFT analyzer fed from the stdout pump - Updated to 2048 fft.
        private readonly SpectrumAnalyzer _spectrum = new SpectrumAnalyzer(
        fftSize: 2048,   // power of two
        publishFps: 120   // ~30 updates/sec
        );

        private IWavePlayer? _output;                 // WasapiOut or WaveOutEvent
        private Process? _ffmpeg;
        private BufferedWaveProvider? _buffer;
        private VolumeWaveProvider16? _volumeProvider;
        private DynamicsWaveProvider16? _dynamicsProvider;
        private SpectrumTapWaveProvider? _spectrumTap;

        // ---- Crossfade (v1) ----
        private BufferedWaveProvider? _nextBuffer;
        private Process? _nextFfmpeg;
        private Task? _nextPumpTask;
        private Task? _nextStderrTask;
        private const int CrossfadeMs = 2500; // tweak by ear

        private string? _preparedCrossfadeFile;
        private TimeSpan _preparedCrossfadeStartOffset = TimeSpan.Zero;
        private bool _preparedCrossfadeReady;

        private MMDeviceEnumerator? _deviceEnumerator;
        private string? _currentOutputDeviceId;
        private AudioDeviceManager? _audioDeviceManager;
        private DateTime _lastDefaultDeviceSwitchUtc = DateTime.MinValue;
        private int _defaultDeviceSwitchInFlight = 0;

        private DynamicsWaveProvider16? _nextDynamicsProvider;

        // Mixer sits between dynamics and volume
        private CrossfadeMixerWaveProvider16? _xfadeMixer;

        // ✅ Scene lane storage
        private BufferedWaveProvider?[] _sceneBuffers = new BufferedWaveProvider?[4];
        private Process?[] _sceneFfmpeg = new Process?[4];

        // Prevent spurious “ffmpeg ended unexpectedly” during intentional swaps
        private volatile bool _suppressFfmpegExitError;

        // Track-local position base (so crossfade can reset “track time” cleanly)
        private long _trackStartPlayedBytes;

        // Counts bytes actually consumed by the output device (works for WASAPI too)
        private CountingWaveProvider? _countingProvider;
        private long _playedBytes;

        private CancellationTokenSource? _cts;
        private Task? _pumpTask;
        private Task? _stderrTask;
        private Task? _eofMonitorTask;
        private Task? _watchdogTask;
        private EventHandler<StoppedEventArgs>? _playbackStoppedHandler;

        private string? _currentFile;
        private static readonly WaveFormat PcmFormat = new WaveFormat(44100, 16, 2);

        private float _volume = 0.8f;

        // When we seek, we restart decoding at an offset. Output position restarts at 0,
        // so we add this base to keep UI position correct.
        private TimeSpan _seekBase = TimeSpan.Zero;

        private bool _normalizeRequested = false;

        // Keep the UI name "NormalizeEnabled" so MainWindow.xaml.cs doesn't need to change.
        // Internally, this is your "Night Mode" dynamics toggle.
        public bool NormalizeEnabled
        {
            get => _normalizeRequested;
            set
            {
                _normalizeRequested = value;
                if (_dynamicsProvider != null)
                {
                    _dynamicsProvider.Enabled = value;
                    if (value) _dynamicsProvider.Reset();
                }
            }
        }

        public event Action<string?>? TrackChanged;
        public event Action? PlaybackEnded;
        public event Action<string>? PlaybackFailed;

        public string? CurrentFile => _currentFile;
        public TimeSpan? Duration { get; private set; }

        public PlaybackState PlaybackState => _output?.PlaybackState ?? PlaybackState.Stopped;

        public int CurrentGeneration
        {
            get
            {
                lock (_gate)
                    return _playbackGeneration;
            }
        }

        // Bytes decoded from ffmpeg stdout (PCM)
        private long _decodedBytes;

        private volatile bool _ffmpegReachedEof;

        // Generation/token for the currently active logical playback pipeline.
        // Any async callback from an older generation must be ignored.
        private int _playbackGeneration = 0;

        // Intentional-stop state must be scoped to the generation that requested it.
        private int _intentionalStopGeneration = -1;

        // True when the user expects playback to continue (Play pressed, not paused/stopped)
        private volatile bool _wantPlaying;

        // ---- Crossfade (v1 end-of-song only) ----
        private readonly TimeSpan _crossfadeDuration = TimeSpan.FromSeconds(3.0);

        // ✅ Ensure PlaybackEnded fires at most once per pipeline
        private int _endedRaised = 0;

        public AudioPlayer()
        {
            _deviceEnumerator = new MMDeviceEnumerator();

            try
            {
                var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _currentOutputDeviceId = device.ID;
            }
            catch
            {
                _currentOutputDeviceId = null;
            }

            _audioDeviceManager = new AudioDeviceManager();
            _audioDeviceManager.DefaultRenderDeviceChanged += AudioDeviceManager_DefaultRenderDeviceChanged;
        }

        private void RaisePlaybackEndedOnce()
        {
            if (Interlocked.Exchange(ref _endedRaised, 1) == 0)
            {
                PlaybackEnded?.Invoke();
            }
        }

        public TimeSpan Position
        {
            get
            {
                long played = Interlocked.Read(ref _playedBytes);
                long rel = played - Interlocked.Read(ref _trackStartPlayedBytes);
                if (rel < 0) rel = 0;

                double seconds = rel / (double)PcmFormat.AverageBytesPerSecond;
                if (seconds < 0) seconds = 0;
                return _seekBase + TimeSpan.FromSeconds(seconds);
            }
        }

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                lock (_gate)
                {
                    if (_volumeProvider != null)
                        _volumeProvider.Volume = _volume;
                }
            }
        }

        public void Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("filePath is empty.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Audio file not found.", filePath);

            bool isSameFile;
            lock (_gate)
            {
                isSameFile = string.Equals(_currentFile, filePath, StringComparison.OrdinalIgnoreCase);
            }

            if (!isSameFile)
            {
                Stop(preserveSeekBase: true, reason: "Load:SwitchFile");
            }
            else
            {
                AppendPlaybackEngineLog($"Load SKIP Stop (same file) | file=\"{filePath}\"");
            }

            lock (_gate)
            {
                _playbackGeneration++;
                _intentionalStopGeneration = -1;
                _currentFile = filePath;
            }

            Duration = ProbeDuration(filePath);
            _seekBase = TimeSpan.Zero;

            Interlocked.Exchange(ref _endedRaised, 0);

            TrackChanged?.Invoke(_currentFile);
        }

        public void Play(string? reason = null)
        {
            AppendPlaybackEngineLog(
                $"Play CALL | reason={reason ?? "null"} | " +
                BuildPlaybackStateSnapshot(""));

            string? file;
            lock (_gate) file = _currentFile;

            if (file == null)
                throw new InvalidOperationException("No file loaded. Call Load() first.");

            lock (_gate)
            {
                _wantPlaying = true;
                _intentionalStopGeneration = -1;
            }

            if (_output?.PlaybackState == PlaybackState.Playing)
                return;

            if (_output?.PlaybackState == PlaybackState.Paused)
            {
                try { _output.Play(); } catch { }
                return;
            }

            _ = Task.Run(async () =>
            {
                await _pipelineSem.WaitAsync().ConfigureAwait(false);
                try
                {
                    string? latestFile;
                    TimeSpan latestSeekBase;

                    lock (_gate)
                    {
                        latestFile = _currentFile;
                        latestSeekBase = _seekBase;
                    }

                    if (string.IsNullOrWhiteSpace(latestFile))
                        return;

                    StartPipeline(latestFile, startOffset: latestSeekBase, startPlaying: true);
                }
                finally
                {
                    _pipelineSem.Release();
                }
            });
        }

        [Conditional("PLAYBACK_ENGINE_LOG")]
        private void AppendPlaybackEngineLog(string message)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "playback_engine_log.txt");
                string line = $"{DateTime.Now:HH:mm:ss.fff} | {message}";
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Never let logging crash playback.
            }
        }

        private string BuildPlaybackStateSnapshot(string prefix = "")
        {
            try
            {
                string? currentFile;
                bool intentionalStop;
                int playbackGeneration;
                int intentionalStopGeneration;
                bool wantPlaying;
                bool reachedEof;
                bool ffmpegAlive;
                int buffered;
                PlaybackState playbackState;
                TimeSpan pos;
                TimeSpan? dur;
                TimeSpan seekBase;

                lock (_gate)
                {
                    currentFile = _currentFile;
                    playbackGeneration = _playbackGeneration;
                    intentionalStopGeneration = _intentionalStopGeneration;
                    intentionalStop = (_intentionalStopGeneration == _playbackGeneration);
                    wantPlaying = _wantPlaying;
                    reachedEof = _ffmpegReachedEof;
                    ffmpegAlive = _ffmpeg != null && !_ffmpeg.HasExited;
                    buffered = _buffer?.BufferedBytes ?? 0;
                    playbackState = _output?.PlaybackState ?? PlaybackState.Stopped;
                    seekBase = _seekBase;
                }

                pos = Position;
                dur = Duration;

                return
                    $"{prefix}" +
                    $"file=\"{currentFile}\" | " +
                    $"pos={pos} | " +
                    $"dur={(dur.HasValue ? dur.Value.ToString() : "null")} | " +
                    $"seekBase={seekBase} | " +
                    $"wantPlaying={wantPlaying} | " +
                    $"generation={playbackGeneration} | " +
                    $"intentionalStopGeneration={intentionalStopGeneration} | " +
                    $"intentionalStop={intentionalStop} | " +
                    $"ffmpegReachedEof={reachedEof} | " +
                    $"ffmpegAlive={ffmpegAlive} | " +
                    $"buffered={buffered} | " +
                    $"playbackState={playbackState}";
            }
            catch (Exception ex)
            {
                return $"{prefix}snapshot_failed={ex.Message}";
            }
        }

        private void EnsureOutputPlayingAfterCommit(string reason)
        {
            IWavePlayer? outputToResume = null;
            string? fileForLog;
            int generationForLog;

            lock (_gate)
            {
                fileForLog = _currentFile;
                generationForLog = _playbackGeneration;

                if (_wantPlaying &&
                    _output != null &&
                    _output.PlaybackState == PlaybackState.Stopped)
                {
                    outputToResume = _output;
                }
            }

            if (outputToResume == null)
                return;

            try
            {
                AppendPlaybackEngineLog(
                    $"EnsureOutputPlayingAfterCommit BEGIN | reason={reason} | file=\"{fileForLog}\" | generation={generationForLog}");

                outputToResume.Play();

                AppendPlaybackEngineLog(
                    $"EnsureOutputPlayingAfterCommit END | reason={reason} | file=\"{fileForLog}\" | generation={generationForLog} | playbackState={outputToResume.PlaybackState}");
            }
            catch (Exception ex)
            {
                AppendPlaybackEngineLog(
                    $"EnsureOutputPlayingAfterCommit FAILED | reason={reason} | file=\"{fileForLog}\" | generation={generationForLog} | {ex}");
            }
        }

        private void Output_PlaybackStopped(
            object? sender,
            StoppedEventArgs args,
            string fileForThisPipeline,
            IWavePlayer outputForThisPipeline,
            int generationForThisPipeline)
        {
            bool isCurrentOutput;
            bool isCurrentGeneration;
            bool intentionalStop;
            int buffered = 0;
            bool wantPlaying;
            bool reachedEof;
            bool ffmpegAlive;
            int currentGeneration;

            lock (_gate)
            {
                currentGeneration = _playbackGeneration;
                isCurrentOutput = ReferenceEquals(_output, outputForThisPipeline);
                isCurrentGeneration = (generationForThisPipeline == _playbackGeneration);
                intentionalStop = (_intentionalStopGeneration == generationForThisPipeline);
                wantPlaying = _wantPlaying;
                reachedEof = _ffmpegReachedEof;
                ffmpegAlive = _ffmpeg != null && !_ffmpeg.HasExited;

                try { buffered = _buffer?.BufferedBytes ?? 0; } catch { buffered = 0; }
            }

            AppendPlaybackEngineLog(
                $"Output_PlaybackStopped ENTER | " +
                $"eventFile=\"{fileForThisPipeline}\" | " +
                $"eventGeneration={generationForThisPipeline} | " +
                $"currentGeneration={currentGeneration} | " +
                $"isCurrentOutput={isCurrentOutput} | " +
                $"isCurrentGeneration={isCurrentGeneration} | " +
                BuildPlaybackStateSnapshot(""));

            if (!isCurrentOutput || !isCurrentGeneration)
            {
                AppendPlaybackEngineLog(
                    $"Output_PlaybackStopped EXIT stale-pipeline | " +
                    $"eventFile=\"{fileForThisPipeline}\" | " +
                    $"eventGeneration={generationForThisPipeline} | " +
                    $"currentGeneration={currentGeneration} | " +
                    $"isCurrentOutput={isCurrentOutput} | " +
                    $"isCurrentGeneration={isCurrentGeneration} | " +
                    BuildPlaybackStateSnapshot(""));
                return;
            }

            if (intentionalStop)
            {
                AppendPlaybackEngineLog(
                    $"Output_PlaybackStopped EXIT intentionalStop | " +
                    $"eventFile=\"{fileForThisPipeline}\" | " +
                    $"isCurrentOutput={isCurrentOutput} | " +
                    BuildPlaybackStateSnapshot(""));
                return;
            }

            if (args.Exception != null)
            {
                AppendPlaybackEngineLog(
                    $"Output_PlaybackStopped EXCEPTION | " +
                    $"eventFile=\"{fileForThisPipeline}\" | " +
                    $"isCurrentOutput={isCurrentOutput} | " +
                    args.Exception);

                if (args.Exception is System.Runtime.InteropServices.COMException comEx)
                {
                    TimeSpan resumePos = Position;
                    bool shouldResume;

                    lock (_gate)
                        shouldResume = _wantPlaying;

                    AppendPlaybackEngineLog(
                        $"Output_PlaybackStopped DEVICE_LOST_RECOVERY | " +
                        $"eventFile=\"{fileForThisPipeline}\" | " +
                        $"resumePos={resumePos} | " +
                        $"shouldResume={shouldResume} | " +
                        $"hresult=0x{comEx.HResult:X8}");

                    _ = Task.Run(async () =>
                    {
                        await _pipelineSem.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            StartPipeline(fileForThisPipeline, startOffset: resumePos, startPlaying: shouldResume);
                        }
                        catch (Exception ex)
                        {
                            PlaybackFailed?.Invoke(
                                "Audio output stopped with error and recovery failed:\n\n" + ex);
                        }
                        finally
                        {
                            _pipelineSem.Release();
                        }
                    });

                    return;
                }

                PlaybackFailed?.Invoke("Audio output stopped with error:\n\n" + args.Exception);
                return;
            }

            if (wantPlaying && !reachedEof && ffmpegAlive && buffered > 0)
            {
                AppendPlaybackEngineLog(
                    $"Output_PlaybackStopped RESUME Play() | " +
                    $"eventFile=\"{fileForThisPipeline}\" | " +
                    $"isCurrentOutput={isCurrentOutput} | " +
                    BuildPlaybackStateSnapshot(""));

                try { outputForThisPipeline.Play(); } catch { }
                return;
            }

            AppendPlaybackEngineLog(
                $"Output_PlaybackStopped EXIT no-resume | " +
                $"eventFile=\"{fileForThisPipeline}\" | " +
                $"isCurrentOutput={isCurrentOutput} | " +
                BuildPlaybackStateSnapshot(""));
        }

        private void RebindPlaybackStoppedHandler(
            IWavePlayer output,
            string fileForThisPipeline,
            int generationForThisPipeline)
        {
            if (_playbackStoppedHandler != null)
            {
                try { output.PlaybackStopped -= _playbackStoppedHandler; } catch { }
            }

            _playbackStoppedHandler = (s, e) =>
                Output_PlaybackStopped(
                    s,
                    e,
                    fileForThisPipeline,
                    output,
                    generationForThisPipeline);

            output.PlaybackStopped += _playbackStoppedHandler;
        }

        private void ClearPreparedCrossfadeState()
        {
            _preparedCrossfadeFile = null;
            _preparedCrossfadeStartOffset = TimeSpan.Zero;
            _preparedCrossfadeReady = false;
        }

        private void CleanupPreparedNextPipeline()
        {
            Process? nextFfmpegToDispose = null;

            lock (_gate)
            {
                nextFfmpegToDispose = _nextFfmpeg;

                _nextFfmpeg = null;
                _nextBuffer = null;
                _nextDynamicsProvider = null;
                _nextPumpTask = null;
                _nextStderrTask = null;

                ClearPreparedCrossfadeState();
            }

            try
            {
                if (nextFfmpegToDispose != null && !nextFfmpegToDispose.HasExited)
                    nextFfmpegToDispose.Kill(entireProcessTree: true);
            }
            catch { }

            try { nextFfmpegToDispose?.Dispose(); } catch { }
        }

        public bool TryPrepareCrossfadeTo(string nextFilePath)
        {
            return BeginCrossfadeInternal(
                nextFilePath: nextFilePath,
                startOffset: TimeSpan.Zero,
                fadeMs: CrossfadeMs,
                isSameTrackLoop: false,
                prepareOnly: true);
        }

        public bool TryCommitPreparedCrossfadeTo(string nextFilePath, int fadeMs = CrossfadeMs)
        {
            if (string.IsNullOrWhiteSpace(nextFilePath))
                return false;

            CancellationToken ct;
            TimeSpan startOffset;

            lock (_gate)
            {
                if (!_preparedCrossfadeReady ||
                    _nextBuffer == null ||
                    _nextDynamicsProvider == null ||
                    _xfadeMixer == null ||
                    _cts == null ||
                    _output?.PlaybackState != PlaybackState.Playing ||
                    !string.Equals(_preparedCrossfadeFile, nextFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                startOffset = _preparedCrossfadeStartOffset;
                ct = _cts.Token;

                _xfadeMixer.SetNext(_nextDynamicsProvider);
                _xfadeMixer.BeginCrossfade(TimeSpan.FromMilliseconds(Math.Clamp(fadeMs, 120, 4000)));

                ClearPreparedCrossfadeState();
            }

            ScheduleCrossfadeCommit(nextFilePath, startOffset, fadeMs, ct);
            return true;
        }

        private void ScheduleCrossfadeCommit(string nextFilePath, TimeSpan startOffset, int fadeMs, CancellationToken ct)
        {
            // Probe next duration BEFORE the commit boundary so the audible handoff
            // does not wait on ffprobe/ffmpeg metadata work.
            TimeSpan? nextDuration = null;
            try
            {
                nextDuration = ProbeDuration(nextFilePath);
            }
            catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(fadeMs + 30, ct);
                    if (ct.IsCancellationRequested) return;

                    string? newCurrentFile;
                    Process? oldFfmpegToDispose = null;

                    // Swap pointers FAST under the lock
                    lock (_gate)
                    {
                        _suppressFfmpegExitError = true;
                        try
                        {
                            oldFfmpegToDispose = _ffmpeg;

                            _ffmpeg = _nextFfmpeg;
                            _buffer = _nextBuffer;
                            _dynamicsProvider = _nextDynamicsProvider;

                            _nextFfmpeg = null;
                            _nextBuffer = null;
                            _nextDynamicsProvider = null;
                            _nextPumpTask = null;
                            _nextStderrTask = null;

                            _ffmpegReachedEof = false;
                            _decodedBytes = 0;

                            _currentFile = nextFilePath;
                            Duration = nextDuration;

                            _seekBase = startOffset + TimeSpan.FromMilliseconds(fadeMs);
                            _trackStartPlayedBytes = Interlocked.Read(ref _playedBytes);

                            // Crossfade commit is a new logical playback generation.
                            _playbackGeneration++;
                            _intentionalStopGeneration = -1;

                            Interlocked.Exchange(ref _endedRaised, 0);

                            _xfadeMixer?.CommitNextAsCurrent();

                            newCurrentFile = _currentFile;
                        }
                        finally
                        {
                            _suppressFfmpegExitError = false;
                        }
                    }

                    Process? committedProc = null;
                    BufferedWaveProvider? committedBuffer = null;
                    IWavePlayer? committedOutput = null;
                    int committedGeneration = -1;
                    string? committedFile = null;

                    lock (_gate)
                    {
                        committedProc = _ffmpeg;
                        committedBuffer = _buffer;
                        committedOutput = _output;
                        committedGeneration = _playbackGeneration;
                        committedFile = _currentFile;
                    }

                    AppendPlaybackEngineLog(
                        $"Crossfade COMMIT | file=\"{newCurrentFile}\" | generation={CurrentGeneration}");

                    if (committedOutput != null && !string.IsNullOrWhiteSpace(committedFile))
                    {
                        RebindPlaybackStoppedHandler(
                            committedOutput,
                            committedFile,
                            committedGeneration);
                    }

                    if (committedProc != null &&
                        committedBuffer != null &&
                        committedOutput != null)
                    {
                        _eofMonitorTask = StartEofMonitor(
                            committedProc,
                            committedBuffer,
                            committedOutput,
                            committedGeneration,
                            ct);

                        _watchdogTask = StartWatchdogMonitor(
                            committedOutput,
                            committedGeneration,
                            ct);
                    }

                    EnsureOutputPlayingAfterCommit("CrossfadeCommit");

                    try
                    {
                        if (oldFfmpegToDispose != null && !oldFfmpegToDispose.HasExited)
                            oldFfmpegToDispose.Kill(entireProcessTree: true);
                    }
                    catch { }

                    try { oldFfmpegToDispose?.Dispose(); } catch { }

                    TrackChanged?.Invoke(newCurrentFile);
                }
                catch (OperationCanceledException) { }
                catch { }
            }, ct);
        }

        public void BeginCrossfadeTo(string nextFilePath)
        {
            // Back-compat wrapper: keep existing callers working
            TryBeginCrossfadeTo(nextFilePath);
        }

        public bool TryBeginCrossfadeTo(string nextFilePath)
        {
            CleanupPreparedNextPipeline();

            return BeginCrossfadeInternal(
                nextFilePath: nextFilePath,
                startOffset: TimeSpan.Zero,
                fadeMs: CrossfadeMs,
                isSameTrackLoop: false,
                prepareOnly: false);
        }

        public void BeginCrossfadeLoopToFraction(double aFraction, int fadeMs = 1000)
        {
            if (aFraction < 0) aFraction = 0;
            if (aFraction > 1) aFraction = 1;

            if (!Duration.HasValue || Duration.Value.TotalSeconds <= 0.01)
                return;

            string? file;
            lock (_gate) file = _currentFile;

            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                return;

            var loopStart = TimeSpan.FromSeconds(Duration.Value.TotalSeconds * aFraction);

            BeginLoopCrossfadeTo(loopStart, fadeMs);
        }

        private bool BeginCrossfadeInternal(string nextFilePath, TimeSpan startOffset, int fadeMs, bool isSameTrackLoop, bool prepareOnly = false)
        {
            if (string.IsNullOrWhiteSpace(nextFilePath))
                return false;

            if (!File.Exists(nextFilePath))
            {
                PlaybackFailed?.Invoke("Next song file is missing.\n\nCheck the file path and re-add the song.");
                return false;
            }

            // Defensive guard:
            // Do not allow a normal crossfade into the same file.
            // The only valid same-file case is an intentional loop crossfade.
            string? currentFile;
            lock (_gate) currentFile = _currentFile;

            if (!isSameTrackLoop &&
                !string.IsNullOrWhiteSpace(currentFile) &&
                string.Equals(currentFile, nextFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Only crossfade if we are actively playing and have a mixer online
            if (_output?.PlaybackState != PlaybackState.Playing || _xfadeMixer == null || _cts == null)
                return false;

            lock (_gate)
            {
                if (_nextFfmpeg != null || _nextBuffer != null)
                    return false;
            }

            var ffmpegPath = ResolveFfmpegPath();
            if (ffmpegPath == null)
            {
                PlaybackFailed?.Invoke("ffmpeg.exe not found. Put it next to the app executable.");
                return false;
            }

            fadeMs = Math.Clamp(fadeMs, 120, 4000);

            CancellationToken ct = _cts.Token;

            _nextBuffer = new BufferedWaveProvider(PcmFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(60),
                ReadFully = true,
                DiscardOnBufferOverflow = true
            };

            // Tap RAW audio for the prepared next pipeline too, so visualizer survives track transitions
            var nextSpectrumTap = new SpectrumTapWaveProvider(_nextBuffer, _spectrum, bands =>
            {
                SpectrumAvailable?.Invoke(bands);
            });

            _nextDynamicsProvider = new DynamicsWaveProvider16(nextSpectrumTap, enabled: _normalizeRequested);
            if (_normalizeRequested) _nextDynamicsProvider.Reset();

            var stderrTail = new StringBuilder(capacity: 4096);
            void AppendStderr(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                if (stderrTail.Length > 16384)
                    stderrTail.Remove(0, stderrTail.Length - 8192);
                stderrTail.Append(s);
            }

            // Start decode (note: startOffset is used here)
            try
            {
                _nextFfmpeg = StartFfmpegDecodeToBuffer(
                    file: nextFilePath,
                    startOffset: startOffset,
                    ffmpegPath: ffmpegPath,
                    buffer: _nextBuffer,
                    stderrTail: stderrTail,
                    appendStderr: AppendStderr,
                    ct: ct,
                    out _nextStderrTask,
                    out _nextPumpTask);
            }
            catch (Exception ex)
            {
                PlaybackFailed?.Invoke("Failed to start ffmpeg for next track.\n\n" + ex);
                return false;
            }

            int requiredMs = fadeMs + 2000;
            bool ready = WaitForCrossfadeReadyAsync(_nextBuffer, requiredMs, ct)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();

            if (!ready)
            {
                CleanupPreparedNextPipeline();
                return false;
            }

            lock (_gate)
            {
                _preparedCrossfadeFile = nextFilePath;
                _preparedCrossfadeStartOffset = startOffset;
                _preparedCrossfadeReady = true;
            }

            if (prepareOnly)
                return true;

            // Attach next track to mixer and fade
            _xfadeMixer.SetNext(_nextDynamicsProvider);
            _xfadeMixer.BeginCrossfade(TimeSpan.FromMilliseconds(fadeMs));

            lock (_gate)
            {
                ClearPreparedCrossfadeState();
            }

            ScheduleCrossfadeCommit(nextFilePath, startOffset, fadeMs, ct);
            return true;
        }

        public void BeginLoopCrossfadeTo(TimeSpan loopAOffset, int fadeMs = 250)
        {
            string? cur;
            lock (_gate) cur = _currentFile;

            if (string.IsNullOrWhiteSpace(cur))
                return;

            // Don’t allow if not actively playing or mixer not ready
            if (_output?.PlaybackState != PlaybackState.Playing || _xfadeMixer == null || _cts == null)
                return;

            // If a crossfade is already staged, ignore (keeps v1 safe)
            if (_nextFfmpeg != null || _nextBuffer != null)
                return;

            // Clamp offset to duration if we have it
            if (Duration.HasValue)
            {
                if (loopAOffset < TimeSpan.Zero) loopAOffset = TimeSpan.Zero;
                if (loopAOffset > Duration.Value) loopAOffset = Duration.Value;
            }
            else if (loopAOffset < TimeSpan.Zero)
            {
                loopAOffset = TimeSpan.Zero;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    BeginCrossfadeInternal(
                        nextFilePath: cur,
                        startOffset: loopAOffset,
                        fadeMs: fadeMs,
                        isSameTrackLoop: true);
                }
                catch
                {
                }
            });
        }

        public void CrossfadeTo(string nextFile, int fadeMs = 900)
        {
            if (string.IsNullOrWhiteSpace(nextFile))
                return;

            if (!File.Exists(nextFile))
            {
                PlaybackFailed?.Invoke("Song file is missing.\n\nCheck the file path and re-add the song.");
                return;
            }

            // If not currently playing / no mixer, just hard switch
            if (_output == null || _xfadeMixer == null || _cts == null)
            {
                Load(nextFile);
                Play(reason: "CrossfadeTo:FallbackNoMixer");
                return;
            }

            BeginCrossfadeInternal(
                nextFilePath: nextFile,
                startOffset: TimeSpan.Zero,
                fadeMs: fadeMs,
                isSameTrackLoop: false);
        }

        public void Pause(string? reason = null)
        {
            AppendPlaybackEngineLog(
                $"Pause CALL | reason={reason ?? "null"} | " +
                BuildPlaybackStateSnapshot(""));

            lock (_gate)
            {
                _wantPlaying = false;
                _intentionalStopGeneration = -1;
            }

            if (_output?.PlaybackState == PlaybackState.Playing)
                _output.Pause();
        }

        public void Stop(bool preserveSeekBase = false, string? reason = null)
        {
            AppendPlaybackEngineLog(
                $"Stop CALL | reason={reason ?? "null"} | preserveSeekBase={preserveSeekBase} | " +
                BuildPlaybackStateSnapshot(""));

            IWavePlayer? output;
            CancellationTokenSource? cts;
            Process? ffmpeg;
            Process? nextFfmpeg;

            // ---- Phase 1: fast state flip + detach everything under lock ----
            lock (_gate)
            {
                _intentionalStopGeneration = _playbackGeneration;
                _wantPlaying = false;

                // Grab references so we can stop/kill/dispose outside the lock
                output = _output;
                cts = _cts;
                ffmpeg = _ffmpeg;
                nextFfmpeg = _nextFfmpeg;

                // Detach everything from the instance immediately
                _output = null;
                _cts = null;

                _ffmpeg = null;
                _nextFfmpeg = null;

                _buffer = null;
                _nextBuffer = null;

                _volumeProvider = null;
                _dynamicsProvider = null;
                _nextDynamicsProvider = null;

                _spectrumTap = null;
                _countingProvider = null;

                _xfadeMixer = null;

                // Tasks are no longer owned once we detach the CTS / pipeline
                _pumpTask = null;
                _stderrTask = null;
                _nextPumpTask = null;
                _nextStderrTask = null;
                _eofMonitorTask = null;
                _watchdogTask = null;
                _playbackStoppedHandler = null;

                // Reset counters / flags
                _decodedBytes = 0;
                _ffmpegReachedEof = false;
                _suppressFfmpegExitError = false;
                ClearPreparedCrossfadeState();

                Interlocked.Exchange(ref _playedBytes, 0);
                Interlocked.Exchange(ref _trackStartPlayedBytes, 0);
                if (!preserveSeekBase)
                    _seekBase = TimeSpan.Zero;

                // Reset spectrum state
                _spectrum.Reset(PcmFormat.SampleRate, PcmFormat.Channels);

                // Reset "ended" gate since pipeline is gone
                Interlocked.Exchange(ref _endedRaised, 0);
            }

            // ---- Phase 2: risky operations OUTSIDE the lock ----
            try
            {
                if (output != null)
                {
                    output.Stop();
                }
            }
            catch { }

            try { cts?.Cancel(); } catch { }

            try
            {
                if (ffmpeg != null && !ffmpeg.HasExited)
                    ffmpeg.Kill(entireProcessTree: true);
            }
            catch { }

            try
            {
                if (nextFfmpeg != null && !nextFfmpeg.HasExited)
                    nextFfmpeg.Kill(entireProcessTree: true);
            }
            catch { }

            try { ffmpeg?.Dispose(); } catch { }
            try { nextFfmpeg?.Dispose(); } catch { }
            try { output?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }
        }

        public void Seek(TimeSpan position, bool resume, string? reason = null)
        {
            _ = Task.Run(async () =>
            {
                await _pipelineSem.WaitAsync().ConfigureAwait(false);
                try
                {
                    string? file;
                    lock (_gate) file = _currentFile;
                    if (file == null) return;

                    var dur = Duration;
                    if (dur.HasValue)
                    {
                        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                        if (position > dur.Value) position = dur.Value;
                    }
                    else if (position < TimeSpan.Zero)
                    {
                        position = TimeSpan.Zero;
                    }

                    _seekBase = position;
                    _wantPlaying = resume;

                    AppendPlaybackEngineLog(
                        $"Seek CALL | reason={reason ?? "null"} | file=\"{file}\" | position={position} | resume={resume}");

                    StartPipeline(file, startOffset: position, startPlaying: resume);
                }
                finally
                {
                    _pipelineSem.Release();
                }
            });
        }

        public void SeekFraction(double progress, bool resume)
        {
            var dur = Duration;
            if (!dur.HasValue || dur.Value.TotalSeconds <= 0.01)
                return;

            progress = Math.Clamp(progress, 0.0, 1.0);
            Seek(TimeSpan.FromSeconds(dur.Value.TotalSeconds * progress), resume);
        }

        private void StartPipeline(string file, TimeSpan startOffset, bool startPlaying)
        {
            Stop(preserveSeekBase: true, reason: "StartPipeline"); // clears old pipeline safely without flashing position to 0

            if (!File.Exists(file))
            {
                PlaybackFailed?.Invoke("Song file is missing.\n\nCheck the file path and re-add the song.");
                return;
            }

            int generationForThisPipeline;

            lock (_gate)
            {
                _playbackGeneration++;
                generationForThisPipeline = _playbackGeneration;
                _intentionalStopGeneration = -1;
                _wantPlaying = startPlaying;
            }

            AppendPlaybackEngineLog(
                $"StartPipeline ENTER | file=\"{file}\" | startOffset={startOffset} | startPlaying={startPlaying} | newGeneration={generationForThisPipeline}");

            _seekBase = startOffset;
            _spectrum.Reset(PcmFormat.SampleRate, PcmFormat.Channels);
            _ffmpegReachedEof = false;
            _decodedBytes = 0;
            Interlocked.Exchange(ref _playedBytes, 0);

            _trackStartPlayedBytes = 0;

            // ✅ new pipeline => allow PlaybackEnded again (exactly once)
            Interlocked.Exchange(ref _endedRaised, 0);

            var ffmpegPath = ResolveFfmpegPath();

            if (ffmpegPath == null)
                throw new FileNotFoundException(
                    "ffmpeg.exe not found. Put it next to the app executable (bin\\Debug\\net10.0-windows\\).",
                    "ffmpeg.exe"
                );

            _buffer = new BufferedWaveProvider(PcmFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(60),
                ReadFully = true,
                DiscardOnBufferOverflow = true
            };

            // Tap RAW audio before normalization so the visualizer keeps its full activity
            _spectrumTap = new SpectrumTapWaveProvider(_buffer, _spectrum, bands =>
            {
                SpectrumAvailable?.Invoke(bands);
            });

            _dynamicsProvider = new DynamicsWaveProvider16(_spectrumTap, enabled: _normalizeRequested);

            // Crossfade mixer sits AFTER dynamics and BEFORE volume.
            _xfadeMixer = new CrossfadeMixerWaveProvider16(_dynamicsProvider);

            // Volume stays AFTER mixer
            _volumeProvider = new VolumeWaveProvider16(_xfadeMixer) { Volume = _volume };

            // Counting provider wraps final output so Position works
            _countingProvider = new CountingWaveProvider(_volumeProvider, bytesRead =>
            {
                Interlocked.Add(ref _playedBytes, bytesRead);
            });

            try
            {
                _output = CreateWasapiOut();
                _output.Init(_countingProvider);
                AppendPlaybackEngineLog("Audio backend selected: WASAPI");
            }
            catch (Exception ex)
            {
                AppendPlaybackEngineLog("WASAPI create/init failed; falling back to WaveOut | " + ex);

                try { _output?.Dispose(); } catch { }
                _output = null;

                try
                {
                    _output = new WaveOutEvent { DesiredLatency = 250, NumberOfBuffers = 8 };
                    _output.Init(_countingProvider);
                    AppendPlaybackEngineLog("Audio backend selected: WaveOut");
                    Debug.WriteLine("WASAPI create/init failed, fell back to WaveOut. " + ex);
                }
                catch (Exception waveOutEx)
                {
                    AppendPlaybackEngineLog("WaveOut init failed | " + waveOutEx);
                    throw new InvalidOperationException(
                        "No usable audio output device is currently available.", waveOutEx);
                }
            }

            var outputForThisPipeline = _output;
            var fileForThisPipeline = file;

            RebindPlaybackStoppedHandler(
                outputForThisPipeline,
                fileForThisPipeline,
                generationForThisPipeline);

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            string ssArg = startOffset > TimeSpan.Zero
                ? $"-ss {startOffset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} "
                : "";

            var stderrTail = new StringBuilder(capacity: 4096);
            void AppendStderr(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                if (stderrTail.Length > 16384)
                    stderrTail.Remove(0, stderrTail.Length - 8192);
                stderrTail.Append(s);
            }

            _ffmpeg = new Process

            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments =
                        $"-hide_banner -loglevel warning -nostdin " +
                        "-fflags +discardcorrupt -err_detect ignore_err " +
                        $"{ssArg}-i {Quote(file)} " +
                        "-vn -sn -dn " +
                        "-f s16le -acodec pcm_s16le -ac 2 -ar 44100 pipe:1",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };


            var proc = _ffmpeg;               // capture AFTER creation
            if (proc == null)
                throw new InvalidOperationException("ffmpeg process was not created.");
            var fileForThisProc = file;       // optional but recommended

            proc.Exited += (_, __) =>
            {
                if (ct.IsCancellationRequested)
                    return;

                if (_suppressFfmpegExitError)
                    return;

                // Ignore exits from old processes (important during crossfade)
                lock (_gate)
                {
                    if (!ReferenceEquals(_ffmpeg, proc))
                        return;
                }

                int code = 0;
                try { code = proc.ExitCode; } catch { }

                if (code == 0)
                {
                    _ffmpegReachedEof = true;
                    AppendPlaybackEngineLog(BuildPlaybackStateSnapshot("FFmpeg Exited EOF | "));
                    return;
                }

                double decodedSeconds = _decodedBytes / (double)PcmFormat.AverageBytesPerSecond;

                var msg =
                    $"FFmpeg ended unexpectedly.\n\n" +
                    $"ExitCode: {code}\n" +
                    $"File: {fileForThisProc}\n" +
                    $"SeekBase: {_seekBase}\n" +
                    $"DecodedSeconds: {decodedSeconds:0.###}";

                PlaybackFailed?.Invoke(msg);
            };

            try
            {
                proc.Start(); // ✅ use proc
                try { proc.PriorityClass = ProcessPriorityClass.Normal; } catch { }
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException($"Failed to start ffmpeg. Path: {ffmpegPath}", ex);
            }

            _stderrTask = Task.Run(async () =>
            {
                try
                {
                    char[] buf = new char[2048];
                    while (!ct.IsCancellationRequested && !proc.HasExited)
                    {
                        int n = await proc.StandardError.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0) break;
                        AppendStderr(new string(buf, 0, n));
                    }
                }
                catch { }
            }, ct);

            _pumpTask = Task.Factory.StartNew(
                () => PumpStdoutToBufferStable(ffmpeg: proc, buffer: _buffer, owner: this, ct: ct), // ✅ use proc
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            // EOF monitor: fire end when ffmpeg EOF + buffer drained
            // Capture the pipeline objects this monitor is responsible for
            var procForThisPipeline = proc;
            var bufferForThisPipeline = _buffer;
            var playbackOutputForThisPipeline = _output;

            _eofMonitorTask = StartEofMonitor(
                procForThisPipeline,
                bufferForThisPipeline,
                playbackOutputForThisPipeline,
                generationForThisPipeline,
                ct);

            _watchdogTask = StartWatchdogMonitor(
                playbackOutputForThisPipeline,
                generationForThisPipeline,
                ct);

            // Only prebuffer when starting near the beginning.
            // Seeking near end should not block waiting for 1.2s that may not exist.
            if (_buffer != null && startOffset < TimeSpan.FromSeconds(1))
            {
                TimeSpan? remainingDuration = null;

                if (Duration.HasValue)
                {
                    remainingDuration = Duration.Value - startOffset;
                    if (remainingDuration < TimeSpan.Zero)
                        remainingDuration = TimeSpan.Zero;
                }

                PrebufferAsync(_buffer, remainingDuration, ct)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }

            if (!ct.IsCancellationRequested && startPlaying)
            {
                AppendPlaybackEngineLog(BuildPlaybackStateSnapshot("StartPipeline Play() | "));
                _output.Play();
            }
        }

        private Task StartWatchdogMonitor(
            IWavePlayer outp,
            int generationForThisPipeline,
            CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct);

                        bool intentionalStop;
                        bool wantPlaying;
                        bool reachedEof;
                        bool ffmpegAlive;
                        int buffered;
                        int currentGeneration;
                        int watchdogGeneration;

                        lock (_gate)
                        {
                            currentGeneration = _playbackGeneration;
                            watchdogGeneration = generationForThisPipeline;
                            intentionalStop = (_intentionalStopGeneration == _playbackGeneration);
                            wantPlaying = _wantPlaying;
                            reachedEof = _ffmpegReachedEof;
                            ffmpegAlive = _ffmpeg != null && !_ffmpeg.HasExited;
                            buffered = _buffer?.BufferedBytes ?? 0;
                        }

                        if (watchdogGeneration != currentGeneration)
                            continue;

                        if (!intentionalStop &&
                            wantPlaying &&
                            !reachedEof &&
                            ffmpegAlive &&
                            outp.PlaybackState == PlaybackState.Stopped &&
                            buffered > 0)
                        {
                            AppendPlaybackEngineLog(BuildPlaybackStateSnapshot("Watchdog RESUME Play() | "));
                            try { outp.Play(); } catch { }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }, ct);
        }

        private Task StartEofMonitor(
            Process procForThisPipeline,
            BufferedWaveProvider bufferForThisPipeline,
            IWavePlayer playbackOutputForThisPipeline,
            int generationForThisPipeline,
            CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        lock (_gate)
                        {
                            if (generationForThisPipeline != _playbackGeneration ||
                                !ReferenceEquals(_ffmpeg, procForThisPipeline) ||
                                !ReferenceEquals(_buffer, bufferForThisPipeline) ||
                                !ReferenceEquals(_output, playbackOutputForThisPipeline))
                            {
                                return;
                            }
                        }

                        bool eofForThisPipeline = false;
                        lock (_gate)
                        {
                            eofForThisPipeline =
                                generationForThisPipeline == _playbackGeneration &&
                                ReferenceEquals(_ffmpeg, procForThisPipeline) &&
                                _ffmpegReachedEof;
                        }

                        if (eofForThisPipeline)
                        {
                            while (!ct.IsCancellationRequested && (bufferForThisPipeline?.BufferedBytes ?? 0) > 0)
                                await Task.Delay(30, ct);

                            if (!ct.IsCancellationRequested)
                            {
                                lock (_gate)
                                {
                                    bool stillCurrent =
                                        generationForThisPipeline == _playbackGeneration &&
                                        ReferenceEquals(_ffmpeg, procForThisPipeline) &&
                                        ReferenceEquals(_buffer, bufferForThisPipeline) &&
                                        ReferenceEquals(_output, playbackOutputForThisPipeline);

                                    if (!stillCurrent)
                                    {
                                        AppendPlaybackEngineLog(
                                            BuildPlaybackStateSnapshot("EOF monitor ABORT stale-after-drain | "));
                                        return;
                                    }

                                    // IMPORTANT:
                                    // During crossfade, the old/current pipeline can hit EOF and drain to zero
                                    // while a next pipeline is already staged and feeding the mixer.
                                    //
                                    // In that case, DO NOT stop the shared output and DO NOT raise PlaybackEnded.
                                    // Just let the old EOF monitor exit quietly and allow the crossfade commit task
                                    // to promote the next pipeline to current.
                                    bool actualCrossfadeActive =
                                        _xfadeMixer != null &&
                                        _xfadeMixer.IsCrossfading;

                                    if (actualCrossfadeActive)
                                    {
                                        AppendPlaybackEngineLog(
                                            BuildPlaybackStateSnapshot("EOF monitor ABORT actual-crossfade-active | "));
                                        return;
                                    }

                                    if (_intentionalStopGeneration != -1)
                                    {
                                        AppendPlaybackEngineLog(
                                            BuildPlaybackStateSnapshot("EOF monitor ABORT already-stopped | "));
                                        return;
                                    }

                                    _intentionalStopGeneration = generationForThisPipeline;
                                }

                                AppendPlaybackEngineLog(BuildPlaybackStateSnapshot("EOF monitor BEFORE stop | "));

                                try { playbackOutputForThisPipeline?.Stop(); } catch { }

                                AppendPlaybackEngineLog(BuildPlaybackStateSnapshot("EOF monitor AFTER stop BEFORE PlaybackEnded | "));

                                lock (_gate)
                                {
                                    bool stillCurrentAfterStop =
                                        generationForThisPipeline == _playbackGeneration &&
                                        ReferenceEquals(_ffmpeg, procForThisPipeline) &&
                                        ReferenceEquals(_buffer, bufferForThisPipeline) &&
                                        ReferenceEquals(_output, playbackOutputForThisPipeline);

                                    if (!stillCurrentAfterStop)
                                    {
                                        AppendPlaybackEngineLog(
                                            BuildPlaybackStateSnapshot("EOF monitor ABORT stale-after-stop-before-PlaybackEnded | "));
                                        return;
                                    }
                                }

                                RaisePlaybackEndedOnce();

                                AppendPlaybackEngineLog(BuildPlaybackStateSnapshot("EOF monitor AFTER PlaybackEnded | "));
                            }

                            return;
                        }

                        await Task.Delay(50, ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }, ct);
        }

        private WasapiOut CreateWasapiOut()
        {
            MMDevice device;

            if (_deviceEnumerator != null)
            {
                device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            else
            {
                device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            lock (_gate)
            {
                _currentOutputDeviceId = device.ID;
            }

            AppendPlaybackEngineLog($"CreateWasapiOut | deviceId={device.ID}");

            // If you still hear micro-stutters, bump to 160–220ms.
            return new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 250);
        }

        private void AudioDeviceManager_DefaultRenderDeviceChanged(string newDeviceId)
        {
            try
            {
                bool shouldRebuild = false;
                bool isPlayingNow = false;
                string? currentFile;

                lock (_gate)
                {
                    currentFile = _currentFile;

                    // Ignore if we're already on this device id.
                    if (string.Equals(_currentOutputDeviceId, newDeviceId, StringComparison.OrdinalIgnoreCase))
                        return;

                    isPlayingNow = _output?.PlaybackState == PlaybackState.Playing;

                    // If we're idle, just remember the new device id.
                    // The next StartPipeline/CreateWasapiOut will naturally use it.
                    if (!isPlayingNow || string.IsNullOrWhiteSpace(currentFile))
                    {
                        _currentOutputDeviceId = newDeviceId;
                        AppendPlaybackEngineLog(
                            $"DefaultDeviceChanged IDLE | newDeviceId={newDeviceId} | " +
                            BuildPlaybackStateSnapshot(""));
                        return;
                    }

                    // Debounce rapid bursts (Bluetooth / USB device churn).
                    var now = DateTime.UtcNow;
                    if ((now - _lastDefaultDeviceSwitchUtc).TotalMilliseconds < 750)
                    {
                        AppendPlaybackEngineLog(
                            $"DefaultDeviceChanged SUPPRESSED debounce | newDeviceId={newDeviceId}");
                        return;
                    }

                    _lastDefaultDeviceSwitchUtc = now;
                    shouldRebuild = true;
                }

                if (!shouldRebuild)
                    return;

                _ = Task.Run(async () =>
                {
                    await _pipelineSem.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        SwitchToCurrentDefaultOutputDevice();
                    }
                    catch (Exception ex)
                    {
                        AppendPlaybackEngineLog("SwitchToCurrentDefaultOutputDevice FAILED | " + ex);
                        PlaybackFailed?.Invoke("Failed to switch to the new default audio device.\n\n" + ex.Message);
                    }
                    finally
                    {
                        _pipelineSem.Release();
                    }
                });
            }
            catch (Exception ex)
            {
                AppendPlaybackEngineLog("AudioDeviceManager_DefaultRenderDeviceChanged FAILED | " + ex);
            }
        }

        private void SwitchToCurrentDefaultOutputDevice()
        {
            if (Interlocked.Exchange(ref _defaultDeviceSwitchInFlight, 1) == 1)
                return;

            try
            {
                string? file;
                TimeSpan resumePos;
                bool shouldResume;
                bool crossfadeActive;

                lock (_gate)
                {
                    file = _currentFile;
                    shouldResume = _output?.PlaybackState == PlaybackState.Playing;

                    // Crossfade is internally represented by staged "next" pipeline state.
                    crossfadeActive = _nextFfmpeg != null || _nextBuffer != null;
                }

                if (string.IsNullOrWhiteSpace(file))
                    return;

                string? newDefaultId = null;
                try
                {
                    if (_deviceEnumerator != null)
                    {
                        var dev = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        newDefaultId = dev.ID;
                    }
                }
                catch
                {
                    // Let StartPipeline try anyway; CreateWasapiOut already has fallback behavior.
                }

                resumePos = Position;

                AppendPlaybackEngineLog(
                    $"SwitchToCurrentDefaultOutputDevice BEGIN | " +
                    $"newDefaultId={newDefaultId ?? "(null)"} | " +
                    $"resumePos={resumePos} | " +
                    $"shouldResume={shouldResume} | " +
                    $"crossfadeActive={crossfadeActive} | " +
                    BuildPlaybackStateSnapshot(""));

                // Safe-first behavior:
                // rebuild the current track pipeline on the new Windows default endpoint.
                // If a crossfade was in progress, this intentionally collapses back to a single pipeline.
                StartPipeline(file, startOffset: resumePos, startPlaying: shouldResume);

                lock (_gate)
                {
                    _currentOutputDeviceId = newDefaultId;
                }

                AppendPlaybackEngineLog(
                    $"SwitchToCurrentDefaultOutputDevice END | " +
                    $"newDefaultId={newDefaultId ?? "(null)"} | " +
                    BuildPlaybackStateSnapshot(""));
            }
            finally
            {
                Interlocked.Exchange(ref _defaultDeviceSwitchInFlight, 0);
            }
        }

        private static async Task PrebufferAsync(
            BufferedWaveProvider buffer,
            TimeSpan? remainingDuration,
            CancellationToken ct)
        {
            // ~2.5s prebuffer improves startup stability under load,
            // but never wait for more audio than can actually exist.
            double targetSeconds = 2.5;

            if (remainingDuration.HasValue)
            {
                targetSeconds = Math.Min(
                    targetSeconds,
                    Math.Max(0.0, remainingDuration.Value.TotalSeconds));
            }

            int targetBytes = (int)(buffer.WaveFormat.AverageBytesPerSecond * targetSeconds);

            for (int i = 0; i < 400 && !ct.IsCancellationRequested; i++)
            {
                if (buffer.BufferedBytes >= targetBytes)
                    break;

                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }

        private static async Task<bool> WaitForCrossfadeReadyAsync(
            BufferedWaveProvider buffer,
            int requiredMs,
            CancellationToken ct)
        {
            int requiredBytes = (int)(buffer.WaveFormat.AverageBytesPerSecond * (requiredMs / 1000.0));

            for (int i = 0; i < 500 && !ct.IsCancellationRequested; i++)
            {
                if (buffer.BufferedBytes >= requiredBytes)
                    return true;

                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            return false;
        }

        private static void PumpStdoutToBufferStable(Process ffmpeg, BufferedWaveProvider buffer, AudioPlayer owner, CancellationToken ct)
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }

            var stream = ffmpeg.StandardOutput.BaseStream;
            byte[] readBuf = new byte[128 * 1024];

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (buffer.BufferedDuration.TotalSeconds > 45)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    int n = stream.Read(readBuf, 0, readBuf.Length);

                    if (n <= 0)
                    {
                        // stdout ended: only mark EOF if THIS process is still the current one
                        lock (owner._gate)
                        {
                            if (ReferenceEquals(owner._ffmpeg, ffmpeg))
                                owner._ffmpegReachedEof = true;
                        }
                        break;
                    }

                    buffer.AddSamples(readBuf, 0, n);
                    Interlocked.Add(ref owner._decodedBytes, n);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.WriteLine("Pump error: " + e);
            }
        }

        private Process StartFfmpegDecodeToBuffer(
        string file,
        TimeSpan startOffset,
        string ffmpegPath,
        BufferedWaveProvider buffer,
        StringBuilder stderrTail,
        Action<string> appendStderr,
        CancellationToken ct,
        out Task stderrTask,
        out Task pumpTask)
        {
            string ssArg = startOffset > TimeSpan.Zero
                ? $"-ss {startOffset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} "
                : "";

            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments =
                        $"-hide_banner -loglevel warning -nostdin " +
                        "-fflags +discardcorrupt -err_detect ignore_err " +
                        $"{ssArg}-i {Quote(file)} " +
                        "-vn -sn -dn " +
                        "-f s16le -acodec pcm_s16le -ac 2 -ar 44100 pipe:1",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            try
            {
                p.Start();
                try { p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException($"Failed to start ffmpeg. Path: {ffmpegPath}", ex);
            }

            stderrTask = Task.Run(async () =>
            {
                try
                {
                    char[] buf = new char[2048];
                    while (!ct.IsCancellationRequested && !p.HasExited)
                    {
                        int n = await p.StandardError.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0) break;
                        appendStderr(new string(buf, 0, n));
                    }
                }
                catch { }
            }, ct);

            pumpTask = Task.Factory.StartNew(
                () => PumpStdoutToBufferStable(ffmpeg: p, buffer: buffer, owner: this, ct: ct),
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            return p;
        }

        private void StopSceneLane(int lane)
        {
            if (lane < 0 || lane >= _sceneBuffers.Length)
                return;

            var proc = _sceneFfmpeg[lane];
            _sceneFfmpeg[lane] = null;

            try
            {
                if (proc != null && !proc.HasExited)
                    proc.Kill(true);
            }
            catch { }

            _sceneBuffers[lane] = null;
        }

        private sealed class BufferedWaveProviderWaveStream : WaveStream
        {
            private readonly BufferedWaveProvider _buffer;

            public BufferedWaveProviderWaveStream(BufferedWaveProvider buffer)
            {
                _buffer = buffer;
            }

            public override WaveFormat WaveFormat => _buffer.WaveFormat;

            public override long Length => long.MaxValue;

            public override long Position
            {
                get => 0;
                set { }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _buffer.Read(buffer, offset, count);
            }
        }

        private sealed class WaveProviderWaveStream : WaveStream
        {
            private readonly IWaveProvider _provider;

            public WaveProviderWaveStream(IWaveProvider provider)
            {
                _provider = provider;
            }

            public override WaveFormat WaveFormat => _provider.WaveFormat;

            public override long Length => long.MaxValue;

            public override long Position
            {
                get => 0;
                set { }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _provider.Read(buffer, offset, count);
            }
        }

        private sealed class SpectrumTapWaveProvider : IWaveProvider
        {
            private readonly IWaveProvider _inner;
            private readonly SpectrumAnalyzer _analyzer;
            private readonly Action<float[]> _publish;

            public SpectrumTapWaveProvider(IWaveProvider inner, SpectrumAnalyzer analyzer, Action<float[]> publish)
            {
                _inner = inner;
                _analyzer = analyzer;
                _publish = publish;
            }

            public WaveFormat WaveFormat => _inner.WaveFormat;

            public int Read(byte[] buffer, int offset, int count)
            {
                int read = _inner.Read(buffer, offset, count);
                if (read > 0)
                    _analyzer.PushPcm16Stereo(buffer, offset, read, _publish);
                return read;
            }
        }

        private sealed class CountingWaveProvider : IWaveProvider
        {
            private readonly IWaveProvider _inner;
            private readonly Action<int> _onReadBytes;

            public CountingWaveProvider(IWaveProvider inner, Action<int> onReadBytes)
            {
                _inner = inner;
                _onReadBytes = onReadBytes;
            }

            public WaveFormat WaveFormat => _inner.WaveFormat;

            public int Read(byte[] buffer, int offset, int count)
            {
                int read = _inner.Read(buffer, offset, count);
                if (read > 0)
                    _onReadBytes(read);
                return read;
            }
        }

        private static string? ResolveFfmpegPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidate = Path.Combine(baseDir, "ffmpeg.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        private static string? ResolveFfprobePath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidate = Path.Combine(baseDir, "ffprobe.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        private static TimeSpan? ProbeDuration(string filePath)
        {
            var ffprobe = ResolveFfprobePath();
            if (ffprobe != null)
            {
                try
                {
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = ffprobe,
                            Arguments = $"-v error -show_entries format=duration -of default=nk=1:nw=1 {Quote(filePath)}",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };

                    p.Start();
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(3000);

                    if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
                        return TimeSpan.FromSeconds(seconds);
                }
                catch { }
            }

            var ffmpeg = ResolveFfmpegPath();
            if (ffmpeg == null) return null;

            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        Arguments = $"-hide_banner -nostdin -i {Quote(filePath)}",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                p.Start();
                string err = p.StandardError.ReadToEnd();
                try { p.Kill(entireProcessTree: true); } catch { }

                var m = Regex.Match(err, @"Duration:\s*(\d{2}):(\d{2}):(\d{2})(\.\d+)?");
                if (m.Success)
                {
                    int hh = int.Parse(m.Groups[1].Value);
                    int mm = int.Parse(m.Groups[2].Value);
                    int ss = int.Parse(m.Groups[3].Value);
                    double frac = 0;
                    if (m.Groups[4].Success)
                        frac = double.Parse("0" + m.Groups[4].Value, CultureInfo.InvariantCulture);

                    return new TimeSpan(0, hh, mm, ss) + TimeSpan.FromSeconds(frac);
                }
            }
            catch { }

            return null;
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        private sealed class SpectrumAnalyzer
        {
            private readonly int _fftSize;
            private readonly int _publishEveryMs;

            private int _sampleRate = 44100;
            private int _channels = 2;

            private readonly float[] _mono;              // rolling mono samples
            private int _writeIndex;
            private int _filled;

            private readonly Complex[] _fftBuffer;
            private readonly float[] _window;
            private readonly float[] _bandsOut;

            private long _lastPublishTick;

            public SpectrumAnalyzer(int fftSize, int publishFps)
            {
                if (fftSize <= 0 || (fftSize & (fftSize - 1)) != 0)
                    throw new ArgumentException("fftSize must be a power of two.");

                _fftSize = fftSize;

                publishFps = Math.Clamp(publishFps, 10, 120);
                _publishEveryMs = (int)Math.Round(1000.0 / publishFps);

                _mono = new float[_fftSize];
                _fftBuffer = new Complex[_fftSize];
                _window = new float[_fftSize];
                _bandsOut = new float[_fftSize / 2];

                // Hann window
                for (int i = 0; i < _fftSize; i++)
                    _window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (_fftSize - 1)));
            }

            private static float Lerp(float a, float b, float t)
            {
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                return a + (b - a) * t;
            }

            public void Reset(int sampleRate, int channels)
            {
                _sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
                _channels = channels <= 0 ? 2 : channels;

                _writeIndex = 0;
                _filled = 0;
                Array.Clear(_mono, 0, _mono.Length);
                Array.Clear(_bandsOut, 0, _bandsOut.Length);
                _lastPublishTick = 0;
            }

            // Your pipeline is always s16le, 2ch, 44.1k, so this is exactly what we need.
            public void PushPcm16Stereo(byte[] pcm, int offset, int count, Action<float[]> publish)
            {
                if (pcm == null || publish == null) return;
                if (count <= 0) return;

                // Throttle publish rate
                long nowTick = Environment.TickCount64;
                bool canPublish = (_lastPublishTick == 0) || (nowTick - _lastPublishTick >= _publishEveryMs);

                // Convert bytes -> mono float samples and write into rolling buffer
                // Frame = 4 bytes (L int16, R int16)
                int end = offset + count;
                int i = offset;

                while (i + 3 < end)
                {
                    short l = (short)(pcm[i] | (pcm[i + 1] << 8));
                    short r = (short)(pcm[i + 2] | (pcm[i + 3] << 8));
                    i += 4;

                    // Mix to mono in [-1..1]
                    float mono = ((l + r) * 0.5f) / 32768f;

                    _mono[_writeIndex] = mono;
                    _writeIndex++;
                    if (_writeIndex >= _fftSize) _writeIndex = 0;

                    if (_filled < _fftSize) _filled++;
                }

                if (!canPublish) return;
                if (_filled < _fftSize) return;

                _lastPublishTick = nowTick;

                // Build FFT input in correct time order (oldest -> newest)
                // Since _writeIndex is where next sample will be written,
                // it's effectively the "start" (oldest) in the ring buffer.
                for (int n = 0; n < _fftSize; n++)
                {
                    int src = _writeIndex + n;
                    if (src >= _fftSize) src -= _fftSize;

                    float s = _mono[src] * _window[n];

                    _fftBuffer[n].X = s;
                    _fftBuffer[n].Y = 0f;
                }

                // NAudio FFT
                int m = (int)Math.Log2(_fftSize);
                FastFourierTransform.FFT(true, m, _fftBuffer);

                // Magnitudes for first half (0..Nyquist)
                // Scale: empirical so it looks good; RainVisualizerOverlay has AGC anyway.
                // We’ll output roughly 0..1-ish values.
                float scale = 1500f / _fftSize;
                float gain = scale * 3.0f;
                float invBandCount = 1f / (_bandsOut.Length - 1);

                for (int b = 0; b < _bandsOut.Length; b++)
                {
                    float re = _fftBuffer[b].X;
                    float im = _fftBuffer[b].Y;
                    float mag = (float)Math.Sqrt(re * re + im * im);

                    float v = mag * gain;

                    // perceptual shaping
                    //v = (float)Math.Pow(v, .25f);

                    float t = b * invBandCount;
                    float tilt;

                    /*		if (t < 0.0075f)          // deep bass .50f
                                    tilt = .50f;
                                else if (t < 0.015f)     // bass .60f
                                    tilt = 2.00f;
                                else if (t < 0.06f)     // low mids
                                    tilt = 3.00f;
                                else if (t < 0.10f)     // mids
                                    tilt = 5.0f;
                                else if (t < 0.15f)     // high mids
                                    tilt = 6.0f;
                                else                    // highs
                                    tilt = 10.0f;
                    */
                    if (t < 0.0075f)
                    {
                        float u = t / 0.0075f;
                        tilt = Lerp(0.50f, 1.50f, u);
                    }
                    else if (t < 0.015f)
                    {
                        float u = (t - 0.0075f) / (0.015f - 0.0075f);
                        tilt = Lerp(1.50f, 3.00f, u);
                    }
                    else if (t < 0.06f)
                    {
                        float u = (t - 0.015f) / (0.06f - 0.015f);
                        tilt = Lerp(3.00f, 4.00f, u);
                    }
                    else if (t < 0.10f)
                    {
                        float u = (t - 0.06f) / (0.10f - 0.06f);
                        tilt = Lerp(4.00f, 5.00f, u);
                    }
                    else if (t < 0.15f)
                    {
                        float u = (t - 0.10f) / (0.15f - 0.10f);
                        tilt = Lerp(5.00f, 15.00f, u);
                    }
                    else
                    {
                        tilt = 15.00f;
                    }

                    v *= tilt;

                    // soft compression so bass doesn't dominate everything
                    //v = v / (1f + 0.18f * v);

                    float prev = _bandsOut[b];
                    float delta = v - prev;

                    if (delta > 0)
                        v += delta * 0.35f;

                    _bandsOut[b] = v;
                }

                var copy = new float[_bandsOut.Length];
                Array.Copy(_bandsOut, copy, copy.Length);
                publish(copy);

            }
        }

        public void Dispose()
        {
            try
            {
                if (_audioDeviceManager != null)
                    _audioDeviceManager.DefaultRenderDeviceChanged -= AudioDeviceManager_DefaultRenderDeviceChanged;
            }
            catch { }

            try { _audioDeviceManager?.Dispose(); } catch { }
            _audioDeviceManager = null;

            try { _deviceEnumerator?.Dispose(); } catch { }
            _deviceEnumerator = null;

            Stop();
            GC.SuppressFinalize(this);
        }
    }
}