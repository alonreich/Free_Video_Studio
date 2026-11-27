using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

public static class MemeManagementService
{
    public static string ResolveFfprobePath()
    {
        return BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
    }

    public static async Task<List<MemeItem>> ScanMemesAsync()
    {
        await MemeDirectory.WhenReadyAsync().ConfigureAwait(true);
        string memeFolder = MemeDirectory.EnsureActiveExists();
        try
        {
            return await MemeCatalog.ScanAsync(memeFolder, ResolveFfprobePath());
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Memes", $"Meme directory scan failed: {ex.Message}");
            return new List<MemeItem>();
        }
    }

    public static async Task<(int Count, string? Error)> DownloadAllMemesAsync(Window parent)
    {
        await MemeDirectory.WhenReadyAsync().ConfigureAwait(true);
        string memeDir = MemeDirectory.EnsureActiveExists();
        var result = await CloudSyncProgressWindow.RunAsync(
            parent, "Getting more memes",
            (progress, ct) => MemeCatalog.SyncAllMemesAsync(memeDir, progress, ct));
        if (result.Item1 > 0) MemeDirectory.NotifyChanged();
        return result;
    }

    public static string DownloadStatus(int count) => count == 0
        ? "You already have every meme in the library."
        : count == 1 ? "✓ Got 1 new meme." : $"✓ Got {count} new memes.";

    public static async Task<double> ProbeMusicDurationSecondsAsync(string ffmpegPath, string musicPath)
    {
        try
        {
            string ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
            if (!File.Exists(ffprobePath)) ffprobePath = ResolveFfprobePath();
            var prober = new FreeVideoStudio.Core.Media.MediaProber(ffprobePath, musicPath);
            return await prober.GetDurationAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Audio", $"Failed to probe music file '{musicPath}': {ex.Message}");
            return 0;
        }
    }
}
