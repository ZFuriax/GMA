using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.Windows.Documents;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        // ---------- Voice triggers ----------
        private readonly VoiceTriggerService _voiceTriggerService = new();
        private DateTime _lastCtrlDownUtc = DateTime.MinValue;
        private DateTime _lastWhisperArmedUtc = DateTime.MinValue;
        private bool _ctrlSegmentActive;
        private const int CtrlPhraseGraceMs = 4000;

        private enum VoiceCaptureMode
        {
            Off = 0,
            On = 1,
            CtrlActivated = 2
        }

        private VoiceCaptureMode _voiceCaptureMode = VoiceCaptureMode.Off;

        private const int DefaultVoiceTriggerCooldownMs = 3000;
        private const string GlyphMic = "\uE720";

        private readonly Dictionary<string, int> _voiceCanonicalToPlaylist =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, int> _voiceCanonicalToScene =
            new(StringComparer.OrdinalIgnoreCase);

        private static string NormalizeLoadedAliasGroup(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();

            foreach (var raw in text.Split(';'))
            {
                string normalized = VoiceTriggerService.NormalizePhrase(raw);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (seen.Add(normalized))
                    parts.Add(normalized);
            }

            return string.Join("; ", parts);
        }

        private static string BuildAliasGroupFromCapturedPhrase(string phrase)
        {
            return VoiceTriggerService.NormalizePhrase(phrase);
        }

        private async Task<string?> CapturePhraseFromMicAsync()
        {
            bool shouldResumeVoiceCapture = _voiceCaptureMode != VoiceCaptureMode.Off;

            try
            {
                try { _voiceTriggerService.Stop(); } catch { }

                var installed = SpeechRecognitionEngine.InstalledRecognizers().ToList();
                if (installed.Count == 0)
                    return null;

                var recognizerInfo =
                    installed.FirstOrDefault(r => string.Equals(r.Culture.Name, CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
                    ?? installed.FirstOrDefault(r => string.Equals(r.Culture.TwoLetterISOLanguageName, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                    ?? installed.FirstOrDefault(r => string.Equals(r.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
                    ?? installed[0];

                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

                using var recognizer = new SpeechRecognitionEngine(recognizerInfo);

                recognizer.LoadGrammar(new DictationGrammar());
                recognizer.SetInputToDefaultAudioDevice();

                recognizer.InitialSilenceTimeout = TimeSpan.FromSeconds(4);
                recognizer.BabbleTimeout = TimeSpan.FromSeconds(4);
                recognizer.EndSilenceTimeout = TimeSpan.FromMilliseconds(1200);
                recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(1500);

                string? bestText = null;
                float bestConfidence = 0f;

                recognizer.SpeechRecognized += (_, e) =>
                {
                    if (e.Result == null)
                        return;

                    string text = e.Result.Text?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    if (text.Length <= 2 && e.Result.Confidence < 0.75f)
                        return;

                    if (e.Result.Confidence >= bestConfidence)
                    {
                        bestConfidence = e.Result.Confidence;
                        bestText = text;
                    }
                };

                recognizer.RecognizeCompleted += (_, __) =>
                {
                    tcs.TrySetResult(bestText);
                };

                recognizer.RecognizeAsync(RecognizeMode.Single);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(8000));

                try { recognizer.RecognizeAsyncCancel(); } catch { }
                try { recognizer.RecognizeAsyncStop(); } catch { }

                if (completed == tcs.Task)
                    return tcs.Task.Result;

                return bestText;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (shouldResumeVoiceCapture)
                    RefreshVoiceCaptureState(showErrors: false);
                else
                    UpdateVoiceCaptureButtonVisuals();
            }
        }

        private void VoiceCaptureToggle_Click(object sender, RoutedEventArgs e)
        {
            CycleVoiceCaptureMode();
        }

        private void CycleVoiceCaptureMode()
        {
            _voiceCaptureMode = _voiceCaptureMode switch
            {
                //VoiceCaptureMode.Off => VoiceCaptureMode.On,
                //VoiceCaptureMode.On => VoiceCaptureMode.CtrlActivated,
                //_ => VoiceCaptureMode.Off

                VoiceCaptureMode.Off => VoiceCaptureMode.CtrlActivated,
                _ => VoiceCaptureMode.Off

            };

            ApplyVoiceCaptureMode();
        }

        private void ApplyVoiceCaptureMode()
        {
            PreviewKeyDown -= VoiceCaptureCtrl_PreviewKeyDown;
            PreviewKeyUp -= VoiceCaptureCtrl_PreviewKeyUp;
            Deactivated -= VoiceCaptureCtrl_WindowDeactivated;

            if (_voiceCaptureMode == VoiceCaptureMode.Off)
            {
                try { _voiceTriggerService.Stop(); } catch { }
                _ctrlSegmentActive = false;
            }
            else
            {
                if (_voiceCaptureMode == VoiceCaptureMode.CtrlActivated)
                {
                    _lastCtrlDownUtc = DateTime.MinValue;
                    _lastWhisperArmedUtc = DateTime.MinValue;
                    _ctrlSegmentActive = false;

                    PreviewKeyDown += VoiceCaptureCtrl_PreviewKeyDown;
                    PreviewKeyUp += VoiceCaptureCtrl_PreviewKeyUp;
                    Deactivated += VoiceCaptureCtrl_WindowDeactivated;
                }

                RefreshVoiceCaptureState(showErrors: true);
            }

            UpdateVoiceCaptureButtonVisuals();
            RequestSaveState();
        }

        private static bool IsCtrlKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl;
        }

        private void VoiceCaptureCtrl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_voiceCaptureMode != VoiceCaptureMode.CtrlActivated)
                return;

            if (!IsCtrlKey(e.Key))
                return;

            _lastCtrlDownUtc = DateTime.UtcNow;

            if (_ctrlSegmentActive || e.IsRepeat)
                return;

            _voiceTriggerService.BeginManualSegment();
            _lastWhisperArmedUtc = DateTime.UtcNow;
            _ctrlSegmentActive = true;
        }

        private void VoiceCaptureCtrl_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (_voiceCaptureMode != VoiceCaptureMode.CtrlActivated)
                return;

            if (!IsCtrlKey(e.Key))
                return;

            EndCtrlActivatedManualSegment();
        }

        private void VoiceCaptureCtrl_WindowDeactivated(object? sender, EventArgs e)
        {
            if (_voiceCaptureMode != VoiceCaptureMode.CtrlActivated)
                return;

            EndCtrlActivatedManualSegment();
        }

        private void EndCtrlActivatedManualSegment()
        {
            if (!_ctrlSegmentActive)
                return;

            bool exported = _voiceTriggerService.EndManualSegmentAndExport();
            if (exported)
                _lastWhisperArmedUtc = DateTime.UtcNow;

            _ctrlSegmentActive = false;
        }

        private void UpdateVoiceCaptureButtonVisuals()
        {
            if (VoiceCaptureToggle == null)
                return;

            bool isActive = _voiceCaptureMode != VoiceCaptureMode.Off;

            if (VoiceCaptureToggle.IsChecked != isActive)
                VoiceCaptureToggle.IsChecked = isActive;

            VoiceCaptureToggle.FontFamily = new FontFamily("Segoe MDL2 Assets");
            VoiceCaptureToggle.FontSize = 14;
            VoiceCaptureToggle.FontWeight = FontWeights.Normal;

            switch (_voiceCaptureMode)
            {
                case VoiceCaptureMode.Off:
                    VoiceCaptureToggle.Content = GlyphMic;
                    VoiceCaptureToggle.ToolTip = new TextBlock
                    {
                        TextAlignment = TextAlignment.Left,
                        TextWrapping = TextWrapping.Wrap,
                        Inlines =
                        {
                            new Run("Voice Capture (Off)") { FontWeight = FontWeights.Bold },
                            new LineBreak(),
                            new Run("Voice capture allows for voice activation of a playlist."),
                            new LineBreak(),
                            new Run("Example: \"Roll for Initiative\" can be set to start your battle playlist. Requires a key"),
                            new LineBreak(),
                            new Run("phrase to be set up for the given playlist by clicking that playlist tab's microphone button."),
                            new LineBreak(),
                            new Run("Modes: Off (disabled), Ctrl (active while Ctrl is held).")
                        }
                    };
                    SetGreenHighlight(VoiceCaptureToggle, false);
                    break;

                case VoiceCaptureMode.On:
                    VoiceCaptureToggle.Content = GlyphMic;
                    VoiceCaptureToggle.ToolTip = new TextBlock
                    {
                        TextAlignment = TextAlignment.Left,
                        TextWrapping = TextWrapping.Wrap,
                        Inlines =
                        {
                            new Run("Voice Capture (Off)") { FontWeight = FontWeights.Bold },
                            new LineBreak(),
                            new Run("Voice capture allows for voice activation of a playlist."),
                            new LineBreak(),
                            new Run("Example: \"Roll for Initiative\" can be set to start your battle playlist. Requires a key"),
                            new LineBreak(),
                            new Run("phrase to be set up for the given playlist by clicking that playlist tab's microphone button."),
                            new LineBreak(),
                            new Run("Modes: Off (disabled), Ctrl (active while Ctrl is held).")
                        }
                    };
                    SetGreenHighlight(VoiceCaptureToggle, true);
                    break;

                case VoiceCaptureMode.CtrlActivated:
                    VoiceCaptureToggle.FontFamily = new FontFamily("Segoe UI");
                    VoiceCaptureToggle.FontSize = 11;
                    VoiceCaptureToggle.FontWeight = FontWeights.SemiBold;
                    VoiceCaptureToggle.Content = "Ctrl";
                    VoiceCaptureToggle.ToolTip = new TextBlock
                    {
                        TextAlignment = TextAlignment.Left,
                        TextWrapping = TextWrapping.Wrap,
                        Inlines =
                        {
                            new Run("Voice Capture (Off)") { FontWeight = FontWeights.Bold },
                            new LineBreak(),
                            new Run("Voice capture allows for voice activation of a playlist."),
                            new LineBreak(),
                            new Run("Example: \"Roll for Initiative\" can be set to start your battle playlist. Requires a key"),
                            new LineBreak(),
                            new Run("phrase to be set up for the given playlist by clicking that playlist tab's microphone button."),
                            new LineBreak(),
                            new Run("Modes: Off (disabled), Ctrl (active while Ctrl is held).")
                        }
                    };
                    SetGreenHighlight(VoiceCaptureToggle, true);
                    break;
            }
        }

        private void RefreshVoiceCaptureState(bool showErrors)
        {
            EnsureWhisperDefaults();

            _voiceCanonicalToPlaylist.Clear();
            _voiceCanonicalToScene.Clear();

            var phraseGroups = new List<VoicePhraseGroup>();

            foreach (var entry in _playlists.Select((pl, i) => new { Playlist = pl, Index = i }))
            {
                var pl = entry.Playlist;

                if (!PlaylistHasAnyVoicePhrases(pl))
                    continue;

                if (pl.Tracks.Count == 0)
                    continue;

                foreach (var group in EnumerateVoicePhraseGroups(pl))
                {
                    if (_voiceCanonicalToPlaylist.ContainsKey(group.CanonicalPhrase))
                        continue;

                    _voiceCanonicalToPlaylist[group.CanonicalPhrase] = entry.Index;
                    phraseGroups.Add(group);
                }
            }

            foreach (var entry in _scenes.Select((sc, i) => new { Scene = sc, Index = i }))
            {
                string normalized = VoiceTriggerService.NormalizePhrase(entry.Scene.KeyPhrase);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (_voiceCanonicalToPlaylist.ContainsKey(normalized))
                    continue;

                if (_voiceCanonicalToScene.ContainsKey(normalized))
                    continue;

                _voiceCanonicalToScene[normalized] = entry.Index;
                phraseGroups.Add(new VoicePhraseGroup(normalized, new[] { normalized }));
            }

            try { _voiceTriggerService.Stop(); } catch { }

            if (_voiceCaptureMode == VoiceCaptureMode.Off)
            {
                UpdateVoiceCaptureButtonVisuals();
                return;
            }

            if (phraseGroups.Count == 0)
            {
                UpdateVoiceCaptureButtonVisuals();
                return;
            }

            _voiceTriggerService.CaptureOnlyMode =
                _voiceCaptureMode == VoiceCaptureMode.CtrlActivated;

            _voiceTriggerService.ResetConfidenceThresholdsToDefaults();

            if (_voiceCaptureMode == VoiceCaptureMode.CtrlActivated)
            {
                _voiceTriggerService.MinOverallConfidence = 0.62f;
                _voiceTriggerService.MinAcceptanceScore = 0.72f;
                _voiceTriggerService.MinBestPhraseSimilarity = 0.68;
            }

            if (!_voiceTriggerService.TryStart(phraseGroups, out var error))
            {
                _voiceCaptureMode = VoiceCaptureMode.Off;
                UpdateVoiceCaptureButtonVisuals();
                RequestSaveState();

                if (showErrors && !string.IsNullOrWhiteSpace(error))
                {
                    MessageBox.Show(
                        this,
                        "Unable to start voice capture.\n\n" + error,
                        "Voice capture",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return;
            }

            UpdateVoiceCaptureButtonVisuals();
        }

        private static IEnumerable<string> EnumerateNormalizedVoiceTriggerPhrases(PlaylistState playlist)
        {
            if (playlist == null)
                yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] groups =
            [
                playlist.VoiceTriggerPhrase1 ?? "",
                playlist.VoiceTriggerPhrase2 ?? "",
                playlist.VoiceTriggerPhrase3 ?? ""
            ];

            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group))
                    continue;

                foreach (var part in group.Split(';'))
                {
                    string normalized = VoiceTriggerService.NormalizePhrase(part);
                    if (string.IsNullOrWhiteSpace(normalized))
                        continue;

                    if (seen.Add(normalized))
                        yield return normalized;
                }
            }
        }

        private void VoiceTriggerService_PhraseRecognized(string normalizedPhrase)
        {
            bool ctrlRecentlyDown =
                IsCtrlDownNow() ||
                (DateTime.UtcNow - _lastCtrlDownUtc).TotalMilliseconds <= CtrlPhraseGraceMs ||
                (DateTime.UtcNow - _lastWhisperArmedUtc).TotalMilliseconds <= CtrlPhraseGraceMs;

            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                HandleVoicePhraseRecognized(normalizedPhrase, ctrlRecentlyDown);
            }));
        }

        private void HandleVoicePhraseRecognized(string normalizedPhrase, bool ctrlWasDownAtRecognition)
        {
            if (_isShuttingDown || _voiceCaptureMode == VoiceCaptureMode.Off)
                return;

            if (_voiceCaptureMode == VoiceCaptureMode.CtrlActivated && !ctrlWasDownAtRecognition)
                return;

            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                return;

            if (_voiceCanonicalToPlaylist.TryGetValue(normalizedPhrase, out int playlistIndex))
            {
                if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                    return;

                var playlist = _playlists[playlistIndex];
                if (!PlaylistHasAnyVoicePhrases(playlist) || playlist.Tracks.Count == 0)
                    return;

                int cooldownMs = playlist.VoiceTriggerCooldownMs > 0
                    ? playlist.VoiceTriggerCooldownMs
                    : DefaultVoiceTriggerCooldownMs;

                var now = DateTime.UtcNow;
                if ((now - playlist.VoiceTriggerLastFireUtc).TotalMilliseconds < cooldownMs)
                    return;

                playlist.VoiceTriggerLastFireUtc = now;
                PlayVoiceTriggeredTrack(playlistIndex);
                return;
            }

            if (_voiceCanonicalToScene.TryGetValue(normalizedPhrase, out int sceneIndex))
            {
                if (sceneIndex < 0 || sceneIndex >= _scenes.Count)
                    return;

                PlayScene(sceneIndex);
            }
        }

        private void PlayVoiceTriggeredTrack(int playlistIndex)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return;

            var playlist = _playlists[playlistIndex];
            if (playlist.Tracks.Count == 0)
                return;

            int nextIndex = playlist.VoiceTriggerLastIndex + 1;
            if (nextIndex < 0 || nextIndex >= playlist.Tracks.Count)
                nextIndex = 0;

            playlist.VoiceTriggerLastIndex = nextIndex;

            CancelWaveformInteraction();

            _crossfadeArmed = false;
            _abCrossfadeArmed = false;
            _lastHandledEndedPath = null;

            _resumePending = false;
            _resumeFile = null;
            _resumeSeconds = 0.0;

            SelectPlaylist(playlistIndex);

            SetPlayingPlaylist(playlistIndex);
            Playing.Index = nextIndex;

            if (_activePlaylist == _playingPlaylist)
                SyncPlaylistSelection();

            _player.Stop();
            ForceLoadCurrentForPlayingPlaylist();

            if (string.IsNullOrWhiteSpace(_player.CurrentFile))
            {
                _uiWantsPlaying = false;
                PlayPauseButton.Content = GlyphPlay;
                RequestSaveState();
                return;
            }

            try
            {
                _player.Play();
                _uiWantsPlaying = true;
                PlayPauseButton.Content = GlyphPause;
                RequestSaveState();
            }
            catch
            {
                _uiWantsPlaying = false;
                PlayPauseButton.Content = GlyphPlay;
            }
        }

        private static RecognizerInfo? SelectInstalledDictationRecognizer()
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers().ToList();
            if (installed.Count == 0)
                return null;

            var currentUi = CultureInfo.CurrentUICulture;

            return installed.FirstOrDefault(r =>
                       string.Equals(r.Culture.Name, currentUi.Name, StringComparison.OrdinalIgnoreCase))
                   ?? installed.FirstOrDefault(r =>
                       string.Equals(r.Culture.TwoLetterISOLanguageName, currentUi.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                   ?? installed.FirstOrDefault(r =>
                       string.Equals(r.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
                   ?? installed[0];
        }

        private sealed class VoicePhraseTestResult
        {
            public string HeardText { get; set; } = "";
            public string MatchedAlias { get; set; } = "";
            public string BestAliasBySimilarity { get; set; } = "";
            public double BestAliasSimilarityScore { get; set; }
            public float OverallConfidence { get; set; }
            public float FinalWordConfidence { get; set; } = -1f;
            public bool OverallPass { get; set; }
            public bool EveryWordPass { get; set; }
            public bool FinalWordPass { get; set; }
            public bool Accepted { get; set; }
            public List<(string Word, float Confidence, bool Pass)> Words { get; } = new();
        }

        private async Task<VoicePhraseTestResult?> TestPhraseGroupAsync(string aliasGroup)
        {
            bool shouldResumeVoiceCapture = _voiceCaptureMode != VoiceCaptureMode.Off;

            try
            {
                try { _voiceTriggerService.Stop(); } catch { }

                var aliases = aliasGroup
                    .Split(';')
                    .Select(VoiceTriggerService.NormalizePhrase)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (aliases.Count == 0)
                    return null;

                var installed = SpeechRecognitionEngine.InstalledRecognizers().ToList();
                if (installed.Count == 0)
                    return null;

                var recognizerInfo =
                    installed.FirstOrDefault(r => string.Equals(r.Culture.Name, CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
                    ?? installed.FirstOrDefault(r => string.Equals(r.Culture.TwoLetterISOLanguageName, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                    ?? installed.FirstOrDefault(r => string.Equals(r.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
                    ?? installed[0];

                VoicePhraseTestResult? grammarResult = null;

                {
                    var tcs = new TaskCompletionSource<VoicePhraseTestResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

                    using var recognizer = new SpeechRecognitionEngine(recognizerInfo);

                    foreach (var alias in aliases)
                    {
                        var gb = new GrammarBuilder { Culture = recognizerInfo.Culture };
                        gb.Append(alias);

                        var grammar = new Grammar(gb)
                        {
                            Name = alias
                        };

                        recognizer.LoadGrammar(grammar);
                    }

                    recognizer.SetInputToDefaultAudioDevice();
                    recognizer.InitialSilenceTimeout = TimeSpan.FromSeconds(2);
                    recognizer.BabbleTimeout = TimeSpan.FromSeconds(2);
                    recognizer.EndSilenceTimeout = TimeSpan.FromMilliseconds(450);
                    recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(600);

                    recognizer.SpeechRecognized += (_, e) =>
                    {
                        if (e?.Result == null)
                            return;

                        string heard = VoiceTriggerService.NormalizePhrase(e.Result.Text);
                        float overall = e.Result.Confidence;

                        float finalWordConfidence = -1f;
                        bool everyWordPass = true;

                        var bestAlias = FindBestAliasBySimilarity(heard, aliases);

                        var result = new VoicePhraseTestResult
                        {
                            HeardText = heard,
                            MatchedAlias = e.Result.Grammar?.Name ?? heard,
                            BestAliasBySimilarity = bestAlias.Alias,
                            BestAliasSimilarityScore = bestAlias.Score,
                            OverallConfidence = overall,
                            OverallPass = overall >= _voiceTriggerService.MinOverallConfidence
                        };

                        if (e.Result.Words != null && e.Result.Words.Count > 0)
                        {
                            foreach (RecognizedWordUnit word in e.Result.Words)
                            {
                                bool pass = word.Confidence >= _voiceTriggerService.MinWordConfidence;
                                if (!pass)
                                    everyWordPass = false;

                                result.Words.Add((word.Text, word.Confidence, pass));
                            }

                            finalWordConfidence = e.Result.Words[e.Result.Words.Count - 1].Confidence;
                        }

                        result.FinalWordConfidence = finalWordConfidence;
                        result.EveryWordPass = everyWordPass;
                        result.FinalWordPass = finalWordConfidence < 0f ||
                                               finalWordConfidence >= _voiceTriggerService.MinFinalWordConfidence;
                        result.Accepted =
                            !string.IsNullOrWhiteSpace(result.HeardText) &&
                            result.OverallPass &&
                            result.EveryWordPass &&
                            result.FinalWordPass;

                        tcs.TrySetResult(result);
                    };

                    recognizer.RecognizeCompleted += (_, __) =>
                    {
                        tcs.TrySetResult(null);
                    };

                    recognizer.RecognizeAsync(RecognizeMode.Single);

                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(4000));

                    try { recognizer.RecognizeAsyncCancel(); } catch { }
                    try { recognizer.RecognizeAsyncStop(); } catch { }
                    try { recognizer.SetInputToNull(); } catch { }

                    if (completed == tcs.Task)
                        grammarResult = tcs.Task.Result;
                }

                if (grammarResult != null)
                    return grammarResult;

                string? fallbackHeard = await CaptureDictationForTestAsync();
                if (string.IsNullOrWhiteSpace(fallbackHeard))
                    return null;

                var fallbackBestAlias = FindBestAliasBySimilarity(fallbackHeard, aliases);

                return new VoicePhraseTestResult
                {
                    HeardText = VoiceTriggerService.NormalizePhrase(fallbackHeard),
                    MatchedAlias = "(no alias matched)",
                    BestAliasBySimilarity = fallbackBestAlias.Alias,
                    BestAliasSimilarityScore = fallbackBestAlias.Score,
                    OverallConfidence = 0f,
                    FinalWordConfidence = -1f,
                    OverallPass = false,
                    EveryWordPass = false,
                    FinalWordPass = false,
                    Accepted = false
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                if (shouldResumeVoiceCapture)
                    RefreshVoiceCaptureState(showErrors: false);
                else
                    UpdateVoiceCaptureButtonVisuals();
            }
        }

        private string FormatVoicePhraseTestResult(VoicePhraseTestResult? result)
        {
            if (result == null)
                return "No speech was recognized.";

            var sb = new StringBuilder();

            sb.AppendLine($"Heard: {result.HeardText}");
            sb.AppendLine($"Grammar match: {result.MatchedAlias}");

            if (!string.IsNullOrWhiteSpace(result.BestAliasBySimilarity))
                sb.AppendLine($"Closest alias by similarity: {result.BestAliasBySimilarity} ({result.BestAliasSimilarityScore:P0})");

            sb.AppendLine();

            sb.AppendLine($"Overall confidence: {result.OverallConfidence:P0}   {(result.OverallPass ? "PASS" : "FAIL")}   (min {_voiceTriggerService.MinOverallConfidence:P0})");

            if (result.FinalWordConfidence >= 0f)
            {
                sb.AppendLine($"Final word confidence: {result.FinalWordConfidence:P0}   {(result.FinalWordPass ? "PASS" : "FAIL")}   (min {_voiceTriggerService.MinFinalWordConfidence:P0})");
            }
            else
            {
                sb.AppendLine("Final word confidence: n/a");
            }

            sb.AppendLine($"Every word passes: {(result.EveryWordPass ? "PASS" : "FAIL")}   (min {_voiceTriggerService.MinWordConfidence:P0} per word)");
            sb.AppendLine();
            sb.AppendLine($"Trigger would activate: {(result.Accepted ? "YES" : "NO")}");

            if (result.Words.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Word confidences:");

                foreach (var word in result.Words)
                {
                    sb.AppendLine($"- {word.Word}: {word.Confidence:P0}   {(word.Pass ? "PASS" : "FAIL")}");
                }
            }

            return sb.ToString();
        }

        private static int LevenshteinDistance(string a, string b)
        {
            a ??= string.Empty;
            b ??= string.Empty;

            int[,] dp = new int[a.Length + 1, b.Length + 1];

            for (int i = 0; i <= a.Length; i++)
                dp[i, 0] = i;

            for (int j = 0; j <= b.Length; j++)
                dp[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;

                    dp[i, j] = Math.Min(
                        Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + cost);
                }
            }

            return dp[a.Length, b.Length];
        }

        private static double SimilarityScore(string a, string b)
        {
            a = VoiceTriggerService.NormalizePhrase(a);
            b = VoiceTriggerService.NormalizePhrase(b);

            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
                return 1.0;

            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return 0.0;

            int distance = LevenshteinDistance(a, b);
            int maxLen = Math.Max(a.Length, b.Length);

            if (maxLen == 0)
                return 1.0;

            return 1.0 - (double)distance / maxLen;
        }

        private static (string Alias, double Score) FindBestAliasBySimilarity(string heardText, IEnumerable<string> aliases)
        {
            string normalizedHeard = VoiceTriggerService.NormalizePhrase(heardText);
            string bestAlias = "";
            double bestScore = double.NegativeInfinity;

            foreach (var alias in aliases)
            {
                string normalizedAlias = VoiceTriggerService.NormalizePhrase(alias);
                if (string.IsNullOrWhiteSpace(normalizedAlias))
                    continue;

                double score = SimilarityScore(normalizedHeard, normalizedAlias);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAlias = normalizedAlias;
                }
            }

            if (double.IsNegativeInfinity(bestScore))
                bestScore = 0.0;

            return (bestAlias, bestScore);
        }

        private async Task<string?> CaptureDictationForTestAsync()
        {
            bool shouldResumeVoiceCapture = _voiceCaptureMode != VoiceCaptureMode.Off;

            try
            {
                try { _voiceTriggerService.Stop(); } catch { }

                var installed = SpeechRecognitionEngine.InstalledRecognizers().ToList();
                if (installed.Count == 0)
                    return null;

                var recognizerInfo =
                    installed.FirstOrDefault(r => string.Equals(r.Culture.Name, CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
                    ?? installed.FirstOrDefault(r => string.Equals(r.Culture.TwoLetterISOLanguageName, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                    ?? installed.FirstOrDefault(r => string.Equals(r.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
                    ?? installed[0];

                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

                using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
                recognizer.LoadGrammar(new DictationGrammar());
                recognizer.SetInputToDefaultAudioDevice();

                recognizer.InitialSilenceTimeout = TimeSpan.FromSeconds(2);
                recognizer.BabbleTimeout = TimeSpan.FromSeconds(2);
                recognizer.EndSilenceTimeout = TimeSpan.FromMilliseconds(500);
                recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(600);

                string? bestText = null;
                float bestConfidence = 0f;

                recognizer.SpeechRecognized += (_, e) =>
                {
                    if (e.Result == null)
                        return;

                    string text = e.Result.Text?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    if (e.Result.Confidence >= bestConfidence)
                    {
                        bestConfidence = e.Result.Confidence;
                        bestText = text;
                    }
                };

                recognizer.RecognizeCompleted += (_, __) =>
                {
                    tcs.TrySetResult(bestText);
                };

                recognizer.RecognizeAsync(RecognizeMode.Single);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(8000));

                try { recognizer.RecognizeAsyncCancel(); } catch { }
                try { recognizer.RecognizeAsyncStop(); } catch { }

                if (completed == tcs.Task)
                    return tcs.Task.Result;

                return bestText;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (shouldResumeVoiceCapture)
                    RefreshVoiceCaptureState(showErrors: false);
                else
                    UpdateVoiceCaptureButtonVisuals();
            }
        }

        private void BeginSetVoiceConfidenceThresholds()
        {
            var dlg = new Window
            {
                Title = "Set Confidence Thresholds",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 420,
                Height = 210,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(17, 17, 17)),
                Foreground = Brushes.White
            };

            Slider MakeSlider(float value)
            {
                return new Slider
                {
                    Minimum = 0.00,
                    Maximum = 1.00,
                    Value = value,
                    TickFrequency = 0.01,
                    IsSnapToTickEnabled = false,
                    Margin = new Thickness(10, 0, 10, 0)
                };
            }

            TextBlock MakeValueLabel(Slider slider)
            {
                var tb = new TextBlock
                {
                    Width = 50,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = slider.Value.ToString("P0")
                };

                slider.ValueChanged += (_, __) =>
                {
                    tb.Text = slider.Value.ToString("P0");
                };

                return tb;
            }

            FrameworkElement MakeRow(string label, Slider slider)
            {
                var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var lbl = new TextBlock
                {
                    Text = label,
                    Margin = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var val = MakeValueLabel(slider);

                Grid.SetColumn(lbl, 0);
                Grid.SetColumn(slider, 1);
                Grid.SetColumn(val, 2);

                row.Children.Add(lbl);
                row.Children.Add(slider);
                row.Children.Add(val);

                return row;
            }

            var overallSlider = MakeSlider(_voiceTriggerService.MinOverallConfidence);
            var wordSlider = MakeSlider(_voiceTriggerService.MinWordConfidence);
            var finalWordSlider = MakeSlider(_voiceTriggerService.MinFinalWordConfidence);

            var reset = new Button
            {
                Content = "Reset to Defaults",
                Margin = new Thickness(10),
                MinWidth = 120
            };

            var ok = new Button
            {
                Content = "OK",
                Margin = new Thickness(10),
                MinWidth = 80,
                IsDefault = true
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Margin = new Thickness(10),
                MinWidth = 80,
                IsCancel = true
            };

            reset.Click += (_, __) =>
            {
                overallSlider.Value = VoiceTriggerService.DefaultMinOverallConfidence;
                wordSlider.Value = VoiceTriggerService.DefaultMinWordConfidence;
                finalWordSlider.Value = VoiceTriggerService.DefaultMinFinalWordConfidence;
            };

            ok.Click += (_, __) => dlg.DialogResult = true;
            cancel.Click += (_, __) => dlg.DialogResult = false;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(reset);
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = "These thresholds apply globally to voice trigger recognition.",
                Margin = new Thickness(10, 10, 10, 10),
                TextWrapping = TextWrapping.Wrap
            });

            content.Children.Add(MakeRow("Overall Confidence", overallSlider));
            content.Children.Add(MakeRow("Per-word Confidence", wordSlider));
            content.Children.Add(MakeRow("Final-word Confidence", finalWordSlider));

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(content);

            dlg.Content = root;

            if (dlg.ShowDialog() != true)
                return;

            _voiceTriggerService.MinOverallConfidence = (float)overallSlider.Value;
            _voiceTriggerService.MinWordConfidence = (float)wordSlider.Value;
            _voiceTriggerService.MinFinalWordConfidence = (float)finalWordSlider.Value;

            RefreshVoiceCaptureState(showErrors: _voiceCaptureMode != VoiceCaptureMode.Off);
            RequestSaveState();
        }

        private void BeginSetVoiceTriggerPhrases(int playlistIndex)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return;

            var playlist = _playlists[playlistIndex];

            var dlg = new Window
            {
                Title = "Set Key Phrases",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 460,
                Height = 250,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(17, 17, 17)),
                Foreground = Brushes.White
            };

            var tb1 = new TextBox
            {
                Margin = new Thickness(10, 0, 0, 6),
                Text = playlist.VoiceTriggerPhrase1 ?? ""
            };

            var tb2 = new TextBox
            {
                Margin = new Thickness(10, 0, 0, 6),
                Text = playlist.VoiceTriggerPhrase2 ?? ""
            };

            var tb3 = new TextBox
            {
                Margin = new Thickness(10, 0, 0, 6),
                Text = playlist.VoiceTriggerPhrase3 ?? ""
            };

            var label1 = new TextBlock
            {
                Text = "Phrase Group 1",
                Margin = new Thickness(10, 8, 10, 2)
            };

            var label2 = new TextBlock
            {
                Text = "Phrase Group 2",
                Margin = new Thickness(10, 0, 10, 2)
            };

            var label3 = new TextBlock
            {
                Text = "Phrase Group 3",
                Margin = new Thickness(10, 0, 10, 2)
            };

            var ok = new Button
            {
                Content = "OK",
                Margin = new Thickness(10),
                MinWidth = 80,
                IsDefault = true
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Margin = new Thickness(10),
                MinWidth = 80
            };

            var clear = new Button
            {
                Content = "Clear Key Phrases",
                Margin = new Thickness(10),
                MinWidth = 130
            };

            ok.Click += (_, __) => dlg.DialogResult = true;
            cancel.Click += (_, __) => dlg.DialogResult = false;
            clear.Click += (_, __) =>
            {
                tb1.Text = string.Empty;
                tb2.Text = string.Empty;
                tb3.Text = string.Empty;
            };

            tb1.KeyDown += (_, e) => { if (e.Key == Key.Enter) { dlg.DialogResult = true; e.Handled = true; } };
            tb2.KeyDown += (_, e) => { if (e.Key == Key.Enter) { dlg.DialogResult = true; e.Handled = true; } };
            tb3.KeyDown += (_, e) => { if (e.Key == Key.Enter) { dlg.DialogResult = true; e.Handled = true; } };

            Grid MakePhraseRow(TextBox tb)
            {
                var grid = new Grid
                {
                    Margin = new Thickness(0, 0, 10, 0)
                };

                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var micButton = new Button
                {
                    Content = GlyphMic,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    Width = 30,
                    Height = 26,
                    Margin = new Thickness(6, tb.Margin.Top, 6, tb.Margin.Bottom),
                    ToolTip = "Record Key Phrase"
                };

                var testButton = new Button
                {
                    Content = "\uEFA9",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    Width = 30,
                    Height = 26,
                    Margin = new Thickness(0, tb.Margin.Top, 10, tb.Margin.Bottom),
                    ToolTip = "Test Key Phrase"
                };

                micButton.Click += async (_, __) =>
                {
                    micButton.IsHitTestVisible = false;

                    var oldContent = micButton.Content;
                    var oldToolTip = micButton.ToolTip;
                    var oldBackground = micButton.Background;

                    micButton.Content = GlyphMic;
                    micButton.Background = new SolidColorBrush(Color.FromRgb(220, 60, 60));
                    micButton.ToolTip = "Listening...";

                    try
                    {
                        var phrase = await CapturePhraseFromMicAsync();

                        if (!string.IsNullOrWhiteSpace(phrase))
                        {
                            string newGroup = BuildAliasGroupFromCapturedPhrase(phrase);

                            if (string.IsNullOrWhiteSpace(tb.Text))
                            {
                                tb.Text = newGroup;
                            }
                            else
                            {
                                string combined = $"{tb.Text}; {newGroup}";
                                tb.Text = NormalizeAliasGroupForDisplay(combined);
                            }
                        }
                    }
                    finally
                    {
                        micButton.Content = oldContent;
                        micButton.Background = oldBackground;
                        micButton.ToolTip = oldToolTip;
                        micButton.IsHitTestVisible = true;
                    }
                };

                testButton.Click += async (_, __) =>
                {
                    testButton.IsHitTestVisible = false;

                    var oldBackground = testButton.Background;
                    var oldForeground = testButton.Foreground;
                    var oldToolTip = testButton.ToolTip;

                    testButton.Background = new SolidColorBrush(Color.FromRgb(220, 180, 60));
                    testButton.Foreground = Brushes.Black;
                    testButton.ToolTip = "Listening for test...";

                    try
                    {
                        string group = NormalizeLoadedAliasGroup(tb.Text);
                        if (string.IsNullOrWhiteSpace(group))
                        {
                            MessageBox.Show(
                                this,
                                "This phrase group is empty.",
                                "Test phrase group",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                            return;
                        }

                        var result = await TestPhraseGroupAsync(group);

                        MessageBox.Show(
                            this,
                            FormatVoicePhraseTestResult(result),
                            "Phrase group test",
                            MessageBoxButton.OK,
                            result != null && result.Accepted ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    }
                    finally
                    {
                        testButton.Background = oldBackground;
                        testButton.Foreground = oldForeground;
                        testButton.ToolTip = oldToolTip;
                        testButton.IsHitTestVisible = true;

                        Keyboard.ClearFocus();
                    }
                };

                tb.Margin = new Thickness(tb.Margin.Left, tb.Margin.Top, 0, tb.Margin.Bottom);

                Grid.SetColumn(tb, 0);
                Grid.SetColumn(micButton, 1);
                Grid.SetColumn(testButton, 2);

                grid.Children.Add(tb);
                grid.Children.Add(micButton);
                grid.Children.Add(testButton);

                return grid;
            }

            var buttons = new Grid
            {
                Margin = new Thickness(0, 0, 0, 0)
            };

            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left side (Clear)
            var leftPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            leftPanel.Children.Add(clear);
            Grid.SetColumn(leftPanel, 0);

            // Right side (OK + Cancel)
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            rightPanel.Children.Add(ok);
            rightPanel.Children.Add(cancel);
            Grid.SetColumn(rightPanel, 1);

            buttons.Children.Add(leftPanel);
            buttons.Children.Add(rightPanel);

            var content = new StackPanel();
            content.Children.Add(label1);
            content.Children.Add(MakePhraseRow(tb1));
            content.Children.Add(label2);
            content.Children.Add(MakePhraseRow(tb2));
            content.Children.Add(label3);
            content.Children.Add(MakePhraseRow(tb3));

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(content);

            dlg.Content = root;

            dlg.Loaded += (_, __) =>
            {
                tb1.Focus();
                tb1.SelectAll();
            };

            if (dlg.ShowDialog() != true)
                return;

            static string NormalizeAliasGroupForDisplay(string? text)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return string.Empty;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var parts = new List<string>();

                foreach (var raw in text.Split(';'))
                {
                    string trimmed = raw?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    // Use normalized version ONLY for duplicate detection
                    string normalized = VoiceTriggerService.NormalizePhrase(trimmed);

                    if (seen.Add(normalized))
                        parts.Add(trimmed); // <-- keep original casing
                }

                return string.Join("; ", parts);
            }

            static List<string> ExpandAliasGroup(string? text)
            {
                var results = new List<string>();
                if (string.IsNullOrWhiteSpace(text))
                    return results;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in text.Split(';'))
                {
                    string normalized = VoiceTriggerService.NormalizePhrase(raw);
                    if (string.IsNullOrWhiteSpace(normalized))
                        continue;

                    if (seen.Add(normalized))
                        results.Add(normalized);
                }

                return results;
            }

            string p1 = NormalizeAliasGroupForDisplay(tb1.Text);
            string p2 = NormalizeAliasGroupForDisplay(tb2.Text);
            string p3 = NormalizeAliasGroupForDisplay(tb3.Text);

            var entered = new List<string>();
            entered.AddRange(ExpandAliasGroup(p1));
            entered.AddRange(ExpandAliasGroup(p2));
            entered.AddRange(ExpandAliasGroup(p3));

            if (entered.Count == 0)
            {
                ClearVoiceTriggerPhrases(playlistIndex);
                return;
            }

            var localDupes = entered
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);

            if (localDupes != null)
            {
                MessageBox.Show(
                    this,
                    $"You entered the same phrase more than once: \"{localDupes.Key}\".",
                    "Duplicate key phrase",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            for (int i = 0; i < _playlists.Count; i++)
            {
                if (i == playlistIndex)
                    continue;

                foreach (var other in EnumerateNormalizedVoiceTriggerPhrases(_playlists[i]))
                {
                    if (entered.Any(x => string.Equals(x, other, StringComparison.OrdinalIgnoreCase)))
                    {
                        MessageBox.Show(
                            this,
                            $"That phrase is already assigned to playlist \"{_playlists[i].Name}\".",
                            "Duplicate key phrase",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            playlist.VoiceTriggerPhrase1 = p1;
            playlist.VoiceTriggerPhrase2 = p2;
            playlist.VoiceTriggerPhrase3 = p3;

            if (playlist.VoiceTriggerCooldownMs <= 0)
                playlist.VoiceTriggerCooldownMs = DefaultVoiceTriggerCooldownMs;

            RefreshVoiceCaptureState(showErrors: _voiceCaptureMode != VoiceCaptureMode.Off);
            RequestSaveState();
            BuildTabs(playlistIndex);
        }

        private void ClearVoiceTriggerPhrases(int playlistIndex)
        {
            if (playlistIndex < 0 || playlistIndex >= _playlists.Count)
                return;

            var playlist = _playlists[playlistIndex];
            playlist.VoiceTriggerPhrase1 = null;
            playlist.VoiceTriggerPhrase2 = null;
            playlist.VoiceTriggerPhrase3 = null;
            playlist.VoiceTriggerLastIndex = -1;
            playlist.VoiceTriggerLastFireUtc = DateTime.MinValue;

            RefreshVoiceCaptureState(showErrors: false);
            RequestSaveState();
            BuildTabs(playlistIndex);
        }

        private void EnsureWhisperDefaults()
        {
            string baseDir = AppContext.BaseDirectory;

            string exeName = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? "whisper-cli.exe"
                : "whisper-cli";

            if (string.IsNullOrWhiteSpace(_voiceTriggerService.WhisperExePath))
                _voiceTriggerService.WhisperExePath = Path.Combine(baseDir, exeName);

            if (string.IsNullOrWhiteSpace(_voiceTriggerService.WhisperModelPath))
                _voiceTriggerService.WhisperModelPath = Path.Combine(baseDir, "models", "ggml-base.en.bin");

            if (_voiceTriggerService.ChunkMilliseconds <= 0)
                _voiceTriggerService.ChunkMilliseconds = 3000;

            if (_voiceTriggerService.StepMilliseconds <= 0)
                _voiceTriggerService.StepMilliseconds = 1500;

            _voiceTriggerService.ShouldTranscribe = ShouldWhisperTranscribeNow;
        }

        private static VoicePhraseGroup? BuildVoicePhraseGroup(string? groupText)
        {
            if (string.IsNullOrWhiteSpace(groupText))
                return null;

            var aliases = groupText
                .Split(';')
                .Select(VoiceTriggerService.NormalizePhrase)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count == 0)
                return null;

            // First entry stays the canonical phrase.
            return new VoicePhraseGroup(aliases[0], aliases);
        }

        private bool ShouldWhisperTranscribeNow()
        {
            if (_voiceCaptureMode == VoiceCaptureMode.Off)
                return false;

            if (_voiceCaptureMode == VoiceCaptureMode.On)
            {
                _lastWhisperArmedUtc = DateTime.UtcNow;
                return true;
            }

            // In Ctrl mode, exports happen manually on Ctrl release.
            return false;
        }

        private static IEnumerable<VoicePhraseGroup> EnumerateVoicePhraseGroups(PlaylistState playlist)
        {
            if (playlist == null)
                yield break;

            string[] groups =
            [
                playlist.VoiceTriggerPhrase1 ?? "",
                playlist.VoiceTriggerPhrase2 ?? "",
                playlist.VoiceTriggerPhrase3 ?? ""
            ];

            foreach (var raw in groups)
            {
                var group = BuildVoicePhraseGroup(raw);
                if (group != null)
                    yield return group;
            }
        }
    }
}