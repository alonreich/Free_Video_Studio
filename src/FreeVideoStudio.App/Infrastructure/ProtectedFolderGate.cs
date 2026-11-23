using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

internal static class ProtectedFolderGate
{
    internal static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(7);

    internal const string MarkerFileName = "controlled_folder_denied.txt";

    private static readonly ConcurrentDictionary<string, bool> _answers = new(StringComparer.OrdinalIgnoreCase);
    private static int _noticePending;

    private static string MarkerPath => Path.Combine(ApplicationPaths.CreateDefault().PreferencesDirectory, MarkerFileName);

    public static bool CanWrite(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (!ControlledFolderAccess.MayBlock(folder)) return true;
        return _answers.GetOrAdd(Path.TrimEndingDirectorySeparator(folder), Decide);
    }

    public static bool CanWriteNow(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        return !ControlledFolderAccess.MayBlock(folder) || KnownFolders.IsWritableDirectory(folder);
    }

    private static bool Decide(string folder)
    {
        if (DenialIsFresh(ReadMarker(), Environment.ProcessPath, DateTime.UtcNow)) return false;

        if (KnownFolders.IsWritableDirectory(folder))
        {
            ForgetDenial();
            return true;
        }

        RuntimeLog.Info("Protected folders",
            "Windows 'Controlled folder access' refused a write into a protected folder; the app's own folders are used instead.");
        RuntimeLog.Debug("Protected folders", $"Refused folder: {folder}");
        WriteMarker();
        Interlocked.Exchange(ref _noticePending, 1);
        return false;
    }

    internal static bool DenialIsFresh(string? marker, string? processPath, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(marker) || string.IsNullOrWhiteSpace(processPath)) return false;
        string[] lines = marker.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2) return false;
        if (!DateTime.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when)) return false;
        if (!string.Equals(lines[1].Trim(), processPath, StringComparison.OrdinalIgnoreCase)) return false;
        TimeSpan age = utcNow - when.ToUniversalTime();
        return age >= TimeSpan.Zero && age < RecheckAfter;
    }

    public static void ForgetDenial()
    {
        try
        {
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private static string? ReadMarker()
    {
        try
        {
            return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    private static void WriteMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n" + (Environment.ProcessPath ?? string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private static readonly ConcurrentDictionary<string, bool> _ffmpegAnswers = new(StringComparer.OrdinalIgnoreCase);

    public static bool FfmpegCanWrite(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (!ControlledFolderAccess.MayBlock(folder)) return true;
        return _ffmpegAnswers.GetOrAdd(Path.TrimEndingDirectorySeparator(folder), ProbeWithFfmpeg);
    }

    private static bool ProbeWithFfmpeg(string folder)
    {
        string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        if (!File.Exists(ffmpeg)) return true;
        string probe = Path.Combine(folder, ".fvs-write-check-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", "0.01", "-y", probe })
                start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return true;
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { RuntimeLog.Swallowed(ex); }
                return true;
            }
            bool written = File.Exists(probe);
            if (!written)
            {
                RuntimeLog.Info("Protected folders",
                    "Windows 'Controlled folder access' refused FFmpeg a write into the export folder; Downloads is used instead.");
                Interlocked.Exchange(ref _noticePending, 1);
            }
            return written;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            RuntimeLog.Swallowed(ex);
            return true;
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Swallowed(ex); }
        }
    }

    public static bool TakePendingNotice() => Interlocked.Exchange(ref _noticePending, 0) == 1;

    public static async Task ShowNoticeAsync(Window owner)
    {
        try
        {
            string program = Environment.ProcessPath ?? "FreeVideoStudio.exe";
            string encoder = Path.Combine(Path.GetDirectoryName(program) ?? AppContext.BaseDirectory, "backend", "ffmpeg.exe");

            var dialog = new Controls.ConfirmDialogWindow();
            dialog.SetTitle("Windows is protecting your folders");
            dialog.SetMessage(
                "Windows Security's \"Controlled folder access\" is on. It stops programs it does not know yet from saving " +
                "into Videos, Music, Documents, Pictures and Desktop, and Free Video Studio is still new to it." +
                Environment.NewLine + Environment.NewLine +
                "Nothing is lost. Until you allow it, new memes and music are kept in the app's own folder and finished " +
                "videos are saved to Downloads." +
                Environment.NewLine + Environment.NewLine +
                "To use those folders, open Windows Security, choose \"Allow an app through Controlled folder access\" " +
                "and add these two programs:" +
                Environment.NewLine + program +
                Environment.NewLine + encoder +
                Environment.NewLine + Environment.NewLine +
                "Then start Free Video Studio again.");
            dialog.SetButtonText("OPEN WINDOWS SECURITY", "NOT NOW");
            await dialog.ShowDialog(owner);
            if (!dialog.Result) return;

            ForgetDenial();
            Services.ShellLauncher.TryOpen("windowsdefender://ransomwareprotection", "Protected folders");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Protected folders", $"The Controlled folder access notice could not be shown: {ex.Message}");
        }
    }
}
