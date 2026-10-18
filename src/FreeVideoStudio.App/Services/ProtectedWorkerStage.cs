using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// WORKERSTAGE_01 — THE ELEVATED UPDATE WORKER RUNS ONLY FROM A FOLDER ITS OWN USER CANNOT CHANGE
/// (05 SYS-UPGRADE-TRUST).
///
/// <para>⚠️ WHY. The worker — the only part of an install or update that runs as administrator — used
/// to be copied into <c>%LOCALAPPDATA%\FreeVideoStudioUpdates\Stage\&lt;id&gt;</c> and started from there
/// with <c>Verb = "runas"</c>. Every program the user runs can write to that folder. Between the copy
/// and the user's "Yes" on the Windows prompt, anything running as the user could swap the file or drop
/// a DLL beside it, and the user — who EXPECTS a prompt at that moment, and whose prompt says "Unknown
/// publisher" for this certificate anyway — would hand it administrator rights.</para>
///
/// <para>The copy now lives in <c>%ProgramData%\FreeVideoStudio-Worker-&lt;random&gt;\FreeVideoStudio.exe</c>:</para>
/// <list type="bullet">
/// <item>The folder is created with a PROTECTED access list (nothing inherited): SYSTEM and
///   Administrators full control; the user read/execute only. The <c>OWNER RIGHTS</c> entry takes away the
///   rights an owner normally keeps implicitly (changing the access list), and gives the creator ONLY
///   "add a file". Standard users have no "delete child" right on <c>%ProgramData%</c> itself, so the folder
///   cannot be renamed or replaced either.</item>
/// <item>The program file is created WITH its final access list (read/execute for the user) by this
///   process, which keeps the one write handle it was granted at creation; nobody else can ever open it
///   for writing, delete it or rename it.</item>
/// <item>The protection is PROVEN, not assumed: after the file is written, writing, deleting and
///   renaming are attempted and must all be refused; the folder must hold exactly that one file; the
///   copy must hash to the bytes that were written and (Release builds) carry the publisher's
///   signature. Any doubt stops the update before Windows is asked — there is no fallback to the
///   user-writable folder.</item>
/// <item>A read handle that denies writers is held until the elevated process has started.</item>
/// <item>The worker refuses to run elevated from anywhere else (<see cref="IsProtectedWorkerPath"/>,
///   checked in <c>UpgradeInstallWorker.RunAsync</c>) and restricts its own DLL search to System32
///   (<c>NativeHelpers.RestrictDllSearchToSystem32</c>), so a file added to the folder is never loaded.</item>
/// </list>
/// <para>The user cannot delete these folders; the elevated worker removes old ones at the end of every
/// install (<see cref="SweepStale"/>, from <c>MaintainAsync</c>) and the uninstaller removes them all.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedWorkerStage : IDisposable
{
    internal const string FolderPrefix = "FreeVideoStudio-Worker-";

    private FileStream? _denyWriters;

    private ProtectedWorkerStage(string folder, FileStream denyWriters)
    {
        FolderPath = folder;
        ExecutablePath = Path.Combine(folder, InstallPayload.ExecutableName);
        _denyWriters = denyWriters;
    }

    /// <summary>The protected folder.</summary>
    public string FolderPath { get; }

    /// <summary>The verified worker copy to start with <c>Verb = "runas"</c>.</summary>
    public string ExecutablePath { get; }

    /// <summary><c>%ProgramData%</c>: the only per-machine folder a standard user may create in but not rename in.</summary>
    internal static string Root => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>
    /// Copies <paramref name="sourceExecutable"/> into a new protected folder and proves the protection.
    /// Throws <see cref="IOException"/> (plain English) when Windows does not allow it.
    /// </summary>
    public static ProtectedWorkerStage Create(string sourceExecutable)
    {
        byte[] image = File.ReadAllBytes(sourceExecutable);
        string expected = Convert.ToHexString(SHA256.HashData(image));
        string folder = Path.Combine(Root, FolderPrefix + Guid.NewGuid().ToString("N"));
        string exe = Path.Combine(folder, InstallPayload.ExecutableName);
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new IOException("The current Windows account could not be identified.");

        try
        {
            new DirectoryInfo(folder).Create(FolderSecurity(user));
            if (Directory.EnumerateFileSystemEntries(folder).Any())
                throw new IOException("the new folder was not empty");

            using (FileStream write = new FileInfo(exe).Create(FileMode.CreateNew,
                       FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize,
                       FileShare.None, 1 << 16, FileOptions.None, FileSecurity(user)))
            {
                write.Write(image);
                write.Flush(flushToDisk: true);
            }

            ProveLocked(folder, exe);

            var denyWriters = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                string[] entries = Directory.GetFileSystemEntries(folder);
                if (entries.Length != 1 || !string.Equals(Path.GetFileName(entries[0]), InstallPayload.ExecutableName, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("unexpected files appeared beside the installer copy");
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(denyWriters)), expected, StringComparison.Ordinal))
                    throw new IOException("the installer copy does not match the original");
                RequirePublisher(exe);
                RuntimeLog.Info("Upgrade", $"WORKERSTAGE_01: protected worker copy ready in {folder}.");
                return new ProtectedWorkerStage(folder, denyWriters);
            }
            catch
            {
                denyWriters.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        {
            RuntimeLog.Fail("Upgrade", $"WORKERSTAGE_01: the protected worker copy could not be prepared in {folder}: {ex}");
            throw new IOException(
                "Windows did not allow a protected copy of the installer to be prepared, so it was not started with administrator rights ("
                + ex.Message + "). Nothing was changed.", ex);
        }
    }

    /// <summary>
    /// True only for <c>%ProgramData%\FreeVideoStudio-Worker-&lt;32 hex&gt;\FreeVideoStudio.exe</c>.
    /// The worker checks its own path with this before doing anything as administrator.
    /// </summary>
    internal static bool IsProtectedWorkerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            string? folder = Path.GetDirectoryName(full);
            string? parent = folder is null ? null : Path.GetDirectoryName(folder);
            string name = folder is null ? string.Empty : Path.GetFileName(folder);
            return string.Equals(Path.GetFileName(full), InstallPayload.ExecutableName, StringComparison.OrdinalIgnoreCase)
                && parent is not null
                && string.Equals(parent.TrimEnd('\\', '/'), Path.GetFullPath(Root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && name.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase)
                && name.Length == FolderPrefix.Length + 32
                && name[FolderPrefix.Length..].All(Uri.IsHexDigit);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    /// <summary>Every worker folder that exists (for the elevated sweep and the uninstaller).</summary>
    internal static IEnumerable<string> ExistingFolders()
    {
        if (!Directory.Exists(Root)) return [];
        try
        {
            return Directory.EnumerateDirectories(Root, FolderPrefix + "*")
                .Where(d => IsProtectedWorkerPath(Path.Combine(d, InstallPayload.ExecutableName)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
            return [];
        }
    }

    /// <summary>
    /// Elevated only. Removes every worker folder except the one this process runs from. A folder still
    /// in use is left for the next install. Returns how many were removed.
    /// </summary>
    internal static int SweepStale(string? runningExecutable)
    {
        string? keep = string.IsNullOrWhiteSpace(runningExecutable) ? null : Path.GetDirectoryName(Path.GetFullPath(runningExecutable));
        int removed = 0;
        foreach (string folder in ExistingFolders())
        {
            if (keep is not null && string.Equals(folder.TrimEnd('\\'), keep.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(folder, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.Info("Upgrade", $"WORKERSTAGE_01: an old worker folder is still in use and is removed next time ({ex.Message}).");
            }
        }
        return removed;
    }

    /// <summary>Releases the deny-writers handle. Call right after the elevated process has started.</summary>
    public void Dispose()
    {
        _denyWriters?.Dispose();
        _denyWriters = null;
    }

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>S-1-3-4: replaces the owner's implicit READ_CONTROL/WRITE_DAC with exactly what it grants.</summary>
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    private const FileSystemRights ReadOnly = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    private static DirectorySecurity FolderSecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags Children = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, Children, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, Children, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(OwnerRights, ReadOnly | FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(user, ReadOnly, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity FileSecurity(SecurityIdentifier user)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(OwnerRights, ReadOnly, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(user, ReadOnly, AccessControlType.Allow));
        return security;
    }

    /// <summary>Each of these must be REFUSED. One that succeeds means the folder is not protected.</summary>
    private static void ProveLocked(string folder, string exe)
    {
        Refused("write to the installer copy", () => { using var _ = new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Refused("change the folder's permissions", () =>
        {
            var info = new DirectoryInfo(folder);
            DirectorySecurity current = info.GetAccessControl();
            current.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(current);
        });
        Refused("delete the installer copy", () => File.Delete(exe));
        string moved = folder + "-moved";
        try
        {
            Refused("rename the folder", () => Directory.Move(folder, moved));
        }
        finally
        {
            if (Directory.Exists(moved) && !Directory.Exists(folder))
            {
                try { Directory.Move(moved, folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Swallowed(ex); }
            }
        }
    }

    private static void Refused(string what, Action attempt)
    {
        try
        {
            attempt();
        }
        catch (UnauthorizedAccessException)
        {
            RuntimeLog.Info("Upgrade", $"WORKERSTAGE_01: '{what}' was refused, as required.");
            return;
        }
        catch (IOException ex)
        {
            RuntimeLog.Info("Upgrade", $"WORKERSTAGE_01: '{what}' was refused ({ex.Message}).");
            return;
        }
        throw new IOException($"the protected folder still allowed this account to {what}");
    }

    private static void RequirePublisher(string exe)
    {
        var verdict = AuthenticodeVerifier.EvaluateUpdateCandidate(exe, Environment.ProcessPath, out string detail);
        if (verdict == AuthenticodeVerifier.TrustVerdict.Trusted) return;
#if DEBUG
        if (verdict == AuthenticodeVerifier.TrustVerdict.NoAnchor)
        {
            RuntimeLog.Info("Upgrade", "WORKERSTAGE_01: unsigned Debug build, publisher check skipped: " + detail);
            return;
        }
#endif
        throw new IOException("the installer copy is not signed by the publisher: " + detail);
    }
}
