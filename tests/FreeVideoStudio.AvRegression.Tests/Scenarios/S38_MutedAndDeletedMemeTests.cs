using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

[Collection(AvCollection.Name)]
public sealed class S38_MutedAndDeletedMemeTests
{
    [Fact, Trait("Tier", "Full")]
    public async Task S38_MutedAndDeletedMemesHaveNoExportedSoundOrResidualPicture()
    {
        var edit = new MainEdit { Source = await TestMedia.GameplayAsync(), TrimStartMs = 2000, TrimEndMs = 13000, BaseSpeed = 1.1 };
        string meme = await TestMedia.MemeAsync(1.0);
        edit.Memes.Add(new MemePlacement(meme, 3, 1, "muted-inline") { PlaySound = false });
        edit.Memes.Add(new MemePlacement(meme, 7, 1, "muted-corner", MemePresentationMode.CornerOverlay) { PlaySound = false });
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S38-muted"));
        double c = Verify.Gameplay(edit, scan);
        Verify.InlineMemes(edit, scan, c);
        Verify.CornerMemes(edit, scan, c);
        edit.Memes.Clear();
        scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S38-deleted"));
        c = Verify.Gameplay(edit, scan);
        Verify.InlineMemes(edit, scan, c);
        Verify.CornerMemes(edit, scan, c);
    }

    [Fact, Trait("Tier", "Quick")]
    public void IntervalChecksRejectUnexpectedMissingDuplicatedAndShiftedEvidence()
    {
        Verify.MatchSpans("overlap", [(1, 3), (2, 4)], [(1, 4)], 0.08);
        Assert.Throws<AvDefect>(() => Verify.MatchSpans("muted", [], [(1, 2)], 0.08));
        Assert.Throws<AvDefect>(() => Verify.MatchSpans("missing", [(1, 2)], [], 0.08));
        Assert.Throws<AvDefect>(() => Verify.MatchSpans("duplicate", [(1, 2)], [(1, 2), (1, 2)], 0.08));
        Assert.Throws<AvDefect>(() => Verify.MatchSpans("drift", [(1, 2)], [(1.2, 2.2)], 0.08));
        Assert.Throws<AvDefect>(() => Av.Near("invalid", double.NaN, 1, 0.08));
        Assert.Throws<AvDefect>(() => Av.Latency("invalid", double.NaN));
    }
}
