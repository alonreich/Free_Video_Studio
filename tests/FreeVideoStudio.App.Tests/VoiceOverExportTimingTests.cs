using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace FreeVideoStudio.App.Tests;

/// <summary>Actual decoded picture/audio parity; requires repository bundled FFmpeg. Artifacts are retained.</summary>
public class VoiceOverExportTimingTests(ITestOutputHelper log)
{
    [Theory]
    [InlineData("normal", 1, false, false, 10)]
    [InlineData("slow", 0.5, false, false, 10)]
    [InlineData("fast", 2, false, false, 10)]
    [InlineData("cuts-freeze-rates", 1, true, false, 10)]
    [InlineData("memes-continuous-music", 1, true, true, 10)]
    [InlineData("long", 1, true, false, 125)]
    public async Task DecodedVoicePulsesStayWithThePicture(string name, double speed, bool edit, bool meme, int sourceSeconds)
    {
        string ffmpeg = Path.Combine(RepoRoot.Path, "binaries", "ffmpeg.exe");
        string ffprobe = Path.Combine(RepoRoot.Path, "binaries", "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg), "This integration test requires bundled FFmpeg.");
        string work = Path.Combine(RepoRoot.Path, "tests", "MediaPipelineChecks", "artifacts", "voice-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(work, "state"));
        string source = Path.Combine(work, "source.mkv");
        async Task Run(params string[] args)
        {
            var p = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-n", "-threads", "2" }.Concat(args)) p.ArgumentList.Add(a);
            var result = await AsyncProcessRunner.RunAsync(p, cancellationToken: new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        await Run("-f", "lavfi", "-i", $"color=c=black:s=160x90:r=60:d={sourceSeconds},drawbox=x=0:y=0:w=iw:h=ih:c=white:t=fill:enable='lt(mod(t,1),0.15)'",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "10", source);
        var segments = edit ? new List<SpeedSegment> { new(2000, 3000, 0.5), new(4000, 5000, 0), new(6000, 8000, 2) } : [];
        var cuts = edit ? new List<CutRange> { new(3000, 3500) } : [];
        var plan = new VoiceOverPlaybackPlan(1, sourceSeconds - 1, segments, speed, cuts);
        string preview = Path.Combine(work, "preview.mkv");
        await VoiceOverPreviewRenderer.RenderAsync(ffmpeg, ffprobe, source, preview, Path.Combine(work, "preview.txt"), plan, CancellationToken.None);
        async Task<double[]> PictureEvents(string path, string suffix)
        {
            string raw = Path.Combine(work, suffix + ".gray");
            await Run("-i", path, "-an", "-vf", "scale=1:1,format=gray", "-f", "rawvideo", raw);
            byte[] frames = await File.ReadAllBytesAsync(raw);
            var events = new List<double>();
            for (int i = 0; i < frames.Length; i++)
                if (frames[i] > 170 && (i == 0 || frames[i - 1] < 100)) events.Add(i / 60.0);
            return events.ToArray();
        }
        double[] spokenEvents = await PictureEvents(preview, "preview");
        Assert.True(spokenEvents.Length > 4);
        string voice = Path.Combine(work, "voice.wav");
        var captureDevices = new SampleCaptureDevices();
        captureDevices.Input.WaveFormat = new WaveFormat(48000, 16, 1);
        using (var recorder = captureDevices.CreateRecorder(voice, 0))
        {
            recorder.ConfigureBudget(new VoiceCaptureBudget(plan.Timeline.TotalOutputSeconds, () => 0));
            recorder.StartRecording();
            int count = (int)VoiceCaptureBudget.FrameCount(plan.Timeline.TotalOutputSeconds, 0, 48000);
            var pcm = new byte[(count + 4800) * 2];
            int eventIndex = 0;
            for (int i = 0; i < count; i++)
            {
                double t = i / 48000.0;
                while (eventIndex + 1 < spokenEvents.Length && t >= spokenEvents[eventIndex + 1]) eventIndex++;
                short sample = (short)(32767 * (t - spokenEvents[eventIndex] < 0.065 ? 0.6 * Math.Sin(2 * Math.PI * 900 * t) : 0));
                pcm[2 * i] = (byte)sample;
                pcm[2 * i + 1] = (byte)(sample >> 8);
            }
            Array.Fill(pcm, (byte)0x71, count * 2, 9600);
            recorder.StopRequested += (_, _) => { };
            captureDevices.Input.Deliver(pcm);
            captureDevices.Input.Deliver(Enumerable.Repeat((byte)0x71, 9600).ToArray());
            recorder.StopRecording();
            Assert.Equal(count * 2, recorder.BytesCaptured);
            Assert.Equal(pcm[..(count * 2)], SampleCaptureDevices.ReadPcm(voice));
        }
        string memePath = Path.Combine(work, "meme.mkv"), music = Path.Combine(work, "music.wav");
        if (meme)
        {
            await Run("-f", "lavfi", "-i", "color=c=black:s=160x90:r=60:d=1", "-c:v", "libx264", "-preset", "ultrafast", memePath);
            await Run("-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "30", "-c:a", "pcm_s16le", music);
        }
        using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "worker")))
        {
            InputPath = source, OutputDirectory = Path.Combine(work, "export"),
            StartTimeMs = 1000, EndTimeMs = (sourceSeconds - 1) * 1000,
            SpeedFactor = speed, SpeedSegments = segments, Cuts = cuts,
            OriginalResolution = "160x90", IsMobileFormat = false, HardwareStrategy = "CPU", QualityLevel = 20,
            EnableFades = false, AutoSpikeFlattening = false,
            VoiceOverTakes = [new(voice, 1, 0, plan.Timeline.TotalOutputSeconds)],
            VoiceOverDuckAudio = false, VoiceOverProtectFromMusic = true,
            KeepMusicDuringMeme = meme,
            MemePlacements = meme ? [new(memePath, 4, 1, "test-meme")] : [],
            MusicConfig = meme ? new JsonObject { ["path"] = music, ["volume"] = 1.0, ["ducking_enabled"] = false, ["carving_enabled"] = false } : null
        };
        Directory.CreateDirectory(worker.OutputDirectory);
        bool success = false; string exported = "";
        worker.Finished += (ok, text) => { success = ok; exported = text; };
        var logs = new List<string>();
        var oldInfo = CoreLogger.InfoAction; var oldFail = CoreLogger.FailAction;
        CoreLogger.InfoAction = (topic, message) => { lock (logs) logs.Add(topic + ": " + message); };
        CoreLogger.FailAction = CoreLogger.InfoAction;
        try { await worker.RunAsync(); }
        finally { CoreLogger.InfoAction = oldInfo; CoreLogger.FailAction = oldFail; await File.WriteAllLinesAsync(Path.Combine(work, "export.log"), logs); }
        Assert.True(success, worker.FailureDetail ?? exported);
        double[] pictureEvents = await PictureEvents(exported, "export");
        string rawAudio = Path.Combine(work, "export.f32");
        await Run("-i", exported, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", rawAudio);
        byte[] bytes = await File.ReadAllBytesAsync(rawAudio);
        float[] samples = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        var audioEvents = new List<double>(); bool wasLoud = false;
        for (int i = 0; i + 48 <= samples.Length; i += 48)
        {
            double energy = 0; for (int k = 0; k < 48; k++) energy += samples[i + k] * samples[i + k];
            bool loud = Math.Sqrt(energy / 48) > 0.05;
            if (loud && !wasLoud) audioEvents.Add(i / 48000.0);
            wasLoud = loud;
        }
        log.WriteLine("Artifacts: " + work);
        Assert.Equal(spokenEvents.Length, pictureEvents.Length);
        Assert.Equal(pictureEvents.Length, audioEvents.Count);
        double insertedAt = plan.OutputAt(5);
        for (int i = 0; i < pictureEvents.Length; i++)
        {
            double expected = spokenEvents[i] + (meme && spokenEvents[i] >= insertedAt - 0.001 ? 1 : 0);
            Assert.InRange(Math.Abs(pictureEvents[i] - expected), 0, 0.034);
            Assert.InRange(Math.Abs(audioEvents[i] - pictureEvents[i]), 0, 0.04);
        }
        log.WriteLine($"Verified {pictureEvents.Length} picture/voice markers over {plan.Timeline.TotalOutputSeconds:F2}s; tolerance 40ms.");
    }
}
