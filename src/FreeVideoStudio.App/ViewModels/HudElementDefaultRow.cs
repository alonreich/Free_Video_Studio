using System.Collections.Generic;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.ViewModels;

public sealed class HudElementDefaultRow : ViewModelBase
{
    public static IReadOnlyList<string> BehaviorNames { get; } = new[] { "Always on", "Always off", "Remember my last choice" };

    private readonly DefaultValues _defaults;

    public HudElementDefaultRow(DefaultValues defaults, string profile, string key, string label)
    {
        _defaults = defaults;
        Profile = profile;
        Key = key;
        Label = label;
    }

    public string Profile { get; }

    public string Key { get; }

    public string Label { get; }

    public string Tip => $"\"{Label}\" in new videos with this profile.\n\nAlways on: starts shown.\nAlways off: starts hidden.\nRemember my last choice: starts the way you last switched it in the main window.";

    public IReadOnlyList<string> Behaviors => BehaviorNames;

    public static int ToIndex(CheckboxDefaultBehavior b) => b switch
    {
        CheckboxDefaultBehavior.AlwaysOn => 0,
        CheckboxDefaultBehavior.AlwaysOff => 1,
        _ => 2,
    };

    public static CheckboxDefaultBehavior FromIndex(int i) => i switch
    {
        0 => CheckboxDefaultBehavior.AlwaysOn,
        1 => CheckboxDefaultBehavior.AlwaysOff,
        _ => CheckboxDefaultBehavior.RememberLast,
    };

    public int BehaviorIndex
    {
        get => ToIndex(_defaults.HudBehavior(Profile, Key));
        set
        {
            if (value < 0 || value > 2 || value == BehaviorIndex) return;
            _defaults.SetHudBehavior(Profile, Key, FromIndex(value));
            OnPropertyChanged();
        }
    }
}
