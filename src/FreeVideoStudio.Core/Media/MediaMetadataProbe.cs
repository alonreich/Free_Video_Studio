using System.Diagnostics;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media.Native;

namespace FreeVideoStudio.Core.Media;

public enum MediaProbeBackend
{
    Auto,
    Native,
    Ffprobe,
}

public sealed record MediaProbeResult(JsonObject? Data, string Backend, int ExitCode, string? Error)
{
    public bool Ok => Data != null;
}

public static class MediaMetadataProbe
{
    public const string BackendEnvironmentVariable = "FVS_MEDIA_PROBE";

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

    internal static INativeMediaBackend? NativeOverride
    {
        get => NativeOverrideSlot.Value;
        set => NativeOverrideSlot.Value = value;
    }

    private static long _fallbacks;
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

        Interlocked.Increment(ref _fallbacks);
        if (result.Status == NativeProbeStatus.MediaError)
            Faults.Recoverable("LIBAV-FALLBACK", $"{Path.GetFileName(mediaPath)}: native probe failed ({result.Detail}); retrying once with ffprobe.");

        TimeSpan remaining = timeout - started.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new OperationCanceledException("Media probe budget exhausted before the ffprobe fallback.");
        return await RunFfprobeAsync(ffprobePath, mediaPath, remaining, cancellationToken).ConfigureAwait(false);
    }

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
