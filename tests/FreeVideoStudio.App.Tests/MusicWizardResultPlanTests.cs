// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Collections.Generic;
using FreeVideoStudio.App;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// MUSICWIN_01 — the one reader that turns a saved music result into plan tracks for the Main App
/// export, the Main App preview, the merger export and the merger preview.
/// </summary>
public sealed class MusicWizardResultPlanTests
{
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
