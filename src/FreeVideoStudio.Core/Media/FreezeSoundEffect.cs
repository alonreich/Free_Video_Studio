using System.Collections.Concurrent;
using System.Globalization;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public enum FreezeSound
{
    Church_Bell,
    Cinematic_Dramatic_Slow_Down,
    Church_Bell_Mid,
    Church_Bell_Late
}

public static class FreezeSoundEffect
{
    public const int SampleRate = 48000;

    public const double TargetPeak = 0.9;

    public const double BellRingOutSec = 0.8;

    public const double MaxTailSec = BellRingOutSec;

    public const double SpliceHeadShare = 0.55;

    public const double SpliceCrossfadeMaxSec = 0.15;

    public const double EndFadeMaxSec = 0.03;

    public const int AssetVersion = 2;

    public const double LevelBelowVideo = 0.83;

    public const double FallbackVideoLufs = -20.0;

    public const double MinGainDb = -30.0;

    public const double MaxGainDb = 6.0;

    public const double ChurchBellLufs = -15.3;

    public const double ChurchBellMidLufs = -15.7;

    public const double ChurchBellLateLufs = -16.6;

    public const double CinematicDramaticSlowDownLufs = -17.3;

    public const FreezeSound Default = FreezeSound.Church_Bell;

    public const double DipDepth = 0.5;

    public const double DipLeadSec = 0.15;

    public const double DipInSec = 0.15;

    public const double DipOutSec = 0.3;

    public const double UnderwaterLowpassHz = 1100;

    public const string MusicCarveEq = "equalizer=f=500:width_type=o:width=1.5:g=-6";

    public static readonly IReadOnlyList<(FreezeSound Sound, string Name, string Label)> Choices =
    [
        (FreezeSound.Church_Bell, "Church_Bell", "Church Bell"),
        (FreezeSound.Church_Bell_Mid, "Church_Bell_Mid", "Church Bell (Mid Start)"),
        (FreezeSound.Church_Bell_Late, "Church_Bell_Late", "Church Bell (Late Start)"),
        (FreezeSound.Cinematic_Dramatic_Slow_Down, "Cinematic_Dramatic_Slow-Down", "Cinematic Dramatic Slow-Down"),
    ];

    public static Func<FreezeSound> SelectedProvider { get; set; } = () => Default;

    public static FreezeSound Selected
    {
        get
        {
            try { return SelectedProvider(); }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
                return Default;
            }
        }
    }

    public static Func<string> CacheDirectoryProvider { get; set; } = () => ApplicationPaths.CreateDefault().FreezeSoundCacheDirectory;

    private static readonly ConcurrentDictionary<FreezeSound, float[]> Assets = new();
    private static readonly ConcurrentDictionary<string, float[]> Renders = new();
    private static readonly object FileLock = new();

    private static string F(double v) => Math.Abs(v) < 1e-12 ? "0" : v.ToString("0.###############", CultureInfo.InvariantCulture);

    public static FreezeSound Parse(string? name)
    {
        foreach (var c in Choices)
            if (string.Equals(c.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return c.Sound;
        return Default;
    }

    public static string NameOf(FreezeSound sound) => Choices.First(c => c.Sound == sound).Name;

    public static string LabelOf(FreezeSound sound) => Choices.First(c => c.Sound == sound).Label;

    public static bool IsBell(FreezeSound sound) =>
        sound is FreezeSound.Church_Bell or FreezeSound.Church_Bell_Mid or FreezeSound.Church_Bell_Late;

    public static double ReferenceLufs(FreezeSound sound) => sound switch
    {
        FreezeSound.Church_Bell_Mid => ChurchBellMidLufs,
        FreezeSound.Church_Bell_Late => ChurchBellLateLufs,
        FreezeSound.Cinematic_Dramatic_Slow_Down => CinematicDramaticSlowDownLufs,
        _ => ChurchBellLufs,
    };

    public static double GainDbFor(double? videoLufs, FreezeSound? sound = null)
    {
        var s = sound ?? Selected;
        double video = videoLufs is double v && double.IsFinite(v) && v > -69.0 ? v : FallbackVideoLufs;
        double target = video + 20 * Math.Log10(LevelBelowVideo);
        return Math.Round(Math.Clamp(target - ReferenceLufs(s), MinGainDb, MaxGainDb), 1);
    }

    public static double TailSec(FreezeSound sound) => IsBell(sound) ? BellRingOutSec : 0;

    private static string ResourceName(FreezeSound sound) => sound switch
    {
        FreezeSound.Cinematic_Dramatic_Slow_Down => "FreezeSound.Cinematic_Dramatic_Slow-Down.wav",
        FreezeSound.Church_Bell_Mid => "FreezeSound.Church_Bell_Mid.wav",
        FreezeSound.Church_Bell_Late => "FreezeSound.Church_Bell_Late.wav",
        _ => "FreezeSound.Church_Bell.wav",
    };

    public static float[] Asset(FreezeSound sound) => Assets.GetOrAdd(sound, s =>
    {
        using var stream = typeof(FreezeSoundEffect).Assembly.GetManifestResourceStream(ResourceName(s))
            ?? throw new InvalidOperationException($"Freeze sound resource missing: {ResourceName(s)}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return DecodePcm16Mono(ms.ToArray());
    });

    internal static float[] DecodePcm16Mono(byte[] wav)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            throw new InvalidDataException("Freeze sound is not a WAV file.");
        int pos = 12;
        int channels = 0, rate = 0, bits = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(wav, body + 2);
                rate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                if (channels != 1 || rate != SampleRate || bits != 16)
                    throw new InvalidDataException($"Freeze sound must be 16-bit mono {SampleRate} Hz.");
                int n = Math.Min(size, wav.Length - body) / 2;
                var samples = new float[n];
                for (int i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(wav, body + 2 * i) / 32768f;
                return samples;
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException("Freeze sound has no data.");
    }

    public static int LengthSamples(double durationSec, FreezeSound sound) =>
        Math.Max(1, (int)Math.Round((Math.Max(0.05, durationSec) + TailSec(sound)) * SampleRate));

    public static float[] Render(double durationSec, FreezeSound? sound = null, double gainDb = 0)
    {
        var s = sound ?? Selected;
        double d = Math.Max(0.05, durationSec);
        double g = Math.Round(Math.Clamp(double.IsFinite(gainDb) ? gainDb : 0, MinGainDb, MaxGainDb), 1);
        return Renders.GetOrAdd($"{NameOf(s)}|{F(d)}|{F(g)}", _ =>
        {
            float[] src = Asset(s);
            if (Math.Abs(g) > 1e-9)
            {
                float k = (float)Math.Pow(10, g / 20.0);
                src = src.Select(v => v * k).ToArray();
            }
            return IsBell(s) ? RingOut(src, d) : Splice(src, d);
        });
    }

    public static float[] PreviewSlice(double holdSec, FreezeSound? sound = null, double gainDb = 0, double? tailRoomSec = null, double startAtSec = 0)
    {
        var s = sound ?? Selected;
        float[] full = Render(holdSec, s, gainDb);
        int hold = (int)Math.Round(Math.Max(0.05, holdSec) * SampleRate);
        int end = full.Length;
        if (tailRoomSec is double room && double.IsFinite(room))
            end = Math.Min(end, hold + (int)Math.Round(Math.Clamp(room, 0, TailSec(s)) * SampleRate));
        int start = (int)Math.Round(Math.Max(0, double.IsFinite(startAtSec) ? startAtSec : 0) * SampleRate);
        start = Math.Min(start, end);
        return full[start..end];
    }

    internal static float[] RingOut(float[] src, double holdSec)
    {
        int hold = (int)Math.Round(holdSec * SampleRate);
        int tail = (int)Math.Round(BellRingOutSec * SampleRate);
        var outp = new float[Math.Max(1, hold + tail)];
        for (int i = 0; i < outp.Length; i++)
        {
            float v = i < src.Length ? src[i] : 0f;
            double g = i < hold ? 1.0 : Math.Clamp(1.0 - (double)(i - hold) / tail, 0, 1);
            outp[i] = (float)(v * g);
        }
        return Finish(outp);
    }

    internal static float[] Splice(float[] src, double holdSec)
    {
        int n = Math.Max(1, (int)Math.Round(holdSec * SampleRate));
        var outp = new float[n];
        if (n >= src.Length)
        {
            Array.Copy(src, outp, src.Length);
            return Finish(outp);
        }
        int head = (int)Math.Round(holdSec * SpliceHeadShare * SampleRate);
        int xf = Math.Max(1, (int)Math.Round(Math.Min(SpliceCrossfadeMaxSec, holdSec * 0.2) * SampleRate));
        int tailStartOut = head - xf;
        int tailStartSrc = src.Length - (n - tailStartOut);
        for (int i = 0; i < n; i++)
        {
            double h = 0, t = 0;
            if (i < head)
            {
                double gh = i < tailStartOut ? 1.0 : Math.Cos(Math.PI / 2 * (i - tailStartOut + 0.5) / xf);
                h = src[i] * gh;
            }
            if (i >= tailStartOut)
            {
                double gt = i < head ? Math.Sin(Math.PI / 2 * (i - tailStartOut + 0.5) / xf) : 1.0;
                int k = tailStartSrc + (i - tailStartOut);
                t = k >= 0 && k < src.Length ? src[k] * gt : 0;
            }
            outp[i] = (float)(h + t);
        }
        return Finish(outp);
    }

    private static float[] Finish(float[] s)
    {
        int fade = Math.Max(1, (int)Math.Round(Math.Min(EndFadeMaxSec, s.Length / (double)SampleRate / 4) * SampleRate));
        for (int i = 0; i < fade && i < s.Length; i++) s[s.Length - 1 - i] *= (float)(i / (double)fade);
        float peak = 0;
        foreach (float v in s) peak = Math.Max(peak, Math.Abs(v));
        if (peak > TargetPeak)
        {
            float k = (float)(TargetPeak / peak);
            for (int i = 0; i < s.Length; i++) s[i] *= k;
        }
        return s;
    }

    public static string RenderedFile(double durationSec, FreezeSound? sound = null, double gainDb = 0)
    {
        var s = sound ?? Selected;
        float[] samples = Render(durationSec, s, gainDb);
        int gainTenths = (int)Math.Round(Math.Clamp(double.IsFinite(gainDb) ? gainDb : 0, MinGainDb, MaxGainDb) * 10);
        string dir = CacheDirectoryProvider();
        string path = Path.Combine(dir, FormattableString.Invariant($"freeze_{NameOf(s)}_v{AssetVersion}_{samples.Length}_g{gainTenths}.wav"));
        lock (FileLock)
        {
            if (File.Exists(path) && new FileInfo(path).Length == 46 + samples.Length * 4L) return path;
            Directory.CreateDirectory(dir);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write("RIFF"u8);
                w.Write(38 + samples.Length * 4);
                w.Write("WAVE"u8);
                w.Write("fmt "u8);
                w.Write(18);
                w.Write((short)3);
                w.Write((short)1);
                w.Write(SampleRate);
                w.Write(SampleRate * 4);
                w.Write((short)4);
                w.Write((short)32);
                w.Write((short)0);
                w.Write("data"u8);
                w.Write(samples.Length * 4);
                foreach (float v in samples) w.Write(v);
            }
            File.Move(tmp, path, overwrite: true);
        }
        return path;
    }

    public static string FilterPath(string path) =>
        path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "'\\''");

    public static string Source(double durationSec, string outLabel, FreezeSound? sound = null, long delaySamples = 0, double gainDb = 0)
    {
        string path = RenderedFile(durationSec, sound, gainDb);
        return $"amovie=filename='{FilterPath(path)}',pan=stereo|c0=c0|c1=c0," +
               $"aformat=sample_fmts=fltp:sample_rates={SampleRate}:channel_layouts=stereo," +
               $"adelay=delays={Math.Max(0, delaySamples)}S:all=1{outLabel}";
    }

    public static string? MusicDipPulse(IEnumerable<(double StartSec, double EndSec)> windows)
    {
        var parts = new List<string>();
        foreach (var (s, e) in windows)
        {
            if (e <= s + 1e-3) continue;
            double rampStart = s - DipLeadSec;
            parts.Add($"clip((t-{F(rampStart)})/{F(DipInSec)},0,1)*clip(({F(e + DipOutSec)}-t)/{F(DipOutSec)},0,1)");
        }
        return parts.Count == 0 ? null : $"clip({string.Join("+", parts)},0,1)";
    }

    public static double MusicDipPulseAt(double t, IEnumerable<(double StartSec, double EndSec)> windows)
    {
        double sum = 0;
        foreach (var (s, e) in windows)
        {
            if (e <= s + 1e-3) continue;
            sum += Math.Clamp((t - (s - DipLeadSec)) / DipInSec, 0, 1) * Math.Clamp((e + DipOutSec - t) / DipOutSec, 0, 1);
        }
        return Math.Clamp(sum, 0, 1);
    }

    public static IEnumerable<string> MusicDipChains(string inLabel, string pulse, string prefix, string outLabel)
    {
        yield return $"{inLabel}asplit=2[{prefix}_dry][{prefix}_wet]";
        yield return $"[{prefix}_wet]lowpass=f={F(UnderwaterLowpassHz)}:p=2,{MusicCarveEq},volume='{pulse}':eval=frame[{prefix}_wet_g]";
        yield return $"[{prefix}_dry]volume='1-({pulse})':eval=frame[{prefix}_dry_g]";
        yield return $"[{prefix}_dry_g][{prefix}_wet_g]amix=inputs=2:weights='1 1':normalize=0," +
                     $"volume='1-{F(DipDepth)}*({pulse})':eval=frame{outLabel}";
    }

    public static string PreviewMusicGraph(string pulse) => string.Join(";", MusicDipChains("", pulse, "fz", ""));
}
