using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.LocalAi;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public partial class AiSubjectPickerWindow
{
    public static async Task<AiCorrectionResult> AskCorrectionAsync(
        Window owner,
        string framePath,
        double uncertainTimeSec,
        AiGameProfile profile,
        int sourceWidth = 0,
        int sourceHeight = 0)
    {
        var dlg = new Window
        {
            Title = "AI Smart Zoom — Check Tracking",
            Width = 720,
            Height = 620,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ThemeResources.Brush(owner, "AppSurfaceBrush", Brushes.Black),
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = DialogChrome.TitleBarHeight,
        };

        var root = new StackPanel { Margin = new Thickness(24, 16, 24, 18), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = "Stage 2 of 3 · Check your selections",
            FontWeight = FontWeight.Bold,
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize18"),
            Foreground = ThemeResources.Brush(owner, "AppTextPrimaryBrush", Brushes.White),
        });

        root.Children.Add(new Border
        {
            Background = ThemeResources.Brush(owner, "AppSurfaceSubtleBrush", Brushes.Transparent),
            BorderBrush = ThemeResources.Brush(owner, "AppBorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = new TextBlock
            {
                Text = "We lost track here. Click the same person or object, or choose Not visible.",
                FontWeight = FontWeight.SemiBold,
                [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize13"),
                Foreground = ThemeResources.Brush(owner, "AppTextPrimaryBrush", Brushes.White),
            }
        });

        var frameBorder = new Border
        {
            Height = 360,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = ThemeResources.Brush(owner, "AppScrimBrush", Brushes.Black),
            BorderBrush = ThemeResources.Brush(owner, "AppBorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1.5),
        };

        var grid = new Grid { Cursor = new Cursor(StandardCursorType.Hand) };
        var img = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        LoadFrameImage(img, framePath);
        var canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        grid.Children.Add(img);
        grid.Children.Add(canvas);
        frameBorder.Child = grid;
        root.Children.Add(frameBorder);

        PixelBox? clickedBox = null;
        (int Ymin, int Xmin, int Ymax, int Xmax)? clickedGridBox = null;
        var trackBtn = new Button { Content = "APPLY CORRECTION & TRACK", MinWidth = 180, MinHeight = 38, IsEnabled = false };
        trackBtn.Classes.Add("Success");

        void DrawCorrectionOval(PixelBox box)
        {
            canvas.Children.Clear();
            if (img.Source is not Bitmap bmp) return;
            double cw = grid.Bounds.Width, ch = grid.Bounds.Height;
            const double margin = 4;
            double bw = bmp.Size.Width, bh = bmp.Size.Height;
            if (cw <= 2 * margin || ch <= 2 * margin || bw <= 0 || bh <= 0) return;

            double scale = Math.Min((cw - 2 * margin) / bw, (ch - 2 * margin) / bh);
            double ox = (cw - bw * scale) / 2, oy = (ch - bh * scale) / 2;
            double x = ox + box.X1 * scale;
            double y = oy + box.Y1 * scale;
            double w = Math.Max(18, box.Width * scale);
            double h = Math.Max(18, box.Height * scale);

            var brightRedBrush = new SolidColorBrush(Color.FromRgb(255, 18, 18));
            var halo = new Ellipse { Width = w + 10, Height = h + 10, Stroke = Brushes.White, StrokeThickness = 2.0, Opacity = 0.95 };
            Canvas.SetLeft(halo, Math.Max(0, x - 5));
            Canvas.SetTop(halo, Math.Max(0, y - 5));
            canvas.Children.Add(halo);

            var glow = new Ellipse { Width = w + 6, Height = h + 6, Stroke = brightRedBrush, StrokeThickness = 6, Opacity = 0.5, Fill = new SolidColorBrush(Color.FromArgb(45, 255, 18, 18)) };
            Canvas.SetLeft(glow, Math.Max(0, x - 3));
            Canvas.SetTop(glow, Math.Max(0, y - 3));
            canvas.Children.Add(glow);

            var oval = new Ellipse { Width = w, Height = h, Stroke = brightRedBrush, StrokeThickness = 3.0 };
            Canvas.SetLeft(oval, x);
            Canvas.SetTop(oval, y);
            canvas.Children.Add(oval);
        }

        grid.PointerPressed += (_, e) =>
        {
            if (img.Source is not Bitmap bmp) return;
            double cw = grid.Bounds.Width, ch = grid.Bounds.Height;
            const double margin = 4;
            double bw = bmp.Size.Width, bh = bmp.Size.Height;
            if (cw <= 2 * margin || ch <= 2 * margin || bw <= 0 || bh <= 0) return;

            double scale = Math.Min((cw - 2 * margin) / bw, (ch - 2 * margin) / bh);
            double ox = (cw - bw * scale) / 2.0, oy = (ch - bh * scale) / 2.0;
            Point pt = e.GetPosition(grid);
            int clickX1000 = (int)Math.Round(Math.Clamp((pt.X - ox) / (bw * scale) * 1000.0, 0, 1000));
            int clickY1000 = (int)Math.Round(Math.Clamp((pt.Y - oy) / (bh * scale) * 1000.0, 0, 1000));

            byte[] rgb = LocalTrackingService.DecodeJpegToRgb(framePath, 320, 180);
            var snapped = LocalSubjectDiscovery.SnapAtPoint(rgb, 320, 180, 2, clickX1000, clickY1000, 1, profile);
            clickedGridBox = (snapped.Ymin, snapped.Xmin, snapped.Ymax, snapped.Xmax);
            int targetW = sourceWidth > 0 ? sourceWidth : (int)bw;
            int targetH = sourceHeight > 0 ? sourceHeight : (int)bh;
            clickedBox = LocalTrackingMath.FromGrid(snapped.Ymin, snapped.Xmin, snapped.Ymax, snapped.Xmax, targetW, targetH);
            var displayBox = LocalTrackingMath.FromGrid(snapped.Ymin, snapped.Xmin, snapped.Ymax, snapped.Xmax, (int)bw, (int)bh);
            DrawCorrectionOval(displayBox);
            trackBtn.IsEnabled = true;
        };

        AiCorrectionResult finalResult = new(Proceed: false, NotVisible: false, CorrectedBox: null, ReviewAnyway: false);

        var cancelBtn = new Button { Content = "CANCEL", MinWidth = 90, MinHeight = 38, IsCancel = true };
        cancelBtn.Classes.Add("Secondary");
        cancelBtn.Click += (_, _) => { finalResult = new(Proceed: false, NotVisible: false, CorrectedBox: null, ReviewAnyway: false); dlg.Close(); };

        var notVisBtn = new Button { Content = "NOT VISIBLE", MinWidth = 110, MinHeight = 38 };
        notVisBtn.Classes.Add("Secondary");
        notVisBtn.Click += (_, _) => { finalResult = new(Proceed: true, NotVisible: true, CorrectedBox: null, ReviewAnyway: false); dlg.Close(); };

        var reviewBtn = new Button { Content = "REVIEW ANYWAY", MinWidth = 130, MinHeight = 38 };
        reviewBtn.Classes.Add("Secondary");
        reviewBtn.Click += (_, _) => { finalResult = new(Proceed: true, NotVisible: false, CorrectedBox: null, ReviewAnyway: true); dlg.Close(); };

        trackBtn.Click += (_, _) =>
        {
            if (clickedBox != null)
            {
                finalResult = new(Proceed: true, NotVisible: false, CorrectedBox: clickedBox, ReviewAnyway: false, GridBox: clickedGridBox);
                dlg.Close();
            }
        };

        var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        btnBar.Children.Add(cancelBtn);
        btnBar.Children.Add(notVisBtn);
        btnBar.Children.Add(reviewBtn);
        btnBar.Children.Add(trackBtn);
        root.Children.Add(btnBar);

        dlg.Content = DialogChrome.Wrap(dlg, "AI SMART ZOOM — TRACKING CORRECTION", root);
        DialogFit.Apply(dlg, owner);
        await dlg.ShowDialog(owner);
        return finalResult;
    }

    public static async Task<string?> AskManualDescriptionAsync(Window owner, string? initialDescription = null)
    {
        var dlg = new Window
        {
            Title = "Describe Who to Follow",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ThemeResources.Brush(owner, "AppSurfaceBrush", Brushes.Black),
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = DialogChrome.TitleBarHeight,
        };

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = "Describe the character or object you want Cloud AI to mark in these pictures:",
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush(owner, "AppTextPrimaryBrush", Brushes.White),
        });
        root.Children.Add(new Border
        {
            Background = ThemeResources.Brush(owner, "AppSurfaceSubtleBrush", Brushes.Transparent),
            BorderBrush = ThemeResources.Brush(owner, "AppBorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = new TextBlock
            {
                Text = "Helpful hints — mention color, outfit, item, or where they appear in the picture:\n"
                     + "• \"Opponent in red jacket on the roof\"\n"
                     + "• \"Player gliding down from the upper right\"\n"
                     + "• \"White sports car in the middle lane\"\n"
                     + "• \"Football player #10 in blue jersey\"",
                TextWrapping = TextWrapping.Wrap,
                [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize11"),
                Foreground = ThemeResources.Brush(owner, "AppTextSecondaryBrush", Brushes.Gray),
            }
        });

        var box = new TextBox
        {
            Text = initialDescription ?? string.Empty,
            Watermark = "Type who or what to follow (e.g. opponent in red jacket on roof)…",
            MaxLength = AiTrackingPrompts.MaxDescriptionLength,
        };
        Avalonia.Automation.AutomationProperties.SetName(box, "Describe who to follow");
        root.Children.Add(box);

        string? submitted = null;
        var cancel = new Button { Content = "CANCEL", MinWidth = 100, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Classes.Add("Secondary");
        var search = new Button { Content = "SEARCH FRAMES", MinWidth = 140, IsDefault = true, IsEnabled = !string.IsNullOrWhiteSpace(box.Text) };
        search.Classes.Add("Primary");
        box.TextChanged += (_, _) => search.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);
        cancel.Click += (_, _) => dlg.Close();
        search.Click += (_, _) =>
        {
            submitted = box.Text?.Trim();
            if (!string.IsNullOrEmpty(submitted)) dlg.Close();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(search);
        root.Children.Add(buttons);

        dlg.Content = DialogChrome.Wrap(dlg, "Describe Who to Follow", root);
        DialogFit.Apply(dlg, owner);
        dlg.Opened += (_, _) =>
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        };
        await dlg.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(submitted) ? null : submitted;
    }
}
