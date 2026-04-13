using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer
{
    internal sealed class VoiceTriggerService : IDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
        private readonly WhisperAudioCapture _audioCapture = new();

        private HashSet<string> _activeNormalizedPhrases = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, PhraseProfile> _phraseProfiles = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _aliasToCanonical = new(StringComparer.OrdinalIgnoreCase);

        private string? _lastAcceptedPhrase;
        private DateTime _lastAcceptedUtc = DateTime.MinValue;

        private string? _lastTranscriptFragment;
        private DateTime _lastTranscriptFragmentUtc = DateTime.MinValue;

        private bool _isRunning;
        private CancellationTokenSource? _cts;

        private int _warmupInProgress;
        private DateTime _startedUtc = DateTime.MinValue;


        public bool CaptureOnlyMode { get; set; }
        public VoiceRecognitionMode RecognitionMode { get; set; } = VoiceRecognitionMode.Command;

        public event Action<string>? PhraseRecognized;

        public const float DefaultMinOverallConfidence = 0.70f;
        public const float DefaultMinWordConfidence = 0.65f;
        public const float DefaultMinFinalWordConfidence = 0.65f;
        public const float DefaultMinAverageMeaningfulWordConfidence = 0.72f;
        public const float DefaultMinAcceptanceScore = 0.80f;
        public const float DefaultMinAlternateGap = 0.10f;
        public const int DefaultTriggerCooldownMs = 1200;
        public const double DefaultMinBestPhraseSimilarity = 0.74;
        public const double DefaultMinBestPhraseScoreGap = 0.10;

        private const int RecentAcceptedTailTrimWindowMs = 2500;

        public float MinOverallConfidence { get; set; } = DefaultMinOverallConfidence;
        public float MinWordConfidence { get; set; } = DefaultMinWordConfidence;
        public float MinFinalWordConfidence { get; set; } = DefaultMinFinalWordConfidence;
        public float MinAverageMeaningfulWordConfidence { get; set; } = DefaultMinAverageMeaningfulWordConfidence;
        public float MinAcceptanceScore { get; set; } = DefaultMinAcceptanceScore;
        public float MinAlternateGap { get; set; } = DefaultMinAlternateGap;
        public int TriggerCooldownMs { get; set; } = DefaultTriggerCooldownMs;
        public double MinBestPhraseSimilarity { get; set; } = DefaultMinBestPhraseSimilarity;
        public double MinBestPhraseScoreGap { get; set; } = DefaultMinBestPhraseScoreGap;

        public string WhisperExePath { get; set; } = string.Empty;
        public string WhisperModelPath { get; set; } = string.Empty;

        // Transcript carryover tuning
        public int TranscriptCarryoverMaxAgeMs { get; set; } = 4000;
        public double TranscriptCarryoverMinGain { get; set; } = 0.08;

        public int ChunkMilliseconds
        {
            get => _audioCapture.ChunkMilliseconds;
            set => _audioCapture.ChunkMilliseconds = value;
        }

        public int StepMilliseconds
        {
            get => _audioCapture.StepMilliseconds;
            set => _audioCapture.StepMilliseconds = value;
        }

        public Func<bool>? ShouldTranscribe
        {
            get => _audioCapture.ShouldExport;
            set => _audioCapture.ShouldExport = value;
        }

        public void BeginManualSegment()
        {
            _audioCapture.BeginManualSegment();
        }

        public bool EndManualSegmentAndExport()
        {
            return _audioCapture.EndManualSegmentAndExport();
        }

        private static readonly string VoiceLogPath =
            Path.Combine(AppContext.BaseDirectory, "voice_log.txt");

        private static readonly HashSet<string> LowValueWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a",
            "an",
            "the",
            "of",
            "to",
            "and",
            "uh",
            "um"
        };

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return _isRunning;
            }
        }

        public void ResetConfidenceThresholdsToDefaults()
        {
            MinOverallConfidence = DefaultMinOverallConfidence;
            MinWordConfidence = DefaultMinWordConfidence;
            MinFinalWordConfidence = DefaultMinFinalWordConfidence;
            MinAverageMeaningfulWordConfidence = DefaultMinAverageMeaningfulWordConfidence;
            MinAcceptanceScore = DefaultMinAcceptanceScore;
            MinAlternateGap = DefaultMinAlternateGap;
            TriggerCooldownMs = DefaultTriggerCooldownMs;
            MinBestPhraseSimilarity = DefaultMinBestPhraseSimilarity;
            MinBestPhraseScoreGap = DefaultMinBestPhraseScoreGap;
        }

        public bool TryStart(IEnumerable<VoicePhraseGroup> phraseGroups, out string? error)
        {
            error = null;

            var groups = phraseGroups?.ToList() ?? new List<VoicePhraseGroup>();
            var allAliases = VoicePhraseGroups.FlattenAliases(groups);

            Stop();

            if (allAliases.Count == 0)
                return true;

            try
            {
                if (string.IsNullOrWhiteSpace(WhisperModelPath))
                {
                    error = "Whisper model path is blank.";
                    return false;
                }

                var cts = new CancellationTokenSource();

                lock (_gate)
                {
                    _activeNormalizedPhrases = new HashSet<string>(allAliases, StringComparer.OrdinalIgnoreCase);
                    _phraseProfiles = allAliases.ToDictionary(
                        p => p,
                        p => PhraseProfile.Create(p),
                        StringComparer.OrdinalIgnoreCase);

                    _aliasToCanonical = VoicePhraseGroups.BuildAliasToCanonicalMap(groups);
                    _lastAcceptedPhrase = null;
                    _lastAcceptedUtc = DateTime.MinValue;
                    _lastTranscriptFragment = null;
                    _lastTranscriptFragmentUtc = DateTime.MinValue;
                    _cts = cts;
                    _isRunning = true;
                    _startedUtc = DateTime.UtcNow;
                }

                Interlocked.Exchange(ref _warmupInProgress, 0);

                _audioCapture.ChunkReady -= AudioCapture_ChunkReady;
                _audioCapture.ChunkReady += AudioCapture_ChunkReady;
                _audioCapture.Start(enablePeriodicExports: !CaptureOnlyMode);

                if (!CaptureOnlyMode)
                {
                    _ = Task.Run(() => RunStartupProbeAsync(cts.Token));
                    _ = Task.Run(() => RunWarmupAsync(cts.Token));
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts;

            lock (_gate)
            {
                cts = _cts;
                _cts = null;
                _isRunning = false;
                _activeNormalizedPhrases.Clear();
                _phraseProfiles.Clear();
                _aliasToCanonical.Clear();
                _lastAcceptedPhrase = null;
                _lastAcceptedUtc = DateTime.MinValue;
                _lastTranscriptFragment = null;
                _lastTranscriptFragmentUtc = DateTime.MinValue;
                _startedUtc = DateTime.MinValue;
            }

            Interlocked.Exchange(ref _warmupInProgress, 0);

            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }

            try { _audioCapture.ChunkReady -= AudioCapture_ChunkReady; } catch { }
            try { _audioCapture.Stop(); } catch { }
        }

        private async Task RunStartupProbeAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(900, token);

                lock (_gate)
                {
                    if (!_isRunning || _cts == null || _cts.Token != token)
                        return;
                }

                _audioCapture.ExportStartupProbeWindow();
                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | startupProbeRequested=true");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | startupProbeError=\"{ex.Message}\"");
            }
        }

        private async Task RunWarmupAsync(CancellationToken token)
        {
            if (Interlocked.Exchange(ref _warmupInProgress, 1) != 0)
                return;

            try
            {
                await Task.Delay(1600, token);

                lock (_gate)
                {
                    if (!_isRunning || _cts == null || _cts.Token != token)
                        return;
                }

                _audioCapture.ExportWarmupWindow();
                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | warmupRequested=true");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | warmupError=\"{ex.Message}\"");
            }
            finally
            {
                Interlocked.Exchange(ref _warmupInProgress, 0);
            }
        }

        private void AudioCapture_ChunkReady(string wavPath, bool isWarmup)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    CancellationToken token;

                    lock (_gate)
                    {
                        if (!_isRunning || _cts == null)
                        {
                            TryDelete(wavPath);
                            return;
                        }

                        token = _cts.Token;
                    }

                    // Do not queue a backlog of stale overlapping windows.
                    if (!await _transcriptionGate.WaitAsync(0, token))
                    {
                        TryDelete(wavPath);
                        return;
                    }

                    try
                    {
                        string transcript = await WhisperProcessRunner.TranscribeAsync(
                            WhisperExePath,
                            WhisperModelPath,
                            wavPath,
                            "en",
                            token);

                        if (!string.IsNullOrWhiteSpace(transcript))
                        {
                            string normalizedWarmup = CollapseImmediateRepeatedPhrase(NormalizePhrase(transcript));

                            if (isWarmup && !LooksLikeStrongCommandTranscript(normalizedWarmup))
                            {
                                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | startupIgnoredRaw=\"{transcript}\"");
                            }
                            else
                            {
                                EvaluateTranscript(transcript);
                            }
                        }
                    }
                    finally
                    {
                        _transcriptionGate.Release();
                        TryDelete(wavPath);
                    }
                }
                catch (OperationCanceledException)
                {
                    TryDelete(wavPath);
                }
                catch (Exception ex)
                {
                    AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | whisperError=\"{ex.Message}\"");
                    TryDelete(wavPath);
                }
            });
        }

        private bool LooksLikeStrongCommandTranscript(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            var candidates = BuildCandidateScores(normalized, _activeNormalizedPhrases);
            if (candidates.Count == 0)
                return false;

            return candidates[0].Score >= 0.88;
        }

        private sealed record class TranscriptVariantChoice
        {
            public string Text { get; init; } = string.Empty;
            public string Source { get; init; } = "current";
            public List<CandidateScore> Candidates { get; init; } = new();

            public string PreviousFragment { get; init; } = string.Empty;
            public bool PreviousFragmentUsable { get; init; }
            public bool CurrentLooksIncomplete { get; init; }

            public double CurrentBestScore { get; init; }
            public double PrevPlusCurrentBestScore { get; init; }
            public double CurrentPlusPrevBestScore { get; init; }

            public string DecisionReason { get; init; } = "current_only";

            public string CurrentText { get; init; } = string.Empty;
            public List<CandidateScore> CurrentCandidates { get; init; } = new();

            public string BestCarryoverText { get; init; } = string.Empty;
            public List<CandidateScore> BestCarryoverCandidates { get; init; } = new();
            public double BestCarryoverScore { get; init; }
        }

        private TranscriptVariantChoice ChooseBestTranscriptVariant(string normalizedCurrent)
        {
            string? previousFragment;
            DateTime previousUtc;

            lock (_gate)
            {
                previousFragment = _lastTranscriptFragment;
                previousUtc = _lastTranscriptFragmentUtc;
            }

            var currentCandidates = BuildCandidateScores(normalizedCurrent, _activeNormalizedPhrases);
            double currentBestScore = currentCandidates.Count > 0 ? currentCandidates[0].Score : 0.0;

            bool currentLooksIncomplete = LooksLikeIncompleteFragment(normalizedCurrent, currentCandidates);

            TranscriptVariantChoice best = new()
            {
                Text = normalizedCurrent,
                Source = "current",
                Candidates = currentCandidates,
                PreviousFragment = previousFragment ?? string.Empty,
                PreviousFragmentUsable = false,
                CurrentLooksIncomplete = currentLooksIncomplete,
                CurrentBestScore = currentBestScore,
                PrevPlusCurrentBestScore = 0.0,
                CurrentPlusPrevBestScore = 0.0,
                DecisionReason = "no_previous_fragment",
                CurrentText = normalizedCurrent,
                CurrentCandidates = currentCandidates,
                BestCarryoverText = string.Empty,
                BestCarryoverCandidates = new(),
                BestCarryoverScore = 0.0
            };

            if (string.IsNullOrWhiteSpace(previousFragment))
                return best;

            if ((DateTime.UtcNow - previousUtc).TotalMilliseconds > TranscriptCarryoverMaxAgeMs)
            {
                return best with
                {
                    PreviousFragment = previousFragment,
                    DecisionReason = "previous_fragment_expired"
                };
            }

            bool previousFragmentUsable = ShouldStoreCarryoverFragment(previousFragment);
            if (!previousFragmentUsable)
            {
                return best with
                {
                    PreviousFragment = previousFragment,
                    PreviousFragmentUsable = false,
                    DecisionReason = "previous_fragment_not_usable"
                };
            }

            if (LooksLikeNonCommandNoise(normalizedCurrent))
            {
                return best with
                {
                    PreviousFragment = previousFragment,
                    PreviousFragmentUsable = true,
                    DecisionReason = "current_looks_like_noise"
                };
            }

            string prevPlusCurrent = CombineCarryoverFragments(previousFragment, normalizedCurrent);
            string currentPlusPrev = CombineCarryoverFragments(normalizedCurrent, previousFragment);

            var prevPlusCurrentCandidates =
                !string.IsNullOrWhiteSpace(prevPlusCurrent) &&
                !string.Equals(prevPlusCurrent, normalizedCurrent, StringComparison.OrdinalIgnoreCase)
                    ? BuildCandidateScores(prevPlusCurrent, _activeNormalizedPhrases)
                    : new List<CandidateScore>();

            var currentPlusPrevCandidates =
                !string.IsNullOrWhiteSpace(currentPlusPrev) &&
                !string.Equals(currentPlusPrev, normalizedCurrent, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentPlusPrev, prevPlusCurrent, StringComparison.OrdinalIgnoreCase)
                    ? BuildCandidateScores(currentPlusPrev, _activeNormalizedPhrases)
                    : new List<CandidateScore>();

            double prevPlusCurrentBestScore =
                prevPlusCurrentCandidates.Count > 0 ? prevPlusCurrentCandidates[0].Score : 0.0;

            double currentPlusPrevBestScore =
                currentPlusPrevCandidates.Count > 0 ? currentPlusPrevCandidates[0].Score : 0.0;

            double bestCarryoverScore = prevPlusCurrentBestScore;
            string bestCarryoverText = prevPlusCurrent;
            string bestCarryoverSource = "carryover_prev_current";
            List<CandidateScore> bestCarryoverCandidates = prevPlusCurrentCandidates;

            if (currentPlusPrevBestScore > bestCarryoverScore)
            {
                bestCarryoverScore = currentPlusPrevBestScore;
                bestCarryoverText = currentPlusPrev;
                bestCarryoverSource = "carryover_current_prev";
                bestCarryoverCandidates = currentPlusPrevCandidates;
            }

            var phraseGuidedCandidates = BuildPhraseGuidedCarryoverCandidates(previousFragment, normalizedCurrent);
            if (phraseGuidedCandidates.Count > 0)
            {
                var guided = phraseGuidedCandidates[0];

                if (guided.Score > bestCarryoverScore)
                {
                    bestCarryoverScore = guided.Score;
                    bestCarryoverText = guided.Phrase;
                    bestCarryoverSource = guided.Source;
                    bestCarryoverCandidates = guided.Candidates;
                }
            }

            double requiredGain = currentLooksIncomplete ? 0.03 : TranscriptCarryoverMinGain;

            bool carryoverAllowed =
                !string.IsNullOrWhiteSpace(bestCarryoverText) &&
                bestCarryoverScore >= currentBestScore + requiredGain &&
                bestCarryoverScore >= 0.68 &&
                (
                    currentLooksIncomplete ||
                    currentBestScore <= 0.72 ||
                    CountTokens(normalizedCurrent) <= 2
                );

            if (carryoverAllowed)
            {
                return new TranscriptVariantChoice
                {
                    Text = bestCarryoverText,
                    Source = bestCarryoverSource,
                    Candidates = bestCarryoverCandidates,
                    PreviousFragment = previousFragment,
                    PreviousFragmentUsable = true,
                    CurrentLooksIncomplete = currentLooksIncomplete,
                    CurrentBestScore = currentBestScore,
                    PrevPlusCurrentBestScore = prevPlusCurrentBestScore,
                    CurrentPlusPrevBestScore = currentPlusPrevBestScore,
                    DecisionReason = currentLooksIncomplete
                        ? "carryover_preferred_for_incomplete_current"
                        : "carryover_won",
                    CurrentText = normalizedCurrent,
                    CurrentCandidates = currentCandidates,
                    BestCarryoverText = bestCarryoverText,
                    BestCarryoverCandidates = bestCarryoverCandidates,
                    BestCarryoverScore = bestCarryoverScore
                };
            }

            return new TranscriptVariantChoice
            {
                Text = normalizedCurrent,
                Source = "current",
                Candidates = currentCandidates,
                PreviousFragment = previousFragment,
                PreviousFragmentUsable = true,
                CurrentLooksIncomplete = currentLooksIncomplete,
                CurrentBestScore = currentBestScore,
                PrevPlusCurrentBestScore = prevPlusCurrentBestScore,
                CurrentPlusPrevBestScore = currentPlusPrevBestScore,
                DecisionReason = "current_won",
                CurrentText = normalizedCurrent,
                CurrentCandidates = currentCandidates,
                BestCarryoverText = bestCarryoverText,
                BestCarryoverCandidates = bestCarryoverCandidates,
                BestCarryoverScore = bestCarryoverScore
            };
        }

        private bool LooksLikeIncompleteFragment(string normalizedCurrent, List<CandidateScore> currentCandidates)
        {
            if (string.IsNullOrWhiteSpace(normalizedCurrent))
                return false;

            if (LooksLikeNonCommandNoise(normalizedCurrent))
                return false;

            int tokenCount = CountTokens(normalizedCurrent);
            double best = currentCandidates.Count > 0 ? currentCandidates[0].Score : 0.0;

            if (tokenCount <= 1)
                return best >= 0.15 && best <= 0.88;

            if (tokenCount == 2)
                return best >= 0.20 && best <= 0.85;

            return best >= 0.30 && best <= 0.82;
        }

        private void UpdateTranscriptCarryover(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            if (!ShouldStoreCarryoverFragment(normalized))
                return;

            lock (_gate)
            {
                _lastTranscriptFragment = normalized;
                _lastTranscriptFragmentUtc = DateTime.UtcNow;
            }
        }

        private List<(string Phrase, List<CandidateScore> Candidates, double Score, string Source)> BuildPhraseGuidedCarryoverCandidates(
            string previousFragment,
            string currentFragment)
        {
            var results = new List<(string Phrase, List<CandidateScore> Candidates, double Score, string Source)>();

            string prev = NormalizePhrase(previousFragment);
            string current = NormalizePhrase(currentFragment);

            if (string.IsNullOrWhiteSpace(prev) || string.IsNullOrWhiteSpace(current))
                return results;

            foreach (string knownPhrase in _activeNormalizedPhrases)
            {
                string normalizedPhrase = NormalizePhrase(knownPhrase);
                if (string.IsNullOrWhiteSpace(normalizedPhrase))
                    continue;

                double prevScore = ComputeSimilarity(prev, normalizedPhrase);
                double currentScore = ComputeSimilarity(current, normalizedPhrase);

                // Require both fragments to have some real relationship to the same phrase.
                if (prevScore < 0.35 || currentScore < 0.35)
                    continue;

                var candidates = BuildCandidateScores(normalizedPhrase, _activeNormalizedPhrases);
                double score = candidates.Count > 0 ? candidates[0].Score : 0.0;

                results.Add((normalizedPhrase, candidates, score, "carryover_phrase_guided"));
            }

            return results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Phrase, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ClearTranscriptCarryover()
        {
            lock (_gate)
            {
                _lastTranscriptFragment = null;
                _lastTranscriptFragmentUtc = DateTime.MinValue;
            }
        }

        private bool ShouldStoreCarryoverFragment(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            if (string.Equals(normalized, "blank audio", StringComparison.OrdinalIgnoreCase))
                return false;

            if (LooksLikeNonCommandNoise(normalized))
                return false;

            int tokenCount = CountTokens(normalized);

            var candidates = BuildCandidateScores(normalized, _activeNormalizedPhrases);
            if (candidates.Count == 0)
                return false;

            double best = candidates[0].Score;

            if (tokenCount <= 1)
                return best >= 0.18 && best < 0.92;

            if (tokenCount == 2)
                return best >= 0.22 && best < 0.92;

            return best >= 0.30 && best < 0.92;
        }

        private static bool LooksLikeNonCommandNoise(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return true;

            // Whisper often returns parenthetical scene/noise descriptions after normalization
            string[] noisyTerms =
            [
                "music",
                "dramatic",
                "tires",
                "screeching",
                "applause",
                "laughter",
                "noise",
                "engine",
                "background"
            ];

            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int noisyHits = tokens.Count(t => noisyTerms.Contains(t, StringComparer.OrdinalIgnoreCase));
            return noisyHits >= 1;
        }




        private void EvaluateTranscript(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return;

            string trimmed = rawText.Trim();

            if (string.Equals(trimmed, "[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "blank audio", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string normalizedCurrent = NormalizePhrase(rawText);
            normalizedCurrent = CollapseImmediateRepeatedPhrase(normalizedCurrent);

            string repairedBeforeTrim = ApplyCommandSpecificRepairs(normalizedCurrent);
            if (!string.Equals(repairedBeforeTrim, normalizedCurrent, StringComparison.OrdinalIgnoreCase))
            {
                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | commandRepairApplied | stage=\"pre_trim\" | before=\"{normalizedCurrent}\" | after=\"{repairedBeforeTrim}\"");
                normalizedCurrent = repairedBeforeTrim;
            }

            if (string.IsNullOrWhiteSpace(normalizedCurrent))
                return;

            string normalizedCurrentTrimmed = TrimRecentAcceptedPhraseTail(normalizedCurrent);
            normalizedCurrentTrimmed = CollapseImmediateRepeatedPhrase(normalizedCurrentTrimmed);

            string repairedAfterTrim = ApplyCommandSpecificRepairs(normalizedCurrentTrimmed);
            if (!string.Equals(repairedAfterTrim, normalizedCurrentTrimmed, StringComparison.OrdinalIgnoreCase))
            {
                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | commandRepairApplied | stage=\"post_trim\" | before=\"{normalizedCurrentTrimmed}\" | after=\"{repairedAfterTrim}\"");
                normalizedCurrentTrimmed = repairedAfterTrim;
            }

            if (!string.Equals(normalizedCurrentTrimmed, normalizedCurrent, StringComparison.OrdinalIgnoreCase))
            {
                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | tailTrimApplied | before=\"{normalizedCurrent}\" | after=\"{normalizedCurrentTrimmed}\"");
                normalizedCurrent = normalizedCurrentTrimmed;
            }

            if (string.IsNullOrWhiteSpace(normalizedCurrent))
            {
                AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | transcriptIgnoredReason=\"trimmed_to_empty_after_recent_tail_filter\"");
                return;
            }

            TranscriptVariantChoice variant = ChooseBestTranscriptVariant(normalizedCurrent);
            string normalized = variant.Text;

            List<CandidateScore> candidates;
            PhraseProfile? profile;
            bool cooldownPass;
            bool duplicateSuppressed = false;
            string canonicalPhrase;

            lock (_gate)
            {
                candidates = variant.Candidates;

                string bestPhraseForCooldown = candidates.Count > 0 ? candidates[0].Phrase : string.Empty;

                _phraseProfiles.TryGetValue(bestPhraseForCooldown, out profile);

                canonicalPhrase = bestPhraseForCooldown;
                if (!string.IsNullOrWhiteSpace(bestPhraseForCooldown) &&
                    _aliasToCanonical.TryGetValue(bestPhraseForCooldown, out var mappedCanonical) &&
                    !string.IsNullOrWhiteSpace(mappedCanonical))
                {
                    canonicalPhrase = mappedCanonical;
                }

                DateTime now = DateTime.UtcNow;
                cooldownPass =
                    !(string.Equals(_lastAcceptedPhrase, canonicalPhrase, StringComparison.OrdinalIgnoreCase) &&
                    (now - _lastAcceptedUtc).TotalMilliseconds < TriggerCooldownMs);

                if (!cooldownPass)
                    duplicateSuppressed = true;
            }

            CandidateScore? best = candidates.Count > 0 ? candidates[0] : null;
            CandidateScore? second = candidates.Count > 1 ? candidates[1] : null;

            string bestPhrase = best?.Phrase ?? string.Empty;
            double bestPhraseSimilarity = best?.Score ?? 0.0;

            string secondPhrase = second?.Phrase ?? string.Empty;
            double secondPhraseSimilarity = second?.Score ?? 0.0;

            bool normalizedPass = !string.IsNullOrWhiteSpace(normalized);
            bool phraseMatchPass = !string.IsNullOrWhiteSpace(bestPhrase);
            bool profilePass = profile != null;

            double bestPhraseGap = bestPhraseSimilarity - secondPhraseSimilarity;
            bool sameIntent = AreSameIntent(bestPhrase, secondPhrase);

            int meaningfulWordCount = profile?.MeaningfulWordCount ?? CountMeaningfulTokens(bestPhrase);
            int totalWordCount = profile?.TotalWordCount ?? CountTokens(bestPhrase);

            bool bestPhraseSimilarityPass =
                bestPhraseSimilarity >= GetRequiredBestPhraseSimilarity(meaningfulWordCount);

            bool bestPhraseGapPass =
                string.IsNullOrWhiteSpace(secondPhrase) ||
                sameIntent ||
                bestPhraseGap >= MinBestPhraseScoreGap;

            float pseudoOverall = (float)bestPhraseSimilarity;
            float finalWordConfidence = CountTokens(normalized) > 0 ? pseudoOverall : -1f;
            float averageMeaningfulConfidence = pseudoOverall;
            float minimumMeaningfulConfidence = pseudoOverall;

            float requiredOverall = GetRequiredOverallConfidence(meaningfulWordCount);
            float requiredAverageMeaningful = GetRequiredAverageMeaningfulConfidence(meaningfulWordCount);
            float requiredAcceptanceScore = GetRequiredAcceptanceScore(meaningfulWordCount);

            bool overallPass = pseudoOverall >= requiredOverall;
            bool finalWordPass = finalWordConfidence < 0f || finalWordConfidence >= MinFinalWordConfidence;
            bool averageMeaningfulPass = averageMeaningfulConfidence >= requiredAverageMeaningful;

            float alternateGap = second != null ? (float)(bestPhraseSimilarity - secondPhraseSimilarity) : 1f;
            bool alternateGapPass = second == null || alternateGap >= MinAlternateGap;

            float acceptanceScore = ComputeAcceptanceScore(
                pseudoOverall,
                averageMeaningfulConfidence,
                minimumMeaningfulConfidence,
                finalWordConfidence >= 0f ? finalWordConfidence : pseudoOverall,
                alternateGapPass,
                meaningfulWordCount,
                totalWordCount);

            bool acceptanceScorePass = acceptanceScore >= requiredAcceptanceScore;

            bool acousticallyGoodEnough = IsAcousticallyGoodEnough(
                pseudoOverall,
                averageMeaningfulConfidence,
                finalWordConfidence >= 0f ? finalWordConfidence : pseudoOverall,
                acceptanceScore,
                meaningfulWordCount);

            bool accepted =
                normalizedPass &&
                phraseMatchPass &&
                profilePass &&
                bestPhraseSimilarityPass &&
                bestPhraseGapPass &&
                cooldownPass &&
                acousticallyGoodEnough;

            bool currentWouldPassWithoutCarryover =
                WouldVariantBeAcceptedForLogging(variant.CurrentText, variant.CurrentCandidates);

            bool bestCarryoverWouldPass =
                !string.IsNullOrWhiteSpace(variant.BestCarryoverText) &&
                variant.BestCarryoverCandidates.Count > 0 &&
                WouldVariantBeAcceptedForLogging(variant.BestCarryoverText, variant.BestCarryoverCandidates);

            var sb = new StringBuilder();
            sb.Append($"{DateTime.Now:HH:mm:ss} | ");
            string normalizedCurrentBeforeTrim = NormalizePhrase(rawText);
            sb.Append($"raw=\"{rawText}\" | ");
            sb.Append($"normalizedCurrentRaw=\"{normalizedCurrentBeforeTrim}\" | ");
            sb.Append($"normalizedCurrent=\"{normalizedCurrent}\" | ");
            sb.Append($"normalizedUsed=\"{normalized}\" | ");
            sb.Append($"carryoverSource=\"{variant.Source}\" | ");
            sb.Append($"carryoverDecision=\"{variant.DecisionReason}\" | ");
            sb.Append($"previousFragment=\"{variant.PreviousFragment}\" | ");
            sb.Append($"previousFragmentUsable={variant.PreviousFragmentUsable} | ");
            sb.Append($"currentLooksIncomplete={variant.CurrentLooksIncomplete} | ");
            sb.Append($"currentBestScore={variant.CurrentBestScore:0.00} | ");
            sb.Append($"prevPlusCurrentBestScore={variant.PrevPlusCurrentBestScore:0.00} | ");
            sb.Append($"currentPlusPrevBestScore={variant.CurrentPlusPrevBestScore:0.00} | ");
            sb.Append($"bestCarryoverScore={variant.BestCarryoverScore:0.00} | ");
            sb.Append($"bestCarryoverText=\"{variant.BestCarryoverText}\" | ");
            sb.Append($"currentWouldPassWithoutCarryover={currentWouldPassWithoutCarryover} | ");
            sb.Append($"bestCarryoverWouldPass={bestCarryoverWouldPass} | ");
            sb.Append($"selectedPhrase=\"{canonicalPhrase}\" | ");
            sb.Append($"selectedPhraseScore={bestPhraseSimilarity:0.00} | ");
            sb.Append($"runnerUpPhrase=\"{secondPhrase}\" | ");
            sb.Append($"runnerUpScore={secondPhraseSimilarity:0.00} | ");
            sb.Append($"phraseGap={bestPhraseGap:0.00} | ");
            sb.Append($"overall={pseudoOverall:0.00} | ");
            sb.Append($"overallPass={overallPass} | ");
            sb.Append($"avgMeaningful={averageMeaningfulConfidence:0.00} | ");
            sb.Append($"avgMeaningfulPass={averageMeaningfulPass} | ");
            sb.Append($"acceptanceScore={acceptanceScore:0.00} | ");
            sb.Append($"acceptanceScorePass={acceptanceScorePass} | ");
            sb.Append($"alternateGap={alternateGap:0.00} | ");
            sb.Append($"alternateGapPass={alternateGapPass} | ");
            sb.Append($"cooldownPass={cooldownPass} | ");
            sb.Append($"duplicateSuppressed={duplicateSuppressed} | ");
            sb.Append($"accepted={accepted}");

            AppendVoiceLog(sb.ToString());

            if (!accepted)
            {
                if (!LooksLikeRecentAcceptedTailContamination(normalizedCurrent))
                {
                    UpdateTranscriptCarryover(normalizedCurrent);
                }
                else
                {
                    AppendVoiceLog($"{DateTime.Now:HH:mm:ss} | carryoverSkippedReason=\"recent_accepted_tail_contamination\" | text=\"{normalizedCurrent}\"");
                }

                return;
            }

            ClearTranscriptCarryover();

            lock (_gate)
            {
                _lastAcceptedPhrase = canonicalPhrase;
                _lastAcceptedUtc = DateTime.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(canonicalPhrase))
                PhraseRecognized?.Invoke(canonicalPhrase);
        }

        private List<CandidateScore> BuildCandidateScores(string normalizedHeard, IEnumerable<string> phrases)
        {
            var list = new List<CandidateScore>();

            foreach (var phrase in phrases)
            {
                string normalizedPhrase = NormalizePhrase(phrase);
                if (string.IsNullOrWhiteSpace(normalizedPhrase))
                    continue;

                double score = ComputeSimilarity(normalizedHeard, normalizedPhrase);
                list.Add(new CandidateScore(normalizedPhrase, score));
            }

            return list
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Phrase, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool WouldVariantBeAcceptedForLogging(string normalized, List<CandidateScore> candidates)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            CandidateScore? best = candidates.Count > 0 ? candidates[0] : null;
            CandidateScore? second = candidates.Count > 1 ? candidates[1] : null;

            string bestPhrase = best?.Phrase ?? string.Empty;
            double bestPhraseSimilarity = best?.Score ?? 0.0;

            string secondPhrase = second?.Phrase ?? string.Empty;
            double secondPhraseSimilarity = second?.Score ?? 0.0;

            if (string.IsNullOrWhiteSpace(bestPhrase))
                return false;

            if (!_phraseProfiles.TryGetValue(bestPhrase, out var profile) || profile == null)
                return false;

            int meaningfulWordCount = profile.MeaningfulWordCount;
            int totalWordCount = profile.TotalWordCount;

            double bestPhraseGap = bestPhraseSimilarity - secondPhraseSimilarity;
            bool sameIntent = AreSameIntent(bestPhrase, secondPhrase);

            bool bestPhraseSimilarityPass =
                bestPhraseSimilarity >= GetRequiredBestPhraseSimilarity(meaningfulWordCount);

            bool bestPhraseGapPass =
                string.IsNullOrWhiteSpace(secondPhrase) ||
                sameIntent ||
                bestPhraseGap >= MinBestPhraseScoreGap;

            float pseudoOverall = (float)bestPhraseSimilarity;
            float finalWordConfidence = CountTokens(normalized) > 0 ? pseudoOverall : -1f;
            float averageMeaningfulConfidence = pseudoOverall;
            float minimumMeaningfulConfidence = pseudoOverall;

            float acceptanceScore = ComputeAcceptanceScore(
                pseudoOverall,
                averageMeaningfulConfidence,
                minimumMeaningfulConfidence,
                finalWordConfidence >= 0f ? finalWordConfidence : pseudoOverall,
                second == null || (float)(bestPhraseSimilarity - secondPhraseSimilarity) >= MinAlternateGap,
                meaningfulWordCount,
                totalWordCount);

            bool acousticallyGoodEnough = IsAcousticallyGoodEnough(
                pseudoOverall,
                averageMeaningfulConfidence,
                finalWordConfidence >= 0f ? finalWordConfidence : pseudoOverall,
                acceptanceScore,
                meaningfulWordCount);

            return bestPhraseSimilarityPass &&
                bestPhraseGapPass &&
                acousticallyGoodEnough;
        }

        private bool IsAcousticallyGoodEnough(
            float overall,
            float averageMeaningfulConfidence,
            float finalWordConfidence,
            float acceptanceScore,
            int meaningfulWordCount)
        {
            if (meaningfulWordCount <= 1)
            {
                return overall >= 0.78f &&
                       averageMeaningfulConfidence >= 0.72f &&
                       finalWordConfidence >= 0.72f;
            }

            if (meaningfulWordCount == 2)
            {
                return
                    acceptanceScore >= 0.60f ||
                    (overall >= 0.58f &&
                     averageMeaningfulConfidence >= 0.58f &&
                     finalWordConfidence >= 0.55f);
            }

            return
                acceptanceScore >= 0.50f ||
                (overall >= 0.50f &&
                 averageMeaningfulConfidence >= 0.45f);
        }

        private static float ComputeAcceptanceScore(
            float overall,
            float averageMeaningfulConfidence,
            float minimumMeaningfulConfidence,
            float finalWordConfidence,
            bool alternateGapPass,
            int meaningfulWordCount,
            int totalWordCount)
        {
            float finalWordComponent = finalWordConfidence >= 0f ? finalWordConfidence : overall;

            float score =
                (overall * 0.35f) +
                (averageMeaningfulConfidence * 0.30f) +
                (minimumMeaningfulConfidence * 0.20f) +
                (finalWordComponent * 0.15f);

            if (!alternateGapPass)
                score -= 0.10f;

            if (meaningfulWordCount <= 1)
                score -= 0.08f;
            else if (meaningfulWordCount == 2)
                score -= 0.04f;

            if (totalWordCount <= 1)
                score -= 0.04f;

            return Math.Clamp(score, 0f, 1f);
        }

        private float GetRequiredOverallConfidence(int meaningfulWordCount)
        {
            if (meaningfulWordCount <= 1)
                return Math.Max(MinOverallConfidence, 0.88f);

            if (meaningfulWordCount == 2)
                return Math.Max(MinOverallConfidence, 0.80f);

            return MinOverallConfidence;
        }

        private float GetRequiredAverageMeaningfulConfidence(int meaningfulWordCount)
        {
            if (meaningfulWordCount <= 1)
                return Math.Max(MinAverageMeaningfulWordConfidence, 0.85f);

            if (meaningfulWordCount == 2)
                return Math.Max(MinAverageMeaningfulWordConfidence, 0.78f);

            return MinAverageMeaningfulWordConfidence;
        }

        private float GetRequiredAcceptanceScore(int meaningfulWordCount)
        {
            if (meaningfulWordCount <= 1)
                return Math.Max(MinAcceptanceScore, 0.88f);

            if (meaningfulWordCount == 2)
                return Math.Max(MinAcceptanceScore, 0.80f);

            return MinAcceptanceScore;
        }


        private static string CollapseImmediateRepeatedPhrase(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                return normalized;

            // Detect exact doubled sequence:
            // "roll for initiative roll for initiative"
            if (tokens.Length % 2 == 0)
            {
                int half = tokens.Length / 2;
                bool same = true;

                for (int i = 0; i < half; i++)
                {
                    if (!string.Equals(tokens[i], tokens[i + half], StringComparison.OrdinalIgnoreCase))
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                    return string.Join(" ", tokens.Take(half));
            }

            return normalized;
        }

        private static string CombineCarryoverFragments(string first, string second)
        {
            first = NormalizePhrase(first);
            second = NormalizePhrase(second);

            if (string.IsNullOrWhiteSpace(first))
                return second;

            if (string.IsNullOrWhiteSpace(second))
                return first;

            var a = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var b = second.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int maxOverlap = Math.Min(a.Length, b.Length);
            int overlap = 0;

            for (int k = maxOverlap; k >= 1; k--)
            {
                bool same = true;

                for (int i = 0; i < k; i++)
                {
                    if (!string.Equals(a[a.Length - k + i], b[i], StringComparison.OrdinalIgnoreCase))
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    overlap = k;
                    break;
                }
            }

            var combinedTokens = new List<string>(a);
            for (int i = overlap; i < b.Length; i++)
                combinedTokens.Add(b[i]);

            string combined = string.Join(" ", combinedTokens);
            combined = NormalizePhrase(combined);
            combined = CollapseImmediateRepeatedPhrase(combined);

            return combined;
        }

        private static List<string> BuildPhraseSuffixCandidates(string normalizedPhrase)
        {
            var results = new List<string>();

            normalizedPhrase = NormalizePhrase(normalizedPhrase);
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                return results;

            var tokens = normalizedPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return results;

            // Build all non-full suffixes:
            // "roll for initiative" -> "for initiative", "initiative"
            // "you continue through the dungeon" -> "continue through the dungeon", ..., "dungeon"
            for (int start = 1; start < tokens.Length; start++)
            {
                string suffix = string.Join(" ", tokens.Skip(start)).Trim();
                if (!string.IsNullOrWhiteSpace(suffix))
                    results.Add(suffix);
            }

            // De-dupe, prefer longer/more specific first.
            return results
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(s => CountTokens(s))
                .ThenByDescending(s => s.Length)
                .ToList();
        }

        private string TrimRecentAcceptedPhraseTail(string normalizedCurrent)
        {
            if (string.IsNullOrWhiteSpace(normalizedCurrent))
                return normalizedCurrent;

            string? lastAcceptedPhrase;
            DateTime lastAcceptedUtc;

            lock (_gate)
            {
                lastAcceptedPhrase = _lastAcceptedPhrase;
                lastAcceptedUtc = _lastAcceptedUtc;
            }

            if (string.IsNullOrWhiteSpace(lastAcceptedPhrase))
                return normalizedCurrent;

            if ((DateTime.UtcNow - lastAcceptedUtc).TotalMilliseconds > RecentAcceptedTailTrimWindowMs)
                return normalizedCurrent;

            string normalizedAccepted = NormalizePhrase(lastAcceptedPhrase);
            if (string.IsNullOrWhiteSpace(normalizedAccepted))
                return normalizedCurrent;

            string working = NormalizePhrase(normalizedCurrent);
            if (string.IsNullOrWhiteSpace(working))
                return working;

            foreach (string suffix in BuildPhraseSuffixCandidates(normalizedAccepted))
            {
                if (string.IsNullOrWhiteSpace(suffix))
                    continue;

                if (string.Equals(working, suffix, StringComparison.OrdinalIgnoreCase))
                    return string.Empty;

                if (!working.StartsWith(suffix + " ", StringComparison.OrdinalIgnoreCase))
                    continue;

                string remainder = working.Substring(suffix.Length).Trim();
                remainder = NormalizePhrase(remainder);
                remainder = CollapseImmediateRepeatedPhrase(remainder);

                if (string.IsNullOrWhiteSpace(remainder))
                    return string.Empty;

                // Only trim if the remainder still looks like the start of something useful.
                var remainderCandidates = BuildCandidateScores(remainder, _activeNormalizedPhrases);
                double remainderBest = remainderCandidates.Count > 0 ? remainderCandidates[0].Score : 0.0;
                int remainderTokens = CountTokens(remainder);

                if (remainderTokens >= 2 || remainderBest >= 0.35)
                    return remainder;
            }

            return working;
        }

        private string ApplyCommandSpecificRepairs(string normalizedCurrent)
        {
            if (string.IsNullOrWhiteSpace(normalizedCurrent))
                return normalizedCurrent;

            string text = NormalizePhrase(normalizedCurrent);
            if (string.IsNullOrWhiteSpace(text))
                return text;

            bool hasRollForInitiative =
                _activeNormalizedPhrases.Contains("roll for initiative");

            if (!hasRollForInitiative)
                return text;

            // Very narrow repair rules based on actual Whisper miss patterns seen in logs.
            // Examples observed:
            // - "roll for it"
            // - "rule for me"
            // - "roll for an inch"
            // - "rule for"
            if (Regex.IsMatch(text, @"^(roll|rule)\s+for(\s+(it|me|an|inch))?$", RegexOptions.IgnoreCase))
                return "roll for initiative";

            return text;
        }

        private bool LooksLikeRecentAcceptedTailContamination(string normalizedCurrent)
        {
            if (string.IsNullOrWhiteSpace(normalizedCurrent))
                return false;

            string trimmed = TrimRecentAcceptedPhraseTail(normalizedCurrent);

            if (string.Equals(NormalizePhrase(normalizedCurrent), NormalizePhrase(trimmed), StringComparison.OrdinalIgnoreCase))
                return false;

            // If trimming removed everything, or removed a lot and left only a tiny fragment,
            // treat it as contamination rather than useful carryover material.
            if (string.IsNullOrWhiteSpace(trimmed))
                return true;

            int originalTokens = CountTokens(normalizedCurrent);
            int trimmedTokens = CountTokens(trimmed);

            if (originalTokens >= 3 && trimmedTokens <= 1)
                return true;

            return false;
        }

        private double GetRequiredBestPhraseSimilarity(int meaningfulWordCount)
        {
            if (meaningfulWordCount <= 1)
                return Math.Max(MinBestPhraseSimilarity, 0.90);

            if (meaningfulWordCount == 2)
                return Math.Max(MinBestPhraseSimilarity, 0.75);

            if (meaningfulWordCount == 3)
                return Math.Max(MinBestPhraseSimilarity, 0.68);

            return MinBestPhraseSimilarity;
        }

        private bool AreSameIntent(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;

            a = NormalizePhrase(a);
            b = NormalizePhrase(b);

            var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !LowValueWords.Contains(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var bTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !LowValueWords.Contains(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int overlap = aTokens.Intersect(bTokens, StringComparer.OrdinalIgnoreCase).Count();
            int minSize = Math.Min(aTokens.Count, bTokens.Count);

            if (minSize == 0)
                return false;

            return (double)overlap / minSize >= 0.66;
        }

        private static int CountTokens(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return 0;

            return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static int CountMeaningfulTokens(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
                return 0;

            return normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(t => !LowValueWords.Contains(t));
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

        private static double ComputeSimilarity(string a, string b)
        {
            a = NormalizePhrase(a);
            b = NormalizePhrase(b);

            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
                return 1.0;

            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return 0.0;

            var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int distance = LevenshteinDistance(a, b);
            int maxLen = Math.Max(a.Length, b.Length);
            double charScore = maxLen == 0 ? 1.0 : 1.0 - (double)distance / maxLen;

            var aMeaningful = aTokens.Where(t => !LowValueWords.Contains(t)).ToArray();
            var bMeaningful = bTokens.Where(t => !LowValueWords.Contains(t)).ToArray();

            double tokenScore;
            if (aMeaningful.Length == 0 && bMeaningful.Length == 0)
            {
                tokenScore = 1.0;
            }
            else if (aMeaningful.Length == 0 || bMeaningful.Length == 0)
            {
                tokenScore = 0.0;
            }
            else
            {
                double totalBest = 0.0;

                foreach (var heardToken in aMeaningful)
                {
                    double best = 0.0;

                    foreach (var phraseToken in bMeaningful)
                    {
                        int td = LevenshteinDistance(heardToken, phraseToken);
                        int tMax = Math.Max(heardToken.Length, phraseToken.Length);
                        double tScore = tMax == 0 ? 1.0 : 1.0 - (double)td / tMax;

                        if (tScore > best)
                            best = tScore;
                    }

                    totalBest += best;
                }

                tokenScore = totalBest / aMeaningful.Length;
            }

            int wordCountDelta = Math.Abs(aTokens.Length - bTokens.Length);
            double lengthPenalty = wordCountDelta switch
            {
                0 => 0.00,
                1 => 0.02,
                2 => 0.05,
                _ => 0.10
            };

            bool shortPhrase = bMeaningful.Length <= 2;

            double combined = shortPhrase
                ? (charScore * 0.35) + (tokenScore * 0.65) - lengthPenalty
                : (charScore * 0.60) + (tokenScore * 0.40) - lengthPenalty;

            return Math.Clamp(combined, 0.0, 1.0);
        }

        public static string NormalizePhrase(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string s = text.Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"[^\p{L}\p{N}\s]", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        [Conditional("DEBUG")]
        private static void AppendVoiceLog(string line)
        {
            try
            {
                File.AppendAllText(VoiceLogPath, line + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Stop();
            _audioCapture.Dispose();
            _transcriptionGate.Dispose();
            GC.SuppressFinalize(this);
        }

        private sealed class PhraseProfile
        {
            public string NormalizedPhrase { get; }
            public int TotalWordCount { get; }
            public int MeaningfulWordCount { get; }

            private PhraseProfile(string normalizedPhrase, int totalWordCount, int meaningfulWordCount)
            {
                NormalizedPhrase = normalizedPhrase;
                TotalWordCount = totalWordCount;
                MeaningfulWordCount = meaningfulWordCount;
            }

            public static PhraseProfile Create(string normalizedPhrase)
            {
                var tokens = normalizedPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int total = tokens.Length;
                int meaningful = tokens.Count(t => !LowValueWords.Contains(t));

                if (meaningful <= 0)
                    meaningful = total;

                return new PhraseProfile(normalizedPhrase, total, meaningful);
            }
        }

        private readonly record struct CandidateScore(string Phrase, double Score);
    }
}