using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Layout;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App.Controls;

public partial class ConfirmDialogWindow
{
    private Button? _cYesBtn;
    private Button? YesBtnCtl => _cYesBtn ??= this.FindControl<Button>("YesBtn");
    private Button? _cNoBtn;
    private Button? NoBtnCtl => _cNoBtn ??= this.FindControl<Button>("NoBtn");
    private Button? _cAltBtn;
    private Button? AltBtnCtl => _cAltBtn ??= this.FindControl<Button>("AltBtn");
}
