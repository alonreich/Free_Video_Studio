using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

public sealed record AiMultiBlockSelectionResult(
    IReadOnlyList<int> SelectedSegmentIndices,
    bool UseLocalFreeDiscovery,
    string? TargetDescription);

public sealed class AiMultiBlockZoomWindow : Window
{
    private readonly IReadOnlyList<MultiBlockEntry> _entries;
    private readonly List<(MultiBlockEntry Entry, CheckBox Check)> _rows = new();
    private readonly TextBlock _summaryText;
    private readonly CheckBox? _localFreeCheck;
    private readonly Button _startBtn;
    private readonly AiMode _mode;
    private readonly AiVendor _u1Vendor;
    private readonly string _u1Model;
    private readonly AiVendor _u2Vendor;
    private readonly string _u2Model;
    private readonly string _u1Key;
    private readonly string _u2Key;

    public AiMultiBlockSelectionResult? Result { get; private set; }

    public AiMultiBlockZoomWindow(
        IReadOnlyList<MultiBlockEntry> entries,
        AiMode mode,
        bool neuralPackReady,
        AiVendor u1Vendor,
        string u1Model,
        AiVendor u2Vendor,
        string u2Model,
        string u1Key,
        string u2Key,
        string? previousDescription)
    {
        _entries = entries;
        _mode = mode;
        _u1Vendor = u1Vendor;
        _u1Model = u1Model;
        _u2Vendor = u2Vendor;
        _u2Model = u2Model;
        _u1Key = u1Key;
        _u2Key = u2Key;

        Title = "AI Smart Zoom — Multi-Block Setup";
        Width = 640;
        MaxHeight = 720;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Black);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = DialogChrome.TitleBarHeight;

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = "Follow a target in each block",
            FontWeight = FontWeight.Bold,
            [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppFontSize18"),
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        });
        root.Children.Add(new TextBlock
        {
            Text = "Each block tracks its own target and stays inside one clip/segment. Existing zooms stay unchecked unless you opt in to replace them. Nothing changes until you press ACCEPT & APPLY.",
            TextWrapping = TextWrapping.Wrap,
            [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppFontSize12"),
            Foreground = ThemeResources.Brush(this, "AppTextSecondaryBrush", Brushes.Gray),
        });

        var selectActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pickEmptyBtn = new Button { Content = "Select blocks without zoom", Classes = { "Secondary" } };
        var pickAllBtn = new Button { Content = "Select all eligible (incl. replace)", Classes = { "Secondary" } };
        var clearBtn = new Button { Content = "Clear all", Classes = { "Secondary" } };
        selectActions.Children.Add(pickEmptyBtn);
        selectActions.Children.Add(pickAllBtn);
        selectActions.Children.Add(clearBtn);
        root.Children.Add(selectActions);

        var listStack = new StackPanel { Spacing = 6 };
        foreach (var entry in _entries)
        {
            var cb = new CheckBox
            {
                IsEnabled = entry.IsEligible,
                IsChecked = entry.IncludeByDefault,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var textStack = new StackPanel { Spacing = 1 };
            textStack.Children.Add(new TextBlock
            {
                Text = $"Block #{entry.SegmentIndex + 1} · {MultiBlockZoomPlan.FormatRange(entry.Segment)} · {entry.Segment.Speed:0.0}x",
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeResources.Brush(this, entry.IsEligible ? "AppTextPrimaryBrush" : "AppTextMutedBrush", Brushes.White),
            });
            textStack.Children.Add(new TextBlock
            {
                Text = entry.StatusText,
                [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppFontSize11"),
                Foreground = ThemeResources.Brush(this,
                    entry.State is MultiBlockZoomState.HasAiZoom or MultiBlockZoomState.HasManualZoom
                        ? "AppWarningBrush"
                        : entry.IsEligible ? "AppTextSecondaryBrush" : "AppTextMutedBrush",
                    Brushes.Gray),
            });
            cb.Content = textStack;
            cb.IsCheckedChanged += (_, _) => UpdateSummary();
            _rows.Add((entry, cb));

            var card = new Border
            {
                BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Background = ThemeResources.Brush(this, "AppSurfaceSubtleBrush", Brushes.Transparent),
                Child = cb,
            };
            listStack.Children.Add(card);
        }

        var scroller = new ScrollViewer
        {
            MaxHeight = 250,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = listStack,
        };
        root.Children.Add(scroller);

        if (AiSetup.UsesOnline(mode))
        {
            _localFreeCheck = new CheckBox
            {
                Content = neuralPackReady
                    ? "Use free PC tracking instead of cloud AI (zero cloud calls)"
                    : "Use free PC tracking (built-in basic scan/tracker, zero cloud calls)",
                IsChecked = false,
            };
            _localFreeCheck.IsCheckedChanged += (_, _) => UpdateSummary();
            root.Children.Add(_localFreeCheck);
        }

        _summaryText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
        };
        root.Children.Add(_summaryText);

        var cancelBtn = new Button { Content = "Cancel", IsCancel = true, Classes = { "Secondary" } };
        _startBtn = new Button { Content = "Start selected blocks", IsDefault = true, MinWidth = 170, Classes = { "Primary" } };
        cancelBtn.Click += (_, _) => Close();
        _startBtn.Click += (_, _) =>
        {
            var picked = SelectedEntries().Select(e => e.SegmentIndex).ToList();
            if (picked.Count == 0) return;
            bool localFree = _localFreeCheck?.IsChecked == true || !AiSetup.UsesOnline(_mode);
            Result = new AiMultiBlockSelectionResult(picked, localFree, null);
            Close();
        };

        pickEmptyBtn.Click += (_, _) =>
        {
            foreach (var r in _rows)
                if (r.Entry.IsEligible) r.Check.IsChecked = r.Entry.State == MultiBlockZoomState.ReadyNoZoom;
            UpdateSummary();
        };
        pickAllBtn.Click += (_, _) =>
        {
            foreach (var r in _rows)
                if (r.Entry.IsEligible) r.Check.IsChecked = true;
            UpdateSummary();
        };
        clearBtn.Click += (_, _) =>
        {
            foreach (var r in _rows)
                r.Check.IsChecked = false;
            UpdateSummary();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(_startBtn);
        root.Children.Add(buttons);

        UpdateSummary();
        Content = DialogChrome.Wrap(this, "Multi-Block AI Smart Zoom", root);
        DialogFit.Apply(this);
    }

    private List<MultiBlockEntry> SelectedEntries()
        => _rows.Where(r => r.Entry.IsEligible && r.Check.IsChecked == true).Select(r => r.Entry).ToList();

    private void UpdateSummary()
    {
        var selected = SelectedEntries();
        bool localFree = _localFreeCheck?.IsChecked == true || !AiSetup.UsesOnline(_mode);
        var est = MultiBlockZoomPlan.EstimateBatch(selected, _mode, localFree, _u1Vendor, _u1Model, _u2Vendor, _u2Model, _u1Key, _u2Key);
        _summaryText.Text = est.PlainSummary;
        _startBtn.IsEnabled = selected.Count > 0;
    }

    public static async Task<AiMultiBlockSelectionResult?> PickAsync(
        Window owner,
        IReadOnlyList<MultiBlockEntry> entries,
        AiMode mode,
        bool neuralPackReady,
        AiVendor u1Vendor,
        string u1Model,
        AiVendor u2Vendor,
        string u2Model,
        string u1Key,
        string u2Key,
        string? previousDescription)
    {
        var dlg = new AiMultiBlockZoomWindow(entries, mode, neuralPackReady, u1Vendor, u1Model, u2Vendor, u2Model, u1Key, u2Key, previousDescription);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }
}
