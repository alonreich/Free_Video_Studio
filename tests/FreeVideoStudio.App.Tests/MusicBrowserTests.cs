using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Media;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed class MusicBrowserTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(MusicWizardWindow window, string name, object value) => typeof(MusicWizardWindow).GetField(name, Flags)!.SetValue(window, value);
    private static T Get<T>(MusicWizardWindow window, string name) => (T)typeof(MusicWizardWindow).GetField(name, Flags)!.GetValue(window)!;
    private static object? Call(MusicWizardWindow window, string name, params object[] args) => typeof(MusicWizardWindow).GetMethod(name, Flags)!.Invoke(window, args);
    private static IDisposable Isolate() => LiveCaptureDriver.IsolateProgramData(Path.Combine(FreeVideoStudio.Testing.TestArtifactDirectory.SuiteWork(FreeVideoStudio.Testing.TestOutputSuite.Name), "music-browser-" + Guid.NewGuid().ToString("N")));
    [AvaloniaFact]
    public void FirstScreenHasFullWidthListAndTwentySecondTransport()
    {
        using var isolated = Isolate();
        var window = new MusicWizardWindow();
        try
        {
            Call(window, "UpdateStepVisibility");
            Assert.Null(window.FindControl<Border>("PlaylistPanel"));
            Assert.True(window.Vm.TransportVisible);
            Assert.Equal("+20s", window.Vm.SkipForwardText);
            Assert.False(window.FindControl<Button>("PlayBtn")!.IsEnabled);
            Set(window, "_currentStep", 3);
            Call(window, "UpdateStepVisibility");
            Assert.Equal("+20s", window.Vm.SkipForwardText);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void MidpointPauseResumeAndSkipKeepTheChosenSongPosition()
    {
        using var isolated = Isolate();
        var window = new MusicWizardWindow();
        using var player = new MpvIpcClient();
        player.SetStateForTesting(0, 200, true, false);
        Set(window, "_audioIpcClient", player);
        var track = new MusicTrackItem { FilePath = "song.mp3", DurationSec = 200 };
        Set(window, "_rowPreviewTrack", track);
        Set(window, "_highlightedTrack", track);
        try
        {
            Call(window, "StartAutoPreview", track);
            Assert.Equal(100, Get<double>(window, "_previewCurrentOffset"));
            Assert.False(player.IsPaused);
            Call(window, "StopPreview");
            Assert.True(player.IsPaused);
            Set(window, "_previewCurrentOffset", 100.0);
            Call(window, "SkipPreview", -20.0);
            Assert.Equal(80, Get<double>(window, "_previewCurrentOffset"));
            Call(window, "TogglePreview");
            Assert.Equal(80, Get<double>(window, "_previewCurrentOffset"));
            Assert.False(player.IsPaused);
            Call(window, "StopPreview");
            Call(window, "SkipPreview", -1000.0);
            Assert.Equal(0, Get<double>(window, "_previewCurrentOffset"));
            Call(window, "SkipPreview", 1000.0);
            Assert.Equal(200, Get<double>(window, "_previewCurrentOffset"));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task RapidSelectionAndLeavingTheScreenInvalidateOlderLoads()
    {
        using var isolated = Isolate();
        var window = new MusicWizardWindow();
        using var player = new MpvIpcClient();
        player.SetStateForTesting(10, 300, false, false);
        Set(window, "_audioIpcClient", player);
        var gate = Get<SemaphoreSlim>(window, "_songPreviewGate");
        try
        {
            await gate.WaitAsync();
            Set(window, "_isPreviewPlaying", true);
            var old = (Task)Call(window, "PlayChosenSongAsync", new MusicTrackItem { FilePath = "old.mp3", DurationSec = 100 }, 50.0)!;
            Call(window, "StopPreview");
            Assert.True(player.IsPaused);
            Set(window, "_isPreviewPlaying", true);
            var latest = (Task)Call(window, "PlayChosenSongAsync", new MusicTrackItem { FilePath = "latest.mp3", DurationSec = 300 }, 150.0)!;
            gate.Release();
            await Task.WhenAll(old, latest);
            Assert.Equal("latest.mp3", Get<string>(window, "_lastLoadedTrackPath"));
            Assert.Equal(150, Get<double>(window, "_previewCurrentOffset"));
            await gate.WaitAsync();
            var leaving = (Task)Call(window, "PlayChosenSongAsync", new MusicTrackItem { FilePath = "abandoned.mp3", DurationSec = 100 }, 50.0)!;
            Call(window, "StopPreview");
            Set(window, "_currentStep", 3);
            gate.Release();
            await leaving;
            Assert.True(player.IsPaused);
            Assert.Equal("latest.mp3", Get<string>(window, "_lastLoadedTrackPath"));
        }
        finally { window.Close(); }
    }
}
