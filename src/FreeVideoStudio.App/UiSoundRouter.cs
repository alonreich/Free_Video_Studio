using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace FreeVideoStudio.App;

internal static class UiSoundRouter
{
    private static readonly HashSet<string> Silent = new(StringComparer.Ordinal)
    {
        "MicRecordButton"
    };

    private static readonly HashSet<string> MarkNames = new(StringComparer.Ordinal)
    {
        "MarkStartButton", "MarkEndButton",
        "MarkStartBtn", "MarkEndBtn"
    };

    private static readonly HashSet<string> OpenNames = new(StringComparer.Ordinal)
    {
        "VideoMergerButton", "CropSettingsButton", "GranularButton",
        "AddMusicButton", "VoiceOverButton", "DetachOverlayButton",
        "MenuVideoMerger", "MenuCropSettings", "MenuSettingsBtn", "MenuSettings",
        "MenuTogglePreviewMonitor"
    };

    private static readonly HashSet<string> ApplyNames = new(StringComparer.Ordinal)
    {
        "RdpFixButton"
    };

    private static readonly HashSet<string> CloseNames = new(StringComparer.Ordinal)
    {
        "MenuExit", "MenuReturnToApp",
        "ReturnButton", "ReturnToMainAppButton",
        "CloseButton", "CancelButton", "CancelBtn"
    };

    public static UiCue? Resolve(object? sender)
    {
        if (sender is not Control control) return null;

        string name = control.Name ?? string.Empty;

        if (name.Length > 0 && Silent.Contains(name)) return null;

        if (control is MenuItem mi && mi.ItemCount > 0) return null;

        if (control is Button btn && btn.Flyout != null) return UiCue.Click;

        if (name.StartsWith("Confirm", StringComparison.Ordinal)) return UiCue.Close;

        if (name.Length > 0)
        {
            if (MarkNames.Contains(name)) return UiCue.Mark;
            if (OpenNames.Contains(name)) return UiCue.Open;
            if (ApplyNames.Contains(name)) return UiCue.Click;
            if (CloseNames.Contains(name)) return UiCue.Close;
        }

        if (control.Classes.Contains("Danger")) return UiCue.Close;

        return UiCue.Click;
    }
}
