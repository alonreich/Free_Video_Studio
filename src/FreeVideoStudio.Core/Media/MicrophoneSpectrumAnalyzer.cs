// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace FreeVideoStudio.Core.Media;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// SPECTRUM_01 — THE STUDIO METER SHOWS WHAT THE MICROPHONE HEARS, BAND BY BAND.
//
// The Voice Over studio meter used to draw every bar as a RANDOM fraction of one overall peak
// (`targetAmp * (0.3 + <random draw> * 0.7)`), so no bar meant any frequency and a silent but
// "ready" device could still look alive. This type is the real thing: a Hann-windowed FFT over
// complete windows of the actual PCM the device delivered, mapped into ascending logarithmic bands
// with a defined Hz range each, expressed in dBFS (docs/02 §4 AUD-VOICEOVER, SPECTRUM_01).
//
//   window      4096 samples, periodic Hann (93 ms at 44.1 kHz), bin width = SampleRate / 4096
//               (10.77 Hz at 44.1 kHz). This is the resolution — no bar is finer than one bin.
//   hop         1024 samples (23 ms): a new spectrum every 23 ms of audio, independent of how the
//               driver chunks its callbacks. The ring buffer carries samples (and partial frames)
//               across callbacks, so splitting the same PCM differently yields the same spectra.
//   bands       40, log-spaced 60 Hz .. 16 kHz (upper edge clamped below Nyquist). A band narrower
//               than one bin is widened to exactly one bin and the remaining range re-spaced, so
//               every band owns >= 1 distinct bin and no bin is counted in two bands.
//   level       band POWER = sum of |X_k|^2 over the band's bins, normalised by N·Σw²/4, so a
//               full-scale sine whose energy falls inside one band reads 0 dBFS regardless of where
//               it sits between bins (power sum, not peak pick — no scalloping). Halving the
//               amplitude reads -6.02 dB. Silence floors at -120 dBFS (no log(0), no NaN).
//   display     Level = clamp((dBFS - (-60)) / 60, 0, 1). Values above 0 dBFS (e.g. square-wave
//               harmonics of a clipped signal) clamp at 1.
//
// Thread model: one instance is NOT thread-safe; the owner (VoiceCaptureSession, SPECTRUM_02)
// serialises calls. Storage is fixed at construction/configuration: nothing grows per callback.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Sample encoding of a capture buffer.</summary>
public enum PcmSampleEncoding
{
    /// <summary>Signed little-endian integer PCM.</summary>
    Pcm,
    /// <summary>32-bit IEEE float, little-endian.</summary>
    IeeeFloat,
}

/// <summary>The real format of a capture buffer. Never assumed — the analyzer decodes by it.</summary>
public readonly record struct PcmCaptureFormat(int SampleRate, int Channels, int BitsPerSample, PcmSampleEncoding Encoding)
{
    /// <summary>The fixed production capture contract (MicLevelMonitor / VoiceRecorder): 44.1 kHz, mono, PCM16.</summary>
    public static PcmCaptureFormat ProductionMicrophone { get; } = new(44100, 1, 16, PcmSampleEncoding.Pcm);

    /// <summary>Bytes per interleaved frame.</summary>
    public int BlockAlign => Channels * (BitsPerSample / 8);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{SampleRate} Hz / {Channels} ch / {BitsPerSample}-bit {Encoding}");
}

/// <summary>
/// One PCM callback, raised by the capture devices. ⚠️ <see cref="Data"/> is the driver's own
/// buffer and is only valid for the duration of the event: consume it synchronously, never keep it.
/// </summary>
public readonly record struct PcmBuffer(byte[] Data, int Count, PcmCaptureFormat Format)
{
    public ReadOnlySpan<byte> Span => Data.AsSpan(0, Math.Clamp(Count, 0, Data.Length));
}

/// <summary>A display band: a defined, ascending frequency range and the FFT bins it owns.</summary>
public readonly record struct SpectrumBand(int Index, double LowHz, double HighHz, int FirstBin, int LastBin)
{
    /// <summary>Geometric centre of the band.</summary>
    public double CenterHz => Math.Sqrt(LowHz * HighHz);

    /// <summary>True when <paramref name="hz"/> falls inside [LowHz, HighHz).</summary>
    public bool Contains(double hz) => hz >= LowHz && hz < HighHz;
}

/// <summary>
/// SPECTRUM_01 — one immutable analysis result. Safe to hand across threads; never mutated.
/// </summary>
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

    /// <summary>Ascending frequency bands, left to right.</summary>
    public ImmutableArray<SpectrumBand> Bands { get; }

    /// <summary>Unclamped band level in dBFS (0 dBFS = full-scale sine). Finite; floors at -120.</summary>
    public ImmutableArray<double> BandDbfs { get; }

    /// <summary>Display level per band, 0..1 over [<see cref="DisplayFloorDb"/>, <see cref="DisplayCeilingDb"/>].</summary>
    public ImmutableArray<double> Levels { get; }

    /// <summary>Analyses performed by the producing analyzer since its last reset (1-based).</summary>
    public long Sequence { get; }

    public PcmCaptureFormat Format { get; }
    public int FftSize { get; }
    public double DisplayFloorDb { get; }
    public double DisplayCeilingDb { get; }

    /// <summary>Frequency resolution of the analysis: one FFT bin.</summary>
    public double BinWidthHz => (double)Format.SampleRate / FftSize;

    /// <summary>Index of the loudest band.</summary>
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

/// <summary>SPECTRUM_01 — windowed-FFT band analyzer over real capture PCM.</summary>
public sealed class MicrophoneSpectrumAnalyzer
{
    public const int DefaultFftSize = 4096;
    public const int DefaultHopSize = 1024;
    public const int DefaultBandCount = 40;
    public const double DefaultMinHz = 60.0;
    public const double DefaultMaxHz = 16000.0;
    public const double DisplayFloorDb = -60.0;
    public const double DisplayCeilingDb = 0.0;

    /// <summary>Floor of the dBFS scale. Digital silence reads exactly this, never -∞.</summary>
    public const double SilenceDb = -120.0;
    private const double SilencePower = 1e-12;   // 10^(SilenceDb / 10)

    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _bandCount;
    private readonly double _minHz;
    private readonly double _maxHz;

    // Fixed storage — sized once, reused for every window (bounded by construction).
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
            // Periodic Hann: the DFT-even form, exact for spectral analysis.
            double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / fftSize);
            _window[n] = w;
            sumSquares += w * w;
        }
        // One-sided power of a unit-amplitude sine through this window: N·Σw²/4 (Parseval).
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

    /// <summary>Frequency resolution: one FFT bin, SampleRate / FftSize.</summary>
    public double BinWidthHz => (double)Format.SampleRate / _fftSize;

    /// <summary>Upper edge actually used: min(requested max, Nyquist - 2 bins).</summary>
    public double EffectiveMaxHz => Bands.IsDefaultOrEmpty ? _maxHz : Bands[^1].HighHz;

    /// <summary>Latency of one complete analysis window in seconds.</summary>
    public double WindowSeconds => (double)_fftSize / Format.SampleRate;

    /// <summary>Most recent result, or null before the first complete window since the last reset.</summary>
    public MicrophoneSpectrumSnapshot? Latest => _latest;

    /// <summary>Total complete frames decoded since the last reset (proves no sample vanishes across callbacks).</summary>
    public long FramesConsumed => _framesConsumed;

    /// <summary>Bytes of an incomplete frame carried to the next callback (0..BlockAlign-1).</summary>
    public int CarriedBytes => _carryCount;

    /// <summary>Total managed state held, in elements. Constant for the instance's lifetime.</summary>
    public int StateElementCount
        => _ring.Length + _window.Length + _re.Length + _im.Length + _cos.Length + _sin.Length
         + _bitReverse.Length + _bandPower.Length + _carry.Length;

    /// <summary>
    /// Validates <paramref name="format"/>, recomputes the band layout for its sample rate and
    /// resets all state. Unsupported formats throw <see cref="NotSupportedException"/> — they are
    /// never reinterpreted as mono PCM16.
    /// </summary>
    public void Reconfigure(PcmCaptureFormat format)
    {
        Validate(format);
        Format = format;
        Bands = BuildBands(format.SampleRate);
        Reset();
    }

    /// <summary>Discards all buffered audio and the latest result (device / session change).</summary>
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

    /// <summary>True when the analyzer can decode <paramref name="format"/>.</summary>
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

    /// <summary>
    /// Feeds raw interleaved bytes in <see cref="Format"/>. Any length is accepted: an incomplete
    /// trailing frame is carried into the next call. Every <see cref="HopSize"/> new frames (once a
    /// full window exists) one FFT runs. Returns the number of windows analysed by this call; when
    /// it is > 0, <see cref="Latest"/> holds the newest result.
    /// </summary>
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

    /// <summary>Convenience for tests and synthetic sources: mono float samples in [-1, 1].</summary>
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
        // Oldest sample is at _writeIndex (the ring is full).
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

    /// <summary>Normalised band power to dBFS, floored at <see cref="SilenceDb"/>. Always finite.</summary>
    internal static double PowerToDbfs(double power, double reference)
    {
        double normalised = power / reference;
        if (!double.IsFinite(normalised) || normalised < SilencePower) return SilenceDb;
        return 10.0 * Math.Log10(normalised);
    }

    /// <summary>dBFS to the 0..1 display scale over [-60, 0] dBFS. Always finite and clamped.</summary>
    public static double DbfsToLevel(double dbfs)
    {
        if (!double.IsFinite(dbfs)) return dbfs > 0 ? 1.0 : 0.0;
        return Math.Clamp((dbfs - DisplayFloorDb) / (DisplayCeilingDb - DisplayFloorDb), 0.0, 1.0);
    }

    /// <summary>
    /// Log-spaced ascending bands with every band at least one bin wide. A band that would be
    /// narrower than one bin is widened to exactly one bin, and the REMAINING range is re-spaced
    /// logarithmically so the top edge still lands on the effective maximum.
    /// </summary>
    private ImmutableArray<SpectrumBand> BuildBands(int sampleRate)
    {
        double bin = (double)sampleRate / _fftSize;
        double nyquist = sampleRate / 2.0;
        double lo = Math.Max(_minHz, 1.5 * bin);              // never the DC bin
        double hi = Math.Min(_maxHz, nyquist - 2.0 * bin);    // strictly below Nyquist
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
            // Bin k (centre k·bin) belongs to the band whose [low, high) contains it; the top band
            // is closed. A band >= one bin wide always contains at least one bin centre.
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

/// <summary>
/// SPECTRUM_01 — meter ballistics on the display side: fast attack, slower release, and decay to
/// zero when no NEW snapshot has arrived for <see cref="StaleAfterSeconds"/> (callbacks stopped,
/// device failed, session reset). Time is supplied by the caller, so behaviour is deterministic
/// and testable. Not thread-safe; the UI thread owns it.
/// </summary>
public sealed class SpectrumMeterBallistics
{
    /// <summary>Attack time constant (s). Rising bars reach 63% of a step in 20 ms.</summary>
    public const double AttackSeconds = 0.020;

    /// <summary>Release time constant (s). Falling bars lose 63% of their height in 220 ms.</summary>
    public const double ReleaseSeconds = 0.220;

    /// <summary>No new snapshot for this long ⇒ the input is treated as absent and the bars fall.</summary>
    public const double StaleAfterSeconds = 0.300;

    /// <summary>Below this a bar is drawn as empty (and the meter is allowed to stop repainting).</summary>
    public const double ZeroThreshold = 0.002;

    private double[] _levels = Array.Empty<double>();
    private MicrophoneSpectrumSnapshot? _lastSnapshot;
    private double _lastSnapshotTime = double.NegativeInfinity;
    private double _lastTime = double.NaN;

    /// <summary>Current displayed level per band, 0..1.</summary>
    public IReadOnlyList<double> Levels => _levels;

    /// <summary>The band layout of the last snapshot seen (empty before the first).</summary>
    public ImmutableArray<SpectrumBand> Bands { get; private set; } = ImmutableArray<SpectrumBand>.Empty;

    /// <summary>True when every bar is at zero and no live input is driving them.</summary>
    public bool IsSettled { get; private set; } = true;

    /// <summary>Drops all bars to zero immediately.</summary>
    public void Reset()
    {
        Array.Clear(_levels);
        _lastSnapshot = null;
        _lastSnapshotTime = double.NegativeInfinity;
        _lastTime = double.NaN;
        IsSettled = true;
    }

    /// <summary>
    /// Advances the meter to <paramref name="nowSeconds"/>. <paramref name="snapshot"/> is the
    /// newest available result (or null when there is no live input). Returns true when any bar
    /// changed visibly, i.e. the caller should repaint.
    /// </summary>
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
