namespace FreeVideoStudio.App.Abstractions;

public interface IFilePickerService
{
    Task<string?> SaveFileAsync(FilePickerRequest request);

    Task<string?> OpenFileAsync(FilePickerRequest request);
}

public sealed record FilePickerRequest(
    string Title,
    string? SuggestedFileName,
    string ExtensionLabel,
    string Extension,
    string? StartDirectoryKey = null);
