using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.LocalAi;
using FreeVideoStudio.Core.Media;
using SkiaSharp;

namespace FreeVideoStudio.App.Services;

internal static class LocalTrackingService
{
    public const string SlowPcNote = " (no fast graphics card found, so this is slow)";

    public static bool IsReady => AiSetup.UsesPc(AiSetup.Current());

    public static bool IsNeuralPackReady => LocalAiPackService.IsReady;

    public static async Task<AiDiscoveryResult> DiscoverLocalSubjectsAsync(
        string videoPath,
        double sourceStartSec,
        double sourceDurationSec,
        AiGameProfile profile,
        Action<string>? log,
        CancellationToken ct,
        int attempt = 0)
    {
        if (!File.Exists(videoPath)) throw new FileNotFoundException("Video file not found", videoPath);
        log?.Invoke("Scanning 3 frames from this segment on your PC (free PC tracking — nothing sent online)…");
        var (f1, f2, f3, t1, t2, t3) = await GeminiTrackingService.ExtractThreeCandidateFramesAsync(
            videoPath, sourceStartSec, sourceDurationSec, ct, attempt).ConfigureAwait(false);

        string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        const int analysisW = 320;
        const int analysisH = 180;
        string[] paths = { f1, f2, f3 };
        double[] times = { t1, t2, t3 };
        var frameRgbs = new List<byte[]>(3);
        var motionPairs = new List<(byte[] PrevRgb, byte[] NextRgb)>(3);

        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            byte[] mainRgb = DecodeJpegToRgb(paths[i], analysisW, analysisH);
            frameRgbs.Add(mainRgb);

            byte[] prevRgb = mainRgb;
            byte[] nextRgb = mainRgb;
            try
            {
                double startRead = Math.Max(sourceStartSec, times[i] - 0.15);
                double readDuration = Math.Min(0.35, sourceStartSec + sourceDurationSec - startRead);
                var burst = new List<byte[]>(3);
                await RawVideoFrameReader.ReadAsync(ffmpeg, videoPath, startRead, readDuration, 10.0,
                    analysisW, analysisH, (_, buf) =>
                    {
                        burst.Add((byte[])buf.Clone());
                        return burst.Count < 3;
                    }, ct, allowEmpty: true).ConfigureAwait(false);
                if (burst.Count >= 2)
                {
                    prevRgb = burst[0];
                    nextRgb = burst[^1];
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
            }
            motionPairs.Add((prevRgb, nextRgb));
        }

        IReadOnlyList<LocalDiscoveredCandidate> found;
        var packStatus = LocalAiPackService.Status();
        IReadOnlyList<LocalAiPackFile> pinnedFiles = LocalAiPackManifest.IsPublished ? LocalAiPackManifest.Files : Array.Empty<LocalAiPackFile>();
        LocalDetectorStatus detectorStatus = LocalAiPack.CheckDetector(packStatus.Folder, pinnedFiles, packStatus);
        log?.Invoke(detectorStatus.Message);

        if (packStatus.IsReady && detectorStatus.IsInstalled)
        {
            try
            {
                using var rt = OrtRuntime.Load(packStatus.Folder);
                using var det = new YoloOnnxDetector(rt, packStatus.Folder, preferGpu: true);
                found = LocalSubjectDiscovery.DiscoverOnFrames(frameRgbs, analysisW, analysisH, motionPairs, profile, det, null);
            }
            catch (Exception ex)
            {
                CoreLogger.Warn("LocalAI", $"YOLO detector load failed: {ex.Message}");
                detectorStatus = new LocalDetectorStatus(LocalDetectorState.Damaged, detectorStatus.Path,
                    $"Object detector ({LocalAiPackManifest.DetectorFile}) failed to load and appears damaged; using motion and standout regions instead.");
                log?.Invoke(detectorStatus.Message);
                found = LocalSubjectDiscovery.DiscoverOnFrames(frameRgbs, analysisW, analysisH, motionPairs, profile);
            }
        }
        else
        {
            found = LocalSubjectDiscovery.DiscoverOnFrames(frameRgbs, analysisW, analysisH, motionPairs, profile);
        }

        var subjects = new List<DiscoveredSubject>(found.Count);
        foreach (var c in found)
        {
            subjects.Add(new DiscoveredSubject
            {
                Id = c.Id,
                Label = c.Label,
                FrameIndex = c.FrameIndex,
                Ymin = c.Ymin,
                Xmin = c.Xmin,
                Ymax = c.Ymax,
                Xmax = c.Xmax,
                IsDetectorMatch = c.IsDetectorMatch,
            });
        }
        log?.Invoke($"Local PC scan found {subjects.Count} suggested region(s) across the 3 segment frames ({(detectorStatus.IsInstalled ? "object detector + motion" : "motion & contrast heuristics, no detector model")}).");
        return new AiDiscoveryResult(subjects, f1, f2, f3, t1, t2, t3, 0)
        {
            DetectorStatus = detectorStatus,
        };
    }

    internal static byte[] DecodeJpegToRgb(string path, int targetW, int targetH)
    {
        var rgb = new byte[targetW * targetH * 3];
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return rgb;
        try
        {
            using var original = SKBitmap.Decode(path);
            if (original == null) return rgb;
            using var scaled = new SKBitmap(new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Opaque));
            if (!original.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear))) return rgb;
            ReadOnlySpan<byte> src = scaled.GetPixelSpan();
            int pixels = targetW * targetH;
            for (int p = 0; p < pixels; p++)
            {
                rgb[p * 3] = src[p * 4];
                rgb[p * 3 + 1] = src[p * 4 + 1];
                rgb[p * 3 + 2] = src[p * 4 + 2];
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
        return rgb;
    }

    public static async Task<AiTrajectorySmoother.SmoothedTrajectory> TrackAsync(
        AiTrackingRequest req, IProgress<string>? status, Action<string>? log, CancellationToken ct, IProgress<double>? fraction = null, bool allowInOnlineMode = false)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (!allowInOnlineMode && !AiSetup.TracksOnPc(AiSetup.Current()))
            throw new InvalidOperationException("Local tracking cannot run automatically while AI mode is set to 'Online only'.");
        if (!File.Exists(req.VideoPath)) throw new FileNotFoundException("Video file not found", req.VideoPath);
        string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        var target = req.Target;
        var box = LocalTrackingMath.FromGrid(target.Ymin, target.Xmin, target.Ymax, target.Xmax, req.SourceWidth, req.SourceHeight);
        if (box.IsEmpty) throw new InvalidOperationException("The target box is empty. Draw a zoom box around the target and try again.");

        string label = string.IsNullOrWhiteSpace(target.Label) ? "the target" : target.Label;
        status?.Report($"Step 3 of 4 · Following '{label}' on this computer…");
        var confirmedRefs = new List<TrackingReference>();
        double initialRefTime = target.ExactTimeSec ?? req.ReferenceTimeInSegmentSec;
        if (initialRefTime <= 0 && target.ExactTimeSec == null)
        {
            initialRefTime = (target.FrameIndex switch { 1 => 0.15, 3 => 0.85, _ => 0.50 }) * req.SourceDurationSec;
        }
        confirmedRefs.Add(new TrackingReference(Math.Clamp(initialRefTime, 0, req.SourceDurationSec), box, target.FrameIndex));

        List<(double TimeSec, PixelBox Box)>? anchors = null;
        if (target.AdditionalAnchors is { Count: > 0 } extraAnchors)
        {
            anchors = new List<(double TimeSec, PixelBox Box)>();
            foreach (var anchor in extraAnchors)
            {
                var abox = LocalTrackingMath.FromGrid(anchor.Ymin, anchor.Xmin, anchor.Ymax, anchor.Xmax, req.SourceWidth, req.SourceHeight);
                if (!abox.IsEmpty)
                {
                    double atime = anchor.ExactTimeSec ?? ((anchor.FrameIndex switch { 1 => 0.15, 3 => 0.85, _ => 0.50 }) * req.SourceDurationSec);
                    atime = Math.Clamp(atime, 0, req.SourceDurationSec);
                    anchors.Add((atime, abox));
                    confirmedRefs.Add(new TrackingReference(atime, abox, anchor.FrameIndex));
                }
            }
        }

        var request = new LocalTrackingRequest(ffmpeg, req.VideoPath, req.SourceStartSec, req.SourceDurationSec,
            req.SourceWidth, req.SourceHeight, box, Math.Clamp(req.ReferenceTimeInSegmentSec, 0, req.SourceDurationSec), req.Profile, anchors, confirmedRefs);

        int lastPercent = -1;
        string speedNote = string.Empty;
        var percent = new Progress<double>(p =>
        {
            fraction?.Report(p);
            int pc = (int)Math.Round(p * 100);
            if (pc / 5 == lastPercent / 5) return;
            lastPercent = pc;
            status?.Report($"Step 3 of 4 · Following '{label}' on this computer{speedNote}… {pc}%");
        });

        var started = DateTime.UtcNow;
        var packStatus = LocalAiPackService.Status();
        LocalTrackingResult? result = null;
        if (packStatus.IsReady)
        {
            try
            {
                result = await LocalAiPackService.WithTrackerAsync(
                    model =>
                    {
                        if (!model.OnGpu)
                        {
                            speedNote = SlowPcNote;
                            status?.Report($"Step 3 of 4 · Following '{label}' on this computer{speedNote}…");
                        }
                        return Task.Run(() => LocalSubjectTracker.TrackAsync(request, model, percent, ct), ct);
                    }, log, ct).ConfigureAwait(false);
            }
            catch (OrtException ex) when (!ct.IsCancellationRequested)
            {
                RuntimeLog.Info("LocalAi", $"Optional neural pack runtime failed ({ex.Message}); falling back to built-in basic local tracker.");
                log?.Invoke($"Optional neural AI pack runtime failed ({ex.Message}) — falling back to built-in basic local tracker on this PC…");
            }
        }
        else if (packStatus.State == LocalAiPackState.Damaged)
        {
            RuntimeLog.Info("LocalAi", $"Optional neural pack is damaged ({packStatus.Message}); using built-in basic local tracker.");
            log?.Invoke("Optional neural AI pack is damaged — using built-in basic local tracker on this PC instead (you can reinstall the optional pack in Settings › AI)…");
        }
        if (result == null)
        {
            if (packStatus.State != LocalAiPackState.Damaged && !packStatus.IsReady)
                log?.Invoke("Following target with built-in basic local tracker on this PC (optional neural SAM2 pack is not installed; rapid motion, occlusions, or busy lookalikes may hold wide)…");
            result = await Task.Run(() => LocalSubjectTracker.TrackBuiltInAsync(request, percent, ct), ct).ConfigureAwait(false);
        }

        var ci = CultureInfo.InvariantCulture;
        string engineKind = result.UsedNeuralPack ? (result.OnGpu ? "neural pack (GPU)" : "neural pack (CPU)") : "built-in basic CPU tracker";
        log?.Invoke($"Tracked {result.Frames.Count} frames at {result.Fps.ToString("0", ci)} per second using {engineKind} " +
                    $"in {(DateTime.UtcNow - started).TotalSeconds.ToString("0.0", ci)} s; target locked in {result.VisibleFrames} of {result.Frames.Count} frames" +
                    (result.AmbiguousFrames > 0 ? $" ({result.AmbiguousFrames} ambiguous frame(s) held wide)." : "."));
        if (result.OwnBodyRefused > 0)
            log?.Invoke($"Ignored your own {(req.Profile == AiGameProfile.FirstPerson ? "hands and weapon" : "character")} in {result.OwnBodyRefused} frame(s) — the camera never follows it.");
        if (result.ForwardCutSec is double fc)
            log?.Invoke($"The shot changes at {fc.ToString("0.0", ci)} s — the camera eases wide from there instead of following someone else.");
        if (result.BackwardCutSec is double bc)
            log?.Invoke($"The shot changes at {bc.ToString("0.0", ci)} s — the camera stays wide before it.");
        CoreLogger.Info("LocalTrack", $"neural={result.UsedNeuralPack} frames={result.Frames.Count} visible={result.VisibleFrames} ambiguous={result.AmbiguousFrames} gpu={result.OnGpu} fps={result.Fps} cutF={result.ForwardCutSec} cutB={result.BackwardCutSec} ownBody={result.OwnBodyRuleOn}/{result.OwnBodyRefused}");

        if (result.VisibleFrames == 0)
            throw new InvalidOperationException($"The {(result.UsedNeuralPack ? "local neural tracker" : "built-in basic local tracker")} could not follow '{label}' in this block. Draw a tighter zoom box around the target, pick another frame, or switch to Online AI tracking.");

        ct.ThrowIfCancellationRequested();
        status?.Report("Step 4 of 4 · Smoothing the camera path…");
        var trajectory = AiTrajectorySmoother.SmoothTrajectory(
            result.Waypoints, req.SourceDurationSec, req.SourceWidth, req.SourceHeight,
            req.BaseScale, req.MinScale, req.DeadbandPercent, req.AvoidHud, req.PortraitMode, req.HudZones);
        trajectory.IsUncertain = result.IsUncertain;
        trajectory.UncertainTimeSec = result.UncertainTimeSec;
        log?.Invoke($"Built {trajectory.Keyframes.Count} camera keyframes from {result.Waypoints.Count} tracking points.");
        return trajectory;
    }
}
