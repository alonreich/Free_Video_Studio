// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Media.Native;

/// <summary>Why a native metadata probe produced no data.</summary>
public enum NativeProbeStatus
{
    /// <summary>Probe succeeded; <see cref="NativeProbeResult.Data"/> is set.</summary>
    Ok,
    /// <summary>The backend cannot run in this process (DLLs missing, version or layout mismatch). Latched.</summary>
    Unavailable,
    /// <summary>libav opened the backend fine but rejected this input (missing, unreadable, invalid data).</summary>
    MediaError,
}

/// <summary>Outcome of one native probe. Holds managed data only — no native pointer escapes the backend.</summary>
public sealed record NativeProbeResult(NativeProbeStatus Status, JsonObject? Data, string Detail);

/// <summary>
/// LIBAVPROBE_01 — narrow in-process metadata seam. Callers get an ffprobe-shaped
/// <see cref="JsonObject"/> (the subset of <c>-show_format -show_streams</c> that the product
/// actually reads) and never see a pointer.
/// </summary>
public interface INativeMediaBackend
{
    /// <summary>
    /// Probes <paramref name="mediaPath"/>. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> fires or <paramref name="timeout"/> elapses (the latter
    /// with no token, mirroring AsyncProcessRunner's timeout). Never throws for media errors.
    /// </summary>
    Task<NativeProbeResult> ProbeAsync(string ffprobePath, string mediaPath, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LIBAVPROBE_01 — ffprobe's METADATA, without the process.
///
/// <para>
/// Mirrors <c>fftools/ffprobe.c</c> at 1e5c65f539 for the fields MediaProber / OutputSizeEstimator
/// consume: avformat_alloc_context → avformat_open_input (format_opts scan_all_pmts=1, exactly as
/// ffprobe sets it) → avformat_find_stream_info(NULL) → read AVFormatContext / AVStream /
/// AVCodecParameters. Output keys and formatting follow ffprobe's JSON writer:
/// <c>format.duration</c> "%f" seconds (omitted when AV_NOPTS_VALUE), <c>format.bit_rate</c> (omitted
/// when ≤ 0), <c>format.tags</c>; per stream, in container order (NO selection here — every
/// consumer keeps its own existing "first video / first audio" rule): <c>index</c>,
/// <c>codec_type</c>, for video <c>width</c>/<c>height</c>/<c>pix_fmt</c>/<c>color_range</c>/
/// <c>color_space</c>/<c>color_transfer</c>/<c>color_primaries</c> (colour keys omitted when
/// UNSPECIFIED, as ffprobe's optional-field rule does), <c>avg_frame_rate</c> "num/den",
/// <c>duration</c>, <c>bit_rate</c>. Attached pictures (MP3 cover art) remain video streams,
/// because that is what ffprobe reports and what every existing consumer already sees.
/// </para>
///
/// <para>
/// ⚠️ OWNERSHIP, ALL ON ONE THREAD, ALL DETERMINISTIC:
///   • InterruptStateHandle (native block read by the [UnmanagedCallersOnly] callback) — disposed LAST.
///   • CancellationTokenRegistration — disposed before the state it writes to (Dispose waits for a
///     running callback).
///   • AVDictionary options — freed with av_dict_free in a finally, success or failure.
///   • AVFormatContext — before open: freed with avformat_free_context if open is never reached;
///     open FAILED: libav already freed it and nulled the pointer, nothing to do; open SUCCEEDED:
///     owned by FormatContextHandle → avformat_close_input(&amp;p).
/// No pointer is stored in a field, returned, or captured by a lambda that outlives the call.
/// </para>
///
/// <para>
/// ⚠️ INTERRUPTION. AVIOInterruptCB is installed BEFORE avformat_open_input, so every blocking read
/// inside open and find_stream_info polls it. It aborts on the caller's token or on the
/// deadline; libav then returns AVERROR_EXIT and the probe throws OperationCanceledException. A probe
/// cannot outlive its timeout by more than one libav I/O step.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class LibAvMediaBackend : INativeMediaBackend
{
    public static LibAvMediaBackend Instance { get; } = new();

    private static int _layoutBroken;   // 1 = a layout guard failed once; backend disabled for the process

    /// <summary>True when the libraries loaded and passed the version guard (loads them on first call).</summary>
    public static bool TryInitialize(string? ffprobePath, out string detail)
    {
        if (Volatile.Read(ref _layoutBroken) != 0)
        {
            detail = "disabled after a struct-layout guard failed.";
            return false;
        }
        return LibAvLibrary.TryEnsureLoaded(ffprobePath, out detail);
    }

    /// <summary>LIBAVFRAME_01 — true when the metadata tier AND the frame tier (swscale-9) are usable.</summary>
    public static bool TryInitializeFrameTier(string? ffmpegOrFfprobePath, out string detail)
    {
        if (Volatile.Read(ref _layoutBroken) != 0)
        {
            detail = "disabled after a struct-layout guard failed.";
            return false;
        }
        return LibAvLibrary.TryEnsureFrameTierLoaded(ffmpegOrFfprobePath, out detail);
    }

    public async Task<NativeProbeResult> ProbeAsync(string ffprobePath, string mediaPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryInitialize(ffprobePath, out string detail))
            return new NativeProbeResult(NativeProbeStatus.Unavailable, null, detail);

        long deadline = timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan
            ? Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency)
            : 0;

        // The blocking libav calls run on a pool thread; every native resource is created and
        // released inside ProbeBlocking on that thread, so awaiting it is the ownership boundary.
        return await Task.Run(() => ProbeBlocking(mediaPath, deadline, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
    }

    internal static unsafe NativeProbeResult ProbeBlocking(string mediaPath, long deadline, CancellationToken token)
    {
        using var interrupt = InterruptStateHandle.Create(deadline);
        using var registration = token.Register(static s => ((InterruptStateHandle)s!).Cancel(), interrupt);
        token.ThrowIfCancellationRequested();

        using FormatContextHandle? ctx = OpenInputBlocking(mediaPath, interrupt, token, out string error);
        if (ctx == null) return new NativeProbeResult(NativeProbeStatus.MediaError, null, error);
        token.ThrowIfCancellationRequested();

        if (!ValidateLayout(ctx.Context, out string layoutProblem))
        {
            MarkLayoutBroken();
            return new NativeProbeResult(NativeProbeStatus.Unavailable, null, $"struct layout guard failed: {layoutProblem}");
        }

        return new NativeProbeResult(NativeProbeStatus.Ok, BuildJson(ctx.Context), "ok");
    }

    /// <summary>
    /// LIBAVPROBE_01 / LIBAVFRAME_01 — the ONE open sequence, shared by the metadata probe and the
    /// single-frame decoder: avformat_alloc_context → interrupt callback installed → open_input
    /// (scan_all_pmts=1, as both ffprobe and ffmpeg set it) → find_stream_info(NULL).
    /// Returns the opened context (caller disposes), or null with <paramref name="error"/> set when
    /// libav rejected the input. Throws OperationCanceledException when the interrupt fired.
    /// Ownership is exactly the pre-existing rule: alloc'd-but-never-opened → avformat_free_context;
    /// open failed → libav already freed it; opened → FormatContextHandle.
    /// </summary>
    internal static unsafe FormatContextHandle? OpenInputBlocking(string mediaPath, InterruptStateHandle interrupt, CancellationToken token, out string error)
    {
        AVFormatContextView* raw = LibAv.avformat_alloc_context();
        if (raw == null) { error = "avformat_alloc_context returned NULL."; return null; }

        raw->interrupt_callback = &InterruptStateHandle.Callback;
        raw->interrupt_opaque = interrupt.Opaque;

        void* options = null;
        int err;
        try
        {
            // fftools/ffprobe.c open_input_file() and fftools/ffmpeg_demux.c ifile_open(): scan_all_pmts=1 unless the user set it.
            LibAv.av_dict_set(&options, "scan_all_pmts", "1", LibAvConstants.AV_DICT_DONT_OVERWRITE);
            err = LibAv.avformat_open_input(&raw, mediaPath, null, &options);
        }
        catch
        {
            // Marshalling failed before libav took the context (e.g. an unpaired surrogate in the
            // path). libav never saw it, so it is ours to free. Re-thrown, not swallowed.
            if (raw != null) LibAv.avformat_free_context(raw);
            throw;
        }
        finally
        {
            LibAv.av_dict_free(&options);
        }

        if (err < 0)
        {
            // demux.c `fail:` — libav freed the context and set raw = NULL. Nothing to release.
            ThrowIfInterrupted(err, interrupt, token);
            error = $"avformat_open_input: {LibAv.ErrorText(err)}";
            return null;
        }

        var ctx = new FormatContextHandle(raw);
        try
        {
            err = LibAv.avformat_find_stream_info(ctx.Context, null);
            if (err < 0)
            {
                ThrowIfInterrupted(err, interrupt, token);
                error = $"avformat_find_stream_info: {LibAv.ErrorText(err)}";
                ctx.Dispose();
                return null;
            }
        }
        catch
        {
            ctx.Dispose();
            throw;
        }
        error = string.Empty;
        return ctx;
    }

    /// <summary>A layout guard failed: native libav is disabled for the rest of the process (both tiers).</summary>
    internal static void MarkLayoutBroken() => Interlocked.Exchange(ref _layoutBroken, 1);

    internal static void ThrowIfInterrupted(int err, InterruptStateHandle interrupt, CancellationToken token)
    {
        if (err != LibAvConstants.AVERROR_EXIT && !interrupt.WasCancelled && !interrupt.DeadlinePassed) return;
        token.ThrowIfCancellationRequested();
        if (interrupt.WasCancelled || interrupt.DeadlinePassed)
            throw new OperationCanceledException("Native media probe timed out.");
    }

    /// <summary>Guard 2 and 3 from LibAvInterop.cs. Cheap; runs on every probe.</summary>
    internal static unsafe bool ValidateLayout(AVFormatContextView* ctx, out string problem)
    {
        if (ctx->av_class != LibAv.avformat_get_class()) { problem = "AVFormatContext.av_class"; return false; }
        if (ctx->nb_streams > 4096 || (ctx->nb_streams > 0 && ctx->streams == null)) { problem = $"nb_streams={ctx->nb_streams}"; return false; }
        void* streamClass = LibAv.av_stream_get_class();
        for (uint i = 0; i < ctx->nb_streams; i++)
        {
            AVStreamView* st = (AVStreamView*)ctx->streams[i];
            if (st == null || st->av_class != streamClass) { problem = $"AVStream[{i}].av_class"; return false; }
            if (st->index != (int)i) { problem = $"AVStream[{i}].index={st->index}"; return false; }
            if (st->codecpar == null) { problem = $"AVStream[{i}].codecpar"; return false; }
            int type = st->codecpar->codec_type;
            if (type < -1 || type > 4) { problem = $"AVStream[{i}].codec_type={type}"; return false; }
        }
        problem = string.Empty;
        return true;
    }

    private static unsafe JsonObject BuildJson(AVFormatContextView* ctx)
    {
        var streams = new JsonArray();
        for (uint i = 0; i < ctx->nb_streams; i++)
        {
            AVStreamView* st = (AVStreamView*)ctx->streams[i];
            AVCodecParametersView* par = st->codecpar;
            var s = new JsonObject { ["index"] = st->index };
            if (LibAv.Utf8(LibAv.av_get_media_type_string(par->codec_type)) is { } type) s["codec_type"] = type;

            if (par->codec_type == LibAvConstants.AVMEDIA_TYPE_VIDEO)
            {
                s["width"] = par->width;
                s["height"] = par->height;
                if (LibAv.Utf8(LibAv.av_get_pix_fmt_name(par->format)) is { } pix) s["pix_fmt"] = pix;
                AddColor(s, "color_range", par->color_range, LibAvConstants.AVCOL_RANGE_UNSPECIFIED, LibAv.av_color_range_name(par->color_range));
                AddColor(s, "color_space", par->color_space, LibAvConstants.AVCOL_SPC_UNSPECIFIED, LibAv.av_color_space_name(par->color_space));
                AddColor(s, "color_transfer", par->color_trc, LibAvConstants.AVCOL_TRC_UNSPECIFIED, LibAv.av_color_transfer_name(par->color_trc));
                AddColor(s, "color_primaries", par->color_primaries, LibAvConstants.AVCOL_PRI_UNSPECIFIED, LibAv.av_color_primaries_name(par->color_primaries));
            }

            s["avg_frame_rate"] = $"{st->avg_frame_rate.num.ToString(CultureInfo.InvariantCulture)}/{st->avg_frame_rate.den.ToString(CultureInfo.InvariantCulture)}";
            if (Seconds(st->duration, st->time_base.num, st->time_base.den) is { } dur) s["duration"] = dur;
            if (par->bit_rate > 0) s["bit_rate"] = par->bit_rate.ToString(CultureInfo.InvariantCulture);
            streams.Add((JsonNode)s);
        }

        var format = new JsonObject();
        if (Seconds(ctx->duration, 1, LibAvConstants.AV_TIME_BASE) is { } fmtDur) format["duration"] = fmtDur;
        if (ctx->bit_rate > 0) format["bit_rate"] = ctx->bit_rate.ToString(CultureInfo.InvariantCulture);
        var tags = ReadDictionary(ctx->metadata);
        if (tags.Count > 0) format["tags"] = tags;

        return new JsonObject { ["streams"] = streams, ["format"] = format };
    }

    private static unsafe void AddColor(JsonObject s, string key, int value, int unspecified, byte* name)
    {
        // ffprobe print_color_*: "unknown" (an optional field, hidden in JSON) when UNSPECIFIED or unnamed.
        if (value == unspecified || name == null) return;
        s[key] = LibAv.Utf8(name);
    }

    /// <summary>ffprobe writer_print_time: N/A for AV_NOPTS_VALUE, else ts * av_q2d(tb) printed "%f".</summary>
    private static string? Seconds(long ts, int num, int den)
    {
        if (ts == LibAvConstants.AV_NOPTS_VALUE || den == 0) return null;
        double d = ts * (num / (double)den);
        return d.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static unsafe JsonObject ReadDictionary(void* dict)
    {
        var tags = new JsonObject();
        if (dict == null) return tags;
        AVDictionaryEntryView* e = null;
        while ((e = LibAv.av_dict_iterate(dict, e)) != null)
        {
            string? key = LibAv.Utf8(e->key);
            if (key == null) continue;
            tags[key] = LibAv.Utf8(e->value) ?? string.Empty;   // duplicate keys: last wins
        }
        return tags;
    }
}
