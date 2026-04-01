// Controls/WaveformProgressBar.cs
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicPlayer.Controls
{
    public sealed class WaveformProgressBar : FrameworkElement
    {
        public static readonly DependencyProperty ProgressProperty =
            DependencyProperty.Register(
                nameof(Progress),
                typeof(double),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LoopAProperty =
            DependencyProperty.Register(
                nameof(LoopA),
                typeof(double),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LoopBProperty =
            DependencyProperty.Register(
                nameof(LoopB),
                typeof(double),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LoopEnabledProperty =
            DependencyProperty.Register(
                nameof(LoopEnabled),
                typeof(bool),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PeaksProperty =
            DependencyProperty.Register(
                nameof(Peaks),
                typeof(double[]),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsOverviewProperty =
            DependencyProperty.Register(
                nameof(IsOverview),
                typeof(bool),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsZoomedProperty =
            DependencyProperty.Register(
                nameof(IsZoomed),
                typeof(bool),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BackgroundProperty =
            DependencyProperty.Register(
                nameof(Background),
                typeof(Brush),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(
                    Brushes.Transparent,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BorderBrushProperty =
            DependencyProperty.Register(
                nameof(BorderBrush),
                typeof(Brush),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(
                    new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BorderThicknessProperty =
            DependencyProperty.Register(
                nameof(BorderThickness),
                typeof(double),
                typeof(WaveformProgressBar),
                new FrameworkPropertyMetadata(
                    1.0,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Background
        {
            get => (Brush)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public Brush BorderBrush
        {
            get => (Brush)GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        public double BorderThickness
        {
            get => (double)GetValue(BorderThicknessProperty);
            set => SetValue(BorderThicknessProperty, value);
        }

        public double Progress
        {
            get => Clamp01((double)GetValue(ProgressProperty));
            set => SetValue(ProgressProperty, Clamp01(value));
        }

        public double LoopA
        {
            get => Clamp01((double)GetValue(LoopAProperty));
            set => SetValue(LoopAProperty, Clamp01(value));
        }

        public double LoopB
        {
            get => Clamp01((double)GetValue(LoopBProperty));
            set => SetValue(LoopBProperty, Clamp01(value));
        }

        public bool LoopEnabled
        {
            get => (bool)GetValue(LoopEnabledProperty);
            set => SetValue(LoopEnabledProperty, value);
        }

        public double[]? Peaks
        {
            get => (double[]?)GetValue(PeaksProperty);
            set => SetValue(PeaksProperty, value);
        }

        public bool IsOverview
        {
            get => (bool)GetValue(IsOverviewProperty);
            set => SetValue(IsOverviewProperty, value);
        }

        public bool IsZoomed
        {
            get => (bool)GetValue(IsZoomedProperty);
            set => SetValue(IsZoomedProperty, value);
        }

        public event Action<double>? SeekRequested;
        public event Action<double>? LoopARequested;
        public event Action<double>? LoopBRequested;

        public event Action? ScrubStarted;
        public event Action? ScrubCompleted;

        private enum DragMode { None, Progress, A, B }
        private DragMode _dragMode = DragMode.None;

        // ---- Chevron handles lane (above waveform) ----
        private const double HandleLaneHeight = 12.0;   // pixels inside inner rect, at top
        private const double HandleHitSlopPx = 10.0;    // horizontal click tolerance for grabbing handle
        private const double MinGap = 0.001;            // keep A and B from collapsing

        public WaveformProgressBar()
        {
            SnapsToDevicePixels = true;
            Focusable = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 2 || h <= 2) return;

            // Background + border (background optional so we can layer a visualizer behind)
            Brush bg = Background ?? Brushes.Transparent;
            Brush bb = BorderBrush;
            double bt = Math.Max(0, BorderThickness);

            Pen? borderPen = null;
            if (bt > 0 && bb != null)
                borderPen = new Pen(bb, bt);

            // Always draw the full control rect, even if background is transparent.
            // This makes the entire control hit-testable, not just the waveform lines.
            dc.DrawRoundedRectangle(
                bg,
                borderPen,
                new Rect(0, 0, w, h),
                6, 6);

            Rect inner = new Rect(6, 6, Math.Max(0, w - 12), Math.Max(0, h - 12));
            if (inner.Width <= 2 || inner.Height <= 2) return;

            GetLayoutRects(inner, out Rect lane, out Rect wave);
            if (wave.Width <= 2 || wave.Height <= 2) return;

            // Waveform (NO changes to how peaks are interpreted; only the draw rect is the waveform area)
            DrawWaveform(dc, wave);

            // Played overlay
            double playedW = wave.Width * Progress;
            if (playedW > 0)
            {
                dc.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(40, 140, 220, 255)),
                    null,
                    new Rect(wave.X, wave.Y, playedW, wave.Height));
            }

            // Loop region shading (only when enabled)
            double a = LoopA;
            double b = LoopB;
            if (LoopEnabled && b > a)
            {
                double x1 = wave.X + wave.Width * Clamp01(a);
                double x2 = wave.X + wave.Width * Clamp01(b);
                dc.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(45, 120, 255, 140)),
                    null,
                    new Rect(x1, wave.Y, x2 - x1, wave.Height));
            }

            // Loop handles: chevrons above the waveform (ONLY when enabled)
            if (LoopEnabled)
            {
                DrawChevronHandle(dc, lane, a, isA: true);
                DrawChevronHandle(dc, lane, b, isA: false);
            }

            // Playhead (in waveform area)
            DrawPlayhead(dc, wave, Progress);
        }

        private static void GetLayoutRects(Rect inner, out Rect lane, out Rect wave)
        {
            // Reserve a small top "lane" for chevrons; keep it proportional if the control is tiny.
            double laneH = Math.Min(HandleLaneHeight, inner.Height * 0.35);
            laneH = Math.Max(0, Math.Min(laneH, inner.Height));

            lane = new Rect(inner.X, inner.Y, inner.Width, laneH);
            wave = new Rect(inner.X, inner.Y + laneH, inner.Width, Math.Max(0, inner.Height - laneH));
        }

        public double GetLaneHeight()
        {
            double innerHeight = Math.Max(0, ActualHeight - 12); // matches inner rect
            double laneH = Math.Min(HandleLaneHeight, innerHeight * 0.35);
            return laneH;
        }

        private void DrawWaveform(DrawingContext dc, Rect r)
        {
            var peaks = Peaks;
            if (peaks == null || peaks.Length < 8)
            {
                double midY = r.Y + r.Height / 2;
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 90)), 1);

                int bars = (int)Math.Max(24, r.Width / 10);
                for (int i = 0; i < bars; i++)
                {
                    double t = i / (double)(bars - 1);
                    double amp = 0.15 + 0.20 * Math.Abs(Math.Sin(t * Math.PI * 6));
                    double x = r.X + t * r.Width;
                    double dy = amp * (r.Height * 0.45);
                    dc.DrawLine(pen, new Point(x, midY - dy), new Point(x, midY + dy));
                }
                return;
            }

            int n = peaks.Length;

            double mid = r.Y + r.Height / 2;
            var penWave = new Pen(new SolidColorBrush(Color.FromRgb(110, 110, 110)), 1);

            int px = (int)Math.Max(1, r.Width);

            // ✅ Key fix:
            // Stretch/resample peaks across the full pixel width so the waveform always spans the whole song.
            // For each pixel column, compute the corresponding range of peak indices.
            double peaksPerPixel = n / (double)px;

            for (int x = 0; x < px; x++)
            {
                double startF = x * peaksPerPixel;
                double endF = (x + 1) * peaksPerPixel;

                int i0 = (int)Math.Floor(startF);
                int i1 = (int)Math.Ceiling(endF);

                if (i0 < 0) i0 = 0;
                if (i1 <= i0) i1 = i0 + 1;
                if (i1 > n) i1 = n;

                double p = 0;
                for (int i = i0; i < i1; i++)
                    p = Math.Max(p, Clamp01(peaks[i]));

                double dy = p * (r.Height * 0.48);
                double xx = r.X + x + 0.5;
                dc.DrawLine(penWave, new Point(xx, mid - dy), new Point(xx, mid + dy));
            }
        }

        private static void DrawPlayhead(DrawingContext dc, Rect r, double progress)
        {
            double x = r.X + r.Width * Clamp01(progress);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(220, 220, 220)), 1);
            dc.DrawLine(pen, new Point(x, r.Y), new Point(x, r.Y + r.Height));
        }

        private static void DrawChevronHandle(DrawingContext dc, Rect lane, double t, bool isA)
        {
            double x = lane.X + lane.Width * Clamp01(t);
            double top = lane.Y;
            double bottom = lane.Y + lane.Height;

            // A = green, B = amber
            Brush brush = isA
                ? new SolidColorBrush(Color.FromArgb(235, 140, 255, 140))
                : new SolidColorBrush(Color.FromArgb(235, 255, 220, 140));

            // Downward triangle/chevron
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                // Point down at the bottom center
                ctx.BeginFigure(new Point(x, bottom), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(x - 6, top), true, false);
                ctx.LineTo(new Point(x + 6, top), true, false);
            }
            geo.Freeze();

            dc.DrawGeometry(brush, null, geo);

            // Optional tiny stem line (helps visually connect to waveform)
            var pen = new Pen(brush, 1);
            dc.DrawLine(pen, new Point(x, bottom), new Point(x, bottom + 4));
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            CaptureMouse();

            Point pos = e.GetPosition(this);
            double t = XToT(pos.X);

            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

            // Layout to determine whether user clicked in the chevron lane or waveform lane
            Rect inner = new Rect(6, 6, Math.Max(0, ActualWidth - 12), Math.Max(0, ActualHeight - 12));
            GetLayoutRects(inner, out Rect lane, out Rect wave);

            bool inLane = pos.Y >= lane.Y && pos.Y <= lane.Y + lane.Height;

            // SHIFT / CTRL shortcuts still supported (do not affect waveform generation)
            if (LoopEnabled && shift)
            {
                _dragMode = DragMode.A;
                SetLoopA(t);
                ScrubStarted?.Invoke();
                InvalidateVisual();
                return;
            }

            if (LoopEnabled && ctrl)
            {
                _dragMode = DragMode.B;
                SetLoopB(t);
                ScrubStarted?.Invoke();
                InvalidateVisual();
                return;
            }

            // If Loop is enabled and click is in the lane, drag a handle (never scrub)
            if (LoopEnabled && inLane)
            {
                double aX = TToX(LoopA);
                double bX = TToX(LoopB);

                double da = Math.Abs(pos.X - aX);
                double db = Math.Abs(pos.X - bX);

                if (da <= HandleHitSlopPx && db <= HandleHitSlopPx)
                    _dragMode = da <= db ? DragMode.A : DragMode.B;
                else if (da <= HandleHitSlopPx)
                    _dragMode = DragMode.A;
                else if (db <= HandleHitSlopPx)
                    _dragMode = DragMode.B;
                else
                    _dragMode = da <= db ? DragMode.A : DragMode.B; // click lane: grab nearest handle

                ScrubStarted?.Invoke();

                if (_dragMode == DragMode.A) SetLoopA(t);
                else if (_dragMode == DragMode.B) SetLoopB(t);

                InvalidateVisual();
                return;
            }

            // Otherwise, scrub/seek in waveform lane
            _dragMode = DragMode.Progress;

            ScrubStarted?.Invoke();

            Progress = t;
            SeekRequested?.Invoke(Progress);
            InvalidateVisual();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!IsMouseCaptured || _dragMode == DragMode.None) return;

            Point pos = e.GetPosition(this);
            double t = XToT(pos.X);

            bool fine = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            if (fine)
                t = Lerp(GetCurrentDragValue(), t, 0.15);

            switch (_dragMode)
            {
                case DragMode.Progress:
                    Progress = t;
                    SeekRequested?.Invoke(Progress);
                    break;

                case DragMode.A:
                    SetLoopA(t);
                    break;

                case DragMode.B:
                    SetLoopB(t);
                    break;
            }

            InvalidateVisual();
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag(raiseCompleted: true);
        }

        private void EndDrag(bool raiseCompleted)
        {
            if (IsMouseCaptured)
                ReleaseMouseCapture();

            _dragMode = DragMode.None;

            if (raiseCompleted)
                ScrubCompleted?.Invoke();
        }

        private double GetCurrentDragValue()
        {
            return _dragMode switch
            {
                DragMode.A => LoopA,
                DragMode.B => LoopB,
                _ => Progress
            };
        }

        private void SetLoopA(double t)
        {
            t = Clamp01(t);
            double b = Clamp01(LoopB);
            LoopA = Math.Min(t, b - MinGap);
            LoopARequested?.Invoke(LoopA);
        }

        private void SetLoopB(double t)
        {
            t = Clamp01(t);
            double a = Clamp01(LoopA);
            LoopB = Math.Max(t, a + MinGap);
            LoopBRequested?.Invoke(LoopB);
        }

        private double XToT(double x)
        {
            double w = Math.Max(1, ActualWidth - 12);
            const double innerX = 6;
            return Clamp01((x - innerX) / w);
        }

        private double TToX(double t)
        {
            double w = Math.Max(1, ActualWidth - 12);
            const double innerX = 6;
            return innerX + Clamp01(t) * w;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}