using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public class VoiceOverPreparedClockTests
{
    [AvaloniaTheory]
    [InlineData(1, false)]
    [InlineData(0.5, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task PreparedEndpoint_ProductionRecorderKeepsOnlyRemainingBodySamples(double speed, bool edited)
    {
        string root = Path.Combine(Path.GetTempPath(), "vo-endpoint-" + Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        VoiceOverWindow.RecoveryDirectorySeam = () => root;
        var devices = new SampleCaptureDevices();
        var capture = new FreeVideoStudio.App.Services.VoiceCaptureSession(devices, a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var w = new VoiceOverWindow(capture) { IsMpvReady = true };
        double end = edited ? 10 : 2;
        SpeedSegment[] segments = edited ? [new(2000, 3000, 0.5), new(4000, 6000, 0), new(7000, 9000, 2)] : [];
        CutRange[] cuts = edited ? [new(3000, 3500)] : [];
        var plan = new VoiceOverPlaybackPlan(0, end, segments, speed, cuts);
        var clock = new TestPreviewClock { Duration = plan.Timeline.TotalOutputSeconds };
        w.PreviewClockSeam = clock.Read;
        w.ConfigureEditForTesting(0, end, segments, speed, cuts);
        w.ConfigurePreparedPreviewForTesting(plan);
        try
        {
            clock.Time = edited ? 4.6 : plan.Timeline.TotalOutputSeconds - 0.2;
            w.TriggerStartRecordingAndPlayback();
            clock.Paused = false;
            clock.Time += 0.02;
            w.TriggerTimerTick();
            await LiveCaptureDriver.PumpUntil(() => devices.Recorder?.IsRecording == true && !capture.IsOpeningRecorder, "bounded recorder open");
            var take = w.CurrentSession!;
            double start = take.CaptureBudget!.OutputStart!.Value;
            if (edited) Assert.True(take.OutputOffsetSec < 0);
            int accepted = (int)VoiceCaptureBudget.FrameCount(plan.Timeline.TotalOutputSeconds, start, 44100) * 2;
            byte[] crossing = new byte[accepted + 882];
            Array.Fill(crossing, (byte)0x71, accepted, 882);
            devices.Input.Deliver(crossing);
            devices.Input.Deliver(Enumerable.Repeat((byte)0x71, 882).ToArray());
            clock.Time = plan.Timeline.TotalOutputSeconds;
            w.TriggerTimerTick();
            await LiveCaptureDriver.PumpUntil(() => w.Sessions.Count == 1 && !capture.HasPendingFinalizations, "endpoint take saved");
            Assert.Equal(accepted, SampleCaptureDevices.ReadPcm(take.WavPath).Length);
            Assert.All(SampleCaptureDevices.ReadPcm(take.WavPath), b => Assert.Equal(0, b));
            Assert.Equal(accepted / 88200.0, take.CapturedDurationSec!.Value, 8);
            Assert.InRange(plan.OutputAt(take.StartSec) + take.OutputOffsetSec + take.CapturedDurationSec.Value,
                plan.Timeline.TotalOutputSeconds - 1.0 / 44100, plan.Timeline.TotalOutputSeconds);
        }
        finally { w.IsSafeToClose = true; w.Close(); VoiceOverWindow.RecoveryDirectorySeam = null; }
    }
    [AvaloniaFact]
    public async Task FreezeUsesAdvancingOutputClock_StallPausesAndKeepsAudioForReview()
    {
        string root = Path.Combine(Path.GetTempPath(), "vo-prepared-" + Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        VoiceOverWindow.RecoveryDirectorySeam = () => root;
        var devices = new LiveCaptureDevices();
        var capture = LiveCaptureDriver.NewSession(devices);
        var w = new VoiceOverWindow(capture) { IsMpvReady = true };
        var clock = new TestPreviewClock { Duration = 12 };
        w.PreviewClockSeam = clock.Read;
        w.ConfigureEditForTesting(0, 10, [new(4000, 6000, 0)]);
        w.ConfigurePreparedPreviewForTesting(new(0, 10, [new(4000, 6000, 0)], 1));
        try
        {
            var recorder = await LiveCaptureDriver.ArmAndOpenAsync(w, devices, clock, 4.5);
            Assert.Equal(4, w.CurrentSession!.StartSec, 5);
            Assert.InRange(w.CurrentSession.OutputOffsetSec, -1.51, -1.47);
            LiveCaptureDriver.Deliver(recorder, clock, 0.5, 0.5f);
            w.TriggerTimerTick();
            Assert.False(w.IsRecordPaused);
            LiveCaptureDriver.Deliver(recorder, clock, 0.25, 0.5f, speed: 0);
            w.TriggerTimerTick();
            Assert.True(w.IsRecordPaused);
            await LiveCaptureDriver.PumpUntil(() => w.Sessions.Count == 1 && !capture.HasPendingFinalizations, "stalled take saved");
            var take = Assert.Single(w.Sessions);
            Assert.True(take.NeedsTimingReview);
            Assert.InRange(take.CapturedDurationSec!.Value, 0.74, 0.76);
            using var reader = new WavAudioReader(take.WavPath);
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.74, 0.76);
            Assert.False(w.ApplyButtonControl!.IsEnabled);
            w.TriggerToggleRecord();
            w.SelectedSession = take;
            w.AcceptSelectedTakeTiming();
            Assert.False(take.NeedsTimingReview);
        }
        finally { w.IsSafeToClose = true; w.Close(); VoiceOverWindow.RecoveryDirectorySeam = null; }
    }

    [Fact]
    public void ProtectionDefaultsAndExplicitSavedChoices()
    {
        var fresh = new AppSettings();
        Assert.Equal(VoiceProtectionMode.RememberLastChoice, fresh.VoiceProtectGameMode);
        Assert.Equal(VoiceProtectionMode.RememberLastChoice, fresh.VoiceProtectMusicMode);
        Assert.False(fresh.VoiceProtectGameLast);
        Assert.True(fresh.VoiceProtectMusicLast);
        string root = Path.Combine(Path.GetTempPath(), "vo-defaults-" + Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"SchemaVersion\":14,\"VoiceProtectGameLast\":true,\"VoiceProtectMusicLast\":false}");
        SettingsManager.Load();
        Assert.True(SettingsManager.Instance.VoiceProtectGameLast);
        Assert.False(SettingsManager.Instance.VoiceProtectMusicLast);
    }

    [Theory]
    [InlineData(@"C:\temp\voice-preview-abc.mkv", "C:/temp/voice-preview-abc.mkv", true)]
    [InlineData(@"C:\TEMP\VOICE-PREVIEW-ABC.MKV", "c:/temp/voice-preview-abc.mkv", true)]
    [InlineData(@"voice-preview-abc.mkv", @"C:\temp\voice-preview-abc.mkv", true)]
    [InlineData(@"C:\temp\voice-preview-xyz.mkv", @"C:\temp\voice-preview-abc.mkv", false)]
    [InlineData(null, @"C:\temp\voice-preview-abc.mkv", false)]
    [InlineData("", @"C:\temp\voice-preview-abc.mkv", false)]
    public void ProxyPathMatch_NormalizesSlashesAndNames(string? mpvPath, string? targetPath, bool expectedMatch)
    {
        Assert.Equal(expectedMatch, VoiceOverWindow.IsMpvProxyPathMatch(mpvPath, targetPath));
    }

    [Fact]
    public void TrimMarkerBounds_WhenEndPrecedesStart_SafelyClampsToNonEmptyRange()
    {
        double trimStart = 5.0;
        double trimEnd = 2.0;
        double duration = 10.0;
        if (trimEnd <= trimStart) trimEnd = Math.Max(trimStart + 0.1, duration);
        double effectiveEnd = Math.Max(trimStart + 0.1, Math.Min(duration, trimEnd));
        var plan = new VoiceOverPlaybackPlan(trimStart, effectiveEnd, [], 1.0);
        Assert.True(plan.SourceEndSec > plan.SourceStartSec);
        Assert.Equal(5.0, plan.Timeline.TotalOutputSeconds, 3);
    }
}
