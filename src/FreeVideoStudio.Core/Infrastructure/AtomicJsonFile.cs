using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Infrastructure;

public static class AtomicJsonFile
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true
    };

    public static JsonObject? ReadObject(string path)
    {
        try
        {
            return ReadObjectOrThrowOnAccess(path);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    public static JsonObject? ReadObjectOrThrowOnAccess(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);

            if (stream.Length == 0) return null;

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;

            JsonNode? node = JsonNode.Parse(json);
            var obj = node as JsonObject;

            if (obj != null)
            {
                if (!obj.ContainsKey("schema_version") && !obj.ContainsKey("Version"))
                {
                    obj["schema_version"] = 1;
                }
            }

            return obj;
        }
        catch (JsonException swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
            return null;
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    public static void WriteObject(string path, JsonObject payload)
        => WriteCore(path, stream =>
        {
            using Utf8JsonWriter writer = new(stream, WriterOptions);
            payload.WriteTo(writer);
            writer.Flush();
        });

    public static void WriteText(string path, string contents)
        => WriteCore(path, stream =>
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
        });

    public static void WriteBytes(string path, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        WriteCore(path, stream => stream.Write(data, 0, data.Length));
    }

    private static void WriteCore(string path, Action<FileStream> emit)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException($"Cannot resolve a parent directory for '{path}'.");
        }

        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough))
            {
                emit(stream);
                stream.Flush(flushToDisk: true);
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
        }
        catch (UnauthorizedAccessException swallowed3)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
        }
    }
}
