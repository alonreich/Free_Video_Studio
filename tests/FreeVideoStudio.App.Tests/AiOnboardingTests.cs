using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiOnboardingTests
{
    private static string Read(params string[] parts) => File.ReadAllText(RepoRoot.SourcePath(new[] { "src" }.Concat(parts).ToArray()));

    [Fact]
    public void OnlyTheDropCardShowsAHandOnTheEmptyScreen()
    {
        string xaml = Read("FreeVideoStudio.App", "MainWindow.axaml");
        Assert.Matches(new Regex(@"Name=""UploadOverlay""[^>]*Cursor=""Arrow"""), xaml);
        Assert.Matches(new Regex(@"Name=""UploadDropCard""[^>]*Background=""Transparent""[^>]*Cursor=""Hand"""), xaml);
        Assert.Contains("Drag &amp; Drop Videos Here:", xaml);
        Assert.DoesNotContain("gameplay video here", xaml);
        Assert.Contains("WireEmptyStateControls();", Read("FreeVideoStudio.App", "MainWindow.Wireup.cs"));
    }

    [Fact]
    public void KillTrackerQuickSwitchLivesOnTheEmptyScreenAndSaysItsState()
    {
        string xaml = Read("FreeVideoStudio.App", "MainWindow.axaml");
        int overlay = xaml.IndexOf("Name=\"UploadOverlay\"", System.StringComparison.Ordinal);
        int toggle = xaml.IndexOf("Name=\"KillTrackerQuickToggle\"", System.StringComparison.Ordinal);
        int overlayEnd = xaml.IndexOf("</Border>", xaml.IndexOf("Name=\"UploadDropCard\"", System.StringComparison.Ordinal), System.StringComparison.Ordinal);
        Assert.True(overlay > 0 && toggle > overlay && toggle < overlayEnd);
        Assert.Equal("🎯 AI Kill Tracker: ON", MainWindow.KillToggleText(true));
        Assert.Equal("🎯 AI Kill Tracker: OFF", MainWindow.KillToggleText(false));
        Assert.InRange(MainWindow.KillToggleOffOpacity, 0.4, 0.8);
        string code = Read("FreeVideoStudio.App", "MainWindow.QuickStart.cs");
        Assert.Contains("EnsureApiKeyConfiguredAsync(this, vendor, AiUsage.EliminationTracker)", code);
        Assert.Contains("s.AiEliminationTrackerEnabled = turnOn", code);
        Assert.Contains("SettingsManager.Committed += onCommit", code);
        Assert.DoesNotContain("FindControl", code);
        Assert.Contains("Click=\"OnKillTrackerQuickToggleClick\"", xaml);
        Assert.Contains("PointerPressed=\"OnUploadDropCardPressed\"", xaml);
    }

    [Fact]
    public void StartScreen_HasNoAiTagline_AndNoKeyWarningButton()
    {
        string xaml = Read("FreeVideoStudio.App", "MainWindow.axaml");
        string quick = Read("FreeVideoStudio.App", "MainWindow.QuickStart.cs");
        Assert.DoesNotContain("AI can zoom, write captions", xaml + quick);
        Assert.DoesNotContain("AiSetupHintButton", xaml + quick);
        Assert.DoesNotContain("AI key needs attention", xaml + quick);
    }

    [Fact]
    public void AiButtonsWithoutAnAiOpenTheSetupInsteadOfAnError()
    {
        Assert.DoesNotContain("API Key Missing", Read("FreeVideoStudio.App", "GranularSpeedEditorWindow.CaptionsLane.cs"));
        Assert.Contains("SetUpAiThenScanAsync", Read("FreeVideoStudio.App", "MainWindow.AiElimination.cs"));
    }

    [Fact]
    public void AiSetupWindowsFollowTextSizeAndSpeakPlainly()
    {
        foreach (string file in new[] { "AiModeWindow.cs", "AiZoomStartWindow.cs" })
        {
            string code = Read("FreeVideoStudio.App", "Controls", file);
            Assert.DoesNotMatch(new Regex(@"FontSize\s*=\s*\d"), code);
        }
        string mode = Read("FreeVideoStudio.App", "Controls", "AiModeWindow.cs");
        Assert.DoesNotContain("ex.Message + \")", mode);
        Assert.DoesNotContain("Costs per use on your AI account.", mode);
    }
}
