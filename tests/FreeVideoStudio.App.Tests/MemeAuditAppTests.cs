using System.IO;
using System.Linq;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>MEMEAUDIT_01 — the App-side rules of the meme audit (2026-10-17).</summary>
public sealed class MemeAuditAppTests
{
    private static TimelineViewModel Timeline()
    {
        var t = new TimelineViewModel { LoadedVideoDurationMs = 60_000, BaseSpeed = 1.0 };
        t.SetTrimStart(10_000);
        t.TrimEndMs = 40_000;
        t.IsTrimEndSet = true;
        return t;
    }

    [Fact]
    public void MEMETRIM_02_MovingMarkStartKeepsEveryMemeOnItsGameplayFrame()
    {
        var t = Timeline();
        t.MemePlacements.Add(new MemePlacement("a.mp4", 5.0, 2.0, "meme0"));   // gameplay 15 s
        t.MarkMemesAnchored();

        t.SetTrimStart(12_000);
        Assert.Empty(t.RebaseMemesToTrim());
        Assert.Equal(3.0, t.MemePlacements.Single().AtSourceSecRelative, 3);     // still gameplay 15 s

        t.SetTrimStart(16_000);
        Assert.Equal(new[] { "a.mp4" }, t.RebaseMemesToTrim());                 // 15 s is now cut off
        Assert.Empty(t.MemePlacements);
    }

    [Fact]
    public void MEMETRIM_02_AMemeAtTheEndFollowsTheEnd_OnePastTheNewEndIsRemoved()
    {
        var t = Timeline();
        t.MemePlacements.Add(new MemePlacement("end.mp4", 30.0, 2.0, "meme0"));  // at MARK END
        t.MemePlacements.Add(new MemePlacement("late.mp4", 25.0, 2.0, "meme1")); // gameplay 35 s
        t.MarkMemesAnchored();

        t.TrimEndMs = 30_000;
        var dropped = t.RebaseMemesToTrim();
        Assert.Equal(new[] { "late.mp4" }, dropped);
        Assert.Equal(20.0, t.MemePlacements.Single().AtSourceSecRelative, 3);
    }

    [Fact]
    public void MEMESOUND_02_TheSoundChoiceAppliesInBothModes_AndThePartTravelsWithIt()
    {
        var vm = new MemeChoiceViewModel();
        vm.SetMeme("m.mp4", isImage: false, fileDurationSec: 10, keepPart: false);
        vm.PlaySound = false;
        vm.ClipInSec = 2.5m;
        vm.LengthSec = 3m;
        var placed = vm.ApplyTo(new MemePlacement("m.mp4", 1, 10, "meme0"));
        Assert.Equal((MemePresentationMode.InlineFullScreen, false, 2.5, 3.0),
            (placed.Mode, placed.PlaySound, placed.ClipInSec, placed.DurationSec));
        Assert.Equal("Video becomes +3.0 sec longer", vm.FullScreenLengthText);

        vm.LengthSec = 99m;                                                     // never past the file
        Assert.Equal(7.5, vm.LengthSeconds, 3);
    }

    [Fact]
    public void MEMETRIM_01_APicturesLengthStaysBetweenOneAndTenSeconds()
    {
        var vm = new MemeChoiceViewModel();
        vm.SetMeme("m.png", isImage: true, fileDurationSec: null, keepPart: false);
        Assert.Equal(4.0, vm.LengthSeconds, 3);
        vm.LengthSec = 0.2m;
        Assert.Equal(1.0, vm.LengthSeconds, 3);
        vm.LengthSec = 30m;
        Assert.Equal(10.0, vm.LengthSeconds, 3);
    }

    [Fact]
    public void MEMEPOS_01_ADraggedPositionShowsTheNearestCorner_ACornerButtonPutsItBack()
    {
        var vm = new MemeChoiceViewModel();
        vm.CornerCommand.Execute(null);
        vm.PosX = 0.1;
        vm.PosY = 0.8;
        Assert.True(vm.HasCustomPosition);
        Assert.True(vm.IsBottomLeft);
        vm.TopRightCommand.Execute(null);
        Assert.False(vm.HasCustomPosition);
        Assert.True(vm.IsTopRight);
        Assert.Equal((-1.0, -1.0), (vm.PosX, vm.PosY));
    }

    [Fact]
    public void MEMEWARN_02_ShapeWarningsApplyToFullScreenOnly()
    {
        var wide = new MemeItem { FileName = "w.mp4", FullPath = "w.mp4", Width = 1920, Height = 1080 };
        var row = new MemePickerRow(wide, portrait: true);
        Assert.True(row.HasWarning);
        row.SetFullScreen(false);
        Assert.False(row.HasWarning);

        var tall = new MemeItem { FileName = "t.mp4", FullPath = "t.mp4", Width = 1080, Height = 1920 };
        var tallRow = new MemePickerRow(tall, portrait: false);
        Assert.True(tallRow.HasWarning);
        Assert.Contains("side bars", tallRow.WarningText);
    }

    [Fact]
    public void MEMEPICK_05_ADoubleClickNoLongerPlacesTheMeme()
    {
        string picker = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", "MemePickerWindow.axaml.cs"));
        Assert.DoesNotContain("DoubleTapped +=", picker);
    }

    [Fact]
    public void MemePickResult_BuildsTheFullPlacement()
    {
        var item = new MemeItem { FileName = "m.mp4", FullPath = "m.mp4" };
        var r = new MemePickResult(item, 3, MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopLeft, MemeOverlaySize.Small,
            false, 1.5, 0.4, 0.6, MemeOverlayAnimation.Fade, MemeTransition.Fade);
        var p = r.ToPlacement(7, 3);
        Assert.Equal(new MemePlacement("m.mp4", 7, 3, "", MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopLeft,
            MemeOverlaySize.Small, false, 1.5, 0.4, 0.6, MemeOverlayAnimation.Fade, MemeTransition.Fade), p);
    }
}
