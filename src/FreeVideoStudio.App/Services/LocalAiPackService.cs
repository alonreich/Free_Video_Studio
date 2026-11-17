using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.LocalAi;

namespace FreeVideoStudio.App.Services;

internal readonly record struct LocalAiInstallProgress(string FileName, int FileIndex, int FileCount, long Done, long Total, string? Problem);

internal static class LocalAiPackService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim ModelGate = new(1, 1);
    private static OrtRuntime? s_runtime;
    private static Sam2BoxTracker? s_tracker;
    private static string? s_loadedFolder;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio-LocalAi/1.0");
        return http;
    }

    public static LocalAiPackStatus Status() => LocalAiPack.Resolve(ApplicationPaths.CreateDefault());

    public static bool IsReady => Status().IsReady;

    public static async Task<LocalAiPackStatus> InstallAsync(Action<LocalAiInstallProgress>? progress, CancellationToken ct)
    {
        if (!LocalAiPackManifest.IsPublished)
            return LocalAiPack.Check(LocalAiPack.InstallFolder(ApplicationPaths.CreateDefault()), Array.Empty<LocalAiPackFile>());

        if (s_runtime != null)
            throw new FreeVideoStudio.App.Infrastructure.UserFacingIOException("The Local AI pack is in use. Restart Free Video Studio, then install it again.");
        string folder = LocalAiPack.InstallFolder(ApplicationPaths.CreateDefault());
        string staging = folder + ".download";
        Directory.CreateDirectory(staging);
        var files = LocalAiPackManifest.Files;
        long total = LocalAiPackManifest.TotalBytes;
        long before = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            long offset = before;
            int index = i;
            await UpdateDownloader.DownloadAsync(Http, f.Url, null, Path.Combine(staging, f.Name), f.Length, f.Sha256,
                p => progress?.Invoke(new LocalAiInstallProgress(f.Name, index + 1, files.Count, offset + p.Done, total, p.Problem)),
                ct).ConfigureAwait(false);
            before += f.Length;
        }

        var check = LocalAiPack.Check(staging, files);
        if (!check.IsReady)
            throw new FreeVideoStudio.App.Infrastructure.UserFacingIOException("The downloaded Local AI files did not pass their check. Nothing was installed.");

        await ModelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UnloadModelLocked();
            string old = folder + ".old-" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bool hadOld = Directory.Exists(folder);
            if (hadOld) Directory.Move(folder, old);
            try
            {
                Directory.Move(staging, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RestorePreviousPack(folder, old, hadOld, ex);
                throw new FreeVideoStudio.App.Infrastructure.UserFacingIOException("The new Local AI files could not be put in place (another program may be using them). Your previous Local AI pack was kept. Try Install again in a minute.", ex);
            }
            TryDeleteFolder(old);
        }
        finally
        {
            ModelGate.Release();
        }
        RuntimeLog.Info("LocalAi", $"Local AI pack v{LocalAiPackManifest.PackVersion} installed ({files.Count} files, {total} bytes).");
        return Status();
    }

    public static async Task<bool> RemoveAsync()
    {
        await ModelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            UnloadModelLocked();
            string folder = LocalAiPack.InstallFolder(ApplicationPaths.CreateDefault());
            TryDeleteFolder(folder + ".download");
            TryDeleteFolder(folder);
            bool gone = !Directory.Exists(folder);
            RuntimeLog.Info("LocalAi", gone ? "Local AI pack removed by the user." : "Local AI pack removal waits for a restart (files in use).");
            return gone;
        }
        finally
        {
            ModelGate.Release();
        }
    }

    public static async Task<T> WithTrackerAsync<T>(Func<Sam2BoxTracker, Task<T>> work, Action<string>? log, CancellationToken ct)
    {
        var status = Status();
        if (!status.IsReady) throw new OrtException(status.Message);
        await ModelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (s_tracker == null || !string.Equals(s_loadedFolder, status.Folder, StringComparison.OrdinalIgnoreCase))
            {
                UnloadModelLocked();
                log?.Invoke("Loading the local tracking model…");
                s_runtime ??= OrtRuntime.Load(status.Folder);
                s_tracker = new Sam2BoxTracker(s_runtime, status.Folder, preferGpu: true);
                s_loadedFolder = status.Folder;
                log?.Invoke(s_tracker.OnGpu ? "Local tracking model ready on your graphics card." : "Local tracking model ready on the processor (no DirectX 12 graphics card found) — this is slower.");
            }
            try
            {
                return await work(s_tracker).ConfigureAwait(false);
            }
            catch (OrtException ex) when (!ct.IsCancellationRequested)
            {
                bool wasOnGpu = s_tracker?.OnGpu == true;
                UnloadModelLocked();
                if (!wasOnGpu || s_runtime == null) throw;
                RuntimeLog.Info("LocalAi", $"{OrtRuntime.CpuFallbackTag}: the graphics card failed while tracking ({ex.Message}); trying again on the processor.");
                log?.Invoke("The graphics card stopped while following the target — trying again on the processor (slower)…");
                s_tracker = new Sam2BoxTracker(s_runtime, status.Folder, preferGpu: false);
                s_loadedFolder = status.Folder;
                try
                {
                    return await work(s_tracker).ConfigureAwait(false);
                }
                catch (OrtException) when (!ct.IsCancellationRequested)
                {
                    UnloadModelLocked();
                    throw;
                }
            }
        }
        finally
        {
            ModelGate.Release();
        }
    }

    internal const string SwapTag = "LOCALAISWAP_01";

    internal static void RestorePreviousPack(string folder, string old, bool hadOld, Exception cause)
    {
        RuntimeLog.Warn("LocalAi", $"{SwapTag}: the new pack could not be moved in ({cause.GetType().Name}: {cause.Message}); putting the previous pack back.");
        if (!hadOld || !Directory.Exists(old)) return;
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.Move(old, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Fail("LocalAi", $"{SwapTag}: the previous pack could not be put back ({ex.GetType().Name}: {ex.Message}); it stays at {old}.");
        }
    }

    private static void UnloadModelLocked()
    {
        s_tracker?.Dispose();
        s_tracker = null;
        s_loadedFolder = null;
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
        }
    }
}
