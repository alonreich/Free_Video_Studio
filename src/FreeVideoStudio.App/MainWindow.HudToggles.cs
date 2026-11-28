using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private string? _hudHoverKey;

    private void WireHudToggles()
    {
        _viewModel.HudToggledByUser += item =>
            SaveRecoveryState(label: $"toggle {item.Label}");
        _viewModel.HudTogglesChanged += () =>
        {
            UpdateHudPanelVisibility();
            _ = RefreshFinalLayoutPreviewAsync();
        };
        _viewModel.HudHoverChanged += key =>
        {
            _hudHoverKey = key;
            PhoneFrameCtl?.HighlightHudLayer(key);
        };

        if (this.FindControl<ItemsControl>("HudTogglesList") is { } list)
        {
            list.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
            {
                string? key = (e.Source as StyledElement)?.DataContext is ViewModels.HudToggleItem item ? item.Key : null;
                if (!string.Equals(key, _hudHoverKey, StringComparison.OrdinalIgnoreCase)) _viewModel.SetHudHover(key);
            }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            list.PointerExited += (_, _) => _viewModel.SetHudHover(null);
        }
        UpdateHudPanelVisibility();
    }

    private void UpdateHudPanelVisibility()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateHudPanelVisibility);
            return;
        }
        bool noMask = Infrastructure.MaskOverlayManager.IsNoMask(Infrastructure.SettingsManager.Instance.ActiveMaskOverlay);
        bool show = !noMask && _viewModel.HasHudToggles;
        var panel = this.FindControl<StackPanel>("InGameOverlaysPanel");
        if (panel != null) panel.IsVisible = show;
        var sep = this.FindControl<Separator>("InGameOverlaysSeparator");
        if (sep != null) sep.IsVisible = show;
    }

    internal static JsonObject WriteHudToggleState(IReadOnlyDictionary<string, bool> state)
    {
        var o = new JsonObject();
        foreach (var pair in state) o[pair.Key] = pair.Value;
        return o;
    }

    internal static IReadOnlyDictionary<string, bool>? ReadHudToggleState(JsonObject? o)
    {
        if (o == null) return null;
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in o)
        {
            if (pair.Value is JsonValue v && v.TryGetValue(out bool b)) map[pair.Key] = b;
        }
        return map.Count > 0 ? map : null;
    }
}
