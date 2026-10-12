
using System;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;


/// <summary>VOLIVE_01 — an immutable view of the take in progress. Safe to hold on the UI thread.</summary>
public sealed record LiveTakeSnapshot(
    long Buffers,
    long Frames,
    int SampleRate,
    double BucketSeconds,
    float[] Peaks,
    float MaxPeak,
    bool IsFrozen)
{
    /// <summary>Seconds of audio actually delivered by the device (frames / sample rate).</summary>
    public double CapturedSeconds => SampleRate > 0 ? Frames / (double)SampleRate : 0;

    /// <summary>True once at least one buffer arrived — silence included. Zero buffers is a fault, not a take.</summary>
    public bool HasData => Buffers > 0 && Frames > 0;
}

/// <summary>VOLIVE_01 — bounded live capture envelope + captured-duration counter for one take.</summary>
public sealed class LiveTakeMeter
{
    public const double DefaultBucketSeconds = 0.05;
    public const int DefaultMaxBuckets = 2048;

    private readonly object _lock = new();
    private readonly double _baseBucketSeconds;
    private readonly int _maxBuckets;
    private readonly float[] _peaks;
    private int _count;
    private float _current;
    private long _framesInCurrent;
    private long _framesPerBucket;
    private long _frames;
    private long _buffers;
    private int _sampleRate;
    private float _maxPeak;
    private bool _frozen;
    private int _version;
    private int _snapshotVersion = -1;
    private LiveTakeSnapshot? _snapshot;
    private byte[] _carry = new byte[16];
    private int _carryCount;

    public LiveTakeMeter(double bucketSeconds = DefaultBucketSeconds, int maxBuckets = DefaultMaxBuckets)
    {
        if (!(bucketSeconds > 0) || double.IsInfinity(bucketSeconds)) throw new ArgumentOutOfRangeException(nameof(bucketSeconds));
        if (maxBuckets < 2) throw new ArgumentOutOfRangeException(nameof(maxBuckets));
        _baseBucketSeconds = bucketSeconds;
        _maxBuckets = maxBuckets & ~1;
        _peaks = new float[_maxBuckets];
    }

    /// <summary>Upper bound on stored envelope values, whatever the take length.</summary>
    public int MaxBuckets => _maxBuckets;

    /// <summary>Stored buckets right now (≤ <see cref="MaxBuckets"/>).</summary>
    public int StoredBuckets { get { lock (_lock) return _count; } }

    /// <summary>Consumes one capture callback synchronously. The buffer is not retained.</summary>
    public void Append(in PcmBuffer pcm)
    {
        var format = pcm.Format;
        int bytes = format.BitsPerSample / 8;
        int block = format.BlockAlign;
        if (format.SampleRate <= 0 || format.Channels <= 0 || bytes <= 0 || block <= 0) return;
        if (bytes is not (2 or 3 or 4)) return;
        if (format.Encoding == PcmSampleEncoding.IeeeFloat && bytes != 4) return;

        lock (_lock)
        {
            if (_frozen) return;
            if (_sampleRate != format.SampleRate)
            {
                if (_sampleRate != 0 && _frames > 0) return;
                _sampleRate = format.SampleRate;
                _framesPerBucket = Math.Max(1, (long)Math.Round(_baseBucketSeconds * _sampleRate));
            }
            if (_carry.Length < block) _carry = new byte[block];

            _buffers++;
            var span = pcm.Span;
            int i = 0;

            if (_carryCount > 0)
            {
                int need = block - _carryCount;
                int take = Math.Min(need, span.Length);
                span.Slice(0, take).CopyTo(_carry.AsSpan(_carryCount));
                _carryCount += take;
                i = take;
                if (_carryCount == block)
                {
                    AddFrameLocked(_carry.AsSpan(0, block), format, bytes);
                    _carryCount = 0;
                }
            }

            for (; i + block <= span.Length; i += block)
                AddFrameLocked(span.Slice(i, block), format, bytes);

            int rest = span.Length - i;
            if (rest > 0)
            {
                span.Slice(i, rest).CopyTo(_carry.AsSpan(_carryCount));
                _carryCount += rest;
            }
            _version++;
        }
    }

    /// <summary>Ends the take. Further <see cref="Append"/> calls are ignored.</summary>
    public void Freeze()
    {
        lock (_lock)
        {
            if (_frozen) return;
            _frozen = true;
            _version++;
        }
    }

    /// <summary>The newest immutable view. Rebuilt only when something changed since the last call.</summary>
    public LiveTakeSnapshot Snapshot()
    {
        lock (_lock)
        {
            if (_snapshot != null && _snapshotVersion == _version) return _snapshot;
            bool partial = _framesInCurrent > 0;
            var peaks = new float[_count + (partial ? 1 : 0)];
            Array.Copy(_peaks, peaks, _count);
            if (partial) peaks[_count] = _current;
            double bucketSeconds = _sampleRate > 0 ? _framesPerBucket / (double)_sampleRate : _baseBucketSeconds;
            _snapshot = new LiveTakeSnapshot(_buffers, _frames, _sampleRate, bucketSeconds, peaks, _maxPeak, _frozen);
            _snapshotVersion = _version;
            return _snapshot;
        }
    }

    private void AddFrameLocked(ReadOnlySpan<byte> frame, PcmCaptureFormat format, int bytes)
    {
        float peak = 0;
        for (int c = 0; c < format.Channels; c++)
        {
            float v = Math.Abs(Decode(frame.Slice(c * bytes, bytes), format.Encoding));
            if (v > peak) peak = v;
        }
        if (peak > 1f) peak = 1f;

        if (peak > _current) _current = peak;
        if (peak > _maxPeak) _maxPeak = peak;
        _frames++;
        if (++_framesInCurrent < _framesPerBucket) return;

        if (_count == _maxBuckets)
        {
            for (int k = 0; k < _maxBuckets / 2; k++)
                _peaks[k] = Math.Max(_peaks[2 * k], _peaks[2 * k + 1]);
            Array.Clear(_peaks, _maxBuckets / 2, _maxBuckets / 2);
            _count = _maxBuckets / 2;
            _framesPerBucket *= 2;
            if (_framesInCurrent < _framesPerBucket) return;
        }

        _peaks[_count++] = _current;
        _current = 0;
        _framesInCurrent = 0;
    }

    private static float Decode(ReadOnlySpan<byte> s, PcmSampleEncoding encoding)
    {
        if (encoding == PcmSampleEncoding.IeeeFloat)
        {
            float f = BitConverter.ToSingle(s);
            return float.IsFinite(f) ? f : 0f;
        }
        return s.Length switch
        {
            2 => (short)(s[0] | (s[1] << 8)) / 32768f,
            3 => ((s[0] << 8 | s[1] << 16 | s[2] << 24) >> 8) / 8388608f,
            4 => (s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24) / 2147483648f,
            _ => 0f,
        };
    }
}
