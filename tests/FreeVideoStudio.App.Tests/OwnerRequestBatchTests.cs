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
    public void PickerFocusView_MapsClicksAndBoxesBackToTheWholePicture()
    {
        Assert.Equal(125, AiSubjectPickerWindow.ViewToSource1000(0, 0.75));
        Assert.Equal(500, AiSubjectPickerWindow.ViewToSource1000(0.5, 0.75));
        Assert.Equal(875, AiSubjectPickerWindow.ViewToSource1000(1, 0.75));
        Assert.Equal(1000, AiSubjectPickerWindow.ViewToSource1000(1, 1.0));
        Assert.Equal(0.5, AiSubjectPickerWindow.SourceToView(500, 0.75), 6);
        Assert.Equal(0.0, AiSubjectPickerWindow.SourceToView(125, 0.75), 6);
        Assert.Equal(0.75, AiSubjectPickerWindow.FocusViewFraction);
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
