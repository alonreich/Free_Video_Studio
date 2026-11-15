using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class UiAuditFixTests
{
    private static readonly HashSet<string> LiteralFontSizeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "CropToolWindow.axaml",
    };

    [Fact]
    public void EveryThemeTokenThatIsUsedIsDefined()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in RepoRoot.SourceFiles(".axaml"))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"x:Key=""([^""]+)""")) defined.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"(?:Dynamic|Static)Resource\s+(App\w+)"))
                used.TryAdd(m.Groups[1].Value, RepoRoot.Relative(file));
        }
        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"Resources\[\s*""(App\w+)""\s*\]\s*=")) defined.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"ThemeResources\.\w+\([^,()]+,\s*""(App\w+)"""))
                used.TryAdd(m.Groups[1].Value, RepoRoot.Relative(file));
        }

        var missing = used.Where(u => !defined.Contains(u.Key)).Select(u => $"{u.Key} (first used in {u.Value})").ToList();
        Assert.True(missing.Count == 0,
            "THEMETOKENS_01 — these tokens are used but never defined, so they render transparent or fall back to a fixed colour:"
          + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void FontSizesFollowTheTextSizeSetting()
    {
        var offenders = new List<string>();
        foreach (string file in RepoRoot.SourceFiles(".axaml"))
        {
            if (LiteralFontSizeFiles.Contains(Path.GetFileName(file))) continue;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"FontSize=""\s*\d"))
                    offenders.Add($"{RepoRoot.Relative(file)}:{i + 1}");
        }
        Assert.True(offenders.Count == 0,
            "FONTTOKENS_01 — a literal FontSize ignores Settings › Text size. Use {DynamicResource AppFontSizeN}:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryWindowUsesTheAppsOwnTitleBar()
    {
        var offenders = RepoRoot.SourceFiles(".axaml")
            .Where(f => File.ReadAllText(f).Contains("<Window", StringComparison.Ordinal))
            .Where(f => !Path.GetFileName(f).Equals("UpdateToastWindow.axaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !File.ReadAllText(f).Contains("ExtendClientAreaToDecorationsHint=\"True\"", StringComparison.Ordinal))
            .Select(RepoRoot.Relative)
            .ToList();
        Assert.True(offenders.Count == 0, "UI-RESIZEGRIP — these windows show the plain Windows title bar:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("12,5", 12.5)]
    [InlineData(" 3 ", 3)]
    [InlineData("0:12.5", 12.5)]
    [InlineData("1:05", 65)]
    public void CaptionTimesAcceptCommonFormats(string text, double expected)
    {
        Assert.True(EditCaptionDialogWindow.TryParseTime(text, out double seconds));
        Assert.Equal(expected, seconds, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1:75")]
    [InlineData("1:2:3")]
    public void CaptionTimesRejectNonsense(string text) =>
        Assert.False(EditCaptionDialogWindow.TryParseTime(text, out _));

    [Fact]
    public void CaptionEditIsRejectedWithAReasonInsteadOfSilentlyChanged()
    {
        Assert.Contains("Start time", EditCaptionDialogWindow.Validate("abc", "2", "hi").Problem);
        Assert.Contains("End time is not", EditCaptionDialogWindow.Validate("1", "x", "hi").Problem);
        Assert.Contains("after the start", EditCaptionDialogWindow.Validate("5", "4", "hi").Problem);
        Assert.Contains("empty", EditCaptionDialogWindow.Validate("1", "2", "   ").Problem);
        Assert.True(EditCaptionDialogWindow.Validate("1", "2", "   ", requireText: false).IsValid);

        var ok = EditCaptionDialogWindow.Validate("1,5", "0:03", "nice shot");
        Assert.True(ok.IsValid);
        Assert.Equal(1.5, ok.StartSec, 6);
        Assert.Equal(3, ok.EndSec, 6);
        Assert.Null(ok.Problem);
    }

    [Fact]
    public void ErrorsAreTranslatedIntoPlainEnglish()
    {
        Assert.Contains("internet", UserFacingError.Describe(new HttpRequestException("No such host is known.")));
        Assert.Contains("Too many requests",
            UserFacingError.Describe(new HttpRequestException("rate", null, HttpStatusCode.TooManyRequests)));
        Assert.Contains("refused", UserFacingError.Describe(new HttpRequestException("x", null, HttpStatusCode.Forbidden)));
        Assert.Contains("trouble", UserFacingError.Describe(new HttpRequestException("x", null, HttpStatusCode.BadGateway)));
        Assert.Contains("too long", UserFacingError.Describe(new TimeoutException()));
        Assert.Contains("drive is full", UserFacingError.Describe(new IOException("disk", unchecked((int)0x80070070))));
        Assert.Contains("blocked access", UserFacingError.Describe(new UnauthorizedAccessException("Access to the path 'C:\\x' is denied.")));
        Assert.Contains("internet", UserFacingError.Describe(new InvalidOperationException("outer", new HttpRequestException("inner"))));

        string generic = UserFacingError.Describe(new InvalidOperationException("Sequence contains no elements"));
        Assert.DoesNotContain("Sequence contains", generic);
        Assert.Contains("Show Logs", generic);
    }

    [Fact]
    public void RawExceptionTextIsNotShownAtTheAuditedSites()
    {
        string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot.Path, rel));
        Assert.DoesNotContain("NativeDialog.ShowError($\"Update check failed: {ex.Message}", Read("src/FreeVideoStudio.App/Services/UpdateService.cs"));
        Assert.DoesNotContain("\"The download failed: \" + ex.Message", Read("src/FreeVideoStudio.App/Controls/SettingsWindow.LocalAi.cs"));
        Assert.DoesNotContain("\"Regeneration Failed\", $\"{ex.Message}", Read("src/FreeVideoStudio.App/GranularSpeedEditorWindow.CaptionsLane.cs"));
        Assert.DoesNotContain("AI Auto-Fit failed: {ex.Message}\", \"AppDangerBrush\"", Read("src/FreeVideoStudio.App/MusicWizardWindow.AiAutoFit.cs"));
    }

    [Fact]
    public void VoiceOverCloseDuringApplyExplainsItself()
    {
        string apply = File.ReadAllText(Path.Combine(RepoRoot.Path, "src/FreeVideoStudio.App/VoiceOverWindow.Apply.cs"));
        Assert.Matches(new Regex(@"if \(_isApplying\)\s*\{\s*e\.Cancel = true;\s*_closeRequestedDuringApply = true;\s*Controls\.FloatingNotice\.Show\("), apply);
        Assert.Matches(new Regex(@"if \(_closeRequestedDuringApply && !_isClosing\)\s*\{\s*_closeRequestedDuringApply = false;\s*Controls\.FloatingNotice\.Warn\(this, StayedOpenAfterApplyMessage\);"), apply);
        Assert.Contains("stayed open", VoiceOverWindow.StayedOpenAfterApplyMessage);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 8)]
    [InlineData(8, 16)]
    [InlineData(32, 64)]
    [InlineData(128, 64)]
    public void VoiceOverPreviewDecodesWithMoreFrameThreads(int cores, int expected) =>
        Assert.Equal(expected, VoiceOverPreviewRenderer.DecodeThreads(cores));

    [Theory]
    [InlineData("out_time_us=5000000", 10, 0.5)]
    [InlineData("out_time_us=20000000", 10, 1.0)]
    [InlineData("out_time_us=0", 10, 0.0)]
    public void VoiceOverPreviewProgressIsParsed(string line, double total, double expected) =>
        Assert.Equal(expected, VoiceOverPreviewRenderer.ParseProgressFraction(line, total)!.Value, 6);

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("frame=12")]
    [InlineData("progress=continue")]
    public void VoiceOverPreviewIgnoresOtherProgressLines(string line) =>
        Assert.Null(VoiceOverPreviewRenderer.ParseProgressFraction(line, 10));

    [Fact]
    public void MessagesWrittenForUsersPassThrough()
    {
        const string key = "Your API key was rejected. Check it in Settings › AI.";
        Assert.Equal(key, UserFacingError.Describe(new AiProviderException(AiVendor.Google, AiProviderErrorKind.InvalidKey, key)));
        Assert.Equal(key, UserFacingError.Describe(new InvalidOperationException("outer", new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.InvalidKey, key))));
        const string pack = "The Local AI pack is in use. Restart Free Video Studio, then install it again.";
        Assert.Equal(pack, UserFacingError.Describe(new UserFacingIOException(pack)));
        Assert.Equal("Pick another song.", UserFacingError.Describe(new AiMusicAutoFitException("Pick another song.")));
        Assert.Contains("cancelled", UserFacingError.Describe(new OperationCanceledException("The user declined the Windows permission prompt.")));
        Assert.Contains("Check the drive", UserFacingError.Describe(new IOException("raw system text")));
    }

    [Fact]
    public void CuratedDownloadAndInstallErrorsUseTheUserFacingType()
    {
        foreach (string rel in new[] { "src/FreeVideoStudio.App/Services/LocalAiPackService.cs", "src/FreeVideoStudio.App/Services/UpdateDownloader.cs" })
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot.Path, rel));
            Assert.DoesNotMatch(new Regex(@"new IOException\(\$?"""), text);
        }
    }

    [AvaloniaFact]
    public void LightThemeOverridesAreNotHiddenByRootTokens()
    {
        var app = new AvaloniaApp();
        app.Initialize();
        Color Lookup(string key, ThemeVariant theme)
        {
            Assert.True(app.Resources.TryGetResource(key, theme, out object? value), key);
            return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
        }
        Assert.Equal(Color.Parse("#a16207"), Lookup("AppWarningBrush", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#facc15"), Lookup("AppWarningBrush", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#2563eb"), Lookup("AppAccentBrush", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#60a5fa"), Lookup("AppAccentBrush", ThemeVariant.Dark));
        Assert.True(app.Resources.TryGetResource("AppWarningBrush", null, out object? themeless));
        Assert.Equal(Color.Parse("#facc15"), Assert.IsAssignableFrom<ISolidColorBrush>(themeless).Color);
    }

    [Fact]
    public void CaptionTimesMustStayInsideTheVideo()
    {
        Assert.Contains("past the end", EditCaptionDialogWindow.Validate("50", "70", "hi", minSec: 0, maxSec: 60).Problem);
        Assert.Contains("past the end", EditCaptionDialogWindow.Validate("5000", "5001", "hi", minSec: 0, maxSec: 60).Problem);
        Assert.Contains("before the video starts", EditCaptionDialogWindow.Validate("1", "3", "hi", minSec: 2, maxSec: 60).Problem);
        Assert.True(EditCaptionDialogWindow.Validate("58", "60.00", "hi", minSec: 0, maxSec: 59.996).IsValid);
        Assert.True(EditCaptionDialogWindow.Validate("5000", "5001", "hi").IsValid);
    }
}
