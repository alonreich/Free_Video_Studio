using System;
using System.IO;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Project;

public readonly record struct ProjectIoResult(bool Success, string? Path, string? Error)
{
    public static ProjectIoResult Ok(string path) => new(true, path, null);
    public static ProjectIoResult Fail(string message) => new(false, null, message);
}

public static class ProjectStore
{
    public const string BackupSuffix = ".bak";

    public static ProjectIoResult Save(ProjectDocument document, string path)
    {
        if (document == null) return ProjectIoResult.Fail("There is no project to save.");
        if (string.IsNullOrWhiteSpace(path)) return ProjectIoResult.Fail("No file name was given.");

        try
        {
            path = NormalizeExtension(path);

            TryBackup(path);

            JsonObject payload = ProjectSerializer.Write(document);
            AtomicJsonFile.WriteObject(path, payload);

            CoreLogger.Info("PROJECT", $"Saved '{Path.GetFileName(path)}'.");
            return ProjectIoResult.Ok(path);
        }
        catch (UnauthorizedAccessException swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
            return ProjectIoResult.Fail("Windows would not let the app write to that folder. Try a folder inside your Documents.");
        }
        catch (DirectoryNotFoundException swallowed3)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
            return ProjectIoResult.Fail("That folder no longer exists.");
        }
        catch (IOException ex)
        {
            CoreLogger.Fail("PROJECT", $"Save failed: {ex.Message}");
            return ProjectIoResult.Fail($"The project could not be written: {ex.Message}");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("PROJECT", $"Save failed: {ex}");
            return ProjectIoResult.Fail("The project could not be saved. The log has the details.");
        }
    }

    public static ProjectDocument? Load(string path, out string? error, out bool loadedFromBackup)
    {
        error = null;
        loadedFromBackup = false;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No file was chosen.";
            return null;
        }

        if (!File.Exists(path))
        {
            error = "That project file no longer exists.";
            return null;
        }

        ProjectDocument? doc = TryReadOne(path, out error);
        if (doc != null) return doc;
        if (IsFromANewerVersion(path)) return null;

        string backup = path + BackupSuffix;
        if (!File.Exists(backup)) return null;

        ProjectDocument? fromBackup = TryReadOne(backup, out string? backupError);
        if (fromBackup == null)
        {
            CoreLogger.Fail("PROJECT", $"Backup also unreadable: {backupError}");
            return null;
        }

        loadedFromBackup = true;
        error = null;
        CoreLogger.Info("PROJECT", $"Live file damaged; recovered '{Path.GetFileName(path)}' from backup.");
        return fromBackup;
    }

    private static ProjectDocument? TryReadOne(string path, out string? error)
    {
        error = null;
        try
        {
            JsonObject? root = AtomicJsonFile.ReadObject(path);
            if (root == null)
            {
                error = "That project file is empty or damaged.";
                return null;
            }

            return ProjectSerializer.Read(MigrationPathResolver.ResolveProject(root), out error);
        }
        catch (UnauthorizedAccessException swallowed2)
        {
            error = "Windows would not let the app read that file.";
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
            return null;
        }
        catch (IOException ex)
        {
            error = $"The project could not be read: {ex.Message}";
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(ex);
            return null;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("PROJECT", $"Load failed: {ex}");
            error = "The project could not be opened. The log has the details.";
            return null;
        }
    }

    private static bool IsFromANewerVersion(string path)
    {
        try
        {
            JsonObject? root = AtomicJsonFile.ReadObject(path);
            if (root?["schema_version"] is not JsonValue value) return false;
            long schema = value.TryGetValue(out long l) ? l : value.TryGetValue(out double d) ? (long)d : 0;
            return schema > ProjectDocument.SchemaVersion;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
    }

    public static string NormalizeExtension(string path)
    {
        if (path.EndsWith(ProjectDocument.FileExtension, StringComparison.OrdinalIgnoreCase)) return path;
        return path + ProjectDocument.FileExtension;
    }

    private static void TryBackup(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            if (new FileInfo(path).Length == 0) return;

            File.Copy(path, path + BackupSuffix, overwrite: true);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }
}
