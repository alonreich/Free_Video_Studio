using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    DrawBox,
}

public sealed record AiZoomStartOptions(
    bool CanReframe,
    bool HasBox,
    bool BoxRunsOnThisPc,
    string? PreviousDescription,
    string FollowBoxCost,
    string FindCost);

public sealed record AiZoomStartResult(AiZoomStartChoice Choice, string Description);

public sealed class AiZoomStartWindow : Window
{
    public const string FixTag = "AIZOOMSTART_01";
    public const string FreeLine = "Free. Nothing is sent online.";

    private readonly List<(RadioButton Radio, AiZoomStartChoice Choice, string Cost)> _choices = new();
    private readonly TextBox _description;
    private readonly StackPanel _descriptionPanel;
    private readonly TextBlock _cost;

    public AiZoomStartResult Result { get; private set; } = new(AiZoomStartChoice.Cancel, string.Empty);

    public static AiZoomStartChoice DefaultChoice(AiZoomStartOptions o)
        => o.CanReframe ? AiZoomStartChoice.Reframe : o.HasBox ? AiZoomStartChoice.FollowBox : AiZoomStartChoice.FindForMe;

    public AiZoomStartWindow(AiZoomStartOptions options)
    {
        Title = "AI Smart Zoom";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Black);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = DialogChrome.TitleBarHeight;

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        root.Children.Add(new TextBlock
        {
            Text = "What should the camera do?",
            FontWeight = FontWeight.Bold,
            FontSize = 18,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        });
        root.Children.Add(new TextBlock
        {
            Text = "Nothing changes in your video until you accept the preview.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = ThemeResources.Brush(this, "AppTextSecondaryBrush", Brushes.Gray),
        });

        if (options.CanReframe)
            Add(root, AiZoomStartChoice.Reframe, "Just re-aim the camera",
                "Keeps the tracking you already have and applies your current Zoom strength. Instant.", FreeLine);
        if (options.HasBox)
            Add(root, AiZoomStartChoice.FollowBox, "Follow the box I drew",
                options.BoxRunsOnThisPc ? "Follows what is inside your zoom box, on this PC." : "Follows what is inside your zoom box. Starts right away.",
                options.FollowBoxCost);
        Add(root, AiZoomStartChoice.FindForMe, "Find who to follow for me",
            "The AI looks at 3 pictures of this part and shows you who it found. You pick one.", options.FindCost);
        if (!options.HasBox)
            Add(root, AiZoomStartChoice.DrawBox, "Let me aim a box myself",
                "A box appears on the video. Aim it at who to follow, then press FOLLOW THIS BOX.", FreeLine);

        _description = new TextBox
        {
            Text = options.PreviousDescription ?? string.Empty,
            MaxLength = AiTrackingPrompts.MaxDescriptionLength,
            Watermark = "e.g. the player on the roof, the red car",
        };
        _description.Classes.Add("FormInput");
        ToolTip.SetTip(_description, "Optional. Say who you mean in your own words and the AI lists the best match first. Leave it empty to see everyone it finds.");
        _descriptionPanel = new StackPanel { Spacing = 4, Margin = new Thickness(28, 0, 0, 0) };
        _descriptionPanel.Children.Add(new TextBlock
        {
            Text = "Who should it follow? (optional)",
            FontSize = 11,
            Foreground = ThemeResources.Brush(this, "AppTextSecondaryBrush", Brushes.Gray),
        });
        _descriptionPanel.Children.Add(_description);
        root.Children.Add(_descriptionPanel);

        _cost = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
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
        _description.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        root.Children.Add(buttons);

        AiZoomStartChoice preset = DefaultChoice(options);
        foreach (var c in _choices)
        {
            c.Radio.IsCheckedChanged += (_, _) => ShowSelection();
            if (c.Choice == preset) c.Radio.IsChecked = true;
        }
        ShowSelection();

        Content = DialogChrome.Wrap(this, "AI Smart Zoom", root);
        Opened += (_, _) =>
        {
            if (Selected() == AiZoomStartChoice.FindForMe) { _description.Focus(); _description.SelectAll(); }
        };
    }

    private void Add(StackPanel root, AiZoomStartChoice choice, string heading, string line, string cost)
    {
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock
        {
            Text = heading,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        });
        body.Children.Add(new TextBlock
        {
            Text = line,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush(this, "AppTextSecondaryBrush", Brushes.Gray),
        });
        var radio = new RadioButton
        {
            GroupName = "AiZoomStart",
            Content = body,
            Padding = new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Avalonia.Automation.AutomationProperties.SetName(radio, heading);
        _choices.Add((radio, choice, cost));
        root.Children.Add(radio);
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
        _descriptionPanel.IsVisible = selected == AiZoomStartChoice.FindForMe;
        foreach (var c in _choices)
            if (c.Choice == selected) _cost.Text = c.Cost;
    }

    private void Accept()
    {
        AiZoomStartChoice selected = Selected();
        if (selected == AiZoomStartChoice.Cancel) return;
        Result = new AiZoomStartResult(selected,
            selected == AiZoomStartChoice.FindForMe ? AiTrackingPrompts.CleanDescription(_description.Text) : string.Empty);
        Close();
    }

    public static async Task<AiZoomStartResult> AskAsync(Window owner, AiZoomStartOptions options)
    {
        var dlg = new AiZoomStartWindow(options);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }
}
