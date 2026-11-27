using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Testing;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed class MemeResultRenderTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task PendingPartSoundAndModeAppearInTheRenderedResult()
    {
        string root = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "MemeResult", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(root, "state"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string ffmpeg = Path.Combine(RepoRoot.Path, "binaries", "ffmpeg.exe");
        string ffprobe = Path.Combine(RepoRoot.Path, "binaries", "ffprobe.exe");
        async Task Run(params string[] args)
        {
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-n", "-threads", "2" }.Concat(args)) start.ArgumentList.Add(arg);
            var result = await AsyncProcessRunner.RunAsync(start, cancellationToken: timeout.Token);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        string source = Path.Combine(root, "source.mp4");
        string meme = Path.Combine(root, "meme.mp4");
        await Run("-f", "lavfi", "-i", "color=c=blue:s=160x90:r=60:d=4", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", source);
        await Run("-f", "lavfi", "-i", "color=c=green:s=160x90:r=60:d=4,drawbox=x=0:y=0:w=iw:h=ih:color=red:t=fill:enable='gte(t,2)'",
            "-f", "lavfi", "-i", @"aevalsrc=if(lt(t\,2)\,0.1*sin(2*PI*300*t)\,0.1*sin(2*PI*900*t)):s=48000:d=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", meme);
        var basis = new ExportPayload
        {
            InputPath = source, OutputDirectory = root, LoadedVideoDurationMs = 4000, TrimEndMs = 4000,
            BaseSpeed = 1, HardwareMode = "CPU", QualityLevel = 2, EnableFades = false, ApplyPeakFlattening = false,
        };
        async Task<string> Render(string name, MemePresentationMode mode, bool sound)
        {
            var pending = new MemePlacement(meme, 2, 1.5, "pending", mode, PlaySound: sound, ClipInSec: 2,
                Animation: MemeOverlayAnimation.Fade, Transition: MemeTransition.Fade);
            var payload = basis with { OutputBaseName = name, MemePlacements = [pending] };
            var result = await new MainMediaController().ExecuteExportAsync(payload, timeout.Token, _ => { }, (_, _, _) => { });
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(File.Exists(result.OutputPath));
            return result.OutputPath!;
        }
        string full = await Render("full", MemePresentationMode.InlineFullScreen, true);
        string corner = await Render("corner", MemePresentationMode.CornerOverlay, true);
        string silent = await Render("silent", MemePresentationMode.InlineFullScreen, false);
        var merger = new VideoMergerWindow();
        string merged;
        try
        {
            var edl = new MergeEdl { Clips = [
                new EdlClip { ClipId = Guid.NewGuid(), Path = source, DurationUs = 4000000, OutUs = 2000000 },
                new EdlClip { ClipId = Guid.NewGuid(), Path = source, DurationUs = 4000000, InUs = 1000000, OutUs = 3000000 }] };
            await merger.ApplyRestorePlanForTesting(new MergerRestorePlan(edl, [], []));
            var bridge = MergeEditorSource.Build(CompositeTimeline.Build(edl));
            var candidate = new MemePlacement(meme, 2.25, 1.5, "pending", ClipInSec: 2);
            var state = new MergeEditorState([], -1, 0, [], [candidate], 1);
            var result = await merger.RenderEditorMemePreviewAsync(bridge, state, false, root, timeout.Token, _ => { });
            Assert.True(result.Success, result.ErrorMessage);
            merged = Assert.IsType<string>(result.OutputPath);
        }
        finally
        {
            merger.IsSafeToClose = true;
            merger.Close();
        }
        double fullSeconds = await new MediaProber(ffprobe, full).GetDurationAsync();
        double cornerSeconds = await new MediaProber(ffprobe, corner).GetDurationAsync();
        Assert.InRange(fullSeconds - cornerSeconds, 1.45, 1.55);
        async Task<byte[]> Pixels(string path, string name)
        {
            string raw = Path.Combine(root, name + ".rgb");
            await Run("-ss", "2.65", "-i", path, "-frames:v", "1", "-vf", "scale=160:90", "-pix_fmt", "rgb24", "-f", "rawvideo", raw);
            return await File.ReadAllBytesAsync(raw, timeout.Token);
        }
        static void Red(byte[] frame, int x, int y)
        {
            int i = (y * 160 + x) * 3;
            Assert.True(frame[i] > frame[i + 1] + 60 && frame[i] > frame[i + 2] + 60, "The chosen late red part should be visible, not the earlier green part.");
        }
        Red(await Pixels(full, "full"), 80, 45);
        Assert.InRange(await new MediaProber(ffprobe, merged).GetDurationAsync(), 5.4, 5.6);
        Red(await Pixels(merged, "merged"), 80, 45);
        var cornerPixels = await Pixels(corner, "corner");
        Red(cornerPixels, 145, 80);
        int top = (15 * 160 + 15) * 3;
        Assert.True(cornerPixels[top + 2] > cornerPixels[top] + 60, "Gameplay should remain visible behind a corner meme.");
        async Task<float[]> Audio(string path, string name)
        {
            string raw = Path.Combine(root, name + ".f32");
            await Run("-ss", "2.4", "-i", path, "-t", "0.3", "-vn", "-ar", "48000", "-ac", "1", "-f", "f32le", raw);
            byte[] bytes = await File.ReadAllBytesAsync(raw, timeout.Token);
            var samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }
        static double Power(float[] samples, int frequency)
        {
            double a = 0, b = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double angle = 2 * Math.PI * frequency * i / 48000;
                a += samples[i] * Math.Cos(angle);
                b += samples[i] * Math.Sin(angle);
            }
            return (a * a + b * b) / Math.Max(1, samples.Length * (double)samples.Length);
        }
        foreach (var pair in new[] { (full, "full"), (corner, "corner"), (merged, "merged") })
        {
            var samples = await Audio(pair.Item1, pair.Item2);
            Assert.True(Power(samples, 900) > 1e-7 && Power(samples, 900) > Power(samples, 300) * 20, "The sound should come from the chosen late part.");
        }
        Assert.True(Power(await Audio(silent, "silent"), 900) < 1e-7, "Sound off should keep the meme silent.");
    }
}
