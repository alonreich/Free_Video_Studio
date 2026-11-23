using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using System.Diagnostics;
using System.IO;

namespace FreeVideoStudio.App.Controls;

public partial class FinishedDialogWindow : Window
{
    private string _outputPath = "";
    private string? _attributionText;

    public int DialogResult { get; private set; } = 0;

    public FinishedDialogWindow()
    {
        InitializeComponent();
        DialogFit.Apply(this, sizeKey: "FinishedDialogSize", grip: false);

        Controls.WindowResizeGrip.Attach(this, "Drag to resize the Finished dialog");

        Opened += (_, _) => UiSoundEffect.PlayProcess();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetOutputPath(string path)
    {
        _outputPath = path;
        var txt = this.FindControl<TextBlock>("MessageText");
        if (txt != null)
        {
            txt.Text = $"File successfully saved to:\n{path}";
        }
        var card = this.FindNameScope()?.Find("ViralPostCard") as Border;
        if (card != null) card.IsVisible = false;
    }

    public void SetAttributionText(string? text)
    {
        _attributionText = text;
        var card = this.FindNameScope()?.Find("AttributionCard") as Border;
        if (card != null)
        {
            card.IsVisible = !string.IsNullOrWhiteSpace(_attributionText);
        }
    }

    internal const string ShareFeedbackTag = "SHAREFAIL_01";

    internal Action<ProcessStartInfo> ShellLaunch { get; set; } = Services.ShellLauncher.Launch;

    internal Func<string, System.Threading.Tasks.Task<bool>>? CopyTextOverride { get; set; }

    internal string? ShareProblem { get; private set; }

    private void OnCopyCreditClicked(object? sender, RoutedEventArgs e) => _ = CopyCreditAsync();

    internal async System.Threading.Tasks.Task CopyCreditAsync()
    {
        if (string.IsNullOrEmpty(_attributionText)) return;
        if (!await TryCopyAsync(_attributionText))
        {
            ShowShareProblem("Couldn't copy the music credit. Copy it by hand from here: " + _attributionText);
            return;
        }
        var btn = this.FindNameScope()?.Find("FinishedCopyCreditBtn") as Button;
        if (btn != null)
        {
            btn.Content = "✅ COPIED!";
            btn.Classes.Clear();
            btn.Classes.Add("Success");
        }
    }

    private async System.Threading.Tasks.Task<bool> TryCopyAsync(string text)
    {
        try
        {
            if (CopyTextOverride is { } copy) return await copy(text);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    private void ShowShareProblem(string message)
    {
        ShareProblem = message;
        RuntimeLog.Info("UI", $"{ShareFeedbackTag}: {message}");
        if (this.FindNameScope()?.Find("ShareStatusText") is TextBlock status)
        {
            status.Text = "⚠ " + message;
            status.IsVisible = true;
        }
    }

    private async System.Threading.Tasks.Task ShareAsync(string site, string url, bool withCredit)
    {
        if (withCredit && !string.IsNullOrEmpty(_attributionText))
        {
            await TryCopyAsync(_attributionText);
            var attrDlg = new MusicAttributionDialogWindow();
            attrDlg.SetAttributionText(_attributionText);
            await attrDlg.ShowDialog(this);
        }
        try
        {
            ShellLaunch(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            bool copied = await TryCopyAsync(url);
            ShowShareProblem(copied
                ? $"Couldn't open the browser for {site}; the link is copied. Paste it into your browser. Your video is saved at {_outputPath}."
                : $"Couldn't open the browser for {site}. Go to {url} yourself. Your video is saved at {_outputPath}.");
            return;
        }
        if (!OpenOutputFolder()) return;
        DialogResult = 1;
        Close();
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount >= 2) return;
        var el = e.Source as StyledElement;
        while (el != null)
        {
            if (el is Button || el is TextBox) return;
            if (el is Control c && c.Name == "ResizeGrip") return;
            el = el.Parent;
        }
        try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        DialogResult = 0;
        Close();
    }

    private async void OnUploadNewClicked(object? sender, RoutedEventArgs e)
    {
        if (Infrastructure.SettingsManager.Instance.ConfirmFinishedDialogExit
            && !await ConfirmAsync("Start a New File",
                                   "Discard the current project and start a new one?\nThe file you just exported is safe on disk.",
                                   "YES, START NEW"))
        {
            return;
        }

        DialogResult = 2;
        Close();
    }

    private async System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string message, string yesText)
    {
        var dlg = new ConfirmDialogWindow();
        dlg.SetTitle(title);
        dlg.SetMessage(message);
        dlg.SetButtonText(yesText, "CANCEL");
        await dlg.ShowDialog(this);
        return dlg.Result;
    }

    private void OnWhatsAppClicked(object? sender, RoutedEventArgs e) => _ = ShareAsync("WhatsApp", "https://web.whatsapp.com", withCredit: false);

    private void OnInstagramClicked(object? sender, RoutedEventArgs e)
    {
        if (TryActivateViralPostForSocial("Instagram")) return;
        _ = ShareAsync("Instagram", "https://www.instagram.com/", withCredit: false);
    }

    private void OnYouTubeClicked(object? sender, RoutedEventArgs e)
    {
        if (TryActivateViralPostForSocial("YouTube")) return;
        _ = ShareAsync("YouTube", "https://studio.youtube.com/", withCredit: true);
    }

    private void OnTikTokClicked(object? sender, RoutedEventArgs e)
    {
        if (TryActivateViralPostForSocial("TikTok")) return;
        _ = ShareAsync("TikTok", "https://www.tiktok.com/creator-center/upload", withCredit: true);
    }

    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (!OpenOutputFolder()) return;
        DialogResult = 1;
        Close();
    }

    private async void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        if (Infrastructure.SettingsManager.Instance.ConfirmFinishedDialogExit
            && !await ConfirmAsync("Exit the App",
                                   "Close the application now?\nThe file you just exported is safe on disk.",
                                   "YES, EXIT"))
        {
            return;
        }

        DialogResult = 1;
        Close();
    }

    private bool OpenOutputFolder()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_outputPath) && File.Exists(_outputPath))
            {
                var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
                psi.ArgumentList.Add("/select," + _outputPath);
                ShellLaunch(psi);
            }
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Could not open the output folder: {ex.Message}");
            ShowShareProblem($"Couldn't open the folder. Your video is saved at {_outputPath}.");
            return false;
        }
    }
}
