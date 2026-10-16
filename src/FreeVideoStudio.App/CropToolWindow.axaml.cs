// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IOPath = System.IO.Path;
using SourceRect = FreeVideoStudio.Core.Editing.CropSourceRect;
using HudRole = FreeVideoStudio.Core.Editing.CropHudRole;

using static FreeVideoStudio.App.Infrastructure.ColorMath;
using static FreeVideoStudio.App.Infrastructure.CropGeometry;

namespace FreeVideoStudio.App;

public partial class CropToolWindow : Window, System.ComponentModel.INotifyDataErrorInfo, Services.IToolNavigationResultSource
{
    public static readonly StyledProperty<string> RoleNameProperty =
        AvaloniaProperty.Register<CropToolWindow, string>(nameof(RoleName), defaultValue: "");

    /// <summary>
    /// ⚠️ NODUPES_02 — DISPLAY ONLY. NEVER DERIVE A ROLE KEY FROM THIS.
    ///
    /// This holds the LABEL of whatever element is currently being placed, for display. It is not
    /// an identity and it cannot be turned back into one: not one of the five built-in elements has
    /// a display name that maps back to its own key, so
    /// <c>RoleName.ToLowerInvariant().Replace(" ", "_")</c> is wrong six times out of six. Code
    /// that did exactly that is what produced duplicate same-named layers in the composer and wrote
    /// crops under keys the exporter does not draw — see <see cref="ConfirmSelectionAsAsync"/> for
    /// the full table.
    ///
    /// A role's identity travels as a <see cref="HudRole"/>. If you need the key, take the role.
    /// </summary>
    public string RoleName
    {
        get => GetValue(RoleNameProperty);
        set => SetValue(RoleNameProperty, value);
    }

    public static readonly StyledProperty<string> NewMaskOverlayNameProperty =
        AvaloniaProperty.Register<CropToolWindow, string>(nameof(NewMaskOverlayName), defaultValue: "");

    public string NewMaskOverlayName
    {
        get => GetValue(NewMaskOverlayNameProperty);
        set => SetValue(NewMaskOverlayNameProperty, value);
    }

    public event System.EventHandler<System.ComponentModel.DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <summary>
    /// ISSUE_01 (audit round 6) — THE PROFILE-NAME BOX NOW ACTUALLY VALIDATES.
    ///
    /// What was here before answered for <see cref="RoleName"/> and nothing else. RoleName is a
    /// leftover: `grep RoleName CropToolWindow.axaml` returns zero hits, because ISSUE_04 deleted
    /// the RoleTextBox that used to bind it. Meanwhile the one TextBox in this window that IS
    /// wrapped in a <c>DataValidationErrors</c> host — NewMaskOverlayTextBox — binds
    /// <see cref="NewMaskOverlayName"/>, and <c>GetErrors("NewMaskOverlayName")</c> fell straight
    /// through to <c>yield break</c>. So Avalonia's binding plugin never saw an error, the
    /// <c>TextBox:error</c> pseudo-class (AvaloniaApp.axaml, ISSUE_09) never fired, the error host
    /// never rendered a message, and a user typing a blank or already-taken profile name got
    /// nothing back but a SAVE AS NEW button that stayed grey for no stated reason.
    ///
    /// The rules below are the SAME rules <see cref="RefreshCreateMaskOverlayButton"/> uses to
    /// decide whether that button lights up — deliberately one predicate, read from one place, so
    /// the message on screen can never disagree with the button beside it.
    /// </summary>
    public bool HasErrors => ValidateNewMaskOverlayName() != null;

    public System.Collections.IEnumerable GetErrors(string? propertyName)
    {
        if (propertyName is null or "" or nameof(NewMaskOverlayName))
        {
            string? error = ValidateNewMaskOverlayName();
            if (error != null) yield return error;
        }
    }

    /// <summary>
    /// The single source of truth for "is this a usable new profile name" (EDITSTATE_01 — the rule
    /// is the session's, NOMASK_01 included). Null when fine, else the message the user reads.
    /// </summary>
    private string? ValidateNewMaskOverlayName() => _edit.ValidateNewProfileName(NewMaskOverlayName);

    /// <summary>
    /// ISSUE_01 — keeps SAVE AS NEW and the inline validation message telling the same story.
    ///
    /// The button lights up only when all four things are true at once: the profile gate is open,
    /// a profile is actually selected (there is nothing to copy otherwise — see the guard in the
    /// click handler), the box is not empty, and <see cref="ValidateNewMaskOverlayName"/> is happy.
    /// Safe to call before InitializeComponent has run: SetEnabled no-ops on a missing control, and
    /// styled-property defaults fire OnPropertyChanged during construction.
    /// </summary>
    private void RefreshCreateMaskOverlayButton()
    {
        bool hasText = !string.IsNullOrWhiteSpace(NewMaskOverlayName);
        SetEnabled("CreateMaskOverlayBtn",
            _edit.GateUnlocked && _edit.ActiveProfile != null && hasText && ValidateNewMaskOverlayName() == null);
    }

    /// <summary>
    /// ISSUE_01 — refreshes the "already taken" set the validator checks against. Called wherever
    /// the profile list is read or rewritten, so a name created in this session starts colliding
    /// immediately rather than after the next window open.
    /// </summary>
    private void RefreshMaskOverlayNameCache(IEnumerable<string> profiles)
    {
        _edit.RefreshExistingProfileNames(profiles);

        ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(nameof(NewMaskOverlayName)));
        RefreshCreateMaskOverlayButton();
    }


    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RoleNameProperty)
        {
            ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(nameof(RoleName)));
        }

        if (change.Property == NewMaskOverlayNameProperty)
        {
            ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(nameof(NewMaskOverlayName)));
            RefreshCreateMaskOverlayButton();
        }
    }
    private const double PortraitWidth = CoordinateConstants.PortraitW;
    private const double PortraitHeight = CoordinateConstants.PortraitH;
    private const double ContentTop = CoordinateConstants.UIPaddingTop;
    private const double ContentBottom = CoordinateConstants.PortraitH - CoordinateConstants.UIPaddingBottom;
    private const double MinSelectionSize = 10;
    private const double MinItemSize = CropEditSession.MinItemSize;
    private const double HandleSize = 24;

    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private readonly FreeVideoStudio.Core.Infrastructure.RecoveryManager _recovery = new FreeVideoStudio.Core.Infrastructure.RecoveryManager(ApplicationPaths.CreateDefault());
    private readonly string? _initialVideoPath;
    /// <summary>
    /// EDITSTATE_01 — THE EDIT ITSELF: placed layers (source rect, layout, z), the selection by role
    /// KEY, tombstones, the active profile and its name rules, the element catalogue, the capture
    /// resolution, dirty and the UNDO_27 history (Core/Editing/CropEditSession). This window keeps
    /// the view: canvases, element visuals, adorners, handles, hit testing, pointer capture, the
    /// frozen-frame zoom, and the Magic Wand's transient scan and candidates.
    /// </summary>
    private readonly CropEditSession _edit = new(Infrastructure.MaskOverlayManager.IsNoMask);

    private readonly ObservableCollection<LayerEntry> _layers = new();

    /// <summary>The VISUALS of the placed layers, one per <see cref="CropEditSession.Layers"/> entry.</summary>
    private readonly List<CropEditorItem> _items = new();
    private readonly List<Control> _candidateControls = new();
    private readonly List<Control> _guideControls = new();
    private readonly List<string> _tempFiles = new();

    private MpvVideoView? _videoHost;
    private Canvas? _sourceCanvas;
    private Canvas? _portraitCanvas;
    private Image? _snapshotImage;
    private Image? _composerBackgroundImage;
    private ListBox? _layerList;
    private Slider? _timelineSlider;
    private TextBlock? _currentTimeLabel;
    private TextBlock? _totalTimeLabel;
    private TextBlock? _statusLabel;
    private TextBlock? _goalLabel;
    private Canvas? _timelineCanvas;
    private TextBlock? _selectionInfo;

    private ScrollViewer? _snapshotScroll;
    private LayoutTransformControl? _snapshotZoomHost;
    private TextBlock? _zoomLabel;

    private Border? _profileGate;

    /// <summary>ISSUE_07 — the composer's "nothing placed yet" panel and its two lines of copy.</summary>
    private Border? _composerEmptyState;
    private TextBlock? _composerEmptyStateTitle;
    private TextBlock? _composerEmptyStateBody;

    /// <summary>ISSUE_08 — the dimming scrim behind the HUD-element chooser.</summary>
    private Border? _rolePopupScrim;
    private TextBlock? _profileStateLabel;

    private Rectangle? _selectionRect;
    private SourceRect? _sourceSelection;
    private Point _sourceSelectionStart;
    private bool _isDrawingSourceSelection;

    private CropEditorItem? _activeEditItem;
    private ComposerEditMode _composerEditMode = ComposerEditMode.None;
    private Point _editPointerStart;
    private double _editStartX;
    private double _editStartY;
    private double _editStartWidth;
    private double _editStartHeight;

    /// <summary>
    /// RESIZEFEEL_01 — height ÷ width of the SOURCE crop, captured once when a resize gesture
    /// starts and held for the whole gesture.
    ///
    /// Deliberately the SOURCE rectangle's ratio, not the placed item's. The placed size has been
    /// through QuantizeItemSize, whose two axes are rounded independently, so the placed ratio is
    /// always a slightly wrong copy of the real one — and locking a resize to it means every
    /// resize starts from the last one's rounding error instead of from the truth.
    /// </summary>
    private double _editSourceAspect = 1.0;
    private CropLayoutSnapshot? _editStartSnapshot;

    private string? _videoPath;
    private string? _snapshotPath;

    private double _durationMs;
    private bool _isTimerUpdatingSlider;
    private bool _isSeeking;
    private double? _nextSeekTarget;
    private Avalonia.Threading.DispatcherTimer? _playheadBadgeTimer;
    private bool _isMpvStarted;
    private bool _suppressLayerSelection;
    private bool _isSafeToClose;
    private bool _changingProfile;
    private bool _isSaving;
    private string? _verifiedEditingProfile;
    private bool _unsavedPromptOpen;
    private bool _returningToMainApp;
    private bool _closeInProgress;
    private DispatcherTimer? _timelineTimer;


    /// <summary>GATE_01 — the initial video is held until a profile exists to load it against.</summary>
    private string? _pendingInitialVideoPath;

    private bool _snapshotFitMode = true;
    private double _snapshotZoomFactor = 1.0;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 4.0;

    /// <summary>
    /// Set once the user drives the zoom themselves (the wheel, or any zoom button other than
    /// FIT); FIT clears it.
    ///
    /// AUTOZOOM_02 — this is NO LONGER the auto-zoom gate. It used to be, and it was the wrong
    /// question: it asks about HISTORY ("has the zoom been touched") when what matters is the
    /// current state ("am I looking at the whole frame"). See AutoZoomToSelection for how that
    /// went wrong in both directions. Its only remaining job is fidelity for CANCELSEL_01 — Escape
    /// has to put the flag back exactly as it found it, or a cancel would silently change whether
    /// the view counts as user-framed.
    /// </summary>
    private bool _userZoomed;


    /// <summary>EDITSTATE_01 — the selected element's visuals, resolved from the session's selection BY KEY.</summary>
    private CropEditorItem? SelectedView
    {
        get => _edit.Selected is { } layer ? _items.FirstOrDefault(i => ReferenceEquals(i.Model, layer)) : null;
        set => _edit.SelectedRoleKey = value?.RoleKey;
    }

    public CropToolWindow() : this((string?)null)
    {
    }

    public CropToolWindow(string? initialVideoPath)
    {
        _initialVideoPath = string.IsNullOrWhiteSpace(initialVideoPath) ? null : initialVideoPath;

        InitializeComponent();

        Controls.WindowResizeGrip.Attach(this, "Drag to resize the Crop Tools");
        _recovery.AcquireLock();
        FreeVideoStudio.App.WindowBoundsHelper.Track(this, "CropToolBounds", fitDisplayOnFirstRun: true);
        FindControls();
        AttachTitleBarDrag();
        WireEvents();
        InitializeHistory();

        _pendingInitialVideoPath = _initialVideoPath;
        ApplyWizardChrome(cropping: false);
        if (_verifiedEditingProfile == null)
        {
            SetProfileGate(unlocked: false);
            SetWizardState(0, "Choose Profile", "Choose a profile from the picker on the left to start editing.");
        }

        Loaded += (_, _) => Controls.CoachOverlay.Register(this, Controls.CoachTours.CropToolKey, Controls.CoachTours.CropTool);

        Loaded += async (_, _) =>
        {
            await InitializeMpvAsync();
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void FindControls()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        _sourceCanvas = this.FindControl<Canvas>("SourceCanvas");
        _portraitCanvas = this.FindControl<Canvas>("PortraitCanvas");
        _snapshotImage = this.FindControl<Image>("SnapshotImage");
        _composerBackgroundImage = this.FindControl<Image>("ComposerBackgroundImage");
        _layerList = null;
        _timelineSlider = this.FindControl<Slider>("TimelineSlider");
        _currentTimeLabel = this.FindControl<TextBlock>("CurrentTimeLabel");
        _totalTimeLabel = this.FindControl<TextBlock>("TotalTimeLabel");
        _statusLabel = this.FindControl<TextBlock>("StatusLabel");
        _goalLabel = this.FindControl<TextBlock>("GoalLabel");
        _timelineCanvas = this.FindControl<Canvas>("CropTimelineScaleCanvas");
        _selectionInfo = this.FindControl<TextBlock>("SelectionInfo");
        _snapshotScroll = this.FindControl<ScrollViewer>("SnapshotScroll");
        _snapshotZoomHost = this.FindControl<LayoutTransformControl>("SnapshotZoomHost");
        _zoomLabel = this.FindControl<TextBlock>("ZoomLabel");
        _profileGate = null;
        _profileStateLabel = this.FindControl<TextBlock>("ProfileStateLabel");
        _composerEmptyState = this.FindControl<Border>("ComposerEmptyState");
        _composerEmptyStateTitle = this.FindControl<TextBlock>("ComposerEmptyStateTitle");
        _composerEmptyStateBody = this.FindControl<TextBlock>("ComposerEmptyStateBody");
        _rolePopupScrim = this.FindControl<Border>("RolePopupScrim");
        WireUpVolumeSlider();
    }

    private void WireEvents()
    {
        if (_rolePopupScrim != null)
        {
            _rolePopupScrim.PointerPressed += (_, e) =>
            {
                HideRolePopup();
                e.Handled = true;
            };
        }

        ApplyRolePopupShadow();
        ActualThemeVariantChanged += (_, _) => ApplyRolePopupShadow();

        if (_sourceCanvas != null)
        {
            _sourceCanvas.PointerPressed += SourceCanvas_PointerPressed;
            _sourceCanvas.PointerMoved += SourceCanvas_PointerMoved;
            _sourceCanvas.PointerExited += (_, _) => SetCrosshairVisible(false);
            _sourceCanvas.PointerReleased += SourceCanvas_PointerReleased;

            _sourceCanvas.PointerCaptureLost += (_, _) => EndSourceDrag();
        }

        if (_portraitCanvas != null)
        {
            _portraitCanvas.PointerPressed += (_, e) =>
            {
                if (ReferenceEquals(e.Source, _portraitCanvas))
                {
                    SelectItem(null);
                }
            };
        }

        if (_layerList != null)
        {
            _layerList.ItemsSource = _layers;
            _layerList.SelectionChanged += (_, _) =>
            {
                if (_suppressLayerSelection)
                {
                    return;
                }

                if (_layerList.SelectedItem is LayerEntry entry)
                {
                    SelectItem(_items.FirstOrDefault(i => i.RoleKey == entry.RoleKey), updateLayerList: false);
                }
            };
        }

        this.AddHandler(Avalonia.Input.DragDrop.DragOverEvent, OnVideoDragOver);
        this.AddHandler(Avalonia.Input.DragDrop.DropEvent, OnVideoDrop);

        ButtonClick("OpenVideoButton", async (_, _) => await OpenVideoAsync());
        ButtonClick("SnapshotButton", async (_, _) => await TakeSnapshotAsync());

        ButtonClick("WandCancelButton", (_, _) =>
        {
            RuntimeLog.Info("CROP", "Magic Wand cancelled by the user.");
            _wandStopRequested = true;
            CancelMagicWand();
        });

        // MAGICWAND_02 — the wand is wired to a real detector now, so the button is live again.
        // MAGICWAND_01 hid it because the old handler drew six boxes at hardcoded fractions of the
        // frame and called them detections; see RunMagicWandAsync for what replaced that.
        ButtonClick("MagicWandButton", async (_, _) => await RunMagicWandAsync());

        ButtonClick("ZoomFitButton", (_, _) => ApplySnapshotZoom(null));
        ButtonClick("ZoomActualButton", (_, _) => { ApplySnapshotZoom(1.0); RecenterOnSelection(); });
        ButtonClick("ZoomOutButton", (_, _) => { ApplySnapshotZoom(CurrentZoom() / 1.25); RecenterOnSelection(); });
        ButtonClick("ZoomInButton", (_, _) => { ApplySnapshotZoom(CurrentZoom() * 1.25); RecenterOnSelection(); });

        if (_snapshotScroll != null)
        {
            _snapshotScroll.SizeChanged += (_, _) =>
            {
                if (_snapshotFitMode && _snapshotPath != null) ApplySnapshotZoom(null);
            };

            _snapshotScroll.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnSnapshotWheel,
                RoutingStrategies.Tunnel,
                handledEventsToo: false);

            AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnWindowWheelDuringDrag,
                RoutingStrategies.Tunnel,
                handledEventsToo: false);
        }
        ButtonClick("PlayPauseButton", async (_, _) => await TogglePlayPauseAsync());
        ButtonClick("DeleteMenuButton", (_, _) =>
        {
            if (!FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ConfirmCropToolDelete)
            {
                DeleteSelectedItem();
            }
            else
            {
                var btn = DeleteMenuButtonCtl;
                var pnl = DeleteConfirmPanelCtl;
                if (btn != null && pnl != null) { btn.IsVisible = false; pnl.IsVisible = true; }
            }
        });
        
        ButtonClick("ConfirmDeleteButton", (_, _) =>
        {
            DeleteSelectedItem();
            var btn = DeleteMenuButtonCtl;
            var pnl = DeleteConfirmPanelCtl;
            if (btn != null && pnl != null) { btn.IsVisible = true; pnl.IsVisible = false; }
        });

        ButtonClick("CancelDeleteButton", (_, _) =>
        {
            var btn = DeleteMenuButtonCtl;
            var pnl = DeleteConfirmPanelCtl;
            if (btn != null && pnl != null) { btn.IsVisible = true; pnl.IsVisible = false; }
        });
        
        ButtonClick("UndoButton", (_, _) => Undo());
        ButtonClick("RedoButton", (_, _) => Redo());
        ButtonClick("RaiseButton", (_, _) => MoveSelectedLayer(1));
        ButtonClick("LowerButton", (_, _) => MoveSelectedLayer(-1));
        
        ButtonClick("CropToolHelpButton", (_, _) => Controls.CoachOverlay.Replay(this));

        ButtonClick("RolePopupNewOk", async (_, _) => await CommitNewRoleAsync());
        var newNameBox = RolePopupNewNameCtl;
        if (newNameBox != null)
        {
            newNameBox.KeyDown += async (_, ke) =>
            {
                if (ke.Key is Key.Enter or Key.Return)
                {
                    ke.Handled = true;
                    await CommitNewRoleAsync();
                }
            };
        }

        ButtonClick("ResetMenuButton", (_, _) =>
        {
            if (!FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ConfirmCropToolReset)
            {
                ResetWorkingState(tombstonePlacedElements: true);
            }
            else
            {
                var btn = ResetMenuButtonCtl;
                var pnl = ResetConfirmPanelCtl;
                if (btn != null && pnl != null) { btn.IsVisible = false; pnl.IsVisible = true; }
            }
        });
        
        ButtonClick("ConfirmResetButton", (_, _) =>
        {
            ResetWorkingState(tombstonePlacedElements: true);
            var btn = ResetMenuButtonCtl;
            var pnl = ResetConfirmPanelCtl;
            if (btn != null && pnl != null) { btn.IsVisible = true; pnl.IsVisible = false; }
        });

        ButtonClick("CancelResetButton", (_, _) =>
        {
            var btn = ResetMenuButtonCtl;
            var pnl = ResetConfirmPanelCtl;
            if (btn != null && pnl != null) { btn.IsVisible = true; pnl.IsVisible = false; }
        });
        ButtonClick("ReturnButton", async (_, _) => await ReturnToMainAppAsync());
        ButtonClick("SaveButton", async (button, _) => await SaveAndReturnAsync(button));
        BuildMaskOverlayUi();


        if (_timelineSlider != null)
        {
            _timelineSlider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && e.NewValue is double newValue && !_isTimerUpdatingSlider)
                {
                    double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
                    if (duration > 0 && _videoHost?.IpcClient != null)
                    {
                        double targetTime = (newValue / 100.0) * duration;
                        ShowPlayheadBadge(targetTime, newValue);
                    }
                }
            };
            
            _timelineSlider.PointerReleased += (s, e) =>
            {
                double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
                if (duration > 0 && _videoHost?.IpcClient != null)
                {
                    double targetTime = (_timelineSlider.Value / 100.0) * duration;
                    _ = SeekInternal(targetTime);
                }
            };
        }

        var timelinePanel = this.FindControl<Border>("TimelinePanel");
        var timelineCanvas = this.FindControl<Canvas>("CropTimelineMarkersCanvas");

        Controls.TimelineKnob.Attach(timelineCanvas, _timelineSlider);

        if (timelinePanel != null && timelineCanvas != null && _timelineSlider != null)
        {
            bool isScrubbingTimeline = false;
            timelinePanel.PointerPressed += (s, e) => {
                if (e.GetCurrentPoint(timelinePanel).Properties.IsLeftButtonPressed) {
                    isScrubbingTimeline = true;
                    e.Pointer.Capture(timelinePanel);
                    SeekTimelineFromPointer(e, timelineCanvas, _timelineSlider);
                }
            };
            timelinePanel.PointerMoved += (s, e) => {
                if (isScrubbingTimeline && e.GetCurrentPoint(timelinePanel).Properties.IsLeftButtonPressed) {
                    SeekTimelineFromPointer(e, timelineCanvas, _timelineSlider);
                }
            };
            timelinePanel.PointerReleased += (s, e) => {
                isScrubbingTimeline = false;
                e.Pointer.Capture(null);
            };
        }

        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timelineTimer.Tick += (_, _) => UpdateTimelineUi();
        _timelineTimer.Start();
    }

    private void BuildMaskOverlayUi()
    {
        var combo = this.FindControl<ComboBox>("CropToolMaskOverlayCombo");
        if (combo != null)
        {
            var allProfiles = FreeVideoStudio.App.Infrastructure.MaskOverlayManager.GetAvailableProfiles();
            var profiles = allProfiles
                .Where(p => !FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(p))
                .ToList();

            if (profiles.Count != allProfiles.Count)
            {
                RuntimeLog.Debug("CROP", $"'{FreeVideoStudio.App.Infrastructure.MaskOverlayManager.NoMaskProfileName}' withheld from the profile picker (NOMASK_02).");
            }

            combo.ItemsSource = profiles;

            RefreshMaskOverlayNameCache(allProfiles);

            // Reopen last deliberate editing choice only if still valid (Requirement 4)
            string lastEdited = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.LastEditedCropProfile;
            string? validInitial = null;
            if (!string.IsNullOrWhiteSpace(lastEdited) &&
                !FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(lastEdited))
            {
                string? matching = profiles.FirstOrDefault(p => string.Equals(p, lastEdited.Trim(), StringComparison.OrdinalIgnoreCase));
                if (matching != null)
                {
                    var testLoad = FreeVideoStudio.App.Infrastructure.MaskOverlayManager.LoadProfile(matching);
                    if (testLoad.Success)
                    {
                        validInitial = testLoad.ProfileName;
                    }
                }
            }

            if (validInitial != null)
            {
                combo.SelectedItem = validInitial;
                _ = OnProfileChosenAsync(validInitial);
            }
            else
            {
                combo.SelectedItem = null;
                SetProfileGate(unlocked: false);
            }

            combo.SelectionChanged += async (s, e) =>
            {
                if (_changingProfile || _isSaving) return;
                if (combo.SelectedItem is not string selected) return;

                if (string.Equals(selected, _verifiedEditingProfile, StringComparison.OrdinalIgnoreCase)) return;

                if (FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(selected))
                {
                    RuntimeLog.Fail("CROP", "The reserved profile reached the Crop Tools picker — NOMASK_02's filter has been bypassed.");
                    combo.SelectedItem = _verifiedEditingProfile;
                    SetStatus($"\"{FreeVideoStudio.App.Infrastructure.MaskOverlayManager.NoMaskProfileName}\" is a reserved built-in profile and cannot be edited.");
                    return;
                }

                _changingProfile = true;
                try
                {
                    if (_returningToMainApp || _closeInProgress) return;
                    if (!await ConfirmUnsavedChangesAsync($"switching to \"{selected}\""))
                    {
                        combo.SelectedItem = _verifiedEditingProfile;
                        return;
                    }

                    await OnProfileChosenAsync(selected);
                    combo.SelectedItem = _verifiedEditingProfile;
                }
                finally { _changingProfile = false; }
            };
        }

        var btn = this.FindControl<Button>("CreateMaskOverlayBtn");
        var txt = this.FindControl<TextBox>("NewMaskOverlayTextBox");
        if (btn != null && txt != null)
        {
            btn.Click += async (s, e) =>
            {
                var newName = txt.Text?.Trim();
                if (string.IsNullOrWhiteSpace(newName)) return;

                if (ValidateNewMaskOverlayName() is { } nameError)
                {
                    SetStatus(nameError);
                    return;
                }

                if (_verifiedEditingProfile == null)
                {
                    SetStatus("Choose the profile you want to copy first.");
                    return;
                }

                var safeName = FreeVideoStudio.App.Infrastructure.MaskOverlayManager.SanitizeProfileName(newName);
                if (safeName == null)
                {
                    SetStatus("Invalid profile name. Avoid characters like \\ / : * ? \" < > |.");
                    return;
                }

                if (FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(safeName))
                {
                    SetStatus("\"" + FreeVideoStudio.App.Infrastructure.MaskOverlayManager.NoMaskProfileName +
                              "\" is a reserved built-in profile. Choose another name.");
                    return;
                }

                try
                {
                    FreeVideoStudio.App.Infrastructure.MaskOverlayManager.CreateNewProfile(safeName);
                    if (_items.Count > 0)
                    {
                        await SaveConfigAsync();
                    }

                    if (combo != null)
                    {
                        var updatedProfiles = FreeVideoStudio.App.Infrastructure.MaskOverlayManager.GetAvailableProfiles();
                        combo.ItemsSource = updatedProfiles
                            .Where(p => !FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(p))
                            .ToList();
                        RefreshMaskOverlayNameCache(updatedProfiles);
                        combo.SelectedItem = safeName;
                    }
                    txt.Text = "";
                    SetStatusSuccess("New overlay created: " + safeName);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("CROP", $"Create profile failed: {ex.Message}");
                    SetStatus("Could not create the profile. See runtime log.");
                }
            };
        }
    }

    /// <summary>
    /// GATE_01 (F3) - the one switch between "nothing chosen yet" and "editing a profile".
    ///
    /// WHY A GATE AND NOT JUST A DEFAULT. This window's save path is
    /// SaveConfigAsync -> CropConfigStore.SaveAsync (live crops_coordinations.conf)
    ///                 -> MaskOverlayManager.SyncActiveProfileFromCurrentConfig()
    /// and that last call copies the live config straight over
    /// SettingsManager.ActiveMaskOverlay's profile file. There is no undo on disk beyond the five
    /// rotating .bak files. So "which profile am I editing" is not a convenience - it decides
    /// which file gets overwritten, and it must never be answered by a leftover global.
    ///
    /// While locked: the overlay covers the whole working row, and every control that could start
    /// work or write anything is disabled. The Active Profile bar (outer grid row 1) is NOT
    /// covered - that is the entire reason it was lifted out of the right-hand panel.
    /// </summary>
    private void SetProfileGate(bool unlocked)
    {
        _edit.GateUnlocked = unlocked;

        if (_profileGate != null)
        {
            _profileGate.IsVisible = !unlocked;
            _profileGate.IsHitTestVisible = !unlocked;
        }

        SetEnabled("WorkAreaGrid", unlocked);

        SetEnabled("OpenVideoButton", unlocked);
        RefreshCreateMaskOverlayButton();
        SetEnabled("NewMaskOverlayTextBox", unlocked);
        SetEnabled("ResetMenuButton", unlocked);
        SetEnabled("SnapToggle", unlocked);

        if (!unlocked)
        {
            SetEnabled("PlayPauseButton", false);
            SetEnabled("SnapshotButton", false);
            SetEnabled("SaveButton", false);
            SetEnabled("UndoButton", false);
            SetEnabled("RedoButton", false);
            SetEnabled("DeleteMenuButton", false);
            SetEnabled("RaiseButton", false);
            SetEnabled("LowerButton", false);
        }

        if (_profileStateLabel != null)
        {
            if (unlocked && !string.IsNullOrWhiteSpace(_verifiedEditingProfile))
            {
                _profileStateLabel.Text = $"EDITING PROFILE: {_verifiedEditingProfile}";
                _profileStateLabel.Foreground = Infrastructure.ThemeResources.Brush(_profileStateLabel, "AppAccentBrush", Brushes.DodgerBlue);
            }
            else
            {
                _profileStateLabel.Text = "No profile selected";
                _profileStateLabel.Foreground = Infrastructure.ThemeResources.Brush(_profileStateLabel, "AppTextMutedBrush", Brushes.Gray);
            }
        }

        Avalonia.Input.DragDrop.SetAllowDrop(this, unlocked);
    }

    /// <summary>
    /// GATE_01 / CROPPROFILE_01 — loads and verifies a profile the user deliberately picked.
    ///
    /// Validates the profile identity and contents first. If loading fails (missing, corrupt,
    /// sentinel, invalid), does NOT clear working state, unlock editing or relabel old data.
    /// Only upon successful verified load is working state reset and editing unlocked.
    /// </summary>
    private async Task<bool> OnProfileChosenAsync(string profileName)
    {
        try
        {
            var loadResult = FreeVideoStudio.App.Infrastructure.MaskOverlayManager.LoadProfile(profileName);
            if (!loadResult.Success)
            {
                RuntimeLog.Fail("CROP", $"Failed to load profile '{profileName}': {loadResult.ErrorMessage}");
                SetStatus(loadResult.ErrorMessage ?? "Could not load profile.");
                if (string.IsNullOrWhiteSpace(_verifiedEditingProfile))
                {
                    SetProfileGate(unlocked: false);
                }
                return false;
            }

            string verifiedName = loadResult.ProfileName!;
            JsonObject validatedConfig = loadResult.Config!;

            _paths.EnsureWritableDirectories();
            using (NamedSystemMutex.Acquire(StateTransferStore.MutexName, StateTransferStore.DefaultMutexTimeout))
            {
                AtomicJsonFile.WriteObject(_paths.CropCoordinatesFile, validatedConfig);
            }

            _verifiedEditingProfile = verifiedName;
            _edit.ActiveProfile = verifiedName;

            ResetWorkingState();
            SetProfileGate(unlocked: true);

            await RehydrateSavedLayersAsync(validatedConfig);
            UpdateComposerEmptyState();

            RefreshActionButtons();
            RuntimeLog.Info("CROP", $"Editing mask profile '{verifiedName}' ({_items.Count} saved element(s) loaded).");

            FreeVideoStudio.App.Infrastructure.SettingsManager.Update(s => s.LastEditedCropProfile = verifiedName);

            if (string.IsNullOrWhiteSpace(_videoPath))
            {
                SetWizardState(1, "Upload Video", $"Editing \"{verifiedName}\". Open a reference clip to start.");
            }

            if (!string.IsNullOrWhiteSpace(_pendingInitialVideoPath) && File.Exists(_pendingInitialVideoPath))
            {
                string pending = _pendingInitialVideoPath;
                _pendingInitialVideoPath = null;
                await LoadVideoAsync(pending, startPaused: false);
            }

            SetStatusSuccess("Profile loaded: " + verifiedName);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Could not open profile '{profileName}': {ex.Message}");
            SetStatus("That profile could not be opened. See runtime log.");
            return false;
        }
    }

    /// <summary>ZOOM_01 (F2) - the scale currently applied to the frozen-frame surface.</summary>
    private double CurrentZoom()
    {
        if (_snapshotZoomHost?.LayoutTransform is ScaleTransform st && st.ScaleX > 0)
        {
            return st.ScaleX;
        }
        return _snapshotZoomFactor;
    }

    /// <summary>
    /// ZOOM_01 (F2) - sets the frozen frame's zoom. Pass null for FIT.
    ///
    /// This scales the RENDER, never the coordinate space. SourceCanvas keeps its true capture
    /// size (1920x1080, 2560x1440, ...) and Avalonia maps pointer positions back through the
    /// LayoutTransform for us, so SourceCanvas_PointerPressed/Moved/Released, ClampToSnapshot and
    /// ToSourceRect all keep working in real source pixels with no scaling maths of their own.
    /// Resizing the canvas instead would have meant rewriting every one of those call sites AND
    /// would have quantised selections to screen pixels.
    ///
    /// The selection rectangle's stroke is divided by the scale so that a 2px outline stays 2px on
    /// screen: at FIT on a 4K capture the scale can be ~0.2, and an unadjusted stroke would render
    /// sub-pixel and vanish - the box would look like it had not been drawn at all.
    /// </summary>
    private void ApplySnapshotZoom(double? factor)
    {
        if (factor == null)
        {
            ApplySnapshotZoomInternal(ComputeFitScale(), fitMode: true, markUserZoom: false);
            _userZoomed = false;
            return;
        }

        ApplySnapshotZoomInternal(factor.Value, fitMode: false, markUserZoom: true);
    }

    /// <summary>
    /// ZOOM_01 - the single place the zoom scale is written.
    /// </summary>
    /// <param name="markUserZoom">
    /// AUTOZOOM_01 - true when a HUMAN asked for this magnification (a zoom button, the wheel).
    /// Once set, AutoZoomToSelection stops changing the zoom and only re-centres: overriding a
    /// magnification the user deliberately chose is the fastest way to make a tool feel like it
    /// is fighting back. AutoZoomToSelection's own call passes false.
    /// </param>
    private void ApplySnapshotZoomInternal(double factor, bool fitMode, bool markUserZoom)
    {
        if (_snapshotZoomHost == null) return;

        _snapshotFitMode = fitMode;
        if (markUserZoom) _userZoomed = true;

        double scale = fitMode ? factor : Math.Clamp(factor, Math.Min(ComputeFitScale(), 1.0), MaxZoom);

        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
        _snapshotZoomFactor = scale;

        if (_snapshotZoomHost.LayoutTransform is ScaleTransform existing)
        {
            if (Math.Abs(existing.ScaleX - scale) > 0.0005 || Math.Abs(existing.ScaleY - scale) > 0.0005)
            {
                existing.ScaleX = scale;
                existing.ScaleY = scale;
            }
        }
        else
        {
            _snapshotZoomHost.LayoutTransform = new ScaleTransform(scale, scale);
        }

        if (_sourceSelection is { } liveSelection)
        {
            UpdateSelectionRect(new Rect(liveSelection.X, liveSelection.Y, liveSelection.Width, liveSelection.Height));
        }
        else if (_selectionRect != null)
        {
            _selectionRect.StrokeThickness = 2.5 / scale;
        }
        foreach (Control control in _candidateControls)
        {
            if (control is Rectangle candidate) candidate.StrokeThickness = 3.0 / scale;
            else if (control is TextBlock caption) caption.FontSize = WandCaptionFontSize / scale;
        }

        if (_zoomLabel != null)
        {
            _zoomLabel.Text = _snapshotFitMode
                ? $"Fit ({scale * 100:F0}%)"
                : $"{scale * 100:F0}%";
        }
    }

    /// <summary>
    /// ZOOM_01 (F2) - the scale that makes the whole capture fit the viewport, with a small margin
    /// so the frame's edges are visibly inside the panel rather than flush against it (a selection
    /// drawn hard against the edge is impossible to grab otherwise).
    /// Capped at 1.0: a 1080p capture in a 4K window should show at 100%, not be blown up.
    /// </summary>
    private double ComputeFitScale()
    {
        double viewportW = _snapshotScroll?.Bounds.Width ?? 0;
        double viewportH = _snapshotScroll?.Bounds.Height ?? 0;
        if (viewportW < 20 || viewportH < 20 || _edit.SnapshotWidth <= 0 || _edit.SnapshotHeight <= 0)
        {
            return _snapshotZoomFactor > 0 ? _snapshotZoomFactor : 1.0;
        }

        const double Margin = 16;
        double fit = Math.Min(
            (viewportW - Margin) / _edit.SnapshotWidth,
            (viewportH - Margin) / _edit.SnapshotHeight);

        return Math.Clamp(fit, MinZoom, 1.0);
    }

    /// <summary>ISSUE_04 — stops the walkthrough timer when this window goes away.</summary>
    protected override void OnClosed(EventArgs e)
    {
        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        StopAnts();
        StopEdgePan();
        _edgePanTimer = null;
        _timelineTimer?.Stop();

        DisposeMagicWand();

        base.OnClosed(e);
    }

    private void ButtonClick(string name, EventHandler<RoutedEventArgs> handler)
    {
        var button = this.FindControl<Button>(name);
        if (button != null)
        {
            button.Click += handler;
        }
    }

    private async Task InitializeMpvAsync()
    {
        if (_isMpvStarted || _videoHost == null)
        {
            return;
        }

        _isMpvStarted = true;
        string mpvPath = ResolveBinaryPath("mpv.exe", "frontend");
        RuntimeLog.Info("CROP", $"Using MPV: {IOPath.GetFileName(mpvPath)}");
        RuntimeLog.Debug("CROP", $"Using MPV path: {mpvPath}");
        await _videoHost.StartMpvProcessAsync(mpvPath);
    }

    /// <summary>
    /// IDEA_7 — extensions accepted by drag-and-drop. Deliberately the SAME list as the file
    /// picker's FileTypeFilter in <see cref="OpenVideoAsync"/>; if one changes, change both.
    /// </summary>
    private static readonly string[] DroppableVideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v"];

    private void OnVideoDragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        bool hasFiles = e.DataTransfer.Contains(Avalonia.Input.DataFormat.File);

        e.DragEffects = hasFiles ? Avalonia.Input.DragDropEffects.Copy : Avalonia.Input.DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnVideoDrop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        try
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            foreach (var file in files)
            {
                string path = file.Path.LocalPath;
                string ext = IOPath.GetExtension(path);
                if (!DroppableVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

                RuntimeLog.Info("CROP", $"Video dropped onto Crop Tools: {IOPath.GetFileName(path)}");
                await LoadVideoAsync(path, startPaused: false);
                return;
            }

            SetStatus("Drop an MP4, MKV, AVI, MOV, WEBM or M4V file.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Dropped file could not be loaded: {ex.Message}");
            SetStatus("That file could not be opened.");
        }
    }

    private async Task OpenVideoAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open Reference Video",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video Files")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.webm", "*.m4v"]
                }
            ]
        };

        var paths = FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault();
        try
        {
            string? startPath = null;
            if (System.IO.File.Exists(paths.SessionStateFile))
            {
                var state = FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.ReadObject(paths.SessionStateFile);
                if (state != null && state.TryGetPropertyValue("CropToolUploadDirectory", out var node) && node != null)
                {
                    startPath = node.ToString();
                }
            }

            if (string.IsNullOrEmpty(startPath) || !System.IO.Directory.Exists(startPath))
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string[] probes = new[]
                {
                    System.IO.Path.Combine(localAppData, "Temp", "Highlights", "Fortnite"),
                    System.IO.Path.Combine(localAppData, "Temp", "Highlights"),
                    System.IO.Path.Combine(localAppData, "NVIDIA Corporation", "GeForce Experience", "Highlights"),
                    System.IO.Path.Combine(myVideos, "Highlights", "Fortnite"),
                    System.IO.Path.Combine(myVideos, "Fortnite"),
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
                try { Environment.CurrentDirectory = startPath; } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(startPath));
            }
        }
        catch (Exception ex) { RuntimeLog.Info("CROP", $"Could not read suggested start location: {ex.Message}"); }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(options);

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(files[0].Path.LocalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                await new StateTransferStore(paths)
                    .UpdatePropertiesAsync(new System.Text.Json.Nodes.JsonObject
                    {
                        ["CropToolUploadDirectory"] = directory
                    });
            }
        }
        catch (Exception ex) { RuntimeLog.Info("CROP", $"Could not save upload directory preference: {ex.Message}"); }

        await LoadVideoAsync(files[0].Path.LocalPath, startPaused: false);
    }
    private async Task LoadVideoAsync(string path, bool startPaused)
    {
        if (!File.Exists(path))
        {
            SetStatus("Video file does not exist.");
            return;
        }

        await InitializeMpvAsync();

        _videoPath = path;
        _snapshotPath = null;
        _durationMs = 0;
        ResetMagicWandForNewClip();
        ClearSourceSelection();
        ClearMagicWandCandidates();
        UpdateComposerEmptyState();
        ShowVideoPanel();

        SetWizardState(2, "Find HUD Frame", "Loading video metadata...");
        SetEnabled("PlayPauseButton", true);
        SetEnabled("SnapshotButton", false);
        SetVisible("TimelinePanel", true);
        SetVisible("UploadHint", false);

        if (_videoHost?.IpcClient != null)
        {
            await _videoHost.IpcClient.LoadFileAsync(path);

            // AUTOPLAY_01 - muted, and playing.
            //
            // MUTED: this screen never exports audio and the user is scanning for a frame with a
            // clear HUD, not watching. Gameplay audio detonating the moment a file is picked is
            // startling and has no upside here. Set BEFORE unpausing, or the first frames play out
            // loud while the property is still in flight.
            await ApplyCurrentVolumeToMpvAsync();

            await _videoHost.IpcClient.SetPropertyAsync("pause", startPaused ? "yes" : "no");
            UpdatePlayPauseIcon(startPaused);
        }

        try
        {
            var prober = new MediaProber(ResolveBinaryPath("ffprobe.exe", "backend"), path);
            _durationMs = Math.Max(0, await prober.GetDurationAsync() * 1000.0);
            _edit.OriginalResolution = await prober.GetResolutionStringAsync();
            var (w, h) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
            _edit.SnapshotWidth = w;
            _edit.SnapshotHeight = h;
            _edit.CaptureResolutionKnown = w > 0 && h > 0;

            double aspectRatio = h > 0 ? (double)w / h : 1.777;
            if (Math.Abs(aspectRatio - (16.0 / 9.0)) > 0.05)
            {
                NativeDialog.ShowError($"The selected video has a resolution of {w}x{h} (Aspect Ratio: {aspectRatio:F2}).\n\nThe crop tool requires a standard 16:9 resolution (e.g., 1920x1080, 2560x1440, 3840x2160) to generate accurate HUD overlay masks. Non-16:9 videos will result in misaligned overlays on standard videos.\n\nPlease upload a 16:9 video for crop configuration.", "Unsupported Aspect Ratio");
                SetWizardState(2, "Find HUD Frame", $"Error: Non 16:9 video ({w}x{h}) rejected.");
                return;
            }

            if (_timelineSlider != null)
            {
                _timelineSlider.Minimum = 0;
                _timelineSlider.Maximum = 100;
                _timelineSlider.Value = 0;
            }

            RuntimeLog.Info("CROP", $"Video loaded: {System.IO.Path.GetFileName(path)} | Resolution: {_edit.OriginalResolution} | Duration: {_durationMs:F0}ms");
            RuntimeLog.Debug("CROP", $"Full path: {path}");
            SetEnabled("SnapshotButton", true);
            SetWizardState(2, "Find HUD Frame", $"Frame ready ({_edit.OriginalResolution}).");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Video probe failed: {ex.Message}");
            SetEnabled("SnapshotButton", true);
            SetWizardState(2, "Find HUD Frame", "Metadata fallback is 1920x1080.");
        }
    }

    private async Task TogglePlayPauseAsync()
    {
        if (_videoHost?.IpcClient == null)
        {
            return;
        }

        bool frozen = SnapshotPanelCtl?.IsVisible == true;
        if (frozen)
        {
            ShowVideoPanel();
            await _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            UpdatePlayPauseIcon(false);
            return;
        }

        bool currentlyPaused = _videoHost.IpcClient.IsPaused;
        await _videoHost.IpcClient.SetPropertyAsync("pause", currentlyPaused ? "no" : "yes");
        UpdatePlayPauseIcon(!currentlyPaused);
    }

    /// <summary>
    /// WIZCOLLAPSE_01 - folds the step tracker away once cropping starts.
    ///
    /// The four dots answer "where am I in this flow", which is a question you stop asking the
    /// moment you are dragging boxes on a frozen frame. Keeping them costs ~34px of height on the
    /// one screen where height is what lets you see the HUD you are cropping. GoalLabel and
    /// StatusLabel stay in both states - those say what to do NEXT, which never stops mattering.
    /// </summary>
    private void ApplyWizardChrome(bool cropping)
    {
        SetVisible("StepStrip", !cropping);
    }

    /// <summary>
    /// PLAYICON_01 - the ONE writer of the transport button's face, copied from the Main App
    /// (MainWindow.axaml.cs, the playIcon/pauseIcon pair).
    ///
    /// Two Paths with one visible at a time, rather than swapping the button's Content between
    /// the strings "PLAY" and "PAUSE". Swapping Content re-measures the button on every toggle,
    /// so it visibly changed width mid-playback, and a 110px button with a word in it was the
    /// only transport control in the suite that did not look like a transport control.
    /// </summary>
    private void UpdatePlayPauseIcon(bool isPaused)
    {
        var play = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pause = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (play == null || pause == null) return;
        play.IsVisible = isPaused;
        pause.IsVisible = !isPaused;
    }

    private async Task TakeSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
        {
            SetStatus("Open a video first.");
            return;
        }

        SetEnabled("SnapshotButton", false);
        SetContent("SnapshotButton", "CAPTURING...");
        SetWizardState(3, "Refine Box", "Capturing snapshot...");

        try
        {
            CleanupTempFiles();
            _tempFiles.Clear();

            _paths.EnsureWritableDirectories();
            string output = IOPath.Combine(_paths.TempDirectory, $"crop_snapshot_{Guid.NewGuid():N}.png");
            string tempOutput = output + ".tmp.png";
            double seconds = GetCurrentTimeMs() / 1000.0;

            await CaptureCurrentPreviewFrameAsync(tempOutput);

            if (!File.Exists(tempOutput) || new FileInfo(tempOutput).Length < 100)
            {
                throw new IOException("Snapshot file was not created.");
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }

            File.Move(tempOutput, output);
            _tempFiles.Add(output);
            RuntimeLog.Info("CROP", $"Snapshot captured: {IOPath.GetFileName(output)} at {seconds:F3}s from {IOPath.GetFileName(_videoPath)}");
            RuntimeLog.Debug("CROP", $"Snapshot path: {output}; source video path: {_videoPath}");
            await LoadSnapshotAsync(output);
            ShowSnapshotPanel();
            _ = RefreshRehydratedThumbnailsAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Snapshot failed: {ex.Message}");
            SetWizardState(2, "Find HUD Frame", "Snapshot failed. Try another frame.");
        }
        finally
        {
            SetEnabled("SnapshotButton", true);
            SetContent("SnapshotButton", "START CROPPING");
        }
    }

    private async Task LoadSnapshotAsync(string path)
    {
        _snapshotPath = path;

        bool wantComposerBg = _composerBackgroundImage != null;

        var (snapshotBitmap, composerBitmap, composerPreviewPath) = await Task.Run(() =>
        {
            using (SKBitmap bitmap = SKBitmap.Decode(path) ?? throw new IOException("Could not decode snapshot."))
            {
                _edit.SnapshotWidth = bitmap.Width;
                _edit.SnapshotHeight = bitmap.Height;
                _edit.OriginalResolution = $"{_edit.SnapshotWidth}x{_edit.SnapshotHeight}";
                _edit.CaptureResolutionKnown = _edit.SnapshotWidth > 0 && _edit.SnapshotHeight > 0;
                BuildContrastSampler(bitmap);
            }

            Bitmap snap;
            using (var snapStream = File.OpenRead(path))
                snap = new Bitmap(snapStream);

            Bitmap? bg = null;
            string? previewPath = null;
            if (wantComposerBg)
            {
                previewPath = CreateComposerBackgroundPreview(path);
                using var bgStream = File.OpenRead(previewPath);
                bg = new Bitmap(bgStream);
            }

            return (snap, bg, previewPath);
        });

        if (composerPreviewPath != null)
        {
            _tempFiles.Add(composerPreviewPath);
        }

        if (_snapshotImage != null)
        {
            if (_snapshotImage.Source is IDisposable oldBitmap)
            {
                oldBitmap.Dispose();
            }
            _snapshotImage.Source = snapshotBitmap;
            _snapshotImage.Width = _edit.SnapshotWidth;
            _snapshotImage.Height = _edit.SnapshotHeight;
        }

        if (_sourceCanvas != null)
        {
            _sourceCanvas.Width = _edit.SnapshotWidth;
            _sourceCanvas.Height = _edit.SnapshotHeight;
        }

        if (composerBitmap != null && _composerBackgroundImage != null)
        {
            _composerBackgroundImage.Source = composerBitmap;
        }

        ClearSourceSelection();
        ClearMagicWandCandidates();
        _wandPreviewIndex = -1;

        ApplySnapshotZoom(null);

        SetWizardState(3, "Refine Box", $"Draw a HUD box on the {_edit.OriginalResolution} snapshot.");
    }

    private async Task CaptureCurrentPreviewFrameAsync(string outputPath)
    {
        if (_videoHost?.IpcClient == null)
        {
            throw new InvalidOperationException("Video preview is not ready.");
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        await _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        await _videoHost.IpcClient.SendCommandAsync("screenshot-to-file", outputPath, "video");
        await WaitForFileAsync(outputPath, TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                try
                {
                    if (new FileInfo(path).Length > 100)
                    {
                        return;
                    }
                }
                catch (IOException swallowed)
                {
                    global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
                }
            }

            await Task.Delay(50, CancellationToken.None);
        }

        throw new TimeoutException("Timed out waiting for the current preview frame snapshot.");
    }

    private string CreateComposerBackgroundPreview(string snapshotPath)
    {
        _paths.EnsureWritableDirectories();
        string output = IOPath.Combine(_paths.TempDirectory, $"crop_canvas_trick_{Guid.NewGuid():N}.png");

        using SKBitmap source = SKBitmap.Decode(snapshotPath) ?? throw new IOException("Could not decode snapshot.");
        using var internalBitmap = new SKBitmap(
            CoordinateConstants.InternalW,
            CoordinateConstants.InternalH,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using (var internalCanvas = new SKCanvas(internalBitmap))
        {
            internalCanvas.Clear(SKColors.Black);
            var (scaledW, scaledH, cropX, cropY, _) = CoordinateMath.ScalePlan($"{source.Width}x{source.Height}");
            var dst = new SKRect(-cropX, -cropY, -cropX + scaledW, -cropY + scaledH);
            internalCanvas.DrawBitmap(source, dst);
            internalCanvas.Flush();
        }

        using var finalBitmap = new SKBitmap(
            CoordinateConstants.PortraitW,
            CoordinateConstants.PortraitH,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using (var finalCanvas = new SKCanvas(finalBitmap))
        {
            finalCanvas.Clear(SKColors.Black);
            var contentDst = new SKRect(
                0,
                CoordinateConstants.UIPaddingTop,
                CoordinateConstants.PortraitW,
                CoordinateConstants.PortraitH - CoordinateConstants.UIPaddingBottom);
            finalCanvas.DrawBitmap(internalBitmap, contentDst);

            // COMPOSERDIM_01 - the red 64-alpha wash over the two letterbox bands is GONE.
            // It was marking "this area is padding", but the AXAML already paints those bands with
            // AppVideoSurfaceBrush over the top, so the wash was invisible where it was meant to be
            // read and only served to muddy the colour of everything underneath. Combined with the
            // old 0.22 background opacity it made the reference frame unreadable, which is the
            // complaint this addresses. The bands are still obvious - they are the empty strips.
            finalCanvas.Flush();
        }

        using SKImage image = SKImage.FromBitmap(finalBitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            data.SaveTo(fs);
        }
        return output;
    }

    /// <summary>
    /// BACKTOVIDEO_01 - back to the moving video.
    ///
    /// The BACK TO VIDEO button that used to call this is deleted. PLAY calls it now: pressing play
    /// while a frame is frozen means "show me the video again", which is the only thing play can
    /// sensibly mean at that moment. Two controls for one idea, sitting next to each other, was the
    /// reason the button read as pointless.
    /// </summary>
    private void ShowVideoPanel()
    {
        SetVisible("VideoPanel", true);
        SetVisible("SnapshotPanel", false);
        SetVisible("MagicWandButton", false);

        SetVisible("SnapshotButton", true);
        SetVisible("ZoomStrip", false);
        ApplyWizardChrome(cropping: false);

        if (!string.IsNullOrWhiteSpace(_videoPath))
        {
            SetWizardState(2, "Find HUD Frame", "Find a clear HUD frame and start cropping.");
        }
    }

    private void ShowSnapshotPanel()
    {
        SetVisible("VideoPanel", false);
        SetVisible("SnapshotPanel", true);
        SetVisible("MagicWandButton", true);
        SetVisible("SnapshotButton", false);
        SetVisible("ZoomStrip", true);
        ApplyWizardChrome(cropping: true);
        SetWizardState(3, "Refine Box", "Drag a box round one HUD piece, then pick what it is.");
    }


    /// <summary>CROPCANVAS_01 — what a drag on the frozen frame is currently doing.</summary>
    private enum SourceDrag
    {
        None,
        Drawing,
        Moving,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight,
        Panning,
    }

    private SourceDrag _sourceDrag = SourceDrag.None;
    private SourceRect _sourceDragOrigin;
    private Point _sourceDragAnchor;
    private Point _panAnchorViewport;
    private readonly List<Rectangle> _selectionHandles = new();

    /// <summary>
    /// CROPCANVAS_01 — handle box and grab tolerance, in SCREEN pixels. Divided by the zoom
    /// scale wherever they are used, so a handle is the same physical size to grab whether the
    /// frame is at Fit (~0.3x on a 4K capture) or at 400%. A constant in source pixels would be
    /// invisible at Fit and enormous when zoomed in.
    /// </summary>
    private const double HandleScreenPx = 14;
    private const double GrabScreenPx = 11;

    private void SourceCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_snapshotPath == null || _sourceCanvas == null)
        {
            return;
        }

        var props = e.GetCurrentPoint(_sourceCanvas).Properties;

        if (props.IsRightButtonPressed)
        {
            CancelSourceSelection();
            e.Handled = true;
            return;
        }

        // PAN_01 — middle-drag pans, the same gesture the Python tool used and the same one
        // every image editor uses. Scrollbars alone are how the previous build earned
        // "scrolling sideways/up/down is extremely confusing": they are the one navigation
        // control you cannot reach without letting go of what you are doing.
        if (props.IsMiddleButtonPressed)
        {
            _sourceDrag = SourceDrag.Panning;
            _panAnchorViewport = e.GetPosition(_snapshotScroll);
            SetSourceCursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(_sourceCanvas);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));
        _sourceCanvas.Focus();

        if (_sourceSelection is { } current)
        {
            SourceDrag corner = HitTestSelectionCorner(p, current);
            if (corner != SourceDrag.None)
            {
                _sourceDrag = corner;
                _sourceDragOrigin = current;
                _lastDragViewportPoint = e.GetPosition(_snapshotScroll);
                StartEdgePan();
                e.Pointer.Capture(_sourceCanvas);
                e.Handled = true;
                return;
            }

            if (p.X >= current.X && p.X <= current.X + current.Width &&
                p.Y >= current.Y && p.Y <= current.Y + current.Height)
            {
                _sourceDrag = SourceDrag.Moving;
                _sourceDragOrigin = current;
                _sourceDragAnchor = p;
                _lastDragViewportPoint = e.GetPosition(_snapshotScroll);
                StartEdgePan();
                SetSourceCursor(StandardCursorType.SizeAll);
                e.Pointer.Capture(_sourceCanvas);
                e.Handled = true;
                return;
            }
        }

        if (e.Source is Control control && control.Tag is CandidateSpec candidate)
        {
            SetSourceSelection(candidate.Rect, candidate.RoleKey);
            AutoZoomToSelection();
            e.Handled = true;
            return;
        }

        _sourceDrag = SourceDrag.Drawing;
        _sourceSelectionStart = p;
        _isDrawingSourceSelection = true;
        _lastDragViewportPoint = e.GetPosition(_snapshotScroll);
        StartEdgePan();
        EnsureSelectionVisuals();
        SetHandlesVisible(false);
        UpdateSelectionRect(new Rect(p, new Size(1, 1)));
        e.Pointer.Capture(_sourceCanvas);
        e.Handled = true;
    }

    private void SourceCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_sourceCanvas == null) return;

        if (_sourceDrag != SourceDrag.None)
        {
            var live = e.GetCurrentPoint(_sourceCanvas).Properties;
            if (!live.IsLeftButtonPressed && !live.IsMiddleButtonPressed && !live.IsRightButtonPressed)
            {
                EndSourceDrag();
                return;
            }
        }

        if (_sourceDrag == SourceDrag.Panning)
        {
            Point now = e.GetPosition(_snapshotScroll);
            NudgeScrollBy(_panAnchorViewport.X - now.X, _panAnchorViewport.Y - now.Y);
            _panAnchorViewport = now;
            e.Handled = true;
            return;
        }

        if (_snapshotScroll != null)
        {
            _lastDragViewportPoint = e.GetPosition(_snapshotScroll);
        }

        Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));

        if (_sourceDrag == SourceDrag.None)
        {
            UpdateCrosshair(p);
            UpdateHoverCursor(p);
            return;
        }

        ApplySourceDragTo(p);
        e.Handled = true;
    }

    /// <summary>
    /// DRAGFREE_01 — the body of an in-progress rubber-band gesture, in SOURCE pixels.
    ///
    /// Split out of SourceCanvas_PointerMoved so the edge-pan tick can drive the same code: when
    /// the view scrolls under a stationary pointer, the source pixel beneath that pointer changes,
    /// and the box has to follow it. Without that the box would freeze the instant the user stopped
    /// moving the mouse at the edge — which is the confinement half of "the cursor gets trapped".
    /// </summary>
    private void ApplySourceDragTo(Point p)
    {
        UpdateCrosshair(p);

        switch (_sourceDrag)
        {
            case SourceDrag.Drawing:
                UpdateSelectionRect(NormalizeRect(_sourceSelectionStart, p));
                return;

            case SourceDrag.Moving:
            {
                double nx = _sourceDragOrigin.X + (p.X - _sourceDragAnchor.X);
                double ny = _sourceDragOrigin.Y + (p.Y - _sourceDragAnchor.Y);
                nx = Math.Clamp(nx, 0, Math.Max(0, _edit.SnapshotWidth - _sourceDragOrigin.Width));
                ny = Math.Clamp(ny, 0, Math.Max(0, _edit.SnapshotHeight - _sourceDragOrigin.Height));
                SetSourceSelection(
                    new SourceRect((int)Math.Round(nx), (int)Math.Round(ny),
                                   _sourceDragOrigin.Width, _sourceDragOrigin.Height),
                    keepRoleName: true);
                return;
            }

            case SourceDrag.ResizeTopLeft:
            case SourceDrag.ResizeTopRight:
            case SourceDrag.ResizeBottomLeft:
            case SourceDrag.ResizeBottomRight:
            {
                Rect resized = ResizeFromCorner(_sourceDragOrigin, _sourceDrag, p);
                if (resized.Width >= MinSelectionSize && resized.Height >= MinSelectionSize)
                {
                    SetSourceSelection(ToSourceRect(resized), keepRoleName: true);
                }
                return;
            }
        }
    }


    /// <summary>DRAGFREE_01 — how close to the viewport edge starts a pan, in screen pixels.</summary>
    private const double EdgePanMargin = 34;

    /// <summary>DRAGFREE_01 — fastest pan, in screen pixels per tick, right at the edge.</summary>
    private const double EdgePanMaxSpeed = 18;

    private DispatcherTimer? _edgePanTimer;
    private Point _lastDragViewportPoint;

    private void StartEdgePan()
    {
        if (_edgePanTimer == null)
        {
            _edgePanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _edgePanTimer.Tick += (_, _) => EdgePanTick();
        }

        _edgePanTimer.Start();
    }

    private void StopEdgePan()
    {
        _edgePanTimer?.Stop();
    }

    private void EdgePanTick()
    {
        if (_snapshotScroll == null || _sourceCanvas == null ||
            _sourceDrag is SourceDrag.None or SourceDrag.Panning)
        {
            StopEdgePan();
            return;
        }

        double vw = _snapshotScroll.Viewport.Width;
        double vh = _snapshotScroll.Viewport.Height;
        if (vw < 40 || vh < 40) return;

        static double Speed(double depth) =>
            Math.Clamp(depth / EdgePanMargin, 0, 1) * EdgePanMaxSpeed;

        double dx = 0, dy = 0;
        if (_lastDragViewportPoint.X < EdgePanMargin) dx = -Speed(EdgePanMargin - _lastDragViewportPoint.X);
        else if (_lastDragViewportPoint.X > vw - EdgePanMargin) dx = Speed(_lastDragViewportPoint.X - (vw - EdgePanMargin));

        if (_lastDragViewportPoint.Y < EdgePanMargin) dy = -Speed(EdgePanMargin - _lastDragViewportPoint.Y);
        else if (_lastDragViewportPoint.Y > vh - EdgePanMargin) dy = Speed(_lastDragViewportPoint.Y - (vh - EdgePanMargin));

        if (dx == 0 && dy == 0) return;

        Vector before = _snapshotScroll.Offset;
        NudgeScrollBy(dx, dy);
        if (_snapshotScroll.Offset == before) return;

        Point? nowSource = _snapshotScroll.TranslatePoint(_lastDragViewportPoint, _sourceCanvas);
        if (nowSource != null)
        {
            ApplySourceDragTo(ClampToSnapshot(nowSource.Value));
        }
    }

    /// <summary>
    /// DRAGFREE_01 — the ONE way a rubber-band gesture ends.
    ///
    /// Called by the release handler, by PointerCaptureLost, by Escape and by the self-heal in
    /// PointerMoved. It leaves no gesture state behind and always puts the cursor back, which the
    /// old code did only on the Panning and Moving paths — after a CORNER drag the cursor kept the
    /// resize arrow until the pointer happened to move again over empty canvas.
    /// </summary>
    private void EndSourceDrag()
    {
        _sourceDrag = SourceDrag.None;
        _isDrawingSourceSelection = false;
        StopEdgePan();
        SetSourceCursor(StandardCursorType.Cross);
    }

    private void SourceCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_sourceCanvas == null) return;

        SourceDrag finished = _sourceDrag;
        EndSourceDrag();
        e.Pointer.Capture(null);

        if (finished == SourceDrag.None) return;

        if (finished == SourceDrag.Panning)
        {
            e.Handled = true;
            return;
        }

        if (finished == SourceDrag.Drawing)
        {
            Point p = ClampToSnapshot(e.GetPosition(_sourceCanvas));
            SourceRect rect = ToSourceRect(NormalizeRect(_sourceSelectionStart, p));
            if (rect.Width < MinSelectionSize || rect.Height < MinSelectionSize)
            {
                ClearSourceSelection();
                e.Handled = true;
                return;
            }

            SetSourceSelection(rect, SuggestRole(rect).Key);

            AutoZoomToSelection();
            ShowRolePopup();
        }
        else if (finished == SourceDrag.Moving)
        {
            ShowRolePopup();
        }
        else
        {
            AutoZoomToSelection();
            ShowRolePopup();
        }

        e.Handled = true;
    }

    /// <summary>
    /// CROPCANVAS_01 — which corner handle (if any) is under <paramref name="p"/>.
    /// The tolerance is in screen pixels converted to source pixels, so the grab area is the
    /// same physical size at every zoom level.
    /// </summary>
    private SourceDrag HitTestSelectionCorner(Point p, SourceRect r)
    {
        double tol = GrabScreenPx / Math.Max(0.01, CurrentZoom());
        bool left = Math.Abs(p.X - r.X) <= tol;
        bool right = Math.Abs(p.X - (r.X + r.Width)) <= tol;
        bool top = Math.Abs(p.Y - r.Y) <= tol;
        bool bottom = Math.Abs(p.Y - (r.Y + r.Height)) <= tol;

        if (top && left) return SourceDrag.ResizeTopLeft;
        if (top && right) return SourceDrag.ResizeTopRight;
        if (bottom && left) return SourceDrag.ResizeBottomLeft;
        if (bottom && right) return SourceDrag.ResizeBottomRight;
        return SourceDrag.None;
    }

    /// <summary>
    /// CANCELSEL_01 - abandon the current selection and undo what drawing it did to the view.
    ///
    /// Cancel has to mean "pretend that never happened", and AUTOZOOM_01 makes that more than
    /// clearing a rectangle: drawing a box MOVES THE VIEW, magnifying and re-centring on it. Without
    /// restoring the zoom and the scroll, escaping from a mis-drawn box left the user stranded at
    /// 300% somewhere they never asked to be, with no idea how they got there - which is worse than
    /// the mistake they were trying to undo.
    ///
    /// The pre-zoom state is captured in AutoZoomToSelection, immediately before it changes
    /// anything, and only when there is not already one saved - so a draw, a resize and another
    /// resize all rewind to where the FIRST one started, not to the middle of the sequence.
    /// </summary>
    private void CancelSourceSelection()
    {
        EndSourceDrag();
        ClearSourceSelection();
        RestoreSourceViewAfterSelection();

        SetStatus("Selection cancelled.");
    }

    // CROPZOOMRESET_01 — commit and cancel both finish the temporary precision zoom.
    // Restore only a view auto-zoom actually changed; preserve manually chosen zoom otherwise.
    private void RestoreSourceViewAfterSelection()
    {
        if (_preZoomState is { } saved)
        {
            _preZoomState = null;
            ApplySnapshotZoomInternal(saved.Scale, fitMode: saved.FitMode, markUserZoom: false);
            _userZoomed = saved.UserZoomed;
            _snapshotZoomHost?.UpdateLayout();
            _snapshotScroll?.UpdateLayout();
            if (_snapshotScroll != null) _snapshotScroll.Offset = saved.Offset;
        }
    }

    /// <summary>CANCELSEL_01 - the view as it was before auto-zoom touched it.</summary>
    private readonly record struct PreZoomState(double Scale, bool FitMode, bool UserZoomed, Vector Offset);

    private PreZoomState? _preZoomState;

    /// <summary>CROPCANVAS_01 — the rect produced by dragging one corner to <paramref name="p"/>.</summary>
    private static Rect ResizeFromCorner(SourceRect origin, SourceDrag corner, Point p)
    {
        double l = origin.X, t = origin.Y, r = origin.X + origin.Width, b = origin.Y + origin.Height;
        switch (corner)
        {
            case SourceDrag.ResizeTopLeft: l = p.X; t = p.Y; break;
            case SourceDrag.ResizeTopRight: r = p.X; t = p.Y; break;
            case SourceDrag.ResizeBottomLeft: l = p.X; b = p.Y; break;
            case SourceDrag.ResizeBottomRight: r = p.X; b = p.Y; break;
        }
        return new Rect(new Point(Math.Min(l, r), Math.Min(t, b)), new Point(Math.Max(l, r), Math.Max(t, b)));
    }

    /// <summary>
    /// CROPCANVAS_01 — the cursor is the only thing telling the user the box can be grabbed at
    /// all. Without this the selection looks like a drawing, not an object.
    /// </summary>
    private void UpdateHoverCursor(Point p)
    {
        if (_sourceCanvas == null) return;

        StandardCursorType wanted = StandardCursorType.Cross;
        if (_sourceSelection is { } r)
        {
            SourceDrag corner = HitTestSelectionCorner(p, r);
            wanted = corner switch
            {
                SourceDrag.ResizeTopLeft or SourceDrag.ResizeBottomRight => StandardCursorType.TopLeftCorner,
                SourceDrag.ResizeTopRight or SourceDrag.ResizeBottomLeft => StandardCursorType.TopRightCorner,
                _ => p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height
                        ? StandardCursorType.Hand
                        : StandardCursorType.Cross,
            };
        }

        SetSourceCursor(wanted);
    }

    /// <summary>
    /// CROPCANVAS_01 — the ONE writer of the frozen frame's cursor.
    ///
    /// Every cursor change goes through here for two reasons. Assigning a Cursor allocates and
    /// invalidates, and PointerMoved fires continuously, so the no-op guard matters. And the
    /// cursors forced during a drag (SizeAll while panning or moving) have to leave _hoverCursor
    /// telling the truth, or the first hover after the drag sees "no change" and leaves the wrong
    /// cursor on screen.
    /// </summary>
    private void SetSourceCursor(StandardCursorType wanted)
    {
        if (_sourceCanvas == null || _hoverCursor == wanted) return;
        _hoverCursor = wanted;
        _sourceCanvas.Cursor = new Cursor(wanted);
    }

    private StandardCursorType _hoverCursor = StandardCursorType.Cross;

    /// <summary>
    /// WHEELZOOM_01 — wheel over the frozen frame zooms, anchored under the pointer.
    ///
    /// "Anchored" means the source pixel under the cursor stays under the cursor. Without it,
    /// zooming in always drifts toward a corner and the user has to chase what they were looking
    /// at with the scrollbars — which is the behaviour this whole pass is removing.
    ///
    /// The correction is MEASURED, not calculated: the zoom host is centred in the ScrollViewer,
    /// so while the content is smaller than the viewport there is padding that no offset
    /// arithmetic accounts for. Asking Avalonia where the point actually landed and correcting by
    /// the difference is right in both cases.
    /// </summary>
    private void OnSnapshotWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_snapshotPath == null || _snapshotScroll == null || _sourceCanvas == null) return;
        if (Math.Abs(e.Delta.Y) < 0.01) return;

        Point anchorSource = e.GetPosition(_sourceCanvas);
        Point anchorViewport = e.GetPosition(_snapshotScroll);

        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        ApplySnapshotZoomInternal(CurrentZoom() * factor, fitMode: false, markUserZoom: true);

        _snapshotZoomHost?.UpdateLayout();
        _snapshotScroll.UpdateLayout();

        if (_sourceSelection is { } sel && _sourceDrag != SourceDrag.Drawing)
        {
            CenterOnSourcePoint(new Point(sel.X + sel.Width / 2.0, sel.Y + sel.Height / 2.0));
            e.Handled = true;
            return;
        }

        Point? landed = _sourceCanvas.TranslatePoint(anchorSource, _snapshotScroll);
        if (landed != null)
        {
            NudgeScrollBy(landed.Value.X - anchorViewport.X, landed.Value.Y - anchorViewport.Y);
        }

        e.Handled = true;
    }

    /// <summary>
    /// WHEELDRAG_01 — window-level wheel, live only while a frozen-frame gesture is in progress.
    /// See the registration in the wiring block for why this exists at all.
    /// </summary>
    private void OnWindowWheelDuringDrag(object? sender, PointerWheelEventArgs e)
    {
        if (_sourceDrag == SourceDrag.None) return;
        if (_snapshotPath == null || _snapshotScroll == null || _sourceCanvas == null) return;

        OnSnapshotWheel(sender, e);

        if (_sourceDrag is not SourceDrag.None and not SourceDrag.Panning)
        {
            Point? nowSource = _snapshotScroll.TranslatePoint(_lastDragViewportPoint, _sourceCanvas);
            if (nowSource != null)
            {
                ApplySourceDragTo(ClampToSnapshot(nowSource.Value));
            }
        }
    }

    /// <summary>
    /// ZOOMKEEP_01 — puts the committed selection back in the middle of the viewport.
    /// Used by the zoom buttons, which otherwise leave the box wherever the scroll offset happened
    /// to be pointing after the scale changed.
    /// </summary>
    private void RecenterOnSelection()
    {
        if (_sourceSelection is not { } sel) return;
        CenterOnSourcePoint(new Point(sel.X + sel.Width / 2.0, sel.Y + sel.Height / 2.0));
    }

    /// <summary>
    /// CROPCANVAS_01 — arrow-key nudge for the SOURCE selection.
    ///
    /// The window already nudged SelectedView, which is a PORTRAIT item, so on the frozen frame
    /// the arrow keys did nothing at all. Getting the last two or three pixels of a HUD box right
    /// with a mouse is unreasonable; this is how that is meant to be done.
    /// Plain arrows move the box, Ctrl+arrows resize its bottom-right corner, Shift multiplies
    /// the step - the same shape as the portrait item's nudge, so there is one thing to learn.
    /// </summary>
    private bool NudgeSourceSelection(Key key, KeyModifiers modifiers)
    {
        if (_sourceSelection is not { } r) return false;

        double step = modifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        bool resize = modifiers.HasFlag(KeyModifiers.Control);

        double dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
        double dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
        if (dx == 0 && dy == 0) return false;

        SourceRect next;
        if (resize)
        {
            next = new SourceRect(
                r.X, r.Y,
                (int)Math.Max(MinSelectionSize, r.Width + dx),
                (int)Math.Max(MinSelectionSize, r.Height + dy));
        }
        else
        {
            next = new SourceRect(
                (int)Math.Clamp(r.X + dx, 0, Math.Max(0, _edit.SnapshotWidth - r.Width)),
                (int)Math.Clamp(r.Y + dy, 0, Math.Max(0, _edit.SnapshotHeight - r.Height)),
                r.Width, r.Height);
        }

        SetSourceSelection(_edit.ClampSourceRect(next), keepRoleName: true);
        return true;
    }

    /// <summary>
    /// AUTOZOOM_01 — after a box is drawn or resized at roughly FIT, zoom so it fills ~70% of the
    /// viewport width and scroll it to the centre.
    ///
    /// WHY IT EXISTS. Drawing a pixel-accurate rectangle on a 1920- or 3840-wide frame shown at
    /// ~30% is not possible. The user drags a ROUGH box, the app magnifies it, and the edges are
    /// then nudgeable. It is the interaction that made the old Python tool feel precise
    /// (crop_widgets.py:_auto_zoom_to_selection).
    ///
    /// AUTOZOOM_02 — WHEN IT MUST NOT FIRE, and why the old test was wrong.
    /// The gate used to be the _userZoomed flag: "has the user touched the zoom this session".
    /// That is a HISTORY question, and it got the two cases that matter backwards.
    ///   * A user already zoomed to 300% on one corner draws a small box there. _userZoomed is
    ///     true, so no re-magnify — but the view was still RE-CENTRED on the box, which slides the
    ///     frame under someone who had deliberately framed it. Tearing them away from their work.
    ///   * Worse: auto-zoom does not set the flag (markUserZoom: false), so after the FIRST
    ///     auto-zoom the flag is still false. Draw a second box and it magnifies again, from an
    ///     already-magnified view, and again on the third. It compounded.
    ///
    /// The right question is about the CURRENT STATE, not the history: am I looking at the whole
    /// frame? Auto-zoom only helps when the frame is small enough that accurate work is impossible,
    /// which is exactly "at or near FIT". So the test is now the live zoom against the fit scale,
    /// with a little headroom for someone who nudged the wheel a notch or two and is still
    /// essentially looking at the whole picture.
    ///
    /// This also fixes the compounding for free: one auto-zoom lands far above the threshold, so
    /// the second box cannot trigger another. Pressing FIT genuinely re-arms it, because FIT is
    /// what puts the zoom back near the fit scale — no flag to reset, no way for the two to
    /// disagree.
    ///
    /// When it does not fire, NOTHING happens: no zoom and no re-centre. A user working zoomed in
    /// has already framed the view they want.
    /// </summary>
    private void AutoZoomToSelection()
    {
        if (_sourceSelection is not { } sel || _snapshotScroll == null) return;

        double viewportW = _snapshotScroll.Viewport.Width;
        if (viewportW < 40) return;

        double fit = ComputeFitScale();
        double current = CurrentZoom();

        if (current > fit * AutoZoomArmThreshold)
        {
            return;
        }

        _preZoomState ??= new PreZoomState(current, _snapshotFitMode, _userZoomed, _snapshotScroll.Offset);

        double target = viewportW * 0.7 / Math.Max(1, sel.Width);

        double viewportH = _snapshotScroll.Viewport.Height;

        double bandPerSide = RolePopupMinHeight + RolePopupGap + RolePopupEdge;
        double usableH = viewportH - 2 * bandPerSide;
        if (viewportH > 40 && usableH > 40)
        {
            target = Math.Min(target, usableH / Math.Max(1, sel.Height));
        }

        target = Math.Clamp(target, fit, MaxZoom);
        if (Math.Abs(target - current) > 0.02)
        {
            ApplySnapshotZoomInternal(target, fitMode: false, markUserZoom: false);
        }

        CenterOnSourcePoint(new Point(
            sel.X + sel.Width / 2.0,
            sel.Y + sel.Height / 2.0));
    }

    /// <summary>
    /// AUTOZOOM_02 — how far above the fit scale the view may be and still count as "looking at the
    /// whole frame". Above this the user has framed the view deliberately and auto-zoom keeps out.
    /// </summary>
    private const double AutoZoomArmThreshold = 1.35;

    /// <summary>
    /// ZOOM_01 — scrolls so a given SOURCE pixel lands at the centre of the viewport.
    ///
    /// Deliberately measured rather than calculated. The zoom host is centred inside the
    /// ScrollViewer, so when the content is smaller than the viewport there is padding that no
    /// offset arithmetic accounts for. Asking Avalonia where the point actually IS
    /// (TranslatePoint) and correcting by the difference is correct in both cases and survives
    /// any future change to the alignment.
    /// </summary>
    private void CenterOnSourcePoint(Point sourcePoint)
    {
        if (_snapshotScroll == null || _sourceCanvas == null) return;

        _snapshotZoomHost?.UpdateLayout();
        _snapshotScroll.UpdateLayout();

        Point? actual = _sourceCanvas.TranslatePoint(sourcePoint, _snapshotScroll);
        if (actual == null) return;

        NudgeScrollBy(
            actual.Value.X - _snapshotScroll.Viewport.Width / 2.0,
            actual.Value.Y - _snapshotScroll.Viewport.Height / 2.0);
    }

    /// <summary>ZOOM_01 — moves the scroll offset by a delta, clamped to the real extent.</summary>
    private void NudgeScrollBy(double dx, double dy)
    {
        if (_snapshotScroll == null) return;

        double maxX = Math.Max(0, _snapshotScroll.Extent.Width - _snapshotScroll.Viewport.Width);
        double maxY = Math.Max(0, _snapshotScroll.Extent.Height - _snapshotScroll.Viewport.Height);
        _snapshotScroll.Offset = new Vector(
            Math.Clamp(_snapshotScroll.Offset.X + dx, 0, maxX),
            Math.Clamp(_snapshotScroll.Offset.Y + dy, 0, maxY));
    }


    /// <summary>BANDCONTRAST_01 — the band's colour before any frame has been measured.</summary>
    private static readonly Color BandDefaultColour = Color.FromRgb(255, 42, 42);

    /// <summary>BANDCONTRAST_01 — widest edge of the sampling copy, in pixels.</summary>
    private const int ContrastSampleMaxEdge = 640;

    private byte[]? _contrastSamples;
    private int _contrastSampleW;
    private int _contrastSampleH;

    private Color _bandColour = BandDefaultColour;
    private Rect _bandColourRect = default;
    private bool _bandColourValid;

    private void BuildContrastSampler(SKBitmap bitmap)
    {
        try
        {
            int srcW = bitmap.Width, srcH = bitmap.Height;
            if (srcW <= 0 || srcH <= 0)
            {
                _contrastSamples = null;
                _contrastSampleW = _contrastSampleH = 0;
                return;
            }

            double f = Math.Min(1.0, ContrastSampleMaxEdge / (double)Math.Max(srcW, srcH));
            int w = Math.Max(1, (int)Math.Round(srcW * f));
            int h = Math.Max(1, (int)Math.Round(srcH * f));

            var buffer = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(srcH - 1, (int)((y + 0.5) * srcH / h));
                int row = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(srcW - 1, (int)((x + 0.5) * srcW / w));
                    SKColor c = bitmap.GetPixel(sx, sy);
                    int i = row + x * 3;
                    buffer[i] = c.Red;
                    buffer[i + 1] = c.Green;
                    buffer[i + 2] = c.Blue;
                }
            }

            _contrastSamples = buffer;
            _contrastSampleW = w;
            _contrastSampleH = h;
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            _contrastSamples = null;
            _contrastSampleW = _contrastSampleH = 0;
        }

        _bandColourValid = false;
        _bandColour = BandDefaultColour;
    }

    /// <summary>
    /// BANDCONTRAST_01 — the band colour for a rectangle, averaged over the pixels its outline
    /// actually crosses (not the whole interior: what has to stand out is the LINE).
    /// </summary>
    private Color BandColourFor(Rect rect)
    {
        byte[] samples = _contrastSamples ?? Array.Empty<byte>();
        if (samples.Length == 0 || _contrastSampleW <= 0 || _contrastSampleH <= 0 ||
            _edit.SnapshotWidth <= 0 || _edit.SnapshotHeight <= 0)
        {
            return BandDefaultColour;
        }

        if (_bandColourValid &&
            Math.Abs(rect.X - _bandColourRect.X) < 3 && Math.Abs(rect.Y - _bandColourRect.Y) < 3 &&
            Math.Abs(rect.Width - _bandColourRect.Width) < 3 && Math.Abs(rect.Height - _bandColourRect.Height) < 3)
        {
            return _bandColour;
        }

        double fx = _contrastSampleW / (double)_edit.SnapshotWidth;
        double fy = _contrastSampleH / (double)_edit.SnapshotHeight;

        int left = Math.Clamp((int)Math.Round(rect.X * fx), 0, _contrastSampleW - 1);
        int top = Math.Clamp((int)Math.Round(rect.Y * fy), 0, _contrastSampleH - 1);
        int right = Math.Clamp((int)Math.Round((rect.X + rect.Width) * fx), 0, _contrastSampleW - 1);
        int bottom = Math.Clamp((int)Math.Round((rect.Y + rect.Height) * fy), 0, _contrastSampleH - 1);
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);

        const int StepsPerEdge = 40;
        const int Band = 2;

        long sumR = 0, sumG = 0, sumB = 0;
        int count = 0;

        void Take(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _contrastSampleW || y >= _contrastSampleH) return;
            int i = (y * _contrastSampleW + x) * 3;
            sumR += samples[i];
            sumG += samples[i + 1];
            sumB += samples[i + 2];
            count++;
        }

        for (int s = 0; s <= StepsPerEdge; s++)
        {
            int x = left + (right - left) * s / StepsPerEdge;
            int y = top + (bottom - top) * s / StepsPerEdge;

            for (int d = -Band; d <= Band; d++)
            {
                Take(x, top + d);
                Take(x, bottom + d);
                Take(left + d, y);
                Take(right + d, y);
            }
        }

        if (count == 0) return BandDefaultColour;

        double mr = sumR / (double)count;
        double mg = sumG / (double)count;
        double mb = sumB / (double)count;

        Color chosen = OppositeOf(mr, mg, mb);

        _bandColour = chosen;
        _bandColourRect = rect;
        _bandColourValid = true;
        return chosen;
    }


    /// <summary>
    /// CROPCANVAS_01 — builds the selection rectangle and its four corner handles, once.
    /// </summary>
    private void EnsureSelectionVisuals()
    {
        if (_sourceCanvas == null || _selectionRect != null)
        {
            return;
        }

        _selectionRect = new Rectangle
        {
            Stroke = new SolidColorBrush(BandDefaultColour),
            StrokeThickness = 2.5 / Math.Max(0.01, CurrentZoom()),
            Fill = new SolidColorBrush(Color.FromArgb(28, BandDefaultColour.R, BandDefaultColour.G, BandDefaultColour.B)),
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
            IsHitTestVisible = false,
            ZIndex = 500
        };
        _sourceCanvas.Children.Add(_selectionRect);
        StartAnts();

        for (int i = 0; i < 2; i++)
        {
            var handle = new Rectangle
            {
                Fill = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red),
                Stroke = Brushes.White,
                StrokeThickness = 1.5 / Math.Max(0.01, CurrentZoom()),
                IsHitTestVisible = false,
                ZIndex = 520
            };
            _selectionHandles.Add(handle);
            _sourceCanvas.Children.Add(handle);
        }
    }

    /// <summary>
    /// ANTS_01 - crawls the selection's dash pattern.
    ///
    /// One shared timer for the single selection rectangle, started when the rectangle is created
    /// and stopped when it is cleared. It must be stopped: a DispatcherTimer holds a strong
    /// reference to its handler, so leaving it running keeps this window alive after it closes and
    /// keeps waking the dispatcher for a rectangle nobody can see.
    /// </summary>
    private DispatcherTimer? _antTimer;
    private double _antOffset;

    private void StartAnts()
    {
        if (_antTimer != null) return;

        _antTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _antTimer.Tick += (_, _) =>
        {
            if (_selectionRect == null) return;
            _antOffset = (_antOffset + 1) % 8;
            _selectionRect.StrokeDashOffset = _antOffset;
        };
        _antTimer.Start();
    }

    private void StopAnts()
    {
        _antTimer?.Stop();
        _antTimer = null;
    }


    private readonly List<Line> _crosshairLines = new();

    /// <summary>CROSSHAIR_01 — gap and dash in SCREEN pixels, divided by the zoom where used.</summary>
    private const double CrosshairGapPx = 9;

    private void EnsureCrosshair()
    {
        if (_sourceCanvas == null || _crosshairLines.Count == 4) return;

        var colour = Infrastructure.ThemeResources.Colour(this, "AppAccentColor", Color.FromRgb(125, 211, 252));
        for (int i = 0; i < 4; i++)
        {
            var line = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(120, colour.R, colour.G, colour.B)),
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 5, 4 },
                IsHitTestVisible = false,
                IsVisible = false,
                ZIndex = 300,
            };
            _crosshairLines.Add(line);
            _sourceCanvas.Children.Add(line);
        }
    }

    private void SetCrosshairVisible(bool visible)
    {
        foreach (Line line in _crosshairLines) line.IsVisible = visible;
    }

    /// <summary>
    /// CROSSHAIR_01 — redraws the four rays around <paramref name="p"/>, in SOURCE pixels.
    /// Thickness and the centre gap are screen constants divided by the zoom, so the guides look
    /// identical at FIT on a 4K capture and at 400% — a fixed source-pixel thickness would be
    /// invisible at one end and a fat band at the other.
    /// </summary>
    private void UpdateCrosshair(Point p)
    {
        if (_sourceCanvas == null || _snapshotPath == null) return;

        EnsureCrosshair();
        if (_crosshairLines.Count != 4) return;

        double scale = Math.Max(0.01, CurrentZoom());
        double gap = CrosshairGapPx / scale;
        double w = _edit.SnapshotWidth;
        double h = _edit.SnapshotHeight;

        foreach (Line line in _crosshairLines)
        {
            line.StrokeThickness = 1.0 / scale;
        }

        _crosshairLines[0].StartPoint = new Point(0, p.Y);
        _crosshairLines[0].EndPoint = new Point(Math.Max(0, p.X - gap), p.Y);

        _crosshairLines[1].StartPoint = new Point(Math.Min(w, p.X + gap), p.Y);
        _crosshairLines[1].EndPoint = new Point(w, p.Y);

        _crosshairLines[2].StartPoint = new Point(p.X, 0);
        _crosshairLines[2].EndPoint = new Point(p.X, Math.Max(0, p.Y - gap));

        _crosshairLines[3].StartPoint = new Point(p.X, Math.Min(h, p.Y + gap));
        _crosshairLines[3].EndPoint = new Point(p.X, h);

        SetCrosshairVisible(true);
    }

    private void SetHandlesVisible(bool visible)
    {
        foreach (Rectangle h in _selectionHandles)
        {
            h.IsVisible = visible;
        }
    }

    private void UpdateSelectionRect(Rect rect)
    {
        EnsureSelectionVisuals();
        if (_selectionRect == null)
        {
            return;
        }

        rect = rect.Intersect(new Rect(0, 0, _edit.SnapshotWidth, _edit.SnapshotHeight));
        Canvas.SetLeft(_selectionRect, rect.X);
        Canvas.SetTop(_selectionRect, rect.Y);
        _selectionRect.Width = Math.Max(1, rect.Width);
        _selectionRect.Height = Math.Max(1, rect.Height);

        Color band = BandColourFor(rect);
        if (_selectionRect.Stroke is SolidColorBrush strokeBrush)
        {
            if (strokeBrush.Color != band) strokeBrush.Color = band;
        }
        else
        {
            _selectionRect.Stroke = new SolidColorBrush(band);
        }

        var tint = Color.FromArgb(28, band.R, band.G, band.B);
        if (_selectionRect.Fill is SolidColorBrush fillBrush)
        {
            if (fillBrush.Color != tint) fillBrush.Color = tint;
        }
        else
        {
            _selectionRect.Fill = new SolidColorBrush(tint);
        }

        double scale = Math.Max(0.01, CurrentZoom());
        _selectionRect.StrokeThickness = 2.5 / scale;

        double h = HandleScreenPx / scale;
        var corners = new[]
        {
            new Point(rect.X, rect.Y),
            new Point(rect.X + rect.Width, rect.Y + rect.Height),
        };

        if (RolePopupCtl?.IsVisible == true)
        {
            PositionRolePopup();
        }

        for (int i = 0; i < _selectionHandles.Count && i < corners.Length; i++)
        {
            Rectangle handle = _selectionHandles[i];
            handle.Width = h;
            handle.Height = h;
            handle.StrokeThickness = 1.5 / scale;
            Canvas.SetLeft(handle, corners[i].X - h / 2);
            Canvas.SetTop(handle, corners[i].Y - h / 2);
        }
    }

    private void SetSourceSelection(SourceRect rect, string? roleKey = null, bool keepRoleName = false)
    {
        _sourceSelection = rect;
        UpdateSelectionRect(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
        SetHandlesVisible(true);
        if (_selectionInfo != null)
        {
            _selectionInfo.Text = $"{rect.Width} x {rect.Height} at {rect.X}, {rect.Y}  \u2014  drag inside to move \u00b7 red corners resize \u00b7 arrows nudge \u00b7 Enter to name it";
        }

        if (keepRoleName)
        {
            return;
        }

        HudRole role = roleKey != null && _edit.TryGetRole(roleKey, out HudRole found)
            ? found
            : SuggestRole(rect);
        RoleName = role.DisplayName;
    }

    private void ClearSourceSelection()
    {
        _sourceSelection = null;
        _sourceDrag = SourceDrag.None;

        if (_sourceCanvas != null)
        {
            if (_selectionRect != null)
            {
                _sourceCanvas.Children.Remove(_selectionRect);
            }
            foreach (Rectangle handle in _selectionHandles)
            {
                _sourceCanvas.Children.Remove(handle);
            }
        }

        _selectionRect = null;
        _selectionHandles.Clear();
        StopAnts();
        HideRolePopup();
        if (_selectionInfo != null)
        {
            _selectionInfo.Text = "Drag a box around one HUD element, then pick what it is.";
        }
    }


    /// <summary>ROLEPOPUP_01 — true while the inline "+ New element" row is open.</summary>
    private bool _rolePopupNewOpen;

    /// <summary>
    /// ROLEPOPUP_01 — builds and shows the element chooser next to the current selection.
    ///
    /// Ordering copies <c>_apply_role_priority</c> from the Python tool: the element most likely to
    /// be the one just drawn goes FIRST, guessed from which quadrant of the frame the box sits in.
    /// It is only a guess, so it is only an ordering — nothing is auto-assigned. Elements already
    /// placed in this session are dimmed and sink to the bottom, because picking one REPLACES it,
    /// which is occasionally what you want and usually not.
    /// </summary>
    private void ShowRolePopup()
    {
        var popup = RolePopupCtl;
        var list = this.FindControl<StackPanel>("RolePopupList");
        if (popup == null || list == null || _sourceSelection is not { } sel) return;

        if (_rolePopupScrim != null) _rolePopupScrim.IsVisible = true;

        CloseRolePopupNewRow();
        list.Children.Clear();

        string? primary = _edit.QuadrantGuess(sel).Key;
        var placed = _items.Select(i => i.RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ordered = _edit.AllRoles
            .OrderBy(r => placed.Contains(r.Key) ? 1 : 0)
            .ThenBy(r => string.Equals(r.Key, primary, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        foreach (HudRole role in ordered)
        {
            bool already = placed.Contains(role.Key);
            bool isPrimary = !already && string.Equals(role.Key, primary, StringComparison.OrdinalIgnoreCase);

            var button = new Button
            {
                Content = role.DisplayName,
                Classes = { isPrimary ? "Primary" : "Secondary" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
                Padding = new Thickness(8, 5),
                Cursor = new Cursor(StandardCursorType.Hand),
                Opacity = already ? 0.55 : 1.0,
            };
            ToolTip.SetTip(button, already
                ? $"\"{role.DisplayName}\" is already placed. Picking it again replaces it with this box."
                : $"Label this box as \"{role.DisplayName}\".");

            HudRole captured = role;
            button.Click += async (_, _) => await ConfirmSelectionAsAsync(captured);
            list.Children.Add(button);
        }

        var addNew = new Button
        {
            Content = "+  New element…",
            Classes = { "Secondary" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(11),
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(8, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(addNew, "This HUD piece is not in the list yet. Give it a name and it is added to this profile.");
        addNew.Click += (_, _) => OpenRolePopupNewRow();
        list.Children.Add(addNew);

        popup.IsVisible = true;
        PositionRolePopup();
    }

    /// <summary>
    /// POPUPCLEAR_01 — places the chooser NEXT TO the selection and never on top of it.
    ///
    /// WHAT WAS WRONG. The old version picked right-of-box, flipped to left-of-box if the right
    /// overflowed, and then finished with an unconditional
    ///     x = Math.Clamp(x, 4, viewportWidth - pw - 4);
    ///     y = Math.Clamp(y, 4, viewportHeight - ph - 4);
    /// That clamp knows about the PANEL EDGES and nothing whatever about the box. Three ways it
    /// put the popup straight over the rubber band:
    ///   * the left flip computes x = boxLeft - gap - popupWidth, which goes NEGATIVE for a box
    ///     near the left edge — and the clamp then slams it back to 4, inside the box;
    ///   * the flip only fires on right-overflow, so it never checked that the left actually fits;
    ///   * y was the box's TOP, so a tall popup beside a short box low in the panel got clamped
    ///     upward, across the box.
    /// Covering the selection is destructive here in the literal sense: the popup swallows the
    /// pointer, so the box underneath cannot be grabbed, resized or even seen while choosing.
    ///
    /// HOW THIS ONE WORKS. Four candidate placements are tried in order — right, left, below,
    /// above — and the first that fits the viewport is taken. The ordering is deliberate: right
    /// first because a right-handed drag ends with the pointer at the box's right edge, then left,
    /// then the vertical pair for a box that spans the panel's width.
    ///
    /// The clamping is what makes the guarantee hold. For a LEFT or RIGHT placement only X decides
    /// whether the popup overlaps, so Y is clamped freely and X is never touched again. For ABOVE
    /// or BELOW only Y decides it, so X is clamped freely and Y is left alone. The axis that keeps
    /// the popup clear of the box is never the axis that gets clamped — which is exactly the
    /// mistake the old code made.
    ///
    /// If nothing fits (a box wider and taller than the panel can flank), the side with the most
    /// free space wins and the popup is pinned flush against the box there. It may then run past
    /// the panel edge, and that is the deliberate trade: a popup half off the edge is recoverable,
    /// a popup welded over the thing you are trying to aim is not.
    /// </summary>
    /// <summary>
    /// POPUPCLEAR_01 — the RolePopup's height ceiling, and the reason it is a FIELD.
    ///
    /// It used to be a `const double PopupMaxHeight = 340;` declared HALFWAY DOWN
    /// <see cref="PositionRolePopup"/>, while the first thing that method does is reset
    /// `popup.MaxHeight` to it — a read seventeen lines above the declaration. C# scopes a local
    /// const to the whole enclosing block but forbids using it before its declaration point, so
    /// that was a hard CS0841 build break, not a style problem.
    ///
    /// Class scope also puts it where it belongs: it is a contract with the AXAML
    /// (`MaxHeight="340"` on the RolePopup Border), not a detail of one method. Change one and
    /// change the other, or the "does it fit?" test in PositionRolePopup starts answering about a
    /// box that is not the box on screen.
    /// </summary>
    private const double PopupMaxHeight = 340;


    /// <summary>Breathing room between the rubber band and the chooser. Never less than this, on
    /// any side, in any branch — that is the "never too close to the rubberband" guarantee.</summary>
    private const double RolePopupGap = 12;

    /// <summary>Breathing room between the chooser and the panel edge.</summary>
    private const double RolePopupEdge = 6;


    /// <summary>Below this the element list is too short to choose from, so a vertical band this
    /// small is not a placement, it is a worse overlap.</summary>
    private const double RolePopupMinHeight = 130;

    private void PositionRolePopup()
    {
        var popup = RolePopupCtl;
        var host = this.FindControl<ScrollViewer>("SnapshotScroll");
        if (popup == null || host == null || _sourceCanvas == null || _sourceSelection is not { } sel) return;

        popup.MaxHeight = PopupMaxHeight;
        popup.UpdateLayout();
        double pw = popup.Bounds.Width > 0 ? popup.Bounds.Width : popup.MinWidth;
        double ph = popup.Bounds.Height > 0 ? popup.Bounds.Height : 200;

        Point? tl = _sourceCanvas.TranslatePoint(new Point(sel.X, sel.Y), host);
        Point? br = _sourceCanvas.TranslatePoint(new Point(sel.X + sel.Width, sel.Y + sel.Height), host);
        if (tl == null || br == null) return;

        double vw = host.Bounds.Width;
        double vh = host.Bounds.Height;
        if (vw < 20 || vh < 20) return;

        const double Gap = RolePopupGap;
        const double Edge = RolePopupEdge;
        const double MinPopupHeight = RolePopupMinHeight;

        double boxL = Math.Max(0, Math.Min(tl.Value.X, br.Value.X));
        double boxT = Math.Max(0, Math.Min(tl.Value.Y, br.Value.Y));
        double boxR = Math.Min(vw, Math.Max(tl.Value.X, br.Value.X));
        double boxB = Math.Min(vh, Math.Max(tl.Value.Y, br.Value.Y));

        double ClampY(double y) => Math.Clamp(y, Edge, Math.Max(Edge, vh - ph - Edge));
        double ClampX(double x) => Math.Clamp(x, Edge, Math.Max(Edge, vw - pw - Edge));

        double freeRight = vw - boxR - Gap - Edge;
        double freeLeft = boxL - Gap - Edge;
        double freeBelow = vh - boxB - Gap - Edge;
        double freeAbove = boxT - Gap - Edge;

        double x, y;

        if (freeRight >= pw)
        {
            x = boxR + Gap;
            y = ClampY(boxT);
        }
        else if (freeLeft >= pw)
        {
            x = boxL - Gap - pw;
            y = ClampY(boxT);
        }
        else if (freeBelow >= ph)
        {
            x = ClampX(boxL);
            y = boxB + Gap;
        }
        else if (freeAbove >= ph)
        {
            x = ClampX(boxL);
            y = boxT - Gap - ph;
        }
        else
        {
            double vBand = Math.Max(freeBelow, freeAbove);
            if (vBand >= MinPopupHeight)
            {
                popup.MaxHeight = Math.Min(PopupMaxHeight, vBand);
                popup.UpdateLayout();
                ph = popup.Bounds.Height > 0 ? popup.Bounds.Height : vBand;

                x = ClampX(boxL);
                y = freeBelow >= freeAbove ? boxB + Gap : boxT - Gap - ph;
            }
            else
            {
                const double MinVisibleFraction = 0.6;

                bool preferRight = freeRight >= freeLeft;
                double horizontalVisible = preferRight ? vw - (boxR + Gap) : boxL - Gap;

                bool preferBelow = freeBelow >= freeAbove;
                double verticalVisible = preferBelow ? vh - (boxB + Gap) : boxT - Gap;

                if (horizontalVisible >= pw * MinVisibleFraction || horizontalVisible >= verticalVisible)
                {
                    x = preferRight ? boxR + Gap : boxL - Gap - pw;
                    y = ClampY(boxT);
                }
                else
                {
                    y = preferBelow ? boxB + Gap : boxT - Gap - ph;
                    x = ClampX(boxL);
                }

                RuntimeLog.Debug("CROP",
                    $"Role chooser has no clear placement inside the panel ({vw:F0}x{vh:F0}, box {boxR - boxL:F0}x{boxB - boxT:F0}); " +
                    "overhanging the panel edge rather than covering the selection.");
            }
        }

        bool overlaps = x < boxR + Gap && boxL - Gap < x + pw
                     && y < boxB + Gap && boxT - Gap < y + ph;

        if (overlaps)
        {
            double pushRight = (boxR + Gap) - x;
            double pushLeft = (x + pw) - (boxL - Gap);
            double pushDown = (boxB + Gap) - y;
            double pushUp = (y + ph) - (boxT - Gap);

            double minPush = Math.Min(Math.Min(pushRight, pushLeft), Math.Min(pushDown, pushUp));

            if (minPush == pushRight) x = boxR + Gap;
            else if (minPush == pushLeft) x = boxL - Gap - pw;
            else if (minPush == pushDown) y = boxB + Gap;
            else y = boxT - Gap - ph;

            RuntimeLog.Fail("CROP",
                "Role chooser placement overlapped the selection and had to be pushed clear — a placement branch in PositionRolePopup is wrong.");
        }

        popup.Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>
    /// ISSUE_09 (audit round 6) — the RolePopup drop shadow, in the theme's colour.
    ///
    /// The AXAML used to carry <c>BoxShadow="0 4 18 0 #66000000"</c>: the only raw colour literal
    /// left in CropToolWindow.axaml, and a fixed 40%-black smear that stayed exactly the same after
    /// a switch to Light, where it reads as dirt rather than depth. Every other colour in that file
    /// is a DynamicResource that follows ResourceDictionary.ThemeDictionaries.
    ///
    /// It cannot be fixed in the markup: <c>BoxShadows</c> is parsed as a whole from one string, so
    /// there is no way to put a DynamicResource on the colour stop alone. So the geometry stays
    /// here in code — the same 0/4/18/0 the markup had — and only the COLOUR comes from the theme,
    /// via the AppPopupShadowColor token now defined in both variant dictionaries. Re-run on
    /// ActualThemeVariantChanged, because a shadow baked at construction would keep the old
    /// variant's colour for the life of the window.
    /// </summary>
    private void ApplyRolePopupShadow()
    {
        var popup = RolePopupCtl;
        if (popup == null) return;

        Color shadow = Infrastructure.ThemeResources.Colour(
            popup, "AppPopupShadowColor", Color.FromArgb(0x66, 0x00, 0x00, 0x00));

        popup.BoxShadow = new BoxShadows(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = 4,
            Blur = 18,
            Spread = 0,
            Color = shadow
        });
    }

    private void HideRolePopup()
    {
        var popup = RolePopupCtl;
        if (popup != null) popup.IsVisible = false;
        if (_rolePopupScrim != null) _rolePopupScrim.IsVisible = false;
        CloseRolePopupNewRow();
    }

    private void OpenRolePopupNewRow()
    {
        var row = this.FindControl<StackPanel>("RolePopupNewRow");
        var box = RolePopupNewNameCtl;
        if (row == null || box == null) return;

        _rolePopupNewOpen = true;
        row.IsVisible = true;
        box.Text = "";
        box.Focus();
        PositionRolePopup();
    }

    private void CloseRolePopupNewRow()
    {
        var row = this.FindControl<StackPanel>("RolePopupNewRow");
        if (row != null) row.IsVisible = false;
        _rolePopupNewOpen = false;
    }

    /// <summary>
    /// ROLEPOPUP_01 — commits the inline "+ New element" name.
    ///
    /// The name is registered in <see cref="_customRoles"/> BEFORE the layer is created, so it is
    /// in the popup's list for every later box in this session. Without that the user would have
    /// to retype it each time, which is how the old free-text field produced near-duplicate keys.
    /// </summary>
    private async Task CommitNewRoleAsync()
    {
        var box = RolePopupNewNameCtl;
        string name = box?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus("Type a name for the new HUD element first.");
            return;
        }

        HudRole role = _edit.RegisterCustomRole(name);
        CloseRolePopupNewRow();
        await ConfirmSelectionAsAsync(role);
    }

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// ROLEPOPUP_01 — one click in the popup = one finished layer.
    ///
    /// NODUPES_02 — IT TAKES THE ROLE. IT USED TO TAKE THE ROLE'S LABEL, AND THAT WAS THE BUG.
    ///
    /// The old signature was <c>ConfirmSelectionAsAsync(string displayName)</c>: the caller had the
    /// HudRole in its hand, threw the key away, passed the display name, and AddCurrentSelection
    /// then tried to reconstruct the key from that name with
    /// <c>name.ToLowerInvariant().Replace(" ", "_")</c>.
    ///
    /// That reconstruction fails for EVERY built-in element, because not one of
    /// their display names is their key:
    ///
    ///     loot        "Loot Area"                                    ->  loot_area
    ///     stats       "Mini Map + Stats"                             ->  mini_map_+_stats
    ///     normal_hp   "Own Health Bar (HP)"                          ->  own_health_bar_(hp)
    ///     team        "Teammates health Bars (HP)"                   ->  teammates_health_bars_(hp)
    ///     spectating  "Spectating Eye"                               ->  spectating_eye
    ///
    /// TryGetRole then missed, RegisterCustomRole minted a brand new role under the
    /// mangled key, and the "replace what is already there" check in AddCurrentSelection compared
    /// <c>loot_area</c> against the rehydrated profile's <c>loot</c>, found no match, and added a
    /// SECOND layer — with the same words on its label. That is the reported duplicate.
    ///
    /// It was not only a UI defect. The mangled key is what got SAVED, so the crop was written to
    /// <c>loot_area</c>, a key <see cref="HudConfig.HudKeys"/> does not know and the export filter
    /// graph does not draw. The user's element was silently missing from the finished video.
    ///
    /// THE RULE, now enforced by the type system rather than by string handling: a role's KEY is
    /// its identity and travels as a <see cref="HudRole"/> from the moment it is chosen to the
    /// moment it is committed. A display name is for reading. It is never parsed back into a key.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private async Task ConfirmSelectionAsAsync(HudRole role)
    {
        RoleName = role.DisplayName;

        HideRolePopup();
        await AddCurrentSelection(role);
    }

    /// <param name="role">
    /// NODUPES_02 — the element this box IS, handed in by the chooser that owns the decision.
    ///
    /// This method used to take no argument and rebuild the role from the window's RoleName string
    /// (<c>RoleName.ToLowerInvariant().Replace(" ", "_")</c>), which produced a wrong key for all
    /// built-in elements and is the whole reason duplicates could exist. See
    /// <see cref="ConfirmSelectionAsAsync"/> for the full table of what that mangling produced.
    /// The key now arrives intact and is never re-derived.
    /// </param>
    private async Task AddCurrentSelection(HudRole role)
    {
        if (_sourceSelection == null || string.IsNullOrWhiteSpace(_snapshotPath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(role.Key))
        {
            SetStatus("Please pick what this box is first.");
            return;
        }

        SourceRect sourceRect = _edit.ClampSourceRect(_sourceSelection.Value);
        if (sourceRect.Width < MinSelectionSize || sourceRect.Height < MinSelectionSize)
        {
            SetStatus("Selection is too small.");
            return;
        }

        try
        {
            var contentRect = CoordinateMath.TransformToContentAreaInt(
                (sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
                _edit.OriginalResolution);
            if (contentRect.w < 2 || contentRect.h < 2)
            {
                SetStatus("Selection does not map to a visible portrait area.");
                return;
            }

            string snapshotPath = _snapshotPath;
            string cropPath = await CropSnapshotRegionForExportPreviewAsync(snapshotPath, sourceRect, role.Key);
            _tempFiles.Add(cropPath);

            // NODUPES_01 / NODUPES_02 / DELETESET_01 — one element, one entry, always: an element
            // already placed (by key OR by label) is REPLACED, and a twin under another key is
            // tombstoned. EDITSTATE_01 — the rule, the default placement and the z collision are
            // the session's; this releases the replaced visuals and builds the new one.
            CropLayer layer = _edit.AddLayer(role, sourceRect, cropPath, contentRect, out var replaced);
            foreach (CropLayer old in replaced)
            {
                if (_items.FirstOrDefault(i => ReferenceEquals(i.Model, old)) is { } oldView) RemoveItem(oldView);
            }

            int z = layer.Z;
            var item = CreateItem(layer);
            _items.Add(item);
            SelectItem(item);
            RefreshLayerList();
            RefreshActionButtons();
            PushHistory(CropHistoryLabels.AddElement);
            RuntimeLog.Info("CROP", $"Added HUD element: {role.DisplayName} (role={role.Key}, source={sourceRect.Width}x{sourceRect.Height} at ({sourceRect.X},{sourceRect.Y}), z={z})");
            ClearSourceSelection();
            ClearMagicWandCandidates();
            _wandPreviewIndex = -1;

            RestoreSourceViewAfterSelection();

            SetWizardState(4, "Portrait Composer", $"Adjust {role.DisplayName}, then finish and save.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Add selection failed: {ex.Message}");
            SetStatus("Could not add that selection.");
        }
    }

    private async Task<string> CropSnapshotRegionForExportPreviewAsync(string snapshotPath, SourceRect sourceRect, string roleKey)
    {
        var contentRect = CoordinateMath.TransformToContentAreaInt(
            (sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
            _edit.OriginalResolution,
            HudConfig.CropDriftType(roleKey));
        var exportRect = CoordinateMath.InverseTransformFromContentAreaInt(
            (contentRect.x, contentRect.y, contentRect.w, contentRect.h),
            _edit.OriginalResolution,
            HudConfig.CropDriftType(roleKey));
        return await CropSnapshotRegionAsync(snapshotPath, new SourceRect(exportRect.x, exportRect.y, exportRect.w, exportRect.h)).ConfigureAwait(false);
    }

    private async Task<string> CropSnapshotRegionAsync(string snapshotPath, SourceRect rect)
    {
        _paths.EnsureWritableDirectories();
        string output = IOPath.Combine(_paths.TempDirectory, $"crop_item_{Guid.NewGuid():N}.png");

        using var data = await Task.Run(() =>
        {
            using SKBitmap source = SKBitmap.Decode(snapshotPath) ?? throw new IOException("Could not decode snapshot.");
            SourceRect clamped = CropEditSession.ClampSourceRect(rect, source.Width, source.Height);

            using var target = new SKBitmap(clamped.Width, clamped.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(target);
            canvas.Clear(SKColors.Transparent);

            var src = new SKRect(clamped.X, clamped.Y, clamped.X + clamped.Width, clamped.Y + clamped.Height);
            var dst = new SKRect(0, 0, clamped.Width, clamped.Height);
            canvas.DrawBitmap(source, src, dst);
            canvas.Flush();

            using SKImage image = SKImage.FromBitmap(target);
            return image.Encode(SKEncodedImageFormat.Png, 100);
        }).ConfigureAwait(false);

        using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
        {
            data.SaveTo(fs);
            await fs.FlushAsync().ConfigureAwait(false);
        }

        return output;
    }

    /// <summary>Builds the VISUALS for one layer of the edit and mounts them on the composer.</summary>
    private CropEditorItem CreateItem(CropLayer snapshot)
    {
        var root = new Canvas
        {
            Width = snapshot.Width,
            Height = snapshot.Height,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Focusable = true,
            Tag = null,
            ZIndex = snapshot.Z,

            // ITEMHIT_01 — THE reason a placed element could not be picked up or moved.
            //
            // In Avalonia a Panel with Background = null does not take part in hit testing at all:
            // there is nothing painted, so there is nothing to hit. Every child inside this Canvas
            // is IsHitTestVisible = false (the image, the outline, the label) and the only hittable
            // parts are the two red handles, which UpdateItemVisual keeps HIDDEN unless the item is
            // already selected. A freshly placed element was therefore completely inert: no click,
            // no select, no drag, and no way to reach the handles that would have let you select it.
            //
            // It was masked for as long as the LAYERS list existed, because clicking a row there was
            // what called SelectItem. Removing that list (LAYERSPANE_01) took away the last route in
            // and turned a latent bug into a dead feature.
            //
            // Brushes.Transparent is NOT the same as null here: a transparent brush paints nothing
            // but IS hit-testable, which is exactly what is wanted. Do not "tidy" this to null.
            Background = Brushes.Transparent,
        };
        Avalonia.Automation.AutomationProperties.SetName(root, $"{snapshot.DisplayName} crop item");

        var image = new Image
        {
            Width = snapshot.Width,
            Height = snapshot.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };

        if (File.Exists(snapshot.CropImagePath))
        {
            using var fs = File.OpenRead(snapshot.CropImagePath);
            image.Source = new Bitmap(fs);
        }

        var border = new Rectangle
        {
            Width = snapshot.Width + 4,
            Height = snapshot.Height + 4,
            Stroke = Brushes.Black,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(border, -2);
        Canvas.SetTop(border, -2);

        // HANDLECURSOR_01 — the two resize handles carried StandardCursorType.SizeAll, which is the
        // SAME cursor the item root uses for "drag me". Hovering a corner therefore looked identical
        // to hovering the middle, so nothing told the user the corners resize. Diagonal arrows, the
        // universal resize affordance, and they point along the axis each corner actually moves.
        var tlHandle = CreateHandle(StandardCursorType.TopLeftCorner);
        var brHandle = CreateHandle(StandardCursorType.BottomRightCorner);

        var labelText = new TextBlock
        {
            Text = "",
            Foreground = Brushes.White,
            FontSize = Infrastructure.ThemeManager.ScaledFontSize(18),
            FontWeight = FontWeight.Bold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3),
            Child = labelText,
            IsHitTestVisible = false
        };

        root.Children.Add(image);
        root.Children.Add(border);
        root.Children.Add(tlHandle);
        root.Children.Add(brHandle);
        root.Children.Add(label);

        var item = new CropEditorItem
        {
            Model = snapshot,
            Root = root,
            Image = image,
            Border = border,
            TopLeftHandle = tlHandle,
            BottomRightHandle = brHandle,
            LabelHost = label,
            LabelText = labelText
        };

        root.Tag = item;
        tlHandle.Tag = ResizeHandle.TopLeft;
        brHandle.Tag = ResizeHandle.BottomRight;

        root.PointerPressed += Item_PointerPressed;
        root.PointerMoved += Item_PointerMoved;
        root.PointerReleased += Item_PointerReleased;

        root.ContextMenu = BuildItemContextMenu();

        if (_portraitCanvas != null)
        {
            _portraitCanvas.Children.Add(root);
        }

        ApplyItemLayout(item);
        UpdateItemVisual(item);
        return item;
    }

    /// <summary>
    /// ITEMMENU_01 - the per-element right-click menu.
    ///
    /// Built fresh per item rather than shared, because Avalonia's ContextMenu carries its own
    /// placement target: one instance attached to several items would open against whichever it
    /// was last attached to. The commands act on SelectedView, and opening the menu selects the
    /// item first (the right-click is routed through Item_PointerPressed), so "the one I
    /// right-clicked" and "the selected one" are always the same element.
    /// </summary>
    private ContextMenu BuildItemContextMenu()
    {
        var bringForward = new MenuItem { Header = "Bring Forward" };
        bringForward.Click += (_, _) => MoveSelectedLayer(1);

        var sendBackward = new MenuItem { Header = "Send Backward" };
        sendBackward.Click += (_, _) => MoveSelectedLayer(-1);

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteSelectedItem();

        return new ContextMenu
        {
            ItemsSource = new List<object>
            {
                bringForward,
                sendBackward,
                new Separator(),
                delete,
            }
        };
    }

    private Rectangle CreateHandle(StandardCursorType cursor)
    {
        return new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            Fill = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red),
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Cursor = new Cursor(cursor),
            IsVisible = false,
            ZIndex = 20
        };
    }

    private void Item_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Canvas root || root.Tag is not CropEditorItem item || _portraitCanvas == null)
        {
            return;
        }

        SelectItem(item);
        root.Focus();
        _activeEditItem = item;
        _editStartSnapshot = CaptureSnapshot();
        _editPointerStart = e.GetPosition(_portraitCanvas);
        _editStartX = item.X;
        _editStartY = item.Y;
        _editStartWidth = item.Width;
        _editStartHeight = item.Height;

        _editSourceAspect = SourceAspectOf(item);

        if (e.Source is Control source && source.Tag is ResizeHandle handle)
        {
            _composerEditMode = handle == ResizeHandle.TopLeft ? ComposerEditMode.ResizeTopLeft : ComposerEditMode.ResizeBottomRight;
        }
        else
        {
            _composerEditMode = ComposerEditMode.Drag;
        }

        if (this.FindControl<Grid>("RuleOfThirdsGrid") is Grid grid)
            grid.Opacity = 1;

        e.Pointer.Capture(root);
        e.Handled = true;
    }

    private void Item_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeEditItem == null || _composerEditMode == ComposerEditMode.None || _portraitCanvas == null)
        {
            return;
        }

        Point p = e.GetPosition(_portraitCanvas);
        double dx = p.X - _editPointerStart.X;
        double dy = p.Y - _editPointerStart.Y;

        if (_composerEditMode == ComposerEditMode.Drag)
        {
            double x = _editStartX + dx;
            double y = _editStartY + dy;
            (x, y) = SnapPosition(_activeEditItem, x, y, _activeEditItem.Width, _activeEditItem.Height);
            (int ix, int iy) = CropEditSession.ClampOverlay(x, y, _activeEditItem.Width, _activeEditItem.Height);
            _activeEditItem.X = ix;
            _activeEditItem.Y = iy;
        }
        else if (_composerEditMode == ComposerEditMode.ResizeBottomRight)
        {
            ResizeFromBottomRight(_activeEditItem, dx, dy);
        }
        else if (_composerEditMode == ComposerEditMode.ResizeTopLeft)
        {
            ResizeFromTopLeft(_activeEditItem, dx, dy);
        }

        ApplyItemLayout(_activeEditItem);
        _edit.Dirty = true;
        RefreshActionButtons();
        e.Handled = true;
    }

    private void Item_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeEditItem == null)
        {
            return;
        }

        e.Pointer.Capture(null);
        ClearGuides();

        if (this.FindControl<Grid>("RuleOfThirdsGrid") is Grid grid)
            grid.Opacity = 0;

        CropLayoutSnapshot current = CaptureSnapshot();
        if (_editStartSnapshot != null && !_editStartSnapshot.Equals(current))
        {
            MarkDirty();
            PushHistory(current, CropHistoryLabels.ForPointerGesture(_composerEditMode != ComposerEditMode.Drag));
            RefreshLayerList();
        }

        _activeEditItem = null;
        _composerEditMode = ComposerEditMode.None;
        _editStartSnapshot = null;
        e.Handled = true;
    }

    /// <summary>RESIZEFEEL_01 — the ratio a HUD element must keep, taken from the SOURCE crop (the session's rule).</summary>
    private double SourceAspectOf(CropEditorItem item) => _edit.SourceAspectOf(item.Model);


    /// <summary>
    /// RESIZEFEEL_01 — resize to a target width with the TOP-LEFT corner pinned, ratio locked.
    ///
    /// Extracted so the pointer drag and the keyboard nudge share one body. They used to share a
    /// method that read the drag-gesture fields directly, which is why the keyboard path was
    /// quietly wrong — see <see cref="NudgeSelectedItemSize"/>. Everything either of them needs is
    /// now a parameter, so neither can pick up the other's leftovers.
    /// </summary>
    /// <param name="anchorX">Portrait X the box grows from. Stays put.</param>
    /// <param name="anchorY">Portrait Y the box grows from. Stays put.</param>
    private void ApplyResizeAnchoredTopLeft(CropEditorItem item, double width, double aspect, double anchorX, double anchorY)
    {
        width = Math.Max(MinItemSize, width);
        double height = width * aspect;

        if (anchorX + width > PortraitWidth)
        {
            width = PortraitWidth - anchorX;
            height = width * aspect;
        }

        if (anchorY + height > ContentBottom)
        {
            height = ContentBottom - anchorY;
            width = aspect > 1e-6 ? height / aspect : width;
        }

        var quantized = _edit.QuantizeLayerSize(item.SourceRect, Math.Max(MinItemSize, width), item.RoleKey);
        item.Width = quantized.width;
        item.Height = quantized.height;
        (item.X, item.Y) = CropEditSession.ClampOverlay(anchorX, anchorY, item.Width, item.Height);
    }

    private void ResizeFromBottomRight(CropEditorItem item, double dx, double dy)
    {
        double aspect = _editSourceAspect;
        ApplyResizeAnchoredTopLeft(
            item,
            _editStartWidth + DiagonalWidthDelta(dx, dy, aspect),
            aspect,
            _editStartX,
            _editStartY);
    }

    /// <summary>
    /// RESIZEFEEL_01 — Ctrl+Arrow resize, from the keyboard.
    ///
    /// THIS USED TO CALL ResizeFromBottomRight DIRECTLY, AND THAT WAS A BUG, not just a signature
    /// mismatch. That method sizes from <c>_editStartWidth</c> anchored at
    /// <c>_editStartX/_editStartY</c> and locks to <c>_editSourceAspect</c> — and all four of those
    /// fields are written by <see cref="Item_PointerPressed"/> and by nothing else. A keyboard
    /// resize performed without a preceding mouse drag therefore sized the selected item from
    /// whatever the LAST DRAGGED item's dimensions happened to be, anchored at that item's corner,
    /// locked to that item's ratio. On a fresh window the fields are all zero, so the first
    /// Ctrl+Arrow collapsed the box to the minimum size and moved it to the top-left corner.
    ///
    /// Reading everything from the item being resized, right now, is the fix. There is no gesture
    /// in progress, so there is no gesture state to consult.
    /// </summary>
    private void NudgeSelectedItemSize(CropEditorItem item, double widthDelta)
    {
        ApplyResizeAnchoredTopLeft(
            item,
            item.Width + widthDelta,
            SourceAspectOf(item),
            item.X,
            item.Y);
    }

    private void ResizeFromTopLeft(CropEditorItem item, double dx, double dy)
    {
        double aspect = _editSourceAspect;
        int anchorRight = (int)(_editStartX + _editStartWidth);
        int anchorBottom = (int)(_editStartY + _editStartHeight);

        double width = Math.Max(MinItemSize, _editStartWidth - DiagonalWidthDelta(dx, dy, aspect));
        double height = width * aspect;

        if (anchorRight - width < 0)
        {
            width = anchorRight;
            height = width * aspect;
        }

        if (anchorBottom - height < ContentTop)
        {
            height = anchorBottom - ContentTop;
            width = aspect > 1e-6 ? height / aspect : width;
        }

        var quantized = _edit.QuantizeLayerSize(item.SourceRect, Math.Max(MinItemSize, width), item.RoleKey);
        item.Width = quantized.width;
        item.Height = quantized.height;
        item.X = Math.Max(0, anchorRight - item.Width);
        item.Y = Math.Max((int)ContentTop, anchorBottom - item.Height);
        (item.X, item.Y) = CropEditSession.ClampOverlay(item.X, item.Y, item.Width, item.Height);
    }

    /// <summary>Normalises the layer's layout (the session's rule) and places its visuals to match.</summary>
    private void ApplyItemLayout(CropEditorItem item)
    {
        _edit.NormalizeLayout(item.Model);
        RenderItemLayout(item);
    }

    /// <summary>Places an element's VISUALS at its layer's layout. No logic: geometry is already settled.</summary>
    private void RenderItemLayout(CropEditorItem item)
    {
        item.Root.Width = item.Width;
        item.Root.Height = item.Height;
        item.Root.ZIndex = item.Z;
        Canvas.SetLeft(item.Root, item.X);
        Canvas.SetTop(item.Root, item.Y);

        item.Image.Width = item.Width;
        item.Image.Height = item.Height;
        item.Border.Width = item.Width + 4;
        item.Border.Height = item.Height + 4;
        Canvas.SetLeft(item.Border, -2);
        Canvas.SetTop(item.Border, -2);

        Canvas.SetLeft(item.TopLeftHandle, -HandleSize / 2);
        Canvas.SetTop(item.TopLeftHandle, -HandleSize / 2);
        Canvas.SetLeft(item.BottomRightHandle, item.Width - HandleSize / 2);
        Canvas.SetTop(item.BottomRightHandle, item.Height - HandleSize / 2);

        double labelWidth = Math.Clamp(Math.Max(96, item.Width), 96, PortraitWidth - 20);
        double labelLeft = (item.Width - labelWidth) / 2;
        labelLeft = Math.Clamp(labelLeft, -item.X + 10, PortraitWidth - item.X - labelWidth - 10);
        item.LabelHost.Width = labelWidth;
        Canvas.SetLeft(item.LabelHost, labelLeft);
        Canvas.SetTop(item.LabelHost, item.Y > PortraitHeight / 2 ? -34 : item.Height + 10);
        item.LabelText.Text = item.DisplayName.ToUpperInvariant();
    }


    private void SelectItem(CropEditorItem? item, bool updateLayerList = true)
    {
        SelectedView = item;
        foreach (CropEditorItem editorItem in _items)
        {
            UpdateItemVisual(editorItem);
        }

        if (updateLayerList && _layerList != null)
        {
            _suppressLayerSelection = true;
            _layerList.SelectedItem = _layers.FirstOrDefault(l => item != null && l.RoleKey == item.RoleKey);
            _suppressLayerSelection = false;
        }

        RefreshActionButtons();
    }

    private void UpdateItemVisual(CropEditorItem item)
    {
        bool selected = ReferenceEquals(item, SelectedView);
        item.Border.Stroke = selected ? Brushes.Gold : Brushes.Black;
        item.Border.StrokeThickness = 2;
        item.TopLeftHandle.IsVisible = selected;
        item.BottomRightHandle.IsVisible = selected;
        item.LabelHost.Background = selected
            ? new SolidColorBrush(Color.FromArgb(220, 113, 63, 18))
            : new SolidColorBrush(Color.FromArgb(210, 0, 0, 0));
    }


    private void DeleteSelectedItem()
    {
        CropEditorItem? view = SelectedView;
        if (_edit.DeleteSelected() == null) return;
        if (view != null) RemoveItem(view);
        SelectItem(null);
        RefreshLayerList();
        RefreshActionButtons();
        PushHistory(CropHistoryLabels.DeleteElement);
    }

    /// <summary>Releases an element's VISUALS. The layer itself is the session's to remove.</summary>
    private void RemoveItem(CropEditorItem item)
    {
        if (_portraitCanvas != null)
        {
            _portraitCanvas.Children.Remove(item.Root);
        }

        try
        {
            (item.Image.Source as IDisposable)?.Dispose();
            item.Image.Source = null;
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        _items.Remove(item);
    }


    /// <summary>
    /// ZCOLLIDE_01 — one press moves the element exactly one place, by swapping with its neighbour
    /// (the rule is the session's; this repaints the two layers that changed).
    /// </summary>
    private void MoveSelectedLayer(int delta)
    {
        if (_edit.MoveSelectedLayer(delta) is not { } swapped) return;

        foreach (CropEditorItem view in _items.Where(i => ReferenceEquals(i.Model, swapped.Moved) || ReferenceEquals(i.Model, swapped.Neighbour)))
            ApplyItemLayout(view);
        RefreshLayerList();
        RefreshActionButtons();
        PushHistory(CropHistoryLabels.ChangeLayerOrder);
    }

    private void RefreshLayerList()
    {
        string? selectedKey = SelectedView?.RoleKey;
        _layers.Clear();
        foreach (CropLayer layer in _edit.ListOrder)
        {
            _layers.Add(new LayerEntry(layer.RoleKey, layer.DisplayName, layer.Z));
        }

        if (_layerList != null && selectedKey != null)
        {
            _layerList.SelectedItem = _layers.FirstOrDefault(l => l.RoleKey == selectedKey);
        }

        var emptyLayers = this.FindControl<TextBlock>("EmptyLayersText");
        if (emptyLayers != null) emptyLayers.IsVisible = _layers.Count == 0;

        UpdateComposerEmptyState();

        RefreshActionButtons();
    }

    /// <summary>
    /// IDEA_1 — turns previously saved layers back into REAL, draggable items. EDITSTATE_01 — what
    /// the document says (and the DRIFT TRAP / RESGUESS_01 rules for reading it) is
    /// <see cref="CropProfileCodec.ReadSavedLayers"/>; this window loads the file, cuts each
    /// thumbnail and builds the visuals. Safe to call more than once: a placed role is skipped.
    /// </summary>
    private async Task RehydrateSavedLayersAsync(JsonObject? profileConfig = null)
    {
        if (_portraitCanvas == null) return;

        try
        {
            JsonObject config = profileConfig != null
                ? HudConfig.Sanitize(profileConfig, migrateLegacy: true)
                : HudConfig.Sanitize(await new CropConfigStore(_paths).LoadAsync(), migrateLegacy: true);

            foreach (CropLayer layer in CropProfileCodec.ReadSavedLayers(config, _edit))
            {
                if (_snapshotPath != null)
                {
                    try { layer.CropImagePath = await CropSnapshotRegionAsync(_snapshotPath, layer.SourceRect); }
                    catch (Exception ex) { RuntimeLog.Info("CROP", $"Thumbnail for '{layer.RoleKey}' could not be built: {ex.Message}"); }
                    if (!string.IsNullOrEmpty(layer.CropImagePath)) _tempFiles.Add(layer.CropImagePath);
                }

                _edit.AdoptSavedLayer(layer);
                _items.Add(CreateItem(layer));
            }

            RefreshLayerList();
            RefreshActionButtons();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Saved layers could not be reopened for editing: {ex.Message}");
        }
    }

    /// <summary>
    /// IDEA_1 — fills in the picture for any item rehydrated before a video was loaded.
    /// Called after a snapshot is available. Items that already have an image are left alone.
    /// </summary>
    private async Task RefreshRehydratedThumbnailsAsync()
    {
        if (_snapshotPath == null) return;

        foreach (CropEditorItem item in _items.ToList())
        {
            if (_edit.TryVerifyGeometry(item.Model)) ApplyItemLayout(item);

            if (!string.IsNullOrEmpty(item.CropImagePath) && File.Exists(item.CropImagePath)) continue;

            try
            {
                string path = await CropSnapshotRegionAsync(_snapshotPath, item.SourceRect);
                if (string.IsNullOrEmpty(path)) continue;

                _tempFiles.Add(path);
                item.CropImagePath = path;

                using var fs = File.OpenRead(path);
                item.Image.Source = new Bitmap(fs);
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("CROP", $"Thumbnail refresh failed for '{item.RoleKey}': {ex.Message}");
            }
        }
    }


    /// <summary>
    /// ISSUE_07 (audit round 6) — shows the composer's empty state whenever nothing is placed.
    ///
    /// The landscape half of this window has had a large "UPLOAD VIDEO" hint since day one. The
    /// portrait half had nothing at all: after a profile was chosen but before the first crop, the
    /// whole right side was a blank 9:16 rectangle, and the only guidance anywhere near it was a
    /// tooltip on the canvas that the user had to hover to find. An empty panel with no words in it
    /// does not read as empty, it reads as broken.
    ///
    /// The wording changes with the stage, because "what do I do next" has two different answers
    /// depending on whether a video is open yet.
    /// </summary>
    private void UpdateComposerEmptyState()
    {
        if (_composerEmptyState == null) return;

        bool empty = _items.Count == 0;
        _composerEmptyState.IsVisible = empty;
        if (!empty) return;

        bool hasVideo = !string.IsNullOrWhiteSpace(_videoPath);

        if (_composerEmptyStateTitle != null)
        {
            _composerEmptyStateTitle.Text = hasVideo ? "NOTHING PLACED YET" : "YOUR PORTRAIT VIDEO";
        }

        if (_composerEmptyStateBody != null)
        {
            _composerEmptyStateBody.Text = hasVideo
                ? "Drag a box round a HUD piece on the left and say what it is. It will appear here, where you can move it, resize it and stack it."
                : "This is the shape your viewers will see. Open a clip on the left to start picking out the HUD pieces that belong in it.";
        }
    }


    private async Task SaveAndReturnAsync(object? sender)
    {
        if (_isSaving || _changingProfile) return;

        string? targetProfile = _verifiedEditingProfile ?? _edit.ActiveProfile;
        if (string.IsNullOrWhiteSpace(targetProfile))
        {
            SetStatus("Choose a profile before saving.");
            return;
        }

        // SAVECONFIRM_01 / CROPSAVEPROMPT_02 — name the destination and the action.
        // Saving is the primary action; going back keeps the current edits open.
        // "KEEP IT" was ambiguous about whether it kept the saved profile or the new work.
        bool confirmed = await Controls.ConfirmDialogWindow.AskAsync(
            this,
            $"Save this layout to \"{targetProfile}\" and return to the main app?\n\n" +
            "This updates the saved layout for this profile.",
            "Save your changes?",
            yesText: "Save changes",
            noText: "Back to editing");

        if (!confirmed)
        {
            SetStatus("Your changes are still here. Press Finish & Save when you are ready.");
            return;
        }

        Button? button = sender as Button;
        if (button != null)
        {
            button.IsEnabled = false;
        }

        var thinkingOverlay = this.FindControl<Grid>("ThinkingOverlay");
        if (thinkingOverlay != null) thinkingOverlay.IsVisible = true;
        
        await Task.Yield();

        bool saved = await SaveConfigAsync();

        if (thinkingOverlay != null) thinkingOverlay.IsVisible = false;

        if (saved)
        {
            var summaryOverlay = this.FindControl<Grid>("SummaryOverlay");
            if (summaryOverlay != null)
            {
                var summaryContent = this.FindControl<StackPanel>("SummaryContent");
                if (summaryContent != null)
                {
                    summaryContent.Children.Clear();
                    var headerModBorder = new Border { Background = SolidColorBrush.Parse("#1e10b981"), Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
                    headerModBorder.Child = new TextBlock { Text = "  MODIFIED ELEMENTS", Foreground = SolidColorBrush.Parse("#10b981"), FontWeight = FontWeight.Bold, FontSize = Infrastructure.ThemeManager.ScaledFontSize(14) };
                    summaryContent.Children.Add(headerModBorder);
                    foreach(var item in _items)
                    {
                        summaryContent.Children.Add(new TextBlock { Text = $"  ✓  {item.DisplayName}", Foreground = SolidColorBrush.Parse("#94a3b8"), FontSize = Infrastructure.ThemeManager.ScaledFontSize(16) });
                    }
                    
                    var existingKeys = _items.Select(x => x.RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var untouched = _edit.AllRoles.Where(r => !existingKeys.Contains(r.Key)).ToList();
                    
                    if (untouched.Count > 0)
                    {
                        summaryContent.Children.Add(new Border { Height = 20 });
                        var headerUnBorder = new Border { Background = SolidColorBrush.Parse("#1e9ca3af"), Padding = new Thickness(8), CornerRadius = new CornerRadius(4) };
                        headerUnBorder.Child = new TextBlock { Text = "  UNTOUCHED (DEFAULTS)", Foreground = SolidColorBrush.Parse("#9ca3af"), FontWeight = FontWeight.Bold, FontSize = Infrastructure.ThemeManager.ScaledFontSize(14) };
                        summaryContent.Children.Add(headerUnBorder);
                        foreach(var u in untouched)
                        {
                            summaryContent.Children.Add(new TextBlock { Text = $"  •  {u.DisplayName}", Foreground = SolidColorBrush.Parse("#9ca3af"), FontSize = Infrastructure.ThemeManager.ScaledFontSize(15) });
                        }
                    }
                }
                
                summaryOverlay.IsVisible = true;

                var returnNowButton = this.FindControl<Button>("ReturnNowButton");
                var returnTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnReturnNow(object? s, RoutedEventArgs e) => returnTcs.TrySetResult(true);
                void OnOverlayPointer(object? s, Avalonia.Input.PointerPressedEventArgs e) => returnTcs.TrySetResult(true);
                void OnOverlayKey(object? s, Avalonia.Input.KeyEventArgs e)
                {
                    if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space or Avalonia.Input.Key.Escape)
                    {
                        returnTcs.TrySetResult(true);
                    }
                }

                if (returnNowButton != null) returnNowButton.Click += OnReturnNow;
                summaryOverlay.PointerPressed += OnOverlayPointer;
                summaryOverlay.KeyDown += OnOverlayKey;
                summaryOverlay.Focus();

                try
                {
                    using var cts = new CancellationTokenSource(1000);
                    cts.Token.Register(() => returnTcs.TrySetResult(false));
                    await returnTcs.Task;
                }
                finally
                {
                    if (returnNowButton != null) returnNowButton.Click -= OnReturnNow;
                    summaryOverlay.PointerPressed -= OnOverlayPointer;
                    summaryOverlay.KeyDown -= OnOverlayKey;
                }
            }
            await ReturnToMainAppAsync();
            return;
        }

        if (button != null)
        {
            button.IsEnabled = true;
            button.Content = "FINISH & SAVE";
        }
        RefreshActionButtons();
    }

    private async Task<bool> SaveConfigAsync()
    {
        if (_isSaving || _changingProfile) return false;

        string? targetProfile = _verifiedEditingProfile ?? _edit.ActiveProfile;
        if (string.IsNullOrWhiteSpace(targetProfile))
        {
            SetStatus("Choose a profile before saving.");
            return false;
        }

        if (_edit.Layers.Count == 0 && _edit.DeletedRoleKeys.Count == 0)
        {
            SetStatus("No HUD elements are currently placed.");
            return false;
        }

        try
        {
            _isSaving = true;
            RuntimeLog.Info("CROP", $"Saving crop coordinates for '{targetProfile}'.");
            var store = new CropConfigStore(_paths);
            
            JsonObject config = await store.LoadAsync();

            int unverifiedCount = CropProfileCodec.WriteLayers(config, _edit);
            foreach (CropEditorItem item in _items) RenderItemLayout(item);

            if (unverifiedCount > 0)
            {
                SetStatus(unverifiedCount == _items.Count
                    ? "Saved position and layer order only. Load the video to edit the crop rectangles."
                    : $"Saved. {unverifiedCount} element(s) kept their stored crop rectangles — load the video to edit those.");
            }

            config = HudConfig.Sanitize(config);
            await store.SaveAsync(config);

            // CROPPROFILE_01 — explicitly sync to targetProfile, not global ActiveMaskOverlay!
            if (!FreeVideoStudio.App.Infrastructure.MaskOverlayManager.SyncProfileFromCurrentConfig(targetProfile))
            {
                SetStatus("Could not save this profile. Your edits are still open. Please try saving again.");
                return false;
            }

            _edit.Dirty = false;
            RefreshActionButtons();
            RuntimeLog.Success("CROP", $"Saved crop coordinates successfully to '{targetProfile}'.");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            RuntimeLog.Fail("CROP", ex);
            SetStatus("Save refused: " + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", ex);
            SetStatus("Save failed. See runtime log.");
            return false;
        }
        finally
        {
            _isSaving = false;
        }
    }

    // CROPUNSAVED_01 — used by profile switches, Return, and the window close button,
    // including an empty layout after the last element was deleted.
    private async Task<bool> ConfirmUnsavedChangesAsync(string destination)
    {
        if (_unsavedPromptOpen) return false;
        if (!_edit.Dirty) return true;
        _unsavedPromptOpen = true;
        try
        {
            string profileName = _verifiedEditingProfile ?? _edit.ActiveProfile ?? "this profile";
            var choice = await Controls.ConfirmDialogWindow.AskSaveChangesAsync(
                this, profileName, destination);
            return choice switch
            {
                Controls.ConfirmDialogWindow.SaveChangesChoice.Save => await SaveConfigAsync(),
                Controls.ConfirmDialogWindow.SaveChangesChoice.Discard => true,
                _ => false
            };
        }
        finally { _unsavedPromptOpen = false; }
    }

    private async Task ReturnToMainAppAsync()
    {
        if (_returningToMainApp || _closeInProgress || _changingProfile ||
            !await ConfirmUnsavedChangesAsync("returning to the main app")) return;
        _returningToMainApp = true;
        try
        {
            _recovery.ReleaseLockOnly();
            RuntimeLog.Info("CROP", "Returning to Main app.");

            if (Services.ToolNavigator.OpenedInProcess)
            {
                NavigationResult = new Services.ToolNavigationResult("Crop Tools", ReturnedToOwner: true, SelectedClipPath: _videoPath);
                RuntimeLog.Info("CROP", "Opened in-process - closing to reveal the editor (TOOLNAV_04).");
                Close();
                return;
            }

            var store = new StateTransferStore(_paths);
            await store.SendHandoffAsync(new FreeVideoStudio.Core.Ipc.HandoffPayload
            {
                SourceProcess = "CropTool",
                TargetProcess = "MainWindow",
                ReturnedFromCropTool = true,
                SelectedClipPath = _videoPath
            });


            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "FreeVideoStudio.exe";

            // RELAUNCHARG_01 — "run-ui" is NOT decoration. This line used to start the exe with NO
            // arguments, and an argument-less launch is how this suite says "I am a standalone
            // installer": DeploymentFootprint.IsStandaloneInstallerHost is
            //     args.Length == 0 && !IsRunningFromInstallPath()
            // so Program.cs line 7 handed the new process to DeploymentLifecycle, which opened an
            // INSTALL LAUNCHER session and asked Windows for Administrator. Pressing FINISH & SAVE
            // in the Crop Tools raised a UAC prompt, and declining it killed the relaunch.
            //
            // It is invisible in an installed build, because IsRunningFromInstallPath() is true
            // there and the first condition never fires — it only bites in a dev build or a copy
            // run from anywhere else, which is exactly where it was found.
            //
            // The outbound direction always got this right: CompanionAppService starts the
            // companion with "--crop-tool". This is the return leg being made symmetrical.
            // Program.cs handles "run-ui" explicitly, and any non-empty argv settles the question.
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "run-ui") { UseShellExecute = false });
            if (p != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (int i = 0; i < 40; i++)
                        {
                            if (p.HasExited) break;
                            p.Refresh();
                            if (p.MainWindowHandle != IntPtr.Zero) break;
                            await Task.Delay(50);
                        }
                    }
                    catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Close());
                });
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Close());
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", "Error returning to main window: " + ex.Message);
            Close();
        }
    }

    /// <param name="tombstonePlacedElements">
    /// EMPTYSAVE_01 — true only for the RESET button. RESET is a deliberate "clear this profile"
    /// gesture, so every element it removes gets a tombstone and the document is left dirty, which
    /// is what lets the user press SAVE afterwards and actually empty the profile on disk.
    /// A PROFILE SWITCH passes false: nothing was deleted there, the items merely belong to a
    /// different document, and tombstoning them would zero the incoming profile's crops.
    /// </param>
    private void ResetWorkingState(bool tombstonePlacedElements = false)
    {
        ClearSourceSelection();
        ClearMagicWandCandidates();
        _wandPreviewIndex = -1;

        _edit.ResetWorking(tombstonePlacedElements);
        foreach (CropEditorItem item in _items.ToList())
        {
            RemoveItem(item);
        }

        SelectItem(null);
        RefreshLayerList();
        RefreshUndoRedoButtons();
        RefreshActionButtons();
        SetStatus(_edit.Dirty
            ? "Working crop items cleared. Press SAVE to apply the empty layout to this profile."
            : "Working crop items cleared.");
    }


    private void MarkDirty(bool pushHistory = false)
    {
        _edit.Dirty = true;
        if (pushHistory)
        {
            PushHistory("edit crop layout");
        }
        RefreshActionButtons();
    }

    private void RefreshActionButtons()
    {
        bool profileChosen = _edit.ActiveProfile != null;

        SetEnabled("SaveButton", _edit.CanSave);

        // DELETEBTN_01: this line used to read SetEnabled("DeleteSelectedButton", ...). There has
        // never been a control by that name in CropToolWindow.axaml - the button is DeleteMenuButton
        // - and SetEnabled resolves through FindControl, which returns null and returns silently for
        // a name that does not exist. So the call did nothing, DELETE kept the IsEnabled="False" it
        // is declared with, and the only way to remove a layer was RESET (which wipes all of them).
        // It failed silently in exactly the way 04_UI_UX_AVALONIA_SPEC.md#UI-THEME describes for
        // the QualityLabel dead readout: the feature looked MISSING rather than broken.
        SetEnabled("DeleteMenuButton", profileChosen && SelectedView != null);

        SetEnabled("RaiseButton", profileChosen && SelectedView != null);
        SetEnabled("LowerButton", profileChosen && SelectedView != null);
        RefreshUndoRedoButtons();
    }

    private async Task SeekInternal(double time)
    {
        if (_isSeeking)
        {
            _nextSeekTarget = time;
            return;
        }
        _isSeeking = true;
        try
        {
            if (_videoHost?.IpcClient != null)
            {
                await _videoHost.IpcClient.SendCommandAsync("seek", time, "absolute");
            }
        }
        finally
        {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue)
            {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                _ = SeekInternal(target);
            }
        }
    }

    private void SeekTimelineFromPointer(PointerEventArgs e, Canvas timelineCanvas, Slider timelineSlider)
    {
        if (e.Handled) return;

        double duration = _videoHost?.IpcClient?.Duration ?? 0.0;
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

    private void ShowPlayheadBadge(double timeSeconds, double sliderValuePercentage)
    {
        var badge = this.FindControl<Avalonia.Controls.Border>("PlayheadBadge");
        var text = this.FindControl<Avalonia.Controls.TextBlock>("PlayheadBadgeText");
        var canvas = this.FindControl<Avalonia.Controls.Canvas>("CropTimelineMarkersCanvas");

        if (badge != null && text != null && canvas != null)
        {
            text.Text = FormatTime(timeSeconds * 1000.0);
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

    private void UpdateTimelineUi()
    {
        if (_timelineSlider == null)
        {
            return;
        }

        double currentMs = GetCurrentTimeMs();
        if (_durationMs <= 0)
        {
            _durationMs = Math.Max(0, (_videoHost?.IpcClient?.Duration ?? 0) * 1000.0);
        }

        _isTimerUpdatingSlider = true;
        try
        {
            if (_durationMs > 0)
            {
                double targetPercentage = (currentMs / _durationMs) * 100.0;
                if (Math.Abs(_timelineSlider.Value - targetPercentage) > 0.5 && !_isSeeking)
                {
                    _timelineSlider.Value = Math.Max(0, Math.Min(targetPercentage, 100.0));
                }
            }
        }
        finally
        {
            _isTimerUpdatingSlider = false;
        }

        if (_currentTimeLabel != null)
        {
            _currentTimeLabel.Text = FormatTime(currentMs);
        }
        if (_totalTimeLabel != null)
        {
            _totalTimeLabel.Text = FormatTime(_durationMs);
        }

        if (_videoHost?.IpcClient != null)
        {
            UpdatePlayPauseIcon(_videoHost.IpcClient.IsPaused);
        }

        DrawTimelineRuler();
    }

    /// <summary>
    /// TICKRULER_01 - the tick ruler, ported from the Main App (MainWindow.Canvas.cs,
    /// UpdateTimelineMarkers).
    ///
    /// This window already declared both canvases the Main App uses - CropTimelineScaleCanvas for
    /// the time labels and CropTimelineMarkersCanvas for the hairlines - and FindControls has
    /// always looked the first one up. NOTHING EVER DREW INTO EITHER. The result was a bare blue
    /// line with a dot on it: no sense of scale, no way to judge where 30 seconds is, which is
    /// what "naked/skinless" was describing.
    ///
    /// The interval ladder is copied exactly from the Main App so the two timelines read the same
    /// way: a mark every 5s on a short clip, widening to 10/30/60/300s as the clip gets longer, so
    /// the ruler never turns into a solid band of ink.
    ///
    /// Redrawn from the 100ms UI tick, so it is guarded three ways: nothing to draw without a
    /// duration, nothing to draw before layout has given the canvas a width, and - the one that
    /// matters - it returns immediately unless the width or the duration actually CHANGED. Without
    /// that guard this would rebuild several dozen visuals ten times a second forever.
    /// </summary>
    private double _rulerDrawnWidth = -1;
    private double _rulerDrawnDuration = -1;

    private void DrawTimelineRuler()
    {
        var markers = this.FindControl<Canvas>("CropTimelineMarkersCanvas");
        if (markers == null || _timelineCanvas == null) return;

        double duration = _durationMs / 1000.0;
        double width = markers.Bounds.Width;
        if (duration <= 0 || width <= 10) return;

        if (Math.Abs(width - _rulerDrawnWidth) < 0.5 && Math.Abs(duration - _rulerDrawnDuration) < 0.01)
        {
            return;
        }
        _rulerDrawnWidth = width;
        _rulerDrawnDuration = duration;

        var badge = this.FindControl<Border>("PlayheadBadge");
        if (badge != null) markers.Children.Remove(badge);
        markers.Children.Clear();
        if (badge != null) markers.Children.Add(badge);
        _timelineCanvas.Children.Clear();

        double interval = 5;
        if (duration > 3600) interval = 300;
        else if (duration > 1800) interval = 60;
        else if (duration > 300) interval = 30;
        else if (duration > 60) interval = 10;

        double height = Math.Max(1, markers.Bounds.Height);
        var tickBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        var labelBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));

        for (double t = 0; t <= duration; t += interval)
        {
            double x = (t / duration) * width;

            var tick = new Rectangle
            {
                Fill = tickBrush,
                Width = 1,
                Height = height,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tick, x);
            markers.Children.Add(tick);

            if (t <= 0.001 || duration - t <= 0.001) continue;

            var label = new TextBlock
            {
                Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"),
                Foreground = labelBrush,
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, Math.Max(0, Math.Min(width - 36, x + 2)));
            Canvas.SetTop(label, 0);
            _timelineCanvas.Children.Add(label);
        }
    }

    private double GetCurrentTimeMs()
    {
        if (_videoHost?.IpcClient?.CurrentTime > 0)
        {
            return _videoHost.IpcClient.CurrentTime * 1000.0;
        }
        if (_timelineSlider != null && _durationMs > 0)
        {
            return (_timelineSlider.Value / 100.0) * _durationMs;
        }

        return 0;
    }

    private (double x, double y) SnapPosition(CropEditorItem item, double x, double y, double width, double height)
    {
        ClearGuides();
        if (this.FindControl<CheckBox>("SnapToggle")?.IsChecked != true)
        {
            return (x, y);
        }

        var xTargets = new List<(double value, string label)>
        {
            (0, "Canvas Left"),
            (CoordinateMath.ScaleRound(Frac.FromDouble(PortraitWidth / 2.0)), "Canvas Center"),
            (PortraitWidth, "Canvas Right")
        };
        var yTargets = new List<(double value, string label)>
        {
            (ContentTop, "Content Top"),
            (CoordinateMath.ScaleRound(Frac.FromDouble(PortraitHeight / 2.0)), "Canvas Center"),
            (ContentBottom, "Content Bottom")
        };

        foreach (CropEditorItem other in _items.Where(i => !ReferenceEquals(i, item)))
        {
            xTargets.Add((other.X, other.DisplayName + " Left"));
            xTargets.Add((other.X + CoordinateMath.ScaleRound(Frac.FromDouble(other.Width / 2.0)), other.DisplayName + " Center"));
            xTargets.Add((other.X + other.Width, other.DisplayName + " Right"));

            yTargets.Add((other.Y, other.DisplayName + " Top"));
            yTargets.Add((other.Y + CoordinateMath.ScaleRound(Frac.FromDouble(other.Height / 2.0)), other.DisplayName + " Center"));
            yTargets.Add((other.Y + other.Height, other.DisplayName + " Bottom"));
        }

        (double snappedX, double? guideX) = SnapAxis(x, width, xTargets);
        (double snappedY, double? guideY) = SnapAxis(y, height, yTargets);

        if (guideX.HasValue)
        {
            DrawGuide(vertical: true, guideX.Value);
        }
        if (guideY.HasValue)
        {
            DrawGuide(vertical: false, guideY.Value);
        }

        return (snappedX, snappedY);
    }


    private void DrawGuide(bool vertical, double value)
    {
        if (_portraitCanvas == null)
        {
            return;
        }

        var line = new Line
        {
            StartPoint = vertical ? new Point(value, 0) : new Point(0, value),
            EndPoint = vertical ? new Point(value, PortraitHeight) : new Point(PortraitWidth, value),
            Stroke = new SolidColorBrush(Color.Parse("#7dd3fc")),
            StrokeThickness = 2,
            IsHitTestVisible = false,
            ZIndex = 1200
        };

        _portraitCanvas.Children.Add(line);
        _guideControls.Add(line);
    }

    private void ClearGuides()
    {
        if (_portraitCanvas == null)
        {
            _guideControls.Clear();
            return;
        }

        foreach (Control control in _guideControls)
        {
            _portraitCanvas.Children.Remove(control);
        }
        _guideControls.Clear();
    }

    private Point ClampToSnapshot(Point point)
    {
        return new Point(
            Math.Max(0, Math.Min(point.X, _edit.SnapshotWidth)),
            Math.Max(0, Math.Min(point.Y, _edit.SnapshotHeight)));
    }


    private SourceRect ToSourceRect(Rect rect)
    {
        int x = CoordinateMath.ScaleRound(Frac.FromDouble(rect.X));
        int y = CoordinateMath.ScaleRound(Frac.FromDouble(rect.Y));
        int right = CoordinateMath.ScaleRound(Frac.FromDouble(rect.Right));
        int bottom = CoordinateMath.ScaleRound(Frac.FromDouble(rect.Bottom));
        return _edit.ClampSourceRect(new SourceRect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y)));
    }


    private HudRole SuggestRole(SourceRect rect)
    {
        double cx = rect.X + rect.Width / 2.0;
        double cy = rect.Y + rect.Height / 2.0;
        bool right = cx > _edit.SnapshotWidth / 2.0;
        bool bottom = cy > _edit.SnapshotHeight / 2.0;

        return new HudRole("custom_element", "Custom Element", 50, -1, -1);
    }


    private static string FormatTime(double millis)
    {
        if (!double.IsFinite(millis) || millis < 0)
        {
            millis = 0;
        }

        TimeSpan ts = TimeSpan.FromMilliseconds(millis);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The internal wizard states, which are NOT the same thing as the header dots:
    ///   0 Choose Profile   1 Upload Video   2 Find HUD Frame   3 Refine Box   4 Portrait Composer
    /// <see cref="UpdateStepDots"/> owns the mapping onto the four header dots. Step 0 is new with
    /// GATE_01 (F3) - before the gate existed the window opened straight on step 1 because it had
    /// already silently picked a profile for the user.
    /// </summary>
    private void SetWizardState(int step, string goal, string status)
    {
        if (_goalLabel != null)
        {
            _goalLabel.Text = step <= 0 ? goal : $"Step {step}: {goal}";
        }
        UpdateStepDots(step);
        SetStatus(status);
    }

    /// <summary>
    /// ISSUE_03: drives the wizard tracker in the header. Nothing used to call into
    /// Step1Dot/Step2Dot/Step3Dot at all, so the indicator sat frozen on "step 1" for the whole
    /// session while <see cref="SetWizardState"/> silently tracked the real progress next to it.
    ///
    /// F6: the header carries FOUR dots now - Profile / Upload / Crop / Save - because GATE_01
    /// put a real user-facing stage in front of Upload. The internal wizard still has five states,
    /// so steps 2 and 3 (Find HUD Frame / Refine Box) both map onto the single "Crop" dot:
    ///
    ///   step 0        -> dot 0  Profile
    ///   step 1        -> dot 1  Upload
    ///   steps 2 and 3 -> dot 2  Crop
    ///   step 4        -> dot 3  Save
    ///
    /// Keep this table and the AXAML dot count in step; a fifth dot with no mapping would simply
    /// never light up, which is the failure the three-dot version shipped with.
    /// </summary>
    private void UpdateStepDots(int step)
    {
        int stage = step <= 0 ? 0
                  : step == 1 ? 1
                  : step >= 4 ? 3
                  : 2;

        var dots = new[]
        {
            (Dot: this.FindControl<Border>("Step1Dot"),
             Icon: this.FindControl<TextBlock>("Step1Icon"),
             Label: this.FindControl<TextBlock>("Step1Label"), Numeral: "1"),
            (Dot: this.FindControl<Border>("Step2Dot"),
             Icon: this.FindControl<TextBlock>("Step2Icon"),
             Label: this.FindControl<TextBlock>("Step2Label"), Numeral: "2"),
            (Dot: this.FindControl<Border>("Step3Dot"),
             Icon: this.FindControl<TextBlock>("Step3Icon"),
             Label: this.FindControl<TextBlock>("Step3Label"), Numeral: "3"),
            (Dot: this.FindControl<Border>("Step4Dot"),
             Icon: this.FindControl<TextBlock>("Step4Icon"),
             Label: this.FindControl<TextBlock>("Step4Label"), Numeral: "4"),
        };

        for (int i = 0; i < dots.Length; i++)
        {
            var (dot, icon, label, numeral) = dots[i];
            if (dot == null || icon == null || label == null) continue;

            string dotClass = i < stage ? "WizDotDone" : i == stage ? "WizDotActive" : "WizDotPending";
            string iconClass = i <= stage ? "WizIconOn" : "WizIconPending";
            string labelClass = i < stage ? "WizLabelDone" : i == stage ? "WizLabelActive" : "WizLabelPending";

            SwapClass(dot, dotClass, "WizDotPending", "WizDotActive", "WizDotDone");
            SwapClass(icon, iconClass, "WizIconPending", "WizIconOn");
            SwapClass(label, labelClass, "WizLabelPending", "WizLabelActive", "WizLabelDone");

            icon.Text = i < stage ? "\u2713" : numeral;
        }
    }

    /// <summary>Applies exactly one of a mutually exclusive class group to a control.</summary>
    private static void SwapClass(StyledElement target, string keep, params string[] group)
    {
        foreach (var c in group)
        {
            if (c == keep) continue;
            target.Classes.Remove(c);
        }
        if (!target.Classes.Contains(keep)) target.Classes.Add(keep);
    }

    /// <summary>
    /// ISSUE_09 — writes the persistent status line AND floats the suite-wide notice.
    ///
    /// The line is deliberately kept: it holds the last message on screen indefinitely, which is
    /// what you want while you are reading a rejection ("Selection is too small") and deciding what
    /// to do. What it could not do is CATCH THE EYE — a user watching the canvas never noticed a
    /// sentence changing at the bottom of the window, which is why this screen felt unresponsive.
    /// The notice supplies the attention; the line supplies the memory.
    ///
    /// Every call site in this window is a discrete event, so all of them float. FloatingNotice's
    /// own dedupe absorbs the repeats from <see cref="SetWizardState"/>.
    /// </summary>
    private void SetStatus(string text)
    {
        if (_statusLabel != null)
        {
            _statusLabel.Text = text;
        }
        Controls.FloatingNotice.Show(this, text);
    }

    /// <summary>ISSUE_09 — same, in the "that worked" colour.</summary>
    private void SetStatusSuccess(string text)
    {
        if (_statusLabel != null)
        {
            _statusLabel.Text = text;
        }
        Controls.FloatingNotice.Success(this, text);
    }

    private void SetEnabled(string name, bool enabled, string? tip = null)
    {
        if (this.FindControl<Control>(name) is { } control)
        {
            control.IsEnabled = enabled;
            if (tip != null) ToolTip.SetTip(control, tip);
        }
    }

    private void SetVisible(string name, bool visible)
    {
        if (this.FindControl<Control>(name) is { } control)
        {
            control.IsVisible = visible;
        }
    }

    private void SetContent(string name, object content)
    {
        if (this.FindControl<Button>(name) is { } button)
        {
            button.Content = content;
        }
    }

    /// <summary>
    /// BINPATH_01 — moved verbatim into <see cref="Infrastructure.BinaryPathProbe"/>.
    ///
    /// ⚠️ THIS WINDOW'S SEARCH ORDER IS NOT THE VOICE-OVER WINDOW'S — eight candidates rooted at
    /// the process directory versus four rooted at AppContext.BaseDirectory. See BinaryPathProbe
    /// for why that divergence matters and why it was NOT resolved in this step.
    /// </summary>
    private static string ResolveBinaryPath(string fileName, string preferredSubdirectory)
        => Infrastructure.BinaryPathProbe.ResolveForCropTool(fileName, preferredSubdirectory);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (FreeVideoStudio.App.Infrastructure.KeyboardFocusPolicy.HotkeysSuspended(TopLevel.GetTopLevel(this)))
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.Key is Key.Enter or Key.Return
            && _sourceSelection != null
            && SnapshotPanelCtl?.IsVisible == true)
        {
            ShowRolePopup();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _sourceDrag != SourceDrag.None)
        {
            EndSourceDrag();
            if (_sourceSelection is { } keep)
            {
                UpdateSelectionRect(new Rect(keep.X, keep.Y, keep.Width, keep.Height));
            }
            else
            {
                ClearSourceSelection();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _rolePopupNewOpen)
        {
            CloseRolePopupNewRow();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape
            && _sourceSelection != null
            && SnapshotPanelCtl?.IsVisible == true)
        {
            CancelSourceSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            DeleteSelectedItem();
            e.Handled = true;
            return;
        }

        if (HistoryShortcutFor(e) is var command && command != HistoryCommand.None)
        {
            if (command == HistoryCommand.Redo) Redo(); else Undo();
            e.Handled = true;
            return;
        }

        if (_sourceSelection != null
            && SnapshotPanelCtl?.IsVisible == true
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (NudgeSourceSelection(e.Key, e.KeyModifiers))
            {
                HideRolePopup();
                e.Handled = true;
                return;
            }
        }

        if (SelectedView != null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                double resizeStep = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 2;
                double delta = e.Key is Key.Left or Key.Up ? -resizeStep : resizeStep;
                NudgeSelectedItemSize(SelectedView, delta);
                ApplyItemLayout(SelectedView);
                MarkDirty();
                PushHistory(CropHistoryLabels.ResizeHud);
                e.Handled = true;
                return;
            }

            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
            double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            (SelectedView.X, SelectedView.Y) = CropEditSession.ClampOverlay(SelectedView.X + dx, SelectedView.Y + dy, SelectedView.Width, SelectedView.Height);
            ApplyItemLayout(SelectedView);
            MarkDirty();
            PushHistory(CropHistoryLabels.MoveCrop);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_isSafeToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closeInProgress || _changingProfile) return;
        if (!_returningToMainApp && !await ConfirmUnsavedChangesAsync("closing Crop Tools")) return;
        _closeInProgress = true;
        FreeVideoStudio.App.WindowBoundsHelper.SaveBoundsOnClose(this, "CropToolBounds");
        Hide();

        try
        {
            _timelineTimer?.Stop();
            _timelineTimer = null;

            if (_playheadBadgeTimer != null)
            {
                _playheadBadgeTimer.Stop();
                _playheadBadgeTimer = null;
            }

            if (_videoHost != null)
            {
                if (_videoHost.IpcClient != null)
                {
                    await _videoHost.IpcClient.SendCommandAsync("stop");
                }
                var shutdown = await _videoHost.ShutdownAsync();
                if (!shutdown.Succeeded) RuntimeLog.Fail("CROP", $"Video preview did not shut down cleanly: {shutdown.Reason} — restart the app before using the preview again.");
                _videoHost = null;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Error during close: {ex.Message}");
        }
        finally
        {
            CleanupTempFiles();
            _isSafeToClose = true;
            Dispatcher.UIThread.Post(Close);
        }
    }

    private void CleanupTempFiles()
    {
        foreach (string path in _tempFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("CROP", $"Temp file cleanup skipped: {ex.Message}");
            }
        }
    }

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
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
                {
                    try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            };
        }
    }

    private sealed record LayerEntry(string RoleKey, string DisplayName, int Z)
    {
        public override string ToString() => $"{DisplayName}  z:{Z}";
    }

    private sealed record CandidateSpec(string RoleKey, SourceRect Rect, string Label = "", HudCandidateSource Source = HudCandidateSource.Local);

    /// <summary>
    /// EDITSTATE_01 — a placed element's VISUALS, pointed at its logical <see cref="CropLayer"/> in
    /// the edit session. The geometry, provenance and identity are the model's; the properties below
    /// only forward to it so the view code reads naturally. Nothing here is history or persistence.
    /// </summary>
    private sealed class CropEditorItem
    {
        public required CropLayer Model { get; init; }
        public string RoleKey => Model.RoleKey;
        public string DisplayName => Model.DisplayName;
        public SourceRect SourceRect { get => Model.SourceRect; set => Model.SourceRect = value; }
        public string CropImagePath { get => Model.CropImagePath; set => Model.CropImagePath = value; }
        public int X { get => Model.X; set => Model.X = value; }
        public int Y { get => Model.Y; set => Model.Y = value; }
        public int Width { get => Model.Width; set => Model.Width = value; }
        public int Height { get => Model.Height; set => Model.Height = value; }
        public int Z { get => Model.Z; set => Model.Z = value; }

        /// <summary>GHOSTKILL_01 / RESGUESS_01 — provenance lives on the model (see <see cref="CropLayer"/>).</summary>
        public bool GeometryVerified { get => Model.GeometryVerified; set => Model.GeometryVerified = value; }

        public required Canvas Root { get; init; }
        public required Image Image { get; init; }
        public required Rectangle Border { get; init; }
        public required Rectangle TopLeftHandle { get; init; }
        public required Rectangle BottomRightHandle { get; init; }
        public required Border LabelHost { get; init; }
        public required TextBlock LabelText { get; init; }
    }

    private enum ComposerEditMode
    {
        None,
        Drag,
        ResizeTopLeft,
        ResizeBottomRight
    }

    private enum ResizeHandle
    {
        TopLeft,
        BottomRight
}
}
