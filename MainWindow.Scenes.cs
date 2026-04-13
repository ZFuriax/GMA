using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private const string ScenesTabName = "Scenes";

        private sealed class SceneDefinition
        {
            public string Name { get; set; } = "New Scene";

            public int MusicPlaylistIndex { get; set; } = -1;

            public string? Ambience1 { get; set; }
            public string? Ambience2 { get; set; }
            public string? Ambience3 { get; set; }

            public double MusicVolume { get; set; } = 1.0;
            public double Ambience1Volume { get; set; } = 1.0;
            public double Ambience2Volume { get; set; } = 1.0;
            public double Ambience3Volume { get; set; } = 1.0;

            public string? KeyPhrase { get; set; } = null;
        }

        private sealed class SceneRow
        {
            public string Name { get; }
            public string DisplayName => Name;
            public string AlbumText { get; }
            public string DurationText => "";
            public string ScenePlayPauseGlyph { get; }
            public Visibility DurationVisibility => Visibility.Collapsed;
            public Visibility ScenePlayPauseVisibility => Visibility.Visible;
            public int Index { get; }

            public SceneRow(string name, string keyPhraseText, int index, string scenePlayPauseGlyph)
            {
                Name = name;
                AlbumText = keyPhraseText;
                Index = index;
                ScenePlayPauseGlyph = scenePlayPauseGlyph;
            }
        }

        private readonly List<SceneDefinition> _scenes = new();
        private bool _isScenesTabSelected = false;

        private int _currentSceneIndex = -1;
        private bool _isApplyingScene = false;

        private void SelectScenesTab()
        {
            _isScenesTabSelected = true;
            SetScenesHeaderMode();

            if (PlaylistTabs.SelectedItem is TabItem tab)
                tab.Tag = "Scenes";

            RefreshScenesUI();
            ApplyScenesContextMenu();
        }

        private void RefreshScenesUI()
        {
            int selectedIndex = GetSelectedSceneIndex();

            PlaylistList.ItemsSource = null;

            var rows = _scenes
                .Select((s, i) => new SceneRow(
                    s.Name,
                    string.IsNullOrWhiteSpace(s.KeyPhrase) ? "" : s.KeyPhrase!,
                    i,
                    IsSceneRowPlaying(i) ? GlyphPause : GlyphPlay))
                .ToList();

            PlaylistList.ItemsSource = rows;

            if (selectedIndex >= 0 && selectedIndex < rows.Count)
                PlaylistList.SelectedIndex = selectedIndex;
            else
                PlaylistList.SelectedIndex = -1;
        }

        private void ApplyScenesContextMenu()
        {
            var ctx = new ContextMenu();

            if (PlaylistList.SelectedItem is SceneRow row)
            {
                var miPlay = new MenuItem { Header = "Play Scene" };
                miPlay.Click += (_, __) => PlayScene(row.Index);

                var miSetPhrase = new MenuItem { Header = "Set Key Phrase..." };
                miSetPhrase.Click += (_, __) => BeginSetSceneKeyPhrase(row.Index);

                var miClearPhrase = new MenuItem
                {
                    Header = "Clear Key Phrase",
                    IsEnabled = rowHasSceneKeyPhrase(row.Index)
                };
                miClearPhrase.Click += (_, __) => ClearSceneKeyPhrase(row.Index);

                var miEdit = new MenuItem { Header = "Edit Scene..." };
                miEdit.Click += (_, __) => EditScene(row.Index);

                var miRemove = new MenuItem { Header = "Remove Scene" };
                miRemove.Click += (_, __) => RemoveScene(row.Index);

                ctx.Items.Add(miPlay);
                ctx.Items.Add(new Separator());
                ctx.Items.Add(miSetPhrase);
                ctx.Items.Add(miClearPhrase);
                ctx.Items.Add(new Separator());
                ctx.Items.Add(miEdit);
                ctx.Items.Add(new Separator());
                ctx.Items.Add(miRemove);
            }
            else
            {
                var miCreate = new MenuItem { Header = "Create Scene" };
                miCreate.Click += (_, __) => CreateSceneFromCurrentState();
                ctx.Items.Add(miCreate);
            }

            PlaylistList.ContextMenu = ctx;
        }

        private bool rowHasSceneKeyPhrase(int sceneIndex)
        {
            return sceneIndex >= 0 &&
                   sceneIndex < _scenes.Count &&
                   !string.IsNullOrWhiteSpace(_scenes[sceneIndex].KeyPhrase);
        }

        private int GetCurrentMusicPlaylistIndexForScene()
        {
            if (_activePlaylist < 0 || _activePlaylist >= _playlists.Count)
                return -1;

            if (string.Equals(Active.Name, AmbiencePlaylistName, StringComparison.OrdinalIgnoreCase))
                return -1;

            return _activePlaylist;
        }

        private SceneDefinition CaptureCurrentSceneDefinition(string sceneName)
        {
            return new SceneDefinition
            {
                Name = sceneName,
                MusicPlaylistIndex = GetCurrentMusicPlaylistIndexForScene(),
                Ambience1 = _sceneTracks[1],
                Ambience2 = _sceneTracks[2],
                Ambience3 = _sceneTracks[3],
                MusicVolume = _sceneLaneVolumes[0],
                Ambience1Volume = _sceneLaneVolumes[1],
                Ambience2Volume = _sceneLaneVolumes[2],
                Ambience3Volume = _sceneLaneVolumes[3]
            };
        }

        private void CreateSceneFromCurrentState()
        {
            string? name = PromptForSceneName("Create Scene", "");
            if (string.IsNullOrWhiteSpace(name))
                return;

            _scenes.Add(CaptureCurrentSceneDefinition(name.Trim()));
            RefreshScenesUI();
            RequestSaveState();
        }

        private void UpdateSceneFromCurrentState(int sceneIndex)
        {
            if (sceneIndex < 0 || sceneIndex >= _scenes.Count)
                return;

            string existingName = _scenes[sceneIndex].Name;
            _scenes[sceneIndex] = CaptureCurrentSceneDefinition(existingName);

            RefreshScenesUI();
            PlaylistList.SelectedIndex = sceneIndex;
            RequestSaveState();
        }

        private void RemoveScene(int sceneIndex)
        {
            if (sceneIndex < 0 || sceneIndex >= _scenes.Count)
                return;

            var result = MessageBox.Show(
                this,
                $"Remove scene '{_scenes[sceneIndex].Name}'?",
                "Remove Scene",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            _scenes.RemoveAt(sceneIndex);
            RefreshScenesUI();
            RequestSaveState();
        }

        private void EditScene(int sceneIndex)
        {
            if (sceneIndex < 0 || sceneIndex >= _scenes.Count)
                return;

            var scene = _scenes[sceneIndex];

            var musicChoices = _playlists
                .Select((p, i) => new { Playlist = p, Index = i })
                .Where(x => !string.Equals(x.Playlist.Name, AmbiencePlaylistName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int ambienceIndex = EnsureAmbiencePlaylistExists();
            var ambienceTracks = _playlists[ambienceIndex].Tracks.ToList();

            var dlg = new Window
            {
                Title = "Edit Scene",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 420,
                Height = 300,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17)),
                Foreground = System.Windows.Media.Brushes.White
            };

            var root = new Grid { Margin = new Thickness(12) };
            for (int i = 0; i < 6; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tbName = new TextBox { Text = scene.Name, Margin = new Thickness(0, 0, 0, 8) };
            var cbMusic = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            var cbA1 = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            var cbA2 = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            var cbA3 = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };

            foreach (var m in musicChoices)
                cbMusic.Items.Add(new ComboBoxItem { Content = m.Playlist.Name, Tag = m.Index });

            cbA1.Items.Add(new ComboBoxItem { Content = "(None)", Tag = null });
            cbA2.Items.Add(new ComboBoxItem { Content = "(None)", Tag = null });
            cbA3.Items.Add(new ComboBoxItem { Content = "(None)", Tag = null });

            foreach (var track in ambienceTracks)
            {
                string title = CleanDisplayTitle(track);
                cbA1.Items.Add(new ComboBoxItem { Content = title, Tag = track });
                cbA2.Items.Add(new ComboBoxItem { Content = title, Tag = track });
                cbA3.Items.Add(new ComboBoxItem { Content = title, Tag = track });
            }

            SetComboByTag(cbMusic, scene.MusicPlaylistIndex);
            SetComboByTag(cbA1, scene.Ambience1);
            SetComboByTag(cbA2, scene.Ambience2);
            SetComboByTag(cbA3, scene.Ambience3);

            AddEditRow(root, 0, "Scene Name:", tbName);
            AddEditRow(root, 1, "Music Playlist:", cbMusic);
            AddEditRow(root, 2, "Ambience 1:", cbA1);
            AddEditRow(root, 3, "Ambience 2:", cbA2);
            AddEditRow(root, 4, "Ambience 3:", cbA3);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnOk = new Button { Content = "OK", MinWidth = 80, Margin = new Thickness(0, 8, 8, 0), IsDefault = true };
            var btnCancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(0, 8, 0, 0) };


            btnCancel.Click += (_, __) => dlg.DialogResult = false;
            btnOk.Click += (_, __) => dlg.DialogResult = true;

            buttons.Children.Add(btnOk);
            buttons.Children.Add(btnCancel);

            Grid.SetRow(buttons, 7);
            Grid.SetColumnSpan(buttons, 2);
            root.Children.Add(buttons);

            dlg.Content = root;

            if (dlg.ShowDialog() != true)
                return;

            string newName = string.IsNullOrWhiteSpace(tbName.Text) ? scene.Name : tbName.Text.Trim();
            int newMusicPlaylistIndex = GetSelectedComboTagInt(cbMusic);
            string? newAmbience1 = GetSelectedComboTagString(cbA1);
            string? newAmbience2 = GetSelectedComboTagString(cbA2);
            string? newAmbience3 = GetSelectedComboTagString(cbA3);

            scene.Name = newName;
            scene.MusicPlaylistIndex = newMusicPlaylistIndex;
            scene.Ambience1 = newAmbience1;
            scene.Ambience2 = newAmbience2;
            scene.Ambience3 = newAmbience3;

            // If this is the currently active scene, keep the live scene-strip structure in sync
            // so later auto-save does not overwrite the edited values.
            if (_currentSceneIndex == sceneIndex)
            {
                _activePlaylist = newMusicPlaylistIndex >= 0 ? newMusicPlaylistIndex : _activePlaylist;

                _sceneTracks[1] = newAmbience1;
                _sceneTracks[2] = newAmbience2;
                _sceneTracks[3] = newAmbience3;

                if (!string.IsNullOrWhiteSpace(newAmbience1))
                    UpdateSceneLaneUI(1, newAmbience1);
                else
                    SceneText2.Text = "Ambience 1 Vol";

                if (!string.IsNullOrWhiteSpace(newAmbience2))
                    UpdateSceneLaneUI(2, newAmbience2);
                else
                    SceneText3.Text = "Ambience 2 Vol";

                if (!string.IsNullOrWhiteSpace(newAmbience3))
                    UpdateSceneLaneUI(3, newAmbience3);
                else
                    SceneText4.Text = "Ambience 3 Vol";
            }

            RefreshScenesUI();
            PlaylistList.SelectedIndex = sceneIndex;
            RequestSaveState();
        }

        private int GetSelectedSceneIndex()
        {
            if (!_isScenesTabSelected)
                return -1;

            if (PlaylistList.SelectedItem is SceneRow row)
                return row.Index;

            int selectedIndex = PlaylistList.SelectedIndex;
            if (selectedIndex >= 0 && selectedIndex < _scenes.Count)
                return selectedIndex;

            return -1;
        }

        private void PlayScene(int sceneIndex)
        {
            if (sceneIndex < 0 || sceneIndex >= _scenes.Count)
                return;

            var scene = _scenes[sceneIndex];

            _currentSceneIndex = sceneIndex;
            PlaylistList.SelectedIndex = sceneIndex;
            _isApplyingScene = true;

            try
            {
                if (!_sceneEnabled)
                {
                    _sceneEnabled = true;
                    SceneStrip.Visibility = Visibility.Visible;
                    SceneButton.IsChecked = true;
                    BuildTabs(_activePlaylist);
                    PlaylistTabs.SelectedIndex = 0;
                    _isScenesTabSelected = true;
                }

                // Stop existing ambience and clear lanes 1..3
                for (int i = 1; i <= 3; i++)
                    ClearSceneLane(i);

                // Apply saved volumes first
                SetSceneLaneVolumeVisual(0, scene.MusicVolume);
                SetSceneLaneVolumeVisual(1, scene.Ambience1Volume);
                SetSceneLaneVolumeVisual(2, scene.Ambience2Volume);
                SetSceneLaneVolumeVisual(3, scene.Ambience3Volume);
                ApplyCombinedSceneAndMasterVolumes();

                // Music
                if (scene.MusicPlaylistIndex >= 0 && scene.MusicPlaylistIndex < _playlists.Count)
                {
                    // Set the active playlist for playback logic, but do not visibly switch tabs.
                    _activePlaylist = scene.MusicPlaylistIndex;
                    _currentSortColumn = Active.SortColumn;
                    _currentSortDirection = Active.SortDirection;

                    if (Active.Tracks.Count > 0)
                    {
                        Active.Index = 0;
                        PlayIndex(0);
                    }

                    // Re-select the Scenes tab after playback startup has finished its own selection work.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_sceneEnabled && PlaylistTabs.Items.Count > 0)
                        {
                            PlaylistTabs.SelectedIndex = 0;
                            SelectScenesTab();
                            PlaylistList.SelectedIndex = sceneIndex;
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }

                // Ambience
                if (!string.IsNullOrWhiteSpace(scene.Ambience1))
                    SetSceneTrack(1, scene.Ambience1);

                if (!string.IsNullOrWhiteSpace(scene.Ambience2))
                    SetSceneTrack(2, scene.Ambience2);

                if (!string.IsNullOrWhiteSpace(scene.Ambience3))
                    SetSceneTrack(3, scene.Ambience3);
            }
            finally
            {
                _isApplyingScene = false;
            }

            RequestSaveState();
        }

        private static void AddEditRow(Grid root, int row, string label, Control editor)
        {
            var tb = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 8)
            };

            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, 0);
            root.Children.Add(tb);

            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            root.Children.Add(editor);
        }

        private static void SetComboByTag(ComboBox combo, object? tagValue)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item)
                {
                    if (Equals(item.Tag, tagValue))
                    {
                        combo.SelectedIndex = i;
                        return;
                    }
                }
            }

            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private static int GetSelectedComboTagInt(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is int value)
                return value;

            return -1;
        }

        private static string? GetSelectedComboTagString(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem item)
                return item.Tag as string;

            return null;
        }

        private bool IsSceneRowPlaying(int sceneIndex)
        {
            if (sceneIndex != _currentSceneIndex)
                return false;

            return _uiWantsPlaying || HasPlayingSceneAmbienceTracks();
        }

        private void PauseCurrentSceneFromRow()
        {
            _player.Pause(reason: "SceneRowPlayPause_Click:Pause");
            EnterPausedState();

            for (int lane = 1; lane <= 3; lane++)
            {
                if (!string.IsNullOrWhiteSpace(_sceneTracks[lane]))
                {
                    _sceneAudio.StopLane(SceneUiLaneToEngineLane(lane));
                    SetSceneLanePlayingState(lane, false);
                }
            }
        }

        private void SetScenesHeaderMode()
        {
            PlaylistHeaderGrid.Visibility = Visibility.Visible;

            PlaylistHeaderPrimaryButton.Content = "Scene";
            PlaylistHeaderSecondaryButton.Content = "Key Phrase";
            PlaylistHeaderTertiaryButton.Content = $"{GlyphPlay} {GlyphPause}";
            PlaylistHeaderTertiaryButton.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
            PlaylistHeaderTertiaryButton.Margin = new Thickness(0, 0, 10, 0);

            PlaylistHeaderPrimaryButton.IsHitTestVisible = false;
            PlaylistHeaderSecondaryButton.IsHitTestVisible = false;
            PlaylistHeaderTertiaryButton.IsHitTestVisible = false;
        }

        private void ResumeCurrentSceneFromRow()
        {
            bool musicStarted = false;

            if (!string.IsNullOrWhiteSpace(_player.CurrentFile))
                musicStarted = EnterPlayingState();

            for (int lane = 1; lane <= 3; lane++)
            {
                string? path = _sceneTracks[lane];
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                _sceneAudio.PlayLane(
                    SceneUiLaneToEngineLane(lane),
                    path,
                    loop: true,
                    volume: (float)_sceneLaneVolumes[lane]);

                SetSceneLanePlayingState(lane, true);
            }

            if (!musicStarted && _currentSceneIndex >= 0 && _currentSceneIndex < _scenes.Count)
                PlayScene(_currentSceneIndex);
        }

        private void SceneRowPlayPause_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Button btn || btn.Tag is not int sceneIndex)
                return;

            if (sceneIndex != _currentSceneIndex)
            {
                PlayScene(sceneIndex);
                RefreshScenesUI();
                return;
            }

            if (IsSceneRowPlaying(sceneIndex))
                PauseCurrentSceneFromRow();
            else
                ResumeCurrentSceneFromRow();

            RefreshScenesUI();
        }

        private void OnSceneLaneVolumeChanged(int laneIndex, double volume)
        {
            if (_isApplyingScene)
                return;

            int selectedSceneIndex = GetSelectedSceneIndex();
            if (selectedSceneIndex < 0 || selectedSceneIndex >= _scenes.Count)
                return;

            var scene = _scenes[selectedSceneIndex];

            switch (laneIndex)
            {
                case 0:
                    scene.MusicVolume = volume;
                    break;
                case 1:
                    scene.Ambience1Volume = volume;
                    break;
                case 2:
                    scene.Ambience2Volume = volume;
                    break;
                case 3:
                    scene.Ambience3Volume = volume;
                    break;
                default:
                    return;
            }

            RequestSaveState();
        }

        private void BeginSetSceneKeyPhrase(int sceneIndex)
        {
            if (sceneIndex < 0 || sceneIndex >= _scenes.Count)
                return;

            var scene = _scenes[sceneIndex];

            string? value = PromptForSceneName(
                "Set Key Phrase",
                scene.KeyPhrase ?? "");

            if (value == null)
                return;

            value = value.Trim();
            scene.KeyPhrase = string.IsNullOrWhiteSpace(value) ? null : value;

            RefreshScenesUI();
            PlaylistList.SelectedIndex = sceneIndex;
            RequestSaveState();
        }

        private void ClearSceneKeyPhrase(int sceneIndex)
        {
            if (sceneIndex < 0 || sceneIndex >= _scenes.Count)
                return;

            _scenes[sceneIndex].KeyPhrase = null;

            RefreshScenesUI();
            PlaylistList.SelectedIndex = sceneIndex;
            RequestSaveState();
        }

        private string? PromptForSceneName(string title, string initialValue)
        {
            var dlg = new Window
            {
                Title = title,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 320,
                Height = 130,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17)),
                Foreground = System.Windows.Media.Brushes.White
            };

            var tb = new TextBox
            {
                Margin = new Thickness(10),
                Text = initialValue ?? ""
            };

            var ok = new Button { Content = "OK", Margin = new Thickness(10), MinWidth = 80, IsDefault = true };
            var cancel = new Button { Content = "Cancel", Margin = new Thickness(10), MinWidth = 80 };

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
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);


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

            return dlg.ShowDialog() == true ? tb.Text : null;
        }
    }
}