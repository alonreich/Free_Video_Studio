using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeVideoStudio.Core.Media;

public static partial class MpvWrapper
{
    private const string LibraryName = "libmpv-2.dll";

    [LibraryImport(LibraryName)]
    public static partial nint mpv_create();

    [LibraryImport(LibraryName)]
    public static partial int mpv_initialize(nint ctx);

    [LibraryImport(LibraryName)]
    public static partial void mpv_terminate_destroy(nint ctx);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_command_string(nint ctx, string args);

    [LibraryImport(LibraryName)]
    private static partial int mpv_command_async(nint ctx, ulong replyUserdata, nint arguments);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_get_property_async(nint ctx, ulong replyUserdata, string name, MpvFormat format);

    public static int CommandAsync(nint handle, ulong replyUserdata, string[] arguments)
    {
        nint[] strings = new nint[arguments.Length];
        nint argv = Marshal.AllocHGlobal((arguments.Length + 1) * nint.Size);
        try
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                strings[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
                Marshal.WriteIntPtr(argv, i * nint.Size, strings[i]);
            }
            Marshal.WriteIntPtr(argv, arguments.Length * nint.Size, nint.Zero);
            return mpv_command_async(handle, replyUserdata, argv);
        }
        finally
        {
            foreach (nint value in strings) if (value != nint.Zero) Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(argv);
        }
    }

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_string(nint ctx, string name, string data);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(nint ctx, string name, string data);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint mpv_get_property_string(nint ctx, string name);

    [LibraryImport(LibraryName)]
    public static partial void mpv_free(nint data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(nint ctx, ulong reply_userdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_unobserve_property(nint ctx, ulong id);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_wait_event(nint ctx, double timeout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(nint ctx);

    public enum MpvEventId : int
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        GetPropertyReply = 3,
        SetPropertyReply = 4,
        CommandReply = 5,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        Seek = 20,
        PlaybackRestart = 21,
        PropertyChange = 22,
    }

    public enum MpvFormat : int
    {
        None = 0,
        String = 1,
        Double = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public nint Name;
        public MpvFormat Format;
        public nint Data;
    }

    public static void SafeDestroy(ref nint handle)
    {
        if (handle == nint.Zero) return;
        mpv_terminate_destroy(handle);
        handle = nint.Zero;
    }

    public static void Stop(nint handle)
    {
        if (handle != nint.Zero)
            mpv_command_string(handle, "stop");
    }

    public static string? GetPropertyString(nint handle, string name)
    {
        if (handle == nint.Zero) return null;
        nint ptr = mpv_get_property_string(handle, name);
        if (ptr == nint.Zero) return null;
        string? result = Marshal.PtrToStringUTF8(ptr);
        mpv_free(ptr);
        return result;
    }

    public static double GetDuration(nint handle)
    {
        string? s = GetPropertyString(handle, "duration");
        return double.TryParse(s, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    public static ulong ObserveProperty(nint handle, string name, MpvFormat format)
    {
        if (handle == nint.Zero) return 0;
        int id = mpv_observe_property(handle, 0, name, (int)format);
        return (ulong)id;
    }

    public static void UnobserveProperty(nint handle, ulong id)
    {
        if (handle != nint.Zero && id != 0)
            mpv_unobserve_property(handle, id);
    }

    public static MpvEvent WaitEvent(nint handle, double timeoutSeconds = -1.0)
    {
        if (handle == nint.Zero)
            return default;

        nint ptr = mpv_wait_event(handle, timeoutSeconds);
        if (ptr == nint.Zero)
            return default;

        return Marshal.PtrToStructure<MpvEvent>(ptr);
    }

    public static MpvEventProperty ReadEventProperty(MpvEvent ev)
    {
        if (ev.Data == nint.Zero)
            return default;
        return Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
    }

    public static string? GetEventPropertyName(MpvEventProperty prop)
    {
        return prop.Name == nint.Zero ? null : Marshal.PtrToStringUTF8(prop.Name);
    }

    public static double ReadEventPropertyDouble(MpvEventProperty prop)
    {
        if (prop.Data == nint.Zero) return 0;
        return Marshal.PtrToStructure<double>(prop.Data);
    }
}
