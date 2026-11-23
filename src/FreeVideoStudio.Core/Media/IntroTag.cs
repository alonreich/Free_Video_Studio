using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Media;

public static class IntroTag
{
    public const string Key = "fvs_intro_sec";

    public const double StandardIntroSec = 0.1;

    public const double MaxPlausibleIntroSec = 2.0;

    public const string MovFlags = "+faststart+use_metadata_tags";

    public static string[] OutputArgs(double introSec)
        => introSec > 0.0005
            ? ["-metadata", $"{Key}={Format(introSec)}", "-movflags", MovFlags]
            : ["-movflags", "+faststart"];

    public static string[] OutputArgs(ExportTiming timing)
    {
        var args = new System.Collections.Generic.List<string>(6);
        if (timing.IntroFrames > 0) args.AddRange(["-metadata", $"{Key}={Format(timing.IntroSec)}"]);
        args.AddRange(["-metadata", $"{ExportTimingTag.Key}={ExportTimingTag.Format(timing)}", "-movflags", MovFlags]);
        return args.ToArray();
    }

    public static string Format(double introSec) => introSec.ToString("F3", CultureInfo.InvariantCulture);

    public static double Read(JsonNode? formatTags, double fileDurationSec)
    {
        if (formatTags is not JsonObject tags) return 0;
        foreach (var kv in tags)
        {
            if (!string.Equals(kv.Key, Key, StringComparison.OrdinalIgnoreCase)) continue;
            return Validate(kv.Value?.ToString(), fileDurationSec);
        }
        return 0;
    }

    public static double Validate(string? raw, double fileDurationSec)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
        if (double.IsNaN(v) || v <= 0.0005 || v > MaxPlausibleIntroSec) return 0;
        if (fileDurationSec > 0 && v > fileDurationSec / 2.0) return 0;
        return v;
    }
}
