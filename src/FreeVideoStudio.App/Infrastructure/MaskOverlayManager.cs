// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App.Infrastructure;

public static class MaskOverlayManager
{
    public static string ProfilesDirectory => Path.Combine(ApplicationPaths.CreateDefault().ProgramDataRoot, "MaskProfiles");

    /// <summary>
    /// NOMASK_01 — the reserved, HUD-free profile. Name lives in Core so the Main App, the
    /// Settings window and the Crop Tools app all compare against the same literal.
    /// </summary>
    public static string NoMaskProfileName => CropConfigDefaults.NoMaskProfileName;

    /// <summary>NOMASK_01 — true when <paramref name="profileName"/> is the reserved HUD-free profile.</summary>
    public static bool IsNoMask(string? profileName)
        => string.Equals(profileName?.Trim(), CropConfigDefaults.NoMaskProfileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ISSUE_3: all reads/writes of the shared crops_coordinations.conf must hold the
    /// same named system mutex that CropConfigStore/StateTransferStore use, because
    /// Main App and Crop Tools are both alive during process handoff.
    /// </summary>
    private static IDisposable AcquireConfigLock()
    {
        return NamedSystemMutex.Acquire(
            StateTransferStore.MutexName,
            StateTransferStore.DefaultMutexTimeout);
    }

    /// <summary>
    /// ISSUE_6: profile names are user input that becomes a file name. Strips invalid
    /// filename characters and rejects names that would resolve outside the profiles
    /// directory. Returns null when no safe name can be produced.
    /// </summary>
    public static string? SanitizeProfileName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(rawName.Trim().Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) return null;

        string fullPath = Path.GetFullPath(Path.Combine(ProfilesDirectory, cleaned + ".json"));
        string root = Path.GetFullPath(ProfilesDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

        return cleaned;
    }

    public static void EnsureDefaults()
    {
        if (!Directory.Exists(ProfilesDirectory))
            Directory.CreateDirectory(ProfilesDirectory);

        string[] defaultProfiles = { "Fortnite", "Counter Strike", "Battlefield", "DJI Drones", "Apex Legends", "Call of Duty", CropConfigDefaults.NoMaskProfileName };
        foreach (var p in defaultProfiles)
        {
            var pPath = Path.Combine(ProfilesDirectory, p + ".json");

            if (IsNoMask(p))
            {
                JsonObject? existing = null;
                if (File.Exists(pPath))
                {
                    try { existing = AtomicJsonFile.ReadObject(pPath); }
                    catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }

                if (!CropConfigDefaults.IsHudFree(existing))
                {
                    if (existing != null)
                        RuntimeLog.Info("MASK PROFILE", $"'{p}' had HUD layers or missing keys. Restoring the HUD-free document.");
                    AtomicJsonFile.WriteObject(pPath, CropConfigDefaults.CreateNoMask());
                }
                continue;
            }

            if (p == "Fortnite" && File.Exists(pPath))
            {
                var existing = AtomicJsonFile.ReadObject(pPath);
                if (existing == null || !CropConfigStore.IsUsableConfig(existing))
                {
                    File.Copy(pPath, pPath + $".invalid.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak");
                    AtomicJsonFile.WriteObject(pPath, CropConfigDefaults.Create());
                    RuntimeLog.Info("MASK PROFILE", "Restored the damaged Fortnite profile from the shipped defaults; the old file was backed up.");
                }
            }

            if (!File.Exists(pPath))
            {
                var def = CropConfigDefaults.Create();
                AtomicJsonFile.WriteObject(pPath, def);
            }
        }

        if (!File.Exists(ApplicationPaths.CreateDefault().CropCoordinatesFile))
        {
            // CROPFIRSTBOOT_01 — ApplyProfile calls EnsureDefaults. Seed the missing live
            // document directly so a fresh install cannot recurse before its first write.
            var activeName = SanitizeProfileName(SettingsManager.Instance.ActiveMaskOverlay);
            var active = activeName == null ? null
                : AtomicJsonFile.ReadObject(Path.Combine(ProfilesDirectory, activeName + ".json"));
            var initial = active != null && CropConfigStore.IsUsableConfig(active)
                ? HudConfig.Sanitize(active, migrateLegacy: true)
                : CropConfigDefaults.Create();
            using (AcquireConfigLock())
            {
                var livePath = ApplicationPaths.CreateDefault().CropCoordinatesFile;
                if (!File.Exists(livePath)) AtomicJsonFile.WriteObject(livePath, initial);
            }
        }
    }

    public static List<string> GetAvailableProfiles()
    {
        EnsureDefaults();
        var files = Directory.GetFiles(ProfilesDirectory, "*.json");
        var list = new List<string>();
        foreach (var f in files)
        {
            list.Add(Path.GetFileNameWithoutExtension(f));
        }
        return list;
    }

    public readonly record struct ProfileLoadResult(
        bool Success,
        string? ProfileName,
        JsonObject? Config,
        string? ErrorMessage)
    {
        public static ProfileLoadResult Succeeded(string profileName, JsonObject config) =>
            new(true, profileName, config, null);

        public static ProfileLoadResult Failed(string? profileName, string errorMessage) =>
            new(false, profileName, null, errorMessage);
    }

    /// <summary>
    /// CROPPROFILE_01 — Explicitly loads and validates a mask profile without altering live state.
    /// Returns a validated identity and configuration, or a detailed failure reason.
    /// </summary>
    public static ProfileLoadResult LoadProfile(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return ProfileLoadResult.Failed(null, "No profile name specified.");
        }

        string? safeName = SanitizeProfileName(profileName);
        if (safeName == null)
        {
            return ProfileLoadResult.Failed(profileName, $"Invalid profile name '{profileName}'.");
        }

        if (IsNoMask(safeName))
        {
            return ProfileLoadResult.Failed(safeName, $"\"{NoMaskProfileName}\" is a reserved HUD-free profile and cannot be edited.");
        }

        EnsureDefaults();

        string pPath = Path.Combine(ProfilesDirectory, safeName + ".json");
        if (!File.Exists(pPath))
        {
            return ProfileLoadResult.Failed(safeName, $"Profile '{safeName}' does not exist.");
        }

        JsonObject? config;
        try
        {
            config = AtomicJsonFile.ReadObject(pPath);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MASK PROFILE", $"Failed to read profile '{safeName}': {ex.Message}");
            return ProfileLoadResult.Failed(safeName, $"Profile '{safeName}' could not be read: {ex.Message}");
        }

        if (config == null || !CropConfigStore.IsUsableConfig(config))
        {
            RuntimeLog.Fail("MASK PROFILE", $"Profile '{safeName}' contains invalid or corrupted configuration.");
            return ProfileLoadResult.Failed(safeName, $"Profile '{safeName}' is corrupted or invalid.");
        }

        var sanitized = HudConfig.Sanitize(config, migrateLegacy: true);
        return ProfileLoadResult.Succeeded(safeName, sanitized);
    }

    public static void ApplyProfile(string profileName)
    {
        try
        {
            var loadResult = LoadProfile(profileName);
            if (!loadResult.Success)
            {
                RuntimeLog.Info("MASK PROFILE", $"Profile '{profileName}' not found or invalid: {loadResult.ErrorMessage}. Keeping current crop configuration.");
                return;
            }

            using (AcquireConfigLock())
            {
                AtomicJsonFile.WriteObject(ApplicationPaths.CreateDefault().CropCoordinatesFile, loadResult.Config!);
            }

            SettingsManager.Update(s => s.ActiveMaskOverlay = loadResult.ProfileName!);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MASK PROFILE", $"ApplyProfile('{profileName}') failed: {ex.Message}");
        }
    }

    /// <summary>
    /// PROJ_11 — THE LIVE HUD MASK, AS A VALUE A PROJECT CAN STORE.
    ///
    /// <para>
    /// The active profile name plus the crop configuration that name currently resolves to, with a
    /// content fingerprint over the configuration. This is what <c>ProjectSession.Capture</c>
    /// records so a <c>.fvsproj</c> knows which mask it was built with — see
    /// <see cref="FreeVideoStudio.Core.Project.ProjectMask"/> for why the name alone is not
    /// enough.
    /// </para>
    ///
    /// <para>
    /// Returns <see langword="null"/> when the live configuration cannot be read at all. The caller
    /// treats that as "no mask recorded", never as "no mask applied" — the two are different and
    /// conflating them would write a project claiming an unmasked export that was in fact masked.
    /// </para>
    /// </summary>
    public static ProjectMask? ReadLiveMask()
    {
        JsonObject? config;
        using (AcquireConfigLock())
        {
            config = AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
        }

        if (config is null) return null;

        string profile = SanitizeProfileName(SettingsManager.Instance.ActiveMaskOverlay) ?? NoMaskProfileName;

        return new ProjectMask(profile, ProjectMask.ComputeFingerprint(config), (JsonObject)config.DeepClone());
    }

    public static bool SyncActiveProfileFromCurrentConfig()
    {
        return SyncProfileFromCurrentConfig(SettingsManager.Instance.ActiveMaskOverlay);
    }

    /// <summary>
    /// CROPPROFILE_01 — Writes live crop coordinates back to a specific named profile file,
    /// avoiding blind fallback to global ActiveMaskOverlay.
    /// </summary>
    public static bool SyncProfileFromCurrentConfig(string? profileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileName)) return false;
            string? safeName = SanitizeProfileName(profileName);
            if (safeName == null) return false;

            if (IsNoMask(safeName))
            {
                RuntimeLog.Info("MASK PROFILE", $"'{safeName}' is read-only. Live crop config NOT written back to it.");
                return false;
            }

            var pPath = Path.Combine(ProfilesDirectory, safeName + ".json");
            JsonObject? current;
            using (AcquireConfigLock())
            {
                current = AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
            }

            if (current != null && CropConfigStore.IsUsableConfig(current))
            {
                if (!Directory.Exists(ProfilesDirectory)) Directory.CreateDirectory(ProfilesDirectory);
                AtomicJsonFile.WriteObject(pPath, current);
                return true;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MASK PROFILE", $"Profile sync for '{profileName}' failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// CROPPROFILE_01 — Atomically writes a validated configuration directly to a specific profile file.
    /// </summary>
    public static bool SaveProfile(string? profileName, JsonObject config)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileName)) return false;
            string? safeName = SanitizeProfileName(profileName);
            if (safeName == null) return false;

            if (IsNoMask(safeName))
            {
                RuntimeLog.Info("MASK PROFILE", $"'{safeName}' is read-only. Profile file NOT written.");
                return false;
            }

            if (!CropConfigStore.IsUsableConfig(config))
            {
                RuntimeLog.Fail("MASK PROFILE", $"SaveProfile('{safeName}') refused: config is not usable.");
                return false;
            }

            if (!Directory.Exists(ProfilesDirectory)) Directory.CreateDirectory(ProfilesDirectory);
            var pPath = Path.Combine(ProfilesDirectory, safeName + ".json");
            var sanitized = HudConfig.Sanitize(config, migrateLegacy: true);
            AtomicJsonFile.WriteObject(pPath, sanitized);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MASK PROFILE", $"SaveProfile('{profileName}') failed: {ex.Message}");
            return false;
        }
    }

    public static void CreateNewProfile(string newName)
    {
        string? safeName = SanitizeProfileName(newName);
        if (safeName == null) return;

        if (IsNoMask(safeName))
        {
            RuntimeLog.Info("MASK PROFILE", $"'{safeName}' is a reserved profile name. Creation refused.");
            return;
        }

        EnsureDefaults();

        var pPath = Path.Combine(ProfilesDirectory, safeName + ".json");
        JsonObject? current;
        using (AcquireConfigLock())
        {
            current = AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
        }
        current ??= CropConfigDefaults.Create();

        AtomicJsonFile.WriteObject(pPath, current);

        SettingsManager.Update(s => s.ActiveMaskOverlay = safeName);
    }
}
