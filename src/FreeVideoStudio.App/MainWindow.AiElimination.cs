using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private readonly List<double> _aiEliminationTimestamps = new();
    private readonly List<AiEliminationMarker> _aiEliminationMarkers = new();
    private CancellationTokenSource? _aiEliminationScanCts;

    private string? _aiEliminationStatus;
    private string? _aiEliminationDetail;
    private bool _aiEliminationStatusIsError;
    private bool _isAiEliminationScanning;

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

    private void SetAiEliminationStatus(string? text, bool isError, string videoPath, string? detail = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_loadedVideoPath != videoPath) return;
            _aiEliminationStatus = text;
            _aiEliminationDetail = detail;
            _aiEliminationStatusIsError = isError;
            QueueTimelineRedrawPass(DispatcherPriority.Background);
        });
    }

    internal const string ScanOwnerTag = "AIELIMOWNER_01";

    private bool IsCurrentAiEliminationScan(CancellationTokenSource cts) => ReferenceEquals(_aiEliminationScanCts, cts);

    private void EndAiEliminationScan(CancellationTokenSource cts)
    {
        if (IsCurrentAiEliminationScan(cts)) _isAiEliminationScanning = false;
    }

    internal const string AskBeforeScanTag = "AIELIMASK_01";

    internal const double AutoScanUnderUsd = AiEliminationTrackerService.AutoScanUnderUsd;

    private Action? _aiEliminationStartAction;

    internal static bool ScanStartsByItself(bool userAsked, bool saved, AiEstimatedUsage est, AiVendor vendor, string model, double autoApproveUnderUsd)
        => AiEliminationTrackerService.StartsByItself(userAsked, saved, est, vendor, model, autoApproveUnderUsd);

    internal const string ChipSetupTag = "AIELIMSETUP_01";

    private async Task SetUpAiThenScanAsync(string videoPath, AiVendor vendor)
    {
        RuntimeLog.Info("AIElimination", $"{ChipSetupTag}: chip clicked without an AI; opening the setup.");
        if (await Controls.AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, vendor, AiUsage.EliminationTracker))
        {
            if (_loadedVideoPath == videoPath) _ = RunAiEliminationScanAsync(videoPath, userAsked: true);
            return;
        }
        if (_loadedVideoPath == videoPath) _ = RunAiEliminationScanAsync(videoPath);
    }

    private async Task FixAiThenScanAsync(string videoPath, AiProviderException problem)
    {
        if (await AiAccountFix.OfferAsync(this, problem, AiUsage.EliminationTracker))
        {
            if (_loadedVideoPath == videoPath) _ = RunAiEliminationScanAsync(videoPath, userAsked: true);
            return;
        }
        if (_loadedVideoPath != videoPath) return;
        _aiEliminationStartAction = () => _ = FixAiThenScanAsync(videoPath, problem);
        QueueTimelineRedrawPass(DispatcherPriority.Background);
    }

    private bool _aiEliminationSettingsHooked;
    private bool _aiEliminationLastEnabled;

    private void HookAiEliminationSettings()
    {
        if (_aiEliminationSettingsHooked) return;
        _aiEliminationSettingsHooked = true;
        _aiEliminationLastEnabled = SettingsManager.Instance.AiEliminationTrackerEnabled;
        Action<AppSettings> onCommit = s => Dispatcher.UIThread.Post(() => OnAiEliminationSettingChanged(s.AiEliminationTrackerEnabled));
        SettingsManager.Committed += onCommit;
        Closed += (_, _) => SettingsManager.Committed -= onCommit;
    }

    private void OnAiEliminationSettingChanged(bool enabled)
    {
        if (enabled == _aiEliminationLastEnabled) return;
        _aiEliminationLastEnabled = enabled;
        if (string.IsNullOrWhiteSpace(_loadedVideoPath)) return;
        if (enabled)
        {
            RuntimeLog.Info("AIElimination", "Mark my kills was switched on; scanning the open video.");
            _ = RunAiEliminationScanAsync(_loadedVideoPath);
            return;
        }
        RuntimeLog.Info("AIElimination", "Mark my kills was switched off; the scan stops.");
        _aiEliminationScanCts?.Cancel();
        _isAiEliminationScanning = false;
        _aiEliminationStartAction = null;
        _aiEliminationStatus = null;
        _aiEliminationDetail = null;
        QueueTimelineRedrawPass(DispatcherPriority.Background);
    }

    private async Task RunAiEliminationScanAsync(string videoPath, bool userAsked = false, bool refresh = false)
    {
        _aiEliminationStartAction = null;
        _aiEliminationScanCts?.Cancel();
        _aiEliminationScanCts?.Dispose();
        var cts = new CancellationTokenSource();
        _aiEliminationScanCts = cts;
        string? reference = null;
        try
        {
            var settings = SettingsManager.Instance;
            if (!settings.AiEliminationTrackerEnabled) return;

            var config = settings.GetUsageConfig(AiUsage.EliminationTracker);
            string apiKey = settings.GetApiKey(config.Vendor);
            if (!AiAccountFix.IsUsable(settings, config.Vendor))
            {
                bool broken = AiAccountFix.IsBroken(settings, config.Vendor);
                RuntimeLog.Info("AIElimination", broken
                    ? $"{AiAccountFix.Tag}: the saved {config.Vendor} key does not work. Skipping scan; the chip offers the fix."
                    : "AI Elimination Tracker enabled but no API key configured. Skipping scan.");
                var setupVendor = config.Vendor;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_loadedVideoPath != videoPath) return;
                    _aiEliminationStartAction = () => _ = SetUpAiThenScanAsync(videoPath, setupVendor);
                    _aiEliminationStatus = settings.AiMode == AiMode.LocalOnly
                        ? "AI kill scan needs online AI: click to change how AI works"
                        : broken
                        ? $"AI kill scan: your {AiVisionProviders.DisplayName(setupVendor)} key does not work. Click to fix"
                        : "AI kill scan: click to connect an AI";
                    _aiEliminationDetail = "Opens the short setup: pick an AI company, get a key from its page, paste it. Then the scan starts.";
                    _aiEliminationStatusIsError = true;
                    QueueTimelineRedrawPass(DispatcherPriority.Background);
                });
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
            var est = AiPreflight.WithCheck(AiCostEstimator.EstimateKillTracker(config.Vendor, model, AiEliminationTrackerService.MomentBudget(durationSec)),
                config.Vendor, apiKey, model);
            string currentStep = $"Starting {vendorName} ({model})…";
            var startedUtc = DateTime.UtcNow;
            string costNote = $"{vendorName} · {AiCostEstimator.PlainCostShort(est, config.Vendor, model)} (estimate)";

            bool saved = !refresh && AiEliminationTrackerService.HasSavedScan(videoPath, durationSec, config.Vendor, model);
            if (!ScanStartsByItself(userAsked, saved, est, config.Vendor, model, settings.AiAutoApproveUnderUsd))
            {
                string price = AiCostEstimator.PlainCostShort(est, config.Vendor, model);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested || _loadedVideoPath != videoPath) return;
                    _aiEliminationStartAction = () => _ = RunAiEliminationScanAsync(videoPath, userAsked: true);
                    _aiEliminationStatus = $"AI kill scan: click to start ({price}, estimate)";
                    _aiEliminationDetail = $"Sends up to {AiEliminationTrackerService.MomentBudget(durationSec) * AiEliminationTrackerService.FrameOffsetsSec.Length} still pictures to {vendorName}.\n{costNote}\nScans under ${AutoScanUnderUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} start by themselves.";
                    _aiEliminationStatusIsError = false;
                    QueueTimelineRedrawPass(DispatcherPriority.Background);
                });
                return;
            }

            _isAiEliminationScanning = true;
            SetAiEliminationStatus("AI: looking for your kills…", false, videoPath, costNote);
            using var elapsedTimer = new System.Threading.Timer(_ =>
            {
                if (cts.IsCancellationRequested || !_isAiEliminationScanning) return;
                int secs = (int)(DateTime.UtcNow - startedUtc).TotalSeconds;
                SetAiEliminationStatus($"AI: looking for your kills… {secs} s", false, videoPath, $"{currentStep}\n{costNote}");
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            using var usage = AiUsageMeter.BeginTask("AI Kill Tracker", config.Vendor);
            reference = usage.Id;
            usage.SetEstimate(est);
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
                        SetAiEliminationStatus(secs >= 1 ? $"AI: looking for your kills… {secs} s" : "AI: looking for your kills…", false, videoPath, $"{status}\n{costNote}");
                    }
                },
                cts.Token, refresh: refresh), cts.Token).ConfigureAwait(false);

            EndAiEliminationScan(cts);
            string usageSummary = usage.Tally.TotalTokens > 0 ? $" · {usage.SummaryWithEstimate(est)}" : "";
            string plainCost = usage.Tally.TotalTokens > 0 ? " " + usage.PlainSummary() : "";
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
                _aiEliminationStatus = result.Failed ? "AI kill scan failed: " + result.Summary : "AI: " + result.Summary;
                _aiEliminationDetail = usageSummary.Length > 0 ? _aiEliminationStatus + usageSummary : null;
                _aiEliminationStatusIsError = result.Failed;
                QueueTimelineRedrawPass(DispatcherPriority.Background);

                _openGranularEditor?.SetAiEliminations(_aiEliminationTimestamps);
                if (result.Failed)
                    Controls.FloatingNotice.Error(this, "AI Kill Tracker: " + result.Summary + plainCost + $" (Reference: {reference})");
                else if (result.Markers.Count > 0)
                    Controls.FloatingNotice.Success(this, $"AI Kill Tracker: {result.Summary} Look for the red ✕ above the timeline.{plainCost}");
                else
                    Controls.FloatingNotice.Info(this, "AI Kill Tracker: " + result.Summary + plainCost);
            });
        }
        catch (OperationCanceledException)
        {
            EndAiEliminationScan(cts);
        }
        catch (Exception ex)
        {
            EndAiEliminationScan(cts);
            string reason = ex is AiProviderException pe ? pe.Message : "something unexpected stopped it. Try reopening the video.";
            reference ??= AiUsageMeter.LastTaskId;
            RuntimeLog.Fail("AIElimination", $"Kill scan failed (reference {reference}): {ex.GetType().Name}: {ex.Message}");
            if (AiAccountFix.Find(ex) is { } account)
            {
                string fixRef = reference;
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested || _loadedVideoPath != videoPath) return;
                    _aiEliminationStartAction = () => _ = FixAiThenScanAsync(videoPath, account);
                    _aiEliminationStatus = $"AI kill scan: {AiAccountFix.ShortProblem(account)}. Click to fix";
                    _aiEliminationDetail = $"{account.Message}\n{AiAccountFix.NothingCharged}\nClick to fix it or pick another AI company. (Reference: {fixRef})";
                    _aiEliminationStatusIsError = true;
                    QueueTimelineRedrawPass(DispatcherPriority.Background);
                    Controls.FloatingNotice.Error(this, $"AI Kill Tracker: {reason} Click the red chip above the timeline to fix it.");
                });
                return;
            }
            SetAiEliminationStatus($"AI kill scan failed: {reason} (Reference: {reference})", true, videoPath);
            if (ex is AiProviderException)
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested && _loadedVideoPath == videoPath)
                        Controls.FloatingNotice.Error(this, $"AI Kill Tracker: {reason} (Reference: {reference})");
                });
        }
    }

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
            ToolTip.SetTip(chip, string.IsNullOrWhiteSpace(_aiEliminationDetail) ? _aiEliminationStatus : _aiEliminationDetail);
            if (!_isAiEliminationScanning && _aiEliminationStartAction is { } start)
            {
                chip.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                chip.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    _aiEliminationStartAction = null;
                    start();
                };
            }
            if (!_isAiEliminationScanning && !string.IsNullOrWhiteSpace(_loadedVideoPath)
                && SettingsManager.Instance.AiEliminationTrackerEnabled)
            {
                string path = _loadedVideoPath;
                var again = new MenuItem { Header = "Scan this video again (sends new AI requests)" };
                again.Click += (_, _) => _ = RunAiEliminationScanAsync(path, userAsked: true, refresh: true);
                chip.ContextMenu = new ContextMenu { ItemsSource = new object[] { again } };
            }
            chip.Measure(Avalonia.Size.Infinity);
            Canvas.SetLeft(chip, Math.Max(0, canvasWidth - chip.DesiredSize.Width - 4));
            Canvas.SetTop(chip, -40);
            canvas.Children.Add(chip);
        }
    }
}
