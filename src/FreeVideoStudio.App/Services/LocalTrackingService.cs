using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.LocalAi;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

internal static class LocalTrackingService
{
    public const string SlowPcNote = " (no fast graphics card found, so this is slow)";

    public static bool IsReady => AiSetup.UsesPc(AiSetup.Current()) && LocalAiPackService.IsReady;

    public static async Task<AiTrajectorySmoother.SmoothedTrajectory> TrackAsync(
        AiTrackingRequest req, IProgress<string>? status, Action<string>? log, CancellationToken ct, IProgress<double>? fraction = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (!File.Exists(req.VideoPath)) throw new FileNotFoundException("Video file not found", req.VideoPath);
        string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        var target = req.Target;
        var box = LocalTrackingMath.FromGrid(target.Ymin, target.Xmin, target.Ymax, target.Xmax, req.SourceWidth, req.SourceHeight);
        if (box.IsEmpty) throw new InvalidOperationException("The target box is empty. Draw a zoom box around the target and try again.");

        string label = string.IsNullOrWhiteSpace(target.Label) ? "the target" : target.Label;
        status?.Report($"Step 3 of 4 · Following '{label}' on this computer…");
        var request = new LocalTrackingRequest(ffmpeg, req.VideoPath, req.SourceStartSec, req.SourceDurationSec,
            req.SourceWidth, req.SourceHeight, box, Math.Clamp(req.ReferenceTimeInSegmentSec, 0, req.SourceDurationSec), req.Profile);

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
        LocalTrackingResult result = await LocalAiPackService.WithTrackerAsync(
            model =>
            {
                if (!model.OnGpu)
                {
                    speedNote = SlowPcNote;
                    status?.Report($"Step 3 of 4 · Following '{label}' on this computer{speedNote}…");
                }
                return Task.Run(() => LocalSubjectTracker.TrackAsync(request, model, percent, ct), ct);
            }, log, ct).ConfigureAwait(false);

        var ci = CultureInfo.InvariantCulture;
        log?.Invoke($"Tracked {result.Frames.Count} frames at {result.Fps.ToString("0", ci)} per second on the {(result.OnGpu ? "graphics card" : "processor")} " +
                    $"in {(DateTime.UtcNow - started).TotalSeconds.ToString("0.0", ci)} s; the target was seen in {result.VisibleFrames}.");
        if (result.OwnBodyRefused > 0)
            log?.Invoke($"Ignored your own {(req.Profile == AiGameProfile.FirstPerson ? "hands and weapon" : "character")} in {result.OwnBodyRefused} frame(s) — the camera never follows it.");
        if (result.ForwardCutSec is double fc)
            log?.Invoke($"The shot changes at {fc.ToString("0.0", ci)} s — the camera eases wide from there instead of following someone else.");
        if (result.BackwardCutSec is double bc)
            log?.Invoke($"The shot changes at {bc.ToString("0.0", ci)} s — the camera stays wide before it.");
        CoreLogger.Info("LocalTrack", $"frames={result.Frames.Count} visible={result.VisibleFrames} gpu={result.OnGpu} fps={result.Fps} cutF={result.ForwardCutSec} cutB={result.BackwardCutSec} ownBody={result.OwnBodyRuleOn}/{result.OwnBodyRefused}");

        if (result.VisibleFrames == 0)
            throw new InvalidOperationException($"The local tracker could not find '{label}' in this block. Draw a tighter zoom box around the target, or pick a clearer moment.");

        ct.ThrowIfCancellationRequested();
        status?.Report("Step 4 of 4 · Smoothing the camera path…");
        var trajectory = AiTrajectorySmoother.SmoothTrajectory(
            result.Waypoints, req.SourceDurationSec, req.SourceWidth, req.SourceHeight,
            req.BaseScale, req.MinScale, req.DeadbandPercent, req.AvoidHud, req.PortraitMode, req.HudZones);
        log?.Invoke($"Built {trajectory.Keyframes.Count} camera keyframes from {result.Waypoints.Count} tracking points.");
        return trajectory;
    }
}
