using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.LocalAi;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow
{
    internal const string CardTag = "LOCALAI_01";

    private CancellationTokenSource? _localAiCts;
    private Action? _showLocalAiCard;
    private Action? _showAiModeRow;

    internal static string LocalAiStatusLine(LocalAiPackStatus s, AiMode mode, string size, LocalDetectorStatus? detector = null)
    {
        string detSuffix = detector?.State switch
        {
            LocalDetectorState.Installed => " Object detector: installed.",
            LocalDetectorState.Absent => " Object detector (yolov8n.onnx): not included (suggestions use motion & contrast).",
            LocalDetectorState.Damaged => $" Object detector ({LocalAiPackManifest.DetectorFile}) is damaged.",
            _ => "",
        };
        return s.State switch
        {
            LocalAiPackState.Ready when AiSetup.UsesPc(mode) => "Optional neural pack installed and in use. AI SMART ZOOM follows targets with SAM2 on this PC." + detSuffix,
            LocalAiPackState.Ready => "Optional neural pack installed, but not in use: you chose 'Online only'. Press Change… above to use PC tracking." + detSuffix,
            LocalAiPackState.Damaged => s.Message + detSuffix,
            LocalAiPackState.NotPublished => AiSetup.UsesPc(mode)
                ? "Built-in basic local Smart Zoom is active on this PC (100% free, zero cloud calls). Optional high-accuracy neural pack has not been published yet."
                : "Built-in basic local Smart Zoom is available on this PC if needed. Optional neural pack has not been published yet.",
            _ => "Not installed (built-in basic local Smart Zoom still works on this PC). Press Change… above to download" + (size.Length > 0 ? $" ({size} optional neural pack)." : "."),
        };
    }

    internal static string LocalAiRemoveQuestion(AiMode saved, bool hasKey) => AiSetup.AfterPackRemoved(saved, hasKey) switch
    {
        AiMode.Hybrid when saved == AiMode.Hybrid => "Remove the optional neural PC AI pack from this computer?\n\nBoth (PC + online) stays active using built-in basic local Smart Zoom.",
        AiMode.LocalOnly when saved == AiMode.LocalOnly => "Remove the optional neural PC AI pack from this computer?\n\nOn this PC only stays active using built-in basic local Smart Zoom.",
        _ => "Remove the optional neural PC AI pack from this computer? You can reinstall it later.",
    };

    private void BuildLocalAiUi(INameScope scope)
    {
        var expander = scope.Find("LocalAiExpander") as Control;
        var status = scope.Find("LocalAiStatusText") as TextBlock;
        var bar = scope.Find("LocalAiProgress") as ProgressBar;
        var install = scope.Find("LocalAiInstallBtn") as Button;
        var cancel = scope.Find("LocalAiCancelBtn") as Button;
        var remove = scope.Find("LocalAiRemoveBtn") as Button;
        if (status == null || install == null || remove == null) return;

        void Show()
        {
            var s = LocalAiPackService.Status();
            var det = LocalAiPack.CheckDetector(s.Folder, LocalAiPackManifest.IsPublished ? LocalAiPackManifest.Files : System.Array.Empty<LocalAiPackFile>(), s);
            bool busy = _localAiCts != null;
            if (expander != null)
                expander.IsVisible = true;
            SetStatusText(status, LocalAiStatusLine(s, AiSetup.Current(), AiSetup.PackSizeText(), det), s.State switch
            {
                LocalAiPackState.Damaged => StatusTone.Warning,
                LocalAiPackState.Ready when AiSetup.UsesPc(AiSetup.Current()) => StatusTone.Success,
                LocalAiPackState.Ready => StatusTone.Warning,
                _ => det.State == LocalDetectorState.Damaged ? StatusTone.Warning : StatusTone.Muted,
            });
            install.Content = "Reinstall";
            install.IsVisible = s.State == LocalAiPackState.Damaged;
            install.IsEnabled = !busy && LocalAiPackManifest.IsPublished;
            remove.IsVisible = s.State is LocalAiPackState.Ready or LocalAiPackState.Damaged;
            remove.IsEnabled = !busy;
            if (cancel != null) cancel.IsVisible = busy;
            if (bar != null) bar.IsVisible = busy;
        }
        _showLocalAiCard = Show;

        install.Click += async (_, _) =>
        {
            if (_localAiCts != null) return;
            _localAiCts = new CancellationTokenSource();
            Show();
            try
            {
                await LocalAiPackService.InstallAsync(p => Dispatcher.UIThread.Post(() =>
                {
                    if (bar != null && p.Total > 0) bar.Value = p.Done * 100.0 / p.Total;
                    SetStatusText(status, p.Problem ?? (p.Total > 0
                        ? $"Downloading… {(int)Math.Round(p.Done * 100.0 / p.Total)}%"
                        : $"Downloading {p.FileIndex} of {p.FileCount}…"), StatusTone.Muted);
                }), _localAiCts.Token);
                _localAiCts.Dispose();
                _localAiCts = null;
                Show();
            }
            catch (OperationCanceledException)
            {
                _localAiCts?.Dispose();
                _localAiCts = null;
                Show();
                SetStatusText(status, "Download stopped. What was downloaded is kept, so Reinstall continues from there.", StatusTone.Muted);
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException or UnauthorizedAccessException)
            {
                RuntimeLog.Fail("LocalAi", "Install failed: " + ex.Message);
                _localAiCts?.Dispose();
                _localAiCts = null;
                Show();
                SetStatusText(status, "The download failed. " + FreeVideoStudio.App.Infrastructure.UserFacingError.Describe(ex) + " What was downloaded is kept.", StatusTone.Danger);
            }
        };
        if (cancel != null) cancel.Click += (_, _) => _localAiCts?.Cancel();
        remove.Click += async (_, _) =>
        {
            var settings = SettingsManager.Instance;
            if (!await ConfirmDialogWindow.AskAsync(this,
                    LocalAiRemoveQuestion(settings.AiMode, AiSetup.HasStoredKey(settings)),
                    "Remove PC AI", "Remove", "Keep", destructive: true)) return;
            bool gone = await LocalAiPackService.RemoveAsync();
            if (gone) AiSetup.ApplyPackRemoved();
            Show();
            _showAiModeRow?.Invoke();
            if (!gone) SetStatusText(status, "Some files are in use. Restart Free Video Studio and press Remove again.", StatusTone.Warning);
        };
        Closed += (_, _) => _localAiCts?.Cancel();
        Show();
    }
}
