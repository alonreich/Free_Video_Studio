using System.Diagnostics;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace FreeVideoStudio.App.Tests;

/// <summary>VOLEVEL_01: measure actual preview WAV and AAC export against the live take reader.</summary>
public class VoiceOverLevelTests(ITestOutputHelper log)
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0.5, false)]
    [InlineData(1, false)]
    [InlineData(1.5, false)]
    [InlineData(1.5, true)]
    public async Task LiveVoiceRenderedPreviewAndExportHaveTheSameLevel(double gain, bool meme)
    {
        string work = Path.Combine(FreeVideoStudio.Testing.TestArtifactDirectory.SuiteWork(FreeVideoStudio.Testing.TestOutputSuite.Name), "voice-level-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(work, "state"));
        string ffmpeg = Path.Combine(RepoRoot.Path, "binaries", "ffmpeg.exe");
        async Task Run(params string[] args)
        {
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-n" }.Concat(args)) start.ArgumentList.Add(arg);
            var result = await AsyncProcessRunner.RunAsync(start, TimeSpan.FromMinutes(2));
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        string source = Path.Combine(work, "source.mkv"), voice = Path.Combine(work, "voice.wav");
        await Run("-f", "lavfi", "-i", "color=s=160x90:r=60:d=6", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=6",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_s16le", source);
        await Run("-f", "lavfi", "-i", "sine=frequency=900:sample_rate=44100:duration=4", "-c:a", "pcm_s16le", voice);
        string cutaway = Path.Combine(work, "meme.mkv"), music = Path.Combine(work, "music.wav");
        if (meme)
        {
            await Run("-f", "lavfi", "-i", "color=s=160x90:r=60:d=1", "-c:v", "libx264", "-preset", "ultrafast", cutaway);
            await Run("-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=6", "-c:a", "pcm_s16le", music);
        }
        using var liveReader = new WavAudioReader(voice) { Volume = VoiceOverLevel.PreviewGain(gain, 1) };
        var live = new float[44100];
        Assert.Equal(live.Length, liveReader.Read(live, 0, live.Length));
        double expected = Math.Sqrt(live.Average(s => (double)s * s));
        string preview = Path.Combine(work, "preview.wav");
        foreach (bool renderPreview in new[] { true, false })
        {
            using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, renderPreview ? "preview-state" : "export-state")))
            {
                InputPath = source, OutputDirectory = Path.Combine(work, "output"),
                StartTimeMs = 1000, EndTimeMs = 5000, SpeedFactor = 1,
                OriginalResolution = "160x90", IsMobileFormat = false, HardwareStrategy = "CPU", QualityLevel = 20,
                EnableFades = false, AutoSpikeFlattening = false,
                VoiceOverTakes = [new(voice, 1, Gain: gain)],
                VoiceOverDuckAudio = false, VoiceOverProtectFromMusic = false,
                AudioPreviewOutputPath = renderPreview ? preview : null,
                KeepMusicDuringMeme = meme,
                MemePlacements = meme ? [new(cutaway, 2, 1, "test-meme")] : [],
                MusicConfig = meme ? new JsonObject { ["path"] = music, ["volume"] = 1.0, ["ducking_enabled"] = false, ["carving_enabled"] = false } : null
            };
            Directory.CreateDirectory(worker.OutputDirectory);
            bool success = false; string output = "";
            worker.Finished += (ok, message) => { success = ok; output = message; };
            await worker.RunAsync();
            Assert.True(success, worker.FailureDetail ?? output);
            string raw = Path.Combine(work, renderPreview ? "preview.f32" : "export.f32");
            await Run("-i", output, "-vn", "-ac", "2", "-ar", "48000", "-f", "f32le", raw);
            var bytes = await File.ReadAllBytesAsync(raw);
            var pcm = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
            foreach (double start in meme ? new[] { 0.6, 3.6 } : new[] { 0.6, 2.6 })
                for (int channel = 0; channel < 2; channel++)
                {
                    int first = (int)(start * 48000), count = 24000;
                    Assert.True(pcm.Length >= (first + count) * 2);
                    double energy = 0;
                    for (int i = first; i < first + count; i++) energy += Math.Pow(pcm[i * 2 + channel], 2);
                    double actual = Math.Sqrt(energy / count);
                    Assert.InRange(Math.Abs(actual - expected), 0, Math.Max(0.00005, expected * 0.025));
                    log.WriteLine($"gain={gain}, preview={renderPreview}, meme={meme}, channel={channel}: live={expected:F6}, decoded={actual:F6}");
                }
        }
        log.WriteLine("Artifacts retained: " + work);
    }
}
