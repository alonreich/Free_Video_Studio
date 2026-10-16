// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.
namespace FreeVideoStudio.Core.Media;

/// <summary>
/// VOSTALL_01: compare the actual output clock with captured PCM, never source seconds.
/// A rendered freeze advances normally. Stalls, seeks and device-clock drift cannot silently
/// accumulate within a take. The tolerance includes mpv's observed-clock and WinMM buffer cadence.
/// </summary>
public sealed class VoiceOverClockGuard
{
    public const double MaximumDriftSec = 0.15;
    public const double StartupGraceSec = 0.3;
    private double _startOutput;
    private double _lastOutput;
    private double _lastGoodCaptured;
    private double _lastGoodOutput;
    public bool IsArmed { get; private set; }
    public double SafeCapturedSeconds => _lastGoodCaptured;
    public double ResumeOutputSeconds => _lastGoodOutput;

    public void Arm(double outputSec)
    {
        _startOutput = _lastOutput = _lastGoodOutput = outputSec;
        _lastGoodCaptured = 0;
        IsArmed = true;
    }

    public void Reset() => IsArmed = false;

    public bool Observe(double outputSec, double capturedSec, bool paused, bool seeking)
    {
        if (!IsArmed) return true;
        if (!double.IsFinite(outputSec) || !double.IsFinite(capturedSec)) return false;
        double elapsed = outputSec - _startOutput;
        bool interrupted = paused || seeking || outputSec < _lastOutput - 0.03 ||
            (capturedSec >= StartupGraceSec && Math.Abs(elapsed - capturedSec) > MaximumDriftSec);
        _lastOutput = outputSec;
        if (interrupted) return false;
        _lastGoodCaptured = Math.Max(0, Math.Min(capturedSec, elapsed));
        _lastGoodOutput = _startOutput + _lastGoodCaptured;
        return true;
    }
}
