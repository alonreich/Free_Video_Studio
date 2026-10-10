using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

// Opt-in native regression: FVS_TEST_NATIVE_MPV=1 dotnet test --filter MpvSeekCompletionTests.
public sealed class NativeMpvTheoryAttribute : TheoryAttribute
{
    public NativeMpvTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FVS_TEST_NATIVE_MPV") != "1")
            Skip = "Requires Windows, bundled libmpv/ffmpeg, and FVS_TEST_NATIVE_MPV=1.";
    }
}

public sealed class MpvSeekCompletionTests
{
    [NativeMpvTheory]
    [InlineData("video")]
    [InlineData("merger-edl")]
    [InlineData("audio")]
    public async Task PausedSeek_CompletesOnlyAfterTheRequestedPositionIsReady(string mode)
    {
        string root = FindRoot();
        // Load by absolute path without occupying the assembly's DllImportResolver slot.
        NativeLibrary.Load(Path.Combine(root, "binaries", "libmpv-2.dll"));
        string dir = Path.Combine(Path.GetTempPath(), "FvsSeekTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string clip = Path.Combine(dir, "long-gop.mp4");
        nint handle = 0;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(root, "binaries", "ffmpeg.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "testsrc2=s=1920x1080:r=30:d=8", "-f", "lavfi", "-i", "sine=frequency=440:duration=8",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "300", "-c:a", "aac", "-shortest", clip })
                start.ArgumentList.Add(arg);
            using (var ffmpeg = Process.Start(start)!)
            {
                var errors = ffmpeg.StandardError.ReadToEndAsync();
                await ffmpeg.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                Assert.True(ffmpeg.ExitCode == 0, await errors);
            }

            handle = MpvWrapper.mpv_create();
            Assert.NotEqual(nint.Zero, handle);
            foreach (var (name, value) in new[] { ("vo", "null"), ("ao", "null"), ("hwdec", "no"),
                ("vd-lavc-threads", "1"), ("terminal", "no"), ("idle", "yes"), ("keep-open", "yes") })
                Assert.True(MpvWrapper.mpv_set_option_string(handle, name, value) >= 0);
            if (mode == "audio") Assert.True(MpvWrapper.mpv_set_option_string(handle, "vid", "no") >= 0);
            Assert.True(MpvWrapper.mpv_initialize(handle) >= 0);
            using var player = new MpvIpcClient(handle);
            string input = mode == "merger-edl"
                ? $"edl://%{System.Text.Encoding.UTF8.GetByteCount(clip)}%{clip},0.1,7.9;" : clip;
            await player.LoadFileAsync(input);
            await WaitUntil(() => player.CurrentTime > 0.1);
            await player.SetPropertyAsync("pause", "yes");

            var completed = new TaskCompletionSource<(string? Seeking, double Position)>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.SeekCompleted += () =>
            {
                if (!completed.Task.IsCompleted)
                    completed.TrySetResult((player.GetPropertyString("seeking"),
                        double.Parse(player.GetPropertyString("time-pos")!, CultureInfo.InvariantCulture)));
            };
            await player.SendCommandAsync("seek", 6.75, "absolute+exact");
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("no", result.Seeking);
            // The paused audio-only clock includes output buffering; it need not
            // equal the target until playback resumes. Video exposes the decoded frame.
            if (mode != "audio")
            {
                Assert.InRange(result.Position, 6.75, 6.80);
                Assert.InRange(player.CurrentTime, 6.75, 6.80);
            }
            Assert.False(player.IsSeeking);
            Assert.True(player.IsPaused);
            await player.SetPropertyAsync("pause", "no");
            await WaitUntil(() => player.CurrentTime > 7.0);

            // A burst of marker moves must settle on the latest target, then play through it.
            await player.SetPropertyAsync("pause", "yes");
            for (int i = 0; i < 20; i++)
                await player.SendCommandAsync("seek", 1 + i * 0.1, "absolute+exact");
            await WaitUntil(() => !player.IsSeeking);
            if (mode != "audio") Assert.InRange(player.CurrentTime, 2.9, 2.95);
            await player.SetPropertyAsync("pause", "no");
            await WaitUntil(() => player.CurrentTime > 3.2);

            if (mode == "merger-edl")
            {
                // Removing the custom cover restores the first 0.1 s of the clip.
                // Reload at the same source moment, then resume beyond that point.
                await player.SetPropertyAsync("pause", "yes");
                double sourcePosition = player.CurrentTime + 0.1;
                await player.LoadFileAsync(clip, sourcePosition);
                await player.SetPropertyAsync("pause", "yes");
                await WaitUntil(() => Math.Abs(player.CurrentTime - sourcePosition) < 0.05);
                await player.SetPropertyAsync("pause", "no");
                await WaitUntil(() => player.CurrentTime > sourcePosition + 0.2);
            }

            // Rejected commands and a stop during a seek must release the gate too.
            await player.SendCommandAsync("seek", "not-a-number", "absolute+exact");
            Assert.False(player.IsSeeking);
            await player.SendCommandAsync("seek", 6.75, "absolute+exact");
            await player.SendCommandAsync("stop");
            await WaitUntil(() => !player.IsSeeking);
        }
        finally
        {
            if (handle != 0) MpvWrapper.mpv_terminate_destroy(handle);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "Playback did not reach the expected position.");
            await Task.Delay(20);
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FreeVideoStudio.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
