using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App.Infrastructure;

public static class LiveMaskCache
{
    private static ProjectMask? _mask;
    private static string? _profileAtRead;
    private static volatile bool _loaded;
    private static int _refreshQueued;
    private static FileSystemWatcher? _watcher;
    private static readonly object StartGate = new();

    public static ProjectMask? Current
    {
        get
        {
            EnsureStarted();
            string? active = SafeActiveProfile();
            if (!_loaded || !string.Equals(active, Volatile.Read(ref _profileAtRead), StringComparison.Ordinal))
            {
                RequestRefresh();
            }
            return Volatile.Read(ref _mask);
        }
    }

    public static ProjectMask? ReadNow()
    {
        EnsureStarted();
        string? active = SafeActiveProfile();
        ProjectMask? mask = MaskOverlayManager.ReadLiveMask();
        Publish(mask, active);
        return mask;
    }

    public static void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Task.Run(RefreshCore);
    }

    private static void RefreshCore()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        string? active = SafeActiveProfile();
        try
        {
            Publish(MaskOverlayManager.ReadLiveMask(), active);
        }
        catch (Exception ex)
        {
            FreeVideoStudio.Core.Abstractions.Faults.Degraded("PROJECT",
                "The HUD mask could not be read, so project saves may record the previous mask. " +
                "Editing and export still work.",
                ex);
        }
    }

    private static void Publish(ProjectMask? mask, string? profile)
    {
        Volatile.Write(ref _mask, mask);
        Volatile.Write(ref _profileAtRead, profile);
        _loaded = true;
    }

    private static string? SafeActiveProfile()
    {
        try { return SettingsManager.Instance.ActiveMaskOverlay; }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    private static void EnsureStarted()
    {
        if (_watcher != null) return;
        lock (StartGate)
        {
            if (_watcher != null) return;
            try
            {
                string file = ApplicationPaths.CreateDefault().CropCoordinatesFile;
                string? dir = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);

                var watcher = new FileSystemWatcher(dir, Path.GetFileName(file))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                FileSystemEventHandler changed = (_, _) => RequestRefresh();
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, _) => RequestRefresh();
                watcher.Error += (_, e) =>
                {
                    RuntimeLog.Fail("PROJECT", $"Mask file watcher overflowed/failed ({e.GetException().Message}); refreshing.");
                    RequestRefresh();
                };
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex)
            {
                FreeVideoStudio.Core.Abstractions.Faults.Degraded("PROJECT",
                    "HUD mask changes made outside this window may not be noticed until the next save. " +
                    "Editing, saving and export still work.",
                    ex);
                _watcher = new FileSystemWatcher();
            }
            RequestRefresh();
        }
    }
}
