namespace FreeVideoStudio.Core.Infrastructure;

public static class UiStateStore
{
    private static readonly object Sync = new();
    private static bool _migrated;

    private static string Directory_ => ApplicationPaths.CreateDefault().UiStateDirectory;

    public static void MigrateLegacyFilesOnce()
    {
        lock (Sync)
        {
            if (_migrated) return;
            _migrated = true;
            if (DevelopmentEnvironment.IsActive) return;

            try
            {
                string legacy = ApplicationPaths.LegacyRoamingUiStateDirectory;
                if (!Directory.Exists(legacy)) return;

                string target = Directory_;
                Directory.CreateDirectory(target);

                int moved = 0;
                foreach (string file in Directory.GetFiles(legacy))
                {
                    string destination = Path.Combine(target, Path.GetFileName(file));

                    if (File.Exists(destination)) continue;

                    try
                    {
                        File.Copy(file, destination, overwrite: false);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        CoreLogger.Info("UiState", $"Failed to migrate '{file}': {ex.Message}");
                    }
                }

                if (moved > 0)
                {
                    CoreLogger.Info("UiState",
                        $"Migrated {moved} legacy UI-state file(s) out of %APPDATA% into the consolidated ProgramData root.");
                }
            }
            catch (Exception ex)
            {
                CoreLogger.Info("UiState", $"Legacy UI-state migration skipped: {ex.Message}");
            }
        }
    }

    private static string PathFor(string fileName)
    {
        MigrateLegacyFilesOnce();
        string dir = Directory_;
        try { Directory.CreateDirectory(dir); } catch (Exception ex) { CoreLogger.Warn("UiState", $"Failed to create directory '{dir}': {ex.Message}"); }
        return Path.Combine(dir, fileName);
    }

    public static string ReadText(string fileName, string fallback = "")
    {
        try
        {
            string path = PathFor(fileName);
            return File.Exists(path) ? File.ReadAllText(path) : fallback;
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("UiState", $"Failed to read text from '{fileName}': {ex.Message}");
            return fallback;
        }
    }

    public static void WriteText(string fileName, string value)
    {
        try
        {
            AtomicJsonFile.WriteText(PathFor(fileName), value);
        }
        catch (Exception ex)
        {
            CoreLogger.Info("UiState", $"Could not write '{fileName}': {ex.Message}");
        }
    }

    public static int ReadInt(string fileName, int fallback = 0)
    {
        return int.TryParse(ReadText(fileName).Trim(), out int n) ? n : fallback;
    }

    public static void WriteInt(string fileName, int value)
    {
        WriteText(fileName, value.ToString());
    }
}
