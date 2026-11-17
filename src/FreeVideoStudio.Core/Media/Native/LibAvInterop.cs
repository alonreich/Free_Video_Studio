using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeVideoStudio.Core.Media.Native;

internal static class LibAvConstants
{
    public const long AV_NOPTS_VALUE = long.MinValue;
    public const int AV_TIME_BASE = 1_000_000;
    public const int AVMEDIA_TYPE_VIDEO = 0;
    public const int AVCOL_RANGE_UNSPECIFIED = 0;
    public const int AVCOL_PRI_UNSPECIFIED = 2;
    public const int AVCOL_TRC_UNSPECIFIED = 2;
    public const int AVCOL_SPC_UNSPECIFIED = 2;
    public const int AV_DICT_DONT_OVERWRITE = 16;
    public const int AV_LOG_QUIET = -8;
    public const int AVERROR_EXIT = -1414092869;

    public static uint MajorMinor(uint version) => version & 0xFFFF_FF00u;
    public static uint Version(uint major, uint minor) => (major << 16) | (minor << 8);

    public static readonly uint ExpectedAvFormat = Version(62, 0);
    public static readonly uint ExpectedAvCodec = Version(62, 0);
    public static readonly uint ExpectedAvUtil = Version(60, 1);

    public static readonly uint ExpectedSwScale = Version(9, 0);
    public const int AVERROR_EOF = -541478725;
    public const int AVERROR_EAGAIN = -11;
    public const int AV_PIX_FMT_NONE = -1;
    public const int AV_PIX_FMT_RGB24 = 2;
    public const int AV_FRAME_DATA_DISPLAYMATRIX = 6;
    public const int AV_PKT_DATA_FRAME_CROPPING = 36;
    public const int AV_FRAME_FLAG_INTERLACED = 8;
    public const int AVCHROMA_LOC_UNSPECIFIED = 0;
    public const int AVDISCARD_DEFAULT = 0;
    public const int AVDISCARD_ALL = 48;
    public const int AV_DISPOSITION_DEFAULT = 1;
    public const int AV_DISPOSITION_ATTACHED_PIC = 1024;
    public const int AVSTREAM_EVENT_FLAG_NEW_PACKETS = 2;
    public const int AVFMT_SEEK_TO_PTS = 0x4000000;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVRational
{
    public int num;
    public int den;
}

[StructLayout(LayoutKind.Explicit, Size = 472)]
internal unsafe struct AVFormatContextView
{
    [FieldOffset(0)] public void* av_class;
    [FieldOffset(8)] public AVInputFormatView* iformat;
    [FieldOffset(44)] public uint nb_streams;
    [FieldOffset(48)] public void** streams;
    [FieldOffset(96)] public long start_time;
    [FieldOffset(104)] public long duration;
    [FieldOffset(112)] public long bit_rate;
    [FieldOffset(192)] public void* metadata;
    [FieldOffset(216)] public delegate* unmanaged[Cdecl]<void*, int> interrupt_callback;
    [FieldOffset(224)] public void* interrupt_opaque;
}

[StructLayout(LayoutKind.Explicit, Size = 216)]
internal unsafe struct AVStreamView
{
    [FieldOffset(0)] public void* av_class;
    [FieldOffset(8)] public int index;
    [FieldOffset(16)] public AVCodecParametersView* codecpar;
    [FieldOffset(32)] public AVRational time_base;
    [FieldOffset(48)] public long duration;
    [FieldOffset(64)] public int disposition;
    [FieldOffset(68)] public int discard;
    [FieldOffset(88)] public AVRational avg_frame_rate;
    [FieldOffset(200)] public int event_flags;
}

[StructLayout(LayoutKind.Explicit, Size = 20)]
internal unsafe struct AVInputFormatView
{
    [FieldOffset(0)] public byte* name;
    [FieldOffset(16)] public int flags;
}

[StructLayout(LayoutKind.Explicit, Size = 176)]
internal unsafe struct AVCodecParametersView
{
    [FieldOffset(0)] public int codec_type;
    [FieldOffset(4)] public int codec_id;
    [FieldOffset(32)] public void* coded_side_data;
    [FieldOffset(40)] public int nb_coded_side_data;
    [FieldOffset(44)] public int format;
    [FieldOffset(48)] public long bit_rate;
    [FieldOffset(72)] public int width;
    [FieldOffset(76)] public int height;
    [FieldOffset(100)] public int color_range;
    [FieldOffset(104)] public int color_primaries;
    [FieldOffset(108)] public int color_trc;
    [FieldOffset(112)] public int color_space;
    [FieldOffset(120)] public int video_delay;
}

[StructLayout(LayoutKind.Explicit, Size = 104)]
internal unsafe struct AVPacketView
{
    [FieldOffset(0)] public void* buf;
    [FieldOffset(8)] public long pts;
    [FieldOffset(16)] public long dts;
    [FieldOffset(24)] public byte* data;
    [FieldOffset(32)] public int size;
    [FieldOffset(36)] public int stream_index;
}

[StructLayout(LayoutKind.Explicit, Size = 416)]
internal unsafe struct AVFrameView
{
    [FieldOffset(0)] public fixed long data[8];
    [FieldOffset(64)] public fixed int linesize[8];
    [FieldOffset(96)] public void* extended_data;
    [FieldOffset(104)] public int width;
    [FieldOffset(108)] public int height;
    [FieldOffset(116)] public int format;
    [FieldOffset(124)] public AVRational sample_aspect_ratio;
    [FieldOffset(136)] public long pts;
    [FieldOffset(144)] public long pkt_dts;
    [FieldOffset(152)] public AVRational time_base;
    [FieldOffset(276)] public int flags;
    [FieldOffset(280)] public int color_range;
    [FieldOffset(284)] public int color_primaries;
    [FieldOffset(288)] public int color_trc;
    [FieldOffset(292)] public int colorspace;
    [FieldOffset(296)] public int chroma_location;
    [FieldOffset(304)] public long best_effort_timestamp;
    [FieldOffset(408)] public long duration;
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
internal unsafe struct AVFrameSideDataView
{
    [FieldOffset(0)] public int type;
    [FieldOffset(8)] public byte* data;
    [FieldOffset(16)] public nuint size;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal unsafe struct AVPacketSideDataView
{
    [FieldOffset(0)] public byte* data;
    [FieldOffset(8)] public nuint size;
    [FieldOffset(16)] public int type;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct AVDictionaryEntryView
{
    [FieldOffset(0)] public byte* key;
    [FieldOffset(8)] public byte* value;
}

internal static unsafe class LibAv
{
    private static delegate* unmanaged[Cdecl]<uint> _avformat_version;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*> _avformat_alloc_context;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*, void> _avformat_free_context;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView**, byte*, void*, void**, int> _avformat_open_input;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*, void**, int> _avformat_find_stream_info;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView**, void> _avformat_close_input;
    private static delegate* unmanaged[Cdecl]<void*> _avformat_get_class;
    private static delegate* unmanaged[Cdecl]<void*> _av_stream_get_class;
    private static delegate* unmanaged[Cdecl]<uint> _avcodec_version;
    private static delegate* unmanaged[Cdecl]<uint> _avutil_version;
    private static delegate* unmanaged[Cdecl]<int, void> _av_log_set_level;
    private static delegate* unmanaged[Cdecl]<void**, byte*, byte*, int, int> _av_dict_set;
    private static delegate* unmanaged[Cdecl]<void**, void> _av_dict_free;
    private static delegate* unmanaged[Cdecl]<void*, AVDictionaryEntryView*, AVDictionaryEntryView*> _av_dict_iterate;
    private static delegate* unmanaged[Cdecl]<int, byte*> _av_get_media_type_string;
    private static delegate* unmanaged[Cdecl]<int, byte*> _av_get_pix_fmt_name;
    private static delegate* unmanaged[Cdecl]<int, byte*> _av_color_range_name;
    private static delegate* unmanaged[Cdecl]<int, byte*> _av_color_primaries_name;
    private static delegate* unmanaged[Cdecl]<int, byte*> _av_color_transfer_name;
    private static delegate* unmanaged[Cdecl]<int, byte*> _av_color_space_name;
    private static delegate* unmanaged[Cdecl]<int, byte*, nuint, int> _av_strerror;

    internal static readonly (string Library, string Symbol)[] Imports =
    {
        (LibAvLibrary.AvFormat, "avformat_version"), (LibAvLibrary.AvFormat, "avformat_alloc_context"),
        (LibAvLibrary.AvFormat, "avformat_free_context"), (LibAvLibrary.AvFormat, "avformat_open_input"),
        (LibAvLibrary.AvFormat, "avformat_find_stream_info"), (LibAvLibrary.AvFormat, "avformat_close_input"),
        (LibAvLibrary.AvFormat, "avformat_get_class"), (LibAvLibrary.AvFormat, "av_stream_get_class"),
        (LibAvLibrary.AvCodec, "avcodec_version"),
        (LibAvLibrary.AvUtil, "avutil_version"), (LibAvLibrary.AvUtil, "av_log_set_level"),
        (LibAvLibrary.AvUtil, "av_dict_set"), (LibAvLibrary.AvUtil, "av_dict_free"), (LibAvLibrary.AvUtil, "av_dict_iterate"),
        (LibAvLibrary.AvUtil, "av_get_media_type_string"), (LibAvLibrary.AvUtil, "av_get_pix_fmt_name"),
        (LibAvLibrary.AvUtil, "av_color_range_name"), (LibAvLibrary.AvUtil, "av_color_primaries_name"),
        (LibAvLibrary.AvUtil, "av_color_transfer_name"), (LibAvLibrary.AvUtil, "av_color_space_name"),
        (LibAvLibrary.AvUtil, "av_strerror"),
    };

    internal static void Bind(IReadOnlyDictionary<string, nint> modules)
    {
        nint E(string lib, string sym) => NativeLibrary.GetExport(modules[lib], sym);
        var t = Imports.Select(i => E(i.Library, i.Symbol)).ToArray();
        int k = 0;
        _avformat_version = (delegate* unmanaged[Cdecl]<uint>)t[k++];
        _avformat_alloc_context = (delegate* unmanaged[Cdecl]<AVFormatContextView*>)t[k++];
        _avformat_free_context = (delegate* unmanaged[Cdecl]<AVFormatContextView*, void>)t[k++];
        _avformat_open_input = (delegate* unmanaged[Cdecl]<AVFormatContextView**, byte*, void*, void**, int>)t[k++];
        _avformat_find_stream_info = (delegate* unmanaged[Cdecl]<AVFormatContextView*, void**, int>)t[k++];
        _avformat_close_input = (delegate* unmanaged[Cdecl]<AVFormatContextView**, void>)t[k++];
        _avformat_get_class = (delegate* unmanaged[Cdecl]<void*>)t[k++];
        _av_stream_get_class = (delegate* unmanaged[Cdecl]<void*>)t[k++];
        _avcodec_version = (delegate* unmanaged[Cdecl]<uint>)t[k++];
        _avutil_version = (delegate* unmanaged[Cdecl]<uint>)t[k++];
        _av_log_set_level = (delegate* unmanaged[Cdecl]<int, void>)t[k++];
        _av_dict_set = (delegate* unmanaged[Cdecl]<void**, byte*, byte*, int, int>)t[k++];
        _av_dict_free = (delegate* unmanaged[Cdecl]<void**, void>)t[k++];
        _av_dict_iterate = (delegate* unmanaged[Cdecl]<void*, AVDictionaryEntryView*, AVDictionaryEntryView*>)t[k++];
        _av_get_media_type_string = (delegate* unmanaged[Cdecl]<int, byte*>)t[k++];
        _av_get_pix_fmt_name = (delegate* unmanaged[Cdecl]<int, byte*>)t[k++];
        _av_color_range_name = (delegate* unmanaged[Cdecl]<int, byte*>)t[k++];
        _av_color_primaries_name = (delegate* unmanaged[Cdecl]<int, byte*>)t[k++];
        _av_color_transfer_name = (delegate* unmanaged[Cdecl]<int, byte*>)t[k++];
        _av_color_space_name = (delegate* unmanaged[Cdecl]<int, byte*>)t[k++];
        _av_strerror = (delegate* unmanaged[Cdecl]<int, byte*, nuint, int>)t[k++];
    }

    private static byte[] Utf8Z(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    internal static uint avformat_version() => _avformat_version();
    internal static AVFormatContextView* avformat_alloc_context() => _avformat_alloc_context();
    internal static void avformat_free_context(AVFormatContextView* s) => _avformat_free_context(s);

    internal static int avformat_open_input(AVFormatContextView** ps, string url, void* fmt, void** options)
    {
        byte[] u = Utf8Z(url);
        fixed (byte* pu = u) return _avformat_open_input(ps, pu, fmt, options);
    }

    internal static int avformat_find_stream_info(AVFormatContextView* ic, void** options) => _avformat_find_stream_info(ic, options);

    internal static void avformat_close_input(AVFormatContextView** s) => _avformat_close_input(s);
    internal static void* avformat_get_class() => _avformat_get_class();
    internal static void* av_stream_get_class() => _av_stream_get_class();
    internal static uint avcodec_version() => _avcodec_version();
    internal static uint avutil_version() => _avutil_version();
    internal static void av_log_set_level(int level) => _av_log_set_level(level);

    internal static int av_dict_set(void** pm, string key, string value, int flags)
    {
        byte[] k = Utf8Z(key), v = Utf8Z(value);
        fixed (byte* pk = k)
        fixed (byte* pv = v)
            return _av_dict_set(pm, pk, pv, flags);
    }

    internal static void av_dict_free(void** m) => _av_dict_free(m);
    internal static AVDictionaryEntryView* av_dict_iterate(void* m, AVDictionaryEntryView* prev) => _av_dict_iterate(m, prev);
    internal static byte* av_get_media_type_string(int mediaType) => _av_get_media_type_string(mediaType);
    internal static byte* av_get_pix_fmt_name(int pixFmt) => _av_get_pix_fmt_name(pixFmt);
    internal static byte* av_color_range_name(int range) => _av_color_range_name(range);
    internal static byte* av_color_primaries_name(int primaries) => _av_color_primaries_name(primaries);
    internal static byte* av_color_transfer_name(int transfer) => _av_color_transfer_name(transfer);
    internal static byte* av_color_space_name(int space) => _av_color_space_name(space);
    internal static int av_strerror(int errnum, byte* errbuf, nuint errbufSize) => _av_strerror(errnum, errbuf, errbufSize);

    internal static string? Utf8(byte* s) => s == null ? null : Marshal.PtrToStringUTF8((nint)s);

    internal static string ErrorText(int err)
    {
        const int Size = 256;
        byte* buf = stackalloc byte[Size];
        return av_strerror(err, buf, Size) == 0 ? (Utf8(buf) ?? $"AVERROR {err}") : $"AVERROR {err}";
    }
}

internal sealed unsafe class InterruptStateHandle : SafeHandle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public int Cancelled;
        public long DeadlineTimestamp;
    }

    private InterruptStateHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public static InterruptStateHandle Create(long deadlineTimestamp)
    {
        var h = new InterruptStateHandle();
        State* s = (State*)NativeMemory.AllocZeroed((nuint)sizeof(State));
        s->DeadlineTimestamp = deadlineTimestamp;
        h.SetHandle((nint)s);
        return h;
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public void* Opaque => (void*)handle;

    public void Cancel() => Volatile.Write(ref ((State*)handle)->Cancelled, 1);

    public bool WasCancelled => Volatile.Read(ref ((State*)handle)->Cancelled) != 0;

    public bool DeadlinePassed
    {
        get
        {
            long d = ((State*)handle)->DeadlineTimestamp;
            return d != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > d;
        }
    }

    protected override bool ReleaseHandle()
    {
        NativeMemory.Free((void*)handle);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static int Callback(void* opaque)
    {
        if (opaque == null) return 0;
        State* s = (State*)opaque;
        if (Volatile.Read(ref s->Cancelled) != 0) return 1;
        long d = s->DeadlineTimestamp;
        return d != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > d ? 1 : 0;
    }
}

internal sealed unsafe class FormatContextHandle : SafeHandle
{
    public FormatContextHandle(AVFormatContextView* opened) : base(IntPtr.Zero, ownsHandle: true)
        => SetHandle((nint)opened);

    public override bool IsInvalid => handle == IntPtr.Zero;

    public AVFormatContextView* Context => (AVFormatContextView*)handle;

    protected override bool ReleaseHandle()
    {
        AVFormatContextView* p = (AVFormatContextView*)handle;
        LibAv.avformat_close_input(&p);
        return true;
    }
}

internal static unsafe class LibAvFrame
{
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*, int, long, long, long, int, int> _avformat_seek_file;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*, AVPacketView*, int> _av_read_frame;
    private static delegate* unmanaged[Cdecl]<int, void*> _avcodec_find_decoder;
    private static delegate* unmanaged[Cdecl]<void*, void*> _avcodec_alloc_context3;
    private static delegate* unmanaged[Cdecl]<void**, void> _avcodec_free_context;
    private static delegate* unmanaged[Cdecl]<void*, AVCodecParametersView*, int> _avcodec_parameters_to_context;
    private static delegate* unmanaged[Cdecl]<void*, void*, void**, int> _avcodec_open2;
    private static delegate* unmanaged[Cdecl]<void*, AVPacketView*, int> _avcodec_send_packet;
    private static delegate* unmanaged[Cdecl]<void*, AVFrameView*, int> _avcodec_receive_frame;
    private static delegate* unmanaged[Cdecl]<void*, void> _avcodec_flush_buffers;
    private static delegate* unmanaged[Cdecl]<AVPacketView*> _av_packet_alloc;
    private static delegate* unmanaged[Cdecl]<AVPacketView**, void> _av_packet_free;
    private static delegate* unmanaged[Cdecl]<AVPacketView*, void> _av_packet_unref;
    private static delegate* unmanaged[Cdecl]<void*, int, int, AVPacketSideDataView*> _av_packet_side_data_get;
    private static delegate* unmanaged[Cdecl]<AVFrameView*> _av_frame_alloc;
    private static delegate* unmanaged[Cdecl]<AVFrameView**, void> _av_frame_free;
    private static delegate* unmanaged[Cdecl]<AVFrameView*, void> _av_frame_unref;
    private static delegate* unmanaged[Cdecl]<AVFrameView*, AVFrameView*, int> _av_frame_copy_props;
    private static delegate* unmanaged[Cdecl]<AVFrameView*, int, AVFrameSideDataView*> _av_frame_get_side_data;
    private static delegate* unmanaged[Cdecl]<void*, byte*, long, int, int> _av_opt_set_int;
    private static delegate* unmanaged[Cdecl]<uint> _swscale_version;
    private static delegate* unmanaged[Cdecl]<void*> _sws_alloc_context;
    private static delegate* unmanaged[Cdecl]<void**, void> _sws_free_context;
    private static delegate* unmanaged[Cdecl]<void*, AVFrameView*, AVFrameView*, int> _sws_scale_frame;

    internal static readonly (string Library, string Symbol)[] Imports =
    {
        (LibAvLibrary.AvFormat, "avformat_seek_file"), (LibAvLibrary.AvFormat, "av_read_frame"),
        (LibAvLibrary.AvCodec, "avcodec_find_decoder"), (LibAvLibrary.AvCodec, "avcodec_alloc_context3"),
        (LibAvLibrary.AvCodec, "avcodec_free_context"), (LibAvLibrary.AvCodec, "avcodec_parameters_to_context"),
        (LibAvLibrary.AvCodec, "avcodec_open2"), (LibAvLibrary.AvCodec, "avcodec_send_packet"),
        (LibAvLibrary.AvCodec, "avcodec_receive_frame"), (LibAvLibrary.AvCodec, "avcodec_flush_buffers"),
        (LibAvLibrary.AvCodec, "av_packet_alloc"), (LibAvLibrary.AvCodec, "av_packet_free"),
        (LibAvLibrary.AvCodec, "av_packet_unref"), (LibAvLibrary.AvCodec, "av_packet_side_data_get"),
        (LibAvLibrary.AvUtil, "av_frame_alloc"), (LibAvLibrary.AvUtil, "av_frame_free"),
        (LibAvLibrary.AvUtil, "av_frame_unref"), (LibAvLibrary.AvUtil, "av_frame_copy_props"),
        (LibAvLibrary.AvUtil, "av_frame_get_side_data"), (LibAvLibrary.AvUtil, "av_opt_set_int"),
        (LibAvLibrary.SwScale, "swscale_version"), (LibAvLibrary.SwScale, "sws_alloc_context"),
        (LibAvLibrary.SwScale, "sws_free_context"), (LibAvLibrary.SwScale, "sws_scale_frame"),
    };

    internal static void Bind(IReadOnlyDictionary<string, nint> modules)
    {
        nint E(string lib, string sym) => NativeLibrary.GetExport(modules[lib], sym);
        var t = Imports.Select(i => E(i.Library, i.Symbol)).ToArray();
        int k = 0;
        _avformat_seek_file = (delegate* unmanaged[Cdecl]<AVFormatContextView*, int, long, long, long, int, int>)t[k++];
        _av_read_frame = (delegate* unmanaged[Cdecl]<AVFormatContextView*, AVPacketView*, int>)t[k++];
        _avcodec_find_decoder = (delegate* unmanaged[Cdecl]<int, void*>)t[k++];
        _avcodec_alloc_context3 = (delegate* unmanaged[Cdecl]<void*, void*>)t[k++];
        _avcodec_free_context = (delegate* unmanaged[Cdecl]<void**, void>)t[k++];
        _avcodec_parameters_to_context = (delegate* unmanaged[Cdecl]<void*, AVCodecParametersView*, int>)t[k++];
        _avcodec_open2 = (delegate* unmanaged[Cdecl]<void*, void*, void**, int>)t[k++];
        _avcodec_send_packet = (delegate* unmanaged[Cdecl]<void*, AVPacketView*, int>)t[k++];
        _avcodec_receive_frame = (delegate* unmanaged[Cdecl]<void*, AVFrameView*, int>)t[k++];
        _avcodec_flush_buffers = (delegate* unmanaged[Cdecl]<void*, void>)t[k++];
        _av_packet_alloc = (delegate* unmanaged[Cdecl]<AVPacketView*>)t[k++];
        _av_packet_free = (delegate* unmanaged[Cdecl]<AVPacketView**, void>)t[k++];
        _av_packet_unref = (delegate* unmanaged[Cdecl]<AVPacketView*, void>)t[k++];
        _av_packet_side_data_get = (delegate* unmanaged[Cdecl]<void*, int, int, AVPacketSideDataView*>)t[k++];
        _av_frame_alloc = (delegate* unmanaged[Cdecl]<AVFrameView*>)t[k++];
        _av_frame_free = (delegate* unmanaged[Cdecl]<AVFrameView**, void>)t[k++];
        _av_frame_unref = (delegate* unmanaged[Cdecl]<AVFrameView*, void>)t[k++];
        _av_frame_copy_props = (delegate* unmanaged[Cdecl]<AVFrameView*, AVFrameView*, int>)t[k++];
        _av_frame_get_side_data = (delegate* unmanaged[Cdecl]<AVFrameView*, int, AVFrameSideDataView*>)t[k++];
        _av_opt_set_int = (delegate* unmanaged[Cdecl]<void*, byte*, long, int, int>)t[k++];
        _swscale_version = (delegate* unmanaged[Cdecl]<uint>)t[k++];
        _sws_alloc_context = (delegate* unmanaged[Cdecl]<void*>)t[k++];
        _sws_free_context = (delegate* unmanaged[Cdecl]<void**, void>)t[k++];
        _sws_scale_frame = (delegate* unmanaged[Cdecl]<void*, AVFrameView*, AVFrameView*, int>)t[k++];
    }

    internal static int avformat_seek_file(AVFormatContextView* s, int streamIndex, long minTs, long ts, long maxTs, int flags)
        => _avformat_seek_file(s, streamIndex, minTs, ts, maxTs, flags);
    internal static int av_read_frame(AVFormatContextView* s, AVPacketView* pkt) => _av_read_frame(s, pkt);
    internal static void* avcodec_find_decoder(int codecId) => _avcodec_find_decoder(codecId);
    internal static void* avcodec_alloc_context3(void* codec) => _avcodec_alloc_context3(codec);
    internal static void avcodec_free_context(void** avctx) => _avcodec_free_context(avctx);
    internal static int avcodec_parameters_to_context(void* avctx, AVCodecParametersView* par) => _avcodec_parameters_to_context(avctx, par);
    internal static int avcodec_open2(void* avctx, void* codec, void** options) => _avcodec_open2(avctx, codec, options);
    internal static int avcodec_send_packet(void* avctx, AVPacketView* pkt) => _avcodec_send_packet(avctx, pkt);
    internal static int avcodec_receive_frame(void* avctx, AVFrameView* frame) => _avcodec_receive_frame(avctx, frame);
    internal static void avcodec_flush_buffers(void* avctx) => _avcodec_flush_buffers(avctx);
    internal static AVPacketView* av_packet_alloc() => _av_packet_alloc();
    internal static void av_packet_free(AVPacketView** pkt) => _av_packet_free(pkt);
    internal static void av_packet_unref(AVPacketView* pkt) => _av_packet_unref(pkt);
    internal static AVPacketSideDataView* av_packet_side_data_get(void* sd, int nbSd, int type) => _av_packet_side_data_get(sd, nbSd, type);
    internal static AVFrameView* av_frame_alloc() => _av_frame_alloc();
    internal static void av_frame_free(AVFrameView** frame) => _av_frame_free(frame);
    internal static void av_frame_unref(AVFrameView* frame) => _av_frame_unref(frame);
    internal static int av_frame_copy_props(AVFrameView* dst, AVFrameView* src) => _av_frame_copy_props(dst, src);
    internal static AVFrameSideDataView* av_frame_get_side_data(AVFrameView* frame, int type) => _av_frame_get_side_data(frame, type);
    internal static int av_opt_set_int(void* obj, string name, long value, int searchFlags)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(name) + 1];
        System.Text.Encoding.UTF8.GetBytes(name, 0, name.Length, bytes, 0);
        fixed (byte* p = bytes) return _av_opt_set_int(obj, p, value, searchFlags);
    }
    internal static uint swscale_version() => _swscale_version();
    internal static void* sws_alloc_context() => _sws_alloc_context();
    internal static void sws_free_context(void** ctx) => _sws_free_context(ctx);
    internal static int sws_scale_frame(void* ctx, AVFrameView* dst, AVFrameView* src) => _sws_scale_frame(ctx, dst, src);
}

internal sealed unsafe class CodecContextHandle : SafeHandle
{
    public CodecContextHandle(void* ctx) : base(IntPtr.Zero, ownsHandle: true) => SetHandle((nint)ctx);
    public override bool IsInvalid => handle == IntPtr.Zero;
    public void* Context => (void*)handle;
    protected override bool ReleaseHandle()
    {
        void* p = (void*)handle;
        LibAvFrame.avcodec_free_context(&p);
        return true;
    }
}

internal sealed unsafe class PacketHandle : SafeHandle
{
    public PacketHandle(AVPacketView* pkt) : base(IntPtr.Zero, ownsHandle: true) => SetHandle((nint)pkt);
    public override bool IsInvalid => handle == IntPtr.Zero;
    public AVPacketView* Packet => (AVPacketView*)handle;
    protected override bool ReleaseHandle()
    {
        AVPacketView* p = (AVPacketView*)handle;
        LibAvFrame.av_packet_free(&p);
        return true;
    }
}

internal sealed unsafe class FrameHandle : SafeHandle
{
    public FrameHandle(AVFrameView* frame) : base(IntPtr.Zero, ownsHandle: true) => SetHandle((nint)frame);
    public override bool IsInvalid => handle == IntPtr.Zero;
    public AVFrameView* Frame => (AVFrameView*)handle;
    protected override bool ReleaseHandle()
    {
        AVFrameView* p = (AVFrameView*)handle;
        LibAvFrame.av_frame_free(&p);
        return true;
    }
}

internal sealed unsafe class SwsContextHandle : SafeHandle
{
    public SwsContextHandle(void* ctx) : base(IntPtr.Zero, ownsHandle: true) => SetHandle((nint)ctx);
    public override bool IsInvalid => handle == IntPtr.Zero;
    public void* Context => (void*)handle;
    protected override bool ReleaseHandle()
    {
        void* p = (void*)handle;
        LibAvFrame.sws_free_context(&p);
        return true;
    }
}
