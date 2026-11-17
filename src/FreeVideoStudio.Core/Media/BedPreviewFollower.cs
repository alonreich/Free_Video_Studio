namespace FreeVideoStudio.Core.Media;

public enum BedPreviewAction { None, Start, Seek, Stop }

public readonly record struct BedPreviewDecision(BedPreviewAction Action, string? Path = null, double PositionSec = 0);

public sealed class BedPreviewFollower
{
    public const double DriftToleranceSec = 0.12;
    public const int DriftStrikes = 2;
    public const int DriftConfirmMs = 500;
    public const int SettleMs = 350;

    private string? _path;
    private long _overSinceMs = -1;
    private long _holdUntilMs;

    public bool IsPlaying { get; private set; }

    public string? Path => _path;

    public int Starts { get; private set; }

    public int Corrections { get; private set; }

    public int Resyncs { get; private set; }

    private bool _resyncPending;

    public void RequestResync()
    {
        if (!IsPlaying) return;
        _resyncPending = true;
    }

    public BedPreviewDecision Decide(IReadOnlyList<MusicBedSegment> plan, double bedClockSec, bool shouldPlay,
        bool videoSeeking, bool allowStart, double playerPositionSec, bool playerSeeking, long nowMs)
    {
        (string Path, double PositionSec, int SegmentIndex)? target = shouldPlay ? MusicBedPlan.Locate(plan, bedClockSec) : null;
        if (target is not { } want) return IsPlaying ? Stopped() : default;
        if (videoSeeking) return default;
        bool resync = _resyncPending && IsPlaying && string.Equals(want.Path, _path, StringComparison.OrdinalIgnoreCase);
        _resyncPending = false;
        if (resync || !IsPlaying || !string.Equals(want.Path, _path, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsPlaying && !allowStart) return default;
            IsPlaying = true;
            _path = want.Path;
            _overSinceMs = -1;
            _holdUntilMs = nowMs + SettleMs;
            if (resync) Resyncs++; else Starts++;
            return new BedPreviewDecision(BedPreviewAction.Start, want.Path, Math.Max(0, want.PositionSec));
        }
        if (nowMs < _holdUntilMs || playerSeeking || !double.IsFinite(playerPositionSec)) return default;
        if (Math.Abs(playerPositionSec - want.PositionSec) <= DriftToleranceSec)
        {
            _overSinceMs = -1;
            return default;
        }
        if (_overSinceMs < 0) { _overSinceMs = nowMs; return default; }
        if (nowMs - _overSinceMs < DriftConfirmMs) return default;
        _overSinceMs = -1;
        _holdUntilMs = nowMs + SettleMs;
        Corrections++;
        return new BedPreviewDecision(BedPreviewAction.Seek, want.Path, Math.Max(0, want.PositionSec));
    }

    public void NotifyStopped()
    {
        IsPlaying = false;
        _path = null;
        _overSinceMs = -1;
        _resyncPending = false;
    }

    private BedPreviewDecision Stopped()
    {
        NotifyStopped();
        return new BedPreviewDecision(BedPreviewAction.Stop);
    }

    public static double BedClock(double outputSec, double startDelaySec, bool keepMusicDuringMeme,
        IEnumerable<(double AnchorOutputSec, double DurationSec)>? inlineMemes)
    {
        if (!keepMusicDuringMeme || inlineMemes == null) return outputSec;
        double shift = 0;
        foreach (var (anchor, duration) in inlineMemes)
            if (duration > 0 && anchor >= startDelaySec - 1e-3 && anchor <= outputSec + 1e-3) shift += duration;
        return outputSec + shift;
    }

    public static bool PlaysDuringMeme(bool keepMusicDuringMeme) => keepMusicDuringMeme;
}
