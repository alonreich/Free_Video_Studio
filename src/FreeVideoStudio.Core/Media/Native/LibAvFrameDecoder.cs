// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Diagnostics;
using System.Globalization;

namespace FreeVideoStudio.Core.Media.Native;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LIBAVFRAME_01 — ffmpeg's <c>-ss T -i FILE -frames:v 1 [-vf scale=W:-2]</c>, without the process.
///
/// <para>
/// Every step mirrors fftools at 1e5c65f539 (the bundled N-119166 build), so the SAME frame comes out:
///   1. OPEN — the shared LibAvMediaBackend.OpenInputBlocking (scan_all_pmts=1, interrupt callback,
///      find_stream_info). Layout guards as LIBAVPROBE_01, plus AVFrame/AVPacket default guards.
///   2. SEEK (ffmpeg_demux.c ifile_open) — timestamp = T + ic->start_time; when the demuxer lacks
///      AVFMT_SEEK_TO_PTS and any stream has video_delay, seek 3/23 s earlier (the dts heuristic);
///      avformat_seek_file(ic, -1, INT64_MIN, ts, ts, 0). A failed seek is ignored, as ffmpeg does.
///   3. STREAM (ffmpeg_mux_init.c map_auto_video) — score = w·h + 1e8·NEW_PACKETS + 5e6·DEFAULT,
///      attached pictures score 1; highest wins, first on ties. All other streams → AVDISCARD_ALL
///      (ffmpeg discards unused inputs the same way, after the seek).
///   4. DECODE (ffmpeg_dec.c) — avcodec_find_decoder(codec_id), threads=auto, pkt_timebase =
///      stream time_base; flushed after the seek; send/receive until a frame is SELECTED.
///   5. SELECT (ffmpeg_demux ts_offset + ffmpeg_filter insert_trim + trim.c) — frame pts is
///      best_effort_timestamp; ffmpeg shifts every packet by av_rescale_q(-timestamp, 1/1e6, tb)
///      and its trim filter (start 0) keeps the FIRST frame with shifted pts ≥ 0. So: the first
///      decoded frame with best_effort + offset ≥ 0. Real timestamps and time_base only — never a
///      nominal frame rate. End of stream before such a frame → NoFrame (ffmpeg writes nothing).
///   6. ORIENT (ffmpeg_filter.c autorotate) — the frame's DISPLAYMATRIX side data gives transpose
///      clock / cclock / *_flip, hflip+vflip, or a lone vflip, exactly as ffmpeg chooses them; any
///      other angle needs the rotate filter → MediaError (subprocess).
///   7. SCALE (vf_scale.c + scale_eval.c) — out size from the ORIENTED size: W wide, height
///      av_rescale(W, h, w·2)·2 for -2. ONE sws_scale_frame from the decoded format straight to rgb24 (ffmpeg's png format; widened to BGRA)
///      at that size (swscale defaults = bicubic; interlaced flag cleared and chroma location
///      reset exactly as vf_scale's defaults do), so an 8K source never becomes an 8K managed buffer.
///      The 90° transposes are applied to the small BGRA result in managed code.
/// </para>
///
/// <para>
/// Deliberately NOT reproduced (→ MediaError → ffmpeg subprocess, so the outcome is unchanged):
/// attached-picture streams, container-level cropping (AV_PKT_DATA_FRAME_CROPPING), non-90°
/// rotation, frames without any timestamp. Not handled at all (same as most players): MPEG-TS
/// timestamp wrap/discontinuity correction.
/// </para>
///
/// <para>
/// ⚠️ OWNERSHIP — ALL ON ONE POOL THREAD, ALL DETERMINISTIC, REVERSE ORDER OF CREATION:
/// SwsContextHandle → dst FrameHandle → decode FrameHandle → PacketHandle → CodecContextHandle →
/// FormatContextHandle → CancellationTokenRegistration → InterruptStateHandle (LAST, after the
/// context that points at it is closed). Each SafeHandle is created only from a non-NULL pointer
/// and frees with the matching libav *_free(&amp;p). The pixels are COPIED into a managed array before
/// any handle is released; nothing native escapes this method.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class LibAvFrameDecoder : INativeFrameDecoder
{
    public static LibAvFrameDecoder Instance { get; } = new();

    /// <summary>Largest output this path will allocate (16384² BGRA = 1 GiB is already absurd for a still).</summary>
    private const long MaxOutputPixels = 8192L * 8192L;

    public async Task<NativeFrameResult> DecodeAsync(string ffmpegPath, string mediaPath, VideoFrameRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.TargetWidth < 0) throw new ArgumentOutOfRangeException(nameof(request), "TargetWidth must be ≥ 0.");
        if (!LibAvMediaBackend.TryInitializeFrameTier(ffmpegPath, out string detail))
            return new NativeFrameResult(NativeFrameStatus.Unavailable, null, detail);

        long deadline = timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan
            ? Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency)
            : 0;

        // Every native resource is created and released inside DecodeBlocking on ONE pool thread
        // (never the UI thread); awaiting it is the ownership boundary.
        return await Task.Run(() => DecodeBlocking(mediaPath, request, deadline, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static NativeFrameResult Fail(NativeFrameStatus status, string detail) => new(status, null, detail);

    internal static unsafe NativeFrameResult DecodeBlocking(string mediaPath, VideoFrameRequest request, long deadline, CancellationToken token)
    {
        using var interrupt = InterruptStateHandle.Create(deadline);
        using var registration = token.Register(static s => ((InterruptStateHandle)s!).Cancel(), interrupt);
        token.ThrowIfCancellationRequested();

        // ── 1. open ──────────────────────────────────────────────────────────────────────────────
        using FormatContextHandle? input = LibAvMediaBackend.OpenInputBlocking(mediaPath, interrupt, token, out string openError);
        if (input == null) return Fail(NativeFrameStatus.MediaError, openError);
        AVFormatContextView* ic = input.Context;
        if (!LibAvMediaBackend.ValidateLayout(ic, out string problem) || !ValidateFrameTierLayout(ic, out problem))
        {
            LibAvMediaBackend.MarkLayoutBroken();
            return Fail(NativeFrameStatus.Unavailable, $"struct layout guard failed: {problem}");
        }
        ThrowIfAborted(interrupt, token);

        // ── 2. seek (ffmpeg_demux.c ifile_open) ──────────────────────────────────────────────────
        long timestamp = request.SeekMicroseconds;
        if (ic->start_time != LibAvConstants.AV_NOPTS_VALUE) timestamp += ic->start_time;
        long seekTimestamp = timestamp;
        if ((ic->iformat->flags & LibAvConstants.AVFMT_SEEK_TO_PTS) == 0)
        {
            for (uint i = 0; i < ic->nb_streams; i++)
            {
                if (((AVStreamView*)ic->streams[i])->codecpar->video_delay != 0)
                {
                    seekTimestamp -= 3 * LibAvConstants.AV_TIME_BASE / 23;
                    break;
                }
            }
        }
        int err = LibAvFrame.avformat_seek_file(ic, -1, long.MinValue, seekTimestamp, seekTimestamp, 0);
        if (err < 0) LibAvMediaBackend.ThrowIfInterrupted(err, interrupt, token);   // else: ffmpeg only warns and decodes from where it is
        ThrowIfAborted(interrupt, token);

        // ── 3. stream (ffmpeg_mux_init.c map_auto_video) ─────────────────────────────────────────
        int vIndex = SelectVideoStream(ic);
        if (vIndex < 0) return Fail(NativeFrameStatus.NoFrame, "no video stream");
        AVStreamView* st = (AVStreamView*)ic->streams[vIndex];
        AVCodecParametersView* par = st->codecpar;
        if ((st->disposition & LibAvConstants.AV_DISPOSITION_ATTACHED_PIC) != 0)
            return Fail(NativeFrameStatus.MediaError, "attached picture stream (left to ffmpeg)");
        if (HasContainerCropping(par))
            return Fail(NativeFrameStatus.MediaError, "container cropping side data (left to ffmpeg)");
        AVRational tb = st->time_base;
        if (tb.num <= 0 || tb.den <= 0) return Fail(NativeFrameStatus.MediaError, $"invalid stream time_base {tb.num}/{tb.den}");

        // ── 4. decoder (ffmpeg_dec.c dec_open) ───────────────────────────────────────────────────
        void* codec = LibAvFrame.avcodec_find_decoder(par->codec_id);
        if (codec == null) return Fail(NativeFrameStatus.MediaError, $"no decoder for codec id {par->codec_id}");
        void* rawCodecCtx = LibAvFrame.avcodec_alloc_context3(codec);
        if (rawCodecCtx == null) return Fail(NativeFrameStatus.MediaError, "avcodec_alloc_context3 returned NULL.");
        using var dec = new CodecContextHandle(rawCodecCtx);
        err = LibAvFrame.avcodec_parameters_to_context(dec.Context, par);
        if (err < 0) return Fail(NativeFrameStatus.MediaError, $"avcodec_parameters_to_context: {LibAv.ErrorText(err)}");
        void* decoderOptions = null;
        try
        {
            LibAv.av_dict_set(&decoderOptions, "threads", "auto", 0);
            LibAv.av_dict_set(&decoderOptions, "pkt_timebase",
                $"{tb.num.ToString(CultureInfo.InvariantCulture)}/{tb.den.ToString(CultureInfo.InvariantCulture)}", 0);
            err = LibAvFrame.avcodec_open2(dec.Context, codec, &decoderOptions);
        }
        finally
        {
            LibAv.av_dict_free(&decoderOptions);
        }
        if (err < 0) return Fail(NativeFrameStatus.MediaError, $"avcodec_open2: {LibAv.ErrorText(err)}");
        LibAvFrame.avcodec_flush_buffers(dec.Context);   // the decoder is opened after the seek; flushing makes "no pre-seek state" explicit

        for (uint i = 0; i < ic->nb_streams; i++)
            if (i != (uint)vIndex) ((AVStreamView*)ic->streams[i])->discard = LibAvConstants.AVDISCARD_ALL;

        // ── 5. decode + select (ts_offset + trim start 0) ────────────────────────────────────────
        long offset = FrameMath.RescaleQ(-timestamp, 1, LibAvConstants.AV_TIME_BASE, tb.num, tb.den);

        AVPacketView* rawPkt = LibAvFrame.av_packet_alloc();
        if (rawPkt == null) return Fail(NativeFrameStatus.MediaError, "av_packet_alloc returned NULL.");
        using var pkt = new PacketHandle(rawPkt);
        AVFrameView* rawFrame = LibAvFrame.av_frame_alloc();
        if (rawFrame == null) return Fail(NativeFrameStatus.MediaError, "av_frame_alloc returned NULL.");
        using var decoded = new FrameHandle(rawFrame);
        if (!ValidateFreshPacket(pkt.Packet, out problem) || !ValidateFreshFrame(decoded.Frame, out problem))
        {
            LibAvMediaBackend.MarkLayoutBroken();
            return Fail(NativeFrameStatus.Unavailable, $"struct layout guard failed: {problem}");
        }

        AVFrameView* frame = decoded.Frame;
        bool selected = false, draining = false;
        long pts = LibAvConstants.AV_NOPTS_VALUE;
        while (!selected)
        {
            ThrowIfAborted(interrupt, token);   // between packets
            if (!draining)
            {
                err = LibAvFrame.av_read_frame(ic, pkt.Packet);
                if (err < 0)
                {
                    // AVERROR_EOF or a read error: ffmpeg ends this input either way and drains the decoder.
                    LibAvMediaBackend.ThrowIfInterrupted(err, interrupt, token);
                    draining = true;
                    LibAvFrame.avcodec_send_packet(dec.Context, null);
                }
                else
                {
                    bool ours = pkt.Packet->stream_index == vIndex;
                    if (ours) LibAvFrame.avcodec_send_packet(dec.Context, pkt.Packet);   // a corrupt packet is skipped, as ffmpeg does without -xerror
                    LibAvFrame.av_packet_unref(pkt.Packet);
                    if (!ours) continue;
                }
            }

            while (true)
            {
                err = LibAvFrame.avcodec_receive_frame(dec.Context, frame);
                if (err == LibAvConstants.AVERROR_EAGAIN)
                {
                    if (draining) return Fail(NativeFrameStatus.NoFrame, "decoder returned EAGAIN while draining");
                    break;
                }
                if (err == LibAvConstants.AVERROR_EOF)
                    return Fail(NativeFrameStatus.NoFrame, $"no frame at or after {FfmpegTime.Describe(request.SeekMicroseconds)} s");
                if (err < 0) return Fail(NativeFrameStatus.MediaError, $"avcodec_receive_frame: {LibAv.ErrorText(err)}");
                ThrowIfAborted(interrupt, token);   // between decoded frames

                pts = frame->best_effort_timestamp;
                if (pts == LibAvConstants.AV_NOPTS_VALUE)
                    return Fail(NativeFrameStatus.MediaError, "decoded frame carries no timestamp (left to ffmpeg)");
                if ((Int128)pts + offset >= 0) { selected = true; break; }
                LibAvFrame.av_frame_unref(frame);
            }
        }

        if (frame->width <= 0 || frame->height <= 0 || frame->format == LibAvConstants.AV_PIX_FMT_NONE || frame->data[0] == 0)
            return Fail(NativeFrameStatus.MediaError, $"decoder returned an empty frame ({frame->width}x{frame->height}, format {frame->format})");

        // ── 6. orientation (autorotate) ──────────────────────────────────────────────────────────
        int[]? matrix = null;
        AVFrameSideDataView* sd = LibAvFrame.av_frame_get_side_data(frame, LibAvConstants.AV_FRAME_DATA_DISPLAYMATRIX);
        if (sd != null && sd->data != null && sd->size >= 36)
        {
            matrix = new int[9];
            for (int i = 0; i < 9; i++) matrix[i] = ((int*)sd->data)[i];
        }
        FrameOrientation orientation = FrameOrientation.FromDisplayMatrix(matrix);
        if (orientation.Unsupported)
            return Fail(NativeFrameStatus.MediaError, "display matrix needs the rotate filter (left to ffmpeg)");

        // ── 7. size + one swscale pass (rgb24 → BGRA) ───────────────────────────────────────────────────
        int orientedW = orientation.SwapsAxes ? frame->height : frame->width;
        int orientedH = orientation.SwapsAxes ? frame->width : frame->height;
        if (!FrameMath.TryOutputSize(orientedW, orientedH, request.TargetWidth, request.EvenHeight, out int outW, out int outH)
            || (long)outW * outH > MaxOutputPixels)
            return Fail(NativeFrameStatus.MediaError, $"unsupported output size for {orientedW}x{orientedH} → width {request.TargetWidth}");
        int scaleW = orientation.SwapsAxes ? outH : outW;
        int scaleH = orientation.SwapsAxes ? outW : outH;
        ThrowIfAborted(interrupt, token);   // before the expensive conversion

        // vf_scale.c scale_frame(): interl=0 clears the interlaced flag; in_chroma_loc defaults to UNSPECIFIED.
        frame->flags &= ~LibAvConstants.AV_FRAME_FLAG_INTERLACED;
        frame->chroma_location = LibAvConstants.AVCHROMA_LOC_UNSPECIFIED;

        AVFrameView* rawDst = LibAvFrame.av_frame_alloc();
        if (rawDst == null) return Fail(NativeFrameStatus.MediaError, "av_frame_alloc returned NULL.");
        using var dst = new FrameHandle(rawDst);
        err = LibAvFrame.av_frame_copy_props(dst.Frame, frame);
        if (err < 0) return Fail(NativeFrameStatus.MediaError, $"av_frame_copy_props: {LibAv.ErrorText(err)}");
        dst.Frame->width = scaleW;
        dst.Frame->height = scaleH;
        // rgb24 — the SAME destination format ffmpeg's scale → png chain negotiates, so swscale runs the
        // same output writer. (Windows validation 2026-10-04: with a BGRA destination the 12–24× downscales
        // measured MAE 2.9–3.9 against ffmpeg's PNG, vs ≤ 1.4 elsewhere.) Widened to BGRA below.
        dst.Frame->format = LibAvConstants.AV_PIX_FMT_RGB24;

        void* rawSws = LibAvFrame.sws_alloc_context();
        if (rawSws == null) return Fail(NativeFrameStatus.MediaError, "sws_alloc_context returned NULL.");
        using var sws = new SwsContextHandle(rawSws);
        LibAvFrame.av_opt_set_int(sws.Context, "threads", 0, 0);   // vf_scale: the filter graph's thread count (auto); output is identical either way
        err = LibAvFrame.sws_scale_frame(sws.Context, dst.Frame, frame);
        if (err < 0) return Fail(NativeFrameStatus.MediaError, $"sws_scale_frame: {LibAv.ErrorText(err)}");
        AVFrameView* o = dst.Frame;
        int srcStride = o->linesize[0];
        if (o->data[0] == 0 || o->width != scaleW || o->height != scaleH || srcStride < scaleW * 3)
            return Fail(NativeFrameStatus.MediaError, $"swscale produced an unexpected frame ({o->width}x{o->height}, stride {srcStride})");

        // Copy into managed memory BEFORE any handle is released: R,G,B → B,G,R,255.
        int rowBytes = scaleW * 4;
        byte[] scaled = new byte[rowBytes * scaleH];
        fixed (byte* pDst = scaled)
        {
            byte* pSrc = (byte*)o->data[0];
            for (int y = 0; y < scaleH; y++)
            {
                byte* s = pSrc + (long)y * srcStride;
                byte* d = pDst + (long)y * rowBytes;
                for (int x = 0; x < scaleW; x++, s += 3, d += 4)
                {
                    d[0] = s[2];
                    d[1] = s[1];
                    d[2] = s[0];
                    d[3] = 255;
                }
            }
        }

        byte[] pixels = orientation.Apply(scaled, scaleW, scaleH, out int finalW, out int finalH);
        double? seconds = pts * (double)tb.num / tb.den;
        return new NativeFrameResult(NativeFrameStatus.Ok, new DecodedVideoFrame(pixels, finalW, finalH, finalW * 4, seconds), "ok");
    }

    private static void ThrowIfAborted(InterruptStateHandle interrupt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (interrupt.WasCancelled || interrupt.DeadlinePassed)
            throw new OperationCanceledException("Native frame decode timed out.");
    }

    /// <summary>ffmpeg_mux_init.c map_auto_video for ONE input file and an image output (qcr ≠ APIC).</summary>
    private static unsafe int SelectVideoStream(AVFormatContextView* ic)
    {
        int best = -1;
        long bestScore = 0;
        for (uint i = 0; i < ic->nb_streams; i++)
        {
            AVStreamView* st = (AVStreamView*)ic->streams[i];
            if (st->codecpar->codec_type != LibAvConstants.AVMEDIA_TYPE_VIDEO) continue;
            long score = (long)st->codecpar->width * st->codecpar->height
                       + 100_000_000L * ((st->event_flags & LibAvConstants.AVSTREAM_EVENT_FLAG_NEW_PACKETS) != 0 ? 1 : 0)
                       + 5_000_000L * ((st->disposition & LibAvConstants.AV_DISPOSITION_DEFAULT) != 0 ? 1 : 0);
            if ((st->disposition & LibAvConstants.AV_DISPOSITION_ATTACHED_PIC) != 0) score = 1;
            if (score > bestScore) { bestScore = score; best = (int)i; }
        }
        if (best < 0) return -1;
        AVStreamView* chosen = (AVStreamView*)ic->streams[best];
        if ((chosen->disposition & LibAvConstants.AV_DISPOSITION_ATTACHED_PIC) == 0)
            bestScore -= 5_000_000L * ((chosen->disposition & LibAvConstants.AV_DISPOSITION_DEFAULT) != 0 ? 1 : 0);
        return bestScore > 0 ? best : -1;
    }

    private static unsafe bool HasContainerCropping(AVCodecParametersView* par)
    {
        if (par->coded_side_data == null || par->nb_coded_side_data <= 0) return false;
        AVPacketSideDataView* sd = LibAvFrame.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, LibAvConstants.AV_PKT_DATA_FRAME_CROPPING);
        if (sd == null || sd->data == null || sd->size < 16) return false;
        uint* c = (uint*)sd->data;
        return (c[0] | c[1] | c[2] | c[3]) != 0;
    }

    /// <summary>Guards for the fields only this tier reads (on top of LibAvMediaBackend.ValidateLayout).</summary>
    private static unsafe bool ValidateFrameTierLayout(AVFormatContextView* ic, out string problem)
    {
        if (ic->iformat == null || ic->iformat->name == null) { problem = "AVFormatContext.iformat"; return false; }
        for (uint i = 0; i < ic->nb_streams; i++)
        {
            AVStreamView* st = (AVStreamView*)ic->streams[i];
            if (st->discard != LibAvConstants.AVDISCARD_DEFAULT) { problem = $"AVStream[{i}].discard={st->discard}"; return false; }
            if (st->codecpar->nb_coded_side_data < 0 || st->codecpar->nb_coded_side_data > 1024) { problem = $"AVStream[{i}].nb_coded_side_data"; return false; }
        }
        problem = string.Empty;
        return true;
    }

    /// <summary>libavcodec/packet.c get_packet_defaults: pts = dts = AV_NOPTS_VALUE, no data.</summary>
    private static unsafe bool ValidateFreshPacket(AVPacketView* p, out string problem)
    {
        bool ok = p->pts == LibAvConstants.AV_NOPTS_VALUE && p->dts == LibAvConstants.AV_NOPTS_VALUE
                  && p->data == null && p->size == 0 && p->buf == null && p->stream_index == 0;
        problem = ok ? string.Empty : "AVPacket defaults";
        return ok;
    }

    /// <summary>libavutil/frame.c get_frame_defaults — a shifted AVFrame layout cannot pass all of these.</summary>
    private static unsafe bool ValidateFreshFrame(AVFrameView* f, out string problem)
    {
        bool ok = f->extended_data == (void*)f   // extended_data = data (offset 0)
                  && f->format == LibAvConstants.AV_PIX_FMT_NONE
                  && f->pts == LibAvConstants.AV_NOPTS_VALUE && f->pkt_dts == LibAvConstants.AV_NOPTS_VALUE
                  && f->best_effort_timestamp == LibAvConstants.AV_NOPTS_VALUE
                  && f->time_base.num == 0 && f->time_base.den == 1
                  && f->sample_aspect_ratio.num == 0 && f->sample_aspect_ratio.den == 1
                  && f->color_primaries == LibAvConstants.AVCOL_PRI_UNSPECIFIED && f->color_trc == LibAvConstants.AVCOL_TRC_UNSPECIFIED
                  && f->colorspace == LibAvConstants.AVCOL_SPC_UNSPECIFIED && f->color_range == LibAvConstants.AVCOL_RANGE_UNSPECIFIED
                  && f->chroma_location == LibAvConstants.AVCHROMA_LOC_UNSPECIFIED && f->flags == 0 && f->duration == 0
                  && f->width == 0 && f->height == 0;
        problem = ok ? string.Empty : "AVFrame defaults";
        return ok;
    }
}

/// <summary>
/// LIBAVFRAME_01 — the integer math ffmpeg uses, reproduced exactly (pure managed, unit-tested
/// without the DLLs).
/// </summary>
internal static class FrameMath
{
    /// <summary>libavutil/mathematics.c av_rescale_rnd(a, b, c, AV_ROUND_NEAR_INF): half away from zero.</summary>
    public static long RescaleNearInf(long a, long b, long c)
    {
        if (c <= 0 || b < 0) throw new ArgumentOutOfRangeException(nameof(c));
        if (a < 0) return -RescaleNearInf(-Math.Max(a, -long.MaxValue), b, c);
        Int128 r = (Int128)a * b + c / 2;
        return (long)(r / c);
    }

    /// <summary>av_rescale_q(a, bq, cq) = av_rescale_rnd(a, bq.num·cq.den, cq.num·bq.den, NEAR_INF).</summary>
    public static long RescaleQ(long a, int bNum, int bDen, int cNum, int cDen)
        => RescaleNearInf(a, (long)bNum * cDen, (long)cNum * bDen);

    /// <summary>
    /// libavfilter/scale_eval.c ff_scale_adjust_dimensions for <c>scale=W:-1</c> / <c>scale=W:-2</c>
    /// (w_adj = 1, no force_original_aspect_ratio). targetWidth 0 = keep the input size.
    /// </summary>
    public static bool TryOutputSize(int inW, int inH, int targetWidth, bool evenHeight, out int outW, out int outH)
    {
        outW = inW;
        outH = inH;
        if (inW <= 0 || inH <= 0) return false;
        if (targetWidth <= 0) return true;
        int factor = evenHeight ? 2 : 1;
        long h = RescaleNearInf(targetWidth, inH, (long)inW * factor) * factor;
        if (h <= 0 || h > int.MaxValue) return false;
        outW = targetWidth;
        outH = (int)h;
        return true;
    }
}

/// <summary>
/// LIBAVFRAME_01 — ffmpeg's autorotate decision (fftools/ffmpeg_filter.c configure_input_video_filter
/// + cmdutils.c get_rotation + libavutil/display.c av_display_rotation_get) and the matching pixel
/// remaps of vf_transpose / vf_hflip / vf_vflip, applied to a packed 32-bit image.
/// </summary>
internal readonly record struct FrameOrientation(int TransposeDir, bool HFlip, bool VFlip, bool Unsupported)
{
    /// <summary>vf_transpose dir values: 0 cclock_flip, 1 clock, 2 cclock, 3 clock_flip; -1 = none.</summary>
    public static readonly FrameOrientation Identity = new(-1, false, false, false);

    public bool SwapsAxes => TransposeDir >= 0;

    public static FrameOrientation FromDisplayMatrix(int[]? m)
    {
        // ffmpeg keeps an all-zero matrix when the frame has none; av_display_rotation_get then
        // returns NaN and every branch below is false — i.e. no filter. A null matrix is that case.
        if (m == null || m.Length < 9) return Identity;
        double theta = GetRotation(m);
        if (Math.Abs(theta - 90) < 1.0) return new(m[3] > 0 ? 0 : 1, false, false, false);
        if (Math.Abs(theta - 180) < 1.0) return new(-1, m[0] < 0, m[4] < 0, false);
        if (Math.Abs(theta - 270) < 1.0) return new(m[3] < 0 ? 3 : 2, false, false, false);
        if (Math.Abs(theta) > 1.0) return new(-1, false, false, true);   // ffmpeg inserts "rotate"
        if (Math.Abs(theta) < 1.0) return new(-1, false, m[4] < 0, false);
        return Identity;   // NaN
    }

    /// <summary>fftools/cmdutils.c get_rotation (C round() = half away from zero).</summary>
    internal static double GetRotation(int[] m)
    {
        double theta = -Math.Round(DisplayRotationGet(m), MidpointRounding.AwayFromZero);
        theta -= 360 * Math.Floor(theta / 360 + 0.9 / 360);
        return theta;
    }

    /// <summary>libavutil/display.c av_display_rotation_get (16.16 fixed point).</summary>
    internal static double DisplayRotationGet(int[] m)
    {
        static double Fp(int x) => x / 65536.0;
        double scale0 = Math.Sqrt(Fp(m[0]) * Fp(m[0]) + Fp(m[3]) * Fp(m[3]));
        double scale1 = Math.Sqrt(Fp(m[1]) * Fp(m[1]) + Fp(m[4]) * Fp(m[4]));
        if (scale0 == 0.0 || scale1 == 0.0) return double.NaN;
        double rotation = Math.Atan2(Fp(m[1]) / scale1, Fp(m[0]) / scale0) * 180 / Math.PI;
        return -rotation;
    }

    /// <summary>
    /// Applies the filters ffmpeg would insert, in ffmpeg's order (transpose, or hflip then vflip),
    /// to a packed width×height image of 4-byte pixels. Returns a new buffer (or the same one when
    /// nothing applies).
    /// </summary>
    public byte[] Apply(byte[] src, int w, int h, out int outW, out int outH)
    {
        outW = w;
        outH = h;
        byte[] cur = src;
        if (TransposeDir >= 0)
        {
            // vf_transpose filter_slice: dst row y / col x reads src row x (reversed when dir&1),
            // col y; dst rows are written bottom-up when dir&2. outW = h, outH = w.
            int ow = h, oh = w;
            var dstBuf = new byte[cur.Length];
            var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(cur);
            var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dstBuf);
            for (int r = 0; r < oh; r++)
            {
                int y = (TransposeDir & 2) != 0 ? oh - 1 - r : r;    // the src column this dst row reads
                for (int c = 0; c < ow; c++)
                {
                    int srcRow = (TransposeDir & 1) != 0 ? h - 1 - c : c;
                    d[r * ow + c] = s[srcRow * w + y];
                }
            }
            cur = dstBuf;
            outW = ow;
            outH = oh;
            return cur;
        }
        if (HFlip)
        {
            var dstBuf = new byte[cur.Length];
            var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(cur);
            var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dstBuf);
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    d[r * w + c] = s[r * w + (w - 1 - c)];
            cur = dstBuf;
        }
        if (VFlip)
        {
            var dstBuf = new byte[cur.Length];
            int row = w * 4;
            for (int r = 0; r < h; r++)
                Buffer.BlockCopy(cur, (h - 1 - r) * row, dstBuf, r * row, row);
            cur = dstBuf;
        }
        return cur;
    }
}
