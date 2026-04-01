using System;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private void UpdatePlaybackUI()
        {
            if (_isScrubbing)
            {
                // keep the user-held scrub visual, but don't let a stale scrub state
                // freeze the progress forever if playback has moved to another track
                if (!string.IsNullOrWhiteSpace(_player.CurrentFile) &&
                    !string.Equals(_player.CurrentFile, _uiCurrentFile, StringComparison.OrdinalIgnoreCase))
                {
                    EndScrub();
                }
                else
                {
                    return;
                }
            }

            var curFile = _player.CurrentFile;
            if (!string.IsNullOrWhiteSpace(curFile) &&
                !string.Equals(curFile, _uiCurrentFile, StringComparison.OrdinalIgnoreCase))
            {
                // Audio advanced but UI didn't get/handle TrackChanged
                ForceUiTrackTo(curFile);
            }

            var pos = _player.Position;
            var dur = _player.Duration;

            RefreshCrossfadeDisableStateForCurrentTrack();
            MaybeArmCrossfade(pos, dur);

            TimeText.Text = $"{FormatTime(pos)} / {(dur.HasValue ? FormatTime(dur.Value) : "--:--")}";

            double progress = 0.0;
            if (dur.HasValue && dur.Value.TotalSeconds > 0.01)
                progress = Math.Clamp(pos.TotalSeconds / dur.Value.TotalSeconds, 0.0, 1.0);

            if (_player.PlaybackState == NAudio.Wave.PlaybackState.Playing &&
                WaveformBar.Progress > 0.999 &&
                dur.HasValue && pos < TimeSpan.FromMilliseconds(250))
            {
                WaveformBar.Progress = 0.0;
            }

            if (HandleABLoopDuringPlayback(dur, progress))
                return;

            LogTransport("UpdatePlaybackUI.ProgressSet",
                $"progress={progress:0.0000} pos={pos} dur={(dur.HasValue ? dur.Value.ToString() : "null")} currentFile=\"{ClipLogValue(_player.CurrentFile)}\"");

            WaveformBar.Progress = progress;

            // Save resume position occasionally while playing (crash safety)
            if (_player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastResumeSaveUtc).TotalSeconds >= 2)
                {
                    _lastResumeSaveUtc = now;
                    RequestSaveState();
                }
            }

            PlayPauseButton.Content = _uiWantsPlaying ? GlyphPause : GlyphPlay;
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

            return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }
    }
}