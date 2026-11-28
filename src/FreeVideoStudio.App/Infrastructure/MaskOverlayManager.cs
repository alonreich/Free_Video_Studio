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

    public static string NoMaskProfileName => CropConfigDefaults.NoMaskProfileName;

    public static bool IsNoMask(string? profileName)
        => string.Equals(profileName?.Trim(), CropConfigDefaults.NoMaskProfileName, StringComparison.OrdinalIgnoreCase);

    private static IDisposable AcquireConfigLock()
    {
        return NamedSystemMutex.Acquire(
            StateTransferStore.MutexName,
            StateTransferStore.DefaultMutexTimeout);
    }

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

    public static IReadOnlyList<HudElementInfo> ReadProfileElements(string? profileName)
    {
        if (IsNoMask(profileName)) return Array.Empty<HudElementInfo>();
        var load = LoadProfile(profileName);
        return load.Success ? HudElements.FromConfig(load.Config) : Array.Empty<HudElementInfo>();
    }

    public static HudVisibility DefaultVisibility(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return HudVisibility.FromLegacy(SettingsManager.Instance.Defaults.StartShowTeammates, true);
        var d = SettingsManager.Instance.Defaults;
        var hidden = ReadProfileElements(profileName).Where(e => !d.StartHudElement(profileName!, e.Key)).Select(e => e.Key);
        return new HudVisibility(hidden);
    }

    public static AiGameProfile? ProfileGameType(string? profileName)
    {
        if (IsNoMask(profileName)) return AiGameProfile.Generic;
        var load = LoadProfile(profileName);
        return HudElements.ResolveGameType(load.Success ? load.Config : null, profileName);
    }

    public static bool SetProfileGameType(string? profileName, AiGameProfile? type)
    {
        try
        {
            string? safeName = SanitizeProfileName(profileName);
            if (safeName == null || IsNoMask(safeName)) return false;
            var load = LoadProfile(safeName);
            if (!load.Success) return false;
            var config = load.Config!;
            HudElements.SetGameType(config, type);
            AtomicJsonFile.WriteObject(Path.Combine(ProfilesDirectory, safeName + ".json"), config);
            if (string.Equals(SanitizeProfileName(SettingsManager.Instance.ActiveMaskOverlay), safeName, StringComparison.OrdinalIgnoreCase))
            {
                using (AcquireConfigLock())
                {
                    var live = AtomicJsonFile.ReadObject(ApplicationPaths.CreateDefault().CropCoordinatesFile);
                    if (live != null)
                    {
                        HudElements.SetGameType(live, type);
                        AtomicJsonFile.WriteObject(ApplicationPaths.CreateDefault().CropCoordinatesFile, live);
                    }
                }
                if (type is { } t) SettingsManager.Update(s => s.AiTrackingGameProfile = t);
            }
            RuntimeLog.Info("MASK PROFILE", $"'{safeName}' game type set to {(type?.ToString() ?? "automatic")}.");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MASK PROFILE", $"SetProfileGameType('{profileName}') failed: {ex.Message}");
            return false;
        }
    }

    public static string? CreateBlankProfile(string newName, AiGameProfile? gameType = null)
    {
        string? safeName = SanitizeProfileName(newName);
        if (safeName == null || IsNoMask(safeName)) return null;
        EnsureDefaults();
        var pPath = Path.Combine(ProfilesDirectory, safeName + ".json");
        if (File.Exists(pPath)) return null;
        var blank = CropConfigDefaults.CreateNoMask();
        HudElements.SetGameType(blank, gameType);
        AtomicJsonFile.WriteObject(pPath, blank);
        RuntimeLog.Info("MASK PROFILE", $"Created blank profile '{safeName}' (no HUD elements).");
        return safeName;
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
