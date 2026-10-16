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

public partial class FinishedDialogWindow
{
    private CancellationTokenSource? _viralPostCts;
    private ViralPostResult? _viralPostResult;

    private void StartViralPostGeneration(string videoPath)
    {
        var card = this.FindNameScope()?.Find("ViralPostCard") as Border;
        if (card == null) return;

        if (!SettingsManager.Instance.AiViralPostHelperEnabled)
        {
            card.IsVisible = false;
            return;
        }

        card.IsVisible = true;
        var statusBadge = this.FindNameScope()?.Find("ViralPostStatusBadge") as TextBlock;
        if (statusBadge != null) statusBadge.Text = "GENERATING 2026 HOOKS…";

        var titleBox = this.FindNameScope()?.Find("ViralTitleTextBox") as TextBox;
        if (titleBox != null) titleBox.Text = "Analyzing video highlights…";

        var descBox = this.FindNameScope()?.Find("ViralDescriptionTextBox") as TextBox;
        if (descBox != null) descBox.Text = "Formulating viral hooks and trending hashtags…";

        _viralPostCts?.Cancel();
        _viralPostCts?.Dispose();
        var cts = new CancellationTokenSource();
        _viralPostCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = SettingsManager.Instance;
                var config = settings.GetUsageConfig(AiUsage.ViralPost);
                string apiKey = settings.GetApiKey(config.Vendor);
                string ffmpegPath = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");

                var result = await AiViralPostService.GenerateViralPostAsync(
                    ffmpegPath,
                    videoPath,
                    config.Vendor,
                    apiKey,
                    config.Model,
                    cts.Token).ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _viralPostResult = result;
                    if (titleBox != null) titleBox.Text = result?.Title ?? "Bro thought he was safe... 💀";
                    if (descBox != null) descBox.Text = result?.FormattedDescription ?? "#gaming #clutch #fyp #viral #shorts";
                    if (statusBadge != null) statusBadge.Text = string.IsNullOrWhiteSpace(apiKey) ? "OFFLINE TEMPLATE" : "AI GENERATED";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    if (titleBox != null) titleBox.Text = "Bro thought he was safe... 💀";
                    if (descBox != null) descBox.Text = "Rate this clip 1-10 in the comments 👇\n\n#gaming #clutch #fyp #viral #shorts";
                    if (statusBadge != null) statusBadge.Text = "TEMPLATE FALLBACK";
                });
            }
        });
    }

    private void OnCopyViralTitleClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var titleBox = this.FindNameScope()?.Find("ViralTitleTextBox") as TextBox;
            string text = titleBox?.Text ?? _viralPostResult?.Title ?? "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null)
                {
                    _ = top.Clipboard.SetTextAsync(text);
                }
            }

            var btn = this.FindNameScope()?.Find("CopyViralTitleBtn") as Button;
            if (btn != null)
            {
                btn.Content = "✅ COPIED!";
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private void OnCopyViralDescriptionClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var descBox = this.FindNameScope()?.Find("ViralDescriptionTextBox") as TextBox;
            string text = descBox?.Text ?? _viralPostResult?.FormattedDescription ?? "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null)
                {
                    _ = top.Clipboard.SetTextAsync(text);
                }
            }

            var btn = this.FindNameScope()?.Find("CopyViralDescBtn") as Button;
            if (btn != null)
            {
                btn.Content = "✅ COPIED!";
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }
}
