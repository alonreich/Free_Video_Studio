using Avalonia.Controls;

using Avalonia.Interactivity;

using Avalonia.Markup.Xaml;

using Avalonia.Input;

using Avalonia.Threading;

using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

using static FreeVideoStudio.App.Infrastructure.TrackSearch;
using static FreeVideoStudio.App.Infrastructure.MusicWizardDraw;
using static FreeVideoStudio.App.Infrastructure.AudioPeakMath;

namespace FreeVideoStudio.App;

public class MusicTrackItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string DurationText { get; set; } = "";
    public string SizeText { get; set; } = "";
    public double DurationSec { get; set; } = 0.0;
    public long LastModifiedTicks { get; set; } = 0;

    private bool _isRecent;
    public bool IsRecent 
    { 
        get => _isRecent; 
        set 
        { 
            if (_isRecent != value) 
            { 
                _isRecent = value; 
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsRecent))); 
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PinText))); 
            } 
        } 
    }
    public string PinText => IsRecent ? "RECENT" : "";

    private bool _isPreviewing;
    public bool IsPreviewing
    {
        get => _isPreviewing;
        set
        {
            if (_isPreviewing == value) return;
            _isPreviewing = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsPreviewing)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PreviewGlyph)));
        }
    }
    public string PreviewGlyph => IsPreviewing ? "■" : "▶";

    private bool _isInPlaylist;
    public bool IsInPlaylist
    {
        get => _isInPlaylist;
        set
        {
            if (_isInPlaylist == value) return;
            _isInPlaylist = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsInPlaylist)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AddGlyph)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AddTip)));
        }
    }
    public string AddGlyph => IsInPlaylist ? "✓" : "+";
    public string AddTip => IsInPlaylist ? "This song is in your songs. Click to take it out." : "Add this song to your songs";
    public string RowTip => "Click to listen from the middle. Double-click or press Use song to continue.";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public partial class MusicWizardResult
{
    public string MusicFilePath { get; set; } = string.Empty;
    public System.Collections.Generic.List<string> MusicFilePaths { get; set; } = new();
    public System.Collections.Generic.List<double> MusicDurationsSeconds { get; set; } = new();
    public double OffsetSeconds { get; set; } = 0.0;
    public double TimelineStartSeconds { get; set; } = 0.0;
    public double TimelineEndSeconds { get; set; } = 0.0;
    public bool EnableDucking { get; set; } = true;

    public bool EnableCarving { get; set; } = true;

    public double VideoVolume { get; set; } = 1.0;

    public double MusicVolume { get; set; } = 1.0;

    public double MusicDurationSeconds { get; set; } = 0.0;

    public bool LoopMusic { get; set; } = false;
}

public partial class MusicWizardWindow : Window

{
    public ObservableCollection<MusicTrackItem> AvailableTracks { get; } = new();

    public MusicWizardResult? Result { get; private set; }

    public MusicWizardResult? InitialState { get; set; }

    private readonly FreeVideoStudio.App.Controls.VoiceOverPreviewPlayer _voiceOverPlayer = new();

    public static readonly Avalonia.StyledProperty<string> MusicSearchTextProperty =
        Avalonia.AvaloniaProperty.Register<MusicWizardWindow, string>(nameof(MusicSearchText), string.Empty);
    public string MusicSearchText
    {
        get => GetValue(MusicSearchTextProperty);
        set => SetValue(MusicSearchTextProperty, value);
    }

    internal const double DefaultWidthDip = 1000;
    public const string ChooseBoundsKey = "MusicWizardBounds";
    public const string PartBoundsKey = "MusicWizardPartBounds";
    public const string ReviewBoundsKey = "MusicWizardReviewBounds";
    private readonly WindowBoundsHelper.BoundsTracker _bounds;

    public string CurrentBoundsKey => _bounds.Key;

    private string BoundsKeyForView() => _currentStep != StepFit ? ChooseBoundsKey : Vm.IsReviewView ? ReviewBoundsKey : PartBoundsKey;
    private int _currentStep = 1;

    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();

    private bool _isSafeToClose = false;

    private string? _lastWaveformFile;

    private double _trackDuration = 100.0;

    private MusicTrackItem? _selectedTrack;

    private FreeVideoStudio.Core.Media.MpvIpcClient? _audioIpcClient;
    private bool _isPreviewPlaying = false;
    private double _previewCurrentOffset = 0.0;
    private DateTime? _previewStartTime = null;
    private DateTime? _phase3PreviewClockStartTime = null;
    private double _phase3PreviewClockStartOffsetSec = 0.0;
    private Avalonia.Threading.DispatcherTimer? _playheadTimer;
    private Avalonia.Controls.Shapes.Line? _waveformPlayheadLine;

    private string _videoPath = "";
    private double _trimStartMs = 0;
    private double _trimEndMs = 0;
    private double _actualVideoDurationMs = 0;
    private string? _lastLoadedTrackPath;
    private string? _lastConfiguredTrackPath;
    private readonly System.Collections.Generic.List<MusicTrackItem> _allTracks = new();
    private readonly System.Collections.Generic.HashSet<string> _recentMusicPaths = new(StringComparer.OrdinalIgnoreCase);
    private string _musicSearchText = string.Empty;
    private string _musicSortMode = "Name";
    private CancellationTokenSource? _phase3LoadCts;
    private int _phase3LoadVersion;
    private bool _phase3Ready;
    private double _phase3VideoDurationSec = 60.0;
    private System.Collections.Generic.List<string> _lastPhase3ThumbFiles = new();
    private string? _lastPhase3WaveFile;
    private System.Collections.Generic.List<string>? _mergerVideos = null;
    private bool _isMergerMode = false;
    private double _phase3BaseSpeed = 1.0;
    private readonly System.Collections.Generic.List<FreeVideoStudio.Core.Media.SpeedSegment> _phase3SpeedSegments = new();
    private int _waveformRenderVersion = 0;
    private readonly System.Collections.Generic.List<double> _phase3ClipDurationsSec = new();
    private CancellationTokenSource? _audioAnalysisCts;
    private CancellationTokenSource? _musicScanCts;
    private readonly SemaphoreSlim _trackProbeGate = new(4, 4);
    private int _musicScanVersion;
    private int _phase3MusicSyncInFlight;
    private string? _phase3PreviewMusicPath;
    private double _phase3PreviewMusicSegmentStartSec = double.NaN;

    private FreeVideoStudio.App.MpvVideoView? WizardVideoHost => VideoHostBorderCtl?.Child as FreeVideoStudio.App.MpvVideoView;

    private sealed class AudioEnergyAnalysis
    {
        public double BucketSeconds { get; init; }
        public double DurationSeconds { get; init; }
        public double[] Energy { get; init; } = Array.Empty<double>();
        public System.Collections.Generic.List<double> PeakTimesSeconds { get; init; } = new();
    }

    private sealed class Phase3MusicPreviewSegment
    {
        public string Path { get; init; } = string.Empty;
        public double TimelineStartSec { get; init; }
        public double TimelineEndSec { get; init; }
        public double FileStartSec { get; init; }
    }

    public MusicWizardWindow()

    {
        InitializeComponent();
        WireViewModel();
        Controls.WindowResizeGrip.Attach(this, "Drag to resize the Add Music wizard");
        _bounds = FreeVideoStudio.App.WindowBoundsHelper.Track(this, ChooseBoundsKey, fitDisplayOnFirstRun: true, firstRunMaxWidthDip: DefaultWidthDip);
        _playheadTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playheadTimer.Tick += PlayheadTimer_Tick;
        WirePreviewDetach();

        this.Loaded += (_, _) => Controls.CoachOverlay.Register(this, Controls.CoachTours.MusicWizardKey, Controls.CoachTours.MusicWizard);
        this.Loaded += (_, _) =>
        {
            if (this.FindControl<Button>("AiAutoFitBtn") is { } fit)
                Controls.AiModeWindow.ApplyCloudOnlyLook(fit, ToolTip.GetTip(fit) as string ?? string.Empty);
        };
        WireHelpButton();
    }

    private void WireHelpButton()
    {
        var help = this.FindControl<Avalonia.Controls.Button>("WizardHelpButton");
        if (help != null) help.Click += (_, _) =>
        {
            if (PartLanesActive) ShowPartDemo();
            else Controls.CoachOverlay.Replay(this);
        };
    }

    private PreviewDetachController? _previewDetach;

    private void WirePreviewDetach()
    {
        var btn = this.FindControl<Button>("WizardDetachPreviewBtn");
        if (btn == null) return;

        _previewDetach = new PreviewDetachController(
            this,
            PreviewDetachController.MusicWizardKey,
            "Preview Monitor — Add Music Wizard",
            () => WizardVideoHost);

        _previewDetach.StateChanged += detached =>
        {
            var watermark = this.FindControl<Avalonia.Controls.Border>("WizardPreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;
            _previewDetach!.SyncButton(btn);
        };

        _previewDetach.DetachUnavailable += why => SetPhase3Status(why);

        btn.Click += (_, _) => _previewDetach.Toggle();
        _previewDetach.SyncButton(btn);
    }

    private void RefreshDetachButtonState()
        => _previewDetach?.SyncButton(this.FindControl<Button>("WizardDetachPreviewBtn"));

    private void PlayheadTimer_Tick(object? sender, EventArgs e)
    {
        OnStepsTick();
        if (_currentStep == 3 && _phase3Memes.Count > 0 && WizardVideoHost?.IpcClient != null)
            EnsureMemePreviewDirector();
        if (_memePreview != null)
        {
            _memePreview.Suspended = _currentStep != 3;
            _memePreview.SetMemes(_phase3Memes);
            _memePreview.Tick();
            if (_memePreview.IsActive)
            {
                _voiceOverPlayer.UpdatePlayback(true, false, 0, static t => t, false);
                return;
            }
        }

        if (_currentStep == StepChoose && _songPreviewReady && _isPreviewPlaying && _audioIpcClient?.IsEof == true)
        {
            StopPreview();
            _previewCurrentOffset = _rowPreviewTrack?.DurationSec ?? 0;
        }
        if (_previewFadeStartUtc.HasValue) ApplyPreviewMusicVolume();
        else ApplyCrossfadeGainIfChanged();

        if (_isPreviewPlaying)
        {
            SyncPhase3VideoPreviewClock();
            QueuePhase3MusicPreviewSync();
            EnforcePhase3PreviewEnd();
            UpdatePlayhead();
        }

        UpdatePhase3LiveZoomCrop();

        if (WizardVideoHost?.IpcClient != null)
        {
            double t = WizardVideoHost.IpcClient.CurrentTime;
            var timeMapper = Phase3VoiceTimeMapper();
            double editedTimeSec = timeMapper(t);
            bool isVoicePaused = WizardVideoHost.IpcClient.IsPaused || RenderedMixLive;
            bool ended = _trimEndMs > 0 && t >= _trimEndMs / 1000.0;
            _voiceOverPlayer.UpdatePlayback(isVoicePaused, ended, editedTimeSec, timeMapper, false);
        }
    }

    private Func<double, double>? _phase3VoiceMapper;
    private string? _phase3VoiceMapperKey;

    private Func<double, double> Phase3VoiceTimeMapper()
    {
        string key = string.Join(",", _phase3SpeedSegments) + "|" + string.Join(",", _phase3Cuts) + "|" + _phase3BaseSpeed + "|" + _trimStartMs + "|" + _trimEndMs;
        if (_phase3VoiceMapper == null || key != _phase3VoiceMapperKey)
        {
            _phase3VoiceMapper = WizardVoiceTimeMapper(_trimStartMs, _trimEndMs, _phase3SpeedSegments, _phase3BaseSpeed, _phase3Cuts);
            _phase3VoiceMapperKey = key;
        }
        return _phase3VoiceMapper;
    }

    internal static Func<double, double> WizardVoiceTimeMapper(double trimStartMs, double trimEndMs,
        System.Collections.Generic.IReadOnlyList<FreeVideoStudio.Core.Media.SpeedSegment> segmentsWithFreeze, double baseSpeed,
        System.Collections.Generic.IReadOnlyList<FreeVideoStudio.Core.Media.CutRange> absoluteCuts) =>
        FreeVideoStudio.Core.Media.PreviewTimeMap.Create(trimStartMs, trimEndMs, segmentsWithFreeze, baseSpeed, absoluteCuts).SourceToOutput;

    private string _lastLiveCrop = "";

    public bool IsPortraitPreview { get; set; }

    private void UpdatePhase3LiveZoomCrop()
    {
        if (!FreeVideoStudio.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration) return;

        var ipc = WizardVideoHost?.IpcClient;
        if (ipc == null) return;

        if (_currentStep != 3) { ClearPhase3LiveZoomCrop(); return; }
        if (_phase3SpeedSegments.Count == 0 && !IsPortraitPreview) { ClearPhase3LiveZoomCrop(); return; }

        double outputRelativeSec = GetCurrentPhase3VideoRelativeSeconds();
        double sourceRelativeSec = MapPhase3OutputToSourceRelativeSeconds(outputRelativeSec);
        double durSec = Math.Max(0.1, GetPhase3SourceDurationSeconds());

        var result = FreeVideoStudio.Core.Media.ZoomPreviewSimulator.Compute(
            _phase3SpeedSegments, sourceRelativeSec, durSec,
            IsPortraitPreview, ipc.VideoWidth, ipc.VideoHeight, trimStartSec: _trimStartMs / 1000.0);

        if (!result.HasCrop) { ClearPhase3LiveZoomCrop(); return; }
        if (result.Crop == _lastLiveCrop) return;
        _lastLiveCrop = result.Crop;
        _ = ipc.SetPropertyAsync("video-crop", result.Crop);
    }

    private void ClearPhase3LiveZoomCrop()
    {
        if (_lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = WizardVideoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }

    public MusicWizardWindow(System.Collections.Generic.List<string> mergerVideos, double totalDurationSec) : this()
    {
        _mergerVideos = mergerVideos;
        _isMergerMode = true;
        _videoPath = mergerVideos.FirstOrDefault() ?? "";
        _trimStartMs = 0;
        _trimEndMs = totalDurationSec * 1000.0;
        _playheadTimer?.Start();
        SharedInit();
        this.Loaded += async (_, _) => { await ResumeFromInitialStateAsync(); ResetWizardHistory(); };
    }

    public MusicWizardWindow(System.Collections.Generic.List<string> mergerVideos, double totalDurationSec, double baseSpeed) : this()
    {
        _mergerVideos = mergerVideos;
        _isMergerMode = true;
        _videoPath = mergerVideos.FirstOrDefault() ?? "";
        _trimStartMs = 0;
        _trimEndMs = totalDurationSec * 1000.0;
        ConfigurePhase3Timeline(baseSpeed, null);
        _playheadTimer?.Start();
        SharedInit();
        this.Loaded += async (_, _) => { await ResumeFromInitialStateAsync(); ResetWizardHistory(); };
    }

    public MusicWizardWindow(
        string videoPath,
        double trimStartMs,
        double trimEndMs,
        double baseSpeed = 1.0,
        System.Collections.Generic.IReadOnlyList<FreeVideoStudio.Core.Media.SpeedSegment>? speedSegments = null,
        VoiceOverWindow.VoiceOverResult? voiceOverResult = null,
        System.Collections.Generic.IReadOnlyList<FreeVideoStudio.Core.Media.CutRange>? cuts = null,
        System.Collections.Generic.IReadOnlyList<FreeVideoStudio.Core.Media.MemePlacement>? memes = null) : this()
    {
        _voiceOverPlayer.Result = voiceOverResult;
        _videoPath = videoPath;
        _trimStartMs = trimStartMs;
        _trimEndMs = trimEndMs;
        if (cuts != null) _phase3Cuts.AddRange(cuts);
        if (memes != null) _phase3Memes.AddRange(memes);
        ConfigurePhase3Timeline(baseSpeed, speedSegments);
        _playheadTimer?.Start();
        SharedInit();

        this.Loaded += async (_, _) => { await ResumeFromInitialStateAsync(); ResetWizardHistory(); };
    }
    private async Task ResumeFromInitialStateAsync()
    {
        var state = InitialState;
        InitialState = null;
        if (state == null || (string.IsNullOrWhiteSpace(state.MusicFilePath) && state.OrderedPaths().Count == 0))
        {
            return;
        }

        try
        {
            await RestorePlaylistFromResultAsync(state);
            if (_playlist.Count == 0)
            {
                ShowToast("The songs from last time were not found — please pick a song.");
                return;
            }

            _trackDuration = _playlist[0].FullSec > 0 ? _playlist[0].FullSec : _trackDuration;
            _previewCurrentOffset = _songStartSeconds;
            _lastConfiguredTrackPath = _playlist[0].Path;
            _lastLoadedTrackPath = null;

            var listbox = MusicListBoxCtl;
            var firstItem = TrackFor(_playlist[0].Path);
            if (listbox != null && AvailableTracks.Contains(firstItem))
            {
                _suppressTrackSelectionSideEffects = true;
                try { listbox.SelectedItem = firstItem; }
                finally { _suppressTrackSelectionSideEffects = false; }
                _highlightedTrack = firstItem;
            }

            var videoVolSlider = VideoVolSliderCtl;
            if (videoVolSlider != null) videoVolSlider.Value = Math.Clamp(state.VideoVolume * 100.0, videoVolSlider.Minimum, videoVolSlider.Maximum);
            var musicVolSlider = MusicVolSliderCtl;
            if (musicVolSlider != null) musicVolSlider.Value = Math.Clamp(state.MusicVolume * 100.0, musicVolSlider.Minimum, musicVolSlider.Maximum);
            var duckingCheck = DuckingCheckBoxCtl;
            if (duckingCheck != null) duckingCheck.IsChecked = state.EnableDucking;
            var carvingCheck = CarvingCheckBoxCtl;
            if (carvingCheck != null) carvingCheck.IsChecked = state.EnableCarving;
            ApplyMixSwitchesFromSettings();
            var loopCheck = LoopMusicCheckBoxCtl;
            if (loopCheck != null) loopCheck.IsChecked = state.LoopMusic;

            StopPreview();
            RuntimeLog.Info("MUSIC_WIZARD",
                $"Reopened on Check & finish for editing: {_playlist.Count} song(s), first '{Path.GetFileName(_playlist[0].Path)}', song start {_songStartSeconds:F2}s.");
            await EnterFitAsync(ViewModels.MusicWizardView.Review, -1);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Could not reopen the existing music placement, starting from the song list instead: {ex.Message}");
            _currentStep = StepChoose;
            UpdateStepVisibility();
            UpdateNextButtonState();
            ShowToast("Could not reopen your music setup — please pick the song again.");
        }
    }

    private readonly System.Collections.Generic.List<FreeVideoStudio.Core.Media.CutRange> _phase3Cuts = new();

    private readonly System.Collections.Generic.List<FreeVideoStudio.Core.Media.MemePlacement> _phase3Memes = new();

    private Infrastructure.MemePreviewDirector? _memePreview;

    private void EnsureMemePreviewDirector()
    {
        if (_memePreview != null) return;

        _memePreview = new Infrastructure.MemePreviewDirector(
            () => WizardVideoHost?.IpcClient,
            () => _videoPath,
            () => _trimStartMs / 1000.0,
            SetMemeSwapOverlay,
            "MUSIC_WIZARD");

        _memePreview.MemeStarted += () =>
        {
            if (_audioIpcClient != null) _ = _audioIpcClient.SetPropertyAsync("pause", "yes");
        };
        _memePreview.MemeEnded += () =>
        {
            if (_audioIpcClient != null && _isPreviewPlaying)
                _ = _audioIpcClient.SetPropertyAsync("pause", "no");
        };
    }

    private void SetMemeSwapOverlay(bool visible, string message)
    {
        var overlay = this.FindControl<Avalonia.Controls.Border>("MemeSwapOverlay");
        var text = this.FindControl<Avalonia.Controls.TextBlock>("MemeSwapOverlayText");
        if (text != null && !string.IsNullOrEmpty(message)) text.Text = message;
        if (overlay != null) overlay.IsVisible = visible;
    }

    private void ConfigurePhase3Timeline(
        double baseSpeed,
        System.Collections.Generic.IReadOnlyList<FreeVideoStudio.Core.Media.SpeedSegment>? speedSegments)
    {
        _phase3BaseSpeed = baseSpeed > 0.001 ? baseSpeed : 1.0;
        _phase3SpeedSegments.Clear();
        if (speedSegments != null)
            _phase3SpeedSegments.AddRange(speedSegments);
    }

    private void OnGlobalMasterVolumeChanged(int volume)
    {
        if (WizardVideoHost?.IpcClient != null)
            _ = WizardVideoHost.IpcClient.ApplyPreviewGainAsync(GetPreviewVideoBalance());
        if (_audioIpcClient != null)
            _ = _audioIpcClient.ApplyPreviewGainAsync(GetPreviewMusicBalance());
        _freezeSound.SetGameBalance(GetPreviewVideoBalance());
    }
    private void SharedInit()
    {
        {
            try
            {
                var state = Services.InProcessSessionState.ReadSnapshot();
                var vSlider = VideoVolSliderCtl;
                var mSlider = MusicVolSliderCtl;
                if (vSlider != null && state.TryGetPropertyValue("WizardVideoVolume", out var vvNode) && vvNode != null)
                    vSlider.Value = vvNode.GetValue<double>();
                if (mSlider != null && state.TryGetPropertyValue("WizardMusicVolume", out var mvNode) && mvNode != null)
                    mSlider.Value = mvNode.GetValue<double>();
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }

        FreeVideoStudio.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged += OnGlobalMasterVolumeChanged;

        this.Closing += (s, e) => {
            WindowBoundsHelper.SaveBoundsOnClose(this, _bounds.Key);
        };

        LoadRecentMusicPins();

        var listbox = MusicListBoxCtl;
        if (listbox != null)
        {
            listbox.ItemsSource = AvailableTracks;
            listbox.SelectionChanged += (s, e) => OnListHighlight(listbox.SelectedItem as MusicTrackItem);
            listbox.DoubleTapped += (s, e) =>
            {
                if (listbox.SelectedItem is not MusicTrackItem picked || _currentStep != StepChoose) return;
                ProceedWithSong(picked);
                e.Handled = true;
            };
            listbox.AddHandler(InputElement.KeyDownEvent, OnMusicListKeyDown, RoutingStrategies.Tunnel);
            LoadMusicDirectory();
        }

        var clearSearchBtn = this.FindControl<Button>("ClearSearchBtn");
        var searchBox = this.FindControl<TextBox>("MusicSearchBox");
        if (searchBox != null)
        {
            searchBox.TextChanged += (s, e) =>
            {
                _musicSearchText = searchBox.Text ?? string.Empty;
                if (clearSearchBtn != null)
                    clearSearchBtn.IsVisible = !string.IsNullOrEmpty(_musicSearchText);
                ApplyTrackFilterAndSort();
            };
            searchBox.KeyDown += OnMusicSearchKeyDown;
            Dispatcher.UIThread.Post(() => searchBox.Focus(), DispatcherPriority.Input);
        }

        if (clearSearchBtn != null)
        {
            clearSearchBtn.Click += (s, e) =>
            {
                if (searchBox != null)
                {
                    searchBox.Text = string.Empty;
                    clearSearchBtn.IsVisible = false;
                    searchBox.Focus();
                }
            };
        }

        var sortCombo = this.FindControl<ComboBox>("MusicSortComboBox");
        if (sortCombo != null)
        {
            sortCombo.SelectionChanged += (s, e) =>
            {
                if (sortCombo.SelectedItem is ComboBoxItem item && item.Content != null)
                {
                    _musicSortMode = item.Content.ToString() ?? "Name";
                    ApplyTrackFilterAndSort();
                }
            };
        }

        AddHandler(DragDrop.DropEvent, OnFileDrop);

        var loopCheck = LoopMusicCheckBoxCtl;
        if (loopCheck != null)
        {
            loopCheck.IsCheckedChanged += (s, e) => OnPlaylistChanged();
        }

        ApplyMixSwitchesFromSettings();

        var duckingCheck = DuckingCheckBoxCtl;
        if (duckingCheck != null)
        {
            duckingCheck.IsCheckedChanged += (s, e) =>
            {
                UpdateDuckingCompareButton();
                ApplyPreviewMusicVolume();
                UpdateProblemFlags();
                OnSoundSettingChanged();
            };
        }

        var carvingCheckWire = CarvingCheckBoxCtl;
        if (carvingCheckWire != null)
        {
            carvingCheckWire.IsCheckedChanged += (s, e) =>
            {
                ApplyPreviewMusicFilters();
                UpdateProblemFlags();
                OnSoundSettingChanged();
            };
        }

        var fitByEndBtn = this.FindControl<Button>("FitByEndBtn");
        if (fitByEndBtn != null)
            fitByEndBtn.Click += async (s, e) => await ApplyFitByEndAsync(fitByEndBtn);

        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnWizardKeyDown,
                   Avalonia.Interactivity.RoutingStrategies.Tunnel);

        WireWizardHistory();

        var smartFitBtn = this.FindControl<Button>("SmartFitBtn");
        if (smartFitBtn != null)
            smartFitBtn.Click += async (s, e) => await ApplySmartFitAsync(smartFitBtn);

        var downloadSongsBtn = this.FindControl<Button>("DownloadSongsBtn");
        if (downloadSongsBtn != null)
        {
            downloadSongsBtn.Click += async (s, e) =>
            {
                downloadSongsBtn.IsEnabled = false;
                try
                {
                    await RunSongDownloadAsync();
                }
                finally
                {
                    downloadSongsBtn.IsEnabled = true;
                }
            };
        }

        var changeFolderBtn = this.FindControl<Button>("ChangeFolderBtn");
        if (changeFolderBtn != null)
        {
            changeFolderBtn.Click += async (s, e) =>
            {
                var musicPath = ResolveCurrentMusicDirectory();
                Avalonia.Platform.Storage.IStorageFolder? musicFolder = null;
                try
                {
                    var uri = new Uri(musicPath);
                    musicFolder = await this.StorageProvider.TryGetFolderFromPathAsync(uri);
                }
                catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

                var result = await this.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Music Folder",
                    SuggestedStartLocation = musicFolder,
                    AllowMultiple = false
                });

                if (result != null && result.Count > 0)
                {
                    string selectedFolderPath = result[0].Path.LocalPath;
                    try
                    {
                        await new FreeVideoStudio.Core.Ipc.StateTransferStore(_paths)
                            .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject
                            {
                                ["CustomMusicDirectory"] = selectedFolderPath
                            });
                    }
                    catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

                    await ScanDirectoryForMusicAsync(selectedFolderPath);
                }
            };
        }

        WireSongLaneAndStrip();

        var laneHolder = this.FindControl<Panel>("Phase3LaneContentHolder");
        var lanesHost = Phase3LanesCtl;
        var thumbContent = this.FindControl<Panel>("Phase3ThumbLaneContent");
        var waveContent = this.FindControl<Panel>("Phase3WaveLaneContent");
        if (laneHolder != null && lanesHost?.LaneAHost != null && lanesHost.LaneBHost != null)
        {
            if (thumbContent != null)
            {
                laneHolder.Children.Remove(thumbContent);
                thumbContent.IsVisible = true;
                lanesHost.LaneAHost.Children.Add(thumbContent);
            }
            if (waveContent != null)
            {
                laneHolder.Children.Remove(waveContent);
                waveContent.IsVisible = true;
                lanesHost.LaneBHost.Children.Add(waveContent);
            }
            laneHolder.IsVisible = false;
        }

        var phase3Lanes = Phase3LanesCtl;
        if (phase3Lanes != null)
        {
            phase3Lanes.LaneASeekable = true;
            phase3Lanes.LaneBSeekable = false;
            phase3Lanes.SeekRequested += videoRelativeSec =>
            {
                if (Vm.IsPartView)
                {
                    SeekPartVideo(videoRelativeSec);
                    return;
                }
                bool wasPlaying = _isPreviewPlaying;
                if (wasPlaying) StopPreview();
                _previewCurrentOffset = _songStartSeconds + videoRelativeSec;
                SeekPhase3VideoHost(videoRelativeSec, forcePause: true);
                UpdatePlayhead();
            };
            phase3Lanes.SizeChanged += (_, _) => DrawPhase3TimelineScale();
        }

        var phase3WaveformClip = this.FindControl<Canvas>("Phase3WaveformClip");
        if (phase3WaveformClip != null)
        {
            phase3WaveformClip.SizeChanged += (s, e) => UpdatePhase3WaveformLaneWidth();
        }

        var videoVolSlider = VideoVolSliderCtl;
        if (videoVolSlider != null)
        {
            videoVolSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    RefreshVolumeLabels();
                    if (_currentStep == StepFit)
                    {
                        var wizardVideoHost = WizardVideoHost;
                        if (wizardVideoHost?.IpcClient != null)
                            _ = wizardVideoHost.IpcClient.ApplyPreviewGainAsync(GetPreviewVideoBalance());
                        _freezeSound.SetGameBalance(GetPreviewVideoBalance());
                        SaveWizardVolumes();
                        UpdateProblemFlags();
                        OnSoundSettingChanged();
                    }
                }
            };
        }

        var musicVolSlider = MusicVolSliderCtl;
        if (musicVolSlider != null)
        {
            musicVolSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    RefreshVolumeLabels();
                    if (_audioIpcClient != null)
                    {
                        ApplyPreviewMusicVolume();
                        SaveWizardVolumes();
                        UpdateProblemFlags();
                    }
                    OnSoundSettingChanged();
                }
            };
        }

        RefreshVolumeLabels();
        if (videoVolSlider != null) videoVolSlider.AttachedToVisualTree += (_, _) => RefreshVolumeLabels();

        var playBtn = PlayBtnCtl;
        if (playBtn != null)
        {
            playBtn.Click += (s, e) => TogglePreview();
        }

        var skipBackBtn = this.FindControl<Button>("SkipBackBtn");
        if (skipBackBtn != null) skipBackBtn.Click += (s, e) => SkipPreview(-20);

        var skipForwardBtn = this.FindControl<Button>("SkipForwardBtn");
        if (skipForwardBtn != null) skipForwardBtn.Click += (s, e) => SkipPreview(20);

        var nextBtn = NextBtnCtl;
        if (nextBtn != null) nextBtn.Click += (s, e) =>
        {
            RuntimeLog.Info("UI", "User clicked Next in Music Wizard.");
            OnNextClicked(s, e);
        };

        var backBtn = this.FindControl<Button>("BackBtn");
        _backBtn = backBtn;
        if (backBtn != null) backBtn.Click += async (s, e) =>
        {
            RuntimeLog.Info("UI", "User clicked Back in Music Wizard.");
            await OnBackClickedAsync(s, e);
        };

        RefreshPlaylistViews();
        UpdateCoverageBar();
        UpdateStepVisibility();
        UpdateNextButtonState();
        UpdatePreviewControlsState();
        UpdateDuckingCompareButton();
        UpdateProblemFlags();
        AttachTitleBarDrag();
    }

    private void InitializeComponent()

    {
        AvaloniaXamlLoader.Load(this);
    }
    private void UpdateStepProgress()
    {
        var dots = new[]
        {
            (this.FindControl<Avalonia.Controls.Border>("Step1Dot"), this.FindControl<TextBlock>("Step1Icon"), this.FindControl<TextBlock>("Step1Label")),
            (this.FindControl<Avalonia.Controls.Border>("Step2Dot"), this.FindControl<TextBlock>("Step2Icon"), this.FindControl<TextBlock>("Step2Label")),
            (this.FindControl<Avalonia.Controls.Border>("Step3Dot"), this.FindControl<TextBlock>("Step3Icon"), this.FindControl<TextBlock>("Step3Label")),
        };
        int current = _currentStep != StepFit ? 0 : Vm.IsReviewView ? 2 : 1;
        StepDotBrushes brushes = new(this);
        for (int i = 0; i < dots.Length; i++)
        {
            var (dot, icon, label) = dots[i];
            if (dot == null || icon == null || label == null) continue;
            if (i < current)
            {
                dot.Background = brushes.Done;
                icon.Text = "✓";
                icon.Foreground = brushes.OnAccent;
                label.Foreground = brushes.Muted;
                label.FontWeight = Avalonia.Media.FontWeight.Normal;
            }
            else if (i == current)
            {
                dot.Background = brushes.Active;
                icon.Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                icon.Foreground = brushes.OnAccent;
                label.Foreground = brushes.Active;
                label.FontWeight = Avalonia.Media.FontWeight.Bold;
            }
            else
            {
                dot.Background = brushes.Pending;
                icon.Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                icon.Foreground = brushes.Muted;
                label.Foreground = brushes.Muted;
                label.FontWeight = Avalonia.Media.FontWeight.Normal;
            }
        }
    }

    private readonly struct StepDotBrushes
    {
        public readonly Avalonia.Media.IBrush Done, Active, Pending, Muted, OnAccent;
        public StepDotBrushes(Avalonia.Controls.Control owner)
        {
            Done = Infrastructure.ThemeResources.Brush(owner, "AppSuccessBrush", Avalonia.Media.Brush.Parse("#3f9c6b"));
            Active = Infrastructure.ThemeResources.Brush(owner, "AppAccentBrush", Avalonia.Media.Brush.Parse("#3b82f6"));
            Pending = Infrastructure.ThemeResources.Brush(owner, "AppPanelBrush", Avalonia.Media.Brush.Parse("#334155"));
            Muted = Infrastructure.ThemeResources.Brush(owner, "AppTextMutedBrush", Avalonia.Media.Brush.Parse("#94a3b8"));
            OnAccent = Infrastructure.ThemeResources.Brush(owner, "AppOnAccentTextBrush", Avalonia.Media.Brushes.White);
        }
    }

    private bool _suppressTrackSelectionSideEffects;

    private Panel? _transportFooter;

    private void PlaceTransportBar()
    {
        var bar = this.FindControl<StackPanel>("TransportBar");
        var partHost = this.FindControl<Panel>("PartTransportHost");
        if (bar == null || partHost == null) return;
        if (_transportFooter == null && bar.Parent is Panel footer && !ReferenceEquals(footer, partHost)) _transportFooter = footer;
        var target = _currentStep == StepFit && Vm.IsPartView ? partHost : _transportFooter;
        if (target == null || ReferenceEquals(bar.Parent, target)) return;
        if (bar.Parent is Panel current) current.Children.Remove(bar);
        target.Children.Add(bar);
    }

    private void UpdateStepVisibility()
    {
        this.FindControl<Grid>("Step1Panel")!.IsVisible = _currentStep == StepChoose;
        this.FindControl<Grid>("Step3Panel")!.IsVisible = _currentStep == StepFit;
        Vm.TransportVisible = true;
        Vm.ChoosingSong = _currentStep == StepChoose;

        if (_currentStep == StepChoose) Vm.View = ViewModels.MusicWizardView.Choose;
        PlaceTransportBar();
        _bounds.SwitchTo(BoundsKeyForView());
        ApplyStepButtons();

        UpdateFinalPlacementSummary();
        UpdateProblemFlags();
        UpdateDuckingCompareButton();
        UpdateStepProgress();
        UpdatePreviewControlsState();
        RefreshPlaylistViews();
        RefreshSongCard();
        EnsureSongStripWaveform();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (this.Content is Avalonia.Controls.Control contentControl)
            {
                contentControl.InvalidateMeasure();
                contentControl.InvalidateArrange();
            }
            if (_currentStep == StepFit)
            {
                DrawSongLane();
                DrawSongStrip();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnMusicSearchKeyDown(object? sender, KeyEventArgs e)
    {
        var listbox = MusicListBoxCtl;
        if (listbox == null) return;

        if (e.Key == Key.Escape)
        {
            if (sender is TextBox searchBox)
                searchBox.Text = string.Empty;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (AvailableTracks.Count == 0) return;
            int currentIndex = listbox.SelectedIndex;
            int nextIndex = e.Key == Key.Down
                ? Math.Min(AvailableTracks.Count - 1, currentIndex + 1)
                : Math.Max(0, currentIndex <= 0 ? 0 : currentIndex - 1);
            listbox.SelectedIndex = nextIndex;
            if (listbox.SelectedItem != null)
                listbox.ScrollIntoView(listbox.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (listbox.SelectedItem == null && AvailableTracks.Count > 0)
                listbox.SelectedIndex = 0;
            if (listbox.SelectedItem != null && _currentStep == 1)
                OnNextClicked(listbox, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private string _quickJumpBuffer = string.Empty;
    private DateTime _lastQuickJumpTime = DateTime.MinValue;
    private static readonly TimeSpan QuickJumpTimeout = TimeSpan.FromMilliseconds(1000);

    private void OnMusicListKeyDown(object? sender, KeyEventArgs e)
    {
        if (AvailableTracks.Count == 0) return;
        var listbox = MusicListBoxCtl;
        if (listbox == null) return;

        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Enter or Key.Escape or Key.Tab)
        {
            _quickJumpBuffer = string.Empty;
            return;
        }

        if (e.Key == Key.Back)
        {
            if (_quickJumpBuffer.Length > 0)
            {
                _quickJumpBuffer = _quickJumpBuffer.Substring(0, _quickJumpBuffer.Length - 1);
                _lastQuickJumpTime = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(_quickJumpBuffer))
                {
                    ExecuteQuickJumpMatch(listbox, _quickJumpBuffer, isRepeatChar: false);
                }
            }
            e.Handled = true;
            return;
        }

        char? inputChar = null;
        if (!string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length == 1 && !char.IsControl(e.KeySymbol[0]))
        {
            inputChar = e.KeySymbol[0];
        }
        else if (e.Key >= Key.A && e.Key <= Key.Z)
        {
            inputChar = (char)('a' + (e.Key - Key.A));
        }
        else if (e.Key >= Key.D0 && e.Key <= Key.D9)
        {
            inputChar = (char)('0' + (e.Key - Key.D0));
        }
        else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
        {
            inputChar = (char)('0' + (e.Key - Key.NumPad0));
        }

        if (inputChar.HasValue)
        {
            var now = DateTime.UtcNow;
            if (now - _lastQuickJumpTime > QuickJumpTimeout)
            {
                _quickJumpBuffer = string.Empty;
            }
            _lastQuickJumpTime = now;
            _quickJumpBuffer += inputChar.Value;

            bool isAllSameChar = _quickJumpBuffer.Length > 0 &&
                _quickJumpBuffer.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(_quickJumpBuffer[0]));

            ExecuteQuickJumpMatch(listbox, _quickJumpBuffer, isRepeatChar: isAllSameChar);
            e.Handled = true;
        }
    }

    private void ExecuteQuickJumpMatch(ListBox listbox, string query, bool isRepeatChar)
    {
        if (string.IsNullOrEmpty(query) || AvailableTracks.Count == 0) return;

        int currentIndex = listbox.SelectedIndex;
        if (currentIndex < 0) currentIndex = 0;

        MusicTrackItem? match = null;

        if (isRepeatChar)
        {
            char firstChar = query[0];
            for (int i = 1; i <= AvailableTracks.Count; i++)
            {
                int idx = (currentIndex + i) % AvailableTracks.Count;
                var track = AvailableTracks[idx];
                string name = track.Title ?? track.Name ?? string.Empty;
                if (name.StartsWith(firstChar.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    match = track;
                    break;
                }
            }
            if (match == null)
            {
                for (int i = 1; i <= AvailableTracks.Count; i++)
                {
                    int idx = (currentIndex + i) % AvailableTracks.Count;
                    var track = AvailableTracks[idx];
                    string name = track.Title ?? track.Name ?? string.Empty;
                    if (name.IndexOf(firstChar.ToString(), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = track;
                        break;
                    }
                }
            }
        }
        else
        {
            match = AvailableTracks.FirstOrDefault(t =>
            {
                string name = t.Title ?? t.Name ?? string.Empty;
                return name.StartsWith(query, StringComparison.OrdinalIgnoreCase);
            });

            if (match == null)
            {
                match = AvailableTracks.FirstOrDefault(t =>
                {
                    string name = t.Title ?? t.Name ?? string.Empty;
                    return name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }
        }

        if (match != null)
        {
            listbox.SelectedItem = match;
            listbox.ScrollIntoView(match);
        }
    }
    private void ApplyTrackFilterAndSort()
    {
        string selectedPath = _highlightedTrack?.FilePath ?? string.Empty;
        var visible = SortTracks(_allTracks.Where(t => TrackMatchesSearch(t, _musicSearchText))).ToList();

        _suppressTrackSelectionSideEffects = true;
        try
        {
            AvailableTracks.Clear();
            foreach (var track in visible)
                AvailableTracks.Add(track);

            var listbox = MusicListBoxCtl;
            if (listbox != null)
            {
                var selectedVisibleTrack = visible.FirstOrDefault(t =>
                    string.Equals(t.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
                listbox.SelectedItem = selectedVisibleTrack;
                _highlightedTrack = selectedVisibleTrack;
            }
        }
        finally { _suppressTrackSelectionSideEffects = false; }

        RefreshAddButtons();
        UpdateMusicEmptyState();
        UpdateMusicResultCount();
    }

    private System.Collections.Generic.IEnumerable<MusicTrackItem> SortTracks(System.Collections.Generic.IEnumerable<MusicTrackItem> tracks)
    {
        return _musicSortMode switch
        {
            "Newest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenByDescending(t => t.LastModifiedTicks)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "Shortest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenBy(t => t.DurationSec <= 0 ? double.MaxValue : t.DurationSec)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "Longest" => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenByDescending(t => t.DurationSec)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            _ => tracks
                .OrderByDescending(t => t.IsRecent)
                .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void UpdateMusicResultCount()
    {
        var countText = this.FindControl<TextBlock>("MusicResultCountText");
        if (countText == null) return;

        if (_allTracks.Count == 0)
        {
            countText.Text = "0 songs";
        }
        else if (AvailableTracks.Count == _allTracks.Count)
        {
            countText.Text = $"{_allTracks.Count} songs";
        }
        else
        {
            countText.Text = $"{AvailableTracks.Count} of {_allTracks.Count} songs";
        }
    }
    private System.Collections.Generic.List<MusicTrackItem> GetAutoFillSourceTracks()
    {
        return AvailableTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(t.FilePath))
            .ToList();
    }

    private void SetSmartFitStatus(string message, bool isWarning = false)
    {
        var status = this.FindControl<TextBlock>("SmartFitStatusText");
        if (status == null) return;

        status.Text = message;
        status.Foreground = isWarning
            ? Avalonia.Media.Brushes.Orange
            : Avalonia.Media.Brushes.LightGreen;
    }

    private void CancelAudioAnalysis()
    {
        try { _audioAnalysisCts?.Cancel(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        try { _audioAnalysisCts?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        _audioAnalysisCts = null;
    }

    private void CancelMusicScan()
    {
        Interlocked.Increment(ref _musicScanVersion);
        try { _musicScanCts?.Cancel(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        try { _musicScanCts?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        _musicScanCts = null;
    }

    private async Task<AudioEnergyAnalysis?> AnalyzeAudioEnergyAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return null;

        try
        {
            var result = await FreeVideoStudio.Core.Media.MusicEnergyAnalysis.AnalyzeFileAsync(
                ResolveFfmpegPath(), audioPath, cancellationToken);
            if (result == null) return null;

            return new AudioEnergyAnalysis
            {
                BucketSeconds = result.BucketSeconds,
                DurationSeconds = result.DurationSeconds,
                Energy = result.Energy,
                PeakTimesSeconds = result.PeakTimesSeconds
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Audio energy analysis failed: {ex.Message}");
            return null;
        }
    }
    private static double? FindNearestPeakTime(AudioEnergyAnalysis analysis, double targetSeconds, double radiusSeconds)
    {
        double? best = null;
        double bestDistance = double.MaxValue;

        foreach (double peak in analysis.PeakTimesSeconds)
        {
            double distance = Math.Abs(peak - targetSeconds);
            if (distance <= radiusSeconds && distance < bestDistance)
            {
                best = peak;
                bestDistance = distance;
            }
        }

        return best;
    }
    private double FindSmartFitStart(AudioEnergyAnalysis analysis, double usableWindowSeconds, double trackDuration)
    {
        if (trackDuration <= 0) trackDuration = analysis.DurationSeconds;
        if (trackDuration <= 0 || analysis.Energy.Length == 0)
            return 0.0;

        usableWindowSeconds = Math.Min(usableWindowSeconds, trackDuration);
        double maxStartSeconds = Math.Max(0.0, trackDuration - usableWindowSeconds);
        if (maxStartSeconds <= 0.01)
            return 0.0;

        double bucketSeconds = analysis.BucketSeconds;
        int windowBuckets = Math.Max(1, Math.Min(analysis.Energy.Length, (int)Math.Round(usableWindowSeconds / bucketSeconds)));
        int maxStartBucket = Math.Min(analysis.Energy.Length - 1, (int)Math.Floor(maxStartSeconds / bucketSeconds));
        int stepBuckets = Math.Max(1, (int)Math.Round(0.10 / bucketSeconds));
        int earlyBuckets = Math.Max(1, (int)Math.Round(Math.Min(12.0, Math.Max(1.0, usableWindowSeconds * 0.25)) / bucketSeconds));

        var prefix = new double[analysis.Energy.Length + 1];
        for (int i = 0; i < analysis.Energy.Length; i++)
            prefix[i + 1] = prefix[i] + analysis.Energy[i];

        int bestBucket = 0;
        double bestScore = double.NegativeInfinity;
        for (int start = 0; start <= maxStartBucket; start += stepBuckets)
        {
            int end = Math.Min(analysis.Energy.Length, start + windowBuckets);
            if (end <= start) continue;
            int earlyEnd = Math.Min(end, start + earlyBuckets);
            double fullAverage = (prefix[end] - prefix[start]) / (end - start);
            double earlyAverage = (prefix[earlyEnd] - prefix[start]) / Math.Max(1, earlyEnd - start);
            double score = fullAverage * 0.65 + earlyAverage * 0.35;
            if (start < (int)Math.Round(1.0 / bucketSeconds) && earlyAverage < 0.04)
                score -= 0.05;
            if (score > bestScore)
            {
                bestScore = score;
                bestBucket = start;
            }
        }

        double startSeconds = bestBucket * bucketSeconds;
        double? snapped = FindNearestPeakTime(analysis, startSeconds, 1.0);
        return Math.Clamp(snapped ?? startSeconds, 0.0, maxStartSeconds);
    }

    private double HeardLengthOf(int index)
    {
        var seg = FirstSegmentOf(index);
        if (seg is MusicBedSegment s) return s.DurationSec + s.LeadInSec;
        return index >= 0 && index < _playlist.Count ? _playlist[index].WindowSec : 0;
    }

    private void ApplySongStartSeconds(double startSeconds, string statusMessage)
    {
        if (_playlist.Count == 0) return;
        int i = Math.Clamp(_selectedSongIndex, 0, _playlist.Count - 1);
        SetSongIn(i, startSeconds);
        OnPlaylistChanged();
        SetSmartFitStatus(statusMessage);
        RecordWizardEdit();
    }

    private async Task ApplySmartFitAsync(Button button)
    {
        var entry = SelectedEntry;
        int smartFitIndex = _selectedSongIndex;
        if (entry == null || !File.Exists(entry.Path))
        {
            ShowToast("Pick a song first.");
            return;
        }

        CancelAudioAnalysis();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _audioAnalysisCts = cts;
        button.IsEnabled = false;
        button.Opacity = 0.5;
        SetSmartFitStatus("Finding the strongest part...");

        try
        {
            var analysis = await GetTrackEnergyAsync(entry.Path, cts.Token);
            int i = OccurrenceIndex(smartFitIndex, entry.Path);
            if (analysis == null || i < 0)
            {
                SetSmartFitStatus("Could not read this song.", isWarning: true);
                return;
            }
            var now = _playlist[i];
            double full = now.FullSec > 0 ? now.FullSec : analysis.DurationSeconds;
            bool endsTheVideo = i == _playlist.Count - 1;
            double heard = Math.Max(MusicPlaylistMath.MinWindowSec, HeardLengthOf(i));
            if (heard >= full - 0.5)
            {
                SetSmartFitStatus("This whole song already plays — there is no better part to jump to.");
                return;
            }
            double start = FindSmartFitStart(analysis, heard, full);
            _playlist[i] = endsTheVideo
                ? MusicPlaylistMath.WithIn(now, start)
                : MusicPlaylistMath.Slide(now, start - now.InSec);
            OnPlaylistChanged();
            SetSmartFitStatus($"Now starts at {Clock(start)} — the strongest part of the song.");
            RecordWizardEdit();
        }
        catch (OperationCanceledException swallowed5)
        {
            SetSmartFitStatus("That took too long — nothing was changed.", isWarning: true);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed5);
        }
        finally
        {
            if (ReferenceEquals(_audioAnalysisCts, cts))
                _audioAnalysisCts = null;
            cts.Dispose();
            button.IsEnabled = true;
            button.Opacity = 1.0;
        }
    }

    private readonly System.Collections.Generic.Dictionary<string, AudioEnergyAnalysis> _energyCache =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task<AudioEnergyAnalysis?> GetTrackEnergyAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath)) return null;
        if (_energyCache.TryGetValue(audioPath, out var cached)) return cached;

        var analysis = await AnalyzeAudioEnergyAsync(audioPath, cancellationToken);
        if (analysis != null) _energyCache[audioPath] = analysis;
        return analysis;
    }

    private const double AutoPreviewBlindFraction = 0.40;
    private const double PreviewFadeInSeconds = 1.0;

    private CancellationTokenSource? _autoPreviewCts;
    private readonly System.Collections.Generic.Dictionary<string, double> _autoPreviewStartCache =
        new(StringComparer.OrdinalIgnoreCase);

    private DateTime? _previewFadeStartUtc;

    private void StartAutoPreview(MusicTrackItem track)
    {
        _previewCurrentOffset = track.DurationSec > 0 ? track.DurationSec / 2 : double.NaN;
        StartPreviewInternal(_previewCurrentOffset);
    }

    private async Task LearnAutoPreviewStartAsync(MusicTrackItem track)
    {
        try { _autoPreviewCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _autoPreviewCts = cts;

        try
        {
            var analysis = await GetTrackEnergyAsync(track.FilePath, cts.Token);
            if (analysis == null || cts.IsCancellationRequested) return;

            double length = track.DurationSec > 0 ? track.DurationSec : analysis.DurationSeconds;
            _autoPreviewStartCache[track.FilePath] = FindAutoPreviewStart(analysis, length);
        }
        catch (OperationCanceledException swallowed3)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MUSIC_WIZARD", $"Could not learn the best passage of '{Path.GetFileName(track.FilePath)}': {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_autoPreviewCts, cts)) _autoPreviewCts = null;
            cts.Dispose();
        }
    }

    private double FindAutoPreviewStart(AudioEnergyAnalysis analysis, double trackDurationSeconds)
    {
        double length = trackDurationSeconds > 0 ? trackDurationSeconds : analysis.DurationSeconds;
        if (length <= 20.0 || analysis.Energy.Length == 0) return Math.Max(0, length * 0.35);

        double bucket = analysis.BucketSeconds;
        const double windowSeconds = 12.0;
        int windowBuckets = Math.Max(1, (int)Math.Round(windowSeconds / bucket));

        int firstBucket = (int)Math.Floor((length * 0.15) / bucket);
        int lastBucket = (int)Math.Floor((length * 0.85) / bucket) - windowBuckets;
        firstBucket = Math.Clamp(firstBucket, 0, Math.Max(0, analysis.Energy.Length - 1));
        lastBucket = Math.Clamp(lastBucket, firstBucket, Math.Max(firstBucket, analysis.Energy.Length - windowBuckets));

        var prefix = new double[analysis.Energy.Length + 1];
        for (int i = 0; i < analysis.Energy.Length; i++) prefix[i + 1] = prefix[i] + analysis.Energy[i];

        int step = Math.Max(1, (int)Math.Round(0.25 / bucket));
        int bestBucket = firstBucket;
        double bestAverage = double.NegativeInfinity;

        for (int start = firstBucket; start <= lastBucket; start += step)
        {
            int end = Math.Min(analysis.Energy.Length, start + windowBuckets);
            if (end <= start) continue;
            double average = (prefix[end] - prefix[start]) / (end - start);
            if (average > bestAverage)
            {
                bestAverage = average;
                bestBucket = start;
            }
        }

        double seconds = bestBucket * bucket;
        double? onBeat = FindNearestPeakTime(analysis, seconds, 1.0);
        return Math.Clamp(onBeat ?? seconds, 0.0, Math.Max(0.0, length - 5.0));
    }

    private double CurrentPreviewFadeFactor()
    {
        if (_previewFadeStartUtc is not DateTime started) return 1.0;
        double elapsed = (DateTime.UtcNow - started).TotalSeconds;
        if (elapsed >= PreviewFadeInSeconds)
        {
            _previewFadeStartUtc = null;
            return 1.0;
        }
        double t = Math.Clamp(elapsed / PreviewFadeInSeconds, 0.0, 1.0);
        return t * t;
    }

    private const double FitByEndTailCushionSeconds = 0.35;
    private double? FindFitByEndStart(AudioEnergyAnalysis analysis, double neededSeconds, double trackDuration, out double musicEndSeconds)
    {
        musicEndSeconds = 0.0;
        if (analysis.Energy.Length == 0) return null;

        double bucket = analysis.BucketSeconds;
        if (trackDuration <= 0.01) trackDuration = analysis.DurationSeconds;
        if (trackDuration <= 0.01) return null;

        double sum = 0;
        int counted = 0;
        foreach (double value in analysis.Energy)
        {
            if (value <= 0.005) continue;
            sum += value;
            counted++;
        }
        if (counted == 0) return null;
        double average = sum / counted;
        double threshold = average / 3.0;

        int endBucket = -1;
        for (int i = analysis.Energy.Length - 1; i >= 0; i--)
        {
            if (analysis.Energy[i] >= threshold) { endBucket = i; break; }
        }
        if (endBucket < 0) return null;

        musicEndSeconds = Math.Clamp((endBucket + 1) * bucket + FitByEndTailCushionSeconds, 0.0, trackDuration);

        double start = musicEndSeconds - neededSeconds;
        if (start < 0.0) return null;

        double? onBeat = null;
        double bestDistance = double.MaxValue;
        foreach (double peak in analysis.PeakTimesSeconds)
        {
            if (peak > start || peak < start - 1.0) continue;
            double distance = start - peak;
            if (distance < bestDistance) { bestDistance = distance; onBeat = peak; }
        }

        return Math.Clamp(onBeat ?? start, 0.0, Math.Max(0.0, trackDuration - 0.01));
    }

    private async Task ApplyFitByEndAsync(Button button)
    {
        if (_playlist.Count == 0) return;
        int last = _playlist.Count - 1;
        var entry = _playlist[last];
        int fitEndIndex = last;
        if (!File.Exists(entry.Path))
        {
            ShowToast("That song file is missing.");
            return;
        }

        CancelAudioAnalysis();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _audioAnalysisCts = cts;
        button.IsEnabled = false;
        button.Opacity = 0.5;
        SetSmartFitStatus("Finding where the song really ends...");

        try
        {
            var analysis = await GetTrackEnergyAsync(entry.Path, cts.Token);
            if (analysis == null)
            {
                SetSmartFitStatus("Could not read this song.", isWarning: true);
                return;
            }

            double videoDuration = GetPhase3VideoDurationSeconds();
            var seg = FirstSegmentOf(last);
            double comesIn = seg is MusicBedSegment s ? Math.Max(0, s.OutputStartSec - s.LeadInSec) : MusicBedPlan.EndSec(CurrentPlan(), 0.0);
            double needed = Math.Max(MusicPlaylistMath.MinWindowSec, videoDuration - comesIn);
            double full = entry.FullSec > 0 ? entry.FullSec : analysis.DurationSeconds;
            double? start = FindFitByEndStart(analysis, needed, full, out double musicEnd);
            if (start == null)
            {
                SetSmartFitStatus("This song is too short to reach the end of the video — turn on Loop songs or add another song.", isWarning: true);
                return;
            }

            int at = OccurrenceIndex(fitEndIndex, entry.Path);
            if (at < 0) return;
            _playlist[at] = _playlist[at] with { InSec = start.Value, OutSec = Math.Max(start.Value + MusicPlaylistMath.MinWindowSec, musicEnd), UserTrimmed = true };
            _selectedSongIndex = at;
            OnPlaylistChanged();
            SetSmartFitStatus($"Starts at {Clock(start.Value)} and finishes on its last note right as the video ends.");
            RuntimeLog.Info("MUSIC_WIZARD",
                $"Fit By End Of Video: song {at + 1} ends at {musicEnd:F2}s, heard for {needed:F2}s, start set to {start.Value:F2}s.");
            RecordWizardEdit();
        }
        catch (OperationCanceledException swallowed2)
        {
            SetSmartFitStatus("That took too long — nothing was changed.", isWarning: true);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
        }
        finally
        {
            if (ReferenceEquals(_audioAnalysisCts, cts)) _audioAnalysisCts = null;
            cts.Dispose();
            button.IsEnabled = true;
            button.Opacity = 1.0;
        }
    }

    private void OnWizardKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox || focused is Avalonia.Controls.NumericUpDown) return;
        if (WizardOverlayOpen) return;

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && focused is not ListBox && focused is not ListBoxItem)
        {
            e.Handled = true;
            if (NextBtnCtl is { IsEnabled: true, IsVisible: true }) OnNextClicked(this, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (_backBtn is { IsEnabled: true }) _ = OnBackClickedAsync(this, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.Space)
        {
            TogglePreview();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Left && e.Key != Key.Right) return;
        if (_currentStep == StepFit && !_phase3Ready) return;
        if (focused is Canvas c && c.Name == "WaveformCanvas") return;

        double direction = e.Key == Key.Right ? 1.0 : -1.0;
        SkipPreview(direction * (_currentStep == StepChoose ? 20.0 : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 30.0 : 5.0));
        e.Handled = true;
    }

    private MusicTrackItem? FindTrackByPath(string path)
    {
        return _allTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadRecentMusicPins()
    {
        try
        {
            var state = Services.InProcessSessionState.ReadSnapshot();
            if (state["RecentMusicPaths"] is System.Text.Json.Nodes.JsonArray recentArray)
            {
                foreach (var node in recentArray)
                {
                    string? path = node?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(path))
                        _recentMusicPaths.Add(path);
                }
            }
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
    }

    private void SaveRecentMusicPins(System.Collections.Generic.IEnumerable<string> selectedPaths)
    {
        try
        {
            var orderedPaths = selectedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Concat(_recentMusicPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            _recentMusicPaths.Clear();
            foreach (string path in orderedPaths)
                _recentMusicPaths.Add(path);

            var recentArray = new System.Text.Json.Nodes.JsonArray();
            foreach (string path in orderedPaths)
                recentArray.AddNode(System.Text.Json.Nodes.JsonValue.Create(path));

            FreeVideoStudio.Core.Ipc.SessionStateWriter.For(_paths)
                .Enqueue(new System.Text.Json.Nodes.JsonObject
                {
                    ["RecentMusicPaths"] = recentArray
                });
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
    }
    private void HandleSongOffsetKeyDown(KeyEventArgs e)
    {
        if (!Vm.IsPartView || SelectedEntry == null) return;
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5.0 : 1.0;
        double here = CurrentPartListeningSec();
        if (e.Key == Key.Left) SeekPart(here - step);
        else if (e.Key == Key.Right) SeekPart(here + step);
        else if (e.Key == Key.Home) SeekPart(0);
        else return;
        e.Handled = true;
    }

    private bool _isSeekingPhase3Video = false;
    private double? _nextPhase3SeekTarget = null;
    private long _lastPhase3SeekTimestamp = 0;
    private const int Phase3SeekCoalesceMs = 15;
    private DispatcherTimer? _phase3SeekFlushTimer;

    private void OnPhase3SeekCompleted()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            _isSeekingPhase3Video = false;
            if (_nextPhase3SeekTarget.HasValue)
            {
                double target = _nextPhase3SeekTarget.Value;
                _nextPhase3SeekTarget = null;
                await SeekPhase3VideoHostInternal(target, forcePause: true);
            }
        });
    }

    private void EnsurePhase3SeekFlushTimer()
    {
        if (_phase3SeekFlushTimer != null)
        {
            if (!_phase3SeekFlushTimer.IsEnabled) _phase3SeekFlushTimer.Start();
            return;
        }

        _phase3SeekFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Phase3SeekCoalesceMs) };
        _phase3SeekFlushTimer.Tick += (_, _) =>
        {
            if (!_nextPhase3SeekTarget.HasValue) { _phase3SeekFlushTimer?.Stop(); return; }
            if (_isSeekingPhase3Video) return;
            if (Environment.TickCount64 - _lastPhase3SeekTimestamp < Phase3SeekCoalesceMs) return;

            double target = _nextPhase3SeekTarget.Value;
            _nextPhase3SeekTarget = null;
            _phase3SeekFlushTimer?.Stop();
            _ = SeekPhase3VideoHostInternal(target, forcePause: true);
        };
        _phase3SeekFlushTimer.Start();
    }

    private void SeekPhase3VideoHost(double outputRelativeSec, bool forcePause)
    {
        long now = Environment.TickCount64;
        if (_isSeekingPhase3Video || (now - _lastPhase3SeekTimestamp < Phase3SeekCoalesceMs))
        {
            _nextPhase3SeekTarget = outputRelativeSec;
            EnsurePhase3SeekFlushTimer();
            return;
        }

        _ = SeekPhase3VideoHostInternal(outputRelativeSec, forcePause);
    }

    private async Task SeekPhase3VideoHostInternal(double outputRelativeSec, bool forcePause)
    {
        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient == null)
        {
            _isSeekingPhase3Video = false;
            return;
        }

        _isSeekingPhase3Video = true;
        _lastPhase3SeekTimestamp = Environment.TickCount64;
        StopPhase3FreezeSound();

        double sourceRelativeSec = MapPhase3OutputToSourceRelativeSeconds(outputRelativeSec);
        double sourceAbsSec = (_trimStartMs / 1000.0) + sourceRelativeSec;
        double speed = GetPhase3PreviewSpeedAtSourceRelativeSeconds(sourceRelativeSec);

        try
        {
            await wizardVideoHost.IpcClient.SendCommandAsync("seek", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            await wizardVideoHost.IpcClient.SetPropertyAsync("speed", Math.Max(0.001, speed).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
            if (forcePause || speed <= 0.001)
                await wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }
        catch (Exception ex)
        {
            _isSeekingPhase3Video = false;
            RuntimeLog.Swallowed(ex);
        }
    }

    private void SyncPhase3VideoPreviewClock()
    {
        if (_currentStep != 3 || !_isPreviewPlaying) return;

        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient == null) return;

        double outputRelativeSec = GetCurrentPhase3VideoRelativeSeconds();
        double sourceRelativeSec = MapPhase3OutputToSourceRelativeSeconds(outputRelativeSec);
        double sourceAbsSec = (_trimStartMs / 1000.0) + sourceRelativeSec;
        double speed = GetPhase3PreviewSpeedAtSourceRelativeSeconds(sourceRelativeSec);

        UpdatePhase3FreezeSound(outputRelativeSec);
        if (speed <= 0.001)
        {
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
            return;
        }

        _ = wizardVideoHost.IpcClient.SetPropertyAsync("speed", speed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
        if (Math.Abs(wizardVideoHost.IpcClient.CurrentTime - sourceAbsSec) > 0.15)
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", sourceAbsSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "no");
    }
    private void UpdateNextButtonState()
    {
        var nextBtn = NextBtnCtl;
        if (nextBtn == null) return;

        if (_currentStep == StepChoose)
        {
            bool ready = _highlightedTrack != null;
            nextBtn.IsEnabled = ready;
            nextBtn.Opacity = ready ? 1.0 : 0.5;
            ToolTip.SetTip(nextBtn, !ready
                ? (_isMergerMode ? "Choose a song first" : "Select a song first")
                : "Use the highlighted song and choose which part of it plays");
        }
        else
        {
            nextBtn.IsEnabled = _phase3Ready && _playlist.Count > 0;
            nextBtn.Opacity = nextBtn.IsEnabled ? 1.0 : 0.5;
            ToolTip.SetTip(nextBtn, !_phase3Ready ? "Wait for the preview to finish loading"
                : Vm.IsPartView ? "Keep this part of the song" : "Put this music on your video");
        }
        ApplyStepButtons();
    }

    private async void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        if (_choosingSongToProceed) return;
        _choosingSongToProceed = true;
        try
        {
            if (_currentStep == StepChoose)
            {
                StopPreview();
                await _songPreviewGate.WaitAsync();
                _songPreviewGate.Release();
                if (_highlightedTrack == null)
                {
                    ShowToast(_isMergerMode ? "⚠ Choose a song first!" : "⚠ Please select a song first!");
                    return;
                }
                if (!PlaceChosenOccurrence(_highlightedTrack)) return;
                await EnterFitAsync(ViewModels.MusicWizardView.Part, _editIndex);
                return;
            }

            if (Vm.IsPartView)
            {
                await OnStepNextAsync();
                return;
            }

            if (!_phase3Ready || _playlist.Count == 0)
            {
                ShowToast("The preview is still loading.");
                return;
            }

            StopPreview();
            Result = BuildResultFromPlaylist();
            SaveRecentMusicPins(Result.MusicFilePaths);
            RuntimeLog.Success("MUSIC_WIZARD", $"Wizard completed. {Result.MusicFilePaths.Count} song(s), first: {Path.GetFileName(Result.MusicFilePath)}, SongStart: {Result.OffsetSeconds:F2}s, Timeline: {Result.TimelineStartSeconds:F2}-{Result.TimelineEndSeconds:F2}s, Loop: {Result.LoopMusic}, Ducking: {Result.EnableDucking}, Carving: {Result.EnableCarving}, VideoVol: {Result.VideoVolume}, MusicVol: {Result.MusicVolume}");
            RuntimeLog.Debug("MUSIC_WIZARD", $"Wizard completed song windows: {string.Join("; ", Result.MusicFilePaths.Select((p, i) => $"{Path.GetFileName(p)} {Result.MusicStartsSeconds[i]:F2}-{Result.MusicEndsSeconds[i]:F2}"))}");
            _isSafeToClose = true;
            Close();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Next/Apply failed: {ex.Message}");
            ShowToast("Something went wrong — please try again.");
        }
        finally { _choosingSongToProceed = false; }
    }

    private double GetKnownTrackDurationSeconds(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return 0.0;
        foreach (var e in _playlist)
            if (e.FullSec > 0 && string.Equals(e.Path, filePath, StringComparison.OrdinalIgnoreCase)) return e.FullSec;
        var item = AvailableTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        item ??= _allTracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        return item?.DurationSec > 0 ? item.DurationSec : 0.0;
    }

    private async Task<string?> GenerateThumbnailsStripAsync(string ffmpegPath, string videoPath, double startSec, double durationSec, CancellationToken cancellationToken, int frames = ThumbnailStripGenerator.DefaultFrames)
        => await ThumbnailStripGenerator.GenerateAsync(
            ffmpegPath, videoPath, _paths.TempDirectory, startSec, durationSec, cancellationToken, frames, "MusicWizard");

    private async Task<string?> GeneratePhase3MusicSequenceWaveformAsync(
        string ffmpegPath,
        System.Collections.Generic.IReadOnlyList<Phase3MusicPreviewSegment> segments,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        string? tempPng = null;
        Process? process = null;
        try
        {
            var usableSegments = segments
                .Where(segment => segment.TimelineEndSec > segment.TimelineStartSec + 0.001 &&
                                  !string.IsNullOrWhiteSpace(segment.Path) &&
                                  File.Exists(segment.Path))
                .ToList();

            if (usableSegments.Count == 0)
                return null;

            tempPng = Path.Combine(_paths.TempDirectory, $"fvs_wave_sequence_{Guid.NewGuid():N}.png");

            var filter = new System.Text.StringBuilder();
            for (int i = 0; i < usableSegments.Count; i++)
            {
                var segment = usableSegments[i];
                double duration = Math.Max(0.001, segment.TimelineEndSec - segment.TimelineStartSec);
                filter.Append($"[{i}:a]atrim=start={segment.FileStartSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:duration={duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},");
                filter.Append($"asetpts=PTS-STARTPTS,aformat=channel_layouts=mono[a{i}];");
            }

            if (usableSegments.Count > 1)
            {
                filter.Append(string.Concat(Enumerable.Range(0, usableSegments.Count).Select(i => $"[a{i}]")));
                filter.Append($"concat=n={usableSegments.Count}:v=0:a=1[a_seq];");
                filter.Append($"[a_seq]volume=1.5,showwavespic=s={width}x{height}:colors=0x7DD3FC:draw=full[v_wave]");
            }
            else
            {
                filter.Append($"[a0]volume=1.5,showwavespic=s={width}x{height}:colors=0x7DD3FC:draw=full[v_wave]");
            }

            var seqArgs = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
            foreach (var segment in usableSegments)
            {
                seqArgs.Add("-i");
                seqArgs.Add(segment.Path);
            }
            seqArgs.Add("-filter_complex");
            seqArgs.Add(filter.ToString());
            seqArgs.Add("-map");
            seqArgs.Add("[v_wave]");
            seqArgs.Add("-frames:v");
            seqArgs.Add("1");
            seqArgs.Add(tempPng);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in seqArgs) psi.ArgumentList.Add(arg);

            process = Process.Start(psi);
            using var _processCleanup = process;
            if (process == null) return null;
            ChildProcessTracker.AddProcess(process);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await outputTask;
            _ = await errorTask;

            if (process.ExitCode == 0 && File.Exists(tempPng))
                return tempPng;

            if (File.Exists(tempPng))
                File.Delete(tempPng);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
            }

            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to generate sequence waveform: {ex.Message}");
            if (tempPng != null && File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
            }
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }

        return null;
    }

    private string FindBinary(string name)
    {
        string basePath = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var current = new DirectoryInfo(basePath);
        while (current != null)
        {
            string fPath = Path.Combine(current.FullName, "frontend", name);
            if (File.Exists(fPath)) return Path.GetFullPath(fPath);

            string bPath = Path.Combine(current.FullName, "backend", name);
            if (File.Exists(bPath)) return Path.GetFullPath(bPath);

            string srcPath = Path.Combine(current.FullName, "binaries", name);
            if (File.Exists(srcPath)) return Path.GetFullPath(srcPath);

            current = current.Parent;
        }

        return name;
    }

    private string ResolveMpvPath() 
    {
        string p = FindBinary("mpv.exe");
        if (p == "mpv.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }

    private string ResolveFfprobePath() 
    {
        string p = FindBinary("ffprobe.exe");
        if (p == "ffprobe.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffprobe.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }

    private string ResolveFfmpegPath() 
    {
        string p = FindBinary("ffmpeg.exe");
        if (p == "ffmpeg.exe")
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "binaries", "ffmpeg.exe");
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        return p;
    }

    private void SetLoadingOverlay(string name, bool isVisible)
    {
        var overlay = this.FindControl<Avalonia.Controls.Border>(name);
        if (overlay != null) overlay.IsVisible = isVisible;
    }

    private async Task LoadPhase3DataAsync(CancellationToken cancellationToken, int loadVersion)
    {
        SetPhase3Status("Loading final preview...");
        SetLoadingOverlay("Phase3VideoLoadingOverlay", true);
        SetLoadingOverlay("Phase3ThumbLoadingOverlay", true);
        SetLoadingOverlay("Phase3WaveLoadingOverlay", true);
        _phase3Ready = false;
        UpdateNextButtonState();
        UpdatePreviewControlsState();

        try
        {
            _phase3VideoDurationSec = GetPhase3VideoDurationSeconds();
            var thumbLaneGrid = this.FindControl<Avalonia.Controls.Grid>("ThumbnailLaneGrid");
            var waveLane = this.FindControl<Avalonia.Controls.Image>("WaveformLaneImage");
            if (thumbLaneGrid != null)
            {
                foreach (var child in thumbLaneGrid.Children)
                {
                    if (child is Avalonia.Controls.Image oldFrame)
                    {
                        (oldFrame.Source as IDisposable)?.Dispose();
                        oldFrame.Source = null;
                    }
                }
                thumbLaneGrid.Children.Clear();
                thumbLaneGrid.ColumnDefinitions.Clear();
            }
            if (waveLane != null)
            {
                (waveLane.Source as IDisposable)?.Dispose();
                waveLane.Source = null;
            }
            _phase3WaveSpanSec = 0;
            _phase3ClipDurationsSec.Clear();

            _previewDetach?.Attach();

            var border = VideoHostBorderCtl;
            if (border != null && !string.IsNullOrEmpty(_videoPath))
            {
                if (border.Child is FreeVideoStudio.App.MpvVideoView oldHost)
                {
                    var oldShutdown = await oldHost.ShutdownAsync();
                    if (!oldShutdown.Succeeded)
                    {
                        RuntimeLog.Fail("MUSIC_WIZARD", $"The previous preview did not shut down cleanly ({oldShutdown.Reason}); phase 3 preview is unavailable until the app is restarted.");
                        ShowToast("Preview unavailable. Restart the app to restore it.");
                        return;
                    }
                    border.Child = null;
                }

                var wizardVideoHost = new FreeVideoStudio.App.MpvVideoView
                {
                    Name = "WizardVideoHost",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                border.Child = wizardVideoHost;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(50, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_currentStep != 3 || loadVersion != _phase3LoadVersion) return;

                await wizardVideoHost.StartMpvProcessAsync(ResolveMpvPath());

                if (wizardVideoHost.IpcClient == null)
                    throw new InvalidOperationException("Video preview did not start.");

                wizardVideoHost.IpcClient.SeekCompleted += OnPhase3SeekCompleted;

                await wizardVideoHost.IpcClient.LoadFileAsync(_videoPath, _trimStartMs / 1000.0);
                await wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", (_trimStartMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                await wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                RuntimeLog.Info("MUSIC_WIZARD", "Phase 3 MPV preview video loaded.");
                RefreshDetachButtonState();

                var videoVolSlider = VideoVolSliderCtl;
                if (videoVolSlider != null)
                    await wizardVideoHost.IpcClient.ApplyPreviewGainAsync(GetPreviewVideoBalance());
            }
            SetLoadingOverlay("Phase3VideoLoadingOverlay", false);

            cancellationToken.ThrowIfCancellationRequested();

            if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;

            _phase3Ready = true;
            SetPhase3Status("");
            UpdateFinalPlacementSummary();
            UpdateProblemFlags();
            DrawPhase3TimelineScale();
            UpdatePlayhead();
            UpdateNextButtonState();
            UpdatePreviewControlsState();

            _phase3ThumbTask = RunPhase3ThumbnailLaneAsync(thumbLaneGrid, cancellationToken, loadVersion);
            _phase3WaveTask = RunPhase3WaveformLaneAsync(waveLane, cancellationToken, loadVersion);
        }
        catch (OperationCanceledException swallowed9)
        {
            SetPhase3Status("");
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed9);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to load phase 3 preview: {ex.Message}");
            SetPhase3Status("Final preview could not load. You can go back and try again.");
            _phase3Ready = false;
            UpdateNextButtonState();
            UpdatePreviewControlsState();
        }
        finally
        {
            SetLoadingOverlay("Phase3VideoLoadingOverlay", false);

            if (loadVersion != _phase3LoadVersion)
            {
                SetLoadingOverlay("Phase3ThumbLoadingOverlay", false);
                SetLoadingOverlay("Phase3WaveLoadingOverlay", false);
            }

            if (loadVersion == _phase3LoadVersion)
            {
                DrawPhase3TimelineScale();
            }
        }
    }

    private Task _phase3ThumbTask = Task.CompletedTask;
    private Task _phase3WaveTask = Task.CompletedTask;

    private async Task RunPhase3ThumbnailLaneAsync(
        Avalonia.Controls.Grid? thumbLaneGrid, CancellationToken cancellationToken, int loadVersion)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (thumbLaneGrid != null)
            {
                var ffmpeg = ResolveFfmpegPath();
                var videosToThumb = (_isMergerMode && _mergerVideos != null && _mergerVideos.Count > 0) 
                    ? _mergerVideos : new System.Collections.Generic.List<string> { _videoPath };

                foreach (var f in _lastPhase3ThumbFiles) { try { if (File.Exists(f)) File.Delete(f); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); } }
                _lastPhase3ThumbFiles.Clear();

                double totalDur = 0;
                var videoDurs = new System.Collections.Generic.List<double>();
                foreach (var v in videosToThumb)
                {
                    double dur = 10.0;
                    if (!_isMergerMode) dur = _phase3VideoDurationSec;
                    else
                    {
                        var prober = new FreeVideoStudio.Core.Media.MediaProber(ffmpeg.Replace("ffmpeg.exe", "ffprobe.exe"), v);
                        try { dur = MergerWindowDuration(videoDurs.Count) ?? await prober.GetDurationAsync(); } catch (System.Exception swallowed7)
                        {
                            dur = 10.0;
                            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed7);
                        }
                    }
                    videoDurs.Add(dur);
                    totalDur += dur;
                }

                if (totalDur <= 0) totalDur = 1.0;
                _phase3ThumbSlots.Clear();
                _phase3ClipDurationsSec.Clear();
                _phase3ClipDurationsSec.AddRange(videoDurs.Select(d => Math.Max(0.1, d / Math.Max(0.001, _phase3BaseSpeed))));

                var laneImages = new System.Collections.Generic.List<Avalonia.Controls.Image>(videosToThumb.Count);
                for (int i = 0; i < videosToThumb.Count; i++)
                {
                    thumbLaneGrid.ColumnDefinitions.Add(
                        new Avalonia.Controls.ColumnDefinition(videoDurs[i], Avalonia.Controls.GridUnitType.Star));

                    var img = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.Fill };
                    Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(
                        img, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                    Avalonia.Controls.Grid.SetColumn(img, i);
                    thumbLaneGrid.Children.Add(img);
                    laneImages.Add(img);
                }

                using var stripGate = new System.Threading.SemaphoreSlim(
                    Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

                var stripTasks = new System.Collections.Generic.List<Task>(videosToThumb.Count);
                for (int i = 0; i < videosToThumb.Count; i++)
                {
                    double vDur = videoDurs[i];
                    int framesCount = Math.Max(1, (int)Math.Round(15 * (vDur / totalDur)));
                    double startOffset = (_isMergerMode || i > 0) ? MergerWindowStart(i) : (_trimStartMs / 1000.0);
                    string videoForStrip = videosToThumb[i];
                    var target = laneImages[i];
                    _phase3ThumbSlots.Add(new Phase3ThumbSlot(target, videoForStrip, startOffset, vDur, framesCount));

                    stripTasks.Add(Task.Run(async () =>
                    {
                        await stripGate.WaitAsync(cancellationToken);
                        try
                        {
                            bool warmed = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => MountWarmFilmstrip(target, videoForStrip, startOffset, vDur, framesCount, loadVersion));
                            if (warmed) return;
                            bool streamed = await ThumbnailStripGenerator.StreamAsync(
                                ffmpeg, videoForStrip, startOffset, vDur, cancellationToken,
                                onReady: wb =>
                                {
                                    if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
                                    (target.Source as IDisposable)?.Dispose();
                                    target.Source = wb;
                                },
                                onFrame: () => target.InvalidateVisual(),
                                frames: framesCount,
                                logTag: "MUSIC_WIZARD");

                            if (streamed) return;

                            string? path = await GenerateThumbnailsStripAsync(
                                ffmpeg, videoForStrip, startOffset, vDur, cancellationToken, framesCount);
                            if (path == null) return;

                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
                                _lastPhase3ThumbFiles.Add(path);
                                try
                                {
                                    using var fs = File.OpenRead(path);
                                    target.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                                }
                                catch (Exception ex)
                                {
                                    RuntimeLog.Fail("MUSIC_WIZARD", $"Could not load a filmstrip thumbnail: {ex.Message}");
                                }
                            });
                        }
                        finally { stripGate.Release(); }
                    }));
                }

                await Task.WhenAll(stripTasks);

                if (loadVersion == _phase3LoadVersion && _currentStep == 3)
                {
                    DrawPhase3TimelineScale();
                }
            }
        }
        catch (OperationCanceledException swallowed11)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed11);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"The film lane could not be generated: {ex.Message}");
        }
        finally
        {
            if (loadVersion == _phase3LoadVersion)
            {
                SetLoadingOverlay("Phase3ThumbLoadingOverlay", false);
            }
        }
    }

    private async Task RunPhase3WaveformLaneAsync(
        Avalonia.Controls.Image? waveLane, CancellationToken cancellationToken, int loadVersion)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (waveLane != null && _playlist.Count > 0)
            {
                var previewSegments = BuildPhase3MusicPreviewSegments();
                double audibleMusicDuration = previewSegments.Count > 0
                    ? Math.Min(GetPhase3VideoDurationSeconds(), previewSegments[^1].TimelineEndSec)
                    : 0.0;
                double tailSec = PartLanesActive ? WaveTailSec(previewSegments) : 0.0;
                if (tailSec > 0)
                {
                    var last = previewSegments[^1];
                    previewSegments[^1] = new Phase3MusicPreviewSegment
                    {
                        Path = last.Path,
                        TimelineStartSec = last.TimelineStartSec,
                        TimelineEndSec = last.TimelineEndSec + tailSec,
                        FileStartSec = last.FileStartSec,
                    };
                    audibleMusicDuration = last.TimelineEndSec + tailSec;
                }
                waveLane.Width = double.NaN;
                waveLane.Margin = new Avalonia.Thickness(0, 0, 0, 0);

                if (audibleMusicDuration > 0.01)
                {
                    var ffmpeg = ResolveFfmpegPath();
                    bool useSequenceWaveform = previewSegments.Count > 1 || IsPhase3LoopMusicEnabled();
                    string? wavePath = useSequenceWaveform
                        ? await GeneratePhase3MusicSequenceWaveformAsync(ffmpeg, previewSegments, 1200, 60, cancellationToken)
                        : await FreeVideoStudio.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(
                            ffmpeg, previewSegments[0].Path, 1200, 60, previewSegments[0].FileStartSec, audibleMusicDuration, cancellationToken);

                    if (loadVersion != _phase3LoadVersion || _currentStep != 3) return;
                    if (wavePath != null)
                    {
                        try
                        {
                            using var fs = File.OpenRead(wavePath);
                            (waveLane.Source as IDisposable)?.Dispose();
                            waveLane.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                            DeleteTempFile(ref _lastPhase3WaveFile);
                            _lastPhase3WaveFile = wavePath;
                            _phase3WaveSpanSec = audibleMusicDuration;
                            UpdatePhase3WaveformLaneWidth();
                        }
                        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
                    }
                }
            }
        }
        catch (OperationCanceledException swallowed6)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed6);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"The music waveform lane could not be generated: {ex.Message}");
        }
        finally
        {
            if (loadVersion == _phase3LoadVersion)
            {
                SetLoadingOverlay("Phase3WaveLoadingOverlay", false);
            }
        }
    }

    private void CancelPhase3Load()
    {
        _phase3LoadVersion++;
        try { _phase3LoadCts?.Cancel(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        try { _phase3LoadCts?.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        _phase3LoadCts = null;
        _phase3Ready = false;
    }

    private async Task DisposePhase3VideoHostAsync()
    {
        var border = VideoHostBorderCtl;
        if (border?.Child is FreeVideoStudio.App.MpvVideoView wizardVideoHost)
        {
            if (wizardVideoHost.IpcClient != null)
                wizardVideoHost.IpcClient.SeekCompleted -= OnPhase3SeekCompleted;
            border.Child = null;
            var shutdown = await wizardVideoHost.ShutdownAsync();
            if (!shutdown.Succeeded) RuntimeLog.Fail("MUSIC_WIZARD", $"Phase 3 preview did not shut down cleanly: {shutdown.Reason}");
        }
    }

    private double GetPhase3VideoDurationSeconds()
    {
        double duration = _trimEndMs > _trimStartMs
            ? (_trimEndMs - _trimStartMs) / 1000.0
            : (_actualVideoDurationMs > _trimStartMs ? (_actualVideoDurationMs - _trimStartMs) / 1000.0 : 60.0);
        double effectiveDuration = CalculatePhase3EffectiveDurationSeconds(duration);
        return Math.Max(0.1, effectiveDuration);
    }

    private double GetPhase3SourceDurationSeconds()
    {
        double duration = _trimEndMs > _trimStartMs
            ? (_trimEndMs - _trimStartMs) / 1000.0
            : (_actualVideoDurationMs > _trimStartMs ? (_actualVideoDurationMs - _trimStartMs) / 1000.0 : 60.0);
        return Math.Max(0.1, duration);
    }

    private double CalculatePhase3EffectiveDurationSeconds(double sourceDurationSec)
    {
        var timeline = FreeVideoStudio.Core.Media.OutputTimeline.Create(
            sourceDurationSec * 1000.0,
            _phase3SpeedSegments,
            _phase3BaseSpeed,
            _trimStartMs,
            FreeVideoStudio.Core.Media.MemePlacement.ToInsertions(_phase3Memes),
            FreeVideoStudio.Core.Media.CutRange.ToClipRelative(_phase3Cuts, _trimStartMs));
        return Math.Max(0.001, timeline.TotalOutputSeconds);
    }

    private double MapPhase3OutputToSourceRelativeSeconds(double outputRelativeSec)
    {
        double sourceDurationSec = GetPhase3SourceDurationSeconds();
        var timeline = FreeVideoStudio.Core.Media.OutputTimeline.Create(
            sourceDurationSec * 1000.0,
            _phase3SpeedSegments,
            _phase3BaseSpeed,
            _trimStartMs,
            FreeVideoStudio.Core.Media.MemePlacement.ToInsertions(_phase3Memes),
            FreeVideoStudio.Core.Media.CutRange.ToClipRelative(_phase3Cuts, _trimStartMs));
        double clamped = Math.Clamp(outputRelativeSec, 0, GetPhase3VideoDurationSeconds());
        return timeline.OutputToSourceRelative(clamped);
    }

    private double GetPhase3PreviewSpeedAtSourceRelativeSeconds(double sourceRelativeSec)
    {
        double sourceAbsMs = _trimStartMs + sourceRelativeSec * 1000.0;
        foreach (var seg in _phase3SpeedSegments)
        {
            if (sourceAbsMs >= seg.StartMs && sourceAbsMs <= seg.EndMs)
                return Math.Max(0.0, seg.Speed);
        }

        return Math.Max(0.001, _phase3BaseSpeed);
    }

    private double GetCurrentPhase3VideoRelativeSeconds()
    {
        double fallback = Math.Clamp(_previewCurrentOffset - _songStartSeconds, 0, GetPhase3VideoDurationSeconds());
        if (_currentStep == 3 && _isPreviewPlaying && _phase3PreviewClockStartTime.HasValue)
            return Math.Clamp(
                _phase3PreviewClockStartOffsetSec + (DateTime.UtcNow - _phase3PreviewClockStartTime.Value).TotalSeconds,
                0,
                GetPhase3VideoDurationSeconds());

        return fallback;
    }

    private void EnforcePhase3PreviewEnd()
    {
        if (_currentStep != 3 || !_isPreviewPlaying) return;

        double videoDuration = GetPhase3VideoDurationSeconds();
        if (GetCurrentPhase3VideoRelativeSeconds() < videoDuration - 0.03) return;

        _previewCurrentOffset = _songStartSeconds + videoDuration;
        var wizardVideoHost = WizardVideoHost;
        if (wizardVideoHost?.IpcClient != null)
        {
            double endTime = (_trimStartMs / 1000.0) + MapPhase3OutputToSourceRelativeSeconds(videoDuration);
            _ = wizardVideoHost.IpcClient.SetPropertyAsync("time-pos", endTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        StopPreview();
        if (PartLanesActive && _editIndex >= 0 && _editIndex < _playlist.Count && EntryOverflowSec(_editIndex) > 0)
        {
            _partCursorSec = PartSongSecAt(videoDuration);
            _ = StartPartAuditionAsync(_playlist[_editIndex].Path, _partCursorSec);
        }
        UpdatePlayhead();
    }

    private FreeVideoStudio.App.Infrastructure.DebouncedStateWriter? _volumeWriter;

    private void SaveWizardVolumes()
    {
        try
        {
            var updates = new System.Text.Json.Nodes.JsonObject();
            if (VideoVolSliderCtl is { } videoVolSlider) updates["WizardVideoVolume"] = videoVolSlider.Value;
            if (MusicVolSliderCtl is { } musicVolSlider) updates["WizardMusicVolume"] = musicVolSlider.Value;
            (_volumeWriter ??= new FreeVideoStudio.App.Infrastructure.DebouncedStateWriter(_paths, owner: this)).Queue(updates);
        }
        catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
    }

    private void UpdatePhase3WaveformLaneWidth()
    {
        var clip = this.FindControl<Canvas>("Phase3WaveformClip");
        var waveImg = this.FindControl<Avalonia.Controls.Image>("WaveformLaneImage");
        if (clip == null || waveImg == null) return;

        double videoDuration = GetPhase3VideoDurationSeconds();
        if (videoDuration <= 0) return;

        double ratio = PartLanesActive && _phase3WaveSpanSec > 0
            ? Math.Clamp(_phase3WaveSpanSec / Math.Max(videoDuration, LaneAxisSec()), 0, 1)
            : Math.Min(GetQueuedMusicCoverageSeconds(), videoDuration) / Math.Max(videoDuration, LaneAxisSec());
        waveImg.Width = clip.Bounds.Width * ratio;
        waveImg.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        waveImg.Height = clip.Bounds.Height > 1 ? clip.Bounds.Height : 60;
        waveImg.Opacity = _partCut != null ? 0 : 1;
    }
    private void UpdateFinalPlacementSummary()
    {
        var label = this.FindControl<TextBlock>("FinalPlacementLabel");
        if (label == null) return;
        string tip = "Press play to watch your video with the music.\n\n" +
                     "The coloured blocks under the film are your songs, exactly where they play. Drag the right edge of a block to make that song end sooner or later.\n\n" +
                     "The card at the bottom shows the song you picked: the bright box is the part that plays.";
        if (_playlist.Count > 0)
            tip += $"\n\nYour video is {Clock(GetPhase3VideoDurationSeconds())} long; {_playlist.Count} song{(_playlist.Count == 1 ? "" : "s")}.";
        ToolTip.SetTip(label, tip);
    }

    private bool IsPhase3LoopMusicEnabled()
    {
        return LoopMusicCheckBoxCtl?.IsChecked ?? false;
    }

    private System.Collections.Generic.List<Phase3MusicPreviewSegment> BuildPhase3MusicPreviewSegments()
    {
        var segments = new System.Collections.Generic.List<Phase3MusicPreviewSegment>();
        foreach (var seg in CurrentPlan())
        {
            if (string.IsNullOrWhiteSpace(seg.Path) || !File.Exists(seg.Path)) continue;
            segments.Add(new Phase3MusicPreviewSegment
            {
                Path = seg.Path,
                TimelineStartSec = seg.OutputStartSec,
                TimelineEndSec = seg.OutputEndSec,
                FileStartSec = seg.FileOffsetSec + seg.LeadInSec
            });
        }
        return segments;
    }

    private Phase3MusicPreviewSegment? FindPhase3MusicPreviewSegment(double outputRelativeSec)
    {
        foreach (var segment in BuildPhase3MusicPreviewSegments())
        {
            if (outputRelativeSec >= segment.TimelineStartSec &&
                outputRelativeSec < segment.TimelineEndSec - 0.005)
            {
                return segment;
            }
        }

        return null;
    }

    private async void QueuePhase3MusicPreviewSync()
    {
        if (_currentStep != 3 || !_isPreviewPlaying || _audioIpcClient == null)
            return;

        if (Interlocked.Exchange(ref _phase3MusicSyncInFlight, 1) == 1)
            return;

        try
        {
            await SyncPhase3MusicPreviewTrackAsync(forceReload: false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Phase 3 music preview sync failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _phase3MusicSyncInFlight, 0);
        }
    }

    private async Task EnsureAudioPreviewClientAsync()
    {
        if (_audioIpcClient != null)
            return;

        _audioIpcClient = new FreeVideoStudio.Core.Media.MpvIpcClient();
        await _audioIpcClient.StartAudioOnlyAsync(ResolveMpvPath());
    }

    private async Task SyncPhase3MusicPreviewTrackAsync(bool forceReload)
    {
        if (_currentStep != 3 || !_isPreviewPlaying)
            return;

        await EnsureAudioPreviewClientAsync();

        if (_audioIpcClient == null)
            return;

        double outputRelativeSec = GetCurrentPhase3VideoRelativeSeconds();
        if (await SyncRenderedMixAsync(outputRelativeSec)) return;
        var segment = FindPhase3MusicPreviewSegment(outputRelativeSec);
        if (segment == null)
        {
            await _audioIpcClient.SetPropertyAsync("pause", "yes");
            _phase3PreviewMusicPath = null;
            _phase3PreviewMusicSegmentStartSec = double.NaN;
            return;
        }

        double audioStartOffset = Math.Max(0.0, segment.FileStartSec + outputRelativeSec - segment.TimelineStartSec);
        string targetPath = segment.Path.Replace("\\", "/");
        bool segmentChanged =
            !string.Equals(_phase3PreviewMusicPath, targetPath, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(_phase3PreviewMusicSegmentStartSec - segment.TimelineStartSec) > 0.001;

        if (forceReload || segmentChanged)
        {
            await _audioIpcClient.SetPropertyAsync("start", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await _audioIpcClient.SendCommandAsync("loadfile", targetPath, "replace");
            _phase3PreviewMusicPath = targetPath;
            _lastLoadedTrackPath = targetPath;
            _phase3PreviewMusicSegmentStartSec = segment.TimelineStartSec;
        }
        else if (Math.Abs(_audioIpcClient.CurrentTime - audioStartOffset) > 0.5)
        {
            try
            {
                await _audioIpcClient.SendCommandAsync("seek", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }

        await _audioIpcClient.ApplyPreviewGainAsync(GetPreviewMusicBalance());
        ApplyPreviewMusicFilters();
        if (!PreviewStillWantsMusic()) { await _audioIpcClient.SetPropertyAsync("pause", "yes"); return; }
        await _audioIpcClient.SetPropertyAsync("pause", "no");
    }

    private void RefreshVolumeLabels()
    {
        if (this.FindControl<TextBlock>("VideoVolLabel") is { } v && VideoVolSliderCtl is { } vs) v.Text = $"{vs.Value:0}%";
        if (this.FindControl<TextBlock>("MusicVolLabel") is { } m && MusicVolSliderCtl is { } ms) m.Text = $"{ms.Value:0}%";
    }

    private bool PreviewStillWantsMusic() => _currentStep == StepFit && _isPreviewPlaying && !_partAuditionActive;

    private void ApplyPreviewMusicFilters()
    {
        if (_audioIpcClient == null) return;
        _ = _audioIpcClient.SetPropertyAsync("af", "");
    }

    private void ApplyMixSwitchesFromSettings()
    {
        var d = Infrastructure.SettingsManager.Instance.Defaults;
        void Apply(Avalonia.Controls.Primitives.ToggleButton? cb, bool enabled)
        {
            if (cb == null || enabled) return;
            cb.IsChecked = false;
            cb.IsEnabled = false;
            ToolTip.SetTip(cb, "Turned off in Settings › Sound & Music › Music vs. game sound.");
        }
        Apply(DuckingCheckBoxCtl, d.DuckingEnabled);
        Apply(CarvingCheckBoxCtl, d.CarvingEnabled);
    }
    private void UpdatePreviewControlsState()
    {
        bool enabled = _currentStep == StepChoose ? _highlightedTrack != null : _playlist.Count > 0 && _phase3Ready;
        foreach (string name in new[] { "PlayBtn", "SkipBackBtn", "SkipForwardBtn" })
        {
            var btn = this.FindControl<Button>(name);
            if (btn == null) continue;
            btn.IsEnabled = enabled;
            btn.Opacity = enabled ? 1.0 : 0.5;
        }
    }

    private void UpdateDuckingCompareButton()
    {
    }

    private double GetPreviewVideoBalance() =>
        RenderedMixLive ? 0.0 : Math.Clamp((VideoVolSliderCtl?.Value ?? 100.0) / 100.0, 0.0, 1.0);

    private double GetPreviewMusicBalance() =>
        RenderedMixLive ? 1.0 : Math.Clamp((MusicVolSliderCtl?.Value ?? 100.0) / 100.0 * CurrentPreviewFadeFactor() * CurrentCrossfadeGain(), 0.0, 1.0);

    private void ApplyPreviewMusicVolume()
    {
        if (_audioIpcClient == null) return;
        _ = _audioIpcClient.ApplyPreviewGainAsync(GetPreviewMusicBalance());
    }
    private void UpdateProblemFlags()
    {
        var panel = this.FindControl<Border>("ProblemFlagsPanel");
        var text = this.FindControl<TextBlock>("ProblemFlagsText");
        if (panel == null || text == null) return;

        var flags = new System.Collections.Generic.List<string>();
        if (_playlist.Count == 0)
        {
            panel.IsVisible = false;
            text.Text = "";
            return;
        }

        foreach (var e in _playlist)
        {
            if (string.IsNullOrWhiteSpace(e.Path) || !File.Exists(e.Path))
                flags.Add($"Song file is missing: {Path.GetFileName(e.Path)}.");
        }

        double videoVolume = VideoVolSliderCtl?.Value ?? 100.0;
        double musicVolume = MusicVolSliderCtl?.Value ?? 100.0;
        if (musicVolume <= 1.0)
            flags.Add("The music volume is at zero — you will not hear it.");
        if (videoVolume <= 1.0)
            flags.Add("The video's own sound is at zero.");

        bool duckingEnabled = DuckingCheckBoxCtl?.IsChecked ?? true;
        if (!duckingEnabled && videoVolume >= 70.0 && musicVolume >= 80.0)
            flags.Add("Game sound protection is off while both the music and the game are loud (see ⚙ Sound options).");

        if (_isMergerMode && _mergerVideos != null && _phase3ClipDurationsSec.Count > 0 &&
            _phase3ClipDurationsSec.Count != _mergerVideos.Count)
        {
            flags.Add("Could not read the length of every clip; the clip lines may be slightly off.");
        }

        panel.IsVisible = _currentStep == StepFit && flags.Count > 0;
        text.Text = string.Join(Environment.NewLine, flags.Select(flag => $"⚠ {flag}"));
    }

    private void SetPhase3Status(string message)
    {
        var status = this.FindControl<TextBlock>("Phase3StatusLabel");
        if (status == null) return;
        status.Text = message;
        status.IsVisible = !string.IsNullOrWhiteSpace(message);
    }
    private void TogglePreview()
    {
        if (_currentStep == StepChoose)
        {
            var row = _highlightedTrack ?? _rowPreviewTrack;
            if (_isPreviewPlaying) StopPreview();
            else if (row != null)
            {
                _rowPreviewTrack = row;
                row.IsPreviewing = true;
                StartPreviewInternal(_previewCurrentOffset >= row.DurationSec ? 0 : _previewCurrentOffset);
            }
            return;
        }

        if (Vm.IsPartView)
        {
            TogglePartPreview();
            return;
        }

        if (_isPreviewPlaying)
        {
            StopPreview();
            return;
        }

        if (_playlist.Count == 0)
        {
            ShowToast("⚠ Add a song first!");
            return;
        }

        if (!_phase3Ready)
        {
            ShowToast("The preview is still loading.");
            return;
        }

        StartPreviewInternal(_previewCurrentOffset);
    }

    private void SkipPreview(double offsetSeconds)
    {
        if (_currentStep == StepChoose)
        {
            if (_rowPreviewTrack == null || _rowPreviewTrack.DurationSec <= 0) return;
            bool playing = _isPreviewPlaying;
            StopPreview();
            _previewCurrentOffset = Math.Clamp(_previewCurrentOffset + offsetSeconds, 0, _rowPreviewTrack.DurationSec);
            if (playing) StartPreviewInternal(_previewCurrentOffset);
            return;
        }
        if (_currentStep != StepFit || !_phase3Ready || _playlist.Count == 0) return;
        if (Vm.IsPartView)
        {
            SeekPart(CurrentPartListeningSec() + offsetSeconds);
            return;
        }
        bool wasPlaying = _isPreviewPlaying;
        StopPreview();
        double videoRelative = Math.Clamp(GetCurrentPhase3VideoRelativeSeconds() + offsetSeconds, 0, GetPhase3VideoDurationSeconds());
        _previewCurrentOffset = _songStartSeconds + videoRelative;
        SeekPhase3VideoHost(videoRelative, forcePause: !wasPlaying);
        if (wasPlaying)
        {
            StartPreviewInternal(_previewCurrentOffset);
        }
        else
        {
            UpdatePlayhead();
        }
    }

    private async void StartPreviewInternal(double startOffset, bool fadeIn = false)

    {
        _previewFadeStartUtc = fadeIn ? DateTime.UtcNow : null;

        SetPlayButtonPlaying(true);

        _isPreviewPlaying = true;

        _previewCurrentOffset = startOffset;

        _previewStartTime = DateTime.UtcNow;
        _phase3PreviewClockStartTime = null;

        if (_currentStep == 3)
        {
            double outputRelativeSec = Math.Clamp(startOffset - _songStartSeconds, 0, GetPhase3VideoDurationSeconds());
            _phase3PreviewClockStartOffsetSec = outputRelativeSec;
            _phase3PreviewClockStartTime = DateTime.UtcNow;
            SeekPhase3VideoHost(outputRelativeSec, forcePause: false);
            SyncPhase3VideoPreviewClock();
        }

        try

        {
            if (_currentStep == StepChoose)
            {
                await PlayChosenSongAsync(_rowPreviewTrack, startOffset);
                return;
            }
            await EnsureAudioPreviewClientAsync();
            var audioClient = _audioIpcClient;
            if (audioClient == null)
                return;

            if (_currentStep == StepFit)
            {
                await SyncPhase3MusicPreviewTrackAsync(forceReload: false);
                return;
            }

            var previewTrack = _rowPreviewTrack;
            if (previewTrack == null || !_isPreviewPlaying) return;
            double clampLimit = previewTrack.DurationSec;
            double audioStartOffset = clampLimit > 0 ? Math.Clamp(startOffset, 0, clampLimit) : Math.Max(0, startOffset);
            string targetPath = previewTrack.FilePath.Replace("\\", "/");
            if (_lastLoadedTrackPath != targetPath)
            {
                await audioClient.SetPropertyAsync("start", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await audioClient.SendCommandAsync("loadfile", targetPath, "replace");
                _lastLoadedTrackPath = targetPath;
            }
            else
            {
                await audioClient.SendCommandAsync("seek", audioStartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            }

            await audioClient.ApplyPreviewGainAsync(GetPreviewMusicBalance());
            ApplyPreviewMusicFilters();
            await audioClient.SetPropertyAsync("pause", "no");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Preview failed: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            ShowToast($"⚠ Preview playback failed: {ex.Message}");
        }
    }

    private void StopPreview()
    {
        ++_songPreviewVersion;
        ++_partAuditionVersion;
        _partAuditionActive = false;
        if (_previewStartTime.HasValue)
        {
            if (_currentStep == 3)
            {
                _previewCurrentOffset = _songStartSeconds + GetCurrentPhase3VideoRelativeSeconds();
                _phase3PreviewClockStartTime = null;
            }
            else
            {
                if (_songPreviewReady)
                    _previewCurrentOffset += (DateTime.UtcNow - _previewStartTime.Value).TotalSeconds;
            }

            if (_rowPreviewTrack != null && _currentStep != StepFit && _rowPreviewTrack.DurationSec > 0 && _previewCurrentOffset > _rowPreviewTrack.DurationSec)
                _previewCurrentOffset = _rowPreviewTrack.DurationSec;
            _previewStartTime = null;
        }
        if (_rowPreviewTrack != null) _rowPreviewTrack.IsPreviewing = false;
        _isPreviewPlaying = false;
        _songPreviewReady = false;
        StopPhase3FreezeSound();
        _lastAppliedCrossfadeGain = 1.0;
        _previewFadeStartUtc = null;
        _phase3PreviewMusicPath = null;
        _phase3PreviewMusicSegmentStartSec = double.NaN;

        if (_audioIpcClient != null)

        {
            _ = _audioIpcClient.SetPropertyAsync("pause", "yes");
        }

        if (_currentStep == 3)

        {
            var wizardVideoHost = WizardVideoHost;

            if (wizardVideoHost?.IpcClient != null)

                _ = wizardVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }

        SetPlayButtonPlaying(false);
    }

    private bool _waveformRenderInFlight;

    private async Task RenderWaveformAsync(string? filePath)

    {
        if (string.IsNullOrEmpty(filePath)) return;
        _waveformRenderInFlight = true;
        try { await RenderWaveformCoreAsync(filePath); }
        finally { _waveformRenderInFlight = false; }
    }
    private async Task RenderWaveformCoreAsync(string filePath)
    {
        int renderVersion = System.Threading.Interlocked.Increment(ref _waveformRenderVersion);
        string requestedPath = filePath;
        var waveformImage = this.FindControl<Image>("WaveformImage");
        var loadingText = this.FindControl<TextBlock>("WaveformLoadingText");
        if (waveformImage == null || loadingText == null) return;

        (waveformImage.Source as IDisposable)?.Dispose();
        waveformImage.Source = null;

        if (!_songWaveCache.TryGetValue(filePath, out string? pngFile) || !File.Exists(pngFile))
        {
            loadingText.IsVisible = true;
            loadingText.Text = "Drawing the song...";
            var ffmpegPath = ResolveFfmpegPath();
            pngFile = await FreeVideoStudio.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(ffmpegPath, filePath);
            if (pngFile != null && File.Exists(pngFile)) _songWaveCache[filePath] = pngFile;
        }

        if (renderVersion != _waveformRenderVersion ||
            !string.Equals(SelectedSongPath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        loadingText.IsVisible = false;
        if (pngFile != null && File.Exists(pngFile))
        {
            try
            {
                using var fs = File.OpenRead(pngFile);
                waveformImage.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                _lastWaveformFile = pngFile;
                DrawSongStrip();
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("MUSIC_WIZARD", "Failed to load waveform: " + ex.Message);
            }
        }
        else
        {
            loadingText.Text = "Could not draw this song.";
            loadingText.IsVisible = true;
        }
    }
    private void DrawPhase3TimelineScale()
    {
        var lanes = Phase3LanesCtl;
        double canvasWidth = lanes?.Bounds.Width ?? 0;
        if (canvasWidth <= 0) return;
        DrawPhase3MergerOverlays(canvasWidth);
        DrawSongLane();
    }

    private void DrawPhase3MergerOverlays(double canvasWidth)
    {
        var thumbCanvas = this.FindControl<Canvas>("ThumbnailOverlayCanvas");
        if (thumbCanvas == null) return;
        thumbCanvas.Children.Clear();
        if (!_isMergerMode || _mergerVideos == null || _mergerVideos.Count < 1 || _phase3ClipDurationsSec.Count < 1) return;

        double laneWidth = thumbCanvas.Bounds.Width > 0 ? thumbCanvas.Bounds.Width : canvasWidth;
        double totalClipDuration = _phase3ClipDurationsSec.Sum();
        if (totalClipDuration <= 0.01) return;

        double cursor = 0;
        for (int i = 0; i < _phase3ClipDurationsSec.Count; i++)
        {
            double x0 = Math.Clamp(cursor / totalClipDuration * laneWidth, 0, laneWidth);
            cursor += _phase3ClipDurationsSec[i];
            double x1 = Math.Clamp(cursor / totalClipDuration * laneWidth, 0, laneWidth);
            if (i > 0) AddLaneBoundary(thumbCanvas, x0, 60, Avalonia.Media.Brushes.White, 0.85);
            if (x1 - x0 < 24) continue;

            string name = i < _mergerVideos.Count ? Path.GetFileNameWithoutExtension(_mergerVideos[i]) : "";
            var label = new Border
            {
                Background = Infrastructure.ThemeResources.Brush(this, "AppScrimBrush", new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(170, 15, 23, 42))),
                CornerRadius = new Avalonia.CornerRadius(3),
                Padding = new Avalonia.Thickness(4, 0),
                MaxWidth = Math.Max(20, x1 - x0 - 8),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = x1 - x0 > 90 ? $"{i + 1} · {name}" : $"{i + 1}",
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                    Foreground = Avalonia.Media.Brushes.White,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                },
            };
            Canvas.SetLeft(label, x0 + 4);
            Canvas.SetTop(label, 2);
            thumbCanvas.Children.Add(label);
        }
    }

    private void UpdatePlayhead()
    {
        var canvas = this.FindControl<Canvas>("WaveformCanvas");
        if (canvas != null)
        {
            double x = -10;
            var entry = SelectedEntry;
            if (entry != null && entry.FullSec > 0 && _currentStep == StepFit && Vm.IsPartView)
                x = Math.Clamp(CurrentPartListeningSec() / entry.FullSec, 0, 1) * canvas.Bounds.Width;
            EnsurePlayheadLine(
                canvas,
                ref _waveformPlayheadLine,
                Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Avalonia.Media.Brushes.Red),
                dashed: false);
            _waveformPlayheadLine!.StartPoint = new Avalonia.Point(x, 0);
            _waveformPlayheadLine.EndPoint = new Avalonia.Point(x, canvas.Bounds.Height);
        }

        if (_currentStep == StepFit)
        {
            var p3Lanes = Phase3LanesCtl;
            if (p3Lanes != null)
            {
                double axis = LaneAxisSec();
                if (Math.Abs(p3Lanes.DurationSeconds - axis) > 0.001)
                    p3Lanes.DurationSeconds = axis;
                p3Lanes.PositionSeconds = PartLanesActive ? PartTimelineSec() : GetCurrentPhase3VideoRelativeSeconds();
            }
            Vm.PastVideoEnd = PartLanesActive && _editIndex >= 0 && PartTimelineSec() > GetPhase3VideoDurationSeconds() + 0.02;
        }
        else
        {
            Vm.PastVideoEnd = false;
        }
    }

    private async Task OnBackClickedAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            await OnStepBackAsync();
        }
        catch (Exception ex) { RuntimeLog.Fail("MUSIC_WIZARD", $"Back navigation failed: {ex.Message}"); }
    }
    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;

        var musicExts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".ogg" };
        var dropped = files.Where(f => musicExts.Contains(System.IO.Path.GetExtension(f.Name))).ToList();
        if (dropped.Count == 0)
        {
            ShowToast("Drop an MP3, WAV, M4A, AAC, or OGG file.");
            UpdateMusicEmptyState();
            return;
        }

        MusicTrackItem? first = null;
        foreach (var file in dropped)
        {
            string path = file.Path.LocalPath;
            var track = FindTrackByPath(path);
            if (track == null)
            {
                track = new MusicTrackItem
                {
                    Name = Path.GetFileName(path),
                    Title = Path.GetFileNameWithoutExtension(path),
                    FilePath = path,
                    DurationText = "Loading...",
                    SizeText = "",
                    LastModifiedTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0,
                    IsRecent = _recentMusicPaths.Contains(path)
                };
                _allTracks.Add(track);
                _ = ProbeTrackInfoAsync(track);
            }
            first ??= track;
            RuntimeLog.Debug("MUSIC_WIZARD", $"Dropped music path: {path}");
        }

        var searchBox = this.FindControl<TextBox>("MusicSearchBox");
        if (searchBox != null) searchBox.Text = string.Empty;
        ApplyTrackFilterAndSort();
        var listbox = MusicListBoxCtl;
        if (listbox != null && first != null) listbox.SelectedItem = first;
        RuntimeLog.Info("MUSIC_WIZARD", $"{dropped.Count} music file(s) dropped.");
        ShowToastSuccess(dropped.Count == 1 ? "✔ Song added to the list!" : $"✔ {dropped.Count} songs added to the list!");
        UpdateMusicEmptyState();
    }

    private Button? _backBtn;
    private bool _discardConfirmed;
    private bool _askingDiscard;

    protected override async void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (!_isSafeToClose && !_discardConfirmed && Result == null && _playlist.Count > 0
            && e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            if (_askingDiscard) return;
            _askingDiscard = true;
            try
            {
                StopPreview();
                bool discard = await Controls.ConfirmDialogWindow.AskAsync(this,
                    "The music you picked will not be added to your video.", "Discard changes?", "DISCARD", "KEEP EDITING", destructive: true);
                RuntimeLog.Info("UI", discard ? "User discarded the Music Wizard changes." : "User kept editing in the Music Wizard.");
                if (discard)
                {
                    _discardConfirmed = true;
                    Close();
                }
            }
            finally { _askingDiscard = false; }
            return;
        }
        CancelAiAutoFit();
        CancelAudioAnalysis();
        CancelMusicScan();
        CancelPhase3Load();
        StopPreview();
        ClearPhase3LiveZoomCrop();
        await DisposePhase3VideoHostAsync();

        if (_playheadTimer != null) { _playheadTimer.Stop(); _playheadTimer.Tick -= PlayheadTimer_Tick; _playheadTimer = null; }

        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        this.Hide();

        FreeVideoStudio.App.WindowBoundsHelper.SaveBoundsOnClose(this, _bounds.Key);
        _isSafeToClose = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        _freezeSound.Dispose();
        CancelAiAutoFit();
        CancelSilenceAnalysis();
        DeleteSongWaveCache();

        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        if (_playheadTimer != null) { _playheadTimer.Stop(); _playheadTimer.Tick -= PlayheadTimer_Tick; _playheadTimer = null; }
        FreeVideoStudio.Core.Media.MpvIpcClient.GlobalMasterVolumeChanged -= OnGlobalMasterVolumeChanged;
        CancelAudioAnalysis();
        CancelMusicScan();
        if (_lastWaveformFile != null && File.Exists(_lastWaveformFile))
        {
            try { File.Delete(_lastWaveformFile); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }
        foreach (var f in _lastPhase3ThumbFiles) { try { if (File.Exists(f)) File.Delete(f); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); } }
        _lastPhase3ThumbFiles.Clear();
        DeleteTempFile(ref _lastPhase3WaveFile);
        StopPreview();

        if (_audioIpcClient != null)

        {
            try { _audioIpcClient.Dispose(); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }

            _audioIpcClient = null;
        }

        _voiceOverPlayer.Dispose();
        DisposeSoundPreview();
        base.OnClosed(e);
    }

    private async Task RunSongDownloadAsync()
    {
        string targetDir = ResolveCurrentMusicDirectory();

        var confirm = new Controls.ConfirmDialogWindow();
        confirm.SetTitle("Download more songs?");
        confirm.SetMessage(
            "This will connect to the internet and download background music tracks from the " +
            "official library." + Environment.NewLine + Environment.NewLine +
            "Saving into: " + targetDir + Environment.NewLine + Environment.NewLine +
            "Continue?");
        confirm.SetButtonText("DOWNLOAD", "CANCEL");
        await confirm.ShowDialog(this);
        if (!confirm.Result) return;

        var (count, error) = await Controls.CloudSyncProgressWindow.RunAsync(
            this, "Downloading songs",
            (progress, ct) => MemeCatalog.SyncSongsFromCloudAsync(targetDir, progress, ct));

        _ = ScanDirectoryForMusicAsync(targetDir);

        if (error != null)
        {
            await ErrorReporter.ShowAsync(this, "Song download problem", error,
                "See the log entries tagged [Songs] for the per-file detail.");
            return;
        }

        RuntimeLog.Info("Songs", $"Song download finished: {count} new track(s).");
    }

    private void ShowToast(string message)
        => Controls.FloatingNotice.Show(this, message);

    private void ShowToastSuccess(string message)
        => Controls.FloatingNotice.Success(this, message);

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar != null)
        {
            titleBar.IsHitTestVisible = true;
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
}
