using System.Diagnostics;
using System.Globalization;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>Bundled FFmpeg/FFprobe, plus the decoders that turn a rendered file back into marker times.</summary>
internal static class Ff
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string N(double v) => v.ToString("0.######", Inv);

    public static async Task<string> RunAsync(string exe, params string[] args)
    {
        var p = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) p.ArgumentList.Add(a);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var r = await AsyncProcessRunner.RunAsync(p, cancellationToken: cts.Token);
        if (r.ExitCode != 0) throw new InvalidOperationException($"{System.IO.Path.GetFileName(exe)} failed ({r.ExitCode}): {Tail(r.StandardError)}");
        return r.StandardOutput;
    }

    public static Task Ffmpeg(params string[] args) =>
        RunAsync(Repo.Ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-threads", "2" }.Concat(args).ToArray());

    public static async Task<double> DurationAsync(string path)
    {
        string s = await RunAsync(Repo.Ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path);
        return double.Parse(s.Trim(), Inv);
    }

    /// <summary>Mean luma (0..255) of every frame at a fixed 60 fps, optionally of one region (x,y,w,h as fractions).</summary>
    public static async Task<byte[]> LumaAsync(string path, string work, string tag, (double X, double Y, double W, double H)? region = null)
    {
        string raw = System.IO.Path.Combine(work, tag + ".gray");
        string crop = region is { } r ? $"crop=iw*{N(r.W)}:ih*{N(r.H)}:iw*{N(r.X)}:ih*{N(r.Y)}," : "";
        await Ffmpeg("-i", path, "-an", "-vf", $"fps=60,{crop}scale=1:1:flags=area,format=gray", "-f", "rawvideo", raw);
        return await File.ReadAllBytesAsync(raw);
    }

    /// <summary>The source frame number shown in each 60 fps frame of a file (-1 where no barcode is readable).</summary>
    public static async Task<int[]> FrameCodesAsync(string path, string work, string tag)
    {
        string raw = System.IO.Path.Combine(work, tag + ".code");
        int bits = TestMedia.CodeBits;
        await Ffmpeg("-i", path, "-an", "-vf", $"fps=60,crop=iw:ih/6:0:0,scale={bits}:1:flags=area,format=gray", "-f", "rawvideo", raw);
        byte[] b = await File.ReadAllBytesAsync(raw);
        var codes = new int[b.Length / bits];
        for (int f = 0; f < codes.Length; f++)
        {
            int v = 0; bool clean = true;
            for (int k = 0; k < bits; k++)
            {
                byte px = b[f * bits + k];
                if (px > 150) v |= 1 << k; else if (px > 90) clean = false;
            }
            codes[f] = clean ? v : -1;
        }
        return codes;
    }

    /// <summary>Frame times (s) where the luma crosses up into [lo, hi].</summary>
    public static double[] RisingEdges(byte[] luma, int lo, int hi = 255)
    {
        var events = new List<double>();
        bool was = false;
        for (int i = 0; i < luma.Length; i++)
        {
            bool now = luma[i] >= lo && luma[i] <= hi;
            if (now && !was) events.Add(i / 60.0);
            was = now;
        }
        return events.ToArray();
    }

    /// <summary>Contiguous spans (s) where the luma stays inside [lo, hi] for at least minFrames.</summary>
    public static List<(double Start, double End)> Spans(byte[] luma, int lo, int hi, int minFrames = 6)
    {
        var spans = new List<(double, double)>();
        int start = -1;
        for (int i = 0; i <= luma.Length; i++)
        {
            bool inside = i < luma.Length && luma[i] >= lo && luma[i] <= hi;
            if (inside && start < 0) start = i;
            if (!inside && start >= 0)
            {
                if (i - start >= minFrames) spans.Add((start / 60.0, i / 60.0));
                start = -1;
            }
        }
        return spans;
    }

    /// <summary>Mono 48 kHz samples of one narrow frequency band (separates gameplay / music / meme / voice tones).</summary>
    public static async Task<float[]> BandAsync(string path, string work, string tag, double hz)
    {
        string raw = System.IO.Path.Combine(work, tag + ".f32");
        string band = $"bandpass=f={N(hz)}:width_type=q:w=6,bandpass=f={N(hz)}:width_type=q:w=6";
        await Ffmpeg("-i", path, "-vn", "-af", band, "-ac", "1", "-ar", "48000", "-f", "f32le", raw);
        byte[] bytes = await File.ReadAllBytesAsync(raw);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    /// <summary>1 ms RMS envelope.</summary>
    public static double[] Envelope(float[] samples)
    {
        var env = new double[samples.Length / 48];
        for (int w = 0; w < env.Length; w++)
        {
            double e = 0;
            for (int k = 0; k < 48; k++) { float s = samples[w * 48 + k]; e += s * s; }
            env[w] = Math.Sqrt(e / 48);
        }
        return env;
    }

    /// <summary>Onset times (s) of pulses: rises above a fraction of the loudest pulse, with a refractory gap.</summary>
    public static double[] Onsets(double[] env, double fraction = 0.3, double minGapSec = 0.15, double floor = 0.004)
    {
        double peak = env.Length == 0 ? 0 : env.Max();
        double on = Math.Max(floor, peak * fraction), off = on * 0.5;
        var result = new List<double>();
        bool loud = false; double last = double.NegativeInfinity;
        for (int i = 0; i < env.Length; i++)
        {
            double t = i / 1000.0;
            if (!loud && env[i] >= on)
            {
                loud = true;
                if (t - last >= minGapSec) { result.Add(t); last = t; }
            }
            else if (loud && env[i] < off) loud = false;
        }
        return result.ToArray();
    }

    /// <summary>Spans (s) where a band is continuously audible (a meme's tone, a voice take).</summary>
    public static List<(double Start, double End)> AudibleSpans(double[] env, double level, double minSec = 0.1)
    {
        var spans = new List<(double, double)>();
        int start = -1;
        for (int i = 0; i <= env.Length; i++)
        {
            bool on = i < env.Length && env[i] >= level;
            if (on && start < 0) start = i;
            if (!on && start >= 0) { spans.Add((start / 1000.0, i / 1000.0)); start = -1; }
        }
        for (int i = spans.Count - 1; i > 0; i--)
            if (spans[i].Item1 - spans[i - 1].Item2 < 0.05) { spans[i - 1] = (spans[i - 1].Item1, spans[i].Item2); spans.RemoveAt(i); }
        spans.RemoveAll(s => s.Item2 - s.Item1 < minSec);
        return spans;
    }

    private static string Tail(string s) => s.Length <= 600 ? s.Trim() : s[^600..].Trim();
}
