using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FreeVideoStudio.App.Abstractions;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

public sealed class StorageProviderFilePicker : IFilePickerService
{
    private readonly IActiveWindowProvider _windows;
    private readonly IFaultSink _faults;

    public StorageProviderFilePicker(IActiveWindowProvider windows, IFaultSink faults)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
    }

    public Task<string?> SaveFileAsync(FilePickerRequest request) => RunAsync(request, save: true);

    public Task<string?> OpenFileAsync(FilePickerRequest request) => RunAsync(request, save: false);

    private async Task<string?> RunAsync(FilePickerRequest request, bool save)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window? owner = _windows.ActiveWindow;
            if (owner is null)
            {
                _faults.Degraded("PICKER",
                    "The file dialog could not open because no window is active. Nothing was saved — try again from the main window.");
                return null;
            }

            IStorageProvider storage = owner.StorageProvider;

            var fileType = new FilePickerFileType(request.ExtensionLabel)
            {
                Patterns = new[] { "*." + request.Extension.TrimStart('.') }
            };

            IStorageFolder? start = await TryResolveStartFolderAsync(storage, request.StartDirectoryKey);

            string? chosen;
            if (save)
            {
                IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = request.Title,
                    SuggestedFileName = request.SuggestedFileName,
                    DefaultExtension = request.Extension.TrimStart('.'),
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[] { fileType },
                    SuggestedStartLocation = start
                });
                chosen = file?.TryGetLocalPath();
            }
            else
            {
                IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = request.Title,
                    AllowMultiple = false,
                    FileTypeFilter = new[] { fileType },
                    SuggestedStartLocation = start
                });
                chosen = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            }

            if (chosen is not null) RememberDirectory(request.StartDirectoryKey, chosen);

            return chosen;
        });
    }

    private async Task<IStorageFolder?> TryResolveStartFolderAsync(IStorageProvider storage, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        string remembered = UiStateStore.ReadText(key!);
        if (string.IsNullOrWhiteSpace(remembered) || !Directory.Exists(remembered)) return null;

        try
        {
            return await storage.TryGetFolderFromPathAsync(new Uri(remembered));
        }
        catch (Exception ex)
        {
            _faults.Recoverable("PICKER", $"Remembered folder '{remembered}' could not be resolved: {ex.Message}", ex);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    private void RememberDirectory(string? key, string chosenFilePath)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        _faults.Guard("PICKER",
            "Your chosen folder could not be remembered — the next dialog may open somewhere else. Everything else is unaffected.",
            () =>
            {
                string? dir = Path.GetDirectoryName(chosenFilePath);
                if (!string.IsNullOrWhiteSpace(dir)) UiStateStore.WriteText(key!, dir!);
            },
            FaultTier.Recoverable);
    }
}
