using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.ComponentModel;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        // ---------- Playlist persistence ----------
        private sealed class PersistedState
        {
            public int ActivePlaylist { get; set; } = 0;
            public List<PersistedPlaylist> Playlists { get; set; } = new();

            public int RepeatMode { get; set; } = 0;
            public int XFadeMode { get; set; } = 0;
            public bool ShuffleEnabled { get; set; } = false;
            public bool NormalizeEnabled { get; set; } = false;
            public double VolumePercent { get; set; } = 100;

            public int VoiceCaptureMode { get; set; } = 0;

            public float VoiceMinOverallConfidence { get; set; } = VoiceTriggerService.DefaultMinOverallConfidence;
            public float VoiceMinWordConfidence { get; set; } = VoiceTriggerService.DefaultMinWordConfidence;
            public float VoiceMinFinalWordConfidence { get; set; } = VoiceTriggerService.DefaultMinFinalWordConfidence;

            public bool LoopEnabled { get; set; } = false;
            public double LoopA { get; set; } = 0.0;
            public double LoopB { get; set; } = 1.0;

            public string? ResumeFile { get; set; } = null;
            public double ResumePositionSeconds { get; set; } = 0.0;
            public bool ResumeWasPlaying { get; set; } = false;

            public double WindowLeft { get; set; } = double.NaN;
            public double WindowTop { get; set; } = double.NaN;
            public double WindowWidth { get; set; } = double.NaN;
            public double WindowHeight { get; set; } = double.NaN;
            public double ExpandedWindowHeight { get; set; } = double.NaN;
            public int WindowState { get; set; } = 0;
            public bool PlaylistCollapsed { get; set; } = false;

            // 🔥 Scene Mode
            public bool SceneEnabled { get; set; } = false;
            public double[] SceneLaneVolumes { get; set; } = new[] { 1.0, 1.0, 1.0, 1.0 };
            public string?[] SceneTracks { get; set; } = new string?[4];
        }

        private sealed class PersistedPlaylist
        {
            public string Name { get; set; } = "Playlist";
            public List<string> Tracks { get; set; } = new();
            public int ActiveIndex { get; set; } = -1;

            public string? SortColumn { get; set; } = null;
            public int SortDirection { get; set; } = (int)ListSortDirection.Ascending;

            public bool VoiceTriggerEnabled { get; set; } = false;
            public string? VoiceTriggerPhrase1 { get; set; } = null;
            public string? VoiceTriggerPhrase2 { get; set; } = null;
            public string? VoiceTriggerPhrase3 { get; set; } = null;
            public int VoiceTriggerLastIndex { get; set; } = -1;
            public int VoiceTriggerCooldownMs { get; set; } = DefaultVoiceTriggerCooldownMs;

            // 🔥 Ambience flag
            public bool IsAmbience { get; set; } = false;
        }

        private static readonly JsonSerializerOptions StateJsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _stateDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicPlayer");

        private string _stateFile = "";

        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
        private bool _isLoadingState;

        private void InitStatePersistence()
        {
            _stateFile = Path.Combine(_stateDir, "playlists.json");

            _saveTimer.Tick += (_, __) =>
            {
                _saveTimer.Stop();
                SaveStateNow();
            };
        }

        private void RequestSaveState()
        {
            if (_isLoadingState) return;

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveStateNow()
        {
            if (_isLoadingState) return;

            try
            {
                Directory.CreateDirectory(_stateDir);

                string? resumePath = _player.CurrentFile;
                if (string.IsNullOrWhiteSpace(resumePath))
                {
                    if (Active.Index >= 0 && Active.Index < Active.Tracks.Count)
                        resumePath = Active.Tracks[Active.Index];
                }

                var bounds = this.WindowState == WindowState.Normal
                    ? new Rect(this.Left, this.Top, this.Width, this.Height)
                    : this.RestoreBounds;

                var st = new PersistedState
                {
                    ActivePlaylist = _activePlaylist,
                    Playlists = _playlists.Select(p => new PersistedPlaylist
                    {
                        Name = p.Name,
                        Tracks = p.Tracks.ToList(),
                        ActiveIndex = p.Index,
                        SortColumn = p.SortColumn,
                        SortDirection = (int)p.SortDirection,
                        VoiceTriggerEnabled = p.VoiceTriggerEnabled,
                        VoiceTriggerPhrase1 = p.VoiceTriggerPhrase1,
                        VoiceTriggerPhrase2 = p.VoiceTriggerPhrase2,
                        VoiceTriggerPhrase3 = p.VoiceTriggerPhrase3,
                        VoiceTriggerLastIndex = p.VoiceTriggerLastIndex,
                        VoiceTriggerCooldownMs = p.VoiceTriggerCooldownMs,
                        IsAmbience = p.IsAmbience
                    }).ToList(),

                    RepeatMode = (int)_repeatMode,
                    XFadeMode = (int)_xFadeMode,
                    ShuffleEnabled = ShuffleToggle.IsChecked == true,
                    NormalizeEnabled = NormalizeToggle.IsChecked == true,
                    VolumePercent = VolumeSlider.Value,
                    VoiceCaptureMode = (int)_voiceCaptureMode,

                    VoiceMinOverallConfidence = _voiceTriggerService.MinOverallConfidence,
                    VoiceMinWordConfidence = _voiceTriggerService.MinWordConfidence,
                    VoiceMinFinalWordConfidence = _voiceTriggerService.MinFinalWordConfidence,

                    LoopEnabled = LoopToggle.IsChecked == true,
                    LoopA = Math.Clamp(WaveformBar.LoopA, 0.0, 1.0),
                    LoopB = Math.Clamp(WaveformBar.LoopB, 0.0, 1.0),

                    ResumeFile = resumePath,
                    ResumePositionSeconds = Math.Max(0.0, _player.Position.TotalSeconds),
                    ResumeWasPlaying = false,

                    WindowLeft = bounds.Left,
                    WindowTop = bounds.Top,
                    WindowWidth = bounds.Width,
                    WindowHeight = bounds.Height,
                    ExpandedWindowHeight = _playlistCollapsed
                        ? ((_expandedHeight > 0) ? _expandedHeight : bounds.Height)
                        : bounds.Height,
                    WindowState = (int)this.WindowState,
                    PlaylistCollapsed = _playlistCollapsed,

                    SceneEnabled = _sceneEnabled,
                    SceneLaneVolumes = _sceneLaneVolumes.ToArray(),
                    SceneTracks = _sceneTracks.ToArray(),
                };

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        var jsonOut = JsonSerializer.Serialize(st, StateJsonOptions);
                        var tmp = _stateFile + ".tmp";

                        File.WriteAllText(tmp, jsonOut);
                        File.Copy(tmp, _stateFile, overwrite: true);

                        try { File.Delete(tmp); } catch { }

                        break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(20);
                    }
                }
            }
            catch
            {
            }
        }

        private bool TryLoadState()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_stateFile) || !File.Exists(_stateFile))
                    return false;

                var json = File.ReadAllText(_stateFile);
                var st = JsonSerializer.Deserialize<PersistedState>(json, StateJsonOptions);
                if (st == null || st.Playlists == null || st.Playlists.Count == 0)
                    return false;

                _playlists.Clear();

                foreach (var pl in st.Playlists)
                {
                    var ps = new PlaylistState
                    {
                        Name = string.IsNullOrWhiteSpace(pl.Name) ? "Playlist" : pl.Name.Trim(),
                        Index = pl.ActiveIndex,
                        SortColumn = pl.SortColumn,
                        SortDirection = Enum.IsDefined(typeof(ListSortDirection), pl.SortDirection)
                            ? (ListSortDirection)pl.SortDirection
                            : ListSortDirection.Ascending,
                        VoiceTriggerEnabled = pl.VoiceTriggerEnabled,
                        VoiceTriggerPhrase1 = NormalizeLoadedAliasGroup(pl.VoiceTriggerPhrase1),
                        VoiceTriggerPhrase2 = NormalizeLoadedAliasGroup(pl.VoiceTriggerPhrase2),
                        VoiceTriggerPhrase3 = NormalizeLoadedAliasGroup(pl.VoiceTriggerPhrase3),
                        VoiceTriggerLastIndex = pl.VoiceTriggerLastIndex,
                        VoiceTriggerCooldownMs = pl.VoiceTriggerCooldownMs > 0
                            ? pl.VoiceTriggerCooldownMs
                            : DefaultVoiceTriggerCooldownMs,
                        IsAmbience = pl.IsAmbience
                    };

                    if (pl.Tracks != null && pl.Tracks.Count > 0)
                        ps.Tracks.AddRange(pl.Tracks.Where(t => !string.IsNullOrWhiteSpace(t)));

                    if (ps.Tracks.Count == 0) ps.Index = -1;
                    else ps.Index = Math.Clamp(ps.Index, -1, ps.Tracks.Count - 1);

                    _playlists.Add(ps);
                }

                if (_playlists.Count == 0)
                    return false;

                _activePlaylist = Math.Clamp(st.ActivePlaylist, 0, _playlists.Count - 1);

                _repeatMode = Enum.IsDefined(typeof(RepeatMode), st.RepeatMode)
                    ? (RepeatMode)st.RepeatMode
                    : RepeatMode.All;

                if (_repeatMode == RepeatMode.None)
                    _repeatMode = RepeatMode.All;

                _xFadeMode = Enum.IsDefined(typeof(XFadeMode), st.XFadeMode)
                    ? (XFadeMode)st.XFadeMode
                    : XFadeMode.Off;

                ShuffleToggle.IsChecked = st.ShuffleEnabled;
                NormalizeToggle.IsChecked = st.NormalizeEnabled;
                _voiceCaptureMode = Enum.IsDefined(typeof(VoiceCaptureMode), st.VoiceCaptureMode)
                    ? (VoiceCaptureMode)st.VoiceCaptureMode
                    : VoiceCaptureMode.Off;

                _voiceTriggerService.MinOverallConfidence =
                    Math.Clamp(st.VoiceMinOverallConfidence, 0.0f, 1.0f);

                _voiceTriggerService.MinWordConfidence =
                    Math.Clamp(st.VoiceMinWordConfidence, 0.0f, 1.0f);

                _voiceTriggerService.MinFinalWordConfidence =
                    Math.Clamp(st.VoiceMinFinalWordConfidence, 0.0f, 1.0f);

                double vol = Math.Clamp(st.VolumePercent, VolumeSlider.Minimum, VolumeSlider.Maximum);
                VolumeSlider.Value = vol;

                _player.Volume = (float)(vol / 100.0);
                _player.NormalizeEnabled = st.NormalizeEnabled;

                UpdateRepeatButtonVisuals();
                UpdateXFadeButtonVisuals();

                LoopToggle.IsChecked = st.LoopEnabled;
                WaveformBar.LoopA = Math.Clamp(st.LoopA, 0.0, 1.0);
                WaveformBar.LoopB = Math.Clamp(st.LoopB, 0.0, 1.0);

                _resumeFile = st.ResumeFile;
                _resumeSeconds = Math.Max(0.0, st.ResumePositionSeconds);
                _resumePending = true;

                _expandedHeight = !double.IsNaN(st.ExpandedWindowHeight) && st.ExpandedWindowHeight > 0
                    ? st.ExpandedWindowHeight
                    : st.WindowHeight;

                _restorePlaylistCollapsed = st.PlaylistCollapsed;

                RestoreWindowPlacement(st);

                _playlistCollapsed = st.PlaylistCollapsed;

                _sceneEnabled = st.SceneEnabled;

                if (st.SceneLaneVolumes != null && st.SceneLaneVolumes.Length == 4)
                {
                    bool allZero =
                        st.SceneLaneVolumes.All(v => Math.Abs(v) < 0.000001);

                    for (int i = 0; i < 4; i++)
                    {
                        _sceneLaneVolumes[i] = allZero
                            ? 1.0
                            : Math.Clamp(st.SceneLaneVolumes[i], 0.0, 1.0);
                    }
                }

                if (st.SceneTracks != null && st.SceneTracks.Length == 4)
                {
                    for (int i = 0; i < 4; i++)
                        _sceneTracks[i] = st.SceneTracks[i];
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetGreenHighlight(SceneButton, _sceneEnabled);
                    SceneStrip.Visibility = _sceneEnabled ? Visibility.Visible : Visibility.Collapsed;

                    for (int i = 0; i < 4; i++)
                    {
                        // Restore all lanes as NOT playing on startup.
                        _sceneLanePlaying[i] = false;

                        if (i >= 1)
                            UpdateSceneLanePlayButton(i);

                        SetSceneLaneVolumeVisual(i, _sceneLaneVolumes[i]);

                        if (!string.IsNullOrWhiteSpace(_sceneTracks[i]))
                        {
                            UpdateSceneLaneUI(i, _sceneTracks[i]!);
                        }
                    }

                    ApplyCombinedSceneAndMasterVolumes();
                }));

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RestoreWindowPlacement(PersistedState st)
        {
            if (double.IsNaN(st.WindowLeft) || double.IsNaN(st.WindowTop) ||
                double.IsNaN(st.WindowWidth) || double.IsNaN(st.WindowHeight))
                return;

            if (st.WindowWidth < this.MinWidth) st.WindowWidth = this.MinWidth;
            if (st.WindowHeight < this.MinHeight) st.WindowHeight = this.MinHeight;

            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsW = SystemParameters.VirtualScreenWidth;
            double vsH = SystemParameters.VirtualScreenHeight;

            double left = st.WindowLeft;
            double top = st.WindowTop;
            double width = st.WindowWidth;
            double height = st.WindowHeight;

            const double margin = 50;

            if (left + margin > vsLeft + vsW) left = vsLeft + vsW - margin;
            if (top + margin > vsTop + vsH) top = vsTop + vsH - margin;
            if (left + width < vsLeft + margin) left = vsLeft + margin - width;
            if (top + height < vsTop + margin) top = vsTop + margin - height;

            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Left = left;
            this.Top = top;
            this.Width = width;
            this.Height = height;

            var state = (WindowState)st.WindowState;
            if (state == WindowState.Maximized)
                this.WindowState = WindowState.Maximized;
        }
    }
}