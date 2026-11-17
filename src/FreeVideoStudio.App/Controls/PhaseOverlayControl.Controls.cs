using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Layout;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App.Controls;

public partial class PhaseOverlayControl
{
    private TextBox? _cLiveLogTextBox;
    private TextBox? LiveLogTextBoxCtl => _cLiveLogTextBox ??= this.FindControl<TextBox>("LiveLogTextBox");
    private Canvas? _cFightCanvas;
    private Canvas? FightCanvasCtl => _cFightCanvas ??= this.FindControl<Canvas>("FightCanvas");
    private Canvas? _cFighterA;
    private Canvas? FighterACtl => _cFighterA ??= this.FindControl<Canvas>("FighterA");
    private Canvas? _cFighterB;
    private Canvas? FighterBCtl => _cFighterB ??= this.FindControl<Canvas>("FighterB");
    private ProgressBar? _cPhaseProgressBar;
    private ProgressBar? PhaseProgressBarCtl => _cPhaseProgressBar ??= this.FindControl<ProgressBar>("PhaseProgressBar");
    private Avalonia.Controls.ProgressBar? _cHypeMeterBar;
    private Avalonia.Controls.ProgressBar? HypeMeterBarCtl => _cHypeMeterBar ??= this.FindControl<Avalonia.Controls.ProgressBar>("HypeMeterBar");
    private Avalonia.Controls.Shapes.Ellipse? _cBulbGlass;
    private Avalonia.Controls.Shapes.Ellipse? BulbGlassCtl => _cBulbGlass ??= this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BulbGlass");
    private Canvas? _cDoor;
    private Canvas? DoorCtl => _cDoor ??= this.FindControl<Canvas>("Door");
    private Canvas? _cLogTangle;
    private Canvas? LogTangleCtl => _cLogTangle ??= this.FindControl<Canvas>("LogTangle");
}
