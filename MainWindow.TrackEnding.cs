using System;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private void ResumeCurrentTrackFromStartAfterTrackEnd(string action)
        {
            LogTransport("HandleTrackEnded.Decision", $"action={action}");
            _pendingTrackChangeSource = "PlaybackEnded";
            _player.Seek(TimeSpan.Zero, resume: true, reason: $"HandleTrackEnded:{action}");
            _uiWantsPlaying = true;
            SyncPlayPauseButton();
        }

        private void AdvancePlayingPlaylistAfterTrackEnd(int nextIndex, string action)
        {
            _pendingTrackChangeSource = "PlaybackEnded";

            if (StartPlaybackAtPlayingIndex(nextIndex))
            {
                LogTransport("HandleTrackEnded.Action", $"action={action} nextIndex={nextIndex}");
                return;
            }

            StopPlaybackAfterTrackEnd($"{action}LoadFailed");
        }

        private void StopPlaybackAfterTrackEnd(string action)
        {
            LogTransport("HandleTrackEnded.Action", $"action={action}");
            LogTransport("HandleTrackEnded.StopCall",
                $"reason={_pendingTrackChangeSource ?? "null"} currentFile=\"{ClipLogValue(_player.CurrentFile)}\" pos={_player.Position}");

            _player.Stop(reason: $"HandleTrackEnded:{action}");
            EnterPausedState();
            ForceUiResetAtStop();
            BeginSpectrumFadeOut();
        }

        private void HandleTrackEnded()
        {
            bool shouldAdvance =
                _uiWantsPlaying ||
                _player.PlaybackState == NAudio.Wave.PlaybackState.Playing;

            if (!shouldAdvance)
            {
                LogTransport(
                    "HandleTrackEnded.RecoverPlaybackIntent",
                    $"previousWantPlaying={_uiWantsPlaying} playbackState={_player.PlaybackState} playingIndex={Playing.Index} currentFile=\"{ClipLogValue(_player.CurrentFile)}\"");

                // If PlaybackEnded made it here, upstream suppression already decided
                // this is a valid end-of-track event. Under load, UI intent and output
                // state can lag or briefly disagree, so recover rather than stall.
                _uiWantsPlaying = true;
                SyncPlayPauseButton();
            }

            LogTransport("HandleTrackEnded.ENTER_DEBUG",
                $"wantPlaying={_uiWantsPlaying} playingIndex={Playing.Index}");

            LogTransport("HandleTrackEnded.Enter",
                $"playingPlaylist={_playingPlaylist} activePlaylist={_activePlaylist} playingIndex={Playing.Index} currentFile=\"{ClipLogValue(_player.CurrentFile)}\" pos={_player.Position} dur={(_player.Duration.HasValue ? _player.Duration.Value.ToString() : "null")} pendingTrackChangeSource={_pendingTrackChangeSource ?? "null"}");

            CancelWaveformInteraction();

            // ----- A-B Loop Safety Net -----
            // If loop is enabled and we reached track end without triggering the UI loop,
            // force a loop back to A instead of advancing/stopping.

            if (WaveformBar.LoopEnabled && _player.Duration.HasValue)
            {
                double a = Math.Clamp(WaveformBar.LoopA, 0.0, 1.0);
                double b = Math.Clamp(WaveformBar.LoopB, 0.0, 1.0);

                if (b > a + 0.0001)
                {
                    double progress = _player.Duration.Value.TotalSeconds <= 0
                        ? 0
                        : _player.Position.TotalSeconds / _player.Duration.Value.TotalSeconds;

                    // If we reached or passed B (or very close to it), loop instead of ending
                    if (progress >= b - 0.01) // slightly larger epsilon for safety
                    {
                        LogTransport("HandleTrackEnded.ABLoopFallback",
                            $"progress={progress:0.0000} loopA={a:0.0000} loopB={b:0.0000}");

                        _pendingTrackChangeSource = "ABLoopFallback";

                        if (_xFadeMode == XFadeMode.CrossLoop)
                        {
                            _player.BeginCrossfadeLoopToFraction(a, CrossfadeMs);
                        }
                        else
                        {
                            _player.SeekFraction(a, resume: true);
                        }

                        _uiWantsPlaying = true;
                        SyncPlayPauseButton();
                        return;
                    }
                }
            }

            // NOTE:
            // Crossfade-related PlaybackEnded suppression is handled upstream in
            // TrySuppressPlaybackEndedDuringCrossfade(...).
            //
            // If we reach HandleTrackEnded(), we assume the event is valid and should
            // advance playback.
            //
            // Do NOT reintroduce crossfade suppression logic here unless the helper is updated.
            // This avoids duplicated logic and race-condition bugs.

            if (Playing.Tracks.Count == 0 || Playing.Index < 0)
            {
                LogTransport("HandleTrackEnded.Exit", "reason=NoPlayingTrack");
                return;
            }

            if (_repeatMode == RepeatMode.One)
            {
                ResumeCurrentTrackFromStartAfterTrackEnd("RepeatOneSeekToStart");
                return;
            }

            if (ShuffleEnabled)
            {
                var previousIndex = Playing.Index;
                var next = GetNextShuffleIndexForPlaylist(_playingPlaylist);

                LogTransport(
                    "HandleTrackEnded.ShuffleDecision",
                    $"previousIndex={previousIndex} next={(next?.ToString() ?? "null")}");

                if (next != null && next.Value != previousIndex)
                {
                    Playing.ShuffleHistory.Push(previousIndex);
                    AdvancePlayingPlaylistAfterTrackEnd(next.Value, "ShuffleAdvance");
                    return;
                }

                StopPlaybackAfterTrackEnd("StopAfterShuffle");
                return;
            }

            if (_repeatMode == RepeatMode.All)
            {
                var nextIndex = (_activePlaylist == _playingPlaylist)
                    ? GetAdjacentIndexByView(+1, wrap: true)
                    : GetAdjacentIndexByStoredView(_playingPlaylist, Playing.Index, +1, wrap: true);

                LogTransport(
                    "HandleTrackEnded.RepeatAllDecision",
                    $"nextIndex={(nextIndex?.ToString() ?? "null")}");

                if (nextIndex != null && nextIndex.Value != Playing.Index)
                {
                    AdvancePlayingPlaylistAfterTrackEnd(nextIndex.Value, "RepeatAllAdvance");
                    return;
                }

                if (nextIndex != null && nextIndex.Value == Playing.Index && Playing.Tracks.Count == 1)
                {
                    ResumeCurrentTrackFromStartAfterTrackEnd("RepeatAllSingleTrackSeekToStart");
                    return;
                }

                StopPlaybackAfterTrackEnd("StopAfterRepeatAllFallback");
                return;
            }

            var normalNextIndex = (_activePlaylist == _playingPlaylist)
                ? GetAdjacentIndexByView(+1, wrap: false)
                : GetAdjacentIndexByStoredView(_playingPlaylist, Playing.Index, +1, wrap: false);

            LogTransport(
                "HandleTrackEnded.NormalDecision",
                $"nextIndex={(normalNextIndex?.ToString() ?? "null")}");

            if (normalNextIndex != null && normalNextIndex.Value != Playing.Index)
            {
                AdvancePlayingPlaylistAfterTrackEnd(normalNextIndex.Value, "NormalAdvance");
                return;
            }

            StopPlaybackAfterTrackEnd("StopAtEndOfPlaylist");
        }

        private void TryAdvanceToNextPlaylist()
        {
            for (int i = _activePlaylist + 1; i < _playlists.Count; i++)
            {
                if (_playlists[i].Tracks.Count > 0)
                {
                    SelectPlaylist(i);

                    if (!StartPlaybackAtActiveIndex(0))
                    {
                        EnterPausedState();
                        ForceUiResetAtStop();
                        BeginSpectrumFadeOut();
                    }

                    return;
                }
            }

            _player.Stop(reason: "TryAdvanceToNextPlaylist:NoNextPlaylist");
            EnterPausedState();
            ForceUiResetAtStop();
            BeginSpectrumFadeOut();
        }

        private void ForceUiResetAtStop()
        {
            WaveformBar.Progress = 0.0;

            var dur = _player.Duration;
            if (dur.HasValue)
                TimeText.Text = $"00:00 / {FormatTime(dur.Value)}";
            else
                TimeText.Text = "00:00 / --:--";

            PlayPauseButton.Content = GlyphPlay;
        }
    }
}