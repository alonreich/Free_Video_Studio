using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FreeVideoStudio.Core.Media;

public static class MemeMusicStop
{
    public const double RampSec = 0.08;

    private static string F(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    public static string? Pulse(IEnumerable<(double Start, double End)> windows)
    {
        var parts = windows.Where(w => w.End > w.Start + 1e-3)
            .Select(w => $"clip((t-({F(w.Start)}-{F(RampSec)}))/{F(RampSec)},0,1)*clip((({F(w.End)}+{F(RampSec)})-t)/{F(RampSec)},0,1)")
            .ToList();
        return parts.Count == 0 ? null : $"clip({string.Join("+", parts)},0,1)";
    }

    public static double PulseAt(double t, IEnumerable<(double Start, double End)> windows)
    {
        double sum = 0;
        foreach (var (s, e) in windows)
        {
            if (e <= s + 1e-3) continue;
            sum += System.Math.Clamp((t - (s - RampSec)) / RampSec, 0, 1) * System.Math.Clamp((e + RampSec - t) / RampSec, 0, 1);
        }
        return System.Math.Clamp(sum, 0, 1);
    }

    public static string Filter(string pulse) => $"volume='1-({pulse})':eval=frame";
}
