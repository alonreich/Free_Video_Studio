// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeVideoStudio.Core.Media.Native;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// LIBAVPROBE_01 — THE ONLY FILE IN THE PRODUCT THAT TOUCHES A LIBAV POINTER.
//
// Source of truth: FFmpeg git commit 1e5c65f5392b7d4daca0282840750987fef0a582
// ("avutil/dict: fix memleak in av_dict_set()", 2025-04-07), the revision behind the bundled
// build N-119166-g1e5c65f539-20250408 (Lavf 62.0.100, Lavc 62.0.101, Lavu 60.1.100).
// NOT FFmpeg 8.0 and NOT FFmpeg.AutoGen. Every offset below was produced by compiling that
// revision's public headers with x86_64-w64-mingw32-gcc (the Win64 ABI the DLLs were built for)
// and reading offsetof()/sizeof() back out of the object file. Every imported symbol was checked
// against the export tables of binaries\avformat-62.dll, avcodec-62.dll and avutil-60.dll
// (objdump -p; see LibAv.Imports for the list).
//
// ⚠️ A WRONG OFFSET IS A SILENT WRONG ANSWER OR AN ACCESS VIOLATION, SO THREE GUARDS STAND IN FRONT
// OF EVERY READ (LibAvLibrary.TryEnsureLoaded + LibAvMediaBackend.ValidateLayout):
//   1. avformat/avcodec/avutil_version() must report EXACTLY the major.minor these offsets came
//      from (micro may differ: micro bumps do not change public struct layout).
//   2. AVFormatContext.av_class must equal avformat_get_class(), and every AVStream.av_class must
//      equal av_stream_get_class() — a shifted layout reads garbage there first.
//   3. AVStream.index must equal its slot and AVStream.codecpar must be non-null.
// Any guard failing disables the native backend for the process; MediaMetadataProbe then uses the
// ffprobe subprocess exactly as before. A newer FFmpeg drop therefore DEGRADES TO THE OLD PATH,
// it never misreads.
//
// Why unsafe: libav's API is pointer-to-struct and the entry points are unmanaged function pointers
// (see LibAv). The alternative (FFmpeg.AutoGen-style generated bindings) targets a different FFmpeg
// ABI and uses reflection-era marshalling. Pointer reads are
// confined to the *View structs below and to LibAvMediaBackend; no pointer leaves this namespace.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Constants copied from the 1e5c65f539 headers (values verified by compiling them).</summary>
internal static class LibAvConstants
{
    public const long AV_NOPTS_VALUE = long.MinValue;           // libavutil/avutil.h
    public const int AV_TIME_BASE = 1_000_000;                  // libavutil/avutil.h
    public const int AVMEDIA_TYPE_VIDEO = 0;                    // enum AVMediaType
    public const int AVMEDIA_TYPE_AUDIO = 1;
    public const int AVCOL_RANGE_UNSPECIFIED = 0;               // libavutil/pixfmt.h
    public const int AVCOL_PRI_UNSPECIFIED = 2;
    public const int AVCOL_TRC_UNSPECIFIED = 2;
    public const int AVCOL_SPC_UNSPECIFIED = 2;
    public const int AV_DICT_DONT_OVERWRITE = 16;               // libavutil/dict.h
    public const int AV_LOG_QUIET = -8;                         // libavutil/log.h
    public const int AVERROR_EXIT = -1414092869;                // FFERRTAG('E','X','I','T')
    public const int AVERROR_ENOMEM = -12;

    /// <summary>AV_VERSION_INT(major, minor, 0) — the version guard ignores micro.</summary>
    public static uint MajorMinor(uint version) => version & 0xFFFF_FF00u;
    public static uint Version(uint major, uint minor) => (major << 16) | (minor << 8);

    public static readonly uint ExpectedAvFormat = Version(62, 0);
    public static readonly uint ExpectedAvCodec = Version(62, 0);
    public static readonly uint ExpectedAvUtil = Version(60, 1);

    // ── LIBAVFRAME_01 — single-frame decode tier (values read back from the compiled 1e5c65f539 headers) ──
    public static readonly uint ExpectedSwScale = Version(9, 0);   // libswscale/version*.h: 9.0.100
    public const int AVERROR_EOF = -541478725;                   // FFERRTAG('E','O','F',' ')
    public const int AVERROR_EAGAIN = -11;                       // AVERROR(EAGAIN), Win64 CRT errno 11
    public const int AV_PIX_FMT_NONE = -1;                       // enum AVPixelFormat
    public const int AV_PIX_FMT_BGRA = 28;
    public const int AV_PIX_FMT_RGB24 = 2;
    public const int AV_FRAME_DATA_DISPLAYMATRIX = 6;            // enum AVFrameSideDataType
    public const int AV_PKT_DATA_FRAME_CROPPING = 36;            // enum AVPacketSideDataType
    public const int AV_FRAME_FLAG_INTERLACED = 8;               // AV_FRAME_FLAG_INTERLACED (1 << 3)
    public const int AVCHROMA_LOC_UNSPECIFIED = 0;               // enum AVChromaLocation
    public const int AVDISCARD_DEFAULT = 0;                      // enum AVDiscard
    public const int AVDISCARD_ALL = 48;
    public const int AV_DISPOSITION_DEFAULT = 1;                 // libavformat/avformat.h
    public const int AV_DISPOSITION_ATTACHED_PIC = 1024;
    public const int AVSTREAM_EVENT_FLAG_NEW_PACKETS = 2;
    public const int AVFMT_SEEK_TO_PTS = 0x4000000;              // AVInputFormat.flags
    public const int AV_CODEC_ID_NONE = 0;
}

/// <summary>struct AVRational (libavutil/rational.h). Sequential, 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVRational
{
    public int num;
    public int den;
}

/// <summary>
/// Read-only VIEW of struct AVFormatContext (libavformat/avformat.h, sizeof = 472 on Win64).
/// Only fields this backend reads or writes are declared. Never allocated from managed code.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 472)]
internal unsafe struct AVFormatContextView
{
    [FieldOffset(0)] public void* av_class;
    [FieldOffset(8)] public AVInputFormatView* iformat;   // LIBAVFRAME_01 (read only)
    [FieldOffset(44)] public uint nb_streams;
    [FieldOffset(48)] public void** streams;
    [FieldOffset(96)] public long start_time;           // LIBAVFRAME_01 — AV_TIME_BASE units, AV_NOPTS_VALUE if unknown
    [FieldOffset(104)] public long duration;            // AV_TIME_BASE units, AV_NOPTS_VALUE if unknown
    [FieldOffset(112)] public long bit_rate;
    [FieldOffset(192)] public void* metadata;           // AVDictionary*
    [FieldOffset(216)] public delegate* unmanaged[Cdecl]<void*, int> interrupt_callback;   // AVIOInterruptCB.callback
    [FieldOffset(224)] public void* interrupt_opaque;                                      // AVIOInterruptCB.opaque
}

/// <summary>Read-only VIEW of struct AVStream (sizeof = 216 on Win64).</summary>
[StructLayout(LayoutKind.Explicit, Size = 216)]
internal unsafe struct AVStreamView
{
    [FieldOffset(0)] public void* av_class;
    [FieldOffset(8)] public int index;
    [FieldOffset(16)] public AVCodecParametersView* codecpar;
    [FieldOffset(32)] public AVRational time_base;
    [FieldOffset(48)] public long duration;
    [FieldOffset(64)] public int disposition;           // LIBAVFRAME_01 — AV_DISPOSITION_* bit field
    [FieldOffset(68)] public int discard;               // LIBAVFRAME_01 — enum AVDiscard; the ONLY AVStream field ever written
    [FieldOffset(88)] public AVRational avg_frame_rate;
    [FieldOffset(200)] public int event_flags;          // LIBAVFRAME_01 — AVSTREAM_EVENT_FLAG_*
}

/// <summary>LIBAVFRAME_01 — read-only VIEW of the public head of struct AVInputFormat (libavformat/avformat.h).</summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal unsafe struct AVInputFormatView
{
    [FieldOffset(0)] public byte* name;
    [FieldOffset(16)] public int flags;                 // AVFMT_* (AVFMT_SEEK_TO_PTS)
}

/// <summary>Read-only VIEW of struct AVCodecParameters (libavcodec/codec_par.h, sizeof = 176 on Win64).</summary>
[StructLayout(LayoutKind.Explicit, Size = 176)]
internal unsafe struct AVCodecParametersView
{
    [FieldOffset(0)] public int codec_type;             // enum AVMediaType
    [FieldOffset(4)] public int codec_id;               // LIBAVFRAME_01 — enum AVCodecID
    [FieldOffset(32)] public void* coded_side_data;     // LIBAVFRAME_01 — AVPacketSideData* (passed back to libav only)
    [FieldOffset(40)] public int nb_coded_side_data;    // LIBAVFRAME_01
    [FieldOffset(44)] public int format;                // enum AVPixelFormat for video
    [FieldOffset(48)] public long bit_rate;
    [FieldOffset(72)] public int width;
    [FieldOffset(76)] public int height;
    [FieldOffset(100)] public int color_range;
    [FieldOffset(104)] public int color_primaries;
    [FieldOffset(108)] public int color_trc;
    [FieldOffset(112)] public int color_space;
    [FieldOffset(120)] public int video_delay;          // LIBAVFRAME_01 — ffmpeg's -ss dts heuristic reads it
}

/// <summary>
/// LIBAVFRAME_01 — VIEW of struct AVPacket (libavcodec/packet.h, sizeof = 104 on Win64). Allocated
/// ONLY by av_packet_alloc and freed ONLY by av_packet_free (<see cref="PacketHandle"/>).
/// </summary>
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

/// <summary>
/// LIBAVFRAME_01 — VIEW of struct AVFrame (libavutil/frame.h, sizeof = 416 on Win64; the
/// deprecated fields removed at the lavu 60 bump are NOT in this layout). Allocated ONLY by
/// av_frame_alloc, freed ONLY by av_frame_free (<see cref="FrameHandle"/>).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 416)]
internal unsafe struct AVFrameView
{
    [FieldOffset(0)] public fixed long data[8];         // uint8_t *data[AV_NUM_DATA_POINTERS] (read as addresses)
    [FieldOffset(64)] public fixed int linesize[8];
    [FieldOffset(96)] public void* extended_data;       // == &data[0] for video — layout guard
    [FieldOffset(104)] public int width;
    [FieldOffset(108)] public int height;
    [FieldOffset(116)] public int format;               // enum AVPixelFormat
    [FieldOffset(124)] public AVRational sample_aspect_ratio;
    [FieldOffset(136)] public long pts;
    [FieldOffset(144)] public long pkt_dts;
    [FieldOffset(152)] public AVRational time_base;
    [FieldOffset(276)] public int flags;                // AV_FRAME_FLAG_*
    [FieldOffset(280)] public int color_range;
    [FieldOffset(284)] public int color_primaries;
    [FieldOffset(288)] public int color_trc;
    [FieldOffset(292)] public int colorspace;
    [FieldOffset(296)] public int chroma_location;
    [FieldOffset(304)] public long best_effort_timestamp;
    [FieldOffset(408)] public long duration;
}

/// <summary>LIBAVFRAME_01 — struct AVFrameSideData (libavutil/frame.h): type@0, data@8, size@16 (sizeof 40).</summary>
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal unsafe struct AVFrameSideDataView
{
    [FieldOffset(0)] public int type;
    [FieldOffset(8)] public byte* data;
    [FieldOffset(16)] public nuint size;
}

/// <summary>LIBAVFRAME_01 — struct AVPacketSideData (libavcodec/packet.h): data@0, size@8, type@16 (sizeof 24).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal unsafe struct AVPacketSideDataView
{
    [FieldOffset(0)] public byte* data;
    [FieldOffset(8)] public nuint size;
    [FieldOffset(16)] public int type;
}

/// <summary>struct AVDictionaryEntry (libavutil/dict.h): { char *key; char *value; }.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct AVDictionaryEntryView
{
    [FieldOffset(0)] public byte* key;
    [FieldOffset(8)] public byte* value;
}

/// <summary>
/// LIBAVPROBE_02 — libav entry points as UNMANAGED FUNCTION POINTERS bound with
/// <see cref="NativeLibrary.GetExport"/> against the exact module handles <see cref="LibAvLibrary"/>
/// loaded by absolute path.
///
/// <para>
/// ⚠️ WHY NOT [LibraryImport]. The first cut used source-generated [LibraryImport] plus
/// <c>NativeLibrary.SetDllImportResolver</c>. .NET allows ONE resolver per assembly, and
/// FreeVideoStudio.Core already has another legitimate owner of that slot: the libmpv P/Invokes
/// (MediaPipelineChecks registers a resolver for <c>MpvWrapper</c>'s assembly to point libmpv-2.dll at
/// binaries\). Whoever registers second throws, so the two features could not coexist — the
/// harness proved it ("A resolver is already set for the assembly"). Function pointers need no
/// resolver at all, cannot bind a DLL found through PATH or the app directory (an unbound pointer is
/// null, never "searched for"), and are fully NativeAOT-safe: no marshalling stubs, no
/// Marshal.GetDelegateForFunctionPointer, no reflection, no runtime code generation.
/// </para>
///
/// <para>
/// Strings in: converted to NUL-terminated UTF-8 here (libav's file protocol converts UTF-8 to
/// UTF-16 on Windows). Strings out (<c>const char*</c> owned by libav) are returned as
/// <c>byte*</c> and copied with <see cref="Utf8"/>; they are never freed.
/// </para>
/// </summary>
internal static unsafe class LibAv
{
    // avformat-62.dll
    private static delegate* unmanaged[Cdecl]<uint> _avformat_version;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*> _avformat_alloc_context;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*, void> _avformat_free_context;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView**, byte*, void*, void**, int> _avformat_open_input;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*, void**, int> _avformat_find_stream_info;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView**, void> _avformat_close_input;
    private static delegate* unmanaged[Cdecl]<void*> _avformat_get_class;
    private static delegate* unmanaged[Cdecl]<void*> _av_stream_get_class;
    // avcodec-62.dll
    private static delegate* unmanaged[Cdecl]<uint> _avcodec_version;
    // avutil-60.dll
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

    /// <summary>The 21 imported symbols (all verified present in the bundled DLL export tables).</summary>
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

    /// <summary>
    /// Binds every entry point from the given module handles. Throws
    /// <see cref="EntryPointNotFoundException"/> if any symbol is missing; on throw nothing is bound
    /// (all-or-nothing), so a partially bound table can never be called.
    /// </summary>
    internal static void Bind(IReadOnlyDictionary<string, nint> modules)
    {
        nint E(string lib, string sym) => NativeLibrary.GetExport(modules[lib], sym);
        var t = Imports.Select(i => E(i.Library, i.Symbol)).ToArray();   // resolve ALL before assigning ANY
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

    /// <summary>NUL-terminated UTF-8 copy for a <c>const char*</c> argument.</summary>
    private static byte[] Utf8Z(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    internal static uint avformat_version() => _avformat_version();
    internal static AVFormatContextView* avformat_alloc_context() => _avformat_alloc_context();
    internal static void avformat_free_context(AVFormatContextView* s) => _avformat_free_context(s);

    /// <summary>On failure libav frees the context it was given and sets <c>*ps = NULL</c>
    /// (libavformat/demux.c, label <c>fail:</c>). The caller must not free it again.</summary>
    internal static int avformat_open_input(AVFormatContextView** ps, string url, void* fmt, void** options)
    {
        byte[] u = Utf8Z(url);
        fixed (byte* pu = u) return _avformat_open_input(ps, pu, fmt, options);
    }

    internal static int avformat_find_stream_info(AVFormatContextView* ic, void** options) => _avformat_find_stream_info(ic, options);

    /// <summary>Takes AVFormatContext** — closes the input, frees the context, writes NULL back.</summary>
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

    /// <summary>Copies a libav-owned NUL-terminated UTF-8 string. Never frees it.</summary>
    internal static string? Utf8(byte* s) => s == null ? null : Marshal.PtrToStringUTF8((nint)s);

    internal static string ErrorText(int err)
    {
        const int Size = 256;
        byte* buf = stackalloc byte[Size];
        return av_strerror(err, buf, Size) == 0 ? (Utf8(buf) ?? $"AVERROR {err}") : $"AVERROR {err}";
    }
}

/// <summary>
/// LIBAVPROBE_01 — interrupt state shared with libav's AVIOInterruptCB. Lives in NATIVE memory so
/// the callback never touches a managed object (no GCHandle, no captured delegate). Owned by a
/// SafeHandle that the probe disposes deterministically AFTER avformat_close_input has returned.
/// </summary>
internal sealed unsafe class InterruptStateHandle : SafeHandle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public int Cancelled;             // 1 = abort at the next libav I/O check
        public long DeadlineTimestamp;    // Stopwatch timestamp; 0 = no deadline
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

    /// <summary>The opaque pointer handed to libav. Valid until this handle is disposed.</summary>
    public void* Opaque => (void*)handle;

    /// <summary>
    /// Called from the CancellationToken registration. The probe disposes that registration
    /// (which waits for an in-flight callback) BEFORE it disposes this handle, so the native
    /// block is always alive here.
    /// </summary>
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

    /// <summary>
    /// AVIOInterruptCB.callback. [UnmanagedCallersOnly] = a real C function pointer, no delegate,
    /// no reverse-P/Invoke thunk allocated at run time (NativeAOT-safe). Must never throw.
    /// Returns non-zero to make the blocking libav call fail with AVERROR_EXIT.
    /// </summary>
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

/// <summary>
/// LIBAVPROBE_01 — owns one opened AVFormatContext. ReleaseHandle calls avformat_close_input with
/// a pointer-to-pointer, as the API requires. Created ONLY after avformat_open_input succeeded
/// (on failure libav already freed the context), so there is exactly one free per context.
/// Disposed with <c>using</c> on the probing thread — the finalizer is a backstop, not the owner.
/// </summary>
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

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LIBAVFRAME_01 — the SINGLE-FRAME DECODE tier of the same binding: entry points from
/// avformat-62 / avcodec-62 / avutil-60 (modules <see cref="LibAvLibrary"/> already loaded) plus
/// swscale-9 (loaded by <see cref="LibAvLibrary.TryEnsureFrameTierLoaded"/> from the SAME directory,
/// after avutil, which is its only libav import). Same rules as <see cref="LibAv"/>: unmanaged
/// function pointers bound with NativeLibrary.GetExport, all-or-nothing, no resolver, no
/// marshalling stubs. Every symbol below was checked against the bundled export tables
/// (objdump -p) and every signature against the 1e5c65f539 headers.
///
/// <para>
/// The tier is latched SEPARATELY from the metadata tier: a missing swscale-9.dll or a version
/// mismatch disables frame decoding only (callers keep their ffmpeg subprocess), never metadata.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal static unsafe class LibAvFrame
{
    // avformat-62.dll
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*, int, long, long, long, int, int> _avformat_seek_file;
    private static delegate* unmanaged[Cdecl]<AVFormatContextView*, AVPacketView*, int> _av_read_frame;
    // avcodec-62.dll
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
    // avutil-60.dll
    private static delegate* unmanaged[Cdecl]<AVFrameView*> _av_frame_alloc;
    private static delegate* unmanaged[Cdecl]<AVFrameView**, void> _av_frame_free;
    private static delegate* unmanaged[Cdecl]<AVFrameView*, void> _av_frame_unref;
    private static delegate* unmanaged[Cdecl]<AVFrameView*, AVFrameView*, int> _av_frame_copy_props;
    private static delegate* unmanaged[Cdecl]<AVFrameView*, int, AVFrameSideDataView*> _av_frame_get_side_data;
    private static delegate* unmanaged[Cdecl]<void*, byte*, long, int, int> _av_opt_set_int;
    // swscale-9.dll
    private static delegate* unmanaged[Cdecl]<uint> _swscale_version;
    private static delegate* unmanaged[Cdecl]<void*> _sws_alloc_context;
    private static delegate* unmanaged[Cdecl]<void**, void> _sws_free_context;
    private static delegate* unmanaged[Cdecl]<void*, AVFrameView*, AVFrameView*, int> _sws_scale_frame;

    /// <summary>The 24 imported symbols of the frame tier (all verified present in the bundled DLL export tables).</summary>
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

    /// <summary>All-or-nothing, exactly like <see cref="LibAv.Bind"/>.</summary>
    internal static void Bind(IReadOnlyDictionary<string, nint> modules)
    {
        nint E(string lib, string sym) => NativeLibrary.GetExport(modules[lib], sym);
        var t = Imports.Select(i => E(i.Library, i.Symbol)).ToArray();   // resolve ALL before assigning ANY
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

    /// <summary>int avformat_seek_file(AVFormatContext *s, int stream_index, int64_t min_ts, int64_t ts, int64_t max_ts, int flags)</summary>
    internal static int avformat_seek_file(AVFormatContextView* s, int streamIndex, long minTs, long ts, long maxTs, int flags)
        => _avformat_seek_file(s, streamIndex, minTs, ts, maxTs, flags);
    /// <summary>int av_read_frame(AVFormatContext *s, AVPacket *pkt) — on success the packet holds a reference the caller must unref.</summary>
    internal static int av_read_frame(AVFormatContextView* s, AVPacketView* pkt) => _av_read_frame(s, pkt);
    /// <summary>const AVCodec *avcodec_find_decoder(enum AVCodecID id) — static, never freed.</summary>
    internal static void* avcodec_find_decoder(int codecId) => _avcodec_find_decoder(codecId);
    internal static void* avcodec_alloc_context3(void* codec) => _avcodec_alloc_context3(codec);
    /// <summary>Takes AVCodecContext** — frees and writes NULL back.</summary>
    internal static void avcodec_free_context(void** avctx) => _avcodec_free_context(avctx);
    internal static int avcodec_parameters_to_context(void* avctx, AVCodecParametersView* par) => _avcodec_parameters_to_context(avctx, par);
    /// <summary>On return *options holds the entries the codec did NOT consume; the caller still owns (and frees) the dictionary.</summary>
    internal static int avcodec_open2(void* avctx, void* codec, void** options) => _avcodec_open2(avctx, codec, options);
    /// <summary>A NULL packet enters draining mode. Does not take ownership of the packet's reference.</summary>
    internal static int avcodec_send_packet(void* avctx, AVPacketView* pkt) => _avcodec_send_packet(avctx, pkt);
    /// <summary>Unrefs <paramref name="frame"/> first, then (on 0) hands it a new reference.</summary>
    internal static int avcodec_receive_frame(void* avctx, AVFrameView* frame) => _avcodec_receive_frame(avctx, frame);
    internal static void avcodec_flush_buffers(void* avctx) => _avcodec_flush_buffers(avctx);
    internal static AVPacketView* av_packet_alloc() => _av_packet_alloc();
    internal static void av_packet_free(AVPacketView** pkt) => _av_packet_free(pkt);
    internal static void av_packet_unref(AVPacketView* pkt) => _av_packet_unref(pkt);
    /// <summary>Returns a pointer INTO the codecpar's side-data array (owned by libav), or NULL.</summary>
    internal static AVPacketSideDataView* av_packet_side_data_get(void* sd, int nbSd, int type) => _av_packet_side_data_get(sd, nbSd, type);
    internal static AVFrameView* av_frame_alloc() => _av_frame_alloc();
    internal static void av_frame_free(AVFrameView** frame) => _av_frame_free(frame);
    internal static void av_frame_unref(AVFrameView* frame) => _av_frame_unref(frame);
    internal static int av_frame_copy_props(AVFrameView* dst, AVFrameView* src) => _av_frame_copy_props(dst, src);
    /// <summary>Returns a pointer INTO the frame's side data (owned by the frame), or NULL.</summary>
    internal static AVFrameSideDataView* av_frame_get_side_data(AVFrameView* frame, int type) => _av_frame_get_side_data(frame, type);
    internal static int av_opt_set_int(void* obj, string name, long value, int searchFlags)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(name) + 1];
        System.Text.Encoding.UTF8.GetBytes(name, 0, name.Length, bytes, 0);
        fixed (byte* p = bytes) return _av_opt_set_int(obj, p, value, searchFlags);
    }
    internal static uint swscale_version() => _swscale_version();
    /// <summary>SwsContext with every option at its AVOption default (sws_flags = bicubic) — what vf_scale starts from.</summary>
    internal static void* sws_alloc_context() => _sws_alloc_context();
    internal static void sws_free_context(void** ctx) => _sws_free_context(ctx);
    /// <summary>Dynamic-mode scale: frame properties come from the frames; allocates dst buffers when dst->data[0] is NULL.</summary>
    internal static int sws_scale_frame(void* ctx, AVFrameView* dst, AVFrameView* src) => _sws_scale_frame(ctx, dst, src);
}

/// <summary>LIBAVFRAME_01 — owns one AVCodecContext; ReleaseHandle = avcodec_free_context(&amp;p).</summary>
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

/// <summary>LIBAVFRAME_01 — owns one AVPacket; ReleaseHandle = av_packet_free(&amp;p) (which also unrefs it).</summary>
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

/// <summary>LIBAVFRAME_01 — owns one AVFrame; ReleaseHandle = av_frame_free(&amp;p) (which also unrefs its buffers).</summary>
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

/// <summary>LIBAVFRAME_01 — owns one SwsContext; ReleaseHandle = sws_free_context(&amp;p).</summary>
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
