using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Infrastructure;

public sealed class RecoveryManager
{
    public const int SchemaVersion = 1;

    private readonly ApplicationPaths _paths;
    private readonly TimeSpan _safeModeThreshold = TimeSpan.FromSeconds(120);
    private static readonly object _saveLock = new();

    private static long _writeVersion;
    private static readonly Dictionary<string, long> _appliedVersionByFile = new(StringComparer.OrdinalIgnoreCase);

    private static long NextVersion() => Interlocked.Increment(ref _writeVersion);

    private bool TryClaimVersionLocked(long version)
    {
        string key = _paths.RecoveryStateFile;
        if (_appliedVersionByFile.TryGetValue(key, out long applied) && version <= applied) return false;
        _appliedVersionByFile[key] = version;
        return true;
    }
    private bool _skipCleanup;

    public const int MaxRestoreAttempts = 3;
    private int _crashedRestoreAttempts;
    private bool _restoreAttemptedThisProcess;

    public int CrashedRestoreAttempts => _crashedRestoreAttempts;

    public bool RestoreCrashDetected => _crashedRestoreAttempts > 0;

    public bool CanRetryRestore => !_restoreAttemptedThisProcess && _crashedRestoreAttempts < MaxRestoreAttempts;

    public string? QuarantinedSnapshotFile { get; private set; }

    public RecoveryManager(ApplicationPaths? paths = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
    }

    public void MarkCleanShutdownIntent()
    {
        try
        {
            File.WriteAllText(_paths.CleanShutdownIntentFile,
                $"{Environment.ProcessId}:{DateTime.UtcNow:O}");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    private void ClearCleanShutdownIntent()
    {
        try
        {
            if (File.Exists(_paths.CleanShutdownIntentFile))
                File.Delete(_paths.CleanShutdownIntentFile);
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public bool CheckFault()
    {
        if (!File.Exists(_paths.RecoveryStateFile))
        {
            return false;
        }

        if (File.Exists(_paths.CleanShutdownIntentFile))
        {
            CoreLogger.Info("Recovery",
                "Previous session was closing intentionally but did not finish its cleanup (typically a Windows shutdown). Not treating this as a crash.");
            ClearCleanShutdownIntent();
            CleanupLock();
            return false;
        }

        if (IsSafeModeActive())
        {
            return false;
        }

        if (RestoreCrashDetected)
        {
            CoreLogger.Info("Recovery",
                $"Previous restore attempt crashed ({_crashedRestoreAttempts} consecutive). Recovery state preserved; awaiting user choice.");
            return true;
        }

        if (!File.Exists(_paths.AppSessionLockFile))
        {
            return false;
        }

        try
        {
            string content = File.ReadAllText(_paths.AppSessionLockFile).Trim();
            string[] parts = content.Split(':');
            if (parts.Length > 0 && int.TryParse(parts[0], out int oldPid))
            {
                if (oldPid != Environment.ProcessId)
                {
                    try
                    {
                        Process proc = Process.GetProcessById(oldPid);
                        if (!proc.HasExited)
                        {
                            if (proc.ProcessName == Process.GetCurrentProcess().ProcessName)
                            {
                                if (parts.Length > 1 && long.TryParse(parts[1], out long oldTicks))
                                {
                                    if (proc.StartTime.Ticks == oldTicks)
                                        return false;
                                }
                                else
                                {
                                    return false;
                                }
                            }
                        }
                    }
                    catch (ArgumentException swallowed3)
                    {
                        global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
                    }
                    catch (InvalidOperationException swallowed4)
                    {
                        global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);
                    }
                    catch (System.ComponentModel.Win32Exception swallowed7)
                    {
                        global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed7);
                    }
                }
            }
        }
        catch (Exception swallowed6)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed6);
        }

        CoreLogger.Info("Recovery", "Previous session did not shut down cleanly (crash detected). Recovery state is available to restore.");
        return true;
    }

    public bool IsSafeModeActive()
    {
        if (!File.Exists(_paths.SafeModeSentinelFile)) return false;

        try
        {
            string content = "";
            try { content = File.ReadAllText(_paths.SafeModeSentinelFile).Trim(); }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            if (TryParseOwner(content, out int ownerPid, out long ownerTicks))
            {
                if (ownerPid == Environment.ProcessId) return true;

                if (IsProcessStillRunning(ownerPid, ownerTicks)) return true;
            }
            else
            {
                TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_paths.SafeModeSentinelFile);
                if (age < _safeModeThreshold) return true;
            }

            _crashedRestoreAttempts = Math.Max(_crashedRestoreAttempts, ParseRestoreAttempt(content));
            CoreLogger.Info("Recovery",
                $"Restore owner is gone (attempt {_crashedRestoreAttempts} crashed). Stale restore marker cleared; recovery state preserved.");

            File.Delete(_paths.SafeModeSentinelFile);
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        return false;
    }

    private static int ParseRestoreAttempt(string content)
    {
        string[] parts = (content ?? "").Split(':');
        return parts.Length >= 3 && int.TryParse(parts[2], out int attempt) && attempt > 0 ? attempt : 1;
    }

    private static bool TryParseOwner(string content, out int pid, out long startTicks)
    {
        pid = 0; startTicks = 0;
        if (string.IsNullOrWhiteSpace(content)) return false;
        string[] parts = content.Split(':');
        return parts.Length >= 2
               && int.TryParse(parts[0], out pid)
               && long.TryParse(parts[1], out startTicks);
    }

    private static bool IsProcessStillRunning(int pid, long startTicks)
    {
        try
        {
            using Process proc = Process.GetProcessById(pid);
            return !proc.HasExited && proc.StartTime.Ticks == startTicks;
        }
        catch (ArgumentException swallowed8)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed8);
            return false;
        }
        catch (InvalidOperationException swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
            return false;
        }
        catch (System.ComponentModel.Win32Exception swallowed5)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed5);
            return false;
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); return false; }
    }

    public bool ActivateSafeMode()
    {
        if (!CanRetryRestore)
        {
            CoreLogger.Info("Recovery",
                $"Restore not started: {_crashedRestoreAttempts} consecutive restore crashes (limit {MaxRestoreAttempts}) or already attempted in this process.");
            return false;
        }

        _restoreAttemptedThisProcess = true;
        try
        {
            _paths.EnsureWritableDirectories();
            File.WriteAllText(_paths.SafeModeSentinelFile,
                $"{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.Ticks}:{_crashedRestoreAttempts + 1}");
            CoreLogger.Info("Recovery", "Safe mode activated to prevent a crash loop.");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Recovery", $"Failed to activate safe mode: {ex.Message}");
        }
        return true;
    }

    public void DeactivateSafeMode()
    {
        try
        {
            if (File.Exists(_paths.SafeModeSentinelFile))
            {
                File.Delete(_paths.SafeModeSentinelFile);
            }
            _crashedRestoreAttempts = 0;
            CoreLogger.Info("Recovery", "Safe mode deactivated after successful recovery.");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void AcquireLock()
    {
        try
        {
            _paths.EnsureWritableDirectories();
            string lockData = $"{Environment.ProcessId}:{Process.GetCurrentProcess().StartTime.Ticks}";
            File.WriteAllText(_paths.AppSessionLockFile, lockData);
            ClearCleanShutdownIntent();
            CoreLogger.Info("Recovery", $"Session lock acquired (PID {Environment.ProcessId}).");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Recovery", $"Failed to acquire session lock: {ex.Message}");
        }
    }

    public void CleanupLock()
    {
        if (_skipCleanup)
        {
            return;
        }

        try
        {
            if (File.Exists(_paths.AppSessionLockFile))
            {
                File.Delete(_paths.AppSessionLockFile);
            }

            if (File.Exists(_paths.SafeModeSentinelFile))
            {
                File.Delete(_paths.SafeModeSentinelFile);
            }

            ClearState();
            ClearCleanShutdownIntent();
            CoreLogger.Info("Recovery", "Session lock and recovery state cleaned up on normal shutdown.");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void SetSkipCleanup(bool skip)
    {
        _skipCleanup = skip;
    }

    public void ReleaseLockOnly()
    {
        try
        {
            if (File.Exists(_paths.AppSessionLockFile))
            {
                File.Delete(_paths.AppSessionLockFile);
            }
            CoreLogger.Info("Recovery", "Session lock released for process handoff (recovery state preserved).");
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void SaveStateAsync(JsonObject state)
    {
        long version = NextVersion();
        Task.Run(() => SaveStateVersioned(state, version));
    }

    public void UpdateGranularSession(JsonObject? session)
    {
        lock (_saveLock)
        {
            var state = AtomicJsonFile.ReadObject(_paths.RecoveryStateFile);
            if (state == null && session == null) return;
            state ??= new JsonObject();
            state["granular_session"] = session?.DeepClone();
            WriteLocked(state);
        }
    }

    public void SaveState(JsonObject state) => SaveStateVersioned(state, NextVersion());

    internal static long ReserveVersionForTests() => NextVersion();
    internal void ApplySaveForTests(JsonObject state, long version) => SaveStateVersioned(state, version);

    private void SaveStateVersioned(JsonObject state, long version)
    {
        lock (_saveLock)
        {
            if (!TryClaimVersionLocked(version))
            {
                CoreLogger.Debug("Recovery", $"Dropped a stale recovery save (v{version}); a newer save or clear already landed.");
                return;
            }

            WriteLocked(state);
        }
    }

    private void WriteLocked(JsonObject state)
    {
        {
            try
            {
                _paths.EnsureWritableDirectories();

                state["schema_version"] = SchemaVersion;

                if (!state.ContainsKey("granular_session") && File.Exists(_paths.RecoveryStateFile))
                {
                    try
                    {
                        var existing = AtomicJsonFile.ReadObject(_paths.RecoveryStateFile);
                        if (existing != null && existing.TryGetPropertyValue("granular_session", out var gran) && gran != null)
                        {
                            state["granular_session"] = gran.DeepClone();
                        }
                    }
                    catch (Exception ex) { CoreLogger.Swallowed(ex); }
                }

                AtomicJsonFile.WriteObject(_paths.RecoveryStateFile, state);
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("Recovery", $"Failed to save recovery state: {ex.Message}");
            }
        }
    }

    public JsonObject? LoadState()
    {
        if (!File.Exists(_paths.RecoveryStateFile))
        {
            return null;
        }

        try
        {
            var state = AtomicJsonFile.ReadObject(_paths.RecoveryStateFile);
            if (state != null)
            {
                int fileVersion = SchemaVersion;
                if (state.TryGetPropertyValue("schema_version", out JsonNode? versionNode) && versionNode != null)
                {
                    try 
                    { 
                        if (versionNode.AsValue().TryGetValue(out int v)) fileVersion = v;
                        else if (versionNode.AsValue().TryGetValue(out string? s) && int.TryParse(s, out int parsed)) fileVersion = parsed;
                        else fileVersion = versionNode.GetValue<int>(); 
                    }
                    catch
                    {
                        CoreLogger.Info("Recovery", "Unreadable schema_version in recovery state. Assuming current schema.");
                        fileVersion = SchemaVersion;
                    }

                    if (fileVersion < 1)
                    {
                        CoreLogger.Info("Recovery", $"Invalid schema_version {fileVersion} in recovery state. Assuming current schema.");
                        fileVersion = SchemaVersion;
                    }
                }
                else
                {
                    CoreLogger.Info("Recovery",
                        "Recovery state predates schema stamping; treating it as the current shape.");
                }

                if (fileVersion != SchemaVersion)
                {
                    CoreLogger.Info("Recovery",
                        $"Recovery state was written by a different app version (found schema {fileVersion}, this build expects {SchemaVersion}). " +
                        "Skipping the restore and starting fresh; the file has been left on disk.");
                    return null;
                }

                CoreLogger.Info("Recovery", "Recovery state loaded for restore.");
            }
            return state;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("Recovery", $"Failed to load recovery state: {ex.Message}");
            return null;
        }
    }

    public bool QuarantineSnapshot()
    {
        long version = NextVersion();
        lock (_saveLock)
        {
            if (!TryClaimVersionLocked(version)) return false;
            try
            {
                if (!File.Exists(_paths.RecoveryStateFile)) return false;
                string target = $"{_paths.RecoveryStateFile}.crashed-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
                File.Move(_paths.RecoveryStateFile, target, overwrite: false);
                QuarantinedSnapshotFile = target;
                _crashedRestoreAttempts = 0;
                CoreLogger.Info("Recovery", $"Crashing recovery snapshot preserved at {QuarantinedSnapshotFile}.");
                return true;
            }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); return false; }
        }
    }

    public void DiscardRecoveryState()
    {
        ClearState();
        _crashedRestoreAttempts = 0;
        CoreLogger.Info("Recovery", "Recovery state discarded by explicit user choice.");
    }

    public void ClearState()
    {
        long version = NextVersion();
        lock (_saveLock)
        {
            if (!TryClaimVersionLocked(version)) return;
            try
            {
                if (File.Exists(_paths.RecoveryStateFile))
                {
                    File.Delete(_paths.RecoveryStateFile);
                }
            }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        }
    }
}
