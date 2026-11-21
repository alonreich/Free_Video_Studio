using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

public enum AiZoomStartChoice
{
    Cancel,
    Reframe,
    FollowBox,
    FindForMe,
    LocalSelect,
    MultiBlock,
}

public sealed record AiZoomStartOptions(
    bool CanReframe,
    bool HasBox,
    bool BoxRunsOnThisPc,
    string? PreviousDescription,
    string FollowBoxCost,
    string FindCost,
    AiMode Mode = AiMode.Hybrid,
    bool NeuralPackReady = false,
    int EligibleMultiBlockCount = 0,
    bool CanUseCloudDiscovery = true,
    string? MultiBlockSummary = null,
    bool HasWorkingKey = true);

public sealed record AiZoomStartResult(AiZoomStartChoice Choice, string Description);

public sealed class AiZoomStartWindow : Window
{
    public const string FixTag = "AIZOOMSTART_01";
    public const string FreeLine = "Free. Nothing is sent online.";
    public const string CloudTrackerHeading = "Cloud AI Tracker";
    public const string LocalSelectHeading = "Let me select (free, runs on this PC)";

    private readonly List<(RadioButton Radio, AiZoomStartChoice Choice, string Cost)> _choices = new();
    private readonly TextBlock _cost;

    public AiZoomStartResult Result { get; private set; } = new(AiZoomStartChoice.Cancel, string.Empty);

    public static AiZoomStartChoice DefaultChoice(AiZoomStartOptions o)
    {
        if (o.HasBox) return AiZoomStartChoice.FollowBox;
        if (o.CanReframe) return AiZoomStartChoice.Reframe;
        bool hasCloud = o.CanUseCloudDiscovery && o.HasWorkingKey && AiSetup.UsesOnline(o.Mode) && !string.IsNullOrWhiteSpace(o.FindCost);
        if (hasCloud) return AiZoomStartChoice.FindForMe;
        if (o.HasWorkingKey && AiSetup.UsesOnline(o.Mode) && !string.IsNullOrWhiteSpace(o.FindCost) && !o.BoxRunsOnThisPc)
            return AiZoomStartChoice.FindForMe;
        return AiZoomStartChoice.LocalSelect;
    }

    public AiZoomStartWindow(AiZoomStartOptions options)
    {
        Title = "AI Smart Zoom";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Black);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = DialogChrome.TitleBarHeight;

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 8 };

        if (options.CanReframe)
        {
            Add(root, AiZoomStartChoice.Reframe,
                "Re-aim existing AI zoom",
                "Instant · " + FreeLine);
        }

        if (options.HasBox)
        {
            Add(root, AiZoomStartChoice.FollowBox,
                "Follow my zoom box",
                string.IsNullOrWhiteSpace(options.FollowBoxCost) ? FreeLine : options.FollowBoxCost);
        }

        bool showCloudDiscovery = options.CanUseCloudDiscovery && options.HasWorkingKey
            && (AiSetup.UsesOnline(options.Mode) || (options.Mode == AiMode.NotChosen && !string.IsNullOrWhiteSpace(options.FindCost)));
        if (showCloudDiscovery)
        {
            string findCost = string.IsNullOrWhiteSpace(options.FindCost)
                ? "Sends 3 sample frames to your cloud AI to mark selectable targets."
                : options.FindCost;
            Add(root, AiZoomStartChoice.FindForMe,
                CloudTrackerHeading,
                findCost);
        }

        Add(root, AiZoomStartChoice.LocalSelect,
            LocalSelectHeading,
            FreeLine);

        if (options.EligibleMultiBlockCount >= 2)
        {
            Add(root, AiZoomStartChoice.MultiBlock,
                $"Zoom all {options.EligibleMultiBlockCount} blocks…",
                "Review estimate before tracking. One Undo reverts the whole batch.");
        }

        _cost = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        };
        root.Children.Add(_cost);

        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Classes.Add("Secondary");
        var start = new Button { Content = "Start", IsDefault = true, MinWidth = 110 };
        start.Classes.Add("Primary");
        cancel.Click += (_, _) => Close();
        start.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        root.Children.Add(buttons);

        AiZoomStartChoice preset = DefaultChoice(options);
        foreach (var c in _choices)
        {
            c.Radio.IsCheckedChanged += (_, _) => ShowSelection();
            if (c.Choice == preset) c.Radio.IsChecked = true;
        }
        if (_choices.TrueForAll(c => c.Radio.IsChecked != true) && _choices.Count > 0)
            _choices[0].Radio.IsChecked = true;
        ShowSelection();

        Content = DialogChrome.Wrap(this, "AI Smart Zoom", root);
        DialogFit.Apply(this);
    }

    private RadioButton Add(StackPanel root, AiZoomStartChoice choice, string heading, string cost)
    {
        var label = new TextBlock
        {
            Text = heading,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        };
        var radio = new RadioButton
        {
            GroupName = "AiZoomStart",
            Content = label,
            Padding = new Thickness(6, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        Avalonia.Automation.AutomationProperties.SetName(radio, heading);
        _choices.Add((radio, choice, cost));
        root.Children.Add(radio);
        return radio;
    }

    private AiZoomStartChoice Selected()
    {
        foreach (var c in _choices)
            if (c.Radio.IsChecked == true) return c.Choice;
        return AiZoomStartChoice.Cancel;
    }

    private void ShowSelection()
    {
        AiZoomStartChoice selected = Selected();
        foreach (var c in _choices)
            if (c.Choice == selected) _cost.Text = c.Cost;
    }

    private void Accept()
    {
        AiZoomStartChoice selected = Selected();
        if (selected == AiZoomStartChoice.Cancel) return;
        Result = new AiZoomStartResult(selected, string.Empty);
        Close();
    }

    public static async Task<AiZoomStartResult> AskAsync(Window owner, AiZoomStartOptions options)
    {
        var dlg = new AiZoomStartWindow(options);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }
}
