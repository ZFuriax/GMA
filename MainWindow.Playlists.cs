using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private const string RecentlyOpenedPlaylistName = "Recently Opened";

        private void RenameActivePlaylistTo(string name)
        {
            if (_activePlaylist < 0 || _activePlaylist >= _playlists.Count)
                return;

            string trimmed = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            _playlists[_activePlaylist].Name = trimmed;
            BuildTabs(_activePlaylist);
            PlaylistTabs.SelectedIndex = _activePlaylist;
            RequestSaveState();
        }

        public void OpenFromShell(IEnumerable<string> rawPaths)
        {
            var files = NormalizeIncomingShellFiles(rawPaths);
            if (files.Count == 0)
                return;

            CancelWaveformInteraction();

            int recentIndex = EnsurePlaylistExists(RecentlyOpenedPlaylistName);
            var recent = _playlists[recentIndex];

            foreach (var file in files)
                AddOrMoveTrackToPlaylist(recent, file);

            BuildTabs();
            recentIndex = 0;
            recent = _playlists[recentIndex];

            SelectPlaylist(recentIndex);

            string firstToPlay = files[0];
            int playIndex = recent.Tracks.FindIndex(p =>
                string.Equals(p, firstToPlay, StringComparison.OrdinalIgnoreCase));

            if (playIndex < 0)
                playIndex = recent.Tracks.Count - 1;

            recent.Index = playIndex;

            RefreshPlaylistUI();
            SyncPlaylistSelection();

            _resumePending = false;
            _resumeFile = null;
            _resumeSeconds = 0.0;

            if (TryLoadCurrentTrackOrHandleMissing())
            {
                try
                {
                    EnterPlayingState();
                }
                catch
                {
                    EnterPausedState();
                }
            }
            else
            {
                EnterPausedState();
            }

            RequestSaveState();
        }

        private List<string> NormalizeIncomingShellFiles(IEnumerable<string> rawPaths)
        {
            var result = new List<string>();
            if (rawPaths == null)
                return result;

            foreach (var raw in rawPaths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string path;
                try
                {
                    path = Path.GetFullPath(raw);
                }
                catch
                {
                    continue;
                }

                if (!File.Exists(path))
                    continue;

                string ext = Path.GetExtension(path);
                if (string.IsNullOrWhiteSpace(ext))
                    continue;

                bool supported = SupportedExt.Any(x =>
                    string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));

                if (!supported)
                    continue;

                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase))
                    result.Add(path);
            }

            return result;
        }

        private int EnsurePlaylistExists(string name)
        {
            int existing = _playlists.FindIndex(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
            {
                if (existing != 0)
                {
                    var pl = _playlists[existing];
                    _playlists.RemoveAt(existing);
                    _playlists.Insert(0, pl);
                }

                return 0;
            }

            _playlists.Insert(0, new PlaylistState { Name = name });
            return 0;
        }

        private static void AddOrMoveTrackToPlaylist(PlaylistState playlist, string path)
        {
            if (playlist == null || string.IsNullOrWhiteSpace(path))
                return;

            int existing = playlist.Tracks.FindIndex(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
                playlist.Tracks.RemoveAt(existing);

            playlist.Tracks.Add(path);
        }

        private void RemoveAt(int removeIndex)
        {
            if (removeIndex < 0 || removeIndex >= Active.Tracks.Count)
                return;

            string removedPath = Active.Tracks[removeIndex];
            bool wasPlaying = _player.PlaybackState == NAudio.Wave.PlaybackState.Playing;

            bool removingCurrent =
                !string.IsNullOrEmpty(_player.CurrentFile) &&
                string.Equals(_player.CurrentFile, removedPath, StringComparison.OrdinalIgnoreCase);

            if (removingCurrent)
                _player.Stop();

            Active.Tracks.RemoveAt(removeIndex);

            if (Active.Tracks.Count == 0)
            {
                Active.Index = -1;
                RefreshPlaylistUI();

                ForceUiResetAtStop();

                Active.ShuffleBag.Clear();
                Active.ShuffleHistory.Clear();
                return;
            }

            if (removeIndex < Active.Index)
                Active.Index--;

            if (removingCurrent)
            {
                Active.Index = Math.Clamp(removeIndex, 0, Active.Tracks.Count - 1);
                if (wasPlaying)
                {
                    _ = StartPlaybackAtActiveIndex(Active.Index);
                }
                else
                {
                    ForceLoadCurrent();
                }
            }

            RefreshPlaylistUI();

            if (ShuffleEnabled)
            {
                RebuildShuffleForTargetPlaylist();
            }

            RequestSaveState();
        }

        private void RemoveAll()
        {
            bool stop =
                !string.IsNullOrEmpty(_player.CurrentFile) &&
                Active.Tracks.Any(t => string.Equals(t, _player.CurrentFile, StringComparison.OrdinalIgnoreCase));

            if (stop)
                _player.Stop();

            Active.Tracks.Clear();
            Active.Index = -1;
            RefreshPlaylistUI();
            ForceUiResetAtStop();
            Active.ShuffleBag.Clear();
            Active.ShuffleHistory.Clear();
            RequestSaveState();
        }

        private void RemoveSelected()
        {
            if (PlaylistList.SelectedItems.Count == 0)
                return;

            var paths = PlaylistList.SelectedItems
                .OfType<PlaylistRow>()
                .Select(r => r.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var p in paths)
            {
                int idx;
                while ((idx = Active.Tracks.FindIndex(t =>
                           string.Equals(t, p, StringComparison.OrdinalIgnoreCase))) >= 0)
                {
                    RemoveAt(idx);
                }
            }
        }

        private void PlaylistRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected();
        private void PlaylistRemoveAll_Click(object sender, RoutedEventArgs e) => RemoveAll();

        private void PlaylistList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                PlaylistList.SelectAll();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete)
            {
                if (PlaylistList.SelectedItems.Count > 0)
                {
                    RemoveSelected();
                    e.Handled = true;
                }
            }
        }

        private static ListBoxItem? GetListBoxItemFromEventSource(DependencyObject? source)
        {
            while (source != null && source is not ListBoxItem)
                source = VisualTreeHelper.GetParent(source);

            return source as ListBoxItem;
        }

        private void PlaylistList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);

            if (element is ListBoxItem item)
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                if (!ctrl && !shift)
                {
                    if (!item.IsSelected)
                        PlaylistList.SelectedItem = item.Content;
                }

                PlaylistList.Focus();
            }
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);

                bool ok = paths != null && paths.Any(p => IsSupportedAudioFile(p) || IsDirectoryPath(p));

                e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
                return;
            }
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(PlaylistDragFormat))
            {
                e.Handled = false;
                return;
            }

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (paths == null || paths.Length == 0)
                return;

            bool wasEmpty = Active.Tracks.Count == 0;

            string? renameTo = null;
            if (wasEmpty && paths.Length == 1 && Directory.Exists(paths[0]))
            {
                var dir = paths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                renameTo = Path.GetFileName(dir);
            }

            var files = ExpandDroppedPathsToAudioFiles(paths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
                return;

            bool shouldAutoplay =
                !Active.IsAmbience &&
                _player.PlaybackState != NAudio.Wave.PlaybackState.Playing;
            int targetPlaylist = _activePlaylist;

            Active.Tracks.AddRange(files);
            if (wasEmpty)
                Active.Index = 0;

            if (!string.IsNullOrWhiteSpace(renameTo))
                RenameActivePlaylistTo(renameTo);

            RefreshPlaylistUI();

            if (ShuffleEnabled)
            {
                RebuildShuffleForTargetPlaylist();
            }

            if (shouldAutoplay && Active.Tracks.Count > 0)
            {
                SetPlayingPlaylist(targetPlaylist);
                ForceLoadCurrentForPlayingPlaylist();

                if (!string.IsNullOrEmpty(_player.CurrentFile))
                {
                    try
                    {
                        EnterPlayingState();
                    }
                    catch
                    {
                        EnterPausedState();
                    }
                }
            }

            RequestSaveState();
            e.Handled = true;
        }

        private void MovePlaylistItem(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= Active.Tracks.Count) return;

            if (toIndex < 0) toIndex = 0;
            if (toIndex > Active.Tracks.Count) toIndex = Active.Tracks.Count;

            if (fromIndex == toIndex)
                return;

            string item = Active.Tracks[fromIndex];
            Active.Tracks.RemoveAt(fromIndex);

            if (toIndex > fromIndex)
                toIndex--;

            if (toIndex < 0) toIndex = 0;
            if (toIndex > Active.Tracks.Count) toIndex = Active.Tracks.Count;

            Active.Tracks.Insert(toIndex, item);

            if (Active.Index == fromIndex)
            {
                Active.Index = toIndex;
            }
            else if (fromIndex < Active.Index && toIndex >= Active.Index)
            {
                Active.Index--;
            }
            else if (fromIndex > Active.Index && toIndex <= Active.Index)
            {
                Active.Index++;
            }

            RefreshPlaylistUI();

            PlaylistList.SelectedIndex = Math.Clamp(toIndex, 0, Active.Tracks.Count - 1);
            PlaylistList.ScrollIntoView(PlaylistList.SelectedItem);

            if (ShuffleEnabled)
            {
                RebuildShuffleForTargetPlaylist();
            }
        }
    }
}