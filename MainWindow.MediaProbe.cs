using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        // ---------- Waveform analysis ----------
        private readonly Dictionary<string, double[]> _waveCache = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _waveCts;
        private string? _waveRequestedPath;

        // ---------- Metadata probing ----------
        private string? ProbeAlbumForUi(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                using var f = TagLib.File.Create(filePath);
                var album = f.Tag?.Album;
                return string.IsNullOrWhiteSpace(album) ? null : album.Trim();
            }
            catch
            {
                return null;
            }
        }

        private TimeSpan? ProbeDurationForUi(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var ffprobe = Path.Combine(baseDir, "ffprobe.exe");

            try
            {
                if (File.Exists(ffprobe))
                {
                    using var p = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = ffprobe,
                            Arguments = $"-v error -show_entries format=duration -of default=nk=1:nw=1 {Quote(filePath)}",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };

                    p.Start();

                    string output = p.StandardOutput.ReadToEnd().Trim();

                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(true); } catch { }
                        return null;
                    }

                    if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
                        return TimeSpan.FromSeconds(seconds);
                }
            }
            catch { }

            return null;
        }

        // ---------- Waveform ----------
        private async Task EnsureWaveformAsync(string filePath)
        {
            if (_waveCache.TryGetValue(filePath, out var cached))
            {
                if (string.Equals(_waveRequestedPath, filePath, StringComparison.OrdinalIgnoreCase))
                    WaveformBar.Peaks = cached;

                return;
            }

            _waveCts?.Cancel();
            _waveCts = new CancellationTokenSource();
            var ct = _waveCts.Token;

            if (string.Equals(_waveRequestedPath, filePath, StringComparison.OrdinalIgnoreCase))
                WaveformBar.Peaks = null;

            try
            {
                var peaks = await Task.Run(() => BuildPeaksWithFfmpeg(filePath, ct), ct);

                if (ct.IsCancellationRequested) return;

                _waveCache[filePath] = peaks;

                if (string.Equals(_waveRequestedPath, filePath, StringComparison.OrdinalIgnoreCase))
                    WaveformBar.Peaks = peaks;
            }
            catch
            {
                // ignore
            }
        }

        private static double[] BuildPeaksWithFfmpeg(string filePath, CancellationToken ct)
        {
            const int targetPeaks = 2500;

            string? ffmpeg = ResolveFfmpegPath();
            if (ffmpeg == null)
                return Array.Empty<double>();

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments =
                    "-hide_banner -loglevel error -nostdin " +
                    $"-i {Quote(filePath)} " +
                    "-vn -sn -dn " +
                    "-ac 1 -ar 8000 -f s16le pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = new Process { StartInfo = psi };
            p.Start();

            using var stdout = p.StandardOutput.BaseStream;

            _ = Task.Run(async () =>
            {
                try { await p.StandardError.ReadToEndAsync(); } catch { }
            }, ct);

            var peaks = new double[targetPeaks];
            var samples = new List<short>(8000 * 60);
            byte[] buf = new byte[64 * 1024];

            while (!ct.IsCancellationRequested)
            {
                int n = stdout.Read(buf, 0, buf.Length);
                if (n <= 0) break;

                int sampleCount = n / 2;
                for (int i = 0; i < sampleCount; i++)
                {
                    short s = (short)(buf[i * 2] | (buf[i * 2 + 1] << 8));
                    samples.Add(s);
                }

                if (samples.Count > 8000 * 60 * 15)
                    break;
            }

            try
            {
                if (!p.HasExited)
                    p.WaitForExit(2000);
            }
            catch { }

            if (samples.Count < 100)
                return Array.Empty<double>();

            int total = samples.Count;
            for (int i = 0; i < targetPeaks; i++)
            {
                int start = (int)((long)i * total / targetPeaks);
                int end = (int)((long)(i + 1) * total / targetPeaks);
                if (end <= start) end = start + 1;
                if (end > total) end = total;

                int max = 0;
                for (int j = start; j < end; j++)
                {
                    int v = samples[j];
                    int a = (v == short.MinValue) ? 32768 : Math.Abs(v);
                    if (a > max) max = a;
                }

                peaks[i] = Math.Clamp(max / 32768.0, 0.0, 1.0);
            }

            return peaks;
        }

        // ---------- ffmpeg helpers ----------
        private static string? ResolveFfmpegPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidate = Path.Combine(baseDir, "ffmpeg.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}