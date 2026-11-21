using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Controls;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class WindowPlacementTests
{
    private static readonly DisplayArea Laptop150 = new(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 1.5);
    private static readonly DisplayArea Right4K = new(new PixelRect(1920, 0, 3840, 2160), new PixelRect(1920, 0, 3840, 2100), 1.0);

    [Fact]
    public void TitleBarMustBeReachable_NotJustOnePixel()
    {
        var displays = new List<DisplayArea> { Laptop150 };
        Assert.Equal(-1, WindowPlacement.GrabbableDisplay(displays, 1919 - 1, 100, 1200));
        Assert.Equal(-1, WindowPlacement.GrabbableDisplay(displays, 200, 1031, 1200));
        Assert.Equal(-1, WindowPlacement.GrabbableDisplay(displays, 200, -500, 1200));
        Assert.Equal(0, WindowPlacement.GrabbableDisplay(displays, 200, 100, 1200));
        Assert.Equal(0, WindowPlacement.GrabbableDisplay(displays, 1920 - 200, 100, 1200));
    }

    [Fact]
    public void UnpluggedDisplay_HasNoGrabbableTitleBar()
    {
        var displays = new List<DisplayArea> { Laptop150 };
        Assert.Equal(-1, WindowPlacement.GrabbableDisplay(displays, 2500, 300, 1600));
        displays.Add(Right4K);
        Assert.Equal(1, WindowPlacement.GrabbableDisplay(displays, 2500, 300, 1600));
    }

    [Fact]
    public void FallbackSizeIsShrunkToTheUsableArea_AndCentredInsideIt()
    {
        var fit = WindowPlacement.FitInside(Laptop150, 2400, 1400, WindowPlacement.EditorCap);
        Assert.True(fit.Width <= 1280 * 0.95 + 0.01);
        Assert.True(fit.Height <= 688 * 0.95 + 0.01);
        var pos = WindowPlacement.Center(Laptop150, fit.Width, fit.Height);
        Assert.True(pos.X >= 0 && pos.Y >= 0);
        Assert.True(pos.Y + fit.Height * 1.5 <= 1032 + 1);
    }

    [Fact]
    public void KeepInside_UsesTheWorkingAreaNotTheTaskbar()
    {
        var p = WindowPlacement.KeepInside(Laptop150, new PixelPoint(1800, 1000), 400, 300);
        Assert.True(p.X + 600 <= 1920);
        Assert.True(p.Y + 450 <= 1032);
    }

    [Fact]
    public void FirstRun_IsSixteenByNine_AndMinimumsNeverExceedTheScreen()
    {
        var normal = WindowPlacement.FirstRunSize(Right4K, 800, 600, WindowPlacement.EditorCap);
        Assert.InRange(normal.Width / normal.Height, 1.76, 1.79);
        var tight = WindowPlacement.FirstRunSize(Laptop150, 1100, 760, WindowPlacement.EditorCap);
        Assert.True(tight.Height <= WindowPlacement.Cap(Laptop150, WindowPlacement.EditorCap).Height);
    }

    [Fact]
    public void DisplayFingerprintFindsAReturningDisplay()
    {
        var displays = new List<DisplayArea> { Laptop150, Right4K };
        Assert.Equal(1, WindowPlacement.IndexOf(displays, Right4K.Fingerprint));
        Assert.Equal(-1, WindowPlacement.IndexOf(displays, "9999,0,10,10"));
    }

    [Fact]
    public void InsideDesktop_DetectsAWindowBiggerThanTheDesktop()
    {
        var displays = new List<DisplayArea> { Laptop150 };
        Assert.True(WindowPlacement.InsideDesktop(displays, new PixelRect(10, 10, 800, 600)));
        Assert.False(WindowPlacement.InsideDesktop(displays, new PixelRect(10, 10, 3000, 1800)));
    }

    [AvaloniaFact]
    public void DialogFit_NeverOpensADialogTallerThanItsCap()
    {
        var w = new Window { MinWidth = 300, MinHeight = 5000, Width = 600, Height = 6000, Content = new Border() };
        DialogFit.Apply(w);
        Assert.True(w.MinHeight < 5000);
        Assert.True(w.Height < 6000);
        Assert.IsType<ScrollViewer>(w.Content is Grid g ? g.Children[0] : w.Content);
    }

    [Fact]
    public void EveryDialogIsFittedToTheScreen()
    {
        string[] dialogs =
        {
            "AiCommentaryDialogWindow.axaml.cs", "AiSubjectPickerWindow.axaml.cs", "AudioFixPromptWindow.axaml.cs",
            "CloudSyncProgressWindow.axaml.cs", "ConfirmDialogWindow.axaml.cs", "EditCaptionDialogWindow.axaml.cs",
            "ErrorDialogWindow.axaml.cs", "FinishedDialogWindow.axaml.cs", "MemePickerWindow.axaml.cs",
            "MusicAttributionDialogWindow.axaml.cs", "UpdateFinishedWindow.axaml.cs", "AiModeWindow.cs",
            "AiMultiBlockZoomWindow.cs", "AiZoomPreviewModalWindow.cs", "AiZoomStartWindow.cs",
            "AiSubjectPickerWindow.Correction.cs",
        };
        var missing = dialogs
            .Where(f => !File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", f)).Contains("DialogFit.Apply(", System.StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0, "WINPLACE_01 — these dialogs skip DialogFit: " + string.Join(", ", missing));
        Assert.Contains("DialogFit.Apply(", File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "UpgradeProgressWindow.axaml.cs")));
    }

    [Fact]
    public void SetupWindowHasARealSizeSoItCanBeCentred()
    {
        string xaml = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "DeploymentProgressWindow.axaml"));
        Assert.Matches(new Regex("\\sWidth=\"560\"\\s+Height=\"360\""), xaml);
    }
}
