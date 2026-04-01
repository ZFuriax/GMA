using System;
using NAudio.Wave;

namespace MusicPlayer
{
    /// <summary>
    /// Simple "Night Mode" dynamics: gentle compressor + limiter.
    /// Bypassable at runtime (Enabled=false => pass-through).
    /// </summary>
    internal sealed class DynamicsProcessorSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public bool Enabled { get; set; } = false;

        // --- Medium preset (tweak later by ear) ---
        // Compressor
        private readonly float _thresholdDb = -26.0f;
        private readonly float _ratio = 4.0f;
        private readonly float _attackMs = 10.0f;
        private readonly float _releaseMs = 300.0f;
        private readonly float _makeupGainDb = +6.5f;

        // Limiter
        private readonly float _limiterCeilingDb = -1.0f;

        // Envelope detector state (shared across channels; OK for music leveling)
        private float _envDb = -120.0f;

        // Precomputed coefficients
        private readonly float _attackCoef;
        private readonly float _releaseCoef;

        private readonly float _makeupGainLin;
        private readonly float _limiterCeilingLin;

        public DynamicsProcessorSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));

            if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                throw new ArgumentException("Source must be IEEE float", nameof(source));

            int sr = source.WaveFormat.SampleRate;
            _attackCoef = MsToCoef(_attackMs, sr);
            _releaseCoef = MsToCoef(_releaseMs, sr);

            _makeupGainLin = DbToLin(_makeupGainDb);
            _limiterCeilingLin = DbToLin(_limiterCeilingDb);
        }

        public void Reset()
        {
            _envDb = -120.0f;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _source.Read(buffer, offset, count);
            if (n <= 0) return n;

            if (!Enabled)
                return n; // pass-through

            // Process sample-by-sample
            for (int i = 0; i < n; i++)
            {
                float x = buffer[offset + i];

                // --- Detector (peak-ish, smoothed) ---
                float ax = MathF.Abs(x);

                // Avoid log(0)
                float instDb = (ax < 1e-9f) ? -120.0f : LinToDb(ax);

                // Attack when getting louder, release when getting quieter
                float coef = instDb > _envDb ? _attackCoef : _releaseCoef;
                _envDb = coef * _envDb + (1f - coef) * instDb;

                // --- Compressor gain computation (static curve) ---
                float grDb = 0.0f;
                if (_envDb > _thresholdDb)
                {
                    // above threshold: output grows slower
                    // gain reduction = (input - threshold) * (1 - 1/ratio)
                    grDb = (_envDb - _thresholdDb) * (1.0f - 1.0f / _ratio);
                }

                float gainLin = DbToLin(-grDb) * _makeupGainLin;

                float y = x * gainLin;

                // --- Limiter (hard ceiling) ---
                // Keep it extremely simple & safe.
                if (y > _limiterCeilingLin) y = _limiterCeilingLin;
                else if (y < -_limiterCeilingLin) y = -_limiterCeilingLin;

                buffer[offset + i] = y;
            }

            return n;
        }

        private static float MsToCoef(float ms, int sampleRate)
        {
            // 1-pole smoothing coefficient
            // coef close to 1 => slow, close to 0 => fast
            float t = ms / 1000.0f;
            if (t <= 0) return 0;
            return MathF.Exp(-1.0f / (sampleRate * t));
        }

        private static float DbToLin(float db) => MathF.Pow(10.0f, db / 20.0f);

        private static float LinToDb(float lin) => 20.0f * MathF.Log10(lin);
    }
}