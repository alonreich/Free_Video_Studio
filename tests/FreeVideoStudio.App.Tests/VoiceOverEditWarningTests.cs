using System.Reflection;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public class VoiceOverEditWarningTests
{
    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    [AvaloniaTheory]
    [InlineData(false, "speed")]
    [InlineData(true, "speed")]
    [InlineData(false, "cut")]
    [InlineData(true, "cut")]
    [InlineData(false, "freeze")]
    [InlineData(true, "freeze")]
    public async Task TimingWarningCancelsOrKeepsEditAndPreservesTake(bool keep, string edit)
    {
        string root = Path.Combine(Path.GetTempPath(), "vo-warning-" + Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        FreeVideoStudio.App.Infrastructure.AppServices.Initialize(new FreeVideoStudio.Core.Infrastructure.ApplicationPaths(root));
        string voice = Path.Combine(root, "take.wav");
        File.WriteAllBytes(voice, [1]); // guard reads metadata; it must not open or modify this file.
        var w = new MainWindow();
        var vm = (MainViewModel)w.DataContext!;
        vm.Timeline.LoadedVideoDurationMs = 20000;
        vm.Timeline.BaseSpeed = 1;
        vm.VoiceOverResult = new() { VoiceOverTakes = [new(voice, 2, 0, 5, 7)] };
        Call(w, "ResetVoiceTimingBaseline");
        if (edit == "speed") vm.Timeline.BaseSpeed = 2;
        if (edit == "cut") vm.Timeline.Cuts.Add(new(4000, 5000));
        if (edit == "freeze") vm.Timeline.SpeedSegments.Add(new(4000, 5000, 0));
        bool deferred = (bool)Call(w, "DeferVoiceTimingChange", false, "change speed", null)!;
        Assert.True(deferred);
        int prompts = 0;
        w.VoiceTimingDecisionSeam = () => { prompts++; return Task.FromResult(keep); };
        await (Task)Call(w, "SettleVoiceTimingAsync")!;
        Assert.Equal(1, prompts);
        Assert.Equal(keep && edit == "speed" ? 2 : 1, vm.Timeline.BaseSpeed);
        Assert.Equal(keep && edit == "cut" ? 1 : 0, vm.Timeline.Cuts.Count);
        Assert.Equal(keep && edit == "freeze" ? 1 : 0, vm.Timeline.SpeedSegments.Count);
        var take = Assert.Single(vm.VoiceOverResult.VoiceOverTakes);
        Assert.Equal(keep, take.NeedsTimingReview);
        Assert.Equal(voice, take.Path);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(voice));
        FreeVideoStudio.App.Infrastructure.AppServices.ResetForTests();
    }
}
