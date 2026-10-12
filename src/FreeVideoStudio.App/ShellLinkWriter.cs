
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FreeVideoStudio.App;

/// <summary>
/// NOPOWERSHELL_01 — Windows shortcuts (.lnk) are written and read through the Shell's own
/// <c>IShellLinkW</c> / <c>IPersistFile</c> COM objects, called directly through their vtables
/// (the same NativeAOT-safe technique as <c>Interop\D3D11Interop.cs</c>).
///
/// <para>
/// This used to start <c>powershell.exe</c> (one copy with <c>-EncodedCommand</c>) for every
/// shortcut. A program that spawns hidden, scrambled PowerShell during installation is exactly
/// what antivirus heuristics are trained to flag (MITRE T1059.001), and it was one of the reasons
/// new releases were reported as a trojan. The result on disk is identical: WScript.Shell's
/// CreateShortcut is itself a thin wrapper over this same ShellLink object.
/// </para>
///
/// The COM objects run on a private STA thread (the ShellLink class is apartment-threaded), so
/// callers on any thread or apartment are safe. Every failure surfaces as <see cref="IOException"/>,
/// which is what the upgrade code already treats as "retry later".
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ShellLinkWriter
{
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010B-0000-0000-C000-000000000046");

    private const uint ClsctxInprocServer = 0x1;
    private const uint StgmRead = 0x0;
    private const int MaxLinkPath = 32768;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    private const int SlotQueryInterface = 0;
    private const int SlotRelease = 2;
    private const int SlotGetPath = 3;
    private const int SlotSetDescription = 7;
    private const int SlotSetWorkingDirectory = 9;
    private const int SlotSetIconLocation = 17;
    private const int SlotSetPath = 20;
    private const int SlotLoad = 5;
    private const int SlotSave = 6;

    [LibraryImport("ole32.dll")]
    private static unsafe partial int CoCreateInstance(Guid* rclsid, void* pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);

    public static async Task CreateAsync(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconLocation,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(shortcutPath))
        {
            throw new ArgumentException("shortcutPath must be provided.", nameof(shortcutPath));
        }

        if (string.IsNullOrEmpty(targetPath))
        {
            throw new ArgumentException("targetPath must be provided.", nameof(targetPath));
        }

        string? directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string fullShortcutPath = Path.GetFullPath(shortcutPath);
        await RunOnStaThreadAsync(
            () => WriteLink(fullShortcutPath, targetPath, workingDirectory, iconLocation, description),
            "create",
            cancellationToken).ConfigureAwait(false);

        if (!File.Exists(shortcutPath))
        {
            throw new FileNotFoundException("Windows reported success but the shortcut file was not created.", shortcutPath);
        }
    }

    /// <summary>The shortcut's target path, or an empty string when it has none.</summary>
    public static Task<string> ReadTargetAsync(string shortcutPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(shortcutPath))
        {
            throw new ArgumentException("shortcutPath must be provided.", nameof(shortcutPath));
        }

        string fullShortcutPath = Path.GetFullPath(shortcutPath);
        return RunOnStaThreadAsync(() => ReadLinkTarget(fullShortcutPath), "read", cancellationToken);
    }

    private static async Task<T> RunOnStaThreadAsync<T>(Func<T> work, string operation, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(work()); }
            catch (Exception ex)
            {
                RuntimeLog.Debug("ShellLink", ex.Message);
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "FVS ShellLink"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            return await completion.Task.WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IOException($"Windows shortcut operation ({operation}) timed out.");
        }
    }

    private static unsafe bool WriteLink(string shortcutPath, string targetPath, string workingDirectory, string iconLocation, string? description)
    {
        void* link = null;
        void* persist = null;
        try
        {
            link = CreateShellLink();
            void** vtable = *(void***)link;

            CallString(link, vtable[SlotSetPath], targetPath, "SetPath");
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                CallString(link, vtable[SlotSetWorkingDirectory], workingDirectory, "SetWorkingDirectory");
            }

            if (!string.IsNullOrEmpty(description))
            {
                CallString(link, vtable[SlotSetDescription], description, "SetDescription");
            }

            if (!string.IsNullOrEmpty(iconLocation))
            {
                (string iconPath, int iconIndex) = SplitIconLocation(iconLocation);
                fixed (char* p = iconPath)
                {
                    Check(((delegate* unmanaged<void*, char*, int, int>)vtable[SlotSetIconLocation])(link, p, iconIndex), "SetIconLocation");
                }
            }

            persist = QueryPersistFile(link);
            void** persistTable = *(void***)persist;
            fixed (char* p = shortcutPath)
            {
                Check(((delegate* unmanaged<void*, char*, int, int>)persistTable[SlotSave])(persist, p, 1), "IPersistFile.Save");
            }

            return true;
        }
        finally
        {
            Release(persist);
            Release(link);
        }
    }

    private static unsafe string ReadLinkTarget(string shortcutPath)
    {
        void* link = null;
        void* persist = null;
        try
        {
            link = CreateShellLink();
            persist = QueryPersistFile(link);
            void** persistTable = *(void***)persist;
            fixed (char* p = shortcutPath)
            {
                Check(((delegate* unmanaged<void*, char*, uint, int>)persistTable[SlotLoad])(persist, p, StgmRead), "IPersistFile.Load");
            }

            void** vtable = *(void***)link;
            char[] buffer = new char[MaxLinkPath];
            fixed (char* b = buffer)
            {
                Check(((delegate* unmanaged<void*, char*, int, void*, uint, int>)vtable[SlotGetPath])(link, b, buffer.Length, null, 0), "GetPath");
                return new string(b).Trim();
            }
        }
        finally
        {
            Release(persist);
            Release(link);
        }
    }

    private static unsafe void* CreateShellLink()
    {
        Guid clsid = ClsidShellLink;
        Guid iid = IidShellLinkW;
        void* link = null;
        Check(CoCreateInstance(&clsid, null, ClsctxInprocServer, &iid, &link), "CoCreateInstance(ShellLink)");
        if (link == null)
        {
            throw new IOException("Windows did not return a shortcut object.");
        }

        return link;
    }

    private static unsafe void* QueryPersistFile(void* link)
    {
        Guid iid = IidPersistFile;
        void* persist = null;
        void** vtable = *(void***)link;
        Check(((delegate* unmanaged<void*, Guid*, void**, int>)vtable[SlotQueryInterface])(link, &iid, &persist), "QueryInterface(IPersistFile)");
        if (persist == null)
        {
            throw new IOException("Windows did not return the shortcut's file interface.");
        }

        return persist;
    }

    private static unsafe void CallString(void* instance, void* method, string value, string name)
    {
        fixed (char* p = value)
        {
            Check(((delegate* unmanaged<void*, char*, int>)method)(instance, p), name);
        }
    }

    private static unsafe void Release(void* instance)
    {
        if (instance == null)
        {
            return;
        }

        void** vtable = *(void***)instance;
        _ = ((delegate* unmanaged<void*, uint>)vtable[SlotRelease])(instance);
    }

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new IOException($"Windows shortcut call {operation} failed (HRESULT 0x{hresult:X8}).");
        }
    }

    /// <summary>"C:\app.exe,0" → ("C:\app.exe", 0); a location without an index means index 0.</summary>
    internal static (string Path, int Index) SplitIconLocation(string iconLocation)
    {
        int comma = iconLocation.LastIndexOf(',');
        if (comma > 0 &&
            int.TryParse(iconLocation.AsSpan(comma + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return (iconLocation[..comma].Trim(), index);
        }

        return (iconLocation.Trim(), 0);
    }
}
