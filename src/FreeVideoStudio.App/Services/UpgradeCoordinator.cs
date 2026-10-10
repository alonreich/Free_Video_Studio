// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>UPGRADE_10 — the original user's broker owns settings and first-launch confirmation.
/// Only the worker writes Program Files. A protected machine journal is the commit decision
/// for all participating user-data journals, including recovery after loss of the broker.</summary>
internal static class UpgradeCoordinator
{
    private static string Store => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeVideoStudioMigration");
    private static string SessionPath => Path.Combine(Store, "session.json");
    private static string UserGate => @"Local\FreeVideoStudio_UserMigration_" + WindowsIdentity.GetCurrent().User!.Value;

    public static bool HasPendingSession => File.Exists(SessionPath) &&
        AtomicJsonFile.ReadObject(SessionPath)?["phase"]?.GetValue<string>() == "Pending";

    public static bool HasPendingMachine => Directory.Exists(InstallDiscovery.Store) &&
        Directory.EnumerateDirectories(InstallDiscovery.Store).Any(p =>
            File.Exists(Path.Combine(p, "journal.json")) &&
            AtomicJsonFile.ReadObject(Path.Combine(p, "journal.json"))?["Phase"]?.GetValue<string>() is
                "Switching" or "AwaitingConfirmation" or "RollingBack");

    /// <summary>
    /// UPGRADEUX_06 — "the elevated worker must run from this clean staging folder". The broker passes
    /// it when it was started straight from the downloaded installer instead of from a staged copy.
    /// </summary>
    internal const string StageArgument = "--upgrade-stage";

    private static string StageRoot => Path.Combine(Path.GetTempPath(), "FVS_Upgrade");

    public static async Task<int> LaunchAsync(string[] args, bool recovery = false)
    {
        string stage = Path.Combine(StageRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);

        // ══════════════════════════════════════════════════════════════════════════════════════
        // UPGRADEUX_06 — NO SILENT COPY BEFORE THE WINDOW.
        //
        // The launcher used to copy its whole executable (322 MB for the full installer) into the
        // staging folder and verify it BEFORE starting the broker, so a double-clicked installer
        // showed nothing at all for seconds (longer on a slow disk).
        //
        // The copy still happens — the ELEVATED worker must start from a fresh folder only this
        // update writes to, never from Downloads, where a planted DLL would be loaded into an
        // administrator process. But it now happens INSIDE the broker, after its window is up (or
        // while the user is still working, for an in-app update). Only the two small window
        // libraries are staged here first.
        //
        // The broker itself is still started from a staged copy when this executable lives where
        // the update will replace it: the installed app (recovery), or any install root (a copy of
        // the installer sitting inside an old program folder). A running executable cannot be moved.
        // ══════════════════════════════════════════════════════════════════════════════════════
        bool stageBroker = DeploymentFootprint.IsRunningFromInstallPath() || RunsFromAnInstallRoot();
        string executable = Environment.ProcessPath!;
        if (stageBroker)
        {
            executable = Path.Combine(stage, InstallPayload.ExecutableName);
            UpgradeFiles.CopyVerified(Environment.ProcessPath!, executable);
        }
        StageWindowLibraries(stage);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--upgrade-broker");
        if (recovery) start.ArgumentList.Add("--recover-only");
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (!stageBroker)
        {
            start.ArgumentList.Add(StageArgument);
            start.ArgumentList.Add(stage);
        }
        if (DeploymentFootprint.IsRunningFromInstallPath() && !args.Contains("--wait-pid"))
        {
            start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start the update.");
        if (DeploymentFootprint.IsRunningFromInstallPath()) return 0;
        await process.WaitForExitAsync().ConfigureAwait(false);
        try { UpgradeFiles.DeleteTree(stage, Path.GetDirectoryName(stage)!); }
        catch (IOException ex) { RuntimeLog.Swallowed(ex); }
        catch (UnauthorizedAccessException ex) { RuntimeLog.Swallowed(ex); }
        return process.ExitCode;
    }

    /// <summary>
    /// UPGRADEUX_01 — the broker draws a window, so it needs the two native UI libraries beside its
    /// staged copy. Sources, in order: beside this executable (the installed app), the embedded
    /// payload (the full installer), the installed program folder (the compact installer, whose
    /// payload carries the app only — the runtime fingerprint guarantees those libraries match).
    /// A miss is not an error: the broker then runs without a window and uses message boxes.
    /// </summary>
    /// <summary>
    /// UPGRADEUX_06 — true when this executable sits inside a folder the update will move or replace.
    /// Any doubt answers true: staging costs a copy, not staging could block the folder switch.
    /// </summary>
    private static bool RunsFromAnInstallRoot()
    {
        string exe = Environment.ProcessPath ?? string.Empty;
        try
        {
            return InstallDiscovery.FindRoots()
                .Append(DeploymentFootprint.LegacyInstallFolder)
                .Any(root => UpgradeFiles.IsWithin(exe, root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            RuntimeLog.Info("Upgrade", "Could not tell where the installer runs from; staging a copy: " + ex.Message);
            return true;
        }
    }

    /// <summary>
    /// UPGRADEUX_06 — the staging folder the launcher created, or null. Accepted only as a direct child
    /// of <c>%TEMP%\FVS_Upgrade</c>, so an argument can never point the worker somewhere else.
    /// </summary>
    internal static string? StageFromArgs(string[] args)
    {
        int index = Array.FindIndex(args, a => a.Equals(StageArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;
        try
        {
            string stage = Path.GetFullPath(args[index + 1]);
            if (!string.Equals(Path.GetDirectoryName(stage), Path.GetFullPath(StageRoot), StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(stage)) return null;
            UpgradeFiles.RequirePlainPath(stage);
            return stage;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            RuntimeLog.Info("Upgrade", "Ignored an invalid staging folder argument: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// UPGRADEUX_06 — copies this installer into the staging folder for the elevated worker. Runs
    /// in the background while the window is already showing (or while the user is still working).
    /// </summary>
    private static string PrepareWorkerCopy(string stage)
    {
        string target = Path.Combine(stage, InstallPayload.ExecutableName);
        if (!File.Exists(target)) UpgradeFiles.CopyVerified(Environment.ProcessPath!, target);
        return target;
    }

    private static void StageWindowLibraries(string stage)
    {
        string[] names = ["libSkiaSharp.dll", "libHarfBuzzSharp.dll"];
        foreach (string dll in names)
        {
            string source = Path.Combine(AppContext.BaseDirectory, dll);
            if (File.Exists(source)) UpgradeFiles.CopyVerified(source, Path.Combine(stage, dll));
        }
        if (names.All(dll => File.Exists(Path.Combine(stage, dll)))) return;
        DeploymentLifecycle.ExtractAvaloniaDependencies(stage);
        foreach (string dll in names)
        {
            string target = Path.Combine(stage, dll), installed = Path.Combine(DeploymentFootprint.InstallFolder, dll);
            if (!File.Exists(target) && File.Exists(installed)) UpgradeFiles.CopyVerified(installed, target);
        }
        if (!names.All(dll => File.Exists(Path.Combine(stage, dll))))
            RuntimeLog.Info("Upgrade", "Window libraries not found; the update will run with message boxes only.");
    }

    /// <param name="progress">UPGRADEUX_01 — what the user sees. Display only: it never changes the
    /// protocol, the order of steps, or whether a failure rolls back. Null means headless.</param>
    public static async Task<int> RunAsync(string[] args, IUpgradeProgress? progress = null)
    {
        progress ??= HeadlessUpgradeProgress.Instance;
        bool recovery = args.Contains("--recover-only", StringComparer.OrdinalIgnoreCase);
        bool waitsForApp = args.Contains("--wait-pid", StringComparer.OrdinalIgnoreCase);
        UpgradeKind kind = recovery ? UpgradeKind.Recovery : DetectKind();
        progress.Begin(kind, waitsForApp);

        // UPGRADEUX_06 — the worker copy is made in the background from the first moment: while the
        // user is still working (in-app update) or while the window shows the first steps.
        string? stage = StageFromArgs(args);
        Task<string> workerExecutable = stage is null
            ? Task.FromResult(Environment.ProcessPath!)
            : Task.Run(() => PrepareWorkerCopy(stage));

        // UPGRADE_02 — the window stays hidden while the user is still working; it appears the
        // moment the app that started this update has closed (UPGRADEUX_01).
        // UPGRADEUX_04 — the wait happens BEFORE the per-user gate is taken. Holding the gate while
        // the user kept working made every Video Merger / Crop Tool launched in the meantime fail at
        // startup with "An update is finishing for this Windows account".
        if (waitsForApp) progress.Step(UpgradeStep.CloseOldVersion, "Waiting for Free Video Studio to close…");
        try { await WaitForSourceExitAsync(args).ConfigureAwait(false); }
        catch (InvalidOperationException gone) { RuntimeLog.Swallowed(gone); }
        progress.Show();

        using var gate = new Semaphore(1, 1, UserGate);
        if (!gate.WaitOne(0))
        {
            // UPGRADEUX_04 — this used to exit with code 2 and no word to the user.
            await progress.FailedAsync("Another update is already running",
                "Free Video Studio is already being installed or updated in another window. Please wait for it to finish.")
                .ConfigureAwait(false);
            return 2;
        }
        Process? worker = null, app = null;
        UpgradeChannel? channel = null, health = null;
        UserDataUpgrade? data = null;
        JsonObject? session = null;
        bool uacDeclined = false, cancelled = false;
        string? previousVersion = null;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var healthListener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            if (UpgradeInstallWorker.IsElevated())
                throw new IOException("Please run the installer normally, without 'Run as administrator'. Windows will ask for permission when needed.");

            if (!recovery)
            {
                // UPGRADEUX_03 — ask the user to close any open copy BEFORE Windows is asked.
                progress.Step(UpgradeStep.CheckNothingOpen);
                string[] knownRoots = InstallDiscovery.FindRoots();
                previousVersion = ReadInstalledVersion(knownRoots);
                if (!await EnsureAppsClosedAsync(knownRoots, progress).ConfigureAwait(false))
                {
                    cancelled = true;
                    throw new OperationCanceledException("The user cancelled because the app was still open.");
                }
            }

            if (!workerExecutable.IsCompleted)
                progress.Step(UpgradeStep.CheckNothingOpen, "Getting the installer ready… (a few seconds)");
            string workerPath = await workerExecutable.ConfigureAwait(false);

            // UPGRADEUX_02 — explain the Windows prompt before it appears.
            progress.Step(UpgradeStep.Permission, UpgradeText.PermissionDetail(kind));
            listener.Start();
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var start = new ProcessStartInfo(workerPath) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--upgrade-worker");
            start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(token);
            try
            {
                worker = Process.Start(start) ?? throw new IOException("Windows did not start the installer.");
            }
            catch (System.ComponentModel.Win32Exception denied) when (denied.NativeErrorCode == ErrorCancelled)
            {
                uacDeclined = true;
                throw new OperationCanceledException("The user declined the Windows permission prompt.", denied);
            }
            channel = await AcceptAsync(listener, token).ConfigureAwait(false);
            channel.Progress = detail => ReportWorkerProgress(progress, detail);
            await channel.SendAsync("mode", recovery ? "recover" : "install").ConfigureAwait(false);
            if (recovery)
            {
                progress.Step(UpgradeStep.Settings, "Putting your files and settings back in order…");
                await channel.ExpectAsync("recovered").ConfigureAwait(false);
                await RecoverUserSessionAsync().ConfigureAwait(false);
                progress.Step(UpgradeStep.Start);
                StartApp();
                progress.Succeeded("Everything is back in order. Free Video Studio is opening now.");
                return 0;
            }

            JsonObject ready = JsonNode.Parse(await channel.ExpectAsync("ready").ConfigureAwait(false))!.AsObject();
            string machine = ready["transaction"]!.GetValue<string>();
            RequireMachineJournal(machine);
            string[] roots = ready["roots"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
            if (kind != UpgradeKind.Install) progress.Step(UpgradeStep.Settings);
            await RecoverUserSessionAsync().ConfigureAwait(false);
            // NOSPACE_01 — the spaced CURRENT-brand folder is not a previous brand: moving it must not
            // make a previous brand's leftover settings win over the user's current ones.
            bool preferLegacy = !args.Contains("--source-current", StringComparer.OrdinalIgnoreCase) &&
                roots.Any(p => !InstallDiscovery.KnownDestinations.Contains(UpgradeFiles.FullPath(p), StringComparer.OrdinalIgnoreCase));
            Directory.CreateDirectory(Store);
            string shellBackup = Path.Combine(Store, "shell-" + Guid.NewGuid().ToString("N"));
            var shell = new UpgradeRegistration(false, shellBackup, roots);
            await shell.CaptureAsync().ConfigureAwait(false);
            session = new JsonObject
            {
                ["phase"] = "Pending", ["machine"] = machine, ["shell"] = shellBackup,
                ["roots"] = new JsonArray(roots.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["transactions"] = new JsonArray()
            };
            Save(session);
            data = UserDataUpgrade.Begin(UserDataUpgrade.CurrentRoots(), UserDataUpgrade.LocalRoot,
                preferLegacy: preferLegacy, prepared: transaction =>
                {
                    session["transactions"]!.AsArray().AddNode((JsonNode?)JsonValue.Create(transaction.DirectoryPath));
                    Save(session);
                });
            ValidateSettings();
            session["userReady"] = true; Save(session);
            progress.Step(UpgradeStep.Unpack, null, 0);
            await channel.SendAsync("begin").ConfigureAwait(false);
            await channel.ExpectAsync("installed").ConfigureAwait(false);
            progress.Step(UpgradeStep.Shortcuts);
            await TryApplyShellAsync(shell, session).ConfigureAwait(false);

            progress.Step(UpgradeStep.Start, kind == UpgradeKind.Install
                ? "Opening Free Video Studio for the first time…"
                : "Opening the new version and checking that it started correctly…");
            healthListener.Start();
            string healthToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            app = StartApp(((IPEndPoint)healthListener.LocalEndpoint).Port, healthToken, previousVersion, kind);
            health = await AcceptAsync(healthListener, healthToken).ConfigureAwait(false);
            string pid = await health.ExpectAsync("healthy").ConfigureAwait(false);
            if (pid != app.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) || app.HasExited)
                throw new IOException("The new app did not confirm its startup.");
            await channel.SendAsync("healthy", pid).ConfigureAwait(false);
            await channel.ExpectAsync("committed").ConfigureAwait(false);
            data.Confirm();
            session["phase"] = "Committed";
            Save(session);
            await TryRemoveShellAsync(shell, session).ConfigureAwait(false);
            await health.SendAsync("committed").ConfigureAwait(false);
            if (args.Contains("--no-launch", StringComparer.OrdinalIgnoreCase)) app.CloseMainWindow();
            progress.Succeeded(SuccessMessage(kind, previousVersion));
            return 0;
        }
        catch (Exception ex)
        {
            if (workerExecutable.IsFaulted) RuntimeLog.Swallowed(workerExecutable.Exception!);   // observed, never unobserved
            if (ex is OperationCanceledException) RuntimeLog.Info("Upgrade", "Stopped by the user before anything changed: " + ex.Message);
            else RuntimeLog.Fail("Upgrade", ex.ToString());
            bool committed = session?["machine"] is JsonValue m && IsMachineCommitted(m.GetValue<string>());
            if (!committed && app is { HasExited: false })
            {
                app.Kill(entireProcessTree: true);
                await app.WaitForExitAsync().ConfigureAwait(false);
            }
            channel?.Dispose(); channel = null;
            if (worker is { HasExited: false })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try { await worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException wait) { RuntimeLog.Swallowed(wait); }
            }
            if (worker == null || worker.HasExited)
            {
                try { await RecoverUserSessionAsync().ConfigureAwait(false); }
                catch (Exception restore) { RuntimeLog.Fail("Upgrade recovery", restore.ToString()); }
            }
            if (committed && health != null)
            {
                try { await health.SendAsync("committed").ConfigureAwait(false); }
                catch (Exception notify) { RuntimeLog.Swallowed(notify); }
            }
            if (committed)
            {
                progress.Succeeded(SuccessMessage(kind, previousVersion)
                    + " A little tidying-up could not finish and will be retried the next time it starts.");
                return 1;
            }

            // UPGRADEUX_04 — say what happened in plain words, and put the user back where they were.
            bool reopened = waitsForApp && !cancelled && TryReopenPreviousVersion(worker);
            string reopenedLine = reopened ? Environment.NewLine + Environment.NewLine + "Your previous version has been opened again." : string.Empty;
            string title, message;
            if (uacDeclined)
            {
                title = kind == UpgradeKind.Install ? "Installation cancelled" : "Update cancelled";
                message = UpgradeText.UacDeclined(kind) + reopenedLine;
            }
            else if (cancelled)
            {
                title = "Update cancelled";
                message = "Free Video Studio was still open, so nothing was changed. You can update later from Help › Check for Updates." + reopenedLine;
            }
            else if (HasPendingMachine || HasPendingSession)
            {
                title = "The update did not finish";
                message = "Something stopped the update part-way, and a backup of your previous version was kept." + Environment.NewLine + Environment.NewLine
                        + "Reason: " + ex.Message + Environment.NewLine + Environment.NewLine
                        + "Run the installer again (or simply open Free Video Studio) to put everything back in order.";
            }
            else
            {
                title = kind == UpgradeKind.Install ? "Free Video Studio could not be installed" : "The update could not finish";
                message = (kind == UpgradeKind.Install
                            ? "Nothing was installed."
                            : "Your previous version was kept exactly as it was.")
                        + Environment.NewLine + Environment.NewLine + "Reason: " + ex.Message
                        + Environment.NewLine + Environment.NewLine
                        + (kind == UpgradeKind.Install ? "You can run the installer again." : "You can try again later from Help › Check for Updates.")
                        + reopenedLine;
            }
            await progress.FailedAsync(title, message).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            health?.Dispose(); channel?.Dispose(); worker?.Dispose(); app?.Dispose();
            listener.Stop(); healthListener.Stop(); gate.Release();
        }
    }

    /// <summary>ERROR_CANCELLED — what <c>Process.Start</c> reports when the user clicks "No" on UAC.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>UPGRADEUX_01 — fresh install or update, for the window's wording only.</summary>
    private static UpgradeKind DetectKind()
    {
        try
        {
            return InstallDiscovery.FindRoots().Any(Directory.Exists) ? UpgradeKind.Update : UpgradeKind.Install;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The same check runs again below and reports properly; this is only the window title.
            RuntimeLog.Info("Upgrade", "Could not tell install from update for the title: " + ex.Message);
            return UpgradeKind.Update;
        }
    }

    /// <summary>UPGRADEUX_05 — the version being replaced, so the new app can say "updated from X".</summary>
    private static string? ReadInstalledVersion(IEnumerable<string> roots)
    {
        foreach (string root in roots)
        foreach (string name in InstallDiscovery.Executables)
        {
            string exe = Path.Combine(root, name);
            try
            {
                if (File.Exists(exe) && DeploymentLifecycle.TryParseVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion, out Version v))
                    return v.ToString();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.Swallowed(ex);
            }
        }
        return null;
    }

    private static string SuccessMessage(UpgradeKind kind, string? previousVersion)
    {
        string now = DeploymentLifecycle.GetCurrentVersion();
        return kind == UpgradeKind.Install
            ? $"Free Video Studio {now} is installed and opening now."
            : previousVersion != null && previousVersion != now
                ? $"Free Video Studio was updated from {previousVersion} to {now}. Your settings were kept, and the new version is opening now."
                : $"Free Video Studio {now} is installed. Your settings were kept, and it is opening now.";
    }

    /// <summary>
    /// UPGRADEUX_03 — before Windows is asked for permission, make sure no copy of the app is open.
    /// "Close it" asks each window to close normally, so its own "save your work?" question still
    /// appears; nothing is killed (05 SYS-UPGRADE: "No pre-existing editing process is killed").
    /// Returns false if the user cancels.
    /// </summary>
    private static async Task<bool> EnsureAppsClosedAsync(string[] roots, IUpgradeProgress progress)
    {
        while (true)
        {
            var (mine, other) = InstallDiscovery.FindOpenApps(roots, Environment.ProcessId);
            try
            {
                if (mine.Count == 0 && !other) return true;
                RunningAppChoice choice = await progress.AskToCloseRunningAppAsync(mine.Count, other).ConfigureAwait(false);
                if (choice == RunningAppChoice.Cancel) return false;
                if (mine.Count == 0) continue;   // "Try again" for another account's copy.

                progress.Step(UpgradeStep.CheckNothingOpen,
                    "Closing Free Video Studio… If it asks whether to save your work, answer it first.");
                foreach (Process process in mine)
                {
                    try { process.CloseMainWindow(); }
                    catch (InvalidOperationException ex) { RuntimeLog.Swallowed(ex); }
                }
                // Give the user time to answer a save question; ask again if it is still open.
                DateTime deadline = DateTime.UtcNow.AddMinutes(2);
                while (DateTime.UtcNow < deadline && mine.Any(p => !HasExited(p)))
                    await Task.Delay(500).ConfigureAwait(false);
            }
            finally
            {
                foreach (Process process in mine) process.Dispose();
            }
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException ex) { RuntimeLog.Swallowed(ex); return true; }
    }

    /// <summary>
    /// UPGRADEUX_04 — after a failure that changed nothing, reopen the version the user was using.
    /// Never when a rollback is still pending: opening the app then would start recovery (and a
    /// second Windows prompt) immediately, which is the opposite of calm.
    /// </summary>
    private static bool TryReopenPreviousVersion(Process? worker)
    {
        try
        {
            if (worker is { HasExited: false } || HasPendingMachine || HasPendingSession) return false;
            if (!File.Exists(DeploymentFootprint.InstallPath)) return false;
            using Process launched = StartApp();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RuntimeLog.Fail("Upgrade", "Could not reopen the previous version: " + ex.Message);
            return false;
        }
    }

    /// <summary>UPGRADEUX_01 — "phase|fraction" from the elevated worker, shown as a step and a bar.</summary>
    private static void ReportWorkerProgress(IUpgradeProgress progress, string detail)
    {
        int bar = detail.IndexOf('|');
        string phase = bar < 0 ? detail : detail[..bar];
        double? fraction = bar >= 0 && double.TryParse(detail[(bar + 1)..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double f) ? f : null;
        switch (phase)
        {
            case "unpack": progress.Step(UpgradeStep.Unpack, null, fraction); break;
            case "install": progress.Step(UpgradeStep.Install, "Installing files and checking every one of them…", fraction); break;
            case "switch": progress.Step(UpgradeStep.Install, UpgradeText.WorkerProgressDetail(phase)); break;
            case "test": progress.Step(UpgradeStep.Test, UpgradeText.WorkerProgressDetail(phase)); break;
            case "shortcuts": progress.Step(UpgradeStep.Shortcuts, UpgradeText.WorkerProgressDetail(phase)); break;
        }
    }

    /// <summary>
    /// NOSPACE_01 — repoints THIS user's own shortcuts after a LATER machine install moved the
    /// program (Program Files\Free Video Studio → Program Files\FreeVideoStudio).
    ///
    /// The installing user's shortcuts are handled by the broker, and the machine-wide Start menu
    /// and Public Desktop by the elevated worker. Every OTHER account on the PC already completed
    /// its own migration earlier (session "Committed"), so <see cref="CompleteUserAsync"/> never
    /// looked again — their personal Desktop icon kept pointing at a folder that no longer exists.
    ///
    /// Only links that already exist and point into a moved folder are rewritten; nothing is
    /// created, so a deliberately deleted icon stays deleted. Runs once per machine transaction
    /// (recorded as "retargetedMachine"). Never throws: a failure is logged and retried next start.
    /// </summary>
    private static async Task RetargetAfterLaterMachineMoveAsync(JsonObject session)
    {
        try
        {
            string? latest = Directory.Exists(InstallDiscovery.Store)
                ? Directory.EnumerateDirectories(InstallDiscovery.Store)
                    .Where(p => File.Exists(Path.Combine(p, "journal.json")) && DirectoryUpgrade.IsCommitted(p))
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() : null;
            if (latest == null) return;
            if (string.Equals(latest, session["machine"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(latest, session["retargetedMachine"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) return;

            var journal = AtomicJsonFile.ReadObject(Path.Combine(latest, "journal.json"))!;
            string[] moved = journal["Originals"]!.AsArray().Select(x => x!["Path"]!.GetValue<string>())
                .Where(p => !string.Equals(UpgradeFiles.FullPath(p), UpgradeFiles.FullPath(InstallDiscovery.Destination), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            int changed = moved.Length == 0 ? 0 : await UpgradeRegistration.RetargetUserLinksAsync(moved).ConfigureAwait(false);
            session["retargetedMachine"] = latest;
            Save(session);
            RuntimeLog.Info("Upgrade", $"Repointed {changed} personal shortcut(s) after the program moved.");
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("Upgrade shortcuts", $"Personal shortcuts were not repointed; will retry: {ex.Message}");
        }
    }

    internal static void ValidateSettings()
    {
        ValidateSettingsFile(Path.Combine(UserDataUpgrade.LocalRoot, "settings.json"));
    }

    internal static void ValidateSettingsFile(string path)
    {
        if (!File.Exists(path)) return;
        JsonObject json = AtomicJsonFile.ReadObject(path) ?? throw new IOException("Saved settings are unreadable. They have been preserved.");
        if (json["SchemaVersion"]?.GetValue<int>() > Infrastructure.SettingsManager.CurrentSchemaVersion)
            throw new IOException("These settings need a newer app version. They have been preserved.");
        _ = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), Infrastructure.SettingsJsonContext.Default.AppSettings)
            ?? throw new IOException("The saved settings could not be read. They have been preserved.");
    }

    private static void RequireMachineJournal(string directory)
    {
        if (!UpgradeFiles.IsWithin(directory, InstallDiscovery.Store) || Path.GetDirectoryName(directory) != InstallDiscovery.Store)
            throw new IOException("Unexpected installation recovery location.");
        UpgradeFiles.RequirePlainPath(directory);
    }

    private static bool IsMachineCommitted(string directory)
    {
        RequireMachineJournal(directory);
        return File.Exists(Path.Combine(directory, "journal.json")) && DirectoryUpgrade.IsCommitted(directory);
    }

    public static async Task RecoverUserSessionAsync()
    {
        if (!File.Exists(SessionPath)) return;
        JsonObject session = AtomicJsonFile.ReadObject(SessionPath) ?? throw new IOException("The migration record is unreadable. Recovery backups have been preserved.");
        if (session["phase"]?.GetValue<string>() != "Pending") return;
        bool committed = IsMachineCommitted(session["machine"]!.GetValue<string>()) && session["userReady"]?.GetValue<bool>() == true;
        foreach (JsonNode? node in session["transactions"]!.AsArray().Reverse())
        {
            string directory = node!.GetValue<string>();
            UserUpgradeRoot root = UserDataUpgrade.CurrentRoots().Single(r =>
                string.Equals(Path.GetDirectoryName(directory), r.Store, StringComparison.OrdinalIgnoreCase));
            DirectoryUpgrade transaction = DirectoryUpgrade.Open(directory, root.LegacyRoots.Append(root.Destination));
            if (committed) transaction.Confirm(DateTimeOffset.UtcNow);
            else transaction.Rollback();
        }
        if (!committed) await ShellFromSession(session).RestoreAsync().ConfigureAwait(false);
        session["phase"] = committed ? "Committed" : "RolledBack";
        Save(session);
    }

    private static UpgradeRegistration ShellFromSession(JsonObject session)
    {
        string path = session["shell"]!.GetValue<string>();
        if (Path.GetDirectoryName(path) != Store) throw new IOException("Unexpected shortcut recovery location.");
        return new UpgradeRegistration(false, path,
            session["roots"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray());
    }

    public static async Task CompleteUserAsync()
    {
        using var gate = new Semaphore(1, 1, UserGate);
        if (!gate.WaitOne(0)) throw new IOException("An update is finishing for this Windows account. Please try opening the app again shortly.");
        try
        {
            await RecoverUserSessionAsync().ConfigureAwait(false);
            if (File.Exists(SessionPath) && AtomicJsonFile.ReadObject(SessionPath)?["phase"]?.GetValue<string>() == "Committed")
            {
                JsonObject session = AtomicJsonFile.ReadObject(SessionPath) ?? throw new IOException("The migration record is unreadable.");
                if (session["phase"]?.GetValue<string>() == "Committed" && session["shellPending"]?.GetValue<bool>() == true)
                {
                    var shell = ShellFromSession(session);
                    await TryApplyShellAsync(shell, session).ConfigureAwait(false);
                    await TryRemoveShellAsync(shell, session).ConfigureAwait(false);
                }
                await RetargetAfterLaterMachineMoveAsync(session).ConfigureAwait(false);   // NOSPACE_01
            }
            else
            {
                string? machine = Directory.Exists(InstallDiscovery.Store)
                    ? Directory.EnumerateDirectories(InstallDiscovery.Store)
                        .Where(p => File.Exists(Path.Combine(p, "journal.json")) && DirectoryUpgrade.IsCommitted(p))
                        .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() : null;
                if (machine != null)
                {
                    var journal = AtomicJsonFile.ReadObject(Path.Combine(machine, "journal.json"))!;
                    string[] roots = journal["Originals"]!.AsArray().Select(x => x!["Path"]!.GetValue<string>())
                        .Append(InstallDiscovery.Destination).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    InstallDiscovery.EnsureIdle(roots, Environment.ProcessId);
                    string shellPath = Path.Combine(Store, "shell-" + Guid.NewGuid().ToString("N"));
                    var shell = new UpgradeRegistration(false, shellPath, roots);
                    await shell.CaptureAsync().ConfigureAwait(false);
                    var session = new JsonObject
                    {
                        ["phase"] = "Pending", ["machine"] = machine, ["shell"] = shellPath,
                        ["roots"] = new JsonArray(roots.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                        ["transactions"] = new JsonArray()
                    };
                    Save(session);
                    try
                    {
                        var data = UserDataUpgrade.Begin(UserDataUpgrade.CurrentRoots(), UserDataUpgrade.LocalRoot,
                            prepared: transaction => { session["transactions"]!.AsArray().AddNode((JsonNode?)JsonValue.Create(transaction.DirectoryPath)); Save(session); });
                        ValidateSettings();
                        session["userReady"] = true; Save(session);
                        data.Confirm();
                        session["phase"] = "Committed"; Save(session);
                        await TryApplyShellAsync(shell, session).ConfigureAwait(false);
                        await TryRemoveShellAsync(shell, session).ConfigureAwait(false);
                    }
                    catch { await RecoverUserSessionAsync().ConfigureAwait(false); throw; }
                }
            }
            UserDataUpgrade.RecoverInterrupted(UserDataUpgrade.CurrentRoots());
        }
        finally { gate.Release(); }
    }

    private static async Task TryApplyShellAsync(UpgradeRegistration shell, JsonObject session)
    {
        session["shellPending"] = true; Save(session);
        session["shellApplied"] = false; Save(session);
        try { await shell.ApplyAsync().ConfigureAwait(false); session["shellApplied"] = true; Save(session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { RuntimeLog.WarnThrottled("Upgrade shortcuts", ex.Message); }
    }

    private static async Task TryRemoveShellAsync(UpgradeRegistration shell, JsonObject session)
    {
        try
        {
            if (session["shellApplied"]?.GetValue<bool>() != true) return;
            await shell.RemoveLegacyAsync().ConfigureAwait(false);
            session["shellPending"] = !shell.DesktopAvailable; Save(session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { session["shellPending"] = true; Save(session); RuntimeLog.WarnThrottled("Upgrade shortcuts", ex.Message); }
    }

    private static void Save(JsonObject session) => AtomicJsonFile.WriteObject(SessionPath, session);

    private static Process StartApp(int? port = null, string? token = null, string? previousVersion = null, UpgradeKind kind = UpgradeKind.Update)
    {
        var start = new ProcessStartInfo(DeploymentFootprint.InstallPath) { UseShellExecute = false, WorkingDirectory = InstallDiscovery.Destination };
        start.ArgumentList.Add("run-ui");
        if (port.HasValue)
        {
            start.ArgumentList.Add("--upgrade-health"); start.ArgumentList.Add(port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(token!);
            // UPGRADEUX_05 — lets the new app say "Updated from X to Y" (or "Installed").
            start.ArgumentList.Add(UpgradeFinishedNotice.KindArgument);
            start.ArgumentList.Add(kind == UpgradeKind.Install ? "install" : "update");
            if (!string.IsNullOrWhiteSpace(previousVersion))
            {
                start.ArgumentList.Add(UpgradeFinishedNotice.FromArgument);
                start.ArgumentList.Add(previousVersion);
            }
        }
        return Process.Start(start) ?? throw new IOException("Could not start the installed app.");
    }

    private static async Task<UpgradeChannel> AcceptAsync(TcpListener listener, string token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (true)
        {
            var candidate = new UpgradeChannel(await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false));
            try
            {
                if (await candidate.ExpectAsync("hello", TimeSpan.FromSeconds(5)).ConfigureAwait(false) == token) return candidate;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { RuntimeLog.Swallowed(ex); }
            candidate.Dispose();
        }
    }

    private static async Task WaitForSourceExitAsync(string[] args)
    {
        int index = Array.IndexOf(args, "--wait-pid");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid)) return;
        try
        {
            using Process source = Process.GetProcessById(pid);
            await source.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException ex) { RuntimeLog.Info("Upgrade", "The source app already closed: " + ex.Message); }
    }

    public static async Task ConfirmWindowAsync(string[] args)
    {
        int index = Array.IndexOf(args, "--upgrade-health");
        if (index < 0 || index + 2 >= args.Length || !int.TryParse(args[index + 1], out int port)) return;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var channel = new UpgradeChannel(client);
        await channel.SendAsync("hello", args[index + 2]).ConfigureAwait(false);
        if (Infrastructure.SettingsManager.LoadFailureMessage != null)
            throw new IOException("Saved settings did not load successfully.");
        await channel.SendAsync("healthy", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await channel.ExpectAsync("committed").ConfigureAwait(false);
    }
}
