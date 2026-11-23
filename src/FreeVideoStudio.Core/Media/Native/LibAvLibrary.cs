using System.Runtime.InteropServices;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media.Native;

internal static class LibAvLibrary
{
    internal const string AvFormat = "avformat-62";
    internal const string AvCodec = "avcodec-62";
    internal const string AvUtil = "avutil-60";
    internal const string SwResample = "swresample-6";
    internal const string SwScale = "swscale-9";

    internal static readonly string[] LoadOrder = { AvUtil, SwResample, AvCodec, AvFormat };

    private sealed record LoadState(bool Ok, string Detail, string? Directory, IReadOnlyDictionary<string, nint>? Modules = null);

    private static readonly object Gate = new();
    private static LoadState? _state;

    public static bool TryEnsureLoaded(string? ffprobePath, out string detail)
    {
        var state = Volatile.Read(ref _state);
        if (state == null)
        {
            lock (Gate)
            {
                state = _state;
                if (state == null)
                {
                    state = Load(ffprobePath);
                    Volatile.Write(ref _state, state);
                    if (state.Ok) CoreLogger.Info("LibAv", state.Detail);
                    else CoreLogger.Warn("LibAv", $"Native media backend unavailable, ffprobe will be used: {state.Detail}");
                }
            }
        }
        detail = state.Detail;
        return state.Ok;
    }

    internal static IEnumerable<string> CandidateDirectories(string? ffprobePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ffprobePath) && Path.IsPathFullyQualified(ffprobePath)
            && Path.GetDirectoryName(ffprobePath) is { Length: > 0 } probeDir && seen.Add(probeDir))
            yield return probeDir;

        foreach (string? baseDir in new[] { Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            foreach (string dir in new[] { "backend", "binaries" })
            {
                foreach (string candidate in new[]
                         {
                             Path.GetFullPath(Path.Combine(baseDir, dir)),
                             Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", dir)),
                         })
                {
                    if (seen.Add(candidate)) yield return candidate;
                }
            }
        }
    }

    private static LoadState Load(string? ffprobePath)
    {
        if (!OperatingSystem.IsWindows())
            return new LoadState(false, "native libav backend is Windows-only (bundled DLLs are Win64).", null);

        string? dir = CandidateDirectories(ffprobePath)
            .FirstOrDefault(d => LoadOrder.All(n => File.Exists(Path.Combine(d, n + ".dll"))));
        if (dir == null)
            return new LoadState(false, $"no directory holds {string.Join(", ", LoadOrder.Select(n => n + ".dll"))}.", null);

        var handles = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in LoadOrder)
        {
            string full = Path.Combine(dir, name + ".dll");
            if (!NativeLibrary.TryLoad(full, out nint h))
            {
                foreach (nint loaded in handles.Values) NativeLibrary.Free(loaded);
                return new LoadState(false, $"LoadLibrary failed for {full}.", null);
            }
            handles[name] = h;
        }
        try
        {
            LibAv.Bind(handles);
        }
        catch (EntryPointNotFoundException ex)
        {
            CoreLogger.Swallowed(ex);
            return new LoadState(false, $"missing export in {dir}: {ex.Message}", null);
        }

        uint fmt = LibAv.avformat_version(), codec = LibAv.avcodec_version(), util = LibAv.avutil_version();
        string versions = $"avformat {Describe(fmt)}, avcodec {Describe(codec)}, avutil {Describe(util)}";
        if (LibAvConstants.MajorMinor(fmt) != LibAvConstants.ExpectedAvFormat
            || LibAvConstants.MajorMinor(codec) != LibAvConstants.ExpectedAvCodec
            || LibAvConstants.MajorMinor(util) != LibAvConstants.ExpectedAvUtil)
        {
            return new LoadState(false, $"version mismatch ({versions}); struct layout pinned to avformat 62.0, avcodec 62.0, avutil 60.1.", null);
        }

        LibAv.av_log_set_level(LibAvConstants.AV_LOG_QUIET);
        return new LoadState(true, $"native media backend loaded from {dir} ({versions}).", dir, handles);
    }

    private static LoadState? _frameState;

    public static bool TryEnsureFrameTierLoaded(string? ffprobeOrFfmpegPath, out string detail)
    {
        if (!TryEnsureLoaded(ffprobeOrFfmpegPath, out detail)) return false;
        var state = Volatile.Read(ref _frameState);
        if (state == null)
        {
            lock (Gate)
            {
                state = _frameState;
                if (state == null)
                {
                    state = LoadFrameTier(Volatile.Read(ref _state)!);
                    Volatile.Write(ref _frameState, state);
                    if (state.Ok) CoreLogger.Info("LibAv", state.Detail);
                    else CoreLogger.Warn("LibAv", $"Native frame decoding unavailable, ffmpeg will be used: {state.Detail}");
                }
            }
        }
        detail = state.Detail;
        return state.Ok;
    }

    private static LoadState LoadFrameTier(LoadState core)
    {
        if (core.Directory == null || core.Modules == null)
            return new LoadState(false, "metadata tier not loaded.", null);
        string full = Path.Combine(core.Directory, SwScale + ".dll");
        if (!File.Exists(full))
            return new LoadState(false, $"{full} is missing.", null);
        if (!NativeLibrary.TryLoad(full, out nint sws))
            return new LoadState(false, $"LoadLibrary failed for {full}.", null);

        var modules = new Dictionary<string, nint>(core.Modules, StringComparer.OrdinalIgnoreCase) { [SwScale] = sws };
        try
        {
            LibAvFrame.Bind(modules);
        }
        catch (EntryPointNotFoundException ex)
        {
            CoreLogger.Swallowed(ex);
            return new LoadState(false, $"missing export in {core.Directory}: {ex.Message}", null);
        }

        uint swsVersion = LibAvFrame.swscale_version();
        if (LibAvConstants.MajorMinor(swsVersion) != LibAvConstants.ExpectedSwScale)
            return new LoadState(false, $"version mismatch (swscale {Describe(swsVersion)}); frame tier pinned to swscale 9.0.", null);

        return new LoadState(true, $"native frame decoding loaded from {core.Directory} (swscale {Describe(swsVersion)}).", core.Directory, modules);
    }

    private static string Describe(uint v) => $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";
}
