using System;
using System.Windows;
using System.Windows.Input;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        // ---------- A-B loop behavior ----------
        private const double LoopEpsilon = 0.003;
        private bool _abCrossfadeArmed = false;

        // ---------- Loop marker dragging ----------
        private enum LoopHandle { None, A, B }
        private LoopHandle _draggingHandle = LoopHandle.None;
        private const double LoopHandleHitSlopPx = 10.0;

        private void WireLoopUiEvents()
        {
            WaveformBar.LoopARequested += frac =>
            {
                WaveformBar.LoopA = Math.Clamp(frac, 0.0, 1.0);
                LogTransport("LoopARequested", $"loopA={WaveformBar.LoopA:0.0000}");
                RequestSaveState();
            };

            WaveformBar.LoopBRequested += frac =>
            {
                WaveformBar.LoopB = Math.Clamp(frac, 0.0, 1.0);
                LogTransport("LoopBRequested", $"loopB={WaveformBar.LoopB:0.0000}");
                RequestSaveState();
            };
        }

        private LoopHandle HitTestLoopHandle(Point p)
        {
            if (!WaveformBar.LoopEnabled)
                return LoopHandle.None;

            // 🔒 Restrict loop handles to the top row only
            double loopRowHeight = WaveformBar.GetLaneHeight() + 6.0;

            if (p.Y > loopRowHeight)
                return LoopHandle.None;

            const double innerX = 6.0;
            double innerW = WaveformBar.ActualWidth - 12.0;
            if (innerW <= 1)
                return LoopHandle.None;

            double ax = innerX + Math.Clamp(WaveformBar.LoopA, 0.0, 1.0) * innerW;
            double bx = innerX + Math.Clamp(WaveformBar.LoopB, 0.0, 1.0) * innerW;

            double da = Math.Abs(p.X - ax);
            double db = Math.Abs(p.X - bx);

            bool hitA = da <= LoopHandleHitSlopPx;
            bool hitB = db <= LoopHandleHitSlopPx;

            if (hitA && hitB)
                return da <= db ? LoopHandle.A : LoopHandle.B;

            if (hitA) return LoopHandle.A;
            if (hitB) return LoopHandle.B;

            return LoopHandle.None;
        }

        private void DragLoopHandleTo(Point p)
        {
            const double innerX = 6.0;
            double innerW = WaveformBar.ActualWidth - 12.0;
            if (innerW <= 1) return;

            double frac = Math.Clamp((p.X - innerX) / innerW, 0.0, 1.0);

            const double minGap = 0.001;

            if (_draggingHandle == LoopHandle.A)
            {
                double b = Math.Clamp(WaveformBar.LoopB, 0.0, 1.0);
                WaveformBar.LoopA = Math.Min(frac, b - minGap);
            }
            else if (_draggingHandle == LoopHandle.B)
            {
                double a = Math.Clamp(WaveformBar.LoopA, 0.0, 1.0);
                WaveformBar.LoopB = Math.Max(frac, a + minGap);
            }
        }

        private bool HandleABLoopDuringPlayback(TimeSpan? dur, double progress)
        {
            if (!dur.HasValue ||
                _player.PlaybackState != NAudio.Wave.PlaybackState.Playing ||
                !WaveformBar.LoopEnabled)
            {
                return false;
            }

            double a = Math.Clamp(WaveformBar.LoopA, 0.0, 1.0);
            double b = Math.Clamp(WaveformBar.LoopB, 0.0, 1.0);

            if (b <= a + 0.0001)
                return false;

            if (progress >= b || progress >= (b - LoopEpsilon))
            {
                if (_xFadeMode == XFadeMode.CrossLoop)
                {
                    if (!_abCrossfadeArmed)
                    {
                        _abCrossfadeArmed = true;
                        LogTransport("UpdatePlaybackUI.ABLoopCrossfadeToA", $"progress={progress:0.0000} loopA={a:0.0000} loopB={b:0.0000}");
                        _pendingTrackChangeSource = "ABLoop";
                        _player.BeginCrossfadeLoopToFraction(a);
                    }

                    return true;
                }

                WaveformBar.Progress = a;
                LogTransport("UpdatePlaybackUI.ABLoopHardSeekToA", $"progress={progress:0.0000} loopA={a:0.0000} loopB={b:0.0000}");
                _player.SeekFraction(a, resume: true);
                _abCrossfadeArmed = false;
                return true;
            }

            _abCrossfadeArmed = false;
            return false;
        }
    }
}