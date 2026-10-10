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
using SourceRect = FreeVideoStudio.Core.Editing.CropSourceRect;   // EDITSTATE_01 — the logical types live in Core
using HudRole = FreeVideoStudio.Core.Editing.CropHudRole;

// COLORMATH_01 / CROPJSON_01 / CROPGEOM_01 / BINPATH_01 — these four helper types hold methods
// extracted verbatim from this class. Imported with `using static` on purpose: every one of the
// ~60 call sites below keeps the exact unqualified spelling it already had, so the extraction
// cannot change a single statement inside this file.
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
        // A null/empty propertyName means "entity-level errors" in the INotifyDataErrorInfo
        // contract; Avalonia asks per-property, but answering both costs nothing and keeps the
        // implementation honest.
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

        // Re-run validation against the new set: a name typed before the list refreshed may have
        // just become a duplicate, or stopped being one.
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

        // ISSUE_01 — the notification that makes the red border and the message appear and vanish
        // as the user types, and the one that keeps SAVE AS NEW in step with them.
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
    // CROPGEOM_01 — SnapThreshold moved to CropGeometry alongside SnapAxis, its only consumer.

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
    // LAYERSPANE_01 - the LAYERS ListBox was removed from the AXAML. `_layers` is KEPT: it is the
    // z-order model that MoveSelectedLayer, RefreshLayerList and the save path all read, and it is
    // what the right-click menu re-sorts. Only the visual list went away. This field stays declared
    // and always null so the SelectItem / RefreshLayerList null-guards keep documenting that.
    private ListBox? _layerList;
    private Slider? _timelineSlider;
    private TextBlock? _currentTimeLabel;
    private TextBlock? _totalTimeLabel;
    private TextBlock? _statusLabel;
    private TextBlock? _goalLabel;
    private Canvas? _timelineCanvas;
    private TextBlock? _selectionInfo;

    // ZOOM_01 (F2) — the frozen-frame viewport. _snapshotZoomHost carries the LayoutTransform that
    // scales the 1:1 source surface; SourceCanvas itself is NEVER resized, so every existing
    // GetPosition(SourceCanvas) call keeps returning true source pixels.
    private ScrollViewer? _snapshotScroll;
    private LayoutTransformControl? _snapshotZoomHost;
    private TextBlock? _zoomLabel;

    // GATE_01 (F3) — blank-start gating.
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
    private bool _unsavedPromptOpen;
    private bool _returningToMainApp;
    private bool _closeInProgress;
    private DispatcherTimer? _timelineTimer;


    /// <summary>GATE_01 — the initial video is held until a profile exists to load it against.</summary>
    private string? _pendingInitialVideoPath;

    // ZOOM_01 (F2) — fit mode recomputes on every viewport resize; a manual factor does not.
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

    // ROLEPOPUP_01 / NODUPES_02 — the element catalogue (built-ins + this profile's custom elements),
    // its key/display-name rules and the quadrant guess live in the edit session (EDITSTATE_01).

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

        // GRIP_01 — the bottom-right resize corner. These windows are borderless, so the OS
        // draws no resize frame: without this there is nothing to grab and nothing telling the
        // user the Crop Tools can be resized at all. One shared implementation — see
        // Controls/WindowResizeGrip.cs for why it is not per-window code.
        Controls.WindowResizeGrip.Attach(this, "Drag to resize the Crop Tools");
        _recovery.AcquireLock();
        FreeVideoStudio.App.WindowBoundsHelper.Track(this, "CropToolBounds", fitDisplayOnFirstRun: true);   // FIRSTFIT_01
        FindControls();
        AttachTitleBarDrag();
        WireEvents();
        InitializeHistory();

        // GATE_01 (F3) — start blank and locked. Step 0 is a real user-facing stage now.
        _pendingInitialVideoPath = _initialVideoPath;
        ApplyWizardChrome(cropping: false);   // WIZCOLLAPSE_01 - dots visible until cropping starts
        SetProfileGate(unlocked: false);
        SetWizardState(0, "Choose Profile", "Pick the profile you want to edit, up at the top.");

        Loaded += (_, _) => Controls.CoachOverlay.Register(this, Controls.CoachTours.CropToolKey, Controls.CoachTours.CropTool);

        Loaded += async (_, _) =>
        {
            // MPV is started eagerly because spinning up the player takes long enough to be felt,
            // and starting it costs nothing while the screen is gated. What is NOT done here any
            // more is reading the crop config: RehydrateSavedLayersAsync and
            // LoadExistingPlaceholdersAsync used to run unconditionally on Loaded, which meant the
            // window silently opened holding the live contents of whatever profile was last active.
            // Both now run from OnProfileChosenAsync, after a deliberate choice. (GATE_01)
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
        _layerList = null;   // LAYERSPANE_01 - no such control any more; the guards below handle it.
        _timelineSlider = this.FindControl<Slider>("TimelineSlider");
        _currentTimeLabel = this.FindControl<TextBlock>("CurrentTimeLabel");
        _totalTimeLabel = this.FindControl<TextBlock>("TotalTimeLabel");
        _statusLabel = this.FindControl<TextBlock>("StatusLabel");
        _goalLabel = this.FindControl<TextBlock>("GoalLabel");
        _timelineCanvas = this.FindControl<Canvas>("CropTimelineScaleCanvas");
        _selectionInfo = this.FindControl<TextBlock>("SelectionInfo");
        _snapshotScroll = this.FindControl<ScrollViewer>("SnapshotScroll");                 // ZOOM_01
        _snapshotZoomHost = this.FindControl<LayoutTransformControl>("SnapshotZoomHost");   // ZOOM_01
        _zoomLabel = this.FindControl<TextBlock>("ZoomLabel");                              // ZOOM_01
        _profileGate = this.FindControl<Border>("ProfileGateOverlay");                      // GATE_01
        _profileStateLabel = this.FindControl<TextBlock>("ProfileStateLabel");              // GATE_01
        _composerEmptyState = this.FindControl<Border>("ComposerEmptyState");               // ISSUE_07
        _composerEmptyStateTitle = this.FindControl<TextBlock>("ComposerEmptyStateTitle");  // ISSUE_07
        _composerEmptyStateBody = this.FindControl<TextBlock>("ComposerEmptyStateBody");    // ISSUE_07
        _rolePopupScrim = this.FindControl<Border>("RolePopupScrim");                       // ISSUE_08
        WireUpVolumeSlider();
    }

    private void WireEvents()
    {
        // ISSUE_08 (audit round 6) — click-away dismissal for the HUD-element chooser.
        //
        // RolePopup is deliberately an in-panel Border rather than an Avalonia Popup or Flyout
        // (ROLEPOPUP_01: those are separate OS windows, which is exactly how the old Python
        // RoleToolbar earned its multi-monitor bug). The cost of that correct choice was that
        // IsLightDismissEnabled — a Popup-only property — was not available, and nobody supplied a
        // replacement: Escape was the ONLY way out of the chooser. The scrim is that replacement.
        // It covers the frame while the chooser is up, so the click that lands on it is a click
        // OUTSIDE the chooser, and that is what dismisses it.
        if (_rolePopupScrim != null)
        {
            _rolePopupScrim.PointerPressed += (_, e) =>
            {
                HideRolePopup();
                // Handled, or the press falls through to SourceCanvas and starts drawing a new box
                // on the way out of a dialog — which is precisely the accident a light dismiss is
                // supposed to prevent.
                e.Handled = true;
            };
        }

        // ISSUE_09 (audit round 6) — the chooser's drop shadow, built from the themed token, and
        // rebuilt whenever the user switches Light/Dark so it never goes stale.
        ApplyRolePopupShadow();
        ActualThemeVariantChanged += (_, _) => ApplyRolePopupShadow();

        if (_sourceCanvas != null)
        {
            _sourceCanvas.PointerPressed += SourceCanvas_PointerPressed;
            _sourceCanvas.PointerMoved += SourceCanvas_PointerMoved;
            // CROSSHAIR_01 - the guides belong to the pointer, so they leave with it.
            _sourceCanvas.PointerExited += (_, _) => SetCrosshairVisible(false);
            _sourceCanvas.PointerReleased += SourceCanvas_PointerReleased;

            // DRAGFREE_01 — A DRAG MUST NEVER OUTLIVE THE BUTTON THAT STARTED IT.
            //
            // Every branch of SourceCanvas_PointerPressed takes a pointer capture, and the ONLY
            // place that released it was SourceCanvas_PointerReleased. Anything that takes the
            // capture away before the button comes up — a popup opening, a window losing focus,
            // the pointer being grabbed by another control, Alt+Tab — therefore left _sourceDrag
            // set forever. The rectangle then followed the pointer with no button held and no way
            // to stop it: the reported "my mouse cursor gets trapped on the rubberband".
            // PointerCaptureLost is the event that says exactly that happened, so it ends the drag.
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

        // WANDPROGRESS_01 — the way out of a run in progress. Cancelling the token unwinds
        // RunMagicWandAsync through its OperationCanceledException branch, which already closes the
        // overlay and restores the button in its finally block, so there is nothing to undo here.
        ButtonClick("WandCancelButton", (_, _) =>
        {
            RuntimeLog.Info("CROP", "Magic Wand cancelled by the user.");
            _wandStopRequested = true;
            CancelMagicWand();   // AIHUD_03 - also invalidates any late AI answer
        });

        // MAGICWAND_02 — the wand is wired to a real detector now, so the button is live again.
        // MAGICWAND_01 hid it because the old handler drew six boxes at hardcoded fractions of the
        // frame and called them detections; see RunMagicWandAsync for what replaced that.
        ButtonClick("MagicWandButton", async (_, _) => await RunMagicWandAsync());

        // ZOOM_01 (F2)
        // ZOOMKEEP_01 — every zoom EXCEPT Fit re-centres on the selection. Fit is the one gesture
        // that means "show me the whole frame again", so centring on the box there would fight the
        // request.
        ButtonClick("ZoomFitButton", (_, _) => ApplySnapshotZoom(null));
        ButtonClick("ZoomActualButton", (_, _) => { ApplySnapshotZoom(1.0); RecenterOnSelection(); });
        ButtonClick("ZoomOutButton", (_, _) => { ApplySnapshotZoom(CurrentZoom() / 1.25); RecenterOnSelection(); });
        ButtonClick("ZoomInButton", (_, _) => { ApplySnapshotZoom(CurrentZoom() * 1.25); RecenterOnSelection(); });

        // ZOOM_01 — fit is a RELATIONSHIP to the viewport, not a number, so it has to be
        // recomputed whenever the viewport changes. A manual zoom is a number and is left alone.
        if (_snapshotScroll != null)
        {
            _snapshotScroll.SizeChanged += (_, _) =>
            {
                if (_snapshotFitMode && _snapshotPath != null) ApplySnapshotZoom(null);
            };

            // WHEELZOOM_01 — the wheel zooms the frame; it does not scroll it.
            //
            // Tunnel, not Bubble: a ScrollViewer consumes PointerWheelChanged itself, so a
            // bubbling handler would only ever see the leftovers and the view would scroll
            // instead of zoom. Tunnelling gets the event on the way DOWN, before the
            // ScrollViewer's own handling, and Handled = true stops it there.
            //
            // On an image canvas the wheel means zoom to everyone who has used any image editor,
            // and it is the gesture that replaces hunting for scrollbars. The zoom is anchored
            // under the cursor (see below) so the thing you are pointing at does not run away.
            _snapshotScroll.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnSnapshotWheel,
                RoutingStrategies.Tunnel,
                handledEventsToo: false);

            // ══════════════════════════════════════════════════════════════════════════════════
            // WHEELDRAG_01 — THE WHEEL MUST KEEP WORKING WHILE THE BAND IS BEING STRETCHED.
            //
            // The handler above is attached to the ScrollViewer, so it only ever sees a wheel
            // event that Avalonia routed THROUGH the ScrollViewer — that is, one whose pointer is
            // over it. While a rubber band is being dragged the pointer is captured by SourceCanvas
            // and routinely leaves that area: the user drags out past the frame onto the toolbar or
            // the portrait panel, and from there the wheel reached a different subtree entirely.
            // The zoom simply stopped responding, at exactly the moment ("this box is bigger than
            // what I can see") when zooming out is the thing you need.
            //
            // This second registration is on the WINDOW, so it sees the event wherever the pointer
            // is. It stays out of the way completely unless a gesture is actually in progress —
            // otherwise a wheel over the layer list or the timeline would zoom the frozen frame.
            // It runs first (a tunnel from the window reaches the window before the ScrollViewer)
            // and marks the event handled, and the registration above is handledEventsToo: false,
            // so the two can never both act on one notch.
            // ══════════════════════════════════════════════════════════════════════════════════
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

        // ROLEPOPUP_01 - the inline "+ New element" row. Enter commits, so the whole naming flow
        // is type-and-press without reaching for the mouse; the ADD button is there for people who
        // do not expect Enter to mean anything.
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

        // ALWAYSLIVE_01 (audit round 6) — the "Show Saved Crops" checkbox and the opacity slider
        // are gone from the AXAML, and the two handlers that used to be wired here went with them.
        //
        // The history: GHOSTKILL_01 had already deleted the non-interactive green "ghost" copies of
        // saved crops, leaving the checkbox driving the real items instead. That was the right
        // repair of a broken control but the wrong question. Saved elements ARE the profile the
        // user just deliberately chose to edit — hiding them, or fading them to 8% on a nameless
        // 20-255 slider, only ever made the user's own work harder to see, and it cost ~280px of
        // the narrowest pane in the window to do it.
        //
        // Saved elements are now always drawn, always at full opacity and always interactive.
        // CropLayer.FromSavedConfig (EDITSTATE_01) survives purely as provenance (it is what SaveConfigAsync
        // uses to tell a re-saved element from a new one); nothing reads it for visibility any
        // more, so there is nothing left to toggle. See ApplySavedCropVisibility's removal.

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
            // ══════════════════════════════════════════════════════════════════════════════════
            // NOMASK_02 — THE RESERVED PROFILE IS NOT IN THE LIST AT ALL.
            //
            // NOMASK_01 let it be listed and then refused the selection afterwards: the user picked
            // it, the ComboBox visibly changed, the handler rolled the selection back and printed a
            // sentence explaining why. That is an offer followed by a refusal — the worst shape a
            // control can have, because the only way to learn the rule is to break it, and the
            // rollback makes the picker look broken while it happens.
            //
            // "No Mask Profile" is a PROTECTED, DELIBERATELY EMPTY profile: no HUD elements, no
            // overlay masking, by design. There is nothing in it to edit, and everything in this
            // window edits. So it is filtered out of the source list and the window never has to
            // talk about it again: it cannot be chosen, cannot be loaded, cannot be saved over, and
            // cannot be reached by keyboard or by an accidental index-based selection either — you
            // cannot select what is not there.
            //
            // The name is ALSO still reserved for creation (ValidateNewMaskOverlayName rejects it),
            // so SAVE AS NEW cannot claim it from the other direction, and the Main App still
            // refuses to launch this window at all while it is the active profile
            // (MainWindow.BlockCropToolsForNoMaskProfile) — three independent locks on one door,
            // because losing that profile's emptiness is not recoverable from inside this tool.
            // ══════════════════════════════════════════════════════════════════════════════════
            var allProfiles = FreeVideoStudio.App.Infrastructure.MaskOverlayManager.GetAvailableProfiles();
            var profiles = allProfiles
                .Where(p => !FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(p))
                .ToList();

            if (profiles.Count != allProfiles.Count)
            {
                RuntimeLog.Debug("CROP", $"'{FreeVideoStudio.App.Infrastructure.MaskOverlayManager.NoMaskProfileName}' withheld from the profile picker (NOMASK_02).");
            }

            combo.ItemsSource = profiles;

            // The duplicate-name cache is fed the UNFILTERED list on purpose: the reserved profile
            // still occupies its file name on disk, so SAVE AS NEW must still collide with it.
            RefreshMaskOverlayNameCache(allProfiles);   // ISSUE_01

            // GATE_01 (F3) - NO preselection. The line removed here was
            //     combo.SelectedItem = SettingsManager.Instance.ActiveMaskOverlay;
            // and it is the origin of this window's most dangerous behaviour: the window opened
            // already LIVE on a shipped preset, while FINISH & SAVE runs through
            // SyncActiveProfileFromCurrentConfig(), which writes the live crop config straight over
            // that preset's file. A user who opened Crop Tools only to look around could destroy
            // "Fortnite" in two clicks. The ComboBox's PlaceholderText now reads
            // "Choose a profile to edit..." until the user makes a deliberate choice.
            // Do NOT restore the preselection. ActiveMaskOverlay is still consulted at SAVE time,
            // which is the only moment it is actually needed.
            combo.SelectedItem = null;

            combo.SelectionChanged += async (s, e) =>
            {
                if (_changingProfile) return;
                if (combo.SelectedItem is not string selected) return;

                // Compare against _edit.ActiveProfile - what is really LOADED - not against
                // SettingsManager.ActiveMaskOverlay, which is global state the Main App also
                // writes. This equality guard is also what absorbs the re-entrant pass caused by
                // the two `combo.SelectedItem = _edit.ActiveProfile` rollbacks below.
                if (string.Equals(selected, _edit.ActiveProfile, StringComparison.OrdinalIgnoreCase)) return;

                // NOMASK_02 — unreachable by construction now (the reserved profile is filtered
                // out of ItemsSource above), and kept anyway as a last line of defence. A future
                // change that repopulates this ComboBox from somewhere else must not be able to
                // reintroduce the one selection that can destroy a protected profile. It is silent
                // rather than explanatory precisely BECAUSE it should never fire: a message here
                // would be a message about a bug, not about the user.
                if (FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(selected))
                {
                    RuntimeLog.Fail("CROP", "The reserved profile reached the Crop Tools picker — NOMASK_02's filter has been bypassed.");
                    combo.SelectedItem = _edit.ActiveProfile;
                    return;
                }

                _changingProfile = true;
                try
                {
                    // Keep the picker on the loaded profile until the user has chosen an action.
                    combo.SelectedItem = _edit.ActiveProfile;
                    if (_returningToMainApp || _closeInProgress ||
                        !await ConfirmUnsavedChangesAsync($"switching to \"{selected}\"")) return;
                    await OnProfileChosenAsync(selected);
                    combo.SelectedItem = _edit.ActiveProfile;
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

                // ISSUE_01 — one predicate, two consumers. The button should already be disabled in
                // every one of these cases; this is the guard for a programmatic or keyboard-forced
                // click, and it reports the SAME sentence the field is showing rather than a second,
                // differently-worded one.
                if (ValidateNewMaskOverlayName() is { } nameError)
                {
                    SetStatus(nameError);
                    return;
                }

                // GATE_01 (F3) - SAVE AS NEW makes a COPY OF THE SELECTED PROFILE under a new
                // name. With no profile selected there is nothing to copy from, and
                // MaskOverlayManager.CreateNewProfile would silently snapshot whatever the live
                // crop config happens to hold - the last profile the MAIN APP applied, which the
                // user never chose here and probably cannot name. The button is disabled in that
                // state (SetProfileGate); this is the matching guard for a programmatic click.
                if (_edit.ActiveProfile == null)
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

                // NOMASK_01 — the reserved name cannot be claimed. MaskOverlayManager.CreateNewProfile
                // already refuses it, but it refuses SILENTLY: without this the handler would carry on
                // to SaveConfigAsync and set combo.SelectedItem to a profile that was never created.
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
                        // NOMASK_02 — the refill has to apply the SAME filter as the initial build,
                        // or creating a profile would quietly put the reserved one back in the list.
                        var updatedProfiles = FreeVideoStudio.App.Infrastructure.MaskOverlayManager.GetAvailableProfiles();
                        combo.ItemsSource = updatedProfiles
                            .Where(p => !FreeVideoStudio.App.Infrastructure.MaskOverlayManager.IsNoMask(p))
                            .ToList();
                        RefreshMaskOverlayNameCache(updatedProfiles);   // ISSUE_01 - unfiltered, see above
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
            // A hidden Border still answers hit-tests in some layouts; belt and braces, because a
            // click that lands on a "closed" gate and reaches the canvas behind it is exactly the
            // destructive edit this whole mechanism exists to prevent.
            _profileGate.IsHitTestVisible = !unlocked;
        }

        // ISSUE_02 (audit round 6) — THE GATE NOW STOPS THE KEYBOARD TOO.
        //
        // Everything above this line is a MOUSE lock. IsHitTestVisible does not touch focus, and
        // the overlay carried no Focusable/TabNavigation of its own (its ThinkingOverlay and
        // SummaryOverlay siblings both do). Meanwhile PortraitCanvas is visible from the moment the
        // window opens, is Focusable, is a tab stop, and appears in NONE of the SetEnabled calls
        // below — so Tab walked straight past the lock and landed on the canvas the lock exists to
        // protect, where the arrow keys and Delete are live.
        //
        // Switching the whole working area off is the only version of this that cannot be walked
        // around: it removes every descendant from the focus order in one move, no matter what is
        // added to that subtree later. The gate overlay is a SIBLING of WorkAreaGrid inside the row
        // grid (declared after it, ZIndex 9000), so it stays visible and interactive.
        //
        // ORDER MATTERS: enable the container FIRST, then let the individual rules below switch
        // things back off. Reversing these two would re-enable every button the block underneath
        // just disabled.
        SetEnabled("WorkAreaGrid", unlocked);

        // The profile picker itself and the walkthrough button stay live in BOTH states.
        SetEnabled("OpenVideoButton", unlocked);
        RefreshCreateMaskOverlayButton();   // ISSUE_01 - gate is one of its four conditions
        SetEnabled("NewMaskOverlayTextBox", unlocked);
        SetEnabled("ResetMenuButton", unlocked);
        SetEnabled("SnapToggle", unlocked);

        if (!unlocked)
        {
            // Controls that are gated AND state-driven are forced off here, then handed back to
            // their normal owners (LoadVideoAsync, RefreshActionButtons, ...) once unlocked. They
            // are deliberately NOT enabled by this method on the way up: a profile being chosen
            // does not mean a video is loaded or that there is anything to save.
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
            _profileStateLabel.Text = unlocked
                ? "Editing: " + (_edit.ActiveProfile ?? "")
                : "No profile selected";
        }

        // IDEA_7 / drag-and-drop: OnVideoDrop is registered on the window, so it would happily
        // accept a dropped clip through a locked gate. DragDrop.AllowDrop is the switch that is
        // actually checked before the drop is routed.
        Avalonia.Input.DragDrop.SetAllowDrop(this, unlocked);
    }

    /// <summary>
    /// GATE_01 (F3) - loads a profile the user deliberately picked.
    ///
    /// ORDER MATTERS. ApplyProfile writes the chosen profile into the live crop config, so the
    /// working state has to be cleared FIRST (otherwise items belonging to the previous profile
    /// survive into this one and get written back to the wrong file on the next save), and the
    /// rehydrate has to run AFTER (it reads the live config that ApplyProfile just wrote).
    /// </summary>
    private async Task OnProfileChosenAsync(string profileName)
    {
        try
        {
            FreeVideoStudio.App.Infrastructure.MaskOverlayManager.ApplyProfile(profileName);
            _edit.ActiveProfile = profileName;

            ResetWorkingState();
            SetProfileGate(unlocked: true);

            await RehydrateSavedLayersAsync();
            // ALWAYSLIVE_01 — ApplySavedCropVisibility() used to run here to apply the checkbox and
            // the opacity slider to everything that had just been rehydrated. Rehydrated elements
            // are now simply visible, like every other item, so there is nothing to apply.
            UpdateComposerEmptyState();   // ISSUE_07

            RefreshActionButtons();
            RuntimeLog.Info("CROP", $"Editing mask profile '{profileName}' ({_items.Count} saved element(s) loaded).");

            if (string.IsNullOrWhiteSpace(_videoPath))
            {
                SetWizardState(1, "Upload Video", $"Editing \"{profileName}\". Open a reference clip to start.");
            }

            // GATE_01 - a clip handed over by the Main App waits here until there is a profile to
            // load it against, instead of being loaded into a session that cannot legally save.
            if (!string.IsNullOrWhiteSpace(_pendingInitialVideoPath) && File.Exists(_pendingInitialVideoPath))
            {
                string pending = _pendingInitialVideoPath;
                _pendingInitialVideoPath = null;
                await LoadVideoAsync(pending, startPaused: false);   // AUTOPLAY_01
            }

            SetStatusSuccess("Profile loaded: " + profileName);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Could not open profile '{profileName}': {ex.Message}");
            SetStatus("That profile could not be opened. See runtime log.");
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
            // FIT is the user saying "show me everything again", so it also RELEASES the manual
            // zoom lock: after pressing FIT, drawing a box auto-zooms to it once more.
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

        // The floor is the FIT scale, not the MinZoom constant: zooming out past "the whole frame
        // is visible" only ever loses the user. The old Python tool clamped identically
        // (crop_widgets.py wheelEvent: min_allowed_zoom = the fit scale). Min(fit, 1.0) keeps a
        // capture SMALLER than the viewport from being locked above 100%.
        double scale = fitMode ? factor : Math.Clamp(factor, Math.Min(ComputeFitScale(), 1.0), MaxZoom);

        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
        _snapshotZoomFactor = scale;

        // LIST_06: this WRITES a size derived from a measurement, so it must never read back the
        // thing it sizes. ComputeFitScale measures the ScrollViewer (sized by the window), never
        // the transformed content, and the write is skipped when nothing actually changed -
        // an identical assignment still invalidates layout, which would keep SizeChanged firing
        // forever and make the scrollbars jump under the pointer.
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

        // CROPCANVAS_01 — re-lay the selection at the new scale. The rectangle's stroke AND the
        // four corner handles are sized from SCREEN constants divided by the scale, so they all go
        // wrong together if this is skipped: at Fit on a 4K capture the handles would be a couple
        // of screen pixels across and impossible to grab.
        if (_sourceSelection is { } liveSelection)
        {
            UpdateSelectionRect(new Rect(liveSelection.X, liveSelection.Y, liveSelection.Width, liveSelection.Height));
        }
        else if (_selectionRect != null)
        {
            _selectionRect.StrokeThickness = 2.5 / scale;   // BANDCONTRAST_01
        }
        foreach (Control control in _candidateControls)
        {
            if (control is Rectangle candidate) candidate.StrokeThickness = 3.0 / scale;
            else if (control is TextBlock caption) caption.FontSize = WandCaptionFontSize / scale;   // AIHUD_01
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
        StopAnts();                 // ANTS_01 - see StartAnts for why this is not optional.
        StopEdgePan();              // DRAGFREE_01 - a DispatcherTimer keeps this window alive.
        _edgePanTimer = null;
        _timelineTimer?.Stop();

        // MAGICWAND_02 — cancel any detection still running. It owns an ffmpeg child process and
        // up to ~90 MB of sampled frames; leaving it to finish against a closed window would keep
        // both alive for as long as the analysis takes.
        DisposeMagicWand();   // AIHUD_03 - cancels the run and invalidates any late AI answer

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
                await LoadVideoAsync(path, startPaused: false);   // AUTOPLAY_01
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

        await LoadVideoAsync(files[0].Path.LocalPath, startPaused: false);   // AUTOPLAY_01
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
        // MAGICWAND_02 — a new clip invalidates everything the wand learned from the old one.
        ResetMagicWandForNewClip();
        ClearSourceSelection();
        ClearMagicWandCandidates();
        UpdateComposerEmptyState();   // ISSUE_07 - the empty-state copy changes once a clip is open
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

            // PLAYING: the Main App starts a clip playing and so does this now. `startPaused` is
            // still honoured because RE-freezing a frame (BACK TO VIDEO -> pick another moment)
            // must not restart playback under the user.
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
            _edit.CaptureResolutionKnown = w > 0 && h > 0;   // RESGUESS_01

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

        // BACKTOVIDEO_01 - while a frozen frame is on screen, PLAY means "back to the video".
        // Checked before the pause state, because what the user is looking at decides what the
        // button means: pressing play at a still frame cannot sensibly mean anything else.
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
                _edit.CaptureResolutionKnown = _edit.SnapshotWidth > 0 && _edit.SnapshotHeight > 0;   // RESGUESS_01
                BuildContrastSampler(bitmap);                                          // BANDCONTRAST_01
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
        // MAGICWAND_02 — a new frozen frame wipes the DRAWN candidates but not the cached ones:
        // they are in source-pixel space and describe the CLIP, which has not changed (AI boxes
        // describe a FRAME and are re-asked or dropped on the next press - AIHUD_04). Rewinding
        // the step cursor means the next press shows the whole set again rather than resuming
        // halfway through a walk the user has forgotten about.
        _wandPreviewIndex = -1;

        // ZOOM_01 (F2) - fit the whole capture into the panel. Without this the frame renders at
        // 1:1 (SnapshotImage/SourceCanvas are given the real capture size just above), which on a
        // 1080p capture in a half-width panel means the user sees roughly a quarter of their game
        // screen and has to hunt for the HUD with scrollbars. Fit is the only sane default here;
        // 100% is one click away for pixel-exact work.
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
                    global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
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

        // BACKTOVIDEO_01 - START CROPPING belongs to the video, so it comes back with it. The pair
        // reads as one toggle: while the video moves you can freeze it, while it is frozen you can
        // only go back.
        SetVisible("SnapshotButton", true);
        SetVisible("ZoomStrip", false);                          // ZOOMBAR_01
        ApplyWizardChrome(cropping: false);                      // WIZCOLLAPSE_01

        if (!string.IsNullOrWhiteSpace(_videoPath))
        {
            SetWizardState(2, "Find HUD Frame", "Find a clear HUD frame and start cropping.");
        }
    }

    private void ShowSnapshotPanel()
    {
        SetVisible("VideoPanel", false);
        SetVisible("SnapshotPanel", true);
        // MAGICWAND_02 — un-hidden, the line MAGICWAND_01 struck out. The condition it set has
        // been met: HudAutoDetector is the real frame analyser. The wand is only meaningful on a
        // frozen frame, which is why it lives here and not in ShowVideoPanel.
        SetVisible("MagicWandButton", true);
        // BACKTOVIDEO_01 - hidden while a frame is frozen. There is nothing to freeze: you are
        // already looking at a still. PLAY is the way out and START CROPPING returns with the video.
        SetVisible("SnapshotButton", false);
        SetVisible("ZoomStrip", true);                            // ZOOMBAR_01
        ApplyWizardChrome(cropping: true);                        // WIZCOLLAPSE_01
        SetWizardState(3, "Refine Box", "Drag a box round one HUD piece, then pick what it is.");
    }

    // ==================================================================================
    // CROPCANVAS_01 — the frozen-frame selection surface.
    //
    // WHAT WAS HERE BEFORE, AND WHY IT HAD TO GO.
    // SourceCanvas_PointerPressed treated EVERY press as the start of a brand-new rubber
    // band. There was no hit-test, no handle, no move. A box drawn two pixels off could
    // only be fixed by drawing the whole thing again, on an image rendered at ~35% scale,
    // with scrollbars as the only way to move around. That is the whole of "the rubber
    // band is really hard to work with".
    //
    // The model below is the one the old Python tool used (developer_tools/crop_widgets.py,
    // DrawWidget.mousePressEvent), because it was right: a press is dispatched to one of
    // four outcomes, checked in this order, and only the last one draws anything new.
    //
    //   1. a corner handle      -> resize from that corner
    //   2. inside the selection -> move the whole box
    //   3. on a wand candidate  -> adopt that box
    //   4. anywhere else        -> start a new box
    //
    // Every coordinate in this region is a TRUE SOURCE PIXEL of the capture (1920x1080,
    // 2560x1440, ...). SourceCanvas is never resized — ZOOM_01's LayoutTransformControl
    // scales the rendering and Avalonia inverts that transform for us — so
    // e.GetPosition(_sourceCanvas) needs no scaling maths anywhere below. The ONLY places
    // the zoom scale appears are the ones that must stay a constant size ON SCREEN:
    // handle size, stroke width, and the grab tolerance.
    // ==================================================================================

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

        // CANCELSEL_01 - right-click abandons the selection and puts the view back.
        // Checked FIRST, before the drag dispatch below, so it works whether the box is finished or
        // still being dragged out.
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

        // 1 + 2: act on the existing selection before considering a new one.
        if (_sourceSelection is { } current)
        {
            SourceDrag corner = HitTestSelectionCorner(p, current);
            if (corner != SourceDrag.None)
            {
                _sourceDrag = corner;
                _sourceDragOrigin = current;
                _lastDragViewportPoint = e.GetPosition(_snapshotScroll);   // DRAGFREE_01
                StartEdgePan();                                            // DRAGFREE_01
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
                _lastDragViewportPoint = e.GetPosition(_snapshotScroll);   // DRAGFREE_01
                StartEdgePan();                                            // DRAGFREE_01
                SetSourceCursor(StandardCursorType.SizeAll);
                e.Pointer.Capture(_sourceCanvas);
                e.Handled = true;
                return;
            }
        }

        // 3: a Magic Wand candidate. MAGICWAND_02 - the real detector landed, so this branch is
        // live. Tag carries the CandidateSpec that ShowMagicWandCandidates attached, which is how
        // one click both places the box AND pre-picks the role HudAutoDetector believes it is.
        if (e.Source is Control control && control.Tag is CandidateSpec candidate)
        {
            SetSourceSelection(candidate.Rect, candidate.RoleKey);
            AutoZoomToSelection();
            e.Handled = true;
            return;
        }

        // 4: nothing else applied — draw a new box.
        _sourceDrag = SourceDrag.Drawing;
        _sourceSelectionStart = p;
        _isDrawingSourceSelection = true;
        _lastDragViewportPoint = e.GetPosition(_snapshotScroll);   // DRAGFREE_01
        StartEdgePan();                                            // DRAGFREE_01
        EnsureSelectionVisuals();
        SetHandlesVisible(false);
        UpdateSelectionRect(new Rect(p, new Size(1, 1)));
        e.Pointer.Capture(_sourceCanvas);
        e.Handled = true;
    }

    private void SourceCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_sourceCanvas == null) return;

        // DRAGFREE_01 — SELF-HEALING. If a drag is somehow still live with no button down, the
        // capture was lost without the release ever arriving. End it here rather than letting the
        // rectangle keep chasing a pointer that is not pressing anything.
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
            // The anchor is viewport-relative, so it stays valid after the offset moves.
            _panAnchorViewport = now;
            e.Handled = true;
            return;
        }

        // DRAGFREE_01 — remember where the pointer is IN THE VIEWPORT, for the edge-pan tick.
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
        // CROSSHAIR_01 - updated on every move, including mid-drag: lining the FAR edge of a box up
        // with something on the other side of the frame is exactly when the guides earn their keep.
        UpdateCrosshair(p);

        switch (_sourceDrag)
        {
            case SourceDrag.Drawing:
                UpdateSelectionRect(NormalizeRect(_sourceSelectionStart, p));
                return;

            case SourceDrag.Moving:
            {
                // Translate by the pointer delta and clamp the WHOLE rect, so dragging into an
                // edge slides along it instead of shrinking the box.
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

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // DRAGFREE_01 — EDGE AUTO-PAN, AND THE END-OF-DRAG PATH EVERYTHING SHARES.
    //
    // The frozen frame is routinely magnified past the viewport (AUTOZOOM_01 is built to do exactly
    // that), so a box the user wants to draw or stretch is frequently BIGGER than what is on
    // screen. With no auto-pan the gesture simply stops at the edge of the viewport: the pointer
    // can go no further, the box can grow no further, and the only way out is to abandon the drag,
    // scroll, and start again. That is the other half of "my mouse cursor gets trapped".
    //
    // A timer rather than a per-move nudge, because the interesting case is the pointer HELD at
    // the edge, where no move events arrive at all.
    // ══════════════════════════════════════════════════════════════════════════════════════════

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

        // Speed ramps with how far into the margin the pointer is, so a pointer just inside the
        // edge creeps and one pinned against it moves briskly. A constant speed reads as a lurch.
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
        if (_snapshotScroll.Offset == before) return;   // already against the extent

        // The view moved, so the source pixel under the (stationary) pointer moved with it.
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
        EndSourceDrag();                 // DRAGFREE_01 — one exit, cursor restored, pan stopped
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

            // AUTOZOOM_01 — the move that makes this tool usable. See the method.
            AutoZoomToSelection();
            ShowRolePopup();                                   // ROLEPOPUP_01
        }
        else if (finished == SourceDrag.Moving)
        {
            ShowRolePopup();                                   // ROLEPOPUP_01
        }
        else
        {
            // A resize also re-zooms: after dragging a corner the box is a different size, so
            // the "fills ~70% of the viewport" relationship has to be re-established or the
            // next adjustment is made at the wrong magnification.
            AutoZoomToSelection();
            ShowRolePopup();                                   // ROLEPOPUP_01
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
        EndSourceDrag();   // DRAGFREE_01
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
        // Normalised, so dragging a corner past its opposite flips the box instead of
        // producing a negative-size rect that would silently fail every downstream clamp.
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

        // Captured BEFORE the scale changes: this is the source pixel the user is pointing at,
        // and where on screen they are pointing at it.
        Point anchorSource = e.GetPosition(_sourceCanvas);
        Point anchorViewport = e.GetPosition(_snapshotScroll);

        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        ApplySnapshotZoomInternal(CurrentZoom() * factor, fitMode: false, markUserZoom: true);

        _snapshotZoomHost?.UpdateLayout();
        _snapshotScroll.UpdateLayout();

        // ══════════════════════════════════════════════════════════════════════════════════════
        // ZOOMKEEP_01 — ONCE A BOX EXISTS, THE BOX IS WHAT THE ZOOM IS ABOUT.
        //
        // Pointer-anchored zoom is the right default on a bare picture: the pixel you point at
        // stays put. It is the wrong rule the moment there is a finished selection on screen,
        // because the thing the user is working on is the BOX, and the pointer is wherever their
        // hand happened to leave it — often outside the box, sometimes outside the frame. Zooming
        // then walked the box off the edge of the viewport and the user had to hunt it down with
        // the scrollbars after every notch.
        //
        // So: a committed selection (the one with the marching ants) is re-centred on every notch
        // and cannot escape. A box still being DRAWN is deliberately left on the pointer anchor —
        // its centre is moving under the user's hand, and chasing it would slide the frame around
        // mid-gesture.
        // ══════════════════════════════════════════════════════════════════════════════════════
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

        // The zoom moved the surface under a pointer that has not moved, so the source pixel it is
        // over has changed. Re-run the gesture against the new one, or the band would lag a notch
        // behind the view until the user jiggled the mouse.
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

        // AUTOZOOM_02 — "at FIT, or only a little above it". 1.35x of the fit scale, not an
        // absolute zoom: on a 4K capture fit is ~0.25 and on a 1080p one in a big panel it is 1.0,
        // so any fixed number would be wrong for one of them.
        if (current > fit * AutoZoomArmThreshold)
        {
            return;
        }

        // CANCELSEL_01 — remember where the view was, once per selection, and only when something
        // is actually about to change it.
        _preZoomState ??= new PreZoomState(current, _snapshotFitMode, _userZoomed, _snapshotScroll.Offset);

        double target = viewportW * 0.7 / Math.Max(1, sel.Width);

        // ══════════════════════════════════════════════════════════════════════════════════════
        // AUTOCENTER_01 (replacing POPUPFIT_01's caps) — ZOOM ONLY AS FAR AS STILL LEAVES THE
        // CHOOSER A BAND TO LIVE IN, AND RESERVE IT WHERE A CENTRED BOX ACTUALLY LEAVES ROOM.
        //
        // The 0.7 above is the precision ambition: make the box big so the user can see what they
        // are trimming.
        //
        // POPUPFIT_01 protected that ambition with a WIDTH reserve, and paired it with a deliberate
        // horizontal bias that pushed the box off-centre so the whole reserve collected on one
        // side. The bias is what the user reported as the view jumping away from the rubber band,
        // and it has been removed (see the end of this method) — which also removes the point of
        // the width reserve, because a centred box splits it into two halves and half a chooser
        // width is not a placement. Both are gone. The box now gets the full precision zoom
        // horizontally.
        //
        // What remains is a HEIGHT reserve, and it is DOUBLED, for exactly the reason the width
        // reserve failed: a centred box splits spare space evenly, so reserving one band's worth
        // yields two half-bands and neither is usable. Reserving two guarantees a full band both
        // above and below, and PositionRolePopup then puts the chooser in whichever is roomier —
        // where the entire viewport width is available to it and nothing needs to be reserved
        // horizontally at all.
        //
        // Height is the right dimension to negotiate over because the element list is inside a
        // ScrollViewer: a shorter chooser still shows every element, a narrower one truncates their
        // names.
        //
        // Applied BEFORE the clamp to [fit, MaxZoom], so the fit scale still wins when even the fit
        // view cannot spare the room (a HUD element that spans the whole frame). PositionRolePopup's
        // shrink-and-overhang path then takes over — the genuine last resort it was meant to be.
        // ══════════════════════════════════════════════════════════════════════════════════════
        double viewportH = _snapshotScroll.Viewport.Height;

        double bandPerSide = RolePopupMinHeight + RolePopupGap + RolePopupEdge;
        double usableH = viewportH - 2 * bandPerSide;
        if (viewportH > 40 && usableH > 40)
        {
            target = Math.Min(target, usableH / Math.Max(1, sel.Height));
        }

        // Clamped at the bottom by the FIT scale rather than by 1.0: on a 4K capture in a small
        // panel, 1.0 would be a zoom IN disguised as a floor.
        target = Math.Clamp(target, fit, MaxZoom);
        if (Math.Abs(target - current) > 0.02)
        {
            ApplySnapshotZoomInternal(target, fitMode: false, markUserZoom: false);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // AUTOCENTER_01 — THE BOX GOES IN THE MIDDLE. FULL STOP.
        //
        // This used to add a deliberate horizontal bias of half the reserved chooser width, to
        // collect the spare room on one side instead of splitting it. It was defensible on paper
        // and wrong in the hand: the user drags a box, lets go, and the frame slides sideways so
        // the thing they just drew is off-centre — for a reason that is invisible to them, since
        // the chooser that the room was being made for has not appeared yet. Reported as "it jumps
        // out of focus from the actual center of the rubberband".
        //
        // The centre is now exact. The chooser gets its room from the two zoom caps above (which
        // are unchanged) and, when that is not enough, from PositionRolePopup's own search — which
        // is allowed to place the chooser in the band above or below the box, and in the last
        // resort to hang past the panel edge. Placing the menu is the menu's problem; it is not a
        // reason to move the user's work off-centre.
        // ══════════════════════════════════════════════════════════════════════════════════════
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

        // The scale change above has not been laid out yet; without this the measurement is of
        // the OLD geometry and the view lands in the wrong place.
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

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // BANDCONTRAST_01 — THE RUBBER BAND PICKS A COLOUR THE FRAME UNDERNEATH CANNOT SWALLOW.
    //
    // The band used to be drawn in AppSuccessColor — a mid green, at 2px, over a Fortnite frame.
    // Against grass, against a health bar, against the green of the minimap, it was invisible; the
    // reported symptom was "hard to see, barely visible". A single fixed colour cannot work here,
    // because the background is not a UI surface the theme controls, it is an arbitrary photograph
    // of a game.
    //
    // So the colour is measured from the picture. The pixels under the band's own outline are
    // averaged, and the band is drawn in the OPPOSITE of that average on both axes that matter:
    //   * opposite HUE   — complementary, so it separates by colour;
    //   * opposite VALUE — dark band on a bright region, bright band on a dark one, so it still
    //                      separates for a colour-blind user and on a washed-out capture.
    // A neutral region (low saturation: grey smoke, white UI, black letterbox) has no meaningful
    // complement, so it gets RED, which is also the colour the band starts as before any frame has
    // been measured. Red on grey is the default, and the dynamic contrast is what departs from it.
    //
    // Measured from a DOWNSCALED copy of the frame, not the live one: a few hundred samples off a
    // ~640px-wide buffer costs microseconds and this runs on every pointer move of a drag.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>BANDCONTRAST_01 — the band's colour before any frame has been measured.</summary>
    private static readonly Color BandDefaultColour = Color.FromRgb(255, 42, 42);

    /// <summary>BANDCONTRAST_01 — widest edge of the sampling copy, in pixels.</summary>
    private const int ContrastSampleMaxEdge = 640;

    private byte[]? _contrastSamples;     // RGB triplets, row-major
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
            // A sampler is an enhancement, never a requirement: without it the band is simply red.
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

        // Recomputing for a rectangle that has barely moved would make the colour shimmer during a
        // drag. Three source pixels is below the point where the average can meaningfully change.
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
        const int Band = 2;   // sample this far either side of the outline

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
                Take(x, top + d);       // top edge
                Take(x, bottom + d);    // bottom edge
                Take(left + d, y);      // left edge
                Take(right + d, y);     // right edge
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

    // COLORMATH_01 — OppositeOf moved verbatim; see the extracted type.

    // COLORMATH_01 — RgbToHsv moved verbatim; see the extracted type.

    // COLORMATH_01 — HsvToColor moved verbatim; see the extracted type.

    // COLORMATH_01 — RelativeLuminance moved verbatim; see the extracted type.

    /// <summary>
    /// CROPCANVAS_01 — builds the selection rectangle and its four corner handles, once.
    /// </summary>
    private void EnsureSelectionVisuals()
    {
        if (_sourceCanvas == null || _selectionRect != null)
        {
            return;
        }

        // BANDCONTRAST_01 — the stroke used to be AppSuccessColor, a mid green. The Zero Raw Hex
        // mandate is about UI SURFACES the theme owns; this line is drawn on top of an arbitrary
        // frame of somebody's gameplay, which no theme token can know anything about. It starts red
        // (the defined default) and BandColourFor replaces it with the measured opposite of the
        // picture underneath on every geometry change. See the block above BandDefaultColour.
        _selectionRect = new Rectangle
        {
            Stroke = new SolidColorBrush(BandDefaultColour),
            StrokeThickness = 2.5 / Math.Max(0.01, CurrentZoom()),
            Fill = new SolidColorBrush(Color.FromArgb(28, BandDefaultColour.R, BandDefaultColour.G, BandDefaultColour.B)),
            // ANTS_01 - a dashed outline, animated by ScrollAnts below. A static thin rectangle on
            // a busy game frame reads as part of the HUD; a crawling dash reads as a selection and
            // nothing else. Copied from the Python tool, which ran the same 100ms 8px cycle
            // (crop_widgets.py _update_ant_dash).
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
            IsHitTestVisible = false,
            ZIndex = 500
        };
        _sourceCanvas.Children.Add(_selectionRect);
        StartAnts();

        // ANTS_01 - TWO handles, top-left and bottom-right, matching the phone-preview items so
        // there is one grab language across both canvases. The other two corners are still
        // resizable: HitTestSelectionCorner tests all four arithmetically, so tr/bl work by feel
        // even though nothing is drawn there.
        for (int i = 0; i < 2; i++)
        {
            // IsHitTestVisible = false deliberately: the corners are hit-tested arithmetically in
            // HitTestSelectionCorner against a SCREEN-pixel tolerance. Letting these little
            // rectangles take the press instead would make the grab area shrink as you zoom out,
            // which is precisely when a handle is hardest to hit.
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

    // ==================================================================================
    // CROSSHAIR_01 — four guide rays from the pointer to the edges of the frozen frame.
    //
    // A HUD box almost never stands alone: its edge has to line up with something on the far side
    // of a 1920- or 3840-wide frame — the opposite end of a health bar, the matching margin on the
    // other side of the screen. Judging that across a picture this wide, by eye, is guesswork.
    // A ruler that reaches both edges turns it into reading off a line.
    //
    // FOUR rays with a gap at the pointer, not two crossing lines. The gap keeps the exact pixel
    // under the cursor visible, which is the one pixel the user is aiming at; a solid crossing
    // covers it. The old Python tool drew two full lines plus a small cross for the same reason
    // (crop_widgets.py _draw_crosshair).
    //
    // Everything here is IsHitTestVisible = false. A guide that eats a pointer event would break
    // the rubber band it exists to help — see 04_UI_UX_AVALONIA_SPEC.md#UI-THEME on decorative
    // overlays taking input.
    // ==================================================================================

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
                // Gentle on purpose: this sits on top of gameplay the user is reading. Strong
                // enough to follow, faint enough not to be mistaken for part of the HUD.
                Stroke = new SolidColorBrush(Color.FromArgb(120, colour.R, colour.G, colour.B)),
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 5, 4 },
                IsHitTestVisible = false,
                IsVisible = false,
                // Below the selection (500) and its handles (520): the box and its grab points
                // always win, the guides never draw over them.
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

        // Thickness only. StrokeDashArray is in UNITS OF StrokeThickness, so the dash rescales
        // itself and is set once in EnsureCrosshair - rebuilding four AvaloniaLists on every
        // pointer move would allocate continuously for no visible difference.
        foreach (Line line in _crosshairLines)
        {
            line.StrokeThickness = 1.0 / scale;
        }

        // left, right, up, down
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

        // BANDCONTRAST_01 — the outline has moved, so what is underneath it has changed.
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

        // Handles are sized in SOURCE pixels but derived from a SCREEN constant, so they stay
        // the same physical size however far in or out the frame is zoomed.
        double h = HandleScreenPx / scale;
        // ANTS_01 - top-left and bottom-right only.
        var corners = new[]
        {
            new Point(rect.X, rect.Y),
            new Point(rect.X + rect.Width, rect.Y + rect.Height),
        };

        // ROLEPOPUP_01 - the popup is anchored to the box, so any change to the box's geometry or
        // to the zoom moves it. UpdateSelectionRect is the single funnel every such change goes
        // through, which is why the call belongs here rather than at a dozen call sites.
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

        // keepRoleName: a move or a resize must not re-guess the element. The user has already
        // chosen (or been offered) a name; silently swapping it because the box crossed the
        // middle of the frame mid-drag would be the kind of thing that makes a tool feel
        // possessed.
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
        StopAnts();   // ANTS_01 - no rectangle, no timer.
        HideRolePopup();
        if (_selectionInfo != null)
        {
            _selectionInfo.Text = "Drag a box around one HUD element, then pick what it is.";
        }
    }

    // ==================================================================================
    // ROLEPOPUP_01 — naming a HUD element happens AT the box.
    //
    // The old flow: drag a box on the frame, then move the eye and the mouse down to a bar at the
    // bottom of the panel, type a name into a free-text field, then find and click ADD SELECTION.
    // Three problems, all of them fatal to a non-technical user:
    //   * the confirm was nowhere near the thing being confirmed;
    //   * free text meant a typo silently created a brand-new element key that
    //     RehydrateSavedLayersAsync would never load back (the old A3 defect);
    //   * nothing on screen told the user which elements this profile even HAS.
    //
    // This is the old Python tool's RoleToolbar (crop_widgets.py) rebuilt as an in-panel Border.
    // Draw a box and the list appears beside it; click a name and the layer is created. Picking
    // the name IS the confirm — there is no second button.
    // ==================================================================================

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

        // ISSUE_08 (audit round 6) — raise the lightbox with the chooser. The scrim is a sibling
        // declared before RolePopup in the same grid cell, so the popup keeps painting on top of it
        // while the frame underneath dims and stops taking clicks.
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

            // NODUPES_02 — capture the ROLE, not its label. See ConfirmSelectionAsAsync.
            HudRole captured = role;
            button.Click += async (_, _) => await ConfirmSelectionAsAsync(captured);
            list.Children.Add(button);
        }

        // The "+ New element" entry. A plus sign, because that is the one symbol everybody already
        // reads as "make another one".
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

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // POPUPFIT_01 — THE ROOM THE CHOOSER NEEDS, SHARED BY THE TWO THINGS THAT DECIDE ITS FATE.
    //
    // These used to be locals inside PositionRolePopup, which meant AUTO-ZOOM knew nothing about
    // them — and auto-zoom is what took the room away. The reported symptom ("the menus are barely
    // visible and cut off" after a precision cut) is the two halves disagreeing:
    //
    //   AutoZoomToSelection magnifies a new box until it fills 70% of the viewport WIDTH. That
    //   leaves 15% down each side. In this window the landscape panel is the 58 of a 58/42 split of
    //   a 1200-1600px window, so the viewport is roughly 700-930px and 15% of it is 105-140px.
    //   The chooser's own MinWidth is 210. It has NEVER fitted beside a freshly auto-zoomed box.
    //
    // So the placement search fell through every branch to its last resort on virtually every
    // precision cut — which is not an edge case at all, it is the main path. Both halves now size
    // themselves from the same three numbers.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Breathing room between the rubber band and the chooser. Never less than this, on
    /// any side, in any branch — that is the "never too close to the rubberband" guarantee.</summary>
    private const double RolePopupGap = 12;

    /// <summary>Breathing room between the chooser and the panel edge.</summary>
    private const double RolePopupEdge = 6;

    // AUTOCENTER_01 — RolePopupPlanningWidth USED TO LIVE HERE and is deliberately gone.
    // It was the chooser width auto-zoom reserved BESIDE the box. Reserving it is what forced the
    // box off-centre, which is the behaviour the user reported as the frame jumping away from the
    // rubber band. The chooser is now placed above or below instead, where the whole viewport width
    // is already available and nothing has to be reserved for it. Do not reintroduce a horizontal
    // reserve without also reintroducing the off-centre bias — the two only ever made sense as a
    // pair, and the pair is what was wrong.

    /// <summary>Below this the element list is too short to choose from, so a vertical band this
    /// small is not a placement, it is a worse overlap.</summary>
    private const double RolePopupMinHeight = 130;

    private void PositionRolePopup()
    {
        var popup = RolePopupCtl;
        var host = this.FindControl<ScrollViewer>("SnapshotScroll");
        if (popup == null || host == null || _sourceCanvas == null || _sourceSelection is not { } sel) return;

        // Reset the ceiling before measuring: a previous tight placement may have shrunk it, and
        // measuring the shrunk size would make the popup stay small once there was room again.
        popup.MaxHeight = PopupMaxHeight;
        popup.UpdateLayout();
        double pw = popup.Bounds.Width > 0 ? popup.Bounds.Width : popup.MinWidth;
        double ph = popup.Bounds.Height > 0 ? popup.Bounds.Height : 200;

        // Measured, not calculated: the box lives on a canvas that is scaled and scrolled, so its
        // position on screen cannot be derived from its source pixels.
        Point? tl = _sourceCanvas.TranslatePoint(new Point(sel.X, sel.Y), host);
        Point? br = _sourceCanvas.TranslatePoint(new Point(sel.X + sel.Width, sel.Y + sel.Height), host);
        if (tl == null || br == null) return;

        double vw = host.Bounds.Width;
        double vh = host.Bounds.Height;
        if (vw < 20 || vh < 20) return;

        // POPUPFIT_01 — the same three numbers auto-zoom sizes itself from. See their declarations.
        const double Gap = RolePopupGap;
        const double Edge = RolePopupEdge;
        const double MinPopupHeight = RolePopupMinHeight;

        // Only the VISIBLE part of the box can be covered, and at high zoom the box routinely
        // extends past the viewport, so clip it before measuring the free bands around it.
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
            // NOTHING FITS AT FULL SIZE — and this is the COMMON case, not an edge case.
            // AUTOZOOM_01 deliberately magnifies a new box until it fills ~70% of the viewport
            // width, which leaves ~15% down each side: far less than the popup needs. So after
            // almost every draw both horizontal bands are too narrow, and the old code's final
            // clamp dropped the popup straight onto the box. That is the reported bug.
            //
            // Height is the dimension that can shrink without loss, because the element list is
            // inside a ScrollViewer — a shorter popup scrolls, a narrower one just truncates the
            // names. So the roomier of the two VERTICAL bands wins and the popup is capped to it.
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
                // ══════════════════════════════════════════════════════════════════════════════
                // POPUPFIT_01 — THE GENUINE LAST RESORT, AND IT STILL MAY NOT TOUCH THE BOX.
                //
                // With auto-zoom now reserving room, reaching this branch means the box fills the
                // viewport even at the fit scale — a HUD element that spans the whole frame. There
                // is no placement that is both fully inside the panel and clear of the box, so one
                // of those two has to give, and the reported complaint settles which: a menu that
                // is "barely visible and cut off" is a menu sitting ON the box, swallowing the
                // pointer that is trying to reach the thing underneath it.
                //
                // So the box wins the no-overlap guarantee outright and the PANEL EDGE gives way.
                // The inward edge is pinned at exactly Gap from the box — the breathing room is
                // never negotiable — and the popup is allowed to hang past the panel on the far
                // side, where nothing is being aimed at.
                //
                // The overhang is bounded at 40% so the chooser can never be reduced to a sliver:
                // past that it stops being usable in its entirety, which is the other half of what
                // was asked for. If even 60% will not fit on the roomier horizontal side, the
                // VERTICAL axis is tried the same way, because a full-frame box is usually wide
                // rather than tall and the band above or below it is the one with real room.
                // ══════════════════════════════════════════════════════════════════════════════
                const double MinVisibleFraction = 0.6;

                bool preferRight = freeRight >= freeLeft;
                double horizontalVisible = preferRight ? vw - (boxR + Gap) : boxL - Gap;

                bool preferBelow = freeBelow >= freeAbove;
                double verticalVisible = preferBelow ? vh - (boxB + Gap) : boxT - Gap;

                if (horizontalVisible >= pw * MinVisibleFraction || horizontalVisible >= verticalVisible)
                {
                    // Side placement. X is pinned off the box and never clamped — clamping X is
                    // exactly what used to drag the popup back on top of the box.
                    x = preferRight ? boxR + Gap : boxL - Gap - pw;
                    y = ClampY(boxT);
                }
                else
                {
                    // Vertical placement. Y is pinned off the box; X is free to be clamped into the
                    // panel because on this axis X cannot cause an overlap.
                    y = preferBelow ? boxB + Gap : boxT - Gap - ph;
                    x = ClampX(boxL);
                }

                RuntimeLog.Debug("CROP",
                    $"Role chooser has no clear placement inside the panel ({vw:F0}x{vh:F0}, box {boxR - boxL:F0}x{boxB - boxT:F0}); " +
                    "overhanging the panel edge rather than covering the selection.");
            }
        }

        // POPUPFIT_01 — the final guarantee, stated once, in one place, after every branch.
        //
        // Whatever the search decided, the chooser must not end up within Gap of the rubber band.
        // The branches above are each individually correct, but they are four separate pieces of
        // reasoning and this window has already shipped one bug (POPUPCLEAR_01) caused by a clamp
        // quietly undoing a placement that was right when it was computed. This is cheap, it is
        // unconditional, and it turns "every branch is careful" into "no branch can be wrong".
        bool overlaps = x < boxR + Gap && boxL - Gap < x + pw
                     && y < boxB + Gap && boxT - Gap < y + ph;

        if (overlaps)
        {
            // Push it out along whichever axis needs the least movement.
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

        // The popup is a sibling of the ScrollViewer in the same grid cell, so a margin measured
        // from the ScrollViewer's own top-left is the correct offset.
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
        if (_rolePopupScrim != null) _rolePopupScrim.IsVisible = false;   // ISSUE_08
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

        // NODUPES_02 — RegisterCustomRole already returns the role, and it returns the EXISTING one
        // when the name matches something known, so typing "Loot Area" into "+ New element" now
        // lands on the built-in `loot` instead of minting a second element that merely looks like it.
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
        // Kept only so the window's public RoleName property still reflects what is being placed.
        // NOTHING derives a key from it any more, and nothing may start doing so again.
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
            _wandPreviewIndex = -1;   // MAGICWAND_02 - see LoadSnapshotAsync for why the cache stays

            RestoreSourceViewAfterSelection(); // CROPZOOMRESET_01

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

        // ITEMMENU_01 - right-click an element for ordering and delete, the way the old Python
        // tool did (portrait_view.py contextMenuEvent). The footer buttons still exist and do the
        // same things; this is the version you reach without moving the mouse off the element you
        // are already working on, which is what the removed LAYERS list was being used for.
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
            Fill = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red),   // TONE_01
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

        // RESIZEFEEL_01 — the ratio this gesture is locked to, captured once at the start so the
        // drag cannot drift onto a rounded copy of it part-way through.
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
            PushHistory(current, CropHistoryLabels.ForPointerGesture(_composerEditMode != ComposerEditMode.Drag));   // UNDO_27 — one gesture, one step
            RefreshLayerList();
        }

        _activeEditItem = null;
        _composerEditMode = ComposerEditMode.None;
        _editStartSnapshot = null;
        e.Handled = true;
    }

    /// <summary>RESIZEFEEL_01 — the ratio a HUD element must keep, taken from the SOURCE crop (the session's rule).</summary>
    private double SourceAspectOf(CropEditorItem item) => _edit.SourceAspectOf(item.Model);

    // CROPGEOM_01 — DiagonalWidthDelta moved verbatim; see the extracted type.

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
        // RESIZEFEEL_01 — locked to the SOURCE ratio rather than to the placed item's rounded one.
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

        // Dragging the TOP-LEFT corner outwards means moving up and left, so the diagonal
        // projection is negated: away from the anchor is a bigger box.
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
        _edit.NormalizeLayout(item.Model);   // EDITSTATE_01 — quantised size, kept on the canvas
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

    // ISSUE_2 / DELETESET_01 / KEYCASE_01 — the tombstones of deliberately deleted elements are the
    // session's (CropEditSession.DeletedRoleKeys): a save merges, so they are the only proof of intent.


    private void DeleteSelectedItem()
    {
        CropEditorItem? view = SelectedView;
        if (_edit.DeleteSelected() == null) return;   // EDITSTATE_01 — tombstones the key (ISSUE_2), marks dirty
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
        // ZTIEBREAK_01 - the list paints top-of-stack first, so it is the reverse of the shared
        // rule (ascending Z, then RoleKey OrdinalIgnoreCase) used by the composer and by
        // MobileFilterBuilder. DisplayName was the old tie-break here and RoleKey is the
        // exporter's; two elements on the same z could therefore be listed in one order and
        // rendered in the other. RoleKey is unique per element, so this is now total.
        foreach (CropLayer layer in _edit.ListOrder)
        {
            _layers.Add(new LayerEntry(layer.RoleKey, layer.DisplayName, layer.Z));
        }

        if (_layerList != null && selectedKey != null)
        {
            _layerList.SelectedItem = _layers.FirstOrDefault(l => l.RoleKey == selectedKey);
        }

        // LAYERSPANE_01 - EmptyLayersText belonged to the removed list. Resolving to null here is
        // expected, not a bug; the guard below keeps the call harmless.
        var emptyLayers = this.FindControl<TextBlock>("EmptyLayersText");
        if (emptyLayers != null) emptyLayers.IsVisible = _layers.Count == 0;

        // ISSUE_07 — every path that adds, deletes, resets or undo/redo-restores an item ends up
        // here, so this is the one place the composer's empty state has to be reconciled from.
        UpdateComposerEmptyState();

        RefreshActionButtons();
    }

    /// <summary>
    /// IDEA_1 — turns previously saved layers back into REAL, draggable items. EDITSTATE_01 — what
    /// the document says (and the DRIFT TRAP / RESGUESS_01 rules for reading it) is
    /// <see cref="CropProfileCodec.ReadSavedLayers"/>; this window loads the file, cuts each
    /// thumbnail and builds the visuals. Safe to call more than once: a placed role is skipped.
    /// </summary>
    private async Task RehydrateSavedLayersAsync()
    {
        if (_portraitCanvas == null) return;

        try
        {
            JsonObject config = HudConfig.Sanitize(await new CropConfigStore(_paths).LoadAsync());

            foreach (CropLayer layer in CropProfileCodec.ReadSavedLayers(config, _edit))
            {
                if (_snapshotPath != null)
                {
                    try { layer.CropImagePath = await CropSnapshotRegionAsync(_snapshotPath, layer.SourceRect); }
                    catch (Exception ex) { RuntimeLog.Info("CROP", $"Thumbnail for '{layer.RoleKey}' could not be built: {ex.Message}"); }
                    if (!string.IsNullOrEmpty(layer.CropImagePath)) _tempFiles.Add(layer.CropImagePath);
                }

                _edit.AdoptSavedLayer(layer);   // GHOSTKILL_01 / RESGUESS_01 flags set by the codec
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
            // RESGUESS_01 — a real frame now exists, so geometry that was read from the profile
            // without one can finally be checked against it. This is the only place an unverified
            // element becomes verified, and it is also the only place it becomes safe for
            // SaveConfigAsync to rewrite that element's crop rectangle.
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

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // ALWAYSLIVE_01 (audit round 6) — ApplySavedCropVisibility() USED TO LIVE HERE. IT IS DELETED.
    //
    // It read "Show Saved Crops" and the opacity slider and applied them to every item flagged
    // FromSavedConfig: hiding them outright when the box was unticked, and fading them to as little
    // as 20/255 otherwise. Both controls are gone from the AXAML, and with them the premise that
    // the elements already in the profile are something to be dimmed or dismissed.
    //
    // The elements of the chosen profile are the reason the window was opened. They are drawn, at
    // full opacity, from the moment RehydrateSavedLayersAsync finishes, and they drag, resize,
    // reorder, delete and re-save exactly like anything drawn this session. CreateItem no longer
    // has a visibility rule to ask about, so there is nothing to reapply after an edit either.
    //
    // DO NOT reintroduce a visibility or opacity gate here. If a future need arises to tell a saved
    // element from a new one, do it with the LABEL (CropEditorItem.LabelText) — a word the user can
    // read — not by making the element harder to see.
    // ══════════════════════════════════════════════════════════════════════════════════════════

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

    // MAGICWAND_02 / AIHUD_01 — the Magic Wand (local detector + optional AI assistance) lives in
    // CropToolWindow.MagicWand.cs, moved out so this code-behind can shrink under MVVM_02.

    private async Task SaveAndReturnAsync(object? sender)
    {
        // GATE_01 (F3): the save path ends in SyncActiveProfileFromCurrentConfig(), which writes
        // over a profile FILE. Reaching it with no chosen profile means overwriting whichever
        // profile some other part of the suite left active.
        if (_edit.ActiveProfile == null)
        {
            SetStatus("Choose a profile before saving.");
            return;
        }

        // SAVECONFIRM_01 / CROPSAVEPROMPT_02 — name the destination and the action.
        // Saving is the primary action; going back keeps the current edits open.
        // "KEEP IT" was ambiguous about whether it kept the saved profile or the new work.
        bool confirmed = await Controls.ConfirmDialogWindow.AskAsync(
            this,
            $"Save this layout to \"{_edit.ActiveProfile}\" and return to the main app?\n\n" +
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
        // EMPTYSAVE_01 — this used to be a bare `if (_items.Count == 0) return false;`, which made
        // "delete the last element and save" impossible: the tombstones in _edit.DeletedRoleKeys are
        // written INSIDE this method, below, so the early return threw away the very record that
        // says the removal was deliberate. The user deleted an element, pressed SAVE, got
        // "No HUD elements are currently placed.", and the element came straight back on reload.
        // An empty profile is a legitimate document; a save with nothing placed AND nothing deleted
        // is the only genuinely empty gesture.
        if (_edit.Layers.Count == 0 && _edit.DeletedRoleKeys.Count == 0)
        {
            SetStatus("No HUD elements are currently placed.");
            return false;
        }

        try
        {
            RuntimeLog.Info("CROP", "Saving crop coordinates.");
            var store = new CropConfigStore(_paths);
            
            // Recover before changing backups. SaveAsync owns the single, locked rotation.
            JsonObject config = await store.LoadAsync();

            // EDITSTATE_01 — the layers, the RESGUESS_01 position-only rule and the DELETESET_01 /
            // KEYCASE_01 tombstones are written by the codec, MERGED into the document on disk.
            int unverifiedCount = CropProfileCodec.WriteLayers(config, _edit);
            foreach (CropEditorItem item in _items) RenderItemLayout(item);   // the codec re-quantised them

            if (unverifiedCount > 0)
            {
                // RESGUESS_01 — say so out loud. A silent partial save is how a user ends up
                // believing a resize was stored when only the move was.
                SetStatus(unverifiedCount == _items.Count
                    ? "Saved position and layer order only. Load the video to edit the crop rectangles."
                    : $"Saved. {unverifiedCount} element(s) kept their stored crop rectangles — load the video to edit those.");
            }

            config = HudConfig.Sanitize(config);
            await store.SaveAsync(config);

            // A live-config write alone is not a successful profile save.
            if (!FreeVideoStudio.App.Infrastructure.MaskOverlayManager.SyncActiveProfileFromCurrentConfig())
            {
                SetStatus("Could not save this profile. Your edits are still open. Please try saving again.");
                return false;
            }

            _edit.Dirty = false;
            RefreshActionButtons();
            RuntimeLog.Success("CROP", "Saved crop coordinates successfully.");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // SILENTRESET_01 — CropConfigStore.SaveAsync now REFUSES a document that fails its
            // preconditions instead of quietly replacing it with factory defaults. That refusal
            // carries a specific reason, and the user is the only one who can act on it, so it is
            // shown rather than buried in the log.
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
            var choice = await Controls.ConfirmDialogWindow.AskSaveChangesAsync(
                this, _edit.ActiveProfile ?? "this profile", destination);
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
        _wandPreviewIndex = -1;   // MAGICWAND_02

        // DELETESET_01 — the tombstones die with the working state (a tombstone belongs to the
        // profile that created it: a profile SWITCH runs this). EMPTYSAVE_01 — RESET tombstones what
        // it removed and stays dirty so SAVE can empty the profile on disk. Both are the session's.
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

    // UNDO_27 — Undo / Redo / PushHistory / CaptureSnapshot / RestoreSnapshot live in
    // CropToolWindow.History.cs, on the shared UndoStack<T> (docs/07_UNDO_AND_HISTORY.md).

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
        // GATE_01 (F3): with no profile chosen there is no file to write to, so SAVE stays off
        // whatever the item state says. This is the second lock on the same door - SetProfileGate
        // disables the button too - because RefreshActionButtons is called from a dozen places and
        // any one of them re-enabling SAVE would re-open the overwrite hole.
        bool profileChosen = _edit.ActiveProfile != null;

        // EMPTYSAVE_01 — `_items.Count > 0` alone kept SAVE greyed out after the last element was
        // deleted, so the deletion could never be committed. Pending tombstones are unsaved work
        // exactly like a placed element is.
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

        // The playhead badge is a XAML child of the markers canvas, so clearing the canvas would
        // destroy it. Take it out first and put it back.
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

            // The first and last labels are skipped: 0:00 and the duration are already printed by
            // the clocks either side of the slider, and drawing them again collides with those.
            if (t <= 0.001 || duration - t <= 0.001) continue;

            var label = new TextBlock
            {
                Text = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss"),
                Foreground = labelBrush,
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                IsHitTestVisible = false
            };
            // TIMELINESLIM_02 - CropTimelineScaleCanvas is an 11px overlay sharing the slider's cell
            // now, top-aligned, so the labels land in the empty band above the rail. SetTop(0) keeps
            // them inside it; anything larger would collide with the thumb.
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

    // CROPGEOM_01 — SnapAxis moved verbatim; see the extracted type.

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

    // CROPGEOM_01 — NormalizeRect moved verbatim; see the extracted type.

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

    // CROPJSON_01 — ReadSectionNode moved verbatim; see the extracted type.

    // CROPJSON_01 — WriteSectionNode moved verbatim; see the extracted type.

    // CROPJSON_01 — EnsureObject moved verbatim; see the extracted type.

    // CROPJSON_01 — ReadInt moved verbatim; see the extracted type.

    // CROPJSON_01 — ReadFrac moved verbatim; see the extracted type.

    // CROPJSON_01 — ReadDouble moved verbatim; see the extracted type.

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
            if (tip != null) ToolTip.SetTip(control, tip);   // UNDO_27 — name what Undo/Redo would do
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
        // KEYFOCUS_01 — while a text input owns focus (the Element Name box, the new overlay
        // profile name, …) the keyboard belongs to it: no delete, no undo, no arrow-nudging
        // while typing. Return without touching e.Handled so the control keeps the key.
        if (FreeVideoStudio.App.Infrastructure.KeyboardFocusPolicy.HotkeysSuspended(TopLevel.GetTopLevel(this)))
        {
            base.OnKeyDown(e);
            return;
        }

        // ROLEPOPUP_01 - Enter opens the chooser for the current selection. This is the keyboard
        // path that replaces the deleted ADD SELECTION button, and it is what makes an arrow-key
        // refinement finishable without touching the mouse again.
        if (e.Key is Key.Enter or Key.Return
            && _sourceSelection != null
            && SnapshotPanelCtl?.IsVisible == true)
        {
            ShowRolePopup();
            e.Handled = true;
            return;
        }

        // DRAGFREE_01 - Escape gets you out of a gesture that is in progress, before it gets you
        // out of anything else. A drag the user wants to abandon is the most urgent thing Escape
        // can mean, and having a keyboard way out is the backstop for every way a capture can be
        // lost that this code has not thought of.
        if (e.Key == Key.Escape && _sourceDrag != SourceDrag.None)
        {
            EndSourceDrag();
            if (_sourceSelection is { } keep)
            {
                // A move or a resize is abandoned back to the box as it stands; only a half-drawn
                // box has nothing to fall back to.
                UpdateSelectionRect(new Rect(keep.X, keep.Y, keep.Width, keep.Height));
            }
            else
            {
                ClearSourceSelection();
            }

            e.Handled = true;
            return;
        }

        // CANCELSEL_01 - Escape backs out one level at a time: first the "+ New element" row if it
        // is open, then the selection itself. Two Escapes to go from typing a name to a clean frame,
        // and never more than one step per press, so a reflex double-tap cannot throw away more than
        // the user meant.
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

        // UNDO_27 — Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z, decided by the shared HistoryShortcut.
        if (HistoryShortcutFor(e) is var command && command != HistoryCommand.None)
        {
            if (command == HistoryCommand.Redo) Redo(); else Undo();
            e.Handled = true;
            return;
        }

        // CROPCANVAS_01 — the frozen frame gets the arrows FIRST, and only while it is the panel
        // on screen with a live selection. Ordering matters: both panels want the arrow keys, and
        // whichever one the user is actually looking at must win. SnapshotPanel is only visible
        // during the draw/refine step, so this cannot steal nudges from the portrait composer.
        if (_sourceSelection != null
            && SnapshotPanelCtl?.IsVisible == true
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (NudgeSourceSelection(e.Key, e.KeyModifiers))
            {
                // ROLEPOPUP_01: a nudge does NOT re-open the chooser. Arrow keys are for the last
                // two or three pixels, and a menu flying up on every keypress would make that
                // unusable. The popup is hidden while nudging and Enter brings it back.
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
                NudgeSelectedItemSize(SelectedView, delta);   // RESIZEFEEL_01
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
        FreeVideoStudio.App.WindowBoundsHelper.SaveBoundsSync(this, "CropToolBounds");
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
