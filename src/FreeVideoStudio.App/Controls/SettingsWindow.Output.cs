using System;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// OUTNAME_01 — Settings › Files &amp; Folders. ONE save folder for both tools (SETFOLDERONE_01: written to
/// <see cref="AppSettings.MainOutputDirectory"/> and <see cref="AppSettings.MergerOutputDirectory"/>,
/// ISSUE_04) and ONE automatic file name (SETNAMEONE_01: the Video Merger adds "-merged" to it). Also the music folder (SETMUSICDIR_01: read-only box +
/// Browse, so a half-typed path can never be saved) and the meme folder (MEMEPENDING_01: pending until
/// SAVE). Values are pending in <see cref="SettingsViewModel"/>; <c>SettingsViewModel.Commit</c>
/// writes them inside the single SETTX_01 transaction.
///
/// An empty folder means "the real Windows Downloads folder, resolved at export time"
/// (<see cref="OutputFolderResolver"/>). A folder is only accepted when it is writable now.
/// </summary>
public partial class SettingsWindow
{
    private void OnChangeSaveFolder(object? sender, RoutedEventArgs e) => _ = ChangeSaveFolderAsync();

    /// <summary>ASYNCUI_02 — Task-returning; <see cref="PickFolderAsync"/> never throws.</summary>
    private async System.Threading.Tasks.Task ChangeSaveFolderAsync()
    {
        string? picked = await PickFolderAsync(Vm.SaveFolder, "Choose where finished videos are saved", requireWritable: true);
        if (picked != null) Vm.SaveFolder = picked;
    }

    private void OnResetSaveFolder(object? sender, RoutedEventArgs e) { Vm.SaveFolder = ""; Vm.OutputFilesStatus = ""; }
    private void OnResetMainOutputBaseName(object? sender, RoutedEventArgs e) => Vm.MainOutputBaseName = OutputFileNaming.MainDefaultBaseName;

    private void OnBrowseMusicFolder(object? sender, RoutedEventArgs e) => _ = BrowseMusicFolderAsync();

    private async System.Threading.Tasks.Task BrowseMusicFolderAsync()
    {
        string? picked = await PickFolderAsync(
            string.IsNullOrWhiteSpace(Vm.MusicFolder) ? MemeDirectory.GetMusicRoot() : Vm.MusicFolder,
            "Choose the folder that holds your music", requireWritable: false);
        if (picked != null) Vm.MusicFolder = picked;
    }

    private void OnResetMusicFolder(object? sender, RoutedEventArgs e) => Vm.MusicFolder = "";

    private void OnChangeMemeFolder(object? sender, RoutedEventArgs e) => _ = ChangeMemeFolderAsync();

    private async System.Threading.Tasks.Task ChangeMemeFolderAsync()
    {
        string current = string.IsNullOrWhiteSpace(Vm.MemeDirectoryPath) ? MemeDirectory.GetActive() : Vm.MemeDirectoryPath;
        string? picked = await PickFolderAsync(current, "Choose your meme folder", requireWritable: false);
        if (picked == null) return;
        try
        {
            _ = Directory.GetFiles(picked);
            Vm.MemeDirectoryPath = picked;
            Vm.OutputFilesStatus = "";
        }
        catch (Exception ex)
        {
            Vm.OutputFilesStatus = ex is UnauthorizedAccessException
                ? "⚠ That folder can't be read (access denied). Keeping the previous meme folder."
                : "⚠ That folder can't be used. Keeping the previous meme folder.";
            RuntimeLog.Fail("Memes", $"Meme folder rejected in Settings: {ex.GetType().Name}");
        }
    }

    private void OnResetMemeFolder(object? sender, RoutedEventArgs e) => Vm.MemeDirectoryPath = "";

    /// <summary>MEMEPURE_01 / SHELLLAUNCH_01 — the default folder is created only here, where it is about to be opened.</summary>
    private void OnOpenMemeFolder(object? sender, RoutedEventArgs e)
    {
        string dir = string.IsNullOrWhiteSpace(Vm.MemeDirectoryPath) ? MemeDirectory.EnsureActiveExists() : Vm.MemeDirectoryPath;
        if (!Services.ShellLauncher.TryOpen(dir, "Memes"))
            Vm.OutputFilesStatus = "⚠ Windows could not open the meme folder.";
    }

    /// <summary>Returns a folder the user picked, or null (cancelled / unusable — the status line explains).</summary>
    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string current, string title, bool requireWritable)
    {
        try
        {
            IStorageFolder? start = null;
            foreach (string? candidate in new[] { current, KnownFolders.GetDownloads(), KnownFolders.GetVideos() })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
                start = await StorageProvider.TryGetFolderFromPathAsync(candidate);
                if (start != null) break;
            }

            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                SuggestedStartLocation = start,
                AllowMultiple = false
            });
            if (picked == null || picked.Count == 0) return null;

            string? path = picked[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || (requireWritable && !KnownFolders.IsWritableDirectory(path)))
            {
                Vm.OutputFilesStatus = "⚠ That folder cannot be written to. Pick a different folder.";
                RuntimeLog.Fail("Output", "Settings: selected folder is not usable.");
                return null;
            }

            Vm.OutputFilesStatus = "";
            return path;
        }
        catch (Exception ex)
        {
            Vm.OutputFilesStatus = "⚠ The folder picker failed. The previous folder is kept.";
            RuntimeLog.Fail("Output", $"Settings folder picker failed: {ex.Message}");
            return null;
        }
    }

}
