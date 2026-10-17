// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
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
/// OUTNAME_01 — Settings › Files &amp; Folders. Per tool (Main App, Video Merger): the save folder
/// (<see cref="AppSettings.MainOutputDirectory"/> / <see cref="AppSettings.MergerOutputDirectory"/>,
/// ISSUE_04) and the automatic file-name base. Also the music folder (SETMUSICDIR_01: read-only box +
/// Browse, so a half-typed path can never be saved) and the meme folder (MEMEPENDING_01: pending until
/// SAVE). Values are pending in <see cref="SettingsViewModel"/>; <see cref="ApplyPendingOutputSettings"/>
/// writes them inside the single SETTX_01 transaction in <c>SaveAndClose</c>.
///
/// An empty folder means "the real Windows Downloads folder, resolved at export time"
/// (<see cref="OutputFolderResolver"/>). A folder is only accepted when it is writable now.
/// </summary>
public partial class SettingsWindow
{
    private readonly string _previousMergerOutputDir = SettingsManager.Instance.MergerOutputDirectory ?? "";

    private void OnChangeMainOutputFolder(object? sender, RoutedEventArgs e) =>
        _ = ChangeOutputFolderAsync(merger: false);

    private void OnChangeMergerOutputFolder(object? sender, RoutedEventArgs e) =>
        _ = ChangeOutputFolderAsync(merger: true);

    /// <summary>ASYNCUI_02 — Task-returning; <see cref="PickFolderAsync"/> never throws.</summary>
    private async System.Threading.Tasks.Task ChangeOutputFolderAsync(bool merger)
    {
        string? picked = await PickFolderAsync(
            merger ? Vm.MergerOutputDirectory : Vm.MainOutputDirectory,
            merger ? "Choose where the Video Merger saves merged videos" : "Choose where the Main App saves finished videos",
            requireWritable: true);
        if (picked == null) return;
        if (merger) Vm.MergerOutputDirectory = picked;
        else Vm.MainOutputDirectory = picked;
    }

    private void OnResetMainOutputFolder(object? sender, RoutedEventArgs e) { Vm.MainOutputDirectory = ""; Vm.OutputFilesStatus = ""; }
    private void OnResetMergerOutputFolder(object? sender, RoutedEventArgs e) { Vm.MergerOutputDirectory = ""; Vm.OutputFilesStatus = ""; }
    private void OnResetMainOutputBaseName(object? sender, RoutedEventArgs e) => Vm.MainOutputBaseName = OutputFileNaming.MainDefaultBaseName;
    private void OnResetMergerOutputBaseName(object? sender, RoutedEventArgs e) => Vm.MergerOutputBaseName = OutputFileNaming.MergerDefaultBaseName;

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
            _ = Directory.GetFiles(picked); // must be readable now (the old guard, kept)
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

    private void OnOpenMemeFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir = string.IsNullOrWhiteSpace(Vm.MemeDirectoryPath) ? MemeDirectory.GetActive() : Vm.MemeDirectoryPath;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { RuntimeLog.Fail("Memes", $"Open meme folder failed: {ex.Message}"); }
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

    /// <summary>OUTNAME_01 — applied inside the SaveAndClose settings transaction.</summary>
    private void ApplyPendingOutputSettings(AppSettings s)
    {
        SettingsViewModel.CopySection(Vm.P, s, SettingsSection.Files);
    }

    /// <summary>
    /// ISSUE_04 — the Video Merger also restores its folder from the IPC state store when it opens.
    /// Keep that copy in step after a committed change so the merger shows the folder chosen here.
    /// </summary>
    private void SyncMergerOutputDirectoryState()
    {
        if (string.Equals(_previousMergerOutputDir, Vm.MergerOutputDirectory, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _writer.Enqueue(new System.Text.Json.Nodes.JsonObject
            {
                ["MergerOutputDirectory"] = Vm.MergerOutputDirectory
            });
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
}
