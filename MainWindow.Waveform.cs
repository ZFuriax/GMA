using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private double _waveformZoom = 1.0; // 1.0 = normal
        private const double WaveformZoomMin = 1.0;
        private const double WaveformZoomMax = 20.0;

        private bool _isScrubbing;
        private double _pendingSeekFraction; // 0..1

        // Debug helpers
        private void WaveformBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            LogTransport("WaveformBar.PreviewMouseDown",
                $"x={e.GetPosition(WaveformBar).X:0.0} capture={Mouse.Captured?.GetType().Name ?? "null"}");
        }

        private void WaveformBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            LogTransport("WaveformBar.PreviewMouseUp",
                $"x={e.GetPosition(WaveformBar).X:0.0} capture={Mouse.Captured?.GetType().Name ?? "null"}");
        }

        private void WaveformBar_GotMouseCapture(object sender, MouseEventArgs e)
        {
            LogTransport("WaveformBar.GotCapture",
                $"capture={Mouse.Captured?.GetType().Name ?? "null"}");
        }

        private void WaveformBar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;

            e.Handled = true;

            if (WaveformScroller == null || WaveformBar == null)
                return;

            double oldW = WaveformBar.ActualWidth;
            double mouseX = e.GetPosition(WaveformScroller).X;

            double step = 1.10;
            double newZoom = e.Delta > 0 ? _waveformZoom * step : _waveformZoom / step;
            newZoom = Math.Clamp(newZoom, WaveformZoomMin, WaveformZoomMax);

            if (Math.Abs(newZoom - _waveformZoom) < 0.0001)
                return;

            _waveformZoom = newZoom;
            ApplyWaveformZoom(anchorMouseXInViewport: mouseX, oldContentWidth: oldW);
        }

        private void ApplyWaveformZoom(double? anchorMouseXInViewport = null, double? oldContentWidth = null)
        {
            if (WaveformScroller == null || WaveformBar == null)
                return;

            double viewportW = WaveformScroller.ViewportWidth;
            if (viewportW <= 0.5)
                viewportW = Math.Max(1.0, WaveformScroller.ActualWidth);

            if (_waveformZoom <= 1.0001)
            {
                WaveformScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                WaveformBar.Width = Math.Max(1.0, viewportW);
                WaveformScroller.ScrollToHorizontalOffset(0);
                return;
            }

            WaveformScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

            double newContentW = Math.Max(1.0, Math.Floor(viewportW * _waveformZoom));
            WaveformBar.Width = newContentW;

            if (anchorMouseXInViewport.HasValue && oldContentWidth.HasValue && oldContentWidth.Value > 0.5)
            {
                double mouseX = anchorMouseXInViewport.Value;
                double oldW = oldContentWidth.Value;

                double oldOffset = WaveformScroller.HorizontalOffset;
                double contentX = oldOffset + mouseX;

                double scale = newContentW / oldW;
                double newContentX = contentX * scale;
                double newOffset = newContentX - mouseX;

                WaveformScroller.ScrollToHorizontalOffset(Math.Max(0, newOffset));
            }
        }

        // ---------- Waveform scrubbing ----------
        private void WaveformBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(WaveformBar);

            LogTransport("WaveformBar.MouseDown",
                $"x={p.X:0.0} y={p.Y:0.0} isScrubbing={_isScrubbing} draggingHandle={_draggingHandle} capture={Mouse.Captured?.GetType().Name ?? "null"}");

            var handle = HitTestLoopHandle(p);
            if (handle != LoopHandle.None)
            {
                _draggingHandle = handle;
                WaveformBar.Focus();
                WaveformBar.CaptureMouse();
                DragLoopHandleTo(p);
                e.Handled = true;
                return;
            }

            // If click is in the loop row but not on a handle, do not start scrubbing.
            double loopRowHeight = WaveformBar.GetLaneHeight() + 6.0;

            if (p.Y <= loopRowHeight)
            {
                e.Handled = true;
                return;
            }

            var dur = _player.Duration;
            if (!dur.HasValue || dur.Value.TotalSeconds <= 0.01)
                return;

            WaveformBar.Focus();

            _isScrubbing = true;
            _scrubWasPlaying = _uiWantsPlaying;
            _lastScrubSeekUtc = DateTime.MinValue;

            WaveformBar.CaptureMouse();

            if (_scrubWasPlaying)
                _player.Pause();

            UpdateScrubVisual(p);
            e.Handled = true;
        }

        private void WaveformBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            if (_draggingHandle != LoopHandle.None)
            {
                DragLoopHandleTo(e.GetPosition(WaveformBar));
                e.Handled = true;
                return;
            }

            if (!_isScrubbing)
                return;

            UpdateScrubVisual(e.GetPosition(WaveformBar));
            e.Handled = true;
        }

        private void WaveformBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            LogTransport("WaveformBar.MouseUp",
                $"x={e.GetPosition(WaveformBar).X:0.0} isScrubbing={_isScrubbing} draggingHandle={_draggingHandle} capture={Mouse.Captured?.GetType().Name ?? "null"}");

            if (_draggingHandle != LoopHandle.None)
            {
                DragLoopHandleTo(e.GetPosition(WaveformBar));
                _draggingHandle = LoopHandle.None;
                try { WaveformBar.ReleaseMouseCapture(); } catch { }
                RequestSaveState();
                e.Handled = true;
                return;
            }

            if (!_isScrubbing)
                return;

            UpdateScrubVisual(e.GetPosition(WaveformBar));

            ClearPendingCrossfadeState();
            _suppressCrossfadeUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);

            var dur = _player.Duration;
            if (dur.HasValue)
            {
                double targetSeconds = dur.Value.TotalSeconds * _pendingSeekFraction;
                double remainingSeconds = dur.Value.TotalSeconds - targetSeconds;

                LogTransport("WaveformBar.SeekCommitRemaining",
                    $"remainingSeconds={remainingSeconds:0.000} crossfadeSeconds={CrossfadeMs / 1000.0:0.000}");

                if (remainingSeconds <= 5.0)
                {
                    _disableCrossfadeForCurrentTrack = true;
                    _disableCrossfadeForFile = _player.CurrentFile;
                }
                else
                {
                    _disableCrossfadeForCurrentTrack = false;
                    _disableCrossfadeForFile = _player.CurrentFile;
                }
            }

            LogTransport("WaveformBar.SeekCommit",
                $"fraction={_pendingSeekFraction:0.0000} resume={_scrubWasPlaying} " +
                $"disableCrossfadeForCurrentTrack={_disableCrossfadeForCurrentTrack} " +
                $"currentFile=\"{ClipLogValue(_player.CurrentFile)}\"");

            _player.SeekFraction(_pendingSeekFraction, resume: _scrubWasPlaying);

            if (_scrubWasPlaying)
            {
                LogTransport("WaveformBar.SeekPostState",
                    $"playbackState={_player.PlaybackState}");
            }

            _uiWantsPlaying = _scrubWasPlaying;
            SyncPlayPauseButton();

            EndScrub();
            RequestSaveState();
            e.Handled = true;
        }

        private void UpdateScrubVisual(Point p)
        {
            const double innerX = 6.0;
            double w = WaveformBar.ActualWidth - 12.0;
            if (w <= 1) return;

            double f = (p.X - innerX) / w;

            if (f < 0) f = 0;
            if (f >= 1) f = 0.999;

            _pendingSeekFraction = f;
            WaveformBar.Progress = f;
        }

        private void WaveformBar_LostMouseCapture(object sender, MouseEventArgs e)
        {
            LogTransport("WaveformBar.LostCapture",
                $"isScrubbing={_isScrubbing} draggingHandle={_draggingHandle} capture={Mouse.Captured?.GetType().Name ?? "null"}");

            if (_draggingHandle != LoopHandle.None)
            {
                _draggingHandle = LoopHandle.None;
                return;
            }

            if (!_isScrubbing)
                return;

            EndScrub();
        }

        private void EndScrub()
        {
            _isScrubbing = false;
            try { WaveformBar.ReleaseMouseCapture(); } catch { }
        }
    }
}