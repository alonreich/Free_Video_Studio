using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeVideoStudio.App.Infrastructure;

public static class MemeAssets
{
    private const string StarterFolderName = "starter";

    private static string MarkerPath(string category) =>
        Path.Combine(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot,
                     $"starter_{category}.delivered");

    public const string SongCategory = "mp3";

    public const string MemeCategoryFolder = "meme";

    private static readonly string[] LegacyMemeCategories = { "mp4", "jpeg" };

    public static readonly IReadOnlyDictionary<string, string[]> StarterFiles =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [SongCategory] = new[]
            {
                "!_Inspirational Music - The Travelling Symphony by SavfkMusic (No CopyRight).mp3",
                "!_Inspirational Music - The Travelling Symphony by SavfkMusic (No CopyRight).txt",
                "!_System Recall - Aftertune (No CopyRights).mp3",
                "!_System Recall - Aftertune (No CopyRights).txt",
            },
            [MemeCategoryFolder] = new[]
            {
                "Countdown - Landscape.mp4",
                "Countdown - Portrait.mp4",
                "Quick Flash - Portrait.mp4",
                "Sunset Gradient.png",
                "Test Card.jpg",
                "oopsie.png",
            },
        };

    private static readonly HashSet<string> PrependByDefault =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Countdown - Landscape.mp4",
            "Countdown - Portrait.mp4",
        };

    public static bool DefaultsToStart(string memePath) =>
        !string.IsNullOrWhiteSpace(memePath) &&
        PrependByDefault.Contains(Path.GetFileName(memePath));

    private static string StarterRoot()
    {
        string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        return Path.Combine(baseDir, StarterFolderName);
    }

    public static int DeliverStarter(string category, string destination)
    {
        int copied = 0;
        int failed = 0;
        try
        {
            string marker = MarkerPath(category);
            if (File.Exists(marker)) return 0;

            if (string.Equals(category, MemeCategoryFolder, StringComparison.OrdinalIgnoreCase) &&
                LegacyMemeCategories.Any(c => File.Exists(MarkerPath(c))))
            {
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                RuntimeLog.Info("Starter", "Meme starter set was already delivered by an older build; nothing copied.");
                return 0;
            }

            string src = Path.Combine(StarterRoot(), category);
            if (!Directory.Exists(src))
            {
                RuntimeLog.Info("Starter", $"No starter '{category}' folder shipped with this build — skipping.");
                return 0;
            }

            Directory.CreateDirectory(destination);

            string[] wanted = StarterFiles.TryGetValue(category, out var list) ? list : Array.Empty<string>();

            foreach (string name in wanted)
            {
                try
                {
                    string from = Path.Combine(src, name);
                    string to = Path.Combine(destination, name);
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) continue;
                    File.Copy(from, to);
                    copied++;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Info("Starter", $"Could not deliver '{name}': {ex.Message}");
                    failed++;
                }
            }

            if (failed > 0)
            {
                RuntimeLog.Info("Starter", $"{failed} starter '{category}' file(s) could not be copied to {destination}; delivery will be tried again.");
                return copied;
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            RuntimeLog.Info("Starter",
                copied > 0
                    ? $"Delivered {copied} starter file(s) to {destination}. This happens once — deleting them keeps them deleted."
                    : $"Starter '{category}' already present in {destination}; nothing copied.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Starter", $"Starter delivery for '{category}' skipped: {ex.Message}");
        }
        return copied;
    }
}
