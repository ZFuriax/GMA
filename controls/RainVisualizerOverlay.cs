// Controls/RainVisualizerOverlay.cs
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MusicPlayer.Controls
{
    /// <summary>
    /// LED-matrix spectrum overlay with glow:
    /// - Instant attack + smooth fall
    /// - Log-frequency mapping across the width
    /// - Optional high-frequency cutoff + stretch
    /// - AGC (auto gain control) to avoid constant maxing
    /// - Per-pixel glow (multi-pass halo)
    /// - Optional peak-hold "cap" pixel
    /// </summary>
    public sealed class RainVisualizerOverlay : FrameworkElement
    {
        private readonly DispatcherTimer _timer;
        private readonly object _lock = new();

        private float[]? _bands;

        private double[]? _levels;     // 0..1
        private double[]? _peakCaps;   // 0..1 (peak hold)

        private double _agcPeak = 0.20;
        private double _currentGain = 1.0;
        // Loudness-following (makes bars shrink when the song gets quieter)
        private double _rmsSmoothed = 0.0;
        private double _rmsRef = 0.15;        // slowly-tracked "typical loud" RMS reference
        private double _loudnessFactor = 1.0; // 0..1 applied on top of peak AGC

        public double LoudnessAttack { get; set; } = 0.25;   // how fast it reacts to getting louder
        public double LoudnessRelease { get; set; } = 0.03;  // how slow it reacts to getting quieter
        public double LoudnessFloor { get; set; } = 0.05;    // keep some minimum energy (prevents near-black on silence)
        public double LoudnessExponent { get; set; } = 0.65; // curve: lower -> more shrink on quiet parts

        private DateTime _lastTick = DateTime.UtcNow;

        // ----------------------------
        // Visual / behavior tuning
        // ----------------------------

        /// <summary>How many columns you WANT. Actual columns clamp to available width.</summary>
        public int Columns { get; set; } = 73;

        /// <summary>Use only first X% of bins, then stretch over full width (0..1).</summary>
        public double CutoffPercent { get; set; } = 0.65;

        /// <summary>
        /// NEW: Truncate the frequency mapping to only the left portion (e.g. 0.66..0.75),
        /// then stretch that truncated range across the whole visualizer width.
        /// Example: 0.72 = "only map 0..72% of the spectrum, but draw it across 100% width".
        /// </summary>
        public double TruncateAndStretchPercent { get; set; } = 0.72;

        /// <summary>Minimum FFT bin used (avoid DC bin 0).</summary>
        public int MinBin { get; set; } = 1;

        /// <summary>How fast the displayed bar falls (full-scale per second).</summary>
        public double FallSpeed { get; set; } = 2.1;

        /// <summary>Peak cap fall speed (slower feels more classic).</summary>
        public double PeakFallSpeed { get; set; } = 0.65;

        /// <summary>Base gain after AGC (small values recommended).</summary>
        public double Gain { get; set; } = 3.0;

        /// <summary>Gamma compression. &lt;1 makes quiet stuff more visible.</summary>
        public double Gamma { get; set; } = 1.0;

        /// <summary>Target headroom for AGC (bars usually reach this, not 100%).</summary>
        public double TargetHeadroom { get; set; } = 0.85;

        /// <summary>AGC peak smoothing: fast rise.</summary>
        public double AgcAttack { get; set; } = 0.30;

        /// <summary>AGC peak smoothing: slow fall.</summary>
        public double AgcRelease { get; set; } = 0.035;

        /// <summary>Padding inside the overlay.</summary>
        public Thickness PaddingPx { get; set; } = new Thickness(2, 2, 2, 2);

        // LED matrix “pixels”
        public double PixelSizePx { get; set; } = 2.0;
        public double PixelGapPx { get; set; } = 2.2;

        /// <summary>Faint unlit grid opacity (0..1). 0 = no grid.</summary>
        public double UnlitOpacity { get; set; } = 0.06;

        /// <summary>Overall local opacity multiplier (use XAML Opacity too).</summary>
        public double LocalOpacity { get; set; } = 1.0;

        // Glow tuning
        public bool GlowEnabled { get; set; } = true;
        public double GlowStrength { get; set; } = 0.38; // 0..1 (how strong halo is)
        public double GlowRadiusMultiplier { get; set; } = 3.2; // how big halo grows

        // Peak cap
        public bool PeakCapEnabled { get; set; } = true;

        public RainVisualizerOverlay()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };

            _timer.Tick += (_, __) =>
            {
                var now = DateTime.UtcNow;
                var dt = (now - _lastTick).TotalSeconds;
                _lastTick = now;
                if (dt <= 0) dt = 0.016;

                Step(dt);
                InvalidateVisual();
            };

            _timer.Start();
        }

        /// <summary>Feed spectrum/FFT magnitudes here (same array you were already passing).</summary>
        public void SetBands(float[] bands)
        {
            if (bands == null || bands.Length == 0) return;

            var copy = (float[])bands.Clone();
            lock (_lock) _bands = copy;

            // --- AGC: track frame peak, smooth it, compute gain so peaks land near TargetHeadroom ---
            double framePeak = 0.0;

            // Also compute RMS for loudness-following
            double sumSq = 0.0;
            int n = 0;

            for (int i = 0; i < copy.Length; i++)
            {
                double v = copy[i];
                if (v > framePeak) framePeak = v;

                if (v > 0)
                {
                    sumSq += v * v;
                    n++;
                }
            }

            double frameRms = (n > 0) ? Math.Sqrt(sumSq / n) : 0.0;

            // Smooth peak tracking (fast rise, slow fall)
            if (framePeak > _agcPeak)
                _agcPeak += (framePeak - _agcPeak) * AgcAttack;
            else
                _agcPeak += (framePeak - _agcPeak) * AgcRelease;

            double denom = Math.Max(1e-6, _agcPeak);
            double g = TargetHeadroom / denom;

            // IMPORTANT: do NOT boost quiet passages
            _currentGain = Math.Min(1.0, g);

            // --- Loudness-following (only shrinks when quieter) ---
            // Smooth RMS (use attack/release style)
            if (frameRms > _rmsSmoothed)
                _rmsSmoothed += (frameRms - _rmsSmoothed) * LoudnessAttack;
            else
                _rmsSmoothed += (frameRms - _rmsSmoothed) * LoudnessRelease;

            // Track a "typical loud" reference slowly (rises faster, falls very slowly)
            if (_rmsSmoothed > _rmsRef)
                _rmsRef = _rmsRef + (_rmsSmoothed - _rmsRef) * 0.10;
            else
                _rmsRef = _rmsRef + (_rmsSmoothed - _rmsRef) * 0.005;

            double refSafe = Math.Max(1e-6, _rmsRef);
            double ratio = _rmsSmoothed / refSafe; // ~1 at "typical loud", <1 when quieter

            // Map ratio -> 0..1 with a floor and curve
            double raw = (ratio - LoudnessFloor) / Math.Max(1e-6, (1.0 - LoudnessFloor));
            raw = Clamp01(raw);
            _loudnessFactor = Math.Pow(raw, LoudnessExponent);
        }

        private void Step(double dt)
        {
            double usableW = Math.Max(0, ActualWidth - PaddingPx.Left - PaddingPx.Right);
            double cellStepX = PixelSizePx + PixelGapPx;

            // how many columns actually fit right now
            int colsByWidth = (int)Math.Floor((usableW + PixelGapPx) / cellStepX);

            // choose the smaller of “desired Columns” and what fits,
            // BUT if Columns is too small, let it grow to fill width
            int wantCols = Math.Max(8, Math.Max(Columns, colsByWidth));

            if (_levels == null || _levels.Length != wantCols)
                _levels = new double[wantCols];

            if (_peakCaps == null || _peakCaps.Length != wantCols)
                _peakCaps = new double[wantCols];

            float[]? bands;
            lock (_lock) bands = _bands;

            if (bands == null || bands.Length < 2)
            {
                // no data yet: decay
                for (int i = 0; i < _levels.Length; i++)
                {
                    _levels[i] = Math.Max(0, _levels[i] - FallSpeed * dt);
                    _peakCaps[i] = Math.Max(0, _peakCaps[i] - PeakFallSpeed * dt);
                }
                return;
            }

            // cutoff
            int maxBin = Math.Max(MinBin + 1, (int)Math.Floor(bands.Length * Clamp01(CutoffPercent)) - 1);
            maxBin = Math.Min(maxBin, bands.Length - 1);

            // NEW: truncate range of t used for bin mapping, but still draw across full width
            double tScale = Clamp01(TruncateAndStretchPercent);
            if (tScale < 0.01) tScale = 0.01;

            for (int c = 0; c < _levels.Length; c++)
            {
                // Use band edges (each column covers a range of bins)
                double t0 = (c / (double)_levels.Length);
                double t1 = ((c + 1) / (double)_levels.Length);

                //double amp = Math.Max(0.0, bands[b0]);

                int b0 = LogMapBin(t0, MinBin, maxBin);
                int b1 = LogMapBin(t1, MinBin, maxBin);

                // Ensure order
                if (b1 < b0) { var tmp = b0; b0 = b1; b1 = tmp; }

                // Slightly widen the sampled neighborhood so fast hits are less likely to be missed
                int radius = (c < 12) ? 2 : 1; // a little wider in the bass / low mids
                int start = Math.Max(MinBin, b0 - radius);
                int end = Math.Min(maxBin, b1 + radius);

                double peak = 0.0;
                double sum = 0.0;
                int count = 0;

                for (int i = start; i <= end; i++)
                {
                    double v = Math.Max(0.0, bands[i]);
                    if (v > peak) peak = v;
                    sum += v;
                    count++;
                }

                double avg = count > 0 ? (sum / count) : 0.0;

                // Hybrid: keep transient punch from peak, but stabilize with a little average
                double amp = Math.Max(peak, avg * 1.20);

                // Apply AGC + user gain
                amp *= _currentGain;
                //amp *= _loudnessFactor;
                //amp *= (0.65 + 0.35 * _loudnessFactor); // softer quiet-part shrink
                amp *= Gain;

                // Clamp, then gamma (compress range)
                amp = Clamp01(amp);
                amp = Math.Pow(amp, Gamma);

                // Instant attack + smooth fall
                if (amp >= _levels[c])
                    _levels[c] = amp;
                else
                    _levels[c] = Math.Max(0, _levels[c] - FallSpeed * dt);

                // Peak cap (hold the peak, then fall slowly)
                if (PeakCapEnabled)
                {
                    if (_levels[c] >= _peakCaps[c])
                        _peakCaps[c] = _levels[c];
                    else
                        _peakCaps[c] = Math.Max(0, _peakCaps[c] - PeakFallSpeed * dt);
                }
                else
                {
                    _peakCaps[c] = 0;
                }
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 2 || h <= 2) return;

            if (_levels == null || _levels.Length == 0) return;

            // Layout
            double usableW = Math.Max(0, w - PaddingPx.Left - PaddingPx.Right);
            double usableH = Math.Max(0, h - PaddingPx.Top - PaddingPx.Bottom);

            double cellStepX = PixelSizePx + PixelGapPx;
            double cellStepY = PixelSizePx + PixelGapPx;

            // How many columns/rows we can actually render given pixel sizing
            int maxColsByWidth = (int)Math.Floor((usableW + PixelGapPx) / cellStepX);
            int cols = Math.Max(8, Math.Min(_levels.Length, Math.Max(8, maxColsByWidth)));

            int rows = (int)Math.Floor((usableH + PixelGapPx) / cellStepY);
            if (rows <= 0) return;

            // Unlit grid brush
            byte unlitA = (byte)(Clamp01(UnlitOpacity * LocalOpacity) * 255);
            var unlitBrush = new SolidColorBrush(Color.FromArgb(unlitA, 255, 255, 255));
            unlitBrush.Freeze();

            // For each column
            for (int c = 0; c < cols; c++)
            {
                double level = Clamp01(_levels[c]);

                int litRows = (int)Math.Round(level * rows);
                if (litRows < 0) litRows = 0;
                if (litRows > rows) litRows = rows;

                // peak cap row index
                int capRow = -1;
                if (PeakCapEnabled && _peakCaps != null)
                {
                    capRow = (int)Math.Round(Clamp01(_peakCaps[c]) * rows) - 1;
                    if (capRow < 0) capRow = 0;
                    if (capRow >= rows) capRow = rows - 1;
                }

                double x = PaddingPx.Left + c * cellStepX;

                // Hue by frequency index (left warm -> right cool)
                Color baseColor = ColorForIndexGifStyle(c, cols);

                // Draw bottom-up rows
                for (int r = 0; r < rows; r++)
                {
                    bool isLit = r < litRows;

                    // y position (bottom row is r=0)
                    double y = PaddingPx.Top + (rows - 1 - r) * cellStepY;

                    Rect cell = new Rect(x, y, PixelSizePx, PixelSizePx);

                    if (!isLit)
                    {
                        dc.DrawRectangle(unlitBrush, null, cell);
                        continue;
                    }

                    // Fade-to-black as you go down from the *top* of the lit bar.
                    // r is 0..rows-1 (bottom-up), litRows is how many pixels are lit from the bottom.
                    int topLitRow = Math.Max(0, litRows - 1);

                    // 0 at the top of the lit stack, 1 at the bottom of the lit stack
                    double downFromTop = (topLitRow <= 0) ? 0.0 : (topLitRow - r) / (double)topLitRow;
                    downFromTop = Clamp01(downFromTop);

                    // Shape the fade (higher power = brighter top, darker bottom)
                    double fade = Math.Pow(1.0 - downFromTop, 2.2);

                    // Base brightness still follows overall column level, but the fade dominates the look.
                    double bright = Clamp01((0.10 + 0.90 * level) * (0.25 + 0.75 * fade));

                    // Core color
                    byte aCore = (byte)(Clamp01(LocalOpacity) * 255);
                    Color core = Color.FromArgb(
                        aCore,
                        (byte)ClampToByte(baseColor.R * bright),
                        (byte)ClampToByte(baseColor.G * bright),
                        (byte)ClampToByte(baseColor.B * bright));

                    // Optional peak cap pixel: draw it “whiter”
                    bool isCap = PeakCapEnabled && (r == capRow);
                    if (isCap)
                    {
                        // push toward white but keep hue
                        core = Color.FromArgb(
                            aCore,
                            (byte)ClampToByte(core.R * 0.65 + 255 * 0.35),
                            (byte)ClampToByte(core.G * 0.65 + 255 * 0.35),
                            (byte)ClampToByte(core.B * 0.65 + 255 * 0.35));
                    }

                    // Glow halo (multi-pass rectangles)
                    if (GlowEnabled)
                    {
                        DrawGlow(dc, cell, core, level);
                    }

                    // Core pixel
                    var coreBrush = new SolidColorBrush(core);
                    coreBrush.Freeze();
                    dc.DrawRectangle(coreBrush, null, cell);
                }
            }
        }

        private void DrawGlow(DrawingContext dc, Rect cell, Color core, double level)
        {
            // Simulate blur by drawing a few larger rectangles with decreasing alpha
            // (fast and looks like LED bloom)
            double strength = Clamp01(GlowStrength * (0.35 + 0.65 * level) * LocalOpacity);
            if (strength <= 0) return;

            // Base alpha for halo
            byte a0 = (byte)ClampToByte(core.A * strength);

            // Slightly shift halo color toward the core hue, but soften it
            Color halo = Color.FromArgb(
                a0,
                (byte)ClampToByte(core.R * 0.92),
                (byte)ClampToByte(core.G * 0.92),
                (byte)ClampToByte(core.B * 0.92));

            // 3-pass halo
            // Each pass expands and drops alpha
            for (int pass = 0; pass < 3; pass++)
            {
                double t = pass / 2.0; // 0, 0.5, 1
                double expand = (PixelSizePx * 0.25 + PixelGapPx * 0.30) * (1.0 + t * GlowRadiusMultiplier);

                byte a = (byte)ClampToByte(halo.A * (1.0 - 0.45 * pass));
                if (a <= 0) continue;

                var b = new SolidColorBrush(Color.FromArgb(a, halo.R, halo.G, halo.B));
                b.Freeze();

                Rect r = new Rect(
                    cell.X - expand,
                    cell.Y - expand,
                    cell.Width + expand * 2,
                    cell.Height + expand * 2);

                dc.DrawRectangle(b, null, r);
            }
        }

        // ----------------------------
        // Helpers
        // ----------------------------

        private static int LogMapBin(double t, int minBin, int maxBin)
        {
            t = Clamp01(t);

            double lo = Math.Log(Math.Max(1, minBin));
            double hi = Math.Log(Math.Max(minBin + 1, maxBin));
            double v = Math.Exp(lo + t * (hi - lo));

            int bin = (int)Math.Round(v);
            if (bin < minBin) bin = minBin;
            if (bin > maxBin) bin = maxBin;
            return bin;
        }

        private static double SampleNeighborhood(float[] bands, int center, int radius)
        {
            int start = Math.Max(0, center - radius);
            int end = Math.Min(bands.Length - 1, center + radius);

            double sum = 0;
            int n = 0;
            for (int i = start; i <= end; i++)
            {
                double v = bands[i];
                if (v < 0) v = 0;
                sum += v;
                n++;
            }
            return n == 0 ? 0 : sum / n;
        }

        // Color mapping similar to many LED analyzers:
        // low = red/orange, mid = green, high = cyan/blue.
        private static Color ColorForIndexGifStyle(int i, int n)
        {
            if (n <= 1) return Color.FromRgb(255, 64, 64);
            double t = i / (double)(n - 1);

            // We'll sweep hue from ~0° (red) to ~210° (blue)
            // but keep saturation high to feel like LEDs.
            double hue = 210.0 * t; // 0..210
            return HsvToRgb(hue, 0.95, 1.0);
        }

        // HSV->RGB (0-360, 0-1, 0-1)
        private static Color HsvToRgb(double h, double s, double v)
        {
            h = (h % 360 + 360) % 360;
            s = Clamp01(s);
            v = Clamp01(v);

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            byte r = (byte)ClampToByte((r1 + m) * 255);
            byte g = (byte)ClampToByte((g1 + m) * 255);
            byte b = (byte)ClampToByte((b1 + m) * 255);

            return Color.FromRgb(r, g, b);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private static double ClampToByte(double v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }
    }
}