using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Threading;
using System;

namespace FreeVideoStudio.App;

public partial class MainWindow : Window
{
    private MpvVideoView? _videoHost;

    private PreviewDetachController? _previewDetach;
    private PreviewDetachController PreviewDetach => _previewDetach ??= BuildPreviewDetachController();
    private PreviewMonitorWindow? DetachedPreviewWindow => _previewDetach?.Monitor;
    public bool IsPreviewDetached => _previewDetach?.IsDetached == true;

    public MpvVideoView? ActiveVideoHost
    {
        get
        {
            return _videoHost;
        }
    }
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;

    private readonly MainViewModel _viewModel;
    private readonly ProjectRecoveryService _recoveryService;

    private double _trimStartMs { get => _viewModel.Timeline.TrimStartMs; set => _viewModel.Timeline.TrimStartMs = value; }
    private double _trimEndMs { get => _viewModel.Timeline.TrimEndMs; set => _viewModel.Timeline.TrimEndMs = value; }
    private bool _trimStartSet { get => _viewModel.Timeline.IsTrimStartSet; set => _viewModel.Timeline.IsTrimStartSet = value; }
    private bool _trimEndSet { get => _viewModel.Timeline.IsTrimEndSet; set => _viewModel.Timeline.IsTrimEndSet = value; }
    private double _thumbnailPosMs { get => _viewModel.Timeline.ThumbnailPosMs; set => _viewModel.Timeline.ThumbnailPosMs = value; }
    private bool _thumbnailSet { get => _viewModel.Timeline.IsThumbnailSet; set => _viewModel.Timeline.IsThumbnailSet = value; }
    private double _baseSpeed { get => _viewModel.Timeline.BaseSpeed; set => _viewModel.Timeline.BaseSpeed = value; }
    private double _freezeTimeMs { get => _viewModel.Timeline.FreezeTimeMs; set => _viewModel.Timeline.FreezeTimeMs = value; }
    private double _freezeDurationS { get => _viewModel.Timeline.FreezeDurationS; set => _viewModel.Timeline.FreezeDurationS = value; }
    private List<CutRange> _cuts => _viewModel.Timeline.Cuts;
    private List<SpeedSegment> _speedSegments => _viewModel.Timeline.SpeedSegments;
    private List<FreeVideoStudio.Core.Media.MemePlacement> _memePlacements => _viewModel.Timeline.MemePlacements;
    private string _hardwareMode { get => _viewModel.Export.HardwareMode; set => _viewModel.Export.HardwareMode = value; }
    private bool _isGranularSpeedActive { get => _viewModel.IsGranularSpeedActive; set => _viewModel.IsGranularSpeedActive = value; }
    private bool _isMusicActive { get => _viewModel.IsMusicActive; set => _viewModel.IsMusicActive = value; }
    private bool _exportedCleanSinceLastEdit { get => _viewModel.ExportedCleanSinceLastEdit; set => _viewModel.ExportedCleanSinceLastEdit = value; }
    private VoiceOverWindow.VoiceOverResult? _voiceOverResult { get => _viewModel.VoiceOverResult; set => _viewModel.VoiceOverResult = value; }
    private MusicWizardResult? _musicWizardResult { get => _viewModel.MusicWizardResult; set => _viewModel.MusicWizardResult = value; }

    public string OverlayText { get => _viewModel.OverlayText; set => _viewModel.OverlayText = value; }
    public bool IsPortraitMode { get => _viewModel.IsPortraitMode; set => _viewModel.IsPortraitMode = value; }
    public bool IsTeammates { get => _viewModel.IsTeammates; set => _viewModel.IsTeammates = value; }
    public bool IsSpectating { get => _viewModel.IsSpectating; set => _viewModel.IsSpectating = value; }
    public bool IsEnableFade { get => _viewModel.IsEnableFade; set => _viewModel.IsEnableFade = value; }
    public bool IsAddMeme { get => _viewModel.IsAddMeme; set => _viewModel.IsAddMeme = value; }
    public int QualitySliderValue { get => _viewModel.QualitySliderValue; set => _viewModel.QualitySliderValue = value; }
    public int MainSpeedSliderValue { get => _viewModel.MainSpeedSliderValue; set => _viewModel.MainSpeedSliderValue = value; }
    public double MainVolume { get => _viewModel.MainVolume; set => _viewModel.MainVolume = value; }

    private bool _prewarmArmed = false;

    private double _loadedVideoDurationMs = 0;

    private NAudio.Wave.WaveOut? _voiceOverPlayer;
    private FreeVideoStudio.Core.Media.WavAudioReader? _voiceOverReader;
    private readonly List<VoiceOverPreviewTake> _voiceOverPreviewTakes = new();

    private sealed class VoiceOverPreviewTake
    {
        public required VoiceOverTake Take { get; init; }
        public required NAudio.Wave.WaveOut Player { get; init; }
        public required FreeVideoStudio.Core.Media.WavAudioReader Reader { get; init; }
        public double StartProjectSec { get; set; }
        public long DriftSinceMs;
    }

    private Avalonia.Controls.Control? _thumbnailCameraControl;

    private bool _isCurrentlyFrozen = false;
    private DateTime _freezeStartTime;

    private string FormatTime(TimeSpan time, bool includeMilliseconds = false)
    {
        double dur = ActiveVideoHost?.IpcClient?.Duration ?? 0;
        bool showHours = dur >= 3600 || time.TotalHours >= 1;

        if (showHours)
            return includeMilliseconds ? time.ToString("hh\\:mm\\:ss\\.ff") : time.ToString("hh\\:mm\\:ss");
        else
            return includeMilliseconds ? time.ToString("mm\\:ss\\.ff") : time.ToString("mm\\:ss");
    }

    private void SetTrimStart(double valueMs)
    {
        _trimStartMs = valueMs;
        RebaseMemesAfterTrimChange();
        if (_musicWizardResult != null && _isMusicActive)
        {
            double dur = _musicWizardResult.TimelineEndSeconds - _musicWizardResult.TimelineStartSeconds;
            _musicWizardResult.TimelineStartSeconds = valueMs / 1000.0;
            _musicWizardResult.TimelineEndSeconds = (valueMs / 1000.0) + dur;
        }
        SchedulePrewarm();
    }

    private static string ResolveFfmpegPath() => ExportViewModel.ResolveFfmpegPath();

    private void BeginWholeVideoFilmstrip()
    {
        try { FreeVideoStudio.App.Services.WholeVideoFilmstrip.Begin(ResolveFfmpegPath(), _loadedVideoPath, _loadedVideoDurationMs / 1000.0); }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void SchedulePrewarm()
    {
        try
        {
            if (!_prewarmArmed) return;
            if (string.IsNullOrWhiteSpace(_loadedVideoPath)) return;
            if (_trimEndMs <= _trimStartMs) return;

            FreeVideoStudio.App.Services.FilmstripPrewarm.Schedule(
                ResolveFfmpegPath(),
                _loadedVideoPath,
                _trimStartMs / 1000.0,
                (_trimEndMs - _trimStartMs) / 1000.0);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private bool _draggingStartMarker = false;
    private bool _draggingEndMarker = false;
    private bool _userExplicitMarkEnd = false;
    private bool _qualitySliderInitialized = false;
    private bool _draggingMusicStart = false;
    private bool _draggingMusicEnd = false;
    private bool _draggingMusicBlock = false;
    private DispatcherTimer? _playbackTimer;
    private bool _isTimerUpdatingSlider = false;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private bool _isMusicBlockFocused = false;

    private bool _suppressNextMusicDeselect = false;
    private Avalonia.Controls.Shapes.Rectangle? _musicBlockRectRef;
    private Avalonia.Controls.Control? _musicStartPopupRef;
    private Avalonia.Controls.Control? _musicEndPopupRef;
    private Avalonia.Controls.Shapes.Rectangle? _regionRectRef;
    private DispatcherTimer? _marchingAntsTimer;
    private double _marchingAntsOffset = 0;
    private bool _isThumbnailMarkerSelected = false;
    private bool _isDraggingThumbnailMarker = false;

    private bool IsMarkerGestureActive =>
        _draggingStartMarker || _draggingEndMarker ||
        _draggingMusicStart || _draggingMusicEnd || _draggingMusicBlock ||
        _isDraggingThumbnailMarker;
    private Avalonia.Controls.Shapes.Rectangle? _thumbnailMarkerIconAntsRef;
    private Avalonia.Controls.Shapes.Rectangle? _thumbnailMarkerLineAntsRef;
    private bool? _keepMusicDuringMeme;

    private bool _isTimelineDrawn = false;
    private string _loadedVideoPath
    {
        get => _viewModel?.LoadedVideoPath ?? string.Empty;
        set => _viewModel.LoadedVideoPath = string.IsNullOrWhiteSpace(value) ? null : value;
    }
    private readonly IExportCoordinator _exportCoordinator =
        new ExportCoordinator(FreeVideoStudio.Core.Abstractions.Faults.Sink);

private readonly RecoveryManager _recovery = new RecoveryManager();

    private bool _isRestoring = false;

    private Services.ProjectSession? _projectSession;

    private KineticScrubController? _kineticScrub;

    public MainWindow()
    {
        RuntimeLog.Info("UI", "Initializing MainWindow");
        InitializeComponent();
        Title = $"Free Video Studio v{DeploymentLifecycle.GetCurrentVersion()}";

        _viewModel = new MainViewModel(_paths);
        _recoveryService = new ProjectRecoveryService(_paths);
        DataContext = _viewModel;
        InitializeSizeEstimate();

        WireComponents();

        var services = Infrastructure.AppServices.Current;
        _settingsServices = services.SettingsWindow;
        _projectSession = new Services.ProjectSession(
            services.Projects,
            services.FilePicker,
            services.Notifier,
            services.Faults,
            services.Clock,
            _viewModel,
            ProbeVideoMetricsForProject,
            HasUnsavedWork,
            Infrastructure.LiveMaskCache.ReadNow,
            Services.ToolNavigator.ReadMergeQueue,
            readLiveMaskFast: () => Infrastructure.LiveMaskCache.Current);

        Infrastructure.LiveMaskCache.RequestRefresh();

        _projectSession.StateChanged += (_, _) => RefreshProjectTitle();
        _projectSession.DocumentApplied += (_, document) => OnProjectDocumentApplied(document);
        _projectSession.ProjectVideoChanged += (_, path) => _ = ShowProjectVideoAsync(path);

        this.Opened += (s, e) => { _ = FreeVideoStudio.App.Services.UpdateService.RunStartupCheckAsync(this); };
    }

    private void OnVideoHandedOffFromAnotherLaunch(string path)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                Activate();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

                if (string.IsNullOrEmpty(path)) return;

                RuntimeLog.Info("SingleInstance", $"Loading handed-off video: {Path.GetFileName(path)}");
                await LoadVideoIntoEditorAsync(path, "opened-with-handoff");
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("SingleInstance", ex);
                await ErrorReporter.ShowAsync(this, "Could not open that video",
                    "The app was asked to open a video from Windows Explorer, but it could not be loaded.",
                    ex.ToString());
            }
        });
    }

    private List<MemeItem> _memeItems = new();
    private string? _pendingMemeRestorePath;

    private Task<double> ProbeMusicDurationSecondsAsync(string musicPath)
        => MemeManagementService.ProbeMusicDurationSecondsAsync(ResolveFfmpegPath(), musicPath);

    private int _memeScanGeneration;

    private void PopulateMemeComboBox()
    {
        _ = PopulateMemeComboBoxAsync().ContinueWith(
            t => RuntimeLog.Fail("Memes", $"Meme combo refresh failed: {t.Exception?.GetBaseException().Message}"),
            System.Threading.CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task PopulateMemeComboBoxAsync()
    {
        var cb = MemeComboBoxCtl;
        if (cb == null) return;

        int generation = Interlocked.Increment(ref _memeScanGeneration);

        var scanned = await MemeManagementService.ScanMemesAsync();

        if (Volatile.Read(ref _memeScanGeneration) != generation)
        {
            RuntimeLog.Info("Memes",
                $"Discarding superseded meme scan (generation {generation}, current {Volatile.Read(ref _memeScanGeneration)}).");
            return;
        }

        _memeItems = scanned;
        ApplyMemeItemsToCombo(preserveSelection: true);

        if (_pendingMemeRestorePath != null)
        {
            string p = _pendingMemeRestorePath;
            _pendingMemeRestorePath = null;
            var match = _memeItems.FirstOrDefault(m => string.Equals(m.FullPath, p, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(m.FileName, Path.GetFileName(p), StringComparison.OrdinalIgnoreCase));
            if (match != null) cb.SelectedItem = match;
        }
    }

    private void ApplyMemeItemsToCombo(bool preserveSelection)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyMemeItemsToCombo(preserveSelection));
            return;
        }

        var cb = MemeComboBoxCtl;
        if (cb == null) return;

        cb.ItemTemplate = MemeManagementService.CreateMemeItemTemplate(this, () => IsPortraitMode);

        var prev = preserveSelection ? cb.SelectedItem as MemeItem : null;
        var list = new List<MemeItem>(_memeItems)
        {
            new MemeItem { IsDownloadAction = true, DownloadCategory = MemeCategory.Video, FileName = "Download more meme videos..." },
            new MemeItem { IsDownloadAction = true, DownloadCategory = MemeCategory.Image, FileName = "Download more meme pictures..." },
        };
        cb.ItemsSource = list;
        if (prev != null && !prev.IsDownloadAction)
        {
            var again = list.FirstOrDefault(m => !m.IsDownloadAction &&
                string.Equals(m.FullPath, prev.FullPath, StringComparison.OrdinalIgnoreCase));
            if (again != null) cb.SelectedItem = again;
        }
    }

    private async Task RunCloudMemeSyncAsync(MemeCategory category)
    {
        string label = MemeCatalog.LabelFor(category);
        var (count, error) = await MemeManagementService.DownloadCloudMemesAsync(this, category);

        if (error != null)
        {
            ShowTacticalFeedback($"⚠ Could not finish downloading {label}");
            await ErrorReporter.ShowAsync(this, $"Problem downloading {label}", error,
                "See the log entries tagged [Memes] for the per-file detail.");
        }
        else if (count > 0)
        {
            ShowTacticalFeedback($"✓ Downloaded {count} new {label}");
        }
        else
        {
            ShowTacticalFeedback($"You already have all available {label}");
        }
    }

    private async Task PushAssetsAsync()
    {
        try
        {
            await Infrastructure.MemeDirectory.WhenReadyAsync();
            int delivered = await Task.Run(() => {
                string musicFolder = Infrastructure.MemeDirectory.GetMusicRoot();
                string videosFolder = Infrastructure.MemeDirectory.GetVideosRoot();
                string memeFolder = Infrastructure.MemeDirectory.EnsureActiveExists();
                int added = 0;

                if (!Directory.Exists(memeFolder)) Directory.CreateDirectory(memeFolder);

                string legacyMeme = Path.Combine(videosFolder, "MEME");
                if (Directory.Exists(legacyMeme) &&
                    !string.Equals(Path.GetFullPath(legacyMeme), Path.GetFullPath(memeFolder), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var f in Directory.GetFiles(legacyMeme))
                    {
                        try
                        {
                            string dest = Path.Combine(memeFolder, Path.GetFileName(f));
                            if (!File.Exists(dest)) { File.Copy(f, dest); added++; }
                        }
                        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
                    }
                }

                Infrastructure.MemeAssets.DeliverStarter(Infrastructure.MemeAssets.SongCategory, musicFolder);
                added += Infrastructure.MemeAssets.DeliverStarter(Infrastructure.MemeAssets.MemeCategoryFolder, memeFolder);
                return added;
            });

            if (delivered > 0) Dispatcher.UIThread.Post(PopulateMemeComboBox);

            if (Infrastructure.ProtectedFolderGate.TakePendingNotice())
                await Infrastructure.ProtectedFolderGate.ShowNoticeAsync(this);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("STARTUP", $"Failed to push assets: {ex.Message}");
        }
    }

    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Canvas timelineCanvas, Slider timelineSlider)
    {
        if (e.Handled) return;
        if (_draggingStartMarker || _draggingEndMarker) return;

        double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;
        double width = timelineCanvas.Bounds.Width;
        if (duration <= 0 || width <= 0) return;

        double x = Math.Clamp(e.GetPosition(timelineCanvas).X, 0, width);
        double sliderValue = (x / width) * 100.0;
        double targetTime = (sliderValue / 100.0) * duration;

        try
        {
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = sliderValue;
        }
        finally
        {
            _isTimerUpdatingSlider = false;
        }

        _ = SeekInternal(targetTime);
        ShowPlayheadBadge(targetTime, sliderValue);
        e.Handled = true;
    }

    private Avalonia.Threading.DispatcherTimer? _playheadBadgeTimer;
    private void ShowPlayheadBadge(double timeSeconds, double sliderValuePercentage)
    {
        var badge = this.FindControl<Avalonia.Controls.Border>("PlayheadBadge");
        var text = this.FindControl<Avalonia.Controls.TextBlock>("PlayheadBadgeText");
        var canvas = TimelineMarkersCanvasCtl;

        if (badge != null && text != null && canvas != null)
        {
            text.Text = FormatTime(TimeSpan.FromSeconds(timeSeconds), true);
            double canvasWidth = canvas.Bounds.Width;
            double x = (sliderValuePercentage / 100.0) * canvasWidth;
            Avalonia.Controls.Canvas.SetLeft(badge, x - 25);
            badge.Opacity = 1.0;

            if (_playheadBadgeTimer == null)
            {
                _playheadBadgeTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _playheadBadgeTimer.Tick += (s, ev) =>
                {
                    _playheadBadgeTimer.Stop();
                    badge.Opacity = 0.0;
                };
            }
            _playheadBadgeTimer.Stop();
            _playheadBadgeTimer.Start();
        }
    }

    private void UpdateTooltips()
    {
        var kb = SettingsManager.Instance.KeyBinds;

        var playPauseBtn = PlayPauseButtonCtl;
        if (playPauseBtn != null) ToolTip.SetTip(playPauseBtn, $"Play or pause the video ({kb.PlayPause})");

        var markStartBtn = MarkStartButtonCtl;
        if (markStartBtn != null) ToolTip.SetTip(markStartBtn, $"Mark the beginning of your clip ({kb.MarkStart})");

        var markEndBtn = MarkEndButtonCtl;
        if (markEndBtn != null) ToolTip.SetTip(markEndBtn, $"Mark the end of your clip ({kb.MarkEnd})");
    }

    private void OnVideoDragEnter(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(Avalonia.Input.DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            var dropzone = AmbientDropzoneCtl;
            dropzone?.Activate();
            var uploadOverlay = UploadOverlayCtl;
            if (uploadOverlay != null && uploadOverlay.IsVisible)
            {
                uploadOverlay.Opacity = 0.15;
            }
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnVideoDragLeave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dropzone = AmbientDropzoneCtl;
        dropzone?.Deactivate();
        var uploadOverlay = UploadOverlayCtl;
        if (uploadOverlay != null && uploadOverlay.IsVisible)
        {
            uploadOverlay.Opacity = 0.95;
        }
    }

    private void OnVideoDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(Avalonia.Input.DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnVideoDrop(object? sender, DragEventArgs e) => _ = OnVideoDropAsync(e);

    private async Task OnVideoDropAsync(DragEventArgs e)
    {
        try
        {
            var dropzone = AmbientDropzoneCtl;
            dropzone?.Deactivate();
            var uploadOverlay = UploadOverlayCtl;
            if (uploadOverlay != null && uploadOverlay.IsVisible)
            {
                uploadOverlay.Opacity = 0.95;
            }

            var files = e.DataTransfer.TryGetFiles();
            if (files == null)
            {
                return;
            }

            await HandleExternalFileDropAsync(files.Select(f => f.Path.LocalPath).ToArray());
        }
        catch (Exception ex)
        {
            Core.Abstractions.Faults.Degraded("UPLOAD", "The dropped file could not be opened. Try OPEN VIDEO instead; your current work is untouched.", ex);
        }
    }

    private async Task HandleExternalFileDropAsync(string[] paths)
    {
        foreach (var path in paths)
        {
            if (IsSupportedVideoPath(path))
            {
                await LoadVideoIntoEditorAsync(path, "dropped");
                return;
            }
        }

        ShowTacticalFeedback("Drop an MP4, MKV, AVI, or MOV file.");
        PlayUiSound();
    }

    private async void OnUploadVideoClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open Video (select multiple — the newest file is loaded)",
            AllowMultiple = true,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov" } } }
        };

        try
        {
            string startPath = "";
            try
            {
                string stateFile = _paths.SessionStateFile;
                if (File.Exists(stateFile))
                {
                    var state = FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.ReadObject(stateFile);
                    if (state != null && state.TryGetPropertyValue("UploadVideoDirectory", out var node) && node != null)
                    {
                        startPath = node.ToString();
                    }
                }

                if (string.IsNullOrEmpty(startPath) || !Directory.Exists(startPath))
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string hardcodedVideos = System.IO.Path.Combine(userProfile, "Videos");
                    string[] probes = new[]
                    {
                        System.IO.Path.Combine(myVideos, "Fortnite"),
                        System.IO.Path.Combine(hardcodedVideos, "Fortnite"),
                        System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(hardcodedVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                        System.IO.Path.Combine(localAppData, "Temp", "Highlights", "Fortnite"),
                        System.IO.Path.Combine(localAppData, "Temp", "Highlights"),
                        System.IO.Path.Combine(localAppData, "NVIDIA Corporation", "GeForce Experience", "Highlights"),
                        System.IO.Path.Combine(myVideos, "Highlights"),
                        System.IO.Path.Combine(myDocuments, "Highlights")
                    };

                    startPath = myVideos;
                    foreach (var probe in probes)
                    {
                        if (System.IO.Directory.Exists(probe))
                        {
                            startPath = probe;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
                {
                    try 
                    {
                        var uri = new Uri(startPath);
                        options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(uri);
                    }
                    catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
                }
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

        SetTimelinePopupsVisible(false);
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        SetTimelinePopupsVisible(true);

        if (files.Count > 0)
        {
            string selectedPath = files
                .Select(f => f.Path.LocalPath)
                .Where(File.Exists)
                .OrderByDescending(p => { try { return File.GetLastWriteTimeUtc(p); } catch (System.Exception swallowed5)
                {
                    global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed5);
                    return DateTime.MinValue;
                } })
                .FirstOrDefault() ?? files[0].Path.LocalPath;

            if (files.Count > 1)
            {
                RuntimeLog.Info("UI", $"User marked {files.Count} files; loading newest by modified time: {Path.GetFileName(selectedPath)}");
                RuntimeLog.Debug("UI", $"Full path: {selectedPath}");
            }

            try
            {
                string dir = Path.GetDirectoryName(selectedPath) ?? "";
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Text.Json.Nodes.JsonObject state;
                    state = new System.Text.Json.Nodes.JsonObject
                    {
                        ["UploadVideoDirectory"] = dir
                    };
                    FreeVideoStudio.Core.Ipc.SessionStateWriter.For(_paths).Enqueue(state);
                }
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

            await LoadVideoIntoEditorAsync(selectedPath, "uploaded");
        }
    }

    private static bool IsSupportedVideoPath(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov";
    }

    private async Task LoadVideoIntoEditorAsync(string path, string source)
    {
        if (!IsSupportedVideoPath(path) || !File.Exists(path))
        {
            ShowTacticalFeedback("Choose an MP4, MKV, AVI, or MOV file.");
            PlayUiSound();
            return;
        }

        if (!await ConfirmReplaceLoadedVideoAsync(path)) return;

        RuntimeLog.Info("UI", $"User {source} video: {Path.GetFileName(path)}");
        RuntimeLog.Debug("UI", $"Full path: {path}");
        ShowTacticalFeedback("Loading video...");

        ClearLiveZoomCrop();

        var videoHost = _videoHost;
        if (videoHost?.IpcClient == null)
        {
            ShowTacticalFeedback("Video player is still starting.");
            PlayUiSound();
            return;
        }

        _loadedVideoPath = path;
        _isTimelineDrawn = false;
        SaveUploadDirectory(path);
        ResetEditingStateForNewVideo();

        double previousDuration = videoHost.IpcClient.Duration;
        videoHost.IsVisible = true;
        await videoHost.IpcClient.LoadFileAsync(path);
        await videoHost.IpcClient.SetPropertyAsync("pause", "no");

        if (!await WaitForVideoMetadataAsync(previousDuration))
        {
            ShowTacticalFeedback("Video preview failed to load.");
            PlayUiSound();
            return;
        }

        _loadedVideoDurationMs = videoHost.IpcClient.Duration * 1000.0;
        _viewModel.Timeline.LoadedVideoDurationMs = _loadedVideoDurationMs;
        BeginWholeVideoFilmstrip();
        ApplyDefaults();
        UpdateSpeedLabel();
        UpdateEstimatedQuality();
        UpdatePortraitOverlay();

        var uploadOverlay = UploadOverlayCtl;
        if (uploadOverlay != null) uploadOverlay.IsVisible = false;

        EnableEditingControls();

        BeginProjectHistory();

        SaveRecoveryState();

        _ = RunAudioPeakCheckAsync(path);
        if (Infrastructure.SettingsManager.Instance.AiEliminationTrackerEnabled)
            _ = RunAiEliminationScanAsync(path);
    }

    private bool? _applyPeakFlattening;

    private double? _gameplayLoudnessLufs;

    private CancellationTokenSource? _peakProbeCts;

    private async Task RunAudioPeakCheckAsync(string path)
    {
        try { _peakProbeCts?.Cancel(); } catch (System.ObjectDisposedException swallowed8)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed8);
        }
        try { _peakProbeCts?.Dispose(); } catch (System.ObjectDisposedException swallowed)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
        }
        var cts = new CancellationTokenSource();
        _peakProbeCts = cts;

        _applyPeakFlattening = null;
        _gameplayLoudnessLufs = null;

        try
        {
            var settings = Infrastructure.SettingsManager.Instance;

            string ffmpeg = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            var reading = await FreeVideoStudio.Core.Media.AudioLoudnessProbe
                .MeasureAsync(ffmpeg, path, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested || reading == null) return;
            if (!string.Equals(_loadedVideoPath, path, StringComparison.OrdinalIgnoreCase)) return;

            _gameplayLoudnessLufs = reading.IntegratedLufs;
            FreeVideoStudio.Core.Media.VideoLoudnessCache.Set(path, reading.IntegratedLufs);

            if (settings.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.Ask)
            {
                _applyPeakFlattening = settings.PeakFlatteningPrompt == Infrastructure.AudioFixPrompt.AlwaysApply;
                return;
            }

            if (!reading.HasHarshPeaks) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _applyPeakFlattening = await Controls.AudioFixPromptWindow.ResolveAsync(
                    this,
                    settings.PeakFlatteningPrompt,
                    () => Controls.AudioFixPromptWindow.ForHarshPeaks(reading),
                    pref =>
                    {
                        Infrastructure.SettingsManager.Update(s => s.PeakFlatteningPrompt = pref);
                    },
                    "AudioPeaks");
            });
        }
        catch (OperationCanceledException swallowed3)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("AudioPeaks", $"Peak check skipped: {ex.Message}");
        }
    }

    private void SaveUploadDirectory(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                FreeVideoStudio.Core.Ipc.SessionStateWriter.For(_paths)
                    .Enqueue(new System.Text.Json.Nodes.JsonObject
                    {
                        ["UploadVideoDirectory"] = directory
                    });
            }
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
    }

    private void ResetProjectStateToUpload()
    {
        _loadedVideoPath = string.Empty;
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
            ActiveVideoHost.IsVisible = false;
        }

        ResetEditingStateForNewVideo();
        UpdateTimelineMarkers();

        var uploadOverlay = UploadOverlayCtl;
        if (uploadOverlay != null) { uploadOverlay.IsVisible = true; uploadOverlay.Opacity = 0.95; }

        var detachBtnReset = DetachOverlayButtonCtl;
        if (detachBtnReset != null) detachBtnReset.IsVisible = false;
        SetDetachMenuAvailable(false);
        var unlockHintReset = this.FindControl<TextBlock>("TrimUnlockHint");
        if (unlockHintReset != null) unlockHintReset.IsVisible = true;

        var timelineOverlay = TimelineOverlayCtl;
        if (timelineOverlay != null) timelineOverlay.IsVisible = false;

        var qualityPanel = this.FindControl<StackPanel>("QualityPanel");
        if (qualityPanel != null) qualityPanel.IsVisible = false;

        var speedPanel = this.FindControl<Grid>("SpeedPanel");
        if (speedPanel != null) speedPanel.IsVisible = false;

        var process = ProcessButtonCtl;
        if (process != null) process.IsEnabled = false;
        var playPause = PlayPauseButtonCtl;
        if (playPause != null) playPause.IsEnabled = false;
        var markStart = MarkStartButtonCtl;
        if (markStart != null) markStart.IsEnabled = false;
        var markEnd = MarkEndButtonCtl;
        if (markEnd != null) markEnd.IsEnabled = false;
        var thumb = SetThumbnailButtonCtl;
        if (thumb != null) thumb.IsEnabled = false;
        var addMusic = AddMusicButtonCtl;
        if (addMusic != null) addMusic.IsEnabled = false;
        var gran = GranularButtonCtl;
        if (gran != null) gran.IsEnabled = false;
        var voBtn = VoiceOverButtonCtl;
        if (voBtn != null) voBtn.IsEnabled = false;
        _cuts.Clear();

        SaveRecoveryState(label: "clear edits");
    }

    private void ClearOverlayTextForNextVideo(string reason)
    {
        if (FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.KeepOverlayTextBetweenVideos)
        {
            return;
        }

        if (string.IsNullOrEmpty(OverlayText)) return;

        RuntimeLog.Info("UI", $"Clearing the text caption ({reason}). Turn this off in Settings \u2192 Defaults \u2192 \"Leave text from Video to Video\".");
        OverlayText = string.Empty;
    }

    private void ResetEditingStateForNewVideo()
    {
        _loadedVideoDurationMs = 0;
        _viewModel.Timeline.LoadedVideoDurationMs = 0;
        ClearOverlayTextForNextVideo("a different video was loaded");

        _viewModel.Timeline.ClearPerVideoEdits();
        _viewModel.CaptionTrack = null;
        _aiEliminationTimestamps.Clear();
        _aiEliminationMarkers.Clear();
        _aiEliminationStatus = null;
        _aiEliminationStartAction = null;
        _aiEliminationScanCts?.Cancel();
        _musicWizardResult = null;
        StopMusicPreview();
        _ = StopPreviewMixPlaybackAsync();
        SetMusicButtonActive(false);
        _trimStartMs = 0;
        _trimEndMs = 0;
        _trimStartSet = false;
        _trimEndSet = false;
        _thumbnailSet = false;
        _thumbnailPosMs = 0;
        UpdateThumbnailButtonState();
        _freezeTimeMs = -1;
        _freezeDurationS = 1.0;
        ApplyVoiceOverState(null, isRestore: true);

        var addMemeCb = AddMemeCheckboxCtl;
        if (addMemeCb != null) addMemeCb.IsChecked = false;

        var markStartReset = MarkStartButtonCtl;
        if (markStartReset != null) markStartReset.Content = "MARK START";
        var markEndReset = MarkEndButtonCtl;
        if (markEndReset != null) markEndReset.Content = "MARK END";
        SetGranularButtonActive(false);
    }

    private async Task<bool> WaitForVideoMetadataAsync(double previousDuration)
    {
        if (ActiveVideoHost?.IpcClient == null) return false;

        for (int i = 0; i < 30; i++)
        {
            double duration = ActiveVideoHost.IpcClient.Duration;
            if (duration > 0 && (previousDuration <= 0 || Math.Abs(duration - previousDuration) > 0.01 || i >= 10))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return ActiveVideoHost.IpcClient.Duration > 0;
    }

    private void ApplyDefaults()
    {
        var d = SettingsManager.Instance.Defaults;
        _baseSpeed = d.StartSpeed;
        var speedSliderReset = MainSpeedSliderCtl;
        if (speedSliderReset != null) speedSliderReset.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);
        ForcePreviewSpeed();

        var qs = QualitySliderCtl;
        if (qs != null)
        {
            qs.Value = FreeVideoStudio.App.ViewModels.QualityLadder.ClampIndex(d.StartQualityIndex);
            _qualitySliderInitialized = true;
        }

        var portrait = PortraitModeCheckboxCtl;
        if (portrait != null) portrait.IsChecked = d.StartPortrait;

        var teammates = TeammatesCheckboxCtl;
        if (teammates != null) teammates.IsChecked = d.StartShowTeammates;

        var spectating = SpectatingCheckboxCtl;
        if (spectating != null) spectating.IsChecked = true;

        var enableFade = EnableFadeCheckboxCtl;
        if (enableFade != null) enableFade.IsChecked = d.StartEnableFade;

        ApplyMaskProfileToOverlayUi();
    }

    private void SetDetachMenuAvailable(bool available)
    {
        var menuTogglePreview = MenuTogglePreviewMonitorCtl;
        if (menuTogglePreview == null) return;
        menuTogglePreview.IsEnabled = available;
        ToolTip.SetTip(menuTogglePreview, available
            ? "Pop out the video player into its own separate floating window so you can move it around."
            : "Load a video first — there is nothing to pop out yet.");
    }

    private void EnableEditingControls()
    {
        var timelineOverlay = TimelineOverlayCtl;
        if (timelineOverlay != null) timelineOverlay.IsVisible = true;

        var detachBtn = DetachOverlayButtonCtl;
        if (detachBtn != null) detachBtn.IsVisible = true;
        SetDetachMenuAvailable(true);

        var unlockHint = this.FindControl<TextBlock>("TrimUnlockHint");
        if (unlockHint != null) unlockHint.IsVisible = false;

        var gb = GranularButtonCtl;
        if (gb != null) gb.IsEnabled = true;

        var thumbnail = SetThumbnailButtonCtl;
        if (thumbnail != null) thumbnail.IsEnabled = true;

        var markStart = MarkStartButtonCtl;
        if (markStart != null) markStart.IsEnabled = true;

        var playPause = PlayPauseButtonCtl;
        if (playPause != null) playPause.IsEnabled = true;

        var voiceOver = VoiceOverButtonCtl;
        if (voiceOver != null) voiceOver.IsEnabled = true;

        var markEnd = MarkEndButtonCtl;
        if (markEnd != null) markEnd.IsEnabled = true;

        var process = ProcessButtonCtl;
        if (process != null) process.IsEnabled = true;

        var addMusic = AddMusicButtonCtl;
        if (addMusic != null) addMusic.IsEnabled = true;

        var portrait = PortraitModeCheckboxCtl;
        if (portrait != null) portrait.IsEnabled = true;

        var teammates = TeammatesCheckboxCtl;
        if (teammates != null) teammates.IsEnabled = true;

        var spectatingToggle = SpectatingCheckboxCtl;
        if (spectatingToggle != null) spectatingToggle.IsEnabled = true;

        var enableFade = EnableFadeCheckboxCtl;
        if (enableFade != null) enableFade.IsEnabled = true;

        var addMemeCb = AddMemeCheckboxCtl;
        if (addMemeCb != null) addMemeCb.IsEnabled = true;

        var qualityPanel = this.FindControl<StackPanel>("QualityPanel");
        if (qualityPanel != null) qualityPanel.IsVisible = true;
        var speedPanel = this.FindControl<Grid>("SpeedPanel");
        if (speedPanel != null) speedPanel.IsVisible = true;

        var qs = QualitySliderCtl;
        if (qs != null)
        {
            qs.IsEnabled = true;
            qs.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            Avalonia.Controls.ToolTip.SetTip(qs, "Target maximum file size in MB");
        }

        var ss = MainSpeedSliderCtl;
        if (ss != null)
        {
            ss.IsEnabled = true;
            ss.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            Avalonia.Controls.ToolTip.SetTip(ss, "Playback speed multiplier");
        }

        var speedPresets = this.FindControl<Grid>("MainSpeedPresetsPanel");
        if (speedPresets != null) speedPresets.IsEnabled = true;
    }

    private void UpdatePortraitOverlay()
    {
        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)MobileCheckboxCtl ?? PortraitModeCheckboxCtl;
        var portraitTextInput = PortraitTextInputCtl;

        bool isPortrait = mobileCheckbox?.IsChecked == true;

        if (portraitTextInput != null)
            portraitTextInput.IsVisible = isPortrait;

        var previewPortraitImage = PhoneFrameCtl?.PortraitImageControl;
        if (previewPortraitImage != null && portraitTextInput != null && isPortrait)
        {
            try
            {
                string tempPngPath = System.IO.Path.Combine(_paths.TempDirectory, "preview_portrait_text.png");

                FreeVideoStudio.Core.Media.TextOverlayGenerator.GeneratePng(portraitTextInput.Text ?? "", tempPngPath);

                using (var stream = System.IO.File.OpenRead(tempPngPath))
                {
                    var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                    var oldBitmap = previewPortraitImage.Source as Avalonia.Media.Imaging.Bitmap;
                    previewPortraitImage.Source = bitmap;
                    oldBitmap?.Dispose();
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("UI", ex);
            }
        }

        ApplyPortraitModeToActiveHost();
    }

    private void ApplyPortraitModeToActiveHost()
    {
        var mobileCheckbox = (Avalonia.Controls.Primitives.ToggleButton?)MobileCheckboxCtl ?? PortraitModeCheckboxCtl;
        bool isPortrait = mobileCheckbox?.IsChecked == true;
        bool isVideoLoaded = !string.IsNullOrEmpty(_loadedVideoPath);
        RuntimeLog.Info("UI", $"ApplyPortraitModeToActiveHost: evaluated isPortrait={isPortrait}, isVideoLoaded={isVideoLoaded}");

        var mainContainer = this.FindControl<Grid>("MainContainerGrid");
        if (mainContainer != null)
        {
            mainContainer.Height = isPortrait ? 1280 : 1080;
        }

        var phoneFrame = PhoneFrameCtl;
        if (phoneFrame != null)
        {
            phoneFrame.IsVisible = isPortrait && !IsPreviewDetached && isVideoLoaded;
        }

        if (_videoHost != null)
        {
            _videoHost.RenderTransform = null;
        }

        var detached = DetachedPreviewWindow;
        if (detached != null)
        {
            detached.TogglePortraitOverlay(isPortrait && isVideoLoaded);

            var previewPortraitImage = PhoneFrameCtl?.PortraitImageControl;
            if (isPortrait && isVideoLoaded && previewPortraitImage != null)
            {
                detached.SetSkiaTextPlaceholder(previewPortraitImage.Source as Avalonia.Media.Imaging.Bitmap);
            }
        }

        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("vf", "");
        }

        UpdateEstimatedQuality();
    }

    private void PlayUiSound()
    {
    }

    private void BuildShortcutSheetRows()
    {
        var rows = this.FindControl<StackPanel>("ShortcutSheetRows");
        if (rows != null)
        {
            KeyboardShortcutService.BuildShortcutSheetRows(rows, this);
        }
    }
    private void ShowTacticalFeedback(string text)
        => Controls.FloatingNotice.Show(this, text);

    private void ShowTacticalError(string text)
        => Controls.FloatingNotice.Error(this, text);

    private void ShowTimelineGlow(double timeMs, Avalonia.Media.IBrush color)
    {
        var canvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        if (canvas == null || ActiveVideoHost?.IpcClient == null) return;

        double duration = ActiveVideoHost.IpcClient.Duration;
        if (duration <= 0) return;

        double canvasWidth = canvas.Bounds.Width;
        double targetX = (timeMs / 1000.0 / duration) * canvasWidth;

        var glow = new Avalonia.Controls.Shapes.Rectangle
        {
            Fill = color,
            Width = 2,
            Height = canvas.Bounds.Height,
            Opacity = 0.8,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(glow, targetX - 1);
        canvas.Children.Add(glow);

        Task.Run(async () =>
        {
            double w = 2;
            double op = 0.8;
            while (op > 0)
            {
                w += 2;
                op -= 0.05;
                await Task.Delay(16);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    glow.Width = w;
                    glow.Opacity = op;
                    Canvas.SetLeft(glow, targetX - (w / 2));
                });
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => canvas.Children.Remove(glow));
        });
    }

    private PreviewDetachController BuildPreviewDetachController()
    {
        var controller = new PreviewDetachController(
            this,
            PreviewDetachController.MainWindowKey,
            "Preview Monitor — Main App",
            () => _videoHost);

        controller.StateChanged += detached =>
        {
            var watermark = this.FindControl<Border>("PreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;

            controller.SyncButton(DetachOverlayButtonCtl);

            var menuTogglePreview = MenuTogglePreviewMonitorCtl;
            if (menuTogglePreview != null) menuTogglePreview.Header = detached ? "Attach Preview Monitor" : "Detach Preview Monitor";

            if (!detached && _videoHost != null) _videoHost.IsVisible = true;

            ApplyPortraitModeToActiveHost();
        };

        return controller;
    }

    public Task AttachPreviewMonitor()
    {
        PreviewDetach.Attach();
        return Task.CompletedTask;
    }

    private Task DetachPreviewMonitor()
    {
        if (string.IsNullOrEmpty(_loadedVideoPath)) return Task.CompletedTask;
        PreviewDetach.Detach();
        return Task.CompletedTask;
    }

    private double CalculateEffectiveDurationMs()
    {
        _viewModel.Timeline.LoadedVideoDurationMs = _loadedVideoDurationMs;
        return _viewModel.Timeline.CalculateEffectiveDurationMs();
    }

    private void UpdateEstimatedQuality()
    {
        RequestSizeEstimate();
    }

    private List<SpeedSegment> BuildExportSpeedSegments() => _viewModel.Timeline.BuildExportSpeedSegments();

    private double SourceMsToOutputSeconds(double sourceMs, IReadOnlyList<SpeedSegment>? segments = null)
        => _viewModel.Timeline.SourceMsToOutputSeconds(sourceMs, segments);
    private void UpdateSpeedLabel() => Infrastructure.SpeedLabel.Apply(this, _baseSpeed);

    private void ApplyMainSpeedPreset(double speed)
    {
        var speedSlider = MainSpeedSliderCtl;
        SpeedPresetButtons.SetSpinningWheelValue(speedSlider, speed);

        double previousSpeed = _baseSpeed;
        _baseSpeed = Math.Clamp(speed, 0.1, 4.0);
        ForcePreviewSpeed();

        if (Math.Abs(previousSpeed - _baseSpeed) > 0.001)
            InvalidateVoiceOverRecordingForTimingChange();
        UpdateEstimatedQuality();
        UpdateSpeedLabel();
        SaveRecoveryState(label: "change speed");
        EndProjectGesture();
    }

    private void SetTimelinePopupsVisible(bool visible)
    {
        var canvas = this.FindControl<Canvas>("TimelineMarkersCanvas");
        if (canvas != null)
        {
            foreach (var child in canvas.Children)
            {
                if (child is Avalonia.Controls.Primitives.Popup popup)
                {
                    popup.IsOpen = visible;
                }
            }
        }
        if (_musicStartPopupRef != null) _musicStartPopupRef.IsVisible = visible;
        if (_musicEndPopupRef != null) _musicEndPopupRef.IsVisible = visible;
    }

    private void NormalizeMusicPlacement(MusicWizardResult result)
    {
        result.OffsetSeconds = Math.Max(0, result.OffsetSeconds);

        double videoStartSec = _trimStartSet ? _trimStartMs / 1000.0 : 0.0;
        double videoEndSec = _trimEndMs > _trimStartMs
            ? _trimEndMs / 1000.0
            : (ActiveVideoHost?.IpcClient?.Duration ?? 0.0);

        if (videoEndSec <= videoStartSec)
            videoEndSec = videoStartSec + Math.Max(1.0, ActiveVideoHost?.IpcClient?.Duration ?? 1.0);

        if (result.TimelineEndSeconds <= result.TimelineStartSeconds)
        {
            result.TimelineStartSeconds = videoStartSec;
            result.TimelineEndSeconds = videoEndSec;
        }

        result.TimelineStartSeconds = Math.Clamp(result.TimelineStartSeconds, 0, Math.Max(0, videoEndSec - 0.5));
        result.TimelineEndSeconds = Math.Clamp(result.TimelineEndSeconds, result.TimelineStartSeconds + 0.5, videoEndSec);
    }

    private void AdjustPreviewMasterVolume(int delta) => Infrastructure.MasterVolumeUi.Nudge(delta);

    private void ApplyPreviewPlayersVolume()
    {
        double videoBase = 1.0;
        double musicBase = 1.0;

        if (_musicWizardResult != null && _isMusicActive)
        {
            videoBase = _musicWizardResult.VideoVolume;
            musicBase = _musicWizardResult.MusicVolume;
        }

        _ = ActiveVideoHost?.IpcClient?.ApplyPreviewGainAsync(videoBase * PreviewGameDuckGain());
        _freezeSound.SetGameBalance(videoBase * PreviewGameDuckGain());
        _musicBed?.ApplyGain();
        ApplyPreviewMixVolume();
    }

    private void EnsureTrimPointsSet()
    {
        double reported = ActiveVideoHost?.IpcClient?.Duration ?? 0.0;
        if (reported > 0) _loadedVideoDurationMs = reported * 1000.0;

        if (!_trimStartSet && ActiveVideoHost?.IpcClient != null)
        {
            double dur = ActiveVideoHost.IpcClient.Duration;
            if (dur > 0)
            {
                _trimStartSet = true;
                _trimEndSet = true;
                _trimStartMs = 0;
                _trimEndMs = dur * 1000.0;
                _prewarmArmed = false;
                FreeVideoStudio.App.Services.FilmstripPrewarm.Clear();
                var markStartBtn = MarkStartButtonCtl;
                if (markStartBtn != null) markStartBtn.Content = "MARK START [" + FormatTime(TimeSpan.Zero) + "]";
                var markEndBtn = MarkEndButtonCtl;
                if (markEndBtn != null) markEndBtn.Content = $"MARK END [{FormatTime(TimeSpan.FromSeconds(dur))}]";
                UpdateTimelineMarkers();
                SaveRecoveryState(label: "reset trim");
            }
        }
    }

    private bool _tickFaultLogged;

    private string _lastLiveCrop = "";

    private void UpdateLiveZoomCrop()
    {
        if (!FreeVideoStudio.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration) return;

        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) return;

        double durSec = Math.Max(0.1, ((_trimEndMs > 0 ? _trimEndMs : ipc.Duration * 1000.0) - _trimStartMs) / 1000.0);
        double tSec = Math.Max(0, (ipc.CurrentTime * 1000.0) - _trimStartMs) / 1000.0;

        bool portrait = PortraitModeCheckboxCtl?.IsChecked == true;
        var result = FreeVideoStudio.Core.Media.ZoomPreviewSimulator.Compute(
            _speedSegments, tSec, durSec, portrait, ipc.VideoWidth, ipc.VideoHeight, trimStartSec: _trimStartMs / 1000.0);

        if (!result.HasCrop) { ClearLiveZoomCrop(); return; }
        if (result.Crop == _lastLiveCrop) return;
        _lastLiveCrop = result.Crop;
        _ = ipc.SetPropertyAsync("video-crop", result.Crop);
    }

    private void ClearLiveZoomCrop()
    {
        if (_lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        try { PlaybackTimerTickCore(); }
        catch (Exception ex)
        {
            if (!_tickFaultLogged)
            {
                _tickFaultLogged = true;
                RuntimeLog.Fail("UI", $"PlaybackTimer tick fault (sync loop kept alive): {ex}");
            }
        }
    }

    private void PlaybackTimerTickCore()
    {
        var previewMemes = EffectiveMemePlacements();
        if (previewMemes.Count > 0) EnsureMemePreviewDirector();
        if (_memePreview != null)
        {
            _memePreview.SetMemes(previewMemes);
            _memePreview.Tick();
            if (_memePreview.IsActive) { _cornerMemes?.Hide(); UpdateMusicDuringMeme(); return; }
        }
        UpdateCornerMemeOverlay();

        if (_cuts.Count > 0) _ = SkipPlayheadOutOfCutAsync();

        if (ActiveVideoHost?.IpcClient == null) return;

        UpdateThumbnailButtonState();

        UpdateLiveZoomCrop();

        var canvas = TimelineMarkersCanvasCtl;
        if (canvas != null && canvas.Children.Count == 0)
        {
            UpdateTimelineMarkers();
        }

        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            bool isPaused = ActiveVideoHost.IpcClient.IsPaused;
            if (_isCurrentlyFrozen) isPaused = false;
            playIcon.IsVisible = isPaused;
            pauseIcon.IsVisible = !isPaused;
        }

        double time = ActiveVideoHost.IpcClient.CurrentTime;
        double dur = ActiveVideoHost.IpcClient.Duration;
        double displayTime = dur > 0 ? Math.Clamp(time, 0, dur) : Math.Max(0, time);

        bool videoEnded = ActiveVideoHost.IpcClient.IsEof || (dur > 0 && time >= dur - 0.05);

        UpdateMusicPreview(time, videoEnded);
        UpdateVoiceOverPreview(time, videoEnded);
        UpdatePreviewMix(time, videoEnded);

        double currentAbsMs = time * 1000.0;

        if (_freezeTimeMs >= 0 && !_isCurrentlyFrozen && !ActiveVideoHost.IpcClient.IsPaused)
        {
            bool insideFreezeWindow = currentAbsMs >= _freezeTimeMs && currentAbsMs <= _freezeTimeMs + 150;
            bool alreadyHeldThisAnchor = Math.Abs(_lastFreezeTriggerMs - _freezeTimeMs) < 0.5;

            if (insideFreezeWindow && !alreadyHeldThisAnchor)
            {
                _lastFreezeTriggerMs = _freezeTimeMs;
                _isCurrentlyFrozen = true;
                _freezeStartTime = DateTime.UtcNow;
                RuntimeLog.Info("UI",
                    $"Freeze hold started at {currentAbsMs:F0}ms (anchor {_freezeTimeMs:F0}ms, {_freezeDurationS:0.0}s).");
                TransportTrace("freeze-hold-start", "pause");
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                StartLiveFreezeSound();
                return;
            }

            if (!insideFreezeWindow &&
                (currentAbsMs < _freezeTimeMs - 250 || currentAbsMs > _freezeTimeMs + 400))
            {
                _lastFreezeTriggerMs = -1;
            }
        }
        else if (_isCurrentlyFrozen)
        {
            if ((DateTime.UtcNow - _freezeStartTime).TotalSeconds >= _freezeDurationS)
            {
                _isCurrentlyFrozen = false;
                TransportTrace("freeze-hold-release", "play");
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            else
            {
                return;
            }
        }

        FollowPreviewSpeed(ActiveVideoHost.IpcClient);
        _ = _previewSpeed.ApplyAt(ActiveVideoHost.IpcClient, currentAbsMs / 1000.0,
            !ActiveVideoHost.IpcClient.IsPaused && !ActiveVideoHost.IpcClient.IsSeeking && !_isSeeking, PreviewOutputSeconds(time));

        var timeElapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (timeElapsed != null) timeElapsed.Text = FormatTime(TimeSpan.FromSeconds(displayTime));

        var timelineSlider = TimelineSliderCtl;
        if (timelineSlider != null && dur > 0)
        {
            if (!_isTimelineDrawn)
            {
                UpdateTimelineMarkers();
                _isTimelineDrawn = true;
            }
            _isTimerUpdatingSlider = true;
            timelineSlider.Value = Math.Clamp((time / dur) * 100.0, 0.0, 100.0);
            _isTimerUpdatingSlider = false;

            var timeRemaining = this.FindControl<TextBlock>("TimeRemaining");
            if (timeRemaining != null) timeRemaining.Text = "-" + FormatTime(TimeSpan.FromSeconds(Math.Max(0, dur - displayTime)));
        }

        if (ActiveVideoHost.IpcClient.IsEof || IsMainPreviewAtEnd())
        {
            if (!_mainEndParkIssued)
            {
                _mainEndParkIssued = true;
                TransportTrace("tick-end-park", "pause");
                _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
            }
        }
        else
        {
            _mainEndParkIssued = false;
        }
    }

    private string ResolveHardwareMode() => _viewModel.Export.ResolveHardwareMode();

    private async Task InitializeHardwareScanAsync() => await _viewModel.Export.InitializeHardwareScanAsync();

    private bool CheckRdpGpuBlocked() => ExportViewModel.CheckRdpGpuBlocked();

    private void RdpFixButton_Click(object? sender, RoutedEventArgs e) => _ = RdpFixAsync();

    private async Task RdpFixAsync()
    {
        try
        {
            bool success = await _viewModel.Export.AutoFixRdpGpuPolicyAsync();
            if (success)
            {
                ShowTacticalFeedback("RDP policy updated! Please re-connect or reboot.");
                await InitializeHardwareScanAsync();
            }
            else
            {
                ShowTacticalFeedback("Failed to update RDP policy.");
            }
        }
        catch (Exception ex)
        {
            Core.Abstractions.Faults.Degraded("RDP", "The remote-desktop graphics fix did not finish. Exports still work, just more slowly.", ex);
        }
    }
    private double GetCurrentMpvTime() { return ActiveVideoHost?.IpcClient?.CurrentTime ?? 0.0; }

    private double GetSpeedForPosition(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (positionMs >= seg.StartMs && positionMs < seg.EndMs)
            {
                return seg.Speed;
            }
        }
        return _baseSpeed;
    }

    private void InitializeMpv() => _ = InitializeMpvAsync();

    private async Task InitializeMpvAsync()
    {
        try
        {
            await StartVideoHostAsync();
        }
        catch (Exception ex)
        {
            Core.Abstractions.Faults.Degraded("PLAYER", "The video preview could not start. You can still upload, edit and export; reopening the app usually brings the preview back.", ex);
        }
    }

    private void OnSeekCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue) {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                await SeekInternal(target);
            }
        });
    }

    private void ReturnToTrimStartPaused()
    {
        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) return;

        try
        {
            TransportTrace("return-to-trim-start", "pause");
            _ = ipc.SetPropertyAsync("pause", "yes");
            _ = SeekInternal(_trimStartMs / 1000.0);
            ClearLiveZoomCrop();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Could not park the preview at the trim start: {ex.Message}");
        }
    }

    private double MainTimelineEndSeconds()
    {
        double duration = ActiveVideoHost?.IpcClient?.Duration ?? 0;

        return duration;
    }

    private bool IsMainPreviewAtEnd()
    {
        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) return false;
        if (ipc.IsEof) return true;

        double end = MainTimelineEndSeconds();
        if (end <= 0) return false;
        return ipc.CurrentTime >= end - 0.05;
    }

    private void TogglePlayPauseTransport()
    {
        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) return;

        if (_isCurrentlyFrozen)
        {
            _isCurrentlyFrozen = false;
            TransportTrace("user-transport (cancel freeze)", "pause");
            _freezeSound.Stop();
            _ = ipc.SetPropertyAsync("pause", "yes");
            return;
        }

        if (!ipc.IsPaused)
        {
            TransportTrace("user-transport", "pause");
            _ = ipc.SetPropertyAsync("pause", "yes");
            return;
        }

        if (IsMainPreviewAtEnd() && !_userExplicitMarkEnd)
        {
            double restartAt = Math.Max(0, _trimStartMs / 1000.0);
            RuntimeLog.Info("UI",
                $"PLAY pressed at the end of the clip ({ipc.CurrentTime:0.###}s) — restarting from MARK START ({restartAt:0.###}s).");
            _isCurrentlyFrozen = false;
            _mainEndParkIssued = false;
            _ = SeekInternal(restartAt);
        }

        _userExplicitMarkEnd = false;
        TransportTrace("user-transport", "play");
        _ = ipc.SetPropertyAsync("pause", "no");
    }

    private bool _mainEndParkIssued;

    private double _lastFreezeTriggerMs = -1;

    private void TransportTrace(string who, string action)
    {
        try
        {
            var ipc = ActiveVideoHost?.IpcClient;
            RuntimeLog.Info("TRANSPORT",
                $"{action.ToUpperInvariant(),-5} by {who} | t={ipc?.CurrentTime ?? -1:0.###}s " +
                $"dur={ipc?.Duration ?? -1:0.###}s eof={ipc?.IsEof} pausedBefore={ipc?.IsPaused} " +
                $"frozen={_isCurrentlyFrozen} freezeAt={_freezeTimeMs:F0}ms freezeArmed={_lastFreezeTriggerMs < 0} " +
                $"endParked={_mainEndParkIssued} seeking={_isSeeking} nextSeek={_nextSeekTarget?.ToString("0.###") ?? "-"}");
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private async Task SeekInternal(double time) {
        if (_isSeeking) {
            _nextSeekTarget = time;
            return;
        }

        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) {
            _nextSeekTarget = null;
            return;
        }

        _lastFreezeTriggerMs = -1;
        _freezeSound.Stop();
        _mainEndParkIssued = false;

        _isSeeking = true;
        try {
            await ipc.SendCommandAsync("seek", time, "absolute");
        }
        catch (Exception ex) {
            _isSeeking = false;
            RuntimeLog.Fail("UI", $"Seek command failed: {ex.Message}");
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void OnSuccessAction(string action)
    {
        if (action == "whatsapp")
        {
            Services.ShellLauncher.TryOpen("whatsapp://send?text=CheckOutThisVideo", "SHARE");
        }
        else if (action == "folder")
        {
            try { Services.ShellLauncher.Launch(new ProcessStartInfo("explorer.exe", ".") { CreateNoWindow = true }); }
            catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not open the output folder: {ex.Message}"); }
        }

        ShutdownVideoPipelineForProcessExit();
        Environment.Exit(0);
    }

    private bool _isSafeToClose = false;
    private readonly PreviewSpeedGovernor _previewSpeed = new();

    private void FollowPreviewSpeed(FreeVideoStudio.Core.Media.MpvIpcClient? ipc)
    {
        _previewSpeed.IsSuspended ??= () => _memePreview?.IsActive == true;
        _previewSpeed.UseSchedule(_speedSegments, _baseSpeed);
        _previewSpeed.Follow(ipc);
    }

    private void ForcePreviewSpeed()
    {
        FollowPreviewSpeed(ActiveVideoHost?.IpcClient);
        var ipc = ActiveVideoHost?.IpcClient;
        double atMs = ipc != null && double.IsFinite(ipc.CurrentTime) ? ipc.CurrentTime * 1000.0 : _trimStartMs;
        _ = _previewSpeed.Force(ipc, GetSpeedForPosition(atMs));
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ForceReleaseDragStates(e.Pointer);
    }

    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ForceReleaseDragStates(e.Pointer);
    }

    private void ForceReleaseDragStates(Avalonia.Input.IPointer pointer)
    {
        bool needsUpdate = false;

        if (_draggingStartMarker || _draggingEndMarker)
        {
            _draggingStartMarker = false;
            _draggingEndMarker = false;
            needsUpdate = true;
        }

        if (_draggingMusicStart || _draggingMusicEnd || _draggingMusicBlock)
        {
            _draggingMusicStart = false;
            _draggingMusicEnd = false;
            _draggingMusicBlock = false;
            needsUpdate = true;
        }

        if (needsUpdate)
        {
            try { pointer?.Capture(null); } catch (System.Exception swallowed10)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed10);
            }
            UpdateTimelineMarkers();
            SaveRecoveryState(label: "drag marker");
        }
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Delete && _isMusicBlockFocused && !e.Handled)
        {
            RemoveBackgroundMusic("Delete key");
            e.Handled = true;
        }
    }

    private bool _closePromptOpen;

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        StopMusicPreview();
        _ = StopPreviewMixPlaybackAsync();
        if (_isSafeToClose)
        {
            _mainSizeWorker?.Dispose();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closePromptOpen) return;

        _closePromptOpen = true;
        try
        {
            if (!await ConfirmStopExportOnCloseAsync())
            {
                RuntimeLog.Info("UI", "Close cancelled — the user chose to let the running export finish.");
                return;
            }

            if (!await ConfirmProjectDiscardOnCloseAsync())
            {
                RuntimeLog.Info("UI", "Close cancelled — the user chose to keep unsaved project work.");
                return;
            }
        }
        finally
        {
            _closePromptOpen = false;
        }

        _mainSizeWorker?.Dispose();

        _recovery.MarkCleanShutdownIntent();
        _aiEliminationScanCts?.Cancel();

        RuntimeLog.Info("UI", "Closing MainWindow. Saving state and cleaning up asynchronously.");

        try
        {
            await _exportCoordinator.ShutdownAsync(TimeSpan.FromSeconds(3));

            if (_mainSizeWorker != null)
                await Task.WhenAny(_mainSizeWorker.Completion, Task.Delay(1000));
            if (this.IsVisible)
                await WindowBoundsHelper.SaveBoundsAsync(this, "MainWindowBounds");

            this.Hide();

            if (ActiveVideoHost?.IpcClient != null)
            {
                await ActiveVideoHost.IpcClient.SendCommandAsync("stop");
                ActiveVideoHost.IpcClient.Dispose();
            }
            ActiveVideoHost?.Dispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Error saving state during close: {ex.Message}");
        }
        finally
        {
            try { _recovery.CleanupLock(); }
            catch (Exception ex) { RuntimeLog.Fail("UI", $"Recovery cleanup failed during close: {ex.Message}"); }

            _isSafeToClose = true;
            ShutdownVideoPipelineForProcessExit();
            Environment.Exit(0);
        }
    }

    private bool _videoPipelineShutDown;

    private void ShutdownVideoPipelineForProcessExit()
    {
        if (_videoPipelineShutDown) return;
        _videoPipelineShutDown = true;

        try { _previewDetach?.Attach(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not reattach the preview during shutdown: {ex.Message}"); }

        try { _videoHost?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Video preview teardown reported: {ex.Message}"); }

        DisposeMusicPreview();
        _previewSpeed.Follow(null);
        DisposePreviewMix();
    }

    protected override void OnClosed(EventArgs e)
    {
        _freezeSound.Dispose();
        _mainSizeWorker?.Dispose();
        _viewModel.SizeEstimateRequested -= UpdateEstimatedQuality;
        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        base.OnClosed(e);
        SingleInstanceGuard.VideoPathReceived -= OnVideoHandedOffFromAnotherLaunch;
        SingleInstanceGuard.Release();
        DisposeVoiceOverPreviewTakes();
        TeardownCornerMemes();
        ShutdownVideoPipelineForProcessExit();
        Environment.Exit(0);
    }

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.DoubleTapped += (s, e) =>
            {
                this.WindowState = this.WindowState == Avalonia.Controls.WindowState.Maximized 
                    ? Avalonia.Controls.WindowState.Normal 
                    : Avalonia.Controls.WindowState.Maximized;
                e.Handled = true;
            };
            titleBar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
                }
            };
        }
    }

    private void SetGranularButtonActive(bool active)
    {
        var btn = GranularButtonCtl;
        if (btn == null) return;
        _isGranularSpeedActive = active;
        if (active)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
            btn.Content = "EDIT SPEEDS";
            ToolTip.SetTip(btn, "You have speed changes on this video. Click to reopen the Speed Editor and change them, or to remove them all.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
            btn.Content = "GRANULAR SPEED";
            ToolTip.SetTip(btn, "Adjust granular speed settings");
        }

        var mainSpeedSlider = MainSpeedSliderCtl;
        var presetsPanel = this.FindControl<Grid>("MainSpeedPresetsPanel");
        if (mainSpeedSlider != null)
        {
            mainSpeedSlider.IsEnabled = !active;
            mainSpeedSlider.Opacity = active ? 0.45 : 1.0;
            ToolTip.SetTip(mainSpeedSlider, active
                ? "Granular speed edits are active. Reopen EDIT SPEEDS to adjust speeds or remove granular segments."
                : "Slide left to make the video slow motion, or right to speed it up.");
        }
        if (presetsPanel != null)
        {
            presetsPanel.IsEnabled = !active;
            presetsPanel.Opacity = active ? 0.45 : 1.0;
            ToolTip.SetTip(presetsPanel, active
                ? "Granular speed edits are active. Reopen EDIT SPEEDS to adjust speeds or remove granular segments."
                : null);
        }

        SaveRecoveryState(label: "granular speed edits");
    }

    private void SetMusicButtonActive(bool active)
    {
        if (AddMusicButtonCtl == null) return;
        _isMusicActive = active;
        ShowMusicButtonState(active);
        SaveRecoveryState(label: "music");
    }

    private void ShowMusicButtonState(bool active)
    {
        var btn = AddMusicButtonCtl;
        if (btn == null) return;
        var txt = this.FindControl<TextBlock>("AddMusicText");
        if (active)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
            if (txt != null) txt.Text = " EDIT MUSIC ";
            ToolTip.SetTip(btn, "This video has background music. Click to reopen the wizard on your current setup and change it, or to take the music off.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
            if (txt != null) txt.Text = " ADD MUSIC ";
            ToolTip.SetTip(btn, "Add background music to the video");
        }
    }

    private static bool HasVoiceOverWav(VoiceOverWindow.VoiceOverResult? result)
    {
        foreach (var take in GetExistingVoiceOverTakes(result))
        {
            return true;
        }

        return false;
    }

    private static List<VoiceOverTake> GetExistingVoiceOverTakes(VoiceOverWindow.VoiceOverResult? result)
    {
        var takes = new List<VoiceOverTake>();
        if (result == null) return takes;

        if (result.VoiceOverTakes != null)
        {
            foreach (var take in result.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path) && System.IO.File.Exists(take.Path))
                {
                    takes.Add(take);
                }
            }
        }

        if (takes.Count == 0 &&
            !string.IsNullOrWhiteSpace(result.VoiceOverWavPath) &&
            System.IO.File.Exists(result.VoiceOverWavPath))
        {
            takes.Add(new VoiceOverTake(result.VoiceOverWavPath!, result.VoiceOverStartTimestampSec));
        }

        return takes;
    }

    private static bool HasVoiceOverEffect(VoiceOverWindow.VoiceOverResult? result)
    {
        return HasVoiceOverWav(result) ||
               result?.VoiceOverTakes?.Count > 0 || !string.IsNullOrWhiteSpace(result?.VoiceOverWavPath) ||
               result?.DuckAudio == true;
    }

    private void DisposeVoiceOverPreviewTakes()
    {
        foreach (var take in _voiceOverPreviewTakes)
            Infrastructure.PreviewAudioSync.DisposeOffUiThread(take.Player, take.Reader);
        _voiceOverPreviewTakes.Clear();
    }

    private void ApplyVoiceOverState(VoiceOverWindow.VoiceOverResult? result, bool isRestore = false)
    {
        _voiceOverResult = HasVoiceOverEffect(result) ? result : null;
        var btn = VoiceOverButtonCtl;
        var text = this.FindControl<TextBlock>("VoiceOverText");

        Infrastructure.PreviewAudioSync.DisposeOffUiThread(_voiceOverPlayer);
        _voiceOverPlayer = null;
        _voiceOverReader?.Dispose();
        _voiceOverReader = null;
        DisposeVoiceOverPreviewTakes();

        if (_voiceOverResult != null)
        {
            var takes = GetExistingVoiceOverTakes(_voiceOverResult);
            bool hasWav = takes.Count > 0;
            if (btn != null)
            {
                btn.Classes.Remove("VoiceOverEntry");
                btn.Classes.Remove("Danger");
                if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
                ToolTip.SetTip(btn, hasWav ? "Edit or remove the current voiceover" : "Record your own voice over the video");
            }
            if (text != null) text.Text = hasWav ? "EDIT VOICE OVER" : "VOICE OVER";

            if (hasWav)
            {
                foreach (var take in takes)
                {
                    try
                    {
                        var reader = new FreeVideoStudio.Core.Media.WavAudioReader(take.Path);
                        var player = Infrastructure.PreviewAudioSync.CreateVoicePlayer();
                        player.Init(reader);
                        _voiceOverPreviewTakes.Add(new VoiceOverPreviewTake
                        {
                            Take = take,
                            Reader = reader,
                            Player = player
                        });
                    }
                    catch (Exception ex)
                    {
                        CoreLogger.Fail("MainWindow", $"Failed to load VoiceOver preview take '{Path.GetFileName(take.Path)}': {ex.Message}");
                        CoreLogger.Debug("MainWindow", $"Failed to load VoiceOver preview take path '{take.Path}': {ex}");
                    }
                }
            }
        }
        else
        {
            _voiceOverResult = null;
            if (btn != null)
            {
                btn.Classes.Remove("Danger");
                btn.Classes.Remove("Primary");
                if (!btn.Classes.Contains("VoiceOverEntry")) btn.Classes.Add("VoiceOverEntry");
                ToolTip.SetTip(btn, "Open Voice Over Studio");
            }
            if (text != null) text.Text = "VOICE OVER";
        }

        ResetVoiceTimingBaseline();
        RefreshVoiceTimingReviewBadge();
        if (!isRestore) SaveRecoveryState(label: "voice over");
    }

    private void InvalidateVoiceOverRecordingForTimingChange()
    {
        RefreshVoiceTimingReviewBadge();
    }

    private Avalonia.Threading.DispatcherTimer? _recoveryDebounceTimer;

    private void ScheduleRecoveryStateSave()
    {
        if (_recoveryDebounceTimer == null)
        {
            _recoveryDebounceTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _recoveryDebounceTimer.Tick += (s, e) =>
            {
                _recoveryDebounceTimer!.Stop();
                SaveRecoveryState();
            };
        }
        _recoveryDebounceTimer.Stop();
        _recoveryDebounceTimer.Start();
    }

    private async Task<bool> ConfirmToolSwitchAsync(string toolDisplayName)
    {
        if (!FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ConfirmMainAppSwitchTool)
        {
            return true;
        }

        if (!HasUnsavedWork())
        {
            RuntimeLog.Info("UI", $"{toolDisplayName} switch: nothing to lose, no prompt shown.");
            return true;
        }

        bool go = await Controls.ConfirmDialogWindow.AskAsync(
            this,
            $"{toolDisplayName} opens in place of this window, so the editor closes while you are in it.\n\n" +
            "Your edits are saved first and will be waiting when you come back.\n\n" +
            $"Open {toolDisplayName} now?",
            "Leaving the editor",
            yesText: "OPEN " + toolDisplayName.ToUpperInvariant(),
            noText: "STAY HERE");

        if (!go) RuntimeLog.Info("UI", $"{toolDisplayName} switch cancelled by the user.");
        return go;
    }

    private async Task SwitchToCompanionAppAsync(string argument, string toolDisplayName)
    {
        if (!await ConfirmToolSwitchAsync(toolDisplayName)) return;

        ShowCompanionHandoffOverlay(toolDisplayName);
        SaveRecoveryState(sync: true, isUserEdit: false);

        Services.ToolLaunchContext context =
            argument.Equals("--crop-tool", StringComparison.OrdinalIgnoreCase)
                ? new Services.CropToolLaunchContext(_loadedVideoPath, _trimStartMs, _trimEndMs)
                : new Services.MergerLaunchContext();

        var navigator = new Services.ToolNavigator(Infrastructure.AppServices.Current.Faults);

        bool opened = await navigator.OpenAsync(
            argument.Equals("--crop-tool", StringComparison.OrdinalIgnoreCase)
                ? Services.ToolNavigator.Tool.CropTool
                : Services.ToolNavigator.Tool.VideoMerger,
            this,
            context,
            shutdownVideoPipeline: ShutdownVideoPipelineAsync,
            restoreVideoPipeline: RestoreVideoPipelineAfterTool,
            onToolReturned: OnToolNavigationResult);

        HideCompanionHandoffOverlay();

        if (!opened) RuntimeLog.Info("UI", $"{toolDisplayName} was not opened; the editor is unchanged.");
    }
    private void ShowCompanionHandoffOverlay(string toolName)
    {
        try
        {
            if (this.Content is not Panel rootPanel) return;

            var card = new StackPanel
            {
                Spacing = 16,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            card.Children.Add(new TextBlock
            {
                Text = $"Opening {toolName}…",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(20),
                FontWeight = Avalonia.Media.FontWeight.Bold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ffffff")))
            });
            card.Children.Add(new ProgressBar
            {
                IsIndeterminate = true,
                Width = 240,
                Height = 4
            });
            card.Children.Add(new TextBlock
            {
                Text = "Your edits have been saved and will be waiting when you come back.",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(12),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                MaxWidth = 420,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = Infrastructure.ThemeResources.Brush(this, "AppTextMutedBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#b6c2d0")))
            });

            _companionHandoffOverlay = new Border
            {
                Background = Infrastructure.ThemeResources.Brush(this, "AppDimmerHeavyBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C8000000"))),
                ZIndex = int.MaxValue,
                Child = card
            };
            rootPanel.Children.Add(_companionHandoffOverlay);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.WarnThrottled("UI", $"Handoff overlay could not be shown: {ex.Message}");
        }
    }

    private void HideCompanionHandoffOverlay()
    {
        try
        {
            if (_companionHandoffOverlay != null && this.Content is Panel rootPanel)
            {
                rootPanel.Children.Remove(_companionHandoffOverlay);
            }
            _companionHandoffOverlay = null;
        }
        catch (System.Exception ex) { RuntimeLog.WarnThrottled("UI", $"Handoff overlay could not be hidden: {ex.Message}"); }
    }

    private Border? _companionHandoffOverlay;

    private bool HasUnsavedWork() => _recoveryService.HasUnsavedWork(_viewModel, _viewModel.Timeline, _viewModel.Export);

    private bool BlockCropToolsForNoMaskProfile()
    {
        if (!FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(
                FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay))
        {
            return false;
        }

        RuntimeLog.Info("UI", "Crop Tools blocked: the reserved No Mask Profile is active.");
        NativeDialog.ShowError(
            "\"" + FreeVideoStudio.App.Infrastructure.MaskOverlayManager.NoMaskProfileName + "\" has no overlay elements to edit.\n\n" +
            "It converts your video to portrait with the text strip and nothing else, on purpose.\n\n" +
            "To edit overlay positions, pick a different profile first: Settings \u2192 Mask Overlay.",
            "Nothing to Edit");
        return true;
    }

    private void ApplyMaskProfileToOverlayUi()
    {
        try
        {
            bool noMask = FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(
                FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ActiveMaskOverlay);

            var panel = this.FindControl<StackPanel>("InGameOverlaysPanel");
            if (panel != null) panel.IsVisible = !noMask;

            var sep = this.FindControl<Separator>("InGameOverlaysSeparator");
            if (sep != null) sep.IsVisible = !noMask;

            if (noMask)
            {
                var team = TeammatesCheckboxCtl;
                if (team != null) team.IsChecked = false;

                var spec = SpectatingCheckboxCtl;
                if (spec != null) spec.IsChecked = false;
            }

            RuntimeLog.Info("UI", $"ApplyMaskProfileToOverlayUi: noMask={noMask}.");
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private Infrastructure.MemePreviewDirector? _memePreview;

    private void EnsureMemePreviewDirector()
    {
        if (_memePreview != null) return;

        _memePreview = new Infrastructure.MemePreviewDirector(
            () => ActiveVideoHost?.IpcClient,
            () => _loadedVideoPath,
            () => _trimStartMs / 1000.0,
            SetMemeSwapOverlay,
            "MEME",
            () => (_trimEndMs > _trimStartMs ? _trimEndMs : _loadedVideoDurationMs) / 1000.0);

        _memePreview.MemeStarted += OnMainMemeCutawayStarted;
    }

    private void SetMemeSwapOverlay(bool visible, string message)
        => Infrastructure.MemeSwapOverlay.Set(this, visible, message);

    private const double MinSurvivingMs = 500.0;

    private async Task AfterCutsChangedAsync()
    {
        NormalizeCutsInPlace();
        DropMemesInsideCuts();
        UpdateTimelineMarkers();
        UpdateEstimatedQuality();
        SaveRecoveryState(label: "edit cuts");

        if (_musicWizardResult != null && _cuts.Count > 0)
        {
            ShowTacticalFeedback("♪ Video length changed — re-run ADD MUSIC to re-align the beat drop.");
        }

        await SkipPlayheadOutOfCutAsync();
    }

    private void RebaseMemesAfterTrimChange()
    {
        var dropped = _viewModel.Timeline.RebaseMemesToTrim();
        _memePreview?.SetMemes(EffectiveMemePlacements());
        if (dropped.Count > 0)
        {
            RuntimeLog.Info("MEME", $"Trim change removed {dropped.Count} meme(s) now outside MARK START–MARK END: {string.Join(", ", dropped)}.");
            ShowTacticalFeedback(dropped.Count == 1
                ? $"Meme '{dropped[0]}' was outside the new START–END, so it was removed"
                : $"{dropped.Count} memes were outside the new START–END, so they were removed");
        }
    }

    private void DropMemesInsideCuts()
    {
        var dropped = _viewModel.Timeline.DropMemesInsideCuts();
        if (dropped.Count > 0)
        {
            ShowTacticalFeedback($"Deleted parts removed {dropped.Count} meme placement(s)");
        }
    }

    private void NormalizeCutsInPlace() => _viewModel.Timeline.NormalizeCutsInPlace();
    private double SurvivingMsFor(double a, double b) => _viewModel.Timeline.SurvivingMsFor(a, b);
    private double RemovedCutSeconds() => _viewModel.Timeline.RemovedCutSeconds();
    private async Task SkipPlayheadOutOfCutAsync()
    {
        try
        {
            if (_cuts.Count == 0) return;
            var client = ActiveVideoHost?.IpcClient;
            if (client == null) return;

            double nowMs = client.CurrentTime * 1000.0;
            foreach (var c in _cuts)
            {
                if (nowMs > c.StartMs + 1 && nowMs < c.EndMs - 1)
                {
                    double toSec = c.EndMs / 1000.0;
                    RuntimeLog.Info("CUT", $"Playhead was inside a deleted section; skipping to {toSec:F2}s.");
                    await client.SendCommandAsync("seek",
                        toSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");
                    _memePreview?.NotifySeek();
                    return;
                }
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void InitializeUxInnovations()
    {
        _kineticScrub = new KineticScrubController();
        _kineticScrub.SeekRequested += (ms) =>
        {
            if (ActiveVideoHost?.IpcClient != null)
            {
                _ = SeekInternal(ms / 1000.0);
                double duration = ActiveVideoHost.IpcClient.Duration;
                if (duration > 0)
                {
                    var slider = TimelineSliderCtl;
                    if (slider != null)
                    {
                        _isTimerUpdatingSlider = true;
                        slider.Value = (ms / 1000.0 / duration) * 100.0;
                        _isTimerUpdatingSlider = false;
                    }
                    ShowPlayheadBadge(ms / 1000.0, (ms / 1000.0 / duration) * 100.0);
                }
            }
        };

        var portraitGrid = this.FindControl<Grid>("PortraitDimmingGrid");
        if (portraitGrid != null)
        {
            Controls.LiquidMorph.AttachPortraitMorph(portraitGrid);
        }
    }

    private void TriggerParticleBurst(Point windowRelativePoint,
        Controls.ParticleBurstCanvas.BurstPreset preset)
    {
        var particleLayer = this.FindControl<Controls.ParticleBurstCanvas>("ParticleLayer");
        if (particleLayer == null) return;

        var localPoint = particleLayer.TranslatePoint(windowRelativePoint, particleLayer);
        if (localPoint.HasValue)
        {
            particleLayer.Burst(localPoint.Value, preset);
        }
        else
        {
            particleLayer.Burst(new Point(particleLayer.Bounds.Width / 2, particleLayer.Bounds.Height / 2), preset);
        }
    }

    private bool _isRadialOpen = false;

    private void OnWindowPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            var radialMenu = RadialMenuCtl;
            if (radialMenu != null)
            {
                radialMenu.Open(e.GetPosition(this));
                _isRadialOpen = true;
                e.Handled = true;
            }
        }
    }

    private void OnWindowPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_isRadialOpen)
        {
            var radialMenu = RadialMenuCtl;
            if (radialMenu != null)
            {
                var pos = e.GetPosition(radialMenu);
                radialMenu.UpdateHover(pos);
                e.Handled = true;
            }
        }
    }

    private void OnWindowPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_isRadialOpen && e.InitialPressMouseButton == Avalonia.Input.MouseButton.Right)
        {
            var radialMenu = RadialMenuCtl;
            radialMenu?.Close();
            _isRadialOpen = false;
            e.Handled = true;
        }
    }
}
