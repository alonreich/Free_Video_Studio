using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Layout;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// MVVM_03 — the cached control accessors for <see cref="TimelineLanesControl"/>.
///
/// <para>
/// ⚠️ IN ITS OWN FILE ON PURPOSE. MVVM_02 caps window code-behind and the cap may only ever
/// fall, so adding these to the code-behind — even while REMOVING lookups from it — would
/// have pushed several files past their ceiling and forced the ratchet up. A ratchet that
/// gets raised to accommodate an improvement stops being a ratchet.
/// </para>
///
/// <para>
/// These replace repeated <c>this.FindControl&lt;T&gt;("Name")</c> calls — some controls were
/// resolved by string fifteen times in one file. Each lookup walks the visual tree and can
/// return null, so a renamed control compiled cleanly and produced a dead button at run time
/// (QUALITY_04). Now a rename breaks in exactly one place.
/// </para>
///
/// <para>
/// This is a STEP toward MVVM_01, not the destination. The end state is a binding to a
/// view-model property; until then, one resolution per control means the eventual binding
/// replaces one accessor instead of hunting every call site.
/// </para>
/// </summary>
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
