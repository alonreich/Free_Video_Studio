using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

public readonly record struct HudToggleDensity(double SwitchScale, double RowMinHeight, double BaseFontSize);

public sealed partial class MainViewModel
{
    public const string HudTeamKey = "team";
    public const string HudSpectatingKey = "spectating";

    private bool _applyingHud;
    private bool _hudTogglesEnabled;

    public ObservableCollection<HudToggleItem> HudToggles { get; } = new();

    public string? HudProfileName { get; private set; }

    public Func<string, IReadOnlyList<HudElementInfo>> HudElementSource { get; set; } = MaskOverlayManager.ReadProfileElements;

    public Action<string, string, bool>? HudChoiceRecorder { get; set; } = HudChoiceMemory.Record;

    public event Action? HudTogglesChanged;

    public event Action<string?>? HudHoverChanged;

    public event Action<HudToggleItem>? HudToggledByUser;

    public bool HasHudToggles => HudToggles.Count > 0;

    public string HudToggleSummary => HudToggles.Count == 0 ? string.Empty : $"{HudToggles.Count(t => t.IsOn)} of {HudToggles.Count} on";

    public bool HudTogglesEnabled
    {
        get => _hudTogglesEnabled;
        set
        {
            if (!SetProperty(ref _hudTogglesEnabled, value)) return;
            foreach (HudToggleItem item in HudToggles) item.IsEnabled = value;
        }
    }

    public static HudToggleDensity DensityFor(int count) => count switch
    {
        <= 5 => new HudToggleDensity(1.0, 36, 11),
        <= 8 => new HudToggleDensity(0.8, 28, 10),
        _ => new HudToggleDensity(0.66, 24, 9.5),
    };

    public HudToggleItem? FindHudToggle(string key)
        => HudToggles.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public HudVisibility HudVisibility => HudToggles.Count == 0
        ? HudVisibility.FromLegacy(_isTeammates, _isSpectating)
        : new HudVisibility(HudToggles.Where(t => !t.IsOn).Select(t => t.Key));

    public IReadOnlyDictionary<string, bool> HudToggleState
        => HudToggles.ToDictionary(t => t.Key, t => t.IsOn, StringComparer.OrdinalIgnoreCase);

    public void ReloadHudToggles(string? profile, bool resetToDefaults, IReadOnlyDictionary<string, bool>? saved = null)
    {
        IReadOnlyList<HudElementInfo> elements = Array.Empty<HudElementInfo>();
        if (!string.IsNullOrWhiteSpace(profile) && !MaskOverlayManager.IsNoMask(profile))
        {
            try { elements = HudElementSource(profile!) ?? Array.Empty<HudElementInfo>(); }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                RuntimeLog.Swallowed(ex);
            }
        }

        bool sameProfile = string.Equals(profile, HudProfileName, StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, bool>? previous = !resetToDefaults && sameProfile ? HudToggleState : null;
        var defaults = SettingsManager.Instance.Defaults;
        var density = DensityFor(elements.Count);

        _applyingHud = true;
        try
        {
            foreach (HudToggleItem old in HudToggles) old.Toggled -= OnHudItemToggled;
            HudToggles.Clear();
            foreach (HudElementInfo e in elements)
            {
                bool on = saved != null && saved.TryGetValue(e.Key, out bool s) ? s
                    : previous != null && previous.TryGetValue(e.Key, out bool p) ? p
                    : defaults.StartHudElement(profile!, e.Key);
                var item = new HudToggleItem(e.Key, e.Label, on)
                {
                    IsEnabled = _hudTogglesEnabled,
                    SwitchScale = density.SwitchScale,
                    RowMinHeight = density.RowMinHeight,
                    FontSize = ThemeManager.ScaledFontSize(density.BaseFontSize),
                };
                item.Toggled += OnHudItemToggled;
                HudToggles.Add(item);
            }
            HudProfileName = profile;
            SyncLegacyHudFields();
        }
        finally { _applyingHud = false; }

        RuntimeLog.Info("UI", $"{HudElements.Tag}: '{profile}' has {HudToggles.Count} HUD switch(es) ({HudVisibility}).");
        OnPropertyChanged(nameof(HasHudToggles));
        OnPropertyChanged(nameof(HudToggleSummary));
        HudTogglesChanged?.Invoke();
    }

    public void ApplyHudToggleState(IReadOnlyDictionary<string, bool>? saved)
    {
        if (saved == null || saved.Count == 0) return;
        _applyingHud = true;
        try
        {
            foreach (HudToggleItem item in HudToggles)
                if (saved.TryGetValue(item.Key, out bool on)) item.IsOn = on;
            SyncLegacyHudFields();
        }
        finally { _applyingHud = false; }
        OnPropertyChanged(nameof(HudToggleSummary));
        HudTogglesChanged?.Invoke();
    }

    internal void SetHudToggleProgrammatic(string key, bool value)
    {
        if (FindHudToggle(key) is not { } item) return;
        bool was = _applyingHud;
        _applyingHud = true;
        try { item.IsOn = value; }
        finally { _applyingHud = was; }
    }

    public void SetHudHover(string? key)
    {
        foreach (HudToggleItem item in HudToggles)
            item.IsHighlighted = key != null && string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase);
        HudHoverChanged?.Invoke(key);
    }

    private void SyncLegacyHudFields()
    {
        if (FindHudToggle(HudTeamKey) is { } team && _isTeammates != team.IsOn) { _isTeammates = team.IsOn; OnPropertyChanged(nameof(IsTeammates)); }
        if (FindHudToggle(HudSpectatingKey) is { } spec && _isSpectating != spec.IsOn) { _isSpectating = spec.IsOn; OnPropertyChanged(nameof(IsSpectating)); }
    }

    private void OnHudItemToggled(HudToggleItem item)
    {
        SyncLegacyHudFields();
        OnPropertyChanged(nameof(HudToggleSummary));
        if (!_applyingHud)
        {
            if (!string.IsNullOrWhiteSpace(HudProfileName)) HudChoiceRecorder?.Invoke(HudProfileName!, item.Key, item.IsOn);
            RuntimeLog.Info("UI", $"{HudElements.Tag}: '{item.Label}' switched {(item.IsOn ? "on" : "off")}.");
            NotifyStateDirty();
        }
        HudTogglesChanged?.Invoke();
        if (!_applyingHud) HudToggledByUser?.Invoke(item);
    }
}
