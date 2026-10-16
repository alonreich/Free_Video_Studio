using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public class VoiceOverTimingTests
{
    [Theory]
    [InlineData(0.5, 20)]
    [InlineData(1, 10)]
    [InlineData(2, 5)]
    public void TrimAndInitialSpeedUseOutputSeconds(double speed, double duration)
    {
        var p = new VoiceOverPlaybackPlan(10, 20, null, speed);
        Assert.Equal(duration, p.Timeline.TotalOutputSeconds, 8);
        Assert.Equal(10, p.SourceAt(0), 8);
        Assert.Equal(15, p.SourceAt(5 / speed), 8);
        Assert.Equal(0, p.OutputAt(10), 8);
    }

    [Fact]
    public void FreezeAnchorOffsetPreservesSpeechStartingInsideHold()
    {
        var p = new VoiceOverPlaybackPlan(0, 10, [new(4000, 6000, 0)], 1);
        Assert.Equal(4, p.SeekOutputAt(4), 8);
        Assert.Equal(6, p.OutputAt(4), 8);
        Assert.Equal(4, p.SourceAt(5), 8);
        var take = new VoiceOverTake("take.wav", 4, -1, 0.5, 4);
        Assert.Equal(5, VoiceOverTiming.OutputStart(take, p.OutputAt), 8);
        Assert.Equal(4, p.SourceAt(5.5), 8);
    }

    [Fact]
    public void OnlyTimingOverSpokenPictureChangesSignature()
    {
        var original = new VoiceOverPlaybackPlan(0, 20, null, 1);
        string Sig(VoiceOverPlaybackPlan p) => VoiceOverTiming.Signature(p.Timeline, p.SourceStartSec, p.SourceEndSec, 5, 10);
        string baseline = Sig(original);
        Assert.Equal(baseline, Sig(new(0, 20, [new(0, 4000, 2)], 1)));
        Assert.Equal(baseline, Sig(new(0, 20, [new(11000, 15000, 0.5)], 1)));
        Assert.Equal(baseline, Sig(new(0, 20, [new(6000, 8000, 1, 10, 20, 100, 100, "320x180")], 1)));
        Assert.NotEqual(baseline, Sig(new(0, 20, [new(6000, 8000, 2)], 1)));
        Assert.NotEqual(baseline, Sig(new(0, 20, [new(6000, 8000, 0)], 1)));
        Assert.NotEqual(baseline, Sig(new(0, 20, null, 1, [new(6000, 8000)])));
        Assert.NotEqual(baseline, Sig(new(7, 20, null, 1)));
    }

    [Fact]
    public void ClockAllowsLongPlaybackAndStopsOnStallWithoutLosingSafeAnchor()
    {
        var guard = new VoiceOverClockGuard();
        guard.Arm(4);
        for (int i = 1; i <= 2400; i++) Assert.True(guard.Observe(4 + i * 0.05, i * 0.05, false, false));
        Assert.False(guard.Observe(124, 120.2, false, false));
        Assert.Equal(124, guard.ResumeOutputSeconds, 8);
        Assert.Equal(120, guard.SafeCapturedSeconds, 8);
        guard.Arm(8);
        Assert.False(guard.Observe(8, 0, true, false));
        guard.Arm(8);
        Assert.False(guard.Observe(8, 0, false, true));
        guard.Arm(8);
        Assert.False(guard.Observe(7, 0, false, false));
    }

    [Fact]
    public void NarrationSplitsAtMemesAndClipsAtBodyEdges()
    {
        var pieces = VoiceOverSplice.Place(-1, 12, 10, 2, [(0, 1), (4, 3), (4, 1), (10, 7)]);
        Assert.Equal(new VoiceOverSplice.Piece(1, 3, 4), pieces[0]);
        Assert.Equal(new VoiceOverSplice.Piece(5, 11, 6), pieces[1]);
        Assert.Equal(2, pieces.Count);
        Assert.Empty(VoiceOverSplice.Place(12, 2, 10, 0, []));
    }

    [Fact]
    public void AllTakeMetadataAndProtectionSurviveSaveReopenAndUndo()
    {
        var takes = new[] { new VoiceOverTake("a.wav", 4, -1, 0.5, 4, "signature", true), new VoiceOverTake("b.wav", 8, 0, 2, 10) };
        var original = new ProjectDocument { Source = new SourceClip { FilePath = "source.mp4" }, Audio = new ProjectAudio { VoiceOverTakes = takes, VoiceOverDuckAudio = true, VoiceOverProtectFromMusic = false } };
        var restored = ProjectSerializer.Read(ProjectSerializer.Write(original), out var error);
        Assert.Null(error);
        Assert.Equal(original.Audio, restored!.Audio);
        Assert.Equal(takes, restored.Audio.VoiceOverTakes);
        var history = new UndoStack<ProjectDocument>(original);
        var changed = original with { Audio = original.Audio with { VoiceOverTakes = [takes[1]] } };
        history.Apply(changed, "remove take");
        Assert.Equal(original.Audio, history.Undo()!.Audio);
        Assert.Equal(changed.Audio, history.Redo()!.Audio);
        Assert.Equal(new VoiceOverTake("legacy.wav", 2), Assert.Single(VoiceOverTakeJson.Read(JsonNode.Parse("[{\"path\":\"legacy.wav\",\"startSec\":2}]"))));
    }
}
