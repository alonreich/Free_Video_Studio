using System.Security.Cryptography;
using System.Text;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>DEVISOLATION_01: one storage/IPC boundary; application behaviour stays shared.</summary>
public static class DevelopmentEnvironment
{
    public const string MarkerFileName = ".fvs-development";
    public const string SharedMediaEnvironmentVariable = "FVS_DEV_SHARED_MEDIA";
    public static bool ShareUserMedia => IsActive && Environment.GetEnvironmentVariable(SharedMediaEnvironmentVariable) == "1";

    public const string SystemTempEnvironmentVariable = "FVS_SYSTEM_TEMP_ROOT";
    public const string HelperTempEnvironmentVariable = "FVS_DEV_HELPER_TEMP";
    public static string SystemTempRoot => Path.GetFullPath(
        Environment.GetEnvironmentVariable(SystemTempEnvironmentVariable) ??
        Environment.GetEnvironmentVariable("TMP") ?? Path.GetTempPath());
    public static string TemporaryDataRoot => Path.Combine(SystemTempRoot, "FreeVideoStudio_DEV", "data");
    public static string HelperTempRoot => ShareUserMedia
        ? Path.Combine(SystemTempRoot, "Local", "FreeVideoStudio_DEV", "data", "scratch", "os")
        : IsActive ? Path.Combine(DataRoot!, "scratch", "os") : Path.Combine(SystemTempRoot, "FreeVideoStudio");
    public static string HelperTempDirectory => ShareUserMedia && ValidHelperSession() is { } session ? session : HelperTempRoot;

    private static string? ValidHelperSession()
    {
        string? value = Environment.GetEnvironmentVariable(HelperTempEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        string full = Path.GetFullPath(value);
        return string.Equals(Path.GetDirectoryName(full), HelperTempRoot, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(Path.GetFileName(full), "N", out _) ? full : null;
    }

    /// <summary>Keep direct launches and companion processes inside the same sandbox too.</summary>
    public static void InitializeFromRuntimeMarker()
    {
        Environment.SetEnvironmentVariable(SystemTempEnvironmentVariable, SystemTempRoot);
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, MarkerFileName)))
        {
            string root = DataRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeVideoStudio_DEV", "data");
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, root);
            Environment.SetEnvironmentVariable(SharedMediaEnvironmentVariable, "1");
        }
        if (ShareUserMedia) MigratePreferencesToTemporaryStorage(DataRoot!, TemporaryDataRoot);
        if (ShareUserMedia && ValidHelperSession() is null)
            Environment.SetEnvironmentVariable(HelperTempEnvironmentVariable,
                Path.Combine(HelperTempRoot, Guid.NewGuid().ToString("N")));
        string temp = HelperTempDirectory;
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        if (IsActive && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR")))
            Environment.SetEnvironmentVariable("FVS_DEV_LOG_DIR", Path.Combine(ShareUserMedia ? TemporaryDataRoot : DataRoot!, "logs"));
    }

    public static void MigratePreferencesToTemporaryStorage(string persistentRoot, string temporaryRoot)
    {
        using var guard = NamedSystemMutex.Acquire(
            NamedSystemMutex.UserScopedName("FvsTemporaryPreferences"), TimeSpan.FromSeconds(10));
        string marker = Path.Combine(persistentRoot, ".temporary-preferences-v1");
        if (File.Exists(marker)) return;
        Directory.CreateDirectory(persistentRoot);
        foreach (string name in new[] { "settings.json", "session_state.json", "uistate", "roaming" })
            CopyAbsent(Path.Combine(persistentRoot, name), Path.Combine(temporaryRoot, name));
        AtomicJsonFile.WriteText(marker, "STORAGEPATHS_01");
    }

    private static void CopyAbsent(string source, string destination)
    {
        if (!File.Exists(source) && !Directory.Exists(source)) return;
        foreach (string path in new[] { source, destination })
            for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing linked preferences migration path.");
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (string entry in Directory.EnumerateFileSystemEntries(source))
            {
                if (Path.GetFileName(source).Equals("uistate", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(entry) is "meme_dimensions.json" or "update_last_check_utc.txt" or
                        "update_last_result.txt" or "update_pending_notes.txt") continue;
                CopyAbsent(entry, Path.Combine(destination, Path.GetFileName(entry)));
            }
        }
        else if (!File.Exists(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
    }

    public static string? DataRoot
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
        }
    }

    public static bool IsActive => DataRoot is not null;
    public static string? DirectoryFor(string name) => DataRoot is { } root ? Path.Combine(root, name) : null;

    public static string ScopeSuffix => ScopeFor(DataRoot);

    public static string ScopeFor(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return "_DEV_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
    }
}
