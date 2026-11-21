using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.LocalAi;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class LocalAiTests
{
    private static string Work(string name)
    {
        string dir = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "LocalAi", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MaskToBox_UsesPositiveLogitsAndScalesToSource()
    {
        var mask = Enumerable.Repeat(-5f, 16 * 16).ToArray();
        for (int y = 4; y <= 7; y++)
            for (int x = 2; x <= 5; x++)
                mask[y * 16 + x] = 3f;
        var box = LocalTrackingMath.MaskToBox(mask, 16, 1600, 800);
        Assert.NotNull(box);
        Assert.Equal(200, box!.Value.X1, 3);
        Assert.Equal(600, box.Value.X2, 3);
        Assert.Equal(200, box.Value.Y1, 3);
        Assert.Equal(400, box.Value.Y2, 3);
        Assert.Null(LocalTrackingMath.MaskToBox(Enumerable.Repeat(-1f, 256).ToArray(), 16, 1600, 800));
    }

    [Fact]
    public void BoxPrompt_IsTwoCornersAndAPositiveCentreInModelSpace()
    {
        var (coords, labels) = LocalTrackingMath.BoxPrompt(new PixelBox(960, 540, 1920, 1080), 1440, 810, 1920, 1080, 1024);
        Assert.Equal(new[] { 512f, 512f, 1024f, 1024f, 768f, 768f }, coords);
        Assert.Equal(new[] { 2f, 3f, 1f }, labels);
    }

    [Fact]
    public void EncoderInput_IsImageNetNormalisedChw()
    {
        var rgb = new byte[2 * 2 * 3];
        for (int p = 0; p < 4; p++) { rgb[p * 3] = 255; rgb[p * 3 + 1] = 0; rgb[p * 3 + 2] = 128; }
        var chw = new float[12];
        LocalTrackingMath.FillEncoderInput(rgb, 2, chw);
        Assert.Equal((1f - 0.485f) / 0.229f, chw[0], 4);
        Assert.Equal((0f - 0.456f) / 0.224f, chw[4], 4);
        Assert.Equal((128f / 255f - 0.406f) / 0.225f, chw[8], 4);
    }

    [Fact]
    public void Consistency_RejectsLowScoreSizeJumpsAndLeaps()
    {
        var prev = new PixelBox(100, 100, 200, 300);
        Assert.True(LocalTrackingMath.IsConsistent(prev, new PixelBox(110, 105, 210, 305), 0.9));
        Assert.False(LocalTrackingMath.IsConsistent(prev, new PixelBox(110, 105, 210, 305), 0.3));
        Assert.False(LocalTrackingMath.IsConsistent(prev, new PixelBox(0, 0, 400, 800), 0.9));
        Assert.False(LocalTrackingMath.IsConsistent(prev, new PixelBox(80, 80, 230, 330), 0.9));
        Assert.False(LocalTrackingMath.IsConsistent(new PixelBox(100, 100, 200, 300), new PixelBox(100, 100, 210, 310), 0.9, referenceArea: 4000));
        Assert.False(LocalTrackingMath.IsConsistent(prev, new PixelBox(1500, 100, 1600, 300), 0.9));
        Assert.True(LocalTrackingMath.IsConsistent(default, new PixelBox(1, 1, 5, 5), 0.9));
    }

    [Fact]
    public void NextPrompt_FollowsMotionWidensAndStaysInFrame()
    {
        var p = LocalTrackingMath.NextPrompt(new PixelBox(100, 100, 200, 200), 10, 0, 1920, 1080);
        Assert.True(p.X1 < 110 && p.X2 > 210);
        Assert.True(p.Cx > 150);
        var edge = LocalTrackingMath.NextPrompt(new PixelBox(1850, 1000, 1920, 1080), 30, 30, 1920, 1080);
        Assert.True(edge.X2 <= 1920 && edge.Y2 <= 1080);
    }

    [Fact]
    public void Waypoints_AreHalfSecondMediansWithVisibilityMajorityAndEndAnchor()
    {
        var frames = new List<TrackedFrame>();
        for (int i = 0; i <= 20; i++)
        {
            double t = i / 10.0;
            bool vis = t < 1.25;
            var box = new PixelBox(900, 500, 1020, 760);
            if (i == 3) box = new PixelBox(0, 0, 50, 50);
            frames.Add(new TrackedFrame(t, box, vis, 0.9));
        }
        var wps = LocalTrackingMath.ToWaypoints(frames, 2.0, 1920, 1080);
        Assert.Equal(new[] { 0.0, 0.5, 1.0, 1.5, 2.0 }, wps.Select(w => w.Time).ToArray());
        Assert.True(wps[0].Visible && wps[1].Visible && wps[2].Visible);
        Assert.False(wps[3].Visible);
        Assert.False(wps[4].Visible);
        Assert.Equal(500, wps[1].Cx);
        Assert.Equal(583, wps[1].Cy);
    }

    [Fact]
    public void FromGrid_MapsDiscoveryBoxToPixels()
    {
        var b = LocalTrackingMath.FromGrid(100, 250, 500, 750, 1920, 1080);
        Assert.Equal(480, b.X1, 3);
        Assert.Equal(108, b.Y1, 3);
        Assert.Equal(1440, b.X2, 3);
        Assert.Equal(540, b.Y2, 3);
    }

    [Fact]
    public void Candidates_KeepEveryNonEmptyMaskWithItsScore()
    {
        int plane = 8 * 8;
        var masks = new float[3 * plane];
        Array.Fill(masks, -1f);
        masks[0 * plane + 9] = 1f;
        masks[2 * plane + 18] = 1f;
        var list = Sam2BoxTracker.Candidates(masks, new long[] { 1, 3, 8, 8 }, new[] { 0.6f, 0.99f, 0.8f }, 800, 800);
        Assert.Equal(2, list.Count);
        Assert.Equal(0.8, list[1].Score, 3);
        Assert.Equal(200, list[1].Box.X1, 3);
    }

    [Fact]
    public void Choose_PrefersTheMaskWhereTheTargetIsExpectedOverALargerConfidentBackgroundMask()
    {
        var last = new PixelBox(600, 270, 680, 410);
        var predicted = new PixelBox(626, 270, 706, 410);
        var candidates = new[]
        {
            new MaskCandidate(new PixelBox(560, 200, 760, 480), 0.95),
            new MaskCandidate(new PixelBox(628, 268, 704, 412), 0.80),
            new MaskCandidate(new PixelBox(5, 30, 1240, 630), 0.99),
        };
        var pick = LocalTrackingMath.Choose(candidates, predicted, last, last.Area);
        Assert.NotNull(pick);
        Assert.Equal(628, pick!.Value.Box.X1, 3);
        Assert.Null(LocalTrackingMath.Choose(new[] { new MaskCandidate(new PixelBox(1000, 600, 1080, 700), 0.99) }, predicted, last, last.Area));
        Assert.True(LocalTrackingMath.Iou(last, last) > 0.999);
        Assert.Equal(0, LocalTrackingMath.Iou(last, new PixelBox(0, 0, 10, 10)));
    }

    [Fact]
    public void Choose_WhileTheMotionIsUnknownTakesTheMaskOfTheSameSizeNotTheOneStretchedOverTheOldPlace()
    {
        var last = new PixelBox(625, 267, 700, 408);
        var stretched = new MaskCandidate(new PixelBox(635, 262, 725, 402), 0.98);
        var moved = new MaskCandidate(new PixelBox(650, 262, 725, 402), 0.98);
        var candidates = new[] { stretched, new MaskCandidate(new PixelBox(635, 262, 685, 402), 0.74), moved };

        Assert.Equal(stretched.Box, LocalTrackingMath.Choose(candidates, last, last, last.Area)!.Value.Box);
        Assert.Equal(moved.Box, LocalTrackingMath.Choose(candidates, last, last, last.Area, motionUnknown: true)!.Value.Box);
        Assert.Null(LocalTrackingMath.Choose(new[] { new MaskCandidate(new PixelBox(1000, 600, 1075, 741), 0.99) }, last, last, last.Area, motionUnknown: true));

        Assert.Equal(1.0, LocalTrackingMath.SameSize(last, new PixelBox(0, 0, 75, 141)), 6);
        Assert.Equal(0.5, LocalTrackingMath.SameSize(new PixelBox(0, 0, 10, 10), new PixelBox(50, 50, 70, 60)), 6);
        Assert.Equal(0, LocalTrackingMath.SameSize(last, default));
    }

    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var f = new byte[w * h * 3];
        for (int p = 0; p < w * h; p++) { f[p * 3] = r; f[p * 3 + 1] = g; f[p * 3 + 2] = b; }
        return f;
    }

    [Fact]
    public void SceneCut_IgnoresGradualChangeAndFindsHardCut()
    {
        var d = new SceneCutDetector();
        for (int i = 0; i < 20; i++)
            Assert.False(d.Push(Solid(32, 32, (byte)(60 + i), (byte)(60 + i), (byte)(60 + i)), 32, 32));
        Assert.True(d.Push(Solid(32, 32, 240, 240, 240), 32, 32));
        Assert.False(d.Push(Solid(32, 32, 240, 240, 240), 32, 32));
    }

    [Fact]
    public void OverlayEvents_FindAPopUpInTheStillAreaButNotACutOrTheLivePicture()
    {
        const int w = 64, h = 36;
        var rng = new Random(7);
        var frames = new List<byte[]>();
        for (int i = 0; i < 60; i++)
        {
            var f = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool live = x >= 16 && x < 48;
                    f[y * w + x] = live ? (byte)rng.Next(0, 256) : (byte)40;
                }
            if (i >= 30 && i < 34)
                for (int y = 2; y < 6; y++)
                    for (int x = 2; x < 12; x++)
                        f[y * w + x] = 230;
            if (i == 50) Array.Fill(f, (byte)250);
            frames.Add(f);
        }
        var events = HudOverlayEventDetector.Detect(frames, 2.0);
        Assert.Contains(events, e => Math.Abs(e.TimeSec - 15.0) < 0.01);
        Assert.DoesNotContain(events, e => e.TimeSec >= 24.5 && e.TimeSec <= 25.5);
    }

    [Fact]
    public void KillPlanner_WithoutPopUpsKeepsTheLoudestByBudget()
    {
        var plan = KillMomentPlanner.Build(new[] { 30.0, 10.0, 20.0, 5.0 }, null, 3);
        Assert.False(plan.GateUsed);
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, plan.Moments);
    }

    [Fact]
    public void KillPlanner_KeepsConfirmedDropsUnconfirmedAndAddsSilentPopUps()
    {
        var events = new[] { new OverlayEvent(11.0, 0.02), new OverlayEvent(60.0, 0.05) };
        var plan = KillMomentPlanner.Build(new[] { 10.0, 40.0 }, events, 10);
        Assert.True(plan.GateUsed);
        Assert.Equal(1, plan.AudioConfirmed);
        Assert.Equal(1, plan.AudioDropped);
        Assert.Equal(1, plan.SilentAdded);
        Assert.Equal(new[] { 10.0, 59.5 }, plan.Moments);
    }

    [Fact]
    public void Appearance_TellsDifferentColoursApartAndBlendsSlowly()
    {
        const int size = 64;
        var rgb = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 3;
                if (x < 32) { rgb[i] = 220; rgb[i + 1] = 30; rgb[i + 2] = 30; }
                else { rgb[i] = 30; rgb[i + 1] = 30; rgb[i + 2] = 220; }
            }
        var red = LocalTrackingMath.Appearance(rgb, size, new PixelBox(0, 0, 320, 640), 640, 640);
        var red2 = LocalTrackingMath.Appearance(rgb, size, new PixelBox(20, 100, 300, 600), 640, 640);
        var blue = LocalTrackingMath.Appearance(rgb, size, new PixelBox(330, 0, 640, 640), 640, 640);
        Assert.InRange(red.Sum(), 0.999f, 1.001f);
        Assert.True(LocalTrackingMath.Similarity(red, red2) > 0.99);
        Assert.True(LocalTrackingMath.Similarity(red, blue) < LocalTrackingMath.MinAppearance);
        var look = (float[])red.Clone();
        LocalTrackingMath.Blend(look, blue, LocalTrackingMath.AppearanceRate);
        Assert.True(LocalTrackingMath.Similarity(look, red) > 0.9);
    }

    [Fact]
    public void Choose_RefusesADifferentLookingBodyAndTheOwnAvatar()
    {
        var last = new PixelBox(600, 300, 680, 460);
        var predicted = new PixelBox(610, 300, 690, 460);
        var same = new MaskCandidate(new PixelBox(612, 302, 690, 462), 0.80, 0);
        var other = new MaskCandidate(new PixelBox(608, 298, 688, 458), 0.95, 1);
        var pick = LocalTrackingMath.Choose(new[] { same, other }, predicted, last, last.Area,
            appearance: b => b == other.Box ? 0.1 : 0.9);
        Assert.Equal(same.Box, pick!.Value.Box);
        Assert.Equal(0, pick.Value.Index);

        var avatar = new MaskCandidate(new PixelBox(500, 420, 780, 720), 0.99, 2);
        Assert.True(LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.Fortnite, avatar.Box, 1280, 720));
        Assert.Null(LocalTrackingMath.Choose(new[] { avatar }, avatar.Box, default, 0,
            excluded: b => LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.ThirdPerson, b, 1280, 720)));
    }

    [Fact]
    public void OwnBody_FollowsTheProfile()
    {
        var avatar = new PixelBox(500, 420, 780, 720);
        var hands = new PixelBox(700, 450, 1200, 720);
        var farEnemy = new PixelBox(900, 200, 940, 280);
        Assert.True(LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.ThirdPerson, avatar, 1280, 720));
        Assert.False(LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.Generic, avatar, 1280, 720));
        Assert.False(LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.Fortnite, farEnemy, 1280, 720));
        Assert.True(LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.FirstPerson, hands, 1280, 720));
        Assert.False(LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.FirstPerson, farEnemy, 1280, 720));
    }

    [Fact]
    public void ShiftMask_MovesTheRememberedMaskWithTheTarget()
    {
        const int m = 8;
        var src = new float[m * m];
        Array.Fill(src, -5f);
        src[2 * m + 2] = 7f;
        var dst = new float[m * m];
        LocalTrackingMath.ShiftMask(src, dst, m, 3, 1, -10f);
        Assert.Equal(7f, dst[3 * m + 5]);
        Assert.Equal(-10f, dst[0]);
        Assert.Equal(-5f, dst[7 * m + 7]);
        Assert.Equal(1, dst.Count(v => v == 7f));
    }

    [Fact]
    public void LocalDiscovery_FindsMovingTargetsAndSnapsPointAndParsesYolo()
    {
        const int w = 320, h = 180;
        byte[] prev = new byte[w * h * 3];
        byte[] curr = new byte[w * h * 3];
        byte[] next = new byte[w * h * 3];
        for (int y = 40; y < 100; y++)
        {
            for (int x = 60; x < 100; x++)
            {
                int i = (y * w + x) * 3;
                curr[i] = 240; curr[i + 1] = 40; curr[i + 2] = 40;
            }
            for (int x = 80; x < 120; x++)
            {
                int i = (y * w + x) * 3;
                next[i] = 240; next[i + 1] = 40; next[i + 2] = 40;
            }
        }
        var found = LocalSubjectDiscovery.DiscoverOnFrames(
            new[] { curr, curr, curr }, w, h,
            new[] { (prev, next), (prev, next), (prev, next) },
            AiGameProfile.ThirdPerson);
        Assert.NotEmpty(found);
        Assert.Contains(found, c => c.Label.Contains("Motion region") && !c.IsDetectorMatch);

        var snapped = LocalSubjectDiscovery.SnapAtPoint(curr, w, h, 2, 250, 380, 5, AiGameProfile.ThirdPerson);
        Assert.Equal(5, snapped.Id);
        Assert.Equal(2, snapped.FrameIndex);
        Assert.True(snapped.Xmax > snapped.Xmin && snapped.Ymax > snapped.Ymin);

        float[] yoloData = new float[84 * 2];
        yoloData[0] = 160f;
        yoloData[2] = 160f;
        yoloData[4] = 80f;
        yoloData[6] = 180f;
        yoloData[8] = 0.88f;
        var dets = YoloOnnxDetector.ParseYoloOutput(yoloData, new long[] { 1, 84, 2 }, 640, 1280, 720, AiGameProfile.Fortnite);
        Assert.Single(dets);
        Assert.Equal("Character", dets[0].Category);
    }

    [Fact]
    public void PackCheck_ReportsEveryState()
    {
        string dir = Work("pack");
        Assert.Equal(LocalAiPackState.NotPublished, LocalAiPack.Check(dir, Array.Empty<LocalAiPackFile>()).State);
        var bytes = new byte[] { 1, 2, 3, 4 };
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var files = new[] { new LocalAiPackFile("a.bin", "https://example.invalid/a.bin", 4, hash), new LocalAiPackFile("b.bin", "https://example.invalid/b.bin", 4, hash) };
        Assert.Equal(LocalAiPackState.NotInstalled, LocalAiPack.Check(Path.Combine(dir, "missing"), files).State);
        Assert.Equal(LocalAiPackState.NotInstalled, LocalAiPack.Check(dir, files).State);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), bytes);
        Assert.Equal(LocalAiPackState.Damaged, LocalAiPack.Check(dir, files).State);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), bytes);
        var ok = LocalAiPack.Check(dir, files);
        Assert.True(ok.IsReady);
        Assert.True(ok.Verified);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[] { 9, 9, 9, 9 });
        File.SetLastWriteTimeUtc(Path.Combine(dir, "b.bin"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(LocalAiPackState.Damaged, LocalAiPack.Check(dir, files).State);
    }

    [Fact]
    public void CheckDetector_ReportsPackUnavailableAbsentDamagedAndInstalled()
    {
        string dir = Work("detector");
        var missingStatus = new LocalAiPackStatus(LocalAiPackState.NotInstalled, dir, "Not installed.", false);
        Assert.Equal(LocalDetectorState.PackUnavailable, LocalAiPack.CheckDetector(dir, Array.Empty<LocalAiPackFile>(), missingStatus).State);

        var readyStatus = new LocalAiPackStatus(LocalAiPackState.Ready, dir, "Installed.", true);
        Assert.Equal(LocalDetectorState.Absent, LocalAiPack.CheckDetector(dir, Array.Empty<LocalAiPackFile>(), readyStatus).State);

        string yoloPath = Path.Combine(dir, LocalAiPackManifest.DetectorFile);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(yoloPath, new byte[128]);
        Assert.Equal(LocalDetectorState.Damaged, LocalAiPack.CheckDetector(dir, Array.Empty<LocalAiPackFile>(), readyStatus).State);

        byte[] validPayload = new byte[2048];
        new Random(42).NextBytes(validPayload);
        File.WriteAllBytes(yoloPath, validPayload);
        Assert.Equal(LocalDetectorState.Installed, LocalAiPack.CheckDetector(dir, Array.Empty<LocalAiPackFile>(), readyStatus).State);

        string hash = Convert.ToHexString(SHA256.HashData(validPayload));
        var pinned = new[] { new LocalAiPackFile(LocalAiPackManifest.DetectorFile, "https://example.invalid/y.onnx", validPayload.Length, hash) };
        Assert.Equal(LocalDetectorState.Installed, LocalAiPack.CheckDetector(dir, pinned, readyStatus).State);

        File.WriteAllBytes(yoloPath, new byte[2048]);
        File.SetLastWriteTimeUtc(yoloPath, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(LocalDetectorState.Damaged, LocalAiPack.CheckDetector(dir, pinned, readyStatus).State);

        Assert.False(LocalAiPackFiles.Released);
    }

    [MediaFact]
    public async Task BuiltInTracker_FollowsTargetDetectsAmbiguityExcludesOwnBodyAndStopsAtHardCut()
    {
        string cutFilter =
            "color=c=0x2a3a2a:s=640x360:r=20:d=2.0,noise=alls=20:allf=t[bg1];" +
            "color=c=0xd02020:s=48x48:r=20:d=2.0[top];color=c=0x2060e0:s=48x48:r=20:d=2.0[bot];" +
            "[top][bot]vstack=inputs=2[char];" +
            "color=c=0x4040a0:s=140x160:r=20:d=2.0[me];" +
            "[bg1][me]overlay=x=250:y=200[s1];" +
            "[s1][char]overlay=x='80+t*180':y='110'[a];" +
            "color=c=0xf0ead8:s=640x360:r=20:d=1.5,noise=alls=10:allf=t[b];" +
            "[a][b]concat=n=2:v=1:a=0[v]";
        string cutVid = await LocalTrackerEndToEndTests.MakeVideoAsync("builtin_cut.mp4", cutFilter);
        var cutRes = await LocalSubjectTracker.TrackBuiltInAsync(
            new LocalTrackingRequest(MediaFactAttribute.Ffmpeg!, cutVid, 0, 3.5, 640, 360,
                new PixelBox(72, 102, 136, 214), 0.1, AiGameProfile.Fortnite),
            null, CancellationToken.None);
        Assert.False(cutRes.UsedNeuralPack);
        Assert.NotNull(cutRes.ForwardCutSec);
        Assert.InRange(cutRes.ForwardCutSec!.Value, 1.7, 2.3);
        Assert.All(cutRes.Frames.Where(f => f.TimeSec >= 2.4), f => Assert.False(f.Visible));
        Assert.True(cutRes.VisibleFrames >= 8);
        foreach (var f in cutRes.Frames.Where(f => f.Visible && f.TimeSec < 1.8))
            Assert.False(LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.Fortnite, f.Box, 640, 360));

        string occFilter =
            "color=c=0x2a3a2a:s=640x360:r=20:d=3.0,noise=alls=25:allf=t[bg];" +
            "color=c=0xe02525:s=52x96:r=20:d=3.0[target];" +
            "[bg][target]overlay=x='200+t*20':y='120':enable='lt(t,1.2)'[v]";
        string occVid = await LocalTrackerEndToEndTests.MakeVideoAsync("builtin_occ.mp4", occFilter);
        var occRes = await LocalSubjectTracker.TrackBuiltInAsync(
            new LocalTrackingRequest(MediaFactAttribute.Ffmpeg!, occVid, 0, 3.0, 640, 360,
                new PixelBox(194, 112, 260, 224), 0.2, AiGameProfile.Generic),
            null, CancellationToken.None);
        Assert.False(occRes.UsedNeuralPack);
        int afterOcc = occRes.Frames.Count(f => f.TimeSec >= 1.5);
        int hiddenAfterOcc = occRes.Frames.Count(f => f.TimeSec >= 1.5 && !f.Visible);
        Assert.True(hiddenAfterOcc >= afterOcc * 0.7,
            $"Expected >=70% occluded frames hidden after 1.5s, got {hiddenAfterOcc}/{afterOcc}: " +
            string.Join(", ", occRes.Frames.Select(f => $"{f.TimeSec:0.0}:{(f.Visible ? 1 : 0)}:{f.Score:0.00}")));

        string twinFilter =
            "color=c=0x202820:s=640x360:r=20:d=2.4,noise=alls=10:allf=t[bg];" +
            "color=c=0xe03030:s=48x96:r=20:d=2.4[t0];" +
            "color=c=0xe03030:s=48x96:r=20:d=2.4[t1];" +
            "color=c=0xe03030:s=48x96:r=20:d=2.4[t2];" +
            "[bg][t0]overlay=x='296':y='120':enable='lt(t,0.25)'[s0];" +
            "[s0][t1]overlay=x='260':y='120':enable='gte(t,0.40)'[s1];" +
            "[s1][t2]overlay=x='332':y='120':enable='gte(t,0.40)'[v]";
        string twinVid = await LocalTrackerEndToEndTests.MakeVideoAsync("builtin_ambig.mp4", twinFilter);
        var twinRes = await LocalSubjectTracker.TrackBuiltInAsync(
            new LocalTrackingRequest(MediaFactAttribute.Ffmpeg!, twinVid, 0, 2.4, 640, 360,
                new PixelBox(296, 120, 344, 216), 0.0, AiGameProfile.Generic),
            null, CancellationToken.None);
        Assert.False(twinRes.UsedNeuralPack);
        Assert.True(twinRes.AmbiguousFrames > 0);
    }

    [Fact]
    public void Manifest_PinsHttpsReleaseFilesWithFullHashes()
    {
        Assert.StartsWith("https://github.com/alonreich/Free_Video_Studio/releases/download/", LocalAiPackManifest.ReleaseBaseUrl);
        foreach (var f in LocalAiPackManifest.Files)
        {
            Assert.StartsWith(LocalAiPackManifest.ReleaseBaseUrl, f.Url);
            Assert.Equal(64, f.Sha256.Length);
            Assert.True(f.Length > 0);
        }
        if (LocalAiPackManifest.IsPublished)
            foreach (string name in LocalAiPackManifest.RequiredNames)
                Assert.Contains(LocalAiPackManifest.Files, f => f.Name == name);
    }

    [Fact]
    public void UnreleasedPack_IsNotPublishedEvenWithPins()
    {
        Assert.Equal(LocalAiPackFiles.Released && LocalAiPackFiles.All.Length > 0, LocalAiPackManifest.IsPublished);
        var status = LocalAiPack.Check(Work("unreleased"), LocalAiPackManifest.IsPublished ? LocalAiPackManifest.Files : Array.Empty<LocalAiPackFile>());
        Assert.Equal(LocalAiPackManifest.IsPublished ? LocalAiPackState.NotInstalled : LocalAiPackState.NotPublished, status.State);
    }

    [Fact]
    public void FrameReaderArguments_ReadAFixedRateFixedSizeRawStream()
    {
        var args = RawVideoFrameReader.BuildArguments("in.mp4", 2.5, 4, 10, 1024, 1024);
        string joined = string.Join(" ", args);
        Assert.Contains("-ss 2.500", joined);
        Assert.Contains("-t 4.000", joined);
        Assert.Contains("fps=fps=10:round=near,scale=1024:1024", joined);
        Assert.Contains("-f rawvideo -pix_fmt rgb24 -", joined);
    }

    [MediaFact]
    public async Task FrameReader_StreamsTheExpectedFramesFromARealVideo()
    {
        string dir = Work("reader");
        string video = Path.Combine(dir, "src.mp4");
        var psi = new System.Diagnostics.ProcessStartInfo(MediaFactAttribute.Ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (string a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=30", "-t", "3", "-pix_fmt", "yuv420p", video })
            psi.ArgumentList.Add(a);
        using (var p = System.Diagnostics.Process.Start(psi)!) { await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode); }

        int frames = 0;
        int count = await RawVideoFrameReader.ReadAsync(MediaFactAttribute.Ffmpeg!, video, 1.0, 1.5, 10, 64, 64,
            (_, rgb) => { Assert.Equal(64 * 64 * 3, rgb.Length); frames++; return true; }, CancellationToken.None);
        Assert.InRange(count, 14, 16);
        Assert.Equal(count, frames);

        var events = await HudOverlayEventDetector.ScanAsync(MediaFactAttribute.Ffmpeg!, video, 3, CancellationToken.None);
        Assert.NotNull(events);
    }
}

internal static class OrtTestFolder
{
    public static readonly string? Folder = Environment.GetEnvironmentVariable("FVS_TEST_ORT_DIR") is { Length: > 0 } d
        && File.Exists(Path.Combine(d, "onnxruntime.dll")) && File.Exists(Path.Combine(d, "tiny.onnx")) ? d : null;
}

public sealed class OrtFactAttribute : FactAttribute
{
    public const string Category = "LocalAiRuntime";

    public OrtFactAttribute()
    {
        if (OrtTestFolder.Folder == null) Skip = "LOCALAITEST_01: set FVS_TEST_ORT_DIR to a folder with onnxruntime.dll, DirectML.dll and tiny.onnx.";
    }
}

public sealed class LocalAiPackFactAttribute : FactAttribute
{
    public static readonly string? Pack = Environment.GetEnvironmentVariable("FVS_TEST_LOCALAI_DIR") is { Length: > 0 } d
        && LocalAiPack.CheckUnpinned(d).IsReady ? d : null;

    public LocalAiPackFactAttribute()
    {
        if (Pack == null) Skip = "LOCALAITEST_01: set FVS_TEST_LOCALAI_DIR to a Local AI pack folder.";
        else if (MediaFactAttribute.Ffmpeg == null) Skip = "LOCALAITEST_01: no FFmpeg (set FVS_TEST_FFMPEG).";
    }
}

public sealed class OrtRuntimeTests
{
    [OrtFact, Trait("Category", OrtFactAttribute.Category)]
    public void TinyModel_RunsOnCpuAndOnDirectMl()
    {
        using var rt = OrtRuntime.Load(OrtTestFolder.Folder!);
        foreach (bool gpu in new[] { false, true })
        {
            using var s = rt.CreateSession(Path.Combine(OrtTestFolder.Folder!, "tiny.onnx"), gpu, out bool onGpu);
            if (!gpu) Assert.False(onGpu);
            var outs = s.Run(new[] { ("x", new[] { 1f, 2f, 3f, 4f }, new long[] { 1, 4 }) }, new[] { "y", "s" });
            Assert.Equal(new[] { 2f, 4f, 6f, 8f }, outs["y"].Data);
            Assert.Equal(new long[] { 1, 4 }, outs["y"].Shape);
            Assert.Equal(10f, outs["s"].Data.Single());
        }
    }

    [OrtFact, Trait("Category", OrtFactAttribute.Category)]
    public void MissingModel_IsAPlainError()
    {
        using var rt = OrtRuntime.Load(OrtTestFolder.Folder!);
        Assert.Throws<OrtException>(() => rt.CreateSession(Path.Combine(OrtTestFolder.Folder!, "nope.onnx"), false, out _));
    }

    [Fact]
    public void MissingRuntime_IsAPlainError()
        => Assert.Throws<OrtException>(() => OrtRuntime.Load(Path.Combine(Path.GetTempPath(), "fvs-no-ort-" + Guid.NewGuid().ToString("N"))));
}

public sealed class LocalTrackerEndToEndTests
{
    private static string Pack => LocalAiPackFactAttribute.Pack!;

    internal static async Task<string> MakeVideoAsync(string name, string filter)
    {
        string dir = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "LocalTrack");
        Directory.CreateDirectory(dir);
        string video = Path.Combine(dir, name);
        var psi = new System.Diagnostics.ProcessStartInfo(MediaFactAttribute.Ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (string a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-filter_complex", filter, "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", video })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
        return video;
    }

    private const string Scene =
        "color=c=0x2a3a2a:s=1280x720:r=30:d={0},noise=alls=25:allf=t[bg];" +
        "color=c=0xd02020:s=70x140:r=30:d={0}[obj];" +
        "[bg][obj]overlay=x='100+t*260':y='300+40*sin(t*2)'";

    [LocalAiPackFact, Trait("Category", OrtFactAttribute.Category)]
    public async Task TheRealModelFollowsAMovingTargetBothWays()
    {
        string video = await MakeVideoAsync("moving.mp4", string.Format(System.Globalization.CultureInfo.InvariantCulture, Scene, 4) + "[v]");
        using var rt = OrtRuntime.Load(Pack);
        using var model = new Sam2BoxTracker(rt, Pack, preferGpu: true);
        double t0 = 2.0;
        double x0 = 100 + t0 * 260, y0 = 300 + 40 * Math.Sin(t0 * 2);
        var req = new LocalTrackingRequest(MediaFactAttribute.Ffmpeg!, video, 0, 4, 1280, 720,
            new PixelBox(x0 - 15, y0 - 20, x0 + 85, y0 + 160), t0);
        var trace = new System.Text.StringBuilder();
        LocalSubjectTracker.Trace = line => trace.AppendLine(line);
        LocalTrackingResult result;
        try { result = await LocalSubjectTracker.TrackAsync(req, model, null, CancellationToken.None); }
        finally { LocalSubjectTracker.Trace = null; }
        File.WriteAllText(Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "LocalTrack", "moving-trace.txt"), trace.ToString());

        Assert.True(result.VisibleFrames >= result.Frames.Count * 0.9, $"seen {result.VisibleFrames}/{result.Frames.Count}: " + string.Join(" | ", result.Frames.Select(f => $"{f.TimeSec:0.0}:{(f.Visible ? 1 : 0)}:{f.Score:0.00}:{f.Box.X1:0},{f.Box.Y1:0},{f.Box.X2:0},{f.Box.Y2:0}")));
        Assert.Null(result.ForwardCutSec);
        Assert.Null(result.BackwardCutSec);
        foreach (var f in result.Frames.Where(f => f.Visible))
        {
            double expectCx = 100 + f.TimeSec * 260 + 35;
            Assert.True(Math.Abs(f.Box.Cx - expectCx) < 40, $"t={f.TimeSec:0.00} cx={f.Box.Cx:0} expected {expectCx:0}");
        }
        Assert.True(result.Waypoints.Count >= 8);
        Assert.All(result.Waypoints, w => Assert.True(w.Visible));
    }

    [LocalAiPackFact, Trait("Category", OrtFactAttribute.Category)]
    public async Task TheRealModelNeverSwitchesToTheOwnAvatarOrACrossingBody()
    {
        string filter =
            "color=c=0x2a3a2a:s=1280x720:r=30:d=4,noise=alls=25:allf=t[bg];" +
            "color=c=0x3050e0:s=280x300:r=30:d=4[me];" +
            "color=c=0xd02020:s=60x120:r=30:d=4[obj];" +
            "color=c=0xe0d040:s=60x120:r=30:d=4[other];" +
            "[bg][me]overlay=x=500:y=420[a];" +
            "[a][other]overlay=x='1100-t*250':y=260[b];" +
            "[b][obj]overlay=x='100+t*250':y=260[v]";
        string video = await MakeVideoAsync("avatar.mp4", filter);
        using var rt = OrtRuntime.Load(Pack);
        using var model = new Sam2BoxTracker(rt, Pack, preferGpu: true);
        var req = new LocalTrackingRequest(MediaFactAttribute.Ffmpeg!, video, 0, 4, 1280, 720,
            new PixelBox(85, 245, 175, 395), 0, AiGameProfile.Fortnite);
        var result = await LocalSubjectTracker.TrackAsync(req, model, null, CancellationToken.None);

        Assert.True(result.OwnBodyRuleOn);
        foreach (var f in result.Frames.Where(f => f.Visible))
        {
            Assert.False(LocalTrackingMath.LooksLikeOwnBody(AiGameProfile.Fortnite, f.Box, 1280, 720), $"t={f.TimeSec:0.00} on the avatar");
            double expectCx = 100 + f.TimeSec * 250 + 30;
            Assert.True(Math.Abs(f.Box.Cx - expectCx) < 60, $"t={f.TimeSec:0.00} cx={f.Box.Cx:0} expected {expectCx:0} (swapped onto the crossing body?)");
        }
        Assert.True(result.VisibleFrames >= result.Frames.Count * 0.7, $"seen {result.VisibleFrames}/{result.Frames.Count}");
    }

    [LocalAiPackFact, Trait("Category", OrtFactAttribute.Category)]
    public async Task TheRealModelStopsAtAHardCut()
    {
        string a = string.Format(System.Globalization.CultureInfo.InvariantCulture, Scene, 2) + "[a];";
        string b = "color=c=0xe8e0c0:s=1280x720:r=30:d=2,noise=alls=15:allf=t[b];[a][b]concat=n=2:v=1:a=0[v]";
        string video = await MakeVideoAsync("cut.mp4", a + b);
        using var rt = OrtRuntime.Load(Pack);
        using var model = new Sam2BoxTracker(rt, Pack, preferGpu: true);
        var req = new LocalTrackingRequest(MediaFactAttribute.Ffmpeg!, video, 0, 4, 1280, 720,
            new PixelBox(85, 280, 185, 460), 0);
        var result = await LocalSubjectTracker.TrackAsync(req, model, null, CancellationToken.None);

        Assert.NotNull(result.ForwardCutSec);
        Assert.InRange(result.ForwardCutSec!.Value, 1.8, 2.3);
        Assert.All(result.Frames.Where(f => f.TimeSec >= 2.3), f => Assert.False(f.Visible));
        Assert.Contains(result.Frames, f => f.Visible && f.TimeSec < 1.8);
    }
}
