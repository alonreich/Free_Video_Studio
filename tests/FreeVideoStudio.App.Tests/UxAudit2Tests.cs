using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using FreeVideoStudio.App.Controls;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class UxAudit2Tests
{
    private static string Src(string relative) => File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", relative));

    [Fact]
    public void ErrorNoticesStayLongEnoughToReadAndAreAnnounced()
    {
        Assert.True(FloatingNotice.HoldMsFor(NoticeKind.Error, "x") >= 6000);
        Assert.True(FloatingNotice.HoldMsFor(NoticeKind.Warning, "x") >= 4500);
        Assert.True(FloatingNotice.HoldMsFor(NoticeKind.Info, "Saved") <= 2000);
        string longText = new string('a', 160);
        Assert.True(FloatingNotice.HoldMsFor(NoticeKind.Info, longText) > FloatingNotice.HoldMsFor(NoticeKind.Info, "a"));
        Assert.True(FloatingNotice.HoldMsFor(NoticeKind.Error, new string('a', 5000)) <= 15000);
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Assertive, FloatingNotice.LiveSettingFor(NoticeKind.Error));
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Assertive, FloatingNotice.LiveSettingFor(NoticeKind.Warning));
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, FloatingNotice.LiveSettingFor(NoticeKind.Success));
    }

    [Theory]
    [InlineData("MmResult.BadDeviceId calling waveInOpen")]
    [InlineData("MmResult.Allocated calling waveInOpen")]
    [InlineData("0x88890004 AUDCLNT_E_DEVICE_INVALIDATED")]
    [InlineData("Something odd: E_FAIL 0x80004005")]
    public void MicrophoneFaultsNeverShowRawSystemText(string raw)
    {
        string text = VoiceOverWindow.DescribeMicFault(new InvalidOperationException(raw));
        Assert.DoesNotContain("MmResult", text);
        Assert.DoesNotContain("0x", text);
        Assert.DoesNotContain("waveIn", text);
        Assert.Contains("microphone", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows", VoiceOverWindow.DescribeMicFault(new UnauthorizedAccessException("denied")));
        Assert.Contains("microphone", VoiceOverWindow.DescribeMicFault(null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VoiceOverDoesNotPutExceptionMessagesOnScreen()
    {
        string vo = Src("VoiceOverWindow.axaml.cs") + Src("VoiceOverWindow.Apply.cs");
        Assert.DoesNotContain("fault?.Message ?? \"device error\"}\");", vo);
        Assert.DoesNotContain("Microphone error: {err}", vo);
        Assert.DoesNotContain("could not be saved ({ex.Message})", vo);
    }

    [Fact]
    public void HandoffFailureTellsTheUserWhatToDo()
    {
        string message = SingleInstanceGuard.HandoffFailureMessage(@"C:\clips\match 12.mp4");
        Assert.Contains("match 12.mp4", message);
        Assert.Contains("OPEN VIDEO", message);
        Assert.Contains("HandoffFailedNotifier(", Src("SingleInstanceGuard.cs"));
    }

    [Fact]
    public void MergerRemoveIsInstantAndNeverClaimsItCannotBeUndone()
    {
        string xaml = Src("VideoMergerWindow.axaml");
        Assert.DoesNotContain("ConfirmRemoveVideoButton", xaml);
        int remove = xaml.IndexOf("Name=\"RemoveVideoButton\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("</Button>", remove, StringComparison.Ordinal);
        Assert.True(remove > 0 && end > remove);
        string removeButton = xaml[remove..end];
        Assert.DoesNotContain("Button.Flyout", removeButton);
        Assert.DoesNotContain("Cannot be undone", removeButton);
        string code = Src("VideoMergerWindow.axaml.cs");
        Assert.Contains("_ = RequestRemoveClipsAsync(TakeToolbarRemoveIds());", code);
    }

    [Fact]
    public void FlyoutConfirmationsDoNotFlashWhenTheyAreSwitchedOff()
    {
        Assert.Contains("ConfirmFlyoutGate.Attach(clearBtn", Src("GranularSpeedEditorWindow.axaml.cs"));
        Assert.Contains("ConfirmFlyoutGate.Attach(cancelButton", Src("MainWindow.Wireup.cs"));
    }

    [Fact]
    public void TextOnTheDarkOverlayIsLightInBothThemes()
    {
        const string overlay = "{DynamicResource AppOverlayBrush}";
        var themeText = new HashSet<string>(StringComparer.Ordinal)
        {
            "{DynamicResource AppTextPrimaryBrush}",
            "{DynamicResource AppTextSecondaryBrush}",
            "{DynamicResource AppTextMutedBrush}",
        };
        var stops = new HashSet<string>(StringComparer.Ordinal) { "Flyout", "ToolTip", "ContextMenu", "Window", "UserControl" };
        var offenders = new List<string>();
        foreach (string file in RepoRoot.SourceFiles(".axaml"))
        {
            if (Path.GetFileName(file).Equals("AvaloniaApp.axaml", StringComparison.OrdinalIgnoreCase)) continue;
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement el in doc.Descendants())
            {
                string? fg = (string?)el.Attribute("Foreground");
                if (fg == null || !themeText.Contains(fg)) continue;
                string? owner = null;
                for (XElement? p = el.Parent; p != null; p = p.Parent)
                {
                    if (stops.Contains(p.Name.LocalName)) break;
                    string? bg = (string?)p.Attribute("Background");
                    if (bg != null && bg != "Transparent") { owner = bg; break; }
                }
                if (owner == overlay)
                    offenders.Add($"{RepoRoot.Relative(file)}:{((IXmlLineInfo)el).LineNumber}");
            }
        }
        Assert.True(offenders.Count == 0,
            "OVERLAYTEXT_01 — text on AppOverlayBrush must use AppOnOverlayTextBrush / AppOnOverlayMutedTextBrush:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void LoadingAnotherVideoAsksOnlyWhenWorkWouldBeLost()
    {
        string load = Src("MainWindow.axaml.cs");
        int body = load.IndexOf("private async Task LoadVideoIntoEditorAsync(", StringComparison.Ordinal);
        int guard = load.IndexOf("ConfirmReplaceLoadedVideoAsync(path)", body, StringComparison.Ordinal);
        int reset = load.IndexOf("ResetEditingStateForNewVideo();", body, StringComparison.Ordinal);
        Assert.True(body > 0 && guard > body && guard < reset, "NEWVIDEOGUARD_01 — the question must come before the edits are cleared.");

        string project = Src("MainWindow.Project.cs");
        Assert.Contains("if (_exportedCleanSinceLastEdit || !HasUnsavedWork()) return true;", project);
        Assert.Contains("ConfirmDiscardAsync(\"open another video\")", project);
    }

    [Fact]
    public void FinishedWorkClosesWithoutAskingAndStartsEmpty()
    {
        string project = Src("MainWindow.Project.cs");
        int close = project.IndexOf("ConfirmProjectDiscardOnCloseAsync()", StringComparison.Ordinal);
        int clean = project.IndexOf("if (_exportedCleanSinceLastEdit)", close, StringComparison.Ordinal);
        int ask = project.IndexOf("ConfirmDiscardAsync(\"Close\")", close, StringComparison.Ordinal);
        Assert.True(clean > close && clean < ask, "DONECLEAN_01 — a finished export must skip the close question.");
        Assert.Contains("_recovery.ClearState();", project);

        string merger = Src("VideoMergerWindow.axaml.cs");
        Assert.Contains("MarkMergeFinishedClean();", merger);
        Assert.Contains("if (_mergedCleanSinceLastEdit) WipeFinishedMergeForExit();", merger);
        string session = Src("VideoMergerWindow.Session.cs");
        Assert.Contains("MergerAutosave.Value.Clear();", session);
        Assert.Contains("_mergedCleanSinceLastEdit = false;", session);
    }

    [Fact]
    public void CrashRecoveryKeepsEverythingNeededToCarryOn()
    {
        string save = Src("MainWindow.Recovery.cs");
        foreach (string key in new[] { "playheadSec", "aiEliminations", "applyPeakFlattening", "gameplayLoudnessLufs" })
            Assert.Contains($"transient[\"{key}\"]", save);

        string restore = Src("MainWindow.Restore.cs");
        Assert.Contains("RestoreSessionExtras(state, videoPath, metadataReady);", restore);
        Assert.Contains("_projectSession?.MarkRecovered();", restore);
        Assert.Contains("metadataReady = await WaitForVideoMetadataAsync(previousDuration);", restore);
        Assert.Contains("_viewModel.Timeline.LoadedVideoDurationMs = _loadedVideoDurationMs;", restore);

        string main = Src("MainWindow.axaml.cs");
        int enable = main.IndexOf("private void EnableEditingControls()", StringComparison.Ordinal);
        int overlay = main.IndexOf("timelineOverlay.IsVisible = true;", enable, StringComparison.Ordinal);
        Assert.True(enable > 0 && overlay > enable && overlay - enable < 400,
            "RECOVERYFULL_01 — the timeline must come back with the editing controls, after a fresh load and after a restore.");
    }
}
