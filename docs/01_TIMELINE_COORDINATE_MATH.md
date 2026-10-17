# SPECIFICATION 01: TIMELINE & COORDINATE MATH

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FreeVideoStudio.Core/Media/CoordinateMath.cs` | `CoordinateConstants`, `CoordinateMath`, `Frac` | `CoordinateConstants.PortraitW`, `PortraitH`, `InternalW`, `InternalH`, `BackendScale`; `Frac.ScaleRound`, `SnapZoomWindow` | Canvas size constants (`CoordinateConstants`) and the exact-fraction transforms between 16:9 landscape source and 9:16 portrait export. |
| `src/FreeVideoStudio.Core/Media/OutputTimeline.cs` | `OutputTimeline`, `Chunk`, `Cut`, `Insertion` | `SourceToOutput`, `OutputToSourceRelative`, `SnapInsertionPoint`, `NormalizeCuts`, `InsertionAt`, `Create` | Authoritative mathematical model for single-clip linear output durations and frame-to-output conversions. |
| `src/FreeVideoStudio.Core/Media/MemePlacement.cs` | `MemePlacement`, `MemePresentationMode`, `MemeOverlayCorner`, `MemeOverlaySize`, `MemeOverlayLayout` | `ToInsertions`, `InlineOnly`, `CornerOnly`, `OutputDurationSec`, `VisibleInterval`, `Place`, `ScaleFilter` | MEMEMODE_01 — a meme is a full-screen insertion or a zero-duration corner overlay; the one overlay geometry. **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/HudAutoDetector.cs` | `HudAutoDetector`, `OnlineFrameAccumulator` | `DetectHudRegions`, `ScanFrame`, `SampleFramesAsync` | Automated HUD element detection and coordinate bounds discovery via constant-memory online sequential streaming (≤ 25 MB footprint). |
| `src/FreeVideoStudio.Core/Media/HudConfig.cs` | `HudConfig` | `Sanitize`, `HudKeys`, `GetContentCrop` | HUD configuration schema validation, profile definitions, and content-space coordinates. |
| `src/FreeVideoStudio.Core/Media/HudImageOps.cs` | `HudImageOps` | `Crop`, `Threshold`, `MatchTemplate`, `ComputeWelfordVariance` | Low-level pixel buffer extraction, template matching, and online streaming variance accumulation. |
| `src/FreeVideoStudio.Core/Media/AiHudDetection.cs` | `IAiHudDetectionService`, `HudCandidate`, `AiHudCandidate`, `AiHudFrame`, `AiHudOutcome`, `AiHudDetectionResult`, `AiHudLabelMapper`, `AiHudGeometry` | `DetectAsync`, `ToSourcePixels`, `ToRoleKey`, `AIHUD_01` | Optional AI Magic Wand contract and the ONE normalized→source-pixel conversion (§12a). |
| `src/FreeVideoStudio.Core/Media/AiHudResponseParser.cs` | `AiHudResponseParser`, `AiHudParseResult` | `Parse`, `IsValid`, `MinSide`, `MinArea`, `MaxSide`, `MaxArea`, `MaxCandidates` | Strict validation of AI HUD answers; nothing unvalidated reaches pixel space (§12a). |
| `src/FreeVideoStudio.Core/Media/HudCandidateFusion.cs` | `HudCandidateFusion` | `Fuse`, `IoU`, `Containment`, `MatchIoU`, `RefineIoU`, `RefineConfidence`, `MinAiOnlyConfidence`, `AIHUD_02` | Deterministic local + AI candidate fusion (§12a). |
| `src/FreeVideoStudio.Core/Media/HudDetectionCoordinator.cs` | `HudDetectionCoordinator`, `HudDetectionRequest`, `HudDetectionResult` | `RunAsync`, `Cancel`, `IsCurrent`, `AIHUD_03`, `HudLocalPreview`, `WANDPROGRESSIVE_01` | Runs local + AI detection, fallback, cancellation, stale-result suppression and the early local preview (§12a). **⚠ CO-GOVERNED BY: 08**|
| `src/FreeVideoStudio.Core/Media/AiHudResultCache.cs` | `AiHudResultCache`, `AiHudCacheKey` | `TryGet`, `Store`, `AIHUD_04` | In-memory cache keyed by source fingerprint + frame key + model; success only, never credentials (§12a). |
| `src/FreeVideoStudio.App/Services/GeminiHudDetectionService.cs` | `GeminiHudDetectionService` | `DetectAsync`, `Redact`, `ResolveModel`, `ApiKeyHeader`, `Prompt` | Gemini provider for the Magic Wand: one frozen frame, header-only key, no retry (§12a). **⚠ CO-GOVERNED BY: 05, 08**|
| `src/FreeVideoStudio.App/Services/AiHudFrameBuilder.cs` | `AiHudFrameBuilder` | `FromSnapshotFile`, `EncodeJpeg`, `SourceFingerprint`, `MaxLongEdge` | Encodes the ONE frozen frame sent to the AI, with its opaque identity (§12a). |
| `src/FreeVideoStudio.App/CropToolWindow.MagicWand.cs` | `CropToolWindow` (Partial) | `RunMagicWandAsync`, `ConfirmWandCloudConsentAsync`, `ShowMagicWandCandidates`, `StepMagicWandPreview`, `ToCandidateSpecs` | Magic Wand UI: progress, privacy notice, labelled candidates, never commits (§12a). **⚠ CO-GOVERNED BY: 04, 08**|
| `src/FreeVideoStudio.Core/Media/AiTrajectorySmoother.cs` | `AiTrajectorySmoother`, `AiTrackingKeyframe`, `SmoothedTrajectory`, `RawSubjectWaypoint` | `SmoothTrajectory`, `AvoidHudZones`, `RawWaypoints`, `ToJson`, `FromJson`, `EvaluateExportCrop`, `ToFfmpegCropFilter` | Universal AI tracking trajectory interpolation, velocity-adaptive scale breathing, deadband jitter filtering, segment-range filter (AITIME_01), HUD zone avoidance (AIHUDZONE_01), stored raw waypoints for offline re-framing (AIRESMOOTH_01), boundary anchoring. **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.App/Services/GeminiTrackingService.cs` | `GeminiTrackingService`, `GeminiModelSession`, `DiscoveredSubject`, `AiDiscoveryResult`, `AiTrackingRequest` | `ExtractThreeCandidateFramesAsync`, `DiscoverSubjectsAsync`, `DiscoverSubjectsWithLookaheadAsync`, `PlanChunks`, `TrackSubjectAsync`, `ParseDiscovery`, `ParseWaypoints`, `BuildRequest`, `IsMainPlayerCharacter`, `IsInanimateScenery`, `CropSubjectJpeg`, `SanitizeWaypointsAgainstLocalPlayer` | Candidate extraction, lookahead probing (same-file limit), chunked dense tracking (AICHUNK_01) with fps metadata (AIFPS_01) and response schemas, profile-aware own-character exclusion (AIEXCLUDE_02), retired-model fallback (AIMODELFALLBACK_01). All transport via `GeminiClient`. **⚠ CO-GOVERNED BY: 03, 04, 05**|
| `src/FreeVideoStudio.App/Services/AiTrackingPrompts.cs` | `AiTrackingPrompts` | `Discovery`, `Tracking`, `ExclusionRule` | Profile-driven prompt text (AIGAME_01). |
| `src/FreeVideoStudio.Core/Media/AiGameProfile.cs` | `AiGameProfile`, `AiGameProfiles`, `AiNormalizedRect`, `AiHudAvoidZones` | `Parse`, `ExcludesThirdPersonAvatar`, `DefaultsFor`, `FromCropConfig` | Footage profile and HUD avoid zones (AIGAME_01, AIHUDZONE_01). |
| `src/FreeVideoStudio.App/GranularSpeedEditorWindow.AiZoom.cs` | `GranularSpeedEditorWindow` (Partial) | `OnAiSmartZoomClickedAsync`, `TryResolveAiSource`, `ReframeExistingAiZoomAsync`, `BeginAiPreview`, `EndAiPreviewLoop`, `CancelAiTracking` | Granular AI tracking dispatch (AIMERGE_01 source/clip resolution, AILOOKAHEAD_01 block extension, AIOWNBOX_01 own box, AIRESMOOTH_01 re-frame), status overlay, loop preview and undo-correct ACCEPT. **⚠ CO-GOVERNED BY: 04, 07**|
| `src/FreeVideoStudio.Core/Media/CanvasMath.cs` | `CanvasMath` | `FinalWidth`, `FinalHeight`, `ContentWidth`, `ContentHeight`, `BackendWidth`, `BackendHeight`, `ProtectCropDrift` | Canvas sizes (re-exported from `CoordinateConstants`) and HUD crop-drift rounding. |
| `src/FreeVideoStudio.App/MainWindow.axaml.cs` | `MainWindow` | `SourceMsToOutputSeconds`, `UpdateTimelineMarkers`, `UpdateThumbnailButtonState`, `TogglePlayPauseTransport`, `MainTimelineEndSeconds`, `IsMainPreviewAtEnd`, `_lastFreezeTriggerMs`, `_mainEndParkIssued` | Master timeline UI coordination, playhead tracking, transport, freeze trigger and double-counting avoidance. **⚠ CO-GOVERNED BY: 02, 04, GOV**|
| `src/FreeVideoStudio.App/MainWindow.Canvas.cs` | `MainWindow` (Partial) | `AttachThumbnailCameraMarkerInteractions`, `EndThumbnailMarkerDrag`, `SeekMainPreviewToMarkerMs`, `TimelineCameraStickName` | Primary timeline canvas drawing, hitbox evaluation, marker drag lifecycle, and caret positioning. |
| `src/FreeVideoStudio.App/MainWindow.Wireup.cs` | `MainWindow` (Partial) | `WireComponents`, `markerDragActive`, `MainWindow` | Control wiring for the main timeline: transport, marker drags and the thumbnail button's click handler (THUMB_01). |
| `src/FreeVideoStudio.App/MainWindow.Controls.cs` | `MainWindow` (Partial) | `SetThumbnailButtonCtl`, `PlayPauseButtonCtl` | Cached control accessors (MVVM_03) for the thumbnail and transport buttons. **⚠ CO-GOVERNED BY: 02, 04, GOV**|
| `src/FreeVideoStudio.App/MainWindow.Shortcuts.cs` | `MainWindow` (Partial) | `ExecuteMarkStart`, `ExecuteMarkEnd`, `MainWindow` | Keyboard transport and marker shortcuts. Must share the transport with the on-screen controls. |
| `src/FreeVideoStudio.App/GranularSpeedEditorWindow.axaml.cs` | `GranularSpeedEditorWindow`, `SegDragMode` | `ZoomCanvas_PointerMoved`, `ZoomCanvas_PointerReleased`, `RelayoutFrameLane`, `SelectSegment`, `ClampZoomInsideItsBlock`, `EndUndoGesture` | Granular speed timeline editing, rubberband sweeps, freeze & zoom manipulation. **⚠ CO-GOVERNED BY: 04, 05**|
| `src/FreeVideoStudio.App/Controls/PhoneFrameMockup.axaml.cs` | `PhoneFrameMockup` | `PhoneFrameMockup` | High-fidelity 9:16 phone mockup preview with 600px semi-transparent flanks. **⚠ CO-GOVERNED BY: 04**|
| `src/FreeVideoStudio.App/Controls/TimelineKnob.cs` | `TimelineKnob` | `Attach` | High-precision timeline knob physics and collision boundaries. |
| `src/FreeVideoStudio.App/Controls/KineticScrubController.cs` | `KineticScrubController` | `OnTick`, `Release`, `Cancel`, `IsFlinging`, `SeekRequested`, `FlingSettled` | Kinetic inertia scrubbing across timeline and preview views. |
| `src/FreeVideoStudio.App/Controls/TimelineLanesControl.axaml.cs` | `TimelineLanesControl` | `MaxZoomFactor`, `Refresh`, `FormatClock`, `MarkerHeadroomPx` | Multi-lane timeline rendering for speed, memes, and cuts. |
| `src/FreeVideoStudio.App/VoiceOverWindow.axaml.cs` | `VoiceOverWindow` | `_timeline`, `ApplyMasterVolume`, `Dispose`, `IsDuckAudio` | Audio take timeline alignment excluding memes to prevent double-counting. **⚠ CO-GOVERNED BY: 02**|
| `src/FreeVideoStudio.App/VoiceOverWindow.RecordingState.cs` | `VoiceOverWindow` (partial) | `SourceEndForCapturedAudio`, `BuildStudioTimeline`, `UpdateLiveRecordingVisuals`, `VOREC_02`, `VOREC_03` | Voice-over take extent on the source axis, mapped through the studio's meme-blind OutputTimeline. **⚠ CO-GOVERNED BY: 02, 04**|
| `src/FreeVideoStudio.App/MusicWizardWindow.axaml.cs` | `MusicWizardWindow` | `OutputTimeline`, `TimelineStartSeconds`, `Name`, `FilePath` | Phase 3 music timeline synchronization and end-fit calculations. **⚠ CO-GOVERNED BY: 02**|
| `src/FreeVideoStudio.App/ViewModels/TimelineViewModel.cs` | `TimelineViewModel` | `SourceMsToOutputSeconds`, `PreviewSourceToOutputSeconds` | Main App source-ms to output-seconds mapping for export and live preview; meme-blind by design (§2 Double-Counting Guard). |
| `src/FreeVideoStudio.Core/Media/MergeEdl.cs` | `MergeEdl`, `EdlClip`, `EdlEffects`, `EdlAnchor`, `EdlZoom`, `EdlMeme` | `FromJson`, `ToJson`, `MergeEdlJsonContext` | The Video Merger's edit list in source µs (§10 MERGEEDL_01, EDLNULL_01). **⚠ CO-GOVERNED BY: 06**|
| `src/FreeVideoStudio.Core/Media/CompositeTimeline.cs` | `CompositeTimeline`, `CompositeClip` | `Build`, `KeepWindow`, `IntroCutUs`, `UsToFrames`, `FramesToUs`, `MergedSecToBodyOutputSec`, `SyntheticIntroSec` | Three clocks, one mapper: source µs, merged frames, output seconds (§10 COMPOSITE_01). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/MergedTimeline.cs` | `MergedTimeline`, `MergedClip`, `MergeClipSource` | `Build`, `Composite`, `ToMerged`, `MergedLengthSec`, `MergedEndSec` | Seconds adapter over `CompositeTimeline` (§10 P2.4). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/MergeEditorBridge.cs` | `MergeEditorSource`, `MergeEditorClip`, `MergeEditorState` | `Build`, `MpvUrl`, `ToEditor`, `FromEditor`, `ExtraFreezes`, `PlacementAt` | One granular editor over the whole merge (§10 MERGEEDIT_01). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/MergerPreviewPlan.cs` | `MergerPreviewPlan`, `PreviewStep`, `PreviewStepKind` | `Build`, `SpeedAt`, `OutputSecAt`, `MemePlacements` | Merger preview parity with the export (§10 MERGEPREVIEW_01). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Media/MusicPadAlignment.cs` | `MusicPadAlignment` | `Align` | Shifts music tracks by the fade-in pad so the export matches the preview (MUSICPAD_01). **⚠ CO-GOVERNED BY: 02, 03**|
| `src/FreeVideoStudio.App/VideoMergerWindow.EdlPreview.cs` | `VideoMergerWindow` (Partial) | `EnsureEdlLoaded`, `LoadEdlAsync`, `EnsurePreviewPlan`, `TickPreviewEffects`, `TickMergerMemes`, `PreviewOutputSec` | The Merger preview as ONE EDL (§10 MERGEPREVIEW_EDL_01). **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.App/VideoMergerWindow.Timeline.cs` | `VideoMergerWindow` (Partial) | `SeekMerged`, `TickMergedPlayback`, `DrawMergedTimeline`, `PreviewDurationSec`, `PreviewPositionSec` | Merged-clock playback, seeking and timeline drawing. **⚠ CO-GOVERNED BY: 03**|
| `src/FreeVideoStudio.Core/Editing/GranularEditSession.Segments.cs` | `GranularEditSession` (Partial), `EditOutcome` | `MarkEnd`, `AddPendingSegment`, `NeighbourBounds`, `ClampZoomInsideItsBlock`, `ClampZoomEdgeAgainstSlowNeighbours`, `SlowZoomHasRoom`, `DeleteRange`, `ApplyCutToSegmentsAndFreeze`, `SurvivingMsAfterCuts` | The editor's block/zoom/freeze/cut rules as logical commands; every time mapping delegates to `OutputTimeline` (EDITSTATE_01). **⚠ CO-GOVERNED BY: 04, 07**|
| `src/FreeVideoStudio.Core/Editing/GranularEditSession.Memes.cs` | `GranularEditSession` (Partial) | `ResolveNewMemeAnchor`, `AddMeme`, `UpdateMeme`, `MoveMemeTo`, `SnapMemeToNearestLegalEdge`, `TimelineExcludingMeme`, `MemeMinSeparationOutSec` | Meme placement in BOTH presentation modes (inline = output insertion, corner = zero output seconds), never collapsed (MEMEMODE_01, EDITSTATE_01). **⚠ CO-GOVERNED BY: 04, 07**|
| `src/FreeVideoStudio.App/GranularSpeedEditorWindow.Merge.cs` | `GranularSpeedEditorWindow` (Partial) | `InitializeMergeMode`, `DrawMergeDividers`, `BuildMergeFrameLaneAsync` | The granular editor in merge mode: clip dividers and the merged frame lane. **⚠ CO-GOVERNED BY: 04**|

---

## 1. 16:9 to 9:16 Portrait Canvas Trick & FOV Geometry  {#TL-PORTRAIT}
* **Source Resolution Mandate:** Native source footage must be 16:9 landscape (1920 x 1080, 2560 x 1440, or 3840 x 2160).
* **Surviving Center Slice:** The active gameplay slice is strictly 720 source pixels wide:
  $$\text{SurvivingSourceWidth} = \frac{1280 \text{ (internal portrait width)}}{1.7777\dots \text{ (16:9 aspect ratio)}} = 720 \text{ source px}$$
  `CoordinateMath.cs` is the absolute mathematical authority. The dimmer must never be locked to 1280 source pixels.
* **Canvas Voids & Airspace Priority:** Scaling to 1280px width inside a 1080p width constraint alters the aspect ratio, creating exactly 150px black voids at the top and bottom of the final 1080 x 1920 canvas:
  $$\text{PadTop} = 150\text{px}, \quad \text{PadBottom} = 150\text{px}, \quad \text{ContentHeight} = 1620\text{px}$$
  * The top 150px void is reserved for the mobile status bar (clock at 9:41, battery, 5G, Wi-Fi arches) and SkiaSharp title text overlay.
  * The bottom 150px void houses mobile navigation controls.
  * Neither void element may ever overlap the centered 720px gameplay area.
* **Dimmer Mask:** `PhoneFrameMockup.axaml` dims inactive flanks via two 600px semi-transparent (`#99000000`) blocks on a 1920px canvas, leaving the 720px center clear:
  $$\text{FlankWidth} = \frac{1920 - 720}{2} = 600\text{px}$$
  Side dimming dynamically stretches on window resize to prevent distortion of the phone frame.
* **HUD Overlay Math Parity:** Live preview and FFmpeg export derive HUD dimensions from an identical basis:
  $$\text{ItemSize} = \text{contentH} \times \text{scale} \times \text{backendScale}$$
  Quantized via `CropEditSession.QuantizeLayerSize` (the Crop Tool's edit state, EDITSTATE_01; preview) and `MobileFilterBuilder.Build` (export). Heavy SkiaSharp image decodes and crop operations run off the UI thread.
  `RATIOLOCK_01`: backend HUD sizes use even pixels with a 2px minimum. Width follows the requested scale; height follows the source aspect ratio within 1 backend pixel. Preview rounds the backend dimensions to the nearest content pixel. Overlay positions are clamped and rounded to individual content pixels, never forced onto a 27px grid.
* **No Mask Profile:** Read-only profile exporting 1080 x 1920 with zero HUD overlays. Injects explicit zero-size rectangles for all 5 HUD keys to prevent default restoration by the sanitizer. In-game overlay controls are hidden and forced off. Portrait source footage is cover-scaled and center-cropped to the 2:3 content area.

---

## 2. Authoritative OutputTimeline Model  {#TL-OUTPUTTIMELINE}
`src/FreeVideoStudio.Core/Media/OutputTimeline.cs` is the mathematical authority mapping source time to finished output time for single-clip linear editing and per-clip segment evaluations across preview playback and FFmpeg rendering. For multi-clip NLE virtual timelines, `CompositeTimeline.cs` and `MergeEdl.cs` govern the composite multi-clip mapping across `edl://` libmpv preview and FFmpeg concat filtergraphs (see §10 `TL-COMPOSITE`).

### Chunk Classification
$$\text{TotalOutputSeconds} = \sum_{c \in \text{Chunks}} \text{Duration}(c)$$

1. **`Normal`:** Consumes source time; consumes output time scaled by speed factor S:
   $$\Delta t_{\text{out}} = \frac{t_{\text{end}} - t_{\text{start}}}{S}$$
2. **`Freeze`:** Consumes 0 source time; occupies output time equal to hold duration D_freeze (0.2s <= D_freeze <= 10.0s). Synthesizes a `Speed = 0` segment on export.
3. **`Cut` (Delete Parts):** Consumes source time; occupies 0 output time:
   $$\Delta t_{\text{out}} = 0$$
4. **`Insertion` (Meme Cutaway):** Consumes 0 source time; occupies output time equal to meme duration:
   $$\Delta t_{\text{out}} = D_{\text{meme}}$$
   Only FULL SCREEN memes are insertions (MEMEMODE_01). A CORNER OVERLAY meme is never an `Insertion`:
   $$\Delta t_{\text{out}}(\text{corner}) = 0$$
   `MemePlacement.ToInsertions` drops it, so `OutputTimeline`, `ProjectDocument.BuildTimeline`, `CompositeTimeline`, the editors and every size estimate agree by construction.

### Freeze Insertion Invariant
A freeze is an insertion, never a replacement. Freezing at t_anchor for duration D holds the exact video frame at t_anchor for D seconds, and resumes playback from t_anchor:
$$\text{TotalOutputSeconds}' = \text{TotalOutputSeconds} + D_{\text{freeze}}$$

### Caret Crossing Holds
Output-to-source mapping inside a hold is many-to-one:
$$\forall t_{\text{out}} \in [t_{\text{freeze\_start}}, t_{\text{freeze\_end}}]: \quad \text{OutputToSourceRelative}(t_{\text{out}}) = t_{\text{anchor}}$$
Playhead position is supplied directly and advances continuously in real time across the frozen span. Clicking inside a frozen span centers the playhead at the clicked coordinate, not the edge.

### Double-Counting Guard
Memes are anchored to clip-relative source seconds (`AtSourceSecRelative`). The Main App's mappings `TimelineViewModel.SourceMsToOutputSeconds` (export) and `TimelineViewModel.PreviewSourceToOutputSeconds` (live preview) — `MainWindow.SourceMsToOutputSeconds` only delegates to the former — and the Voice Over studio's `VoiceOverWindow._timeline` deliberately omit memes from their `OutputTimeline` instances because independent offsets are applied during export (`MemeTimeInsertedBefore`). Memes must never be passed into `OutputTimeline` at these call sites.

### Voice-Over Take Extent (VOREC_02)
`VOSAMPLE_01`: the saved PCM budget uses the prepared body's total output duration and the immutable actual output start established immediately before capture. `maximumFrames = floor(max(0, outputEnd - actualOutputStart) * sampleRate)`. Opening buffers consume this budget; UI callback delays cannot move the anchor. Freeze offsets are part of that start; memes and intros are not recordable body time.
A voice-over WAV runs in OUTPUT time. **VOCLOCK_01:** `VoiceOverPlaybackPlan` and `VoiceOverPreviewRenderer` prepare the picture with the export's `GranularSpeedBuilder`, including speeds, cuts and freezes, then mpv plays that picture at verified 1x. The 50 ms UI timer does not implement recording-picture timing. The studio axis remains SOURCE time.

For a take with captured duration $D$, source anchor $t_s$, and output offset $o$:
$$t_e = t_{\text{trim}} + \text{OutputToSourceRelative}\big(\text{SourceToOutput}(t_s) + o + D\big), \qquad t_e \ge t_s$$
The inverse is lossy during a freeze. A take starting inside the hold stores its exact output offset (normally negative relative to `SourceToOutput(anchor)`, which returns the end of the hold) and actual WAV duration. Never reconstruct its duration by subtracting mapped source endpoints. Apply trims WAV seconds, preserves the offset, and stores a timing signature for the spoken source span. `SeekOutputAt` seeks to the beginning of a freeze; ordinary inverse mapping retains the hold's anchor. Tests: `VoiceOverTimingTests`, `VoiceOverTimelineTests`, `VoiceOverPreparedClockTests`, `VoiceOverExportTimingTests`.

---

## 3. Marker & Speed Segment Physics  {#TL-MARKERS}
* **Marker Collision:**
  $$0 \le t_{\text{start}} \le t_{\text{end}} - \Delta t_{\text{min}} \quad (\Delta t_{\text{min}} = 1\text{ ms or } 1\text{ frame})$$
  Markers act as impenetrable hard limits.
* **Event Dispatching:** Dragging markers updates state on `PointerReleased` only. Pointer drag continuously drives throttled seeks without modifying play/pause state. Button presses while paused seek to mark; button presses while playing mark without pausing. Clicking a Granular Speed Segment selects it without moving the playhead.
* **Speed Segment Barrier:** Neighboring speed blocks may touch but never overlap (`SegGapMs = 0`):
  $$t_{\text{start}, i+1} - t_{\text{end}, i} \ge 0\text{ms}$$
  **SEAM_01:** two edges within `SeamEpsilonMs = 1` ms are one seam. A grab on a seam takes the edge on the pointer's side (left of it = the earlier block's END, right of it = the later block's START), and the canvas hit test and the edge sticks apply the same rule. Segments slide freely across the Freeze Camera marker. Minimum block duration is 200ms.
* **Proportional Thirds Hitbox:** A block's grab zone for each edge is:
  $$\text{GrabZone} = \min\left(8\text{px}, \frac{\text{BlockWidth}}{3}\right)$$
  Nearest edge wins when adjacent.
* **Trim-Marker Clones:** Active speed blocks render SeaGreen vertical start/end edge sticks (24px hitbox, 3px stroke). Dragging routes to the identical block-edge resize pipeline, so it obeys the same neighbor barrier (blocks may touch) and 200ms minimum width. Swept rubber-band blocks arrive pre-selected.

---

## 4. Frozen Segment Object  {#TL-FREEZE}
* **Duration Bounds:**
  $$0.2\text{s} \le t_{\text{freeze}} \le 10.0\text{s}$$
  Preset buttons offer fixed lengths: 0.5s, 1.0s, 1.5s, 2.0s, 2.5s, 3.0s.
* **Visual Representation:** Thumbnail lane displays the held frame with a cool blue wash, diagonal hatching, solid boundary posts, and a centered `❄ FROZEN X.Xs` label (dropped if block width < 60px).
* **Interaction:** Double yellow camera popsicles extend to the bottom timeline lane. Dragging popsicles resizes from that edge; dragging body moves anchor time; Arrow keys trim (END focused) or slide (body focused) by 1 frame.
* **Layer Priority:** Frozen band sits on top of speed blocks and intercepts clicks first. Dragging left tracks leading edge; dragging right tracks trailing edge.
* **One Hold Per Pass (FREEZE_01) — NON-NEGOTIABLE:** The main screen's playback tick triggers the hold from a **150 ms window** around the anchor:
  $$\text{Trigger} \iff t_{\text{playhead}} \in [t_{\text{anchor}},\; t_{\text{anchor}} + 150\text{ms}]$$
  The hold PAUSES the player, so the clock does not advance while it runs, and on release playback resumes for exactly one tick — **one frame, 16–33 ms** — before the test runs again. Without a record of having already fired, that lands back inside the same 150 ms window and re-freezes. The result is a player that advances a single frame and pauses itself, five to nine times over, with no way out.
  `MainWindow._lastFreezeTriggerMs` records WHICH anchor has been consumed (the anchor time, not a bool), so moving the freeze re-arms it for free. Re-arming is otherwise **deliberate only**:
  * the playhead leaves the window with real clearance (`< t_anchor - 250ms` or `> t_anchor + 400ms`), so a frame of jitter cannot re-arm it; or
  * an explicit seek (`SeekInternal`) — a scrub is a new pass over the timeline.

  ⚠️ Resuming playback is NOT a re-arm. That is the loop. `VoiceOverWindow` has carried this guard since VOFIX_01; the main screen simply never had it.

---

## 5. DELETE PARTS (Cut Normalization)  {#TL-CUTS}
* **Merge & Discard Rules:**
  * Adjacent cuts closer than 0.30s merge into one:
    $$t_{\text{start}, i+1} - t_{\text{end}, i} < 0.30\text{s} \implies \text{Merge}$$
  * Overlapping cuts merge.
  * Cuts < 0.04s are discarded.
* **Safety Floor:** At least 0.5s of clip footage must survive:
  $$\text{SurvivingSourceSeconds} \ge 0.5\text{s}$$
* **Segment Reconciliation:** Cuts clear fully contained speed blocks, trim partial overlaps, drop remnants < 200ms, and clear freezes whose anchor was cut. Spanning blocks are preserved.
* **Playback Skip:** Playback loops across Main, Speed Editor, VoiceOver, and Music Wizard skip cut spans in a single seek on tick.
* **Audio Crossfade:** FFmpeg export injects an 8ms audio edge crossfade at each cut boundary to eliminate jump-cut pops.

---

## 6. Live Zoom-In Geometry & Workflow  {#TL-ZOOM}
* **Data Model:** `SpeedSegment` carries `ZoomX, ZoomY, ZoomW, ZoomH, ZoomOrigRes, ZoomSlow, ZoomStartMs, ZoomEndMs`.
* **Bounds & Clamping:** Floor constraint of at most 8x upscale (`MaxZoomUpscale = 8`): the box is never narrower than 1/8 of the width the viewer finally sees, in source pixels — the full source width in landscape, the surviving slice (`InternalW / scale`, 720px on 1080p) in portrait. A smaller box is snapped up on commit. Clamped to 16:9 (landscape) or 2:3 (portrait mapping directly to surviving 720px width via 2.0/3.0 ratio):
  $$\text{Aspect} = \begin{cases} 16/9, & \text{Landscape} \\ 2/3, & \text{Portrait} \end{cases}$$
* **Color Token Mandate:** Zoom box, handles, and timeline markers use `AppZoomBrush` exclusively.
* **Interactive Gestures (ZOOMLIVE):** Non-destructive toggle; faint suggestion on click; commits on first drag/resize. Selecting a segment pauses playback on its first frame and mounts the box. Magnifiers own clicks while box is open; block edges stand down. Resizing block pulls zoom span within bounds.
* **Slow Zoom Ramp Constraints:**
  * The 0.5s ramp constants (`ZoomRampSeconds = 0.5`) apply to SOURCE footage time, interpolating pre-roll and post-roll outside `ZoomStartMs`/`ZoomEndMs`. Output duration scales inversely with segment speed and can differ with cuts or freezes; there is no fixed half-second of final playback.
  * Per-side ramp evaluation: Each side (incoming and outgoing) is evaluated independently by the renderer. When one side lacks room (less than 0.5s free source footage to clip boundary/instant zoom, or less than 1.0s between adjacent slow zooms), only that facing ramp is omitted (snaps instantly on that side); the opposite ramp is preserved if room exists. The whole zoom does not become instant merely because one side lacks room.
  * Adjacent slow zooms require a 1.0s gap (`ZoomRampRequiredGapBetweenSlowZooms = 1.0s`); if < 1.0s, neither gets a ramp in that gap and both snap on the facing side.
  * Clamping vs whole-block movement: Edge dragging clamps against slow neighbours (`ClampZoomEdgeAgainstSlowNeighbours`) and switching Instant to Slow refuses if gap < 1.0s (`SlowZoomHasRoom`). Whole-block movement and clip boundary placements do not universally clamp; when placed where a glide cannot fit, that side's ramp is omitted and UI availability guides indicate the missing glide.
* **Preview Parity:** every preview surface takes the window from `ZoomPreviewSimulator.Compute`, which reads these ramp rules from `GranularSpeedBuilder` and evaluates an AI trajectory with the export's own expression (03 FFM-ZOOMGRAPH, AIPARITY_01). Where the export pads black past the frame edge the preview must clamp; that is reported, not hidden (`EdgeClamped`, 04 UI-PREVIEWFIDELITY).

---

## 7. Meme Placement & Scrubbing Physics  {#TL-MEME}
* **Two Presentation Modes (MEMEMODE_01) — NON-NEGOTIABLE:**
  * `InlineFullScreen` (default, and every meme saved before MEMEMODE_01): interrupts the gameplay; adds `DurationSec` to the output. All rules below apply.
  * `CornerOverlay`: plays OVER the running gameplay in one of four corners (`MemeOverlayCorner`, default BottomRight), Small / Medium / Large (`MemeOverlaySize`, default Medium), with optional sound (`PlaySound`). It adds ZERO output seconds and owns only a VISIBILITY INTERVAL on the GAMEPLAY clock (output seconds with full-screen memes not counted):
    $$[\,t_s,\ t_e\,) = [\,\text{SourceToOutput}_{\text{gameplay}}(t_{\text{anchor}}),\ \min(t_s + D_{\text{meme}},\ T_{\text{gameplay}})\,)$$
    (`MemePlacement.VisibleInterval`; clipped, never lengthening). Music, voice-over, cuts and other memes after it do not move. A full-screen cutaway inside the interval pauses it with the gameplay (the export overlays before splicing). A FREEZE does not pause it: the freeze is held frames of the rendered stream the overlay runs on, so the Main App preview drives it with the freeze-aware gameplay clock (CORNERPARITY_01).
  * Corner overlays are NOT snapped by D7/D8 (only pushed out of deleted footage), may overlap anything, and in the Merger are always `EdlMemePlacement.Mid` at their exact source µs. Switching a meme FULL SCREEN → CORNER removes its length; CORNER → FULL SCREEN adds it back and re-applies D7/D8 at the anchor.
* **Placement:** Anchored to clip-relative source seconds (`AtSourceSecRelative`). Placed on finished timeline in Speed Editor. `MemePlacement.Id` is a generated alphanumeric identifier, never derived from filename.
* **Snapping:** `SnapInsertionPoint` pushes placement forward past cuts, freezes, or speed blocks. Memes cannot interrupt speed blocks or share timestamps. Cuts delete contained memes.
* **Marker Count Is Ruler-Dependent (MEME_06) — NON-NEGOTIABLE:**
  * **Granular Speed Editor (OUTPUT-time ruler):** a meme occupies D_meme output seconds, so it draws as a BAND with TWO clown popsicles, one at each boundary. Neither head is individually draggable; only the whole block moves, from its middle (a meme's length is the file's own length, so a resize grip would advertise a control that cannot act).
  * **Main App (SOURCE-time ruler):** a meme occupies ZERO source seconds — its start and end are the SAME instant. It MUST render as exactly **ONE display-only clown head**, placed at the gameplay moment it interrupts. Two heads would land on the identical pixel with no band between them, which confuses the user and corrupts marker hit testing. Moving and removing happen only in the Speed Editor.
* **Hitbox:** Rendered band has an invisible 18px minimum click floor (`MemeGrabMinWidthPx = 18`).
* **Scrubbing Physics:** Dragging sets caret to output start:
  $$t_{\text{caret}} = \text{SourceToOutput}(t_{\text{anchor}}) - D_{\text{meme}}$$
  Preview updates to source anchor. Canvas retains pointer capture. Ruler pivot updates live during drag. `BaseTimeline` cache is not cleared on move. Caret stays sticky on release until playback resumes.

---

## 8. End Of Timeline: Stop, Then Restart  {#TL-ENDSTOP}
The last frame is a **stop**, and the next transport press is a **restart**. Every preview surface obeys this identically — the main screen (`MAINEND_01`), the Voice Over studio (`VOEND_01`).

* **Park once, not once per tick.** A playback tick that re-issues `pause=yes` on every pass while the player sits at the end will land on top of a PLAY the user has just requested. The stop is a one-shot, re-armed by any seek or any play.
* **PLAY at the end means MARK START, never "resume".** Unpausing a player with nothing left to play advances one frame and stops again. Parked at the end:
  $$t_{\text{playhead}} \ge t_{\text{end}} - 0.05\text{s}, \quad t_{\text{end}} = \begin{cases} t_{\text{MARK END}}, & \text{trimmed} \\ \text{Duration}, & \text{otherwise} \end{cases}$$
  the next PLAY seeks to MARK START first. RECORD in the Voice Over studio does the same, otherwise the trim-end stop kills the take on the tick after it arms and every take comes out empty.
* **One transport, all controls.** The on-screen button and the keyboard shortcut route through a single method (`MainWindow.TogglePlayPauseTransport`). A second bare `SetPropertyAsync("pause", "no")` on any play path reintroduces the trap on whichever control skipped it.
* **One transport is not enough — it must also be reached ONCE per press.** A toggle wired to both `Command` and `Click` fires twice and cancels itself, which looks exactly like a playback fault and is not one: `04_UI_UX_AVALONIA_SPEC.md` §4 (UI-SAFEGUARDS, DOUBLEFIRE_01). Before suspecting the player, check whether an unconditional transport on the same screen (MARK START) still works. If it does, the fault is the toggle's wiring, not the clock.
* **Clamping is not rewinding.** `NormalizePreviewPlaybackPosition` clamps to just inside the end (`end - 0.05`) — still inside the stop window. Clamping constrains a position; it does not decide where playback should begin. The restart is the caller's job.

---

## 9. Hitbox Priorities & Time Badges  {#TL-HITBOX}
* **Z-Index Hierarchy (Highest to Lowest):**
  1. Music Note Symbols
  2. Yellow Camera Overlays
  3. Slider Playhead Caret
  4. MARK START / MARK END markers
* **Exclusive Focus:** Exactly one object holds focus with crawling marching ants. Released via `Esc`, right-click down, or selecting another object. `Esc` is consumed only if an element holds focus. Right-click release is detected on pointer-down.
* **Audio Marker Precision:** Vertical lines from music note icons drop to exact millisecond coordinates with sub-pixel rendering, bypassing video frame snapping.
* **Dynamic Time Badge:** Attached to caret playhead; 1-pixel movement resets 1.0s timer and restores 100% opacity; fades to 0% after 1.0s idle.
* **Thumbnail Selection:** Defaults to 66% mark:
  $$t_{\text{thumb\_default}} = t_{\text{start}} + \frac{2}{3}(t_{\text{end}} - t_{\text{start}})$$
  Manual override locks position. Button cycles `SET THUMBNAIL` / `MOVE THUMBNAIL HERE` / `REMOVE THUMBNAIL` based on playhead position. `Shift + Left/Right` steps by exact frames via mpv `container-fps`.
* **Marker Drag Lifecycle (THUMB_02):** Every draggable timeline marker must end its drag on **three** events, not one:
  1. `PointerReleased` — the ordinary exit;
  2. `PointerMoved` with the left button no longer held — the button was released somewhere the control never saw;
  3. `PointerCaptureLost` — **the only event guaranteed to arrive** when the captured control is removed from the tree, the window loses focus, or the pointer is stolen.

  `UpdateTimelineMarkers` clears and rebuilds the marker canvas from a **posted** callback, so a rebuild can land between a press and its release and destroy the control holding capture. With only (1), the drag flag stays raised for the rest of the session; the rebuilt marker's move handler then runs on a bare hover and re-seeks and re-pauses the player on every mouse movement.
  A marker being dragged must also appear in `markerDragActive`, which is what suppresses the canvas rebuild for the duration of a gesture. A marker missing from that set is the bug above waiting to happen.

## 10. Video Merger: EDL and Composite Timeline  {#TL-COMPOSITE}
Owner of the Video Merger's time maths (Video-Merger-Migration.md P2). The Main App's clocks (§1–§9) are unchanged.
* **EDLNULL_01 — old files must load safely.** The source-generated JSON reader assigns default(T) to every `init` property a file does not mention (initializers do not run). So (1) every list/object property of the EDL records is null-proof (a null init keeps the empty default; record equality compares non-null fields), and (2) `MergeEdl.FromJson` first completes every object with the missing keys of a freshly constructed default (`ScraperEnabled`, `BaseSpeed`, volumes, a fresh `ClipId`). Found in the field: an autosave written before `Cuts` existed made every capture throw, the Merger's autosave froze on an old queue, and a later remove crashed the app.
* **MERGEEDL_01 — the merge is data.** `MergeEdl` (clips, order, user windows, effects, thumbnail, music, base speed) is the only state. Time unit inside a clip is **source µs** (`long`), anchored as `EdlAnchor(ClipId, SourceUs)`; `ClipId` is a GUID given on queueing. Records holding lists implement structural equality by hand (UNDOEQ_01). JSON is source-generated (`MergeEdlJsonContext`).
* **COMPOSITE_01 — three clocks, one mapper.** `CompositeTimeline.Build(edl)`:
  1. **Source µs** per clip (real frame pts, FRAMESNAP_01; `EdlClip.IntroCutUs`).
  2. **Merged frame** — integer frames at 60 fps, kept content end to end at 1.0x. Each clip's frame count is `round(keptUs × 60 / 10^6)` on its own (what the export's per-clip `fps=60` emits), so any number of clips adds up with zero drift.
  3. **Output sec** — per clip the Main App's `OutputTimeline` (speed segments, freezes as speed-0 segments, memes as insertions, cuts as holes — D18) over a clip length of `frames / 60`, with the global `BaseSpeed` applied per clip to footage OUTSIDE speed segments (D19: segment speeds are absolute, freezes/memes are real-time holds — the Main App editor's rule); clips summed; then `+ SyntheticIntroSec` (0.1 s when a custom thumbnail is set).
* **Intro removal is derived, never stored:** clip 1 only when a custom thumbnail is set; clips 2..N while the scraper is on; refused when < 50 ms would remain. A position inside a removed intro maps to the first kept frame.
* **Meme placement (D4):** `AtStart` → before the clip (its fade-in follows); `Mid` → at `AtUs`; `AtEnd` → where the fade-out begins when the kept window reaches the file end and the tag's `fadeout` is known, else at the clip end.
* **Remap:** through `(ClipId, SourceUs)`, so positions survive scraper toggles, a new thumbnail and reordering; `null` (stale) when the clip was removed.
* **`MergedTimeline` is a seconds adapter (P2.4).** It builds a `MergeEdl` from its `MergeClipSource` list (clip ids derived from the queue index), reads windows, intro removal and integer frame boundaries from `CompositeTimeline` (`Composite` property), and keeps only the continuous in-clip playhead maths. `MergedClip.MergedLengthSec` = frames / 60; `MergedEndSec` uses it; `ToMerged` never leaves the clip's merged slice. Delete the adapter once all merger callers read `Composite`.
* **MERGEEDIT_01 — one granular editor over the whole merge (D16).** `MergeEditorSource.Build(composite)` (in `MergeEditorBridge.cs`) turns the merge into ONE editor source: `MpvUrl` is an inline `edl://` of every clip's kept window (length-prefixed paths `%bytes%path`, start = keep-in, length = `MergedFrames / 60`, so mpv's time-pos IS the merged clock — P4.1 PASS), plus per-clip merged-ms spans and fade lengths. `ToEditor(edl)` maps every clip's effects into merged ms (the v1 editor shows ONE freeze; the others are counted in `ExtraFreezes` and kept). `FromEditor(state, edl)` puts results back: segments/cuts crossing a boundary are SPLIT per clip (identical output), a freeze or meme belongs to the clip under it (a boundary belongs to the clip that starts there), meme placement by position (D17: within the clip's fade-in or first frame = AtStart; within its fade-out or last frame = AtEnd, stored at the fade-out start; else Mid). Zoom stays in the clip's SOURCE px with its size (`EdlZoom.SourceW/H`). `ToEditor` must run before `FromEditor` on the same source.
* **MERGEPREVIEW_EDL_01 — the Merger preview is ONE EDL (P4.2).** Once the queue is analysed, `VideoMergerWindow.EdlPreview.cs` loads `MergeEditorSource.Build(_timeline.Composite).MpvUrl` (mpv time-pos = merged seconds, `hr-seek=yes`). The URL is compared every tick; a new layout reloads it at the same (file, source second). Before analysis the per-file preview is used.
* **MERGEPREVIEW_01 — preview parity (P8.1).** `MergerPreviewPlan.Build(composite)` reads the export's `OutputTimeline.Chunks` of every clip into merged-time steps (Play at absolute speed / Cut / Freeze / Meme). The tick sets mpv `speed` = `SpeedAt` (re-asserted after any EDL change), seeks past cuts, and pauses for each hold once per pass (seek re-arms: holds before the target count as done, one AT the target plays). `OutputSecAt(merged)` = `CompositeTimeline.MergedSecToBodyOutputSec` (T8.1a) and drives the music preview, which advances through holds and never changes speed. Pressing the transport during a hold pauses. Memes (P8.2): `MergerPreviewPlan.MemePlacements` (merged clock, origin 0) drive the Main App's `MemePreviewDirector` with the EDL URL as the gameplay source (MEME_07: park, play the meme file behind the MemeSwapOverlay veil, reload the same EDL at the anchor); the Merger tick returns early while it is active and seeks are ignored; the music stops during the meme and resumes at the output moment after it.

---

## 11. Universal AI Smart Tracking Trajectory & Dynamic Camera Math  {#TL-AITRACKING}
Governs automated camera tracking zooms produced by AI Vision Providers (Google Gemini, Anthropic Claude, OpenAI via `GeminiTrackingService.cs`, `AiTrackingPrompts.cs`, `AiGameProfile.cs`) and smoothed via `AiTrajectorySmoother.cs`.
* **Coordinate Invariant (Normalized 0-1000 Grid to Source Canvas Pixels):**
  All vision providers yield waypoints in integer coordinates normalized to $[0, 1000]$ where $(0, 0)$ is top-left and $(1000, 1000)$ is bottom-right:
  $$C_{x,\text{px}} = \frac{C_x}{1000.0} \times W_{\text{src}}, \qquad C_{y,\text{px}} = \frac{C_y}{1000.0} \times H_{\text{src}}$$
  $$W_{\text{target,px}} = \frac{W}{1000.0} \times W_{\text{src}}, \qquad H_{\text{target,px}} = \frac{H}{1000.0} \times H_{\text{src}}$$
* **Velocity-Adaptive Dynamic Camera Breathing:**
  The tracking camera breathes dynamically with target velocity rather than holding a rigid zoom multiplier:
  * Low target velocity ($v \le v_{\text{slow}} = 80\,\text{px/s}$): camera maintains tight zoom $S_{\text{base}}$ (default $2.2\times$).
  * High target velocity ($v \ge v_{\text{fast}} = 450\,\text{px/s}$): camera proportionally zooms out towards $S_{\text{min}}$ (default $1.3\times$) to keep action in frame without violent whips or motion-blur disorientation:
    $$S(v) = S_{\text{base}} - \left(\frac{v - v_{\text{slow}}}{v_{\text{fast}} - v_{\text{slow}}}\right) \times (S_{\text{base}} - S_{\text{min}}), \quad \text{clamped to } [S_{\text{min}}, S_{\text{base}}]$$
* **Target Occlusion, Death, or Exit Easing:**
  When a target becomes occluded, dies, or exits the frame (`Visible = false`), the camera does NOT snap or freeze. It initiates a smooth $0.4\,\text{s}$ exponential ease-out back to full-frame ($1.0\times$ scale, $C_x = W_{\text{src}}/2, C_y = H_{\text{src}}/2$).
* **Humanoid Vertical Framing Bias:**
  In third-person and first-person gaming footage, humanoid targets have height dominating width ($H > 1.15 \times W$). To frame the upper torso and head naturally above center rather than dead-centering on the character's pelvis or feet:
  $$C_{y,\text{optical}} = C_y - 0.12 \times H_{\text{target}}$$
* **Deadband Jitter Filter:**
  To eliminate AI bounding-box micro-jitter on stationary or slowly shifting targets, position deltas below $2.0\%$ of source dimensions are suppressed via deadband hysteresis:
  $$\Delta C_x < 0.02 \times W_{\text{src}} \implies \text{keep previous } C_x$$
* **Game profile (`AIGAME_01`, `AiGameProfile.cs`, Settings › AI Tracking "What are you recording?"):** `Generic`, `ThirdPerson` (default), `Fortnite`, `FirstPerson`. The prompts (`AiTrackingPrompts.cs`), the own-character exclusion and the default HUD zones follow from it. No prompt names a game the user did not choose, and no prompt names a player skin. The `Fortnite` profile adds its scan hints (bubble-shield domes, builds and roofs, gliders); `ThirdPerson`/`FirstPerson` ask for distant, partly hidden or rooftop opponents.
* **Own-character exclusion (`AIEXCLUDE_02`, `IsMainPlayerCharacter(subject, profile)`):**
  1. Semantic (every profile): a WORD match (`\bself\b`, so "shelf" is not "self") on labels such as `"main player"`, `"local player"`, `"my character"`, `"own avatar"`, `"first-person hands"`, `"held weapon"`.
  2. Spatial third-person anchor — ONLY for `ThirdPerson`/`Fortnite`, ONLY when the label is generic (`player`, `character`, `avatar`, `person`, `skin`, `foreground`, `closest`, `back view`, `from behind`) and NEVER when it calls the subject hostile (`opponent`, `enemy`, `rival`, `combatant`, `hostile`, `attacker`, `target`, `foe`): $Y_{\max} \ge 750$, $250 \le C_x \le 650$, $\text{Height} \ge 300$. The previous unconditional "massive anchor" rule ($Y_{\max} \ge 850$, $H \ge 400$ ⇒ self) is REMOVED: it threw away close-range opponents, the best highlights.
  Tracking prompts carry the profile's exclusion rule (`AiTrackingPrompts.ExclusionRule`); `Generic` has none.
* **Multimodal Visual Reference Crop Anchoring (`CropSubjectJpeg`):** the confirmed target's box is cropped from the frame it is clearest in (15% padding, SkiaSharp JPEG) and sent with the clip. The prompt states WHEN in the clip that box was observed (`observedAtSec`) instead of an undated "initially observed". A box the user drew (`AIOWNBOX_01`) is sent without a reference crop.
* **Defensive Waypoint Sanitizer (`SanitizeWaypointsAgainstLocalPlayer`, third-person profiles only):** if the confirmed target was external, any waypoint with $Y_{\max} \ge 720, 240 \le C_x \le 660, H \ge 260$ is suppressed (`Visible = false`) so the inertial hold carries the camera.
* **Inanimate Scenery & Geometry Purge (`IsInanimateScenery`):** static geometry (buildings, towers, statues, houses, roofs, terrain, trees) is purged unless a combatant/player qualifier is present.
* **Adaptive Forward Lookahead Probing (`AILOOKAHEAD_01`, `DiscoverSubjectsWithLookaheadAsync`):** when the segment's 3 stills show nobody, up to 2 probes run at $+\max(2.0, 0.75 D)$ and $+\max(4.0, 1.5 D)$. The probe limit is the last SOURCE second of the SAME file (`sourceEndLimitSec` = trim end, or the merge clip's kept end) — never the merged/trimmed timeline length, which the old code compared against an absolute source time. A target found by a probe lies AFTER the block: the picker says so and the editor offers to extend the block (UI-AIZOOM); the result records `LookaheadOffsetSec`.
* **Chunked tracking (`AICHUNK_01`, `PlanChunks`):** one request carries at most `MaxChunkSec` = 8 s of video. A block of duration $D$ is split into $n = \lceil D / 8 \rceil$ equal chunks ($n = 1$ for $D \le 8.5$); chunk $k$ starts from the last visible box of chunk $k-1$, waypoints are offset by the chunk start, and the whole block is smoothed once. Blocks over `MaxTrackSec` = 40 s are refused with a plain reason. (Before: the slice was clamped to 8 s and the last waypoint was held frozen for the rest of a longer block.)
* **Sampling rate (`AIFPS_01`):** the clip part carries `videoMetadata.fps = 2` so the 0.5 s waypoint grid is backed by real frames (Gemini samples video at 1 fps by default). If the API rejects the request (HTTP 400) it is re-sent once without the field, asking for a 1.0 s grid.
* **Structured answers:** discovery and tracking requests use `temperature = 0`, `responseMimeType = application/json` and a `responseSchema`; text parts are concatenated (thought parts skipped); coordinates are clamped to $[0,1000]$; malformed JSON is an ERROR (`GeminiException` `MalformedResponse`), never silently "no targets".
* **Segment-range filter (`AITIME_01`):** before smoothing, waypoints with non-finite time or $t \notin [0, D]$ are dropped. (Before: a 2 s minimum slice on a 1 s block produced points past the end, the last one was re-timed to $D$, and the keyframe times ran backwards.)
* **Re-frame without the AI (`AIRESMOOTH_01`):** `SmoothedTrajectory.RawWaypoints` (JSON `"raw"`, omitted when null so older documents round-trip unchanged) keeps the sanitised waypoints, so new camera settings can be re-applied offline. Smoothing is deterministic: re-smoothing a trajectory's own `RawWaypoints` with the same settings reproduces it exactly.
* **HUD Clearance (`AIHUDZONE_01`, `AiHudAvoidZones`, `AiTrajectorySmoother.AvoidHudZones`):** when `avoidHud` is on (landscape only — portrait keeps the center-slice rule below), each crop window that overlaps a HUD zone moves by the SMALLEST single horizontal or vertical shift that clears the zone, keeps the window inside the frame AND keeps the whole target box inside the window; if no shift does all three, the window stays (the target wins). Two passes. Zones come from the user's Crop Tool layout (`crops_1080p` → `InverseTransformFromContentAreaInt` once, zero layers skipped) and otherwise from the profile: top-right $(0.78, 0, 0.22, 0.32)$, bottom-left $(0, 0.80, 0.30, 0.20)$, bottom-right $(0.70, 0.80, 0.30, 0.20)$; `Generic` has none. (Before: only the top-right zone, moved left AND down at once, regardless of the target — and the spec described the bottom corners the code never avoided.)
* **Merge clips (`AIMERGE_01`):** coordinates use the frame size of the clip actually sent (read from the decoded still), not the editor's reference resolution; a block spanning two clips is refused.
* **Strict Boundary Anchoring:**
  Smoothed trajectories are strictly anchored at $t = 0.0$ and $t = D_{\text{segment}}$, guaranteeing deterministic interpolation across the entire segment duration.
* **Portrait Mode Center Slice Invariant (`PORTRAIT_01`):**
  When exporting in portrait mode (9:16), if the zoom box width $W_{\text{crop}}$ fits within the surviving portrait width ($W_{\text{crop}} \le W_{\text{surv}}$), `cropX` is strictly clamped within the active center slice $[(W_{\text{src}} - W_{\text{surv}})/2, (W_{\text{src}} + W_{\text{surv}})/2 - W_{\text{crop}}]$, guaranteeing that the tracking camera never reveals out-of-bounds void areas.
* **Portrait Mode Effective Scale & Safe Margins (`AIPORTRAIT_ZOOM_01`, `AIPORTRAIT_MARGINS_01`):**
  Portrait mode already applies an implicit $\sim 2.67\times$ horizontal center-cut to convert $16:9$ footage to mobile $2:3$ / $9:16$. To prevent extreme magnification ($>5.8\times$) that crops out player character bodies and causes foreground avatar heads to obstruct the view in third-person games, portrait base zoom is capped to $1.65\times$. Furthermore, portrait safe framing margins enforce a top margin fraction of $0.20$ and bottom margin fraction of $0.72$, ensuring subjects are never obscured by top/bottom letterbox bands ($150\,\text{px}$) or mobile HUD overlays (minimap, weapon inventory, health bar).

* **AI Kill Tracker (`AIELIM_02`, `AIELIM_03`, `AiEliminationTrackerService`):** loud moments come from 8 kHz mono PCM energy (250 ms windows above max(600, 1.75 × average), one per 2.5 s burst); since `AIELIM_03` the loudest `MomentBudget(duration)` = clamp(⌈duration / 5 s⌉, 24, 60) are checked (it was 24 for any length) in batches of 6, each still 1280 px wide (`FrameWidth`; at 640 px a kill feed was unreadable), and the prompt counts ONLY the player's own kills (personal banner / counter, own hit marker, victory screen, enemy collapsing at the player's aim — a kill-feed line alone is not enough, it lists everyone's kills). A video without an audio stream is reported as "no sound — nothing was checked" (`AiFfmpeg.HasAudioStreamAsync`) instead of "no loud moments". The length comes from the player or, before it reports, `AiFfmpeg.ProbeDurationSecAsync` (the old 60 s guess sent the picture of 0:59.95 for every moment after one minute). Each moment sends TWO frames taken AFTER the sound (+0.4 s, +1.2 s — kill feeds and banners appear a beat after the shot), labelled "[Moment M3 · frame A · 12.4s]"; the answer must echo the moment ID, so it can never be shifted onto the wrong moment (the old prompt never said whether frames were counted from 0 or 1). Markers are ABSOLUTE source seconds; the speed editor places them through its output timeline and hides those whose moment is cut.


---

## 11a. AI Live Commentary Captions & Timeline Mapping  {#TL-CAPTIONS}
Governs automated first-person commentary narration, syllable-weighted timing, and timeline projection (`CaptionModel.cs`, `CaptionTimeline.cs`).
* **Source Frame of Reference Invariant:**
  AI generation services emit commentary lines (`CaptionLine`) and words (`CaptionWord`) referenced strictly to original source media seconds ($t_{\text{src}} \in [0, D_{\text{src}}]$).
* **Syllable-Weighted Word Distribution:**
  When transcription or commentary generation returns line-level intervals without explicit per-word timing, `CaptionTimeline.DistributeWords` distributes duration proportionally to word syllable weights:
  $$s_i = \max(1, \text{vowelGroups}(w_i) - \text{silentE}(w_i) - \text{silentEd}(w_i))$$
  $$\Delta t_i = (t_{\text{end}} - t_{\text{start}}) \times \frac{s_i}{\sum_j s_j}$$
  This avoids robotic uniform word slicing and guarantees realistic karaoke cadence on polysyllabic words.
* **Timeline Projection & Speed Ramp Retiming (`CaptionTimeline.Map`):**
  Pure projection from source space into finished-video output space projects every word through `OutputTimeline.SourceToOutput`:
  * **Speed changes:** Words during slow-motion ($v < 1.0$) proportionally expand in output duration; fast-motion ($v > 1.0$) words compress.
  * **Freezes:** Held frames extend the coincident word by $t_{\text{freeze}}$, maintaining subtitle presence during action freezes.
  * **Cuts:** Words falling inside user cuts ($[t_{\text{cut,start}}, t_{\text{cut,end}}]$) are excised; lines with zero surviving words or net duration $< 0.2\,\text{s}$ are dropped.
  * **ASS Karaoke Precision:** Each mapped word produces integer centiseconds $\Delta t_{\text{csec}} = \max(1, \text{round}(\Delta t_{\text{out}} \times 100))$ ready for ASS `\k` tags.
* **Canvas Layout & Dynamic HUD Clearance (`AICAPTION_LAYOUT_01`, `CaptionLayoutEngine`):**
  Pure geometric layout engine positioning captions across portrait and landscape canvases:
  * **Portrait (1080x1920):** Captions sit inside the $1080 \times 1620$ content area with $60\,\text{px}$ side margins (max width $960\,\text{px}$), strictly above the $150\,\text{px}$ bottom void and below the $150\,\text{px}$ top void unless `CaptionPosition.BottomVoid` is chosen.
  * **Landscape (1920x1080):** Captions sit in the lower third with $160\,\text{px}$ side margins (max width $1600\,\text{px}$).
  * **Line Constraints:** Strictly limited to $\le 8$ words per line and $\le 2$ lines on screen per caption event; font size dynamically scales (×0.85) if required to stay within 2 lines.
  * **Size & default height (`AICAPTION_LAYOUT_02`, 2026-10-16):** font `PortraitFontSize` = 76 px (was 54) and `LandscapeFontSize` = 64 px (was 58), outline 4.5. The DEFAULT position (`BottomContent`) centres the caption box on the line between the upper two-thirds and the lower third: $Y_{\text{centre}} = H \times \tfrac{2}{3}$ (1280 on 1920, 720 on 1080), clamped inside the content area. It used to sit at the very bottom of the content area, on top of the Fortnite health / shield / stamina band. `Top` and `BottomVoid` are unchanged.
  * **HUD Collision Avoidance:** HUD rectangles (portrait export: the user's Crop Tool layout, `03` FFM-CAPTIONS) push the box upward until it is $\ge$ `HudGapPx` = 24 px clear of every piece ($Y \le Y_{\text{hud}} - H_{\text{box}} - 24$), repeated up to 8 passes because moving off one piece can land on another (the old single pass stopped after the first move). A move that would leave the content area is not made.
  * **Placement, size and look (`CAPTIONPLACE_01`, `CAPTIONSIZE_01`, `CAPTIONLOOK_01`, 2026-10-17):** the options window offers three spots — `Top` (upper third, unchanged), `Middle` (NEW: the box's TOP at $H \times$ `MiddleTopFraction` = 0.54, just under the centre so the aim point stays clear; a HUD piece it overlaps pushes it DOWN, never up onto the crosshair) and `BottomContent` (lower third, unchanged). `CaptionTrack.Size` scales the base font (`CaptionLayoutEngine.SizeScale`: Small 0.8, Medium 1.0 = the size before the option, Large 1.25). `CaptionTrack.Look`: `Outline` — outline 6 (was 4.5), shadow 3 (was 2) at 75 % opacity (was 50 %); `Box` — ASS `BorderStyle` 3, a ~69 %-opaque black box per line (outline colour `&H50000000`, padding 14). Both fields are additive in `CaptionTrackJson` (missing = Outline / Medium).
  * **Right-to-left captions (`CAPTIONRTL_01`):** the style's `Encoding` is −1, so libass takes each line's base direction from its text. A line containing Hebrew or Arabic letters (`CaptionAssWriter.IsRightToLeft`) is written as ONE plain run starting with U+200F RIGHT-TO-LEFT MARK: no per-word `\k` or emphasis tags inside it, because tags between words split the line into runs that some libass versions lay out left to right (a Hebrew sentence came out in reverse word order). Such lines therefore appear whole, without the word-by-word sweep.
  * **Anchoring:** each Dialogue line carries `{\an5\fs<size>\pos(cx,cy)}` — centre anchor at the layout box centre, with the layout's own font size. The style's bottom-centre anchor used to put the text's BOTTOM on the box centre, so every caption sat half a box higher than computed.
* **Advanced SubStation Alpha Writer (`AICAPTION_ASS_01`, `CaptionAssWriter`):**
  Pure `.ass` v4.00+ script generator consumed identically by FFmpeg (`subtitles` filter) during export and libmpv (`sub-add`) during preview:
  * **PlayRes Invariant:** `PlayResX` and `PlayResY` match the video canvas ($1080 \times 1920$ or $1920 \times 1080$), guaranteeing exact pixel parity between preview and render.
  * **Karaoke Animation:** Produces `\k<csec>` centisecond timing tags for real-time word highlight progression, with gold/yellow emphasis color tagging (`\c&H0000E5FF&`).
  * **Safe Escaping:** Escapes special ASS control characters (`{`, `}`, `\`) and formats line wraps with `\N`.
* **AI Generation Service (`AICOMMENTARY_SERVICE_01`, `CommentaryService`, `AiCommentaryPrompts`):**
  Multimodal AI commentary pipeline generating synchronized gaming narration:
  * **Chunking Architecture:** Clips longer than 8 seconds are partitioned into overlapping segments ($\le 8\,\text{s}$ chunk length, $1\,\text{s}$ temporal overlap).
  * **Multimodal Input Dispatch:** Google Gemini consumes native 2 fps MP4 video chunks with audio; Anthropic Claude and OpenAI consume timestamped frame sequences extracted via `AiFrameSequenceExtractor.SampleFramesAsync`.
  * **Seam De-Duplication:** Lines spanning overlap seams are merged deterministically by temporal IoU ($\ge 45\%$) or textual similarity, retaining the higher-confidence candidate and unioning timestamps.
  * **Tone & Profile Prompts:** Specialized prompt schemas enforce first-person gaming perspectives across Hype, Chill, Funny, and Tactical tones.
  * **The commentary "brain" (`COMMENTARYMIX_01`, `CommentaryPersonality`, 2026-10-17):** the single tone became four amounts, 0 (Off) … 4 (Max): Funny, Sarcastic, Energetic (0 = calm and quiet … 4 = maximum hype — it never means "technical") and Tactical, plus how often it talks (Quiet · Normal · Busy) and whether swearing is allowed (off by default, `SWEARING_01`). The prompt receives every amount as WORDS ("Funny (A lot): strongly, in most lines — …"), never as numbers. Four one-click styles set the amounts: Hype Streamer (1,0,4,1), Chill Friend (2,1,0,1), Coach (0,0,2,4), Roast Me (3,4,2,0). `CaptionTrack.Tone` keeps the mix's `DominantTone` for older readers; a request with only a tone uses that tone's style.
  * **Tactical honesty (`TACTICAL_01`):** with Tactical above Off, the prompt requires every "why" to come from something visible (or audible, for vendors that hear the clip) — health or shield going down, how many enemies are visible, distance, a big hit, the zone edge, ammo — stated as a plain observation, and forbids invented thoughts, hopes, plans or feelings; with no visible reason the AI describes only the action or writes nothing. At "A lot" / "Max" lines the AI itself rated below `TacticalMinConfidence` = 0.4 are dropped.
  * **Longer tactical lines (`TACTICAL_02`):** at Tactical ≥ `LongLinesFromTactical` (3) the prompt allows 12 words per line (else 8) and `EnsureReadableOnScreen` keeps each line ≥ 2.0 s of finished video (else 1.2 s). The layout still wraps at 8 words per visual line, 2 lines.
  * **Kill marks are facts (`TACTICAL_03`):** the editor passes the AI Kill Tracker's elimination times (`CommentaryRequest.KillTimesSec`, absolute source seconds); each part's prompt lists the ones inside it, part-relative ("these eliminations by YOU really happened at 1.5s, 6.0s … do not claim any other"), or says the tracker found none there. Without tracker results nothing is said about kills.
  * **Hebrew (`CAPTIONRTL_01`):** "Language: Hebrew" asks for Hebrew letters in natural Israeli gamer speech, game names kept as in the game; rendering is described under the layout rules above.
  * **Rewrite one line (`REWRITELINE_01`):** the regenerate request of a single cue carries the remembered mix, the kill marks and `RewriteOf` = the old text ("return exactly ONE line … that replaces: …").
* **Absolute Source Seconds, End to End (`AICOMMENTARY_SERVICE_01`):** a part's answer is offset by the part's ABSOLUTE source start (`request.StartSec + chunk.Start`) and kept inside that part (lines starting past its end are dropped, ends are clamped). Before this, lines were offset by the part's position inside the range only, so on any trimmed clip or single-block run every caption landed early by the trim/segment start.
* **Speed-Aware Generation (`AICOMMENTARY_SPEED_01`):** the request carries the editor's output timeline (trim-relative, so `TimelineOffsetSec` = trim start). Each part's prompt lists, in part-relative seconds, every slow-motion / fast-forward stretch (with its speed), freeze and cut (`OutputTimeline.DescribeSourceRange`, `CommentaryService.DescribePlayback`). After generation `EnsureReadableOnScreen` keeps every line inside the requested range and on screen for at least `MinReadableOutputSec` = 1.2 s of FINISHED video (a line inside a 4x stretch is lengthened in source time), never into the next line or past the range.
* **Segment Merge (`AICOMMENTARY_MERGE_01`):** commentary made for ONE block replaces only existing lines fully inside that block; lines crossing its edge are trimmed to the outside part (dropped under 0.5 s); ids are renumbered in time order. "Entire timeline" still replaces the whole track.
* **Part Retry & Report (`AICOMMENTARY_REPORT_01`):** an unreadable answer, a block, a bad request, a timeout or a busy server retries that part once; a part that still fails is skipped and counted (`CommentaryResult.FailedChunks`) and the editor says "N of M part(s) could not be analysed". Key / permission / model / quota errors stop the run. All parts failing is an error, never an empty "success".
* **Preview Clock (`AICOMMENTARY_PREVIEWCLOCK_01`):** libmpv plays the SOURCE file and applies speed changes and freezes live, so the preview `.ass` is written in source time with no output projection (`timeline: null`); the export burns the same track through its output timeline (origin = trim start).
* **Captions Lane Editor & Interaction Math (`AICAPTION_LANE_01`, `TimelineLanesControl`, `GranularSpeedEditorWindow.CaptionsLane.cs`):**
  Interactive timeline lane dedicated to commentary caption cues:
  * **Lane Dimensions:** Fixed 36 px height (Row 4, `LaneCBorder`, `LaneCHostPanel`) sitting directly below Lane B, toggleable via `LaneCVisible`.
  * **Coordinate Conversion:** Cue blocks are positioned horizontally using source-time coordinate projection $X = \text{SrcMsToX}(t_{\text{start,ms}})$, with width $W = \text{SrcMsToX}(t_{\text{end,ms}}) - X$. Inverse scrubbing and dragging resolves time via $t_{\text{ms}} = \text{XToSrcMs}(X)$.
  * **Cue Block Visuals:** Rendered in golden amber theme brushes (`#f59e0b`) with a semi-transparent fill (`#40f59e0b`), contrasting border, line text label, and left/right resize grip handles ($6\,\text{px}$ wide).
  * **Drag & Resize Clamping:**
    * Center move dragging adjusts both start and end synchronously, clamped within clip boundaries $[0, D_{\text{src}}]$ and strictly forbidden from overlapping adjacent caption cues.
    * Left handle resize modifies $t_{\text{start}}$, clamped such that $t_{\text{start}} \le t_{\text{end}} - 0.2\,\text{s}$ (minimum cue duration $200\,\text{ms}$) and $t_{\text{start}} \ge t_{\text{prev,end}}$.
    * Right handle resize modifies $t_{\text{end}}$, clamped such that $t_{\text{end}} \ge t_{\text{start}} + 0.2\,\text{s}$ and $t_{\text{end}} \le t_{\text{next,start}}$.
  * **Modal Editing & Regeneration (`EditCaptionDialogWindow`):** Double-clicking an individual cue block opens `EditCaptionDialogWindow` enabling direct text editing, start/end timing adjustment, single-line AI regeneration via `CommentaryService`, and deletion.
  * **Keyboard Deletion:** Selecting a cue and pressing `Delete` or `Backspace` removes the caption cue, registering a full undo checkpoint.
* **Document Persistence & Recovery (`CAPTIONS_01`, `CaptionTrackJson`):**
  Full persistence across `.fvsproj` project documents and crash recovery envelopes:
  * **Schema Version 6:** Bumps `ProjectDocument.SchemaVersion` from 5 to 6, adding `CaptionTrack? Captions { get; init; }`. Legacy documents (schemas 1–5) read with null fallback.
  * **Reflection-Free AOT Codec:** `CaptionTrackJson.Write` and `CaptionTrackJson.Read` serialize `CaptionTrack`, `CaptionLine`, and `CaptionWord` without runtime reflection or dynamic code generation.
  * **Crash Recovery Integration:** `GranularRecoveryCodec.Build` and `TryApply` persist and restore active `CaptionTrack` in the crash recovery state envelope.

---

## 12. HUD Auto-Detector Streaming Memory Optimization  {#TL-HUDSTREAMING}
Governs automated computer vision HUD discovery in `HudAutoDetector.cs` and `HudImageOps.cs`.
* **Constant Memory Streaming Mandate:**
  Legacy implementations buffered up to 400 uncompressed BGR24 frames ($960 \times 540 \times 3 \approx 1.55\,\text{MB/frame}$) into an unbounded `List<byte[]>`, exceeding $600\,\text{MB}$ on the Large Object Heap (LOH) and triggering out-of-memory aborts and multi-second GC pauses on 8 GB systems.
* **Online Sequential Stream Architecture:**
  Full-array buffering is replaced with sequential single-frame streaming (`SampleFramesAsync`):
  * Active memory footprint is strictly bounded to $\le 25\,\text{MB}$ total, regardless of clip duration or frame count.
  * Temporal variance is accumulated online in a single pass using Welford's algorithm (`OnlineFrameAccumulator`):
    $$M_{k} = M_{k-1} + \frac{x_k - M_{k-1}}{k}, \qquad S_{k} = S_{k-1} + (x_k - M_{k-1})(x_k - M_k), \qquad \sigma^2 = \frac{S_n}{n - 1}$$
  * Temporal median and stability masks are computed on downscaled streaming buffers, releasing each raw video frame back to the system immediately after consumption.
* **Stretched-Resolution HUD Aspect Ratio Tolerance:**
  In competitive gaming resolutions where the video source aspect ratio deviates from 16:9 (e.g., 4:3 stretched like 1440x1080, 16:10 like 1680x1050, or 21:9 ultrawide), HUD bounding-box geometry is scaled by the canvas stretch factor:
  $$\text{stretchFactor} = \frac{W_{\text{src}} / H_{\text{src}}}{16.0 / 9.0}$$
  $$\text{minAspect} = \begin{cases} \text{spec.MinAspect} \times \text{stretchFactor}, & \text{if } \text{stretchFactor} < 1.0 \\ \text{spec.MinAspect}, & \text{otherwise} \end{cases}$$
  $$\text{maxAspect} = \begin{cases} \text{spec.MaxAspect} \times \text{stretchFactor}, & \text{if } \text{stretchFactor} > 1.0 \\ \text{spec.MaxAspect}, & \text{otherwise} \end{cases}$$
  $$\text{idealAspect} = \text{spec.IdealAspect} \times \text{stretchFactor}$$
  This prevents valid HUD elements (weapons, mini-map, bars) from being discarded by aspect ratio filters when playing on non-16:9 stretched resolutions.

---

## 12a. AI-Assisted Magic Wand — Optional Cloud HUD Candidates  {#TL-AIHUD}
Governs `AiHudDetection.cs`, `AiHudResponseParser.cs`, `HudCandidateFusion.cs`, `HudDetectionCoordinator.cs`, `AiHudResultCache.cs` (Core) and `GeminiHudDetectionService.cs`, `AiHudFrameBuilder.cs`, `CropToolWindow.MagicWand.cs` (App).
* **`AIHUD_01` — the local detector is the baseline, never replaced.** `HudAutoDetector` (§12) always runs and needs no key, network or consent. The AI provider may ADD candidates and REFINE a local box; it can never remove a local candidate and it never commits anything. No key, no consent, network failure, timeout, HTTP 429, model error or malformed answer ⇒ the user receives exactly the local result.
* **One frozen frame only.** The provider receives ONE still — the frozen snapshot the user is looking at — re-encoded as JPEG with long edge ≤ 1600 px (`AiHudFrameBuilder.MaxLongEdge`). The video file is never read for upload and never sent.
* **AI response schema (normalized 0..1 SOURCE-FRAME coordinates, top-left origin):**
  `{"candidates":[{"label":string,"normalizedX":number,"normalizedY":number,"normalizedWidth":number,"normalizedHeight":number,"confidence":number}]}` (a bare array of the same objects is accepted; a markdown fence is stripped).
* **Validation (`AiHudResponseParser.IsValid`) — reject, never clamp:** non-finite values (NaN, ±Infinity); $w \le 0$ or $h \le 0$; $x, y \notin [0,1]$; $x + w > 1 + \varepsilon$ or $y + h > 1 + \varepsilon$ ($\varepsilon = 0.002$); confidence $\notin [0,1]$; empty label; $\min(w,h) < 0.01$ or $w \cdot h < 0.0005$ (tiny); $\max(w,h) > 0.90$ or $w \cdot h > 0.33$ (near-whole-screen junk). Malformed JSON or wrong shape rejects the whole answer. At most 12 candidates are kept (confidence desc, then $y, x, w, h$, label ordinal).
* **Coordinate Invariant — converted exactly once (`AiHudGeometry.ToSourcePixels`):**
  $$x_0 = \mathrm{round}(x \cdot W_{\text{src}}),\; y_0 = \mathrm{round}(y \cdot H_{\text{src}}),\; x_1 = \mathrm{round}((x+w) \cdot W_{\text{src}}),\; y_1 = \mathrm{round}((y+h) \cdot H_{\text{src}})$$
  each clamped to $[0, W_{\text{src}}]$ / $[0, H_{\text{src}}]$, rounding `AwayFromZero`; box $= (x_0, y_0, x_1 - x_0, y_1 - y_0)$. $W_{\text{src}}, H_{\text{src}}$ are the SOURCE frame's dimensions (the snapshot's own pixels, landscape or portrait) — never Canvas/display pixels and never the downscaled JPEG. The result enters the existing crop geometry pipeline (`SourceRect` → `ClampSourceRect`) unchanged.
* **`AIHUD_02` — deterministic fusion (`HudCandidateFusion.Fuse`):** AI boxes in total order (confidence desc, $y, x, w, h$, label). Each claims the unclaimed local box with the highest IoU when roles are compatible (equal, or either unknown) and IoU $\ge 0.30$ or containment (intersection / smaller area) $\ge 0.70$; ties → lower local index. A pair becomes ONE `Fused` result: local box kept unless AI confidence $\ge 0.60$ and IoU $\ge 0.50$ (then the AI box refines it); local role wins. Unmatched local boxes survive unchanged. Unmatched AI boxes survive only at confidence $\ge 0.35$ and IoU $< 0.50$ with every kept box. Output = local/fused in local rank order, then AI-only; cap 12 (local results are never cut in favour of AI-only ones).
* **`AIHUD_03` — cancellation and staleness (`HudDetectionCoordinator`):** each run takes a new generation and cancels the previous token; `Cancel()` (STOP, new clip) and `Dispose()` (window close) also advance the generation. A run publishes only if, after BOTH detectors finish, its token is live AND its generation is current; otherwise it returns `null` and the window draws nothing. A slow AI answer therefore cannot overwrite a newer scan.
* **`WANDPROGRESSIVE_01` — local finds first (`HudLocalPreview`, `RunAsync(..., localReady)`):** when the offline scan finishes while the AI request is still pending, the coordinator reports the local candidates ONCE through `localReady` (only if the run is still current and not cancelled), so the window can show them immediately. The final fused result still arrives only through the return value and still obeys `AIHUD_03`; nothing is reported early when there is no AI request or the AI already answered.
* **`AIHUD_04` — cache:** `AiHudResultCache` (16 entries, in-memory, per process) keyed by (source fingerprint = hash of path+length+mtime, frame key = SHA-256 of the snapshot bytes, model). Success only. Never persisted, never holds credentials. In the window, AI-derived candidates are bound to the frame they were computed for; a new frozen frame re-asks the AI (local result reused) or reverts to local-only.
* **Faults:** AI failure ⇒ one `Degraded` notice through `IFaultSink` naming what stopped and that the offline results are still shown; missing key is not a fault. Provider-internal catches report `Recoverable` with redacted detail only (no raw exception object).
