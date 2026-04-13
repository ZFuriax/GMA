using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace MusicPlayer
{
    internal static class WhisperProcessRunner
    {
        private static readonly object _factoryGate = new();

        private static WhisperFactory? _factory;
        private static string? _loadedModelPath;

        public static async Task<string> TranscribeAsync(
            string whisperCliPath,
            string modelPath,
            string wavPath,
            string language,
            CancellationToken cancellationToken)
        {
            // whisperCliPath is intentionally unused now.
            _ = whisperCliPath;

            if (string.IsNullOrWhiteSpace(modelPath))
                throw new InvalidOperationException("Whisper model path is blank.");

            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Whisper model file not found.", modelPath);

            if (!File.Exists(wavPath))
                throw new FileNotFoundException("Input WAV file not found.", wavPath);

            AppendWhisperRunnerLog(
                $"{DateTime.Now:HH:mm:ss.fff} START (in-process){Environment.NewLine}" +
                $"  MODEL: {modelPath}{Environment.NewLine}" +
                $"  WAV: {wavPath}{Environment.NewLine}" +
                $"  LANG: {EscapeArg(language)}{Environment.NewLine}");

            WhisperFactory factory = GetOrCreateFactory(modelPath);

            var transcript = new StringBuilder();

            using var fileStream = File.OpenRead(wavPath);
            using var processor = factory.CreateBuilder()
                .WithLanguage(EscapeArg(language))
                .Build();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await foreach (var result in processor.ProcessAsync(fileStream).WithCancellation(timeoutCts.Token))
                {
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        if (transcript.Length > 0)
                            transcript.Append(' ');

                        transcript.Append(result.Text.Trim());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AppendWhisperRunnerLog(
                    $"{DateTime.Now:HH:mm:ss.fff} TIMEOUT/CANCELLED (in-process){Environment.NewLine}" +
                    $"TEXT:{Environment.NewLine}{transcript}{Environment.NewLine}");

                throw new TimeoutException(
                    "In-process Whisper transcription did not finish within 30 seconds." + Environment.NewLine +
                    "PARTIAL TEXT:" + Environment.NewLine + transcript);
            }

            string normalized = NormalizeTranscript(transcript.ToString());

            AppendWhisperRunnerLog(
                $"{DateTime.Now:HH:mm:ss.fff} DONE (in-process){Environment.NewLine}" +
                $"TEXT:{Environment.NewLine}{normalized}{Environment.NewLine}");

            return normalized;
        }

        private static WhisperFactory GetOrCreateFactory(string modelPath)
        {
            lock (_factoryGate)
            {
                if (_factory != null &&
                    string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                {
                    return _factory;
                }

                try
                {
                    _factory?.Dispose();
                }
                catch
                {
                }

                _factory = WhisperFactory.FromPath(modelPath);
                _loadedModelPath = modelPath;
                return _factory;
            }
        }

        [Conditional("DEBUG")]
        private static void AppendWhisperRunnerLog(string text)
        {
            try
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "whisper_runner.log");
                File.AppendAllText(logPath, text);
            }
            catch
            {
            }
        }

        private static string NormalizeTranscript(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();

            while (s.Contains("  ", StringComparison.Ordinal))
                s = s.Replace("  ", " ", StringComparison.Ordinal);

            return s;
        }

        private static string EscapeArg(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "en" : value.Trim();
        }
    }
}