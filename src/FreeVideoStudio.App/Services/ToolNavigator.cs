using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App.Services;

public sealed class ToolNavigator
{
    private readonly IFaultSink _faults;

    private bool _navigating;

    public ToolNavigator(IFaultSink faults)
    {
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
    }

    public enum Tool
    {
        CropTool,
        VideoMerger,
    }

    public bool IsToolOpen { get; private set; }

    public static bool OpenedInProcess { get; private set; }

    private static ProjectMerge? _mergeQueue;

    public static void PublishMergeEdl(FreeVideoStudio.Core.Media.MergeEdl edl)
    {
        if (edl.Clips.Count == 0)
        {
            _mergeQueue = null;
            return;
        }
        var clips = new List<MergeClip>(edl.Clips.Count);
        foreach (var c in edl.Clips) clips.Add(new MergeClip(c.Path, c.InUs / 1_000_000.0, c.OutUs / 1_000_000.0));
        _mergeQueue = new ProjectMerge { Clips = clips, BaseSpeed = edl.BaseSpeed, Edl = edl };
    }

    public static ProjectMerge? ReadMergeQueue() => _mergeQueue;

    public static void RestoreMergeQueue(ProjectMerge? merge)
        => _mergeQueue = merge is { HasClips: true } ? merge : null;

    public async Task<bool> OpenAsync(
        Tool tool,
        Window owner,
        ToolLaunchContext context,
        Func<System.Threading.CancellationToken, Task<PreviewShutdownResult>> shutdownVideoPipeline,
        Action? restoreVideoPipeline = null,
        Action<ToolNavigationResult>? onToolReturned = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shutdownVideoPipeline);

        if (_navigating || IsToolOpen) return false;
        _navigating = true;

        try
        {
            string toolName = tool == Tool.CropTool ? "Crop Tools" : "Video Merger";

            var shutdown = await shutdownVideoPipeline(System.Threading.CancellationToken.None);
            if (!shutdown.Succeeded)
            {
                _faults.Fatal("UI",
                    $"{toolName} was not opened: the current video preview could not be shut down safely. " +
                    "Your work is unchanged — restart the app to restore the preview, then try again.",
                    technicalDetail: shutdown.Reason);
                RuntimeLog.Fail("UI", $"Tool navigation cancelled: preview teardown failed ({shutdown.Reason}).");
                return false;
            }

            Window toolWindow;
            try
            {
                toolWindow = tool == Tool.CropTool
                    ? new CropToolWindow(context as CropToolLaunchContext)
                    : new VideoMergerWindow(context as MergerLaunchContext);
            }
            catch (Exception ex)
            {
                _faults.Fatal("UI", $"{toolName} could not be opened, so nothing has changed.", ex);
                restoreVideoPipeline?.Invoke();
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
                return false;
            }

            IsToolOpen = true;
            OpenedInProcess = true;
            RuntimeLog.Info("UI", $"Opening {toolName} in-process; main window hidden (TOOLNAV_01).");

            toolWindow.Closed += (_, _) =>
            {
                IsToolOpen = false;
                OpenedInProcess = false;

                var result = (toolWindow as IToolNavigationResultSource)?.NavigationResult
                    ?? ToolNavigationResult.Cancelled(toolName);
                onToolReturned?.Invoke(result);

                _faults.Guard("UI",
                    "The editor could not be brought back automatically. It is still running — " +
                    "select it from the taskbar.",
                    () =>
                    {
                        owner.Show();
                        owner.Activate();
                        RestoreMainWindowReference(owner);
                        restoreVideoPipeline?.Invoke();
                    });

                RuntimeLog.Info("UI", $"{toolName} closed; main window restored.");
            };

            PointLifetimeAt(toolWindow);

            toolWindow.Show();
            owner.Hide();
            return true;
        }
        finally
        {
            _navigating = false;
        }
    }

    private void PointLifetimeAt(Window window)
        => _faults.Guard("UI", string.Empty, () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = window;
        }, FaultTier.Recoverable);

    private void RestoreMainWindowReference(Window owner)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = owner;
    }
}
