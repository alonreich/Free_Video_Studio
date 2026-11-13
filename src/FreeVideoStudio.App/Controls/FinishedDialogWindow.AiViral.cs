using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// AIVIRAL_HONEST_01 — the Viral Post card on the Finished screen. The badge always says what really
/// happened (WRITING… / AI WRITTEN / NO AI KEY / AI FAILED); a stock template is never shown as an AI answer.
/// NEW VERSION asks again for a different take (creative temperature, previous title passed as "avoid").
/// VIRALVM_01: the work and the decisions live in <see cref="ViralPostViewModel"/>; this file draws its state.
/// </summary>
public partial class FinishedDialogWindow
{
    private ViralPostViewModel? _viral;

    private T? FindViral<T>(string name) where T : class => this.FindNameScope()?.Find(name) as T;

    private void StartViralPostGeneration(string videoPath)
    {
        var card = FindViral<Border>("ViralPostCard");
        if (card == null) return;

        if (_viral == null)
        {
            _viral = new ViralPostViewModel(SettingsManagerStore.Instance);
            _viral.Changed += view => Dispatcher.UIThread.Post(() => RenderViral(view));
            Closed += (_, _) => _viral.Dispose();
        }

        if (!_viral.IsEnabled)
        {
            card.IsVisible = false;
            return;
        }

        card.IsVisible = true;
        _ = _viral.StartAsync(videoPath);
    }

    private void RenderViral(ViralPostView view)
    {
        if (view.Post != null)
        {
            if (FindViral<TextBox>("ViralTitleTextBox") is { } title) title.Text = view.Post.Title;
            if (FindViral<TextBox>("ViralDescriptionTextBox") is { } desc) desc.Text = view.Post.FormattedDescription;
            ResetCopyButtons();
        }
        if (FindViral<TextBlock>("ViralPostStatusBadge") is { } b) b.Text = view.Badge;
        if (FindViral<TextBlock>("ViralPostMessageText") is { } m)
        {
            m.Text = view.Message ?? string.Empty;
            m.IsVisible = !string.IsNullOrWhiteSpace(view.Message);
        }
        if (FindViral<Grid>("ViralTitleRow") is { } tr) tr.IsVisible = view.ShowText;
        if (FindViral<Grid>("ViralCaptionRow") is { } cr) cr.IsVisible = view.ShowText;
        if (FindViral<Grid>("ViralCopyAllRow") is { } car) car.IsVisible = view.ShowText;
        if (FindViral<Button>("ViralCancelBtn") is { } cb) cb.IsVisible = view.ShowCancel;
        if (FindViral<Button>("ViralRegenerateBtn") is { } rb)
        {
            rb.IsVisible = view.ShowRegenerate;
            rb.Content = view.RegenerateLabel;
        }
        if (FindViral<Button>("ViralSetupAiBtn") is { } sb) sb.IsVisible = view.ShowSetup;
    }

    private void OnViralCancelClicked(object? sender, RoutedEventArgs e) => _viral?.Cancel();

    private void OnViralRegenerateClicked(object? sender, RoutedEventArgs e)
    {
        if (_viral != null) _ = _viral.RegenerateAsync();
    }

    private void OnViralSetupAiClicked(object? sender, RoutedEventArgs e) => _ = SetupAiAndRetryAsync();

    private async Task SetupAiAndRetryAsync()
    {
        try
        {
            if (_viral != null && await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, _viral.Vendor, AiUsage.ViralPost))
                await _viral.RetryAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private void OnCopyViralTitleClicked(object? sender, RoutedEventArgs e)
        => CopyViralText(FindViral<TextBox>("ViralTitleTextBox")?.Text ?? _viral?.View.Post?.Title, "CopyViralTitleBtn", "📋 COPY TITLE");

    private void OnCopyViralDescriptionClicked(object? sender, RoutedEventArgs e)
        => CopyViralText(FindViral<TextBox>("ViralDescriptionTextBox")?.Text ?? _viral?.View.Post?.FormattedDescription, "CopyViralDescBtn", "📋 COPY CAPTION");

    private void OnCopyViralAllClicked(object? sender, RoutedEventArgs e)
    {
        string? title = FindViral<TextBox>("ViralTitleTextBox")?.Text ?? _viral?.View.Post?.Title;
        string? desc = FindViral<TextBox>("ViralDescriptionTextBox")?.Text ?? _viral?.View.Post?.FormattedDescription;
        string text = string.IsNullOrWhiteSpace(title) ? (desc ?? "") : string.IsNullOrWhiteSpace(desc) ? title : $"{title}\n\n{desc}";
        CopyViralText(text, "CopyViralAllBtn", "📋 COPY ALL (YT / TIKTOK)");
    }

    /// <summary>Copies, shows "✅ COPIED!" for two seconds, then puts the label back so the next copy is visible too.</summary>
    private void CopyViralText(string? text, string buttonName, string idleLabel)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                _ = clipboard.SetTextAsync(text);

            if (FindViral<Button>(buttonName) is not { } btn) return;
            btn.Content = "✅ COPIED!";
            DispatcherTimer.RunOnce(() => btn.Content = idleLabel, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private void ResetCopyButtons()
    {
        if (FindViral<Button>("CopyViralTitleBtn") is { } a) a.Content = "📋 COPY TITLE";
        if (FindViral<Button>("CopyViralDescBtn") is { } b) b.Content = "📋 COPY CAPTION";
        if (FindViral<Button>("CopyViralAllBtn") is { } c) c.Content = "📋 COPY ALL (YT / TIKTOK)";
    }
}
