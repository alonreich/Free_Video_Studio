
using System.Text.Json;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Ipc;

public sealed class StateTransferStore
{
    /// <summary>USERSCOPE_01 — per Windows user (the data root it guards is per user).</summary>
    public static readonly string MutexName = NamedSystemMutex.UserScopedName("FvsStateTransferMutex");
    public const int SchemaVersion = 1;
    public static readonly TimeSpan DefaultMutexTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan InteractiveMutexTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] BoundsKeys =
    [
        "MainWindowBounds",
        "VideoMergerBounds",
        "CropToolBounds",
        "GranularBounds",
        "MusicWizardBounds",
        "SettingsBounds",
        "VoiceOverWindowBounds",
        "PreviewMonitorWindowBounds",
        "GranularPreviewMonitorBounds",
        "MusicWizardPreviewMonitorBounds",
        "VoiceOverPreviewMonitorBounds",
        "MergerPreviewMonitorBounds"
    ];

    private static readonly string[] SubprocessStateKeys =
    [
        "AdvancedEditorState",
        "VideoMergerState",
        "CropToolState"
    ];

    private static readonly string[] DirectoryPreferenceKeys =
    [
        "UploadVideoDirectory",
        "MergerUploadDirectory",
        "MergerOutputDirectory",
        "CropToolUploadDirectory",
        "CustomMusicDirectory"
    ];

    public StateTransferStore(ApplicationPaths? paths = null)
    {
        Paths = paths ?? ApplicationPaths.CreateDefault();
    }

    public ApplicationPaths Paths { get; }

    public static JsonObject SanitizeObjectInternal(JsonObject state, string context) => SanitizeObject(state, context);
    public static void ApplySanitizedUpdatesInternal(JsonObject current, JsonObject updates, string context) => ApplySanitizedUpdates(current, updates, context);

    public static JsonObject LoadFromDiskDirect(ApplicationPaths paths)
    {
        try
        {
            var store = new StateTransferStore(paths);
            return store.LoadUnlocked();
        }
        catch (System.Exception swallowed6)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed6);
            return new JsonObject { ["schema_version"] = SchemaVersion };
        }
    }

    public async Task<JsonObject> LoadAsync(CancellationToken cancellationToken = default)
    {
        var server = NamedPipeStateServer.ActiveInstance;
        if (server != null && server.IsRunning)
        {
            return server.GetState();
        }

        var startedServer = NamedPipeStateServer.TryStart(Paths);
        if (startedServer != null)
        {
            return startedServer.GetState();
        }

        var ipcState = await NamedPipeStateClient.GetStateAsync(NamedPipeStateClient.FastProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (ipcState != null)
        {
            return ipcState;
        }

        return await Task.Run(() =>
        {
            Paths.EnsureWritableDirectories();

            try
            {
                using NamedSystemMutex guard = NamedSystemMutex.Acquire(
                    MutexName,
                    DefaultMutexTimeout,
                    cancellationToken);

                return LoadUnlocked();
            }
            catch (FreeVideoStudio.Core.Infrastructure.LockException swallowed3)
            {
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
                return new JsonObject();
            }
        }, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>IPCOWNER_01 — total time a mutation may spend retrying a live-but-silent server.</summary>
    public static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(2);

    /// <summary>IPCOWNER_01 — ceiling for one pipe attempt while backing off.</summary>
    private static readonly TimeSpan MaxPipeAttemptTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>IPCOWNER_01 test seam — treat this process's own server as if it were in another
    /// process (forces the pipe / lease path). False in production.</summary>
    internal bool IgnoreInProcessServerForTests { get; init; }

    /// <summary>IPCOWNER_01 test seam — overrides <see cref="DeliveryBudget"/>.</summary>
    internal TimeSpan? DeliveryBudgetForTests { get; init; }

    private enum StateMutation { Save, Update, Handoff, Clear }

    private enum OwnerCommit { Committed, NotOwner, Failed }

    /// <summary>Returns true only when the state was acknowledged by the server or durably committed.</summary>
    public Task<bool> SaveAsync(JsonObject state, CancellationToken cancellationToken = default)
        => StartCommit(StateMutation.Save, Clone(state), NamedPipeStateClient.DefaultTimeout, null, cancellationToken);

    /// <summary>Returns true only when the update was acknowledged by the server or durably committed.</summary>
    public Task<bool> UpdatePropertiesAsync(JsonObject updates, CancellationToken cancellationToken = default)
        => StartCommit(StateMutation.Update, Clone(updates), NamedPipeStateClient.DefaultTimeout, null, cancellationToken);


    public Task<bool> SendHandoffAsync(HandoffPayload payload, TimeSpan? timeout = null, CancellationToken ct = default)
        => StartCommit(StateMutation.Handoff, BuildHandoffNode(payload), timeout ?? NamedPipeStateClient.DefaultTimeout, null, ct);

    /// <summary>Returns true only when the clear was acknowledged by the server or durably committed.</summary>
    public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        => StartCommit(StateMutation.Clear, null, NamedPipeStateClient.DefaultTimeout, null, cancellationToken);

    private static JsonObject BuildHandoffNode(HandoffPayload payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeof(HandoffPayload), IpcJsonContext.Default);
        var node = JsonNode.Parse(bytes) as JsonObject ?? new JsonObject();
        if (payload.ReturnedFromCropTool.HasValue)
        {
            node["returned_from_crop_tool"] = payload.ReturnedFromCropTool.Value;
        }
        return node;
    }

    /// <summary>The commit always runs on the thread pool, so the named mutex is never taken on the UI
    /// thread and no caller's SynchronizationContext is captured. Nothing may block on the result
    /// (UIIPC_01): callers await it, or enqueue through <see cref="SessionStateWriter"/>.</summary>
    private Task<bool> StartCommit(StateMutation op, JsonObject? payload, TimeSpan firstAttempt, TimeSpan? budgetOverride, CancellationToken ct)
        => Task.Run(() => CommitAsync(op, payload, firstAttempt, budgetOverride, ct));

    private async Task<bool> CommitAsync(StateMutation op, JsonObject? payload, TimeSpan firstAttempt, TimeSpan? budgetOverride, CancellationToken ct)
    {
        TimeSpan budget = budgetOverride ?? DeliveryBudgetForTests ?? DeliveryBudget;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan attemptTimeout = firstAttempt > TimeSpan.Zero ? firstAttempt : NamedPipeStateClient.FastProbeTimeout;
        int attempt = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var server = NamedPipeStateServer.ActiveInstance;
                if (!IgnoreInProcessServerForTests && server != null && server.IsRunning && ApplyInProcess(server, op, payload))
                {
                    return true;
                }

                TimeSpan remaining = budget - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;

                TimeSpan thisAttempt = attemptTimeout < remaining ? attemptTimeout : remaining;
                if (await SendOverPipeAsync(op, payload, thisAttempt, ct).ConfigureAwait(false))
                {
                    return true;
                }

                remaining = budget - clock.Elapsed;
                TimeSpan lockWait = remaining > TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100);
                switch (TryCommitAsPersistenceOwner(op, payload, lockWait, ct))
                {
                    case OwnerCommit.Committed: return true;
                    case OwnerCommit.Failed: return false;
                }

                if (clock.Elapsed >= budget) break;
                attempt++;
                attemptTimeout = attemptTimeout + attemptTimeout < MaxPipeAttemptTimeout ? attemptTimeout + attemptTimeout : MaxPipeAttemptTimeout;
                int backoffMs = Math.Min(20 * attempt, 100);
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            CoreLogger.Swallowed(ex);
            CoreLogger.Warn("SessionState", $"Session state {op} was cancelled before it was acknowledged or written.");
            return false;
        }

        CoreLogger.Fail("SessionState",
            $"Session state {op} was NOT saved: the state server holds the lease but did not acknowledge within {budget.TotalMilliseconds:0} ms. " +
            "Nothing was written to disk, so the live session state was not overwritten.");
        return false;
    }

    private static bool ApplyInProcess(NamedPipeStateServer server, StateMutation op, JsonObject? payload) => op switch
    {
        StateMutation.Save => server.SaveState(payload!),
        StateMutation.Update or StateMutation.Handoff => server.UpdateProperties(payload!),
        _ => server.ClearState(),
    };

    private static Task<bool> SendOverPipeAsync(StateMutation op, JsonObject? payload, TimeSpan timeout, CancellationToken ct) => op switch
    {
        StateMutation.Save => NamedPipeStateClient.SaveStateAsync(payload!, timeout, ct),
        StateMutation.Update => NamedPipeStateClient.UpdatePropertiesAsync(payload!, timeout, ct),
        StateMutation.Handoff => NamedPipeStateClient.SendHandoffAsync(payload!, timeout, ct),
        _ => NamedPipeStateClient.ClearStateAsync(timeout, ct),
    };

    /// <summary>
    /// IPCOWNER_01 — the ONLY direct writer of SessionStateFile outside the server. Creating the lease
    /// NEW proves that no server (current or pre-fix build, which OWNS the same name) is alive, and
    /// holding the handle across the write keeps one from starting and loading the file until the
    /// atomic replace has finished. Nothing is ever acquired on the lease (IPCLEASE_01).
    /// Lock order: lease handle → <see cref="MutexName"/> → AtomicJsonFile. Synchronous on purpose:
    /// the named mutex is thread-affine, so no await may sit between acquire and release.
    /// </summary>
    private OwnerCommit TryCommitAsPersistenceOwner(StateMutation op, JsonObject? payload, TimeSpan lockWait, CancellationToken ct)
    {
        Mutex lease;
        bool createdNew;
        try
        {
            lease = new Mutex(initiallyOwned: false, name: IpcProtocol.ServerMutexName, createdNew: out createdNew);
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("SessionState", $"Could not verify state-server ownership: {ex.Message}");
            return OwnerCommit.NotOwner;
        }

        using (lease)
        {
            if (!createdNew) return OwnerCommit.NotOwner;

            try
            {
                Paths.EnsureWritableDirectories();
                using NamedSystemMutex guard = NamedSystemMutex.Acquire(MutexName, lockWait, ct);

                switch (op)
                {
                    case StateMutation.Save:
                    {
                        JsonObject state = SanitizeObject(payload!, "save");
                        state["schema_version"] = SchemaVersion;
                        AtomicJsonFile.WriteObject(Paths.SessionStateFile, state);
                        break;
                    }
                    case StateMutation.Update:
                    case StateMutation.Handoff:
                    {
                        JsonObject current = LoadUnlocked();
                        ApplySanitizedUpdates(current, payload!, op == StateMutation.Handoff ? "handoff" : "update");
                        current["schema_version"] = SchemaVersion;
                        AtomicJsonFile.WriteObject(Paths.SessionStateFile, current);
                        break;
                    }
                    default:
                    {
                        AtomicJsonFile.TryDelete(Paths.SessionStateFile);
                        if (File.Exists(Paths.SessionStateFile))
                        {
                            CoreLogger.Fail("SessionState", "Could not clear session state: the file could not be deleted.");
                            return OwnerCommit.Failed;
                        }
                        break;
                    }
                }

                return OwnerCommit.Committed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LockException ex)
            {
                CoreLogger.Fail("SessionState", $"Could not save session state — the file was locked: {ex.Message}");
                return OwnerCommit.Failed;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("SessionState", $"Could not save session state: {ex.Message}");
                return OwnerCommit.Failed;
            }
        }
    }

    private JsonObject LoadUnlocked()
    {
        try
        {
            JsonObject state = AtomicJsonFile.ReadObject(Paths.SessionStateFile) ?? new JsonObject();
            if (state.Count == 0) return state;

            bool versionOk =
                state.TryGetPropertyValue("schema_version", out var versionNode) &&
                versionNode is JsonValue versionVal &&
                versionVal.TryGetValue<int>(out int version) &&
                version == SchemaVersion;

            if (!versionOk)
            {
                CoreLogger.Info("SessionState",
                    $"Session state has a different schema version; keeping only the entries this build understands (expected {SchemaVersion}).");
                JsonObject migrated = SanitizeObject(state, "load (version mismatch)");
                migrated["schema_version"] = SchemaVersion;
                return migrated;
            }

            return SanitizeObject(state, "load");
        }
        catch (JsonException ex)
        {
            CoreLogger.Fail("SessionState", $"Session state file is not valid JSON and has been quarantined: {ex.Message}");
            QuarantineCorruptedSessionFile();
            return new JsonObject();
        }
        catch (InvalidDataException ex)
        {
            CoreLogger.Fail("SessionState", $"Session state could not be interpreted: {ex.Message}");
            return new JsonObject();
        }
        catch (IOException ex)
        {
            CoreLogger.Fail("SessionState", $"Session state could not be read right now: {ex.Message}");
            return new JsonObject();
        }
        catch (UnauthorizedAccessException swallowed18)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed18);
            return new JsonObject();
        }
    }

    private static JsonObject SanitizeObject(JsonObject state, string context)
    {
        var clean = new JsonObject();
        List<string>? dropped = null;

        foreach (KeyValuePair<string, JsonNode?> property in state)
        {
            if (TryAcceptProperty(property.Key, property.Value, out JsonNode? accepted))
            {
                clean[property.Key] = accepted;
            }
            else
            {
                (dropped ??= new List<string>()).Add(property.Key);
            }
        }

        if (dropped != null)
        {
            CoreLogger.Info("SessionState",
                $"Ignored {dropped.Count} unusable session_state entr{(dropped.Count == 1 ? "y" : "ies")} during {context}: {string.Join(", ", dropped)}.");
        }

        return clean;
    }

    private static void ApplySanitizedUpdates(JsonObject current, JsonObject updates, string context)
    {
        List<string>? dropped = null;

        foreach (KeyValuePair<string, JsonNode?> property in updates)
        {
            if (TryAcceptProperty(property.Key, property.Value, out JsonNode? accepted))
            {
                current[property.Key] = accepted;
            }
            else
            {
                (dropped ??= new List<string>()).Add(property.Key);
            }
        }

        if (dropped != null)
        {
            CoreLogger.Fail("SessionState",
                $"Refused {dropped.Count} unusable session_state entr{(dropped.Count == 1 ? "y" : "ies")} during {context}: {string.Join(", ", dropped)}. Everything else was saved.");
        }
    }

    private static bool TryAcceptProperty(string key, JsonNode? value, out JsonNode? accepted)
    {
        accepted = null;

        try
        {
            ValidateKnownProperty(key, value);
        }
        catch (InvalidDataException swallowed13)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed13);
            return false;
        }
        catch (Exception swallowed8)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed8);
            return false;
        }

        accepted = value?.DeepClone();
        return true;
    }

    private void QuarantineCorruptedSessionFile()
    {
        string path = Paths.SessionStateFile;
        if (!File.Exists(path))
        {
            return;
        }

        string corruptedPath = $"{path}.corrupted";
        AtomicJsonFile.TryDelete(corruptedPath);
        try
        {
            File.Move(path, corruptedPath, overwrite: true);
        }
        catch (IOException swallowed19)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed19);
        }
        catch (UnauthorizedAccessException swallowed14)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed14);
        }
    }

    private static JsonObject Clone(JsonObject source)
    {
        return source.DeepClone().AsObject();
    }

    private static void ValidateKnownProperty(string key, JsonNode? value)
    {
        if (value is null)
        {
            return;
        }

        if (key == "schema_version")
        {
            if (!TryGetInt(value, out int schemaVersion) || schemaVersion < 1)
                throw new InvalidDataException("Invalid session_state schema_version.");
            return;
        }

        if (BoundsKeys.Contains(key))
        {
            if (value is not JsonObject bounds)
                throw new InvalidDataException($"Invalid session_state bounds object for '{key}'.");
            ValidateBoundsObject(key, bounds);
            return;
        }

        if (DirectoryPreferenceKeys.Contains(key))
        {
            if (!TryGetString(value, out _))
                throw new InvalidDataException($"Invalid session_state directory value for '{key}'.");
            return;
        }

        if (key is "WizardVideoVolume" or "WizardMusicVolume" or "MainVolume")
        {
            if (!TryGetDouble(value, out _))
                throw new InvalidDataException($"Invalid session_state volume value for '{key}'.");
            return;
        }

        if (key == "returned_from_crop_tool")
        {
            if (!TryGetBool(value, out _))
                throw new InvalidDataException("Invalid session_state returned_from_crop_tool value.");
            return;
        }

        if (SubprocessStateKeys.Contains(key))
        {
            if (value is not JsonObject subprocessState)
                throw new InvalidDataException($"Invalid session_state subprocess object for '{key}'.");
            ValidateSubprocessState(key, subprocessState);
            return;
        }

        if (key is "source" or "pid" or "written_utc" or "target" or "handoff_utc" or "selected_clip_path" or "selected_clip_start_ms" or "selected_clip_end_ms")
        {
            return;
        }

        if (key is "window_bounds" or "properties")
        {
            if (value is not JsonObject)
                throw new InvalidDataException($"Invalid session_state object for '{key}'.");
            return;
        }

        if (key == "RecentMusicPaths")
        {
            if (value is not System.Text.Json.Nodes.JsonArray)
                throw new InvalidDataException("Invalid session_state RecentMusicPaths value.");
            return;
        }

        throw new InvalidDataException($"Unknown or unvalidated session_state property: '{key}'.");
    }

    private static void ValidateSubprocessState(string key, JsonObject state)
    {
        foreach (KeyValuePair<string, JsonNode?> property in state)
        {
            if (string.IsNullOrEmpty(property.Key))
                throw new InvalidDataException($"Invalid empty property key inside subprocess object '{key}'.");
        }

        if (state.TryGetPropertyValue("schema_version", out JsonNode? version) && version != null)
        {
            if (!TryGetInt(version, out int schemaVersion) || schemaVersion < 1)
                throw new InvalidDataException($"Invalid schema_version inside subprocess object '{key}'.");
        }
        else
        {
            throw new InvalidDataException($"Missing schema_version inside subprocess object '{key}'.");
        }
    }

    private static void ValidateBoundsObject(string key, JsonObject bounds)
    {
        if (bounds.TryGetPropertyValue("X", out JsonNode? x) && x != null && !TryGetInt(x, out _))
            throw new InvalidDataException($"Invalid session_state bounds X value for '{key}'.");
        if (bounds.TryGetPropertyValue("Y", out JsonNode? y) && y != null && !TryGetInt(y, out _))
            throw new InvalidDataException($"Invalid session_state bounds Y value for '{key}'.");
        if (bounds.TryGetPropertyValue("Width", out JsonNode? width) && width != null && !TryGetDouble(width, out _))
            throw new InvalidDataException($"Invalid session_state bounds Width value for '{key}'.");
        if (bounds.TryGetPropertyValue("Height", out JsonNode? height) && height != null && !TryGetDouble(height, out _))
            throw new InvalidDataException($"Invalid session_state bounds Height value for '{key}'.");
        if (bounds.TryGetPropertyValue("WindowState", out JsonNode? windowState) && windowState != null && !TryGetInt(windowState, out _))
            throw new InvalidDataException($"Invalid session_state bounds WindowState value for '{key}'.");
    }

    private static bool TryGetString(JsonNode node, out string value)
    {
        value = string.Empty;
        try
        {
            value = node.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException swallowed10)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed10);
            return false;
        }
        catch (FormatException swallowed4)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);
            return false;
        }
    }

    private static bool TryGetDouble(JsonNode node, out double value)
    {
        value = 0;
        try
        {
            value = node.GetValue<double>();
            return true;
        }
        catch (InvalidOperationException swallowed21)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed21);
            return false;
        }
        catch (FormatException swallowed15)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed15);
            return false;
        }
    }

    private static bool TryGetInt(JsonNode node, out int value)
    {
        value = 0;
        try
        {
            value = node.GetValue<int>();
            return true;
        }
        catch (InvalidOperationException swallowed16)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed16);
            return false;
        }
        catch (FormatException swallowed17)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed17);
            return false;
        }
    }

    private static bool TryGetBool(JsonNode node, out bool value)
    {
        value = false;
        try
        {
            value = node.GetValue<bool>();
            return true;
        }
        catch (InvalidOperationException swallowed11)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed11);
            return false;
        }
        catch (FormatException swallowed7)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed7);
            return false;
        }
    }
}
