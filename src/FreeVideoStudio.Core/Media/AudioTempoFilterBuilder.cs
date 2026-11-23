using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public static class AudioTempoFilterBuilder
{
    public const string RubberbandOptions = "transients=mixed:window=short";

    public const double MinTempo = 0.01;
    public const double MaxTempo = 100.0;

    public const double UnityEpsilon = 0.0001;

    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> ProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly AsyncLocal<bool?> ScopedOverride = new();

    private static volatile int _probed = -1;

    public static bool RubberbandAvailable => ScopedOverride.Value ?? (_probed == 1);

    public static bool IsProbed => _probed >= 0;

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

    public static AudioTempoEngine EngineFor(double speed, bool rubberbandAvailable)
    {
        speed = Normalise(speed, log: false);
        if (Math.Abs(speed - 1.0) < UnityEpsilon) return AudioTempoEngine.None;
        if (speed < 1.0 && rubberbandAvailable) return AudioTempoEngine.Rubberband;
        return AudioTempoEngine.Atempo;
    }

    public static List<string> Build(double speed) => Build(speed, RubberbandAvailable);

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

    public static string Segment(double speed, bool leadingComma) => Segment(speed, leadingComma, RubberbandAvailable, null);

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

    public static List<string> BuildAtempoChain(double speed) => BuildAtempoChainCore(Normalise(speed, log: true));

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

public enum AudioTempoEngine
{
    None,
    Atempo,
    Rubberband,
}
