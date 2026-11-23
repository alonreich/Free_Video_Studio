using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Layout;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    private ListBox? _cMusicListBox;
    private ListBox? MusicListBoxCtl => _cMusicListBox ??= this.FindControl<ListBox>("MusicListBox");
    private Slider? _cVideoVolSlider;
    private Slider? VideoVolSliderCtl => _cVideoVolSlider ??= this.FindControl<Slider>("VideoVolSlider");
    private Slider? _cMusicVolSlider;
    private Slider? MusicVolSliderCtl => _cMusicVolSlider ??= this.FindControl<Slider>("MusicVolSlider");
    private CheckBox? _cLoopMusicCheckBox;
    private CheckBox? LoopMusicCheckBoxCtl => _cLoopMusicCheckBox ??= this.FindControl<CheckBox>("LoopMusicCheckBox");
    private TextBlock? _cOffsetLabel;
    private TextBlock? OffsetLabelCtl => _cOffsetLabel ??= this.FindControl<TextBlock>("OffsetLabel");
    private ToggleSwitch? _cDuckingCheckBox;
    private ToggleSwitch? DuckingCheckBoxCtl => _cDuckingCheckBox ??= this.FindControl<ToggleSwitch>("DuckingCheckBox");
    private ToggleSwitch? _cCarvingCheckBox;
    private ToggleSwitch? CarvingCheckBoxCtl => _cCarvingCheckBox ??= this.FindControl<ToggleSwitch>("CarvingCheckBox");
    private FreeVideoStudio.App.Controls.TimelineLanesControl? _cPhase3Lanes;
    private FreeVideoStudio.App.Controls.TimelineLanesControl? Phase3LanesCtl => _cPhase3Lanes ??= this.FindControl<FreeVideoStudio.App.Controls.TimelineLanesControl>("Phase3Lanes");
    private Avalonia.Controls.Border? _cVideoHostBorder;
    private Avalonia.Controls.Border? VideoHostBorderCtl => _cVideoHostBorder ??= this.FindControl<Avalonia.Controls.Border>("VideoHostBorder");
    private Button? _cPlayBtn;
    private Button? PlayBtnCtl => _cPlayBtn ??= this.FindControl<Button>("PlayBtn");
    private Button? _cNextBtn;
    private Button? NextBtnCtl => _cNextBtn ??= this.FindControl<Button>("NextBtn");
}
