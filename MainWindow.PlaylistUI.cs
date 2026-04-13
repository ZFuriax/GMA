using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private void BuildTabs(int? preferredIndex = null)
        {
            int currentSelectedIndex = PlaylistTabs.SelectedIndex;

            PlaylistTabs.Items.Clear();

            var visiblePlaylistIndices = new List<int>();

            int ambienceIndex = _playlists.FindIndex(p =>
                string.Equals(p.Name, AmbiencePlaylistName, StringComparison.OrdinalIgnoreCase));

            if (_sceneEnabled)
            {
                var scenesTab = new TabItem
                {
                    Header = ScenesTabName,
                    Tag = "Scenes",
                    Style = (Style)FindResource("PlaylistTabItemStyle")
                };

                PlaylistTabs.Items.Add(scenesTab);

                if (ambienceIndex >= 0)
                    visiblePlaylistIndices.Add(ambienceIndex);
            }

            for (int i = 0; i < _playlists.Count; i++)
            {
                if (i == ambienceIndex)
                    continue;

                visiblePlaylistIndices.Add(i);
            }

            foreach (int playlistIndex in visiblePlaylistIndices)
            {
                var playlist = _playlists[playlistIndex];
                bool isAmbiencePlaylist = string.Equals(
                    playlist.Name,
                    AmbiencePlaylistName,
                    StringComparison.OrdinalIgnoreCase);

                var tab = new TabItem
                {
                    Header = BuildPlaylistTabHeader(playlistIndex, playlist),
                    Tag = playlistIndex == _playingPlaylist ? "Playing" : null,
                    Style = (Style)FindResource("PlaylistTabItemStyle")
                };

                if (!isAmbiencePlaylist)
                {
                    var ctx = new ContextMenu();

                    var miRename = new MenuItem { Header = "Rename" };
                    miRename.Click += (_, __) => BeginRenamePlaylist(playlistIndex);

                    var miSetPhrase = new MenuItem { Header = "Set Key Phrases..." };
                    miSetPhrase.Click += (_, __) => BeginSetVoiceTriggerPhrases(playlistIndex);

                    var miClearPhrase = new MenuItem
                    {
                        Header = "Clear Key Phrases",
                        IsEnabled = PlaylistHasAnyVoicePhrases(playlist)
                    };
                    miClearPhrase.Click += (_, __) =>
                    {
                        ClearVoiceTriggerPhrases(playlistIndex);
                        BuildTabs(_activePlaylist);
                    };

                    var miRemove = new MenuItem { Header = "Remove" };
                    miRemove.Click += (_, __) => RemovePlaylist(playlistIndex);

                    ctx.Items.Add(miRename);
                    ctx.Items.Add(new Separator());
                    ctx.Items.Add(miSetPhrase);
                    ctx.Items.Add(miClearPhrase);
                    ctx.Items.Add(new Separator());
                    ctx.Items.Add(miRemove);

                    tab.ContextMenu = ctx;
                }

                PlaylistTabs.Items.Add(tab);
            }

            if (PlaylistTabs.Items.Count == 0)
                return;

            int targetTabIndex;

            if (preferredIndex.HasValue)
            {
                int preferredPlaylistIndex = preferredIndex.Value;

                int visibleOffset = visiblePlaylistIndices.IndexOf(preferredPlaylistIndex);
                if (visibleOffset >= 0)
                    targetTabIndex = _sceneEnabled ? visibleOffset + 1 : visibleOffset;
                else
                    targetTabIndex = _sceneEnabled ? 1 : 0;
            }
            else
            {
                int activeVisibleOffset = visiblePlaylistIndices.IndexOf(_activePlaylist);
                if (activeVisibleOffset >= 0)
                    targetTabIndex = _sceneEnabled ? activeVisibleOffset + 1 : activeVisibleOffset;
                else
                    targetTabIndex = _sceneEnabled ? 1 : 0;
            }

            PlaylistTabs.SelectedIndex = Math.Clamp(targetTabIndex, 0, PlaylistTabs.Items.Count - 1);
            RefreshPlayingPlaylistTabHighlight();
        }

        private void RefreshPlayingPlaylistTabHighlight()
        {
            int ambienceIndex = _playlists.FindIndex(p =>
                string.Equals(p.Name, AmbiencePlaylistName, StringComparison.OrdinalIgnoreCase));

            var visiblePlaylistIndices = new List<int>();

            if (_sceneEnabled && ambienceIndex >= 0)
                visiblePlaylistIndices.Add(ambienceIndex);

            for (int i = 0; i < _playlists.Count; i++)
            {
                if (i == ambienceIndex)
                    continue;

                visiblePlaylistIndices.Add(i);
            }

            for (int i = 0; i < PlaylistTabs.Items.Count; i++)
            {
                if (PlaylistTabs.Items[i] is not TabItem tab)
                    continue;

                if (_sceneEnabled && i == 0)
                {
                    tab.Tag = "Scenes";
                    continue;
                }

                int visibleOffset = _sceneEnabled ? i - 1 : i;
                if (visibleOffset >= 0 && visibleOffset < visiblePlaylistIndices.Count)
                {
                    int playlistIndex = visiblePlaylistIndices[visibleOffset];
                    tab.Tag = (playlistIndex == _playingPlaylist) ? "Playing" : null;
                }
                else
                {
                    tab.Tag = null;
                }
            }
        }

        private void RemovePlaylist(int playlistIndex)
        {
            if (string.Equals(_playlists[playlistIndex].Name, "Ambience", StringComparison.OrdinalIgnoreCase))
                return;

            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return;

            // If user tries to remove the ONLY playlist, don't delete it.
            // Instead: stop playback, clear tracks, reset index, rename to "Playlist 1".
            if (_playlists.Count == 1)
            {
                if (_player.PlaybackState == NAudio.Wave.PlaybackState.Playing ||
                    _player.PlaybackState == NAudio.Wave.PlaybackState.Paused)
                {
                    _player.Stop();
                }

                var pl = _playlists[0];
                pl.Tracks.Clear();
                pl.Index = -1;
                pl.Name = "Playlist 1";

                _activePlaylist = 0;

                BuildTabs();
                SelectPlaylist(0);
                RefreshPlaylistUI();

                ForceUiResetAtStop();

                pl.ShuffleBag.Clear();
                pl.ShuffleHistory.Clear();

                pl.VoiceTriggerEnabled = false;
                pl.VoiceTriggerPhrase1 = null;
                pl.VoiceTriggerPhrase2 = null;
                pl.VoiceTriggerPhrase3 = null;
                pl.VoiceTriggerLastIndex = -1;
                pl.VoiceTriggerCooldownMs = DefaultVoiceTriggerCooldownMs;
                pl.VoiceTriggerLastFireUtc = DateTime.MinValue;

                RefreshVoiceCaptureState(showErrors: false);
                RequestSaveState();

                return;
            }

            bool removingActive = playlistIndex == _activePlaylist;

            _playlists.RemoveAt(playlistIndex);

            if (removingActive)
            {
                _activePlaylist = Math.Clamp(playlistIndex - 1, 0, _playlists.Count - 1);
            }
            else if (playlistIndex < _activePlaylist)
            {
                _activePlaylist = Math.Max(0, _activePlaylist - 1);
            }

            BuildTabs();
            SelectPlaylist(_activePlaylist);
            RefreshVoiceCaptureState(showErrors: false);
            RequestSaveState();
        }

        private void BeginRenamePlaylist(int playlistIndex)
        {
            if (string.Equals(_playlists[playlistIndex].Name, "Ambience", StringComparison.OrdinalIgnoreCase))
                return;

            if (playlistIndex < 0 || playlistIndex >= _playlists.Count) return;

            string current = _playlists[playlistIndex].Name;

            var dlg = new Window
            {
                Title = "Rename playlist",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 320,
                Height = 130,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(17, 17, 17)),
                Foreground = Brushes.White
            };

            var tb = new TextBox
            {
                Margin = new Thickness(10),
                Text = current
            };

            var ok = new Button { Content = "OK", Margin = new Thickness(10), MinWidth = 80 };
            var cancel = new Button { Content = "Cancel", Margin = new Thickness(10), MinWidth = 80 };

            ok.IsDefault = true;

            ok.Click += (_, __) => dlg.DialogResult = true;
            cancel.Click += (_, __) => dlg.DialogResult = false;

            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    dlg.DialogResult = true;
                    e.Handled = true;
                }
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(tb);
            dlg.Content = root;

            dlg.Loaded += (_, __) =>
            {
                tb.Focus();
                tb.SelectAll();
            };

            if (dlg.ShowDialog() == true)
            {
                string name = tb.Text.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = current;

                if (IsReservedPlaylistName(name))
                {
                    MessageBox.Show(
                        this,
                        "Playlists cannot be named Scenes or Ambience.",
                        "Invalid Playlist Name",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    tb.Focus();
                    tb.SelectAll();
                    return;
                }

                _playlists[playlistIndex].Name = name;
                BuildTabs(playlistIndex);
                RequestSaveState();
            }
        }

        private void AddPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            int n = _playlists.Count + 1;
            _playlists.Add(new PlaylistState { Name = $"Playlist {n}" });

            BuildTabs();
            SelectPlaylist(_playlists.Count - 1);
            RequestSaveState();
        }

        private void PlaylistTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int selected = PlaylistTabs.SelectedIndex;

            int ambienceIndex = _playlists.FindIndex(p =>
                string.Equals(p.Name, AmbiencePlaylistName, StringComparison.OrdinalIgnoreCase));

            var visiblePlaylistIndices = new List<int>();

            if (_sceneEnabled && ambienceIndex >= 0)
                visiblePlaylistIndices.Add(ambienceIndex);

            for (int i = 0; i < _playlists.Count; i++)
            {
                if (i == ambienceIndex)
                    continue;

                visiblePlaylistIndices.Add(i);
            }

            if (_sceneEnabled)
            {
                if (selected == 0)
                {
                    SelectScenesTab();
                    return;
                }

                int visibleOffset = selected - 1;
                if (visibleOffset >= 0 && visibleOffset < visiblePlaylistIndices.Count)
                    SelectPlaylist(visiblePlaylistIndices[visibleOffset]);

                return;
            }

            if (selected >= 0 && selected < visiblePlaylistIndices.Count)
                SelectPlaylist(visiblePlaylistIndices[selected]);
        }

        private void SelectPlaylist(int index)
        {
            if (index < 0 || index >= _playlists.Count)
                return;

            _activePlaylist = index;

            int ambienceIndex = _playlists.FindIndex(p =>
                string.Equals(p.Name, AmbiencePlaylistName, StringComparison.OrdinalIgnoreCase));

            var visiblePlaylistIndices = new List<int>();

            if (_sceneEnabled && ambienceIndex >= 0)
                visiblePlaylistIndices.Add(ambienceIndex);

            for (int i = 0; i < _playlists.Count; i++)
            {
                if (i == ambienceIndex)
                    continue;

                visiblePlaylistIndices.Add(i);
            }

            int visibleOffset = visiblePlaylistIndices.IndexOf(index);
            int tabIndex = _sceneEnabled ? visibleOffset + 1 : visibleOffset;

            if (visibleOffset >= 0 && PlaylistTabs.SelectedIndex != tabIndex)
                PlaylistTabs.SelectedIndex = tabIndex;

            _currentSortColumn = Active.SortColumn;
            _currentSortDirection = Active.SortDirection;

            PlaylistHeaderGrid.Visibility = Visibility.Visible;
            _isScenesTabSelected = false;

            PlaylistHeaderPrimaryButton.Content = "Song";
            PlaylistHeaderSecondaryButton.Content = "Album";
            PlaylistHeaderTertiaryButton.Content = "Duration";
            PlaylistHeaderTertiaryButton.ClearValue(Control.FontFamilyProperty);
            PlaylistHeaderTertiaryButton.Margin = new Thickness(6, 0, 0, 0);

            PlaylistHeaderPrimaryButton.IsHitTestVisible = true;
            PlaylistHeaderSecondaryButton.IsHitTestVisible = true;
            PlaylistHeaderTertiaryButton.IsHitTestVisible = true;

            if (_sceneEnabled && PlaylistTabs.Items.Count > 0 && PlaylistTabs.Items[0] is TabItem scenesTab)
                scenesTab.Tag = "Scenes";

            PlaylistList.ContextMenu = _defaultPlaylistListContextMenu;

            RefreshPlaylistUI();

            if (!string.IsNullOrWhiteSpace(_currentSortColumn) && PlaylistList?.ItemsSource != null)
            {
                var view = CollectionViewSource.GetDefaultView(PlaylistList.ItemsSource);
                if (view is ListCollectionView lcv)
                {
                    lcv.CustomSort = new PlaylistRowComparer(_currentSortColumn, _currentSortDirection);
                    view.Refresh();
                }
            }
        }

        private void RefreshPlaylistUI()
        {
            _metaCts?.Cancel();
            _metaCts = new CancellationTokenSource();
            var ct = _metaCts.Token;

            var rows = Active.Tracks
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

            PlaylistList.ItemsSource = null;
            PlaylistList.ItemsSource = rows;

            SyncPlaylistSelection();

            _ = PopulateMetadataAsync(rows, ct);
        }

        private async Task PopulateMetadataAsync(List<PlaylistRow> rows, CancellationToken ct)
        {
            try
            {
                await Task.Run(async () =>
                {
                    foreach (var row in rows)
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        if (string.IsNullOrWhiteSpace(row.AlbumText))
                        {
                            string? cachedAlb = null;
                            lock (_metaCacheLock)
                                _albumCache.TryGetValue(row.Path, out cachedAlb);

                            if (!string.IsNullOrWhiteSpace(cachedAlb))
                            {
                                await Dispatcher.InvokeAsync(() => row.AlbumText = cachedAlb);
                            }
                            else
                            {
                                var alb = ProbeAlbumForUi(row.Path);
                                if (!string.IsNullOrWhiteSpace(alb))
                                {
                                    lock (_metaCacheLock)
                                        _albumCache[row.Path] = alb;

                                    await Dispatcher.InvokeAsync(() => row.AlbumText = alb);
                                }
                            }
                        }

                        if (ct.IsCancellationRequested)
                            return;

                        if (row.DurationText == "--:--")
                        {
                            string? cachedDur = null;
                            lock (_metaCacheLock)
                                _durationCache.TryGetValue(row.Path, out cachedDur);

                            if (!string.IsNullOrWhiteSpace(cachedDur) && cachedDur != "--:--")
                            {
                                await Dispatcher.InvokeAsync(() => row.DurationText = cachedDur);
                            }
                            else
                            {
                                string text = "--:--";

                                try
                                {
                                    var ts = ProbeDurationForUi(row.Path);
                                    if (ts.HasValue)
                                    {
                                        var t = ts.Value;
                                        text = t.Hours > 0
                                            ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}"
                                            : $"{t.Minutes:00}:{t.Seconds:00}";
                                    }
                                }
                                catch { }

                                if (text != "--:--")
                                {
                                    lock (_metaCacheLock)
                                        _durationCache[row.Path] = text;

                                    await Dispatcher.InvokeAsync(() => row.DurationText = text);
                                }
                            }
                        }
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private void SyncPlaylistSelection()
        {
            if (Active.Index < 0 || Active.Index >= Active.Tracks.Count)
            {
                PlaylistList.SelectedIndex = -1;
                return;
            }

            if (PlaylistList.ItemsSource is IEnumerable<PlaylistRow> rows)
            {
                var row = rows.FirstOrDefault(r => r.SourceIndex == Active.Index);

                if (row != null)
                {
                    PlaylistList.SelectedItem = row;
                    PlaylistList.ScrollIntoView(row);
                    return;
                }
            }

            PlaylistList.SelectedIndex = -1;
        }

        private void EnsureLoadedCurrent()
        {
            TryLoadCurrentTrackOrHandleMissing();
        }

        private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isScenesTabSelected)
            {
                ApplyScenesContextMenu();
                return;
            }

            // Only let selection changes move the active index when playback is truly stopped.
            // When paused, the first click of a double-click should not pre-mutate Active.Index.
            if (_player.PlaybackState != NAudio.Wave.PlaybackState.Stopped)
                return;

            if (PlaylistList.SelectedItem is not PlaylistRow row)
                return;

            if (row.SourceIndex >= 0 && row.SourceIndex < Active.Tracks.Count)
                Active.Index = row.SourceIndex;
        }

        private void Header_File_Click(object sender, RoutedEventArgs e) => SortPlaylist("DisplayName");
        private void Header_Album_Click(object sender, RoutedEventArgs e) => SortPlaylist("AlbumText");
        private void Header_Duration_Click(object sender, RoutedEventArgs e) => SortPlaylist("DurationText");
    }
}