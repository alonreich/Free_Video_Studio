// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Runtime.InteropServices;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media.Native;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LIBAVPROBE_02 — DETERMINISTIC LIBAV LOADING. NEVER THE USER'S PATH.
///
/// <para>
/// The libraries are loaded ONCE per process, by ABSOLUTE PATH, from the first directory that
/// holds all four of avutil-60, swresample-6, avcodec-62 and avformat-62:
///   1. the directory of the ffprobe.exe the caller resolved (FFM-BINPATH already chose it, so the
///      native probe reads media with exactly the libraries ffprobe would have used);
///   2. <c>backend\</c>, <c>..\..\..\..\..\backend\</c>, <c>binaries\</c>, <c>..\..\..\..\..\binaries\</c>
///      relative to the exe (the FFM-BINPATH order: installed layout first, dev tree second).
/// </para>
///
/// <para>
/// ⚠️ DEPENDENTS. Windows resolves avformat's imports (avcodec-62.dll, avutil-60.dll) by MODULE NAME,
/// and a name that is already loaded in the process wins over any search path. Loading in strict
/// dependency order — avutil → swresample → avcodec → avformat — therefore makes every dependent
/// bind to the copy we chose, regardless of PATH, the current directory or SetDllDirectory.
/// </para>
///
/// <para>
/// The entry points in <see cref="LibAv"/> are bound with NativeLibrary.GetExport against THOSE
/// handles (no DllImportResolver — see LibAv for why). No libav entry point is called before
/// <see cref="TryEnsureLoaded"/> succeeded. A failure is LATCHED: the process does
/// not retry a broken install on every probe; MediaMetadataProbe uses ffprobe instead.
/// The libraries are never unloaded (process lifetime), which also means a running app holds
/// backend\*.dll open — the installer/upgrader already requires the app to be closed.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static class LibAvLibrary
{
    internal const string AvFormat = "avformat-62";
    internal const string AvCodec = "avcodec-62";
    internal const string AvUtil = "avutil-60";
    internal const string SwResample = "swresample-6";
    /// <summary>LIBAVFRAME_01 — frame tier only. Imports avutil-60 alone (PE import table), so it binds to the copy already loaded.</summary>
    internal const string SwScale = "swscale-9";

    /// <summary>Strict dependency order (verified from each DLL's PE import table).</summary>
    internal static readonly string[] LoadOrder = { AvUtil, SwResample, AvCodec, AvFormat };

    private sealed record LoadState(bool Ok, string Detail, string? Directory, IReadOnlyDictionary<string, nint>? Modules = null);

    private static readonly object Gate = new();
    private static LoadState? _state;

    /// <summary>Directory the libraries were loaded from, or null.</summary>
    public static string? LoadedDirectory => Volatile.Read(ref _state)?.Directory;

    /// <summary>Loads (once) and verifies the libraries. Never throws.</summary>
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
            // The struct views were derived from 62.0 / 62.0 / 60.1. Any other major.minor may move
            // a field; refusing is the only safe answer. The DLLs stay loaded but are never called again.
            return new LoadState(false, $"version mismatch ({versions}); struct layout pinned to avformat 62.0, avcodec 62.0, avutil 60.1.", null);
        }

        // ffprobe runs with -v quiet. Same here: libav must not write to the (absent) stderr.
        LibAv.av_log_set_level(LibAvConstants.AV_LOG_QUIET);
        return new LoadState(true, $"native media backend loaded from {dir} ({versions}).", dir, handles);
    }

    // ── LIBAVFRAME_02 — the single-frame decode tier ────────────────────────────────────────────
    //
    // NOT a second loader: it runs only after the tier above succeeded, loads exactly ONE more
    // module (swscale-9) by absolute path from the SAME directory, binds LibAvFrame against the
    // module handles already held, and checks swscale's major.minor. Latched separately, so a
    // missing/mismatched swscale disables frame decoding (callers keep their ffmpeg subprocess)
    // and never touches metadata. Never unloaded.

    private static LoadState? _frameState;

    /// <summary>Loads (once) the metadata tier and then the frame tier. Never throws.</summary>
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
