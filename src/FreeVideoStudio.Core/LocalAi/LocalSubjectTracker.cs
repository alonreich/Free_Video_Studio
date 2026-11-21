using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.LocalAi;

public sealed record LocalTrackingRequest(
    string FfmpegPath,
    string VideoPath,
    double SourceStartSec,
    double DurationSec,
    int SourceWidth,
    int SourceHeight,
    PixelBox InitialBox,
    double InitialTimeSec,
    AiGameProfile Profile = AiGameProfile.Generic,
    IReadOnlyList<(double TimeSec, PixelBox Box)>? AdditionalAnchors = null,
    IReadOnlyList<TrackingReference>? ConfirmedReferences = null);

public sealed record LocalTrackingResult(
    IReadOnlyList<TrackedFrame> Frames,
    List<RawSubjectWaypoint> Waypoints,
    bool OnGpu,
    double Fps,
    double? ForwardCutSec,
    double? BackwardCutSec,
    int VisibleFrames,
    int OwnBodyRefused = 0,
    bool OwnBodyRuleOn = false,
    bool UsedNeuralPack = true,
    int AmbiguousFrames = 0,
    bool IsUncertain = false,
    double? UncertainTimeSec = null,
    int DisagreementFrames = 0);

public static class LocalSubjectTracker
{
    public const double GpuFps = 10.0;
    public const double CpuFps = 3.0;
    public const double BuiltInFps = 8.0;
    public const int BuiltInInputSize = 256;
    public const double BackwardChunkSec = 2.0;

    internal static Action<string>? Trace;

    public static Task<LocalTrackingResult> TrackAsync(LocalTrackingRequest req, Sam2BoxTracker model,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(model);
        if (req.DurationSec <= 0 || req.SourceWidth <= 0 || req.SourceHeight <= 0)
            throw new ArgumentException("The block has no length or the video has no size.", nameof(req));

        return TrackCoreAsync(req, model, model.OnGpu ? GpuFps : CpuFps, model.InputSize, progress, cancellationToken);
    }

    public static Task<LocalTrackingResult> TrackBuiltInAsync(LocalTrackingRequest req,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.DurationSec <= 0 || req.SourceWidth <= 0 || req.SourceHeight <= 0)
            throw new ArgumentException("The block has no length or the video has no size.", nameof(req));

        return TrackCoreAsync(req, null, BuiltInFps, BuiltInInputSize, progress, cancellationToken);
    }

    private static async Task<LocalTrackingResult> TrackCoreAsync(
        LocalTrackingRequest req,
        Sam2BoxTracker? model,
        double fps,
        int size,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        bool isSam2 = model != null;
        bool ownBodyRule = !LocalTrackingMath.LooksLikeOwnBody(req.Profile, req.InitialBox, req.SourceWidth, req.SourceHeight)
            && (req.Profile.ExcludesThirdPersonAvatar() || req.Profile == AiGameProfile.FirstPerson);
        AiGameProfile? exclude = ownBodyRule ? req.Profile : null;

        var refs = new List<TrackingReference>();
        if (req.ConfirmedReferences is { Count: > 0 } cRefs)
        {
            foreach (var r in cRefs)
            {
                if (r.TimeSec >= -1e-4 && r.TimeSec <= req.DurationSec + 1e-4 && !r.Box.IsEmpty)
                    refs.Add(r with { TimeSec = Math.Clamp(r.TimeSec, 0, req.DurationSec) });
            }
        }
        else
        {
            refs.Add(new TrackingReference(Math.Clamp(req.InitialTimeSec, 0, req.DurationSec), req.InitialBox));
            if (req.AdditionalAnchors != null)
            {
                foreach (var a in req.AdditionalAnchors)
                {
                    if (a.TimeSec >= -1e-4 && a.TimeSec <= req.DurationSec + 1e-4 && !a.Box.IsEmpty)
                        refs.Add(new TrackingReference(Math.Clamp(a.TimeSec, 0, req.DurationSec), a.Box));
                }
            }
        }
        if (refs.Count == 0)
        {
            refs.Add(new TrackingReference(Math.Clamp(req.InitialTimeSec, 0, req.DurationSec), req.InitialBox));
        }
        refs.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));

        var videoFrames = new List<(double T, byte[] Rgb)>();
        await RawVideoFrameReader.ReadAsync(req.FfmpegPath, req.VideoPath, req.SourceStartSec, req.DurationSec, fps,
            size, size, (i, rgb) =>
            {
                double t = i / fps;
                if (t > req.DurationSec + 1e-6) return false;
                videoFrames.Add((t, (byte[])rgb.Clone()));
                return true;
            }, cancellationToken, allowEmpty: true).ConfigureAwait(false);

        if (videoFrames.Count == 0)
        {
            return new LocalTrackingResult(Array.Empty<TrackedFrame>(), new List<RawSubjectWaypoint>(),
                isSam2 && model!.OnGpu, fps, null, null, 0, 0, ownBodyRule, isSam2);
        }

        int expectedTicks = Math.Max(1, videoFrames.Count * (refs.Count > 1 ? 2 : 1));
        int doneTicks = 0;
        void Tick() => progress?.Report(Math.Min(1.0, ++doneTicks / (double)expectedTicks));

        TrackedFrame StepOne(DirectionState state, int frameIdx)
        {
            var vf = videoFrames[frameIdx];
            return isSam2
                ? state.Step(model!, vf.Rgb, size, vf.T, req.SourceWidth, req.SourceHeight)
                : state.StepBuiltIn(vf.Rgb, size, vf.T, req.SourceWidth, req.SourceHeight);
        }

        int FindNearestFrame(double t)
        {
            int best = 0;
            double minDt = double.MaxValue;
            for (int i = 0; i < videoFrames.Count; i++)
            {
                double dt = Math.Abs(videoFrames[i].T - t);
                if (dt < minDt)
                {
                    minDt = dt;
                    best = i;
                }
            }
            return best;
        }

        int ownBodyRefused = 0;
        int ambiguousFrames = 0;
        double? forwardCutSec = null;
        double? backwardCutSec = null;
        bool isUncertain = false;
        double? uncertainTimeSec = null;
        int totalDisagreements = 0;

        var allFrames = new List<TrackedFrame>(videoFrames.Count);

        if (refs.Count == 1)
        {
            var r0 = refs[0];
            int idx0 = FindNearestFrame(r0.TimeSec);

            var backward = new List<TrackedFrame>();
            var bState = new DirectionState(r0.Box, exclude);
            model?.Forget();
            for (int i = idx0 - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tf = StepOne(bState, i);
                backward.Add(tf);
                Tick();
                if (bState.Stopped) break;
            }
            backward.Reverse();
            if (bState.CutAt is double bc) backwardCutSec = bc;
            ownBodyRefused += bState.OwnBodyRefused;
            ambiguousFrames += bState.AmbiguousFrames;

            var forward = new List<TrackedFrame>();
            var fState = new DirectionState(r0.Box, exclude);
            model?.Forget();
            for (int i = idx0; i < videoFrames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tf = StepOne(fState, i);
                forward.Add(tf);
                Tick();
                if (fState.Stopped) break;
            }
            if (fState.CutAt is double fc) forwardCutSec = fc;
            ownBodyRefused += fState.OwnBodyRefused;
            ambiguousFrames += fState.AmbiguousFrames;

            allFrames.AddRange(backward);
            allFrames.AddRange(forward);
        }
        else
        {
            var r0 = refs[0];
            int idx0 = FindNearestFrame(r0.TimeSec);
            if (idx0 > 0)
            {
                var preFrames = new List<TrackedFrame>();
                var preState = new DirectionState(r0.Box, exclude);
                model?.Forget();
                for (int i = idx0 - 1; i >= 0; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tf = StepOne(preState, i);
                    preFrames.Add(tf);
                    Tick();
                    if (preState.Stopped) break;
                }
                preFrames.Reverse();
                if (preState.CutAt is double bc) backwardCutSec = bc;
                ownBodyRefused += preState.OwnBodyRefused;
                ambiguousFrames += preState.AmbiguousFrames;
                allFrames.AddRange(preFrames);
            }

            for (int k = 0; k < refs.Count - 1; k++)
            {
                var refEarlier = refs[k];
                var refLater = refs[k + 1];
                int idxA = FindNearestFrame(refEarlier.TimeSec);
                int idxB = FindNearestFrame(refLater.TimeSec);
                if (idxA > idxB) idxB = idxA;

                var fwd = new List<TrackedFrame>();
                var fState = new DirectionState(refEarlier.Box, exclude);
                model?.Forget();
                for (int i = idxA; i <= idxB; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tf = StepOne(fState, i);
                    fwd.Add(tf);
                    Tick();
                    if (fState.Stopped) break;
                }
                if (fState.CutAt is double fc && (forwardCutSec == null || fc < forwardCutSec.Value))
                    forwardCutSec = fc;
                ownBodyRefused += fState.OwnBodyRefused;
                ambiguousFrames += fState.AmbiguousFrames;

                var bwd = new List<TrackedFrame>();
                var bState = new DirectionState(refLater.Box, exclude);
                model?.Forget();
                for (int i = idxB; i >= idxA; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tf = StepOne(bState, i);
                    bwd.Add(tf);
                    Tick();
                    if (bState.Stopped) break;
                }
                bwd.Reverse();
                if (bState.CutAt is double bc && (backwardCutSec == null || bc > backwardCutSec.Value))
                    backwardCutSec = bc;
                ownBodyRefused += bState.OwnBodyRefused;
                ambiguousFrames += bState.AmbiguousFrames;

                var reconciled = LocalTrackingMath.ReconcileInterval(fwd, bwd, refEarlier, refLater, req.SourceWidth, req.SourceHeight);
                if (reconciled.IsUncertain)
                {
                    isUncertain = true;
                    uncertainTimeSec ??= reconciled.UncertainTimeSec;
                }
                totalDisagreements += reconciled.DisagreementCount;

                int startAppend = 0;
                if (allFrames.Count > 0 && reconciled.Frames.Count > 0 && Math.Abs(allFrames[^1].TimeSec - reconciled.Frames[0].TimeSec) <= 0.005)
                {
                    startAppend = 1;
                }
                for (int s = startAppend; s < reconciled.Frames.Count; s++)
                {
                    allFrames.Add(reconciled.Frames[s]);
                }
            }

            var rLast = refs[^1];
            int idxLast = FindNearestFrame(rLast.TimeSec);
            if (idxLast < videoFrames.Count - 1)
            {
                var postFrames = new List<TrackedFrame>();
                var postState = new DirectionState(rLast.Box, exclude);
                model?.Forget();
                for (int i = idxLast + 1; i < videoFrames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tf = StepOne(postState, i);
                    postFrames.Add(tf);
                    Tick();
                    if (postState.Stopped) break;
                }
                if (postState.CutAt is double fc && (forwardCutSec == null || fc < forwardCutSec.Value))
                    forwardCutSec = fc;
                ownBodyRefused += postState.OwnBodyRefused;
                ambiguousFrames += postState.AmbiguousFrames;

                int startAppend = 0;
                if (allFrames.Count > 0 && postFrames.Count > 0 && Math.Abs(allFrames[^1].TimeSec - postFrames[0].TimeSec) <= 0.005)
                {
                    startAppend = 1;
                }
                for (int s = startAppend; s < postFrames.Count; s++)
                {
                    allFrames.Add(postFrames[s]);
                }
            }
        }

        allFrames.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));
        int visible = 0;
        foreach (var f in allFrames) if (f.Visible) visible++;
        var waypoints = LocalTrackingMath.ToWaypoints(allFrames, req.DurationSec, req.SourceWidth, req.SourceHeight);

        return new LocalTrackingResult(
            allFrames,
            waypoints,
            isSam2 && model!.OnGpu,
            fps,
            forwardCutSec,
            backwardCutSec,
            visible,
            ownBodyRefused,
            ownBodyRule,
            isSam2,
            ambiguousFrames,
            isUncertain,
            uncertainTimeSec,
            totalDisagreements);
    }

    public const string MotionOnsetTag = "LOCALTRACK_02";

    private sealed record DirectionStart(PixelBox Box, float[] Look, double Vx, double Vy, bool HasVelocity, LocalTrackingMath.BuiltInSignature? BuiltInSig = null);

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
        private LocalTrackingMath.BuiltInSignature? _initSig;
        private LocalTrackingMath.BuiltInSignature? _emaSig;
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
            _initSig = r.BuiltInSig;
            _emaSig = r.BuiltInSig;
            _accepted = 1;
            if (!r.HasVelocity) return;
            _vx = -r.Vx;
            _vy = -r.Vy;
            _hasVelocity = true;
        }

        public DirectionStart? Start => _start;

        public int OwnBodyRefused { get; private set; }
        public int AmbiguousFrames { get; private set; }

        public bool Stopped { get; private set; }
        public double? CutAt { get; private set; }

        public void SnapAnchor(PixelBox box)
        {
            _last = box;
            _lost = 0;
            _hasVelocity = false;
            _referenceArea = box.Area;
            _newDirection = true;
        }

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

        public TrackedFrame StepBuiltIn(byte[] rgb, int size, double t, int w, int h)
        {
            if (!Stopped && _cuts.Push(rgb, size, size))
            {
                Stopped = true;
                CutAt = t;
            }
            if (Stopped) return new TrackedFrame(t, _last, false, 0);

            bool unknownMotion = !_first && !_hasVelocity;
            double velScale = _lost > 0 ? Math.Max(0.2, 1.0 - 0.22 * _lost) : 1.0;
            PixelBox predicted = _first ? _last.Clamp(w, h) : Shifted(_last, _vx * (_lost + 1) * velScale, _vy * (_lost + 1) * velScale, w, h);
            _newDirection = false;
            var candidates = GenerateBuiltInCandidates(rgb, size, predicted, _initSig, _emaSig, w, h, _first, _lost, out bool excludedHit, out bool isAmbiguous, _exclude);
            if (excludedHit) OwnBodyRefused++;
            if (isAmbiguous)
            {
                AmbiguousFrames++;
                _lost++;
                return new TrackedFrame(t, predicted, false, candidates.Count > 0 ? candidates[0].Score : 0);
            }
            Func<PixelBox, bool>? excluded = null;
            if (_exclude is AiGameProfile profile)
            {
                excluded = box =>
                {
                    if (!LocalTrackingMath.LooksLikeOwnBody(profile, box, w, h)) return false;
                    OwnBodyRefused++;
                    return true;
                };
            }
            MaskCandidate? pick = candidates.Count > 0
                ? LocalTrackingMath.Choose(candidates, predicted, _first ? default : _last, _referenceArea, null, excluded, unknownMotion)
                : null;
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
                var sampleLook = LocalTrackingMath.Appearance(rgb, size, nb, w, h);
                var sampleSig = LocalTrackingMath.ExtractBuiltInSignature(rgb, size, nb, w, h);
                if (_first)
                {
                    _initSig = sampleSig;
                    _emaSig = sampleSig;
                    _start = new DirectionStart(nb, (float[])sampleLook.Clone(), 0, 0, false, sampleSig);
                }
                else
                {
                    if (c.Score >= 0.68 && _emaSig is { } ema)
                    {
                        LocalTrackingMath.BlendBuiltInSignature(ref ema, sampleSig, LocalTrackingMath.AppearanceRate);
                        _emaSig = ema;
                    }
                    else if (!_emaSig.HasValue)
                    {
                        _emaSig = sampleSig;
                    }
                }
                if (_look == null) _look = sampleLook;
                else LocalTrackingMath.Blend(_look, sampleLook, LocalTrackingMath.AppearanceRate);
                _accepted++;
                _last = nb;
                _lost = 0;
                _first = false;
                return new TrackedFrame(t, nb, true, c.Score);
            }
            _lost++;
            return new TrackedFrame(t, predicted, false, 0);
        }

        private static IReadOnlyList<MaskCandidate> GenerateBuiltInCandidates(
            byte[] rgb, int size, PixelBox predicted,
            LocalTrackingMath.BuiltInSignature? initSig,
            LocalTrackingMath.BuiltInSignature? emaSig,
            int w, int h, bool isFirst, int lostCount,
            out bool excludedHit, out bool isAmbiguous, AiGameProfile? exclude)
        {
            excludedHit = false;
            isAmbiguous = false;
            var list = new List<MaskCandidate>();
            if (predicted.IsEmpty) return list;
            if (isFirst || initSig == null)
            {
                list.Add(new MaskCandidate(predicted, 0.90, 0));
                return list;
            }
            double bw = Math.Max(8, predicted.Width);
            double bh = Math.Max(8, predicted.Height);
            double[] offsets = lostCount >= ReacquireAfterLost
                ? new[] { -1.6, -1.1, -0.75, -0.45, -0.22, 0.0, 0.22, 0.45, 0.75, 1.1, 1.6 }
                : lostCount > 0
                    ? new[] { -0.75, -0.45, -0.22, 0.0, 0.22, 0.45, 0.75 }
                    : new[] { -0.45, -0.22, 0.0, 0.22, 0.45 };
            double minInit = lostCount >= ReacquireAfterLost ? ReacquireMinSimilarity : 0.56;
            double[] scales = { 0.90, 1.0, 1.10 };
            double maxDist = Math.Max(8.0, Math.Sqrt(bw * bw + bh * bh));
            int idx = 0;
            foreach (double sc in scales)
            {
                double sw = bw * sc;
                double sh = bh * sc;
                foreach (double oy in offsets)
                {
                    double cy = predicted.Cy + oy * bh;
                    foreach (double ox in offsets)
                    {
                        double cx = predicted.Cx + ox * bw;
                        var cand = new PixelBox(cx - sw * 0.5, cy - sh * 0.5, cx + sw * 0.5, cy + sh * 0.5).Clamp(w, h);
                        if (cand.Width < 4 || cand.Height < 4) continue;
                        if (exclude is AiGameProfile prof && LocalTrackingMath.LooksLikeOwnBody(prof, cand, w, h))
                        {
                            excludedHit = true;
                            continue;
                        }
                        var candSig = LocalTrackingMath.ExtractBuiltInSignature(rgb, size, cand, w, h);
                        double simInit = LocalTrackingMath.BuiltInSimilarity(initSig.Value, candSig);
                        if (simInit < minInit) continue;
                        double simEma = emaSig.HasValue ? LocalTrackingMath.BuiltInSimilarity(emaSig.Value, candSig) : simInit;
                        double appearanceSim = 0.68 * simInit + 0.32 * simEma;
                        double dist = Math.Sqrt(Math.Pow(cand.Cx - predicted.Cx, 2) + Math.Pow(cand.Cy - predicted.Cy, 2));
                        double distWeight = 1.0 - 0.12 * Math.Clamp(dist / maxDist, 0, 1);
                        double score = Math.Clamp(appearanceSim * distWeight, 0.0, 0.99);
                        if (score >= LocalTrackingMath.MinScore)
                            list.Add(new MaskCandidate(cand, score, idx++));
                    }
                }
            }
            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (list.Count > 0) list[0] = RefineBuiltIn(list[0], rgb, size, initSig.Value, emaSig, predicted, maxDist, w, h, exclude);
            if (list.Count >= 2)
            {
                var best = list[0];
                foreach (var alt in list)
                {
                    if (LocalTrackingMath.Iou(best.Box, alt.Box) >= 0.20) continue;
                    double gap = best.Score - alt.Score;
                    if (gap <= 0.025 && (lostCount > 0 || Math.Abs(LocalTrackingMath.Iou(best.Box, predicted) - LocalTrackingMath.Iou(alt.Box, predicted)) < 0.15))
                    {
                        isAmbiguous = true;
                        break;
                    }
                }
            }
            if (list.Count > 6) list.RemoveRange(6, list.Count - 6);
            return list;
        }

        public const string RefineTag = "LOCALTRACK_03";
        public const int ReacquireAfterLost = 3;
        public const double ReacquireMinSimilarity = 0.66;
        private static readonly double[] RefineOffsets = { -0.08, -0.04, 0.0, 0.04, 0.08 };

        private static MaskCandidate RefineBuiltIn(MaskCandidate coarse, byte[] rgb, int size,
            LocalTrackingMath.BuiltInSignature initSig, LocalTrackingMath.BuiltInSignature? emaSig,
            PixelBox predicted, double maxDist, int w, int h, AiGameProfile? exclude)
        {
            var best = coarse;
            double bw = coarse.Box.Width, bh = coarse.Box.Height;
            foreach (double oy in RefineOffsets)
            {
                foreach (double ox in RefineOffsets)
                {
                    if (ox == 0 && oy == 0) continue;
                    double cx = coarse.Box.Cx + ox * bw, cy = coarse.Box.Cy + oy * bh;
                    var cand = new PixelBox(cx - bw * 0.5, cy - bh * 0.5, cx + bw * 0.5, cy + bh * 0.5).Clamp(w, h);
                    if (cand.Width < 4 || cand.Height < 4) continue;
                    if (exclude is AiGameProfile prof && LocalTrackingMath.LooksLikeOwnBody(prof, cand, w, h)) continue;
                    var sig = LocalTrackingMath.ExtractBuiltInSignature(rgb, size, cand, w, h);
                    double simInit = LocalTrackingMath.BuiltInSimilarity(initSig, sig);
                    double simEma = emaSig.HasValue ? LocalTrackingMath.BuiltInSimilarity(emaSig.Value, sig) : simInit;
                    double dist = Math.Sqrt(Math.Pow(cand.Cx - predicted.Cx, 2) + Math.Pow(cand.Cy - predicted.Cy, 2));
                    double score = Math.Clamp((0.68 * simInit + 0.32 * simEma) * (1.0 - 0.12 * Math.Clamp(dist / maxDist, 0, 1)), 0.0, 0.99);
                    if (score > best.Score + 0.002) best = new MaskCandidate(cand, score, coarse.Index);
                }
            }
            return best;
        }

        private static string Fmt(PixelBox b) => $"{b.X1:0},{b.Y1:0},{b.X2:0},{b.Y2:0}";

        private static PixelBox Shifted(PixelBox b, double dx, double dy, int w, int h)
            => new PixelBox(b.X1 + dx, b.Y1 + dy, b.X2 + dx, b.Y2 + dy).Clamp(w, h);
    }
}
