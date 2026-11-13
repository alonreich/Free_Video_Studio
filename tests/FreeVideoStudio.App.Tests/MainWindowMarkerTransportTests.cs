using System;
using System.IO;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class MainWindowMarkerTransportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDisposable _programData;

    public MainWindowMarkerTransportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mw-marker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _programData = LiveCaptureDriver.IsolateProgramData(_tempDir);
        FreeVideoStudio.App.Infrastructure.AppServices.Initialize(new FreeVideoStudio.Core.Infrastructure.ApplicationPaths(_tempDir));
        VideoRenderMode.Initialize();
    }

    public void Dispose()
    {
        _programData.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (MainWindow window, MpvIpcClient ipc) SetupMainWindow(double durationSec = 20.0, double initialTimeSec = 0.0, bool isPaused = true)
    {
        var window = new MainWindow();
        var ipc = new MpvIpcClient();
        ipc.SetStateForTesting(initialTimeSec, durationSec, isPaused, isEof: false);

        var videoHost = new MpvVideoView();
        videoHost.SetIpcClientForTesting(ipc);
        window.SetActiveVideoHostForTesting(videoHost);

        var vm = (MainViewModel)window.DataContext!;
        vm.Timeline.LoadedVideoDurationMs = durationSec * 1000.0;

        return (window, ipc);
    }

    [AvaloniaFact]
    public void MarkEnd_PausesVideo_AndSetsCaretPosition_AndSliderMatches()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 12.0, isPaused: false);

        window.ExecuteMarkEndForTesting();

        Assert.True(window.TrimEndSetForTesting);
        Assert.Equal(12000.0, window.TrimEndMsForTesting, 0.01);
        Assert.True(window.UserExplicitMarkEndForTesting);
        Assert.True(ipc.IsPaused);

        var slider = window.TimelineSliderCtlForTesting;
        Assert.NotNull(slider);
        Assert.Equal(60.0, slider.Value, 0.1);
    }

    [AvaloniaFact]
    public void PlayAfterMarkEnd_ResumesFromMarkEnd_WithoutRewindingToMarkStart()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 12.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 2000.0, endMs: 12000.0);

        window.ExecuteMarkEndForTesting();
        Assert.True(window.UserExplicitMarkEndForTesting);
        Assert.True(ipc.IsPaused);

        window.TogglePlayPauseTransportForTesting();

        Assert.False(ipc.IsPaused);
        Assert.Equal(12.0, ipc.CurrentTime, 0.1);
        Assert.False(window.UserExplicitMarkEndForTesting);

        window.PlaybackTimerTickForTesting();
        Assert.False(ipc.IsPaused);
    }

    [AvaloniaFact]
    public void PlayAfterSeekingPastMarkEnd_PlaysWithoutRewindingToMarkStart()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 15.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 2000.0, endMs: 10000.0);
        window.SetUserExplicitMarkEndForTesting(false);

        window.TogglePlayPauseTransportForTesting();

        Assert.False(ipc.IsPaused);
        Assert.Equal(15.0, ipc.CurrentTime, 0.1);
    }

    [AvaloniaFact]
    public void PlayAfterNaturalEndOfClip_RewindsToMarkStart()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 20.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 3000.0, endMs: 20000.0);
        window.SetUserExplicitMarkEndForTesting(false);

        ipc.SetStateForTesting(currentTime: 20.0, duration: 20.0, isPaused: true, isEof: true);

        window.TogglePlayPauseTransportForTesting();

        Assert.False(ipc.IsPaused);
        Assert.Equal(3.0, ipc.CurrentTime, 0.1);
    }

    [AvaloniaFact]
    public void PlaybackTimerTick_WhenPlayingPastMarkEnd_ContinuesPlayingUntilNaturalEndOfClip()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 9.96, isPaused: false);
        window.SetTrimPointsForTesting(startMs: 2000.0, endMs: 10000.0);

        window.PlaybackTimerTickForTesting();
        Assert.False(ipc.IsPaused);

        ipc.SetStateForTesting(currentTime: 19.96, duration: 20.0, isPaused: false, isEof: false);
        window.PlaybackTimerTickForTesting();
        Assert.True(ipc.IsPaused);
    }

    [AvaloniaFact]
    public void PlaybackTimerTick_WhenPlayingBeforeMarkEnd_ContinuesPlaying()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 5.0, isPaused: false);
        window.SetTrimPointsForTesting(startMs: 2000.0, endMs: 10000.0);

        window.PlaybackTimerTickForTesting();

        Assert.False(ipc.IsPaused);
    }

    [AvaloniaFact]
    public void DraggingMarkEnd_MovesCaretPlayhead_AndUpdatesSliderInRealTime()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 5.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 2000.0, endMs: 18000.0);

        window.SimulateDragEndMarkerForTesting(newX: 500.0, canvasWidth: 1000.0, duration: 20.0);

        Assert.Equal(10000.0, window.TrimEndMsForTesting, 0.01);
        Assert.True(window.UserExplicitMarkEndForTesting);
        Assert.Equal(10.0, ipc.CurrentTime, 0.1);

        var slider = window.TimelineSliderCtlForTesting;
        Assert.NotNull(slider);
        Assert.Equal(50.0, slider.Value, 0.1);
    }

    [AvaloniaFact]
    public void DraggingMarkEnd_ClampsBeforeMarkStart()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 8.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 4000.0, endMs: 15000.0);

        window.SimulateDragEndMarkerForTesting(newX: 100.0, canvasWidth: 1000.0, duration: 20.0);

        Assert.True(window.TrimEndMsForTesting > 4000.0);
        Assert.True(ipc.CurrentTime > 4.0);
    }

    [AvaloniaFact]
    public void MarkStart_KeepsPlaying_AndUpdatesSlider()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 5.0, isPaused: false);
        window.SetTrimPointsForTesting(startMs: 0.0, endMs: 18000.0);

        window.ExecuteMarkStartForTesting();

        Assert.True(window.TrimStartSetForTesting);
        Assert.Equal(5000.0, window.TrimStartMsForTesting, 0.01);
        Assert.False(ipc.IsPaused);

        var slider = window.TimelineSliderCtlForTesting;
        Assert.NotNull(slider);
        Assert.Equal(25.0, slider.Value, 0.1);
    }

    [AvaloniaFact]
    public void DraggingMarkStart_MovesCaretPlayhead_AndUpdatesSliderInRealTime()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 2.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 1000.0, endMs: 18000.0);

        window.SimulateDragStartMarkerForTesting(newX: 300.0, canvasWidth: 1000.0, duration: 20.0);

        Assert.Equal(6000.0, window.TrimStartMsForTesting, 0.01);
        Assert.Equal(6.0, ipc.CurrentTime, 0.1);

        var slider = window.TimelineSliderCtlForTesting;
        Assert.NotNull(slider);
        Assert.Equal(30.0, slider.Value, 0.1);
    }

    [AvaloniaFact]
    public void DraggingMarkStart_ClampsBeforeMarkEnd()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 2.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 1000.0, endMs: 10000.0);

        window.SimulateDragStartMarkerForTesting(newX: 700.0, canvasWidth: 1000.0, duration: 20.0);

        Assert.True(window.TrimStartMsForTesting < 10000.0);
        Assert.True(ipc.CurrentTime < 10.0);
    }

    [AvaloniaFact]
    public void MarkStartAndMarkEnd_InvalidOrderingAttempts_AreRejected()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 15.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 2000.0, endMs: 10000.0);

        ipc.SetStateForTesting(currentTime: 15.0, duration: 20.0, isPaused: true, isEof: false);
        window.ExecuteMarkStartForTesting();
        Assert.Equal(2000.0, window.TrimStartMsForTesting, 0.01);

        ipc.SetStateForTesting(currentTime: 1.0, duration: 20.0, isPaused: true, isEof: false);
        window.ExecuteMarkEndForTesting();
        Assert.Equal(10000.0, window.TrimEndMsForTesting, 0.01);
    }

    [AvaloniaFact]
    public void StartMarkerCaptureLost_ClearsDraggingState_AndResetsGestureActive()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 2.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 1000.0, endMs: 10000.0);

        window.SetDraggingStartMarkerForTesting(true);
        Assert.True(window.IsMarkerGestureActiveForTesting);

        window.SimulateStartMarkerCaptureLostForTesting(canvasWidth: 1000.0, duration: 20.0);

        Assert.False(window.IsMarkerGestureActiveForTesting);
        Assert.True(window.TrimStartSetForTesting);
    }

    [AvaloniaFact]
    public void EndMarkerCaptureLost_ClearsDraggingState_AndResetsGestureActive()
    {
        var (window, ipc) = SetupMainWindow(durationSec: 20.0, initialTimeSec: 8.0, isPaused: true);
        window.SetTrimPointsForTesting(startMs: 1000.0, endMs: 10000.0);

        window.SetDraggingEndMarkerForTesting(true);
        Assert.True(window.IsMarkerGestureActiveForTesting);

        window.SimulateEndMarkerCaptureLostForTesting(canvasWidth: 1000.0, duration: 20.0);

        Assert.False(window.IsMarkerGestureActiveForTesting);
        Assert.True(window.TrimEndSetForTesting);
        Assert.True(window.UserExplicitMarkEndForTesting);
    }
}
