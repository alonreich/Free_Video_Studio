using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.Core.Undo;

public sealed class UndoSidecarStore
{
    public const int SchemaVersion = 1;

    public const int MaxEntries = UndoStack<ProjectDocument>.DefaultMaxDepth;

    private const string FolderName = "History";

    private readonly string _folder;

    public UndoSidecarStore(string historyFolder)
    {
        _folder = historyFolder ?? throw new ArgumentNullException(nameof(historyFolder));
    }

    public static UndoSidecarStore CreateDefault(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new UndoSidecarStore(Path.Combine(paths.ProgramDataRoot, FolderName));
    }

    public string PathFor(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string normalised = Path.GetFullPath(projectPath).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Path.Combine(_folder, Convert.ToHexString(hash, 0, 12) + ".history.json");
    }

    public bool Save(string projectPath, string sourceFingerprint, UndoStack<ProjectDocument> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return Save(projectPath, sourceFingerprint, history.UndoEntries, history.RedoEntries);
    }

    public bool Save(
        string projectPath,
        string sourceFingerprint,
        IReadOnlyList<UndoEntry<ProjectDocument>> undoEntries,
        IReadOnlyList<UndoEntry<ProjectDocument>> redoEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(undoEntries);
        ArgumentNullException.ThrowIfNull(redoEntries);

        try
        {
            Directory.CreateDirectory(_folder);

            JsonObject root = new()
            {
                ["schema_version"] = SchemaVersion,
                ["project_path"] = Path.GetFullPath(projectPath),
                ["source_fingerprint"] = sourceFingerprint ?? string.Empty,
                ["saved_utc"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["undo"] = WriteEntries(undoEntries),
                ["redo"] = WriteEntries(redoEntries),
            };

            AtomicJsonFile.WriteObject(PathFor(projectPath), root);
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("UNDO", $"Could not write the history sidecar: {ex.Message}");
            return false;
        }
    }

    public UndoSidecar? Load(string projectPath, string expectedSourceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        try
        {
            string path = PathFor(projectPath);
            if (!File.Exists(path)) return null;

            JsonObject? root = AtomicJsonFile.ReadObject(path);
            if (root is null) return null;

            if (root["schema_version"] is not JsonValue sv || !sv.TryGetValue(out int schema) || schema != SchemaVersion)
            {
                CoreLogger.Debug("UNDO", "History sidecar is from a different schema; starting a fresh history.");
                return null;
            }

            string stored = root["source_fingerprint"]?.GetValue<string>() ?? string.Empty;
            if (!string.Equals(stored, expectedSourceFingerprint ?? string.Empty, StringComparison.Ordinal))
            {
                CoreLogger.Debug("UNDO",
                    "History sidecar does not match this project's current state; starting a fresh history.");
                return null;
            }

            IReadOnlyList<UndoEntry<ProjectDocument>> undo = ReadEntries(root["undo"] as JsonArray);
            IReadOnlyList<UndoEntry<ProjectDocument>> redo = ReadEntries(root["redo"] as JsonArray);

            if (undo.Count == 0 && redo.Count == 0) return null;

            return new UndoSidecar(undo, redo);
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("UNDO", $"Could not read the history sidecar: {ex.Message}");
            return null;
        }
    }

    public void Delete(string projectPath)
    {
        try
        {
            string path = PathFor(projectPath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("UNDO", $"Could not delete the history sidecar: {ex.Message}");
        }
    }

    private static JsonArray WriteEntries(IReadOnlyList<UndoEntry<ProjectDocument>> entries)
    {
        JsonArray array = new();

        int skip = Math.Max(0, entries.Count - MaxEntries);

        for (int i = skip; i < entries.Count; i++)
        {
            JsonNode entry = new JsonObject
            {
                ["label"] = entries[i].Label,
                ["at_ms"] = entries[i].AtUnixMs,
                ["state"] = ProjectSerializer.Write(entries[i].State),
            };
            array.Add(entry);
        }

        return array;
    }

    private static IReadOnlyList<UndoEntry<ProjectDocument>> ReadEntries(JsonArray? array)
    {
        List<UndoEntry<ProjectDocument>> entries = new();
        if (array is null) return entries;

        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject o) continue;
            if (o["state"] is not JsonObject stateObj) continue;

            ProjectDocument? doc = ProjectSerializer.Read(stateObj, out string? error);

            if (doc is null)
            {
                CoreLogger.Warn("UNDO", $"Skipping an unreadable history entry: {error}");
                continue;
            }

            long atMs = o["at_ms"] is JsonValue v && v.TryGetValue(out long parsed) ? parsed : 0;
            entries.Add(new UndoEntry<ProjectDocument>(doc, o["label"]?.GetValue<string>() ?? "edit", atMs));
        }

        return entries;
    }
}

public sealed record UndoSidecar(
    IReadOnlyList<UndoEntry<ProjectDocument>> Undo,
    IReadOnlyList<UndoEntry<ProjectDocument>> Redo);
