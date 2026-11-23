using System;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Project;

public static class RecoveryEnvelope
{
    public const string FormatId = "fvsrecovery";

    public const int CurrentFormatVersion = 1;

    private const string KeyFormat = "recovery_format";
    private const string KeyVersion = "recovery_format_version";
    private const string KeySavedAt = "saved_at_utc";
    private const string KeyProject = "project";
    private const string KeyTransient = "transient";

    public static JsonObject Write(ProjectDocument document, DateTimeOffset savedAtUtc, JsonObject? transientMetadata)
    {
        ArgumentNullException.ThrowIfNull(document);

        JsonObject root = new()
        {
            [KeyFormat] = FormatId,
            [KeyVersion] = CurrentFormatVersion,
            [KeySavedAt] = savedAtUtc.ToUnixTimeSeconds(),
            [KeyProject] = ProjectSerializer.Write(document),
        };

        if (transientMetadata is { Count: > 0 })
            root[KeyTransient] = transientMetadata.DeepClone();

        return root;
    }

    public static bool IsEnvelope(JsonObject root)
        => root[KeyFormat] is JsonValue v
        && v.TryGetValue(out string? s)
        && string.Equals(s, FormatId, StringComparison.Ordinal);

    public static bool TryRead(
        JsonObject root,
        out ProjectDocument? document,
        out JsonObject? transientMetadata,
        out string? error)
    {
        document = null;
        transientMetadata = null;
        error = null;

        if (!IsEnvelope(root))
        {
            error = "The recovery payload is not a canonical recovery envelope.";
            return false;
        }

        long version = ReadLong(root, KeyVersion, 0);
        if (version > CurrentFormatVersion)
        {
            error = $"This recovery file was written by a newer build (envelope version {version}, "
                  + $"this build writes {CurrentFormatVersion}). Update the app to restore it.";
            return false;
        }
        if (version < 1)
        {
            error = $"This recovery file uses a retired envelope format (version {version}).";
            return false;
        }

        if (root[KeyProject] is not JsonObject projectNode)
        {
            error = "The recovery envelope carried no project document.";
            return false;
        }

        document = ProjectSerializer.Read(projectNode, out error);
        if (document is null) return false;

        if (root[KeyTransient] is JsonObject t) transientMetadata = t;
        return true;
    }

    public static bool HasContent(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return !string.IsNullOrWhiteSpace(document.Source.FilePath);
    }

    private static long ReadLong(JsonObject o, string key, long fallback)
    {
        if (o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out string? s) && long.TryParse(s, out long parsed)) return parsed;
        return fallback;
    }
}
