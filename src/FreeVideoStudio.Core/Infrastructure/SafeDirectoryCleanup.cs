using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// CLEANUPBOUND_01 — Windows cleanup through pinned, non-reparse handles. Every ancestor stays
/// open without delete sharing until its descendants are gone. Delete disposition is set on the
/// verified object itself, never by reopening its name. Locked/linked entries fail closed.
/// </summary>
public static class SafeDirectoryCleanup
{
    public static void DeleteTree(string path) => Delete(path, directory: true, null);
    public static void DeleteFile(string path) => Delete(path, directory: false, null);
    internal static void DeleteTree(string path, Action<string> onPinned) => Delete(path, true, onPinned);

    private static void Delete(string path, bool directory, Action<string>? onPinned)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Handle-bound cleanup requires Windows.");
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Cleanup requires an absolute path.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(full) ?? throw new IOException("Cleanup has no filesystem root.");
        if (full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup cannot remove a volume or share root.");

        var ancestors = new List<SafeFileHandle>();
        try
        {
            var parents = new Stack<string>();
            for (string? parent = Path.GetDirectoryName(full); parent != null; parent = Path.GetDirectoryName(parent))
                parents.Push(parent);
            while (parents.TryPop(out string? parent))
            {
                SafeFileHandle? handle = Open(parent, delete: false, out var info);
                if (handle == null) return;
                ancestors.Add(handle);
                RequireKind(info, directory: true);
            }
            DeleteEntry(full, directory, 0, onPinned);
        }
        finally
        {
            for (int i = ancestors.Count - 1; i >= 0; i--) ancestors[i].Dispose();
        }
    }

    private static void DeleteEntry(string path, bool? directory, int depth, Action<string>? onPinned)
    {
        if (depth > 256) throw new IOException("Cleanup directory depth exceeds its safety limit.");
        using SafeFileHandle? handle = Open(path, delete: true, out var info);
        if (handle == null) return;
        if (directory.HasValue) RequireKind(info, directory.Value);
        onPinned?.Invoke(path);
        bool isDirectory = (info.Attributes & (uint)FileAttributes.Directory) != 0;
        if (isDirectory)
        {
            // The pinned parent cannot be exchanged for a junction during enumeration or child open.
            // New entries may make final disposition fail; they never trigger an unverified retry.
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
                DeleteEntry(child, null, depth + 1, onPinned);
        }
        if ((info.Attributes & (uint)FileAttributes.ReadOnly) != 0)
        {
            var basic = new FileBasicInfo { Attributes = info.Attributes & ~(uint)FileAttributes.ReadOnly };
            if (basic.Attributes == 0) basic.Attributes = (uint)FileAttributes.Normal;
            if (!SetBasic(handle, 0, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>())) ThrowLastError(path);
        }
        byte disposition = 1;
        if (!SetDisposition(handle, 4, ref disposition, 1)) ThrowLastError(path);
    }

    private static SafeFileHandle? Open(string path, bool delete, out ByHandleInfo info)
    {
        const uint ReadAttributes = 0x80, WriteAttributes = 0x100, DeleteAccess = 0x10000;
        const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
        var handle = CreateFile(path, ReadAttributes | (delete ? DeleteAccess | WriteAttributes : 0),
            (uint)FileShare.Read, IntPtr.Zero, 3, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            info = default;
            if (error is 2 or 3) return null;
            throw new IOException($"Cleanup could not lock '{path}': {new Win32Exception(error).Message}");
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out info)) ThrowLastError(path);
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cleanup refused a linked file or directory: {path}");
            // Changing attributes on a multiply-linked file would affect a name outside this tree.
            if (delete && info.Links > 1 && (info.Attributes & (uint)FileAttributes.ReadOnly) != 0)
                throw new IOException($"Cleanup refused a read-only hard-linked file: {path}");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RequireKind(ByHandleInfo info, bool directory)
    {
        if (((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new IOException("Cleanup target changed its file/directory type.");
    }

    private static void ThrowLastError(string path) =>
        throw new IOException($"Cleanup preserved '{path}': {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long Creation, Access, Write, Change;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleInfo info);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle file, int infoClass, ref byte info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBasic(SafeFileHandle file, int infoClass, ref FileBasicInfo info, uint size);
}
