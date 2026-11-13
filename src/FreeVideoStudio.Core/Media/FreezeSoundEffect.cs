using System.Globalization;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// FREEZESFX_01 (docs/02 AUD-FREEZESFX) — the movie-style "slow motion" sound of a freeze, and the
/// music dip that makes room for it.
///
/// <para><b>The sound.</b> A held frame used to be pure silence (<c>anullsrc</c>). It is now a deep,
/// falling bass hit — the "car-crash slow-motion" boom of film trailers — synthesised by FFmpeg itself
/// (<c>aevalsrc</c>), so there is no sound file to ship, license or lose: a sub-bass sine that glides
/// from <see cref="StartHz"/> down to <see cref="EndHz"/> over the whole hold, with a short noise
/// "impact" at the start, a slow decay that keeps a low rumble under the entire freeze, and a soft
/// fade at its end. A low-pass keeps it a felt boom rather than a beep. It replaces the silence in
/// the ONE graph compiler (<see cref="GranularSpeedBuilder"/>), so every path that renders a freeze —
/// export, the rendered preview mix, the voice-over picture — carries the same sound.</para>
///
/// <para><b>The music dip.</b> While a freeze plays, the music glides down to
/// <c>1 - </c><see cref="DipDepth"/> of its level (starting <see cref="DipLeadSec"/> before the hold,
/// over <see cref="DipInSec"/>), stays there for the hold, and fades back to normal over
/// <see cref="DipOutSec"/> after it, so the boom stands out and the music returns gently.</para>
/// </summary>
public static class FreezeSoundEffect
{
    /// <summary>Starting pitch of the falling bass, in Hz.</summary>
    public const double StartHz = 68.0;

    /// <summary>Pitch the bass has fallen to at the end of the hold, in Hz.</summary>
    public const double EndHz = 32.0;

    /// <summary>Peak level of the bass (linear, before the video volume fader and the safety limiter).</summary>
    public const double BassLevel = 0.55;

    /// <summary>Level of the short noise "impact" at the start of the hold.</summary>
    public const double ImpactLevel = 0.18;

    /// <summary>Fraction of the music level REMOVED while a freeze plays (0.65 = music at 35%).</summary>
    public const double DipDepth = 0.65;

    /// <summary>The music starts dipping this long before the hold begins.</summary>
    public const double DipLeadSec = 0.15;

    /// <summary>How long the music takes to glide down.</summary>
    public const double DipInSec = 0.5;

    /// <summary>How long the music takes to come back after the hold.</summary>
    public const double DipOutSec = 1.2;

    private static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>
    /// The FFmpeg expression of the sound, sample by sample, for a hold of <paramref name="durationSec"/>
    /// (t = seconds since the hold began). Public for tests.
    /// </summary>
    public static string Expression(double durationSec)
    {
        double d = Math.Max(0.05, durationSec);
        double tail = Math.Min(0.3, d / 2.0);
        string bass =
            $"{F(BassLevel)}*sin(2*PI*({F(StartHz)}*t-{F(StartHz - EndHz)}*t*t/{F(2 * d)}))" +
            $"*min(1,t/0.03)*(0.3+0.7*exp(-2.2*t))";
        string impact = $"{F(ImpactLevel)}*(2*random(0)-1)*exp(-9*t)*min(1,t/0.004)";
        return $"({bass}+{impact})*max(0,min(1,({F(d)}-t)/{F(tail)}))";
    }

    /// <summary>
    /// One filtergraph chain producing the sound as a 48 kHz stereo stream of exactly
    /// <paramref name="durationSec"/> seconds, labelled <paramref name="outLabel"/> (e.g. "[a_c3]").
    /// Drop-in replacement for the <c>anullsrc</c> a freeze used to produce.
    /// </summary>
    public static string Source(double durationSec, string outLabel)
    {
        string dur = durationSec.ToString("F5", CultureInfo.InvariantCulture);
        return $"aevalsrc=exprs='{Expression(durationSec)}':s=48000:c=stereo:d={dur}," +
               $"lowpass=f=220,aformat=sample_rates=48000:channel_layouts=stereo," +
               $"atrim=duration={dur},asetpts=PTS-STARTPTS{outLabel}";
    }

    /// <summary>
    /// An FFmpeg expression in t (seconds on the MUSIC's clock) that is 1 while a freeze plays, ramps in
    /// before it and out after it, and 0 elsewhere. Null when there is no freeze.
    /// </summary>
    public static string? MusicDipPulse(IEnumerable<(double StartSec, double EndSec)> windows)
    {
        var parts = new List<string>();
        foreach (var (s, e) in windows)
        {
            if (e <= s + 1e-3) continue;
            double rampStart = s - DipLeadSec;
            parts.Add($"clip((t-{F(rampStart)})/{F(DipInSec)},0,1)*clip(({F(e + DipOutSec)}-t)/{F(DipOutSec)},0,1)");
        }
        return parts.Count == 0 ? null : $"clip({string.Join("+", parts)},0,1)";
    }

    /// <summary>
    /// FREEZESFX_02 — the SAME sound rendered in-process (mono float samples, -1..1) for previews that do
    /// not go through FFmpeg (the Speed Editor plays a freeze by pausing its player). Same formula as
    /// <see cref="Expression"/> and the same 2-pole Butterworth low-pass (RBJ biquad, Q = 0.7071, the
    /// FFmpeg <c>lowpass</c> filter's default); only the noise impact's random sequence differs.
    /// </summary>
    public static float[] Render(double durationSec, int sampleRate = 48000)
    {
        double d = Math.Max(0.05, durationSec);
        double tail = Math.Min(0.3, d / 2.0);
        int n = Math.Max(1, (int)Math.Round(d * sampleRate));
        var samples = new float[n];

        double w0 = 2 * Math.PI * 220.0 / sampleRate;
        double alpha = Math.Sin(w0) / (2 * 0.70710678118654752);
        double cos = Math.Cos(w0);
        double a0 = 1 + alpha;
        double b0 = (1 - cos) / 2 / a0, b1 = (1 - cos) / a0, b2 = (1 - cos) / 2 / a0;
        double a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;

        var rng = new Random(20261112);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double bass = BassLevel * Math.Sin(2 * Math.PI * (StartHz * t - (StartHz - EndHz) * t * t / (2 * d)))
                          * Math.Min(1, t / 0.03) * (0.3 + 0.7 * Math.Exp(-2.2 * t));
            double impact = ImpactLevel * (2 * rng.NextDouble() - 1) * Math.Exp(-9 * t) * Math.Min(1, t / 0.004);
            double x = (bass + impact) * Math.Max(0, Math.Min(1, (d - t) / tail));
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            samples[i] = (float)Math.Clamp(y, -1.0, 1.0);
        }
        return samples;
    }

    /// <summary>The volume filter that applies <paramref name="pulse"/> to the music bed.</summary>
    public static string MusicDipFilter(string pulse) =>
        $"volume='1-{F(DipDepth)}*({pulse})':eval=frame";
}
