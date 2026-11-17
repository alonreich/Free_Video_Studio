using System;
using System.Globalization;
using System.Text.Json;
using FreeVideoStudio.Core.Diagnostics;

namespace FreeVideoStudio.App.Services;

internal static class AiDiagnostics
{
    public const string TraceTag = "AITRACE_02";

    private static readonly string[] SettingNames =
    {
        "thinkingLevel", "thinkingBudget", "budget_tokens", "reasoning_effort", "detail", "fps", "mediaResolution",
        "temperature", "max_tokens", "maxOutputTokens", "tool_choice",
    };

    internal static string Tag => $"task={AiUsageMeter.CurrentTaskId} ";

    internal static string Start(string vendor, string model, byte[] body, string key)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = new System.Text.StringBuilder();
            var settings = new System.Text.StringBuilder();
            var media = new MediaCount();
            Collect(doc.RootElement, text, settings, media, null);
            Write(id, $"request feature=\"{AiUsageMeter.CurrentTaskName}\" vendor={vendor} model={model} bytes={body.Length} "
                + $"media={media.Items} mediaKB={(media.Base64Chars * 3 / 4 / 1024).ToString(CultureInfo.InvariantCulture)} "
                + $"settings=[{settings.ToString().Trim()}] prompt={text}", key);
        }
        catch (JsonException ex) { RuntimeLog.Swallowed(ex); }
        return id;
    }

    private sealed class MediaCount
    {
        public int Items;
        public long Base64Chars;
    }

    private static void Collect(JsonElement node, System.Text.StringBuilder text, System.Text.StringBuilder settings, MediaCount media, string? parentName)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                string name = property.Name;
                var value = property.Value;
                if (name == "text" && value.ValueKind == JsonValueKind.String)
                    text.AppendLine(value.GetString());
                else if (name == "data" && value.ValueKind == JsonValueKind.String && parentName is "inlineData" or "source")
                {
                    media.Items++;
                    media.Base64Chars += value.GetString()?.Length ?? 0;
                }
                else if ((name == "url" && value.ValueKind == JsonValueKind.String && parentName == "image_url")
                      || (name == "image_url" && value.ValueKind == JsonValueKind.String && value.GetString()?.StartsWith("data:", StringComparison.Ordinal) == true))
                {
                    media.Items++;
                    media.Base64Chars += value.GetString()?.Length ?? 0;
                }
                else if (Array.IndexOf(SettingNames, name) >= 0 && value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    settings.Append(name).Append('=').Append(value.ToString()).Append(' ');
                else if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    if (name == "tool_choice" && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var tc))
                        settings.Append("tool_choice=").Append(tc.ToString()).Append(' ');
                    else if (name is "input_schema" or "responseSchema" or "schema") continue;
                    Collect(value, text, settings, media, name);
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) Collect(item, text, settings, media, parentName);
    }

    internal static void End(string id, string outcome, TimeSpan elapsed, string key)
        => Write(id, $"ended {outcome} after {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s", key);

    internal static void Write(string id, string text, string key)
    {
        string safe = SecretRedactor.RedactSecrets(text, key);
        const int limit = 32768;
        if (safe.Length > limit) safe = safe[..limit] + " [truncated]";
        RuntimeLog.Info("AI-Diagnostic", $"task={AiUsageMeter.CurrentTaskId} request={id} {safe}");
    }
}
