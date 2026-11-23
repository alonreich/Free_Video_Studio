using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.App.Services;

public static class AiUsageReport
{
    public const string Tag = "AIUSAGEREPORT_01";

    public sealed record Entry(string Feature, string Vendor, string Model, long EstimatedTokens, long UsedTokens, long Requests, long ThinkingTokens = -1);

    public sealed record Row(string Feature, string Vendor, string Model, int Runs, long EstimatedTokens, long UsedTokens, long Requests = 0, long ThinkingTokens = -1)
    {
        public double Ratio => EstimatedTokens > 0 ? UsedTokens / (double)EstimatedTokens : 0;
        public long? ThinkingPerRequest => ThinkingTokens >= 0 && Requests > 0 ? ThinkingTokens / Requests : null;
    }

    private static readonly Regex ThinkingField = new(@" thinkingTokens=(?<t>\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Line = new(
        @"AI-Usage - task=\S+ vendor=(?<vendor>\S+) model=(?<model>\S+) estTokens=(?<est>-?\d+) usedTokens=(?<used>\d+) requests=(?<req>\d+) feature=""(?<feature>[^""]*)""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string FormatLogFields(string feature, AiVendor vendor, string? model, long? estimatedTokens, long usedTokens, long requests)
        => string.Create(CultureInfo.InvariantCulture,
            $"vendor={vendor} model={(string.IsNullOrWhiteSpace(model) ? "-" : model.Trim().Replace(' ', '_'))} estTokens={estimatedTokens ?? -1} usedTokens={usedTokens} requests={requests} feature=\"{feature.Replace('"', '\'')}\"");

    public static Entry? Parse(string logLine)
    {
        var m = Line.Match(logLine ?? string.Empty);
        if (!m.Success) return null;
        long N(string g) => long.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        var t = ThinkingField.Match(logLine!, m.Index + m.Length);
        long thinking = t.Success && t.Index == m.Index + m.Length ? long.Parse(t.Groups["t"].Value, CultureInfo.InvariantCulture) : -1;
        return new Entry(m.Groups["feature"].Value, m.Groups["vendor"].Value, m.Groups["model"].Value, N("est"), N("used"), N("req"), thinking);
    }

    public static IReadOnlyList<Row> Summarize(IEnumerable<string> logLines)
    {
        return logLines.Select(Parse)
            .Where(e => e != null && e.Requests > 0 && e.EstimatedTokens > 0)
            .GroupBy(e => (e!.Feature, e.Vendor, e.Model))
            .Select(g => new Row(g.Key.Feature, g.Key.Vendor, g.Key.Model, g.Count(), g.Sum(e => e!.EstimatedTokens), g.Sum(e => e!.UsedTokens),
                g.Where(e => e!.ThinkingTokens >= 0).Sum(e => e!.Requests),
                g.Any(e => e!.ThinkingTokens >= 0) ? g.Where(e => e!.ThinkingTokens >= 0).Sum(e => e!.ThinkingTokens) : -1))
            .OrderBy(r => r.Feature, StringComparer.Ordinal).ThenBy(r => r.Vendor, StringComparer.Ordinal).ThenBy(r => r.Model, StringComparer.Ordinal)
            .ToList();
    }

    public static string Render(IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Estimated vs used tokens (from AI-Usage log lines; only runs that sent something and had an estimate):");
        if (rows.Count == 0)
        {
            sb.AppendLine("  No matching runs found.");
            return sb.ToString();
        }
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {"Feature",-34} {"Company",-10} {"Model",-24} {"Runs",5} {"Estimated",12} {"Used",12} {"Used/Est",9} {"Think/req",10}"));
        foreach (var r in rows)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Trim(r.Feature, 34),-34} {Trim(r.Vendor, 10),-10} {Trim(r.Model, 24),-24} {r.Runs,5} {r.EstimatedTokens,12:N0} {r.UsedTokens,12:N0} {r.Ratio,9:0.00} {(r.ThinkingPerRequest is { } tpr ? tpr.ToString("N0", CultureInfo.InvariantCulture) : "-"),10}"));
        sb.AppendLine("  Used/Est above 1 means the estimate was too low; below 1, too high. Think/req is the real thinking per request (AITHINKCAL_01).");
        return sb.ToString();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
