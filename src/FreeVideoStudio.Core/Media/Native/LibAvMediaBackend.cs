using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Media.Native;

public enum NativeProbeStatus
{
    Ok,
    Unavailable,
    MediaError,
}

public sealed record NativeProbeResult(NativeProbeStatus Status, JsonObject? Data, string Detail);

public interface INativeMediaBackend
{
    Task<NativeProbeResult> ProbeAsync(string ffprobePath, string mediaPath, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class LibAvMediaBackend : INativeMediaBackend
{
    public static LibAvMediaBackend Instance { get; } = new();

    private static int _layoutBroken;

    public static bool TryInitialize(string? ffprobePath, out string detail)
    {
        if (Volatile.Read(ref _layoutBroken) != 0)
        {
            detail = "disabled after a struct-layout guard failed.";
            return false;
        }
        return LibAvLibrary.TryEnsureLoaded(ffprobePath, out detail);
    }

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
            LibAv.av_dict_set(&options, "scan_all_pmts", "1", LibAvConstants.AV_DICT_DONT_OVERWRITE);
            err = LibAv.avformat_open_input(&raw, mediaPath, null, &options);
        }
        catch
        {
            if (raw != null) LibAv.avformat_free_context(raw);
            throw;
        }
        finally
        {
            LibAv.av_dict_free(&options);
        }

        if (err < 0)
        {
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

    internal static void MarkLayoutBroken() => Interlocked.Exchange(ref _layoutBroken, 1);

    internal static void ThrowIfInterrupted(int err, InterruptStateHandle interrupt, CancellationToken token)
    {
        if (err != LibAvConstants.AVERROR_EXIT && !interrupt.WasCancelled && !interrupt.DeadlinePassed) return;
        token.ThrowIfCancellationRequested();
        if (interrupt.WasCancelled || interrupt.DeadlinePassed)
            throw new OperationCanceledException("Native media probe timed out.");
    }

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
        if (value == unspecified || name == null) return;
        s[key] = LibAv.Utf8(name);
    }

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
            tags[key] = LibAv.Utf8(e->value) ?? string.Empty;
        }
        return tags;
    }
}
