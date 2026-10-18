using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class MusicAttributionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "FvsMusicAttrTests_" + Guid.NewGuid().ToString("N"));

    public MusicAttributionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ResolveAttribution_ReadsCompanionTextFile()
    {
        string mp3Path = Path.Combine(_tempDir, "!_TestSong.mp3");
        string txtPath = Path.Combine(_tempDir, "!_TestSong.txt");
        File.WriteAllText(mp3Path, "dummy audio");
        File.WriteAllText(txtPath, "Track: Test Song\nMusic provided by NCS\nWatch: http://example.com");

        string? attribution = MusicAttributionHelper.ResolveSingle(mp3Path);

        Assert.NotNull(attribution);
        Assert.Contains("Track: Test Song", attribution);
        Assert.Contains("Music provided by NCS", attribution);
    }

    [Fact]
    public void ResolveAttribution_GeneratesFallbackForExclamationPrefix_WhenTxtMissing()
    {
        string mp3Path = Path.Combine(_tempDir, "!_Cool Beat NCS.mp3");
        File.WriteAllText(mp3Path, "dummy audio");

        string? attribution = MusicAttributionHelper.ResolveSingle(mp3Path);

        Assert.NotNull(attribution);
        Assert.Contains("Cool Beat NCS", attribution);
        Assert.Contains("Music provided by NoCopyrightSounds", attribution);
    }

    [Fact]
    public void ResolveAttribution_ReturnsNullForRegularTracksWithoutTxt()
    {
        string mp3Path = Path.Combine(_tempDir, "RegularTrack.mp3");
        File.WriteAllText(mp3Path, "dummy audio");

        string? attribution = MusicAttributionHelper.ResolveSingle(mp3Path);

        Assert.Null(attribution);
    }

    [Fact]
    public void ResolveAttribution_AggregatesMultipleTracksWithoutDuplicates()
    {
        string track1 = Path.Combine(_tempDir, "!_SongA.mp3");
        string track2 = Path.Combine(_tempDir, "!_SongB.mp3");
        File.WriteAllText(track1, "a");
        File.WriteAllText(Path.Combine(_tempDir, "!_SongA.txt"), "Credit for Song A");
        File.WriteAllText(track2, "b");
        File.WriteAllText(Path.Combine(_tempDir, "!_SongB.txt"), "Credit for Song B");

        string? combined = MusicAttributionHelper.ResolveAttribution(new[] { track1, track2, track1 });

        Assert.NotNull(combined);
        Assert.Contains("Credit for Song A", combined);
        Assert.Contains("Credit for Song B", combined);
        Assert.Equal(combined.IndexOf("Credit for Song A"), combined.LastIndexOf("Credit for Song A"));
    }

    [AvaloniaFact]
    public void MusicAttributionDialog_SetsAndDisplaysText()
    {
        var dlg = new MusicAttributionDialogWindow();
        try
        {
            dlg.SetAttributionText("Track: Sample\nCredit: Test");
            var box = dlg.FindControl<Avalonia.Controls.TextBox>("AttributionTextBox");
            Assert.NotNull(box);
            Assert.Equal("Track: Sample\nCredit: Test", box.Text);
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void FinishedDialog_AttributionCard_VisibilityMatchesPresence()
    {
        var dlg = new FinishedDialogWindow();
        try
        {
            var card = dlg.FindControl<Avalonia.Controls.Border>("AttributionCard");
            Assert.NotNull(card);
            Assert.False(card.IsVisible);

            dlg.SetAttributionText("Sample Attribution Credit");
            Assert.True(card.IsVisible);

            dlg.SetAttributionText(null);
            Assert.False(card.IsVisible);
        }
        finally
        {
            dlg.Close();
        }
    }
}
