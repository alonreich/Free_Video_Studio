using System;
using System.Text.Json;
using FreeVideoStudio.Core.Diagnostics;

namespace FreeVideoStudio.App.Services;

/// <summary>AIDIAGNOSTICS_03 — correlated, redacted text; media and credentials never enter the log.</summary>
internal static class AiDiagnostics
{
    internal static string Start(string vendor, string model, byte[] body, string key)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = new System.Text.StringBuilder();
            CollectText(doc.RootElement, text);
            Write(id, $"request vendor={vendor} model={model} bytes={body.Length} prompt={text}", key);
        }
        catch (JsonException ex) { RuntimeLog.Swallowed(ex); }
        return id;
    }

    private static void CollectText(JsonElement node, System.Text.StringBuilder text)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name == "text" && property.Value.ValueKind == JsonValueKind.String)
                    text.AppendLine(property.Value.GetString());
                else if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    CollectText(property.Value, text);
            }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) CollectText(item, text);
    }

    internal static void Write(string id, string text, string key)
    {
        string safe = SecretRedactor.RedactSecrets(text, key);
        const int limit = 32768;
        if (safe.Length > limit) safe = safe[..limit] + " [truncated]";
        RuntimeLog.Info("AI-Diagnostic", $"task={AiUsageMeter.CurrentTaskId} request={id} {safe}");
    }
}
