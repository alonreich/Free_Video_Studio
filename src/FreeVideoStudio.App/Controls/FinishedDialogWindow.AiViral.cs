// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md (#UI-MERGER-MULTISELECT), docs/05_SYSTEM_LIFECYCLE_STORAGE.md (#SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// AIVIRAL_HONEST_01 — the Viral Post card on the Finished screen. The badge always says what really
/// happened (WRITING… / AI WRITTEN / NO AI KEY / AI FAILED); a stock template is never shown as an AI answer.
/// NEW VERSION asks again for a different take (creative temperature, previous title passed as "avoid").
/// </summary>
public partial class FinishedDialogWindow
{
    private CancellationTokenSource? _viralPostCts;
    private ViralPostResult? _viralPostResult;
    private string? _viralVideoPath;
    private bool _viralCloseHooked;

    private T? FindViral<T>(string name) where T : class => this.FindNameScope()?.Find(name) as T;

    private void StartViralPostGeneration(string videoPath)
    {
        var card = FindViral<Border>("ViralPostCard");
        if (card == null) return;

        if (!SettingsManager.Instance.AiViralPostHelperEnabled)
        {
            card.IsVisible = false;
            return;
        }

        if (!_viralCloseHooked)
        {
            _viralCloseHooked = true;
            Closed += (_, _) => { _viralPostCts?.Cancel(); _viralPostCts?.Dispose(); _viralPostCts = null; };
        }

        _viralVideoPath = videoPath;
        card.IsVisible = true;
        RequestViralPost(avoidTitle: null);
    }

    private void RequestViralPost(string? avoidTitle)
    {
        string? videoPath = _viralVideoPath;
        if (string.IsNullOrEmpty(videoPath)) return;

        ShowViralState("WRITING…", message: "Looking at your video and writing a title, caption and hashtags…",
            showText: false, showRegenerate: false, showSetup: false);

        _viralPostCts?.Cancel();
        _viralPostCts?.Dispose();
        var cts = new CancellationTokenSource();
        _viralPostCts = cts;

        var settings = SettingsManager.Instance;
        var config = settings.GetUsageConfig(AiUsage.ViralPost);
        string apiKey = settings.GetApiKey(config.Vendor);
        string model = settings.GetEffectiveModel(AiUsage.ViralPost);
        string ffmpegPath = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");

        _ = Task.Run(async () =>
        {
            ViralPostOutcome outcome;
            try
            {
                outcome = await AiViralPostService.GenerateAsync(ffmpegPath, videoPath, config.Vendor, apiKey, model, avoidTitle, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
                outcome = new ViralPostOutcome(ViralPostStatus.Failed, null, "Something went wrong while writing the post. Try again.");
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                ApplyViralOutcome(outcome);
            });
        });
    }

    private void ApplyViralOutcome(ViralPostOutcome outcome)
    {
        switch (outcome.Status)
        {
            case ViralPostStatus.AiGenerated when outcome.Post != null:
                _viralPostResult = outcome.Post;
                if (FindViral<TextBox>("ViralTitleTextBox") is { } title) title.Text = outcome.Post.Title;
                if (FindViral<TextBox>("ViralDescriptionTextBox") is { } desc) desc.Text = outcome.Post.FormattedDescription;
                ResetCopyButtons();
                ShowViralState("AI WRITTEN", message: null, showText: true, showRegenerate: true, showSetup: false);
                break;
            case ViralPostStatus.NoKey:
                _viralPostResult = null;
                ShowViralState("NO AI KEY", outcome.Message, showText: false, showRegenerate: false, showSetup: true);
                break;
            default:
                _viralPostResult = null;
                ShowViralState("AI FAILED", outcome.Message ?? "The AI could not write the post.", showText: false, showRegenerate: true, showSetup: false);
                if (FindViral<Button>("ViralRegenerateBtn") is { } retry) retry.Content = "↻ RETRY";
                break;
        }
    }

    private void ShowViralState(string badge, string? message, bool showText, bool showRegenerate, bool showSetup)
    {
        if (FindViral<TextBlock>("ViralPostStatusBadge") is { } b) b.Text = badge;
        if (FindViral<TextBlock>("ViralPostMessageText") is { } m)
        {
            m.Text = message ?? string.Empty;
            m.IsVisible = !string.IsNullOrWhiteSpace(message);
        }
        if (FindViral<Grid>("ViralTitleRow") is { } tr) tr.IsVisible = showText;
        if (FindViral<Grid>("ViralCaptionRow") is { } cr) cr.IsVisible = showText;
        if (FindViral<Button>("ViralRegenerateBtn") is { } rb)
        {
            rb.IsVisible = showRegenerate;
            rb.Content = "↻ NEW VERSION";
        }
        if (FindViral<Button>("ViralSetupAiBtn") is { } sb) sb.IsVisible = showSetup;
    }

    private void OnViralRegenerateClicked(object? sender, RoutedEventArgs e)
        => RequestViralPost(_viralPostResult?.Title);

    private void OnViralSetupAiClicked(object? sender, RoutedEventArgs e) => _ = SetupAiAndRetryAsync();

    private async Task SetupAiAndRetryAsync()
    {
        try
        {
            var vendor = SettingsManager.Instance.GetUsageConfig(AiUsage.ViralPost).Vendor;
            if (await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, vendor))
                RequestViralPost(avoidTitle: null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private void OnCopyViralTitleClicked(object? sender, RoutedEventArgs e)
        => CopyViralText(FindViral<TextBox>("ViralTitleTextBox")?.Text ?? _viralPostResult?.Title, "CopyViralTitleBtn", "📋 COPY TITLE");

    private void OnCopyViralDescriptionClicked(object? sender, RoutedEventArgs e)
        => CopyViralText(FindViral<TextBox>("ViralDescriptionTextBox")?.Text ?? _viralPostResult?.FormattedDescription, "CopyViralDescBtn", "📋 COPY CAPTION");

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
    }
}
