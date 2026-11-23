using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace FreeVideoStudio.Core.Media;

public enum PcmSampleEncoding
{
    Pcm,
    IeeeFloat,
}

public readonly record struct PcmCaptureFormat(int SampleRate, int Channels, int BitsPerSample, PcmSampleEncoding Encoding)
{
    public static PcmCaptureFormat ProductionMicrophone { get; } = new(44100, 1, 16, PcmSampleEncoding.Pcm);

    public int BlockAlign => Channels * (BitsPerSample / 8);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{SampleRate} Hz / {Channels} ch / {BitsPerSample}-bit {Encoding}");
}

public readonly record struct PcmBuffer(byte[] Data, int Count, PcmCaptureFormat Format)
{
    public ReadOnlySpan<byte> Span => Data.AsSpan(0, Math.Clamp(Count, 0, Data.Length));
}

public readonly record struct SpectrumBand(int Index, double LowHz, double HighHz, int FirstBin, int LastBin)
{
    public double CenterHz => Math.Sqrt(LowHz * HighHz);

    public bool Contains(double hz) => hz >= LowHz && hz < HighHz;
}

public sealed class MicrophoneSpectrumSnapshot
{
    internal MicrophoneSpectrumSnapshot(
        ImmutableArray<SpectrumBand> bands,
        ImmutableArray<double> bandDbfs,
        ImmutableArray<double> levels,
        long sequence,
        PcmCaptureFormat format,
        int fftSize,
        double floorDb,
        double ceilingDb)
    {
        Bands = bands;
        BandDbfs = bandDbfs;
        Levels = levels;
        Sequence = sequence;
        Format = format;
        FftSize = fftSize;
        DisplayFloorDb = floorDb;
        DisplayCeilingDb = ceilingDb;
    }

    public ImmutableArray<SpectrumBand> Bands { get; }

    public ImmutableArray<double> BandDbfs { get; }

    public ImmutableArray<double> Levels { get; }

    public long Sequence { get; }

    public PcmCaptureFormat Format { get; }
    public int FftSize { get; }
    public double DisplayFloorDb { get; }
    public double DisplayCeilingDb { get; }

    public double BinWidthHz => (double)Format.SampleRate / FftSize;

    public int PeakBandIndex
    {
        get
        {
            int best = 0;
            for (int i = 1; i < BandDbfs.Length; i++) if (BandDbfs[i] > BandDbfs[best]) best = i;
            return best;
        }
    }
}

public sealed class MicrophoneSpectrumAnalyzer
{
    public const int DefaultFftSize = 4096;
    public const int DefaultHopSize = 1024;
    public const int DefaultBandCount = 40;
    public const double DefaultMinHz = 60.0;
    public const double DefaultMaxHz = 16000.0;
    public const double DisplayFloorDb = -60.0;
    public const double DisplayCeilingDb = 0.0;

    public const double SilenceDb = -120.0;
    private const double SilencePower = 1e-12;

    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _bandCount;
    private readonly double _minHz;
    private readonly double _maxHz;

    private readonly float[] _ring;
    private readonly double[] _window;
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _bitReverse;
    private readonly double[] _bandPower;
    private readonly byte[] _carry = new byte[MaxBlockAlign];
    private readonly double _powerReference;

    private const int MaxChannels = 8;
    private const int MaxBlockAlign = MaxChannels * 4;

    private int _carryCount;
    private int _writeIndex;
    private int _filled;
    private int _sinceLastAnalysis;
    private long _sequence;
    private long _framesConsumed;
    private bool _hasPendingResult;
    private MicrophoneSpectrumSnapshot? _latest;

    public MicrophoneSpectrumAnalyzer()
        : this(PcmCaptureFormat.ProductionMicrophone)
    {
    }

    public MicrophoneSpectrumAnalyzer(
        PcmCaptureFormat format,
        int fftSize = DefaultFftSize,
        int hopSize = DefaultHopSize,
        int bandCount = DefaultBandCount,
        double minHz = DefaultMinHz,
        double maxHz = DefaultMaxHz)
    {
        if (fftSize < 64 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fftSize), fftSize, "FFT size must be a power of two >= 64.");
        if (hopSize < 1 || hopSize > fftSize)
            throw new ArgumentOutOfRangeException(nameof(hopSize), hopSize, "Hop must be 1..fftSize.");
        if (bandCount < 2 || bandCount > fftSize / 4)
            throw new ArgumentOutOfRangeException(nameof(bandCount), bandCount, "Band count out of range.");
        if (!(minHz > 0) || !(maxHz > minHz) || double.IsInfinity(maxHz))
            throw new ArgumentOutOfRangeException(nameof(minHz), "Frequency range must be finite with 0 < min < max.");

        _fftSize = fftSize;
        _hopSize = hopSize;
        _bandCount = bandCount;
        _minHz = minHz;
        _maxHz = maxHz;

        _ring = new float[fftSize];
        _window = new double[fftSize];
        _re = new double[fftSize];
        _im = new double[fftSize];
        _cos = new double[fftSize / 2];
        _sin = new double[fftSize / 2];
        _bitReverse = new int[fftSize];
        _bandPower = new double[bandCount];

        double sumSquares = 0;
        for (int n = 0; n < fftSize; n++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / fftSize);
            _window[n] = w;
            sumSquares += w * w;
        }
        _powerReference = fftSize * sumSquares / 4.0;

        for (int k = 0; k < fftSize / 2; k++)
        {
            double a = -2.0 * Math.PI * k / fftSize;
            _cos[k] = Math.Cos(a);
            _sin[k] = Math.Sin(a);
        }
        int bits = 0;
        while ((1 << bits) < fftSize) bits++;
        for (int i = 0; i < fftSize; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _bitReverse[i] = r;
        }

        Reconfigure(format);
    }

    public PcmCaptureFormat Format { get; private set; }
    public int FftSize => _fftSize;
    public int HopSize => _hopSize;
    public ImmutableArray<SpectrumBand> Bands { get; private set; }

    public double BinWidthHz => (double)Format.SampleRate / _fftSize;

    public MicrophoneSpectrumSnapshot? Latest => _latest;

    public long FramesConsumed => _framesConsumed;

    public int CarriedBytes => _carryCount;

    public int StateElementCount
        => _ring.Length + _window.Length + _re.Length + _im.Length + _cos.Length + _sin.Length
         + _bitReverse.Length + _bandPower.Length + _carry.Length;

    public void Reconfigure(PcmCaptureFormat format)
    {
        Validate(format);
        Format = format;
        Bands = BuildBands(format.SampleRate);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_ring);
        Array.Clear(_carry);
        _carryCount = 0;
        _writeIndex = 0;
        _filled = 0;
        _sinceLastAnalysis = 0;
        _sequence = 0;
        _framesConsumed = 0;
        _hasPendingResult = false;
        _latest = null;
    }

    public static bool IsSupported(PcmCaptureFormat format, out string reason)
    {
        if (format.SampleRate < 8000 || format.SampleRate > 384000)
        {
            reason = $"sample rate {format.SampleRate} Hz is outside 8000..384000";
            return false;
        }
        if (format.Channels < 1 || format.Channels > MaxChannels)
        {
            reason = $"{format.Channels} channels is outside 1..{MaxChannels}";
            return false;
        }
        bool ok = format.Encoding switch
        {
            PcmSampleEncoding.Pcm => format.BitsPerSample is 16 or 24 or 32,
            PcmSampleEncoding.IeeeFloat => format.BitsPerSample == 32,
            _ => false,
        };
        reason = ok ? "" : $"{format.BitsPerSample}-bit {format.Encoding} is not a supported sample layout";
        return ok;
    }

    private static void Validate(PcmCaptureFormat format)
    {
        if (!IsSupported(format, out string reason))
            throw new NotSupportedException($"Spectrum analyzer cannot decode {format}: {reason}.");
    }

    public int Process(ReadOnlySpan<byte> data)
    {
        int analyses = 0;
        int blockAlign = Format.BlockAlign;

        if (_carryCount > 0)
        {
            int need = blockAlign - _carryCount;
            int take = Math.Min(need, data.Length);
            data[..take].CopyTo(_carry.AsSpan(_carryCount));
            _carryCount += take;
            data = data[take..];
            if (_carryCount < blockAlign) return 0;
            analyses += PushFrame(_carry.AsSpan(0, blockAlign));
            _carryCount = 0;
        }

        int whole = data.Length / blockAlign;
        for (int f = 0; f < whole; f++)
            analyses += PushFrame(data.Slice(f * blockAlign, blockAlign));

        int rest = data.Length - whole * blockAlign;
        if (rest > 0)
        {
            data[(whole * blockAlign)..].CopyTo(_carry);
            _carryCount = rest;
        }

        if (_hasPendingResult)
        {
            _latest = BuildSnapshot();
            _hasPendingResult = false;
        }
        return analyses;
    }

    public int ProcessSamples(ReadOnlySpan<float> monoSamples)
    {
        if (Format.Channels != 1 || Format.Encoding != PcmSampleEncoding.IeeeFloat)
            throw new InvalidOperationException("ProcessSamples requires a mono IEEE-float format; use Process for PCM bytes.");
        int analyses = 0;
        foreach (float s in monoSamples) analyses += PushSample(Sanitize(s));
        if (_hasPendingResult)
        {
            _latest = BuildSnapshot();
            _hasPendingResult = false;
        }
        return analyses;
    }

    private int PushFrame(ReadOnlySpan<byte> frame)
    {
        int channels = Format.Channels;
        int bytes = Format.BitsPerSample / 8;
        double sum = 0;
        for (int c = 0; c < channels; c++)
            sum += DecodeSample(frame.Slice(c * bytes, bytes));
        return PushSample((float)(sum / channels));
    }

    private double DecodeSample(ReadOnlySpan<byte> s)
    {
        switch (Format.Encoding)
        {
            case PcmSampleEncoding.IeeeFloat:
                return Sanitize(BinaryPrimitives.ReadSingleLittleEndian(s));
            default:
                switch (Format.BitsPerSample)
                {
                    case 16: return BinaryPrimitives.ReadInt16LittleEndian(s) / 32768.0;
                    case 24:
                        int v24 = s[0] | (s[1] << 8) | ((sbyte)s[2] << 16);
                        return v24 / 8388608.0;
                    default: return BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648.0;
                }
        }
    }

    private static float Sanitize(float v) => float.IsFinite(v) ? v : 0f;

    private int PushSample(float sample)
    {
        _ring[_writeIndex] = sample;
        _writeIndex = (_writeIndex + 1) % _fftSize;
        if (_filled < _fftSize) _filled++;
        _framesConsumed++;
        _sinceLastAnalysis++;

        if (_filled < _fftSize || _sinceLastAnalysis < _hopSize) return 0;
        _sinceLastAnalysis = 0;
        Analyze();
        return 1;
    }

    private void Analyze()
    {
        for (int n = 0; n < _fftSize; n++)
        {
            int src = (_writeIndex + n) % _fftSize;
            int dst = _bitReverse[n];
            _re[dst] = _ring[src] * _window[n];
            _im[dst] = 0;
        }

        for (int size = 2; size <= _fftSize; size <<= 1)
        {
            int half = size >> 1;
            int step = _fftSize / size;
            for (int start = 0; start < _fftSize; start += size)
            {
                for (int j = 0; j < half; j++)
                {
                    double wr = _cos[j * step], wi = _sin[j * step];
                    int a = start + j, b = a + half;
                    double tr = _re[b] * wr - _im[b] * wi;
                    double ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti;
                    _re[a] += tr; _im[a] += ti;
                }
            }
        }

        var bands = Bands;
        for (int i = 0; i < bands.Length; i++)
        {
            double p = 0;
            for (int k = bands[i].FirstBin; k <= bands[i].LastBin; k++)
                p += _re[k] * _re[k] + _im[k] * _im[k];
            _bandPower[i] = p;
        }

        _sequence++;
        _hasPendingResult = true;
    }

    private MicrophoneSpectrumSnapshot BuildSnapshot()
    {
        var db = ImmutableArray.CreateBuilder<double>(_bandCount);
        var levels = ImmutableArray.CreateBuilder<double>(_bandCount);
        for (int i = 0; i < _bandCount; i++)
        {
            double d = PowerToDbfs(_bandPower[i], _powerReference);
            db.Add(d);
            levels.Add(DbfsToLevel(d));
        }
        return new MicrophoneSpectrumSnapshot(Bands, db.MoveToImmutable(), levels.MoveToImmutable(),
            _sequence, Format, _fftSize, DisplayFloorDb, DisplayCeilingDb);
    }

    internal static double PowerToDbfs(double power, double reference)
    {
        double normalised = power / reference;
        if (!double.IsFinite(normalised) || normalised < SilencePower) return SilenceDb;
        return 10.0 * Math.Log10(normalised);
    }

    public static double DbfsToLevel(double dbfs)
    {
        if (!double.IsFinite(dbfs)) return dbfs > 0 ? 1.0 : 0.0;
        return Math.Clamp((dbfs - DisplayFloorDb) / (DisplayCeilingDb - DisplayFloorDb), 0.0, 1.0);
    }

    private ImmutableArray<SpectrumBand> BuildBands(int sampleRate)
    {
        double bin = (double)sampleRate / _fftSize;
        double nyquist = sampleRate / 2.0;
        double lo = Math.Max(_minHz, 1.5 * bin);
        double hi = Math.Min(_maxHz, nyquist - 2.0 * bin);
        if (hi - lo < _bandCount * bin)
            throw new NotSupportedException(
                $"{_bandCount} bands of >= {bin:0.##} Hz do not fit {lo:0.#}..{hi:0.#} Hz at {sampleRate} Hz.");

        var edges = new double[_bandCount + 1];
        edges[0] = lo;
        for (int i = 1; i <= _bandCount; i++)
        {
            int remaining = _bandCount - i + 1;
            double logNext = edges[i - 1] * Math.Pow(hi / edges[i - 1], 1.0 / remaining);
            edges[i] = i == _bandCount ? hi : Math.Max(logNext, edges[i - 1] + bin);
        }

        var result = ImmutableArray.CreateBuilder<SpectrumBand>(_bandCount);
        for (int i = 0; i < _bandCount; i++)
        {
            int first = (int)Math.Ceiling(edges[i] / bin - 1e-9);
            int last = i == _bandCount - 1
                ? (int)Math.Floor(edges[i + 1] / bin + 1e-9)
                : (int)Math.Ceiling(edges[i + 1] / bin - 1e-9) - 1;
            first = Math.Max(first, 1);
            last = Math.Min(last, _fftSize / 2 - 1);
            if (last < first)
                throw new NotSupportedException($"Band {i} ({edges[i]:0.#}..{edges[i + 1]:0.#} Hz) owns no FFT bin.");
            result.Add(new SpectrumBand(i, edges[i], edges[i + 1], first, last));
        }
        return result.MoveToImmutable();
    }
}

public sealed class SpectrumMeterBallistics
{
    public const double AttackSeconds = 0.020;

    public const double ReleaseSeconds = 0.220;

    public const double StaleAfterSeconds = 0.300;

    public const double ZeroThreshold = 0.002;

    private double[] _levels = Array.Empty<double>();
    private MicrophoneSpectrumSnapshot? _lastSnapshot;
    private double _lastSnapshotTime = double.NegativeInfinity;
    private double _lastTime = double.NaN;

    public IReadOnlyList<double> Levels => _levels;

    public ImmutableArray<SpectrumBand> Bands { get; private set; } = ImmutableArray<SpectrumBand>.Empty;

    public bool IsSettled { get; private set; } = true;

    public void Reset()
    {
        Array.Clear(_levels);
        _lastSnapshot = null;
        _lastSnapshotTime = double.NegativeInfinity;
        _lastTime = double.NaN;
        IsSettled = true;
    }

    public bool Update(MicrophoneSpectrumSnapshot? snapshot, double nowSeconds)
    {
        if (!double.IsFinite(nowSeconds)) return false;

        bool changed = false;
        if (snapshot != null && (snapshot.Levels.Length != _levels.Length || !SameLayout(snapshot.Bands)))
        {
            _levels = new double[snapshot.Levels.Length];
            Bands = snapshot.Bands;
            changed = true;
        }

        if (snapshot != null && !ReferenceEquals(snapshot, _lastSnapshot))
        {
            _lastSnapshot = snapshot;
            _lastSnapshotTime = nowSeconds;
        }

        bool live = snapshot != null && nowSeconds - _lastSnapshotTime <= StaleAfterSeconds;
        double dt = double.IsNaN(_lastTime) ? 0.05 : Math.Clamp(nowSeconds - _lastTime, 0.0, 0.5);
        _lastTime = nowSeconds;

        double attack = 1.0 - Math.Exp(-dt / AttackSeconds);
        double release = 1.0 - Math.Exp(-dt / ReleaseSeconds);
        bool allZero = true;

        for (int i = 0; i < _levels.Length; i++)
        {
            double target = live ? Math.Clamp(snapshot!.Levels[i], 0.0, 1.0) : 0.0;
            double current = _levels[i];
            double next = target > current
                ? current + (target - current) * attack
                : current + (target - current) * release;
            if (next < ZeroThreshold) next = 0.0;
            if (!double.IsFinite(next)) next = 0.0;
            if (Math.Abs(next - current) > 1e-4) changed = true;
            _levels[i] = next;
            if (next > 0) allZero = false;
        }

        IsSettled = allZero && !live;
        return changed;
    }

    private bool SameLayout(ImmutableArray<SpectrumBand> bands)
    {
        if (Bands.Length != bands.Length) return false;
        for (int i = 0; i < bands.Length; i++) if (Bands[i] != bands[i]) return false;
        return true;
    }
}
