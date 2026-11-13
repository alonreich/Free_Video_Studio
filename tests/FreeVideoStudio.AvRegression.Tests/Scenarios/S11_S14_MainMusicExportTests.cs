using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// Main window — music placed with the pink music notes, then a REAL export. The music must start on
/// the START note, stop on the END note and stay on the beat the preview showed, whatever the
/// speed, speed blocks, freeze, cuts, fades and memes do.
/// </summary>
[Collection(AvCollection.Name)]
public sealed class S11_S14_MainMusicExportTests
{
    private static async Task<MainEdit> EditAsync(double musicStart, double musicEnd, bool fades, double fileOffset = 0)
    {
        var clip = await TestMedia.GameplayAsync();
        string music = await TestMedia.MusicAsync();
        return new MainEdit
        {
            Source = clip, TrimStartMs = 2000, TrimEndMs = 18000, BaseSpeed = 1.1, Fades = fades,
            Music = MainEdit.MusicAt(musicStart, musicEnd, music, 60, fileOffset),
        };
    }

    /// <summary>The reported bug: only the music notes are moved, and the exported music ends somewhere else.</summary>
    [Theory, Trait("Tier", "Quick")]
    [InlineData(5.0, 12.0, true)]
    [InlineData(5.0, 12.0, false)]
    [InlineData(2.0, 11.0, true)]
    [InlineData(6.5, 18.0, true)]
    public async Task S11_MoveOnlyTheMusicNotes_ExportMusicStartsAndEndsOnTheNotes(double start, double end, bool fades)
    {
        var edit = await EditAsync(start, end, fades, fileOffset: 3.0);
        string work = Repo.WorkDir("S11");
        var scan = await ExportRig.MainAsync(edit, work);
        double c = Verify.Gameplay(edit, scan);
        Verify.Music(edit, scan, c);
    }

    [Theory, Trait("Tier", "Quick")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S12_MusicWithSpeedBlocksFreezeAndCut_StaysOnTheBeat(bool fades)
    {
        var edit = await EditAsync(3.0, 17.0, fades);
        edit.Segments.Add(new SpeedSegment(5000, 7000, 0.5));
        edit.Segments.Add(new SpeedSegment(9500, 11500, 2.0));
        edit.FreezeTimeMs = 12500; edit.FreezeDurationS = 1.0;
        edit.Cuts.Add(new CutRange(14000, 15000));
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S12"));
        double c = Verify.Gameplay(edit, scan);
        Verify.Music(edit, scan, c);
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S13_MusicVolumeDuckingAndLoop_KeepTheBeatAndTheEnd()
    {
        var clip = await TestMedia.GameplayAsync();
        string shortSong = await TestMedia.MusicAsync(seed: 8, seconds: 6);
        var edit = new MainEdit
        {
            Source = clip, TrimStartMs = 1000, TrimEndMs = 17000, BaseSpeed = 1.25, Fades = true,
            Music = MainEdit.MusicAt(4.0, 15.0, shortSong, 6, loop: true),
        };
        edit.Music.MusicVolume = 0.45; edit.Music.VideoVolume = 0.7; edit.Music.EnableDucking = true;
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S13"));
        double c = Verify.Gameplay(edit, scan);
        Verify.Music(edit, scan, c);
    }

    /// <summary>Undo/redo returns the exact edit, so the export after undo+redo is the export before.</summary>
    [Fact, Trait("Tier", "Quick")]
    public async Task S14_UndoRedo_ThenExport_SameTimingAsBefore()
    {
        var edit = await EditAsync(4.0, 14.0, fades: false);
        edit.Segments.Add(new SpeedSegment(6000, 8000, 0.5));
        var before = edit.Compose("x");

        var history = new Stack<List<SpeedSegment>>();
        history.Push(new List<SpeedSegment>(edit.Segments));
        edit.Segments.Add(new SpeedSegment(10000, 12000, 2.0));
        var redo = new List<SpeedSegment>(edit.Segments);
        edit.Segments.Clear(); edit.Segments.AddRange(history.Pop());
        edit.Segments.Clear(); edit.Segments.AddRange(redo);
        edit.Segments.RemoveAt(edit.Segments.Count - 1);
        var after = edit.Compose("x");
        Av.True("undo/redo restores the same speed blocks", before.SpeedSegments!.SequenceEqual(after.SpeedSegments!));
        Av.True("undo/redo restores the same music placement",
            before.MusicTracks!.Select(t => (t.Offset, t.Duration, t.TimelineStartDelay)).SequenceEqual(after.MusicTracks!.Select(t => (t.Offset, t.Duration, t.TimelineStartDelay))));

        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S14"));
        double c = Verify.Gameplay(edit, scan);
        Verify.Music(edit, scan, c);
    }
}
