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

            for (int i = 0; i < _playlists.Count; i++)
            {
                int idx = i;
                var pl = _playlists[i];

                var tab = new TabItem
                {
                    Header = BuildPlaylistTabHeader(idx, pl),
                    Tag = idx == _playingPlaylist ? "Playing" : null,
                    Style = (Style)FindResource("PlaylistTabItemStyle")
                };

                var ctx = new ContextMenu();

                var miRename = new MenuItem { Header = "Rename" };
                miRename.Click += (_, __) => BeginRenamePlaylist(idx);

                var miSetPhrase = new MenuItem { Header = "Set Key Phrases..." };
                miSetPhrase.Click += (_, __) => BeginSetVoiceTriggerPhrases(idx);

                //var miSetThresholds = new MenuItem { Header = "Set Confidence Thresholds..." };
                //miSetThresholds.Click += (_, __) => BeginSetVoiceConfidenceThresholds();

                var miClearPhrase = new MenuItem
                {
                    Header = "Clear Key Phrases",
                    IsEnabled = PlaylistHasAnyVoicePhrases(pl)
                };
                miClearPhrase.Click += (_, __) =>
                {
                    ClearVoiceTriggerPhrases(idx);
                    BuildTabs(_activePlaylist);
                };

                var miSetAmbience = new MenuItem
                {
                    Header = pl.IsAmbience ? "Unset Ambience Playlist" : "Set Ambience Playlist"
                };
                miSetAmbience.Click += (_, __) =>
                {
                    _playlists[idx].IsAmbience = !_playlists[idx].IsAmbience;
                    BuildTabs(_activePlaylist);
                    RequestSaveState();
                };

                var miRemove = new MenuItem { Header = "Remove" };
                miRemove.Click += (_, __) => RemovePlaylist(idx);

                ctx.Items.Add(miRename);
                ctx.Items.Add(new Separator());
                ctx.Items.Add(miSetPhrase);

                //ctx.Items.Add(miSetThresholds);

                ctx.Items.Add(miClearPhrase);
                ctx.Items.Add(miSetAmbience);
                ctx.Items.Add(new Separator());
                ctx.Items.Add(miRemove);

                tab.ContextMenu = ctx;
                PlaylistTabs.Items.Add(tab);
            }

            if (PlaylistTabs.Items.Count == 0)
                return;

            int indexToSelect =
                preferredIndex.HasValue &&
                preferredIndex.Value >= 0 &&
                preferredIndex.Value < PlaylistTabs.Items.Count
                    ? preferredIndex.Value
                    : currentSelectedIndex >= 0 &&
                      currentSelectedIndex < PlaylistTabs.Items.Count
                        ? currentSelectedIndex
                        : _activePlaylist >= 0 &&
                          _activePlaylist < PlaylistTabs.Items.Count
                            ? _activePlaylist
                            : 0;

            PlaylistTabs.SelectedIndex = indexToSelect;
            _activePlaylist = indexToSelect;
            RefreshPlayingPlaylistTabHighlight();
        }

        private void RefreshPlayingPlaylistTabHighlight()
        {
            for (int i = 0; i < PlaylistTabs.Items.Count; i++)
            {
                if (PlaylistTabs.Items[i] is TabItem tab)
                {
                    tab.Tag = (i == _playingPlaylist) ? "Playing" : null;
                }
            }
        }

        private void RemovePlaylist(int playlistIndex)
        {
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

                _playlists[playlistIndex].Name = name;
                BuildTabs(playlistIndex);
                PlaylistTabs.SelectedIndex = playlistIndex;
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
            if (PlaylistTabs.SelectedIndex >= 0 && PlaylistTabs.SelectedIndex < _playlists.Count)
                SelectPlaylist(PlaylistTabs.SelectedIndex);
        }

        private void SelectPlaylist(int index)
        {
            if (index < 0 || index >= _playlists.Count) return;

            _activePlaylist = index;
            PlaylistTabs.SelectedIndex = index;

            _currentSortColumn = Active.SortColumn;
            _currentSortDirection = Active.SortDirection;

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