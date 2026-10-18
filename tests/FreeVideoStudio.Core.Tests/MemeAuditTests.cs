using System;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// MEMEAUDIT_01 — the meme audit (2026-10-17): every Core rule it changed, pinned.
/// </summary>
public sealed class MemeAuditTests
{
    private static GranularEditSession New() => new(@"C:\v\clip.mp4", 0, 60000, new GranularHistoryParking());

    private static OutputTimeline Ruler(GranularEditSession s, bool withMemes = false) =>
        OutputTimeline.Create(60000, s.Segments, s.BaseSpeed, 0,
            withMemes ? MemePlacement.ToInsertions(s.Memes) : null, s.CutsForTimeline());

    [Fact]
    public void MEMESNAP_02_AddSnapsToTheNearestBlockEdge_NotAlwaysForward()
    {
        var s = New();
        s.Segments.Add(new SpeedSegment(10000, 40000, 0.5));
        Assert.True(s.ResolveNewMemeAnchor(Ruler(s), 11.0, MemePresentationMode.InlineFullScreen, out double near).Ok);
        Assert.Equal(10.0, near, 3);
        Assert.True(s.ResolveNewMemeAnchor(Ruler(s), 38.0, MemePresentationMode.InlineFullScreen, out double far).Ok);
        Assert.Equal(40.0, far, 3);
    }

    [Fact]
    public void MEMESEP_02_CornerMemesNeverBlockAFullScreenMeme_AndSpacingIsInOutputSeconds()
    {
        var s = New();
        s.AddMeme("corner.png", 5.0, 4.0, MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopLeft, MemeOverlaySize.Small, true);
        Assert.True(s.ResolveNewMemeAnchor(Ruler(s, true), 5.0, MemePresentationMode.InlineFullScreen, out _).Ok);

        s.BaseSpeed = 4.0;
        s.AddMeme("a.mp4", 20.0, 2.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        Assert.False(s.MemeSeparationIsSafe(20.12, null, out _, Ruler(s)));
        Assert.True(s.MemeSeparationIsSafe(20.12, null, out _));
    }

    [Fact]
    public void MEMECUT_02_DeletePartsRemovesTheMemesInsideIt_InTheSameUndoStep()
    {
        var s = New();
        s.AddMeme("in.mp4", 12.0, 2.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        s.AddMeme("out.mp4", 30.0, 2.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        s.DeleteRange(10000, 15000, 60000);
        Assert.Equal(new[] { "in.mp4" }, s.LastCutDroppedMemes);
        Assert.Single(s.Memes);
        s.Undo();
        Assert.Equal(2, s.Memes.Count);
    }

    [Fact]
    public void MEMESNAP_03_ABlockOverAFullScreenMemePushesItToTheNearestEdge_CornersStay()
    {
        var s = New();
        s.AddMeme("full.mp4", 21.0, 2.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        s.AddMeme("corner.png", 25.0, 4.0, MemePresentationMode.CornerOverlay, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        s.Segments.Add(new SpeedSegment(20000, 30000, 2.0));
        Assert.Equal(new[] { "full.mp4" }, s.ResnapMemesOffBlocks());
        Assert.Equal(20.0, s.Memes.Single(m => !m.IsCornerOverlay).AtSourceSecRelative, 3);
        Assert.Equal(25.0, s.Memes.Single(m => m.IsCornerOverlay).AtSourceSecRelative, 3);
        Assert.Empty(s.ResnapMemesOffBlocks());
    }

    [Fact]
    public void MEMEDRAG_03_ACornerMemeMovesFreely_OnlyPushedOutOfDeletedFootage()
    {
        var s = New();
        s.Segments.Add(new SpeedSegment(10000, 20000, 0.5));
        s.Cuts.Add(new CutRange(30000, 35000));
        var c = s.AddMeme("corner.png", 5.0, 4.0, MemePresentationMode.CornerOverlay, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        Assert.True(s.MoveMemeTo(c.Id, 15.0, Ruler(s), "meme-drag", out _, out _));
        Assert.Equal(15.0, s.Memes[0].AtSourceSecRelative, 3);
        Assert.True(s.MoveMemeTo(c.Id, 32.0, Ruler(s), "meme-drag", out _, out _));
        Assert.Equal(35.0, s.Memes[0].AtSourceSecRelative, 3);
    }

    [Fact]
    public void MEMEDRAG_02_ABlockedFullScreenDragSaysWhy()
    {
        var s = New();
        var a = s.AddMeme("a.mp4", 10.0, 2.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        s.AddMeme("b.mp4", 20.0, 2.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        Assert.False(s.MoveMemeTo(a.Id, 20.01, Ruler(s), "meme-drag", out _, out string? why));
        Assert.Contains("b.mp4", why);
    }

    [Fact]
    public void MEMERULER_01_TheExcludingRulerRunsAtTheBaseSpeed()
    {
        var s = New();
        s.BaseSpeed = 2.0;
        Assert.Equal(30.0, s.TimelineExcludingMeme("none", 60000).TotalOutputSeconds, 3);
    }

    [Fact]
    public void MEMESNAP_02_ModeChangeReportsHowFarItMoved()
    {
        var s = New();
        var c = s.AddMeme("m.mp4", 12.0, 2.0, MemePresentationMode.CornerOverlay, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        s.Segments.Add(new SpeedSegment(10000, 30000, 0.5));
        Assert.True(s.UpdateMeme(c.Id, c with { Mode = MemePresentationMode.InlineFullScreen }, 60000, out _, out var after, out double moved).Ok);
        Assert.Equal(10.0, after!.AtSourceSecRelative, 3);
        Assert.Equal(-2.0, moved, 3);
    }

    [Fact]
    public void Json_NewKeysRoundTrip_AndOldFilesReadAsBefore()
    {
        var m = new MemePlacement("m.mp4", 3, 2.5, "meme0", MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopLeft,
            MemeOverlaySize.Large, false, 1.25, 0.3, 0.7, MemeOverlayAnimation.Slide, MemeTransition.Zoom);
        var o = new JsonObject();
        MemePresentationJson.Write(o, m);
        var back = MemePresentationJson.Apply(JsonNode.Parse(o.ToJsonString()) as JsonObject, new MemePlacement("m.mp4", 3, 2.5, "meme0"));
        Assert.Equal(m, back);

        var old = MemePresentationJson.Apply(new JsonObject { ["mode"] = "inline" }, new MemePlacement("m.mp4", 3, 2.5, "meme0"));
        Assert.Equal((0.0, -1.0, -1.0, MemeOverlayAnimation.None, MemeTransition.Cut),
            (old.ClipInSec, old.PosX, old.PosY, old.Animation, old.Transition));
    }

    [Fact]
    public void MEMEPOS_01_ADraggedPositionStaysInsideTheFrame_AtEverySize()
    {
        foreach (var size in Enum.GetValues<MemeOverlaySize>())
        {
            var (x, y, w, h) = MemeOverlayLayout.Place(1080, 1920, 640, 360, MemeOverlayCorner.TopLeft, size, 1.0, 1.0);
            Assert.Equal(1080, x + w);
            Assert.Equal(1920, y + h);
            var (x0, y0, _, _) = MemeOverlayLayout.Place(1080, 1920, 640, 360, MemeOverlayCorner.BottomRight, size, 0.0, 0.0);
            Assert.Equal((0, 0), (x0, y0));
        }
        Assert.Equal(("(W-w)*0.25", "(H-h)*0.5"), MemeOverlayLayout.OverlayPosition(1080, 1920, MemeOverlayCorner.TopLeft, 0.25, 0.5));
        Assert.Equal(MemeOverlayCorner.BottomLeft, MemeOverlayLayout.NearestCorner(0.2, 0.9));
    }

    [Fact]
    public void MEMEDUCK_01_TheGameDipsUnderACornerMemeWithSound_AndNotWithout()
    {
        var withSound = CornerMemeOverlayGraph.Build("[v]", "[a]",
            new[] { new CornerMemeInput(3, false, true, 1.0, 3.0, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true) },
            1080, 1920, "60", "t_");
        Assert.Contains(withSound.Filters, f => f.StartsWith("[a]volume=volume='if(between(t,1.0000,3.0000),0.5,1)':eval=frame", StringComparison.Ordinal));

        var silent = CornerMemeOverlayGraph.Build("[v]", "[a]",
            new[] { new CornerMemeInput(3, false, true, 1.0, 3.0, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, false) },
            1080, 1920, "60", "t_");
        Assert.DoesNotContain(silent.Filters, f => f.Contains("volume=volume", StringComparison.Ordinal));
        Assert.Equal("[a]", silent.AudioLabel);
    }

    [Fact]
    public void MEMEANIM_01_FadeIsAnAlphaFade_SlideMovesTheOverlayX()
    {
        var fade = CornerMemeOverlayGraph.Build("[v]", "[a]",
            new[] { new CornerMemeInput(3, true, false, 2.0, 6.0, MemeOverlayCorner.TopLeft, MemeOverlaySize.Small, false, 0, -1, -1, MemeOverlayAnimation.Fade) },
            1920, 1080, "60", "t_");
        Assert.Contains(fade.Filters, f => f.Contains("fade=t=in:st=0:d=0.25:alpha=1", StringComparison.Ordinal));

        var slide = CornerMemeOverlayGraph.Build("[v]", "[a]",
            new[] { new CornerMemeInput(3, true, false, 2.0, 6.0, MemeOverlayCorner.BottomRight, MemeOverlaySize.Small, false, 0, -1, -1, MemeOverlayAnimation.Slide) },
            1920, 1080, "60", "t_");
        Assert.Contains(slide.Filters, f => f.Contains("overlay=x='if(lt(t,2+0.25)", StringComparison.Ordinal));
        Assert.Equal(1.0, MemeTransitions.CornerSlideOffsetAt(MemeOverlayAnimation.Slide, 0, 4), 3);
        Assert.Equal(0.0, MemeTransitions.CornerSlideOffsetAt(MemeOverlayAnimation.Slide, 2, 4), 3);
        Assert.Equal(0.0, MemeTransitions.CornerOpacityAt(MemeOverlayAnimation.Fade, 0, 4), 3);
        Assert.Equal(1.0, MemeTransitions.CornerOpacityAt(MemeOverlayAnimation.None, 0, 4), 3);
    }

    [Fact]
    public void MEMETRANS_01_ZoomNeverUsesZoompan_AndKeepsTheCanvasSize()
    {
        string zoom = MemeTransitions.VideoFilters(MemeTransition.Zoom, 3, "1080:1920");
        Assert.DoesNotContain("zoompan", zoom);
        Assert.Contains("eval=frame", zoom);
        Assert.Contains("crop=1080:1920", zoom);
        Assert.Equal("", MemeTransitions.VideoFilters(MemeTransition.Cut, 3, "1080:1920"));
        Assert.Contains("afade=t=in", MemeTransitions.AudioFilters(MemeTransition.Fade, 3));
        Assert.Equal("", MemeTransitions.AudioFilters(MemeTransition.Zoom, 3));
    }

    [Fact]
    public void MEMETRIM_01_OnlyTheChosenPartOfAVideoIsRead()
    {
        var args = CornerMemeOverlayGraph.InputArgs("m.mp4", false, 2.5, "60", 1.5).ToArray();
        Assert.Equal(new[] { "-ss", "1.500", "-t", "2.500", "-i", "m.mp4" }, args);
        var still = CornerMemeOverlayGraph.InputArgs("m.png", true, 6, "60").ToArray();
        Assert.Equal(new[] { "-loop", "1", "-framerate", "60", "-t", "6.000", "-i", "m.png" }, still);
    }

    [Fact]
    public void MERGEMEME_02_AFullScreenMemeInAClipsFadeMovesWhereTheMergePutsIt_ACornerIsToldWhereItsClipEnds()
    {
        var edl = new MergeEdl
        {
            Clips = new[]
            {
                new EdlClip { Path = @"C:\c\a.mp4", DurationUs = 10_000_000 },
                new EdlClip { Path = @"C:\c\b.mp4", DurationUs = 10_000_000, IntroCutUs = 100_000, Timing = new ExportTiming(60, 1, 6, 30, 60) },
            },
        };
        var src = MergeEditorSource.Build(CompositeTimeline.Build(edl));

        double at = src.ResolveMemeAt(10.3, corner: false, out _, out string? note);
        Assert.Equal(10.0, at, 3);
        Assert.NotNull(note);

        double end = src.ResolveMemeAt(19.5, corner: false, out _, out note);
        Assert.Equal(18.9, end, 3);
        Assert.NotNull(note);

        Assert.Equal(15.0, src.ResolveMemeAt(15.0, corner: false, out _, out note), 3);
        Assert.Null(note);

        Assert.Equal(19.5, src.ResolveMemeAt(19.5, corner: true, out double clipEnd, out _), 3);
        Assert.Equal(19.9, clipEnd, 3);
    }
}
