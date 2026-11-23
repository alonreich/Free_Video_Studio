using System.Runtime.InteropServices;

namespace FreeVideoStudio.Core.Infrastructure;

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
