//CrossfadeMixerWaveProvider16.cs
using System;
using NAudio.Wave;

namespace MusicPlayer
{
    /// <summary>
    /// Simple end-of-song crossfade mixer for PCM 16-bit little-endian audio.
    /// - Normal mode: outputs "current" only.
    /// - Crossfade mode: outputs mix(current, next) with linear ramp for a fixed duration.
    /// - After fade completes: next becomes current automatically.
    ///
    /// Intended for end-of-track transitions (not A-B loop crossfade).
    /// </summary>
    internal sealed class CrossfadeMixerWaveProvider16 : IWaveProvider
    {
        /// <summary>
        /// Forces promotion of Next -> Current immediately (used by AudioPlayer after fade delay).
        /// Safe even if no next is set.
        /// </summary>
        public void CommitNextAsCurrent()
        {
            lock (_gate)
            {
                if (_next == null)
                    return;

                _current = _next;
                _next = null;

                _isCrossfading = false;
                _fadeTotalSamples = 0;
                _fadeProgressSamples = 0;
            }
        }

        private readonly object _gate = new();

        private IWaveProvider _current;
        private IWaveProvider? _next;

        public WaveFormat WaveFormat => _current.WaveFormat;

        // Crossfade state
        private bool _isCrossfading;
        private int _fadeTotalSamples;     // per-channel samples? We use "sample frames" across all channels.
        private int _fadeProgressSamples;

        // temp read buffers (bytes)
        private byte[] _tmpCur = Array.Empty<byte>();
        private byte[] _tmpNext = Array.Empty<byte>();

        /// <summary>True if we are currently blending current->next.</summary>
        public bool IsCrossfading
        {
            get { lock (_gate) return _isCrossfading; }
        }

        public CrossfadeMixerWaveProvider16(IWaveProvider current)
        {
            _current = current ?? throw new ArgumentNullException(nameof(current));

            ValidatePcm16(_current.WaveFormat);
        }

        /// <summary>
        /// Set (or replace) the current source. Cancels any active crossfade.
        /// </summary>
        public void SetCurrent(IWaveProvider current)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            ValidatePcm16(current.WaveFormat);

            lock (_gate)
            {
                EnsureSameFormat(current.WaveFormat, _current.WaveFormat);
                _current = current;

                // Cancel crossfade
                _next = null;
                _isCrossfading = false;
                _fadeTotalSamples = 0;
                _fadeProgressSamples = 0;
            }
        }

        /// <summary>
        /// Provide the "next" source to fade into. Does not start crossfade yet.
        /// </summary>
        public void SetNext(IWaveProvider next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            ValidatePcm16(next.WaveFormat);

            lock (_gate)
            {
                EnsureSameFormat(next.WaveFormat, _current.WaveFormat);
                _next = next;
            }
        }

        /// <summary>
        /// Starts a crossfade from current -> next over the given duration.
        /// Requires that SetNext() has been called with a valid provider.
        /// </summary>
        public void BeginCrossfade(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.FromMilliseconds(250);

            lock (_gate)
            {
                if (_next == null)
                    return; // nothing to fade to

                int sampleRate = _current.WaveFormat.SampleRate;
                int channels = _current.WaveFormat.Channels;

                // We count "sample frames" (one frame = samples for all channels at a single time step)
                int totalFrames = (int)Math.Round(duration.TotalSeconds * sampleRate);
                if (totalFrames < 1) totalFrames = 1;

                _fadeTotalSamples = totalFrames;       // frames
                _fadeProgressSamples = 0;
                _isCrossfading = true;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || (offset + count) > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;

            IWaveProvider cur;
            IWaveProvider? next;
            bool xfade;
            int fadeTotalFrames;
            int fadeProgressFrames;

            lock (_gate)
            {
                cur = _current;
                next = _next;
                xfade = _isCrossfading && next != null;
                fadeTotalFrames = _fadeTotalSamples;
                fadeProgressFrames = _fadeProgressSamples;
            }

            // Ensure temp buffers big enough
            EnsureTmp(ref _tmpCur, count);
            EnsureTmp(ref _tmpNext, count);

            // Read current always
            int nCur = cur.Read(_tmpCur, 0, count);

            if (nCur < count)
                Array.Clear(_tmpCur, nCur, count - nCur);

            if (!xfade)
            {
                if (nCur > 0)
                    Buffer.BlockCopy(_tmpCur, 0, buffer, offset, nCur);

                return nCur; // <-- key change: return 0 if nCur==0
            }

            // Read next too (try to match current bytes read)
            int nNext = 0;
            if (next != null)
                nNext = next.Read(_tmpNext, 0, count);

            if (nNext < count)
                Array.Clear(_tmpNext, nNext, count - nNext);

            // During crossfade we keep output alive as long as either side has data.
            int n = Math.Max(nCur, nNext);
            if (n > count) n = count;   // clamp to what caller requested
            if (n <= 0) return 0;

            int channels = cur.WaveFormat.Channels;
            // bytesPerFrame = 2 bytes per sample * channels
            int bytesPerFrame = 2 * channels;
            if (bytesPerFrame <= 0) bytesPerFrame = 4;

            int frames = n / bytesPerFrame;

            // If not aligned, only process aligned portion
            int alignedBytes = frames * bytesPerFrame;
            if (alignedBytes <= 0)
                return 0;

            // Mix frame-by-frame (linear ramp)
            int localProgress = fadeProgressFrames;

            for (int f = 0; f < frames; f++)
            {
                float t = fadeTotalFrames <= 1 ? 1f : (localProgress / (float)fadeTotalFrames);
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;

                float gCur = 1f - t;
                float gNext = t;

                int frameBase = f * bytesPerFrame;

                for (int c = 0; c < channels; c++)
                {
                    int i = frameBase + (c * 2);

                    short sCur = 0;
                    if (i + 1 < nCur)
                        sCur = (short)(_tmpCur[i] | (_tmpCur[i + 1] << 8));

                    short sNext = 0;
                    if (i + 1 < nNext) // if next has data at this sample
                        sNext = (short)(_tmpNext[i] | (_tmpNext[i + 1] << 8));

                    float y = (sCur * gCur) + (sNext * gNext);

                    int si = (int)MathF.Round(y);
                    if (si > short.MaxValue) si = short.MaxValue;
                    if (si < short.MinValue) si = short.MinValue;

                    buffer[offset + i] = (byte)(si & 0xFF);
                    buffer[offset + i + 1] = (byte)((si >> 8) & 0xFF);
                }

                localProgress++;
            }

            // Copy any remainder bytes (should be rare) from current to avoid glitches
            // If we didn't write the full 'n' bytes (because of frame alignment),
            // clear the tail so we don't leak old audio from previous reads.
            if (alignedBytes < n)
                Array.Clear(buffer, offset + alignedBytes, n - alignedBytes);

            // Update fade progress + complete swap if finished
            bool completed = false;

            lock (_gate)
            {
                if (_isCrossfading)
                {
                    _fadeProgressSamples += frames;

                    if (_fadeProgressSamples >= _fadeTotalSamples)
                    {
                        if (_next != null)
                            _current = _next;

                        _next = null;

                        _isCrossfading = false;
                        _fadeTotalSamples = 0;
                        _fadeProgressSamples = 0;

                        completed = true;
                    }
                }
            }

            // (Optional) if you want a callback later, you can add an event.
            _ = completed;

            return n;
        }

        private static void EnsureTmp(ref byte[] buf, int needed)
        {
            if (buf.Length < needed)
                buf = new byte[needed];
        }

        private static void ValidatePcm16(WaveFormat wf)
        {
            if (wf.Encoding != WaveFormatEncoding.Pcm || wf.BitsPerSample != 16)
                throw new ArgumentException("CrossfadeMixerWaveProvider16 requires PCM 16-bit input.");
        }

        private static void EnsureSameFormat(WaveFormat a, WaveFormat b)
        {
            if (a.SampleRate != b.SampleRate ||
                a.Channels != b.Channels ||
                a.BitsPerSample != b.BitsPerSample ||
                a.Encoding != b.Encoding)
            {
                throw new ArgumentException("Crossfade sources must have identical WaveFormat.");
            }
        }
    }
}