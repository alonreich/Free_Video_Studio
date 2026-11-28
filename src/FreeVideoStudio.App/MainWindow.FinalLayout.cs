using System.Linq;
using System.Threading.Tasks;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private int _finalLayoutVersion;

    private Infrastructure.HudPreviewController? _hudPreview;

    private Infrastructure.HudPreviewController EnsureHudPreview(Controls.PhoneFrameMockup frame)
    {
        if (_hudPreview != null) return _hudPreview;
        _hudPreview = new Infrastructure.HudPreviewController(frame, () =>
            ActiveVideoHost?.IpcClient is { } ipc
                ? new Infrastructure.HudPlayerState(_loadedVideoPath, ipc.CurrentTime, ipc.Duration, ipc.IsPaused, ipc.VideoWidth, ipc.VideoHeight)
                : null);
        System.Action<Infrastructure.AppSettings> onCommit = committed => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshFinalLayoutPreviewAsync());
        Infrastructure.SettingsManager.Committed += onCommit;
        Closed += (_, _) =>
        {
            Infrastructure.SettingsManager.Committed -= onCommit;
            _hudPreview?.Dispose();
        };
        return _hudPreview;
    }

    internal Task RefreshFinalLayoutPreviewAsync() => RefreshFinalLayoutPreviewCoreAsync();

    private async Task RefreshFinalLayoutPreviewCoreAsync()
    {
        int version = ++_finalLayoutVersion;
        var frame = PhoneFrameCtl;
        if (frame == null) return;
        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)MobileCheckboxCtl ?? PortraitModeCheckboxCtl;
        bool show = mobileCheckbox?.IsChecked == true && !string.IsNullOrEmpty(_loadedVideoPath) && !IsPreviewDetached;
        if (!show || !Infrastructure.HudPreviewController.Enabled)
        {
            frame.SetFinalLayout(null);
            _hudPreview?.Configure(null, FreeVideoStudio.Core.Media.HudVisibility.AllVisible);
            if (!show) return;
            EnsureHudPreview(frame);
            return;
        }
        var visibility = _viewModel.HudVisibility;
        var snapshot = await Infrastructure.FinalLayoutPreview.LoadSnapshotAsync();
        if (version != _finalLayoutVersion) return;
        var layers = snapshot.Visible(visibility);
        var hidden = snapshot.AllLayers.Where(l => !visibility.IsVisible(l.Key)).ToList();
        frame.SetFinalLayout(layers, snapshot.Labels, hidden);
        frame.HighlightHudLayer(_hudHoverKey);
        EnsureHudPreview(frame).Configure(layers, visibility);
        RuntimeLog.Info("UI", $"{Infrastructure.FinalLayoutPreview.Tag}: preview shows {layers.Count} in-game overlay box(es) ({visibility}).");
    }
}
