using System.Collections.Generic;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Media;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System.Reflection;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// MUSICWIN_01 — the one reader that turns a saved music result into plan tracks for the Main App
/// export, the Main App preview, the merger export and the merger preview.
/// </summary>
public sealed class MusicWizardResultPlanTests
{
    [AvaloniaTheory]
    [InlineData(1.1, false)]
    [InlineData(2.0, false)]
    [InlineData(0.5, false)]
    [InlineData(1.0, false)]
    [InlineData(1.1, true)]
    public void FullMusicCoverageReachesExportEnd(double speed, bool loop)
    {
        const double start = 42157.95852637206, end = 90026.8125;
        var result = WizardResult(start, end, speed, 260.79, 13.15, loop);
        Assert.Equal(end / 1000, result.TimelineEndSeconds, 6);
        var timeline = OutputTimeline.Create(end - start, null, speed, start);
        double duration = timeline.SourceToOutput(result.TimelineEndSeconds);
        Assert.InRange(duration, (end - start) / 1000 / speed - 0.02, (end - start) / 1000 / speed + 0.02);
        var aligned = MusicPadAlignment.Align(new[] { new MusicTrack("song.mp3", 13.15, duration, 0, true) },
            1, 0, duration + 1, true, true);
        Assert.Equal(duration + 1.1, aligned[0].TimelineStartDelay + aligned[0].Duration + 0.1, 6);
    }

    [AvaloniaFact]
    public void ShortSongKeepsItsActualCoverageAcrossCutsSlowMotionAndFreezes()
    {
        const double start = 10000, end = 70000;
        var segments = new List<SpeedSegment> { new(20000, 30000, 0.5), new(40000, 43000, 0) };
        var cuts = new List<CutRange> { new(15000, 18000) };
        var result = WizardResult(start, end, 1.1, 55, 5, false, segments, cuts);
        var timeline = OutputTimeline.Create(end - start, segments, 1.1, start,
            cuts: CutRange.ToClipRelative(cuts, start));
        Assert.True(timeline.TotalOutputSeconds > 50);
        Assert.Equal(50, timeline.SourceToOutput(result.TimelineEndSeconds), 5);
        var full = WizardResult(start, end, 1.1, 300, 5, false, segments, cuts);
        Assert.Equal(end / 1000, full.TimelineEndSeconds, 6);
    }

    private static MusicWizardResult WizardResult(double start, double end, double speed, double songLength,
        double songOffset, bool loop, List<SpeedSegment>? segments = null, List<CutRange>? cuts = null)
    {
        string root = System.IO.Path.Combine(RepoRoot.Path, "TestResults", "music-end-" + System.Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        var window = new MusicWizardWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string field, object value) => typeof(MusicWizardWindow).GetField(field, flags)!.SetValue(window, value);
        T Get<T>(string field) => (T)typeof(MusicWizardWindow).GetField(field, flags)!.GetValue(window)!;
        try
        {
            Set("_trimStartMs", start);
            Set("_trimEndMs", end);
            Set("_phase3BaseSpeed", speed);
            if (segments != null) Get<List<SpeedSegment>>("_phase3SpeedSegments").AddRange(segments);
            if (cuts != null) Get<List<CutRange>>("_phase3Cuts").AddRange(cuts);
            Get<List<MusicPlaylistEntry>>("_playlist").Add(new("song.mp3", songLength, songOffset, songLength));
            window.FindControl<CheckBox>("LoopMusicCheckBox")!.IsChecked = loop;
            return (MusicWizardResult)typeof(MusicWizardWindow).GetMethod("BuildResultFromPlaylist", flags)!.Invoke(window, null)!;
        }
        finally { window.Close(); }
    }

    [Fact]
    public void AMissingSongDoesNotShiftTheOtherSongsLengthsOrParts()
    {
        var r = new MusicWizardResult
        {
            MusicFilePath = "a.mp3",
            MusicFilePaths = new List<string> { "a.mp3", "missing.mp3", "c.mp3" },
            MusicDurationsSeconds = new List<double> { 60, 70, 80 },
            MusicStartsSeconds = new List<double> { 5, 6, 7 },
            MusicEndsSeconds = new List<double> { 50, 0, 70 },
        };

        var tracks = r.ToBedTracks(p => p != "missing.mp3");

        Assert.Equal(2, tracks.Count);
        Assert.Equal(("a.mp3", 60.0, 5.0, 50.0), (tracks[0].Path, tracks[0].KnownSec, tracks[0].InSec, tracks[0].OutSec));
        Assert.Equal(("c.mp3", 80.0, 7.0, 70.0), (tracks[1].Path, tracks[1].KnownSec, tracks[1].InSec, tracks[1].OutSec));
    }

    [Fact]
    public void AnOldResultWithoutWindowsUsesTheLegacyLayout()
    {
        var r = new MusicWizardResult
        {
            MusicFilePath = "a.mp3",
            MusicFilePaths = new List<string> { "a.mp3", "b.mp3" },
            MusicDurationsSeconds = new List<double> { 60, 90 },
            OffsetSeconds = 12,
        };

        var tracks = r.ToBedTracks(_ => true);

        Assert.Equal(12.0, tracks[0].InSec);
        Assert.Equal(0.0, tracks[1].InSec);
        Assert.Equal(0.0, tracks[0].OutSec);
        Assert.Equal(60.0, tracks[0].WindowEndSec);
    }

    [Fact]
    public void ASingleSongResultStillWorks()
    {
        var r = new MusicWizardResult { MusicFilePath = "solo.mp3", MusicDurationSeconds = 200, OffsetSeconds = 30 };
        var tracks = r.ToBedTracks(_ => true);
        Assert.Single(tracks);
        Assert.Equal(200.0, tracks[0].KnownSec);
        Assert.Equal(30.0, tracks[0].InSec);
        Assert.Equal("solo", r.DescribeSongs());
    }
}
