using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// CFASAFE_01 — decides whether the app may write into a folder that Windows Security's
/// "Controlled folder access" protects (Videos, Music, Documents, Pictures, Desktop), and remembers
/// a refusal so the app does not knock on that door at every start.
///
/// <para>
/// Each refused write makes Windows show the user a "blocked" notification. The app used to try the
/// same writes at every launch (starter memes into Videos, starter songs into Music, the personal
/// Desktop icon), so a protected PC showed that notification on every start. Now:
/// <list type="bullet">
/// <item>protection off, or the folder is not a protected one → yes, nothing is tried or stored;</item>
/// <item>otherwise ONE real write is tried. Allowed → yes. Refused → no, and the refusal is written
/// to <c>controlled_folder_denied.txt</c> in the preferences folder;</item>
/// <item>while that record is younger than <see cref="RecheckAfter"/> and names this program, the
/// answer stays no WITHOUT another attempt (no notification). It is forgotten when the user opens
/// Windows Security from the app's notice, so the next start checks again.</item>
/// </list>
/// Callers then use a folder of the app's own instead (<see cref="MemeDirectory"/>) or Downloads
/// (<see cref="OutputFolderResolver"/>). The app never changes a Windows Security setting.
/// </para>
/// Spec: docs/05_SYSTEM_LIFECYCLE_STORAGE.md #SYS-CFASAFE.
/// </summary>
internal static class ProtectedFolderGate
{
    /// <summary>How long a remembered refusal is believed before one new attempt is made.</summary>
    internal static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(7);

    internal const string MarkerFileName = "controlled_folder_denied.txt";

    private static readonly ConcurrentDictionary<string, bool> _answers = new(StringComparer.OrdinalIgnoreCase);
    private static int _noticePending;

    private static string MarkerPath => Path.Combine(ApplicationPaths.CreateDefault().PreferencesDirectory, MarkerFileName);

    /// <summary>True when the app may write into <paramref name="folder"/>. One answer per folder and process.</summary>
    public static bool CanWrite(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (!ControlledFolderAccess.MayBlock(folder)) return true;
        return _answers.GetOrAdd(Path.TrimEndingDirectorySeparator(folder), Decide);
    }

    /// <summary>
    /// The same question for installer code that runs as another program (the update worker): one
    /// real attempt when the folder is protected, nothing remembered, no notice.
    /// </summary>
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

    /// <summary>
    /// Pure (unit-tested): a marker is believed when it names this program and is younger than
    /// <see cref="RecheckAfter"/> (and not from the future). Line 1 = UTC time, line 2 = program path.
    /// </summary>
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

    /// <summary>Forgets a remembered refusal: the next start makes one fresh attempt.</summary>
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

    /// <summary>
    /// CFAFFMPEG_01 — the exported video is written by FFmpeg, a SECOND program, and Windows' allowed
    /// list is per program. A user who allowed only Free Video Studio got an export that failed at the
    /// very end. Before a render into a protected folder, FFmpeg itself writes (and the app deletes) a
    /// tiny silent probe file there, once per folder and run; refused → the caller uses Downloads and
    /// shows the notice that names both programs. Nothing is tried when protection is off or the folder
    /// is not protected.
    /// </summary>
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

    /// <summary>True once per process after a NEW refusal was found: the caller shows the notice.</summary>
    public static bool TakePendingNotice() => Interlocked.Exchange(ref _noticePending, 0) == 1;

    /// <summary>
    /// Explains the refusal once and offers to open Windows Security on its "Ransomware protection"
    /// page. Opening it also forgets the remembered refusal, so the next start checks again.
    /// Never throws.
    /// </summary>
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
