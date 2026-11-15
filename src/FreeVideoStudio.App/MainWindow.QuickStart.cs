using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    internal const string DropCardClickTag = "DROPCLICK_01";
    internal const string KillQuickToggleTag = "KILLQUICK_01";
    internal const string AiHintTag = "AIHINT_01";
    internal const double KillToggleOffOpacity = 0.55;

    private bool _killToggleBusy;
    private Button? _killTrackerQuickToggle;
    private Button? _aiSetupHintButton;

    internal static string KillToggleText(bool on) => on ? "🎯 AI Kill Tracker: ON" : "🎯 AI Kill Tracker: OFF";

    private void WireEmptyStateControls()
    {
        Action<AppSettings> onCommit = _ => Dispatcher.UIThread.Post(() =>
        {
            SyncKillTrackerQuickToggle();
            SyncAiSetupHint();
        });
        SettingsManager.Committed += onCommit;
        Closed += (_, _) => SettingsManager.Committed -= onCommit;
    }

    private void OnUploadDropCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || sender is not Control card || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        RuntimeLog.Info("UI", $"{DropCardClickTag}: user clicked the drop card");
        OnUploadVideoClicked(card, new RoutedEventArgs());
    }

    private void OnKillTrackerQuickToggleLoaded(object? sender, RoutedEventArgs e)
    {
        _killTrackerQuickToggle = sender as Button;
        SyncKillTrackerQuickToggle();
    }

    private void OnKillTrackerQuickToggleClick(object? sender, RoutedEventArgs e)
        => _ = RunQuickStartActionAsync(OnKillTrackerQuickToggleAsync);

    private static async Task RunQuickStartActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void OnAiSetupHintLoaded(object? sender, RoutedEventArgs e)
    {
        _aiSetupHintButton = sender as Button;
        SyncAiSetupHint();
    }

    private void OnAiSetupHintClick(object? sender, RoutedEventArgs e)
        => _ = RunQuickStartActionAsync(OpenAiSetupFromHintAsync);

    private async Task OpenAiSetupFromHintAsync()
    {
        try
        {
            RuntimeLog.Info("AiSetup", $"{AiHintTag}: empty-screen hint opened the AI setup.");
            await Controls.AiModeWindow.EnsureSetUpAsync(this);
        }
        finally
        {
            SyncAiSetupHint();
            SyncKillTrackerQuickToggle();
        }
    }

    private void SyncAiSetupHint()
    {
        if (_aiSetupHintButton is not { } hint) return;
        bool ready;
        try { ready = AiSetup.CurrentIsComplete(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); ready = true; }
        hint.IsVisible = !ready;
    }

    private void SyncKillTrackerQuickToggle()
    {
        if (_killTrackerQuickToggle is not { } toggle) return;
        bool on = SettingsManager.Instance.AiEliminationTrackerEnabled;
        toggle.Content = KillToggleText(on);
        toggle.Opacity = on ? 1.0 : KillToggleOffOpacity;
        toggle.Effect = on
            ? new DropShadowEffect
            {
                Color = ThemeResources.Colour(toggle, "AppAccentColor", Color.FromRgb(96, 165, 250)),
                BlurRadius = 18,
                OffsetX = 0,
                OffsetY = 0,
                Opacity = 0.95,
            }
            : null;
        AutomationProperties.SetItemStatus(toggle, on ? "On" : "Off");
    }

    private async Task OnKillTrackerQuickToggleAsync()
    {
        if (_killToggleBusy) return;
        _killToggleBusy = true;
        try
        {
            bool turnOn = !SettingsManager.Instance.AiEliminationTrackerEnabled;
            if (turnOn)
            {
                var vendor = SettingsManager.Instance.GetUsageConfig(AiUsage.EliminationTracker).Vendor;
                if (!await Controls.AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, vendor, AiUsage.EliminationTracker))
                {
                    Controls.FloatingNotice.Info(this, "AI Kill Tracker stays off until an online AI is connected.");
                    return;
                }
            }
            SettingsManager.Update(s => s.AiEliminationTrackerEnabled = turnOn);
            RuntimeLog.Info("AIElimination", $"{KillQuickToggleTag}: quick switch set Mark my kills to {(turnOn ? "on" : "off")}.");
            if (turnOn)
                Controls.FloatingNotice.Success(this, "AI Kill Tracker is on. Open a video and your kills get a red ✕ on the timeline.");
            else
                Controls.FloatingNotice.Info(this, "AI Kill Tracker is off.");
        }
        finally
        {
            _killToggleBusy = false;
            SyncKillTrackerQuickToggle();
            SyncAiSetupHint();
        }
    }
}
