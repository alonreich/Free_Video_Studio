using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App;

public static class SingleInstanceGuard
{
    private const string MutexNameBase = @"Local\FreeVideoStudio_MainApp_SingleInstance";
    private const string PipeNameBase = "FreeVideoStudio_MainApp_Handoff";
    private const int HandoffTimeoutMs = 2000;

    private static Mutex? _mutex;
    private static CancellationTokenSource? _listenerCts;

    private static string? _cachedUserScope;

    private static string UserScope
    {
        get
        {
            if (_cachedUserScope != null) return _cachedUserScope;

            string scope;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    scope = identity.User?.Value ?? identity.Name;
                }
                else
                {
                    scope = Environment.UserName;
                }
            }
            catch (System.Exception swallowed3)
            {
                scope = Environment.UserName;
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);
            }

            _cachedUserScope = scope.Replace('\\', '_');
            return _cachedUserScope;
        }
    }

    private static string MutexName => $"{MutexNameBase}_{UserScope}{FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.ScopeSuffix}";
    private static string PipeName => $"{PipeNameBase}_{UserScope}{FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.ScopeSuffix}";

    public static event Action<string>? VideoPathReceived;

    internal const string HandoffFailTag = "HANDOFFFAIL_01";

    private const int HandoffRetryDelayMs = 400;

    internal static Action<string> HandoffFailedNotifier { get; set; } =
        message => NativeDialog.ShowError(message, "Free Video Studio");

    internal static string HandoffFailureMessage(string videoPath)
        => "Free Video Studio is already open, but this video could not be passed to it:" + Environment.NewLine + Environment.NewLine +
           Path.GetFileName(videoPath) + Environment.NewLine + Environment.NewLine +
           "In the open window, click OPEN VIDEO and pick the file, or drag it onto the window.";

    public static bool AppliesTo(string[] args)
    {
        foreach (string a in args)
        {
            if (a.Equals("--merger", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--crop-tool", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--install", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--install-worker", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public static bool TryAcquire(string[] args)
    {
        if (!AppliesTo(args)) return true;

        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException ex)
        {
            _mutex = ex.Mutex as Mutex ?? new Mutex(true, MutexName, out createdNew);
            createdNew = true;
            RuntimeLog.Fail("SingleInstance", "Acquired an abandoned instance mutex. The previous run crashed.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SingleInstance", $"Could not create the instance mutex: {ex.Message}. Continuing unguarded.");
            return true;
        }

        if (createdNew)
        {
            RuntimeLog.Info("SingleInstance", "This process owns the Main App instance.");
            StartHandoffListener();
            return true;
        }

        RuntimeLog.Info("SingleInstance", "Another Main App instance is already running — handing off and exiting.");

        string? pendingPath = OpenWithLaunch.PendingVideoPath;
        bool delivered = TrySendHandoff(pendingPath);
        if (!delivered && !string.IsNullOrEmpty(pendingPath))
        {
            Thread.Sleep(HandoffRetryDelayMs);
            delivered = TrySendHandoff(pendingPath);
        }

        FocusExistingWindow();

        if (!string.IsNullOrEmpty(pendingPath) && !delivered)
        {
            RuntimeLog.Fail("SingleInstance",
                $"{HandoffFailTag}: could not hand the video path to the running instance; telling the user to load it there.");
            try { HandoffFailedNotifier(HandoffFailureMessage(pendingPath)); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        try { _mutex.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _mutex = null;
        return false;
    }

    public static void Release()
    {
        try { _listenerCts?.Cancel(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _listenerCts = null;

        if (_mutex == null) return;
        try { _mutex.ReleaseMutex(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _mutex.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _mutex = null;
    }

    private static void StartHandoffListener()
    {
        _listenerCts = new CancellationTokenSource();
        CancellationToken token = _listenerCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = CreateSecuredPipeServer();

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    if (!IsConnectedClientTrusted(server))
                    {
                        RuntimeLog.Fail("SingleInstance",
                            "Rejected a handoff connection from a different user account.");
                        try { server.Disconnect(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                        continue;
                    }

                    using var reader = new StreamReader(server);
                    string? payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                    if (payload != null && payload.Length > 8192)
                    {
                        RuntimeLog.Fail("SingleInstance", "Rejected an implausibly large handoff payload.");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        string path = payload.Trim();
                        if (!IsAcceptableHandoffPath(path))
                        {
                            RuntimeLog.Fail("SingleInstance", "Rejected a handoff payload that was not a local video file path.");
                            continue;
                        }
                        if (File.Exists(path))
                        {
                            RuntimeLog.Info("SingleInstance", $"Received a video handoff from another launch: {Path.GetFileName(path)}");
                            RuntimeLog.Debug("SingleInstance", $"Handoff full path: {path}");
                            try { VideoPathReceived?.Invoke(path); } catch (Exception ex) { RuntimeLog.Fail("SingleInstance", ex); }
                        }
                        else
                        {
                            RuntimeLog.Fail("SingleInstance", "Handoff payload pointed at a file that no longer exists.");
                        }
                    }
                    else
                    {
                        try { VideoPathReceived?.Invoke(string.Empty); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                    }
                }
                catch (OperationCanceledException swallowed5)
                {
                    global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed5);
                    return;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("SingleInstance", $"Handoff listener error: {ex.Message}");
                    try { await Task.Delay(500, token).ConfigureAwait(false); } catch (System.Exception swallowed2)
                    {
                        global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
                        return;
                    }
                }
            }
        }, token);
    }

    private static NamedPipeServerStream CreateSecuredPipeServer()
    {
        return FreeVideoStudio.Core.Ipc.PipeAccess.CreateServer(
            PipeName, PipeDirection.In, 1,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
    }

    private static bool IsConnectedClientTrusted(NamedPipeServerStream server)
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            using WindowsIdentity self = WindowsIdentity.GetCurrent();
            string? expected = self.User?.Value;

            bool trusted = false;
            server.RunAsClient(() =>
            {
                using WindowsIdentity peer = WindowsIdentity.GetCurrent();
                trusted = expected != null && peer.User?.Value == expected;
            });

            return trusted;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SingleInstance", $"Could not verify the handoff caller's identity: {ex.Message}");
            return false;
        }
    }

    private static bool IsAcceptableHandoffPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.Length > 4096) return false;
            if (path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (!Path.IsPathFullyQualified(path)) return false;

            string full = Path.GetFullPath(path);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            return ext is ".mp4" or ".mkv" or ".avi" or ".mov";
        }
        catch (System.Exception swallowed4)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed4);
            return false;
        }
    }

    private static bool TrySendHandoff(string? videoPath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(HandoffTimeoutMs);

            if (!FreeVideoStudio.Core.Ipc.PipeAccess.IsServerTrusted(client))
            {
                RuntimeLog.Fail("SingleInstance", "Refused the handoff: the listening pipe belongs to a different user account.");
                return false;
            }

            using var writer = new StreamWriter(client);
            writer.Write(videoPath ?? string.Empty);
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SingleInstance", $"Handoff to the running instance failed: {ex.Message}");
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    private static void FocusExistingWindow()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            string ownName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            uint ownPid = (uint)Environment.ProcessId;

            foreach (var proc in System.Diagnostics.Process.GetProcessesByName(ownName))
            {
                if ((uint)proc.Id == ownPid) continue;

                uint targetPid = (uint)proc.Id;
                IntPtr found = IntPtr.Zero;

                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == targetPid && IsWindowVisible(hWnd))
                    {
                        found = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    ShowWindow(found, SwRestore);
                    SetForegroundWindow(found);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SingleInstance", $"Could not focus the running instance: {ex.Message}");
        }
    }
}
