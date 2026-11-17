using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

public partial class UpdateFinishedWindow : Window
{
    public UpdateFinishedWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is UpdateFinishedViewModel { IsFinishing: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
