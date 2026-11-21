using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.LocalAi;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiModeTests
{
    private const string FakeKey = "AIzaSy-dummy-test-key-000000000000";

    [Theory]
    [InlineData(AiMode.NotChosen, false, false, AiMode.NotChosen)]
    [InlineData(AiMode.NotChosen, true, false, AiMode.OnlineOnly)]
    [InlineData(AiMode.NotChosen, true, true, AiMode.Hybrid)]
    [InlineData(AiMode.NotChosen, false, true, AiMode.NotChosen)]
    [InlineData(AiMode.LocalOnly, true, true, AiMode.LocalOnly)]
    [InlineData(AiMode.OnlineOnly, true, true, AiMode.OnlineOnly)]
    public void Effective_KeepsTheChoiceAndTreatsOldSetupsAsOnline(AiMode saved, bool key, bool pack, AiMode expected)
        => Assert.Equal(expected, AiSetup.Effective(saved, key, pack));

    [Theory]
    [InlineData(AiMode.NotChosen, true, true, false)]
    [InlineData(AiMode.LocalOnly, false, true, true)]
    [InlineData(AiMode.LocalOnly, true, false, true)]
    [InlineData(AiMode.OnlineOnly, true, false, true)]
    [InlineData(AiMode.OnlineOnly, false, true, false)]
    [InlineData(AiMode.Hybrid, true, false, true)]
    [InlineData(AiMode.Hybrid, false, true, false)]
    [InlineData(AiMode.Hybrid, true, true, true)]
    public void Setup_IsCompleteOnlyWhenEveryPartIsThere(AiMode mode, bool key, bool pack, bool expected)
        => Assert.Equal(expected, AiSetup.IsComplete(mode, key, pack));

    [Fact]
    public void PcOnly_HidesEveryKeyFromFeaturesButKeepsItSaved()
    {
        var s = new AppSettings { GeminiApiKey = FakeKey };
        Assert.Equal(FakeKey, s.GetApiKey(AiVendor.Google));
        s.AiMode = AiMode.LocalOnly;
        Assert.Equal(string.Empty, s.GetApiKey(AiVendor.Google));
        Assert.Equal(FakeKey, s.GetStoredApiKey(AiVendor.Google));
        Assert.True(AiSetup.HasStoredKey(s));
        s.AiMode = AiMode.Hybrid;
        Assert.Equal(FakeKey, s.GetApiKey(AiVendor.Google));
    }

    [Fact]
    public void Mode_RoundTripsAndUnknownTextReadsAsNotChosen()
    {
        var s = new AppSettings { AiMode = AiMode.Hybrid };
        string json = JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"AiMode\": \"Hybrid\"", json.Replace("\"AiMode\":\"", "\"AiMode\": \""));
        var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(AiMode.Hybrid, back.AiMode);
        var odd = JsonSerializer.Deserialize(json.Replace("\"Hybrid\"", "\"Banana\""), SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(AiMode.NotChosen, odd.AiMode);
        var old = JsonSerializer.Deserialize("{}", SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(AiMode.NotChosen, old.AiMode);
    }

    [AvaloniaFact]
    public void ModeWindow_ShowsTheThreePlainChoicesWithBothRecommended()
    {
        var w = new AiModeWindow();
        var radios = w.GetLogicalDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(3, radios.Count);
        string Texts(RadioButton r) => string.Join(" | ", r.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("On this PC only", Texts(radios[0]));
        Assert.Contains("100% free.", Texts(radios[0]));
        Assert.Contains("Online only", Texts(radios[1]));
        Assert.Contains("Free with Google's free plan", Texts(radios[1]));
        Assert.StartsWith("Recommended: Both", Texts(radios[2]));
        Assert.Contains("Cheap:", Texts(radios[2]));
        Assert.True(radios[0].IsEnabled);
        Assert.True(radios[1].IsEnabled);
        Assert.True(radios[2].IsEnabled);
        if (!LocalAiPackManifest.IsPublished && !LocalAiPackService.IsReady)
        {
            Assert.Contains("built-in basic", Texts(radios[0]));
            Assert.Contains("built-in basic", Texts(radios[2]));
        }
        Assert.False(w.Finished);
    }

    [AvaloniaFact]
    public void KeyWizard_PicksNoCompanyAndHidesTheStepsUntilOneIsClicked()
    {
        var w = new AiSetupWizardWindow(AiVendor.Google, null, chooseCompanyFirst: true);
        var scope = w.FindNameScope()!;
        Assert.False(((Control)scope.Find("KeyStep1Border")!).IsVisible);
        Assert.False(((Control)scope.Find("KeyStep2Border")!).IsVisible);
        var google = (Button)scope.Find("VendorGoogleBtn")!;
        Assert.DoesNotContain("★", google.Content?.ToString() ?? string.Empty);
        Assert.DoesNotContain("Primary", google.Classes);
        Assert.DoesNotContain("Recommended", ((TextBlock)scope.Find("VendorCapabilityHint")!).Text ?? string.Empty);

        google.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(((Control)scope.Find("KeyStep1Border")!).IsVisible);
        Assert.Contains("Primary", google.Classes);
    }
}
