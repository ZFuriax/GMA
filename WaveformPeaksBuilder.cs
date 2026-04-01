// WaveformPeaksBuilder.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace MusicPlayer
{
    public static class WaveformPeaksBuilder
    {
        public static double[] BuildPeaksViaFfmpeg(
            string audioPath,
            CancellationToken token,
            int sampleRate = 8000,
            int windowSamples = 1024,
            int maxPeaks = 20000)
        {
            if (!File.Exists(audioPath))
                return Array.Empty<double>();

            string ffmpeg = ResolveFfmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpeg))
                throw new FileNotFoundException("ffmpeg.exe not found. Place ffmpeg.exe next to the app or add it to PATH.");

            // Keep ffmpeg quiet and stream raw PCM to stdout
            string args =
                $"-hide_banner -nostats -nostdin -v error -i \"{audioPath}\" -f s16le -ac 1 -ar {sampleRate} pipe:1";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,

                // IMPORTANT: don't redirect stderr to avoid deadlocks
                RedirectStandardError = false,

                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return Array.Empty<double>();

            // ✅ Lower ffmpeg priority so it doesn't compete with audio playback
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* ignore */ }

            // ✅ Lower current thread priority (the caller should run this on a background worker thread)
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { /* ignore */ }

            using var stdout = proc.StandardOutput.BaseStream;

            var peaks = new List<double>(capacity: Math.Min(maxPeaks, 4096));

            byte[] buffer = new byte[64 * 1024];
            int bytesInCarry = 0;
            byte[] carry = new byte[2];

            int samplesInWindow = 0;
            double maxAbsInWindow = 0;

            int read;
            int sampleCounter = 0;

            while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (token.IsCancellationRequested)
                    break;

                int offset = 0;

                if (bytesInCarry == 1 && read > 0)
                {
                    carry[1] = buffer[0];
                    short s = (short)(carry[0] | (carry[1] << 8));
                    ProcessSample(s, ref samplesInWindow, ref maxAbsInWindow, peaks, windowSamples, maxPeaks);
                    offset = 1;
                    bytesInCarry = 0;
                }

                int usable = read - offset;
                int sampleBytes = usable & ~1;

                for (int i = 0; i < sampleBytes; i += 2)
                {
                    if (token.IsCancellationRequested)
                        break;

                    int idx = offset + i;
                    short s = (short)(buffer[idx] | (buffer[idx + 1] << 8));
                    ProcessSample(s, ref samplesInWindow, ref maxAbsInWindow, peaks, windowSamples, maxPeaks);

                    if (peaks.Count >= maxPeaks)
                        break;

                    // ✅ Periodic yield: lets the audio thread keep up on weaker CPUs
                    sampleCounter++;
                    if ((sampleCounter & 0x3FFF) == 0) // every 16384 samples
                        Thread.Sleep(0);
                }

                if (token.IsCancellationRequested || peaks.Count >= maxPeaks)
                    break;

                if ((usable & 1) == 1)
                {
                    carry[0] = buffer[offset + sampleBytes];
                    bytesInCarry = 1;
                }
            }

            if (samplesInWindow > 0 && peaks.Count < maxPeaks)
                peaks.Add(Clamp01(maxAbsInWindow));

            // Let ffmpeg exit; then kill if needed
            try
            {
                if (!proc.WaitForExit(250))
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            // Normalize peaks to max=1 for consistent rendering
            double max = 0;
            for (int i = 0; i < peaks.Count; i++)
                if (peaks[i] > max) max = peaks[i];

            if (max > 0.000001)
            {
                for (int i = 0; i < peaks.Count; i++)
                    peaks[i] = peaks[i] / max;
            }

            return peaks.ToArray();
        }

        private static void ProcessSample(
            short sample,
            ref int samplesInWindow,
            ref double maxAbsInWindow,
            List<double> peaks,
            int windowSamples,
            int maxPeaks)
        {
            // ✅ Critical fix:
            // Math.Abs(short.MinValue) throws OverflowException.
            // Convert to int before abs (or clamp explicitly).
            int si = sample;
            int absInt = (si == short.MinValue) ? 32768 : Math.Abs(si);
            double abs = absInt / 32768.0;

            if (abs > maxAbsInWindow) maxAbsInWindow = abs;

            samplesInWindow++;
            if (samplesInWindow >= windowSamples)
            {
                peaks.Add(Clamp01(maxAbsInWindow));
                samplesInWindow = 0;
                maxAbsInWindow = 0;

                if (peaks.Count >= maxPeaks)
                    return;
            }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private static string ResolveFfmpegPath()
        {
            try
            {
                // Works for single-file publishes too (unlike Assembly.Location)
                string exeDir = AppContext.BaseDirectory;

                string local = Path.Combine(exeDir, "ffmpeg.exe");
                if (File.Exists(local))
                    return local;
            }
            catch { }

            // Fall back to PATH
            return "ffmpeg.exe";
        }
    }
}