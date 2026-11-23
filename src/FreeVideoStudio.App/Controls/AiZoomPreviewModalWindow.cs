using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Controls;

public enum AiZoomPreviewAction
{
    Discard,
    Keep,
    FixTracking
}

public sealed class AiZoomPreviewModalWindow : Window
{
    private readonly Action? _onReplay;
    private readonly Button _replayBtn;
    private readonly TextBlock _statusText;
    private readonly string? _videoPath;
    private readonly double _startSec;
    private readonly double _durationSec;
    private readonly Func<double, string>? _cropAtTime;
    private readonly MpvVideoView? _videoHost;
    private Border? _videoBorder;
    private Size? _videoChrome;
    private const double VideoAspect = 16.0 / 9.0;
    private const double EstimatedChromeHeight = 300;

    private void FitVideoToSmallDisplay()
    {
        if (_videoBorder == null) return;
        var display = WindowPlacement.HomeDisplay(this);
        if (!display.HasValue) return;
        var cap = WindowPlacement.Cap(display.Value, WindowPlacement.DialogCap);
        double maxH = cap.Height - EstimatedChromeHeight;
        if (maxH <= 0 || _videoBorder.Height <= maxH) return;
        double h = Math.Max(180, Math.Floor(maxH));
        _videoBorder.Height = h;
        _videoBorder.Width = Math.Round(h * VideoAspect);
        Width = _videoBorder.Width + 40;
    }

    private void FitVideoToWindow(Size size)
    {
        if (_videoBorder == null || _videoChrome is not Size chrome || SizeToContent != SizeToContent.Manual) return;
        double w = Math.Min(size.Width - chrome.Width, (size.Height - chrome.Height) * VideoAspect);
        w = Math.Max(320, Math.Floor(w));
        double h = Math.Round(w / VideoAspect);
        if (Math.Abs(_videoBorder.Width - w) < 0.5 && Math.Abs(_videoBorder.Height - h) < 0.5) return;
        _videoBorder.Width = w;
        _videoBorder.Height = h;
    }
    private readonly DispatcherTimer? _playbackTimer;
    private string? _lastCrop;

    public AiZoomPreviewAction Action { get; private set; } = AiZoomPreviewAction.Discard;
    public bool Result => Action == AiZoomPreviewAction.Keep;
    public double CurrentPlaybackTimeSec { get; private set; }

    public AiZoomPreviewModalWindow(Action onReplay, string? usageSummary = null, int blockCount = 1)
        : this(onReplay, usageSummary, blockCount, null, 0, 0, null)
    {
    }

    public AiZoomPreviewModalWindow(
        Action? onReplay,
        string? usageSummary,
        int blockCount,
        string? videoPath,
        double startSec,
        double durationSec,
        Func<double, string>? cropAtTime)
    {
        _onReplay = onReplay;
        _videoPath = videoPath;
        _startSec = startSec;
        _durationSec = durationSec;
        _cropAtTime = cropAtTime;

        Title = "AI Smart Zoom Preview";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Black);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = DialogChrome.TitleBarHeight;

        var root = new StackPanel { Margin = new Thickness(20, 10, 20, 14), Spacing = 10 };

        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = ThemeResources.Brush(this, "AppSuccessBrush", Brushes.ForestGreen), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = blockCount > 1 ? $"STAGE 3 OF 3 · REVIEW ({blockCount} BLOCKS)" : "STAGE 3 OF 3 · REVIEW",
            FontWeight = FontWeight.Bold,
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize13"),
            Foreground = ThemeResources.Brush(this, "AppAccentBrush", Brushes.Cyan),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerRow.Children.Add(dot);
        headerRow.Children.Add(title);
        Grid.SetColumn(headerRow, 0);
        topRow.Children.Add(headerRow);

        if (!string.IsNullOrWhiteSpace(usageSummary))
        {
            var usageBlock = new TextBlock
            {
                Text = usageSummary,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray),
                [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize11"),
            };
            Grid.SetColumn(usageBlock, 1);
            topRow.Children.Add(usageBlock);
        }
        root.Children.Add(topRow);

        var videoBorder = new Border
        {
            Width = 720,
            Height = 405,
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _videoHost = new MpvVideoView
        {
            Name = "AiPreviewModalVideoHost",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        videoBorder.Child = _videoHost;
        root.Children.Add(videoBorder);
        _videoBorder = videoBorder;

        var bottomRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        _statusText = new TextBlock
        {
            Text = "Review tracking preview. Plays once, then pauses. Click ▶ REPLAY to re-watch.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize12"),
        };
        Grid.SetColumn(_statusText, 0);
        bottomRow.Children.Add(_statusText);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };

        _replayBtn = new Button
        {
            Content = "⏸ PAUSE",
            MinWidth = 90,
            MinHeight = 34,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _replayBtn.Classes.Add("Secondary");
        ToolTip.SetTip(_replayBtn, "Replays the tracked block from the beginning.");
        _replayBtn.Click += (_, _) => HandleReplay();

        var discardBtn = new Button
        {
            Content = "DISCARD",
            MinWidth = 85,
            MinHeight = 34,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsCancel = true,
        };
        discardBtn.Classes.Add("Danger");
        ToolTip.SetTip(discardBtn, "Throws this AI zoom away. Your block stays as it was.");
        discardBtn.Click += (_, _) => { Action = AiZoomPreviewAction.Discard; Close(); };

        var fixTrackingBtn = new Button
        {
            Content = "FIX TRACKING",
            MinWidth = 105,
            MinHeight = 34,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        fixTrackingBtn.Classes.Add("Secondary");
        ToolTip.SetTip(fixTrackingBtn, "Reopens tracking at the problem frame without losing your selections.");
        fixTrackingBtn.Click += (_, _) =>
        {
            Action = AiZoomPreviewAction.FixTracking;
            if (_videoHost?.IpcClient != null)
            {
                CurrentPlaybackTimeSec = _videoHost.IpcClient.CurrentTime;
            }
            Close();
        };

        var acceptBtn = new Button
        {
            Content = "KEEP",
            MinWidth = 95,
            MinHeight = 34,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsDefault = true,
        };
        acceptBtn.Classes.Add("Success");
        ToolTip.SetTip(acceptBtn, "Keeps this AI zoom on the block. Ctrl+Z takes it off again.");
        acceptBtn.Click += (_, _) => { Action = AiZoomPreviewAction.Keep; Close(); };

        btnRow.Children.Add(_replayBtn);
        btnRow.Children.Add(fixTrackingBtn);
        btnRow.Children.Add(discardBtn);
        btnRow.Children.Add(acceptBtn);
        Grid.SetColumn(btnRow, 1);
        bottomRow.Children.Add(btnRow);

        root.Children.Add(bottomRow);

        Content = DialogChrome.Wrap(this, "AI SMART ZOOM PREVIEW", root);
        FitVideoToSmallDisplay();
        DialogFit.Apply(this, scroll: false);
        Opened += (_, _) => _videoChrome = new Size(Math.Max(0, Bounds.Width - videoBorder.Width), Math.Max(0, Bounds.Height - videoBorder.Height));
        SizeChanged += (_, e) => FitVideoToWindow(e.NewSize);

        if (!string.IsNullOrEmpty(_videoPath) && _durationSec > 0)
        {
            _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _playbackTimer.Tick += (_, _) => OnPlaybackTick();

            Opened += async (_, _) =>
            {
                try
                {
                    string mpvPath = ResolveMpvPath();
                    if (File.Exists(mpvPath) && _videoHost != null)
                    {
                        await _videoHost.StartMpvProcessAsync(mpvPath);
                        if (_videoHost.IpcClient != null)
                        {
                            await _videoHost.IpcClient.LoadFileAsync(_videoPath, _startSec);
                            await _videoHost.IpcClient.SetPropertyAsync("time-pos", _startSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            await _videoHost.IpcClient.SetPropertyAsync("pause", "no");
                            _playbackTimer.Start();
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Swallowed(ex);
                }
            };

            Closed += async (_, _) =>
            {
                _playbackTimer?.Stop();
                if (_videoHost != null)
                {
                    try { await _videoHost.ShutdownAsync(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            };
        }
    }

    private void OnPlaybackTick()
    {
        if (_videoHost?.IpcClient == null) return;
        double cur = _videoHost.IpcClient.CurrentTime;
        double loopEndSec = _startSec + _durationSec;
        if (cur >= loopEndSec)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            SetPlaybackEnded();
            return;
        }
        if (_cropAtTime != null)
        {
            string crop = _cropAtTime(cur) ?? "";
            if (crop != _lastCrop)
            {
                _lastCrop = crop;
                _ = _videoHost.IpcClient.SetPropertyAsync("video-crop", crop);
            }
        }
    }

    private void HandleReplay()
    {
        if (_videoHost?.IpcClient != null && !string.IsNullOrEmpty(_videoPath) && _durationSec > 0)
        {
            double cur = _videoHost.IpcClient.CurrentTime;
            double loopEndSec = _startSec + _durationSec;
            if (!_videoHost.IpcClient.IsPaused && cur < loopEndSec - 0.1)
            {
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                TogglePlaybackState(false);
                return;
            }
            _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", _startSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            TogglePlaybackState(true);
            return;
        }
        _onReplay?.Invoke();
    }

    private static string ResolveMpvPath()
    {
        return BinaryPathResolver.Resolve("mpv.exe", "frontend", "binaries", "backend");
    }

    public void SetPlaybackEnded()
    {
        _replayBtn.Content = "▶ REPLAY";
    }

    public void TogglePlaybackState(bool isPlaying)
    {
        _replayBtn.Content = isPlaying ? "⏸ PAUSE" : "▶ REPLAY";
    }
}
