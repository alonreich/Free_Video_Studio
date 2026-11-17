namespace FreeVideoStudio.App.Services;

public abstract record ToolLaunchContext
{
    protected ToolLaunchContext(string? initialVideoPath)
    {
        InitialVideoPath = initialVideoPath;
    }

    public string? InitialVideoPath { get; init; }
}

public sealed record CropToolLaunchContext : ToolLaunchContext
{
    public CropToolLaunchContext(string? initialVideoPath, double? trimStartMs, double? trimEndMs)
        : base(initialVideoPath)
    {
        TrimStartMs = trimStartMs;
        TrimEndMs = trimEndMs;
    }

    public double? TrimStartMs { get; init; }

    public double? TrimEndMs { get; init; }
}

public sealed record MergerLaunchContext : ToolLaunchContext
{
    public MergerLaunchContext() : base((string?)null)
    {
    }
}

public sealed record ToolNavigationResult(string ToolName, bool ReturnedToOwner, string? SelectedClipPath)
{
    public static ToolNavigationResult Cancelled(string toolName)
        => new(toolName, ReturnedToOwner: false, SelectedClipPath: null);
}

public interface IToolNavigationResultSource
{
    ToolNavigationResult? NavigationResult { get; }
}