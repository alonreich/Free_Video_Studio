using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// OUTNAME_01 — the user-editable automatic file name of every finished export.
///
/// <para>
/// Each sub-application owns ONE base name, edited in Settings › Output Files and stored in
/// <c>AppSettings.MainOutputBaseName</c> (the Video Merger adds "-merged", SETNAMEONE_01). A finished
/// render is saved as <c>&lt;base&gt;-&lt;N&gt;.mp4</c> where N is the first free index in the
/// destination folder (1, 2, 3 …). Defaults: <see cref="MainDefaultBaseName"/> →
/// <c>FreeVideoStudio-1.mp4</c>, <see cref="MergerDefaultBaseName"/> → <c>FreeVideoStudio-merged-1.mp4</c>.
/// SETNAMEONE_01: there is ONE name; the Video Merger adds <see cref="MergerSuffix"/> to it
/// (<see cref="MergerBaseFor"/>), so the two tools' files are told apart without a second setting.
/// </para>
///
/// <para>
/// ⚠️ The base name is USER INPUT that ends up in a file path. <see cref="Sanitize"/> is the only
/// gate: it strips path separators and every character Windows forbids in a file name, removes
/// trailing dots/spaces/dashes (Windows silently drops trailing dots and spaces, which would make
/// two different settings collide on disk), rejects reserved device names (CON, NUL, COM1 …) and
/// clamps the length. Anything that sanitizes to nothing falls back to the tool's default, so an
/// export can never fail — or escape its folder — because of this setting.
/// </para>
///
/// <para>
/// Rescued renders (RESCUE_01) deliberately do NOT follow the custom name: their fixed
/// <see cref="MainRecoveredPrefix"/> / <see cref="MergerRecoveredPrefix"/> are how the user and the
/// crash digest tell the two tools' preserved files apart in the shared temp root.
/// </para>
/// </summary>
public static class OutputFileNaming
{
    /// <summary>OUTNAME_01 — Main App default: FreeVideoStudio-1.mp4, FreeVideoStudio-2.mp4 …</summary>
    public const string MainDefaultBaseName = "FreeVideoStudio";

    /// <summary>SETNAMEONE_01 — what the Video Merger adds to the one file name.</summary>
    public const string MergerSuffix = "-merged";

    /// <summary>OUTNAME_01 / SETNAMEONE_01 — Video Merger default: FreeVideoStudio-merged-1.mp4 …</summary>
    public const string MergerDefaultBaseName = MainDefaultBaseName + MergerSuffix;

    /// <summary>
    /// SETNAMEONE_01 — the Video Merger's file-name base for the user's one name: the cleaned name plus
    /// <see cref="MergerSuffix"/> (kept within <see cref="MaxBaseNameLength"/>). "Clips" → "Clips-merged".
    /// </summary>
    public static string MergerBaseFor(string? name)
    {
        string main = Sanitize(name, MainDefaultBaseName);
        int room = MaxBaseNameLength - MergerSuffix.Length;
        if (main.Length > room) main = main[..room].TrimEnd('.', ' ', '-', '_');
        return main + MergerSuffix;
    }

    /// <summary>RESCUE_01 — fixed prefix of a rescued Main App render in the temp root.</summary>
    public const string MainRecoveredPrefix = "FreeVideoStudio-RECOVERED-";

    /// <summary>RESCUE_01 — fixed prefix of a rescued Video Merger render in the temp root.</summary>
    public const string MergerRecoveredPrefix = "Merged-Videos-RECOVERED-";

    /// <summary>Upper bound for the base name, leaving room for "-10000.mp4" inside MAX_PATH budgets.</summary>
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

    /// <summary>
    /// OUTNAME_01 — returns a base name that is always safe to use as a Windows file-name prefix.
    /// Never throws; returns <paramref name="fallback"/> when nothing usable is left.
    /// </summary>
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

    /// <summary>OUTNAME_01 — "&lt;base&gt;-&lt;index&gt;.mp4".</summary>
    public static string NumberedFileName(string baseName, int index) =>
        $"{baseName}-{index}.mp4";
}
