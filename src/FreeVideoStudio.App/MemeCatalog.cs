using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App;

public sealed class MemeItem
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsImage { get; init; }
    public int Width { get; set; }
    public int Height { get; set; }
    public float AspectRatio => Height > 0 ? (float)Width / Height : 0f;
    public bool IsDownloadAction { get; init; }

    public MemeCategory? DownloadCategory { get; init; }

    public override string ToString() => FileName;
}

public enum MemeCategory
{
    Video,
    Image,
}

public static class MemeCatalog
{
    private static readonly string[] VideoExts = { ".mp4", ".mkv", ".avi" };
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg" };

    private static readonly string[] SongSyncExts = { ".mp3", ".wav", ".m4a", ".ogg", ".flac", ".txt" };

    private const string CloudOwner = "alonreich";
    private const string CloudRepo = "Free_Video_Studio";

    public const string CloudMemeFolder = "meme";

    public static string[] ExtensionsFor(MemeCategory category) =>
        category == MemeCategory.Image ? ImageExts : VideoExts;

    public static string LabelFor(MemeCategory category) =>
        category == MemeCategory.Image ? "meme pictures" : "meme videos";

    private const string CloudSongFolder = "mp3";

    public readonly record struct SyncProgress(string FileName, int Completed, int Total);

    public static async Task<List<MemeItem>> ScanAsync(string directory, string ffprobePath)
    {
        long startTicks = Environment.TickCount64;

        List<ScanCandidate> candidates = await Task.Run(() => EnumerateCandidates(directory)).ConfigureAwait(false);
        if (candidates.Count == 0) return new List<MemeItem>();

        MemeDimensionCache cache = MemeDimensionCache.Load();

        var misses = new List<ScanCandidate>();
        foreach (ScanCandidate c in candidates)
        {
            if (cache.TryGet(c.FullPath, c.Length, c.MTimeTicks, out int cachedW, out int cachedH))
            {
                c.Width = cachedW;
                c.Height = cachedH;
            }
            else
            {
                misses.Add(c);
            }
        }

        if (misses.Count > 0)
        {
            int lanes = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

            await Parallel.ForEachAsync(
                misses,
                new ParallelOptions { MaxDegreeOfParallelism = lanes },
                async (c, ct) =>
                {
                    try
                    {
                        if (c.IsImage)
                        {
                            using var codec = SkiaSharp.SKCodec.Create(c.FullPath);
                            if (codec != null)
                            {
                                c.Width = codec.Info.Width;
                                c.Height = codec.Info.Height;
                            }
                        }
                        else
                        {
                            var (w, h) = await new FreeVideoStudio.Core.Media.MediaProber(ffprobePath, c.FullPath)
                                .GetResolutionAsync().ConfigureAwait(false);
                            c.Width = w;
                            c.Height = h;
                        }
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Info("Memes", $"Dimension probe failed for '{c.FileName}': {ex.Message}");
                    }
                }).ConfigureAwait(false);

            foreach (ScanCandidate c in misses)
            {
                cache.Put(c.FullPath, c.Length, c.MTimeTicks, c.Width, c.Height);
            }

            cache.Save();
        }

        var items = new List<MemeItem>(candidates.Count);
        int excluded = 0;

        foreach (ScanCandidate c in candidates)
        {
            if (!c.IsImage && (c.Width <= 0 || c.Height <= 0))
            {
                RuntimeLog.Fail("Memes", $"Excluding unreadable video meme '{c.FileName}' (failed to probe; would crash export).");
                excluded++;
                continue;
            }

            items.Add(new MemeItem
            {
                FileName = c.FileName,
                FullPath = c.FullPath,
                IsImage = c.IsImage,
                Width = c.Width,
                Height = c.Height
            });
        }

        RuntimeLog.Info("Memes",
            $"Meme scan: {candidates.Count} candidate(s), {candidates.Count - misses.Count} from cache, " +
            $"{misses.Count} probed, {excluded} excluded, {Environment.TickCount64 - startTicks} ms.");

        return items;
    }

    private sealed class ScanCandidate
    {
        public string FileName = "";
        public string FullPath = "";
        public bool IsImage;
        public long Length;
        public long MTimeTicks;
        public int Width;
        public int Height;
    }

    private static List<ScanCandidate> EnumerateCandidates(string directory)
    {
        var candidates = new List<ScanCandidate>();
        if (!Directory.Exists(directory)) return candidates;

        string[] files = Directory.GetFiles(directory);

        foreach (string f in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            bool isVideo = VideoExts.Contains(ext);
            bool isImage = ImageExts.Contains(ext);
            if (!isVideo && !isImage) continue;

            long length;
            long mtimeTicks;
            try
            {
                var fi = new FileInfo(f);
                length = fi.Length;
                mtimeTicks = fi.LastWriteTimeUtc.Ticks;
            }
            catch (System.Exception swallowed2)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
                continue;
            }

            if (length == 0) continue;

            candidates.Add(new ScanCandidate
            {
                FileName = Path.GetFileName(f),
                FullPath = f,
                IsImage = isImage,
                Length = length,
                MTimeTicks = mtimeTicks
            });
        }

        return candidates;
    }

    private static bool IsLfsPointer(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length is <= 0 or > 1024) return false;
            using var r = new StreamReader(path);
            char[] buf = new char[64];
            int n = r.Read(buf, 0, buf.Length);
            return n > 0 && new string(buf, 0, n).StartsWith("version https://git-lfs.github.com/spec", StringComparison.Ordinal);
        }
        catch (System.Exception swallowed3)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);
            return false;
        }
    }

    public static Task<(int downloaded, string? error)> SyncMemesAsync(
        string targetDirectory,
        MemeCategory category,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SyncFolderAsync(targetDirectory, CloudMemeFolder, ExtensionsFor(category),
                           category == MemeCategory.Image ? "Image memes" : "Video memes", progress, cancellationToken);

    public static Task<(int downloaded, string? error)> SyncSongsFromCloudAsync(
        string targetDirectory,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => SyncFolderAsync(targetDirectory, CloudSongFolder, SongSyncExts, "Songs", progress, cancellationToken);

    private static async Task<(int downloaded, string? error)> SyncFolderAsync(
        string targetDirectory,
        string cloudFolder,
        string[] acceptedExtensions,
        string logTag,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        int downloaded = 0;
        try
        {
            Directory.CreateDirectory(targetDirectory);
            var local = new HashSet<string>(
                Directory.GetFiles(targetDirectory).Select(f => Path.GetFileName(f)!),
                StringComparer.OrdinalIgnoreCase);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            cancellationToken.ThrowIfCancellationRequested();

            string listingUrl = $"https://api.github.com/repos/{CloudOwner}/{CloudRepo}/git/trees/HEAD:{cloudFolder}";
            using var listingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            listingCts.CancelAfter(TimeSpan.FromSeconds(30));

            JsonArray tree;
            using (var resp = await http.GetAsync(listingUrl, listingCts.Token))
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    (int)resp.StatusCode == 429)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync halted: GitHub API rate limit (HTTP {(int)resp.StatusCode}).");
                    return (0, "GitHub is rate-limiting this connection right now. Try again in an hour.");
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync: folder '{cloudFolder}' does not exist in the repository.");
                    return (0, "The online library could not be found. Update Free Video Studio to the latest version and try again.");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync: listing '{cloudFolder}' failed with HTTP {(int)resp.StatusCode}.");
                    return (0, $"The online library could not be listed (HTTP {(int)resp.StatusCode}). Try again later.");
                }

                JsonNode? root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
                if (root?["tree"] is not JsonArray arr)
                {
                    RuntimeLog.Fail(logTag, $"Cloud sync: listing '{cloudFolder}' had no tree.");
                    return (0, "The online library answered with something unexpected. Try again later.");
                }
                if (root["truncated"]?.GetValue<bool>() == true)
                    RuntimeLog.Fail(logTag, $"Cloud sync: listing '{cloudFolder}' was truncated by GitHub; only the listed files are fetched.");
                tree = arr;
            }

            var pending = new List<(string Name, long Size)>();
            foreach (var node in tree)
            {
                string? name = node?["path"]?.ToString();
                string? type = node?["type"]?.ToString();
                if (name == null || type != "blob") continue;
                if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) continue;
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

                string ext = Path.GetExtension(name).ToLowerInvariant();
                if (!acceptedExtensions.Contains(ext)) continue;

                string existing = Path.Combine(targetDirectory, name);
                if (local.Contains(name) && File.Exists(existing)
                    && new FileInfo(existing).Length > 0 && !IsLfsPointer(existing))
                    continue;

                long size = 0;
                try { size = node?["size"]?.GetValue<long>() ?? 0; }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                pending.Add((name, size));
            }

            int total = pending.Count;
            RuntimeLog.Info(logTag, $"Cloud sync: {total} file(s) to fetch from '{cloudFolder}'.");
            progress?.Report(new SyncProgress(string.Empty, 0, total));

            if (total == 0) return (0, null);

            int failures = 0;
            foreach ((string name, long size) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SyncProgress(name, downloaded, total));

                string dest = Path.Combine(targetDirectory, name);
                string tmp = dest + ".part";
                string escaped = Uri.EscapeDataString(name);
                string rawUrl = $"https://raw.githubusercontent.com/{CloudOwner}/{CloudRepo}/HEAD/{cloudFolder}/{escaped}";
                string lfsUrl = $"https://media.githubusercontent.com/media/{CloudOwner}/{CloudRepo}/HEAD/{cloudFolder}/{escaped}";

                bool lfsFirst = size > 0 && size <= LfsPointerMaxBytes;
                string[] urls = lfsFirst ? new[] { lfsUrl, rawUrl } : new[] { rawUrl, lfsUrl };

                try
                {
                    bool ok = false;
                    foreach (string url in urls)
                    {
                        if (await TryDownloadAsync(http, url, tmp, cancellationToken)) { ok = true; break; }
                    }

                    if (!ok)
                    {
                        try { File.Delete(tmp); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                        failures++;
                        RuntimeLog.Fail(logTag, $"Cloud sync: '{name}' could not be fetched from either host.");
                        continue;
                    }

                    File.Move(tmp, dest, overwrite: true);
                    local.Add(name);
                    downloaded++;
                    RuntimeLog.Info(logTag, $"Cloud sync: downloaded '{name}' ({new FileInfo(dest).Length} bytes).");
                    progress?.Report(new SyncProgress(name, downloaded, total));
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(tmp); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                    throw;
                }
                catch (Exception exFile)
                {
                    try { File.Delete(tmp); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                    failures++;
                    RuntimeLog.Fail(logTag, $"Cloud sync: '{name}' failed: {exFile.Message}");
                }
            }

            RuntimeLog.Info(logTag, $"Cloud sync complete: {downloaded} new file(s), {failures} failure(s).");

            if (downloaded == 0 && failures > 0)
            {
                return (0, $"All {failures} download(s) failed. Check your internet connection and try again.");
            }
            if (failures > 0)
            {
                return (downloaded, $"{downloaded} downloaded, but {failures} file(s) could not be fetched.");
            }
            return (downloaded, null);
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info(logTag, $"Cloud sync cancelled by the user after {downloaded} file(s).");
            return (downloaded, null);
        }
        catch (HttpRequestException ex)
        {
            RuntimeLog.Fail(logTag, $"Cloud sync network failure: {ex.Message}");
            return (downloaded, "Could not reach GitHub. Check your internet connection and try again.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(logTag, $"Cloud sync failed: {ex.Message}");
            return (downloaded, $"The download could not be completed: {ex.Message}");
        }
    }

    private const long LfsPointerMaxBytes = 1024;

    private static async Task<bool> TryDownloadAsync(HttpClient http, string url, string tmp, CancellationToken ct)
    {
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!resp.IsSuccessStatusCode) return false;
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write);
            await s.CopyToAsync(fs, ct);
        }
        return new FileInfo(tmp).Length > 0 && !IsLfsPointer(tmp);
    }
}
