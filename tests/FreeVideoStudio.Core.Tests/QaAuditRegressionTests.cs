using System;
using System.IO;
using System.Linq;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class QaAuditRegressionTests
{
    private static string Work(string name)
    {
        string dir = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "QaAudit", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CaptionTrack Track(CaptionLine line) =>
        new(CaptionStyle.Karaoke, CaptionPosition.BottomContent, CaptionTone.Hype, "en", new[] { line });

    [Fact]
    public void Qa01_EmphasisOnOneNonLatinWord_HighlightsOnlyThatWord()
    {
        var line = new CaptionLine(1, 1.0, 4.0, "Он убежал и я догнал его", new[] { "догнал" }, 0.9);

        var words = CaptionTimeline.Map(Track(line), null)[0].Words;

        Assert.True(words.Single(w => w.Text == "догнал").IsEmphasized);
        Assert.Equal(new[] { "догнал" }, words.Where(w => w.IsEmphasized).Select(w => w.Text).ToArray());
    }

    [Fact]
    public void Qa01_PunctuationOnlyEmphasis_HighlightsNothing()
    {
        var line = new CaptionLine(1, 1.0, 4.0, "Wait — what was that ?!", new[] { "!!!" }, 0.9);

        var words = CaptionTimeline.Map(Track(line), null)[0].Words;

        Assert.DoesNotContain(words, w => w.IsEmphasized);
    }

    [Fact]
    public void Qa01_LatinEmphasisStillMatchesThroughPunctuationAndCase()
    {
        var line = new CaptionLine(1, 1.0, 4.0, "He ran so I CHASED, him", new[] { "chased" }, 0.9);

        var words = CaptionTimeline.Map(Track(line), null)[0].Words;

        Assert.Equal(new[] { "CHASED," }, words.Where(w => w.IsEmphasized).Select(w => w.Text).ToArray());
    }

    [Theory]
    [InlineData(true, 1080, 1920)]
    [InlineData(false, 1920, 1080)]
    public void Qa02_VeryLongCaption_KeepsEveryLineInsideTheFrame(bool portrait, int width, int height)
    {
        string text = string.Join(" ", Enumerable.Repeat("repositioning carefully behind cover", 10));
        var mapped = CaptionTimeline.Map(Track(new CaptionLine(1, 0.0, 12.0, text)), null)[0];

        var layout = CaptionLayoutEngine.ComputeLayout(mapped, width, height, portrait);

        Assert.Equal(40, layout.Lines.Sum(l => l.Words.Count));
        Assert.All(layout.Lines, l => Assert.True(l.Width <= width,
            $"a caption line is {l.Width:0} px wide on a {width} px frame"));
    }

    [Fact]
    public void Qa02_OrdinarySentenceThatNeedsThreeRows_StaysInsideThePortraitFrame()
    {
        const string text = "I took a lot of damage so I am repositioning behind the cover to heal";
        var mapped = CaptionTimeline.Map(Track(new CaptionLine(1, 0.0, 6.0, text)), null)[0];

        var layout = CaptionLayoutEngine.ComputeLayout(mapped, 1080, 1920, isPortrait: true);

        Assert.Equal(15, layout.Lines.Sum(l => l.Words.Count));
        Assert.All(layout.Lines, l => Assert.True(l.Width <= 1080,
            $"a caption line is {l.Width:0} px wide on a 1080 px frame"));
    }

    [Fact]
    public void Qa03_FailedRescue_LeavesNoEmptyRecoveredFile()
    {
        string dir = Work("rescue-locked");
        string source = Path.Combine(dir, "core.mp4");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
        string temp = Path.Combine(dir, "temp");

        string? rescued;
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            rescued = RescuedOutputPath.TryRescue(source, temp, OutputFileNaming.MainRecoveredPrefix, "Output", "Could not preserve the finished render");
        }

        Assert.Null(rescued);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(temp));
    }

    [Fact]
    public void Qa03_TwoRescuesInTheSameSecond_BothSurviveUnderDifferentNames()
    {
        string dir = Work("rescue-twice");
        string temp = Path.Combine(dir, "temp");
        string first = Path.Combine(dir, "a.mp4");
        string second = Path.Combine(dir, "b.mp4");
        File.WriteAllBytes(first, new byte[] { 1 });
        File.WriteAllBytes(second, new byte[] { 2, 2 });

        string? one = RescuedOutputPath.TryRescue(first, temp, OutputFileNaming.MergerRecoveredPrefix, "Merger", "Could not preserve the finished merge");
        string? two = RescuedOutputPath.TryRescue(second, temp, OutputFileNaming.MergerRecoveredPrefix, "Merger", "Could not preserve the finished merge");

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one, two);
        Assert.Equal(1, new FileInfo(one!).Length);
        Assert.Equal(2, new FileInfo(two!).Length);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Null(RescuedOutputPath.TryRescue(first, temp, OutputFileNaming.MergerRecoveredPrefix, "Merger", "Could not preserve the finished merge"));
    }

    [Fact]
    public void Qa04_OneUnreadableRecentEntry_DoesNotHideOrEraseTheOthers()
    {
        string dir = Work("recent");
        string store = Path.Combine(dir, "recent_projects.json");
        string a = Path.Combine(dir, "a.fvsproj");
        string c = Path.Combine(dir, "c.fvsproj");
        string json = "{\"entries\":["
            + "{\"path\":" + System.Text.Json.JsonSerializer.Serialize(a) + ",\"title\":\"A\",\"opened_utc_seconds\":3},"
            + "{\"path\":42,\"title\":\"broken\"},"
            + "{\"path\":" + System.Text.Json.JsonSerializer.Serialize(c) + ",\"title\":7,\"opened_utc_seconds\":1}"
            + "]}";
        File.WriteAllText(store, json);
        var recent = new RecentProjects(store);

        Assert.Equal(new[] { a, c }, recent.Read().Select(e => e.Path).ToArray());

        string d = Path.Combine(dir, "d.fvsproj");
        recent.Touch(d, "D");

        Assert.Equal(new[] { d, a, c }, recent.Read().Select(e => e.Path).ToArray());
    }
}
