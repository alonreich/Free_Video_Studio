using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class OwnerRequestBatchTests
{
    private static readonly Rect Bounds = new(0, 0, 1000, 500);

    [Fact]
    public void ZoomBoxSnap_PullsANearlyCentredBoxOntoTheCentre()
    {
        var box = new Rect(500 - 100 + 6, 250 - 50 - 5, 200, 100);
        var r = ZoomBoxSnap.Snap(box, Bounds, allowSnap: true);
        Assert.Equal(400, r.Box.X, 6);
        Assert.Equal(200, r.Box.Y, 6);
        Assert.Equal(ZoomGuideState.Snapped, r.Vertical);
        Assert.Equal(ZoomGuideState.Snapped, r.Horizontal);
    }

    [Fact]
    public void ZoomBoxSnap_SnapsOneAxisAndOnlyHintsTheOther()
    {
        var box = new Rect(400 + 5, 200 + 20, 200, 100);
        var r = ZoomBoxSnap.Snap(box, Bounds, allowSnap: true);
        Assert.Equal(400, r.Box.X, 6);
        Assert.Equal(220, r.Box.Y, 6);
        Assert.Equal(ZoomGuideState.Snapped, r.Vertical);
        Assert.Equal(ZoomGuideState.Near, r.Horizontal);
    }

    [Fact]
    public void ZoomBoxSnap_LeavesAFarBoxAloneAndNeverSnapsWhileResizing()
    {
        var far = new Rect(50, 40, 200, 100);
        var r = ZoomBoxSnap.Snap(far, Bounds, allowSnap: true);
        Assert.Equal(far, r.Box);
        Assert.Equal(ZoomGuideState.Hidden, r.Vertical);
        Assert.Equal(ZoomGuideState.Hidden, r.Horizontal);

        var near = new Rect(404, 203, 200, 100);
        var resize = ZoomBoxSnap.Snap(near, Bounds, allowSnap: false);
        Assert.Equal(near, resize.Box);
        Assert.Equal(ZoomGuideState.Near, resize.Vertical);
    }

    [Fact]
    public void PickerFrameZoom_KeepsThePictureCoveringTheViewAndMapsBackToTheWholeFrame()
    {
        var (vw, vh) = AiSubjectPickerWindow.ViewportSize(400, 600, 16.0 / 9.0);
        Assert.Equal(400, vw, 6);
        Assert.Equal(225, vh, 6);
        var (l, cx, cy) = AiSubjectPickerWindow.ComputeLayout(vw, vh, 16.0 / 9.0, AiSubjectPickerWindow.DefaultFrameZoom, 0.5, 0.5);
        Assert.Equal(0.5, cx, 6);
        Assert.Equal(0.5, cy, 6);
        Assert.Equal(vw * 1.7, l.Width, 6);
        Assert.Equal(0.5, (vw / 2 - l.OffsetX) / l.Width, 6);
        var (edge, ecx, _) = AiSubjectPickerWindow.ComputeLayout(vw, vh, 16.0 / 9.0, 1.7, 0.0, 0.5);
        Assert.True(edge.OffsetX <= 1e-9 && ecx > 0);
        var (whole, _, _) = AiSubjectPickerWindow.ComputeLayout(vw, vh, 16.0 / 9.0, 1.0, 0.9, 0.9);
        Assert.Equal(0, whole.OffsetX, 6);
        Assert.Equal(0, whole.OffsetY, 6);
    }

    [Fact]
    public void PickerPortraitView_UsesTheExportsTwoByThreeGameplayArea()
    {
        Assert.Equal(2.0 / 3.0, AiSubjectPickerWindow.PortraitViewAspect, 9);
        Assert.Equal(1.0, AiSubjectPickerWindow.MinFrameZoom(false, 16.0 / 9.0), 9);
        Assert.Equal(0.375, AiSubjectPickerWindow.MinFrameZoom(true, 16.0 / 9.0), 9);
        var (vw, vh) = AiSubjectPickerWindow.ViewportSize(400, 600, AiSubjectPickerWindow.PortraitViewAspect);
        Assert.Equal(400, vw, 6);
        Assert.Equal(600, vh, 6);
        var (l, _, _) = AiSubjectPickerWindow.ComputeLayout(vw, vh, 16.0 / 9.0, 1.0, 0.5, 0.5);
        Assert.Equal(720.0 / 1920.0, vw / l.Width, 6);
    }

    [Fact]
    public void PickerNextImage_PicksAMomentAboutOneToOneAndAHalfSecondsAwayInsideTheSegment()
    {
        var rng = new System.Random(7);
        for (int k = 0; k < 200; k++)
        {
            double t = AiSubjectPickerWindow.NextImageTime(15.0, 10.0, 10.0, rng);
            double d = System.Math.Abs(t - 15.0);
            Assert.InRange(d, 1.0, 1.5);
            double edge = AiSubjectPickerWindow.NextImageTime(10.2, 10.0, 10.0, rng);
            Assert.InRange(edge, 10.0, 20.0);
            Assert.InRange(edge - 10.2, 1.0, 1.5);
            double tiny = AiSubjectPickerWindow.NextImageTime(10.3, 10.0, 0.5, rng);
            Assert.InRange(tiny, 10.0, 10.5);
        }
    }

    [Fact]
    public void SpeedEditorTopLaneIsHalfTheFilmstripLane()
    {
        Assert.Equal(30.0, GranularSpeedEditorWindow.SegmentLaneHeight, 9);
    }

    [Fact]
    public void ToggleSwitchLeavesNoTintedBoxAfterAMouseClick()
    {
        string xaml = System.IO.File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "AvaloniaApp.axaml"));
        Assert.DoesNotContain("ToggleSwitch:focus /template/ Grid#SwitchAreaGrid", xaml);
        Assert.Contains("ToggleSwitch:focus-visible /template/ Grid#SwitchAreaGrid", xaml);
    }

    [Fact]
    public void MusicWizardMergerPreview_PlaysEveryClipWindowOnOneClock()
    {
        var url = MusicWizardWindow.MergerPreviewEdl(
            new[] { @"C:\v\b.mp4", @"C:\v\a.mp4" },
            new[] { (0.0, 24.8), (0.1, 38.65) });
        Assert.Equal(@"edl://%10%C:\v\b.mp4,0,24.8;%10%C:\v\a.mp4,0.1,38.55", url);

        var whole = MusicWizardWindow.MergerPreviewEdl(new[] { @"C:\v\b.mp4", @"C:\v\a.mp4" }, null);
        Assert.Equal(@"edl://%10%C:\v\b.mp4;%10%C:\v\a.mp4", whole);
    }

    [Fact]
    public void PreviewArrowKeys_StepTwoSeconds()
    {
        Assert.Equal(2.0, PreviewKeySeek.StepSeconds);
    }

    [AvaloniaFact]
    public void FinishedDialog_OffersTheMergerOnlyAfterTwoSessionVideos()
    {
        var dlg = new FinishedDialogWindow();
        var btn = (Button)dlg.FindNameScope()!.Find("FinishedMergerBtn")!;
        Assert.False(btn.IsVisible);
        dlg.SetMergerOffer(1);
        Assert.False(btn.IsVisible);
        dlg.SetMergerOffer(2);
        Assert.True(btn.IsVisible);
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(3, dlg.DialogResult);
    }

    private static System.Text.Json.Nodes.JsonObject SampleCoords() => System.Text.Json.Nodes.JsonNode.Parse("""
        {"crops_1080p":{"loot":[520,176,1416,1389],"team":[275,183,-883,1254]},
         "scales":{"loot":"521/520","team":"278/275"},
         "overlays":{"loot":{"x":540,"y":1462},"team":{"x":0,"y":150}},
         "z_orders":{"loot":10,"team":40}}
        """)!.AsObject();

    [Fact]
    public void FinalLayout_FollowsTheTeammatesSwitchAndTheExportGeometry()
    {
        var off = FreeVideoStudio.Core.Media.MobileFilterBuilder.HudCanvasLayers(SampleCoords(), showTeammates: false, showSpectating: true);
        Assert.Equal(new[] { "loot" }, off.Select(l => l.Key));
        var on = FreeVideoStudio.Core.Media.MobileFilterBuilder.HudCanvasLayers(SampleCoords(), showTeammates: true, showSpectating: true);
        Assert.Equal(new[] { "loot", "team" }, on.Select(l => l.Key));
        var team = on.Single(l => l.Key == "team");
        Assert.Equal(0, team.X);
        Assert.Equal(150, team.Y);
        Assert.Equal(278, team.W, 3);
        var phone = FinalLayoutPreview.ToPhone(team);
        Assert.Equal(100, phone.Y, 6);
        Assert.Equal(278 * 720.0 / 1080.0, phone.Width, 6);
        Assert.Equal("Teammate HP", FinalLayoutPreview.Label("team"));
    }

    [AvaloniaFact]
    public void PhoneFrame_DrawsOneLabelledBoxPerOverlay()
    {
        var frame = new PhoneFrameMockup();
        frame.SetFinalLayout(FreeVideoStudio.Core.Media.MobileFilterBuilder.HudCanvasLayers(SampleCoords(), true, true));
        Assert.Equal(2, frame.FinalLayoutBoxCount);
        frame.SetFinalLayout(null);
        Assert.Equal(0, frame.FinalLayoutBoxCount);
    }

}
