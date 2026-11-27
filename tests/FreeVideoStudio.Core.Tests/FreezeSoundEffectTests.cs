using System;
using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class FreezeSoundEffectTests
{
    [Fact]
    public void FreezeAudio_IsTheChosenSoundOverlaidAtTheHold_NotSilence()
    {
        var segments = new List<SpeedSegment> { new(2000, 3500, 0) };
        var (graph, _, _, audio, finalDuration, _) = GranularSpeedBuilder.Build(10000, segments, 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false, freezeSound: FreezeSound.Church_Bell);
        Assert.Contains("amovie=filename='", graph);
        Assert.Contains("adelay=delays=96000S:all=1[a_freeze_sfx_0]", graph);
        Assert.Contains("[a_speed_body][a_freeze_sfx_0]amix=inputs=2:normalize=0:duration=first", graph);
        Assert.DoesNotContain("aevalsrc", graph);
        Assert.Equal("[a_speed_out]", audio);
        Assert.Equal(11.5, finalDuration, 1);
    }

    [Fact]
    public void NoFreeze_NoSoundOverlay()
    {
        var (graph, _, _, _, _, _) = GranularSpeedBuilder.Build(10000, new List<SpeedSegment> { new(2000, 3500, 0.5) }, 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false);
        Assert.DoesNotContain("amovie", graph);
        Assert.DoesNotContain("a_freeze_sfx", graph);
    }

    [Theory]
    [InlineData(FreezeSound.Church_Bell)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down)]
    [InlineData(FreezeSound.Church_Bell_Mid)]
    [InlineData(FreezeSound.Church_Bell_Late)]
    public void Assets_AreEmbedded_48kMono(FreezeSound sound)
    {
        float[] a = FreezeSoundEffect.Asset(sound);
        Assert.InRange(a.Length / 48000.0, 2.9, 5.0);
        Assert.InRange(a.Max(v => Math.Abs(v)), 0.4f, 0.95f);
    }

    [Fact]
    public void Choices_DefaultIsTheBell_AndUnknownNamesFallBackToIt()
    {
        Assert.Equal(FreezeSound.Church_Bell, FreezeSoundEffect.Default);
        Assert.Equal(4, FreezeSoundEffect.Choices.Count);
        Assert.Equal(FreezeSound.Church_Bell, FreezeSoundEffect.Parse(null));
        Assert.Equal(FreezeSound.Church_Bell, FreezeSoundEffect.Parse("Whoosh"));
        Assert.Equal(FreezeSound.Cinematic_Dramatic_Slow_Down, FreezeSoundEffect.Parse("cinematic_dramatic_slow-down"));
        foreach (var c in FreezeSoundEffect.Choices) Assert.Equal(c.Sound, FreezeSoundEffect.Parse(FreezeSoundEffect.NameOf(c.Sound)));
    }

    [Theory]
    [InlineData(FreezeSound.Church_Bell, 0.5)]
    [InlineData(FreezeSound.Church_Bell, 3.0)]
    [InlineData(FreezeSound.Church_Bell, 10.0)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 0.2)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 0.5)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 1.7)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 3.0)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 10.0)]
    [InlineData(FreezeSound.Church_Bell_Mid, 0.5)]
    [InlineData(FreezeSound.Church_Bell_Mid, 3.0)]
    [InlineData(FreezeSound.Church_Bell_Late, 0.5)]
    [InlineData(FreezeSound.Church_Bell_Late, 3.0)]
    public void Sound_LastsTheHoldPlusItsTail_StartsOnTheFrame_AndEndsSilent(FreezeSound sound, double seconds)
    {
        float[] s = FreezeSoundEffect.Render(seconds, sound);
        Assert.Equal((int)Math.Round((seconds + FreezeSoundEffect.TailSec(sound)) * 48000), s.Length);
        Assert.InRange(s.Max(v => Math.Abs(v)), 0.3, FreezeSoundEffect.TargetPeak + 1e-6);
        int onset = Array.FindIndex(s, v => Math.Abs(v) > 0.05f);
        Assert.InRange(onset, 0, 48000 * 30 / 1000);
        Assert.True(Math.Abs(s[^1]) < 0.01f, $"ends at {s[^1]:0.000}, it would click");
    }

    [Fact]
    public void Bell_RingsOutPastTheHold_FadingToSilence()
    {
        float[] asset = FreezeSoundEffect.Asset(FreezeSound.Church_Bell);
        float[] s = FreezeSoundEffect.Render(1.0, FreezeSound.Church_Bell);
        for (int i = 0; i < 48000; i += 97) Assert.Equal(asset[i], s[i], 5);
        Assert.Equal(asset[48000 + 19200] * 0.5, s[48000 + 19200], 3);
        Assert.Equal(0.8, FreezeSoundEffect.TailSec(FreezeSound.Church_Bell));
        Assert.Equal(0.8, FreezeSoundEffect.TailSec(FreezeSound.Church_Bell_Mid));
        Assert.Equal(0.8, FreezeSoundEffect.TailSec(FreezeSound.Church_Bell_Late));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Cinematic_Dramatic_Slow_Down_SpliceKeepsTheHitAndTheClosingSwell(double seconds)
    {
        float[] asset = FreezeSoundEffect.Asset(FreezeSound.Cinematic_Dramatic_Slow_Down);
        float[] s = FreezeSoundEffect.Render(seconds, FreezeSound.Cinematic_Dramatic_Slow_Down);
        Assert.Equal(0, FreezeSoundEffect.TailSec(FreezeSound.Cinematic_Dramatic_Slow_Down));
        for (int i = 0; i < 4800; i += 7) Assert.Equal(asset[i], s[i], 3);
        int fade = (int)Math.Round(Math.Min(0.03, seconds / 4) * 48000);
        int from = s.Length - fade - 4800;
        int offset = asset.Length - s.Length;
        for (int i = from; i < s.Length - fade; i += 7) Assert.Equal(asset[i + offset], s[i], 3);
    }

    [Fact]
    public void Cinematic_Dramatic_Slow_Down_LongHoldPlaysTheWholeSoundThenSilence()
    {
        float[] asset = FreezeSoundEffect.Asset(FreezeSound.Cinematic_Dramatic_Slow_Down);
        float[] s = FreezeSoundEffect.Render(5.0, FreezeSound.Cinematic_Dramatic_Slow_Down);
        Assert.Equal(240000, s.Length);
        Assert.Equal(asset[48000], s[48000], 3);
        Assert.Equal(0f, s[asset.Length + 10]);
    }

    [Fact]
    public void RenderedFile_IsAFloatWav_TheExportReadsSampleForSample()
    {
        string path = FreezeSoundEffect.RenderedFile(0.7, FreezeSound.Cinematic_Dramatic_Slow_Down);
        float[] s = FreezeSoundEffect.Render(0.7, FreezeSound.Cinematic_Dramatic_Slow_Down);
        byte[] b = System.IO.File.ReadAllBytes(path);
        Assert.Equal(46 + s.Length * 4, b.Length);
        Assert.Equal(3, BitConverter.ToInt16(b, 20));
        Assert.Equal(s[1000], BitConverter.ToSingle(b, 46 + 4000));
        Assert.Equal(path, FreezeSoundEffect.RenderedFile(0.7, FreezeSound.Cinematic_Dramatic_Slow_Down));
        string chain = FreezeSoundEffect.Source(0.7, "[x]", FreezeSound.Cinematic_Dramatic_Slow_Down, 4800);
        Assert.Contains("pan=stereo|c0=c0|c1=c0", chain);
        Assert.EndsWith("adelay=delays=4800S:all=1[x]", chain);
    }

    [Fact]
    public void FilterPath_EscapesTheDriveColonAndQuotes()
    {
        Assert.Equal(@"C\:/Users/O'\''Neil/a b/x.wav", FreezeSoundEffect.FilterPath(@"C:\Users\O'Neil\a b\x.wav"));
    }

    [Fact]
    public void FreezeOutputRanges_AreWhereTheHoldsPlay()
    {
        var timeline = OutputTimeline.Create(
            totalDurationMs: 10000,
            segments: new List<SpeedSegment> { new(2000, 4000, 0.5), new(6000, 7000, 0) },
            baseSpeed: 1.0,
            sourceCutStartMs: 0);
        var ranges = timeline.FreezeOutputRanges();
        Assert.Single(ranges);
        Assert.Equal(8.0, ranges[0].StartOutputSec, 3);
        Assert.Equal(9.0, ranges[0].EndOutputSec, 3);
    }

    [Fact]
    public void MusicDip_RampsAroundEachFreeze_AndIsNullWithoutOne()
    {
        Assert.Null(FreezeSoundEffect.MusicDipPulse(new List<(double, double)>()));
        string? pulse = FreezeSoundEffect.MusicDipPulse(new List<(double, double)> { (8.0, 9.0) });
        Assert.NotNull(pulse);
        Assert.Contains("7.85", pulse);
        Assert.Contains("9.3", pulse);
        string chains = string.Join(";", FreezeSoundEffect.MusicDipChains("[m]", pulse!, "fzd", "[out]"));
        Assert.Contains("volume='1-0.5*(", chains);
        Assert.Contains("lowpass=f=1100", chains);
        Assert.Contains(FreezeSoundEffect.MusicCarveEq, chains);
    }

    [Fact]
    public void MusicChain_AppliesTheDip_WhenGiven()
    {
        var music = new System.Text.Json.Nodes.JsonObject { ["path"] = "song.mp3", ["volume"] = 1.0 };
        var tracks = new List<MusicTrack> { new("song.mp3", 0, 30) };
        var (chains, _) = AudioFilterChain.Build(music, 0, 10, 1.0, false, 0, null, musicTracks: tracks,
            freezeMusicDipPulse: FreezeSoundEffect.MusicDipPulse(new List<(double, double)> { (2, 3) }));
        Assert.Contains(chains, c => c.Contains("[a_bg_freeze_dip]"));
    }

    [Theory]
    [InlineData(FreezeSound.Church_Bell, -18.2, -4.5)]
    [InlineData(FreezeSound.Church_Bell_Mid, -18.2, -4.1)]
    [InlineData(FreezeSound.Church_Bell_Late, -18.2, -3.2)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, -18.2, -2.5)]
    [InlineData(FreezeSound.Church_Bell, null, -6.3)]
    [InlineData(FreezeSound.Church_Bell, -80.0, -6.3)]
    [InlineData(FreezeSound.Church_Bell, -60.0, -30.0)]
    [InlineData(FreezeSound.Church_Bell, -5.0, 6.0)]
    public void Gain_PutsTheSoundAt83PercentOfTheVideosLoudness(FreezeSound sound, double? videoLufs, double expectedDb)
    {
        Assert.Equal(expectedDb, FreezeSoundEffect.GainDbFor(videoLufs, sound), 1);
    }

    [Fact]
    public void Gain_ScalesTheRender_AndTheExportReadsTheSameSamples()
    {
        float[] plain = FreezeSoundEffect.Render(1.0, FreezeSound.Church_Bell, 0);
        float[] quiet = FreezeSoundEffect.Render(1.0, FreezeSound.Church_Bell, -6.0);
        for (int i = 2000; i < 40000; i += 101) Assert.Equal(plain[i] * Math.Pow(10, -6.0 / 20), quiet[i], 4);
        string path = FreezeSoundEffect.RenderedFile(1.0, FreezeSound.Church_Bell, -6.0);
        Assert.EndsWith("_g-60.wav", path);
        byte[] b = System.IO.File.ReadAllBytes(path);
        Assert.Equal(quiet[3000], BitConverter.ToSingle(b, 46 + 12000));
    }

    [Fact]
    public void Export_UsesTheVideosLoudnessForTheFreezeSound()
    {
        var segments = new List<SpeedSegment> { new(2000, 3500, 0) };
        var (graph, _, _, _, _, _) = GranularSpeedBuilder.Build(10000, segments, 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false,
            freezeSound: FreezeSound.Church_Bell, freezeVideoLufs: -18.2);
        Assert.Contains("_g-45.wav", graph);
    }

    [Fact]
    public void PreviewVolume_FollowsThePlayersCurve_NotTheRawSliderValue()
    {
        string root = AppContext.BaseDirectory;
        while (!System.IO.File.Exists(System.IO.Path.Combine(root, "FreeVideoStudio.sln"))) root = System.IO.Path.GetDirectoryName(root)!;
        string editor = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        string settings = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "FreeVideoStudio.App", "Controls", "SettingsWindow.axaml.cs"));
        Assert.Contains("_freezeSound.PlayHold(_edit.FreezeDurationS, FreezeLoudnessPath, SpeedEditorGameBalance, FreezeTailRoomSec());", editor);
        string preview = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "FreeVideoStudio.App", "Services", "FreezeSoundPreview.cs"));
        Assert.Contains("MpvIpcClient.MasterLinearGain *", preview);
        Assert.Contains("FreezeSoundPreview.OutputVolume(1.0)", settings);
        Assert.DoesNotContain("GlobalMasterVolume / 100.0", editor + settings);
    }

    private static double[] HighPass(float[] x, double hz, int sampleRate = 48000)
    {
        var y = x.Select(v => (double)v).ToArray();
        for (int pass = 0; pass < 2; pass++)
        {
            double w0 = 2 * Math.PI * hz / sampleRate, alpha = Math.Sin(w0) / (2 * 0.70710678118654752), cos = Math.Cos(w0), a0 = 1 + alpha;
            double b0 = (1 + cos) / 2 / a0, b1 = -(1 + cos) / a0, b2 = (1 + cos) / 2 / a0, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double xi = y[i], yi = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi; y[i] = yi;
            }
        }
        return y;
    }

    private static double Rms(IEnumerable<double> v) { var a = v.ToArray(); return a.Length == 0 ? 0 : Math.Sqrt(a.Sum(x => x * x) / a.Length); }

    private static double Db(double rms) => 20 * Math.Log10(Math.Max(1e-9, rms));

    [Theory]
    [InlineData(FreezeSound.Church_Bell, 0.5)]
    [InlineData(FreezeSound.Church_Bell, 3.0)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 0.5)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 3.0)]
    [InlineData(FreezeSound.Church_Bell_Mid, 0.5)]
    [InlineData(FreezeSound.Church_Bell_Late, 0.5)]
    public void Sound_IsClearOnAPhoneSpeaker(FreezeSound sound, double seconds)
    {
        float[] s = FreezeSoundEffect.Render(seconds, sound);
        int n = (int)(Math.Min(0.5, seconds) * 48000);
        double phone = Rms(HighPass(s, 400).Take(n));
        Assert.True(Db(phone) >= -30, $"through a phone speaker (400 Hz high-pass) it is only {Db(phone):0.0} dBFS");
    }

    [Fact]
    public void MusicDipPulseAt_MatchesTheExportPulse()
    {
        var windows = new List<(double, double)> { (2.0, 3.0) };
        Assert.Equal(0, FreezeSoundEffect.MusicDipPulseAt(1.0, windows));
        Assert.Equal(1, FreezeSoundEffect.MusicDipPulseAt(2.0, windows), 3);
        Assert.Equal(1, FreezeSoundEffect.MusicDipPulseAt(2.5, windows));
        Assert.Equal(0.5, FreezeSoundEffect.MusicDipPulseAt(3.15, windows), 3);
        Assert.Equal(0, FreezeSoundEffect.MusicDipPulseAt(3.3, windows));
    }
}
