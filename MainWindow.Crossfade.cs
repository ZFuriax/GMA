using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private enum XFadeMode { Off, CrossLoop }

        private XFadeMode _xFadeMode = XFadeMode.Off;

        private bool _crossfadeArmed = false;
        private const int CrossfadeMs = 2500;

        private int? _pendingShuffleCrossfadeSourceIndex = null;
        private int? _pendingShuffleCrossfadeTargetIndex = null;

        private DateTime _suppressCrossfadeUntilUtc = DateTime.MinValue;
        private bool _disableCrossfadeForCurrentTrack = false;
        private string? _disableCrossfadeForFile = null;

        private bool _crossfadeTransitionActive;
        private string? _crossfadeFromPath;
        private string? _crossfadeToPath;
        private DateTime _crossfadeStartedUtc = DateTime.MinValue;

        private bool _crossfadePrepareRequested;
        private bool _crossfadePrepared;
        private int? _crossfadePreparedTargetIndex = null;
        private string? _crossfadePreparedTargetPath = null;

        private DateTime _ignorePlaybackEndedUntilUtc = DateTime.MinValue;

        // CROSSFADE INVARIANT:
        //
        // PlaybackEnded must be suppressed whenever ANY of the following is true:
        // - _crossfadeArmed
        // - _crossfadeTransitionActive
        // - DateTime.UtcNow < _ignorePlaybackEndedUntilUtc
        //
        // Enforcement location:
        // - TrySuppressPlaybackEndedDuringCrossfade(...)

        private const string GlyphXFade = "\uE9D9";

        private void ClearPendingCrossfadeState()
        {
            _crossfadeArmed = false;
            _abCrossfadeArmed = false;

            _crossfadeTransitionActive = false;
            _crossfadeFromPath = null;
            _crossfadeToPath = null;
            _crossfadeStartedUtc = DateTime.MinValue;
            _ignorePlaybackEndedUntilUtc = DateTime.MinValue;

            _crossfadePrepareRequested = false;
            _crossfadePrepared = false;
            _crossfadePreparedTargetIndex = null;
            _crossfadePreparedTargetPath = null;

            _pendingShuffleCrossfadeSourceIndex = null;
            _pendingShuffleCrossfadeTargetIndex = null;

            // Reset duplicate ended suppression so a manual skip/restart doesn't suppress
            // the next legitimate natural end for the same file.
            _lastHandledEndedPath = null;
            _lastHandledEndedUtc = DateTime.MinValue;
        }

        private void XFadeButton_Checked(object sender, RoutedEventArgs e)
        {
            _xFadeMode = XFadeMode.CrossLoop;
            UpdateXFadeButtonVisuals();
            LogTransport("XFadeButton_Checked", $"newXFadeMode={_xFadeMode}");
            RequestSaveState();
        }

        private void XFadeButton_Unchecked(object sender, RoutedEventArgs e)
        {
            _xFadeMode = XFadeMode.Off;
            UpdateXFadeButtonVisuals();
            LogTransport("XFadeButton_Unchecked", $"newXFadeMode={_xFadeMode}");
            RequestSaveState();
        }

        private void UpdateXFadeButtonVisuals()
        {
            XFadeButton.Content = GlyphXFade;

            XFadeButton.ToolTip = new TextBlock
            {
                TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Inlines =
                {
                    new Run("Crossfade") { FontWeight = FontWeights.Bold },
                    new LineBreak(),
                    new Run("Blends the end of the current track into the beginning of the next."),
                    new LineBreak(),
                    new Run("Highly recommended when Loop is enabled.")
                }
            };

            bool shouldBeChecked = _xFadeMode != XFadeMode.Off;

            if (XFadeButton.IsChecked != shouldBeChecked)
                XFadeButton.IsChecked = shouldBeChecked;
        }

        private void RefreshCrossfadeDisableStateForCurrentTrack()
        {
            var currentCrossfadeFile = _player.CurrentFile;
            if (!string.Equals(currentCrossfadeFile, _disableCrossfadeForFile, StringComparison.OrdinalIgnoreCase))
            {
                _disableCrossfadeForCurrentTrack = false;
                _disableCrossfadeForFile = currentCrossfadeFile;
            }
        }

        private readonly object _crossfadePrepStateLock = new();

        private void ClearCrossfadePreparationStateLocked()
        {
            _crossfadePrepareRequested = false;
            _crossfadePrepared = false;
            _crossfadePreparedTargetIndex = null;
            _crossfadePreparedTargetPath = null;
        }

        private void QueueCrossfadePreparation(int targetIndex, string nextPath)
        {
            lock (_crossfadePrepStateLock)
            {
                if (_crossfadePrepareRequested ||
                    _crossfadePrepared ||
                    string.IsNullOrWhiteSpace(nextPath))
                {
                    return;
                }

                _crossfadePrepareRequested = true;
                _crossfadePrepared = false;
                _crossfadePreparedTargetIndex = targetIndex;
                _crossfadePreparedTargetPath = nextPath;
            }

            LogTransport(
                "Crossfade.PrepareQueued",
                $"targetIndex={targetIndex} nextPath=\"{ClipLogValue(nextPath)}\"");

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                bool prepared = false;

                try
                {
                    prepared = _player.TryPrepareCrossfadeTo(nextPath);
                }
                catch
                {
                    prepared = false;
                }

                bool accepted;
                lock (_crossfadePrepStateLock)
                {
                    accepted = string.Equals(_crossfadePreparedTargetPath, nextPath, StringComparison.OrdinalIgnoreCase);

                    if (!accepted)
                        return;

                    _crossfadePrepareRequested = false;
                    _crossfadePrepared = prepared;

                    if (!prepared)
                    {
                        _crossfadePreparedTargetIndex = null;
                        _crossfadePreparedTargetPath = null;
                    }
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!accepted)
                        return;

                    if (!prepared)
                    {
                        LogTransport(
                            "Crossfade.PrepareFailed",
                            $"targetIndex={targetIndex} nextPath=\"{ClipLogValue(nextPath)}\"");
                    }
                    else
                    {
                        LogTransport(
                            "Crossfade.Prepared",
                            $"targetIndex={targetIndex} nextPath=\"{ClipLogValue(nextPath)}\"");
                    }
                }));
            });
        }

        private void MaybeArmCrossfade(TimeSpan pos, TimeSpan? dur)
        {
            if (_xFadeMode != XFadeMode.CrossLoop ||
                _crossfadeArmed ||
                _disableCrossfadeForCurrentTrack ||
                DateTime.UtcNow < _suppressCrossfadeUntilUtc ||
                _player.PlaybackState != NAudio.Wave.PlaybackState.Playing ||
                !dur.HasValue ||
                WaveformBar.LoopEnabled)
            {
                return;
            }

            var remaining = dur.Value - pos;
            double remainingMs = remaining.TotalMilliseconds;
            double prepareThresholdMs = CrossfadeMs + 5000;
            double commitThresholdMs = CrossfadeMs + 150;
            double minRunwayMs = CrossfadeMs;

            if (remainingMs > prepareThresholdMs)
                return;

            int? nextIndex = DetermineNextTrackIndexForPlayingPlaylist(wrap: false);

            LogTransport(
                "UpdatePlaybackUI.CrossfadeStageCheck",
                $"remainingMs={remainingMs:0} prepareThresholdMs={prepareThresholdMs:0} commitThresholdMs={commitThresholdMs:0} minRunwayMs={minRunwayMs:0} nextIndex={(nextIndex?.ToString() ?? "null")} prepared={_crossfadePrepared} prepareRequested={_crossfadePrepareRequested}");

            if (remainingMs < minRunwayMs)
            {
                _pendingShuffleCrossfadeSourceIndex = null;
                _pendingShuffleCrossfadeTargetIndex = null;

                LogTransport(
                    "UpdatePlaybackUI.CrossfadeSkippedInsufficientRunway",
                    $"remainingMs={remainingMs:0} minRunwayMs={minRunwayMs:0}");
                return;
            }

            if (nextIndex == null ||
                nextIndex.Value < 0 ||
                nextIndex.Value >= Playing.Tracks.Count)
            {
                return;
            }

            int targetIndex = nextIndex.Value;

            if (targetIndex == Playing.Index)
            {
                _pendingShuffleCrossfadeSourceIndex = null;
                _pendingShuffleCrossfadeTargetIndex = null;

                string? currentPath = _player.CurrentFile;
                if (string.IsNullOrWhiteSpace(currentPath) || !_player.Duration.HasValue)
                {
                    LogTransport(
                        "UpdatePlaybackUI.CrossfadeSameTrackSkippedInvalidState",
                        $"currentPath=\"{ClipLogValue(currentPath)}\" hasDuration={_player.Duration.HasValue}");
                    return;
                }

                BeginCrossfadeTransitionBookkeeping(currentPath, currentPath);

                LogTransport(
                    "UpdatePlaybackUI.CrossfadeArmedSameTrack",
                    $"playingPlaylist={_playingPlaylist} activePlaylist={_activePlaylist} currentIndex={Playing.Index} targetIndex={targetIndex} path=\"{ClipLogValue(currentPath)}\" from=\"{ClipLogValue(_crossfadeFromPath)}\" to=\"{ClipLogValue(_crossfadeToPath)}\"");

                _player.BeginCrossfadeLoopToFraction(0.0, CrossfadeMs);
                _crossfadeArmed = true;
                return;
            }

            string nextPath = Playing.Tracks[targetIndex];

            if (!string.IsNullOrWhiteSpace(_player.CurrentFile) &&
                string.Equals(nextPath, _player.CurrentFile, StringComparison.OrdinalIgnoreCase))
            {
                _pendingShuffleCrossfadeSourceIndex = null;
                _pendingShuffleCrossfadeTargetIndex = null;

                LogTransport(
                    "UpdatePlaybackUI.CrossfadeSkippedSameFile",
                    $"targetIndex={targetIndex} nextPath=\"{ClipLogValue(nextPath)}\"");

                return;
            }

            bool prepMatchesTarget =
                _crossfadePrepared &&
                _crossfadePreparedTargetIndex == targetIndex &&
                string.Equals(_crossfadePreparedTargetPath, nextPath, StringComparison.OrdinalIgnoreCase);

            bool requestMatchesTarget =
                _crossfadePrepareRequested &&
                _crossfadePreparedTargetIndex == targetIndex &&
                string.Equals(_crossfadePreparedTargetPath, nextPath, StringComparison.OrdinalIgnoreCase);

            if (!prepMatchesTarget && !requestMatchesTarget)
            {
                QueueCrossfadePreparation(targetIndex, nextPath);
            }

            if (remainingMs > commitThresholdMs)
            {
                return;
            }

            if (!prepMatchesTarget)
            {
                LogTransport(
                    "UpdatePlaybackUI.CrossfadeCommitWaitingForPreparedTrack",
                    $"targetIndex={targetIndex} nextPath=\"{ClipLogValue(nextPath)}\" remainingMs={remainingMs:0}");

                return;
            }

            bool started = _player.TryCommitPreparedCrossfadeTo(nextPath, CrossfadeMs);

            if (!started)
            {
                lock (_crossfadePrepStateLock)
                {
                    ClearCrossfadePreparationStateLocked();
                }

                _pendingShuffleCrossfadeSourceIndex = null;
                _pendingShuffleCrossfadeTargetIndex = null;

                LogTransport(
                    "UpdatePlaybackUI.CrossfadeNotArmed",
                    $"targetIndex={targetIndex} nextPath=\"{ClipLogValue(nextPath)}\"");

                return;
            }

            BeginCrossfadeTransitionBookkeeping(_player.CurrentFile, nextPath);

            lock (_crossfadePrepStateLock)
            {
                ClearCrossfadePreparationStateLocked();
            }

            if (ShuffleEnabled)
            {
                _pendingShuffleCrossfadeSourceIndex = Playing.Index;
                _pendingShuffleCrossfadeTargetIndex = targetIndex;
            }
            else
            {
                _pendingShuffleCrossfadeSourceIndex = null;
                _pendingShuffleCrossfadeTargetIndex = null;
            }

            LogTransport(
                "UpdatePlaybackUI.CrossfadeArmed",
                $"playingPlaylist={_playingPlaylist} activePlaylist={_activePlaylist} currentIndex={Playing.Index} targetIndex={targetIndex} nextPath=\"{ClipLogValue(nextPath)}\" from=\"{ClipLogValue(_crossfadeFromPath)}\" to=\"{ClipLogValue(_crossfadeToPath)}\"");

            _crossfadeArmed = true;
        }

        private void BeginCrossfadeTransitionBookkeeping(string? fromPath, string? toPath)
        {
            _pendingTrackChangeSource = "Crossfade";

            _crossfadeTransitionActive = true;
            _crossfadeFromPath = fromPath;
            _crossfadeToPath = toPath;
            _crossfadeStartedUtc = DateTime.UtcNow;
            _ignorePlaybackEndedUntilUtc = DateTime.UtcNow.AddMilliseconds(CrossfadeMs + 1500);
        }
        private void SyncCrossfadeStateOnTrackChanged(string? path)
        {
            if (_crossfadeTransitionActive &&
                !string.IsNullOrWhiteSpace(_crossfadeToPath) &&
                !string.IsNullOrWhiteSpace(path) &&
                string.Equals(path, _crossfadeToPath, StringComparison.OrdinalIgnoreCase))
            {
                LogTransport(
                    "TrackChanged.CrossfadeTransitionCommitted",
                    $"from=\"{ClipLogValue(_crossfadeFromPath)}\" to=\"{ClipLogValue(_crossfadeToPath)}\"");

                if (_uiWantsPlaying &&
                    _player.PlaybackState == NAudio.Wave.PlaybackState.Stopped)
                {
                    LogTransport(
                        "TrackChanged.CrossfadeTransitionObservedStopped",
                        $"path=\"{ClipLogValue(path)}\"");
                }

                // IMPORTANT:
                // The incoming track is now current, but we must KEEP the crossfade
                // suppression window alive a little longer because a late/stale
                // PlaybackEnded from the outgoing track can still arrive.
                //
                // So:
                // - clear only the "armed" flag
                // - keep _crossfadeTransitionActive true
                // - keep _ignorePlaybackEndedUntilUtc intact
                _crossfadeArmed = false;
                return;
            }

            // Any unrelated TrackChanged means the old crossfade state is no longer relevant.
            _crossfadeTransitionActive = false;
            _crossfadeFromPath = null;
            _crossfadeToPath = null;
            _crossfadeStartedUtc = DateTime.MinValue;
            _ignorePlaybackEndedUntilUtc = DateTime.MinValue;

            _crossfadeArmed = false;
        }

        // =====================================================================================
        // IMPORTANT: Crossfade PlaybackEnded Suppression (single source of truth)
        //
        // This method is the ONLY place where PlaybackEnded events are suppressed
        // during crossfade transitions.
        //
        // Why this exists:
        // - PlaybackEnded can fire from the *outgoing* track during crossfade
        // - Late/stale events can arrive after the new track has already started
        // - Crossfade has multiple timing states:
        //     * _crossfadeArmed (fade started but not committed)
        //     * _crossfadeTransitionActive (handoff in progress)
        //     * _ignorePlaybackEndedUntilUtc (time-based suppression window)
        //
        // ALL of these must be treated as "crossfade active" and suppress PlaybackEnded.
        //
        // ⚠️ DO NOT duplicate this logic elsewhere (e.g., in HandleTrackEnded).
        // ⚠️ If you change crossfade timing/state fields, update this method.
        // ⚠️ This protects against race conditions between audio pipeline and UI logic.
        //
        // See also:
        // - EOF monitor stale-after-drain fix in AudioPlayer (prevents old track stopping new output)
        // =====================================================================================

        private bool TrySuppressPlaybackEndedDuringCrossfade(string? playbackEndedUiPathSnapshot, DateTime now)
        {
            if (_xFadeMode == XFadeMode.CrossLoop &&
                (_crossfadeArmed || _crossfadeTransitionActive || now < _ignorePlaybackEndedUntilUtc))
            {
                double remainingMs = Math.Max(0, (_ignorePlaybackEndedUntilUtc - now).TotalMilliseconds);

                LogTransport(
                    "PlaybackEnded.SuppressedCrossfadeUnified",
                    $"uiSnapshot=\"{ClipLogValue(playbackEndedUiPathSnapshot)}\" current=\"{ClipLogValue(_uiCurrentFile)}\" crossfadeArmed={_crossfadeArmed} crossfadeTransitionActive={_crossfadeTransitionActive} remainingMs={remainingMs:0} from=\"{ClipLogValue(_crossfadeFromPath)}\" to=\"{ClipLogValue(_crossfadeToPath)}\"");

                return true;
            }

            if (_crossfadeTransitionActive)
            {
                LogTransport(
                    "PlaybackEnded.CrossfadeTransitionWindowExpired",
                    $"from=\"{ClipLogValue(_crossfadeFromPath)}\" to=\"{ClipLogValue(_crossfadeToPath)}\"");

                _crossfadeTransitionActive = false;
                _crossfadeFromPath = null;
                _crossfadeToPath = null;
                _crossfadeStartedUtc = DateTime.MinValue;
                _ignorePlaybackEndedUntilUtc = DateTime.MinValue;
            }

            return false;
        }
    }
}