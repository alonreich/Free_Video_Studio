
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

/// <summary>UPGRADEUX_05 — see <see cref="UpdateFinishedViewModel"/>. All state is bound.</summary>
public partial class UpdateFinishedWindow : Window
{
    public UpdateFinishedWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Cannot be dismissed while the app is still confirming its own startup.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is UpdateFinishedViewModel { IsFinishing: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
