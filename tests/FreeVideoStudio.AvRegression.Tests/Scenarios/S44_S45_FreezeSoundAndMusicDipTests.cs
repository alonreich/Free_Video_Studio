using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

[Collection(AvCollection.Name)]
public sealed class S44_S45_FreezeSoundAndMusicDipTests
{
    private static float[] ReadF32(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
        return f;
    }

    private static double Db(float[] s, double fromSec, double toSec, int sr = 48000)
    {
        int a = Math.Clamp((int)(fromSec * sr), 0, s.Length), z = Math.Clamp((int)(toSec * sr), a, s.Length);
        double sum = 0;
        for (int i = a; i < z; i++) sum += s[i] * (double)s[i];
        return 10 * Math.Log10(Math.Max(1e-18, sum / Math.Max(1, z - a)));
    }

    [Theory, Trait("Tier", "Quick")]
    [InlineData(FreezeSound.Church_Bell, 0.5)]
    [InlineData(FreezeSound.Church_Bell, 3.0)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 0.5)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 1.0)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 3.0)]
    public async Task S44_FreezeSound_TheExportPlaysExactlyWhatTheSpeedEditorPreviews(FreezeSound sound, double seconds)
    {
        string work = Repo.WorkDir($"S44-{sound}-{seconds:0.0}");
        string raw = Path.Combine(work, "export-freeze.f32");
        await Ff.Ffmpeg("-filter_complex", FreezeSoundEffect.Source(seconds, "[s]", sound) + ";[s]pan=mono|c0=c0[o]", "-map", "[o]", "-f", "f32le", raw);
        float[] export = ReadF32(raw);
        float[] preview = FreezeSoundEffect.Render(seconds, sound);
        Av.True($"{seconds:0.0} s freeze ({sound}): the export's sound lasts the hold plus its ring-out", Math.Abs(export.Length - preview.Length) <= 1,
            $"export {export.Length} samples, preview {preview.Length}");
        double worst = 0; int at = 0;
        for (int i = 0; i < Math.Min(export.Length, preview.Length); i++)
        {
            double d = Math.Abs(export[i] - preview[i]);
            if (d > worst) { worst = d; at = i; }
        }
        Av.True($"PREVIEW ≠ EXPORT: the {seconds:0.0} s freeze sound in the Speed Editor is the exported sound", worst <= 0.003,
            $"differs by {worst:0.0000} at {at / 48000.0:0.000}s");
        Av.True($"{seconds:0.0} s freeze sound ({sound}) is loud enough to be heard", Db(export, 0, seconds) > -30, $"{Db(export, 0, seconds):0.0} dBFS");
    }

    private static async Task<float[]> PreviewThroughMpvAsync(string input, string af, string work, string tag)
    {
        string wav = Path.Combine(work, tag + "-preview.wav");
        await using (var p = PlayerRig.Create("freeze-music", audioOnly: true, extra: new Dictionary<string, string>
        {
            ["ao"] = "pcm", ["ao-pcm-file"] = wav, ["ao-pcm-waveheader"] = "yes", ["volume"] = "100",
            ["audio-samplerate"] = "48000", ["audio-channels"] = "mono",
        }))
        {
            await p.Cmd(p.Ipc.SetPropertyAsync("af", af), "the preview music filter (mpv accepts the export's dip chain)");
            await p.Cmd(p.Ipc.LoadFileAsync(input, 0), "load music");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool Done() => p.Ipc.IsEof || (p.Ipc.Duration > 0 && p.Ipc.CurrentTime >= p.Ipc.Duration - 0.02);
            while (!Done() && sw.ElapsedMilliseconds < 20000) await Task.Delay(50);
            Av.True("the preview music played to its end", Done(), $"stopped at {p.Ipc.CurrentTime:0.00}s");
            await Task.Delay(300);
        }
        string raw = Path.Combine(work, tag + "-preview.f32");
        await Ff.Ffmpeg("-i", wav, "-ac", "1", "-ar", "48000", "-f", "f32le", raw);
        return ReadF32(raw);
    }

    [Theory, Trait("Tier", "Quick")]
    [InlineData(300, "low (the music's body)")]
    [InlineData(5000, "treble")]
    public async Task S45_FreezeMusic_KeepsPlaying_Ducked6dB_CarvedAndMuffled_PreviewEqualsExport(int hz, string what)
    {
        string work = Repo.WorkDir($"S45-{hz}");
        string input = Path.Combine(work, $"music-{hz}.wav");
        await Ff.Ffmpeg("-f", "lavfi", "-i", $"sine=frequency={hz}:sample_rate=48000:duration=7", "-af", "volume=0.3", "-ac", "1", input);
        var windows = new List<(double, double)> { (2.0, 3.0) };
        string pulse = FreezeSoundEffect.MusicDipPulse(windows)!;

        string exportRaw = Path.Combine(work, "export.f32");
        await Ff.Ffmpeg("-i", input, "-filter_complex", string.Join(";", FreezeSoundEffect.MusicDipChains("[0:a]", pulse, "fzd", "[o]")),
            "-map", "[o]", "-ac", "1", "-f", "f32le", exportRaw);
        float[] export = ReadF32(exportRaw);

        var plan = new List<MusicBedSegment> { new(input, 0, 7, 0) };
        string af = MusicBedPreviewPlayer.FreezeAf(plan, 0, windows);
        Av.True("the preview builds a music filter for the freeze", af.Length > 0);
        float[] preview = await PreviewThroughMpvAsync(input, af, work, $"m{hz}");

        double before = Db(export, 0.5, 1.5), during = Db(export, 2.4, 2.95), after = Db(export, 3.35, 3.75);
        if (hz < 1000)
        {
            Av.True($"EXPORT: the music keeps playing under the freeze ({what})", during - before > -12,
                $"{during - before:+0.0;-0.0} dB — it drops out");
            Av.True($"EXPORT: the music dips under the freeze ({what}, about −6 dB plus the carve)", during - before < -5,
                $"{during - before:+0.0;-0.0} dB");
        }
        else
            Av.True($"EXPORT: the music is muffled under the freeze ({what})", during - before < -18, $"{during - before:+0.0;-0.0} dB");
        Av.True($"EXPORT: the music comes back after the freeze ({what})", Math.Abs(after - before) < 0.6, $"{after - before:+0.0;-0.0} dB");

        foreach (var (name, from, to) in new[] { ("before", 0.5, 1.5), ("under", 2.4, 2.95), ("after", 3.35, 3.75) })
        {
            double e = Db(export, from, to), pv = Db(preview, from, to);
            Av.True($"PREVIEW ≠ EXPORT: the music {name} the freeze ({what}) sounds the same in the preview", Math.Abs(e - pv) <= 1.0,
                $"export {e:0.0} dBFS, preview {pv:0.0} dBFS");
        }
    }
}
