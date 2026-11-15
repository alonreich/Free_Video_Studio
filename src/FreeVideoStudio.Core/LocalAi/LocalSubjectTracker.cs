using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.LocalAi;

/// <summary>What one local tracking run needs. Times are seconds; <see cref="SourceStartSec"/> is in the SOURCE file.</summary>
public sealed record LocalTrackingRequest(
    string FfmpegPath,
    string VideoPath,
    double SourceStartSec,
    double DurationSec,
    int SourceWidth,
    int SourceHeight,
    PixelBox InitialBox,
    double InitialTimeSec,
    AiGameProfile Profile = AiGameProfile.Generic);

/// <summary>The dense result and the waypoints made from it.</summary>
public sealed record LocalTrackingResult(
    IReadOnlyList<TrackedFrame> Frames,
    List<RawSubjectWaypoint> Waypoints,
    bool OnGpu,
    double Fps,
    double? ForwardCutSec,
    double? BackwardCutSec,
    int VisibleFrames,
    int OwnBodyRefused = 0,
    bool OwnBodyRuleOn = false);

/// <summary>
/// LOCALTRACK_01 (docs/01 TL-LOCALTRACK) — follows one box through a block on the user's own computer. The target is
/// tracked FORWARD from the moment it was picked to the end of the block and BACKWARD from that moment to the start
/// (in 2 s chunks read newest-first, so memory stays at one chunk). Each direction stops at a hard cut
/// (<see cref="SceneCutDetector"/>): the frames after it are "not visible" and the camera eases wide there.
/// </summary>
public static class LocalSubjectTracker
{
    public const double GpuFps = 10.0;
    public const double CpuFps = 3.0;
    public const double BackwardChunkSec = 2.0;

    /// <summary>Diagnostics only (tests): one line per frame with the prediction and every candidate.</summary>
    internal static Action<string>? Trace;

    public static async Task<LocalTrackingResult> TrackAsync(LocalTrackingRequest req, Sam2BoxTracker model,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(model);
        if (req.DurationSec <= 0 || req.SourceWidth <= 0 || req.SourceHeight <= 0)
            throw new ArgumentException("The block has no length or the video has no size.", nameof(req));

        double fps = model.OnGpu ? GpuFps : CpuFps;
        bool ownBodyRule = !LocalTrackingMath.LooksLikeOwnBody(req.Profile, req.InitialBox, req.SourceWidth, req.SourceHeight)
            && (req.Profile.ExcludesThirdPersonAvatar() || req.Profile == AiGameProfile.FirstPerson);
        AiGameProfile? exclude = ownBodyRule ? req.Profile : null;
        int size = model.InputSize;
        double start = Math.Clamp(req.InitialTimeSec, 0, req.DurationSec);
        int expected = Math.Max(1, (int)Math.Ceiling(req.DurationSec * fps));
        int done = 0;
        void Tick() => progress?.Report(Math.Min(1.0, ++done / (double)expected));

        var forward = new List<TrackedFrame>();
        var fState = new DirectionState(req.InitialBox, exclude);
        await RawVideoFrameReader.ReadAsync(req.FfmpegPath, req.VideoPath, req.SourceStartSec + start, req.DurationSec - start, fps,
            size, size, (i, rgb) =>
            {
                double t = start + i / fps;
                if (t > req.DurationSec + 1e-6) return false;
                forward.Add(fState.Step(model, rgb, size, t, req.SourceWidth, req.SourceHeight));
                Tick();
                return true;
            }, cancellationToken).ConfigureAwait(false);

        var backward = new List<TrackedFrame>();
        var bState = new DirectionState(req.InitialBox, exclude, fState.Start);
        double chunkEnd = start;
        while (chunkEnd > 1e-6 && !bState.Stopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double chunkStart = Math.Max(0, chunkEnd - BackwardChunkSec);
            var buffered = new List<(double T, byte[] Rgb)>();
            double from = chunkStart, upTo = chunkEnd;
            await RawVideoFrameReader.ReadAsync(req.FfmpegPath, req.VideoPath, req.SourceStartSec + from, upTo - from, fps,
                size, size, (i, rgb) =>
                {
                    double t = from + i / fps;
                    if (t >= upTo - 1e-6) return false;
                    buffered.Add((t, (byte[])rgb.Clone()));
                    return true;
                }, cancellationToken, allowEmpty: true).ConfigureAwait(false);
            for (int k = buffered.Count - 1; k >= 0; k--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                backward.Add(bState.Step(model, buffered[k].Rgb, size, buffered[k].T, req.SourceWidth, req.SourceHeight));
                Tick();
            }
            chunkEnd = chunkStart;
        }

        backward.Reverse();
        var all = new List<TrackedFrame>(backward.Count + forward.Count);
        all.AddRange(backward);
        all.AddRange(forward);
        int visible = 0;
        foreach (var f in all) if (f.Visible) visible++;
        var waypoints = LocalTrackingMath.ToWaypoints(all, req.DurationSec, req.SourceWidth, req.SourceHeight);
        return new LocalTrackingResult(all, waypoints, model.OnGpu, fps, fState.CutAt, bState.CutAt, visible,
            fState.OwnBodyRefused + bState.OwnBodyRefused, ownBodyRule);
    }

    public const string MotionOnsetTag = "LOCALTRACK_02";

    /// <summary>LOCALTRACK_02 — what the forward pass learned on the picked frame: the target's exact box, its look and its first motion per frame.</summary>
    private sealed record DirectionStart(PixelBox Box, float[] Look, double Vx, double Vy, bool HasVelocity);

    /// <summary>
    /// One direction's memory: the last good box, its motion per frame, how long it has been lost, the target's usual
    /// size (reference area: the first accepted box, then 90 % old + 10 % new) and the cut. While lost the motion is
    /// kept, so the prompt coasts with the target instead of waiting where it was last seen.
    /// </summary>
    private sealed class DirectionState
    {
        private readonly SceneCutDetector _cuts = new();
        private PixelBox _last;
        private double _vx, _vy;
        private bool _hasVelocity;
        private int _lost;
        private bool _first = true;
        private double _referenceArea;
        private readonly AiGameProfile? _exclude;
        private float[]? _look;
        private int _accepted;
        public const int LookAfterFrames = 3;
        public const int ForgetMaskAfterLost = 3;

        private DirectionStart? _start;
        private bool _newDirection;

        public DirectionState(PixelBox initial, AiGameProfile? exclude)
        {
            _last = initial;
            _exclude = exclude;
        }

        public DirectionState(PixelBox initial, AiGameProfile? exclude, DirectionStart? reference) : this(initial, exclude)
        {
            if (reference is not DirectionStart r) return;
            _last = r.Box;
            _first = false;
            _newDirection = true;
            _referenceArea = r.Box.Area;
            _look = (float[])r.Look.Clone();
            _accepted = 1;
            if (!r.HasVelocity) return;
            _vx = -r.Vx;
            _vy = -r.Vy;
            _hasVelocity = true;
        }

        public DirectionStart? Start => _start;

        public int OwnBodyRefused { get; private set; }

        public bool Stopped { get; private set; }
        public double? CutAt { get; private set; }

        public TrackedFrame Step(Sam2BoxTracker model, byte[] rgb, int size, double t, int w, int h)
        {
            if (!Stopped && _cuts.Push(rgb, size, size))
            {
                Stopped = true;
                CutAt = t;
            }
            if (Stopped) return new TrackedFrame(t, _last, false, 0);

            bool unknownMotion = !_first && !_hasVelocity;
            PixelBox predicted = _first ? _last.Clamp(w, h) : Shifted(_last, _vx * (_lost + 1), _vy * (_lost + 1), w, h);
            PixelBox prompt = _first ? predicted : LocalTrackingMath.NextPrompt(_last, _vx, _vy, w, h, _lost + 1);
            if (_first || _newDirection || unknownMotion || _lost >= ForgetMaskAfterLost) model.Forget();
            _newDirection = false;
            var (candidates, objectLogit) = model.Step(rgb, prompt, predicted.Cx, predicted.Cy, w, h,
                predicted.Cx - _last.Cx, predicted.Cy - _last.Cy);
            Trace?.Invoke($"t={t:0.0} obj={objectLogit:0.0} pred={Fmt(predicted)} last={Fmt(_last)} ref={_referenceArea:0} cands=" + string.Join(" ", candidates.Select(k => Fmt(k.Box) + "@" + k.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));
            Func<PixelBox, double>? look = _look != null && _accepted >= LookAfterFrames
                ? box => LocalTrackingMath.Similarity(_look, LocalTrackingMath.Appearance(rgb, size, box, w, h))
                : null;
            bool refused = false;
            Func<PixelBox, bool>? excluded = null;
            if (_exclude is AiGameProfile profile)
            {
                excluded = box =>
                {
                    if (!LocalTrackingMath.LooksLikeOwnBody(profile, box, w, h)) return false;
                    refused = true;
                    return true;
                };
            }
            MaskCandidate? pick = objectLogit > 0
                ? LocalTrackingMath.Choose(candidates, predicted, _first ? default : _last, _referenceArea, look, excluded, unknownMotion)
                : null;
            if (refused) OwnBodyRefused++;
            if (pick is MaskCandidate c)
            {
                PixelBox nb = c.Box;
                if (!_first)
                {
                    int frames = _lost + 1;
                    double dx = (nb.Cx - _last.Cx) / frames, dy = (nb.Cy - _last.Cy) / frames;
                    _vx = _hasVelocity ? 0.5 * _vx + 0.5 * dx : dx;
                    _vy = _hasVelocity ? 0.5 * _vy + 0.5 * dy : dy;
                    if (!_hasVelocity && _start != null) _start = _start with { Vx = dx, Vy = dy, HasVelocity = true };
                    _hasVelocity = true;
                }
                _referenceArea = _referenceArea <= 0 ? nb.Area : 0.9 * _referenceArea + 0.1 * nb.Area;
                var sample = LocalTrackingMath.Appearance(rgb, size, nb, w, h);
                if (_first) _start = new DirectionStart(nb, (float[])sample.Clone(), 0, 0, false);
                if (_look == null) _look = sample;
                else LocalTrackingMath.Blend(_look, sample, LocalTrackingMath.AppearanceRate);
                _accepted++;
                model.Remember(c.Index);
                _last = nb;
                _lost = 0;
                _first = false;
                return new TrackedFrame(t, nb, true, c.Score);
            }
            _lost++;
            return new TrackedFrame(t, predicted, false, Math.Min(1.0, Math.Max(0.0, objectLogit)));
        }

        private static string Fmt(PixelBox b) => $"{b.X1:0},{b.Y1:0},{b.X2:0},{b.Y2:0}";

        private static PixelBox Shifted(PixelBox b, double dx, double dy, int w, int h)
            => new PixelBox(b.X1 + dx, b.Y1 + dy, b.X2 + dx, b.Y2 + dy).Clamp(w, h);
    }
}
