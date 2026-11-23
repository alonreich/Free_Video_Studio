using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Diagnostics;

public sealed class DiagnosticReport
{
    private readonly List<(string Section, string Body)> _sections = new();

    public DiagnosticReport Add(string section, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        _sections.Add((section, body ?? string.Empty));
        return this;
    }

    public DiagnosticReport AddMachineProfile(string appVersion, string? detectedEncoder)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"app version      : {appVersion}");
        sb.AppendLine($"os               : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"64-bit os        : {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"cpu cores        : {Environment.ProcessorCount}");
        sb.AppendLine($"clr              : {Environment.Version}");
        sb.AppendLine($"culture          : {CultureInfo.CurrentCulture.Name}");

        sb.AppendLine($"encoder chosen   : {detectedEncoder ?? "(not probed yet)"}");

        try
        {
            sb.AppendLine($"working set      : {Environment.WorkingSet / (1024 * 1024)} MB");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"working set      : unavailable ({ex.GetType().Name})");
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(ex);
        }

        return Add("MACHINE", sb.ToString());
    }

    public DiagnosticReport AddLogTail(string logPath, int maxLines = 400)
    {
        string name = Path.GetFileName(logPath);

        try
        {
            if (!File.Exists(logPath)) return Add($"LOG {name}", "(no log file)");

            var ring = new string[maxLines];
            int count = 0;

            using (var reader = new StreamReader(new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    ring[count % maxLines] = line;
                    count++;
                }
            }

            var sb = new StringBuilder();
            int start = count > maxLines ? count - maxLines : 0;
            if (start > 0) sb.AppendLine($"... {start} earlier line(s) omitted ...");
            for (int i = start; i < count; i++) sb.AppendLine(Redact(ring[i % maxLines]));

            return Add($"LOG {name}", sb.ToString());
        }
        catch (Exception ex)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(ex);
            return Add($"LOG {name}", $"(could not be read: {ex.GetType().Name} — {ex.Message})");
        }
    }

    public static string Redact(string? line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        string result = SecretRedactor.RedactSecrets(line);
        const string marker = @"\Users\";
        int i = result.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return result;

        var sb = new StringBuilder(result.Length);
        int from = 0;

        while (i >= 0)
        {
            int nameStart = i + marker.Length;
            int nameEnd = result.IndexOf('\\', nameStart);
            if (nameEnd < 0) nameEnd = result.Length;

            sb.Append(result, from, nameStart - from).Append("<user>");
            from = nameEnd;

            i = result.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);
        }

        sb.Append(result, from, result.Length - from);
        return sb.ToString();
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("FREE VIDEO STUDIO — DIAGNOSTIC REPORT");
        sb.AppendLine($"generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("This file is plain text and contains no personal information beyond what is");
        sb.AppendLine("shown below. Read it before sending it anywhere.");
        sb.AppendLine();

        foreach ((string section, string body) in _sections)
        {
            sb.AppendLine(new string('=', 94));
            sb.AppendLine(section);
            sb.AppendLine(new string('=', 94));
            sb.AppendLine(body.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public JsonObject ToJson()
    {
        JsonObject sections = new();
        foreach ((string section, string body) in _sections) sections[section] = body;

        return new JsonObject
        {
            ["generated_utc"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["sections"] = sections,
        };
    }
}
