using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FreeVideoStudio.App.Controls;

public partial class MusicAttributionDialogWindow : Window
{
    private string _attributionText = string.Empty;

    public MusicAttributionDialogWindow()
    {
        InitializeComponent();
        AttachTitleBarDrag();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void AttachTitleBarDrag()
    {
        if (this.FindNameScope()?.Find("TitleBarBorder") is Border titleBar)
        {
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            };
        }
    }

    public void SetAttributionText(string text)
    {
        _attributionText = text ?? string.Empty;
        var box = this.FindNameScope()?.Find("AttributionTextBox") as TextBox;
        if (box != null) box.Text = _attributionText;
    }

    private void OnCopyCreditClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard != null && !string.IsNullOrEmpty(_attributionText))
            {
                _ = top.Clipboard.SetTextAsync(_attributionText);
            }

            var icon = this.FindNameScope()?.Find("CopyIcon") as TextBlock;
            var btnText = this.FindNameScope()?.Find("CopyButtonText") as TextBlock;
            var btn = this.FindNameScope()?.Find("CopyCreditButton") as Button;
            if (icon != null) icon.Text = "✅";
            if (btnText != null) btnText.Text = "COPIED TO CLIPBOARD!";
            if (btn != null)
            {
                btn.Classes.Clear();
                btn.Classes.Add("Success");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private void OnGotItClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
