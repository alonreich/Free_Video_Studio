using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>All scenario tests run one at a time: real-time playback timing must not compete for the CPU.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvCollection { public const string Name = "AV"; }

/// <summary>One-line verdicts. Every failure message says WHAT the user would see and the numbers.</summary>
internal static class Av
{
    public static void True(string what, bool ok, string? detail = null)
    {
        if (!ok) throw new AvDefect(what + (detail == null ? "" : " — " + detail));
    }

    public static T Single<T>(string what, IReadOnlyList<T> items)
    {
        if (items.Count != 1) throw new AvDefect($"{what}: expected exactly one, found {items.Count}");
        return items[0];
    }

    public static void Near(string what, double expected, double actual, double tolerance)
    {
        if (!double.IsFinite(actual) || !double.IsFinite(expected) || !double.IsFinite(tolerance) || tolerance < 0 || Math.Abs(actual - expected) > tolerance)
            throw new AvDefect($"{what}: expected {expected:0.000}s, got {actual:0.000}s (off {(actual - expected) * 1000:+0;-0}ms, budget ±{tolerance * 1000:0}ms)");
    }

    public static void Latency(string what, double ms, double budget = Budget.SeekLatencyMs)
    {
        if (!double.IsFinite(ms) || ms < 0 || !double.IsFinite(budget) || budget < 0 || ms > budget)
            throw new AvDefect($"{what}: SLOW SEEK — {ms:0}ms (budget {budget:0}ms)");
    }

    /// <summary>
    /// Every expected marker time has an actual one within tolerance (after removing a constant
    /// offset when <paramref name="offset"/> is given) and there are no extras.
    /// </summary>
    public static void Markers(string what, IReadOnlyList<double> expected, IReadOnlyList<double> actual, double tolerance, double offset = 0)
    {
        var unused = actual.ToList();
        foreach (double e in expected)
        {
            double want = e + offset;
            int best = -1; double bestErr = double.MaxValue;
            for (int i = 0; i < unused.Count; i++)
            {
                double err = Math.Abs(unused[i] - want);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            if (best < 0 || bestErr > tolerance)
            {
                string got = best < 0 ? "nothing" : $"{unused[best]:0.000}s";
                throw new AvDefect($"{what}: marker expected at {want:0.000}s, nearest {got}" +
                                   (best < 0 ? "" : $" (DRIFT {(unused[best] - want) * 1000:+0;-0}ms, budget ±{tolerance * 1000:0}ms)") +
                                   $" — {expected.Count} expected, {actual.Count} found");
            }
            unused.RemoveAt(best);
        }
        if (unused.Count > 0)
            throw new AvDefect($"{what}: {unused.Count} unexpected marker(s), first at {unused[0]:0.000}s (something plays that should not)");
    }
}
