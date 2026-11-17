using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FreeVideoStudio.Core.Media;

public static class OutputFileNaming
{
    public const string MainDefaultBaseName = "FreeVideoStudio";

    public const string MergerSuffix = "-merged";

    public const string MergerDefaultBaseName = MainDefaultBaseName + MergerSuffix;

    public static string MergerBaseFor(string? name)
    {
        string main = Sanitize(name, MainDefaultBaseName);
        int room = MaxBaseNameLength - MergerSuffix.Length;
        if (main.Length > room) main = main[..room].TrimEnd('.', ' ', '-', '_');
        return main + MergerSuffix;
    }

    public const string MainRecoveredPrefix = "FreeVideoStudio-RECOVERED-";

    public const string MergerRecoveredPrefix = "Merged-Videos-RECOVERED-";

    public const int MaxBaseNameLength = 80;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    private static readonly char[] ForbiddenCharacters =
        Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
            .Distinct()
            .ToArray();

    public static string Sanitize(string? baseName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return fallback;

        var sb = new StringBuilder(baseName.Length);
        foreach (char c in baseName.Trim())
        {
            if (char.IsControl(c)) continue;
            sb.Append(Array.IndexOf(ForbiddenCharacters, c) >= 0 ? '_' : c);
        }

        string cleaned = sb.ToString();
        if (cleaned.Length > MaxBaseNameLength) cleaned = cleaned[..MaxBaseNameLength];
        cleaned = cleaned.TrimEnd('.', ' ', '-', '_').TrimStart(' ', '.');

        if (cleaned.Length == 0 || cleaned.All(c => c == '_')) return fallback;

        string stem = cleaned.Split('.')[0].Trim();
        if (ReservedDeviceNames.Any(r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase))) return fallback;

        return cleaned;
    }

    public static string NumberedFileName(string baseName, int index) =>
        $"{baseName}-{index}.mp4";
}
