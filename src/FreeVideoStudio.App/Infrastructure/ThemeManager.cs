using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace FreeVideoStudio.App.Infrastructure;

public static class ThemeManager
{
    private static double _currentFontMultiplier = 1.0;

    public static void ApplyFromSettings()
    {
        var settings = SettingsManager.Instance;
        ApplyTheme(settings.ThemeMode);
        ApplyFontScale(settings.FontScale);
    }

    public static void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current == null) return;

        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    public static void ApplyFontScale(FontScale scale)
    {
        _currentFontMultiplier = scale.ToMultiplier();
        ApplyFontSizeTokens();
        ApplyFontMultiplierToAllWindows();
    }

    private static readonly (string Key, double Base)[] s_scaledTokens =
    {
        ("AppFontSizeSmall", 10), ("AppFontSizeBase", 11), ("AppFontSizeNormal", 13),
        ("AppFontSize9", 9), ("AppFontSize10", 10), ("AppFontSize11", 11),
        ("AppFontSize12", 12), ("AppFontSize13", 13), ("AppFontSize14", 14),
        ("AppFontSize16", 16), ("AppFontSize18", 18), ("AppFontSize20", 20),
        ("AppFontSize24", 24),
        ("AppFontSize28", 28), ("AppFontSize36", 36), ("AppFontSize48", 48),
        ("AppFontSize60", 60),
        ("AppButtonMinHeight", 32), ("AppSpeedPresetMinWidth", 31),
        ("AppSpeedPresetMinHeight", 25), ("AppCloseBtnSize", 32),
        ("AppWizStepDotSize", 24), ("AppWizConnectorWidth", 10),
        ("AppZoomStripTextMaxWidth", 46),
    };

    private static void ApplyFontSizeTokens()
    {
        if (Application.Current is null) return;
        var res = Application.Current.Resources;
        foreach (var (key, baseValue) in s_scaledTokens)
        {
            res[key] = Math.Round(baseValue * _currentFontMultiplier, 1);
        }
    }

    public static double ScaledFontSize(double baseSize) => Math.Round(baseSize * _currentFontMultiplier, 1);

    private static void ApplyFontMultiplierToAllWindows()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                ApplyFontMultiplierToWindow(window);
            }
        }
    }

    public static void ApplyFontMultiplierToWindow(Window window)
    {
        window.FontSize = Math.Round(13.0 * _currentFontMultiplier, 1);
    }
}