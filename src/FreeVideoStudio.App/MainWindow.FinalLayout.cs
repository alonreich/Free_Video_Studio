using System.Threading.Tasks;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private int _finalLayoutVersion;

    internal Task RefreshFinalLayoutPreviewAsync() => RefreshFinalLayoutPreviewCoreAsync();

    private async Task RefreshFinalLayoutPreviewCoreAsync()
    {
        int version = ++_finalLayoutVersion;
        var frame = PhoneFrameCtl;
        if (frame == null) return;
        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)MobileCheckboxCtl ?? PortraitModeCheckboxCtl;
        bool show = mobileCheckbox?.IsChecked == true && !string.IsNullOrEmpty(_loadedVideoPath);
        if (!show) { frame.SetFinalLayout(null); return; }
        bool teammates = TeammatesCheckboxCtl?.IsChecked == true;
        bool spectating = SpectatingCheckboxCtl?.IsChecked == true;
        var layers = await Infrastructure.FinalLayoutPreview.LoadAsync(teammates, spectating);
        if (version != _finalLayoutVersion) return;
        frame.SetFinalLayout(layers);
        RuntimeLog.Info("UI", $"{Infrastructure.FinalLayoutPreview.Tag}: preview shows {layers.Count} in-game overlay box(es) (teammates {(teammates ? "on" : "off")}, spectating {(spectating ? "on" : "off")}).");
    }
}
