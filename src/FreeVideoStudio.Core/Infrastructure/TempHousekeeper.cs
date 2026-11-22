using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Infrastructure;

public static class TempHousekeeper
{
    public static readonly TimeSpan StartupScratchAge = TimeSpan.FromHours(6);
    public static readonly TimeSpan ShutdownScratchAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan JobFolderIdleAge = TimeSpan.FromHours(6);
    public static readonly TimeSpan FreezeCacheAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan HistoryAge = TimeSpan.FromDays(90);
    public static readonly TimeSpan StartupInterval = TimeSpan.FromHours(12);
    public static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ShutdownBudget = TimeSpan.FromMilliseconds(1500);
    public const string StampFileName = "housekeeping.stamp";

    private static readonly Regex ScratchFile = new(
        @"^(cand_f[123]_[0-9a-f]{32}\.jpg|fvs_(thumb|wave|wave_sequence|music_frame|viral_frame|elim_frame|elim_pcm|pcm|captions_live|cudatag)_[^\\/]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MixFile = new(
        @"^fvs_(preview|wizard)_mix_(?<pid>\d+)_\d+\.wav$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex JobFolder = new(
        @"^(fvs_job_|fvs_merger_|ai_frames_)[0-9A-Za-z_\-]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record Report(int Files, int Folders, long Bytes, bool Skipped);

    public sealed record Roots(string TempRoot, string? FreezeCacheDirectory, string? HistoryDirectory, string StampDirectory);

    public static Roots RootsFor(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new Roots(
            paths.TempDirectory,
            paths.FreezeSoundCacheDirectory,
            Path.Combine(paths.ProgramDataRoot, "History"),
            Path.GetDirectoryName(paths.LaneCacheDirectory) ?? paths.TempDirectory);
    }

    public static Report RunAtStartup(ApplicationPaths paths) => Run(RootsFor(paths), DateTime.UtcNow, shutdown: false, IsProcessAlive);

    public static Report RunAtShutdown(ApplicationPaths paths) => Run(RootsFor(paths), DateTime.UtcNow, shutdown: true, IsProcessAlive);

    public static Report Run(Roots roots, DateTime nowUtc, bool shutdown, Func<int, bool> isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        var clock = Stopwatch.StartNew();
        TimeSpan budget = shutdown ? ShutdownBudget : StartupBudget;
        string stamp = Path.Combine(roots.StampDirectory, StampFileName);
        if (!shutdown && File.Exists(stamp) && nowUtc - File.GetLastWriteTimeUtc(stamp) is { } sinceLastRun
            && sinceLastRun >= TimeSpan.Zero && sinceLastRun < StartupInterval)
            return new Report(0, 0, 0, Skipped: true);

        int files = 0, folders = 0;
        long bytes = 0;
        bool OutOfTime() => clock.Elapsed > budget;
        int me = Environment.ProcessId;
        TimeSpan scratchAge = shutdown ? ShutdownScratchAge : StartupScratchAge;

        if (Directory.Exists(roots.TempRoot))
        {
            foreach (string path in SafeEnumerate(roots.TempRoot, directories: false))
            {
                if (OutOfTime()) break;
                string name = Path.GetFileName(path);
                bool remove;
                var mix = MixFile.Match(name);
                if (mix.Success && int.TryParse(mix.Groups["pid"].Value, out int pid))
                    remove = pid == me ? shutdown : !isProcessAlive(pid);
                else
                    remove = ScratchFile.IsMatch(name) && IsOlderThan(path, nowUtc, scratchAge);
                if (remove && TryDeleteFile(path, ref bytes)) files++;
            }
            if (!shutdown)
            {
                foreach (string dir in SafeEnumerate(roots.TempRoot, directories: true))
                {
                    if (OutOfTime()) break;
                    if (!JobFolder.IsMatch(Path.GetFileName(dir))) continue;
                    if (!TreeIdleLongerThan(dir, nowUtc, JobFolderIdleAge, out long treeBytes)) continue;
                    if (TryDeleteTree(dir)) { folders++; bytes += treeBytes; }
                }
            }
        }

        if (!shutdown && roots.FreezeCacheDirectory is { } freeze && Directory.Exists(freeze))
        {
            foreach (string path in SafeEnumerate(freeze, directories: false))
            {
                if (OutOfTime()) break;
                if (IsOlderThan(path, nowUtc, FreezeCacheAge) && TryDeleteFile(path, ref bytes)) files++;
            }
        }

        if (!shutdown && roots.HistoryDirectory is { } history && Directory.Exists(history))
        {
            foreach (string path in SafeEnumerate(history, directories: false))
            {
                if (OutOfTime()) break;
                if (!path.EndsWith(".history.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (HistoryIsStale(path, nowUtc) && TryDeleteFile(path, ref bytes)) files++;
            }
        }

        if (!shutdown)
        {
            try
            {
                Directory.CreateDirectory(roots.StampDirectory);
                File.WriteAllText(stamp, nowUtc.ToString("O"));
                File.SetLastWriteTimeUtc(stamp, nowUtc);
            }
            catch (Exception ex) { CoreLogger.Warn("TEMP CLEANUP", $"Could not write the cleanup stamp: {ex.Message}"); }
        }
        return new Report(files, folders, bytes, Skipped: false);
    }

    public static bool IsScratchFileName(string name) => ScratchFile.IsMatch(name) || MixFile.IsMatch(name);

    public static bool IsJobFolderName(string name) => JobFolder.IsMatch(name);

    internal static bool HistoryIsStale(string path, DateTime nowUtc)
    {
        try
        {
            if (IsOlderThan(path, nowUtc, HistoryAge)) return true;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            string? project = root?["project_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(project) || !Path.IsPathFullyQualified(project)) return false;
            string? folder = Path.GetDirectoryName(project);
            return folder != null && Directory.Exists(folder) && !File.Exists(project);
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("TEMP CLEANUP", $"Kept an unreadable history file: {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, bool directories)
    {
        string[] entries;
        try { entries = directories ? Directory.GetDirectories(root) : Directory.GetFiles(root); }
        catch (Exception ex)
        {
            CoreLogger.Warn("TEMP CLEANUP", $"Could not list {root}: {ex.Message}");
            yield break;
        }
        foreach (string entry in entries)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(entry); }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            yield return entry;
        }
    }

    private static bool IsOlderThan(string path, DateTime nowUtc, TimeSpan age)
    {
        try { return nowUtc - File.GetLastWriteTimeUtc(path) > age; }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
    }

    private static bool TreeIdleLongerThan(string dir, DateTime nowUtc, TimeSpan age, out long bytes)
    {
        bytes = 0;
        try
        {
            if (nowUtc - Directory.GetLastWriteTimeUtc(dir) <= age) return false;
            int seen = 0;
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (++seen > 20000) return false;
                if (nowUtc - info.LastWriteTimeUtc <= age) return false;
                if (info is FileInfo file) bytes += file.Length;
            }
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
    }

    private static bool TryDeleteFile(string path, ref long bytes)
    {
        try
        {
            long size = new FileInfo(path).Length;
            if (OperatingSystem.IsWindows()) SafeDirectoryCleanup.DeleteFile(path);
            else File.Delete(path);
            bytes += size;
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("TEMP CLEANUP", $"Left {Path.GetFileName(path)} in place: {ex.Message}");
            return false;
        }
    }

    private static bool TryDeleteTree(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) SafeDirectoryCleanup.DeleteTree(path);
            else Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("TEMP CLEANUP", $"Left {Path.GetFileName(path)} in place: {ex.Message}");
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException ex)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return true;
        }
    }
}
