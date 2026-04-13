// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;

namespace MusicPlayer
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private string? _currentSortColumn = null;
        private string? _lastHandledEndedPath;
        private DateTime _lastHandledEndedUtc = DateTime.MinValue;
        private const int EndedDuplicateWindowMs = 750;
        private ListSortDirection _currentSortDirection = ListSortDirection.Ascending;
        private string? _resumeFile;
        private double _resumeSeconds;
        private bool _resumePending;
        private bool _uiWantsPlaying = false;
        private bool _isShuttingDown = false;
        private DateTime _lastVolumePopupClosedUtc = DateTime.MinValue;
        private bool _playlistCollapsed = false;
        private double _expandedWidth;
        private double _expandedHeight;
        private double _expandedMinHeight;
        private bool _restorePlaylistCollapsed;
        private const double CollapsedWindowHeight = 130.0;
        private ResizeMode _expandedResizeMode = ResizeMode.CanResize;
        private double _expandedMaxHeight;
        private string? _pendingTrackChangeSource = null;

        private ContextMenu? _defaultPlaylistListContextMenu;
        private bool _sceneEnabled = false;
        private bool _sceneLaneDragActive = false;
        private int _sceneLaneDragIndex = -1;
        private int _sceneWheelHoverLane = -1;
        private readonly double[] _sceneLaneVolumes = [1.0, 1.0, 1.0, 1.0];
        private readonly bool[] _sceneLanePlaying = new bool[4];
        private CancellationTokenSource? _sceneMusicRampCts;



        private void CollapseButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlaylistCollapsed(!_playlistCollapsed);
            RequestSaveState();
        }

        private double GetCollapsedWindowHeight()
        {
            UpdateLayout();

            double nonPlaylistHeight = ActualHeight - PlaylistSection.ActualHeight;

            // Small safety floor in case layout hasn't fully measured yet
            return Math.Max(nonPlaylistHeight + 1, MinHeight);
        }

        private void AnimateWindowHeight(double toHeight)
        {
            var anim = new DoubleAnimation
            {
                To = toHeight,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new QuadraticEase()
            };

            BeginAnimation(Window.HeightProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private void TogglePlaylistCollapsed(bool collapse, bool animate = true)
        {
            if (collapse == _playlistCollapsed)
                return;

            if (collapse)
            {
                UpdateLayout();

                _expandedWidth = Math.Max(Width, MinWidth);

                if (Height > CollapsedWindowHeight + 4)
                    _expandedHeight = Height;

                _expandedMinHeight = MinHeight;
                _expandedMaxHeight = MaxHeight;
                _expandedResizeMode = ResizeMode;

                CollapseButton.Content = ""; // down chevron
                CollapseButton.ToolTip = "Expand Playlist";
                CollapseButton.IsChecked = true;

                PlaylistSection.Visibility = Visibility.Collapsed;

                MinHeight = CollapsedWindowHeight;
                MaxHeight = CollapsedWindowHeight;
                ResizeMode = ResizeMode.CanMinimize;

                Width = MinWidth;

                if (animate)
                    AnimateWindowHeight(CollapsedWindowHeight);
                else
                    Height = CollapsedWindowHeight;

                _playlistCollapsed = true;
            }
            else
            {
                PlaylistSection.Visibility = Visibility.Visible;
                UpdateLayout();

                CollapseButton.Content = ""; // up chevron
                CollapseButton.ToolTip = "Collapse Playlist";
                CollapseButton.IsChecked = false;

                MinHeight = _expandedMinHeight > 0 ? _expandedMinHeight : 200;
                MaxHeight = _expandedMaxHeight > 0 ? _expandedMaxHeight : double.PositiveInfinity;
                ResizeMode = _expandedResizeMode;

                double targetWidth = (_expandedWidth > 0 && !double.IsNaN(_expandedWidth))
                    ? Math.Max(_expandedWidth, MinWidth)
                    : MinWidth;

                double targetHeight = (_expandedHeight > 0 && !double.IsNaN(_expandedHeight))
                    ? _expandedHeight
                    : Math.Max(Height, ActualHeight + PlaylistSection.ActualHeight);

                Width = targetWidth;

                if (animate)
                    AnimateWindowHeight(targetHeight);
                else
                    Height = targetHeight;

                _playlistCollapsed = false;
            }
        }

        //Debug
        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            LogTransport("Window.PreviewMouseDown",
                $"source={e.OriginalSource?.GetType().Name} captured={Mouse.Captured?.GetType().Name ?? "null"}");
        }
        //End Debug

        //private bool _enableTransportDiagnostics = true;
        private readonly string _transportDiagnosticsPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "transport_diagnostics.log");

        private static string ClipLogValue(string? value, int maxLen = 140)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "(null)";

            string s = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= maxLen)
                return s;

            return s.Substring(0, maxLen) + "...";
        }

        private string GetTransportSnapshot()
        {
            int activeCount = (_activePlaylist >= 0 && _activePlaylist < _playlists.Count)
                ? _playlists[_activePlaylist].Tracks.Count
                : -1;

            int playingCount = (_playingPlaylist >= 0 && _playingPlaylist < _playlists.Count)
                ? _playlists[_playingPlaylist].Tracks.Count
                : -1;

            int activeIdx = (_activePlaylist >= 0 && _activePlaylist < _playlists.Count)
                ? _playlists[_activePlaylist].Index
                : -1;

            int playingIdx = (_playingPlaylist >= 0 && _playingPlaylist < _playlists.Count)
                ? _playlists[_playingPlaylist].Index
                : -1;

            int shuffleBagCount = (_playingPlaylist >= 0 && _playingPlaylist < _playlists.Count)
                ? _playlists[_playingPlaylist].ShuffleBag.Count
                : -1;

            int shuffleHistoryCount = (_playingPlaylist >= 0 && _playingPlaylist < _playlists.Count)
                ? _playlists[_playingPlaylist].ShuffleHistory.Count
                : -1;

            bool loopEnabled = WaveformBar != null && WaveformBar.LoopEnabled;
            double loopA = WaveformBar != null ? WaveformBar.LoopA : 0.0;
            double loopB = WaveformBar != null ? WaveformBar.LoopB : 1.0;

            string currentFile = ClipLogValue(_player.CurrentFile);
            string uiFile = ClipLogValue(_uiCurrentFile);

            string posText = _player.Position.ToString(@"hh\:mm\:ss\.fff");
            string durText = _player.Duration?.ToString(@"hh\:mm\:ss\.fff") ?? "(null)";

            return
                $"activePl={_activePlaylist} activeIdx={activeIdx} activeCount={activeCount} " +
                $"playingPl={_playingPlaylist} playingIdx={playingIdx} playingCount={playingCount} " +
                $"repeat={_repeatMode} shuffle={ShuffleEnabled} bagCount={shuffleBagCount} histCount={shuffleHistoryCount} " +
                $"xFade={_xFadeMode} crossfadeArmed={_crossfadeArmed} abCrossfadeArmed={_abCrossfadeArmed} " +
                $"loopEnabled={loopEnabled} loopA={loopA:0.0000} loopB={loopB:0.0000} " +
                $"pendingSrc={(_pendingShuffleCrossfadeSourceIndex?.ToString() ?? "null")} " +
                $"pendingDst={(_pendingShuffleCrossfadeTargetIndex?.ToString() ?? "null")} " +
                $"uiWantsPlaying={_uiWantsPlaying} playbackState={_player.PlaybackState} " +
                $"pos={posText} dur={durText} currentFile=\"{currentFile}\" uiFile=\"{uiFile}\"";
        }

        [Conditional("TRANSPORT_LOG")]
        private void LogTransport(string eventName, string? details = null)
        {
            string prefix = string.IsNullOrWhiteSpace(details)
                ? eventName
                : $"{eventName} | {details}";

            AppendPlaybackDebugLog($"{prefix} | {GetTransportSnapshot()}");
        }

        [Conditional("TRANSPORT_LOG")]
        private void AppendPlaybackDebugLog(string message)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}";
                File.AppendAllText(_transportDiagnosticsPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Never let diagnostics logging crash the app.
            }
        }


        private sealed class PlaylistRowComparer : System.Collections.IComparer
        {
            private readonly string _column;
            private readonly ListSortDirection _dir;

            public PlaylistRowComparer(string column, ListSortDirection dir)
            {
                _column = column;
                _dir = dir;
            }

            public int Compare(object? x, object? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is not PlaylistRow a) return -1;
                if (y is not PlaylistRow b) return 1;

                int result = _column switch
                {
                    "AlbumText" => StringCompare(a.AlbumText, b.AlbumText),
                    "DurationText" => DurationCompare(a.DurationText, b.DurationText),
                    _ => StringCompare(a.DisplayName, b.DisplayName), // DisplayName
                };

                return _dir == ListSortDirection.Ascending ? result : -result;
            }

            private static int StringCompare(string? s1, string? s2)
                => string.Compare(s1 ?? "", s2 ?? "", ignoreCase: true, culture: CultureInfo.CurrentCulture);

            private static int DurationCompare(string? d1, string? d2)
            {
                // Parses "mm:ss" or "hh:mm:ss". Unknowns go last.
                var t1 = TryParseDuration(d1, out var a) ? a : TimeSpan.MaxValue;
                var t2 = TryParseDuration(d2, out var b) ? b : TimeSpan.MaxValue;
                return t1.CompareTo(t2);
            }

            private static bool TryParseDuration(string? text, out TimeSpan ts)
            {
                ts = default;
                if (string.IsNullOrWhiteSpace(text) || text.Contains("--")) return false;

                // Try standard formats first
                if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out ts))
                    return true;

                // Handle mm:ss explicitly
                var parts = text.Split(':');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int m) &&
                    int.TryParse(parts[1], out int s))
                {
                    ts = new TimeSpan(0, m, s);
                    return true;
                }

                return false;
            }
        }

        internal static string CleanDisplayTitle(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);

            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string s = name.Trim();

            for (int i = 0; i < 3; i++)
            {
                string before = s;

                s = Regex.Replace(
                    s,
                    @"^\s*\d+\s*([\-._)\]]\s*|\s+)",
                    "",
                    RegexOptions.CultureInvariant);

                s = Regex.Replace(
                    s,
                    @"^\s*-\s*",
                    "",
                    RegexOptions.CultureInvariant);

                s = s.Trim();

                if (s == before)
                    break;
            }

            return string.IsNullOrWhiteSpace(s) ? name.Trim() : s;
        }

        private void VolumeButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (VolumePopup.IsOpen)
            {
                VolumePopup.IsOpen = false;
                e.Handled = true; // prevent Click from firing and reopening it
            }
        }

        public sealed class MultiplyConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is double d && parameter != null &&
                    double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mul))
                {
                    return d * mul;
                }
                return value ?? 0.0;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
                => Binding.DoNothing;
        }


        private readonly Dictionary<string, string> _durationCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _albumCache = new(StringComparer.OrdinalIgnoreCase);
        // Thread-safety for metadata caches
        private readonly object _metaCacheLock = new();

        // Cancel previous population job when a new Refresh happens
        private CancellationTokenSource? _metaCts;

        private void SortPlaylist(string columnKey)
        {
            if (PlaylistList?.ItemsSource == null)
                return;

            // Toggle direction if clicking the same column again
            if (string.Equals(_currentSortColumn, columnKey, StringComparison.Ordinal))
            {
                _currentSortDirection =
                    _currentSortDirection == ListSortDirection.Ascending
                        ? ListSortDirection.Descending
                        : ListSortDirection.Ascending;
            }
            else
            {
                _currentSortColumn = columnKey;
                _currentSortDirection = ListSortDirection.Ascending;
            }

            Active.SortColumn = _currentSortColumn;
            Active.SortDirection = _currentSortDirection;
            RequestSaveState();

            var view = CollectionViewSource.GetDefaultView(PlaylistList.ItemsSource);
            if (view == null)
                return;

            // Use a custom comparer so Duration sorts numerically, not lexicographically
            var sortColumn = _currentSortColumn ?? columnKey ?? "Path";

            if (view is ListCollectionView lcv)
            {
                lcv.CustomSort = new PlaylistRowComparer(sortColumn, _currentSortDirection);
            }
            else
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(sortColumn, _currentSortDirection));
            }

            view.Refresh();

            // Keep view's "current" aligned to the playing track after sort
            if (Active.Index >= 0 && Active.Index < Active.Tracks.Count &&
                PlaylistList.ItemsSource is IEnumerable<PlaylistRow> rows)
            {
                var curRow = rows.FirstOrDefault(r => r.SourceIndex == Active.Index);

                if (curRow != null)
                    view.MoveCurrentTo(curRow);
            }
        }

        private bool PruneMissingFilesFromAllPlaylists()
        {
            bool removedAny = false;

            foreach (var pl in _playlists)
            {
                if (pl.Tracks.Count == 0)
                    continue;

                // Remove missing files (keep only paths that still exist)
                int before = pl.Tracks.Count;
                pl.Tracks.RemoveAll(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p));
                if (pl.Tracks.Count != before)
                    removedAny = true;

                // Clamp index safely (since we might have removed stuff)
                if (pl.Tracks.Count == 0)
                {
                    pl.Index = -1;
                    pl.ShuffleBag.Clear();
                    pl.ShuffleHistory.Clear();
                }
                else
                {
                    pl.Index = Math.Clamp(pl.Index, -1, pl.Tracks.Count - 1);
                }
            }

            // Active playlist safety
            if (_playlists.Count == 0)
            {
                // Should never happen in your app, but keep it safe
                _playlists.Add(new PlaylistState { Name = "Playlist 1" });
                _activePlaylist = 0;
                removedAny = true;
            }
            else
            {
                _activePlaylist = Math.Clamp(_activePlaylist, 0, _playlists.Count - 1);
            }

            return removedAny;
        }

        private void SortPlaylistAlphabetically(int playlistIndex)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return;

            var playlist = _playlists[playlistIndex];
            if (playlist.Tracks.Count <= 1)
                return;

            string? currentTrack = null;

            if (playlist.Index >= 0 && playlist.Index < playlist.Tracks.Count)
                currentTrack = playlist.Tracks[playlist.Index];

            // Sort by filename, case-insensitive
            playlist.Tracks.Sort((a, b) =>
                string.Compare(
                    Path.GetFileName(a),
                    Path.GetFileName(b),
                    StringComparison.OrdinalIgnoreCase));

            // Restore current track index
            if (!string.IsNullOrEmpty(currentTrack))
                playlist.Index = playlist.Tracks.FindIndex(p =>
                    string.Equals(p, currentTrack, StringComparison.OrdinalIgnoreCase));

            // If this playlist is currently active, refresh UI
            if (playlistIndex == _activePlaylist)
            {
                RefreshPlaylistUI();
                RequestSaveState();

                if (ShuffleEnabled)
                {
                    RebuildShuffleForTargetPlaylist();
                }
            }
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            // If the popup just closed from this same click, do not reopen it.
            if ((DateTime.UtcNow - _lastVolumePopupClosedUtc).TotalMilliseconds < 200)
                return;

            VolumePopup.IsOpen = !VolumePopup.IsOpen;
        }

        private ToolTip? _volumeToolTip;
        private bool _isVolumeDragging;
        private readonly DispatcherTimer _volumePopupAutoCloseTimer = new();

        // ---- Spectrum UI coalescing (prevents Dispatcher backlog) ----
        private float[]? _latestBands;
        private int _spectrumUiPending; // 0/1

        private readonly AudioPlayer _player = new();
        private readonly SceneAudioEngine _sceneAudio = new();

        // ---- Spectrum fade-out on Stop ----
        private float[]? _lastSpectrumBands;                 // last bands from audio thread
        private readonly DispatcherTimer _spectrumFadeTimer = new();
        private DateTime _spectrumFadeStartUtc = DateTime.MinValue;

        private const int SpectrumFadeMs = 320;              // tweak: 250–450 feels good
        private const int SpectrumFadeTickMs = 25;           // ~40 FPS

        // ✅ Use the same glyphs already used successfully by the other transport buttons
        private const string GlyphPlay = "\uE768";
        private const string GlyphPause = "\uE769";

        // Playlist-type glyphs
        private const string GlyphAmbience = "\uE81E"; // leaf-ish / ambience marker

        // Volume glyphs
        private const string GlyphVolume0 = "\uE992"; // muted / no waves
        private const string GlyphVolume1 = "\uE993"; // low / one wave
        private const string GlyphVolume2 = "\uE994"; // medium / two waves
        private const string GlyphVolume3 = "\uE995"; // high / three waves

        private static bool PlaylistHasAnyVoicePhrases(PlaylistState playlist)
        {
            return !string.IsNullOrWhiteSpace(playlist.VoiceTriggerPhrase1) ||
                   !string.IsNullOrWhiteSpace(playlist.VoiceTriggerPhrase2) ||
                   !string.IsNullOrWhiteSpace(playlist.VoiceTriggerPhrase3);
        }

        private string GetPlaylistVoiceStatusToolTip(PlaylistState playlist)
        {
            return PlaylistHasAnyVoicePhrases(playlist)
                ? "Key Phrases Set"
                : "No Key Phrases Set";
        }

        private object BuildPlaylistTabHeader(int playlistIndex, PlaylistState playlist)
        {
            bool hasPhrases = PlaylistHasAnyVoicePhrases(playlist);
            bool isAmbiencePlaylist = string.Equals(
                playlist.Name,
                AmbiencePlaylistName,
                StringComparison.OrdinalIgnoreCase);

            var root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var title = new TextBlock
            {
                Text = playlist.Name,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };

            root.Children.Add(title);

            if (!isAmbiencePlaylist)
            {
                var micButton = new Button
                {
                    Content = new TextBlock
                    {
                        Text = GlyphMic,
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 12,
                        Foreground = hasPhrases
                            ? new SolidColorBrush(Color.FromRgb(150, 213, 150))
                            : new SolidColorBrush(Color.FromRgb(155, 155, 155))
                    },
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 0, 0),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    ToolTip = "Set Key Phrases"
                };

                micButton.Click += (_, e) =>
                {
                    e.Handled = true;
                    BeginSetVoiceTriggerPhrases(playlistIndex);
                };

                root.Children.Add(micButton);
            }

            string tooltip = isAmbiencePlaylist
                ? "Ambience Playlist"
                : GetPlaylistVoiceStatusToolTip(playlist);

            ToolTipService.SetToolTip(root, tooltip);
            return root;
        }

        // ---------- Multi-playlist (tabs) ----------
        private sealed class PlaylistState
        {
            public string Name { get; set; } = "Playlist 1";
            public List<string> Tracks { get; } = new();
            public int Index { get; set; } = -1;

            public string? SortColumn { get; set; } = null;
            public ListSortDirection SortDirection { get; set; } = ListSortDirection.Ascending;

            public bool VoiceTriggerEnabled { get; set; } = false;
            public string? VoiceTriggerPhrase1 { get; set; } = null;
            public string? VoiceTriggerPhrase2 { get; set; } = null;
            public string? VoiceTriggerPhrase3 { get; set; } = null;
            public int VoiceTriggerLastIndex { get; set; } = -1;
            public int VoiceTriggerCooldownMs { get; set; } = DefaultVoiceTriggerCooldownMs;

            // runtime only; not persisted
            public DateTime VoiceTriggerLastFireUtc { get; set; } = DateTime.MinValue;

            // Shuffle state per-playlist
            public readonly Random Rng = new();
            public readonly List<int> ShuffleBag = new();
            public readonly Stack<int> ShuffleHistory = new();
        }

        private readonly List<PlaylistState> _playlists = new();
        private int _activePlaylist = 0;
        private int _playingPlaylist = 0;

        private static readonly string[] SupportedExt =
            [".mp3", ".m4a", ".ogg", ".wav", ".flac", ".aac", ".wma"];

        private readonly DispatcherTimer _uiTimer;

        private bool ShuffleEnabled => ShuffleToggle.IsChecked == true;

        // ---------- Drag-to-reorder playlist ----------
        private Point _playlistDragStartPoint;
        private bool _playlistDragArmed;
        private const string PlaylistDragFormat = "MusicPlayer.PlaylistIndex";

        // ---------- Drag insert indicator ----------
        private AdornerLayer? _playlistAdornerLayer;
        private InsertionAdorner? _insertionAdorner;
        private int _currentInsertIndex = -1;

        // ---------- Scrubbing ----------
        private bool _scrubWasPlaying;
        private const double ScrubVisualEpsilon = 0.0005;
        private DateTime _lastResumeSaveUtc = DateTime.MinValue;
        private DateTime _lastScrubSeekUtc = DateTime.MinValue;
        private const int ScrubThrottleMs = 90;

        private string? _uiCurrentFile;

        public MainWindow()
        {
            InitializeComponent();

            _defaultPlaylistListContextMenu = PlaylistList.ContextMenu;

            SceneStrip.SizeChanged += (_, __) =>
            {
                SetSceneLaneVolumeVisual(0, _sceneLaneVolumes[0]);
                SetSceneLaneVolumeVisual(1, _sceneLaneVolumes[1]);
                SetSceneLaneVolumeVisual(2, _sceneLaneVolumes[2]);
                SetSceneLaneVolumeVisual(3, _sceneLaneVolumes[3]);
            };

            AddHandler(UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(MainWindow_GlobalPreviewMouseWheel),
                true);

            Closing += MainWindow_Closing;
            _spectrumFadeTimer.Interval = TimeSpan.FromMilliseconds(SpectrumFadeTickMs);
            _spectrumFadeTimer.Tick += (_, __) => SpectrumFadeTick();

            // ✅ Hook spectrum -> RainOverlay (must be after InitializeComponent so RainOverlay exists)
            _player.SpectrumAvailable += bands =>
            {
                // Always keep a copy for Stop fade-out (do not hold onto audio thread buffers)
                if (bands != null && bands.Length > 0)
                {
                    var copy = new float[bands.Length];
                    Array.Copy(bands, copy, bands.Length);
                    _lastSpectrumBands = copy;

                    // If a UI update is already pending, preserve peaks instead of overwriting.
                    var existing = _latestBands;
                    if (existing != null && existing.Length == copy.Length)
                    {
                        for (int i = 0; i < copy.Length; i++)
                        {
                            if (copy[i] > existing[i])
                                existing[i] = copy[i];
                        }
                    }
                    else
                    {
                        _latestBands = copy;
                    }
                }
                else
                {
                    _latestBands = null;
                }

                // If a UI update is already queued, don't queue another
                if (Interlocked.Exchange(ref _spectrumUiPending, 1) == 1)
                    return;

                Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(() =>
                    {
                        try
                        {
                            // If audio is producing spectrum again, cancel any fade-out in progress.
                            if (_spectrumFadeTimer.IsEnabled)
                                _spectrumFadeTimer.Stop();

                            var b = _latestBands;
                            RainOverlay?.SetBands(b ?? Array.Empty<float>());
                        }
                        finally
                        {
                            _latestBands = null;
                            Interlocked.Exchange(ref _spectrumUiPending, 0);
                        }
                    })
                );
            };


            // Keep waveform content width in sync with ScrollViewer viewport when window is resized
            WaveformScroller.SizeChanged += (_, __) => ApplyWaveformZoom();

            InitStatePersistence();
            _voiceTriggerService.PhraseRecognized += VoiceTriggerService_PhraseRecognized;
            _isLoadingState = true;

            if (!TryLoadState())
            {
                _playlists.Add(new PlaylistState { Name = "Playlist 1" });
                _activePlaylist = 0;
            }

            EnsureAmbiencePlaylistExists();

            // prune after we have playlists, before we build UI
            bool pruned = PruneMissingFilesFromAllPlaylists();

            BuildTabs();
            SelectPlaylist(_activePlaylist);

            _isLoadingState = false;

            // persist once if we pruned anything
            if (pruned)
                RequestSaveState();

            ShuffleToggle.Checked += (_, __) =>
            {
                RebuildShuffleForTargetPlaylist();
                LogTransport("ShuffleToggle.Checked");
                RequestSaveState();
            };

            ShuffleToggle.Unchecked += (_, __) =>
            {
                ClearShuffleStateForAllPlaylists();
                LogTransport("ShuffleToggle.Unchecked");
                RequestSaveState();
            };

            NormalizeToggle.Checked += (_, __) => { _player.NormalizeEnabled = true; RequestSaveState(); };
            NormalizeToggle.Unchecked += (_, __) => { _player.NormalizeEnabled = false; RequestSaveState(); };

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _uiTimer.Tick += (_, __) => UpdatePlaybackUI();

            Loaded += (_, __) =>
            {
                if (_restorePlaylistCollapsed)
                    TogglePlaylistCollapsed(true, animate: false);

                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    SetSceneLaneVolumeVisual(0, _sceneLaneVolumes[0]);
                    SetSceneLaneVolumeVisual(1, _sceneLaneVolumes[1]);
                    SetSceneLaneVolumeVisual(2, _sceneLaneVolumes[2]);
                    SetSceneLaneVolumeVisual(3, _sceneLaneVolumes[3]);
                }));

                // ✅ NEW: initialize play/pause buttons to Play state
                for (int i = 0; i < 4; i++)
                {
                    _sceneLanePlaying[i] = false;

                    if (i >= 1)
                        UpdateSceneLanePlayButton(i);
                }
                bool selectResumePlaylistOnFirstTrackChange = _resumePending;

                // Hook Waveform control events (chevrons + scrubbing)
                WaveformBar.SeekRequested += frac =>
                {
                    if (_isScrubbing)
                        return;

                    var dur = _player.Duration;
                    if (!dur.HasValue || dur.Value.TotalSeconds <= 0.01)
                        return;

                    bool shouldResume = _uiWantsPlaying;

                    _player.SeekFraction(Math.Clamp(frac, 0.0, 1.0), resume: shouldResume);

                    _uiWantsPlaying = shouldResume;
                    SyncPlayPauseButton();
                };

                WireLoopUiEvents();

                // Pause while scrubbing; resume if it was playing
                WaveformBar.ScrubStarted += () =>
                {
                    var dur = _player.Duration;
                    if (!dur.HasValue || dur.Value.TotalSeconds <= 0.01)
                        return;

                    _isScrubbing = true;
                    _scrubWasPlaying = _uiWantsPlaying;

                    if (_scrubWasPlaying)
                    {
                        PlayPauseButton.Content = GlyphPause;
                        _player.Pause(reason: "WaveformBar.ScrubStarted");
                    }
                };

                UpdateRepeatButtonVisuals();
                UpdateXFadeButtonVisuals();
                UpdateVoiceCaptureButtonVisuals();
                SetupVolumePopup();

                _player.NormalizeEnabled = NormalizeToggle.IsChecked == true;

                SceneButton.IsChecked = _sceneEnabled;
                InitializeSceneStripUi();

                // ✅ Ensure initial play glyph is correct
                _uiWantsPlaying = false;
                SyncPlayPauseButton();

                _player.TrackChanged += path =>
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        _isScrubbing = false;
                        _draggingHandle = LoopHandle.None;
                        try { WaveformBar.ReleaseMouseCapture(); } catch { }

                        _uiCurrentFile = path;
                        string source = _pendingTrackChangeSource ?? "Unknown";
                        _pendingTrackChangeSource = null;

                        int actualPlaylist = FindPlaylistIndexContainingTrack(path);
                        if (actualPlaylist >= 0)
                        {
                            SetPlayingPlaylist(actualPlaylist);

                            if (selectResumePlaylistOnFirstTrackChange)
                            {
                                selectResumePlaylistOnFirstTrackChange = false;
                                SelectPlaylist(actualPlaylist);
                            }
                        }

                        LogTransport("TrackChanged",
                            $"source={source} playingPlaylist={_playingPlaylist} activePlaylist={_activePlaylist} path=\"{ClipLogValue(path)}\"");

                        // Do not treat TrackChanged as the end of the vulnerable crossfade window.
                        // Late/stale PlaybackEnded callbacks from the outgoing track can still arrive
                        // after the new track is already current.
                        SyncCrossfadeStateOnTrackChanged(path);
                        _abCrossfadeArmed = false;

                        // IMPORTANT:
                        // Reset ended-event suppression for every TrackChanged, even if the file path
                        // is the same. Same-file loop crossfades and repeat-style restarts are still
                        // a new playback cycle and must be allowed to end normally later.
                        _lastHandledEndedPath = null;
                        _lastHandledEndedUtc = DateTime.MinValue;

                        TrackTitleText.Text = !string.IsNullOrWhiteSpace(path)
                            ? CleanDisplayTitle(path)
                            : "No track loaded";
                        TimeText.Text = "00:00 / --:--";
                        WaveformBar.Progress = 0.0;

                        bool consumedPendingCrossfadeTarget = false;

                        if (_pendingShuffleCrossfadeTargetIndex.HasValue &&
                            _pendingShuffleCrossfadeTargetIndex.Value >= 0 &&
                            _pendingShuffleCrossfadeTargetIndex.Value < Playing.Tracks.Count &&
                            !string.IsNullOrWhiteSpace(path) &&
                            string.Equals(
                                Playing.Tracks[_pendingShuffleCrossfadeTargetIndex.Value],
                                path,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            int targetIdx = _pendingShuffleCrossfadeTargetIndex.Value;
                            int? sourceIdx = _pendingShuffleCrossfadeSourceIndex;

                            if (ShuffleEnabled &&
                                sourceIdx.HasValue &&
                                sourceIdx.Value >= 0 &&
                                sourceIdx.Value < Playing.Tracks.Count &&
                                sourceIdx.Value != targetIdx)
                            {
                                int bagPos = Playing.ShuffleBag.IndexOf(targetIdx);
                                if (bagPos >= 0)
                                    Playing.ShuffleBag.RemoveAt(bagPos);

                                Playing.ShuffleHistory.Push(sourceIdx.Value);
                            }

                            Playing.Index = targetIdx;
                            consumedPendingCrossfadeTarget = true;

                            LogTransport(
                                "TrackChanged.ConsumePendingCrossfadeTarget",
                                $"targetIdx={targetIdx} sourceIdx={(sourceIdx?.ToString() ?? "null")}");
                        }

                        _pendingShuffleCrossfadeSourceIndex = null;
                        _pendingShuffleCrossfadeTargetIndex = null;

                        // Only fall back to path-based sync when we truly do not have an explicit
                        // committed crossfade target.
                        if (!consumedPendingCrossfadeTarget && !string.IsNullOrWhiteSpace(path))
                        {
                            int? idx = FindTrackIndexInPlaylist(_playingPlaylist, path);
                            if (idx.HasValue)
                            {
                                Playing.Index = idx.Value;
                                LogTransport("TrackChanged.SyncPlayingIndex", $"idx={idx.Value}");
                            }
                        }

                        // Only sync the visible playlist selection if the user is currently
                        // looking at the playlist that is actually playing.
                        if (_activePlaylist == _playingPlaylist)
                            SyncPlaylistSelection();

                        _waveRequestedPath = path;

                        // Restore REAL waveform here
                        if (!string.IsNullOrWhiteSpace(path))
                            _ = EnsureWaveformAsync(path);
                        else
                            WaveformBar.Peaks = null;
                    }));
                };

                ApplyResumeIfPending();

                _player.PlaybackEnded += () =>
                {
                    int generationAtEvent = _player.CurrentGeneration;
                    // Snapshot the UI's current file at callback time.
                    // During crossfade this may already be the incoming track, so do not treat it
                    // as authoritative for "which track actually ended".
                    string? playbackEndedUiPathSnapshot = _uiCurrentFile;

                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        if (_isShuttingDown)
                            return;

                        if (generationAtEvent != _player.CurrentGeneration)
                        {
                            LogTransport("PlaybackEnded.SuppressedStaleGeneration",
                                    $"eventGen={generationAtEvent} currentGen={_player.CurrentGeneration} uiSnapshot=\"{ClipLogValue(playbackEndedUiPathSnapshot)}\"");
                            return;
                        }

                        LogTransport("PlaybackEnded.Event",
                            $"uiSnapshot=\"{ClipLogValue(playbackEndedUiPathSnapshot)}\" current=\"{ClipLogValue(_uiCurrentFile)}\" crossfadeActive={_crossfadeTransitionActive}");

                        var now = DateTime.UtcNow;

                        if (TrySuppressPlaybackEndedDuringCrossfade(playbackEndedUiPathSnapshot, now))
                            return;

                        if (_crossfadeTransitionActive)
                        {
                            LogTransport("PlaybackEnded.SuppressedDuringActiveTransition");
                            return;
                        }

                        string? endedPathForDuplicateSuppression = playbackEndedUiPathSnapshot;

                        // Suppress only true duplicate end events that arrive almost immediately.
                        // Note: this is based on the UI-side snapshot and is therefore only a best-effort
                        // duplicate filter. The real crossfade safety is the suppression window above.
                        if (!string.IsNullOrWhiteSpace(endedPathForDuplicateSuppression) &&
                            string.Equals(_lastHandledEndedPath, endedPathForDuplicateSuppression, StringComparison.OrdinalIgnoreCase) &&
                            (now - _lastHandledEndedUtc).TotalMilliseconds < EndedDuplicateWindowMs)
                        {
                            LogTransport(
                                "PlaybackEnded.SuppressedDuplicate",
                                $"endedPath=\"{ClipLogValue(endedPathForDuplicateSuppression)}\"");
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(endedPathForDuplicateSuppression))
                        {
                            _lastHandledEndedPath = endedPathForDuplicateSuppression;
                            _lastHandledEndedUtc = now;
                        }

                        // Natural end-of-track should never preserve a near-end resume point.
                        _resumePending = false;
                        _resumeSeconds = 0.0;
                        _resumeFile = null;

                        LogTransport(
                            "PlaybackEnded.DispatchHandleTrackEnded",
                            $"uiSnapshot=\"{ClipLogValue(playbackEndedUiPathSnapshot)}\"");

                        HandleTrackEnded();
                    }));
                };

                _player.PlaybackFailed += msg =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_isShuttingDown)
                    return;

                // Try to detect "missing file / can't open input" cases from the ffmpeg error text.
                bool looksLikeMissing =
                    msg.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("could not", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    msg.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("ExitCode: -2", StringComparison.OrdinalIgnoreCase) >= 0;

                // Try to extract the file path from the message:
                // it contains a line like: "File: C:\path\song.mp3"
                string? fileFromMsg = null;
                try
                {
                    const string marker = "File:";
                    int i = msg.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (i >= 0)
                    {
                        int start = i + marker.Length;
                        int end = msg.IndexOf('\n', start);
                        if (end < 0) end = msg.Length;
                        fileFromMsg = msg.Substring(start, end - start).Trim();
                    }
                }
                catch { /* ignore */ }

                // If it looks missing, show a friendly warning and remove the track from whichever playlist has it.
                if (looksLikeMissing && !string.IsNullOrWhiteSpace(fileFromMsg))
                {
                    MessageBox.Show(
                        this,
                        "Song file is missing.\n\nCheck the file path and re-add the song.",
                        "Missing file",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    // Remove from any playlist that contains this exact path.
                    bool removedAny = false;

                    for (int pi = 0; pi < _playlists.Count; pi++)
                    {
                        var pl = _playlists[pi];
                        int idx = pl.Tracks.FindIndex(p => string.Equals(p, fileFromMsg, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            bool wasActive = (pi == _activePlaylist);

                            // If removing currently playing file, stop first (safe).
                            if (!string.IsNullOrEmpty(_player.CurrentFile) &&
                                string.Equals(_player.CurrentFile, fileFromMsg, StringComparison.OrdinalIgnoreCase))
                            {
                                _player.Stop(reason: "PlaybackFailed:RemoveMissingCurrentFile");
                            }

                            pl.Tracks.RemoveAt(idx);

                            // Clamp index
                            if (pl.Tracks.Count == 0) pl.Index = -1;
                            else
                            {
                                if (pl.Index > idx) pl.Index--;
                                pl.Index = Math.Clamp(pl.Index, 0, pl.Tracks.Count - 1);
                            }

                            removedAny = true;

                            if (wasActive)
                                RefreshPlaylistUI();
                        }
                    }

                    if (removedAny)
                        RequestSaveState();

                    // Don’t call HandleTrackEnded() here; we already removed the bad entry.
                    return;
                }

                // Default behavior for non-missing errors:
                MessageBox.Show(this, msg, "Playback failed", MessageBoxButton.OK, MessageBoxImage.Error);
                HandleTrackEnded();
            }));
        };

                // ✅ Initialize waveform zoom sizing + keep it correct on resize.
                ApplyWaveformZoom();
                WaveformScroller.SizeChanged += (_, __) => ApplyWaveformZoom();

                _uiTimer.Start();
                ApplyVoiceCaptureMode();
            };

            Closed += (_, __) =>
            {
                _uiTimer.Stop();
                _waveCts?.Cancel();
                try { _voiceTriggerService.Dispose(); } catch { }
                try { _sceneAudio.Dispose(); } catch { }
            };
        }

        // ---------- Titlebar ----------
        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                return;

            try { DragMove(); } catch { }
        }

        private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string readmePath = Path.Combine(baseDir, "Readme.txt");

                if (!File.Exists(readmePath))
                {
                    MessageBox.Show(
                        this,
                        "Readme.txt was not found next to the application executable.",
                        "Readme not found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = readmePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Unable to open Readme.txt.\n\n" + ex.Message,
                    "Open Readme failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ---------- Window-level keys ----------
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (PlaylistList.IsKeyboardFocusWithin)
                {
                    PlaylistList.SelectAll();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key != Key.Delete)
                return;

            if (!PlaylistList.IsKeyboardFocusWithin && PlaylistList.SelectedIndex < 0)
                return;

            if (PlaylistList.SelectedItems.Count > 1)
            {
                RemoveSelected();
                e.Handled = true;
                return;
            }

            int idx = PlaylistList.SelectedIndex;
            if (idx >= 0)
            {
                RemoveAt(idx);
                e.Handled = true;
            }
        }

        private bool TryLoadCurrentTrackOrHandleMissingForPlayingPlaylist()
        {
            if (Playing.Tracks.Count == 0 || Playing.Index < 0 || Playing.Index >= Playing.Tracks.Count)
            {
                LogTransport("TryLoadCurrentTrackOrHandleMissingForPlayingPlaylist.Skip", "reason=InvalidPlayingIndex");
                return false;
            }

            string path = Playing.Tracks[Playing.Index];

            if (!string.IsNullOrEmpty(_player.CurrentFile) &&
                string.Equals(_player.CurrentFile, path, StringComparison.OrdinalIgnoreCase))
            {
                LogTransport("TryLoadCurrentTrackOrHandleMissingForPlayingPlaylist.AlreadyLoaded", $"path=\"{ClipLogValue(path)}\"");
                return true;
            }

            try
            {
                LogTransport("TryLoadCurrentTrackOrHandleMissingForPlayingPlaylist.LoadAttempt", $"path=\"{ClipLogValue(path)}\"");
                _player.Load(path);
                LogTransport("TryLoadCurrentTrackOrHandleMissingForPlayingPlaylist.LoadSuccess", $"path=\"{ClipLogValue(path)}\"");
                return true;
            }
            catch (FileNotFoundException)
            {
                LogTransport("TryLoadCurrentTrackOrHandleMissingForPlayingPlaylist.MissingFile", $"path=\"{ClipLogValue(path)}\"");

                MessageBox.Show(
                    this,
                    "Song file is missing.\n\nCheck the file path and re-add the song.",
                    "Missing file",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                int removeIndex = Playing.Index;
                int exact = Playing.Tracks.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (exact >= 0) removeIndex = exact;

                RemoveAt(removeIndex);
                RequestSaveState();
                return false;
            }
            catch (Exception ex)
            {
                LogTransport(
                    "TryLoadCurrentTrackOrHandleMissingForPlayingPlaylist.LoadFailed",
                    $"path=\"{ClipLogValue(path)}\" error=\"{ClipLogValue(ex.Message)}\"");

                MessageBox.Show(
                    this,
                    "Unable to load this song.\n\n" + ex.Message,
                    "Playback error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return false;
            }
        }

        private int? GetAdjacentIndexByStoredView(int playlistIndex, int currentIndex, int delta, bool wrap = false)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return null;

            var playlist = _playlists[playlistIndex];
            if (playlist.Tracks.Count == 0 || currentIndex < 0 || currentIndex >= playlist.Tracks.Count)
                return null;

            var rows = playlist.Tracks
                .Select((p, i) =>
                {
                    string? dur = null;
                    string? alb = null;

                    lock (_metaCacheLock)
                    {
                        _durationCache.TryGetValue(p, out dur);
                        _albumCache.TryGetValue(p, out alb);
                    }

                    return new PlaylistRow(p, i, dur, alb);
                })
                .ToList();

            if (rows.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(playlist.SortColumn))
            {
                rows.Sort((a, b) =>
                    new PlaylistRowComparer(playlist.SortColumn, playlist.SortDirection).Compare(a, b));
            }

            int curViewIndex = rows.FindIndex(r => r.SourceIndex == currentIndex);
            if (curViewIndex < 0)
                return null;

            int nextViewIndex = curViewIndex + delta;

            if (wrap)
            {
                if (nextViewIndex < 0)
                    nextViewIndex = rows.Count - 1;
                else if (nextViewIndex >= rows.Count)
                    nextViewIndex = 0;
            }
            else
            {
                if (nextViewIndex < 0 || nextViewIndex >= rows.Count)
                    return null;
            }

            var nextRow = rows[nextViewIndex];
            return nextRow.SourceIndex;
        }

        private int? GetAdjacentIndexByView(int delta, bool wrap = false)
        {
            // When the Scenes tab is selected, PlaylistList contains SceneRow objects,
            // not PlaylistRow objects. In that case, do not use the visible UI list.
            // Instead, use the active playlist's stored sorted view.
            if (_isScenesTabSelected)
            {
                return GetAdjacentIndexByStoredView(_activePlaylist, Active.Index, delta, wrap);
            }

            if (PlaylistList?.ItemsSource == null)
                return null;

            var view = CollectionViewSource.GetDefaultView(PlaylistList.ItemsSource);
            if (view == null || view.IsEmpty)
                return null;

            var rows = view.OfType<PlaylistRow>().ToList();
            if (rows.Count == 0)
                return null;

            int curSourceIndex = Active.Index;
            if (curSourceIndex < 0 || curSourceIndex >= Active.Tracks.Count)
                return null;

            int curViewIndex = rows.FindIndex(r => r.SourceIndex == curSourceIndex);
            if (curViewIndex < 0)
                return null;

            int nextViewIndex = curViewIndex + delta;

            if (wrap)
            {
                if (nextViewIndex < 0)
                    nextViewIndex = rows.Count - 1;
                else if (nextViewIndex >= rows.Count)
                    nextViewIndex = 0;
            }
            else
            {
                if (nextViewIndex < 0 || nextViewIndex >= rows.Count)
                    return null;
            }

            PlaylistRow nextRow = rows[nextViewIndex];

            return (nextRow.SourceIndex >= 0 && nextRow.SourceIndex < Active.Tracks.Count)
                ? nextRow.SourceIndex
                : null;
        }

        private void BeginSpectrumFadeOut()
        {
            // If we have nothing cached, just hard-clear immediately.
            if (_lastSpectrumBands == null || _lastSpectrumBands.Length == 0)
            {
                RainOverlay?.SetBands(Array.Empty<float>());
                return;
            }

            _spectrumFadeStartUtc = DateTime.UtcNow;

            // Kick one frame immediately so it responds fast
            SpectrumFadeTick();

            if (!_spectrumFadeTimer.IsEnabled)
                _spectrumFadeTimer.Start();
        }

        private void SpectrumFadeTick()
        {
            if (_lastSpectrumBands == null || _lastSpectrumBands.Length == 0)
            {
                _spectrumFadeTimer.Stop();
                RainOverlay?.SetBands(Array.Empty<float>());
                return;
            }

            var elapsedMs = (DateTime.UtcNow - _spectrumFadeStartUtc).TotalMilliseconds;
            double t = elapsedMs / SpectrumFadeMs;

            if (t >= 1.0)
            {
                _spectrumFadeTimer.Stop();

                // Final hard clear
                var zeros = new float[_lastSpectrumBands.Length];
                RainOverlay?.SetBands(zeros);
                return;
            }

            // Smooth easing: fast at first, then gentle near zero
            // scale goes 1 -> 0
            double scale = 1.0 - t;
            scale *= scale; // ease-out quadratic

            var faded = new float[_lastSpectrumBands.Length];
            for (int i = 0; i < faded.Length; i++)
                faded[i] = (float)(_lastSpectrumBands[i] * scale);

            RainOverlay?.SetBands(faded);
        }

        private void CancelWaveformInteraction()
        {
            _draggingHandle = LoopHandle.None;
            _isScrubbing = false;
            _scrubWasPlaying = false;

            try { WaveformBar.ReleaseMouseCapture(); } catch { }
        }

        private static void SetGreenHighlight(ButtonBase button, bool isActive)
        {
            if (isActive)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(46, 59, 46));
                button.BorderBrush = new SolidColorBrush(Color.FromRgb(102, 170, 102));
            }
            else
            {
                button.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                button.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 51, 51));
            }
        }

        private float GetCombinedMusicOutputVolume()
        {
            float master = (float)Math.Clamp(VolumeSlider.Value / 100.0, 0.0, 1.0);

            // Lane 0 = main music only
            float musicSceneVolume = _sceneEnabled
                ? (float)Math.Clamp(_sceneLaneVolumes[0], 0.0, 1.0)
                : 1.0f;

            return master * musicSceneVolume;
        }

        private void ApplySceneAmbienceVolumesAndRefreshUi()
        {
            float master = (float)Math.Clamp(VolumeSlider.Value / 100.0, 0.0, 1.0);

            // Lanes 1..3 = ambience lanes in SceneAudioEngine
            for (int laneIndex = 1; laneIndex <= 3; laneIndex++)
            {
                float laneVolume = (float)Math.Clamp(_sceneLaneVolumes[laneIndex], 0.0, 1.0);
                _sceneAudio.SetLaneVolume(SceneUiLaneToEngineLane(laneIndex), master * laneVolume);
            }

            UpdateVolumeButtonGlyph();
        }

        private void CancelSceneMusicRamp()
        {
            if (_sceneMusicRampCts == null)
                return;

            try { _sceneMusicRampCts.Cancel(); } catch { }
            try { _sceneMusicRampCts.Dispose(); } catch { }
            _sceneMusicRampCts = null;
        }

        private async Task RampMusicOutputVolumeAsync(float targetVolume, int durationMs = 1000)
        {
            CancelSceneMusicRamp();
            _sceneMusicRampCts = new CancellationTokenSource();
            var token = _sceneMusicRampCts.Token;

            float startVolume = _player.Volume;
            const int steps = 20;
            int delayMs = Math.Max(1, durationMs / steps);

            try
            {
                for (int i = 1; i <= steps; i++)
                {
                    token.ThrowIfCancellationRequested();

                    float t = (float)i / steps;
                    _player.Volume = startVolume + ((targetVolume - startVolume) * t);

                    await Task.Delay(delayMs, token);
                }

                _player.Volume = targetVolume;
            }
            catch (OperationCanceledException)
            {
                // Intentionally ignored.
            }
        }

        private void ApplyCombinedSceneAndMasterVolumes()
        {
            CancelSceneMusicRamp();
            _player.Volume = GetCombinedMusicOutputVolume();
            ApplySceneAmbienceVolumesAndRefreshUi();
        }

        private void InitializeSceneStripUi()
        {
            SceneText1.Text = "Music Volume";
            SceneText2.Text = "Ambience 1 Vol";
            SceneText3.Text = "Ambience 2 Vol";
            SceneText4.Text = "Ambience 3 Vol";

            SetSceneLaneVolumeVisual(0, _sceneLaneVolumes[0]);
            SetSceneLaneVolumeVisual(1, _sceneLaneVolumes[1]);
            SetSceneLaneVolumeVisual(2, _sceneLaneVolumes[2]);
            SetSceneLaneVolumeVisual(3, _sceneLaneVolumes[3]);
        }

        private async void SceneButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sceneEnabled)
            {
                if (HasPlayingSceneAmbienceTracks())
                {
                    SceneButton.IsChecked = true;

                    MessageBox.Show(
                        this,
                        "Please pause or close ambient tracks before turning off Scene Mode.",
                        "Scene Mode",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    return;
                }

                _sceneEnabled = false;
                SceneStrip.Visibility = Visibility.Collapsed;
                SceneButton.IsChecked = false;

                _sceneLaneDragActive = false;
                _sceneLaneDragIndex = -1;
                _sceneWheelHoverLane = -1;
                _isScenesTabSelected = false;
                _currentSceneIndex = -1;

                BuildTabs(_activePlaylist);
                SelectPlaylist(_activePlaylist);

                ApplySceneAmbienceVolumesAndRefreshUi();
                await RampMusicOutputVolumeAsync(GetCombinedMusicOutputVolume());
                return;
            }

            _sceneEnabled = true;
            SceneStrip.Visibility = Visibility.Visible;
            SceneButton.IsChecked = true;

            BuildTabs(_activePlaylist);
            PlaylistTabs.SelectedIndex = 0;
            SelectScenesTab();

            ApplySceneAmbienceVolumesAndRefreshUi();
            await RampMusicOutputVolumeAsync(GetCombinedMusicOutputVolume());
        }

        private bool HasPlayingSceneAmbienceTracks()
        {
            for (int i = 1; i < _sceneTracks.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(_sceneTracks[i]) && _sceneLanePlaying[i])
                    return true;
            }

            return false;
        }

        private double GetSceneLaneUsableWidth(int laneIndex, Grid lane)
        {
            if (lane == null)
                return 0;

            // Music lane has no bottom-row buttons, so let it use the full width.
            if (laneIndex == 0)
                return Math.Max(0, lane.ActualWidth);

            Button? closeButton = laneIndex switch
            {
                1 => SceneClose2,
                2 => SceneClose3,
                3 => SceneClose4,
                _ => null
            };

            if (closeButton == null || !closeButton.IsLoaded || closeButton.ActualWidth <= 0)
                return Math.Max(0, lane.ActualWidth);

            try
            {
                Point buttonTopRight = closeButton.TranslatePoint(
                    new Point(closeButton.ActualWidth, 0), lane);

                double usableWidth = buttonTopRight.X;
                return Math.Max(0, Math.Min(usableWidth, lane.ActualWidth));
            }
            catch
            {
                return Math.Max(0, lane.ActualWidth);
            }
        }

        private void SetSceneLaneVolumeVisual(int laneIndex, double volume)
        {
            volume = Math.Clamp(volume, 0.0, 1.0);

            Border? fill = laneIndex switch
            {
                0 => SceneFill1,
                1 => SceneFill2,
                2 => SceneFill3,
                3 => SceneFill4,
                _ => null
            };

            Grid? lane = laneIndex switch
            {
                0 => SceneLane1,
                1 => SceneLane2,
                2 => SceneLane3,
                3 => SceneLane4,
                _ => null
            };

            TextBlock? text = laneIndex switch
            {
                0 => SceneText1,
                1 => SceneText2,
                2 => SceneText3,
                3 => SceneText4,
                _ => null
            };

            if (fill == null || lane == null)
                return;

            double usableWidth = GetSceneLaneUsableWidth(laneIndex, lane);
            fill.Width = usableWidth * volume;
            _sceneLaneVolumes[laneIndex] = volume;

            bool isPlaying = _sceneLanePlaying[laneIndex];

            // Dim paused lanes slightly so state is obvious at a glance.
            fill.Opacity = isPlaying ? 1.0 : 0.55;

            if (text != null)
                text.Opacity = isPlaying ? 1.0 : 0.70;
        }

        private void UpdateSceneLaneVolumeFromMouse(Grid lane, MouseEventArgs e)
        {
            if (lane == null)
                return;

            int laneIndex = lane.Tag is string s
                ? int.Parse(s)
                : Convert.ToInt32(lane.Tag);

            double x = e.GetPosition(lane).X;
            double usableWidth = GetSceneLaneUsableWidth(laneIndex, lane);

            double pct = usableWidth <= 0
                ? 0.0
                : Math.Clamp(x / usableWidth, 0.0, 1.0);

            SetSceneLaneVolumeVisual(laneIndex, pct);
            ApplyCombinedSceneAndMasterVolumes();
            OnSceneLaneVolumeChanged(laneIndex, pct);
        }

        private void SceneLane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_sceneEnabled)
                return;

            if (sender is not Grid lane)
                return;

            _sceneLaneDragActive = true;
            _sceneLaneDragIndex = Convert.ToInt32(lane.Tag);
            lane.CaptureMouse();
            UpdateSceneLaneVolumeFromMouse(lane, e);
        }

        private void SceneLane_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is Grid hoverLane)
                _sceneWheelHoverLane = Convert.ToInt32(hoverLane.Tag);

            if (!_sceneEnabled || !_sceneLaneDragActive)
                return;

            if (sender is not Grid lane)
                return;

            if (Convert.ToInt32(lane.Tag) != _sceneLaneDragIndex)
                return;

            UpdateSceneLaneVolumeFromMouse(lane, e);
        }

        private void SceneLane_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Grid lane && lane.IsMouseCaptured)
                lane.ReleaseMouseCapture();

            _sceneLaneDragActive = false;
            _sceneLaneDragIndex = -1;
            _sceneWheelHoverLane = -1;
        }

        private static int SceneUiLaneToEngineLane(int laneIndex)
        {
            // UI lanes:
            //   0 = Music
            //   1 = Ambience 1
            //   2 = Ambience 2
            //   3 = Ambience 3
            //
            // SceneAudioEngine lanes:
            //   0 = Ambience 1
            //   1 = Ambience 2
            //   2 = Ambience 3

            if (laneIndex < 1 || laneIndex > 3)
                throw new ArgumentOutOfRangeException(nameof(laneIndex));

            return laneIndex - 1;
        }

        private void ClearSceneLane(int laneIndex)
        {
            if (laneIndex < 0 || laneIndex > 3)
                return;

            switch (laneIndex)
            {
                case 0:
                    SceneText1.Text = "Music";
                    break;
                case 1:
                    SceneText2.Text = "Ambience 1 Vol";
                    break;
                case 2:
                    SceneText3.Text = "Ambience 2 Vol";
                    break;
                case 3:
                    SceneText4.Text = "Ambience 3 Vol";
                    break;
            }

            SetSceneLanePlayingState(laneIndex, false);
            SetSceneLaneVolumeVisual(laneIndex, 1.0);

            if (laneIndex >= 1)
            {
                _sceneTracks[laneIndex] = null;
                _sceneAudio.StopLane(SceneUiLaneToEngineLane(laneIndex));
            }

            OnSceneLaneVolumeChanged(laneIndex, _sceneLaneVolumes[laneIndex]);

            ApplyCombinedSceneAndMasterVolumes();
        }

        private void SetSceneLanePlayingState(int lane, bool isPlaying)
        {
            if (lane < 0 || lane > 3)
                return;

            _sceneLanePlaying[lane] = isPlaying;
            UpdateSceneLanePlayButton(lane);
            SetSceneLaneVolumeVisual(lane, _sceneLaneVolumes[lane]);

            if (_isScenesTabSelected)
                RefreshScenesUI();
        }

        private void ToggleSceneLane(int lane)
        {
            if (lane < 0 || lane > 3)
                return;

            bool newIsPlaying = !_sceneLanePlaying[lane];
            SetSceneLanePlayingState(lane, newIsPlaying);

            if (lane >= 1)
            {
                int engineLane = SceneUiLaneToEngineLane(lane);

                if (newIsPlaying)
                {
                    string? path = _sceneTracks[lane];

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        _sceneAudio.PlayLane(
                            engineLane,
                            path,
                            loop: true,
                            volume: (float)_sceneLaneVolumes[lane]);
                    }
                }
                else
                {
                    _sceneAudio.StopLane(engineLane);
                }
            }
        }

        private void ScenePlay1_Click(object sender, RoutedEventArgs e) => ToggleSceneLane(0);
        private void ScenePlay2_Click(object sender, RoutedEventArgs e) => ToggleSceneLane(1);
        private void ScenePlay3_Click(object sender, RoutedEventArgs e) => ToggleSceneLane(2);
        private void ScenePlay4_Click(object sender, RoutedEventArgs e) => ToggleSceneLane(3);

        private void UpdateSceneLanePlayButton(int lane)
        {
            Button? btn = lane switch
            {
                1 => ScenePlay2,
                2 => ScenePlay3,
                3 => ScenePlay4,
                _ => null
            };

            if (btn == null)
                return;

            btn.Content = _sceneLanePlaying[lane] ? GlyphPause : GlyphPlay;
        }

        private void SceneClose1_Click(object sender, RoutedEventArgs e) => ClearSceneLane(0);
        private void SceneClose2_Click(object sender, RoutedEventArgs e) => ClearSceneLane(1);
        private void SceneClose3_Click(object sender, RoutedEventArgs e) => ClearSceneLane(2);
        private void SceneClose4_Click(object sender, RoutedEventArgs e) => ClearSceneLane(3);

        private bool IsCtrlDownNow()
        {
            return (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        }

        private bool HasActiveSceneAmbienceTracks()
        {
            for (int i = 1; i < _sceneTracks.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(_sceneTracks[i]))
                    return true;
            }

            return false;
        }

        private void SetPlayingPlaylist(int playlistIndex, bool rebuildTabs = true)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return;

            if (_playingPlaylist == playlistIndex)
            {
                if (rebuildTabs)
                    BuildTabs(_activePlaylist);
                else
                    RefreshPlayingPlaylistTabHighlight();

                return;
            }

            _playingPlaylist = playlistIndex;

            if (rebuildTabs)
                BuildTabs(_activePlaylist);
            else
                RefreshPlayingPlaylistTabHighlight();
        }

        private int FindPlaylistIndexContainingTrack(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return -1;

            for (int i = 0; i < _playlists.Count; i++)
            {
                var tracks = _playlists[i].Tracks;
                for (int j = 0; j < tracks.Count; j++)
                {
                    if (string.Equals(tracks[j], path, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            return -1;
        }

        // ---------- Volume ----------
        private void SetupVolumePopup()
        {
            _volumeToolTip = new ToolTip
            {
                PlacementTarget = VolumeSlider,
                Placement = PlacementMode.Top,
                StaysOpen = true,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                IsOpen = false
            };

            VolumeSlider.ToolTip = _volumeToolTip;

            _volumePopupAutoCloseTimer.Interval = TimeSpan.FromSeconds(1);
            _volumePopupAutoCloseTimer.Tick += (_, __) =>
            {
                _volumePopupAutoCloseTimer.Stop();

                if (!_isVolumeDragging)
                {
                    VolumePopup.IsOpen = false;
                }
            };

            VolumePopup.Closed += (_, __) =>
            {
                _volumePopupAutoCloseTimer.Stop();
                _lastVolumePopupClosedUtc = DateTime.UtcNow;
            };

            VolumeSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Volume_DragStarted));
            VolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Volume_DragCompleted));
            VolumeSlider.ValueChanged += Volume_ValueChanged;

            ApplyCombinedSceneAndMasterVolumes();
        }

        private void RestartVolumePopupAutoCloseTimer()
        {
            _volumePopupAutoCloseTimer.Stop();
            _volumePopupAutoCloseTimer.Start();
        }

        private void Volume_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isVolumeDragging = true;
            _volumePopupAutoCloseTimer.Stop();
            ShowOrUpdateVolumeToolTip();
        }

        private Grid? GetSceneLaneGrid(int laneIndex)
        {
            return laneIndex switch
            {
                0 => SceneLane1,
                1 => SceneLane2,
                2 => SceneLane3,
                3 => SceneLane4,
                _ => null
            };
        }

        private bool IsCursorOverElementScreenRect(FrameworkElement? element)
        {
            if (element == null || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;

            try
            {
                var topLeft = element.PointToScreen(new Point(0, 0));
                var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

                var mouse = System.Windows.Forms.Control.MousePosition;

                return mouse.X >= topLeft.X &&
                       mouse.X <= bottomRight.X &&
                       mouse.Y >= topLeft.Y &&
                       mouse.Y <= bottomRight.Y;
            }
            catch
            {
                return false;
            }
        }

        private int GetSceneLaneUnderMouse()
        {
            if (!_sceneEnabled || SceneStrip.Visibility != Visibility.Visible)
                return -1;

            for (int laneIndex = 0; laneIndex <= 3; laneIndex++)
            {
                if (IsCursorOverElementScreenRect(GetSceneLaneGrid(laneIndex)))
                    return laneIndex;
            }

            return -1;
        }

        private void AdjustSceneLaneVolumeFromMouseWheel(int laneIndex, MouseWheelEventArgs e)
        {
            if (laneIndex < 0 || laneIndex > 3)
                return;

            e.Handled = true;

            double step = 0.01;

            int notches = Math.Abs(e.Delta) / 120;
            if (notches < 1)
                notches = 1;

            int effectiveNotches = notches * 3;

            double delta = (e.Delta > 0 ? step : -step) * effectiveNotches;
            double newValue = Math.Clamp(_sceneLaneVolumes[laneIndex] + delta, 0.0, 1.0);

            SetSceneLaneVolumeVisual(laneIndex, newValue);
            ApplyCombinedSceneAndMasterVolumes();
            OnSceneLaneVolumeChanged(laneIndex, newValue);
        }

        private bool IsCursorOverVolumeButtonScreenRect()
        {
            if (VolumeButton == null || !VolumeButton.IsVisible || VolumeButton.ActualWidth <= 0 || VolumeButton.ActualHeight <= 0)
                return false;

            try
            {
                var topLeft = VolumeButton.PointToScreen(new Point(0, 0));
                var bottomRight = VolumeButton.PointToScreen(new Point(VolumeButton.ActualWidth, VolumeButton.ActualHeight));

                var mouse = System.Windows.Forms.Control.MousePosition;

                return mouse.X >= topLeft.X &&
                       mouse.X <= bottomRight.X &&
                       mouse.Y >= topLeft.Y &&
                       mouse.Y <= bottomRight.Y;
            }
            catch
            {
                return false;
            }
        }

        private void Volume_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isVolumeDragging = false;
            if (_volumeToolTip != null)
                _volumeToolTip.IsOpen = false;

            _volumePopupAutoCloseTimer.Stop();
            _volumePopupAutoCloseTimer.Start();
        }

        private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isVolumeDragging)
                ShowOrUpdateVolumeToolTip();

            ApplyCombinedSceneAndMasterVolumes();
        }

        private void VolumeButton_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            AdjustVolumeFromMouseWheel(e);
        }

        private void MainWindow_GlobalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsCursorOverVolumeButtonScreenRect())
            {
                AdjustVolumeFromMouseWheel(e);
                return;
            }

            int sceneLane = GetSceneLaneUnderMouse();
            if (sceneLane >= 0)
            {
                AdjustSceneLaneVolumeFromMouseWheel(sceneLane, e);
            }
        }

        private void AdjustVolumeFromMouseWheel(MouseWheelEventArgs e)
        {
            e.Handled = true;

            double step = 1.0;

            int notches = Math.Abs(e.Delta) / 120;
            if (notches < 1)
                notches = 1;

            int effectiveNotches = notches * 3;

            double delta = (e.Delta > 0 ? step : -step) * effectiveNotches;

            double newValue = Math.Clamp(
                VolumeSlider.Value + delta,
                VolumeSlider.Minimum,
                VolumeSlider.Maximum);

            VolumeSlider.Value = newValue;

            VolumePopup.IsOpen = true;
            RestartVolumePopupAutoCloseTimer();
        }

        private void UpdateVolumeButtonGlyph()
        {
            if (VolumeButton == null)
                return;

            double v = VolumeSlider.Value;

            if (v >= 100)
                VolumeButton.Content = GlyphVolume3;
            else if (v >= 50)
                VolumeButton.Content = GlyphVolume2;
            else if (v >= 10)
                VolumeButton.Content = GlyphVolume1;
            else
                VolumeButton.Content = GlyphVolume0;
        }

        private void ShowOrUpdateVolumeToolTip()
        {
            if (_volumeToolTip == null) return;

            _volumeToolTip.Content = $"{(int)Math.Round(VolumeSlider.Value)}%";
            _volumeToolTip.IsOpen = true;
        }
    }


    internal sealed class PlaylistRow : INotifyPropertyChanged
    {
        public string Path { get; }
        public int SourceIndex { get; }

        public string DisplayName => MainWindow.CleanDisplayTitle(Path);
        public Visibility DurationVisibility => Visibility.Visible;
        public Visibility ScenePlayPauseVisibility => Visibility.Collapsed;
        public string ScenePlayPauseGlyph => "";

        private string _albumText = "";
        public string AlbumText
        {
            get => _albumText;
            set
            {
                if (_albumText == value) return;
                _albumText = value;
                OnPropertyChanged();
            }
        }

        private string _durationText = "--:--";
        public string DurationText
        {
            get => _durationText;
            set
            {
                if (_durationText == value) return;
                _durationText = value;
                OnPropertyChanged();
            }
        }

        public PlaylistRow(string path, int sourceIndex, string? durationText = null, string? albumText = null)
        {
            Path = path;
            SourceIndex = sourceIndex;

            if (!string.IsNullOrWhiteSpace(durationText))
                _durationText = durationText!;

            if (!string.IsNullOrWhiteSpace(albumText))
                _albumText = albumText!;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}