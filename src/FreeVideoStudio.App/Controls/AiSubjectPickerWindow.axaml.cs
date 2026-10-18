using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

/// <summary>What the user decided in the subject picker (AIOWNBOX_01).</summary>
public enum AiPickerChoice
{
    Cancelled,
    Track,
    DrawOwnBox,
}

/// <summary>
/// UI-AIZOOM phase 3 — the subject picker. Each card's subject is outlined on the frame it is clearest
/// in (hover previews, click selects); the first subject is pre-selected; frames found by a forward
/// probe are labelled as being AFTER the segment (AILOOKAHEAD_01); "None of these" hands control back
/// so the user can draw their own box.
/// </summary>
public partial class AiSubjectPickerWindow : Window
{
    public DiscoveredSubject? SelectedSubject { get; private set; }

    public AiPickerChoice Choice { get; private set; } = AiPickerChoice.Cancelled;

    private readonly List<DiscoveredSubject> _subjects;
    private readonly List<Border> _cardBorders = new();
    private readonly Image?[] _images = new Image?[3];
    private readonly Canvas?[] _canvases = new Canvas?[3];
    private DiscoveredSubject? _currentSelected;
    private DiscoveredSubject? _hovered;

    public AiSubjectPickerWindow() : this(new AiDiscoveryResult(new List<DiscoveredSubject>(), "", "", "", 0, 0, 0, 0), 0, 0)
    {
    }

    /// <param name="segmentStartSec">The segment's SOURCE start, so the labels can say where each frame sits.</param>
    /// <param name="segmentDurationSec">The segment's length; frames past it are flagged.</param>
    public AiSubjectPickerWindow(AiDiscoveryResult discovery, double segmentStartSec, double segmentDurationSec)
    {
        _subjects = discovery.Subjects ?? new List<DiscoveredSubject>();
        InitializeComponent();
        var scope = this.FindNameScope();
        var ci = CultureInfo.InvariantCulture;

        string[] names = { "Start", "Middle", "End" };
        double[] times = { discovery.T1, discovery.T2, discovery.T3 };
        string[] paths = { discovery.F1, discovery.F2, discovery.F3 };
        for (int i = 0; i < 3; i++)
        {
            double rel = times[i] - segmentStartSec;
            string where = rel > segmentDurationSec + 0.05
                ? $"{(rel - segmentDurationSec).ToString("0.0", ci)}s AFTER the segment"
                : $"{rel.ToString("0.0", ci)}s into the segment";
            if (scope?.Find($"Frame{i + 1}TimeLabel") is TextBlock lbl)
                lbl.Text = $"Frame {i + 1} ({names[i]}) · {where}";
            _images[i] = scope?.Find($"Frame{i + 1}Image") as Image;
            _canvases[i] = scope?.Find($"Frame{i + 1}Canvas") as Canvas;
            LoadFrameImage(_images[i], paths[i]);
            if (_canvases[i] is { } c) c.SizeChanged += (_, _) => RedrawHighlights();
        }

        if (discovery.LookaheadOffsetSec > 0 && scope?.Find("LookaheadNoticeText") is TextBlock notice)
        {
            notice.Text = $"Nobody was visible inside your segment, so these frames come from later in the video (from {discovery.LookaheadOffsetSec.ToString("0.0", ci)}s after the segment start). "
                        + "If you pick someone here, you will be offered to extend the segment so the camera can reach them.";
            notice.IsVisible = true;
        }

        var container = scope?.Find("SubjectsContainer") as StackPanel;
        var confirmBtn = scope?.Find("ConfirmPickerBtn") as Button;
        var statusText = scope?.Find("SelectionStatusText") as TextBlock;

        if (scope?.Find("CancelPickerBtn") is Button cancelBtn)
            cancelBtn.Click += (_, _) => { Choice = AiPickerChoice.Cancelled; Close(false); };

        if (scope?.Find("NoneOfTheseBtn") is Button noneBtn)
            noneBtn.Click += (_, _) => { Choice = AiPickerChoice.DrawOwnBox; Close(false); };

        if (confirmBtn != null)
        {
            confirmBtn.Click += (_, _) =>
            {
                if (_currentSelected == null) return;
                SelectedSubject = _currentSelected;
                Choice = AiPickerChoice.Track;
                Close(true);
            };
        }

        if (container != null && _subjects.Count > 0)
        {
            for (int i = 0; i < _subjects.Count; i++)
            {
                var card = CreateSubjectCard(_subjects[i], i + 1, confirmBtn, statusText);
                _cardBorders.Add(card);
                container.Children.Add(card);
            }
            SelectSubject(_subjects[0], _cardBorders[0], confirmBtn, statusText);
        }
    }

    private static void LoadFrameImage(Image? img, string path)
    {
        if (img == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            img.Source = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    /// <summary>Outlines the selected subject (solid) and the hovered one (dashed) on the frame each is clearest in.</summary>
    private void RedrawHighlights()
    {
        foreach (Canvas? c in _canvases) c?.Children.Clear();
        Draw(_currentSelected, dashed: false);
        if (_hovered != null && _hovered != _currentSelected) Draw(_hovered, dashed: true);
    }

    private void Draw(DiscoveredSubject? sub, bool dashed)
    {
        if (sub == null) return;
        int i = Math.Clamp(sub.FrameIndex, 1, 3) - 1;
        if (_canvases[i] is not { } canvas || _images[i]?.Source is not Bitmap bmp) return;
        double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;
        const double margin = 4;
        double bw = bmp.Size.Width, bh = bmp.Size.Height;
        if (cw <= 2 * margin || ch <= 2 * margin || bw <= 0 || bh <= 0) return;

        double scale = Math.Min((cw - 2 * margin) / bw, (ch - 2 * margin) / bh);
        double ox = (cw - bw * scale) / 2, oy = (ch - bh * scale) / 2;
        double x = ox + sub.Xmin / 1000.0 * bw * scale;
        double y = oy + sub.Ymin / 1000.0 * bh * scale;
        double w = Math.Max(2, (sub.Xmax - sub.Xmin) / 1000.0 * bw * scale);
        double h = Math.Max(2, (sub.Ymax - sub.Ymin) / 1000.0 * bh * scale);

        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Stroke = ThemeResources.Brush(this, dashed ? "AppTextPrimaryBrush" : "AppAccentBrush", Brushes.White),
            StrokeThickness = dashed ? 2 : 3,
            StrokeDashArray = dashed ? new Avalonia.Collections.AvaloniaList<double> { 4, 3 } : null,
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
    }

    private Border CreateSubjectCard(DiscoveredSubject sub, int index, Button? confirmBtn, TextBlock? statusText)
    {
        var card = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12),
            MinWidth = 230,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Transparent),
            BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray)
        };

        var sp = new StackPanel { Spacing = 6 };
        var headerRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Width = 22,
            Height = 22,
            Background = ThemeResources.Brush(this, "AppAccentBrush", Brushes.MediumPurple)
        };
        badge.Child = new TextBlock
        {
            Text = index.ToString(CultureInfo.InvariantCulture),
            Foreground = ThemeResources.Brush(this, "AppOnAccentTextBrush", Brushes.White),
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        headerRow.Children.Add(badge);
        headerRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(sub.Label) ? $"Subject #{sub.Id}" : sub.Label,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White)
        });
        sp.Children.Add(headerRow);

        sp.Children.Add(new TextBlock
        {
            Text = $"Clearest in frame {Math.Clamp(sub.FrameIndex, 1, 3)}",
            FontSize = 11,
            Foreground = ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray)
        });
        sp.Children.Add(new TextBlock
        {
            Text = "CLICK TO SELECT",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeResources.Brush(this, "AppAccentBrush", Brushes.MediumPurple)
        });
        card.Child = sp;

        card.PointerEntered += (_, _) =>
        {
            _hovered = sub;
            if (_currentSelected != sub) card.BorderBrush = ThemeResources.Brush(this, "AppAccentBrush", Brushes.MediumPurple);
            RedrawHighlights();
        };
        card.PointerExited += (_, _) =>
        {
            if (_hovered == sub) _hovered = null;
            if (_currentSelected != sub) card.BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray);
            RedrawHighlights();
        };
        card.PointerPressed += (_, _) => SelectSubject(sub, card, confirmBtn, statusText);
        return card;
    }

    private void SelectSubject(DiscoveredSubject sub, Border selectedCard, Button? confirmBtn, TextBlock? statusText)
    {
        _currentSelected = sub;
        foreach (Border c in _cardBorders)
        {
            bool isThis = c == selectedCard;
            c.BorderBrush = ThemeResources.Brush(this, isThis ? "AppAccentBrush" : "AppBorderBrush", Brushes.Gray);
            c.Background = ThemeResources.Brush(this, isThis ? "AppSurfaceSubtleBrush" : "AppSurfaceBrush", Brushes.Transparent);
        }

        if (confirmBtn != null) confirmBtn.IsEnabled = true;
        if (statusText != null)
        {
            statusText.Text = $"Target: {(string.IsNullOrWhiteSpace(sub.Label) ? $"Subject #{sub.Id}" : sub.Label)}";
            statusText.Foreground = ThemeResources.Brush(this, "AppSuccessBrush", Brushes.ForestGreen);
        }
        RedrawHighlights();
    }
}
