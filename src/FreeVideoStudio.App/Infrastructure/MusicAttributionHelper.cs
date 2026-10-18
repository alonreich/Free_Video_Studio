using System;
using System.Collections.Generic;
using System.IO;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// ATTRIBUTION_01 — resolves copyright and platform attribution credentials for background music tracks.
/// Reads companion .txt credentials (e.g. NCS licenses) or formats title credits for '!_' tracks
/// to prevent automatic muting / copyright blocks on YouTube and TikTok.
/// </summary>
public static class MusicAttributionHelper
{
    public static string? ResolveAttribution(IEnumerable<string>? musicPaths)
    {
        if (musicPaths == null) return null;
        var attributions = new List<string>();
        foreach (var path in musicPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var text = ResolveSingle(path);
            if (!string.IsNullOrWhiteSpace(text) && !attributions.Contains(text))
            {
                attributions.Add(text.Trim());
            }
        }
        return attributions.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, attributions) : null;
    }

    public static string? ResolveSingle(string musicPath)
    {
        if (string.IsNullOrWhiteSpace(musicPath)) return null;

        string txtPath = Path.ChangeExtension(musicPath, ".txt");
        if (File.Exists(txtPath))
        {
            try
            {
                string content = File.ReadAllText(txtPath).Trim();
                if (!string.IsNullOrEmpty(content)) return content;
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }

        string fileName = Path.GetFileNameWithoutExtension(musicPath);
        if (fileName.StartsWith("!_", StringComparison.OrdinalIgnoreCase))
        {
            string cleanTitle = fileName.Substring(2).Trim();
            return $"Track: {cleanTitle}{Environment.NewLine}Music provided by NoCopyrightSounds / Free Video Studio.{Environment.NewLine}Watch / Stream: Free background music";
        }

        return null;
    }
}
