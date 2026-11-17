using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

public static class OutputFolderResolver
{
    public enum AppScope
    {
        Main,
        Merger
    }

    private static string GetConfigured(AppScope scope) => scope switch
    {
        AppScope.Merger => SettingsManager.Instance.MergerOutputDirectory,
        _ => SettingsManager.Instance.MainOutputDirectory
    };

    private static void SetConfigured(AppScope scope, string path)
    {
        bool committed = SettingsManager.Update(s => { s.MainOutputDirectory = path; s.MergerOutputDirectory = path; });

        if (!committed)
        {
            RuntimeLog.Fail("Output", $"{scope} output folder could not be saved as the new default; it applies to this run only.");
        }
        else
        {
            RuntimeLog.Info("Output", $"{scope} output folder set and saved as the new default.");
        }
        RuntimeLog.Debug("Output", $"{scope} output folder path: {path}");
    }

    public static async Task<string?> ResolveAsync(Window owner, AppScope scope)
    {
        string configured = GetConfigured(scope);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (ProtectedFolderGate.CanWrite(configured) && KnownFolders.IsWritableDirectory(configured) &&
                await Task.Run(() => ProtectedFolderGate.FfmpegCanWrite(configured)))
            {
                return configured;
            }

            RuntimeLog.Fail("Output",
                $"{scope} saved output folder is missing or not writable — falling back to Downloads.");
            RuntimeLog.Debug("Output", $"Unusable saved output folder: {configured}");
            if (ProtectedFolderGate.TakePendingNotice())
                await ProtectedFolderGate.ShowNoticeAsync(owner);
        }

        string? downloads = KnownFolders.GetDownloads();
        if (KnownFolders.IsWritableDirectory(downloads))
        {
            RuntimeLog.Info("Output", $"{scope} using the system Downloads folder.");
            return downloads;
        }

        RuntimeLog.Fail("Output",
            downloads == null
                ? "The Downloads folder could not be resolved on this machine."
                : "The Downloads folder was resolved but is not writable.");

        string? picked = await PromptForFolderAsync(owner, scope, downloads);
        if (picked == null)
        {
            RuntimeLog.Info("Output", $"{scope} export cancelled — the user did not choose an output folder.");
            return null;
        }

        SetConfigured(scope, picked);
        return picked;
    }

    private static async Task<string?> PromptForFolderAsync(Window owner, AppScope scope, string? attemptedDownloads)
    {
        string appLabel = scope == AppScope.Merger ? "merged video" : "exported video";

        var confirm = new Controls.ConfirmDialogWindow();
        confirm.SetTitle("Choose where to save");
        confirm.SetMessage(
            "Your Downloads folder could not be used, so the " + appLabel + " has nowhere to go." +
            Environment.NewLine + Environment.NewLine +
            (attemptedDownloads == null
                ? "Windows did not report a Downloads folder for this account."
                : "Tried: " + attemptedDownloads) +
            Environment.NewLine + Environment.NewLine +
            "Choose a folder now? It will be remembered as your default for this tool.");
        confirm.SetButtonText("CHOOSE FOLDER", "CANCEL");
        await confirm.ShowDialog(owner);

        if (!confirm.Result) return null;

        return await ShowFolderPickerAsync(owner, null, scope);
    }

    private static async Task<string?> ShowFolderPickerAsync(Window owner, string? startAt, AppScope scope)
    {
        try
        {
            IStorageFolder? start = null;

            foreach (string? candidate in new[] { startAt, KnownFolders.GetVideos(), KnownFolders.GetDownloads() })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
                try
                {
                    start = await owner.StorageProvider.TryGetFolderFromPathAsync(candidate);
                    if (start != null) break;
                }
                catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }

            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = scope == AppScope.Merger
                    ? "Choose where merged videos are saved"
                    : "Choose where exported videos are saved",
                AllowMultiple = false,
                SuggestedStartLocation = start
            });

            if (folders == null || folders.Count == 0) return null;

            string? path = folders[0].TryGetLocalPath();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Output", $"Folder picker failed: {ex.Message}");
            return null;
        }
    }
}
