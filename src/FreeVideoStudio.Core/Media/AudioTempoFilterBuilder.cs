
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// TEMPO_01 — THE ONE AUDIO TEMPO POLICY (03 FFM-TEMPO). Every export route that changes the speed
/// of clip audio asks this class for its filters: the Main App base-speed path
/// (<see cref="ProcessWorker"/>), every granular chunk (<see cref="GranularSpeedBuilder.Build"/>),
/// Merger plain clips (<see cref="MergerWorker"/>) and Merger effects clips (via
/// <see cref="MergeClipGraph"/> → <see cref="GranularSpeedBuilder.Build"/>). No other file may spell
/// an <c>atempo=</c> or <c>rubberband=</c> filter.
///
/// <para><b>Policy.</b></para>
/// <list type="bullet">
/// <item><b>1.0x</b> — NO filter (AVSYNC_01: <c>atempo=1.0000</c> still runs WSOLA and was measured
/// moving a sharp attack 19.3 ms).</item>
/// <item><b>&lt; 1.0x</b> — <c>rubberband=tempo={s}:transients=mixed</c> when the bundled FFmpeg
/// GENUINELY supports it (probed once, cached); otherwise the atempo chain.</item>
/// <item><b>&gt; 1.0x</b> — the atempo chain, unchanged. No measurement justified a change there.</item>
/// </list>
///
/// <para><b>Why <c>transients=mixed</c> (measured, reference FFmpeg build with the identical option
/// table to the bundled one).</b> The default (<c>crisp</c>) left a steady 440 Hz tone 0.3-0.6 % flat
/// (437.4 Hz at 0.5x) with the off-band floor at only -12 dB; <c>mixed</c> holds it at 440.00 Hz while
/// keeping crisp's attack (10-90 % rise 9-25 ms); <c>smooth</c> is cleanest on tones but smeared a
/// percussive hit to a 59 ms rise at 0.25x — gunshots and footsteps are what game audio is made of.</para>
///
/// <para><b>Duration.</b> Neither filter's own output length is trusted: every caller keeps its
/// existing hard bound (<c>apad,atrim=duration=</c> per granular chunk, CLIPFRAMES_01's
/// <c>apad,atrim=end=</c> on Merger clips) and its SPLICE_01/03 fades, which sit AFTER the tempo
/// filter. Measured unbounded lengths: rubberband exact at every tested rate; atempo up to 1 % short.</para>
///
/// <para><b>Capability.</b> Unknown (never probed — unit tests, gates) means ABSENT, so a graph built
/// before or without a probe is the old, always-valid atempo graph. The probe runs once per FFmpeg
/// binary on the export thread (<see cref="EnsureProbedAsync"/>) and asks two questions: does the
/// filter's option table name <c>tempo</c> and <c>transients</c>/<c>mixed</c> (the exact syntax this
/// class emits), and does a 0.25 s tone actually pass through that exact filter with exit code 0
/// (a listed filter whose library fails to load is not support). Any failure caches ABSENT.</para>
/// </summary>
public static class AudioTempoFilterBuilder
{
    /// <summary>
    /// The Rubber Band options appended to every slowdown filter. Verified against the bundled option table.
    /// AVSYNC_02 — <c>window=short</c>: the standard analysis window smears a burst's onset across
    /// tens of milliseconds and a slow segment's first event lands late. Measured with the bundled
    /// FFmpeg (1 kHz burst, 48 kHz): onset on a segment's first sample +8.1/+8.7/+8.7 ms → +4.2/+4.2/+4.2 ms,
    /// mid-segment onset error -59…-40/-35…-29/-11…-10 ms → -27…-26/-16…-14/-5…-4 ms at 0.25/0.5/0.75x;
    /// a steady 440 Hz tone stays 440.00 Hz at every rate and its off-band floor improves
    /// (-37.3/-37.7/-41.7 dB → -40.1/-40.1/-44.1 dB).
    /// </summary>
    public const string RubberbandOptions = "transients=mixed:window=short";

    /// <summary>Rubber Band and atempo both accept 0.01..100; the chain is clamped to the same range as before.</summary>
    public const double MinTempo = 0.01;
    public const double MaxTempo = 100.0;

    /// <summary>The AVSYNC_01 no-op band around 1.0x.</summary>
    public const double UnityEpsilon = 0.0001;

    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> ProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly AsyncLocal<bool?> ScopedOverride = new();

    private static volatile int _probed = -1;

    /// <summary>The capability the builders currently use (a test scope wins over the probe).</summary>
    public static bool RubberbandAvailable => ScopedOverride.Value ?? (_probed == 1);

    /// <summary>True once a probe has finished in this process.</summary>
    public static bool IsProbed => _probed >= 0;

    /// <summary>
    /// TESTS ONLY — forces the capability for the current async flow (other tests are unaffected).
    /// Dispose to restore.
    /// </summary>
    public static IDisposable OverrideCapability(bool rubberbandAvailable)
    {
        bool? previous = ScopedOverride.Value;
        ScopedOverride.Value = rubberbandAvailable;
        return new Restore(previous);
    }

    private sealed class Restore(bool? previous) : IDisposable
    {
        public void Dispose() => ScopedOverride.Value = previous;
    }

    /// <summary>Which engine the policy picks for a rate (normalised exactly as <see cref="Build(double, bool)"/> does).</summary>
    public static AudioTempoEngine EngineFor(double speed, bool rubberbandAvailable)
    {
        speed = Normalise(speed, log: false);
        if (Math.Abs(speed - 1.0) < UnityEpsilon) return AudioTempoEngine.None;
        if (speed < 1.0 && rubberbandAvailable) return AudioTempoEngine.Rubberband;
        return AudioTempoEngine.Atempo;
    }

    /// <summary>The tempo filters for a rate under the current capability.</summary>
    public static List<string> Build(double speed) => Build(speed, RubberbandAvailable);

    /// <summary>The tempo filters for a rate under an explicit capability. Empty at 1.0x.</summary>
    public static List<string> Build(double speed, bool rubberbandAvailable)
    {
        speed = Normalise(speed, log: true);
        return EngineFor(speed, rubberbandAvailable) switch
        {
            AudioTempoEngine.None => new List<string>(),
            AudioTempoEngine.Rubberband => new List<string>
            {
                $"rubberband=tempo={speed.ToString("F4", CultureInfo.InvariantCulture)}:{RubberbandOptions}"
            },
            _ => BuildAtempoChainCore(speed),
        };
    }

    /// <summary>
    /// The chain as a graph segment: "" at 1.0x (AVSYNC_01 — no dangling comma), otherwise the
    /// filters joined by commas with ONE comma on the requested side.
    /// </summary>
    public static string Segment(double speed, bool leadingComma) => Segment(speed, leadingComma, RubberbandAvailable, null);

    /// <summary>As <see cref="Segment(double, bool)"/>, with the input length that bounds the compensated Rubber Band output.</summary>
    public static string Segment(double speed, bool leadingComma, double? inputDurationSec) =>
        Segment(speed, leadingComma, RubberbandAvailable, inputDurationSec);

    public static string Segment(double speed, bool leadingComma, bool rubberbandAvailable) =>
        Segment(speed, leadingComma, rubberbandAvailable, null);

    public static string Segment(double speed, bool leadingComma, bool rubberbandAvailable, double? inputDurationSec)
    {
        var filters = Build(speed, rubberbandAvailable);
        if (filters.Count == 0) return "";
        if (EngineFor(speed, rubberbandAvailable) == AudioTempoEngine.Rubberband)
            filters = WithStartCompensation(filters, speed, inputDurationSec);
        string joined = string.Join(",", filters);
        return leadingComma ? "," + joined : joined + ",";
    }

    /// <summary>
    /// AVSYNC_04 — Rubber Band's documented start compensation, which FFmpeg's <c>rubberband</c> filter
    /// does not perform (it runs the realtime stretcher and passes its output straight through).
    /// The library asks for <c>getPreferredStartPad()</c> samples of silence before the audio and for
    /// <c>getStartDelay()</c> samples to be dropped from the start of the output; with the short window
    /// at 48 kHz both are 512 for every ratio (Rubber Band 3 API, queried at 0.1-0.99x). Without it,
    /// a slow segment's first event lands +4.2 ms late and events inside it -27…-4 ms early.
    /// Measured with the bundled FFmpeg (1 kHz burst, 48 kHz, 0.25/0.5/0.75x): segment start
    /// +4.2 → +0.06 ms at every rate; inside the segment -27…-26 → +6…+14, -16…-14 → -4.1…-3.7,
    /// -4.8…-4.2 → -2.3…-0.5 ms; 440 Hz stays 440.00 Hz.
    /// The padding makes the stretcher's flushed tail longer by 512/tempo - 512 samples, so when the
    /// caller knows its input length the output is cut to exactly input / tempo, as before.
    /// </summary>
    public const int RubberbandStartPadSamples = 512;
    public const int RubberbandStartDelaySamples = 512;
    private const int TempoSampleRate = 48000;

    private static List<string> WithStartCompensation(List<string> tempo, double speed, double? inputDurationSec)
    {
        double rate = Math.Round(Normalise(speed, log: false), 4);
        string trim = $"atrim=start_sample={RubberbandStartDelaySamples.ToString(CultureInfo.InvariantCulture)}";
        if (inputDurationSec is double d && double.IsFinite(d) && d > 0)
        {
            long outSamples = (long)Math.Round(d * TempoSampleRate / rate);
            trim += $":end_sample={(RubberbandStartDelaySamples + outSamples).ToString(CultureInfo.InvariantCulture)}";
        }
        var list = new List<string> { $"adelay=delays={RubberbandStartPadSamples.ToString(CultureInfo.InvariantCulture)}S:all=1" };
        list.AddRange(tempo);
        list.AddRange(["asetpts=PTS-STARTPTS", trim, "asetpts=PTS-STARTPTS"]);
        return list;
    }

    /// <summary>
    /// The atempo-only fallback chain (ISSUE_04 guard included). Kept public for the gates that
    /// verify atempo's [0.5, 2.0] element bounds; export routes call <see cref="Build(double)"/>.
    /// </summary>
    public static List<string> BuildAtempoChain(double speed) => BuildAtempoChainCore(Normalise(speed, log: true));

    /// <summary>
    /// ISSUE_04 — zero, negative or non-finite rates spun the old normalisation loops forever
    /// (0/0.5 is 0 forever) and froze the app on PROCESS. Fall back to 1.0x, loudly; then clamp.
    /// </summary>
    private static double Normalise(double speed, bool log)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0.0)
        {
            if (log)
                CoreLogger.Fail("GranularSpeed",
                    $"Refusing to build an audio speed chain for an impossible rate ({speed}). " +
                    "Falling back to normal speed (1.0x) so the export can still complete.");
            return 1.0;
        }
        return Math.Clamp(speed, MinTempo, MaxTempo);
    }

    private static List<string> BuildAtempoChainCore(double speed)
    {
        var filters = new List<string>();

        if (Math.Abs(speed - 1.0) < UnityEpsilon) return filters;

        double tmp = speed;
        while (tmp < 0.5) { filters.Add("atempo=0.5"); tmp /= 0.5; }
        while (tmp > 2.0) { filters.Add("atempo=2.0"); tmp /= 2.0; }
        filters.Add($"atempo={tmp.ToString("F4", CultureInfo.InvariantCulture)}");
        return filters;
    }


    /// <summary>
    /// Probes <paramref name="ffmpegPath"/> once (cached per path for the process lifetime) and
    /// publishes the result to <see cref="RubberbandAvailable"/>. Never throws; failure = absent.
    /// Call on the export thread BEFORE any graph is built.
    /// </summary>
    public static async Task<bool> EnsureProbedAsync(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            Publish(false);
            return false;
        }

        var lazy = ProbeCache.GetOrAdd(ffmpegPath, p => new Lazy<Task<bool>>(() => ProbeAsync(p)));
        bool ok;
        try { ok = await lazy.Value.ConfigureAwait(false); }
        catch (Exception ex)
        {
            CoreLogger.Fail("TEMPO", $"Rubber Band probe faulted ({ex.Message}); using atempo.");
            ok = false;
        }
        Publish(ok);
        return ok;
    }

    private static void Publish(bool ok) => _probed = ok ? 1 : 0;

    /// <summary>
    /// Pure parse of <c>ffmpeg -h filter=rubberband</c>: the filter must exist and its option table
    /// must carry every name this class emits.
    /// </summary>
    public static bool HelpListsRequiredOptions(string helpText)
    {
        if (string.IsNullOrEmpty(helpText)) return false;
        if (helpText.Contains("Unknown filter", StringComparison.OrdinalIgnoreCase)) return false;
        if (!helpText.Contains("rubberband AVOptions", StringComparison.Ordinal)) return false;

        bool tempo = false, transients = false, mixed = false, window = false, shortWindow = false;
        foreach (string raw in helpText.Split('\n'))
        {
            string[] t = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            if (t[0] == "tempo") tempo = true;
            else if (t[0] == "transients") transients = true;
            else if (t[0] == "mixed") mixed = true;
            else if (t[0] == "window") window = true;
            else if (t[0] == "short") shortWindow = true;
        }
        return tempo && transients && mixed && window && shortWindow;
    }

    private static async Task<bool> ProbeAsync(string ffmpegPath)
    {
        try
        {
            var help = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            help.ArgumentList.Add("-hide_banner");
            help.ArgumentList.Add("-h");
            help.ArgumentList.Add("filter=rubberband");
            var listed = await AsyncProcessRunner.RunAsync(help, timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!HelpListsRequiredOptions(listed.StandardOutput + "\n" + listed.StandardError))
            {
                CoreLogger.Info("TEMPO", "Bundled FFmpeg has no usable rubberband filter; slowdowns use atempo.");
                return false;
            }

            var trial = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[]
                     {
                         "-hide_banner", "-nostdin", "-loglevel", "error",
                         "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=0.25",
                         "-af", Build(0.5, rubberbandAvailable: true)[0],
                         "-f", "null", "-",
                     })
                trial.ArgumentList.Add(a);
            var run = await AsyncProcessRunner.RunAsync(trial, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                CoreLogger.Fail("TEMPO",
                    $"rubberband is listed but a trial run failed (exit {run.ExitCode}): {run.StandardError.Trim()}. Slowdowns use atempo.");
                return false;
            }

            CoreLogger.Info("TEMPO", $"Rubber Band verified ({RubberbandOptions}); slowdowns below 1.0x use it.");
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("TEMPO", $"Rubber Band probe could not run ({ex.Message}); slowdowns use atempo.");
            return false;
        }
    }
}

/// <summary>The engine <see cref="AudioTempoFilterBuilder"/> chose for a rate.</summary>
public enum AudioTempoEngine
{
    None,
    Atempo,
    Rubberband,
}
