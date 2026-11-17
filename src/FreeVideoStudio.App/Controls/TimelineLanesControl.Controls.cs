using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Layout;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App.Controls;

public partial class TimelineLanesControl
{
    private ScrollViewer? _cLanesScroll;
    private ScrollViewer? LanesScrollCtl => _cLanesScroll ??= this.FindControl<ScrollViewer>("LanesScroll");
    private Grid? _cLanesGrid;
    private Grid? LanesGridCtl => _cLanesGrid ??= this.FindControl<Grid>("LanesGrid");
    private Canvas? _cLaneASeekCanvas;
    private Canvas? LaneASeekCanvasCtl => _cLaneASeekCanvas ??= this.FindControl<Canvas>("LaneASeekCanvas");
    private Canvas? _cLaneBSeekCanvas;
    private Canvas? LaneBSeekCanvasCtl => _cLaneBSeekCanvas ??= this.FindControl<Canvas>("LaneBSeekCanvas");
    private Canvas? _cLaneCSeekCanvas;
    private Canvas? LaneCSeekCanvasCtl => _cLaneCSeekCanvas ??= ResolveControl<Canvas>("LaneCSeekCanvas");
    private Border? _cLaneCBorder;
    private Border? LaneCBorderCtl => _cLaneCBorder ??= ResolveControl<Border>("LaneCBorder");
    private Panel? _cLaneCHostPanel;
    private Panel? LaneCHostPanelCtl => _cLaneCHostPanel ??= ResolveControl<Panel>("LaneCHostPanel");

    private T? ResolveControl<T>(string name) where T : Control =>
        (this.FindNameScope()?.Find(name) as T)
        ?? this.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
}
