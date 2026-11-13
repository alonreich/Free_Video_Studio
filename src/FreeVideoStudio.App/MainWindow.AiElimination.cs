using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App;

/// <summary>
/// AIELIM_02 — background kill scan for the loaded video. The timeline shows a small status chip
/// (scanning / N kills found / why it failed) and a ✕ per kill; clicking a ✕ jumps the preview there.
/// Times are ABSOLUTE source seconds.
/// </summary>
public partial class MainWindow
{
    private readonly List<double> _aiEliminationTimestamps = new();
    private readonly List<AiEliminationMarker> _aiEliminationMarkers = new();
    private CancellationTokenSource? _aiEliminationScanCts;

    /// <summary>AIELIM_02 — text of the timeline chip, null = hidden.</summary>
    private string? _aiEliminationStatus;
    private bool _aiEliminationStatusIsError;
    private bool _isAiEliminationScanning;

    /// <summary>AIELIM_03 — the speed editor while it is open, so late scan results still reach it.</summary>
    private GranularSpeedEditorWindow? _openGranularEditor;

    public void SetAiEliminations(IEnumerable<double>? timestamps)
    {
        _aiEliminationTimestamps.Clear();
        _aiEliminationMarkers.Clear();
        if (timestamps != null)
        {
            foreach (double t in timestamps)
            {
                _aiEliminationTimestamps.Add(t);
                _aiEliminationMarkers.Add(new AiEliminationMarker(t, 1.0, "Elimination"));
            }
        }
        QueueTimelineRedrawPass(DispatcherPriority.Background);
    }

    private void SetAiEliminationStatus(string? text, bool isError, string videoPath)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_loadedVideoPath != videoPath) return;
            _aiEliminationStatus = text;
            _aiEliminationStatusIsError = isError;
            QueueTimelineRedrawPass(DispatcherPriority.Background);
        });
    }

    private async Task RunAiEliminationScanAsync(string videoPath)
    {
        try
        {
            _aiEliminationScanCts?.Cancel();
            _aiEliminationScanCts?.Dispose();
            var cts = new CancellationTokenSource();
            _aiEliminationScanCts = cts;

            var settings = SettingsManager.Instance;
            if (!settings.AiEliminationTrackerEnabled) return;

            var config = settings.GetUsageConfig(AiUsage.EliminationTracker);
            string apiKey = settings.GetApiKey(config.Vendor);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                RuntimeLog.Info("AIElimination", "AI Elimination Tracker enabled but no API key configured. Skipping scan.");
                SetAiEliminationStatus("AI kill scan: no AI key (Settings › AI)", true, videoPath);
                return;
            }

            string ffmpegPath = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            double durationSec = _loadedVideoDurationMs / 1000.0;
            if (durationSec <= 0)
                durationSec = await AiFfmpeg.ProbeDurationSecAsync(ffmpegPath, videoPath, cts.Token).ConfigureAwait(false);
            if (durationSec <= 0)
            {
                SetAiEliminationStatus("AI kill scan: the video's length could not be read.", true, videoPath);
                return;
            }
            string model = settings.GetEffectiveModel(AiUsage.EliminationTracker);
            string vendorName = AiVisionProviders.DisplayName(config.Vendor);
            var est = AiCostEstimator.EstimateKillTracker(config.Vendor, model, AiEliminationTrackerService.MomentBudget(durationSec));
            string currentStep = $"Starting {vendorName} ({model})…";
            var startedUtc = DateTime.UtcNow;

            _isAiEliminationScanning = true;
            SetAiEliminationStatus($"AI ({vendorName} {model}): scanning… ({AiCostEstimator.FormatPreRunBanner(est, config.Vendor, model)})", false, videoPath);
            using var elapsedTimer = new System.Threading.Timer(_ =>
            {
                if (cts.IsCancellationRequested || !_isAiEliminationScanning) return;
                int secs = (int)(DateTime.UtcNow - startedUtc).TotalSeconds;
                SetAiEliminationStatus($"AI: {currentStep} ({secs}s)", false, videoPath);
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            using var usage = AiUsageMeter.BeginTask("AI Kill Tracker", config.Vendor);
            var result = await Task.Run(() => AiEliminationTrackerService.ScanAsync(
                ffmpegPath,
                videoPath,
                durationSec,
                config.Vendor,
                apiKey,
                model,
                status =>
                {
                    currentStep = status;
                    RuntimeLog.Info("AIElimination", status);
                    if (!cts.IsCancellationRequested)
                    {
                        int secs = (int)(DateTime.UtcNow - startedUtc).TotalSeconds;
                        SetAiEliminationStatus(secs >= 1 ? $"AI: {status} ({secs}s)" : "AI: " + status, false, videoPath);
                    }
                },
                cts.Token), cts.Token).ConfigureAwait(false);

            _isAiEliminationScanning = false;
            string usageSummary = usage.Tally.TotalTokens > 0 ? $" · {usage.SummaryWithEstimate(est)}" : "";
            string usageChipNote = usage.Tally.TotalTokens > 0 ? $" ({usage.Tally.TotalTokens:N0} tokens)" : "";
            RuntimeLog.Info("AIElimination", $"Scan finished: {(result.Failed ? "FAILED — " : "")}{result.Summary}{usageSummary} ({result.Markers.Count} marker(s), {result.MomentsChecked} moment(s) checked).");
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || _loadedVideoPath != videoPath) return;
                _aiEliminationTimestamps.Clear();
                _aiEliminationMarkers.Clear();
                foreach (var m in result.Markers)
                {
                    _aiEliminationTimestamps.Add(m.TimestampSec);
                    _aiEliminationMarkers.Add(m);
                }
                _aiEliminationStatus = result.Failed ? "AI kill scan failed: " + result.Summary : "AI: " + result.Summary + usageChipNote;
                _aiEliminationStatusIsError = result.Failed;
                QueueTimelineRedrawPass(DispatcherPriority.Background);

                _openGranularEditor?.SetAiEliminations(_aiEliminationTimestamps);
                if (result.Failed)
                    Controls.FloatingNotice.Error(this, "AI Kill Tracker: " + result.Summary + usageSummary);
                else if (result.Markers.Count > 0)
                    Controls.FloatingNotice.Success(this, $"AI Kill Tracker: {result.Summary} Look for the red ✕ above the timeline.{usageSummary}");
                else
                    Controls.FloatingNotice.Info(this, "AI Kill Tracker: " + result.Summary + usageSummary);
            });
        }
        catch (OperationCanceledException)
        {
            _isAiEliminationScanning = false;
        }
        catch (Exception ex)
        {
            _isAiEliminationScanning = false;
            string reason = ex is AiProviderException pe ? pe.Message : "something unexpected stopped it. Try reopening the video.";
            string reference = AiUsageMeter.LastTaskId;
            RuntimeLog.Fail("AIElimination", $"Kill scan failed (reference {reference}): {ex.GetType().Name}: {ex.Message}");
            SetAiEliminationStatus($"AI kill scan failed: {reason} (Reference: {reference})", true, videoPath);
        }
    }

    /// <summary>AIELIM_02 — the ✕ markers and the status chip above the main (source-time) timeline.</summary>
    private void DrawAiEliminationOverlay(Canvas canvas, double duration, double canvasWidth)
    {
        if (duration <= 0) return;

        var elimBaseColor = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Avalonia.Media.Color.FromRgb(168, 50, 50));
        var elimBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, elimBaseColor.R, elimBaseColor.G, elimBaseColor.B));

        foreach (var marker in _aiEliminationMarkers)
        {
            double elimSec = marker.TimestampSec;
            if (elimSec < 0 || elimSec > duration) continue;
            double ex = (elimSec / duration) * canvasWidth;

            var xMark = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new Avalonia.CornerRadius(9),
                Background = elimBrush,
                BorderBrush = Avalonia.Media.Brushes.White,
                BorderThickness = new Avalonia.Thickness(1.5),
                IsHitTestVisible = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                ZIndex = 500,
                Child = new TextBlock
                {
                    Text = "✕",
                    Foreground = Avalonia.Media.Brushes.White,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(xMark, $"{marker.Description} at {FormatTime(TimeSpan.FromSeconds(elimSec))} — click to jump there");
            xMark.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SeekMainPreviewToMarkerMs(Math.Max(0, elimSec - 1.0) * 1000.0);
            };
            Canvas.SetLeft(xMark, Math.Max(0, Math.Min(canvasWidth - 18, ex - 9)));
            Canvas.SetTop(xMark, -20);
            canvas.Children.Add(xMark);
        }

        if (!string.IsNullOrEmpty(_aiEliminationStatus))
        {
            Control chipContent;
            if (_isAiEliminationScanning)
            {
                var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                panel.Children.Add(new TextBlock
                {
                    Text = _aiEliminationStatus,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                    Foreground = Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", Avalonia.Media.Brushes.White),
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                var cancelBtn = new TextBlock
                {
                    Text = "✕ Cancel",
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = elimBrush,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                cancelBtn.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    _aiEliminationScanCts?.Cancel();
                    _isAiEliminationScanning = false;
                    SetAiEliminationStatus("AI kill scan cancelled", false, _loadedVideoPath);
                };
                panel.Children.Add(cancelBtn);
                chipContent = panel;
            }
            else
            {
                chipContent = new TextBlock
                {
                    Text = _aiEliminationStatus,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                    Foreground = _aiEliminationStatusIsError
                        ? elimBrush
                        : Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", Avalonia.Media.Brushes.White),
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                };
            }

            var chip = new Border
            {
                Background = Infrastructure.ThemeResources.Brush(this, "AppOverlayBrush", Avalonia.Media.Brushes.Black),
                BorderBrush = _aiEliminationStatusIsError ? elimBrush : Infrastructure.ThemeResources.Brush(this, "AppBorderBrush", Avalonia.Media.Brushes.Gray),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(6, 1),
                IsHitTestVisible = true,
                ZIndex = 499,
                MaxWidth = Math.Max(140, canvasWidth * 0.5),
                Child = chipContent,
            };
            ToolTip.SetTip(chip, _aiEliminationStatus);
            chip.Measure(Avalonia.Size.Infinity);
            Canvas.SetLeft(chip, Math.Max(0, canvasWidth - chip.DesiredSize.Width - 4));
            Canvas.SetTop(chip, -40);
            canvas.Children.Add(chip);
        }
    }
}
