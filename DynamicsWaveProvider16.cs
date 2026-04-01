//DynamicsWaveProvider16.cs
using System;
using NAudio.Wave;

namespace MusicPlayer
{
    /// <summary>
    /// Smooth loudness-style dynamics for PCM 16-bit little-endian audio.
    /// Slow loudness rider + RMS detector + channel-linked compression + soft knee + limiter.
    /// Includes a short enable ramp to avoid a "whoomp" when normalization is turned on mid-song.
    /// Designed for stable background playback with reduced pumping.
    /// </summary>
    internal sealed class DynamicsWaveProvider16 : IWaveProvider
    {
        private readonly IWaveProvider _source;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public bool Enabled { get; set; }

        // Gentle short-term compressor
        private readonly float _thresholdDb = -22.0f;
        private readonly float _ratio = 2.2f;
        private readonly float _kneeDb = 12.0f;
        private readonly float _attackMs = 20.0f;
        private readonly float _releaseMs = 300.0f;
        private readonly float _makeupGainDb = +2.5f;
        private readonly float _limiterCeilingDb = -1.0f;

        // Short RMS detector window for compressor
        private readonly float _rmsWindowMs = 60.0f;

        // Slow loudness rider ("table mode")
        private readonly float _targetLoudnessDb = -28.0f;
        private readonly float _riderWindowMs = 2000.0f;
        private readonly float _riderAttackMs = 1800.0f;
        private readonly float _riderReleaseMs = 4000.0f;
        private readonly float _maxRiderBoostDb = 8.0f;
        private readonly float _maxRiderCutDb = 6.0f;

        // Enable ramp to avoid sudden gain jump when turning normalization on
        private readonly float _enableRampMs = 180.0f;

        private readonly int _channels;
        private readonly int _sampleRate;

        private readonly float _attackCoef;
        private readonly float _releaseCoef;
        private readonly float _limiterCeilingLin;

        private readonly float _riderAttackCoef;
        private readonly float _riderReleaseCoef;

        private readonly float _wetMixStep;

        private float _envDb = -120.0f;
        private float _riderGainDb = 0.0f;

        private float _wetMix = 0.0f;
        private bool _wasEnabledLastRead = false;

        // Rolling RMS state for short compressor detector
        private readonly float[] _rmsSquares;
        private int _rmsIndex;
        private int _rmsCount;
        private float _rmsSumSquares;

        // Rolling RMS state for slow loudness rider
        private readonly float[] _riderSquares;
        private int _riderIndex;
        private int _riderCount;
        private float _riderSumSquares;

        public DynamicsWaveProvider16(IWaveProvider source, bool enabled = false)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));

            if (_source.WaveFormat.Encoding != WaveFormatEncoding.Pcm || _source.WaveFormat.BitsPerSample != 16)
                throw new ArgumentException("DynamicsWaveProvider16 requires PCM 16-bit input.", nameof(source));

            Enabled = enabled;

            _sampleRate = _source.WaveFormat.SampleRate;
            _channels = _source.WaveFormat.Channels;

            _attackCoef = MsToCoef(_attackMs, _sampleRate);
            _releaseCoef = MsToCoef(_releaseMs, _sampleRate);
            _riderAttackCoef = MsToCoef(_riderAttackMs, _sampleRate);
            _riderReleaseCoef = MsToCoef(_riderReleaseMs, _sampleRate);

            _limiterCeilingLin = DbToLin(_limiterCeilingDb);

            int windowFrames = Math.Max(1, (int)MathF.Round(_sampleRate * (_rmsWindowMs / 1000.0f)));
            _rmsSquares = new float[windowFrames];

            int riderWindowFrames = Math.Max(1, (int)MathF.Round(_sampleRate * (_riderWindowMs / 1000.0f)));
            _riderSquares = new float[riderWindowFrames];

            float rampFrames = Math.Max(1.0f, _sampleRate * (_enableRampMs / 1000.0f));
            _wetMixStep = 1.0f / rampFrames;

            _wetMix = enabled ? 1.0f : 0.0f;
            _wasEnabledLastRead = enabled;
        }

        public void Reset()
        {
            _envDb = -120.0f;
            _riderGainDb = 0.0f;

            _rmsIndex = 0;
            _rmsCount = 0;
            _rmsSumSquares = 0.0f;
            Array.Clear(_rmsSquares, 0, _rmsSquares.Length);

            _riderIndex = 0;
            _riderCount = 0;
            _riderSumSquares = 0.0f;
            Array.Clear(_riderSquares, 0, _riderSquares.Length);

            _wetMix = Enabled ? 1.0f : 0.0f;
            _wasEnabledLastRead = Enabled;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int n = _source.Read(buffer, offset, count);
            if (n <= 0) return n;

            if (!Enabled)
            {
                _wetMix = 0.0f;
                _wasEnabledLastRead = false;
                return n;
            }

            if (!_wasEnabledLastRead)
            {
                // Just turned on: begin dry->wet fade
                _wetMix = 0.0f;
            }
            _wasEnabledLastRead = true;

            int bytesPerSample = 2;
            int bytesPerFrame = bytesPerSample * _channels;
            int end = offset + n;

            Span<float> samples = stackalloc float[8];
            if (_channels > samples.Length)
                throw new InvalidOperationException("Unsupported channel count.");

            bool seededThisRead = false;

            for (int frameStart = offset; frameStart + bytesPerFrame - 1 < end; frameStart += bytesPerFrame)
            {
                float frameEnergy = 0.0f;

                // Read one interleaved frame and compute linked energy
                for (int ch = 0; ch < _channels; ch++)
                {
                    int i = frameStart + ch * 2;
                    short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                    float x = s / 32768f;
                    samples[ch] = x;
                    frameEnergy += x * x;
                }

                frameEnergy /= _channels;

                // Short RMS window for compressor
                if (_rmsCount < _rmsSquares.Length)
                {
                    _rmsSquares[_rmsIndex] = frameEnergy;
                    _rmsSumSquares += frameEnergy;
                    _rmsCount++;
                }
                else
                {
                    _rmsSumSquares -= _rmsSquares[_rmsIndex];
                    _rmsSquares[_rmsIndex] = frameEnergy;
                    _rmsSumSquares += frameEnergy;
                }

                _rmsIndex++;
                if (_rmsIndex >= _rmsSquares.Length)
                    _rmsIndex = 0;

                float rms = MathF.Sqrt(MathF.Max(_rmsSumSquares / Math.Max(1, _rmsCount), 1e-12f));
                float instDb = LinToDb(rms);

                // Slow loudness rider
                if (_riderCount < _riderSquares.Length)
                {
                    _riderSquares[_riderIndex] = frameEnergy;
                    _riderSumSquares += frameEnergy;
                    _riderCount++;
                }
                else
                {
                    _riderSumSquares -= _riderSquares[_riderIndex];
                    _riderSquares[_riderIndex] = frameEnergy;
                    _riderSumSquares += frameEnergy;
                }

                _riderIndex++;
                if (_riderIndex >= _riderSquares.Length)
                    _riderIndex = 0;

                float riderRms = MathF.Sqrt(MathF.Max(_riderSumSquares / Math.Max(1, _riderCount), 1e-12f));
                float riderDb = LinToDb(riderRms);

                // If we just enabled normalization, seed the detectors from the current material
                // so the first processed frames are already close to the right gain.
                if (!seededThisRead && _wetMix == 0.0f)
                {
                    _envDb = instDb;

                    float seededRiderGainDb = _targetLoudnessDb - riderDb;
                    if (seededRiderGainDb > _maxRiderBoostDb) seededRiderGainDb = _maxRiderBoostDb;
                    if (seededRiderGainDb < -_maxRiderCutDb) seededRiderGainDb = -_maxRiderCutDb;
                    _riderGainDb = seededRiderGainDb;

                    seededThisRead = true;
                }

                float coef = instDb > _envDb ? _attackCoef : _releaseCoef;
                _envDb = coef * _envDb + (1f - coef) * instDb;

                float desiredRiderGainDb = _targetLoudnessDb - riderDb;
                if (desiredRiderGainDb > _maxRiderBoostDb) desiredRiderGainDb = _maxRiderBoostDb;
                if (desiredRiderGainDb < -_maxRiderCutDb) desiredRiderGainDb = -_maxRiderCutDb;

                float riderCoef = desiredRiderGainDb > _riderGainDb ? _riderAttackCoef : _riderReleaseCoef;
                _riderGainDb = riderCoef * _riderGainDb + (1f - riderCoef) * desiredRiderGainDb;

                // Short-term compression
                float grDb = ComputeGainReductionDb(_envDb, _thresholdDb, _ratio, _kneeDb);

                // Total gain = slow rider - compression + fixed makeup
                float totalGainDb = _riderGainDb - grDb + _makeupGainDb;
                float gain = DbToLin(totalGainDb);

                // Apply wet/dry blend, ramping wet in smoothly when enabled
                for (int ch = 0; ch < _channels; ch++)
                {
                    float dry = samples[ch];
                    float processed = dry * gain;
                    float y = dry + ((processed - dry) * _wetMix);

                    if (y > _limiterCeilingLin) y = _limiterCeilingLin;
                    else if (y < -_limiterCeilingLin) y = -_limiterCeilingLin;

                    int si = (int)MathF.Round(y * 32767f);
                    if (si > short.MaxValue) si = short.MaxValue;
                    if (si < short.MinValue) si = short.MinValue;

                    int i = frameStart + ch * 2;
                    buffer[i] = (byte)(si & 0xFF);
                    buffer[i + 1] = (byte)((si >> 8) & 0xFF);
                }

                if (_wetMix < 1.0f)
                {
                    _wetMix += _wetMixStep;
                    if (_wetMix > 1.0f)
                        _wetMix = 1.0f;
                }
            }

            return n;
        }

        private static float ComputeGainReductionDb(float envDb, float thresholdDb, float ratio, float kneeDb)
        {
            float slope = 1.0f - (1.0f / ratio);

            if (kneeDb <= 0.0f)
            {
                if (envDb <= thresholdDb) return 0.0f;
                return (envDb - thresholdDb) * slope;
            }

            float kneeStart = thresholdDb - (kneeDb * 0.5f);
            float kneeEnd = thresholdDb + (kneeDb * 0.5f);

            if (envDb <= kneeStart)
                return 0.0f;

            if (envDb >= kneeEnd)
                return (envDb - thresholdDb) * slope;

            float x = envDb - kneeStart;
            return slope * (x * x) / (2.0f * kneeDb);
        }

        private static float MsToCoef(float ms, int sampleRate)
        {
            float t = ms / 1000.0f;
            if (t <= 0) return 0;
            return MathF.Exp(-1.0f / (sampleRate * t));
        }

        private static float DbToLin(float db) => MathF.Pow(10.0f, db / 20.0f);
        private static float LinToDb(float lin) => 20.0f * MathF.Log10(MathF.Max(lin, 1e-12f));
    }
}