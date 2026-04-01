using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Controls;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private string?[] _sceneTracks = new string?[4];

        private void SyncPlayPauseButton()
        {
            PlayPauseButton.Content = _uiWantsPlaying ? GlyphPause : GlyphPlay;
        }

        private int _enterPlayingStateInFlight = 0;

        private bool EnterPlayingState()
        {
            if (Interlocked.Exchange(ref _enterPlayingStateInFlight, 1) == 1)
            {
                LogTransport("EnterPlayingState.SkippedReentry");
                return _uiWantsPlaying;
            }

            try
            {
                _player.Play(reason: "EnterPlayingState");
                _uiWantsPlaying = true;
                SyncPlayPauseButton();
                return true;
            }
            catch (Exception ex) when (IsNoAudioOutputDeviceException(ex))
            {
                _uiWantsPlaying = false;
                SyncPlayPauseButton();

                MessageBox.Show(
                    this,
                    "No sound devices available.",
                    "Playback Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                LogTransport("PlaybackStartFailed.NoAudioDevice", ClipLogValue(ex.Message));
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _enterPlayingStateInFlight, 0);
            }
        }

        private void EnterPausedState()
        {
            _uiWantsPlaying = false;
            SyncPlayPauseButton();
        }

        private static bool IsNoAudioOutputDeviceException(Exception ex)
        {
            while (ex != null)
            {
                if (ex is InvalidOperationException ioe &&
                    ioe.Message.IndexOf("No usable audio output device", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                if (ex is NAudio.MmException)
                    return true;

                ex = ex.InnerException!;
            }

            return false;
        }

        private bool StartPlaybackAtActiveIndex(int newIndex)
        {
            if (Active.Tracks.Count == 0)
                return false;

            Active.Index = Math.Clamp(newIndex, 0, Active.Tracks.Count - 1);
            SyncPlaylistSelection();

            string targetPath = Active.Tracks[Active.Index];
            bool alreadyLoaded =
                !string.IsNullOrWhiteSpace(targetPath) &&
                !string.IsNullOrWhiteSpace(_player.CurrentFile) &&
                string.Equals(_player.CurrentFile, targetPath, StringComparison.OrdinalIgnoreCase);

            if (!alreadyLoaded)
            {
                ForceLoadCurrent();

                if (string.IsNullOrEmpty(_player.CurrentFile))
                {
                    EnterPausedState();
                    return false;
                }
            }
            else
            {
                LogTransport("StartPlaybackAtActiveIndex.SkipReload",
                    $"index={Active.Index} path=\"{ClipLogValue(targetPath)}\"");
            }

            return EnterPlayingState();
        }

        private bool StartPlaybackAtPlayingIndex(int newIndex)
        {
            if (_playingPlaylist < 0 || _playingPlaylist >= _playlists.Count)
                return false;

            if (Playing.Tracks.Count == 0)
                return false;

            Playing.Index = Math.Clamp(newIndex, 0, Playing.Tracks.Count - 1);

            string targetPath = Playing.Tracks[Playing.Index];
            bool alreadyLoaded =
                !string.IsNullOrWhiteSpace(targetPath) &&
                !string.IsNullOrWhiteSpace(_player.CurrentFile) &&
                string.Equals(_player.CurrentFile, targetPath, StringComparison.OrdinalIgnoreCase);

            if (!alreadyLoaded)
            {
                ForceLoadCurrentForPlayingPlaylist();

                if (string.IsNullOrEmpty(_player.CurrentFile))
                {
                    EnterPausedState();
                    return false;
                }
            }
            else
            {
                LogTransport("StartPlaybackAtPlayingIndex.SkipReload",
                    $"index={Playing.Index} path=\"{ClipLogValue(targetPath)}\"");
            }

            return EnterPlayingState();
        }

        private void AlignActivePlaylistToPlayingForManualTransport()
        {
            if (string.IsNullOrWhiteSpace(_player.CurrentFile))
                return;

            if (_playingPlaylist < 0 || _playingPlaylist >= _playlists.Count)
                return;

            if (_activePlaylist != _playingPlaylist)
                SelectPlaylist(_playingPlaylist);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            CancelWaveformInteraction();

            // Toggle based on UI intent, not WASAPI PlaybackState (which can be stale)
            _uiWantsPlaying = !_uiWantsPlaying;

            if (!_uiWantsPlaying)
            {
                ClearPendingCrossfadeState();
                _player.Pause(reason: "PlayPauseButton_Click:Pause");
                EnterPausedState();
                return;
            }

            // ---- We want to play ----
            if (Active.Tracks.Count == 0 || Active.Index < 0)
            {
                if (!PickFilesIntoPlaylistReplace())
                {
                    EnterPausedState();
                    return;
                }
            }

            _playingPlaylist = _activePlaylist;

            if (Playing.Index < 0 && Playing.Tracks.Count > 0)
                Playing.Index = 0;

            string? targetPath =
                (Playing.Index >= 0 && Playing.Index < Playing.Tracks.Count)
                    ? Playing.Tracks[Playing.Index]
                    : null;

            bool alreadyLoaded =
                !string.IsNullOrWhiteSpace(targetPath) &&
                !string.IsNullOrWhiteSpace(_player.CurrentFile) &&
                string.Equals(_player.CurrentFile, targetPath, StringComparison.OrdinalIgnoreCase);

            // Only reload if we're not simply resuming the currently loaded paused track.
            if (!alreadyLoaded)
            {
                ForceLoadCurrentForPlayingPlaylist();

                // If load failed (missing file removed), do not attempt to Play()
                if (string.IsNullOrEmpty(_player.CurrentFile))
                {
                    EnterPausedState();
                    return;
                }
            }

            // Apply resume seek once, on the first real Play after startup
            if (_resumePending &&
                !string.IsNullOrWhiteSpace(_resumeFile) &&
                string.Equals(_player.CurrentFile, _resumeFile, StringComparison.OrdinalIgnoreCase) &&
                _resumeSeconds > 0.25)
            {
                _player.Seek(
                    TimeSpan.FromSeconds(_resumeSeconds),
                    resume: false,
                    reason: "PlayPauseButton_Click:ResumePending");
                _resumePending = false;
            }
            else if (_resumePending)
            {
                _resumePending = false;
            }

            _ = EnterPlayingState();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_isShuttingDown)
                return;

            _isShuttingDown = true;

            try
            {
                _uiWantsPlaying = false;

                // Stop UI activity first
                _uiTimer?.Stop();
                _saveTimer?.Stop();
                _spectrumFadeTimer?.Stop();
                _ctrlPollTimer?.Stop();

                // Cancel background work
                _waveCts?.Cancel();
                _metaCts?.Cancel();
                try { _voiceTriggerService.Stop(); } catch { }

                // Save while player still has valid state
                SaveStateNow();

                // Hard stop playback before teardown
                try { _player.Stop(reason: "MainWindow_Closing"); } catch { }

                // Small extra safety: let WASAPI stop settle before dispose
                System.Threading.Thread.Sleep(50);

                try { _player.Dispose(); } catch { }
            }
            catch
            {
                // never crash on shutdown
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            CancelWaveformInteraction();
            ClearPendingCrossfadeState();

            EnterPausedState();
            _player.Stop(reason: "StopButton_Click");

            BeginSpectrumFadeOut();

            WaveformBar.Progress = 0.0;
            RequestSaveState();
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            CancelWaveformInteraction();
            ClearPendingCrossfadeState();

            AlignActivePlaylistToPlayingForManualTransport();

            if (Active.Tracks.Count == 0)
                return;

            if (ShuffleEnabled)
            {
                _pendingTrackChangeSource = "PrevButton";
                if (!TryAdvancePreviousShuffle(wrap: true))
                    _pendingTrackChangeSource = null;

                return;
            }

            _pendingTrackChangeSource = "PrevButton";
            if (!TryAdvanceByView(-1, wrap: true))
                _pendingTrackChangeSource = null;
        }

        private List<int> GetCurrentShuffleSequence()
        {
            var seq = new List<int>();

            // Stack enumerates newest -> oldest, so reverse it to get true play order
            seq.AddRange(Active.ShuffleHistory.Reverse());

            if (Active.Index >= 0 && Active.Index < Active.Tracks.Count)
                seq.Add(Active.Index);

            seq.AddRange(Active.ShuffleBag);

            return seq;
        }

        private void SetShufflePositionFromSequence(List<int> sequence, int newPos)
        {
            Active.ShuffleHistory.Clear();
            Active.ShuffleBag.Clear();

            // Everything before newPos becomes history
            for (int i = 0; i < newPos; i++)
                Active.ShuffleHistory.Push(sequence[i]);

            // Everything after newPos becomes upcoming bag
            for (int i = newPos + 1; i < sequence.Count; i++)
                Active.ShuffleBag.Add(sequence[i]);

            Active.Index = sequence[newPos];
        }

        private bool TryAdvancePreviousShuffle(bool wrap)
        {
            var seq = GetCurrentShuffleSequence();
            if (seq.Count == 0)
                return false;

            int curPos = seq.IndexOf(Active.Index);
            if (curPos < 0)
                return false;

            int prevPos;
            if (curPos > 0)
            {
                prevPos = curPos - 1;
            }
            else
            {
                if (!wrap)
                    return false;

                prevPos = seq.Count - 1;
            }

            SetShufflePositionFromSequence(seq, prevPos);
            return StartPlaybackAtActiveIndex(Active.Index);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            CancelWaveformInteraction();
            ClearPendingCrossfadeState();
            AlignActivePlaylistToPlayingForManualTransport();

            if (Active.Tracks.Count == 0)
                return;

            // Manual Next should override Repeat 1 and actually advance.
            if (ShuffleEnabled)
            {
                if (Playing.ShuffleBag.Count == 0 && Playing.Tracks.Count > 1)
                {
                    RebuildShuffleBagForPlaylist(_playingPlaylist, keepCurrent: true);
                    AvoidImmediateShuffleRepeatForPlaylist(_playingPlaylist);
                }

                var next = GetNextShuffleIndexForPlaylist(_playingPlaylist);
                if (next == null)
                    return;

                Playing.ShuffleHistory.Push(Playing.Index);
                _pendingTrackChangeSource = "NextButton";
                _ = StartPlaybackAtPlayingIndex(next.Value);
                return;
            }

            _pendingTrackChangeSource = "NextButton";
            if (!TryAdvanceByView(+1, wrap: true))
                _pendingTrackChangeSource = null;
        }

        private bool PickFilesIntoPlaylistReplace()
        {
            var ofd = new OpenFileDialog
            {
                Title = "Select audio files",
                Filter = "Audio files|*.mp3;*.m4a;*.ogg;*.wav;*.flac;*.aac;*.wma|All files|*.*",
                Multiselect = true
            };

            if (ofd.ShowDialog(this) != true)
                return false;

            var files = ofd.FileNames.Where(IsSupportedAudioFile).ToList();
            if (files.Count == 0)
                return false;

            Active.Tracks.Clear();
            Active.Tracks.AddRange(files);
            Active.Index = 0;

            ForceLoadCurrent();
            RefreshPlaylistUI();

            if (ShuffleEnabled)
            {
                RebuildShuffleForTargetPlaylist();
            }

            RequestSaveState();
            return true;
        }

        private static bool IsSupportedAudioFile(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            return !string.IsNullOrWhiteSpace(ext) && SupportedExt.Contains(ext);
        }

        private static IEnumerable<string> ExpandDroppedPathsToAudioFiles(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;

                if (File.Exists(p))
                {
                    if (IsSupportedAudioFile(p))
                        yield return p;

                    continue;
                }

                if (Directory.Exists(p))
                {
                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var f in files)
                    {
                        if (IsSupportedAudioFile(f))
                            yield return f;
                    }
                }
            }
        }

        private static bool IsDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            return Directory.Exists(path);
        }

        private void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            if (dep == null)
                return;

            var item = ItemsControl.ContainerFromElement(PlaylistList, dep) as ListBoxItem;
            if (item?.DataContext is not PlaylistRow row)
                return;

            if (row.SourceIndex < 0 || row.SourceIndex >= Active.Tracks.Count)
                return;

            bool samePlayingTrack =
                _playingPlaylist == _activePlaylist &&
                row.SourceIndex == Playing.Index;

            bool canRestartInPlace =
                samePlayingTrack &&
                _player.PlaybackState != NAudio.Wave.PlaybackState.Stopped &&
                !string.IsNullOrWhiteSpace(_player.CurrentFile);

            if (canRestartInPlace)
            {
                CancelWaveformInteraction();
                ClearPendingCrossfadeState();

                bool shouldResume = _player.PlaybackState == NAudio.Wave.PlaybackState.Playing;
                _pendingTrackChangeSource = "ManualRestart";

                _player.Seek(
                    TimeSpan.Zero,
                    resume: shouldResume,
                    reason: "PlaylistList_MouseDoubleClick:ManualRestart");
                _uiWantsPlaying = shouldResume;
                SyncPlayPauseButton();
                WaveformBar.Progress = 0.0;
                RequestSaveState();
                return;
            }

            string path = Active.Tracks[row.SourceIndex];

            if (_sceneEnabled && Active.IsAmbience)
            {
                PlayIntoScene(path);
            }
            else
            {
                PlayIndex(row.SourceIndex);
            }
        }

        private int GetNextFreeSceneLane()
        {
            // Skip lane 0 (music)
            for (int i = 1; i < 4; i++)
            {
                if (string.IsNullOrWhiteSpace(_sceneTracks[i]))
                    return i;
            }

            return -1;
        }

        private void PlayIntoScene(string path)
        {
            int lane = GetNextFreeSceneLane();

            if (lane < 0)
            {
                MessageBox.Show(
                    this,
                    "Too many ambience tracks. Please close one.",
                    "Scene Mode",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            SetSceneTrack(lane, path);
        }

        private void SetSceneTrack(int lane, string path)
        {
            if (lane < 0 || lane >= _sceneTracks.Length || string.IsNullOrWhiteSpace(path))
                return;

            _sceneTracks[lane] = path;
            UpdateSceneLaneUI(lane, path);

            // 🔥 Start ambience playback (looping)
            if (lane >= 1)
            {
                _sceneAudio.PlayLane(
                    SceneUiLaneToEngineLane(lane),
                    path,
                    loop: true,
                    volume: (float)_sceneLaneVolumes[lane]);
            }

            // ✅ sync UI state so button flips to pause
            SetSceneLanePlayingState(lane, true);

            // ✅ refresh the slider visual immediately using the lane's current volume
            SetSceneLaneVolumeVisual(lane, _sceneLaneVolumes[lane]);
        }

        private void UpdateSceneLaneUI(int lane, string path)
        {
            string title = CleanDisplayTitle(path);

            switch (lane)
            {
                case 0:
                    SceneText1.Text = title;
                    break;
                case 1:
                    SceneText2.Text = title;
                    break;
                case 2:
                    SceneText3.Text = title;
                    break;
                case 3:
                    SceneText4.Text = title;
                    break;
            }
        }

        private void PlayIndex(int newIndex)
        {
            if (Active.Tracks.Count == 0)
                return;

            _pendingTrackChangeSource = "ManualPlay";

            _playingPlaylist = _activePlaylist;

            newIndex = Math.Clamp(newIndex, 0, Playing.Tracks.Count - 1);

            LogTransport("PlayIndex", $"newIndex={newIndex}");

            if (ShuffleEnabled)
            {
                RebuildShuffleBagForPlaylist(_playingPlaylist, keepCurrent: true);
                AvoidImmediateShuffleRepeatForPlaylist(_playingPlaylist);
            }

            _ = StartPlaybackAtPlayingIndex(newIndex);
        }

        private bool TryAdvanceByView(int delta, bool wrap = false)
        {
            int? nextIdx = GetAdjacentIndexByView(delta, wrap);
            if (nextIdx == null)
                return false;

            return StartPlaybackAtActiveIndex(nextIdx.Value);
        }
    }
}