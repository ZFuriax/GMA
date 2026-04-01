using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer
{
    internal static class WhisperProcessRunner
    {
        public static async Task<string> TranscribeAsync(
            string whisperCliPath,
            string modelPath,
            string wavPath,
            string language,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(whisperCliPath))
                throw new InvalidOperationException("Whisper CLI path is blank.");

            if (!File.Exists(whisperCliPath))
                throw new FileNotFoundException("Whisper CLI executable not found.", whisperCliPath);

            if (string.IsNullOrWhiteSpace(modelPath))
                throw new InvalidOperationException("Whisper model path is blank.");

            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Whisper model file not found.", modelPath);

            if (!File.Exists(wavPath))
                throw new FileNotFoundException("Input WAV file not found.", wavPath);

            string args =
                $"-m \"{modelPath}\" " +
                $"-f \"{wavPath}\" " +
                $"-l {EscapeArg(language)} " +
                "-nt";

            AppendWhisperRunnerLog(
                $"{DateTime.Now:HH:mm:ss.fff} START{Environment.NewLine}" +
                $"  EXE: {whisperCliPath}{Environment.NewLine}" +
                $"  MODEL: {modelPath}{Environment.NewLine}" +
                $"  WAV: {wavPath}{Environment.NewLine}" +
                $"  ARGS: {args}{Environment.NewLine}");

            var psi = new ProcessStartInfo
            {
                FileName = whisperCliPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(whisperCliPath) ?? AppContext.BaseDirectory
            };

            using var process = new Process { StartInfo = psi };

            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stdOut.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stdErr.AppendLine(e.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start whisper-cli.");

            AppendWhisperRunnerLog(
                $"{DateTime.Now:HH:mm:ss.fff} PROCESS STARTED pid={process.Id}{Environment.NewLine}");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch
                {
                }

                AppendWhisperRunnerLog(
                    $"{DateTime.Now:HH:mm:ss.fff} TIMEOUT{Environment.NewLine}" +
                    $"STDOUT:{Environment.NewLine}{stdOut}{Environment.NewLine}" +
                    $"STDERR:{Environment.NewLine}{stdErr}{Environment.NewLine}");

                throw new TimeoutException(
                    "whisper-cli did not finish within 30 seconds." + Environment.NewLine +
                    "STDOUT:" + Environment.NewLine + stdOut + Environment.NewLine +
                    "STDERR:" + Environment.NewLine + stdErr);
            }

            AppendWhisperRunnerLog(
                $"{DateTime.Now:HH:mm:ss.fff} EXIT CODE {process.ExitCode}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{stdOut}{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{stdErr}{Environment.NewLine}");

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"whisper-cli exited with code {process.ExitCode}.{Environment.NewLine}" +
                    $"STDOUT:{Environment.NewLine}{stdOut}{Environment.NewLine}" +
                    $"STDERR:{Environment.NewLine}{stdErr}");
            }

            return NormalizeTranscript(stdOut.ToString());
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