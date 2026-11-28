using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.LocalAi;
using FreeVideoStudio.Core.Media;
using SkiaSharp;

namespace FreeVideoStudio.App.Controls;

public enum AiPickerChoice
{
    Cancelled,
    Track,
    SkipBlock,
    DescribeManually,
    ResampleMoments,
}

public enum FrameChoiceState
{
    Unanswered,
    Selected,
    NotVisible,
}

public sealed record AiCorrectionResult(
    bool Proceed,
    bool NotVisible,
    PixelBox? CorrectedBox,
    bool ReviewAnyway,
    (int Ymin, int Xmin, int Ymax, int Xmax)? GridBox = null);

public partial class AiSubjectPickerWindow : Window
{
    public const string EllipseGlowTag = "AIPICKERELLIPSE_01";

    public DiscoveredSubject? SelectedSubject { get; private set; }

    public AiPickerChoice Choice { get; private set; } = AiPickerChoice.Cancelled;

    public string ManualDescription { get; private set; } = string.Empty;

    public const string FocusViewTag = "AIPICKFOCUS_01";

    internal const string PickIntro = "Click what you want to follow in each picture where you can see it.";

    internal const string PickHeadline = "Step 1 of 2 · Mark what to follow";

    internal const string TrackButtonText = "🎯 Track";

    public double ChosenZoomStrength { get; private set; } = ViewModels.AiZoomTuning.DefaultStrength;

    private readonly Bitmap?[] _fullBitmaps = new Bitmap?[3];

    private StackPanel? _container;

    private readonly List<DiscoveredSubject> _subjects;
    private readonly List<Border> _cardBorders = new();
    private readonly Image?[] _images = new Image?[3];
    private readonly Canvas?[] _canvases = new Canvas?[3];
    private readonly Border?[] _frameBorders = new Border?[3];
    private readonly TextBlock?[] _badges = new TextBlock?[3];
    private readonly FrameChoiceState[] _frameStates = new FrameChoiceState[3];
    private readonly DiscoveredSubject?[] _anchors = new DiscoveredSubject?[3];
    private readonly string[] _framePaths;
    private readonly double[] _frameTimes;
    private readonly double _segmentStartSec;
    private readonly double _segmentDurationSec;
    private readonly AiGameProfile _profile;
    private readonly bool _isLocalFreeMode;
    private readonly string? _previousDescription;
    private readonly string _batchPrefix;
    private DiscoveredSubject? _currentSelected;
    private DiscoveredSubject? _hovered;

    public AiSubjectPickerWindow() : this(new AiDiscoveryResult(new List<DiscoveredSubject>(), "", "", "", 0, 0, 0, 0), 0, 0)
    {
    }

    public AiSubjectPickerWindow(
        AiDiscoveryResult discovery,
        double segmentStartSec,
        double segmentDurationSec,
        bool isLocalFreeMode = false,
        AiGameProfile profile = AiGameProfile.ThirdPerson,
        string? batchContextHeader = null,
        bool? allowDescribeManually = null,
        string? previousDescription = null,
        DiscoveredSubject? initialSubject = null,
        bool portrait = false,
        string? videoPath = null,
        int sourceWidth = 0,
        int sourceHeight = 0)
    {
        _portrait = portrait;
        _videoPath = videoPath;
        _sourceW = sourceWidth > 0 ? sourceWidth : 1920;
        _sourceH = sourceHeight > 0 ? sourceHeight : 1080;
        _subjects = discovery.Subjects != null ? new List<DiscoveredSubject>(discovery.Subjects) : new List<DiscoveredSubject>();
        _framePaths = new[] { discovery.F1, discovery.F2, discovery.F3 };
        _frameTimes = new[] { discovery.T1, discovery.T2, discovery.T3 };
        _segmentStartSec = segmentStartSec;
        _segmentDurationSec = segmentDurationSec;
        _isLocalFreeMode = isLocalFreeMode;
        _profile = profile;
        _previousDescription = previousDescription;
        _batchPrefix = string.IsNullOrWhiteSpace(batchContextHeader) ? string.Empty : $"{batchContextHeader.Trim()} · ";

        InitializeComponent();
        DialogFit.Apply(this, drag: true);
        Closed += (_, _) => OnPickerClosed();
        var scope = this.FindNameScope();

        Title = $"{_batchPrefix}AI Smart Zoom — Choose Who to Follow";

        if (scope?.Find("PickerWindowTitleText") is TextBlock titleLbl)
            titleLbl.Text = $"{_batchPrefix}AI SMART ZOOM — CHOOSE WHO TO FOLLOW";

        if (scope?.Find("FallbackNoticeText") is TextBlock fallbackLbl)
        {
            var notes = new List<string>();
            if (discovery.UsedLocalFallback && !string.IsNullOrWhiteSpace(discovery.FallbackReason))
                notes.Add(discovery.FallbackReason!);
            if ((_isLocalFreeMode || discovery.UsedLocalFallback) && discovery.DetectorStatus is { IsInstalled: false } det)
                notes.Add(det.Message);
            if (notes.Count > 0)
            {
                fallbackLbl.Text = string.Join(" ", notes);
                fallbackLbl.IsVisible = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(batchContextHeader) && scope?.Find("SkipBlockBtn") is Button skipBtn)
        {
            skipBtn.IsVisible = true;
            skipBtn.Click += (_, _) => { Choice = AiPickerChoice.SkipBlock; Close(false); };
        }

        var container = scope?.Find("SubjectsContainer") as StackPanel;
        _container = container;
        var confirmBtn = scope?.Find("ConfirmPickerBtn") as Button;
        var statusText = scope?.Find("SelectionStatusText") as TextBlock;
        var resampleBtn = scope?.Find("ResampleBtn") as Button;

        string[] names = { "Start", "Middle", "End" };
        for (int i = 0; i < 3; i++)
        {
            int frameIdx = i + 1;
            if (scope?.Find($"Frame{frameIdx}TimeLabel") is TextBlock lbl)
                lbl.Text = $"Picture {frameIdx} ({names[i]})";

            _images[i] = scope?.Find($"Frame{frameIdx}Image") as Image;
            _canvases[i] = scope?.Find($"Frame{frameIdx}Canvas") as Canvas;
            _frameBorders[i] = scope?.Find($"Frame{frameIdx}Border") as Border;
            _badges[i] = scope?.Find($"Frame{frameIdx}Badge") as TextBlock;
            _viewports[i] = scope?.Find($"Frame{frameIdx}Viewport") as Border;
            _zoomSliders[i] = scope?.Find($"Frame{frameIdx}ZoomSlider") as Slider;
            _nextImageBtns[i] = scope?.Find($"Frame{frameIdx}NextImageBtn") as Button;

            _fullBitmaps[i] = LoadFrameBitmap(_framePaths[i]);
            if (_images[i] is { } frameImg) frameImg.Source = _fullBitmaps[i];
            _frameZoom[i] = Math.Clamp(DefaultFrameZoom, MinZoomOf(i), MaxFrameZoom);
            WireFrameView(i, scope?.Find($"Frame{frameIdx}HitGrid") as Grid, container);

            if (scope?.Find($"Frame{frameIdx}ClearBtn") is Button clearBtn)
            {
                clearBtn.Click += (_, _) => OnFrameClearClicked(frameIdx);
            }
        }

        Opened += (_, _) =>
        {
            for (int k = 0; k < 3; k++) LayoutFrame(k);
            RedrawHighlights();
        };

        double saved = SettingsManager.Instance.AiZoomBaseScale > 0 ? SettingsManager.Instance.AiZoomBaseScale : ViewModels.AiZoomTuning.DefaultStrength;
        ChosenZoomStrength = Math.Clamp(saved, ViewModels.AiZoomTuning.MinStrength, ViewModels.AiZoomTuning.MaxStrength);
        WireHowClose();

        if (scope?.Find("CancelPickerBtn") is Button cancelBtn)
            cancelBtn.Click += (_, _) => { Choice = AiPickerChoice.Cancelled; Close(false); };

        if (scope?.Find("DescribeManuallyBtn") is Button describeBtn)
        {
            bool canDescribe = allowDescribeManually ?? !_isLocalFreeMode;
            describeBtn.IsVisible = canDescribe;
            describeBtn.Click += async (_, _) =>
            {
                string? entered = await AskManualDescriptionAsync(this, _previousDescription);
                if (!string.IsNullOrWhiteSpace(entered))
                {
                    ManualDescription = entered;
                    Choice = AiPickerChoice.DescribeManually;
                    Close(true);
                }
            };
        }

        if (resampleBtn != null)
        {
            resampleBtn.Click += (_, _) =>
            {
                Choice = AiPickerChoice.ResampleMoments;
                Close(false);
            };
        }

        if (confirmBtn != null)
        {
            confirmBtn.Click += (_, _) =>
            {
                var firstSelected = _anchors.FirstOrDefault(a => a != null) ?? _currentSelected;
                if (firstSelected != null)
                {
                    SelectedSubject = firstSelected;
                    SelectedSubject.AdditionalAnchors = _anchors.Where(a => a != null && a != firstSelected).ToList()!;
                }
                if (firstSelected == null) return;
                Choice = AiPickerChoice.Track;
                Close(true);
            };
        }

        if (container != null && _subjects.Count > 0)
        {
            for (int i = 0; i < _subjects.Count; i++)
            {
                var card = CreateSubjectCard(_subjects[i], i + 1);
                _cardBorders.Add(card);
                container.Children.Add(card);
            }
        }

        if (initialSubject != null)
        {
            int initFrame = Math.Clamp(initialSubject.FrameIndex, 1, 3);
            _frameStates[initFrame - 1] = FrameChoiceState.Selected;
            _anchors[initFrame - 1] = initialSubject;
            _currentSelected = initialSubject;
            SelectedSubject = initialSubject;
            UpdateFrameBadge(initFrame);
            if (initialSubject.AdditionalAnchors is { Count: > 0 } extras)
            {
                foreach (var extra in extras)
                {
                    int extraFrame = Math.Clamp(extra.FrameIndex, 1, 3);
                    _frameStates[extraFrame - 1] = FrameChoiceState.Selected;
                    _anchors[extraFrame - 1] = extra;
                    UpdateFrameBadge(extraFrame);
                }
            }
        }
        else if (_subjects.Count > 0)
        {
            var initial = _subjects[0];
            int initFrame = Math.Clamp(initial.FrameIndex, 1, 3);
            _frameStates[initFrame - 1] = FrameChoiceState.Selected;
            _anchors[initFrame - 1] = initial;
            _currentSelected = initial;
            SelectedSubject = initial;
            UpdateFrameBadge(initFrame);
        }

        UpdateWizardState();
    }

    private void OnFrameClearClicked(int frameIndex)
    {
        int idx = Math.Clamp(frameIndex, 1, 3) - 1;
        _frameStates[idx] = FrameChoiceState.Unanswered;
        _anchors[idx] = null;
        if (_currentSelected != null && _currentSelected.FrameIndex == frameIndex)
        {
            _currentSelected = _anchors.FirstOrDefault(a => a != null);
            SelectedSubject = _currentSelected;
        }
        ResetFrameZoom(idx);
        UpdateFrameBadge(frameIndex);
        RedrawHighlights();
        UpdateWizardState();
    }

    private void UpdateFrameBadge(int frameIndex)
    {
        int idx = Math.Clamp(frameIndex, 1, 3) - 1;
        var badge = _badges[idx];
        var border = _frameBorders[idx];
        if (badge == null || border == null) return;

        switch (_frameStates[idx])
        {
            case FrameChoiceState.Selected:
                badge.Text = "✓ Selected";
                badge.Foreground = ThemeResources.Brush(this, "AppSuccessBrush", Brushes.ForestGreen);
                border.BorderBrush = ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red);
                break;
            case FrameChoiceState.NotVisible:
                badge.Text = "Not visible";
                badge.Foreground = ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray);
                border.BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray);
                break;
            default:
                badge.Text = "Waiting for selection";
                badge.Foreground = ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray);
                border.BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray);
                break;
        }
    }

    private void UpdateWizardState()
    {
        var scope = this.FindNameScope();
        var confirmBtn = scope?.Find("ConfirmPickerBtn") as Button;
        var statusText = scope?.Find("SelectionStatusText") as TextBlock;
        var resampleBtn = scope?.Find("ResampleBtn") as Button;
        var headline = scope?.Find("PickerHeadlineText") as TextBlock;
        var intro = scope?.Find("PickerIntroText") as TextBlock;

        int selectedCount = _frameStates.Count(s => s == FrameChoiceState.Selected);
        if (headline != null) headline.Text = $"{_batchPrefix}{PickHeadline}";
        if (intro != null) intro.Text = PickIntro;
        if (resampleBtn != null) resampleBtn.IsVisible = false;
        if (confirmBtn != null)
        {
            confirmBtn.Content = TrackButtonText;
            confirmBtn.IsEnabled = selectedCount > 0;
        }
        if (statusText != null)
        {
            if (selectedCount == 0)
            {
                statusText.Text = PickIntro;
                statusText.Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White);
            }
            else
            {
                statusText.Text = selectedCount == 3
                    ? "✓ Marked in all 3 pictures. Press 🎯 Track."
                    : $"✓ Marked in {selectedCount} of 3 pictures. Mark the others too, or press 🎯 Track.";
                statusText.Foreground = ThemeResources.Brush(this, "AppSuccessBrush", Brushes.ForestGreen);
            }
        }
        UpdateFinalShot();
    }

    internal void OnFrameClicked(int frameIndex, PointerPressedEventArgs e, StackPanel? container)
    {
        int i = Math.Clamp(frameIndex, 1, 3) - 1;
        if (_fullBitmaps[i] is null || _layouts[i] is not { } l || _viewports[i] is not { } vp) return;
        Point pt = e.GetPosition(vp);
        if (pt.X < 0 || pt.Y < 0 || pt.X > l.ViewW || pt.Y > l.ViewH) return;
        double nx = (pt.X - l.OffsetX) / l.Width, ny = (pt.Y - l.OffsetY) / l.Height;
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return;
        int clickX1000 = (int)Math.Round(nx * 1000.0);
        int clickY1000 = (int)Math.Round(ny * 1000.0);

        byte[] rgb = LocalTrackingService.DecodeJpegToRgb(_framePaths[i], 320, 180);
        int nextId = _subjects.Count + 1;
        var snapped = LocalSubjectDiscovery.SnapAtPoint(rgb, 320, 180, frameIndex, clickX1000, clickY1000, nextId, _profile);
        var added = new DiscoveredSubject
        {
            Id = snapped.Id,
            Label = $"Target on Picture {frameIndex}",
            FrameIndex = snapped.FrameIndex,
            ExactTimeSec = Math.Clamp(_frameTimes[i] - _segmentStartSec, 0, _segmentDurationSec),
            Ymin = snapped.Ymin,
            Xmin = snapped.Xmin,
            Ymax = snapped.Ymax,
            Xmax = snapped.Xmax,
        };

        if (_anchors[frameIndex - 1] is { } oldAnchor)
        {
            int oldIdx = _subjects.IndexOf(oldAnchor);
            if (oldIdx >= 0) _subjects.RemoveAt(oldIdx);
        }
        _anchors[frameIndex - 1] = added;
        _frameStates[frameIndex - 1] = FrameChoiceState.Selected;
        _currentSelected = added;
        SelectedSubject = added;
        _subjects.Insert(0, added);

        var card = CreateSubjectCard(added, 1);
        _cardBorders.Insert(0, card);
        container?.Children.Insert(0, card);

        ZoomToMarked(i, added);
        UpdateFrameBadge(frameIndex);
        RedrawHighlights();
        UpdateWizardState();
    }

    private static Bitmap? LoadFrameBitmap(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
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

    private static Bitmap? RenderThumbnailWithRedOval(string path, DiscoveredSubject sub, Bitmap? fallback)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return fallback;
        try
        {
            using var src = SKBitmap.Decode(path);
            if (src == null || src.Width <= 0 || src.Height <= 0) return fallback;
            using var surface = SKSurface.Create(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (surface == null) return fallback;
            var canvas = surface.Canvas;
            canvas.DrawBitmap(src, 0, 0);

            float x1 = sub.Xmin / 1000f * src.Width;
            float y1 = sub.Ymin / 1000f * src.Height;
            float x2 = sub.Xmax / 1000f * src.Width;
            float y2 = sub.Ymax / 1000f * src.Height;
            float w = Math.Max(16f, x2 - x1);
            float h = Math.Max(16f, y2 - y1);
            float padX = Math.Max(2f, w * 0.04f);
            float padY = Math.Max(2f, h * 0.04f);
            var rect = new SKRect(
                Math.Max(2f, x1 - padX),
                Math.Max(2f, y1 - padY),
                Math.Min(src.Width - 2f, x1 + w + padX),
                Math.Min(src.Height - 2f, y1 + h + padY));

            using (var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(255, 18, 18, 48),
                IsAntialias = true,
            })
            {
                canvas.DrawOval(rect, fillPaint);
            }

            using (var glowPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(6f, src.Width * 0.012f),
                Color = new SKColor(255, 18, 18, 120),
                IsAntialias = true,
            })
            {
                canvas.DrawOval(rect, glowPaint);
            }

            using (var strokePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(3f, src.Width * 0.006f),
                Color = new SKColor(255, 18, 18, 255),
                IsAntialias = true,
            })
            {
                canvas.DrawOval(rect, strokePaint);
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            using var ms = new MemoryStream(data.ToArray());
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return fallback;
        }
    }

    internal void RedrawHighlights()
    {
        foreach (Canvas? c in _canvases) c?.Children.Clear();
        for (int idx = 0; idx < _subjects.Count; idx++)
        {
            var sub = _subjects[idx];
            if (sub == _currentSelected || _anchors.Contains(sub) || sub == _hovered) continue;
            Draw(sub, idx + 1, isSelected: false, isHovered: false);
        }
        for (int a = 0; a < _anchors.Length; a++)
        {
            if (_anchors[a] is { } anchor && anchor != _currentSelected)
            {
                int aIdx = _subjects.IndexOf(anchor) + 1;
                Draw(anchor, Math.Max(1, aIdx), isSelected: true, isHovered: _hovered == anchor, isSecondaryAnchor: true);
            }
        }
        if (_hovered != null && _hovered != _currentSelected && !_anchors.Contains(_hovered))
        {
            int hovIdx = _subjects.IndexOf(_hovered) + 1;
            Draw(_hovered, Math.Max(1, hovIdx), isSelected: false, isHovered: true);
        }
        if (_currentSelected != null)
        {
            int selIdx = _subjects.IndexOf(_currentSelected) + 1;
            Draw(_currentSelected, Math.Max(1, selIdx), isSelected: true, isHovered: _hovered == _currentSelected);
        }
        DrawFinalShotBoxes();
        UpdateFinalShot();
    }

    private void Draw(DiscoveredSubject? sub, int displayIndex, bool isSelected, bool isHovered, bool isSecondaryAnchor = false)
    {
        if (sub == null) return;
        int i = Math.Clamp(sub.FrameIndex, 1, 3) - 1;
        if (_canvases[i] is not { } canvas || _fullBitmaps[i] is null || _layouts[i] is not { } l) return;
        double cw = l.ViewW, ch = l.ViewH;
        double x = l.OffsetX + sub.Xmin / 1000.0 * l.Width;
        double y = l.OffsetY + sub.Ymin / 1000.0 * l.Height;
        double w = Math.Max(18, (sub.Xmax - sub.Xmin) / 1000.0 * l.Width);
        double h = Math.Max(18, (sub.Ymax - sub.Ymin) / 1000.0 * l.Height);

        double padX = Math.Max(2, w * 0.04);
        double padY = Math.Max(2, h * 0.04);
        double ex = x - padX;
        double ey = y - padY;
        double ew = w + 2 * padX;
        double eh = h + 2 * padY;
        if (ex + ew < 0 || ey + eh < 0 || ex > cw || ey > ch) return;

        var brightRedBrush = new SolidColorBrush(Color.FromRgb(255, 18, 18));

        if (isSelected || isHovered)
        {
            var outerHalo = new Ellipse
            {
                Width = ew + 12,
                Height = eh + 12,
                Stroke = Brushes.White,
                StrokeThickness = 2.0,
                Opacity = isSelected ? 0.95 : 0.6,
            };
            Canvas.SetLeft(outerHalo, ex - 6);
            Canvas.SetTop(outerHalo, ey - 6);
            canvas.Children.Add(outerHalo);

            var glowEllipse = new Ellipse
            {
                Width = ew + 8,
                Height = eh + 8,
                Stroke = brightRedBrush,
                StrokeThickness = isSelected ? 8 : 6,
                Opacity = isSelected ? 0.6 : 0.35,
                Fill = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)55 : (byte)28, 255, 18, 18)),
            };
            Canvas.SetLeft(glowEllipse, ex - 4);
            Canvas.SetTop(glowEllipse, ey - 4);
            canvas.Children.Add(glowEllipse);
        }

        var ellipse = new Ellipse
        {
            Width = ew,
            Height = eh,
            Stroke = brightRedBrush,
            StrokeThickness = isSelected ? 3.5 : (isHovered ? 2.8 : 1.6),
            Opacity = isSelected ? 1.0 : (isHovered ? 0.9 : 0.5),
            StrokeDashArray = !isSelected ? new Avalonia.Collections.AvaloniaList<double> { 5, 3 } : null,
        };
        Canvas.SetLeft(ellipse, ex);
        Canvas.SetTop(ellipse, ey);
        canvas.Children.Add(ellipse);

        if (!isSelected && !isHovered) return;
        var pill = new Border
        {
            Background = brightRedBrush,
            BorderBrush = isSelected ? Brushes.White : null,
            BorderThickness = isSelected ? new Thickness(1.5) : new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            Opacity = isSelected || isHovered ? 1.0 : 0.75,
            Child = new TextBlock
            {
                Text = isSelected ? "🎯 TARGET" : TruncateLabel(sub.Label, 22),
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize11"),
            }
        };
        Canvas.SetLeft(pill, Math.Clamp(ex, 2, Math.Max(2, cw - 110)));
        Canvas.SetTop(pill, Math.Clamp(ey - 26, 2, Math.Max(2, ch - 26)));
        canvas.Children.Add(pill);
    }

    private static string TruncateLabel(string? label, int maxLen)
    {
        string s = string.IsNullOrWhiteSpace(label) ? "Target" : label.Trim();
        return s.Length <= maxLen ? s : s[..maxLen] + "…";
    }

    private static BoxShadows CardGlow(bool active)
        => active
            ? new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 0,
                Blur = 18,
                Spread = 2,
                Color = Color.FromArgb(195, 239, 68, 68),
            })
            : default;

    private Border CreateSubjectCard(DiscoveredSubject sub, int index)
    {
        var card = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Width = 236,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Transparent),
            BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray),
        };

        var sp = new StackPanel { Spacing = 6 };
        int frameIdx = Math.Clamp(sub.FrameIndex, 1, 3);
        var thumbBorder = new Border
        {
            Height = 96,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = ThemeResources.Brush(this, "AppScrimBrush", Brushes.Black),
            BorderBrush = ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
        };
        var thumbGrid = new Grid();
        var fallbackBmp = _fullBitmaps[frameIdx - 1];
        var thumbImg = new Image
        {
            Stretch = Stretch.Uniform,
            Source = RenderThumbnailWithRedOval(_framePaths[frameIdx - 1], sub, fallbackBmp),
        };
        var thumbCanvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        thumbCanvas.SizeChanged += (_, _) => DrawThumbnailEllipse(thumbCanvas, fallbackBmp, sub);
        thumbGrid.Children.Add(thumbImg);
        thumbGrid.Children.Add(thumbCanvas);
        thumbBorder.Child = thumbGrid;
        sp.Children.Add(thumbBorder);

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(11),
            Width = 22,
            Height = 22,
            Background = ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red)
        };
        badge.Child = new TextBlock
        {
            Text = index.ToString(CultureInfo.InvariantCulture),
            Foreground = ThemeResources.Brush(this, "AppOnAccentTextBrush", Brushes.White),
            FontWeight = FontWeight.Bold,
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize11"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerRow.Children.Add(badge);
        headerRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(sub.Label) ? $"Option #{sub.Id}" : sub.Label,
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize13"),
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White)
        });
        sp.Children.Add(headerRow);

        card.Child = sp;

        card.PointerEntered += (_, _) =>
        {
            _hovered = sub;
            card.BorderBrush = ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red);
            card.BoxShadow = CardGlow(true);
            RedrawHighlights();
        };
        card.PointerExited += (_, _) =>
        {
            _hovered = null;
            bool isSel = sub == _currentSelected;
            card.BorderBrush = ThemeResources.Brush(this, isSel ? "AppDangerBrush" : "AppBorderBrush", Brushes.Gray);
            card.BoxShadow = CardGlow(isSel);
            RedrawHighlights();
        };
        card.PointerPressed += (_, _) =>
        {
            _currentSelected = sub;
            SelectedSubject = sub;
            int fIdx = Math.Clamp(sub.FrameIndex, 1, 3);
            _anchors[fIdx - 1] = sub;
            _frameStates[fIdx - 1] = FrameChoiceState.Selected;
            UpdateFrameBadge(fIdx);
            RedrawHighlights();
            UpdateWizardState();
        };

        return card;
    }

    private void DrawThumbnailEllipse(Canvas canvas, Bitmap? bmp, DiscoveredSubject sub)
    {
        canvas.Children.Clear();
        if (bmp == null) return;
        double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;
        double bw = bmp.Size.Width, bh = bmp.Size.Height;
        if (cw <= 4 || ch <= 4 || bw <= 0 || bh <= 0) return;

        double scale = Math.Min(cw / bw, ch / bh);
        double ox = (cw - bw * scale) / 2.0, oy = (ch - bh * scale) / 2.0;
        double x = ox + sub.Xmin / 1000.0 * bw * scale;
        double y = oy + sub.Ymin / 1000.0 * bh * scale;
        double w = Math.Max(10, (sub.Xmax - sub.Xmin) / 1000.0 * bw * scale);
        double h = Math.Max(10, (sub.Ymax - sub.Ymin) / 1000.0 * bh * scale);

        double padX = Math.Max(2, w * 0.04);
        double padY = Math.Max(2, h * 0.04);
        double ex = Math.Max(0, x - padX);
        double ey = Math.Max(0, y - padY);
        double ew = Math.Min(cw - ex, w + 2 * padX);
        double eh = Math.Min(ch - ey, h + 2 * padY);

        var redBrush = new SolidColorBrush(Color.FromRgb(255, 18, 18));
        var glow = new Ellipse
        {
            Width = ew + 4,
            Height = eh + 4,
            Stroke = redBrush,
            StrokeThickness = 4,
            Opacity = 0.35,
            Fill = new SolidColorBrush(Color.FromArgb(32, 255, 18, 18)),
        };
        Canvas.SetLeft(glow, Math.Max(0, ex - 2));
        Canvas.SetTop(glow, Math.Max(0, ey - 2));
        canvas.Children.Add(glow);

        var ellipse = new Ellipse
        {
            Width = ew,
            Height = eh,
            Stroke = redBrush,
            StrokeThickness = 2.0,
        };
        Canvas.SetLeft(ellipse, ex);
        Canvas.SetTop(ellipse, ey);
        canvas.Children.Add(ellipse);
    }
}
