using System.Windows;
using System.Windows.Controls.Primitives;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private enum RepeatMode { None, One, All }

        private RepeatMode _repeatMode = RepeatMode.All;

        private const string GlyphRepeatAll = "\uE8EE";
        private const string GlyphRepeatOne = "\uE8ED";

        private bool IsRepeatOff() => false;
        private bool IsRepeatOne() => _repeatMode == RepeatMode.One;
        private bool IsRepeatAll() => _repeatMode != RepeatMode.One;

        private void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            _repeatMode = _repeatMode == RepeatMode.One
                ? RepeatMode.All
                : RepeatMode.One;

            UpdateRepeatButtonVisuals();

            if (ShuffleEnabled)
            {
                RebuildShuffleForTargetPlaylist();
            }

            LogTransport("RepeatButton_Click", $"newRepeatMode={_repeatMode}");
            RequestSaveState();
        }

        private void UpdateRepeatButtonVisuals()
        {
            bool repeatOne = _repeatMode == RepeatMode.One;

            // Always show Repeat One glyph
            RepeatButton.Content = GlyphRepeatOne;

            // Always same tooltip
            RepeatButton.ToolTip = "Repeat One";

            // Highlight only when enabled
            SetGreenHighlight(RepeatButton, repeatOne);
        }

        private int? DetermineNextTrackIndexForPlayingPlaylist(bool wrap)
        {
            int? result;
            string reason;

            if (Playing.Tracks.Count == 0 || Playing.Index < 0)
            {
                result = null;
                reason = "NoPlayingTrack";
                LogTransport("DetermineNextTrackIndexForPlayingPlaylist", $"wrap={wrap} reason={reason} result=null");
                return result;
            }

            if (IsRepeatOne())
            {
                result = Playing.Index;
                reason = "RepeatOne";
                LogTransport("DetermineNextTrackIndexForPlayingPlaylist", $"wrap={wrap} reason={reason} result={result}");
                return result;
            }

            if (ShuffleEnabled)
            {
                if (IsRepeatAll())
                {
                    if (Playing.ShuffleBag.Count == 0 && Playing.Tracks.Count > 1)
                    {
                        RebuildShuffleBagForPlaylist(_playingPlaylist, keepCurrent: true);
                        AvoidImmediateShuffleRepeatForPlaylist(_playingPlaylist);
                        LogTransport("DetermineNextTrackIndexForPlayingPlaylist.RebuiltShuffleBag");
                    }
                }

                result = PeekNextShuffleIndexForPlaylist(_playingPlaylist);
                reason = "Shuffle";
                LogTransport("DetermineNextTrackIndexForPlayingPlaylist", $"wrap={wrap} reason={reason} result={(result?.ToString() ?? "null")}");
                return result;
            }

            if (IsRepeatAll())
            {
                result = (_activePlaylist == _playingPlaylist)
                    ? GetAdjacentIndexByView(+1, wrap: true)
                    : GetAdjacentIndexByStoredView(_playingPlaylist, Playing.Index, +1, wrap: true);
                reason = "RepeatAll";
                LogTransport("DetermineNextTrackIndexForPlayingPlaylist", $"wrap={wrap} reason={reason} result={(result?.ToString() ?? "null")}");
                return result;
            }

            result = (_activePlaylist == _playingPlaylist)
                ? GetAdjacentIndexByView(+1, wrap: wrap)
                : GetAdjacentIndexByStoredView(_playingPlaylist, Playing.Index, +1, wrap: wrap);
            reason = "NormalAdvance";
            LogTransport("DetermineNextTrackIndexForPlayingPlaylist", $"wrap={wrap} reason={reason} result={(result?.ToString() ?? "null")}");
            return result;
        }
    }
}