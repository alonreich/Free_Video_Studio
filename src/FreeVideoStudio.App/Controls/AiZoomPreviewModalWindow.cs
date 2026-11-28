using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
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
    public const string ReviewTransportTag = "AIREVIEWPLAYER_01";

    public const string BoundsKey = "AiZoomPreviewBounds";

    internal const double SeekStepSec = PreviewKeySeek.StepSeconds;

    private readonly Action? _onReplay;
    private readonly Button _replayBtn;
    private readonly Path _playIcon;
    private readonly Path _pauseIcon;
    private readonly TextBlock _statusText;
    private readonly TextBlock _elapsedText;
    private readonly TextBlock _totalText;
    private readonly Slider _timeline;
    private readonly ToggleButton _finalViewBtn;
    private readonly ToggleButton _originalViewBtn;
    private readonly string? _videoPath;
    private readonly double _startSec;
    private readonly double _durationSec;
    private readonly Func<double, string>? _cropAtTime;
    private readonly MpvVideoView? _videoHost;
    private readonly Border _videoBorder;
    private const double VideoAspect = 16.0 / 9.0;
    private readonly DispatcherTimer? _playbackTimer;
    private string? _lastCrop;
    private bool _syncingTimeline;
    private bool _scrubbing;
    private bool _showOriginal;
    private bool _isPlaying = true;

    public AiZoomPreviewAction Action { get; private set; } = AiZoomPreviewAction.Discard;
    public bool Result => Action == AiZoomPreviewAction.Keep;
    public double CurrentPlaybackTimeSec { get; private set; }

    internal bool ShowsOriginal => _showOriginal;

    internal bool IsPlayingState => _isPlaying;

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
        Width = 1100;
        Height = 760;
        MinWidth = 640;
        MinHeight = 520;
        SizeToContent = SizeToContent.Manual;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeResources.Brush(this, "AppSurfaceBrush", Brushes.Black);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = DialogChrome.TitleBarHeight;

        var root = new Grid
        {
            Margin = new Thickness(20, 10, 20, 14),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto"),
        };

        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerRow.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = ThemeResources.Brush(this, "AppSuccessBrush", Brushes.ForestGreen), VerticalAlignment = VerticalAlignment.Center });
        headerRow.Children.Add(new TextBlock
        {
            Text = blockCount > 1 ? $"STEP 2 OF 2 · CHECK THE RESULT ({blockCount} BLOCKS)" : "STEP 2 OF 2 · CHECK THE RESULT",
            FontWeight = FontWeight.Bold,
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize13"),
            Foreground = ThemeResources.Brush(this, "AppAccentBrush", Brushes.Cyan),
            VerticalAlignment = VerticalAlignment.Center,
        });
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

        var videoArea = new Panel { ClipToBounds = true };
        _videoBorder = new Border
        {
            Width = 720,
            Height = 405,
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _videoHost = new MpvVideoView
        {
            Name = "AiPreviewModalVideoHost",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _videoBorder.Child = _videoHost;
        videoArea.Children.Add(_videoBorder);
        videoArea.SizeChanged += (_, e) => FitVideo(e.NewSize);
        Grid.SetRow(videoArea, 1);
        root.Children.Add(videoArea);

        var viewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 4) };
        _finalViewBtn = new ToggleButton { Name = "AiReviewFinalViewBtn", Content = "FINAL VIDEO", IsChecked = true, MinWidth = 130, MinHeight = 32, Cursor = new Cursor(StandardCursorType.Hand), HorizontalContentAlignment = HorizontalAlignment.Center };
        _originalViewBtn = new ToggleButton { Name = "AiReviewOriginalViewBtn", Content = "ORIGINAL", IsChecked = false, MinWidth = 130, MinHeight = 32, Cursor = new Cursor(StandardCursorType.Hand), HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(_finalViewBtn, "Shows the video with the AI zoom, as it will be exported.");
        ToolTip.SetTip(_originalViewBtn, "Shows the same moment without any zoom, to compare.");
        _finalViewBtn.Click += (_, _) => SetShowOriginal(false);
        _originalViewBtn.Click += (_, _) => SetShowOriginal(true);
        viewRow.Children.Add(_finalViewBtn);
        viewRow.Children.Add(_originalViewBtn);
        Grid.SetRow(viewRow, 2);
        root.Children.Add(viewRow);

        var ruler = new ReviewTimelineRuler { Duration = Math.Max(0.1, durationSec), Height = 22, Margin = new Thickness(62, 4, 62, 0), IsHitTestVisible = false };
        Grid.SetRow(ruler, 3);
        root.Children.Add(ruler);

        var timeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*,56"), Margin = new Thickness(0, 0, 0, 4) };
        _elapsedText = new TextBlock { Text = FormatClock(0), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White), [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize11") };
        _timeline = new Slider
        {
            Name = "AiReviewTimeline",
            Minimum = 0,
            Maximum = Math.Max(0.1, durationSec),
            Value = 0,
            Margin = new Thickness(6, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_timeline, "Click or drag to jump to any moment of this block.");
        Avalonia.Automation.AutomationProperties.SetName(_timeline, "Position in the block");
        _timeline.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _syncingTimeline) return;
            SeekTo(_startSec + _timeline.Value);
        };
        _timeline.AddHandler(PointerPressedEvent, (_, _) => _scrubbing = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        _timeline.AddHandler(PointerReleasedEvent, (_, _) => _scrubbing = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        _totalText = new TextBlock { Text = FormatClock(durationSec), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Foreground = ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray), [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize11") };
        Grid.SetColumn(_timeline, 1);
        Grid.SetColumn(_totalText, 2);
        timeRow.Children.Add(_elapsedText);
        timeRow.Children.Add(_timeline);
        timeRow.Children.Add(_totalText);
        Grid.SetRow(timeRow, 4);
        root.Children.Add(timeRow);

        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 30, Margin = new Thickness(0, 2, 0, 8) };
        var backBtn = new Button { Name = "AiReviewBackBtn", Content = "⏪ 2s", MinWidth = 70, MinHeight = 36, Cursor = new Cursor(StandardCursorType.Hand), HorizontalContentAlignment = HorizontalAlignment.Center };
        backBtn.Classes.Add("Secondary");
        ToolTip.SetTip(backBtn, "Go back 2 seconds.");
        backBtn.Click += (_, _) => SeekBy(-SeekStepSec);
        _playIcon = new Path { Stretch = Stretch.Uniform, IsVisible = false };
        _pauseIcon = new Path { Stretch = Stretch.Uniform };
        _playIcon.Bind(Path.DataProperty, _playIcon.GetResourceObservable("AppPlayIconGeometry"));
        _pauseIcon.Bind(Path.DataProperty, _pauseIcon.GetResourceObservable("AppPauseIconGeometry"));
        _playIcon.Bind(Shape.FillProperty, _playIcon.GetResourceObservable("AppOnAccentTextBrush"));
        _pauseIcon.Bind(Shape.FillProperty, _pauseIcon.GetResourceObservable("AppOnAccentTextBrush"));
        var iconPanel = new Panel { Width = 18, Height = 18 };
        iconPanel.Children.Add(_playIcon);
        iconPanel.Children.Add(_pauseIcon);
        _replayBtn = new Button
        {
            Name = "AiReviewPlayPauseBtn",
            Content = iconPanel,
            MinWidth = 70,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _replayBtn.Classes.Add("Primary");
        ToolTip.SetTip(_replayBtn, "Play or pause. At the end it starts again from the beginning.");
        Avalonia.Automation.AutomationProperties.SetName(_replayBtn, "Play or Pause");
        _replayBtn.Click += (_, _) => HandleReplay();
        var fwdBtn = new Button { Name = "AiReviewForwardBtn", Content = "2s ⏩", MinWidth = 70, MinHeight = 36, Cursor = new Cursor(StandardCursorType.Hand), HorizontalContentAlignment = HorizontalAlignment.Center };
        fwdBtn.Classes.Add("Secondary");
        ToolTip.SetTip(fwdBtn, "Go forward 2 seconds.");
        fwdBtn.Click += (_, _) => SeekBy(SeekStepSec);
        transport.Children.Add(backBtn);
        transport.Children.Add(_replayBtn);
        transport.Children.Add(fwdBtn);
        Grid.SetRow(transport, 5);
        root.Children.Add(transport);

        var bottomRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
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
        bottomRow.Children.Add(discardBtn);

        _statusText = new TextBlock
        {
            Text = "Does the camera follow the right target?",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize13"),
        };
        Grid.SetColumn(_statusText, 1);
        bottomRow.Children.Add(_statusText);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var fixTrackingBtn = new Button
        {
            Content = "TRY AGAIN",
            MinWidth = 150,
            MinHeight = 44,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        fixTrackingBtn.Classes.Add("Secondary");
        ToolTip.SetTip(fixTrackingBtn, "Point at the right target again at the moment it went wrong. Your marks are kept.");
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
            Content = "KEEP IT",
            MinWidth = 150,
            MinHeight = 44,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
        };
        acceptBtn.Classes.Add("Success");
        ToolTip.SetTip(acceptBtn, "Keeps this AI zoom on the block. Ctrl+Z takes it off again.");
        acceptBtn.Click += (_, _) => { Action = AiZoomPreviewAction.Keep; Close(); };
        btnRow.Children.Add(fixTrackingBtn);
        btnRow.Children.Add(acceptBtn);
        Grid.SetColumn(btnRow, 2);
        bottomRow.Children.Add(btnRow);
        Grid.SetRow(bottomRow, 6);
        root.Children.Add(bottomRow);

        Content = DialogChrome.Wrap(this, "AI SMART ZOOM PREVIEW", root);
        DialogFit.Apply(this, scroll: false);

        if (!string.IsNullOrEmpty(_videoPath) && _durationSec > 0)
        {
            _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _playbackTimer.Tick += (_, _) => OnPlaybackTick();

            Opened += async (_, _) =>
            {
                try
                {
                    string mpvPath = ResolveMpvPath();
                    if (System.IO.File.Exists(mpvPath) && _videoHost != null)
                    {
                        await _videoHost.StartMpvProcessAsync(mpvPath);
                        if (_videoHost.IpcClient != null)
                        {
                            await _videoHost.IpcClient.LoadFileAsync(_videoPath, _startSec);
                            await _videoHost.IpcClient.SetPropertyAsync("time-pos", _startSec.ToString(CultureInfo.InvariantCulture));
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

    internal static string FormatClock(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.ToString(ts.TotalHours >= 1 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);
    }

    internal static (double W, double H) FitSixteenNine(double availW, double availH)
    {
        if (availW <= 0 || availH <= 0) return (0, 0);
        double w = Math.Min(availW, availH * VideoAspect);
        return (Math.Floor(w), Math.Floor(w / VideoAspect));
    }

    private void FitVideo(Size area)
    {
        var (w, h) = FitSixteenNine(area.Width, area.Height);
        if (w < 160 || h < 90) return;
        if (Math.Abs(_videoBorder.Width - w) < 0.5 && Math.Abs(_videoBorder.Height - h) < 0.5) return;
        _videoBorder.Width = w;
        _videoBorder.Height = h;
    }

    public async Task ShowOverAsync(Window owner)
    {
        bool hasSaved = false;
        try
        {
            hasSaved = Services.InProcessSessionState.ReadSnapshot().TryGetPropertyValue(BoundsKey, out var node) && node is JsonObject;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
        if (!hasSaved)
        {
            if (owner.WindowState is WindowState.Maximized or WindowState.FullScreen)
            {
                WindowState = WindowState.Maximized;
            }
            else if (owner.Bounds.Width > 0 && owner.Bounds.Height > 0)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = owner.Position;
                MinWidth = Math.Min(MinWidth, owner.Bounds.Width);
                MinHeight = Math.Min(MinHeight, owner.Bounds.Height);
                Width = owner.Bounds.Width;
                Height = owner.Bounds.Height;
            }
        }
        WindowBoundsHelper.Track(this, BoundsKey, capFraction: WindowPlacement.EditorCap);
        await ShowDialog(owner);
    }

    private void SetShowOriginal(bool original)
    {
        _showOriginal = original;
        _finalViewBtn.IsChecked = !original;
        _originalViewBtn.IsChecked = original;
        _lastCrop = null;
        _statusText.Text = original
            ? "This is the video without zoom. Press FINAL VIDEO to compare."
            : "Does the camera follow the right target?";
    }

    private void SeekBy(double deltaSec)
    {
        double cur = _videoHost?.IpcClient?.CurrentTime ?? (_startSec + _timeline.Value);
        SeekTo(cur + deltaSec);
    }

    private void SeekTo(double absSec)
    {
        double target = Math.Clamp(absSec, _startSec, _startSec + Math.Max(0, _durationSec - 0.05));
        SyncTimeline(target);
        if (_videoHost?.IpcClient == null) return;
        _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", target.ToString(CultureInfo.InvariantCulture));
    }

    private void SyncTimeline(double absSec)
    {
        double rel = Math.Clamp(absSec - _startSec, 0, Math.Max(0, _durationSec));
        _elapsedText.Text = FormatClock(rel);
        if (_scrubbing) return;
        _syncingTimeline = true;
        _timeline.Value = rel;
        _syncingTimeline = false;
    }

    private void OnPlaybackTick()
    {
        if (_videoHost?.IpcClient == null) return;
        double cur = _videoHost.IpcClient.CurrentTime;
        double loopEndSec = _startSec + _durationSec;
        SyncTimeline(cur);
        if (cur >= loopEndSec && !_videoHost.IpcClient.IsPaused)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            SetPlaybackEnded();
        }
        if (_cropAtTime != null)
        {
            string crop = _cropAtTime(cur) ?? "";
            if (_showOriginal) crop = "";
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
            if (cur >= loopEndSec - 0.1)
            {
                _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", _startSec.ToString(CultureInfo.InvariantCulture));
                SyncTimeline(_startSec);
            }
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

    public void SetPlaybackEnded() => TogglePlaybackState(false);

    public void TogglePlaybackState(bool isPlaying)
    {
        _isPlaying = isPlaying;
        _playIcon.IsVisible = !isPlaying;
        _pauseIcon.IsVisible = isPlaying;
    }
}
