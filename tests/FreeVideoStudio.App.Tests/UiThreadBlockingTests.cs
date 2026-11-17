using System.IO;
using System.Linq;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class UiThreadBlockingTests
{
    private static string Read(params string[] parts) => File.ReadAllText(RepoRoot.SourcePath(new[] { "src" }.Concat(parts).ToArray()));

    [Fact]
    public void AppCode_NeverReadsThePlayerSynchronously_OutsideABackgroundTask()
    {
        string app = RepoRoot.SourcePath("src", "FreeVideoStudio.App");
        var offenders = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains(".GetPropertyString("))
            .Select(Path.GetFileName)
            .Where(n => n != "VoiceOverWindow.PlaybackClock.cs")
            .ToList();
        Assert.True(offenders.Count == 0, "synchronous mpv reads (they block until the player core is free): " + string.Join(", ", offenders));
        Assert.Contains("await Task.Run(() => (Path: ipc.GetPropertyString(\"path\")", Read("FreeVideoStudio.App", "VoiceOverWindow.PlaybackClock.cs"));
    }

    [Fact]
    public void OwnedPlayerTeardown_LeavesTheUiThread()
    {
        string client = Read("FreeVideoStudio.Core", "Media", "MpvIpcClient.cs");
        Assert.Contains("if (_ownsHandle && SynchronizationContext.Current != null)", client);
        Assert.Contains("new Thread(ReleaseNative) { IsBackground = true, Name = \"mpv-teardown\" }.Start();", client);
        Assert.Contains("public async Task<string?> ReadPropertyAsync(string name)", client);
    }

    [Fact]
    public void FreezeSound_UsesItsOwnAudioThread_AndStopsOffTheUiThread()
    {
        string preview = Read("FreeVideoStudio.App", "Services", "FreezeSoundPreview.cs");
        Assert.Contains("new WaveOutEvent", preview);
        Assert.DoesNotContain("new WaveOut ", preview);
        Assert.Contains("System.Threading.Tasks.Task.Run(() =>", preview);
    }

    [Fact]
    public void VoiceAndClickSounds_UseTheirOwnAudioThread_AndReleaseOffTheUiThread()
    {
        string app = RepoRoot.SourcePath("src", "FreeVideoStudio.App");
        var oldStyle = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"WaveOut(?!Event)(?!\.DeviceCount)"))
            .Select(Path.GetFileName).ToList();
        Assert.True(oldStyle.Count == 0, "window-callback WaveOut still used in: " + string.Join(", ", oldStyle));
        Assert.Contains("public static WaveOutEvent CreateVoicePlayer()", Read("FreeVideoStudio.App", "Infrastructure", "PreviewAudioSync.cs"));
        Assert.Contains("public static void DisposeOffUiThread(", Read("FreeVideoStudio.App", "Infrastructure", "PreviewAudioSync.cs"));
        Assert.Contains("new WaveOutEvent { BufferMilliseconds = 17", Read("FreeVideoStudio.App", "UiSoundEffect.cs"));
        Assert.Contains("click sound output dispose", Read("FreeVideoStudio.App", "UiSoundEffect.cs"));
    }

    [Fact]
    public void AppWideWatchdog_StartsWithTheApp_AndNamesTheBlockingCall()
    {
        Assert.Contains("Infrastructure.UiStallWatchdog.Start();", Read("FreeVideoStudio.App", "AvaloniaApp.axaml.cs"));
        string dog = Read("FreeVideoStudio.App", "Infrastructure", "UiStallWatchdog.cs");
        Assert.Contains("BlockingCallTracker.Describe()", dog);
        Assert.Contains("ui_stalls.log", dog);
        Assert.Contains("BlockingCallTracker.Describe()", Read("FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
    }

    [Fact]
    public void BlockingCallTracker_NamesTheCallInProgress_AndForgetsItAfter()
    {
        Assert.DoesNotContain("tracker-test outer", BlockingCallTracker.Describe());
        using (BlockingCallTracker.Enter("tracker-test outer"))
        {
            Assert.Contains("tracker-test outer", BlockingCallTracker.Describe());
            using (BlockingCallTracker.Enter("tracker-test inner"))
                Assert.Contains("tracker-test inner", BlockingCallTracker.Describe());
            Assert.Contains("tracker-test outer", BlockingCallTracker.Describe());
        }
        Assert.DoesNotContain("tracker-test", BlockingCallTracker.Describe());
    }
}
