using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace FreeVideoStudio.App;

public partial class GranularSpeedEditorWindow
{
    public const string GuideLineTag = "SPEEDGUIDE_01";

    public const string FreezeRevealTag = "FREEZEREVEAL_01";

    internal const double FreezeGlowTotalMs = 2000;

    internal const double FreezeGlowEachMs = 600;

    internal static readonly string[] FreezePresetNames =
        { "FreezePreset05", "FreezePreset10", "FreezePreset15", "FreezePreset20", "FreezePreset25", "FreezePreset30" };

    private DispatcherTimer? _freezeGlowTimer;
    private DateTime _freezeGlowStartUtc;
    private bool _freezeAwaitingLength;
    private string? _lastGuide;

    internal enum GuideStep
    {
        MarkStart,
        MarkEnd,
        PickSpeed,
        PickFreezeLength,
        AimZoom,
        Done,
    }

    internal static GuideStep GuideStepFor(bool zoomMode, bool freezeAwaitingLength, bool pendingStart, bool blockSelected, int blockCount)
    {
        if (zoomMode) return GuideStep.AimZoom;
        if (pendingStart) return GuideStep.MarkEnd;
        if (blockSelected) return GuideStep.PickSpeed;
        if (freezeAwaitingLength) return GuideStep.PickFreezeLength;
        return blockCount == 0 ? GuideStep.MarkStart : GuideStep.Done;
    }

    internal static string GuideText(GuideStep step) => step switch
    {
        GuideStep.MarkEnd => "Now play to the end and press MARK END.",
        GuideStep.PickSpeed => "Pick a speed for this block.",
        GuideStep.PickFreezeLength => "Pick how long to freeze.",
        GuideStep.AimZoom => "Drag the box over what to zoom in on.",
        GuideStep.Done => "Add another block with MARK START, or press APPLY.",
        _ => "Play to a moment, then press MARK START.",
    };

    internal static double FreezePresetValue(int index) => 0.5 * (index + 1);

    internal static double FreezeGlowStartMs(int index)
        => index * (FreezeGlowTotalMs - FreezeGlowEachMs) / Math.Max(1, FreezePresetNames.Length - 1);

    internal static bool FreezeGlowLit(int index, double elapsedMs)
    {
        double start = FreezeGlowStartMs(index);
        return elapsedMs >= start && elapsedMs < start + FreezeGlowEachMs;
    }

    private void UpdateGuideLine()
    {
        if (this.FindControl<TextBlock>("GuideLineText") is not { } guide) return;
        var step = GuideStepFor(_zoomModeActive, _freezeAwaitingLength && _edit.FreezeTimeMs >= 0,
            _edit.PendingStartMs >= 0 && _edit.PendingEndMs < 0, _edit.HasSelectedSegment, _edit.Segments.Count);
        string text = GuideText(step);
        if (text == _lastGuide) return;
        _lastGuide = text;
        guide.Text = text;
    }

    private void UpdateFreezePresetsVisibility()
    {
        bool show = _edit.FreezeTimeMs >= 0;
        if (this.FindControl<StackPanel>("FreezePresetsPanel") is { } panel && panel.IsVisible != show) panel.IsVisible = show;
        if (this.FindControl<Border>("FreezePresetsDivider") is { } divider && divider.IsVisible != show) divider.IsVisible = show;
        if (!show)
        {
            _freezeAwaitingLength = false;
            StopFreezeGlow();
        }
    }

    private void StartFreezeGlow()
    {
        _freezeAwaitingLength = true;
        UpdateFreezePresetsVisibility();
        _freezeGlowStartUtc = DateTime.UtcNow;
        _freezeGlowTimer ??= CreateFreezeGlowTimer();
        _freezeGlowTimer.Stop();
        _freezeGlowTimer.Start();
        UpdateGuideLine();
    }

    private DispatcherTimer CreateFreezeGlowTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - _freezeGlowStartUtc).TotalMilliseconds;
            PaintFreezeGlow(elapsed);
            if (elapsed >= FreezeGlowTotalMs) StopFreezeGlow();
        };
        return timer;
    }

    private void PaintFreezeGlow(double elapsedMs)
    {
        var glowBg = Infrastructure.ThemeResources.Brush(this, "AppWarningBrush", new SolidColorBrush(Colors.Gold));
        var glowFg = Infrastructure.ThemeResources.Brush(this, "AppOnAccentTextBrush", Brushes.White);
        for (int i = 0; i < FreezePresetNames.Length; i++)
        {
            if (this.FindControl<Button>(FreezePresetNames[i]) is not { } b) continue;
            if (Math.Abs(_edit.SelectedFreezePresetS - FreezePresetValue(i)) < 0.01) continue;
            if (FreezeGlowLit(i, elapsedMs))
            {
                b.Background = glowBg;
                b.BorderBrush = glowBg;
                b.Foreground = glowFg;
            }
            else
            {
                b.ClearValue(Button.BackgroundProperty);
                b.ClearValue(Button.BorderBrushProperty);
                b.ClearValue(Button.ForegroundProperty);
            }
        }
    }

    private void StopFreezeGlow()
    {
        if (_freezeGlowTimer is not { IsEnabled: true } timer) return;
        timer.Stop();
        PaintFreezeGlow(double.MaxValue);
    }

    private void OnFreezeLengthChosen()
    {
        _freezeAwaitingLength = false;
        StopFreezeGlow();
        UpdateGuideLine();
    }
}
