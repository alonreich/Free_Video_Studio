// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FreeVideoStudio.App.Controls;

public partial class MusicAttributionDialogWindow : Window
{
    private string _attributionText = string.Empty;

    public MusicAttributionDialogWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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
