using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public sealed record HudElementInfo(string Key, string Label, int Z, bool IsBuiltIn);

public static class HudElements
{
    public const string Tag = "HUDELEMENTS_01";
    public const string ElementsSection = "elements";
    public const string GameTypeKey = "game_type";
    public const string LabelKey = "label";
    public const string AnchorKey = "anchor";
    public const string AnchorLeft = "left";
    public const string AnchorRight = "right";
    public const string AnchorCenter = "center";

    private static readonly Dictionary<string, string> BuiltInLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["loot"] = "Loot Area",
        ["stats"] = "Mini Map + Stats",
        ["normal_hp"] = "Own Health Bar (HP)",
        ["team"] = "Teammates health Bars (HP)",
        ["spectating"] = "Spectating Eye",
    };

    public static bool IsBuiltIn(string key) => BuiltInLabels.ContainsKey(key);

    public static string? BuiltInLabel(string key) => BuiltInLabels.TryGetValue(key, out var label) ? label : null;

    public static string Prettify(string key)
    {
        string[] words = (key ?? string.Empty).Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    public static string? StoredLabel(JsonObject? config, string key)
    {
        if (Entry(config, key)?[LabelKey] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
            return s.Trim();
        return null;
    }

    public static string? StoredAnchor(JsonObject? config, string key)
    {
        if (Entry(config, key)?[AnchorKey] is JsonValue v && v.TryGetValue(out string? s))
        {
            string a = (s ?? string.Empty).Trim().ToLowerInvariant();
            if (a is AnchorLeft or AnchorRight or AnchorCenter) return a;
        }
        return null;
    }

    public static string LabelFor(JsonObject? config, string key)
        => StoredLabel(config, key) ?? BuiltInLabel(key) ?? Prettify(key);

    public static string AnchorFor(JsonObject? config, string key)
        => StoredAnchor(config, key) ?? HudConfig.GuessDriftType(key) ?? AnchorCenter;

    public static void WriteElement(JsonObject config, string key, string label, string? anchor)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config[ElementsSection] is not JsonObject section)
        {
            section = new JsonObject();
            config[ElementsSection] = section;
        }
        string a = (anchor ?? string.Empty).Trim().ToLowerInvariant();
        if (a is not (AnchorLeft or AnchorRight or AnchorCenter)) a = AnchorCenter;
        section[key] = new JsonObject { [LabelKey] = string.IsNullOrWhiteSpace(label) ? Prettify(key) : label.Trim(), [AnchorKey] = a };
    }

    public static void RemoveElement(JsonObject config, string key)
    {
        if (config?[ElementsSection] is JsonObject section) section.Remove(key);
    }

    public static IReadOnlyList<HudElementInfo> FromConfig(JsonObject? config)
    {
        var result = new List<HudElementInfo>();
        if (config?["crops_1080p"] is not JsonObject crops) return result;
        var zOrders = config["z_orders"] as JsonObject;
        foreach (var pair in crops)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || HudConfig.IsRetiredRole(pair.Key)) continue;
            if (pair.Value is not JsonArray a || a.Count < 4) continue;
            if (ReadInt(a[0]) <= 1 || ReadInt(a[1]) <= 1) continue;
            int z = zOrders?[pair.Key] is JsonNode zn ? ReadInt(zn) : 50;
            result.Add(new HudElementInfo(pair.Key, LabelFor(config, pair.Key), z, IsBuiltIn(pair.Key)));
        }
        return result
            .OrderByDescending(e => e.Z)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AiGameProfile? GameType(JsonObject? config)
    {
        if (config?[GameTypeKey] is JsonValue v && v.TryGetValue(out string? s)
            && Enum.TryParse(s?.Trim(), ignoreCase: true, out AiGameProfile p) && Enum.IsDefined(p))
            return p;
        return null;
    }

    public static void SetGameType(JsonObject config, AiGameProfile? type)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (type is { } t) config[GameTypeKey] = t.ToString();
        else config.Remove(GameTypeKey);
    }

    public static AiGameProfile? GameTypeFromName(string? profileName)
    {
        string g = (profileName ?? string.Empty).Trim().ToLowerInvariant();
        if (g.Length == 0) return null;
        if (g.Contains("fortnite")) return AiGameProfile.Fortnite;
        if (g.Contains("counter") || g.Contains("battlefield") || g.Contains("apex") || g.Contains("call of duty") || g.Contains("valorant"))
            return AiGameProfile.FirstPerson;
        if (g.Contains("drone") || g.Contains("no mask")) return AiGameProfile.Generic;
        return null;
    }

    public static AiGameProfile? ResolveGameType(JsonObject? config, string? profileName)
        => GameType(config) ?? GameTypeFromName(profileName);

    public static bool UsesFortniteRoles(JsonObject? config, string? profileName)
    {
        if (ResolveGameType(config, profileName) == AiGameProfile.Fortnite) return true;
        return FromConfig(config).Any(e => e.IsBuiltIn);
    }

    private static JsonObject? Entry(JsonObject? config, string key)
    {
        if (config?[ElementsSection] is not JsonObject section) return null;
        if (section.TryGetPropertyValue(key, out JsonNode? exact)) return exact as JsonObject;
        foreach (var pair in section)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value as JsonObject;
        return null;
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<int>(); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            if (double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                return (int)Math.Round(d);
            CoreLogger.Swallowed(ex);
            return 0;
        }
    }
}

public sealed class HudVisibility
{
    private readonly HashSet<string> _hidden;

    public HudVisibility(IEnumerable<string>? hidden)
    {
        _hidden = new HashSet<string>(hidden?.Where(k => !string.IsNullOrWhiteSpace(k)) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public static HudVisibility AllVisible { get; } = new(null);

    public IReadOnlyCollection<string> Hidden => _hidden;

    public bool IsVisible(string key) => !_hidden.Contains(key);

    public static HudVisibility FromLegacy(bool showTeammates, bool showSpectating)
    {
        var hidden = new List<string>(2);
        if (!showTeammates) hidden.Add("team");
        if (!showSpectating) hidden.Add("spectating");
        return new HudVisibility(hidden);
    }

    public override string ToString() => _hidden.Count == 0 ? "all on" : "hidden: " + string.Join(", ", _hidden.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
}
