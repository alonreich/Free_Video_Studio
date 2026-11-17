using System.Diagnostics;
using System.Globalization;

namespace FreeVideoStudio.Core.Media.Native;

public sealed class LibAvFrameDecoder : INativeFrameDecoder
{
    public static LibAvFrameDecoder Instance { get; } = new();

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

        return await Task.Run(() => DecodeBlocking(mediaPath, request, deadline, cancellationToken), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static NativeFrameResult Fail(NativeFrameStatus status, string detail) => new(status, null, detail);

    internal static unsafe NativeFrameResult DecodeBlocking(string mediaPath, VideoFrameRequest request, long deadline, CancellationToken token)
    {
        using var interrupt = InterruptStateHandle.Create(deadline);
        using var registration = token.Register(static s => ((InterruptStateHandle)s!).Cancel(), interrupt);
        token.ThrowIfCancellationRequested();

        using FormatContextHandle? input = LibAvMediaBackend.OpenInputBlocking(mediaPath, interrupt, token, out string openError);
        if (input == null) return Fail(NativeFrameStatus.MediaError, openError);
        AVFormatContextView* ic = input.Context;
        if (!LibAvMediaBackend.ValidateLayout(ic, out string problem) || !ValidateFrameTierLayout(ic, out problem))
        {
            LibAvMediaBackend.MarkLayoutBroken();
            return Fail(NativeFrameStatus.Unavailable, $"struct layout guard failed: {problem}");
        }
        ThrowIfAborted(interrupt, token);

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
        if (err < 0) LibAvMediaBackend.ThrowIfInterrupted(err, interrupt, token);
        ThrowIfAborted(interrupt, token);

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
        LibAvFrame.avcodec_flush_buffers(dec.Context);

        for (uint i = 0; i < ic->nb_streams; i++)
            if (i != (uint)vIndex) ((AVStreamView*)ic->streams[i])->discard = LibAvConstants.AVDISCARD_ALL;

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
            ThrowIfAborted(interrupt, token);
            if (!draining)
            {
                err = LibAvFrame.av_read_frame(ic, pkt.Packet);
                if (err < 0)
                {
                    LibAvMediaBackend.ThrowIfInterrupted(err, interrupt, token);
                    draining = true;
                    LibAvFrame.avcodec_send_packet(dec.Context, null);
                }
                else
                {
                    bool ours = pkt.Packet->stream_index == vIndex;
                    if (ours) LibAvFrame.avcodec_send_packet(dec.Context, pkt.Packet);
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
                ThrowIfAborted(interrupt, token);

                pts = frame->best_effort_timestamp;
                if (pts == LibAvConstants.AV_NOPTS_VALUE)
                    return Fail(NativeFrameStatus.MediaError, "decoded frame carries no timestamp (left to ffmpeg)");
                if ((Int128)pts + offset >= 0) { selected = true; break; }
                LibAvFrame.av_frame_unref(frame);
            }
        }

        if (frame->width <= 0 || frame->height <= 0 || frame->format == LibAvConstants.AV_PIX_FMT_NONE || frame->data[0] == 0)
            return Fail(NativeFrameStatus.MediaError, $"decoder returned an empty frame ({frame->width}x{frame->height}, format {frame->format})");

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

        int orientedW = orientation.SwapsAxes ? frame->height : frame->width;
        int orientedH = orientation.SwapsAxes ? frame->width : frame->height;
        if (!FrameMath.TryOutputSize(orientedW, orientedH, request.TargetWidth, request.EvenHeight, out int outW, out int outH)
            || (long)outW * outH > MaxOutputPixels)
            return Fail(NativeFrameStatus.MediaError, $"unsupported output size for {orientedW}x{orientedH} → width {request.TargetWidth}");
        int scaleW = orientation.SwapsAxes ? outH : outW;
        int scaleH = orientation.SwapsAxes ? outW : outH;
        ThrowIfAborted(interrupt, token);

        frame->flags &= ~LibAvConstants.AV_FRAME_FLAG_INTERLACED;
        frame->chroma_location = LibAvConstants.AVCHROMA_LOC_UNSPECIFIED;

        AVFrameView* rawDst = LibAvFrame.av_frame_alloc();
        if (rawDst == null) return Fail(NativeFrameStatus.MediaError, "av_frame_alloc returned NULL.");
        using var dst = new FrameHandle(rawDst);
        err = LibAvFrame.av_frame_copy_props(dst.Frame, frame);
        if (err < 0) return Fail(NativeFrameStatus.MediaError, $"av_frame_copy_props: {LibAv.ErrorText(err)}");
        dst.Frame->width = scaleW;
        dst.Frame->height = scaleH;
        dst.Frame->format = LibAvConstants.AV_PIX_FMT_RGB24;

        void* rawSws = LibAvFrame.sws_alloc_context();
        if (rawSws == null) return Fail(NativeFrameStatus.MediaError, "sws_alloc_context returned NULL.");
        using var sws = new SwsContextHandle(rawSws);
        LibAvFrame.av_opt_set_int(sws.Context, "threads", 0, 0);
        err = LibAvFrame.sws_scale_frame(sws.Context, dst.Frame, frame);
        if (err < 0) return Fail(NativeFrameStatus.MediaError, $"sws_scale_frame: {LibAv.ErrorText(err)}");
        AVFrameView* o = dst.Frame;
        int srcStride = o->linesize[0];
        if (o->data[0] == 0 || o->width != scaleW || o->height != scaleH || srcStride < scaleW * 3)
            return Fail(NativeFrameStatus.MediaError, $"swscale produced an unexpected frame ({o->width}x{o->height}, stride {srcStride})");

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

    private static unsafe bool ValidateFreshPacket(AVPacketView* p, out string problem)
    {
        bool ok = p->pts == LibAvConstants.AV_NOPTS_VALUE && p->dts == LibAvConstants.AV_NOPTS_VALUE
                  && p->data == null && p->size == 0 && p->buf == null && p->stream_index == 0;
        problem = ok ? string.Empty : "AVPacket defaults";
        return ok;
    }

    private static unsafe bool ValidateFreshFrame(AVFrameView* f, out string problem)
    {
        bool ok = f->extended_data == (void*)f
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

internal static class FrameMath
{
    public static long RescaleNearInf(long a, long b, long c)
    {
        if (c <= 0 || b < 0) throw new ArgumentOutOfRangeException(nameof(c));
        if (a < 0) return -RescaleNearInf(-Math.Max(a, -long.MaxValue), b, c);
        Int128 r = (Int128)a * b + c / 2;
        return (long)(r / c);
    }

    public static long RescaleQ(long a, int bNum, int bDen, int cNum, int cDen)
        => RescaleNearInf(a, (long)bNum * cDen, (long)cNum * bDen);

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

internal readonly record struct FrameOrientation(int TransposeDir, bool HFlip, bool VFlip, bool Unsupported)
{
    public static readonly FrameOrientation Identity = new(-1, false, false, false);

    public bool SwapsAxes => TransposeDir >= 0;

    public static FrameOrientation FromDisplayMatrix(int[]? m)
    {
        if (m == null || m.Length < 9) return Identity;
        double theta = GetRotation(m);
        if (Math.Abs(theta - 90) < 1.0) return new(m[3] > 0 ? 0 : 1, false, false, false);
        if (Math.Abs(theta - 180) < 1.0) return new(-1, m[0] < 0, m[4] < 0, false);
        if (Math.Abs(theta - 270) < 1.0) return new(m[3] < 0 ? 3 : 2, false, false, false);
        if (Math.Abs(theta) > 1.0) return new(-1, false, false, true);
        if (Math.Abs(theta) < 1.0) return new(-1, false, m[4] < 0, false);
        return Identity;
    }

    internal static double GetRotation(int[] m)
    {
        double theta = -Math.Round(DisplayRotationGet(m), MidpointRounding.AwayFromZero);
        theta -= 360 * Math.Floor(theta / 360 + 0.9 / 360);
        return theta;
    }

    internal static double DisplayRotationGet(int[] m)
    {
        static double Fp(int x) => x / 65536.0;
        double scale0 = Math.Sqrt(Fp(m[0]) * Fp(m[0]) + Fp(m[3]) * Fp(m[3]));
        double scale1 = Math.Sqrt(Fp(m[1]) * Fp(m[1]) + Fp(m[4]) * Fp(m[4]));
        if (scale0 == 0.0 || scale1 == 0.0) return double.NaN;
        double rotation = Math.Atan2(Fp(m[1]) / scale1, Fp(m[0]) / scale0) * 180 / Math.PI;
        return -rotation;
    }

    public byte[] Apply(byte[] src, int w, int h, out int outW, out int outH)
    {
        outW = w;
        outH = h;
        byte[] cur = src;
        if (TransposeDir >= 0)
        {
            int ow = h, oh = w;
            var dstBuf = new byte[cur.Length];
            var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(cur);
            var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dstBuf);
            for (int r = 0; r < oh; r++)
            {
                int y = (TransposeDir & 2) != 0 ? oh - 1 - r : r;
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
