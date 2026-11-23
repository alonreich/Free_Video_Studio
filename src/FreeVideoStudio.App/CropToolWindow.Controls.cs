using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Layout;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App;

public partial class CropToolWindow
{
    private Border? _cRolePopup;
    private Border? RolePopupCtl => _cRolePopup ??= this.FindControl<Border>("RolePopup");
    private Grid? _cSnapshotPanel;
    private Grid? SnapshotPanelCtl => _cSnapshotPanel ??= this.FindControl<Grid>("SnapshotPanel");
    private Button? _cDeleteMenuButton;
    private Button? DeleteMenuButtonCtl => _cDeleteMenuButton ??= this.FindControl<Button>("DeleteMenuButton");
    private StackPanel? _cDeleteConfirmPanel;
    private StackPanel? DeleteConfirmPanelCtl => _cDeleteConfirmPanel ??= this.FindControl<StackPanel>("DeleteConfirmPanel");
    private TextBox? _cRolePopupNewName;
    private TextBox? RolePopupNewNameCtl => _cRolePopupNewName ??= this.FindControl<TextBox>("RolePopupNewName");
    private Button? _cResetMenuButton;
    private Button? ResetMenuButtonCtl => _cResetMenuButton ??= this.FindControl<Button>("ResetMenuButton");
    private StackPanel? _cResetConfirmPanel;
    private StackPanel? ResetConfirmPanelCtl => _cResetConfirmPanel ??= this.FindControl<StackPanel>("ResetConfirmPanel");
    private FluidVolumeSlider? _cVolumeSlider;
    private FluidVolumeSlider? VolumeSliderCtl => _cVolumeSlider ??= this.FindControl<FluidVolumeSlider>("VolumeSlider");
    private TextBlock? _cVolumeBadgeText;
    private TextBlock? VolumeBadgeTextCtl => _cVolumeBadgeText ??= this.FindControl<TextBlock>("VolumeBadgeText");
    private Button? _cSpeakerHitBox;
    private Button? SpeakerHitBoxCtl => _cSpeakerHitBox ??= this.FindControl<Button>("SpeakerHitBox");
    private Avalonia.Controls.Shapes.Path? _cVolumeSpeakerIcon;
    private Avalonia.Controls.Shapes.Path? VolumeSpeakerIconCtl => _cVolumeSpeakerIcon ??= this.FindControl<Avalonia.Controls.Shapes.Path>("VolumeSpeakerIcon");
}
