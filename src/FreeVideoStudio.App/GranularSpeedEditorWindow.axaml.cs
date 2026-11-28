using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.Collections.Immutable;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using static FreeVideoStudio.App.Infrastructure.GranularEditorVisuals;

namespace FreeVideoStudio.App;

public partial class GranularSpeedEditorWindow : Window
{
    private MpvVideoView? _videoHost;
    private bool _isSeeking = false;
    private long _lastSeekTimestamp = 0;
    private double? _nextSeekTarget = null;

    private const int SeekCoalesceMs = 60;

    private DispatcherTimer? _seekFlushTimer;

    private readonly FreeVideoStudio.Core.Editing.GranularEditSession _edit;

    private bool _isSafeToClose = false;

    private bool _gpuLiveZoomPreview;
    private string _lastLiveCrop = "";

    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;

    private DispatcherTimer? _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _isTimelineDrawn = false;
    private readonly FreeVideoStudio.Core.Media.PreviewSpeedGovernor _previewSpeed = new();
    private readonly FreeVideoStudio.App.Controls.MusicBedPreviewPlayer _musicBed = new();
    private FreeVideoStudio.App.Models.EditorPreviewLayers _previewLayers = FreeVideoStudio.App.Models.EditorPreviewLayers.None;

    public FreeVideoStudio.App.Models.EditorPreviewLayers PreviewLayers
    {
        get => _previewLayers;
        set
        {
            _previewLayers = value ?? FreeVideoStudio.App.Models.EditorPreviewLayers.None;
            _musicBed.Result = _previewLayers.Music;
            if (_previewLayers.VoiceOver != null && !ReferenceEquals(_previewLayers.VoiceOver, _voiceOverPlayer.Result))
                _voiceOverPlayer.Result = _previewLayers.VoiceOver;
            _ = RefreshFinalLayoutPreviewAsync();
        }
    }

    private int _finalLayoutVersion;

    private Infrastructure.HudPreviewController? _hudPreview;

    private Infrastructure.HudPreviewController EnsureHudPreview(Controls.PhoneFrameMockup frame)
    {
        if (_hudPreview != null) return _hudPreview;
        _hudPreview = new Infrastructure.HudPreviewController(frame, () =>
            _videoHost?.IpcClient is { } ipc
                ? new Infrastructure.HudPlayerState(_edit.VideoPath, ipc.CurrentTime, ipc.Duration, ipc.IsPaused, ipc.VideoWidth, ipc.VideoHeight)
                : null);
        System.Action<Infrastructure.AppSettings> onCommit = committed => Dispatcher.UIThread.Post(() => _ = RefreshFinalLayoutPreviewAsync());
        Infrastructure.SettingsManager.Committed += onCommit;
        Closed += (_, _) =>
        {
            Infrastructure.SettingsManager.Committed -= onCommit;
            _hudPreview?.Dispose();
        };
        return _hudPreview;
    }

    private async Task RefreshFinalLayoutPreviewAsync()
    {
        int version = ++_finalLayoutVersion;
        if (this.FindControl<Controls.PhoneFrameMockup>("GranularPhoneFrame") is not { } frame) return;
        if (!_edit.IsMobileFormat || !Infrastructure.HudPreviewController.Enabled)
        {
            frame.SetFinalLayout(null);
            _hudPreview?.Configure(null, FreeVideoStudio.Core.Media.HudVisibility.AllVisible);
            if (_edit.IsMobileFormat) EnsureHudPreview(frame);
            return;
        }
        var visibility = _previewLayers.HudVisibility;
        var snapshot = await Infrastructure.FinalLayoutPreview.LoadSnapshotAsync();
        if (version != _finalLayoutVersion) return;
        var layers = snapshot.Visible(visibility);
        frame.SetFinalLayout(layers, snapshot.Labels, null);
        EnsureHudPreview(frame).Configure(layers, visibility);
    }

    public bool Accepted { get; private set; }
    public IReadOnlyList<SpeedSegment> ResultSegments => _edit.ResultSegments;

    private string? _draggingMemeId;

    private bool _memeDragIsCorner;

    private double _memeDragGrabOffsetOutSec;

    private double _memeDragLastX = -1;

    private Infrastructure.MemePreviewDirector? _memePreview;

    private bool _memeRebuildStallActive;

    private bool _memeCaretSticky;

    private bool _memeDragWasPlaying;

    private const double MemeGrabMinWidthPx = 18;

    public IReadOnlyList<FreeVideoStudio.Core.Media.MemePlacement> ResultMemes => _edit.ResultMemes;

    public IReadOnlyList<FreeVideoStudio.Core.Media.CutRange> ResultCuts => _edit.ResultCuts;

    public double ResultBaseSpeed => _edit.BaseSpeed;
    public double ResultFreezeTimeMs => _edit.FreezeTimeMs;
    public double ResultFreezeDurationS => _edit.FreezeDurationS;
    public CaptionTrack? ResultCaptionTrack => _edit.CaptionTrack;

    private readonly FreeVideoStudio.App.Controls.VoiceOverPreviewPlayer _voiceOverPlayer = new();

    private bool _freezeArmed = true;
    private double _prevFreezeTickAbsMs = -1;

    private enum FreezeDragMode { None, Move, ResizeStart, ResizeEnd }
    private FreezeDragMode _freezeDragMode = FreezeDragMode.None;

    private enum FreezeMarkerEnd { None, Start, End }

    private FreezeMarkerEnd _freezeFocus = FreezeMarkerEnd.None;

    private readonly List<Avalonia.Controls.Shapes.Rectangle> _freezeMarkerAnts = new();

    private (int Segment, bool IsStart)? _zoomFocus;

    private int _zoomDragSegment = -1;
    private bool _zoomDragIsStart;
    private double _freezeDragGrabOffsetSec;
    private double _freezeDragFixedEndOutSec;
    private readonly List<double> _aiEliminationTimestamps = new();

    public void SetAiEliminations(IEnumerable<double>? timestamps)
    {
        _aiEliminationTimestamps.Clear();
        if (timestamps != null)
        {
            _aiEliminationTimestamps.AddRange(timestamps);
        }
        RedrawTimeline();
    }

    private string? _killScanStatus;
    private string? _killScanDetail;
    private bool _killScanIsError;
    private Action? _killScanCancel;
    private Action? _killScanStart;

    internal const string KillScanStartTag = "AIELIMGATE_01";

    public void SetKillScanStatus(string? text, string? detail, bool isError, Action? cancel, Action? start = null)
    {
        _killScanStatus = text;
        _killScanDetail = detail;
        _killScanIsError = isError;
        _killScanCancel = cancel;
        _killScanStart = start;
        RedrawTimeline();
    }

    private void DrawKillScanChip(Avalonia.Controls.Canvas canvas, double width)
    {
        if (string.IsNullOrEmpty(_killScanStatus)) return;
        var danger = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Avalonia.Media.Brushes.IndianRed);
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = _killScanStatus,
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
            Foreground = _killScanIsError ? danger : Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", Avalonia.Media.Brushes.White),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(120, width * 0.5),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        if (_killScanStart is { } start)
        {
            var go = new TextBlock
            {
                Text = "▶ Start",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Infrastructure.ThemeResources.Brush(this, "AppAccentBrush", Avalonia.Media.Brushes.DeepSkyBlue),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            go.PointerPressed += (_, e) => { e.Handled = true; start(); };
            panel.Children.Add(go);
        }
        if (_killScanCancel is { } cancel)
        {
            var stop = new TextBlock
            {
                Text = "✕ Cancel",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = danger,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            stop.PointerPressed += (_, e) => { e.Handled = true; cancel(); };
            panel.Children.Add(stop);
        }
        var chip = new Avalonia.Controls.Border
        {
            Background = Infrastructure.ThemeResources.Brush(this, "AppSurfaceBrush", Avalonia.Media.Brushes.Black),
            BorderBrush = _killScanIsError ? danger : Infrastructure.ThemeResources.Brush(this, "AppBorderBrush", Avalonia.Media.Brushes.Gray),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(6, 1),
            ZIndex = 600,
            Child = panel,
        };
        if (!string.IsNullOrEmpty(_killScanDetail)) ToolTip.SetTip(chip, _killScanDetail);
        Avalonia.Controls.Canvas.SetRight(chip, 0);
        Avalonia.Controls.Canvas.SetTop(chip, -22);
        canvas.Children.Add(chip);
    }

    private const double LaneBorderInsetPx = 2.0;

    private const double FreezeMarkerOverlayTop = -52.0;

    private const double FreezeMarkerEndStaggerPx = 30.0;

    private const double FreezeMarkerHeadWidthPx = 52.0;
    private bool _isCurrentlyFrozen = false;
    private DateTime _freezeStartTime;
    private readonly Services.FreezeSoundPreview _freezeSound = new();
    private bool _isFreezeCameraSelected = false;
    private int _draggingSegmentIndex = -1;
    private enum SegDragMode { None, Move, ResizeStart, ResizeEnd }
    private SegDragMode _segDragMode = SegDragMode.None;
    private double _dragOrigStartMs;
    private double _dragOrigEndMs;
    private double _dragStartPointerMs;
    private FreeVideoStudio.Core.Media.OutputTimeline? _segDragTimeline;
    private double _segDragOutDurationSec = 0;
    private double? _dragOrigZoomStartMs;
    private double? _dragOrigZoomEndMs;
    private bool _isCanvasScrubbing;

    private const double SeamEpsilonMs = 1.0;

    private volatile string _uiCrumb = "idle";

    private const int WatchdogTickMs = 1000;
    private const int StallReportAfterMs = 2000;
    private const int MaxStallReports = 6;
    private int _stallReportsWritten;
    private long _uiHeartbeatTicks;
    private System.Threading.Timer? _uiWatchdog;

    private void Crumb(string what) => _uiCrumb = what;

    private void StartUiWatchdog()
    {
        System.Threading.Interlocked.Exchange(ref _uiHeartbeatTicks, Environment.TickCount64);
        _uiWatchdog = new System.Threading.Timer(_ =>
        {
            long since = Environment.TickCount64 - System.Threading.Interlocked.Read(ref _uiHeartbeatTicks);

            if (since > StallReportAfterMs)
            {
                if (_stallReportsWritten < MaxStallReports)
                {
                    _stallReportsWritten++;
                    RuntimeLog.EmergencyWrite("Granular",
                        $"UI THREAD STALLED for {since}ms (report {_stallReportsWritten}/{MaxStallReports}). " +
                        $"Last editor step: {_uiCrumb} | " +
                        $"preview UI step: {MpvVideoView.LastUiStep} | " +
                        $"preview render step: {MpvVideoView.LastRenderStep} | " +
                        $"render lock: {MpvVideoView.LastLockStep} | " +
                        $"blocking calls: {FreeVideoStudio.Core.Infrastructure.BlockingCallTracker.Describe()}");
                }
            }
            else
            {
                _stallReportsWritten = 0;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => System.Threading.Interlocked.Exchange(ref _uiHeartbeatTicks, Environment.TickCount64),
                Avalonia.Threading.DispatcherPriority.Background);
        }, null, WatchdogTickMs, WatchdogTickMs);
    }

    private const double MarkerOverlayFallbackHeightPx = 120.0;

    private const double LaneHeight = 60.0;

    internal const double SegmentLaneHeight = LaneHeight / 2.0;

    private const double LaneBlockHeight = SegmentLaneHeight;

    private double _playheadMs;

    private double? _holdCaretOutSec;



    private void DecorateFrozenSpan(Avalonia.Controls.Canvas host, double x, double spanW, double h,
                                    bool withLabel, bool withGrips = false)
    {
        if (spanW <= 1 || h <= 0) return;

        var wash = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = spanW,
            Height = h,
            Fill = FreezeBrush(0x3A),
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(wash, x);
        Avalonia.Controls.Canvas.SetTop(wash, 0);
        host.Children.Add(wash);

        var hatchHost = new Avalonia.Controls.Canvas
        {
            Width = spanW,
            Height = h,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(hatchHost, x);
        Avalonia.Controls.Canvas.SetTop(hatchHost, 0);

        const double HatchStep = 11.0;
        var hatchBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x26, 255, 255, 255));
        for (double hx = -h; hx < spanW; hx += HatchStep)
        {
            hatchHost.Children.Add(new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Avalonia.Point(hx, h),
                EndPoint = new Avalonia.Point(hx + h, 0),
                Stroke = hatchBrush,
                StrokeThickness = 2,
                IsHitTestVisible = false
            });
        }
        host.Children.Add(hatchHost);

        double postW = withGrips ? 4 : 2;
        foreach (double postX in new[] { x, x + spanW - postW })
        {
            var post = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = postW,
                Height = h,
                Fill = FreezeBrush(0xE6),
                IsHitTestVisible = false
            };
            Avalonia.Controls.Canvas.SetLeft(post, postX);
            Avalonia.Controls.Canvas.SetTop(post, 0);
            host.Children.Add(post);

            if (!withGrips || h < 20) continue;

            for (int n = -1; n <= 1; n++)
            {
                var notch = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 8,
                    Height = 2,
                    Fill = FreezeBrush(0xFF),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(notch, postX + postW / 2.0 - 4);
                Avalonia.Controls.Canvas.SetTop(notch, h / 2.0 - 1 + n * 5);
                host.Children.Add(notch);
            }
        }

        if (!withLabel || h < 18) return;

        string label = $"❄ FROZEN {_edit.FreezeDurationS:0.0}s";

        double pillW = label.Length * 6.2 + 12;
        const double PillH = 15;

        if (pillW > spanW - 6) return;

        var pill = new Border
        {
            Width = pillW,
            Height = PillH,
            Background = FreezeBrush(0xF0),
            CornerRadius = new Avalonia.CornerRadius(3),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(12, 20, 34))
            }
        };
        Avalonia.Controls.Canvas.SetLeft(pill, x + (spanW - pillW) / 2.0);
        Avalonia.Controls.Canvas.SetTop(pill, Math.Max(0, (h - PillH) / 2.0));
        host.Children.Add(pill);
    }
    private Avalonia.Controls.Shapes.Rectangle? _selectedSegmentBorderRef;
    private DispatcherTimer? _freezePulseTimer;

    public GranularSpeedEditorWindow() : this(string.Empty, 0, 0) { }

    public static async Task<GranularSpeedEditorWindow> CreateAsync(
        string videoPath,
        double trimStartMs = 0,
        double trimEndMs = 0,
        IEnumerable<SpeedSegment>? existingSegments = null,
        double baseSpeed = 1.1,
        double freezeTimeMs = -1,
        double freezeDurationS = 1.0,
        bool isMobileFormat = false,
        string originalResolution = "1920x1080",
        VoiceOverWindow.VoiceOverResult? voiceOverResult = null,
        IEnumerable<FreeVideoStudio.Core.Media.CutRange>? existingCuts = null,
        IEnumerable<FreeVideoStudio.Core.Media.MemePlacement>? existingMemes = null,
        CaptionTrack? existingCaptions = null)
    {
        double probedSec = 0;

        if (trimEndMs <= 0 && !string.IsNullOrWhiteSpace(videoPath))
        {
            try
            {
                probedSec = await Task.Run(async () =>
                {
                    if (!File.Exists(videoPath)) return 0.0;

                    string ffprobe = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve(
                        "ffprobe.exe", "backend", "binaries");
                    var prober = new FreeVideoStudio.Core.Media.MediaProber(ffprobe, videoPath);
                    return await prober.GetDurationAsync().ConfigureAwait(false);
                }).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                RuntimeLog.Info("Granular",
                    "Duration probe exceeded 10s; opening the editor on the supplied trim window instead.");
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("Granular", $"Duration probe failed: {ex.Message}. Opening on the supplied trim window.");
            }
        }

        return new GranularSpeedEditorWindow(
            videoPath, trimStartMs, trimEndMs, existingSegments, baseSpeed, freezeTimeMs,
            freezeDurationS, isMobileFormat, originalResolution, voiceOverResult, existingCuts,
            existingMemes, probedSec, existingCaptions);
    }

    public GranularSpeedEditorWindow(string videoPath, double trimStartMs = 0, double trimEndMs = 0, IEnumerable<SpeedSegment>? existingSegments = null, double baseSpeed = 1.1, double freezeTimeMs = -1, double freezeDurationS = 1.0, bool isMobileFormat = false, string originalResolution = "1920x1080", VoiceOverWindow.VoiceOverResult? voiceOverResult = null, IEnumerable<FreeVideoStudio.Core.Media.CutRange>? existingCuts = null,
        IEnumerable<FreeVideoStudio.Core.Media.MemePlacement>? existingMemes = null,
        double preProbedDurationSec = 0,
        CaptionTrack? existingCaptions = null)
    {
        _voiceOverPlayer.Result = voiceOverResult;

        _edit = new FreeVideoStudio.Core.Editing.GranularEditSession(videoPath, trimStartMs, trimEndMs,
            FreeVideoStudio.Core.Editing.GranularHistoryParking.Shared);
        _edit.CaptionTrack = existingCaptions;
        _edit.Checkpointed += OnEditCheckpointed;
        _edit.SeedMemes(existingMemes);
        _edit.SeedCutsFromAbsolute(existingCuts);

        _edit.AdoptClipDuration(preProbedDurationSec, fromProbe: true);
        _edit.BaseSpeed = baseSpeed;
        _edit.FreezeTimeMs = freezeTimeMs;
        _edit.FreezeDurationS = freezeDurationS;
        _edit.IsMobileFormat = isMobileFormat;
        if (!string.IsNullOrWhiteSpace(originalResolution)) _edit.OriginalResolution = originalResolution;

        bool restoredGranularSession = TryRehydrateGranularRecovery();

        try { _gpuLiveZoomPreview = FreeVideoStudio.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration; }
        catch (System.Exception swallowed3)
        {
            _gpuLiveZoomPreview = false;
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);
        }
        RuntimeLog.Info("Granular", $"Live zoom preview path: {(_gpuLiveZoomPreview ? "GPU (mpv video-crop simulation)" : "CPU (yellow box overlay only)")}");

        InitializeComponent();

        Controls.WindowResizeGrip.Attach(this, "Drag to resize the Granular Speed Editor");

        var zoomContainer = this.FindControl<Avalonia.Controls.Grid>("ZoomContainerGrid");
        if (zoomContainer != null)
        {
            zoomContainer.Height = _edit.IsMobileFormat ? 1280 : 1080;
        }

        this.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (s, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) ClearTimelineSelection();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        this.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (s, e) =>
        {
            if (_isCanvasScrubbing)
            {
                _isCanvasScrubbing = false;
                e.Pointer.Capture(null);
            }

            if (EndMemeDrag(e)) return;

            if (_zoomDragSegment >= 0)
            {
                int zi = _zoomDragSegment;
                bool zStart = _zoomDragIsStart;
                _zoomDragSegment = -1;
                _isDraggingZoomMarker = false;
                EndUndoGesture();
                e.Pointer.Capture(null);
                RedrawTimeline();

                HideDragReadout();

                if (zi < _edit.Segments.Count)
                {
                    var zseg = _edit.Segments[zi];
                    double edgeMs = zStart ? (zseg.ZoomStartMs ?? zseg.StartMs) : (zseg.ZoomEndMs ?? zseg.EndMs);
                    RefreshSegmentList();
                    RuntimeLog.Info("Granular",
                        $"Zoom {(zStart ? "START" : "END")} settled on block #{zi + 1}: {FormatMs(edgeMs)} (block now {FormatMs(zseg.StartMs)}–{FormatMs(zseg.EndMs)}).");
                    SetStatus($"Zoom {(zStart ? "start" : "end")} at {FormatMs(edgeMs)} — block moved with it.");
                    _ = SeekInternal(edgeMs / 1000.0);
                }
                return;
            }

            if (_freezeDragMode != FreezeDragMode.None)
            {
                var finished = _freezeDragMode;
                _freezeDragMode = FreezeDragMode.None;
                e.Pointer.Capture(null);
                HideDragReadout();
                ClampFreezeIntoClip();
                EndUndoGesture();
                RedrawTimeline();

                SeekGranularPreviewToFreezeMarker();
                RuntimeLog.Info("Granular",
                    $"Freeze settled: {FormatMs(_edit.FreezeTimeMs - _edit.TrimStartMs)} for {_edit.FreezeDurationS:0.00}s ({finished}).");
                SetStatus($"Freeze at {FormatMs(_edit.FreezeTimeMs - _edit.TrimStartMs)}, held for {_edit.FreezeDurationS:0.00}s.");
                return;
            }

            if (_draggingSegmentIndex != -1)
            {
                Crumb($"seg-drag RELEASE idx={_draggingSegmentIndex} mode={_segDragMode}");
                var finishedMode = _segDragMode;
                int finishedIdx = _draggingSegmentIndex;
                if (_segDragMode != SegDragMode.None && _draggingSegmentIndex < _edit.Segments.Count)
                {
                    ClampZoomInsideItsBlock(_draggingSegmentIndex);
                    var seg = _edit.Segments[_draggingSegmentIndex];
                    RuntimeLog.Info("Granular", $"Segment #{_draggingSegmentIndex + 1} settled: rel {FormatMs(seg.StartMs)}–{FormatMs(seg.EndMs)} @ {seg.Speed:0.0}x (abs {FormatMs(seg.StartMs + _edit.TrimStartMs)}–{FormatMs(seg.EndMs + _edit.TrimStartMs)}).");
                    SetStatus($"Block #{_draggingSegmentIndex + 1} set to {FormatMs(seg.StartMs)}–{FormatMs(seg.EndMs)} @ {seg.Speed:0.0}x.");
                }
                _segDragMode = SegDragMode.None;
                _draggingSegmentIndex = -1;
                _segDragTimeline = null;
                _segDragOutDurationSec = 0;
                _dragOrigZoomStartMs = null;
                _dragOrigZoomEndMs = null;
                EndUndoGesture();
                e.Pointer.Capture(null);
                HideDragReadout();
                RefreshSegmentList();

                _redrawDeferredByDrag = false;
                RedrawTimeline();
                QueueRelayoutFrameLane();

                if (finishedMode != SegDragMode.None && finishedIdx >= 0 && finishedIdx < _edit.Segments.Count
                    && _videoHost?.IpcClient?.IsPaused == true)
                {
                    var fseg = _edit.Segments[finishedIdx];
                    bool didChange = Math.Abs(_dragOrigStartMs - fseg.StartMs) > 1.0 || Math.Abs(_dragOrigEndMs - fseg.EndMs) > 1.0;
                    if (didChange)
                    {
                        double seekRelSec = (finishedMode == SegDragMode.ResizeEnd ? fseg.EndMs : fseg.StartMs) / 1000.0;
                        _ = SeekInternal(seekRelSec);
                    }
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);

        FreeVideoStudio.App.WindowBoundsHelper.Track(this, "GranularBounds", seedFromKey: "MainWindowBounds", fitDisplayOnFirstRun: true);
        AttachResizeGrip();
        StartUiWatchdog();
        FreeVideoStudio.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;
        _ = FreeVideoStudio.Core.Media.VideoLoudnessCache.EnsureAsync(
            FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries"), FreezeLoudnessPath);

        _edit.PendingSpeed = _edit.BaseSpeed;
        _previewSpeed.Invalidate();

        var initialSpeedSlider = PendingSpeedSliderCtl; if(initialSpeedSlider!=null)initialSpeedSlider.SetRange(1, 40);
        SpeedPresetButtons.SetSpinningWheelValue(initialSpeedSlider, _edit.PendingSpeed);
        var initialSpeedLabel = PendingSpeedLabelCtl;
        if (initialSpeedLabel != null) initialSpeedLabel.Text = $"{_edit.PendingSpeed:0.0}x";

        if (!restoredGranularSession) _edit.SeedSegmentsFromAbsolute(existingSegments);

        BuildLaneContent();

        _edit.MarkOpened();

        this.Loaded += (s, e) => Controls.CoachOverlay.Register(this, Controls.CoachTours.GranularKey, Controls.CoachTours.Granular);

        this.Loaded += (s, e) =>
        {
            _ = InitializeMpv();
            _ = BuildFrameLaneAsync();
            UpdateCaret();
            RedrawTimeline();
        };
        WireUpControls();
        WireZoomControls();
        AttachTitleBarDrag();
        RefreshSegmentList();
        UpdateDeleteButtonVisibility();

        ScheduleGranularRecoverySave();

        AdoptParkedHistory();

        if (_edit.FreezeTimeMs >= 0)
        {
            var toggle = FreezeImageToggleCtl;
            if (toggle != null)
            {
                toggle.Classes.Remove("Primary");
                toggle.Classes.Add("Danger");
                var icon = FreezeImageToggleIconCtl;
                var txt = FreezeImageToggleTextCtl;
                if (icon != null) icon.Text = "🔓";
                if (txt != null) txt.Text = " UNFREEZE IMAGE ";
            }
        }

        _marchingAntsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _marchingAntsTimer.Tick += (_, _) => {
            _marchingAntsOffset = (_marchingAntsOffset + 1) % 8;
            foreach (var ant in _freezeMarkerAnts) ant.StrokeDashOffset = _marchingAntsOffset;
            if (_selectedSegmentBorderRef != null)
            {
                _selectedSegmentBorderRef.StrokeDashOffset = _marchingAntsOffset;
            }

            if (_zoomBoxRect != null && _zoomBoxRect.IsVisible)
            {
                _zoomBoxRect.StrokeDashOffset = _marchingAntsOffset;
            }
        };
        _marchingAntsTimer.Start();

        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        var fvPopup = this.FindControl<Avalonia.Controls.Primitives.Popup>("FreezeValidationPopup");
        var targetBorder = this.FindControl<Avalonia.Controls.Border>("GranularVideoAreaBorder");
        if (fvPopup != null && targetBorder != null) fvPopup.PlacementTarget = targetBorder;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task InitializeMpv()
    {
        try
        {
        WirePreviewDetach();

        _videoHost = this.FindControl<MpvVideoView>("GranularVideoHost");
        if (_videoHost != null)
        {
            RuntimeLog.Info("Granular", "Initializing MPV video host for Granular Speed Editor.");
            string mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "frontend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "backend", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath)) mpvPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (!System.IO.File.Exists(mpvPath))
            {
                RuntimeLog.Fail("Granular", "Could not locate mpv.exe for Granular Speed Editor. Using PATH fallback.");
                mpvPath = "mpv.exe";
            }
            else
            {
                RuntimeLog.Info("Granular", $"Using MPV: {System.IO.Path.GetFileName(mpvPath)}");
                RuntimeLog.Debug("Granular", $"Using MPV path: {mpvPath}");
            }
            var host = _videoHost;
            await host.StartMpvProcessAsync(mpvPath);
            if (_editorClosing || !ReferenceEquals(_videoHost, host)) return;

            if (_videoHost.IpcClient != null)
            {
                RuntimeLog.Info("Granular", "MPV IPC client connected. Attaching seek handler.");
                _videoHost.IpcClient.SeekCompleted += () => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                        if (_editorClosing) return;
                        _isSeeking = false;
                        if (_nextSeekTarget.HasValue) {
                            double target = _nextSeekTarget.Value;
                            _nextSeekTarget = null;
                            await SeekInternal(target);
                        }
                        UpdateLiveZoomCrop();
                        UpdateZoomPlayheadOverlay();
                    });
                };

                await LoadVideoAsync();
                BuildMemePreviewDirector();

                _edit.AdoptClipDuration(_videoHost.IpcClient.Duration, fromProbe: false);
                UpdateCaret();
                RedrawTimeline();
                if (_thumbBitmap == null)
                {
                    _ = BuildFrameLaneAsync();
                }
            }
            else
            {
                RuntimeLog.Fail("Granular", "MPV IPC client is null after starting MPV process.");
            }
        }
        else
        {
            RuntimeLog.Fail("Granular", "Could not find GranularVideoHost control in XAML.");
        }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Granular", $"Preview startup failed: {ex.Message}");
            if (!_editorClosing) SetStatus("The video preview could not start. Close this editor and try again.");
        }
    }

    private void UpdateTooltips()
    {
        var kb = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playBtn = this.FindControl<Button>("GranularPlayPause");
        if (playBtn != null) ToolTip.SetTip(playBtn, $"Play or pause the video ({kb.PlayPause})");

        var startBtn = this.FindControl<Button>("MarkStartBtn");
        if (startBtn != null) ToolTip.SetTip(startBtn, $"Mark the start of the block ({kb.MarkStart})");

        var endBtn = this.FindControl<Button>("MarkEndBtn");
        if (endBtn != null) ToolTip.SetTip(endBtn, $"Mark the end of the block ({kb.MarkEnd})");

        RefreshTransportKeyBindings();
    }

    private FreeVideoStudio.App.ViewModels.RelayCommand? _playPauseCommand;
    private FreeVideoStudio.App.ViewModels.RelayCommand? _markStartCommand;
    private FreeVideoStudio.App.ViewModels.RelayCommand? _markEndCommand;

    private void RefreshTransportKeyBindings()
    {
        var kb = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.KeyBinds;

        _playPauseCommand ??= new FreeVideoStudio.App.ViewModels.RelayCommand(TogglePlayPause);
        _markStartCommand ??= new FreeVideoStudio.App.ViewModels.RelayCommand(ExecuteMarkStart);
        _markEndCommand ??= new FreeVideoStudio.App.ViewModels.RelayCommand(ExecuteMarkEnd);

        void Attach(string name, Avalonia.Input.Key key, System.Windows.Input.ICommand command)
        {
            var btn = this.FindControl<Button>(name);
            if (btn == null) return;
            btn.Command = command;
            btn.KeyBindings.Clear();
            btn.KeyBindings.Add(new Avalonia.Input.KeyBinding
            {
                Gesture = new Avalonia.Input.KeyGesture(key),
                Command = command
            });
        }

        Attach("GranularPlayPause", kb.PlayPause, _playPauseCommand);
        Attach("MarkStartBtn", kb.MarkStart, _markStartCommand);
        Attach("MarkEndBtn", kb.MarkEnd, _markEndCommand);
    }

    private void TogglePlayPause()
    {
        RuntimeLog.Info("UI", "User toggled Play/Pause in Granular Speed Editor.");
        bool willPlay = _isCurrentlyFrozen || (_videoHost?.IpcClient?.IsPaused == true);
        if (willPlay && _zoomModeActive)
        {
            if (_hasZoomBox && !_zoomBoxTouched && _edit.SelectedSegmentIndex >= 0 && _edit.SelectedSegmentIndex < _edit.Segments.Count && !_edit.Segments[_edit.SelectedSegmentIndex].ZoomW.HasValue)
            {
                CommitZoomToSegment("PlayStarted");
            }
            ExitZoomMode();
        }

        if (_isCurrentlyFrozen)
        {
            _isCurrentlyFrozen = false;
            _freezeSound.Stop();
            _holdCaretOutSec = null;
            if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            return;
        }
        if (_videoHost?.IpcClient != null) _ = _videoHost.IpcClient.SetPropertyAsync("pause", _videoHost.IpcClient.IsPaused ? "no" : "yes");
    }

    private void ExecuteMarkStart()
    {
        RuntimeLog.Info("UI", "User clicked Mark Start in Granular Speed Editor.");

        int currentMs = (int)(GetCurrentTime() * 1000);
        if (_edit.MarkStart(currentMs) is int overlap)
        {
            var overlapping = _edit.Segments[overlap];
            ShowFeedback($"⚠ Inside block #{overlap + 1}! Delete it first.");
            NotifyError($"Cannot mark here — overlaps block #{overlap + 1} [{FormatMs(overlapping.StartMs)} – {FormatMs(overlapping.EndMs)}]. Delete it first.");
            return;
        }

        UpdateDeleteButtonVisibility();
        _freezeAwaitingLength = false;
        ShowFeedback($"START: {FormatMs(_edit.PendingStartMs)}");
        RedrawTimeline();
    }

    private void ExecuteMarkEnd()
    {
        RuntimeLog.Info("UI", "User clicked Mark End in Granular Speed Editor.");

        int currentMs = (int)(GetCurrentTime() * 1000);
        var marked = _edit.MarkEnd(currentMs, out int? overlap);
        if (!marked.Ok)
        {
            ShowFeedback(overlap is int o ? $"⚠ Inside block #{o + 1}! Delete it first." : "⚠ END can't be before START");
            NotifyError(marked.Error!);
            return;
        }

        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

            var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
            var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
            if (playIcon != null && pauseIcon != null)
            {
                playIcon.IsVisible = true;
                pauseIcon.IsVisible = false;
            }
        }

        NotifyUndoable($"Block added at {FormatMs(_edit.PendingEndMs)}", "MarkEndBtn");

        AddPendingSegment();

        if (_edit.Segments.Count > 0)
        {
            _edit.SelectedSegmentIndex = _edit.Segments.Count - 1;
        }
        UpdateDeleteButtonVisibility();
        RedrawTimeline();
    }

    private void GranularKeyUpHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (FreeVideoStudio.App.Infrastructure.KeyboardFocusPolicy.HotkeysSuspended(Avalonia.Controls.TopLevel.GetTopLevel(this)))
            return;

        var kb = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);

        if (playPause.Matches(e) || markStart.Matches(e) || markEnd.Matches(e) || e.Key is Avalonia.Input.Key.Space or Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            e.Handled = true;
        }
    }

    private void GranularKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (FreeVideoStudio.App.Infrastructure.KeyboardFocusPolicy.HotkeysSuspended(Avalonia.Controls.TopLevel.GetTopLevel(this)))
            return;

        var kb = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.KeyBinds;

        if (e.Key == Avalonia.Input.Key.Escape && _zoomModeActive)
        {
            ExitZoomMode();
            e.Handled = true;
            return;
        }

        if (e.Key == Avalonia.Input.Key.Escape
            && (_isFreezeCameraSelected || _edit.SelectedSegmentIndex >= 0))
        {
            ClearTimelineSelection();
            e.Handled = true;
            return;
        }

        if (_isFreezeCameraSelected && _edit.FreezeTimeMs >= 0 && e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            int frames = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 10 : 1;
            int dir = e.Key == Avalonia.Input.Key.Left ? -frames : frames;
            if (_freezeFocus == FreezeMarkerEnd.End) NudgeFreezeDurationByFrames(dir);
            else MoveFreezeCameraByFrames(dir);
            e.Handled = true;
            return;
        }

        if (_edit.SelectedMemeId != null && _draggingMemeId == null && e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
        {
            if (e.Key is Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back)
            {
                RemoveSelectedMeme();
                e.Handled = true;
                return;
            }
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                _ = EditMemeAsync(_edit.SelectedMemeId);
                e.Handled = true;
                return;
            }
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                _edit.SelectedMemeId = null;
                UpdateMemeButtonsState();
                RedrawTimeline();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Avalonia.Input.Key.Delete || e.Key == Avalonia.Input.Key.Back)
        {
            if (_selectedCaptionLineId.HasValue)
            {
                DeleteCaptionLine(_selectedCaptionLineId.Value);
                e.Handled = true;
                return;
            }

            if (_edit.SelectedSegmentIndex >= 0 && _edit.SelectedSegmentIndex < _edit.Segments.Count)
            {
                RequestDeleteSegment(_edit.SelectedSegmentIndex);
                e.Handled = true;
                return;
            }
        }

        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var markStart = new Avalonia.Input.KeyGesture(kb.MarkStart);
        var markEnd = new Avalonia.Input.KeyGesture(kb.MarkEnd);
        var seekFwd = new Avalonia.Input.KeyGesture(kb.SeekForward);
        var seekBack = new Avalonia.Input.KeyGesture(kb.SeekBackward);
        var fineSeekFwdCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekFwdShift = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Shift);
        var fineSeekBackCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekBackShift = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Shift);

        if (playPause.Matches(e))
        {
            ExecuteTransportCommand(_playPauseCommand, e);
        }
        else if (markStart.Matches(e))
        {
            ExecuteTransportCommand(_markStartCommand, e);
        }
        else if (markEnd.Matches(e))
        {
            ExecuteTransportCommand(_markEndCommand, e);
        }
        else if (fineSeekFwdCtrl.Matches(e) || fineSeekFwdShift.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double trimEndSec = (_edit.TrimEndMs > 0) ? _edit.TrimEndMs / 1000.0 : double.MaxValue;
            double target = Math.Min(currentAbs + Infrastructure.PreviewKeySeek.StepSeconds, trimEndSec);
            _ = SeekInternal(target - (_edit.TrimStartMs / 1000.0));
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            double currentAbs = _videoHost?.IpcClient?.CurrentTime ?? 0;
            double target = Math.Max(currentAbs - Infrastructure.PreviewKeySeek.StepSeconds, _edit.TrimStartMs / 1000.0);
            _ = SeekInternal(target - (_edit.TrimStartMs / 1000.0));
            e.Handled = true;
        }
    }

    private async Task SeekInternal(double time) {
        long now = Environment.TickCount64;
        if (_isSeeking || (now - _lastSeekTimestamp < SeekCoalesceMs)) {
            _nextSeekTarget = time;
            EnsureSeekFlushTimer();
            return;
        }
        _isSeeking = true;
        _lastSeekTimestamp = now;
        double absTime = (_edit.TrimStartMs / 1000.0) + time;
        double trimEndSec = (_edit.TrimEndMs > 0) ? _edit.TrimEndMs / 1000.0 : double.MaxValue;
        absTime = Math.Min(absTime, Math.Max(0.0, trimEndSec - 0.005));
        absTime = Math.Max(absTime, _edit.TrimStartMs / 1000.0);
        try {
            if (_videoHost?.IpcClient != null) {
                await _videoHost.IpcClient.SendCommandAsync("seek", absTime.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            } else {
                _isSeeking = false;
            }
        } catch (System.Exception ex) {
            RuntimeLog.Swallowed(ex);
            _isSeeking = false;
        }
    }

    private void EnsureSeekFlushTimer()
    {
        if (_seekFlushTimer != null) { if (!_seekFlushTimer.IsEnabled) _seekFlushTimer.Start(); return; }

        _seekFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SeekCoalesceMs) };
        _seekFlushTimer.Tick += (_, _) =>
        {
            if (!_nextSeekTarget.HasValue) { _seekFlushTimer?.Stop(); return; }
            if (_isSeeking) return;
            if (Environment.TickCount64 - _lastSeekTimestamp < SeekCoalesceMs) return;

            double target = _nextSeekTarget.Value;
            _nextSeekTarget = null;
            _seekFlushTimer?.Stop();
            _ = SeekInternal(target);
        };
        _seekFlushTimer.Start();
    }

    private async Task LoadVideoAsync()
    {
        if (string.IsNullOrWhiteSpace(_edit.VideoPath) || _videoHost?.IpcClient == null) return;

        double startSec = _edit.TrimStartMs / 1000.0;

        await _videoHost.IpcClient.LoadFileAsync(_edit.VideoPath, startSec);
        await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        _previewSpeed.Invalidate();
        RuntimeLog.Info("Granular", $"Loaded preview video at {startSec:0.###}s.");
    }

    private void WireUpControls()
    {
        var canvas = _segmentCanvas;
        if (canvas != null)
        {
            canvas.SizeChanged += (s, e) => RedrawTimeline();

            canvas.IsHitTestVisible = true;

            canvas.PointerPressed += (s, e) =>
            {
                double dur = GetDuration();
                if (dur <= 0) return;
                double w = canvas.Bounds.Width;
                if (w <= 0) return;
                double totalMs = dur * 1000.0;
                double msPerPx = totalMs / w;
                double pointerMs = Math.Clamp(XToSrcMs(e.GetPosition(canvas).X, w), 0, totalMs);
                double edgeMs = 8.0 * msPerPx;

                if (_edit.FreezeTimeMs >= 0 && _edit.FreezeDurationS > 0)
                {
                    double outSecAtPress = OutXToOutSec(e.GetPosition(canvas).X, w);
                    double holdStart = FreezeHoldStartOutSec();
                    double holdEnd = holdStart + _edit.FreezeDurationS;
                    double gripSec = Math.Min(8.0 * (OutDurationSec() / w), _edit.FreezeDurationS / 3.0);

                    if (outSecAtPress >= holdStart - gripSec && outSecAtPress <= holdEnd + gripSec)
                    {
                        var fm = FreezeDragMode.Move;
                        if (Math.Abs(outSecAtPress - holdStart) <= gripSec) fm = FreezeDragMode.ResizeStart;
                        else if (Math.Abs(outSecAtPress - holdEnd) <= gripSec) fm = FreezeDragMode.ResizeEnd;

                        _freezeDragMode = fm;
                        _freezeDragFixedEndOutSec = holdEnd;
                        _freezeDragGrabOffsetSec = Math.Max(0,
                            OutXToBaseOutSec(e.GetPosition(canvas).X, w) - holdStart);
                        _isFreezeCameraSelected = true;
                        _edit.SelectedSegmentIndex = -1;
                        UpdateDeleteButtonVisibility();
                        e.Pointer.Capture(canvas);
                        SetStatus(fm switch
                        {
                            FreezeDragMode.ResizeStart => "Dragging the freeze START — release to set.",
                            FreezeDragMode.ResizeEnd => "Dragging the freeze END — release to set.",
                            _ => "Moving the freeze — release to set."
                        });
                        RedrawTimeline();
                        e.Handled = true;
                        return;
                    }
                }

                int hitIdx = -1;
                SegDragMode mode = SegDragMode.None;
                double bestEdgeDist = double.MaxValue;
                for (int i = 0; i < _edit.Segments.Count; i++)
                {
                    var sg = _edit.Segments[i];
                    double effEdgeMs = Math.Min(edgeMs, Math.Max(0.0, sg.EndMs - sg.StartMs) / 3.0);
                    double dStart = Math.Abs(pointerMs - sg.StartMs);
                    double dEnd = Math.Abs(pointerMs - sg.EndMs);
                    bool startWinsTie = pointerMs >= sg.StartMs;
                    bool endWinsTie = pointerMs <= sg.EndMs;
                    if (dStart <= effEdgeMs && (dStart < bestEdgeDist || (dStart == bestEdgeDist && startWinsTie))) { bestEdgeDist = dStart; hitIdx = i; mode = SegDragMode.ResizeStart; }
                    if (dEnd <= effEdgeMs && (dEnd < bestEdgeDist || (dEnd == bestEdgeDist && endWinsTie))) { bestEdgeDist = dEnd; hitIdx = i; mode = SegDragMode.ResizeEnd; }
                }
                if (hitIdx < 0)
                {
                    for (int i = 0; i < _edit.Segments.Count; i++)
                    {
                        var sg = _edit.Segments[i];
                        if (pointerMs > sg.StartMs && pointerMs < sg.EndMs) { hitIdx = i; mode = SegDragMode.Move; break; }
                    }
                }

                if (hitIdx >= 0 && mode != SegDragMode.None)
                {
                    SelectSegment(hitIdx, jumpPlayhead: true);
                    var seg = _edit.Segments[hitIdx];

                    if (e.GetCurrentPoint(canvas).Properties.IsRightButtonPressed)
                    {
                        ShowSegmentContextMenu(canvas, hitIdx);
                        e.Handled = true;
                        return;
                    }

                    Crumb($"seg-drag PRESS idx={hitIdx} mode={mode} pointerMs={pointerMs:0}");
                    RuntimeLog.Info("Granular", $"Segment drag started: #{hitIdx + 1} mode={mode}.");
                    _draggingSegmentIndex = hitIdx;
                    _segDragMode = mode;
                    _dragOrigStartMs = seg.StartMs;
                    _dragOrigEndMs = seg.EndMs;
                    _dragOrigZoomStartMs = seg.ZoomStartMs;
                    _dragOrigZoomEndMs = seg.ZoomEndMs;
                    _segDragTimeline = OutTimeline();
                    _segDragOutDurationSec = OutDurationSec();
                    _dragStartPointerMs = pointerMs;
                    e.Pointer.Capture(canvas);
                    SetStatus(mode == SegDragMode.Move
                        ? $"Moving block #{hitIdx + 1} — release to set."
                        : $"Resizing block #{hitIdx + 1} — release to set.");
                    UpdateDragReadout(seg.StartMs, seg.EndMs);
                    RedrawTimeline();
                    e.Handled = true;
                    return;
                }

                if (_isFreezeCameraSelected)
                {
                    _isFreezeCameraSelected = false;
                    _freezeFocus = FreezeMarkerEnd.None;
                }
                if (_edit.SelectedSegmentIndex >= 0)
                {
                    _edit.SelectedSegmentIndex = -1;
                    UpdateDeleteButtonVisibility();
                }

                _isCanvasScrubbing = true;
                e.Pointer.Capture(canvas);
                SetPlayheadFromScrub(pointerMs);
                RedrawTimeline();
                e.Handled = true;
            };

            canvas.PointerMoved += (s, e) =>
            {
                double dur = GetDuration();
                if (dur <= 0) return;
                double w = canvas.Bounds.Width;
                if (w <= 0) return;
                double totalMs = dur * 1000.0;
                double msPerPx = totalMs / w;
                double pointerMs = Math.Clamp(XToSrcMs(e.GetPosition(canvas).X, w), 0, totalMs);

                if (PumpMemeDrag(e, canvas)) return;

                if (_isCanvasScrubbing)
                {
                    SetPlayheadFromScrub(pointerMs);
                    e.Handled = true;
                    return;
                }

                if (_zoomDragSegment >= 0 && _zoomDragSegment < _edit.Segments.Count)
                {
                    double zx = Math.Clamp(e.GetPosition(canvas).X, 0, w);
                    double newMs = ClampZoomEdgeAgainstSlowNeighbours(
                        _zoomDragSegment, XToSrcMs(zx, w), _zoomDragIsStart);

                    var zseg = _edit.Segments[_zoomDragSegment];

                    var (zLower, zUpper, _, _) = _edit.NeighbourBounds(_zoomDragSegment, zseg.StartMs, zseg.EndMs, totalMs);

                    if (_zoomDragIsStart)
                    {
                        double zNewStart = Math.Clamp(newMs, zLower,
                            Math.Max(zLower, zseg.EndMs - GranularEditSession.SegMinWidthMs));
                        PushUndo("move zoom box", "zoom-edge");
                        _edit.Segments[_zoomDragSegment] = zseg with
                        {
                            StartMs = zNewStart,
                            ZoomStartMs = zNewStart
                        };
                    }
                    else
                    {
                        double zNewEnd = Math.Clamp(newMs,
                            Math.Min(zUpper, zseg.StartMs + GranularEditSession.SegMinWidthMs), zUpper);
                        _edit.Segments[_zoomDragSegment] = zseg with
                        {
                            EndMs = zNewEnd,
                            ZoomEndMs = zNewEnd
                        };
                    }

                    var zNow = _edit.Segments[_zoomDragSegment];
                    UpdateDragReadout(zNow.StartMs, zNow.EndMs);
                    RedrawTimeline();

                    double zFollowRelSec = (_zoomDragIsStart ? zNow.StartMs : zNow.EndMs) / 1000.0;
                    _ = SeekInternal(zFollowRelSec);

                    e.Handled = true;
                    return;
                }

                if (_freezeDragMode != FreezeDragMode.None)
                {
                    PushUndo(_freezeDragMode == FreezeDragMode.Move ? "move freeze" : "change freeze length",
                             "freeze-drag");

                    double px = e.GetPosition(canvas).X;
                    double holdStartNow = FreezeHoldStartOutSec();

                    switch (_freezeDragMode)
                    {
                        case FreezeDragMode.ResizeEnd:
                            _edit.FreezeDurationS = Math.Clamp(
                                OutXToOutSec(px, w) - holdStartNow, GranularEditSession.MinFreezeDurationS, GranularEditSession.MaxFreezeDurationS);
                            break;

                        case FreezeDragMode.ResizeStart:
                        {
                            double newHoldStartSec = Math.Clamp(OutXToBaseOutSec(px, w),
                                0, Math.Max(0, _freezeDragFixedEndOutSec - GranularEditSession.MinFreezeDurationS));
                            SetFreezeStartFromBaseOutSec(newHoldStartSec);
                            _edit.FreezeDurationS = Math.Clamp(
                                _freezeDragFixedEndOutSec - newHoldStartSec, GranularEditSession.MinFreezeDurationS, GranularEditSession.MaxFreezeDurationS);
                            break;
                        }

                        default:
                            SetFreezeStartFromBaseOutSec(OutXToBaseOutSec(px, w) - _freezeDragGrabOffsetSec);
                            break;
                    }

                    _edit.FreezeDurationS = Math.Round(_edit.FreezeDurationS, 2);
                    ClampFreezeIntoClip();
                    UpdateDragReadout(_edit.FreezeTimeMs - _edit.TrimStartMs,
                                      _edit.FreezeTimeMs - _edit.TrimStartMs + _edit.FreezeDurationS * 1000.0);
                    RedrawTimeline();

                    _ = SeekInternal(Math.Max(0, (_edit.FreezeTimeMs - _edit.TrimStartMs) / 1000.0));

                    e.Handled = true;
                    return;
                }


                if (_draggingSegmentIndex < 0 || _draggingSegmentIndex >= _edit.Segments.Count || _segDragMode == SegDragMode.None)
                    return;

                int idx = _draggingSegmentIndex;

                var (lowerBound, upperBound, lowerBlockIdx, upperBlockIdx) =
                    _edit.NeighbourBounds(idx, _dragOrigStartMs, _dragOrigEndMs, totalMs);

                double newStart = _dragOrigStartMs;
                double newEnd = _dragOrigEndMs;

                upperBound = Math.Min(totalMs, Math.Max(upperBound, lowerBound + GranularEditSession.SegMinWidthMs));

                bool hitLeftWall = false;
                bool hitRightWall = false;

                if (_segDragMode == SegDragMode.Move)
                {
                    double width = Math.Max(GranularEditSession.SegMinWidthMs, _dragOrigEndMs - _dragOrigStartMs);
                    double delta = pointerMs - _dragStartPointerMs;
                    double desiredStart = _dragOrigStartMs + delta;
                    newStart = Math.Clamp(desiredStart, lowerBound, Math.Max(lowerBound, upperBound - width));
                    newEnd = newStart + width;

                    if (desiredStart <= lowerBound && lowerBound > 0 && lowerBlockIdx >= 0) hitLeftWall = true;
                    if (desiredStart >= upperBound - width && upperBound < totalMs && upperBlockIdx >= 0) hitRightWall = true;
                }
                else if (_segDragMode == SegDragMode.ResizeStart)
                {
                    double desiredStart = pointerMs;
                    newStart = Math.Clamp(desiredStart, lowerBound, Math.Max(lowerBound, _dragOrigEndMs - GranularEditSession.SegMinWidthMs));
                    newEnd = _dragOrigEndMs;

                    if (desiredStart <= lowerBound && lowerBound > 0 && lowerBlockIdx >= 0) hitLeftWall = true;
                }
                else if (_segDragMode == SegDragMode.ResizeEnd)
                {
                    double desiredEnd = pointerMs;
                    newEnd = Math.Clamp(desiredEnd, Math.Min(upperBound, _dragOrigStartMs + GranularEditSession.SegMinWidthMs), upperBound);
                    newStart = _dragOrigStartMs;

                    if (desiredEnd >= upperBound && upperBound < totalMs && upperBlockIdx >= 0) hitRightWall = true;
                }

                double snapTol = 8.0 * msPerPx;
                double playheadMs = _playheadMs;
                double NearestSnap(double ms)
                {
                    double best = ms, bestD = snapTol;
                    void Try(double t) { if (t < 0) return; double d = Math.Abs(ms - t); if (d < bestD) { bestD = d; best = t; } }
                    Try(0); Try(totalMs); Try(playheadMs);
                    for (int j = 0; j < _edit.Segments.Count; j++) { if (j == idx) continue; Try(_edit.Segments[j].StartMs); Try(_edit.Segments[j].EndMs); }
                    return best;
                }
                double segWidth = newEnd - newStart;
                if (_segDragMode == SegDragMode.Move)
                {
                    double sS = NearestSnap(newStart), sE = NearestSnap(newEnd);
                    if (Math.Abs(sS - newStart) <= Math.Abs(sE - newEnd) && sS != newStart)
                        {
                            newStart = Math.Clamp(sS, lowerBound, Math.Max(lowerBound, upperBound - segWidth)); newEnd = newStart + segWidth; }
                    else if (sE != newEnd)
                        {
                            newEnd = Math.Clamp(sE, Math.Min(upperBound, lowerBound + segWidth), upperBound); newStart = newEnd - segWidth; }
                }
                else if (_segDragMode == SegDragMode.ResizeStart)
                    newStart = Math.Clamp(NearestSnap(newStart), lowerBound, Math.Max(lowerBound, newEnd - GranularEditSession.SegMinWidthMs));
                else if (_segDragMode == SegDragMode.ResizeEnd)
                    newEnd = Math.Clamp(NearestSnap(newEnd), Math.Min(upperBound, newStart + GranularEditSession.SegMinWidthMs), upperBound);

                if (hitLeftWall)
                    SetStatus("Blocked on the left by the previous block.");
                else if (hitRightWall)
                    SetStatus("Blocked on the right by the next block.");
                else
                {
                    bool explained = false;
                    if (_segDragMode == SegDragMode.Move && idx >= 0 && idx < _edit.Segments.Count)
                    {
                        var seg = _edit.Segments[idx];
                        if (seg.ZoomW.HasValue && seg.ZoomSlow)
                        {
                            double delta = newStart - _dragOrigStartMs;
                            double? zs = _dragOrigZoomStartMs.HasValue ? _dragOrigZoomStartMs.Value + delta : newStart;
                            double? ze = _dragOrigZoomEndMs.HasValue ? _dragOrigZoomEndMs.Value + delta : newEnd;
                            var movedSeg = seg with { StartMs = newStart, EndMs = newEnd, ZoomStartMs = zs, ZoomEndMs = ze };
                            var moveAvail = _edit.GetSlowZoomGlideAvailability(idx, overrideSegment: movedSeg);
                            if (moveAvail.AnyUnavailable)
                            {
                                SetStatus(moveAvail.GetCombinedExplanation() ?? $"Moving block #{idx + 1} — release to set.");
                                explained = true;
                            }
                        }
                    }
                    if (!explained)
                        SetStatus(_segDragMode == SegDragMode.Move ? $"Moving block #{idx + 1} — release to set." : $"Resizing block #{idx + 1} — release to set.");
                }

                PushUndo("resize block", "seg-edge");
                double actualDelta = newStart - _dragOrigStartMs;
                double? newZoomStart = (_segDragMode == SegDragMode.Move && _dragOrigZoomStartMs.HasValue)
                    ? _dragOrigZoomStartMs.Value + actualDelta
                    : _edit.Segments[idx].ZoomStartMs;
                double? newZoomEnd = (_segDragMode == SegDragMode.Move && _dragOrigZoomEndMs.HasValue)
                    ? _dragOrigZoomEndMs.Value + actualDelta
                    : _edit.Segments[idx].ZoomEndMs;
                if (totalMs > GranularEditSession.SegMinWidthMs)
                {
                    newStart = Math.Clamp(newStart, 0, totalMs - GranularEditSession.SegMinWidthMs);
                    newEnd = Math.Clamp(newEnd, newStart + GranularEditSession.SegMinWidthMs, totalMs);
                }
                else
                {
                    newStart = 0;
                    newEnd = Math.Max(0, totalMs);
                }

                LogDragSample(idx, pointerMs, newStart, newEnd, totalMs, lowerBound, upperBound);

                Crumb($"seg-drag MOVE idx={idx} mode={_segDragMode} -> {newStart:0}..{newEnd:0} : commit");
                _edit.Segments[idx] = _edit.Segments[idx] with { StartMs = newStart, EndMs = newEnd, ZoomStartMs = newZoomStart, ZoomEndMs = newZoomEnd };
                Crumb($"seg-drag MOVE idx={idx} : readout");
                UpdateDragReadout(newStart, newEnd);
                Crumb($"seg-drag MOVE idx={idx} : visuals");
                UpdateDraggingVisuals(idx, newStart, newEnd);
                double followRelSec = (_segDragMode == SegDragMode.ResizeEnd ? newEnd : newStart) / 1000.0;
                Crumb($"seg-drag MOVE idx={idx} : seek {followRelSec:0.000}");
                _ = SeekInternal(followRelSec);
                Crumb($"seg-drag MOVE idx={idx} : done");
                e.Handled = true;
            };
        }

        WireDeletePartsButton();
        WireMemeButtons();
        WireUndoRedo();

        var speedSlider = PendingSpeedSliderCtl;
        if (speedSlider != null)
        {
            speedSlider.ValueChanged += (_, e) =>
            {
                _edit.PendingSpeed = Math.Round(e / 10.0, 2);
                var lbl = PendingSpeedLabelCtl;
                if (lbl != null) lbl.Text = $"{_edit.PendingSpeed:0.0}x";

                if (_edit.ApplyPendingSpeedToSelection("seg-speed"))
                {
                    RefreshSegmentList();
                    RedrawTimeline();
                }
            };
        }

        WireUpSpeedPresets(speedSlider);

        WireUpFreezeImage();

        var deleteSegBtn = DeleteSegmentBtnCtl;
        deleteSegBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            RequestDeleteSegment(_edit.SelectedSegmentIndex);
        });

        var clearBtn = ClearAllSegmentsBtnCtl;
        if (clearBtn != null)
            Infrastructure.ConfirmFlyoutGate.Attach(clearBtn, () => FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ConfirmGranularClearAll);
        clearBtn?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            UpdateClearAllPromptText();
            if (!FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ConfirmGranularClearAll)
            {
                clearBtn.Flyout?.Hide();
                ExecuteClearAllSegments();
            }
        });

        var confirmClearAll = this.FindControl<Button>("ConfirmClearAllSegmentsBtn");
        confirmClearAll?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            ClearAllSegmentsBtnCtl?.Flyout?.Hide();
            ExecuteClearAllSegments();
        });

        var keepAll = this.FindControl<Button>("KeepAllSegmentsBtn");
        keepAll?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            ClearAllSegmentsBtnCtl?.Flyout?.Hide();
            RuntimeLog.Info("UI", "User backed out of Clear All in Granular Speed Editor.");
        });

        var acceptBtn = this.FindControl<Button>("AcceptGranularBtn");
        _applyGranularBtn = acceptBtn;
        if (acceptBtn != null) acceptBtn.Click += async (s, e) => {
            RuntimeLog.Info("UI", "User clicked Accept in Granular Speed Editor.");
            if (!await PrepareApplyWithAiTrackingAsync()) return;
            if (!await PrepareApplyWithCommentaryAsync()) return;
            if (_zoomModeActive && _hasZoomBox && _edit.SelectedSegmentIndex >= 0 && _edit.SelectedSegmentIndex < _edit.Segments.Count && !_edit.Segments[_edit.SelectedSegmentIndex].ZoomW.HasValue)
            {
                CommitZoomToSegment("AcceptedDefault");
            }
            Accepted = true;
            ClearUndoHistory("changes applied");
            Close();
        };

        var cancelBtn = CancelGranularBtnCtl;
        if (cancelBtn != null)
        {
            _cancelConfirmFlyout = cancelBtn.Flyout;
            cancelBtn.Click += (_, _) =>
            {
                if (cancelBtn.Flyout != null) return;
                RuntimeLog.Info("UI", "Cancel in Granular Speed Editor with no changes — closing without prompting.");
                Avalonia.Threading.Dispatcher.UIThread.Post(Close, Avalonia.Threading.DispatcherPriority.Background);
            };
        }
        UpdateCancelConfirmState();

        var confirmCancel = this.FindControl<Button>("ConfirmCancelGranularBtn");
        confirmCancel?.AddHandler(Button.ClickEvent, (_, _) =>
        {
            var btn = CancelGranularBtnCtl;
            btn?.Flyout?.Hide();
            RuntimeLog.Info("UI", "User confirmed Cancel in Granular Speed Editor.");
            Avalonia.Threading.Dispatcher.UIThread.Post(Close, Avalonia.Threading.DispatcherPriority.Background);
        });

        UpdateTooltips();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, GranularKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyUpEvent, GranularKeyUpHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void WireUpFreezeImage()
    {
        var freezeImageToggle = FreezeImageToggleCtl;

        var freezePresets = new[] {
            this.FindControl<Button>("FreezePreset05"), this.FindControl<Button>("FreezePreset10"), this.FindControl<Button>("FreezePreset15"),
            this.FindControl<Button>("FreezePreset20"), this.FindControl<Button>("FreezePreset25"), this.FindControl<Button>("FreezePreset30")
        };
        double[] presetValues = { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 };

        Avalonia.Media.IBrush PresetBrush(string key, string fallbackHex)
            => this.TryFindResource(key, ActualThemeVariant, out object? v) && v is Avalonia.Media.IBrush b
                ? b
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(fallbackHex));

        var selectedBg = PresetBrush("AppSelectedPresetBackground", "#14532d");
        var selectedBorder = PresetBrush("AppSelectedPresetBorder", "#22c55e");
        var selectedFg = PresetBrush("AppSelectedPresetForeground", "#86efac");

        void SetFreezePresetSelection(int selectedIndex)
        {
            for (int j = 0; j < freezePresets.Length; j++)
            {
                var preset = freezePresets[j];
                if (preset == null) continue;

                if (j == selectedIndex)
                {
                    preset.Classes.Remove("Primary");
                    preset.Background = selectedBg;
                    preset.BorderBrush = selectedBorder;
                    preset.Foreground = selectedFg;
                }
                else
                {
                    preset.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                    preset.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                    preset.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                }
            }
        }

        int stepperIndex = 0;
        int _freezePulseCount = 0;
        _freezePulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _freezePulseTimer.Tick += (_, _) =>
        {
            stepperIndex = (stepperIndex + 1) % freezePresets.Length;
            _freezePulseCount++;

            var hint1 = FreezeHintLabelCtl;
            var hint2 = FreezeHintLabelBottomCtl;
            double newOpacity = (stepperIndex % 2 == 0) ? 1.0 : 0.0;
            if (hint1 != null) hint1.Opacity = newOpacity;
            if (hint2 != null) hint2.Opacity = newOpacity;

            if (_freezePulseCount >= 20)
            {
                _freezePulseTimer?.Stop();
                if (hint1 != null) hint1.Opacity = 1.0;
                if (hint2 != null) hint2.Opacity = 1.0;
            }

            for (int j = 0; j < freezePresets.Length; j++)
            {
                var b = freezePresets[j];
                if (b == null) continue;

                bool isSelected = (Math.Abs(_edit.SelectedFreezePresetS - presetValues[j]) < 0.01);
                if (isSelected) continue;

                if (j == stepperIndex)
                {
                    b.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(120, 90, 26));
                    b.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(250, 197, 22));
                    b.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 247, 237));
                }
                else
                {
                    b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
                    b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
                    b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
                }
            }
        };

        for (int i = 0; i < freezePresets.Length; i++)
        {
            var btn = freezePresets[i];
            var val = presetValues[i];
            int presetIndex = i;
            if (btn != null)
            {
                btn.Click += (_, _) =>
                {
                    _edit.SelectedFreezePresetS = val;
                    OnFreezeLengthChosen();
                    RememberEditorChoice(d => Math.Abs(d.DefaultFreezeDurationS - val) > 0.001, d => d.DefaultFreezeDurationS = val);

                    _freezePulseTimer?.Stop();

                    SetFreezePresetSelection(presetIndex);

                    var hint = FreezeHintLabelCtl;
                    if (hint != null) hint.IsVisible = false;
                    var hintBottom = FreezeHintLabelBottomCtl;
                    if (hintBottom != null) hintBottom.IsVisible = false;

                    SetFreezePromptControlsEnabled(true);

                    var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("FreezeValidationPopup");
                    if (popup != null) popup.IsOpen = false;

                    if (_edit.SetFreezeDuration(val, "freeze-len"))
                    {
                        EndUndoGesture();
                        RedrawTimeline();
                        FreeVideoStudio.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked freeze preset button. Set freeze duration to {val}s.");
                        ShowFeedback($"FREEZE CREATED: {val:0.0}s");
                    }
                };
            }
        }

        if (freezeImageToggle != null)
        {
            freezeImageToggle.Click += async (_, _) =>
            {
                if (_edit.FreezeTimeMs < 0)
                {
                    if (_edit.SelectedFreezePresetS < 0)
                    {
                        double wanted = Infrastructure.SettingsManager.Instance.Defaults.DefaultFreezeDurationS;
                        if (wanted > 0)
                            _edit.SelectedFreezePresetS = presetValues.OrderBy(v => Math.Abs(v - wanted)).First();
                    }
                    bool promptPreset = (_edit.SelectedFreezePresetS < 0);

                    if (promptPreset)
                    {
                        ShowFeedback("SELECT FREEZE DURATION");

                        _freezePulseCount = 0;
                        _freezePulseTimer?.Start();

                        var hint = FreezeHintLabelCtl;
                        if (hint != null) hint.IsVisible = true;
                        var hintBottom = FreezeHintLabelBottomCtl;
                        if (hintBottom != null) hintBottom.IsVisible = true;

                        SetFreezePromptControlsEnabled(false);

                        FreeVideoStudio.App.RuntimeLog.Info("GRANULAR_EDITOR", "State Change: User clicked 'Freeze Image' toggle but no preset was selected. Showing hint + gentle pulse + greying out other controls.");
                    }

                    double currentAbsMs = (_videoHost?.IpcClient?.CurrentTime ?? 0) * 1000.0;
                    if (_videoHost != null && _videoHost.IpcClient != null) {
                        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                    }
                    _edit.SetFreezeAt(currentAbsMs,
                        promptPreset ? Infrastructure.SettingsManager.Instance.Defaults.DefaultFreezeDurationS : _edit.SelectedFreezePresetS);

                    var icon = FreezeImageToggleIconCtl;
                    var txt = FreezeImageToggleTextCtl;
                    if (icon != null) icon.Text = "🔓";
                    if (txt != null) txt.Text = "UNFREEZE IMAGE";
                    freezeImageToggle.Classes.Remove("Primary");
                    freezeImageToggle.Classes.Add("Danger");

                    RedrawTimeline();
                    UpdateDeleteButtonVisibility();
                    StartFreezeGlow();
                    FreeVideoStudio.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked 'Freeze Image' toggle. Button set to State 2 (Active/Red - UNFREEZE IMAGE).");

                    if (!promptPreset)
                    {
                        NotifyUndoable($"Freeze created ({_edit.FreezeDurationS:0.0}s)", "FreezeImageToggle");
                        for (int k = 0; k < presetValues.Length; k++)
                        {
                            if (Math.Abs(presetValues[k] - _edit.SelectedFreezePresetS) < 0.01)
                            {
                                SetFreezePresetSelection(k);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    _edit.RemoveFreeze();
                    ClearFreezeImage("FREEZE IMAGE REMOVED");
                    FreeVideoStudio.App.RuntimeLog.Info("GRANULAR_EDITOR", $"State Change: User clicked 'Unfreeze Image' toggle. Button released to State 1 (Default/Blue - FREEZE IMAGE). Existing freeze instance was deleted from the timeline.");
                }
            };
        }
    }

    private void WireUpSpeedPresets(FreeVideoStudio.App.Controls.SpinningWheelSlider? speedSlider)
    {
        SpeedPresetButtons.ConfigureBaseButton(
            this,
            _edit.BaseSpeed,
            $"Set speed to Main screen base speed {SpeedPresetButtons.FormatSpeed(_edit.BaseSpeed)}");

        SpeedPresetButtons.WirePresetButtons(this, _edit.BaseSpeed, s =>
        {
            SpeedPresetButtons.SetSpinningWheelValue(speedSlider, s);
            _edit.PendingSpeed = s;
            var lbl = PendingSpeedLabelCtl;
            if (lbl != null) lbl.Text = $"{s:0.0}x";

            if (_edit.ApplyPendingSpeedToSelection(null))
            {
                RefreshSegmentList();
                RedrawTimeline();
            }
        });
    }

    private Avalonia.Controls.Primitives.FlyoutBase? _cancelConfirmFlyout;

    private void UpdateCancelConfirmState()
    {
        var btn = CancelGranularBtnCtl;
        if (btn == null || _cancelConfirmFlyout == null) return;
        var wanted = _edit.IsDirty ? _cancelConfirmFlyout : null;
        if (!ReferenceEquals(btn.Flyout, wanted)) btn.Flyout = wanted;
    }

    private void UpdateDeleteButtonVisibility()
    {
        UpdateCancelConfirmState();
        var deleteSegBtn = DeleteSegmentBtnCtl;
        var clearAllBtn = ClearAllSegmentsBtnCtl;

        bool segSelected = _edit.HasSelectedSegment;

        if (deleteSegBtn != null)
            deleteSegBtn.IsVisible = segSelected;

        if (clearAllBtn != null)
            clearAllBtn.IsVisible = _edit.Segments.Count > 0 || _edit.FreezeTimeMs >= 0;

        var removeZoomBtn = this.FindControl<Button>("RemoveZoomBtn");
        if (removeZoomBtn != null)
            removeZoomBtn.IsVisible = _edit.SelectedSegment?.ZoomW.HasValue == true;

        var zoomBtn = ZoomSegmentBtnCtl;
        if (zoomBtn != null)
        {
            zoomBtn.IsVisible = true;
            if (!segSelected && _zoomModeActive) ExitZoomMode();
        }
        UpdateAiSmartZoomBtnVisualState();
        UpdateAiCommentaryBtnVisualState();
        SyncZoomModeChecksFromSegment();
    }

    private void AddPendingSegment()
    {
        var added = _edit.AddPendingSegment(out SpeedSegment? seg);
        if (!added.Ok)
        {
            NotifyError(added.Error!);
            return;
        }

        var speedSlider = PendingSpeedSliderCtl;
        var speedLbl = PendingSpeedLabelCtl;
        if (speedSlider != null) SpeedPresetButtons.SetSpinningWheelValue(speedSlider, _edit.BaseSpeed);
        if (speedLbl != null) speedLbl.Text = $"{_edit.BaseSpeed:0.0}x";

        RefreshSegmentList();
        if (seg != null) Notify($"Block added: {FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)} @ {seg.Speed:0.0}x");
    }

    private void RefreshSegmentList()
    {
        var panel = this.FindControl<ListBox>("SegmentsPanel");
        if (panel == null) return;
        panel.Items.Clear();

        var countLbl = this.FindControl<TextBlock>("SegmentCountLabel");
        if (countLbl != null)
            countLbl.Text = _edit.Segments.Count == 0 ? "No blocks" : $"{_edit.Segments.Count} block{(_edit.Segments.Count == 1 ? "" : "s")}";

        for (int i = 0; i < _edit.Segments.Count; i++)
        {
            int idx = i;
            var seg = _edit.Segments[i];
            bool isSelected = idx == _edit.SelectedSegmentIndex;

            var border = new SegmentRowBorder
            {
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 1),
                Padding = new Thickness(7, 3),
                Focusable = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            PaintToken(border, Border.BackgroundProperty, isSelected ? "AppZoomCardCheckedBrush" : "AppZoomCardBrush");
            PaintToken(border, Border.BorderBrushProperty, isSelected ? "AppWarningBrush" : "AppBorderBrush");
            ToolTip.SetTip(border, "Click to select this block");
            Avalonia.Automation.AutomationProperties.SetName(border,
                $"Block {idx + 1} of {_edit.Segments.Count}, {FormatClock(seg.StartMs)} to {FormatClock(seg.EndMs)}, speed {seg.Speed:0.0}x" +
                (seg.ZoomW.HasValue ? ", zoomed" : "") + (isSelected ? ", selected" : ""));

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var info = new TextBlock
            {
                Text = $"{FormatClock(seg.StartMs)} → {FormatClock(seg.EndMs)}   {seg.Speed:0.0}x{(seg.ZoomW.HasValue ? "  🔍" : "")}",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(10.5),
                FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };
            PaintToken(info, TextBlock.ForegroundProperty, "AppTextPrimaryBrush");

            var delBtn = new Button
            {
                Content = "✕",
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Background = Infrastructure.ThemeResources.Brush(this, "AppDangerDeepBorderBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#521818"))),
                Foreground = Avalonia.Media.Brushes.White,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 0, 0)
            };
            Avalonia.Automation.AutomationProperties.SetName(delBtn, $"Delete block {idx + 1} of {_edit.Segments.Count}");
            ToolTip.SetTip(delBtn, "Delete this block");
            delBtn.Click += (_, e) =>
            {
                e.Handled = true;
                RequestDeleteSegment(idx);
            };

            void SelectThisSegment()
            {
                SelectSegment(idx, jumpPlayhead: true);
                SetStatus($"Selected block #{idx + 1}. Change speed, press DELETE to remove, or drag its zoom box to re-aim it.");
            }

            border.PointerEntered += (_, _) =>
            {
                if (_edit.SelectedSegmentIndex != idx)
                    PaintToken(border, Border.BackgroundProperty, "AppZoomCardHoverBrush");
            };
            border.PointerExited += (_, _) =>
            {
                if (_edit.SelectedSegmentIndex != idx)
                    PaintToken(border, Border.BackgroundProperty, "AppZoomCardBrush");
            };
            border.GotFocus += (_, _) =>
            {
                if (_edit.SelectedSegmentIndex != idx)
                    PaintToken(border, Border.BorderBrushProperty, "AppFocusOuterBrush");
            };
            border.LostFocus += (_, _) =>
            {
                if (_edit.SelectedSegmentIndex != idx)
                    PaintToken(border, Border.BorderBrushProperty, "AppBorderBrush");
            };
            border.Invoked += SelectThisSegment;
            border.PointerPressed += (_, _) => SelectThisSegment();
            border.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter || e.Key == Avalonia.Input.Key.Space)
                {
                    SelectThisSegment();
                    e.Handled = true;
                }
            };

            Grid.SetColumn(info, 0);
            Grid.SetColumn(delBtn, 1);
            grid.Children.Add(info);
            grid.Children.Add(delBtn);
            border.Child = grid;
            panel.Items.Add(border);
        }
    }

    private void AttachFreezeMarkerInteractions(Control marker, FreezeMarkerEnd which, Canvas timelineCanvas)
    {
        marker.PointerEntered += (_, _) => MainWindow.SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) =>
        {
            if (_freezeDragMode == FreezeDragMode.None) MainWindow.SetTimelineCameraHover(marker, false);
        };
        marker.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(marker).Properties;
            if (props.IsRightButtonPressed) { ClearTimelineSelection(); e.Handled = true; return; }
            if (!props.IsLeftButtonPressed) return;

            double w = timelineCanvas.Bounds.Width;
            if (w <= 0 || _edit.FreezeTimeMs < 0) return;

            double holdStart = FreezeHoldStartOutSec();
            double holdEnd = holdStart + _edit.FreezeDurationS;

            var grabbed = which;
            if (grabbed == FreezeMarkerEnd.None) return;

            FocusFreezeMarker(grabbed);

            _freezeDragMode = grabbed == FreezeMarkerEnd.Start
                ? FreezeDragMode.ResizeStart
                : FreezeDragMode.ResizeEnd;
            _freezeDragFixedEndOutSec = holdEnd;
            _freezeDragGrabOffsetSec = 0;

            marker.Focus();
            MainWindow.SetTimelineCameraHover(marker, true);
            e.Pointer.Capture(timelineCanvas);
            SetStatus(grabbed == FreezeMarkerEnd.Start
                ? "Dragging the freeze START — release to set."
                : "Dragging the freeze END — release to set.");
            RedrawTimeline();
            e.Handled = true;
        };
    }

    private void FocusFreezeMarker(FreezeMarkerEnd which)
    {
        _isFreezeCameraSelected = which != FreezeMarkerEnd.None;
        _freezeFocus = which;
        _zoomFocus = null;
        if (_edit.SelectedSegmentIndex >= 0)
        {
            _edit.SelectedSegmentIndex = -1;
            RefreshSegmentList();
        }
        UpdateDeleteButtonVisibility();
    }

    private void ClearTimelineSelection()
    {
        bool hadSomething = _isFreezeCameraSelected
                            || _freezeFocus != FreezeMarkerEnd.None
                            || _zoomFocus != null
                            || _edit.SelectedSegmentIndex >= 0;

        _isFreezeCameraSelected = false;
        _freezeFocus = FreezeMarkerEnd.None;
        _zoomFocus = null;
        _edit.SelectedSegmentIndex = -1;
        _freezeDragMode = FreezeDragMode.None;
        _zoomDragSegment = -1;
        _isDraggingZoomMarker = false;

        if (!hadSomething) return;

        UpdateDeleteButtonVisibility();
        RefreshSegmentList();
        RedrawTimeline();
        SetStatus("Nothing selected.");
    }

    private void SelectSegment(int index) => SelectSegment(index, jumpPlayhead: true);

    private void SelectSegment(int index, bool jumpPlayhead)
    {
        if (index < 0 || index >= _edit.Segments.Count) return;

        bool changed = index != _edit.SelectedSegmentIndex;

        if (changed && _zoomModeActive)
        {
            int countBefore = _edit.Segments.Count;
            int orphanWas = _zoomCreatedSegmentIndex;

            ExitZoomMode();

            if (_edit.Segments.Count < countBefore && orphanWas >= 0 && orphanWas < index) index--;

            if (index >= _edit.Segments.Count) index = _edit.Segments.Count - 1;
            if (index < 0) return;
        }

        _isFreezeCameraSelected = false;
        _freezeFocus = FreezeMarkerEnd.None;
        _zoomFocus = null;
        _edit.SelectedSegmentIndex = index;
        _edit.SelectedMemeId = null;

        var seg = _edit.Segments[index];
        _edit.PendingSpeed = seg.Speed;

        var speedSlider = PendingSpeedSliderCtl;
        var speedLbl = PendingSpeedLabelCtl;
        if (speedSlider != null && seg.Speed >= 0.01) SpeedPresetButtons.SetSpinningWheelValue(speedSlider, seg.Speed);
        if (speedLbl != null) speedLbl.Text = $"{seg.Speed:0.0}x";

        UpdateDeleteButtonVisibility();
        UpdateMemeButtonsState();
        SyncZoomModeChecksFromSegment();

        if (jumpPlayhead) JumpPlayheadToSegmentEdge(index, toStart: true);

        if (seg.ZoomW.HasValue && !_zoomModeActive) EnterZoomMode();

        RefreshSegmentList();
        RedrawTimeline();

        if (seg.ZoomW.HasValue && seg.ZoomSlow)
        {
            var avail = _edit.GetSlowZoomGlideAvailability(index);
            if (avail.AnyUnavailable)
            {
                SetStatus(avail.GetCombinedExplanation()!);
            }
        }
    }

    private void JumpPlayheadToSegmentEdge(int index, bool toStart)
    {
        if (index < 0 || index >= _edit.Segments.Count) return;
        try
        {
            var ipc = _videoHost?.IpcClient;
            if (ipc != null && !ipc.IsPaused) _ = ipc.SetPropertyAsync("pause", "yes");

            var seg = _edit.Segments[index];
            double relMs = Math.Max(0, (toStart ? seg.StartMs : seg.EndMs));
            SetPlayheadFromScrub(relMs);
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    private void ClampFreezeIntoClip() => _edit.ClampFreezeIntoClip(GetDuration());

    private void MoveFreezeCameraByFrames(int frameDelta)
    {
        if (!_edit.MoveFreezeByFrames(frameDelta, GetDuration(), "freeze-drag")) return;
        SeekGranularPreviewToFreezeMarker();
        RedrawTimeline();
        SetStatus($"Freeze moved to {FormatMs(_edit.FreezeTimeMs - _edit.TrimStartMs)}.");
    }

    private void NudgeFreezeDurationByFrames(int frameDelta)
    {
        if (!_edit.NudgeFreezeDurationByFrames(frameDelta)) return;
        RedrawTimeline();
        SetStatus($"Freeze held for {_edit.FreezeDurationS:0.00}s.");
    }

    private void SeekGranularPreviewToFreezeMarker()
    {
        if (_videoHost?.IpcClient == null || _edit.FreezeTimeMs < 0)
        {
            return;
        }

        _isCurrentlyFrozen = false;
        _freezeSound.Stop();
        _holdCaretOutSec = null;
        _freezeArmed = true;
        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        _ = _videoHost.IpcClient.SendCommandAsync(
            "seek",
            (_edit.FreezeTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "absolute");
    }

    private bool _redrawQueued;

    private void UpdateDraggingVisuals(int segIndex, double newStartMs, double newEndMs)
    {
        var canvas = _segmentCanvas;
        if (canvas == null) return;
        double w = canvas.Bounds.Width;
        double dur = GetDuration();
        if (dur <= 0 || w <= 0) return;

        double x1 = SrcMsToX(newStartMs, w);
        double x2 = SrcMsToX(newEndMs, w);
        double segW = Math.Max(2, x2 - x1);

        foreach (Avalonia.Controls.Control child in canvas.Children)
        {
            if (child.Name == $"SegRect_{segIndex}" || child.Name == $"SegBorder_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x1);
                child.Width = segW;
            }
            else if (child.Name == $"SegEdgeStart_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x1 - 12);
            }
            else if (child.Name == $"SegEdgeEnd_{segIndex}")
            {
                Avalonia.Controls.Canvas.SetLeft(child, x2 - 12);
            }
        }

        if (segIndex >= 0 && segIndex < _edit.Segments.Count)
        {
            var s = _edit.Segments[segIndex];
            if (s.ZoomW.HasValue && s.ZoomSlow)
            {
                double delta = newStartMs - _dragOrigStartMs;
                double? zs = _dragOrigZoomStartMs.HasValue ? _dragOrigZoomStartMs.Value + delta : newStartMs;
                double? ze = _dragOrigZoomEndMs.HasValue ? _dragOrigZoomEndMs.Value + delta : newEndMs;
                double rampMs = FreeVideoStudio.Core.Media.GranularSpeedBuilder.ZoomRampSeconds * 1000.0;
                double zsVal = zs ?? newStartMs;
                double zeVal = ze ?? newEndMs;

                double inStartMs = Math.Max(0, zsVal - rampMs);
                double outEndMs = Math.Min(dur * 1000.0, zeVal + rampMs);

                double inX1 = SrcMsToX(inStartMs, w);
                double inX2 = SrcMsToX(zsVal, w);
                double outX1 = SrcMsToX(zeVal, w);
                double outX2 = SrcMsToX(outEndMs, w);

                double inW = Math.Max(0, inX2 - inX1);
                double outW = Math.Max(0, outX2 - outX1);

                var movedSeg = s with { StartMs = newStartMs, EndMs = newEndMs, ZoomStartMs = zs, ZoomEndMs = ze };
                var mAvail = _edit.GetSlowZoomGlideAvailability(segIndex, overrideSegment: movedSeg);

                var zoomBrush = TryFindBrush("AppZoomBrush", "#2251c1");
                var warnBrush = TryFindBrush("AppWarningBrush", "#facc15");

                foreach (Avalonia.Controls.Control child in canvas.Children)
                {
                    if (child.Name == $"ZoomGlideGuideIn_{segIndex}" && child is Avalonia.Controls.Shapes.Rectangle inRect)
                    {
                        if (mAvail.IncomingAvailable)
                        {
                            inRect.Width = Math.Max(1, inW);
                            inRect.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 34, 81, 193));
                            inRect.Stroke = zoomBrush;
                            inRect.StrokeThickness = 1;
                            inRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                            Avalonia.Controls.Canvas.SetLeft(inRect, inX1);
                        }
                        else
                        {
                            double effW = Math.Max(4, inW);
                            inRect.Width = effW;
                            inRect.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 250, 204, 21));
                            inRect.Stroke = warnBrush;
                            inRect.StrokeThickness = 1.5;
                            inRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2);
                            double left = (inW < 4) ? Math.Max(0, inX2 - effW) : inX1;
                            Avalonia.Controls.Canvas.SetLeft(inRect, left);
                        }
                    }
                    else if (child.Name == $"ZoomGlideGuideOut_{segIndex}" && child is Avalonia.Controls.Shapes.Rectangle outRect)
                    {
                        if (mAvail.OutgoingAvailable)
                        {
                            outRect.Width = Math.Max(1, outW);
                            outRect.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 34, 81, 193));
                            outRect.Stroke = zoomBrush;
                            outRect.StrokeThickness = 1;
                            outRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                            Avalonia.Controls.Canvas.SetLeft(outRect, outX1);
                        }
                        else
                        {
                            double effW = Math.Max(4, outW);
                            outRect.Width = effW;
                            outRect.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 250, 204, 21));
                            outRect.Stroke = warnBrush;
                            outRect.StrokeThickness = 1.5;
                            outRect.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2);
                            Avalonia.Controls.Canvas.SetLeft(outRect, outX1);
                        }
                    }
                }
            }
        }
    }

    private void BuildLaneContent()
    {
        var lanes = GranularLanesCtl;
        if (lanes?.LaneAHost == null || lanes.LaneBHost == null) return;

        var emptyLabel = new TextBlock
        {
            Name = "GranularEmptyLaneLabel",
            Text = "No Speed Blocks Yet!",
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#94a3b8")),
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(12),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _emptyLaneLabel = emptyLabel;
        lanes.LaneAHost.Children.Add(emptyLabel);

        var segCanvas = new Avalonia.Controls.Canvas
        {
            Name = "GranularTimelineCanvas",
            IsHitTestVisible = true,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Focusable = true,
            ClipToBounds = false,
            MinHeight = SegmentLaneHeight,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        segCanvas.Classes.Add("TimelineSeekSurface");
        _segmentCanvas = segCanvas;
        lanes.LaneAHost.Children.Add(segCanvas);

        var thumbGrid = new Avalonia.Controls.Grid
        {
            Name = "GranularThumbnailLaneGrid",
            ClipToBounds = true,
            MinHeight = LaneHeight,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        _thumbLaneGrid = thumbGrid;

        var frameHost = new Avalonia.Controls.Canvas
        {
            Name = "GranularFrameLaneCanvas",
            ClipToBounds = true,
            MinHeight = LaneHeight,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        _frameLaneHost = frameHost;
        _frameLaneHost.SizeChanged += (_, _) => QueueRelayoutFrameLane();
        thumbGrid.Children.Add(frameHost);

        lanes.LaneBHost.Children.Add(thumbGrid);

        var thumbLoading = new Border
        {
            Name = "GranularThumbLoadingOverlay",
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#aa000000")),
            IsHitTestVisible = false,
            IsVisible = false,
            Child = new StackPanel
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 5,
                Children =
                {
                    new ProgressBar { IsIndeterminate = true, MinWidth = 60, MinHeight = 3 },
                    new TextBlock
                    {
                        Text = "Generating Frames...",
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                    }
                }
            }
        };
        _thumbLoadingOverlay = thumbLoading;
        lanes.LaneBHost.Children.Add(thumbLoading);

        SetupCaptionsLane();

        lanes.SetLaneHeights(SegmentLaneHeight, LaneHeight);

        lanes.LaneASeekable = false;
        lanes.LaneBSeekable = true;

        lanes.ZoomGesturesEnabled = true;
        lanes.ZoomChanged += z =>
            SetStatus(z <= 1.0001
                ? "Timeline zoom reset to 100%. Hold Ctrl and scroll to zoom the timeline."
                : $"Timeline zoom {z * 100:0}% — Ctrl+scroll to zoom, scroll to pan.");

        lanes.SeekRequested += outSec =>
        {
            var tl = OutTimeline();
            double srcSec = tl.OutputToSourceRelative(outSec);

            _holdCaretOutSec = tl.IsHoldingFrameAt(outSec) ? outSec : (double?)null;

            _playheadMs = srcSec * 1000.0;
            UpdateCaret();

            if (_zoomModeActive && _edit.SelectedSegmentIndex >= 0 && _edit.SelectedSegmentIndex < _edit.Segments.Count)
            {
                var activeSeg = _edit.Segments[_edit.SelectedSegmentIndex];
                double zStart = activeSeg.ZoomStartMs ?? activeSeg.StartMs;
                double zEnd = activeSeg.ZoomEndMs ?? activeSeg.EndMs;
                if (_playheadMs < zStart - 50 || _playheadMs > zEnd + 50)
                {
                    if (_hasZoomBox && !_zoomBoxTouched && !activeSeg.ZoomW.HasValue)
                    {
                        CommitZoomToSegment("AutoCommitSeekExit");
                    }
                    ExitZoomMode();
                }
            }

            _ = SeekInternal(srcSec);
            UpdateLiveZoomCrop();
            UpdateZoomPlayheadOverlay();
        };
    }

    private TextBlock? _emptyLaneLabel;
    private Avalonia.Controls.Canvas? _segmentCanvas;
    private Avalonia.Controls.Grid? _thumbLaneGrid;
    private Border? _thumbLoadingOverlay;

    private bool _redrawDeferredByDrag;

    private void RedrawTimeline()
    {
        var canvas = _segmentCanvas;
        if (canvas == null) return;

        if (_draggingSegmentIndex >= 0 && _segDragMode != SegDragMode.None)
        {
            _redrawDeferredByDrag = true;
            return;
        }

        if (_redrawQueued) return;
        _redrawQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            if (_editorClosing) return;
            if (_draggingSegmentIndex >= 0 && _segDragMode != SegDragMode.None)
            {
                _redrawDeferredByDrag = true;
                return;
            }
            canvas.Children.Clear();
            UpdateFreezePresetsVisibility();
            UpdateGuideLine();

            if (_draggingMemeId == null) ResnapMemesAfterBlockEdit();

            var emptyLabel = _emptyLaneLabel;
            if (emptyLabel != null)
                emptyLabel.IsVisible = _edit.Segments.Count == 0 && _edit.FreezeTimeMs < 0
                                       && _edit.PendingStartMs < 0;

            UpdateCaret();
            RelayoutFrameLane();
            RedrawCaptionsLane();
            double dur = GetDuration();
            var lanes = GranularLanesCtl;
            double w = canvas.Bounds.Width;
            if (w <= 0 && lanes?.LaneAHost != null && lanes.LaneAHost.Bounds.Width > 0)
                w = lanes.LaneAHost.Bounds.Width;
            if (w <= 0 && lanes != null && lanes.Bounds.Width > 0)
                w = lanes.Bounds.Width;
            if (w > 0 && Math.Abs(canvas.Width - w) > 0.5)
                canvas.Width = w;
            double h = Math.Max(canvas.Bounds.Height, LaneBlockHeight);
            if (dur <= 0 || w <= 0) return;

            var pendingZoomHeads = new List<(int Index, double StartX, double EndX)>();

            for (int i = 0; i < _edit.Segments.Count; i++)
            {
                var seg = _edit.Segments[i];
                double x1 = SrcMsToX(seg.StartMs, w);
                double x2 = SrcMsToX(seg.EndMs,   w);
                bool isSelected = i == _edit.SelectedSegmentIndex;

                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Name = $"SegRect_{i}",
                    Width  = Math.Max(2, x2 - x1),
                    Height = h,
                    Fill   = new Avalonia.Media.SolidColorBrush(GetSegmentOverlayColor(seg)),
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(rect, x1);
                Avalonia.Controls.Canvas.SetTop(rect, 0);
                canvas.Children.Add(rect);

                if (seg.ZoomW.HasValue)
                {
                    double zsMs = seg.ZoomStartMs ?? seg.StartMs;
                    double zeMs = seg.ZoomEndMs ?? seg.EndMs;

                    double zx1 = SrcMsToX(zsMs, w);
                    double zx2 = SrcMsToX(zeMs, w);

                    pendingZoomHeads.Add((i, zx1, zx2));

                    int activeZoomSegIndex = (_zoomDragSegment >= 0 && _zoomDragSegment < _edit.Segments.Count)
                        ? _zoomDragSegment
                        : ((_draggingSegmentIndex >= 0 && _draggingSegmentIndex < _edit.Segments.Count)
                            ? _draggingSegmentIndex
                            : _edit.SelectedSegmentIndex);

                    if (i == activeZoomSegIndex && seg.ZoomSlow)
                    {
                        var avail = _edit.GetSlowZoomGlideAvailability(i);
                        bool inAvail = avail.IncomingAvailable;
                        bool outAvail = avail.OutgoingAvailable;
                        if (_zoomDragSegment == i && _zoomDragAttemptedConflict)
                        {
                            if (_zoomDragIsStart) inAvail = false;
                            else outAvail = false;
                        }

                        double rampMs = FreeVideoStudio.Core.Media.GranularSpeedBuilder.ZoomRampSeconds * 1000.0;
                        double inStartMs = Math.Max(0, zsMs - rampMs);
                        double outEndMs = Math.Min(dur * 1000.0, zeMs + rampMs);

                        double inX1 = SrcMsToX(inStartMs, w);
                        double inX2 = SrcMsToX(zsMs, w);
                        double outX1 = SrcMsToX(zeMs, w);
                        double outX2 = SrcMsToX(outEndMs, w);

                        double inW = Math.Max(0, inX2 - inX1);
                        double outW = Math.Max(0, outX2 - outX1);

                        var zoomBrush = TryFindBrush("AppZoomBrush", "#2251c1");
                        var warnBrush = TryFindBrush("AppWarningBrush", "#facc15");

                        var inGuide = new Avalonia.Controls.Shapes.Rectangle
                        {
                            Name = $"ZoomGlideGuideIn_{i}",
                            Height = h,
                            IsHitTestVisible = false
                        };
                        if (inAvail)
                        {
                            inGuide.Width = Math.Max(1, inW);
                            inGuide.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 34, 81, 193));
                            inGuide.Stroke = zoomBrush;
                            inGuide.StrokeThickness = 1;
                            inGuide.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                            Avalonia.Controls.Canvas.SetLeft(inGuide, inX1);
                        }
                        else
                        {
                            double effW = Math.Max(4, inW);
                            inGuide.Width = effW;
                            inGuide.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 250, 204, 21));
                            inGuide.Stroke = warnBrush;
                            inGuide.StrokeThickness = 1.5;
                            inGuide.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2);
                            double left = (inW < 4) ? Math.Max(0, inX2 - effW) : inX1;
                            Avalonia.Controls.Canvas.SetLeft(inGuide, left);
                        }
                        Avalonia.Controls.Canvas.SetTop(inGuide, 0);
                        canvas.Children.Add(inGuide);

                        var outGuide = new Avalonia.Controls.Shapes.Rectangle
                        {
                            Name = $"ZoomGlideGuideOut_{i}",
                            Height = h,
                            IsHitTestVisible = false
                        };
                        if (outAvail)
                        {
                            outGuide.Width = Math.Max(1, outW);
                            outGuide.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 34, 81, 193));
                            outGuide.Stroke = zoomBrush;
                            outGuide.StrokeThickness = 1;
                            outGuide.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2);
                            Avalonia.Controls.Canvas.SetLeft(outGuide, outX1);
                        }
                        else
                        {
                            double effW = Math.Max(4, outW);
                            outGuide.Width = effW;
                            outGuide.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(70, 250, 204, 21));
                            outGuide.Stroke = warnBrush;
                            outGuide.StrokeThickness = 1.5;
                            outGuide.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(3, 2);
                            Avalonia.Controls.Canvas.SetLeft(outGuide, outX1);
                        }
                        Avalonia.Controls.Canvas.SetTop(outGuide, 0);
                        canvas.Children.Add(outGuide);
                    }
                }

                if (isSelected)
                {
                    var antsBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#fde047"));
                    double segW = Math.Max(2, x2 - x1);

                    var borderRect = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Name = $"SegBorder_{i}",
                        Width = segW,
                        Height = h,
                        Stroke = antsBrush,
                        StrokeThickness = 1,
                        StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(2, 2),
                        StrokeDashOffset = _marchingAntsOffset,
                        IsHitTestVisible = false
                    };
                    Avalonia.Controls.Canvas.SetLeft(borderRect, x1);
                    Avalonia.Controls.Canvas.SetTop(borderRect, 0);
                    canvas.Children.Add(borderRect);
                    _selectedSegmentBorderRef = borderRect;

                    AddSegmentEdgeMarker(canvas, i, isStart: true,  markerX: x1, h: h, canvasWidth: w, durationSeconds: dur, blockWidthPx: segW);
                    AddSegmentEdgeMarker(canvas, i, isStart: false, markerX: x2, h: h, canvasWidth: w, durationSeconds: dur, blockWidthPx: segW);
                }
                else
                {
                    if (_selectedSegmentBorderRef != null && _edit.SelectedSegmentIndex == -1)
                        _selectedSegmentBorderRef = null;
                }
            }

            if (_edit.PendingStartMs >= 0)
            {
                double px = SrcMsToX(_edit.PendingStartMs, w);
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen,
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }

            if (_edit.PendingEndMs >= 0)
            {
                double px = SrcMsToX(_edit.PendingEndMs, w);
                var line = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2, Height = h,
                    Fill = Avalonia.Media.Brushes.SeaGreen,
                    IsHitTestVisible = false
                };
                Avalonia.Controls.Canvas.SetLeft(line, px);
                canvas.Children.Add(line);
            }

            var markerOverlay = GranularLanesCtl?.MarkerOverlayHost;
            markerOverlay?.Children.Clear();
            _freezeMarkerAnts.Clear();

            double MarkerStickHeight(double markerTopPx)
            {
                double overlayH = markerOverlay is { } mo && mo.Bounds.Height > 1
                    ? mo.Bounds.Height
                    : MarkerOverlayFallbackHeightPx;
                return overlayH - markerTopPx - MainWindow.TimelineCameraStickTopPx;
            }

            if (_edit.FreezeTimeMs >= 0)
            {
                double freezeRelMs = Math.Clamp(_edit.FreezeTimeMs - _edit.TrimStartMs, 0, dur * 1000.0);
                double freezeHoldPx = (_edit.FreezeDurationS / OutDurationSec()) * w;
                double freezeX = SrcMsToX(freezeRelMs, w) - freezeHoldPx;

                DecorateFrozenSpan(canvas, freezeX, freezeHoldPx, h, withLabel: false, withGrips: true);

                double startLeftPx = MainWindow.ClampTimelineCameraLeft(freezeX + LaneBorderInsetPx, w);
                double endLeftPx = MainWindow.ClampTimelineCameraLeft(freezeX + freezeHoldPx + LaneBorderInsetPx, w);
                double headStaggerPx = Math.Abs(endLeftPx - startLeftPx) >= FreezeMarkerHeadWidthPx
                    ? 0.0
                    : FreezeMarkerEndStaggerPx;

                Control BuildFreezeMarker(FreezeMarkerEnd which, double leftPx, string tip)
                {
                    var cam = MainWindow.CreateTimelineCameraIcon(
                        _isFreezeCameraSelected && _freezeFocus == which,
                        _marchingAntsOffset,
                        out var iconAnts,
                        out var lineAnts);
                    _freezeMarkerAnts.Add(iconAnts);
                    _freezeMarkerAnts.Add(lineAnts);
                    ToolTip.SetTip(cam, tip);
                    double camTop = which == FreezeMarkerEnd.End
                        ? FreezeMarkerOverlayTop + headStaggerPx
                        : FreezeMarkerOverlayTop;
                    Avalonia.Controls.Canvas.SetTop(cam, camTop);
                    Avalonia.Controls.Canvas.SetLeft(cam, leftPx);
                    MainWindow.StretchTimelineCameraStick(cam, MarkerStickHeight(camTop));
                    AttachFreezeMarkerInteractions(cam, which, canvas);
                    return cam;
                }

                string held = $"Freeze at {FormatMs(freezeRelMs)}, held {_edit.FreezeDurationS:0.00}s.";
                var startCam = BuildFreezeMarker(FreezeMarkerEnd.Start, startLeftPx,
                    held + "\nDrag to move where the hold begins. Drag the band's body to move the whole freeze.");
                var endCam = BuildFreezeMarker(FreezeMarkerEnd.End, endLeftPx,
                    held + "\nDrag to change how long the frame is held.");

                var markerParent = markerOverlay ?? canvas;
                if (headStaggerPx > 0)
                {
                    markerParent.Children.Add(startCam);
                    markerParent.Children.Add(endCam);
                }
                else
                {
                    markerParent.Children.Add(endCam);
                    markerParent.Children.Add(startCam);
                }
            }

            if (pendingZoomHeads.Count > 0)
            {
                var zoomParent = markerOverlay ?? canvas;

                foreach (var (index, zx1, zx2) in pendingZoomHeads)
                {
                    double zStartLeft = MainWindow.ClampTimelineCameraLeft(zx1 + LaneBorderInsetPx, w);
                    double zEndLeft = MainWindow.ClampTimelineCameraLeft(zx2 + LaneBorderInsetPx, w);
                    double zStagger = Math.Abs(zEndLeft - zStartLeft) >= FreezeMarkerHeadWidthPx
                        ? 0.0
                        : FreezeMarkerEndStaggerPx;

                    Control BuildZoomMarker(bool isStart, double leftPx, double topPx, string tip)
                    {
                        var zcam = MainWindow.CreateZoomTimelineCameraIcon(
                            _zoomFocus is { } zf && zf.Segment == index && zf.IsStart == isStart,
                            _marchingAntsOffset,
                            out var zIconAnts,
                            out var zLineAnts);
                        _freezeMarkerAnts.Add(zIconAnts);
                        _freezeMarkerAnts.Add(zLineAnts);
                        ToolTip.SetTip(zcam, tip);
                        Avalonia.Controls.Canvas.SetTop(zcam, topPx);
                        Avalonia.Controls.Canvas.SetLeft(zcam, leftPx);
                        MainWindow.StretchTimelineCameraStick(zcam, MarkerStickHeight(topPx));
                        AttachZoomMarkerInteractions(zcam, index, isStart, canvas);
                        return zcam;
                    }

                    var zseg = _edit.Segments[index];
                    string inTip = $"Zoom on block #{index + 1}.\nDrag to move where the zoom begins.";
                    string outTip = $"Zoom on block #{index + 1}.\nDrag to move where the zoom ends.";
                    if (zseg.ZoomSlow)
                    {
                        var zAvail = _edit.GetSlowZoomGlideAvailability(index);
                        inTip += zAvail.IncomingAvailable
                            ? "\nIncoming glide: 0.5s source ramp."
                            : $"\nIncoming glide unavailable: {zAvail.GetIncomingExplanation()}";
                        outTip += zAvail.OutgoingAvailable
                            ? "\nOutgoing glide: 0.5s source ramp."
                            : $"\nOutgoing glide unavailable: {zAvail.GetOutgoingExplanation()}";
                    }

                    var zStartCam = BuildZoomMarker(true, zStartLeft, FreezeMarkerOverlayTop, inTip);
                    var zEndCam = BuildZoomMarker(false, zEndLeft, FreezeMarkerOverlayTop + zStagger, outTip);

                    if (zStagger > 0)
                    {
                        zoomParent.Children.Add(zStartCam);
                        zoomParent.Children.Add(zEndCam);
                    }
                    else
                    {
                        zoomParent.Children.Add(zEndCam);
                        zoomParent.Children.Add(zStartCam);
                    }
                }
            }

            DrawMemeBands(canvas, markerOverlay, w, h, MarkerStickHeight);
            DrawCornerMemeBands(canvas, w, h);
            DrawKillScanChip(canvas, w);

            if (_aiEliminationTimestamps.Count > 0 && dur > 0)
            {
                var elimBaseColor = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Avalonia.Media.Color.FromRgb(168, 50, 50));
                var elimBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, elimBaseColor.R, elimBaseColor.G, elimBaseColor.B));

                var elimTimeline = OutTimeline();
                foreach (double elimSec in _aiEliminationTimestamps)
                {
                    double elimMs = elimSec * 1000.0;
                    if (elimMs < _edit.TrimStartMs || (_edit.TrimEndMs > 0 && elimMs > _edit.TrimEndMs)) continue;

                    double elimRelMs = elimMs - _edit.TrimStartMs;
                    if (elimTimeline.IsCutAtSource(elimRelMs / 1000.0)) continue;
                    double ex = SrcMsToX(elimRelMs, w);
                    double outSec = elimTimeline.SourceToOutput(elimRelMs / 1000.0);

                    var xMark = new Avalonia.Controls.Border
                    {
                        Width = 18,
                        Height = 18,
                        CornerRadius = new Avalonia.CornerRadius(9),
                        Background = elimBrush,
                        BorderBrush = Avalonia.Media.Brushes.White,
                        BorderThickness = new Avalonia.Thickness(1.5),
                        IsHitTestVisible = true,
                        ZIndex = 500,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        Child = new TextBlock
                        {
                            Text = "✕",
                            Foreground = Avalonia.Media.Brushes.White,
                            FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
                            FontWeight = Avalonia.Media.FontWeight.Bold,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        },
                    };
                    ToolTip.SetTip(xMark, $"Elimination at {TimeSpan.FromSeconds(outSec):mm\\:ss\\.f} in the finished video — click to jump there");
                    double jumpRelMs = Math.Max(0, elimRelMs - 1000.0);
                    xMark.PointerPressed += (_, e) =>
                    {
                        e.Handled = true;
                        SetPlayheadFromScrub(jumpRelMs);
                    };
                    Avalonia.Controls.Canvas.SetLeft(xMark, Math.Max(0, ex - 9));
                    Avalonia.Controls.Canvas.SetTop(xMark, -20);
                    canvas.Children.Add(xMark);
                }
            }
        });
    }

    private bool _isDraggingZoomMarker;

    private void AttachZoomMarkerInteractions(Control marker, int segIndex, bool isStart, Avalonia.Controls.Canvas timelineCanvas)
    {
        marker.PointerEntered += (_, _) => MainWindow.SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) =>
        {
            if (_zoomDragSegment < 0) MainWindow.SetTimelineCameraHover(marker, false);
        };
        marker.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(marker).Properties;
            if (props.IsRightButtonPressed) { ClearTimelineSelection(); e.Handled = true; return; }
            if (!props.IsLeftButtonPressed) return;
            if (segIndex < 0 || segIndex >= _edit.Segments.Count) return;
            if (timelineCanvas.Bounds.Width <= 0) return;

            FocusZoomMarker(segIndex, isStart);

            _zoomDragSegment = segIndex;
            _zoomDragIsStart = isStart;
            _isDraggingZoomMarker = true;

            marker.Focus();
            MainWindow.SetTimelineCameraHover(marker, true);
            e.Pointer.Capture(timelineCanvas);

            JumpPlayheadToZoomEdge(segIndex, isStart);

            SetStatus(isStart
                ? "Dragging the zoom START — the picture follows it. Release to set."
                : "Dragging the zoom END — the picture follows it. Release to set.");
            RedrawTimeline();
            e.Handled = true;
        };
    }

    private void ClampZoomInsideItsBlock(int index) => _edit.ClampZoomInsideItsBlock(index);

    private void ShowSegmentContextMenu(Avalonia.Controls.Canvas canvas, int segIndex)
    {
        if (segIndex < 0 || segIndex >= _edit.Segments.Count) return;

        SelectSegment(segIndex, jumpPlayhead: true);

        var items = new System.Collections.Generic.List<Avalonia.Controls.Control>();
        bool hasZoom = _edit.Segments[segIndex].ZoomW.HasValue;

        if (hasZoom)
        {
            var edit = new Avalonia.Controls.MenuItem
            {
                Header = "Edit Zoom",
                Icon = new TextBlock { Text = "\U0001F50D", Margin = new Avalonia.Thickness(0) }
            };
            edit.Click += (_, _) =>
            {
                if (_edit.SelectedSegmentIndex < 0) return;
                if (!_zoomModeActive) EnterZoomMode();
                Notify("Drag the box to re-aim it. Every change is saved as you go.");
            };
            items.Add(edit);

            var removeZoom = new Avalonia.Controls.MenuItem
            {
                Header = "Remove Zoom (keep the speed)",
                Icon = new TextBlock { Text = "\U0001F6AB", Margin = new Avalonia.Thickness(0) }
            };
            removeZoom.Click += (_, _) =>
            {
                if (_zoomModeActive) ExitZoomMode();
                RemoveZoomFromSelectedSegment();
            };
            items.Add(removeZoom);

            items.Add(new Avalonia.Controls.Separator());
        }

        var del = new Avalonia.Controls.MenuItem
        {
            Header = hasZoom ? "Delete Block (speed AND zoom)" : "Delete Block",
            Icon = new TextBlock { Text = "\U0001F5D1", Margin = new Avalonia.Thickness(0) }
        };
        del.Click += (_, _) => { if (_edit.SelectedSegmentIndex >= 0) ExecuteDeleteSelectedSegment(); };
        items.Add(del);

        var menu = new Avalonia.Controls.ContextMenu { ItemsSource = items };
        menu.Open(canvas);
    }

    private void JumpPlayheadToZoomEdge(int segIndex, bool toStart)
    {
        if (segIndex < 0 || segIndex >= _edit.Segments.Count) return;
        try
        {
            var seg = _edit.Segments[segIndex];
            double ms = toStart
                ? (seg.ZoomStartMs ?? seg.StartMs)
                : (seg.ZoomEndMs ?? seg.EndMs);

            var ipc = _videoHost?.IpcClient;
            if (ipc != null && !ipc.IsPaused) _ = ipc.SetPropertyAsync("pause", "yes");

            SetPlayheadFromScrub(Math.Max(0, ms));
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    private void FocusZoomMarker(int segIndex, bool isStart)
    {
        if (_edit.SelectedSegmentIndex != segIndex) SelectSegment(segIndex, jumpPlayhead: false);
        _isFreezeCameraSelected = false;
        _freezeFocus = FreezeMarkerEnd.None;
        _zoomFocus = (segIndex, isStart);
        UpdateDeleteButtonVisibility();
        if (segIndex >= 0 && segIndex < _edit.Segments.Count)
        {
            var seg = _edit.Segments[segIndex];
            if (seg.ZoomSlow)
            {
                var avail = _edit.GetSlowZoomGlideAvailability(segIndex);
                if (isStart && !avail.IncomingAvailable)
                    SetStatus(avail.GetIncomingExplanation());
                else if (!isStart && !avail.OutgoingAvailable)
                    SetStatus(avail.GetOutgoingExplanation());
                else if (avail.AnyUnavailable)
                    SetStatus(avail.GetCombinedExplanation()!);
            }
        }
    }

    private PreviewDetachController? _previewDetach;

    private void WirePreviewDetach()
    {
        var btn = this.FindControl<Button>("GranularDetachPreviewBtn");
        if (btn == null) return;

        _previewDetach = new PreviewDetachController(
            this,
            PreviewDetachController.GranularKey,
            "Preview Monitor — Granular Speed Editor",
            () => this.FindControl<Avalonia.Controls.Viewbox>("GranularPreviewViewbox"));

        _previewDetach.StateChanged += detached =>
        {
            var watermark = this.FindControl<Avalonia.Controls.Border>("GranularPreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;
            _previewDetach!.SyncButton(btn);
        };

        _previewDetach.DetachUnavailable += why => SetStatus(why);

        btn.Click += (_, _) => _previewDetach.Toggle();
        _previewDetach.SyncButton(btn);
    }

    private void UpdateDetachButtonForZoomMode()
    {
        var btn = this.FindControl<Button>("GranularDetachPreviewBtn");
        if (btn == null) return;
        btn.IsVisible = !_zoomModeActive || (_previewDetach?.IsDetached == true);
    }

    private void AddSegmentEdgeMarker(Avalonia.Controls.Canvas canvas, int segIndex, bool isStart, double markerX, double h, double canvasWidth, double durationSeconds, double blockWidthPx)
    {
        const double OuterReach = 12.0;
        double innerReach = Math.Clamp(blockWidthPx / 2.0, 0.0, OuterReach);
        double boxWidth = OuterReach + innerReach;
        double rawBoxLeft = isStart ? markerX - OuterReach : markerX - innerReach;
        double maxLeft = Math.Max(0.0, canvasWidth - boxWidth);
        double boxLeft = Math.Clamp(rawBoxLeft, 0.0, maxLeft);
        double stickOffset = Math.Clamp(markerX - boxLeft, 1.5, Math.Max(1.5, boxWidth - 1.5));

        var hitBox = new Avalonia.Controls.Border
        {
            Name = isStart ? $"SegEdgeStart_{segIndex}" : $"SegEdgeEnd_{segIndex}",
            Width = boxWidth,
            Height = h,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
            ZIndex = 110
        };
        var stick = new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = Avalonia.Media.Brushes.SeaGreen,
            Width = 3,
            Height = h,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(stickOffset - 1.5, 0, 0, 0)
        };
        hitBox.Child = stick;
        ToolTip.SetTip(hitBox, isStart
            ? "Drag left/right to move this block's START"
            : "Drag left/right to move this block's END");

        Avalonia.Controls.Canvas.SetLeft(hitBox, boxLeft);
        Avalonia.Controls.Canvas.SetTop(hitBox, 0);

        hitBox.PointerEntered += (_, _) => { hitBox.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(40, 46, 139, 87)); stick.Fill = Avalonia.Media.Brushes.MediumSeaGreen; };
        hitBox.PointerExited += (_, _) => { hitBox.Background = Avalonia.Media.Brushes.Transparent; stick.Fill = Avalonia.Media.Brushes.SeaGreen; };

        hitBox.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
            if (segIndex < 0 || segIndex >= _edit.Segments.Count) return;

            if (_zoomModeActive && segIndex == _edit.SelectedSegmentIndex)
            {
                SetStatus("The zoom box is open, so the magnifiers own this edge. Press ZOOM BY HAND or Escape to resize the block itself.");
                e.Handled = true;
                return;
            }

            double seamPointerMs = canvasWidth > 0
                ? Math.Clamp(XToSrcMs(e.GetPosition(canvas).X, canvasWidth), 0, durationSeconds * 1000.0)
                : 0;
            int edgeIdx = segIndex;
            bool edgeIsStart = isStart;
            ResolveSeamEdge(ref edgeIdx, ref edgeIsStart, seamPointerMs);

            var seg = _edit.Segments[edgeIdx];

            _edit.SelectedSegmentIndex = edgeIdx;
            _draggingSegmentIndex = edgeIdx;
            _segDragMode = edgeIsStart ? SegDragMode.ResizeStart : SegDragMode.ResizeEnd;
            _dragOrigStartMs = seg.StartMs;
            _dragOrigEndMs = seg.EndMs;
            _dragOrigZoomStartMs = seg.ZoomStartMs;
            _dragOrigZoomEndMs = seg.ZoomEndMs;
            _segDragTimeline = OutTimeline();
            _segDragOutDurationSec = OutDurationSec();
            double totalMs = durationSeconds * 1000.0;
            _dragStartPointerMs = canvasWidth > 0
                ? Math.Clamp(XToSrcMs(e.GetPosition(canvas).X, canvasWidth), 0, totalMs)
                : 0;

            e.Pointer.Capture(canvas);
            SetStatus($"Resizing block #{segIndex + 1} — release to set.");
            e.Handled = true;
        };

        canvas.Children.Add(hitBox);
    }

    private bool _zoomModeActive;
    private enum ZoomDrag { None, Draw, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }
    private ZoomDrag _zoomDrag = ZoomDrag.None;
    private Avalonia.Point _zoomDragStart;
    private Avalonia.Rect _zoomStartRect;
    private Avalonia.Rect _zoomUiRect;
    private bool _hasZoomBox;
    private readonly Avalonia.Controls.Shapes.Rectangle[] _zoomDim = new Avalonia.Controls.Shapes.Rectangle[4];
    private Avalonia.Controls.Shapes.Rectangle? _zoomBoxRect;
    private readonly Avalonia.Controls.Shapes.Rectangle[] _zoomHandles = new Avalonia.Controls.Shapes.Rectangle[4];
    private Avalonia.Controls.Border? _zoomTutorial;
    private DispatcherTimer? _zoomTutorialTimer;
    private const double ZoomHandlePx = 16;
    private const double ZoomHandleVisualPx = 13.6;

    private const double ZoomBandThicknessPx = 2.5;

    private const double MaxZoomUpscale = 8.0;

    private const double DefaultZoomFactor = 2.0;

    private bool _zoomDragAttemptedConflict;

    private Avalonia.Media.IBrush TryFindBrush(string key, string fallbackHex)
        => this.TryFindResource(key, ActualThemeVariant, out object? v) && v is Avalonia.Media.IBrush b
            ? b
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(fallbackHex));

    private double ClampZoomEdgeAgainstSlowNeighbours(int segIndex, double proposedMs, bool isStart)
    {
        double clamped = _edit.ClampZoomEdgeAgainstSlowNeighbours(segIndex, proposedMs, isStart);
        bool clampedBySlow = Math.Abs(clamped - proposedMs) > 0.5;
        _zoomDragAttemptedConflict = false;

        if (segIndex >= 0 && segIndex < _edit.Segments.Count)
        {
            var seg = _edit.Segments[segIndex];
            if (seg.ZoomSlow)
            {
                var attemptedSeg = isStart
                    ? seg with { ZoomStartMs = proposedMs, StartMs = Math.Min(seg.StartMs, proposedMs) }
                    : seg with { ZoomEndMs = proposedMs, EndMs = Math.Max(seg.EndMs, proposedMs) };
                var attemptedAvail = _edit.GetSlowZoomGlideAvailability(segIndex, overrideSegment: attemptedSeg);

                if (isStart && (!attemptedAvail.IncomingAvailable || clampedBySlow))
                {
                    _zoomDragAttemptedConflict = true;
                    SetStatus(attemptedAvail.GetIncomingExplanation());
                    return clamped;
                }
                else if (!isStart && (!attemptedAvail.OutgoingAvailable || clampedBySlow))
                {
                    _zoomDragAttemptedConflict = true;
                    SetStatus(attemptedAvail.GetOutgoingExplanation());
                    return clamped;
                }
            }
        }

        if (clampedBySlow)
        {
            _zoomDragAttemptedConflict = true;
            string shortage = isStart
                ? "There is not enough footage for the incoming glide."
                : "There is not enough footage for the outgoing glide.";
            SetStatus($"These smooth zooms need 1 second of footage between them. {shortage}");
        }
        else
        {
            SetStatus(isStart ? "Moving zoom start — release to set." : "Moving zoom end — release to set.");
        }
        return clamped;
    }

    private double ZoomAspect => _edit.IsMobileFormat ? (2.0 / 3.0) : (16.0 / 9.0);

    private void WireZoomControls()
    {
        var zoomBtn = ZoomSegmentBtnCtl;
        if (zoomBtn != null) zoomBtn.Click += (_, __) =>
        {
            if (GuideWhenNothingMarked("ZOOM BY HAND")) return;
            ToggleZoomMode();
        };

        WireAiTrackingControls();
        WireAiCommentaryControls();

        var removeZoomBtn = this.FindControl<Button>("RemoveZoomBtn");
        if (removeZoomBtn != null) removeZoomBtn.Click += (_, __) => RemoveZoomFromSelectedSegment();

        var canvas = ZoomOverlayCanvasCtl;
        if (canvas != null)
        {
            canvas.PointerPressed += ZoomCanvas_PointerPressed;
            canvas.PointerMoved += ZoomCanvas_PointerMoved;
            canvas.PointerReleased += ZoomCanvas_PointerReleased;
        }

        var slowCb = SlowZoomCheckCtl;
        var instCb = InstantZoomCheckCtl;
        if (slowCb != null) slowCb.IsCheckedChanged += (_, __) => OnZoomModeChanged(fromSlow: true);
        if (instCb != null) instCb.IsCheckedChanged += (_, __) => OnZoomModeChanged(fromSlow: false);

        var helpBtn = this.FindControl<Button>("HelpButton");
        var helpClose = this.FindControl<Button>("HelpCloseButton");
        var helpOverlay = this.FindControl<Avalonia.Controls.Grid>("HelpOverlay");
        if (helpBtn != null) helpBtn.Click += (_, __) => { if (helpOverlay != null) helpOverlay.IsVisible = true; };
        if (helpClose != null) helpClose.Click += (_, __) => { if (helpOverlay != null) helpOverlay.IsVisible = false; };
        if (helpOverlay != null)
            helpOverlay.PointerPressed += (s, e) => { if (ReferenceEquals(e.Source, helpOverlay)) helpOverlay.IsVisible = false; };

        var helpShowMe = this.FindControl<Button>("HelpShowMeButton");
        if (helpShowMe != null) helpShowMe.Click += (_, __) =>
        {
            if (helpOverlay != null) helpOverlay.IsVisible = false;
            Controls.CoachOverlay.Replay(this);
        };

        SyncZoomModeChecksFromSegment();

        var portraitGrid = this.FindControl<Avalonia.Controls.Grid>("GranularPortraitDimmingGrid");
        if (portraitGrid != null) portraitGrid.IsVisible = _edit.IsMobileFormat;
        _ = RefreshFinalLayoutPreviewAsync();
    }

    private void UpdateDragReadout(double startMs, double endMs)
    {
        var badge = this.FindControl<Border>("DragReadoutBadge");
        var txt = this.FindControl<TextBlock>("DragReadoutText");
        if (badge == null || txt == null) return;
        double lenS = Math.Max(0, endMs - startMs) / 1000.0;
        txt.Text = $"Start {FormatMs(startMs)}    End {FormatMs(endMs)}    Length {lenS:0.00}s";

        bool stylePanelUp = ZoomStylePanelCtl?.IsVisible == true;
        if (stylePanelUp)
        {
            badge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            badge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            badge.Margin = new Thickness(0, 0, 0, 12);
        }

        badge.IsVisible = true;
    }

    private void HideDragReadout()
    {
        var badge = this.FindControl<Border>("DragReadoutBadge");
        if (badge != null) badge.IsVisible = false;
    }

    private void RequestDeleteSegment(int index) => _ = RequestDeleteSegmentAsync(index);

    private async System.Threading.Tasks.Task RequestDeleteSegmentAsync(int index)
    {
        if (index < 0 || index >= _edit.Segments.Count) return;

        if (_edit.SelectedSegmentIndex != index) SelectSegmentAt(index);

        if (FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ConfirmGranularDeleteSegment)
        {
            var seg = _edit.Segments[index];
            bool ok = await FreeVideoStudio.App.Controls.ConfirmDialogWindow.AskAsync(this,
                $"Delete the speed block from {FormatMs(seg.StartMs)} to {FormatMs(seg.EndMs)}?\n\n" +
                "Changed your mind later? Press Ctrl+Z to bring it back.",
                "Delete this block?", "DELETE", "KEEP IT", destructive: true);
            if (!ok) { RuntimeLog.Info("UI", "User kept the speed segment (delete prompt)."); return; }
            if (_edit.SelectedSegmentIndex != index && index < _edit.Segments.Count) SelectSegmentAt(index);
        }
        ExecuteDeleteSelectedSegment();
    }

    private void ExecuteDeleteSelectedSegment()
    {
        if (_edit.DeleteSelectedSegment() is not { } seg) return;
        RuntimeLog.Info("UI", $"User deleted a speed segment in the Granular Speed Editor ({FormatMs(seg.StartMs)} to {FormatMs(seg.EndMs)}).");
        RefreshSegmentList();
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        if (_videoHost?.IpcClient != null)
        {
            double curMs = GetCurrentTime() * 1000.0;
            double spd = _edit.SpeedAt(curMs);
            _ = _previewSpeed.Force(_videoHost.IpcClient, spd);
        }
        SetStatus("Selected block deleted.");
        NotifyUndoable("Block deleted", "DeleteSegmentBtn");
        ScheduleGranularRecoverySave();
    }

    private void ClearFreezeImage(string? feedback)
    {
        _edit.ClearFreeze();
        _isFreezeCameraSelected = false;
        _freezeFocus = FreezeMarkerEnd.None;
        _freezeDragMode = FreezeDragMode.None;
        _freezeMarkerAnts.Clear();

        var icon = FreezeImageToggleIconCtl;
        var txt = FreezeImageToggleTextCtl;
        if (icon != null) icon.Text = "\U0001F4F8";
        if (txt != null) txt.Text = " FREEZE IMAGE ";

        var toggle = FreezeImageToggleCtl;
        if (toggle != null)
        {
            toggle.Classes.Remove("Danger");
            if (!toggle.Classes.Contains("Primary")) toggle.Classes.Add("Primary");
        }

        if (!string.IsNullOrEmpty(feedback)) ShowFeedback(feedback!);

        _freezePulseTimer?.Stop();
        var hint = FreezeHintLabelCtl;
        if (hint != null) hint.IsVisible = false;
        var hintBottom = FreezeHintLabelBottomCtl;
        if (hintBottom != null) hintBottom.IsVisible = false;

        foreach (var name in new[] { "FreezePreset05", "FreezePreset10", "FreezePreset15",
                                     "FreezePreset20", "FreezePreset25", "FreezePreset30" })
        {
            var b = this.FindControl<Button>(name);
            if (b == null) continue;
            b.ClearValue(Avalonia.Controls.Button.BackgroundProperty);
            b.ClearValue(Avalonia.Controls.Button.BorderBrushProperty);
            b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
        }

        SetFreezePromptControlsEnabled(true);
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        ScheduleGranularRecoverySave();
    }

    private void SetFreezePromptControlsEnabled(bool enabled)
    {
        var controlsToToggle = new Control?[] {
            this.FindControl<Button>("MarkStartBtn"),
            this.FindControl<Button>("MarkEndBtn"),
            this.FindControl<Button>("GranularPlayPause"),
            PendingSpeedSliderCtl,
            this.FindControl<StackPanel>("SpeedPresetsPanel"),
            DeleteSegmentBtnCtl,
            ClearAllSegmentsBtnCtl,
        };
        foreach (var c in controlsToToggle)
        {
            if (c is Avalonia.Input.InputElement input) input.IsEnabled = enabled;
        }
    }

    private void ExecuteClearAllSegments()
    {
        bool hadFreeze = _edit.HasFreeze;
        RuntimeLog.Info("UI", $"User cleared ALL {_edit.Segments.Count} speed segment(s){(hadFreeze ? " and the frozen frame" : "")} in the Granular Speed Editor.");

        _edit.ClearAll();
        _zoomFocus = null;
        _zoomDragSegment = -1;
        _isDraggingZoomMarker = false;

        if (hadFreeze) ClearFreezeImage(null);

        RefreshSegmentList();
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        ScheduleGranularRecoverySave();
        if (_videoHost?.IpcClient != null)
        {
            _ = _previewSpeed.Force(_videoHost.IpcClient, _edit.BaseSpeed);
        }
        SetStatus(hadFreeze
            ? "All blocks, the frozen frame and pending selections cleared."
            : "All blocks and pending selections cleared.");
        NotifyUndoable(hadFreeze ? "Cleared everything, including the frozen frame" : "Cleared all blocks",
            "ClearAllSegmentsBtn");
    }

    private void UpdateClearAllPromptText()
    {
        var t = this.FindControl<TextBlock>("ClearAllDetailText");
        if (t == null) return;

        int n = _edit.Segments.Count;
        int zooms = 0;
        foreach (var s2 in _edit.Segments)
        {
            if (s2.ZoomW.HasValue && s2.ZoomH.HasValue) zooms++;
        }
        bool hasFreeze = _edit.FreezeTimeMs >= 0;

        if (n == 0 && !hasFreeze)
        {
            t.Text = "There are no speed blocks or frozen frames to erase. Only a half-finished MARK START / MARK END selection would be reset.";
            return;
        }

        if (n == 0)
        {
            t.Text = $"The frozen frame ({_edit.FreezeDurationS:0.00}s held) will be erased and the whole clip goes back to normal speed. A half-finished MARK START / MARK END selection is also reset. Your video file is not touched.";
            return;
        }

        string extras = zooms > 0 ? $", including {zooms} zoom(s)," : "";
        string freeze = hasFreeze ? $" The frozen frame ({_edit.FreezeDurationS:0.00}s held) is erased with them." : "";
        t.Text = $"All {n} speed block(s) on the timeline{extras} will be erased and the whole clip goes back to normal speed.{freeze} A half-finished MARK START / MARK END selection is also reset. Your video file is not touched.";
    }

    private bool _syncingZoomChecks;

    private static void RememberEditorChoice(Func<Infrastructure.DefaultValues, bool> differs, Action<Infrastructure.DefaultValues> write)
    {
        try
        {
            if (!differs(Infrastructure.SettingsManager.Instance.Defaults)) return;
            Infrastructure.SettingsManager.Update(s => write(s.Defaults));
        }
        catch (Exception ex) { FreeVideoStudio.App.RuntimeLog.Swallowed(ex); }
    }

    private bool ZoomSlowSelected => SlowZoomCheckCtl?.IsChecked == true;

    private void OnZoomModeChanged(bool fromSlow)
    {
        if (_syncingZoomChecks) return;
        var slowCb = SlowZoomCheckCtl;
        var instCb = InstantZoomCheckCtl;
        if (slowCb == null || instCb == null) return;

        bool slow = fromSlow ? (slowCb.IsChecked == true) : (instCb.IsChecked != true);
        _syncingZoomChecks = true;
        slowCb.IsChecked = slow;
        instCb.IsChecked = !slow;
        _syncingZoomChecks = false;
        RememberEditorChoice(d => d.DefaultZoomSlow != slow, d => d.DefaultZoomSlow = slow);

        if (_edit.SelectedSegmentIndex >= 0 && _edit.SelectedSegmentIndex < _edit.Segments.Count)
        {
            var seg = _edit.Segments[_edit.SelectedSegmentIndex];
            if (!seg.ZoomW.HasValue && _hasZoomBox && _zoomModeActive)
            {
                _zoomBoxTouched = true;
                _zoomSessionCreatedSegment = false;
                CommitZoomToSegment("StyleSelected");
                RenderZoomBox();
            }
            else if (seg.ZoomW.HasValue && seg.ZoomSlow != slow)
            {
                if (slow && !_edit.SlowZoomHasRoom(_edit.SelectedSegmentIndex, out double needSec))
                {
                    _syncingZoomChecks = true;
                    slowCb.IsChecked = false;
                    instCb.IsChecked = true;
                    _syncingZoomChecks = false;
                    NotifyError($"This zoom is too close to another gliding zoom — they need {needSec:0.0}s between them, " +
                                "or neither can glide. Move one of them apart first, or leave this one instant.");
                    RuntimeLog.Info("Granular",
                        $"Refused INSTANT→SLOW on block #{_edit.SelectedSegmentIndex + 1}: less than {needSec:0.###}s clear of another slow zoom.");
                    return;
                }

                PushUndo("change zoom style");
                _edit.Segments[_edit.SelectedSegmentIndex] = seg with { ZoomSlow = slow };
                RuntimeLog.Info("Granular", $"Zoom ramp mode → {(slow ? "SLOW" : "INSTANT")} on segment #{_edit.SelectedSegmentIndex + 1}.");
                RefreshSegmentList();
                RedrawTimeline();
                if (slow)
                {
                    var avail = _edit.GetSlowZoomGlideAvailability(_edit.SelectedSegmentIndex);
                    if (avail.AnyUnavailable)
                    {
                        SetStatus(avail.GetCombinedExplanation()!);
                    }
                }
            }
        }
    }

    private void SyncZoomModeChecksFromSegment()
    {
        bool slow;
        if (_edit.SelectedSegmentIndex >= 0 && _edit.SelectedSegmentIndex < _edit.Segments.Count
            && _edit.Segments[_edit.SelectedSegmentIndex].ZoomW.HasValue)
            slow = _edit.Segments[_edit.SelectedSegmentIndex].ZoomSlow;
        else
            slow = Infrastructure.SettingsManager.Instance.Defaults.DefaultZoomSlow;
        var slowCb = SlowZoomCheckCtl;
        var instCb = InstantZoomCheckCtl;
        _syncingZoomChecks = true;
        if (slowCb != null) slowCb.IsChecked = slow;
        if (instCb != null) instCb.IsChecked = !slow;
        _syncingZoomChecks = false;
    }

    private Avalonia.Rect GetVideoDisplayRect(Avalonia.Controls.Canvas canvas)
    {
        var (sw, sh) = FreeVideoStudio.Core.Media.CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        double srcAspect = sh > 0 ? (double)sw / sh : 16.0 / 9.0;
        double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;
        if (cw <= 1 || ch <= 1) return new Avalonia.Rect(0, 0, Math.Max(1, cw), Math.Max(1, ch));
        double vidW, vidH;
        if (cw / ch > srcAspect) { vidH = ch; vidW = ch * srcAspect; }
        else { vidW = cw; vidH = cw / srcAspect; }
        return new Avalonia.Rect((cw - vidW) / 2.0, (ch - vidH) / 2.0, vidW, vidH);
    }

    private void ToggleZoomMode()
    {
        if (_zoomModeActive) { ExitZoomMode(); return; }

        if (!EnsureZoomTargetSegment()) return;

        EnterZoomMode();
    }

    private bool _zoomSessionCreatedSegment;

    private int _zoomCreatedSegmentIndex = -1;

    private void PulseZoomConfirmFeedback()
    {
        try
        {
            UiSoundEffect.PlayMark();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void RemoveZoomFromSelectedSegment()
    {
        if (!_edit.HasSelectedSegment) return;
        var removed = _edit.RemoveZoomFromSelection();
        if (!removed.Ok) { NotifyError(removed.Error!); return; }

        ClearLiveZoomCrop();
        RuntimeLog.Info("Granular", $"Zoom removed from segment #{_edit.SelectedSegmentIndex + 1}.");
        RefreshSegmentList();
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        Notify("Zoom removed. Press ZOOM BY HAND to set a new one.");
    }

    private bool CreateZoomBlockFromPendingMarks()
    {
        double start = Math.Min(_edit.PendingStartMs, _edit.PendingEndMs);
        double end = Math.Max(_edit.PendingStartMs, _edit.PendingEndMs);
        var created = _edit.CreateZoomBlockFromPendingMarks(out int newIndex);
        if (!created.Ok) { NotifyError(created.Error!); return false; }

        AfterZoomContainerCreated(newIndex);
        SetStatus($"Zoom will cover your marked span, {FormatClock(start)} to {FormatClock(end)}.");
        return true;
    }

    private void AfterZoomContainerCreated(int newIndex)
    {
        SelectSegmentAt(newIndex);
        _zoomSessionCreatedSegment = true;
        _zoomCreatedSegmentIndex = _edit.SelectedSegmentIndex;
        RefreshSegmentList();
        RedrawTimeline();
    }

    private bool EnsureZoomTargetSegment()
    {
        if (_edit.SelectedSegment is { } selected)
        {
            if (selected.ZoomW.HasValue)
            {
                NotifyError($"Block #{_edit.SelectedSegmentIndex + 1} already has a zoom. Press REMOVE ZOOM to clear it first, or drag its markers on the timeline to change WHEN it happens.");
                return false;
            }
            return true;
        }

        if (_edit.PendingStartMs >= 0 && _edit.PendingEndMs >= 0)
        {
            return CreateZoomBlockFromPendingMarks();
        }

        if (_edit.PendingStartMs >= 0 && _edit.PendingEndMs < 0)
        {
            NotifyError("Mark an END first — press MARK END where the zoom should stop, then press ZOOM BY HAND.");
            return false;
        }

        double dur = GetDuration();
        if (dur <= 0)
        {
            NotifyError("Load a video first.");
            return false;
        }

        int playheadMs = (int)Math.Round(GetCurrentTime() * 1000.0);
        int timelineEndMs = (int)Math.Round(dur * 1000.0);

        if (_edit.FindSegmentAt(playheadMs) is int under)
        {
            SelectSegmentAt(under);
            SetStatus("Zoom will be added to the block under the playhead.");
            return true;
        }

        var created = _edit.CreateZoomContainerAt(playheadMs, timelineEndMs, out int newIndex);
        if (!created.Ok) { NotifyError(created.Error!); return false; }

        AfterZoomContainerCreated(newIndex);
        if (_edit.SelectedSegment is { } block)
            SetStatus($"Added a {(block.EndMs - block.StartMs) / 1000.0:0.0}s block at normal speed to hold the zoom — drag its edges to change when the zoom happens.");
        return true;
    }

    private void SelectSegmentAt(int index) => SelectSegment(index, jumpPlayhead: true);

    private async System.Threading.Tasks.Task CommitZoomAndPrimePreviewAsync()
    {
        if (!_gpuLiveZoomPreview) return;

        var busy = this.FindControl<Border>("ZoomApplyBusyOverlay");
        if (busy != null) busy.IsVisible = true;
        try
        {
            UpdateLiveZoomCrop();

            string want = _lastLiveCrop;
            for (int i = 0; i < 20 && want.Length > 0; i++)
            {
                var cropClient = _videoHost?.IpcClient;
                var applied = cropClient == null ? null : await cropClient.ReadPropertyAsync("video-crop");
                if (!string.IsNullOrEmpty(applied) && applied != "no") break;
                await System.Threading.Tasks.Task.Delay(50);
            }
            await System.Threading.Tasks.Task.Delay(150);
            RuntimeLog.Info("Granular", "APPLY ZOOM-IN: GPU live zoom preview primed.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Granular", $"GPU live zoom prime failed (falling back to box overlay): {ex.Message}");
        }
        finally
        {
            if (busy != null) busy.IsVisible = false;
        }
    }

    private void ClearLiveZoomCrop()
    {
        if (!_gpuLiveZoomPreview || _lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = _videoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }

    private void UpdateLiveZoomCrop()
    {
        if (!_gpuLiveZoomPreview || _videoHost?.IpcClient == null) return;
        if (_zoomModeActive) { ClearLiveZoomCrop(); return; }

        double curMs = _videoHost.IpcClient.IsPaused ? _playheadMs : Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _edit.TrimStartMs);
        double tSec = curMs / 1000.0;
        double durSec = Math.Max(0.1, ((_edit.TrimEndMs > 0 ? _edit.TrimEndMs : _videoHost.IpcClient.Duration * 1000.0) - _edit.TrimStartMs) / 1000.0);

        var (psw, psh) = FreeVideoStudio.Core.Media.CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (psw <= 0 || psh <= 0)
        {
            if (_videoHost.IpcClient.VideoWidth > 0 && _videoHost.IpcClient.VideoHeight > 0)
            {
                psw = _videoHost.IpcClient.VideoWidth;
                psh = _videoHost.IpcClient.VideoHeight;
            }
        }

        var result = FreeVideoStudio.Core.Media.ZoomPreviewSimulator.Compute(
            _edit.Segments, tSec, durSec, _edit.IsMobileFormat, psw, psh,
            trimStartSec: 0);

        if (!result.HasCrop) { ClearLiveZoomCrop(); return; }
        if (result.Crop == _lastLiveCrop) return;
        _lastLiveCrop = result.Crop;
        _ = _videoHost.IpcClient.SetPropertyAsync("video-crop", result.Crop);
    }

    private void EnterZoomMode()
    {
        var canvas = ZoomOverlayCanvasCtl;
        var zoomBtn = ZoomSegmentBtnCtl;
        if (canvas == null) return;

        ClearLiveZoomCrop();

        _zoomModeActive = true;
        UpdateGuideLine();
        UpdateDetachButtonForZoomMode();
        EnsureZoomVisuals(canvas);
        canvas.IsVisible = true;
        canvas.IsHitTestVisible = true;

        var stylePanel = ZoomStylePanelCtl;
        if (stylePanel != null) stylePanel.IsVisible = true;

        var seg = _edit.Segments[_edit.SelectedSegmentIndex];
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FreeVideoStudio.Core.Media.CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (seg.ZoomW.HasValue && seg.ZoomH.HasValue && seg.ZoomX.HasValue && seg.ZoomY.HasValue && sw > 0 && sh > 0)
        {
            double sx = vid.Width / sw, sy = vid.Height / sh;
            _zoomUiRect = new Avalonia.Rect(vid.X + seg.ZoomX.Value * sx, vid.Y + seg.ZoomY.Value * sy,
                                            seg.ZoomW.Value * sx, seg.ZoomH.Value * sy);
            _hasZoomBox = true;
            _zoomBoxTouched = true;
        }
        else
        {
            PlaceDefaultZoomBoxWhenLaidOut(canvas);
        }

        SyncZoomModeChecksFromSegment();
        RenderZoomBox();
        MaybeShowZoomTutorial(canvas);
        SetStatus(_zoomBoxTouched
            ? "Editing this zoom. Drag the box to re-aim it, or its corners to resize. Every change is saved as you go."
            : "Drag the box to aim it, or its corners to resize. The zoom starts the moment you touch it.");
    }

    private void ExitZoomMode()
    {
        var canvas = ZoomOverlayCanvasCtl;
        var zoomBtn = ZoomSegmentBtnCtl;
        _zoomModeActive = false;
        UpdateGuideLine();
        UpdateDetachButtonForZoomMode();
        _zoomDrag = ZoomDrag.None;
        if (canvas != null) canvas.IsVisible = false;

        var stylePanel = ZoomStylePanelCtl;
        if (stylePanel != null) stylePanel.IsVisible = false;

        HideZoomTutorial();
        if (zoomBtn != null)
        {
            zoomBtn.Content = "🔍 ZOOM BY HAND";
            zoomBtn.Classes.Remove("Success");
            if (!zoomBtn.Classes.Contains("ZoomAction")) zoomBtn.Classes.Add("ZoomAction");
        }

        if (_zoomFactorBadge != null) _zoomFactorBadge.IsVisible = false;
        UpdateAiSmartZoomBtnVisualState();
        UpdateAiCommentaryBtnVisualState();

        int orphan = _zoomCreatedSegmentIndex;
        if (_zoomSessionCreatedSegment && !_zoomBoxTouched
            && orphan >= 0 && orphan < _edit.Segments.Count
            && !_edit.Segments[orphan].ZoomW.HasValue)
        {
            RuntimeLog.Info("Granular",
                $"Zoom cancelled before it was aimed — removing the empty block it would have used (#{orphan + 1}).");
            _edit.Segments.RemoveAt(orphan);

            if (_edit.SelectedSegmentIndex == orphan) _edit.SelectedSegmentIndex = -1;
            else if (_edit.SelectedSegmentIndex > orphan) _edit.SelectedSegmentIndex--;

            RefreshSegmentList();
            RedrawTimeline();
            UpdateDeleteButtonVisibility();
        }

        _hasZoomBox = false;
        _zoomBoxTouched = false;
        _zoomSessionCreatedSegment = false;
        _zoomCreatedSegmentIndex = -1;
    }

    private void EnsureZoomVisuals(Avalonia.Controls.Canvas canvas)
    {
        if (_zoomBoxRect != null) return;
        var dimBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#80000000"));
        for (int i = 0; i < 4; i++)
        {
            _zoomDim[i] = new Avalonia.Controls.Shapes.Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            canvas.Children.Add(_zoomDim[i]);
        }
        _zoomBoxRect = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = ZoomAntsBrush(),
            StrokeThickness = ZoomBandThicknessPx,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 2 },
            StrokeDashOffset = _marchingAntsOffset,
            Fill = Avalonia.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
        canvas.Children.Add(_zoomBoxRect);
        for (int i = 0; i < 4; i++)
        {
            _zoomHandles[i] = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = ZoomHandleVisualPx, Height = ZoomHandleVisualPx,
                Fill = ZoomAntsBrush(),
                Stroke = Avalonia.Media.Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false
            };
            canvas.Children.Add(_zoomHandles[i]);
        }

        for (int i = 0; i < 2; i++)
        {
            var shade = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#99000000")),
                IsHitTestVisible = false,
                IsVisible = false,
                ZIndex = -1
            };
            _portraitShade[i] = shade;
            canvas.Children.Add(shade);
        }

        for (int i = 0; i < 2; i++)
        {
            var edge = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = 2,
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#38bdf8")),
                IsHitTestVisible = false,
                IsVisible = false,
                ZIndex = 5
            };
            _portraitEdges[i] = edge;
            canvas.Children.Add(edge);
        }

        _zoomFactorText = new TextBlock
        {
            Foreground = Infrastructure.ThemeResources.Brush(this, "AppOnAccentTextBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ffffff"))),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(14),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        _zoomFactorBadge = new Border
        {
            Background = ZoomBrush(),
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Thickness(9, 3),
            IsHitTestVisible = false,
            IsVisible = false,
            ZIndex = 20,
            Child = _zoomFactorText
        };
        canvas.Children.Add(_zoomFactorBadge);

        for (int i = 0; i < 2; i++)
        {
            var guide = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = ZoomBrush(),
                IsHitTestVisible = false,
                IsVisible = false,
                ZIndex = 15
            };
            _zoomGuides[i] = guide;
            canvas.Children.Add(guide);
        }
    }

    private void RenderZoomGuides(Avalonia.Controls.Canvas canvas)
    {
        if (_zoomGuides[0] == null || _zoomGuides[1] == null) return;
        var b = ZoomBoundsUi(canvas);
        bool dragging = _zoomDrag != ZoomDrag.None && _hasZoomBox && b.Width > 0 && b.Height > 0;
        void Show(Avalonia.Controls.Shapes.Rectangle g, Infrastructure.ZoomGuideState st, bool vertical)
        {
            if (!dragging || st == Infrastructure.ZoomGuideState.Hidden) { g.IsVisible = false; return; }
            bool snapped = st == Infrastructure.ZoomGuideState.Snapped;
            double t = snapped ? 2.0 : 1.0;
            g.Opacity = snapped ? 0.95 : 0.35;
            g.IsVisible = true;
            if (vertical)
            {
                g.Width = t; g.Height = b.Height;
                Avalonia.Controls.Canvas.SetLeft(g, b.X + b.Width / 2.0 - t / 2.0);
                Avalonia.Controls.Canvas.SetTop(g, b.Y);
            }
            else
            {
                g.Width = b.Width; g.Height = t;
                Avalonia.Controls.Canvas.SetLeft(g, b.X);
                Avalonia.Controls.Canvas.SetTop(g, b.Y + b.Height / 2.0 - t / 2.0);
            }
        }
        Show(_zoomGuides[0]!, _zoomGuideV, vertical: true);
        Show(_zoomGuides[1]!, _zoomGuideH, vertical: false);
    }

    private bool _zoomBoxTouched;
    private readonly Avalonia.Controls.Shapes.Rectangle?[] _zoomGuides = new Avalonia.Controls.Shapes.Rectangle?[2];
    private Infrastructure.ZoomGuideState _zoomGuideV, _zoomGuideH;
    private Border? _zoomFactorBadge;
    private TextBlock? _zoomFactorText;

    private readonly Avalonia.Controls.Shapes.Rectangle?[] _portraitEdges = new Avalonia.Controls.Shapes.Rectangle?[2];

    private readonly Avalonia.Controls.Shapes.Rectangle?[] _portraitShade = new Avalonia.Controls.Shapes.Rectangle?[2];

    private void RenderPortraitBoundary(Avalonia.Controls.Canvas canvas)
    {
        if (_portraitEdges[0] == null || _portraitEdges[1] == null
            || _portraitShade[0] == null || _portraitShade[1] == null) return;

        void HideAll()
        {
            _portraitEdges[0]!.IsVisible = false;
            _portraitEdges[1]!.IsVisible = false;
            _portraitShade[0]!.IsVisible = false;
            _portraitShade[1]!.IsVisible = false;
        }

        if (!_edit.IsMobileFormat) { HideAll(); return; }

        var vid = GetVideoDisplayRect(canvas);
        if (vid.Width <= 0 || vid.Height <= 0) { HideAll(); return; }

        double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;
        double portraitW = vid.Height * (2.0 / 3.0);
        double left = vid.X + (vid.Width - portraitW) / 2.0;
        double right = left + portraitW;

        void Shade(Avalonia.Controls.Shapes.Rectangle r, double x, double w)
        {
            r.IsVisible = true;
            r.Width = Math.Max(0, w);
            r.Height = Math.Max(0, ch);
            Avalonia.Controls.Canvas.SetLeft(r, Math.Max(0, x));
            Avalonia.Controls.Canvas.SetTop(r, 0);
        }

        Shade(_portraitShade[0]!, 0, left);
        Shade(_portraitShade[1]!, right, cw - right);

        void Place(Avalonia.Controls.Shapes.Rectangle edge, double x)
        {
            edge.IsVisible = true;
            edge.Height = vid.Height;
            Avalonia.Controls.Canvas.SetLeft(edge, x - 1);
            Avalonia.Controls.Canvas.SetTop(edge, vid.Y);
        }

        Place(_portraitEdges[0]!, left);
        Place(_portraitEdges[1]!, right);
    }

    private bool _isZoomRenderPending = false;
    private void PlaceDefaultZoomBoxWhenLaidOut(Avalonia.Controls.Canvas canvas, bool isRetry = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_zoomModeActive) return;
            if (_edit.SelectedSegmentIndex < 0 || _edit.SelectedSegmentIndex >= _edit.Segments.Count) return;
            if (_edit.Segments[_edit.SelectedSegmentIndex].ZoomW.HasValue) return;

            if (!IsVideoRectUsable(GetVideoDisplayRect(canvas)))
            {
                if (!isRetry) PlaceDefaultZoomBoxWhenLaidOut(canvas, isRetry: true);
                else RuntimeLog.Fail("Granular", "Could not place the default zoom box — the preview never reported a size.");
                return;
            }

            _zoomUiRect = BuildDefaultZoomRect(canvas);
            _hasZoomBox = true;
            _zoomBoxTouched = false;
            RenderZoomBox();
        }, isRetry ? Avalonia.Threading.DispatcherPriority.Background
                   : Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private Avalonia.Rect BuildDefaultZoomRect(Avalonia.Controls.Canvas canvas)
    {
        var bounds = ZoomBoundsUi(canvas);
        var vid = GetVideoDisplayRect(canvas);
        double aspect = ZoomAspect;

        double w = bounds.Width / DefaultZoomFactor;
        double h = w / aspect;
        if (h > bounds.Height) { h = bounds.Height; w = h * aspect; }

        double minW = MinZoomWidthUi(vid.Width / Math.Max(1, GetSourceW()));
        if (w < minW) { w = Math.Min(minW, bounds.Width); h = w / aspect; }

        return new Avalonia.Rect(
            bounds.X + (bounds.Width - w) / 2.0,
            bounds.Y + (bounds.Height - h) / 2.0,
            w, h);
    }

    private void RenderZoomBox()
    {
        if (_isZoomRenderPending) return;
        _isZoomRenderPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _isZoomRenderPending = false;
            var canvas = ZoomOverlayCanvasCtl;
            if (canvas == null || _zoomBoxRect == null) return;
            double cw = canvas.Bounds.Width, ch = canvas.Bounds.Height;

            RenderPortraitBoundary(canvas);
            RenderZoomGuides(canvas);

            if (!_hasZoomBox)
            {
                _zoomBoxRect.IsVisible = false;
                foreach (var h in _zoomHandles) h.IsVisible = false;
                for (int i = 0; i < 4; i++) if (_zoomDim[i] != null) { _zoomDim[i].Width = 0; _zoomDim[i].Height = 0; }
                if (_zoomFactorBadge != null) _zoomFactorBadge.IsVisible = false;
                return;
            }

            var r = _zoomUiRect;
            _zoomBoxRect.IsVisible = true;
            _zoomBoxRect.Width = r.Width; _zoomBoxRect.Height = r.Height;
            Avalonia.Controls.Canvas.SetLeft(_zoomBoxRect, r.X);
            Avalonia.Controls.Canvas.SetTop(_zoomBoxRect, r.Y);

            void Dim(int i, double x, double y, double w, double h)
            {
                var d = _zoomDim[i]; if (d == null) return;
                d.Width = Math.Max(0, w); d.Height = Math.Max(0, h);
                Avalonia.Controls.Canvas.SetLeft(d, x); Avalonia.Controls.Canvas.SetTop(d, y);
            }
            Dim(0, 0, 0, cw, r.Y);
            Dim(1, 0, r.Bottom, cw, ch - r.Bottom);
            Dim(2, 0, r.Y, r.X, r.Height);
            Dim(3, r.Right, r.Y, cw - r.Right, r.Height);

            var corners = new[] { new Avalonia.Point(r.X, r.Y), new Avalonia.Point(r.Right, r.Y),
                                  new Avalonia.Point(r.X, r.Bottom), new Avalonia.Point(r.Right, r.Bottom) };
            for (int i = 0; i < 4; i++)
            {
                _zoomHandles[i].IsVisible = true;
                Avalonia.Controls.Canvas.SetLeft(_zoomHandles[i], corners[i].X - ZoomHandleVisualPx / 2);
                Avalonia.Controls.Canvas.SetTop(_zoomHandles[i], corners[i].Y - ZoomHandleVisualPx / 2);
            }

            var vidRect = GetVideoDisplayRect(canvas);

            _zoomBoxRect.Opacity = _zoomBoxTouched ? 1.0 : 0.45;
            foreach (var h in _zoomHandles) h.Opacity = _zoomBoxTouched ? 1.0 : 0.45;

            if (_zoomFactorBadge != null && _zoomFactorText != null)
            {
                double factor = ZoomFactorOf(r, ZoomBoundsUi(canvas));
                _zoomFactorText.Text = $"{factor:0.0}x";
                _zoomFactorBadge.IsVisible = true;
                _zoomFactorBadge.Measure(new Avalonia.Size(cw, ch));
                double bw = _zoomFactorBadge.DesiredSize.Width, bh = _zoomFactorBadge.DesiredSize.Height;
                double fx = Math.Clamp(r.X + (r.Width - bw) / 2, 2, Math.Max(2, cw - bw - 2));
                double fy = r.Y - bh - 8;
                if (fy < 2) fy = Math.Min(r.Bottom + 8, Math.Max(2, ch - bh - 2));
                Avalonia.Controls.Canvas.SetLeft(_zoomFactorBadge, fx);
                Avalonia.Controls.Canvas.SetTop(_zoomFactorBadge, fy);
            }
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private void ZoomCanvas_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!_zoomModeActive || sender is not Avalonia.Controls.Canvas canvas) return;
        if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        HideZoomTutorial();

        var p = e.GetPosition(canvas);
        _zoomDragStart = p;
        _zoomStartRect = _zoomUiRect;

        if (_hasZoomBox)
        {
            var r = _zoomUiRect;
            double hz = ZoomHandlePx;
            bool NearCorner(Avalonia.Point c) => Math.Abs(p.X - c.X) <= hz && Math.Abs(p.Y - c.Y) <= hz;
            if (NearCorner(new Avalonia.Point(r.X, r.Y))) _zoomDrag = ZoomDrag.ResizeTL;
            else if (NearCorner(new Avalonia.Point(r.Right, r.Y))) _zoomDrag = ZoomDrag.ResizeTR;
            else if (NearCorner(new Avalonia.Point(r.X, r.Bottom))) _zoomDrag = ZoomDrag.ResizeBL;
            else if (NearCorner(new Avalonia.Point(r.Right, r.Bottom))) _zoomDrag = ZoomDrag.ResizeBR;
            else if (r.Contains(p)) { _zoomDrag = ZoomDrag.Move; canvas.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand); }
            else _zoomDrag = ZoomDrag.Draw;
        }
        else _zoomDrag = ZoomDrag.Draw;

        e.Pointer.Capture(canvas);
        e.Handled = true;
    }

    private Avalonia.Input.StandardCursorType ZoomHoverCursor(Avalonia.Point p)
    {
        if (_hasZoomBox)
        {
            var r = _zoomUiRect;
            double hz = ZoomHandlePx;
            bool Near(Avalonia.Point c) => Math.Abs(p.X - c.X) <= hz && Math.Abs(p.Y - c.Y) <= hz;
            if (Near(new Avalonia.Point(r.X, r.Y)) || Near(new Avalonia.Point(r.Right, r.Bottom)))
                return Avalonia.Input.StandardCursorType.TopLeftCorner;
            if (Near(new Avalonia.Point(r.Right, r.Y)) || Near(new Avalonia.Point(r.X, r.Bottom)))
                return Avalonia.Input.StandardCursorType.TopRightCorner;
            if (r.Contains(p)) return Avalonia.Input.StandardCursorType.Hand;
        }
        return Avalonia.Input.StandardCursorType.Cross;
    }

    private void ZoomCanvas_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_zoomModeActive || sender is not Avalonia.Controls.Canvas canvas) return;

        if (_zoomDrag == ZoomDrag.None)
        {
            canvas.Cursor = new Avalonia.Input.Cursor(ZoomHoverCursor(e.GetPosition(canvas)));
            return;
        }

        var vid = GetVideoDisplayRect(canvas);
        var bounds = ZoomBoundsUi(canvas);

        var p = e.GetPosition(canvas);
        double aspect = ZoomAspect;

        double scale = vid.Width / Math.Max(1, GetSourceW());
        double minW = MinZoomWidthUi(scale);
        double minH = minW / aspect;

        if (_zoomDrag == ZoomDrag.Draw || _zoomDrag == ZoomDrag.ResizeBR || _zoomDrag == ZoomDrag.ResizeTR
            || _zoomDrag == ZoomDrag.ResizeBL || _zoomDrag == ZoomDrag.ResizeTL)
        {
            Avalonia.Point anchor = _zoomDrag switch
            {
                ZoomDrag.ResizeBR => _zoomStartRect.TopLeft,
                ZoomDrag.ResizeTR => new Avalonia.Point(_zoomStartRect.X, _zoomStartRect.Bottom),
                ZoomDrag.ResizeBL => new Avalonia.Point(_zoomStartRect.Right, _zoomStartRect.Y),
                ZoomDrag.ResizeTL => _zoomStartRect.BottomRight,
                _ => _zoomDragStart
            };
            double w = Math.Abs(p.X - anchor.X);
            double h = w / aspect;
            if (w < minW) { w = minW; h = minH; }
            double x = p.X >= anchor.X ? anchor.X : anchor.X - w;
            double y = p.Y >= anchor.Y ? anchor.Y : anchor.Y - h;
            _zoomUiRect = ClampToVideo(new Avalonia.Rect(x, y, w, h), bounds, aspect, minW, minH);
            _hasZoomBox = true;
            var guides = Infrastructure.ZoomBoxSnap.Snap(_zoomUiRect, bounds, allowSnap: false);
            _zoomGuideV = guides.Vertical;
            _zoomGuideH = guides.Horizontal;
        }
        else if (_zoomDrag == ZoomDrag.Move)
        {
            double dx = p.X - _zoomDragStart.X, dy = p.Y - _zoomDragStart.Y;
            double maxX = Math.Max(bounds.X, bounds.Right - _zoomStartRect.Width);
            double maxY = Math.Max(bounds.Y, bounds.Bottom - _zoomStartRect.Height);
            double nx = Math.Clamp(_zoomStartRect.X + dx, bounds.X, maxX);
            double ny = Math.Clamp(_zoomStartRect.Y + dy, bounds.Y, maxY);
            var snap = Infrastructure.ZoomBoxSnap.Snap(
                new Avalonia.Rect(nx, ny, _zoomStartRect.Width, _zoomStartRect.Height), bounds, allowSnap: true);
            _zoomUiRect = snap.Box;
            _zoomGuideV = snap.Vertical;
            _zoomGuideH = snap.Horizontal;
        }

        _zoomDragMoved = true;
        RenderZoomBox();
        e.Handled = true;
    }

    private bool _zoomDragMoved;

    private void ZoomCanvas_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (sender is Avalonia.Controls.Canvas canvas) { e.Pointer.Capture(null); canvas.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Cross); }
        if (!_zoomModeActive) return;
        bool wasDraw = _zoomDrag == ZoomDrag.Draw;
        bool wasResize = _zoomDrag is ZoomDrag.ResizeTL or ZoomDrag.ResizeTR or ZoomDrag.ResizeBL or ZoomDrag.ResizeBR;
        bool moved = _zoomDragMoved;
        _zoomDrag = ZoomDrag.None;
        _zoomDragMoved = false;
        _zoomGuideV = _zoomGuideH = Infrastructure.ZoomGuideState.Hidden;
        RenderZoomBox();
        if (!_hasZoomBox) return;

        if (wasDraw && !moved)
        {
            _zoomUiRect = _zoomStartRect;
            RenderZoomBox();
            SetStatus("Drag to draw a new box, or drag the one that is there to move it.");
            e.Handled = true;
            return;
        }

        bool firstTouch = !_zoomBoxTouched;
        _zoomBoxTouched = true;
        _zoomSessionCreatedSegment = false;

        CommitZoomToSegment(wasDraw ? "Created" : wasResize ? "Resized" : "Moved");

        if (firstTouch)
        {
            PulseZoomConfirmFeedback();
            RenderZoomBox();
        }

        _ = CommitZoomAndPrimePreviewAsync();

        e.Handled = true;
    }

    private Avalonia.Rect ClampToVideo(Avalonia.Rect r, Avalonia.Rect vid, double aspect, double minW, double minH)
    {
        double w = Math.Min(r.Width, vid.Width);
        double h = w / aspect;
        if (h > vid.Height) { h = vid.Height; w = h * aspect; }
        if (w < minW) { w = minW; h = minH; }

        double maxX = Math.Max(vid.X, vid.Right - w);
        double maxY = Math.Max(vid.Y, vid.Bottom - h);
        double x = Math.Clamp(r.X, vid.X, maxX);
        double y = Math.Clamp(r.Y, vid.Y, maxY);
        return new Avalonia.Rect(x, y, w, h);
    }

    private int GetSourceW() => FreeVideoStudio.Core.Media.CoordinateMath.GetResolutionInts(_edit.OriginalResolution).w;

    private double ZoomOutputWidthSource()
    {
        if (!_edit.IsMobileFormat) return Math.Max(1, GetSourceW());

        var (sw, sh) = FreeVideoStudio.Core.Media.CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (sw <= 0 || sh <= 0) return FreeVideoStudio.Core.Media.CoordinateConstants.PortraitW;

        double scale = Math.Max(
            FreeVideoStudio.Core.Media.CoordinateConstants.InternalW / (double)sw,
            FreeVideoStudio.Core.Media.CoordinateConstants.InternalH / (double)sh);
        return FreeVideoStudio.Core.Media.CoordinateConstants.InternalW / scale;
    }

    private double MinZoomWidthUi(double uiPerSourcePx)
        => (ZoomOutputWidthSource() / MaxZoomUpscale) * uiPerSourcePx;

    private Avalonia.Rect ZoomBoundsUi(Avalonia.Controls.Canvas canvas)
    {
        var vid = GetVideoDisplayRect(canvas);
        if (!_edit.IsMobileFormat) return vid;
        double portraitW = vid.Height * (2.0 / 3.0);
        return new Avalonia.Rect(vid.X + (vid.Width - portraitW) / 2.0, vid.Y, portraitW, vid.Height);
    }

    private double ZoomFactorOf(Avalonia.Rect boxUi, Avalonia.Rect boundsUi)
        => boxUi.Width < 1 ? 1.0 : boundsUi.Width / boxUi.Width;

    private void CommitZoomToSegment(string action)
    {
        var canvas = ZoomOverlayCanvasCtl;
        if (canvas == null || !_edit.HasSelectedSegment) return;
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FreeVideoStudio.Core.Media.CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (!IsVideoRectUsable(vid) || sw <= 0 || sh <= 0)
        {
            RuntimeLog.Info("Granular", "Zoom commit skipped — the preview has no usable size yet.");
            return;
        }

        double sx = sw / vid.Width, sy = sh / vid.Height;
        int zx = Even(Math.Clamp((int)Math.Round((_zoomUiRect.X - vid.X) * sx), 0, sw - 2));
        int zy = Even(Math.Clamp((int)Math.Round((_zoomUiRect.Y - vid.Y) * sy), 0, sh - 2));
        int zw = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Width * sx), 2, sw - zx));
        int zh = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Height * sy), 2, sh - zy));

        int minWsrc = Even((int)Math.Round(ZoomOutputWidthSource() / MaxZoomUpscale));
        if (minWsrc >= 2 && zw < minWsrc)
        {
            int cx = zx + zw / 2, cy = zy + zh / 2;
            int fixedW = Math.Min(minWsrc, Even(sw));
            int fixedH = Even((int)Math.Round(fixedW / ZoomAspect));
            if (fixedH > sh) { fixedH = Even(sh); fixedW = Even((int)Math.Round(fixedH * ZoomAspect)); }

            int newX = Even(Math.Clamp(cx - fixedW / 2, 0, Math.Max(0, sw - fixedW)));
            int newY = Even(Math.Clamp(cy - fixedH / 2, 0, Math.Max(0, sh - fixedH)));

            RuntimeLog.Info("Granular",
                $"Zoom box was below the {MaxZoomUpscale:0}x quality floor (W={zw}) — snapped up to {fixedW}x{fixedH}.");
            zx = newX; zy = newY; zw = fixedW; zh = fixedH;
        }

        var seg = _edit.Segments[_edit.SelectedSegmentIndex];
        bool slow = ZoomSlowSelected;
        _edit.ApplyZoomToSelection(zx, zy, zw, zh, $"{sw}x{sh}", slow);

        RuntimeLog.Info("Granular", $"Zoom {action} on segment #{_edit.SelectedSegmentIndex + 1} (start {FormatMs(seg.StartMs)}): X={zx} Y={zy} W={zw} H={zh} src={sw}x{sh} mobile={_edit.IsMobileFormat} ramp={(slow ? "SLOW" : "INSTANT")}.");
        RefreshSegmentList();
        RedrawTimeline();
    }

    private static bool _zoomTutorialShownThisSession = false;
    private void MaybeShowZoomTutorial(Avalonia.Controls.Canvas canvas)
    {
        if (_zoomTutorialShownThisSession) return;
        if (ReadZoomTutorialCount() >= 3) return;
        _zoomTutorialShownThisSession = true;
        WriteZoomTutorialCount(ReadZoomTutorialCount() + 1);

        if (_zoomTutorial == null)
        {
            _zoomTutorial = new Avalonia.Controls.Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B0000000")),
                BorderBrush = ZoomBrush(),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 12),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "CLICK AND DRAG TO DRAW ZOOM BOX",
                    Foreground = ZoomBrush(),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(16)
                }
            };
            canvas.Children.Add(_zoomTutorial);
        }
        _zoomTutorial.IsVisible = true;
        _zoomTutorial.Opacity = 1;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_zoomTutorial == null || !_zoomModeActive) return;
            _zoomTutorial.Measure(Avalonia.Size.Infinity);
            double tw = _zoomTutorial.DesiredSize.Width;
            Avalonia.Controls.Canvas.SetLeft(_zoomTutorial, Math.Max(0, (canvas.Bounds.Width - tw) / 2));
            Avalonia.Controls.Canvas.SetTop(_zoomTutorial, 40);

            _zoomTutorialTimer?.Stop();
            _zoomTutorialTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _zoomTutorialTimer.Tick += (_, __) => HideZoomTutorial();
            _zoomTutorialTimer.Start();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void HideZoomTutorial()
    {
        _zoomTutorialTimer?.Stop();
        _zoomTutorialTimer = null;
        if (_zoomTutorial != null) { _zoomTutorial.IsVisible = false; }
    }

    private bool SkipPreviewOutOfCut()
    {
        try
        {
            if (_edit.Cuts.Count == 0) return false;
            var ipc = _videoHost?.IpcClient;
            if (ipc == null) return false;

            if (_isCurrentlyFrozen || _isCanvasScrubbing) return false;

            double nowMs = ipc.CurrentTime * 1000.0;
            foreach (var cut in _edit.Cuts)
            {
                double absStartMs = cut.StartMs + _edit.TrimStartMs;
                double absEndMs = cut.EndMs + _edit.TrimStartMs;
                if (nowMs <= absStartMs + 1 || nowMs >= absEndMs - 1) continue;

                double trimEndMs = _edit.TrimEndMs > 0 ? _edit.TrimEndMs : absEndMs;
                double toSec = Math.Min(absEndMs, trimEndMs) / 1000.0;

                _ = ipc.SetPropertyAsync("time-pos",
                    toSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                _memePreview?.NotifySeek();
                return true;
            }
        }
        catch (System.Exception ex) { RuntimeLog.SwallowedThrottled(ex); }

        return false;
    }

    private void UpdateZoomPlayheadOverlay()
    {
        if (_zoomModeActive) return;
        var canvas = ZoomOverlayCanvasCtl;
        if (canvas == null || _videoHost?.IpcClient == null) return;

        if (_gpuLiveZoomPreview)
        {
            if (canvas.IsVisible) canvas.IsVisible = false;
            return;
        }

        double tRelMs = _videoHost.IpcClient.IsPaused ? _playheadMs : Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _edit.TrimStartMs);
        SpeedSegment? active = null;
        foreach (var s in _edit.Segments)
            if (s.ZoomW.HasValue && tRelMs >= s.StartMs && tRelMs <= s.EndMs) { active = s; break; }

        if (active == null) { if (canvas.IsVisible) { canvas.IsVisible = false; } return; }

        EnsureZoomVisuals(canvas);
        var vid = GetVideoDisplayRect(canvas);
        var (sw, sh) = FreeVideoStudio.Core.Media.CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (sw <= 0 || sh <= 0) return;
        double scx = vid.Width / sw, scy = vid.Height / sh;
        _zoomUiRect = new Avalonia.Rect(vid.X + active.ZoomX!.Value * scx, vid.Y + active.ZoomY!.Value * scy,
                                        active.ZoomW!.Value * scx, active.ZoomH!.Value * scy);
        _hasZoomBox = true;
        canvas.IsVisible = true;
        canvas.IsHitTestVisible = false;
        RenderZoomBox();
        foreach (var h in _zoomHandles) h.IsVisible = false;
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_editorClosing || _videoHost?.IpcClient == null) return;

        _memePreview?.Tick();
        if (_memePreview != null && _memePreview.IsActive)
        {
            _cornerMemes?.Hide();
            HoldCaretDuringMeme();
            UpdatePreviewLayers(_videoHost.IpcClient.CurrentTime, double.PositiveInfinity, memeActive: true);
            return;
        }

        if (SkipPreviewOutOfCut()) return;

        if (_videoHost.IpcClient.VideoWidth > 0 && _videoHost.IpcClient.VideoHeight > 0)
        {
            string liveRes = $"{_videoHost.IpcClient.VideoWidth}x{_videoHost.IpcClient.VideoHeight}";
            if (liveRes != _edit.OriginalResolution) _edit.OriginalResolution = liveRes;
        }
        double curPlaybackRelMs = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _edit.TrimStartMs);
        UpdateEditorCornerMemes(curPlaybackRelMs);
        if (_zoomModeActive && _edit.SelectedSegmentIndex >= 0 && _edit.SelectedSegmentIndex < _edit.Segments.Count)
        {
            var activeSeg = _edit.Segments[_edit.SelectedSegmentIndex];
            double zStart = activeSeg.ZoomStartMs ?? activeSeg.StartMs;
            double zEnd = activeSeg.ZoomEndMs ?? activeSeg.EndMs;
            if (curPlaybackRelMs < zStart - 50 || curPlaybackRelMs > zEnd + 50)
            {
                if (_hasZoomBox && !_zoomBoxTouched && !activeSeg.ZoomW.HasValue)
                {
                    CommitZoomToSegment("AutoCommitPlaybackExit");
                }
                ExitZoomMode();
            }
        }

        UpdateZoomPlayheadOverlay();
        UpdateLiveZoomCrop();

        double t = _videoHost.IpcClient.CurrentTime;
        double fullDur = _videoHost.IpcClient.Duration;

        double trimEndSec = (_edit.TrimEndMs > 0) ? _edit.TrimEndMs / 1000.0 : fullDur;
        if (t >= trimEndSec && !_videoHost.IpcClient.IsPaused)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", trimEndSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        double trimStartSec = _edit.TrimStartMs / 1000.0;
        double relTime = Math.Max(0, t - trimStartSec);
        double trimDurSec = Math.Max(0.1, trimEndSec - trimStartSec);

        if (fullDur > 0)
        {
            if (!_isTimelineDrawn)
            {
                RedrawTimeline();
                _isTimelineDrawn = true;
            }
        }

        double currentRelMs = relTime * 1000.0;
        double currentAbsMs = currentRelMs + _edit.TrimStartMs;

        double prevFreezeTickAbsMs = _prevFreezeTickAbsMs;
        _prevFreezeTickAbsMs = currentAbsMs;

        if (_edit.FreezeTimeMs >= 0 && currentAbsMs < _edit.FreezeTimeMs - 40) _freezeArmed = true;

        if (_edit.FreezeTimeMs >= 0 && _freezeArmed && !_isCurrentlyFrozen && !_videoHost.IpcClient.IsPaused)
        {
            if (prevFreezeTickAbsMs >= 0
                && prevFreezeTickAbsMs <= _edit.FreezeTimeMs + 60
                && currentAbsMs >= _edit.FreezeTimeMs)
            {
                _isCurrentlyFrozen = true;
                _freezeArmed = false;
                _freezeStartTime = DateTime.UtcNow;
                _freezeSound.PlayHold(_edit.FreezeDurationS, FreezeLoudnessPath, SpeedEditorGameBalance, FreezeTailRoomSec());
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
                _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", (_edit.FreezeTimeMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                _memePreview?.NotifySeek();
                return;
            }
        }
        else if (_isCurrentlyFrozen)
        {
            double heldFor = (DateTime.UtcNow - _freezeStartTime).TotalSeconds;
            if (heldFor >= _edit.FreezeDurationS)
            {
                _isCurrentlyFrozen = false;
                _holdCaretOutSec = null;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            else
            {
                if (!_isCanvasScrubbing)
                {
                    _holdCaretOutSec = FreezeHoldStartOutSec() + Math.Clamp(heldFor, 0, _edit.FreezeDurationS);
                    UpdateCaret();
                }
                return;
            }
        }

        FollowPreviewSpeed();
        _ = _previewSpeed.ApplyAt(_videoHost.IpcClient, currentAbsMs / 1000.0, !_videoHost.IpcClient.IsPaused && !_videoHost.IpcClient.IsSeeking && !_isSeeking,
            CurrentPreviewTimeMapper()(t));

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = _videoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
        }

        if (trimDurSec > 0 && !_isCanvasScrubbing)
        {
            if (_memeCaretSticky && _videoHost.IpcClient.IsPaused)
            {
            }
            else
            {
                _memeCaretSticky = false;
                _holdCaretOutSec = null;
                _playheadMs = relTime * 1000.0;
                UpdateCaret();
            }
        }

        UpdatePreviewLayers(t, trimEndSec, memeActive: false);
    }

    private void FollowPreviewSpeed()
    {
        _previewSpeed.IsSuspended ??= () => _memePreview?.IsActive == true;
        _previewSpeed.UseSchedule(_edit.Segments, _edit.BaseSpeed, _edit.TrimStartMs);
        _previewSpeed.Follow(_videoHost?.IpcClient);
    }

    internal static Func<double, double> EditorPreviewTimeMapper(OutputTimeline timeline, double trimStartMs) =>
        absoluteSeconds => timeline.SourceToOutput(absoluteSeconds - trimStartMs / 1000.0);

    internal static OutputTimeline EditorPreviewTimeline(GranularEditSession edit, double durationSec, Services.EditorTimelineCache cache) =>
        cache.Get(Math.Max(0.001, durationSec) * 1000, edit.Segments, edit.Cuts, [], edit.BaseSpeed,
            edit.FreezeTimeMs >= 0 ? edit.FreezeTimeMs - edit.TrimStartMs : -1, edit.FreezeDurationS);

    private Func<double, double> CurrentPreviewTimeMapper()
    {
        var voiceTimeline = EditorPreviewTimeline(_edit, GetDuration(), _voiceTimelineCache);
        if (!ReferenceEquals(_voiceTimeline, voiceTimeline) || _voiceTimeMapper == null)
        {
            _voiceTimeline = voiceTimeline;
            _voiceTimeMapper = EditorPreviewTimeMapper(voiceTimeline, _edit.TrimStartMs);
        }
        return _voiceTimeMapper;
    }

    private void UpdatePreviewLayers(double t, double trimEndSec, bool memeActive)
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc == null) return;
        bool hasVoice = _voiceOverPlayer.Result != null;
        if (!hasVoice && !_musicBed.HasMusic) return;
        var timeMapper = CurrentPreviewTimeMapper();
        double editedTimeSec = _isCurrentlyFrozen
            ? FreezeHoldStartOutSec() + Math.Clamp((DateTime.UtcNow - _freezeStartTime).TotalSeconds, 0, _edit.FreezeDurationS)
            : timeMapper(t);
        bool paused = ipc.IsPaused && !_isCurrentlyFrozen;
        bool ended = !memeActive && t >= trimEndSec;
        if (hasVoice)
            _voiceOverPlayer.UpdatePlayback(paused || memeActive, ended, editedTimeSec, timeMapper, _isCurrentlyFrozen && !memeActive);
        var inlineMemes = new List<(double AnchorOutputSec, double DurationSec)>();
        foreach (var m in FreeVideoStudio.Core.Media.MemePlacement.InlineOnly(_edit.Memes))
            inlineMemes.Add((timeMapper(_edit.TrimStartMs / 1000.0 + m.AtSourceSecRelative), m.DurationSec));
        _musicBed.KeepMusicDuringMeme = FreeVideoStudio.Core.Media.MemePlacement.AnyInlineKeepsMusic(_edit.Memes);
        _musicBed.Update(new FreeVideoStudio.App.Controls.MusicPreviewFrame(_edit.TrimStartMs,
            _edit.TrimStartMs + GetDuration() * 1000.0, ms => timeMapper(ms / 1000.0), editedTimeSec,
            !paused && !ended, ipc.IsSeeking || _isSeeking, !_isCanvasScrubbing, memeActive, inlineMemes,
            _memePreview?.CurrentPhase == Infrastructure.MemePreviewDirector.Phase.PlayingMeme,
            _voiceTimeline?.FreezeOutputRanges(),
            Infrastructure.MemeMusicPreview.ActiveMemeStopsMusic(_edit.Memes, _memePreview?.ActiveMemeId)));
    }

    private void SetPlayheadFromScrub(double msFromTrimStart)
    {
        double dur = GetDuration();
        if (dur <= 0) return;

        _memeCaretSticky = false;
        _holdCaretOutSec = null;
        _playheadMs = Math.Clamp(msFromTrimStart, 0, dur * 1000.0);
        UpdateCaret();

        if (_zoomModeActive && _edit.SelectedSegmentIndex >= 0 && _edit.SelectedSegmentIndex < _edit.Segments.Count)
        {
            var activeSeg = _edit.Segments[_edit.SelectedSegmentIndex];
            double zStart = activeSeg.ZoomStartMs ?? activeSeg.StartMs;
            double zEnd = activeSeg.ZoomEndMs ?? activeSeg.EndMs;
            if (_playheadMs < zStart - 50 || _playheadMs > zEnd + 50)
            {
                if (_hasZoomBox && !_zoomBoxTouched && !activeSeg.ZoomW.HasValue)
                {
                    CommitZoomToSegment("AutoCommitScrubExit");
                }
                ExitZoomMode();
            }
        }

        if (_videoHost?.IpcClient != null) _ = SeekInternal(_playheadMs / 1000.0);
        _memePreview?.NotifySeek();
        UpdateLiveZoomCrop();
        UpdateZoomPlayheadOverlay();
    }

    private string? _thumbStripFile;
    private CancellationTokenSource? _thumbCts;
    private Avalonia.Media.Imaging.Bitmap? _thumbBitmap;
    private Avalonia.Controls.Canvas? _frameLaneHost;
    private Controls.TimelineFilmstrip? _filmstrip;
    private OutputTimeline? _frameLaneTimeline;
    private Avalonia.Size _frameLaneSize;
    private bool _editorClosing;

    private async Task BuildFrameLaneAsync()
    {
        var laneGrid = _thumbLaneGrid;
        var loading = _thumbLoadingOverlay;
        if (laneGrid == null) return;

        if (string.IsNullOrWhiteSpace(_edit.VideoPath) || !File.Exists(_edit.VideoPath)) { if (IsMergeMode) await BuildMergeFrameLaneAsync(laneGrid, loading); return; }

        double dur = GetDuration();
        if (dur <= 0) return;

        _thumbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        var token = cts.Token;

        if (loading != null) loading.IsVisible = true;
        try
        {
            string ffmpeg = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            string temp = FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory;

            void MountLane(Avalonia.Media.Imaging.Bitmap bmp)
            {
                if (_editorClosing || token.IsCancellationRequested) { bmp.Dispose(); return; }
                DeleteThumbStrip();
                _thumbBitmap = bmp;
                if (_frameLaneHost == null)
                {
                    _frameLaneHost = new Avalonia.Controls.Canvas
                    {
                        Name = "GranularFrameLaneCanvas",
                        ClipToBounds = true,
                        MinHeight = LaneHeight,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                    };
                    _frameLaneHost.SizeChanged += (_, _) => QueueRelayoutFrameLane();
                    laneGrid.Children.Clear();
                    laneGrid.Children.Add(_frameLaneHost);
                }
                if (loading != null) loading.IsVisible = false;
                RelayoutFrameLane();
            }

            Avalonia.Media.Imaging.Bitmap? warmed = FreeVideoStudio.App.Services.FilmstripPrewarm.TryTake(
                _edit.VideoPath, _edit.TrimStartMs / 1000.0, dur)
                ?? FreeVideoStudio.App.Services.WholeVideoFilmstrip.TryCompose(
                    _edit.VideoPath, _edit.TrimStartMs / 1000.0, dur, ThumbnailStripGenerator.DefaultFrames);
            if (warmed != null)
            {
                MountLane(warmed);
                RuntimeLog.Info("Granular", "Film lane served from the background prewarm.");
                return;
            }

            bool streamed = await ThumbnailStripGenerator.StreamAsync(
                ffmpeg, _edit.VideoPath, _edit.TrimStartMs / 1000.0, dur, token,
                onReady: wb => MountLane(wb),
                onFrame: () => { if (!_editorClosing) _filmstrip?.InvalidateVisual(); },
                logTag: "Granular");

            if (token.IsCancellationRequested) return;
            if (streamed) return;

            string? strip = await ThumbnailStripGenerator.GenerateAsync(
                ffmpeg, _edit.VideoPath, temp,
                _edit.TrimStartMs / 1000.0, dur, token, logTag: "Granular");

            if (token.IsCancellationRequested || strip == null) return;

            var fallbackBitmap = await Task.Run(() => new Avalonia.Media.Imaging.Bitmap(strip));
            if (_editorClosing || token.IsCancellationRequested)
            {
                fallbackBitmap.Dispose();
                try { File.Delete(strip); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                return;
            }
            MountLane(fallbackBitmap);
            _thumbStripFile = strip;
        }
        catch (OperationCanceledException swallowed2)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("Granular", $"Could not build the film-frame lane: {ex.Message}");
        }
        finally
        {
            if (loading != null) loading.IsVisible = false;
            if (ReferenceEquals(_thumbCts, cts)) _thumbCts = null;
            try { cts.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }

    private bool _frameLaneQueued;
    private bool _inFrameLaneRelayout;

    private void QueueRelayoutFrameLane()
    {
        if (_frameLaneQueued) return;
        _frameLaneQueued = true;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _frameLaneQueued = false;
            RelayoutFrameLane();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RelayoutFrameLane()
    {
        if (_draggingSegmentIndex >= 0 && _segDragMode != SegDragMode.None)
        {
            _redrawDeferredByDrag = true;
            return;
        }

        if (_inFrameLaneRelayout) return;
        _inFrameLaneRelayout = true;
        try { RelayoutFrameLaneCore(); }
        finally { _inFrameLaneRelayout = false; }
    }

    private void RelayoutFrameLaneCore()
    {
        var host = _frameLaneHost;
        var bmp = _thumbBitmap;
        if (_editorClosing || host == null || bmp == null) return;
        double w = host.Bounds.Width > 0
            ? host.Bounds.Width
            : (_segmentCanvas?.Bounds.Width > 0
                ? _segmentCanvas.Bounds.Width
                : (_thumbLaneGrid?.Bounds.Width > 0 ? _thumbLaneGrid.Bounds.Width : 0));
        double h = host.Bounds.Height > 0
            ? host.Bounds.Height
            : (_thumbLaneGrid?.Bounds.Height > 0 ? _thumbLaneGrid.Bounds.Height : LaneHeight);
        if (w <= 0 || h <= 0) return;

        if (Math.Abs(host.Width - w) > 0.5) host.Width = w;
        if (Math.Abs(host.Height - h) > 0.5) host.Height = h;

        var timeline = OutTimeline();
        var size = new Avalonia.Size(w, h);
        if (ReferenceEquals(_frameLaneTimeline, timeline) && _frameLaneSize == size && _filmstrip != null && host.Children.Contains(_filmstrip))
        {
            if (_filmstrip.Bitmap != bmp)
            {
                _filmstrip.Bitmap = bmp;
                _filmstrip.InvalidateVisual();
            }
            return;
        }

        _frameLaneTimeline = timeline;
        _frameLaneSize = size;
        host.Children.Clear();
        _filmstrip = new Controls.TimelineFilmstrip
        {
            Bitmap = bmp, Timeline = timeline, SourceDurationSeconds = GetDuration(),
            Width = w, Height = h, IsHitTestVisible = false
        };
        Avalonia.Controls.Canvas.SetLeft(_filmstrip, 0);
        Avalonia.Controls.Canvas.SetTop(_filmstrip, 0);
        host.Children.Add(_filmstrip);
        double output = 0;
        foreach (var chunk in timeline.Chunks)
        {
            double x = output / Math.Max(0.001, timeline.TotalOutputSeconds) * w;
            double width = chunk.OutputLengthSec / Math.Max(0.001, timeline.TotalOutputSeconds) * w;
            output += chunk.OutputLengthSec;
            if (chunk.IsFreeze) DecorateFrozenSpan(host, x, width, h, withLabel: true);
        }
    }

    private void DeleteThumbStrip()
    {
        try { _thumbBitmap?.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _thumbBitmap = null;
        _filmstrip = null;
        _frameLaneTimeline = null;
        _frameLaneSize = default;
        if (_frameLaneHost != null) _frameLaneHost.Children.Clear();

        if (string.IsNullOrEmpty(_thumbStripFile)) return;
        try { if (File.Exists(_thumbStripFile)) File.Delete(_thumbStripFile); }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        _thumbStripFile = null;
    }

    private void UpdateCaret()
    {
        var lanes = GranularLanesCtl;
        if (lanes == null) return;

        double outDur = OutDurationSec();
        if (Math.Abs(lanes.DurationSeconds - outDur) > 0.001) lanes.DurationSeconds = outDur;

        double outSec = _holdCaretOutSec ?? OutTimeline().SourceToOutput(_playheadMs / 1000.0);
        lanes.PositionSeconds = Math.Clamp(outSec, 0, outDur);
    }

    private double FreezeHoldStartOutSec()
    {
        double freezeRelSec = Math.Max(0, (_edit.FreezeTimeMs - _edit.TrimStartMs) / 1000.0);
        return Math.Max(0, OutTimeline().SourceToOutput(freezeRelSec) - _edit.FreezeDurationS);
    }

    private double GetCurrentTime()
    {
        if (_videoHost?.IpcClient == null) return 0;
        double absTime = _videoHost.IpcClient.CurrentTime;
        double relTime = absTime - (_edit.TrimStartMs / 1000.0);
        double trimEndSec = (_edit.TrimEndMs > 0) ? _edit.TrimEndMs / 1000.0 : double.MaxValue;
        if (relTime < 0) relTime = 0;
        if (relTime > trimEndSec - (_edit.TrimStartMs / 1000.0)) relTime = trimEndSec - (_edit.TrimStartMs / 1000.0);
        return relTime;
    }

    private readonly Services.EditorTimelineCache _outputTimelineCache = new();
    private readonly Services.EditorTimelineCache _voiceTimelineCache = new();
    private OutputTimeline? _voiceTimeline;
    private Func<double, double>? _voiceTimeMapper;

    public IReadOnlyList<MemeItem> AvailableMemes { get; set; } = System.Array.Empty<MemeItem>();

    private void OnRemoveMemeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RemoveSelectedMeme();

    private void RemoveSelectedMeme()
    {
        var removed = _edit.RemoveSelectedMeme();
        if (removed == null) { UpdateMemeButtonsState(); return; }

        RuntimeLog.Info("MEME", $"Removed '{System.IO.Path.GetFileName(removed.FilePath)}'.");
        _memeCaretSticky = false;
        InvalidateMemeTimelines();
        RedrawTimeline();
        UpdateMemeButtonsState();
        _ = RefreshMemePreviewAsync("Re-timing your video without that meme...");
        Notify("Meme removed");
    }

    private async Task<double> ResolveMemeDurationAsync(MemeItem item)
    {
        if (item.IsImage) return FreeVideoStudio.Core.Media.MemePlacement.StillImageDurationSec;

        try
        {
            string ffprobe = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve(
                "ffprobe.exe", "backend", "binaries");
            var prober = new FreeVideoStudio.Core.Media.MediaProber(ffprobe, item.FullPath);
            double probed = await prober.GetDurationAsync();
            return probed > 0.01 ? probed : 0.0;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Could not read the length of '{System.IO.Path.GetFileName(item.FullPath)}': {ex.Message}");
            return 0.0;
        }
    }

    private void InvalidateMemeTimelines()
    {
        _outputTimelineCache.Clear();
    }

    private void UpdateMemeButtonsState()
    {
        if (_removeMemeBtn != null) _removeMemeBtn.IsVisible = _edit.SelectedMemeId != null;
        UpdateSelectedMemeActions();

        if (_memeListBtn != null)
        {
            _memeListBtn.IsVisible = _edit.Memes.Count > 0;
            if (_memeListBtn.Content is TextBlock t) t.Text = $"MEMES ({_edit.Memes.Count}) ▾";
        }
    }

    private Button? _removeMemeBtn;
    private Button? _memeListBtn;

    private FreeVideoStudio.Core.Media.OutputTimeline OutTimeline()
        => _outputTimelineCache.Get(Math.Max(0.001, GetDuration()) * 1000, _edit.Segments, _edit.Cuts, _edit.Memes,
            speed: EditorBaseSpeed,
            freezeStartMs: _edit.FreezeTimeMs >= 0 ? _edit.FreezeTimeMs - _edit.TrimStartMs : -1,
            freezeDurationSeconds: _edit.FreezeDurationS);

    private double OutDurationSec() => Math.Max(0.001, OutTimeline().TotalOutputSeconds);

    private readonly Services.EditorTimelineCache _baseTimelineCache = new();

    private FreeVideoStudio.Core.Media.OutputTimeline BaseTimeline()
        => _baseTimelineCache.Get(Math.Max(0.001, GetDuration()) * 1000, _edit.Segments, _edit.Cuts, [], EditorBaseSpeed);

    private double EditorBaseSpeed => _edit.BaseSpeed > 0.001 && double.IsFinite(_edit.BaseSpeed) ? _edit.BaseSpeed : 1.0;

    private double OutXToBaseOutSec(double x, double w)
    {
        if (w <= 0) return 0;
        double outSec = Math.Clamp((x / w) * OutDurationSec(), 0, OutDurationSec());
        if (_edit.FreezeTimeMs < 0 || _edit.FreezeDurationS <= 0) return outSec;

        double holdStart = FreezeHoldStartOutSec();
        if (outSec <= holdStart) return outSec;
        return Math.Max(holdStart, outSec - Math.Min(_edit.FreezeDurationS, outSec - holdStart));
    }

    private double OutXToOutSec(double x, double w)
        => w <= 0 ? 0 : Math.Clamp((x / w) * OutDurationSec(), 0, OutDurationSec());

    private void SetFreezeStartFromBaseOutSec(double baseOutSec)
    {
        double relSec = BaseTimeline().OutputToSourceRelative(
            Math.Clamp(baseOutSec, 0, BaseTimeline().TotalOutputSeconds));
        _edit.MoveFreezeToSourceRelSec(relSec, "freeze-drag");
    }

    private double TimelineZoomFactor
        => GranularLanesCtl
               ?.ZoomFactor ?? 1.0;

    private double SrcMsToX(double srcRelMs, double w)
        => (OutTimeline().SourceToOutput(srcRelMs / 1000.0) / OutDurationSec()) * w;

    private double XToSrcMs(double x, double w)
    {
        if (w <= 0) return 0;
        if (_segDragTimeline != null && _segDragOutDurationSec > 0)
        {
            double outSecDrag = Math.Clamp(x, 0, w) / w * _segDragOutDurationSec;
            return _segDragTimeline.OutputToSourceRelative(outSecDrag) * 1000.0;
        }
        double outSec = Math.Clamp(x, 0, w) / w * OutDurationSec();
        return OutTimeline().OutputToSourceRelative(outSec) * 1000.0;
    }

    private double GetDuration()
    {
        double fullDur = (_videoHost?.IpcClient != null && _videoHost.IpcClient.Duration > 0)
            ? _videoHost.IpcClient.Duration
            : _edit.ProbedDurationSec;
        double trimEndSec = (_edit.TrimEndMs > 0) ? _edit.TrimEndMs / 1000.0 : fullDur;
        double trimStartSec = Math.Max(0, _edit.TrimStartMs / 1000.0);
        if (trimEndSec <= trimStartSec) return 0;

        return Math.Max(0.1, trimEndSec - trimStartSec);
    }

    private Avalonia.Media.Color GetSegmentOverlayColor(SpeedSegment seg)
    {
        double speed = seg.Speed;
        double baseSpd = _edit.BaseSpeed;

        if (speed < 0.01)
        {
            return Avalonia.Media.Color.FromArgb(230, 96, 165, 250);
        }
        else if (speed < baseSpd - 0.0001)
        {
            double factor = Math.Clamp((baseSpd - speed) / Math.Max(0.001, baseSpd - 0.1), 0.0, 1.0);
            byte alpha = (byte)(51 + factor * (230 - 51));
            var slow = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Avalonia.Media.Color.FromRgb(168, 50, 50));
            return Avalonia.Media.Color.FromArgb(alpha, slow.R, slow.G, slow.B);
        }
        else
        {
            double factor = Math.Clamp((speed - baseSpd) / Math.Max(0.001, 4.1 - baseSpd), 0.0, 1.0);
            byte alpha = (byte)(51 + factor * (230 - 51));
            var fast = Infrastructure.ThemeResources.Colour(this, "AppSuccessColor", Avalonia.Media.Color.FromRgb(63, 156, 107));
            return Avalonia.Media.Color.FromArgb(alpha, fast.R, fast.G, fast.B);
        }
    }

    private bool GuideWhenNothingMarked(string actionName)
    {
        if (_edit.CurrentMarkedRange() != null) return false;

        RuntimeLog.Info("GUIDE", $"{actionName} pressed with no marked range. Showing the MARK START / MARK END walkthrough.");
        NotifyError("Mark a section on the timeline first.");

        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            try
            {
                Controls.CoachOverlay.PlayOnce(this, new List<Controls.CoachStep>
                {
                    new("First, mark where it starts",
                        "Move the video to where your section should BEGIN, then press MARK START.",
                        "MarkStartBtn", Controls.CoachGesture.Click),
                    new("Now play the video",
                        "Press PLAY and let it run to where your section should END.",
                        "GranularPlayPause", Controls.CoachGesture.Click),
                    new("Watch it play",
                        "The line sweeps along the timeline as the video plays. Stop when you reach the end of the bit you want.",
                        "GranularLanes", Controls.CoachGesture.DragHorizontal),
                    new("Then mark where it ends",
                        "Press MARK END. The stretch between your two marks is now selected — and THAT is what "
                        + actionName + " works on.",
                        "MarkEndBtn", Controls.CoachGesture.Click),
                });
            }
            catch (Exception ex) { RuntimeLog.Fail("GUIDE", ex); }
        };
        delay.Start();

        return true;
    }

    private void WireMemeButtons()
    {
        var addBtn = this.FindControl<Button>("AddMemeBtn");
        if (addBtn != null) addBtn.Click += OnAddMemeClicked;

        _removeMemeBtn = this.FindControl<Button>("RemoveMemeBtn");
        if (_removeMemeBtn != null) _removeMemeBtn.Click += OnRemoveMemeClicked;

        _memeListBtn = this.FindControl<Button>("MemeListBtn");
        if (_memeListBtn != null) _memeListBtn.Click += (_, _) => OpenMemeListMenu(_memeListBtn);

        InstallSelectedMemeActions();
        UpdateMemeButtonsState();
    }

    private void WireDeletePartsButton()
    {
        var btn = this.FindControl<Button>("DeletePartsBtn");
        if (btn == null) return;
        btn.AddHandler(Button.ClickEvent, (_, _) => OnDeletePartsClicked());
    }

    private async void OnDeletePartsClicked()
    {
        try
        {
            RuntimeLog.Info("CUT", "User clicked DELETE PARTS in the Granular Speed Editor.");

            if (GuideWhenNothingMarked("DELETE PARTS")) return;

            var range = _edit.CurrentMarkedRange()!.Value;
            double durMs = GetDuration() * 1000.0;

            double startMs = Math.Max(0, Math.Min(range.startMs, durMs));
            double endMs = Math.Max(0, Math.Min(range.endMs, durMs));

            bool allowed = _edit.CanDeleteRange(startMs, endMs, durMs, out double survivingMs);

            RuntimeLog.Info("CUT",
                $"DELETE PARTS requested: {FormatMs(startMs)} -> {FormatMs(endMs)} " +
                $"({(endMs - startMs) / 1000.0:F2}s). Clip is {durMs / 1000.0:F2}s, " +
                $"{survivingMs / 1000.0:F2}s would survive across {_edit.Cuts.Count + 1} cut(s).");

            if (!allowed)
            {
                RuntimeLog.Fail("CUT", $"DELETE PARTS refused — only {survivingMs:F0}ms would be left.");
                NotifyError("That would delete almost the whole video. At least half a second has to be left.");
                return;
            }

            if (FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ConfirmMainAppCut)
            {
                bool ok = await Controls.ConfirmDialogWindow.AskAsync(
                    this,
                    $"Delete this whole scene from the video?\n\n" +
                    $"{FormatMs(startMs)} to {FormatMs(endMs)}  —  {(endMs - startMs) / 1000.0:F1} seconds.\n\n" +
                    "The timeline closes up and the video gets shorter. Your original recording is not touched, " +
                    "and CLEAR ALL puts everything back.",
                    "Delete Entire Scene?",
                    yesText: "DELETE IT",
                    noText: "KEEP IT",
                    destructive: true);
                if (!ok)
                {
                    RuntimeLog.Info("CUT", "DELETE PARTS cancelled by the user at the confirmation.");
                    return;
                }
            }

            _edit.DeleteRange(startMs, endMs, durMs);

            _outputTimelineCache.Clear();
            _baseTimelineCache.Clear();
            _gameplayTimelineCache.Clear();

            var droppedMemes = _edit.LastCutDroppedMemes;
            if (droppedMemes.Count > 0)
            {
                RuntimeLog.Info("MEME", $"DELETE PARTS removed {droppedMemes.Count} meme(s) inside the scene: {string.Join(", ", droppedMemes)}.");
                UpdateMemeButtonsState();
                _ = RefreshMemePreviewAsync("Re-timing your video...", stall: false);
            }

            double removedSec = _edit.TotalCutSeconds();
            RuntimeLog.Success("CUT",
                $"DELETE PARTS applied. {_edit.Cuts.Count} cut(s) now removing {removedSec:F2}s total. " +
                $"{_edit.Segments.Count} speed block(s) survive, freeze={(_edit.FreezeTimeMs >= 0 ? FormatMs(_edit.FreezeTimeMs - _edit.TrimStartMs) : "none")}. " +
                $"Finished video is about {OutDurationSec():F2}s. Timeline condensed and recalculated.");

            _voiceOverPlayer.Reload();

            RefreshSegmentList();
            UpdateDeleteButtonVisibility();
            RedrawTimeline();
            SetStatus($"Scene deleted — {(endMs - startMs) / 1000.0:F1}s removed. Video is now about {OutDurationSec():F1}s." +
                      (droppedMemes.Count > 0 ? $" {droppedMemes.Count} meme(s) inside it were removed too (Ctrl+Z brings them back)." : ""));
            NotifyUndoable($"Deleted {(endMs - startMs) / 1000.0:F1}s of video", "DeletePartsBtn");
        }
        catch (Exception ex) { RuntimeLog.Fail("CUT", ex); }
    }

    private void ShowFeedback(string text)
        => Controls.FloatingNotice.Show(this, text);

    private void SetStatus(string msg)
    {
        var lbl = this.FindControl<TextBlock>("BottomStatusLabel");
        if (lbl != null) lbl.Text = msg;
    }

    private void Notify(string msg)
    {
        SetStatus(msg);
        Controls.FloatingNotice.Success(this, msg);
    }

    private void NotifyError(string msg)
    {
        SetStatus(msg);
        Controls.FloatingNotice.Error(this, msg);
    }

    private const int GranularRecoveryDebounceMs = 300;

    private readonly ApplicationPaths _granularRecoveryPaths = ApplicationPaths.CreateDefault();
    private readonly RecoveryManager _granularRecovery = new();
    private DispatcherTimer? _granularRecoveryTimer;
    private Services.EditorRecoveryWriter? _granularRecoveryWriter;

    private void ScheduleGranularRecoverySave()
    {
        if (_editorClosing || IsMergeMode) return;
        if (_granularRecoveryTimer == null)
        {
            _granularRecoveryTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(GranularRecoveryDebounceMs)
            };
            _granularRecoveryTimer.Tick += (_, _) =>
            {
                _granularRecoveryTimer.Stop();
                if (_editorClosing) return;
                _granularRecoveryWriter ??= new Services.EditorRecoveryWriter(_granularRecovery.UpdateGranularSession);
                _granularRecoveryWriter.Request(GranularRecoveryCodec.Build(_edit, DateTime.UtcNow));
            };
        }
        _granularRecoveryTimer.Stop();
        _granularRecoveryTimer.Start();
    }

    private bool TryRehydrateGranularRecovery()
    {
        JsonObject? node = null;
        try
        {
            node = AtomicJsonFile.ReadObject(_granularRecoveryPaths.RecoveryStateFile)
                ?[GranularRecoveryCodec.Key]?.AsObject();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        return GranularRecoveryCodec.TryApply(node, _edit);
    }

    private Task RemoveGranularRecoverySessionAsync()
    {
        _granularRecoveryTimer?.Stop();
        _granularRecoveryWriter ??= new Services.EditorRecoveryWriter(_granularRecovery.UpdateGranularSession);
        return _granularRecoveryWriter.FinishAsync();
    }

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (_isSafeToClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_editorClosing) return;
        _editorClosing = true;
        CancelAiTracking();
        CancelAiCommentary();
        _isSeeking = false;
        _nextSeekTarget = null;
        _seekFlushTimer?.Stop();
        _uiWatchdog?.Dispose();
        _uiWatchdog = null;
        _marchingAntsTimer?.Stop();
        _playbackTimer?.Stop();
        _freezePulseTimer?.Stop();
        _zoomTutorialTimer?.Stop();
        _thumbCts?.Cancel();
        DeleteThumbStrip();
        var host = _videoHost;
        try
        {
            ClearLiveZoomCrop();
            await Task.WhenAll(RemoveGranularRecoverySessionAsync(),
                WindowBoundsHelper.SaveBoundsAsync(this, "GranularBounds"));
            var previewShutdown = host != null ? await host.ShutdownAsync() : PreviewShutdownResult.AlreadyStopped;
            if (!previewShutdown.Succeeded) RuntimeLog.Fail("Granular close", $"Video preview did not shut down cleanly: {previewShutdown.Reason}");
            _videoHost = null;
        }
        catch (Exception ex) { RuntimeLog.Fail("Granular close", ex); }
        finally
        {
            Hide();
            _isSafeToClose = true;
            Dispatcher.UIThread.Post(Close);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _freezeSound.Dispose();
        CancelAiTracking();
        CancelAiCommentary();
        ParkHistoryForReopen();

        _granularRecoveryTimer?.Stop();
        _granularRecoveryTimer = null;

        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        FreeVideoStudio.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        RuntimeLog.Info("Granular", "Granular Speed Editor closed. Disposing resources.");

        _isSeeking = false;
        _nextSeekTarget = null;
        _seekFlushTimer?.Stop();
        _seekFlushTimer = null;
        _playbackTimer?.Stop();
        _marchingAntsTimer?.Stop();
        _freezePulseTimer?.Stop();
        _zoomTutorialTimer?.Stop();
        _memePreview = null;
        TeardownEditorCornerMemes();
        _voiceOverPlayer.Dispose();
        _musicBed.Dispose();
        _previewSpeed.Dispose();
        _videoHost = null;
        base.OnClosed(e);
    }

    internal const double SpeedEditorGameBalance = 1.0;

    private string? FreezeLoudnessPath => IsMergeMode ? null : _edit.VideoPath;

    private double FreezeTailRoomSec() =>
        Math.Max(0, OutDurationSec() - (FreezeHoldStartOutSec() + _edit.FreezeDurationS));

    private void OnGlobalMasterVolumeChanged(int masterVolumePercentage)
    {
        _freezeSound.SetGameBalance(SpeedEditorGameBalance);
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.ApplyPreviewGainAsync();
        }
    }

    private long _lastDragSampleTicks;
    private void LogDragSample(int idx, double pointerMs, double newStart, double newEnd,
                               double totalMs, double lowerBound, double upperBound)
    {
        long now = Environment.TickCount64;
        if (now - _lastDragSampleTicks < 250) return;
        _lastDragSampleTicks = now;

        RuntimeLog.Debug("Granular",
            $"DRAG idx={idx} mode={_segDragMode} ptr={pointerMs:0} -> [{newStart:0}..{newEnd:0}] " +
            $"bounds=[{lowerBound:0}..{upperBound:0}] totalMs={totalMs:0} " +
            $"outDurSec={OutDurationSec():0.000} segs={_edit.Segments.Count}");
    }

    private void ResolveSeamEdge(ref int idx, ref bool isStart, double pointerMs)
    {
        if (idx < 0 || idx >= _edit.Segments.Count) return;
        double edgePos = isStart ? _edit.Segments[idx].StartMs : _edit.Segments[idx].EndMs;

        for (int j = 0; j < _edit.Segments.Count; j++)
        {
            if (j == idx) continue;

            if (isStart && pointerMs < edgePos && Math.Abs(_edit.Segments[j].EndMs - edgePos) <= SeamEpsilonMs)
            {
                idx = j; isStart = false; return; }

            if (!isStart && pointerMs > edgePos && Math.Abs(_edit.Segments[j].StartMs - edgePos) <= SeamEpsilonMs)
            {
                idx = j; isStart = true; return; }
        }
    }

    private void AttachResizeGrip()
    {
        var resizeGrip = this.FindControl<Border>("ResizeGrip");
        if (resizeGrip == null) return;

        resizeGrip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        resizeGrip.PointerPressed += (s, e) =>
        {
            if (WindowState == WindowState.Maximized) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            try
            {
                BeginResizeDrag(WindowEdge.SouthEast, e);
                e.Handled = true;
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        };
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
            titleBar.DoubleTapped += (s, e) =>
            {
                this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            };
        }
}

    private static void PaintToken(Avalonia.StyledElement element, Avalonia.AvaloniaProperty property, string key)
        => element.Bind(property, element.GetResourceObservable(key));

    internal sealed class SegmentRowBorder : Border
    {
        public event Action? Invoked;

        protected override Type StyleKeyOverride => typeof(Border);

        protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(SegmentRowBorder owner)
            : Avalonia.Automation.Peers.ControlAutomationPeer(owner), Avalonia.Automation.Provider.IInvokeProvider
        {
            protected override Avalonia.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()
                => Avalonia.Automation.Peers.AutomationControlType.ListItem;

            protected override bool IsContentElementCore() => true;

            protected override bool IsControlElementCore() => true;

            public void Invoke() => owner.Invoked?.Invoke();
        }
    }
}
