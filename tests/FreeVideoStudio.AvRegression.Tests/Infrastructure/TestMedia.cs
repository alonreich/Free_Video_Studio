using System.Text;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>A generated clip and the exact source times of its markers.</summary>
internal sealed record Clip(string Path, double DurationSec, double Fps, double[] Events, bool HasAudio, int Width, int Height);

/// <summary>
/// Generated marker media (cached under logs/AV_Results/work/media). Markers sit at IRREGULAR, seeded
/// times so a drift of exactly one period can never look correct.
/// <list type="bullet">
/// <item>Gameplay: black picture, a 3-frame WHITE flash + a 50 ms 400 Hz pulse at every event, and the frame number as a barcode in the top strip (exposes skipped/repeated frames).</item>
/// <item>Music: a 40 ms 2500 Hz pulse at every event over a continuous 3300 Hz bed (shows exactly where the music starts, stops and pauses).</item>
/// <item>Meme: mid-GRAY picture with a continuous 1200 Hz tone (visible and audible as a block).</item>
/// <item>Voice: 900 Hz pulses (voice-over takes).</item>
/// </list>
/// </summary>
internal static class TestMedia
{
    public const double GameplayHz = 400, MusicHz = 2500, MusicBedHz = 3300, MemeHz = 1200, VoiceHz = 900;
    private const int Version = 3;

    /// <summary>Frame-number barcode: 12 cells along the top sixth of the picture, bit k = cell k.</summary>
    public const int CodeBits = 12;
    public const double CodeStripH = 1.0 / 6;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static double[] Events(int seed, double durationSec, double fps = 60, double firstSec = 0.4, double minGap = 0.55, double maxGap = 1.35)
    {
        var rng = new Random(seed);
        var list = new List<double>();
        double t = firstSec;
        while (t < durationSec - 0.3)
        {
            list.Add(Math.Round(t * fps) / fps);
            t += minGap + rng.NextDouble() * (maxGap - minGap);
        }
        return list.ToArray();
    }

    private static string Windows(IEnumerable<double> events, double len)
    {
        var sb = new StringBuilder();
        foreach (double e in events) sb.Append(sb.Length == 0 ? "" : "+").Append($"between(t,{Ff.N(e)},{Ff.N(e + len)})");
        return sb.Length == 0 ? "0" : sb.ToString();
    }

    private static async Task<string> CachedAsync(string name, Func<string, Task> make)
    {
        string path = System.IO.Path.Combine(Repo.MediaDir, $"v{Version}-{name}");
        if (File.Exists(path)) return path;
        await Gate.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            string tmp = path + ".part" + System.IO.Path.GetExtension(path);
            await make(tmp);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Gameplay clip; keyframe every second like a real recording (seeks must decode up to 59 frames).</summary>
    public static async Task<Clip> GameplayAsync(int seed = 1, double seconds = 20, double fps = 60, int width = 320, int height = 180, bool audio = true, string container = "mp4")
    {
        double[] ev = Events(seed, seconds, fps);
        string name = $"game-s{seed}-{Ff.N(seconds)}s-{Ff.N(fps)}fps-{width}x{height}-{(audio ? "a" : "na")}.{container}";
        string path = await CachedAsync(name, async tmp =>
        {
            string flash = string.Join("+", ev.Select(e => $"between(n,{Math.Round(e * fps)},{Math.Round(e * fps) + 2})"));
            var args = new List<string>
            {
                "-f", "lavfi", "-i", $"color=c=black:s={width}x{height}:r={Ff.N(fps)}:d={Ff.N(seconds)},drawbox=x=0:y=ih/6:w=iw:h=ih*5/6:c=white:t=fill:enable='{flash}'{Barcode()}",
            };
            if (audio)
                args.AddRange(new[] { "-f", "lavfi", "-i", $"aevalsrc=exprs='0.6*sin(2*PI*{Ff.N(GameplayHz)}*t)*({Windows(ev, 0.05)})':s=48000:d={Ff.N(seconds)}" });
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-g", Ff.N(fps), "-pix_fmt", "yuv420p" });
            if (audio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-ac", "2" });
            args.Add(tmp);
            await Ff.Ffmpeg(args.ToArray());
        });
        return new Clip(path, seconds, fps, ev, audio, width, height);
    }

    /// <summary>drawbox cells that spell the frame number (n) in binary across the top strip.</summary>
    private static string Barcode()
    {
        var sb = new StringBuilder();
        for (int k = 0; k < CodeBits; k++)
            sb.Append($",drawbox=x=iw*{k}/{CodeBits}:y=0:w=iw/{CodeBits}:h=ih/6:c=white:t=fill:enable='mod(floor(n/{1 << k}),2)'");
        return sb.ToString();
    }

    /// <summary>Variable frame rate: 3 s at 30 fps then 3 s at 60 fps, joined without re-timing.</summary>
    public static async Task<Clip> VariableFrameRateAsync()
    {
        var a = await GameplayAsync(seed: 41, seconds: 3, fps: 30, container: "mkv");
        var b = await GameplayAsync(seed: 42, seconds: 3, fps: 60, container: "mkv");
        string path = await CachedAsync("vfr-41-42.mkv", async tmp =>
        {
            string list = tmp + ".txt";
            await File.WriteAllTextAsync(list, $"file '{a.Path.Replace("\\", "/")}'\nfile '{b.Path.Replace("\\", "/")}'\n");
            await Ff.Ffmpeg("-f", "concat", "-safe", "0", "-i", list, "-c", "copy", tmp);
        });
        return new Clip(path, 6, 60, a.Events.Concat(b.Events.Select(e => e + 3)).ToArray(), true, 320, 180);
    }

    public static Task<string> MusicAsync(int seed = 7, double seconds = 60) =>
        CachedAsync($"music-s{seed}-{Ff.N(seconds)}s.wav", tmp => Ff.Ffmpeg("-f", "lavfi", "-i",
            $"aevalsrc=exprs='0.5*sin(2*PI*{Ff.N(MusicHz)}*t)*({Windows(Events(seed, seconds, 1000), 0.04)})+0.2*sin(2*PI*{Ff.N(MusicBedHz)}*t)':s=48000:d={Ff.N(seconds)}",
            "-ac", "2", "-c:a", "pcm_s16le", tmp));

    public static double[] MusicEvents(int seed = 7, double seconds = 60) => Events(seed, seconds, 1000);

    /// <summary>The beat times of a music file made by <see cref="MusicAsync"/> (seed and length are in its name).</summary>
    public static double[] MusicEventsFor(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(System.IO.Path.GetFileName(path), @"music-s(\d+)-([\d.]+)s");
        return MusicEvents(int.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static Task<string> MemeAsync(double seconds = 2, string tag = "a") =>
        CachedAsync($"meme-{tag}-{Ff.N(seconds)}s.mp4", tmp => Ff.Ffmpeg(
            "-f", "lavfi", "-i", $"color=c=0x808080:s=320x180:r=60:d={Ff.N(seconds)}",
            "-f", "lavfi", "-i", $"sine=frequency={Ff.N(MemeHz)}:sample_rate=48000:duration={Ff.N(seconds)}",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-shortest", tmp));

    public static Task<string> AudioOnlyAsync(int seed = 9, double seconds = 10) =>
        CachedAsync($"audioonly-s{seed}-{Ff.N(seconds)}s.m4a", tmp => Ff.Ffmpeg("-f", "lavfi", "-i",
            $"aevalsrc=exprs='0.5*sin(2*PI*{Ff.N(GameplayHz)}*t)*({Windows(Events(seed, seconds, 1000), 0.05)})':s=48000:d={Ff.N(seconds)}",
            "-c:a", "aac", "-ac", "2", tmp));

    /// <summary>A voice-over take in OUTPUT time: a 900 Hz pulse at each given output second.</summary>
    public static async Task<string> VoiceAsync(string work, double[] outputEvents, double seconds)
    {
        string path = System.IO.Path.Combine(work, "voice.wav");
        await Ff.Ffmpeg("-f", "lavfi", "-i",
            $"aevalsrc=exprs='0.6*sin(2*PI*{Ff.N(VoiceHz)}*t)*({Windows(outputEvents, 0.06)})':s=48000:d={Ff.N(seconds)}",
            "-ac", "1", "-c:a", "pcm_s16le", path);
        return path;
    }
}
