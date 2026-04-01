using System;
using System.IO;
using System.Windows;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private PlaylistState Active => _playlists[_activePlaylist];
        private PlaylistState Playing => _playlists[_playingPlaylist];

        private int? FindTrackIndexInPlaylist(int playlistIndex, string? path)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count || string.IsNullOrWhiteSpace(path))
                return null;

            var pl = _playlists[playlistIndex];

            int idx = pl.Tracks.FindIndex(t =>
                string.Equals(t, path, StringComparison.OrdinalIgnoreCase));

            return idx >= 0 ? idx : null;
        }

        private void ApplyLoadedTrackUi(string path)
        {
            _uiCurrentFile = path;

            // Reset UI state that is "per-track"
            _crossfadeArmed = false;
            _abCrossfadeArmed = false;

            TrackTitleText.Text = CleanDisplayTitle(path);
            TimeText.Text = "00:00 / --:--";
            WaveformBar.Progress = 0.0;

            _waveRequestedPath = path;
            _ = EnsureWaveformAsync(path);
        }

        private void PausePlayerAndEnterPausedState()
        {
            _player.Pause();
            EnterPausedState();
        }

        private bool TryFindTrackInAnyPlaylist(string path, out int playlistIndex, out int trackIndex)
        {
            playlistIndex = -1;
            trackIndex = -1;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            for (int pi = 0; pi < _playlists.Count; pi++)
            {
                int idx = _playlists[pi].Tracks.FindIndex(t =>
                    string.Equals(t, path, StringComparison.OrdinalIgnoreCase));

                if (idx >= 0)
                {
                    playlistIndex = pi;
                    trackIndex = idx;
                    return true;
                }
            }

            return false;
        }

        private bool HandleMissingTrackInPlaylist(int playlistIndex, int removeIndex, bool refreshOnlyIfActive)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return false;

            var pl = _playlists[playlistIndex];

            MessageBox.Show(
                this,
                "Song file is missing.\n\nCheck the file path and re-add the song.",
                "Missing file",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            if (removeIndex >= 0 && removeIndex < pl.Tracks.Count)
                pl.Tracks.RemoveAt(removeIndex);

            if (pl.Tracks.Count == 0)
                pl.Index = -1;
            else
                pl.Index = Math.Clamp(removeIndex, 0, pl.Tracks.Count - 1);

            if (!refreshOnlyIfActive || _activePlaylist == playlistIndex)
                RefreshPlaylistUI();

            RequestSaveState();
            return pl.Tracks.Count > 0;
        }

        private bool TryLoadTrackFromPlaylist(int playlistIndex, bool syncSelectionWhenLoaded)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return false;

            var pl = _playlists[playlistIndex];

            if (pl.Index < 0 || pl.Index >= pl.Tracks.Count)
                return false;

            string path = pl.Tracks[pl.Index];

            try
            {
                _player.Load(path);
                ApplyLoadedTrackUi(path);

                if (syncSelectionWhenLoaded)
                    SyncPlaylistSelection();

                return true;
            }
            catch (FileNotFoundException)
            {
                HandleMissingTrackInPlaylist(
                    playlistIndex,
                    pl.Index,
                    refreshOnlyIfActive: playlistIndex == _playingPlaylist);

                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Could not load track.\n\n{ex.Message}",
                    "Playback Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private bool TryLoadCurrentTrackOrHandleMissing()
        {
            if (Active.Tracks.Count == 0 || Active.Index < 0 || Active.Index >= Active.Tracks.Count)
                return false;

            string path = Active.Tracks[Active.Index];

            if (!string.IsNullOrEmpty(_player.CurrentFile) &&
                string.Equals(_player.CurrentFile, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return TryLoadTrackFromPlaylist(_activePlaylist, syncSelectionWhenLoaded: true);
        }

        private void ForceLoadCurrent()
        {
            _ = TryLoadTrackFromPlaylist(_activePlaylist, syncSelectionWhenLoaded: true);
        }

        private void ForceLoadCurrentForPlayingPlaylist()
        {
            bool shouldSyncSelection = _activePlaylist == _playingPlaylist;
            _ = TryLoadTrackFromPlaylist(_playingPlaylist, syncSelectionWhenLoaded: shouldSyncSelection);
        }

        private void ApplyResumeIfPending()
        {
            if (!_resumePending || string.IsNullOrWhiteSpace(_resumeFile))
                return;

            try
            {
                if (File.Exists(_resumeFile))
                {
                    if (TryFindTrackInAnyPlaylist(_resumeFile, out int playlistIndex, out int trackIndex))
                    {
                        _playingPlaylist = playlistIndex;
                        Playing.Index = trackIndex;

                        _player.Load(_resumeFile);
                        ApplyLoadedTrackUi(_resumeFile);

                        if (_activePlaylist == _playingPlaylist)
                            SyncPlaylistSelection();
                    }
                    else
                    {
                        _player.Load(_resumeFile);
                        ApplyLoadedTrackUi(_resumeFile);
                    }

                    _player.Seek(TimeSpan.FromSeconds(_resumeSeconds), resume: false);
                }

                PausePlayerAndEnterPausedState();
            }
            catch
            {
                PausePlayerAndEnterPausedState();
            }
            finally
            {
                _resumePending = false;
            }
        }

        private void ForceUiTrackTo(string path)
        {
            ApplyLoadedTrackUi(path);

            // Make selection match the real playing file if it exists in the PLAYING playlist.
            // Prefer keeping the current index if it already points at this path; this avoids
            // collapsing to the first duplicate occurrence.
            int idx;

            if (Playing.Index >= 0 &&
                Playing.Index < Playing.Tracks.Count &&
                string.Equals(Playing.Tracks[Playing.Index], path, StringComparison.OrdinalIgnoreCase))
            {
                idx = Playing.Index;
            }
            else
            {
                idx = FindTrackIndexInPlaylist(_playingPlaylist, path) ?? -1;
            }

            if (idx >= 0)
            {
                Playing.Index = idx;

                if (_activePlaylist == _playingPlaylist)
                    SyncPlaylistSelection();
            }
        }
    }
}