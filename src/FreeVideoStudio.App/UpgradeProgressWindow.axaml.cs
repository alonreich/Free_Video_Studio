using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App;

public partial class UpgradeProgressWindow : Window
{
    public UpgradeProgressWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Controls.DialogFit.Apply(this, drag: true);
    }

    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && DataContext is UpgradeProgressViewModel vm)
        {
            if (vm.IsAsking)
            {
                vm.CancelCommand.Execute(null);
            }
            else if (!vm.IsFinished)
            {
                vm.Detail = "Please wait — this window closes by itself when the update is finished.";
            }
            else
            {
                vm.CloseCommand.Execute(null);
                base.OnClosing(e);
                return;
            }
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
