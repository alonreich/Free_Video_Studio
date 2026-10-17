using System.IO;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// AICOMMENTARY_CLOSE_01 / AICOMMENTARY_LOG_01 (2026-10-18): the Speed Editor's APPLY used to close the window,
/// and closing threw an unapplied commentary preview away — and cancelled a running one — without a word, so a
/// user who waited for the AI and pressed APPLY exported a video with no captions.
/// </summary>
public sealed class AiCommentaryCloseTests
{
    private static string Src(string file) =>
        File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", file));

    [Fact]
    public void ApplyKeepsAnUnappliedPreview_AndAsksWhileTheAiIsStillWriting()
    {
        string window = Src("GranularSpeedEditorWindow.axaml.cs");
        Assert.Contains("if (!await PrepareApplyWithCommentaryAsync()) return;", window);

        string ai = Src("GranularSpeedEditorWindow.AiCommentary.cs");
        Assert.Contains("EndAiCommentaryPreview(accept: true);", ai.Split("PrepareApplyWithCommentaryAsync()")[1]);
        Assert.Contains("if (_aiCommentaryRunning && !_closeDuringCommentaryConfirmed)", ai);
        Assert.Contains("e.Cancel = true;", ai);
    }

    [Fact]
    public void EveryRunAndItsOutcomeIsLogged()
    {
        string ai = Src("GranularSpeedEditorWindow.AiCommentary.cs");
        Assert.Contains("\"Run started:", ai);
        Assert.Contains("\"Run finished:", ai);
        Assert.Contains("Commentary applied:", ai);
        Assert.Contains("RuntimeLog.Info(\"AiCommentary\", msg);", ai);
    }

    [Fact]
    public void TheApplyButtonSaysItKeepsThePreviewedCommentary()
    {
        string ai = Src("GranularSpeedEditorWindow.AiCommentary.cs");
        Assert.Contains("\"APPLY (incl. commentary)\"", ai);
    }
}
