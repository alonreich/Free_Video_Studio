using System.Globalization;

namespace FreeVideoStudio.Core.Media;

public sealed class SpeedSchedule
{
    private readonly SpeedSegment[] _segments;

    public SpeedSchedule(IEnumerable<SpeedSegment>? segments, double baseSpeed, double segmentOffsetMs = 0)
    {
        _segments = segments?.ToArray() ?? [];
        BaseSpeed = baseSpeed;
        SegmentOffsetMs = segmentOffsetMs;
    }

    public double BaseSpeed { get; }

    public double SegmentOffsetMs { get; }

    public double SpeedAtAbsoluteMs(double absoluteMs)
    {
        double rel = absoluteMs - SegmentOffsetMs;
        foreach (var seg in _segments)
            if (rel >= seg.StartMs && rel < seg.EndMs) return seg.Speed;
        return BaseSpeed;
    }

    public bool SameAs(IReadOnlyList<SpeedSegment> segments, double baseSpeed, double segmentOffsetMs)
    {
        if (BaseSpeed != baseSpeed || SegmentOffsetMs != segmentOffsetMs || _segments.Length != segments.Count) return false;
        for (int i = 0; i < _segments.Length; i++)
            if (!_segments[i].Equals(segments[i])) return false;
        return true;
    }
}

public sealed class PreviewSpeedGovernor : IDisposable
{
    private readonly object _gate = new();
    private double _sent = double.NaN;
    private MpvIpcClient? _followed;
    private volatile SpeedSchedule? _schedule;

    public double LastSent { get { lock (_gate) return _sent; } }

    public int SendCount { get; private set; }

    public Func<bool>? IsSuspended { get; set; }

    public SpeedSchedule? Schedule => _schedule;

    public void Invalidate() { lock (_gate) _sent = double.NaN; }

    public string? Next(double targetSpeed)
    {
        if (!double.IsFinite(targetSpeed)) return null;
        double clamped = Math.Max(0.01, targetSpeed);
        lock (_gate)
        {
            if (!double.IsNaN(_sent) && Math.Abs(clamped - _sent) <= 0.0005) return null;
            _sent = clamped;
            SendCount++;
        }
        return Format(clamped);
    }

    public static string Format(double speed) =>
        Math.Max(0.01, speed).ToString("0.0###", CultureInfo.InvariantCulture);

    public Task<MpvCommandResult>? Apply(MpvIpcClient? ipc, double targetSpeed)
    {
        if (ipc == null || IsSuspended?.Invoke() == true) return null;
        string? value = Next(targetSpeed);
        return value == null ? null : ipc.SetPropertyAsync("speed", value);
    }

    public Task<MpvCommandResult>? Force(MpvIpcClient? ipc, double targetSpeed)
    {
        Invalidate();
        if (ipc == null || ipc.IsPaused || ipc.Duration <= 0) return null;
        return Apply(ipc, targetSpeed);
    }

    public void UseSchedule(IReadOnlyList<SpeedSegment> segments, double baseSpeed, double segmentOffsetMs = 0)
    {
        var current = _schedule;
        if (current != null && current.SameAs(segments, baseSpeed, segmentOffsetMs)) return;
        _schedule = new SpeedSchedule(segments, baseSpeed, segmentOffsetMs);
    }

    public double SpeedAtAbsoluteMs(double absoluteMs) => _schedule?.SpeedAtAbsoluteMs(absoluteMs) ?? double.NaN;

    public const double DefaultSwitchLeadSec = 0.27;

    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();

    public double SwitchLeadSec { get; set; } = DefaultSwitchLeadSec;

    public const double ServoGainPerSec = 1.2;
    public const double ServoMaxCorrection = 0.10;
    public const double ServoStep = 0.01;
    public const double ServoResetSec = 0.40;
    public const double SwitchSettleSec = 0.50;

    private double _lastScheduled = double.NaN;
    private double _settleUntilSec = double.NegativeInfinity;

    public bool InSwitchSettle { get { lock (_gate) return _wall.Elapsed.TotalSeconds < _settleUntilSec; } }

    private double _servo = 1.0;
    private bool _anchored;
    private double _anchorOutSec, _anchorWallSec;

    public double ServoFactor { get { lock (_gate) return _servo; } }

    public double PictureDriftSec { get; private set; }

    public void ResetServo() { lock (_gate) { _anchored = false; _servo = 1.0; PictureDriftSec = 0; } }

    public void ObserveOutput(double outputSec, bool playing)
    {
        lock (_gate)
        {
            double now = _wall.Elapsed.TotalSeconds;
            if (!playing || !double.IsFinite(outputSec)) { _anchored = false; _servo = 1.0; PictureDriftSec = 0; return; }
            if (!_anchored) { _anchored = true; _anchorOutSec = outputSec; _anchorWallSec = now; _servo = 1.0; PictureDriftSec = 0; return; }
            double drift = outputSec - (_anchorOutSec + (now - _anchorWallSec));
            if (Math.Abs(drift) > ServoResetSec) { _anchorOutSec = outputSec; _anchorWallSec = now; _servo = 1.0; PictureDriftSec = 0; return; }
            PictureDriftSec = drift;
            double f = 1.0 - Math.Clamp(ServoGainPerSec * drift, -ServoMaxCorrection, ServoMaxCorrection);
            _servo = Math.Round(f / ServoStep) * ServoStep;
        }
    }

    public double ScheduledSpeedAt(double playheadSec)
    {
        var schedule = _schedule;
        if (schedule == null || !double.IsFinite(playheadSec)) return double.NaN;
        double currentSpeed = schedule.SpeedAtAbsoluteMs(playheadSec * 1000.0);
        double now = double.IsFinite(currentSpeed) && currentSpeed > 0 ? currentSpeed : LastSent;
        if (double.IsNaN(now)) return currentSpeed;
        double aheadMs = (playheadSec + Math.Max(0, SwitchLeadSec) * Math.Max(0.01, now)) * 1000.0;
        double scheduled = schedule.SpeedAtAbsoluteMs(aheadMs);
        lock (_gate)
        {
            if (double.IsFinite(_lastScheduled) && Math.Abs(scheduled - _lastScheduled) >= 0.25)
                _settleUntilSec = _wall.Elapsed.TotalSeconds + SwitchLeadSec + SwitchSettleSec;
            _lastScheduled = scheduled;
        }
        return scheduled * ServoFactor;
    }

    public Task<MpvCommandResult>? ApplyAt(MpvIpcClient? ipc, double playheadSec, bool playing = true, double outputSec = double.NaN)
    {
        if (ipc == null) return null;
        if (IsSuspended?.Invoke() == true) { ResetServo(); return null; }
        if (!double.IsNaN(outputSec) || !playing) ObserveOutput(outputSec, playing);
        if (!playing) return null;
        double target = ScheduledSpeedAt(playheadSec);
        if (double.IsNaN(target)) return null;
        string? value = Next(target);
        return value == null ? null : ipc.SetPropertyAsync("speed", value);
    }

    public void Follow(MpvIpcClient? ipc)
    {
        if (ReferenceEquals(ipc, _followed)) return;
        if (_followed != null) _followed.TimePosChanged -= OnTimePos;
        _followed = ipc;
        Invalidate();
        if (ipc != null) ipc.TimePosChanged += OnTimePos;
    }

    private void OnTimePos(double timeSec)
    {
        var ipc = _followed;
        var schedule = _schedule;
        if (ipc == null || schedule == null) return;
        try { _ = ApplyAt(ipc, timeSec, !ipc.IsPaused && !ipc.IsSeeking); }
        catch (ObjectDisposedException ex) { FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(ex); }
    }

    public void Dispose() => Follow(null);
}
