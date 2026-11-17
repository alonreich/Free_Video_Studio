using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.Core.Abstractions;

public interface IProjectStore
{
    ProjectIoResult Save(ProjectDocument document, string path);

    ProjectDocument? Load(string path, out string? error, out bool loadedFromBackup);

    string NormalizeExtension(string path);
}

public sealed class FileProjectStore : IProjectStore
{
    public static readonly FileProjectStore Instance = new();

    public ProjectIoResult Save(ProjectDocument document, string path)
        => ProjectStore.Save(document, path);

    public ProjectDocument? Load(string path, out string? error, out bool loadedFromBackup)
        => ProjectStore.Load(path, out error, out loadedFromBackup);

    public string NormalizeExtension(string path)
        => ProjectStore.NormalizeExtension(path);
}
