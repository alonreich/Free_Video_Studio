
using System.Runtime.InteropServices;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// FASTINSTALL_01 — a second name for an existing file on the same NTFS volume. Instant, and it uses
/// no extra disk space. Used ONLY between folders the elevated installer owns (the installed program
/// and its own staging area under Program Files): a link shares the bytes, so it must never point at
/// a file a normal user can still write. Anything that fails (other volume, FAT, non-Windows) returns
/// false and the caller copies instead.
/// </summary>
public static partial class HardLink
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkNative(string newFile, string existingFile, nint securityAttributes);

    public static bool TryCreate(string newFile, string existingFile)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(newFile)), Path.GetPathRoot(Path.GetFullPath(existingFile)),
                StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
            return CreateHardLinkNative(newFile, existingFile, nint.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
    }
}
