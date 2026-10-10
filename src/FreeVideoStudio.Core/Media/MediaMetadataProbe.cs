// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Diagnostics;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media.Native;

namespace FreeVideoStudio.Core.Media;

/// <summary>Which metadata backend <see cref="MediaMetadataProbe"/> may use.</summary>
public enum MediaProbeBackend
{
    /// <summary>Native libav first; ffprobe subprocess once if native cannot answer. The default.</summary>
    Auto,
    /// <summary>Native libav only (tests, benchmarks). No subprocess.</summary>
    Native,
    /// <summary>ffprobe subprocess only — the pre-LIBAVPROBE behaviour, kept as the explicit fallback.</summary>
    Ffprobe,
}

/// <summary>
/// One metadata answer. <see cref="Data"/> is ffprobe <c>-show_format -show_streams</c> JSON (the
/// native backend emits the same keys) or null when no backend could read the file.
/// <see cref="ExitCode"/> is the ffprobe exit code when the subprocess ran, else 0 / -1.
/// </summary>
public sealed record MediaProbeResult(JsonObject? Data, string Backend, int ExitCode, string? Error)
{
    public bool Ok => Data != null;
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LIBAVPROBE_03 — THE ONE ROUTER FOR MEDIA METADATA, AND ITS FALLBACK RULE.
///
/// <para>
/// MediaProber and OutputSizeEstimator ask here instead of starting ffprobe themselves. The answer
/// is the same JSON shape either way, so every accessor kept its existing selection rule
/// unchanged (first video stream, any audio stream, format duration then video duration, …).
/// </para>
///
/// <para>
/// FALLBACK — explicit, bounded, logged, testable, one direction only:
///   • native Unavailable (DLLs missing, version/layout guard) → ffprobe. Latched by the backend;
///     logged once by LibAvLibrary.
///   • native MediaError (libav rejected the file) → ffprobe ONCE, with the remaining time budget,
///     reported as a Recoverable fault (log-only: the user's outcome is whatever ffprobe says, i.e.
///     exactly the pre-native outcome). ffprobe uses the same libraries, so it normally fails too —
///     that is fine and costs what every probe cost before.
///   • cancellation → re-thrown, never a fault, never a fallback.
///   • native timeout → OperationCanceledException (as AsyncProcessRunner's timeout always threw);
///     no fallback, because the budget is spent.
///   • ffprobe never falls back to native. There is no loop.
/// <see cref="MediaProbeBackend.Ffprobe"/> (or env <c>FVS_MEDIA_PROBE=ffprobe</c>) forces the old path.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MediaMetadataProbe
{
    /// <summary>Environment override: <c>ffprobe</c> or <c>native</c>; anything else = Auto.</summary>
    public const string BackendEnvironmentVariable = "FVS_MEDIA_PROBE";

    /// <summary>The backend Auto resolves to for this process (environment override honoured).</summary>
    public static MediaProbeBackend DefaultBackend
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable(BackendEnvironmentVariable);
            if (string.Equals(v, "ffprobe", StringComparison.OrdinalIgnoreCase)) return MediaProbeBackend.Ffprobe;
            if (string.Equals(v, "native", StringComparison.OrdinalIgnoreCase)) return MediaProbeBackend.Native;
            return MediaProbeBackend.Auto;
        }
    }

    private static readonly AsyncLocal<INativeMediaBackend?> NativeOverrideSlot = new();

    /// <summary>
    /// Test seam: replaces the native backend for the CURRENT async flow only (AsyncLocal), so a
    /// fault-injection test cannot leak into a parallel test. Null = <see cref="LibAvMediaBackend.Instance"/>.
    /// </summary>
    internal static INativeMediaBackend? NativeOverride
    {
        get => NativeOverrideSlot.Value;
        set => NativeOverrideSlot.Value = value;
    }

    private static long _fallbacks;
    /// <summary>Probes answered by ffprobe after native could not (process lifetime). Diagnostics/tests.</summary>
    public static long FallbackCount => Interlocked.Read(ref _fallbacks);

    public static async Task<MediaProbeResult> ProbeAsync(
        string ffprobePath, string mediaPath, TimeSpan timeout,
        CancellationToken cancellationToken = default, MediaProbeBackend backend = MediaProbeBackend.Auto)
    {
        if (backend == MediaProbeBackend.Auto) backend = DefaultBackend;
        if (backend == MediaProbeBackend.Ffprobe)
            return await RunFfprobeAsync(ffprobePath, mediaPath, timeout, cancellationToken).ConfigureAwait(false);

        var started = Stopwatch.StartNew();
        INativeMediaBackend native = NativeOverride ?? LibAvMediaBackend.Instance;
        NativeProbeResult result = await native.ProbeAsync(ffprobePath, mediaPath, timeout, cancellationToken).ConfigureAwait(false);
        if (result.Status == NativeProbeStatus.Ok)
            return new MediaProbeResult(result.Data, "libav", 0, null);

        if (backend == MediaProbeBackend.Native)
            return new MediaProbeResult(null, "libav", -1, result.Detail);

        // ── Auto: exactly one fallback to the subprocess ───────────────────────────────────────
        Interlocked.Increment(ref _fallbacks);
        if (result.Status == NativeProbeStatus.MediaError)
            Faults.Recoverable("LIBAV-FALLBACK", $"{Path.GetFileName(mediaPath)}: native probe failed ({result.Detail}); retrying once with ffprobe.");

        TimeSpan remaining = timeout - started.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new OperationCanceledException("Media probe budget exhausted before the ffprobe fallback.");
        return await RunFfprobeAsync(ffprobePath, mediaPath, remaining, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pre-LIBAVPROBE subprocess path, unchanged: ffprobe -v quiet -print_format json -show_format -show_streams.</summary>
    internal static async Task<MediaProbeResult> RunFfprobeAsync(string ffprobePath, string mediaPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var probeArgs = new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format", "-show_streams",
            mediaPath
        };

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in probeArgs) psi.ArgumentList.Add(arg);

        CoreLogger.Debug("FFprobe", $"Command: {ffprobePath} {ProcessArgs.FormatForLog(probeArgs)}");

        var run = await AsyncProcessRunner.RunAsync(psi, timeout, cancellationToken).ConfigureAwait(false);
        if (run.ExitCode == 0 && !string.IsNullOrWhiteSpace(run.StandardOutput))
            return new MediaProbeResult(JsonNode.Parse(run.StandardOutput)?.AsObject() ?? new JsonObject(), "ffprobe", 0, null);
        return new MediaProbeResult(null, "ffprobe", run.ExitCode, $"Exit code {run.ExitCode}. Stderr: {run.StandardError}");
    }
}
