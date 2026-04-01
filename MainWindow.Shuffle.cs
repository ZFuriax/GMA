namespace MusicPlayer
{
    public partial class MainWindow
    {
        private int GetShuffleTargetPlaylistIndex()
        {
            bool playbackOwnsContext =
                !string.IsNullOrWhiteSpace(_player.CurrentFile) ||
                _uiWantsPlaying;

            int idx = playbackOwnsContext ? _playingPlaylist : _activePlaylist;

            if (idx < 0 || idx >= _playlists.Count)
                idx = Math.Clamp(_activePlaylist, 0, _playlists.Count - 1);

            return idx;
        }

        private void RebuildShuffleForTargetPlaylist()
        {
            if (!ShuffleEnabled || _playlists.Count == 0)
                return;

            int idx = GetShuffleTargetPlaylistIndex();
            RebuildShuffleBagForPlaylist(idx, keepCurrent: true);
            AvoidImmediateShuffleRepeatForPlaylist(idx);
        }

        private void ClearShuffleStateForAllPlaylists()
        {
            foreach (var pl in _playlists)
            {
                pl.ShuffleBag.Clear();
                pl.ShuffleHistory.Clear();
            }
        }

        private void RebuildShuffleBagForPlaylist(int playlistIndex, bool keepCurrent)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return;

            var pl = _playlists[playlistIndex];
            pl.ShuffleBag.Clear();

            if (pl.Tracks.Count <= 1)
                return;

            for (int i = 0; i < pl.Tracks.Count; i++)
            {
                if (keepCurrent && i == pl.Index)
                    continue;

                pl.ShuffleBag.Add(i);
            }

            for (int i = pl.ShuffleBag.Count - 1; i > 0; i--)
            {
                int j = pl.Rng.Next(i + 1);
                (pl.ShuffleBag[i], pl.ShuffleBag[j]) = (pl.ShuffleBag[j], pl.ShuffleBag[i]);
            }
        }

        private bool EnsureShuffleBagReadyForPlaylist(int playlistIndex)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return false;

            var pl = _playlists[playlistIndex];

            if (pl.Tracks.Count == 0)
                return false;

            if (pl.Tracks.Count == 1)
                return true;

            if (pl.ShuffleBag.Count == 0)
            {
                if (_repeatMode == RepeatMode.All || ShuffleEnabled)
                {
                    RebuildShuffleBagForPlaylist(playlistIndex, keepCurrent: true);
                    AvoidImmediateShuffleRepeatForPlaylist(playlistIndex);
                }
                else
                {
                    return false;
                }
            }

            return pl.ShuffleBag.Count > 0;
        }

        private int? PeekNextShuffleIndexForPlaylist(int playlistIndex)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return null;

            var pl = _playlists[playlistIndex];

            if (pl.Tracks.Count == 0)
                return null;

            if (pl.Tracks.Count == 1)
                return 0;

            if (!EnsureShuffleBagReadyForPlaylist(playlistIndex))
                return null;

            return pl.ShuffleBag[0];
        }

        private int? GetNextShuffleIndexForPlaylist(int playlistIndex)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return null;

            var pl = _playlists[playlistIndex];

            if (pl.Tracks.Count == 0)
                return null;

            if (pl.Tracks.Count == 1)
                return 0;

            if (!EnsureShuffleBagReadyForPlaylist(playlistIndex))
                return null;

            int next = pl.ShuffleBag[0];
            pl.ShuffleBag.RemoveAt(0);
            return next;
        }

        private void AvoidImmediateShuffleRepeatForPlaylist(int playlistIndex)
        {
            if (!ShuffleEnabled)
                return;

            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return;

            var pl = _playlists[playlistIndex];

            if (pl.Index < 0 || pl.Tracks.Count <= 1)
                return;

            if (pl.ShuffleBag.Count <= 1)
                return;

            if (pl.ShuffleBag[0] == pl.Index)
            {
                int swapIndex = pl.ShuffleBag.FindIndex(i => i != pl.Index);
                if (swapIndex > 0)
                {
                    int tmp = pl.ShuffleBag[0];
                    pl.ShuffleBag[0] = pl.ShuffleBag[swapIndex];
                    pl.ShuffleBag[swapIndex] = tmp;
                }
            }
        }
    }
}