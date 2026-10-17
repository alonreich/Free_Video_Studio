// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using Microsoft.Win32;

namespace FreeVideoStudio.App.Services;

/// <summary>UPGRADE_09 — privileged work is restricted to verified machine installation roots.</summary>
internal static class UpgradeInstallWorker
{
    internal static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (!IsElevated() || args.Length < 3 || !int.TryParse(args[1], out int port) || port is < 1 or > 65535 ||
            args[2].Length != 64 || args[2].Any(c => !Uri.IsHexDigit(c))) return 2;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var channel = new UpgradeChannel(client);
        await channel.SendAsync("hello", args[2]).ConfigureAwait(false);
        using var gate = new Semaphore(1, 1, DeploymentFootprint.InstallerGateName);
        using var legacyGate = new Semaphore(1, 1, @"Global\" + LegacyProductIdentity.CompactName + "_InstallerGate");
        bool ownsGate = gate.WaitOne(0), ownsLegacy = false;
        DirectoryUpgrade? transaction = null;
        UpgradeRegistration? registration = null;
        try
        {
            // WORKERSTAGE_01 — administrator work happens only from the protected copy the broker prepared.
            if (!ProtectedWorkerStage.IsProtectedWorkerPath(Environment.ProcessPath))
                throw new IOException("The installer was started from an unprotected folder, so it refused to run with administrator rights. Nothing was changed.");
            if (!ownsGate || !(ownsLegacy = legacyGate.WaitOne(0)))
                throw new IOException("Another installation or uninstall is running. Try again when it finishes.");
            UpgradeFiles.RequirePlainPath(InstallDiscovery.Store);
            await RecoverInterruptedAsync().ConfigureAwait(false);
            string mode = await channel.ExpectAsync("mode").ConfigureAwait(false);
            if (mode == "recover")
            {
                await channel.SendAsync("recovered").ConfigureAwait(false);
                return 0;
            }
            string? smartFolder = null;
            if (mode.StartsWith("install|", StringComparison.Ordinal))
            {
                smartFolder = mode["install|".Length..];
                mode = "install";
            }
            if (mode != "install") throw new IOException("Unknown installation request.");
            SmartUpdateManifest? smart = smartFolder is null ? null : LoadVerifiedSmartUpdate(smartFolder);
            bool allowDowngrade = args.Contains("--allow-downgrade", StringComparer.OrdinalIgnoreCase);
            string[] roots = InstallDiscovery.FindRoots(allowDowngrade);
            InstallDiscovery.EnsureIdle(roots, Environment.ProcessId);
            transaction = DirectoryUpgrade.Create(InstallDiscovery.Store, InstallDiscovery.Destination,
                roots.Where(r => !r.Equals(InstallDiscovery.Destination, StringComparison.OrdinalIgnoreCase)));
            registration = new UpgradeRegistration(true, transaction.DirectoryPath, roots);
            await registration.CaptureAsync().ConfigureAwait(false);
            await channel.SendAsync("ready", new JsonObject
            {
                ["transaction"] = transaction.DirectoryPath,
                ["roots"] = new JsonArray(roots.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray())
            }.ToJsonString()).ConfigureAwait(false);
            await channel.ExpectAsync("begin").ConfigureAwait(false);
            var relay = new ProgressRelay(channel);
            await relay.StepAsync("unpack").ConfigureAwait(false);
            string extracted = Path.Combine(transaction.DirectoryPath, "payload");
            string? reuseRoot = InstallDiscovery.ReuseRoot(roots);
            if (smart is not null)
            {
                long downloadedBytes = UpgradeFiles.Files(smartFolder!).Sum(f => new FileInfo(f).Length);
                InstallDiscovery.EnsureSpace(InstallDiscovery.Destination, [], downloadedBytes);
                await relay.RunAsync("unpack", report => InstallPayload.ExtractFromSmartUpdate(smart, smartFolder!, extracted,
                    new[] { reuseRoot }.Concat(roots).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase), report)).ConfigureAwait(false);
            }
            else
            {
                // NODROPPER_01: full installs use the separate Inno Setup executable.
                throw new IOException("This program is not an installer. Download FreeVideoStudio-Setup.exe to install Free Video Studio.");
            }
            await relay.RunAsync("install", report => transaction.Prepare(candidate =>
            {
                string[] newFiles = UpgradeFiles.Files(extracted).ToArray();
                for (int i = 0; i < newFiles.Length; i++)
                {
                    string target = Path.Combine(candidate, Path.GetRelativePath(extracted, newFiles[i]));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // FASTINSTALL_01: move extracted files into the prepared candidate without a second payload copy.
                    File.Move(newFiles[i], target, overwrite: true);
                    report(0.3 + 0.2 * (i + 1) / newFiles.Length);
                }
                string exe = Path.Combine(candidate, InstallPayload.ExecutableName), uninstall = Path.Combine(candidate, "Uninstall.exe");
                if (File.Exists(uninstall)) File.Delete(uninstall);
                if (!HardLink.TryCreate(uninstall, exe)) UpgradeFiles.CopyVerified(exe, uninstall, overwrite: true);
                InstallPayload.Verify(candidate, f => report(0.5 + 0.5 * f));
            }, relative => !LegacyProductIdentity.ExecutableNames.Contains(relative, StringComparer.OrdinalIgnoreCase)
                           && !relative.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)
                           && !File.Exists(Path.Combine(extracted, relative)),
               f => report(0.3 * f))).ConfigureAwait(false);
            InstallDiscovery.EnsureIdle(roots, Environment.ProcessId);
            await relay.StepAsync("switch").ConfigureAwait(false);
            transaction.Activate();
            await relay.StepAsync("test").ConfigureAwait(false);
            await RunProbeAsync().ConfigureAwait(false);
            await relay.StepAsync("shortcuts").ConfigureAwait(false);
            await registration.ApplyAsync().ConfigureAwait(false);
            await channel.SendAsync("installed").ConfigureAwait(false);
            string pidText = await channel.ExpectAsync("healthy", TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            {
                if (!int.TryParse(pidText, out int pid)) throw new IOException("Invalid first-launch confirmation.");
                using Process app = Process.GetProcessById(pid);
                if (app.HasExited || !string.Equals(app.MainModule?.FileName, DeploymentFootprint.InstallPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The new installed app did not confirm a successful startup.");
            }
            transaction.Confirm(DateTimeOffset.UtcNow);
            try { await registration.RemoveLegacyAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { RuntimeLog.Fail("Upgrade cleanup", cleanup.ToString()); }
            await channel.SendAsync("committed").ConfigureAwait(false);
            await MaintainAsync(ownsInstallerGate: true).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Upgrade", ex.ToString());
            try
            {
                if (transaction?.Phase != "Committed")
                {
                    transaction?.Rollback();
                    if (registration != null) await registration.RestoreAsync().ConfigureAwait(false);
                }
            }
            catch (Exception rollback)
            {
                RuntimeLog.Fail("Upgrade rollback", rollback.ToString());
                ex = new IOException($"Update stopped. Your backup is preserved at {transaction?.DirectoryPath}. Rollback needs another install attempt: {rollback.Message}", ex);
            }
            try { await channel.SendAsync("error", ex.Message).ConfigureAwait(false); }
            catch (Exception connection) { RuntimeLog.Swallowed(connection); }
            return 1;
        }
        finally
        {
            if (ownsLegacy) legacyGate.Release();
            if (ownsGate) gate.Release();
        }
    }

    /// <summary>
    /// UPGRADEUX_01 — relays "phase|fraction" to the broker while synchronous file work runs on the
    /// thread pool. Only this flow writes to the channel, so progress can never interleave with a
    /// protocol message. Reporting is best-effort display data; a lost line changes nothing.
    /// </summary>
    private sealed class ProgressRelay(UpgradeChannel channel)
    {
        private readonly object _gate = new();
        private string _phase = string.Empty, _sent = string.Empty;
        private double _fraction;

        private void Set(string phase, double fraction)
        {
            lock (_gate) { _phase = phase; _fraction = Math.Clamp(fraction, 0, 1); }
        }

        public async Task StepAsync(string phase)
        {
            Set(phase, 0);
            await FlushAsync().ConfigureAwait(false);
        }

        public async Task RunAsync(string phase, Action<Action<double>> work)
        {
            Set(phase, 0);
            await FlushAsync().ConfigureAwait(false);
            Task task = Task.Run(() => work(fraction => Set(phase, fraction)));
            while (!task.IsCompleted)
            {
                await Task.WhenAny(task, Task.Delay(300)).ConfigureAwait(false);
                await FlushAsync().ConfigureAwait(false);
            }
            await task.ConfigureAwait(false);
            Set(phase, 1);
            await FlushAsync().ConfigureAwait(false);
        }

        private async Task FlushAsync()
        {
            string text;
            lock (_gate) text = _phase + "|" + _fraction.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            if (text == _sent) return;
            _sent = text;
            await channel.SendAsync(UpgradeChannel.ProgressKind, text).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// DIST-SMART — the elevated side NEVER trusts what the unelevated updater checked. It re-reads the
    /// manifest and signature from the staging folder and verifies the signature itself, against the
    /// pinned publisher root (or, for a publicly trusted certificate, against its OWN signer).
    /// </summary>
    private static SmartUpdateManifest LoadVerifiedSmartUpdate(string folder)
    {
        UpgradeFiles.RequirePlainPath(folder);
        byte[] json = File.ReadAllBytes(Path.Combine(folder, SmartUpdateManifest.FileName));
        byte[] signature = File.ReadAllBytes(Path.Combine(folder, SmartUpdateManifest.SignatureFileName));
        string? self = OperatingSystem.IsWindows() ? AuthenticodeVerifier.Inspect(Environment.ProcessPath ?? string.Empty) is { ChainValid: true } own
            ? own.Thumbprint : null : null;
        if (!SmartUpdateManifest.VerifySignature(json, signature, self, out string detail))
            throw new IOException("The update could not be verified, so nothing was installed. " + detail);
        SmartUpdateManifest manifest = SmartUpdateManifest.Parse(json);
        if (!DeploymentLifecycle.TryParseVersion(manifest.Version, out Version expected) ||
            !DeploymentLifecycle.TryParseVersion(DeploymentLifecycle.GetCurrentVersion(), out Version mine) || expected != mine)
            throw new IOException("The update files belong to a different version than this installer. Nothing was installed.");
        return manifest;
    }

    private static async Task RunProbeAsync()
    {
        using var process = Process.Start(new ProcessStartInfo(DeploymentFootprint.InstallPath, "--upgrade-probe")
            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new IOException("Could not verify the new executable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException("The new executable did not finish its installation check.");
        }
        if (process.ExitCode != 0) throw new IOException("The new executable failed its installation check.");
    }

    internal static int Probe()
    {
        try
        {
            InstallPayload.Verify(AppContext.BaseDirectory);
            _ = System.Text.Json.JsonSerializer.Deserialize("{}", Infrastructure.SettingsJsonContext.Default.AppSettings);
            NativeHelpers.SetDllDirectory(Path.Combine(AppContext.BaseDirectory, "frontend"));
            nint library = System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "frontend", "libmpv-2.dll"));
            System.Runtime.InteropServices.NativeLibrary.Free(library);
            using var probe = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "backend", "ffprobe.exe"), "-version")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
                ?? throw new IOException("Could not check the media runtime.");
            _ = probe.StandardOutput.ReadToEndAsync();
            _ = probe.StandardError.ReadToEndAsync();
            if (!probe.WaitForExit(30000)) { probe.Kill(); throw new IOException("The media runtime check timed out."); }
            if (probe.ExitCode != 0) throw new IOException("The media runtime check failed.");
            return 0;
        }
        catch (Exception ex) { RuntimeLog.Fail("Upgrade probe", ex.ToString()); return 1; }
    }

    private static IEnumerable<(DirectoryUpgrade Transaction, string[] Roots)> Transactions()
    {
        if (!Directory.Exists(InstallDiscovery.Store)) yield break;
        UpgradeFiles.RequirePlainPath(InstallDiscovery.Store);
        foreach (string directory in Directory.EnumerateDirectories(InstallDiscovery.Store))
        {
            string path = Path.Combine(directory, "journal.json");
            if (!File.Exists(path)) continue;
            JsonObject journal = AtomicJsonFile.ReadObject(path) ?? throw new IOException("An installation recovery record is unreadable. Backups were preserved.");
            // NOSPACE_01 — journals written before the move to the space-free folder name the spaced
            // folder as their destination. They are still ours and must still recover and prune;
            // rejecting them would block every future install on that machine.
            string destination = UpgradeFiles.FullPath(journal["Destination"]!.GetValue<string>());
            if (!InstallDiscovery.KnownDestinations.Contains(destination, StringComparer.OrdinalIgnoreCase))
                throw new IOException("Unexpected destination in protected installation journal.");
            var roots = journal["Originals"]!.AsArray().Select(x => x!["Path"]!.GetValue<string>())
                .Append(destination).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            yield return (DirectoryUpgrade.Open(directory, roots), roots);
        }
    }

    private static async Task RecoverInterruptedAsync()
    {
        foreach (var (transaction, roots) in Transactions())
        {
            if (transaction.Phase is "Committed" or "Pruned" or "RolledBack") continue;
            InstallDiscovery.EnsureIdle(roots, Environment.ProcessId);
            transaction.Rollback();
            await new UpgradeRegistration(true, transaction.DirectoryPath, roots).RestoreAsync().ConfigureAwait(false);
        }
    }

    public static async Task<int> MaintainAsync(bool ownsInstallerGate = false)
    {
        if (!IsElevated()) return 2;
        using var gate = new Semaphore(1, 1, DeploymentFootprint.InstallerGateName);
        if (!ownsInstallerGate && !gate.WaitOne(0)) return 2;
        try
        {
            var all = Transactions().ToList();
            DateTimeOffset? newest = all.Where(x => x.Transaction.Phase is "Committed" or "Pruned")
                .Select(x => x.Transaction.ConfirmedAt).Max();
            // WORKERSTAGE_01 — the user cannot delete the protected worker folders; this elevated pass does.
            int swept = ProtectedWorkerStage.SweepStale(Environment.ProcessPath);
            if (swept > 0) RuntimeLog.Info("Upgrade maintenance", $"Removed {swept} old protected installer folder(s).");
            foreach (var (transaction, roots) in all)
            {
                if (transaction.Phase is "Committed" or "Pruned")
                {
                    await new UpgradeRegistration(true, transaction.DirectoryPath, roots).RemoveLegacyAsync().ConfigureAwait(false);
                    UpgradeFiles.DeleteTree(Path.Combine(transaction.DirectoryPath, "payload"), transaction.DirectoryPath);
                }
                // ONEBACKUP_01: prune older confirmed backups when a newer committed installation exists.
                transaction.PruneBackup(DateTimeOffset.UtcNow,
                    supersededByNewer: newest is { } latest && transaction.ConfirmedAt is { } at && at < latest);
            }
            string xml = await TaskCommandAsync("/Query", "/TN", LegacyProductIdentity.CompactName, "/XML").ConfigureAwait(false);
            var oldExecutables = Transactions().SelectMany(x => x.Roots)
                .SelectMany(root => LegacyProductIdentity.ExecutableNames.Select(name => Path.Combine(root, name)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(xml) && System.Xml.Linq.XDocument.Parse(xml).Descendants()
                    .Where(x => x.Name.LocalName == "Command").Any(x => oldExecutables.Contains(x.Value.Trim('"'))))
                _ = await TaskCommandAsync("/Delete", "/TN", LegacyProductIdentity.CompactName, "/F").ConfigureAwait(false);
            // NOSYSTEMTASK_01: remove the former scheduled maintenance task; maintenance runs through the installer gate.
            string ownTask = await TaskCommandAsync("/Query", "/TN", DeploymentFootprint.ScheduledTaskName, "/XML").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ownTask))
                _ = await TaskCommandAsync("/Delete", "/TN", DeploymentFootprint.ScheduledTaskName, "/F").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) { RuntimeLog.WarnThrottled("Upgrade maintenance", ex.Message); return 1; }
        finally { if (!ownsInstallerGate) gate.Release(); }
    }

    private static async Task<string> TaskCommandAsync(params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("schtasks.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string error = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0) RuntimeLog.Info("Upgrade task", error);
        return await output.ConfigureAwait(false);
    }
}
