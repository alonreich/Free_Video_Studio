# FREE VIDEO STUDIO: ARCHITECTURAL SPECIFICATIONS — START HERE

> **This file is the ONE entry point for every developer and AI agent** (ONBOARD_01). `project_structure.txt`
> and `AGENTS.md` at the repository root only point here. Read §0 completely before you inspect or change
> anything; it tells you what you may never do, what to read for your task, and what you must record
> before you finish.

## 0. Start Here: Rules For Every Developer And AI Agent  {#GOV-START}

### 0.1 Non-negotiable mandates

> ### 🛑 CRITICAL MANDATE FOR ALL AI AGENTS: NEVER EXECUTE `Build.cmd`
> Autonomous AI agents are **STRICTLY FORBIDDEN from ever executing `.\Build.cmd` or `Build.cmd`**.
> `Build.cmd` triggers `build/FvsBuild` in production release mode, which creates git release tags and **OFFICIALLY PUBLISHES A NEW VERSION DIRECTLY TO GITHUB CLOUD** for public download worldwide.
> If an agent must test compilation, packaging, or NativeAOT publish, it **MUST ONLY USE `.\dev_build.cmd`** (local build only, passing `--dev`, never touches GitHub or publishes to the cloud).
> The release gate (`09` [DIST-RELEASEGATE](09_DISTRIBUTION_AND_RELEASE.md#DIST-RELEASEGATE)) makes `Build.cmd` refuse
> a dirty, unpushed or unreviewed release and ask a human to type `PUBLISH`. That protects the release; it is
> NOT permission for an agent to run it.

* **Stop-and-Read:** never inspect or edit code without first reading and citing its mapped specification
  in `docs/` ([`SPEC_GOVERNANCE.md`](SPEC_GOVERNANCE.md) §1–§2). Files marked `⚠` are co-governed: read every spec they name.
* **Proof-of-Read:** open every edit proposal with the three-line header of `SPEC_GOVERNANCE.md` §3 (cite the
  stable `{#ANCHOR}`, never a section number).
* **In-Code Spec Contract:** every new production source file under `src/` starts with the `[SPEC CONTRACT]`
  block (`SPEC_GOVERNANCE.md` §4).
* **Land the sentinel with the fix:** a guarded fix adds its `TAG=path` line to `build/sentinels.txt` in the
  same change (§4 rule 5 below).
* **Never commit secrets:** no keys, `.pfx`, `*.key.pem` or `*.password.txt` (`05` SECRETSCAN_01).

### 0.2 Reading order (the agent route)

1. This file, §0 (rules) → §2 (the 9 invariants).
2. Know the FILE → §3 Domain Routing. Know only a SYMBOL, CONSTANT or TAG → grep [`INDEX.md`](INDEX.md).
3. Read the ONE spec you land on (all of them for a `⚠` file), then the code.
4. Need a path or a component's location? §5 Repository Map.

### 0.3 Before you finish ANY task: record what changed (CHANGELOG_01 / RELNOTES_01)

An agent may not finish a task or commit code that changes logic, UI, workflows, build, tests or project
structure without all of these ([`09` DIST-RELEASENOTES](09_DISTRIBUTION_AND_RELEASE.md#DIST-RELEASENOTES)):

1. **Technical log — [`changes.md`](changes.md) → `## Unreleased`:** one entry per change,
   `- <date> · <TAG> · <what changed and why> (<files>)`. Jargon is fine here.
2. **User notes — [`CHANGELOG.md`](CHANGELOG.md) → `## Upcoming`** (never a guessed version heading): plain,
   short, oversimplified English for end users, `**Headline:** one or two sentences`. Technical jargon,
   method, class and file names and engineering tags are forbidden. MERGE with an existing bullet instead of
   adding a near-duplicate; at most 15 bullets in total (the release lint, RELNOTES_02, refuses more).
   Internal-only changes (tests, docs, build tooling) need no user bullet.
3. **Map sync:** new or removed files, components or rules → update §5 Repository Map below and
   [`INDEX.md`](INDEX.md) (and the owning spec's Code Mini-Map).
4. **Retention is automatic:** the official release promotes both staging sections and keeps 5 releases in
   `CHANGELOG.md` and 10 in `changes.md`. Do not prune or rename sections by hand.

### 0.4 Build, test, release

| Goal | Command | Who |
| :--- | :--- | :--- |
| Run the app from source | `.\dev.cmd` (hot reload; `dev trace`, `dev rebuild`, `dev fresh`) | anyone |
| Full local packaging check (NativeAOT + installer, no GitHub) | `.\dev_build.cmd` | anyone |
| Compile / test | `dotnet build FreeVideoStudio.sln -c Release`, `dotnet test tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj`, `dotnet test tests/FreeVideoStudio.App.Tests/FreeVideoStudio.App.Tests.csproj` | anyone |
| Fix sentinels | `dotnet run --project build/FvsVerify/FvsVerify.csproj` | anyone |
| Official stable release | `.\Build.cmd` (release gate, `09` DIST-RELEASEGATE) | **human only** |
| Beta (pre-release) | `.\Build.cmd --beta` | **human only** |

### 0.5 Which document answers what

| Document | Read it when |
| :--- | :--- |
| [`SPEC_GOVERNANCE.md`](SPEC_GOVERNANCE.md) | Before any code inspection or change: the zero-leakage contract. |
| [`INDEX.md`](INDEX.md) | You know a file, symbol, constant or tag and need its spec. |
| [`01_TIMELINE_COORDINATE_MATH.md`](01_TIMELINE_COORDINATE_MATH.md) … [`08_APPLICATION_COMPOSITION.md`](08_APPLICATION_COMPOSITION.md) | The domain specs (§3 routes you to one). |
| [`09_DISTRIBUTION_AND_RELEASE.md`](09_DISTRIBUTION_AND_RELEASE.md) | Installer, smart updates, release gate, release notes, retention. |
| [`05_SYSTEM_LIFECYCLE_STORAGE.md`](05_SYSTEM_LIFECYCLE_STORAGE.md) | Install/update transaction and its trust, auto-update, signing, dev harness. |
| [`CHANGELOG.md`](CHANGELOG.md) / [`changes.md`](changes.md) | User release notes / technical change log (§0.3). |
| [`New-Release.md`](New-Release.md) | The human checklist after a public release (antivirus submissions). |
| [`REBRAND_MIGRATION.md`](REBRAND_MIGRATION.md) | Old→new product identity and its migration. |


> ### ✅ REBRAND COMPLETE — read [`REBRAND_MIGRATION.md`](REBRAND_MIGRATION.md)
> This product was renamed to **Free Video Studio** (`FreeVideoStudio`). The rebrand record lists the
> full old→new identity map, exactly what happens when an old-brand install updates, the automatic
> output naming (`FreeVideoStudio-N.mp4` / `Merged-Videos-N.mp4`, editable in Settings › Output Files)
> and the only two files allowed to contain the previous name.

## 1. Mission Architecture
Free Video Studio is a specialized, hardware-accelerated desktop video editing suite built with C# and Avalonia UI on .NET 9 (Native AOT compatible). The system transforms raw 16:9 widescreen gameplay footage into master-quality 9:16 portrait montages, mobile highlights, and social video deliverables with zero manual keyframing.

### Product and repository identity

`FreeVideoStudio.sln` contains the renamed `src/FreeVideoStudio.App`, `src/FreeVideoStudio.Core`, `tests/FreeVideoStudio.App.Tests` and `tests/FreeVideoStudio.Core.Tests` projects. The application assembly is `FreeVideoStudio`; its root namespace remains `FreeVideoStudio.App`. The core assembly and namespace are `FreeVideoStudio.Core`. `FvsBuild`, `FvsVerify`, `.fvsproj` and the existing `FVS_*` environment variables retain their internal names.

Use [distribution identity](09_DISTRIBUTION_AND_RELEASE.md#DIST-IDENTITY) for packaging names and local validation commands, and [storage migration](05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-REBRAND) for canonical AppData paths and legacy compatibility. `project_structure.txt` and `AGENTS.md` at the repository root only point to THIS file, the single entry point; the repository map that used to live in `project_structure.txt` is §5 below. Repository links are relative to the checkout; the GitHub repository is `alonreich/Free_Video_Studio` ([`REBRAND_MIGRATION.md`](REBRAND_MIGRATION.md)).

---

## 2. The 9 North Star Architectural Invariants
All subsystems, controls, and rendering components across `src/` must strictly enforce these nine non-negotiable architectural pillars:

1. **Single Binary Executable Mandate:** Zero loose companion assets (`.gif`, `.png`, `.wav`, `.ico`) alongside the output binary. All UI overlays, guide indicators, brand icons, and animations must be generated dynamically via code or vector path geometry (`PathGeometry`) in memory. The user downloads ONE file, the Inno Setup package `FreeVideoStudio-Setup.exe`; the program itself never carries other executables inside its resources (NODROPPER_01, `09` DIST-INSTALLER).
2. **Absolute Authority for Time:** `src/FreeVideoStudio.Core/Media/OutputTimeline.cs` is the authoritative mathematical model for single-clip linear edits and per-clip segment durations. For multi-clip NLE virtual timelines, `src/FreeVideoStudio.Core/Media/CompositeTimeline.cs` and `MergeEdl.cs` constitute the sole mathematical authority mapping source µs, 60 fps merged frames, and output seconds across `edl://` libmpv preview playback and FFmpeg multi-clip rendering.
3. **Strict A/V Process Isolation:** Master preview volume (Windows OS PID session) and preview playback remain completely decoupled from FFmpeg export filtergraphs. Master preview slider adjustments must never alter export loudness.
4. **Leak-Free Render Pipelines:** The deprecated FFmpeg `zoompan` filter is banned suite-wide due to fatal native heap leaks. Dynamic zooms must be achieved via frame-evaluated padding, dynamic scaling, cropping, and contrast-adaptive sharpening (`cas=0.5`).
5. **Zero Raw Hex Styling:** All Avalonia styles, controls, and dynamic templates must resolve colors exclusively through named `DynamicResource` tokens in `AvaloniaApp.axaml`. Hardcoded hex values in shared styling are strictly prohibited.
6. **Thread-Bound Safety Contracts & GPU Preview Lock:** UI dispatchers must never block on native audio/video subsystem calls. WASAPI audio capture lifecycles run on an isolated serialized worker thread; SkiaSharp snapshot encoding and heavy image decodes execute off the UI thread.
   * ⚠️ **MANDATORY ARCHITECTURAL LOCK — libmpv PREVIEW ENGINE:** libmpv's Render API (`mpv_render_context_create`) does NOT support Direct3D 11 (`"d3d11"` returns `-19 / MPV_ERROR_NOT_IMPLEMENTED`). The `WGL_NV_DX_interop` bridge (rendering into OpenGL FBOs shared directly with Direct3D 11 textures in GPU VRAM) combined with non-blocking 0 ms 16-slot ring probing is the **frozen, permanently locked hardware architecture**. It provides zero-copy VRAM throughput, unlocked 120/240 FPS playback, and unobstructed Avalonia XAML overlays without Win32 HWND airspace occlusion. Attempting to bypass WGL with non-existent libmpv D3D11 APIs guarantees a pitch-black screen (audio-only), and re-introducing blocking KeyedMutex timeouts (e.g. 1,000 ms `AcquireSync`) reintroduces 1 FPS lock contention. Modifying this architecture or reintroducing blocking mutex waits is strictly prohibited.
7. **Monotonic Progress Guarantee:** Render progress tracking must be cost-weighted and mathematically monotonic (P(n+1) >= P(n)). Progress bars may never snap, stutter, or lerp backward across multi-pass operations.
8. **Every Rule That Can Be A Test Is A Test — AND A MACHINE RUNS THEM:** A specification paragraph only protects the codebase if the next person reads it. Where a rule can be mechanically asserted — one activation path per control, no raw hex in styling, no `zoompan`, no unexplained empty catch — it lives in `tests/FreeVideoStudio.App.Tests/ArchitectureRuleTests.cs` and the prose explains *why*. A sentinel proves a fix has not been deleted; a test proves it has not been broken. ⚠️ And neither proves anything until something runs them without being asked: `.github/workflows/ci.yml` (`SYS-CI`) is what makes the ratchets real. Before it existed the suite had been red on two genuine shipped bugs — the undo re-entrancy guard (`UNDO_23`) and the project fingerprint round-trip (`PROJ_10`) — for long enough that nobody looked, because five Windows-only tests were permanently red beside them.
9. **No Failure Is Silent:** Every caught exception is classified through `IFaultSink` as Recoverable, Degraded or Fatal (`08_APPLICATION_COMPOSITION.md` §2). `catch { }` and `catch (Exception ex) { Log(ex); }` are not error handling — they leave the user to guess whether they mis-clicked.

---

## 3. Domain Routing

Route by FILE (below) or by SYMBOL (`INDEX.md`). Full paths live in each spec's Code Mini-Map — basenames here are unique across `src/`.
`⚠` = CO-GOVERNED by more than one spec: read EVERY spec that lists it (`INDEX.md` §1 names them).

**[`01_TIMELINE_COORDINATE_MATH.md`](01_TIMELINE_COORDINATE_MATH.md)** — Timeline & coordinate math — 16:9->9:16 geometry, OutputTimeline chunk model, markers, freezes, cuts, zoom spans, meme anchors, hitboxes.

```
CanvasMath.cs  ⚠CompositeTimeline.cs  CoordinateMath.cs  ⚠GranularSpeedEditorWindow.axaml.cs
⚠GranularSpeedEditorWindow.Merge.cs  HudAutoDetector.cs  HudConfig.cs  HudImageOps.cs  KineticScrubController.cs
AiHudDetection.cs  AiHudResponseParser.cs  AiHudResultCache.cs  HudCandidateFusion.cs  ⚠HudDetectionCoordinator.cs
⚠GeminiHudDetectionService.cs  AiHudFrameBuilder.cs  ⚠CropToolWindow.MagicWand.cs
⚠GeminiTrackingService.cs  AiTrackingPrompts.cs  AiGameProfile.cs  ⚠AiTrajectorySmoother.cs  CaptionModel.cs  CaptionTimeline.cs  CaptionLayoutEngine.cs  CaptionAssWriter.cs  CaptionTrackJson.cs  CommentaryPersonality.cs  AiCommentaryPrompts.cs  ⚠GranularSpeedEditorWindow.AiZoom.cs  ⚠GranularSpeedEditorWindow.AiCommentary.cs  ⚠GranularSpeedEditorWindow.CaptionsLane.cs  EditCaptionDialogWindow.axaml.cs
⚠MainWindow.axaml.cs  MainWindow.Canvas.cs  ⚠MainWindow.Controls.cs  MainWindow.Shortcuts.cs
MainWindow.Wireup.cs  ⚠MergeClipGraph.cs  ⚠MergedTimeline.cs  ⚠MergeEditorBridge.cs
⚠MergeEdl.cs  ⚠MergerPreviewPlan.cs  ⚠MergerSession.cs  ⚠MusicPadAlignment.cs
⚠MemePlacement.cs  ⚠GranularSpeedEditorWindow.Memes.cs  ⚠GranularSpeedEditorWindow.MemeBands.cs  ⚠MainWindow.Memes.cs  ⚠MusicWizardWindow.axaml.cs  ⚠OutputTimeline.cs  ⚠PhoneFrameMockup.axaml.cs  TimelineKnob.cs
TimelineLanesControl.axaml.cs  TimelineViewModel.cs  ⚠VideoMergerWindow.EdlPreview.cs
⚠GranularEditSession.Segments.cs  ⚠GranularEditSession.Memes.cs  ⚠CropEditSession.cs  ⚠CropProfileCodec.cs
⚠VideoMergerWindow.History.cs  ⚠VideoMergerWindow.Lanes.cs  ⚠VideoMergerWindow.Session.cs
⚠VideoMergerWindow.Timeline.cs  ⚠VideoMergerWindow.TimelineSelect.cs  ⚠VoiceOverWindow.axaml.cs  ⚠VoiceOverWindow.RecordingState.cs
```

**[`02_AUDIO_ENGINE_MASTERING.md`](02_AUDIO_ENGINE_MASTERING.md)** — Audio engine & mastering — PID preview volume, LUFS targets, sidechain ducking, Voice Over Studio, WASAPI threading, music bed fades.

```
AudioFilterChain.cs  AudioLoudnessProbe.cs  ⚠FluidVolumeSlider.cs  ⚠MainWindow.axaml.cs
⚠MainWindow.Controls.cs  ⚠MemeLoudness.cs  MicLevelMonitor.cs  MicrophoneSpectrumAnalyzer.cs  ⚠MicrophoneSpectrumControl.cs  MpvIpcClient.cs
⚠MusicPadAlignment.cs  ⚠MusicWizardWindow.axaml.cs  VoiceOverPreviewPlayer.cs
⚠VoiceOverWindow.axaml.cs  ⚠VoiceOverWindow.RecordingState.cs  VoiceRecorder.cs  VoiceCaptureSession.cs  LiveTakeMeter.cs  WavAudioReader.cs
WaveformGenerator.cs  RenderedMixGate.cs  ⚠VoiceOverWindow.PreviewProtection.cs
```

**[`03_FFMPEG_EXPORT_PIPELINE.md`](03_FFMPEG_EXPORT_PIPELINE.md)** — FFmpeg export pipeline — encoder discovery, zoom filtergraph, concat/bitrate, meme concat & cutaway preview, fades, progress, binary paths, meme library.

```
AudioTempoFilterBuilder.cs  CornerMemeOverlayGraph.cs  ⚠CompositeTimeline.cs  EncoderManager.cs  ExportColorPolicy.cs  ExportTimingTag.cs
⚠ExportCoordinator.cs  ⚠IExportCoordinator.cs  ExportViewModel.cs  FfmpegDiagnosticCollector.cs  ⚠FfmpegJobLifetime.cs  FramePtsProbe.cs
GpuCapabilityProbe.cs  GranularSpeedBuilder.cs  MemeTransitions.cs  ⚠MemePlacementPreview.cs  HardwareCapability.cs  HardwareScanner.cs  IntroTag.cs
MainWindow.SizeEstimate.cs  MemeAssets.cs  MemeCatalog.cs  ⚠MemeLoudness.cs  MemePreviewDirector.cs
MergeClipAnalyzer.cs  ⚠MergeClipGraph.cs  ⚠MergedTimeline.cs  ⚠MergeEditorBridge.cs  OutputFileNaming.cs
⚠MergeEdl.cs  ⚠MergerPreviewPlan.cs  MergerWorker.cs  MobileFilterBuilder.cs
⚠MusicPadAlignment.cs  OutputFileSize.cs  OutputSizeEstimator.cs  ProcessWorker.cs
QualityLadder.cs  TextOverlayGenerator.cs  TwoPassEncoding.cs  ⚠VideoMergerWindow.EdlPreview.cs
⚠VideoMergerWindow.Timeline.cs  ZoomPreviewSimulator.cs  ⚠PreviewFidelity.cs  ⚠MainWindow.PreviewFidelity.cs
```

**[`04_UI_UX_AVALONIA_SPEC.md`](04_UI_UX_AVALONIA_SPEC.md)** — UI/UX & Avalonia — design tokens, high-DPI layout, tooltips, confirmations, coach tours, granular editor layout, undo/redo, detachable previews, merger queue.

```
AmbientBubblesBackground.cs  AvaloniaApp.axaml  CoachOverlay.cs  ConfirmDialogWindow.axaml.cs
FloatingNotice.cs  ⚠FluidVolumeSlider.cs  GrabCursors.cs  ⚠GranularSpeedEditorWindow.axaml.cs
⚠GranularSpeedEditorWindow.History.cs  ⚠GranularSpeedEditorWindow.Merge.cs  ⚠IUserNotifier.cs
⚠LaneDiskCache.cs  MainViewModel.cs  ⚠MainWindow.axaml.cs  ⚠MainWindow.Controls.cs  MemePickerWindow.axaml
MemeThumbnailCache.cs  MemePickerPrefsStore.cs  ⚠MemePlacementPreview.cs  OwnedLruCache.cs  ViewportThumbnailLoader.cs  ⚠MemeChoiceViewModel.cs  ⚠CornerMemeOverlayPresenter.cs  ⚠GranularSpeedEditorWindow.Memes.cs
⚠MainWindow.CornerMemes.cs  ⚠VideoMergerWindow.CornerMemes.cs  ⚠MicrophoneSpectrumControl.cs  PreviewFidelityBadge.cs  ⚠PreviewFidelity.cs  ⚠MainWindow.PreviewFidelity.cs  ⚠PhoneFrameMockup.axaml.cs  PreviewDetachController.cs  PreviewMonitorWindow.axaml
ProgressiveLanes.cs  SettingsWindow.axaml.cs  SettingsWindow.Output.cs  SettingsWindow.Tools.cs  ⚠SettingsViewModel.cs  SpinningWheelSlider.cs  UpdateToastWindow.axaml.cs  AiCommentaryDialogWindow.axaml(.cs)  AiCommentaryOptionsViewModel.cs  EditCaptionDialogWindow.axaml(.cs)  GranularSpeedEditorWindow.CaptionsLane.cs  GranularSpeedEditorWindow.AiCommentary.cs
⚠VideoMergerWindow.EdlPreview.cs  ⚠VideoMergerWindow.Lanes.cs  VideoMergerWindow.Playhead.cs
⚠VideoMergerWindow.Session.cs  ⚠VideoMergerWindow.TimelineSelect.cs  ViewModelBase.cs  WaveformPeaks.cs
⚠WindowBoundsHelper.cs  WindowResizeGrip.cs  ⚠GranularEditSession.cs  ⚠CropEditSession.cs  CropLayer.cs
```

**[`05_SYSTEM_LIFECYCLE_STORAGE.md`](05_SYSTEM_LIFECYCLE_STORAGE.md)** — System lifecycle & storage — mutexes, logging pipeline, window bounds, deferred-close contract, crash recovery, atomic writes, dev build harness & fix sentinels, signing.

```
AppDataPaths.cs  AppDataPathsTests.cs  LegacyAppDataNames.txt  LegacyResidueSweep.cs  RebrandTests.cs
⚠ApplicationPaths.cs  ⚠AtomicJsonFile.cs  ⚠Build.cmd  dev_build.cmd  ⚠CodeSigning.cs  CooperativeShutdownGate.cs
CoreLogger.cs  CrashLogDigest.cs  CropConfigDefaults.cs  CropConfigStore.cs  DeploymentLifecycle.cs
⚠dev.cmd  DiskSpaceGuard.cs  FvsBuild/Program.cs  ⚠GranularSpeedEditorWindow.axaml.cs
IpcProtocol.cs  ⚠LaneDiskCache.cs  LatestEstimateWorker.cs  ⚠MainWindow.ToolReturn.cs
MaskOverlayManager.cs  MergerAutosaveStore.cs  ⚠MergerSession.cs  NamedPipeStateClient.cs
NamedPipeStateServer.cs  ProjectRecoveryService.cs  ⚠RecoveryManager.cs  RuntimeLog.cs
SettingsManager.cs  SingleInstanceGuard.cs  StateTransferStore.cs  ⚠StorageProviderFilePicker.cs  ⚠ToolNavigator.cs
GeminiClient.cs  IAiVisionProvider.cs  GeminiProvider.cs  ClaudeProvider.cs  OpenAiProvider.cs  AiVisionProviders.cs  AiFrameSequenceExtractor.cs  CommentaryService.cs  SecretProtector.cs  SecretLeakTests.cs
UiStateStore.cs  ⚠UpdateService.cs  ⚠VideoMergerWindow.Session.cs  ⚠WindowBoundsHelper.cs
GranularRecoveryCodec.cs  GranularJsonRead.cs  ⚠CropProfileCodec.cs  ⚠CropEditSession.cs
```

**[`06_PROJECT_DOCUMENT_MODEL.md`](06_PROJECT_DOCUMENT_MODEL.md)** — Project document model — the saveable `.fvsproj`, schema versioning & the amputation rule, AOT-safe JSON, atomic persistence & backup, source integrity, recent projects, trim/AOT analyser policy.

```
AotJson.cs  ⚠AtomicJsonFile.cs  FreeVideoStudio.App.csproj  ⚠IProjectStore.cs
⚠MainWindow.Project.cs  ⚠MergeEdl.cs  ⚠OutputTimeline.cs  ⚠ProjectDocument.cs  ProjectSerializer.cs  MemePresentationJson.cs  CaptionTrackJson.cs
⚠ProjectSession.cs  ProjectStore.cs  RecentProjects.cs  ⚠VideoMergerWindow.History.cs
```

**[`07_UNDO_AND_HISTORY.md`](07_UNDO_AND_HISTORY.md)** — Undo, redo & edit history — the four inherited rules (gesture coalescing, ceiling on push, redo invalidation, no-op rejection), immutable state contract, re-entrancy guard, persistence.

```
⚠GranularSpeedEditorWindow.axaml.cs  ⚠GranularSpeedEditorWindow.History.cs  ⚠GranularSpeedEditorWindow.AiCommentary.cs  ⚠GranularSpeedEditorWindow.CaptionsLane.cs  ⚠MainWindow.Project.cs
⚠ProjectDocument.cs  ⚠ProjectSession.cs  UndoSidecarStore.cs  UndoStack.cs
⚠GranularEditSession.cs  GranularEditSession.History.cs  ⚠CropEditSession.cs  CropLayer.cs
```

**[`08_APPLICATION_COMPOSITION.md`](08_APPLICATION_COMPOSITION.md)** — Application composition, seams & fault reporting — the composition root, the service interfaces, fault tiers (Recoverable/Degraded/Fatal), and the architecture tests that enforce the other specs' rules.

```
AppServices.cs  ArchitectureRuleTests.cs  ⚠CodeSigning.cs  ⚠dev.cmd  Fault.cs  FaultCounters.cs
⚠ExportCoordinator.cs  Faults.cs  ⚠FfmpegJobLifetime.cs  ⚠.github/workflows/ci.yml  IClock.cs  IFaultSink.cs
IFilePickerService.cs  ⚠IProjectStore.cs  ⚠IUserNotifier.cs  ⚠MainWindow.Project.cs
⚠MainWindow.ToolReturn.cs  ⚠ProjectSession.cs  ⚠StorageProviderFilePicker.cs  ⚠ToolNavigator.cs
UserFacingFaultSink.cs  WindowsOnlyFactAttribute.cs  ⚠GranularEditSession.cs
```

**[`09_DISTRIBUTION_AND_RELEASE.md`](09_DISTRIBUTION_AND_RELEASE.md)** — Distribution, update size & repository weight — the 322MB installer, the runtime/app package split, the fingerprint that decides which one a patch downloads, LFS enforcement and the history-rewrite runbook.

```
⚠Build.cmd  CHANGELOG.md  dev_build.cmd  .gitattributes  ⚠.github/workflows/ci.yml  .github/workflows/lfs-guard.yml
GitHubReleasePublisher.cs  RuntimePayloadManifest.cs  Staging.cs  ⚠UpdateService.cs
```

**[`REBRAND_MIGRATION.md`](REBRAND_MIGRATION.md)** — Rebrand record — old→new identity map, old-brand update flow, residue removal, automatic output names, verification.

---
## 4. Agent Navigation & Entry Protocol
1. **Entry Rule:** Always read [`SPEC_GOVERNANCE.md`](SPEC_GOVERNANCE.md) before performing any code generation or inspection.
2. **Context Routing:** Know the FILE -> use §3 above. Know only a SYMBOL, CONSTANT or TAG (e.g. `SnapInsertionPoint`, `SafetyCeilingDbtp`, `ZOOMLIVE_07`) -> grep [`INDEX.md`](INDEX.md). Read the ONE spec you land on; do not pre-load the others.
3. **Co-Governed Files (⚠):** A file listed under more than one spec is bound by ALL of them. Reading one is NOT compliance — this is the exact leakage `SPEC_GOVERNANCE.md` §2 exists to prevent.
4. **Cite Anchors, Not Numbers:** Quote the stable `{#ANCHOR}` id (e.g. `FFM-BINPATH`) in the Proof-of-Read header. Section numbers shift as specs grow.
5. **Land The Sentinel With The Fix:** Any bug fix or safety invariant guarded mechanically gets a `TAG=path` line in `build/sentinels.txt` in the SAME change (checked by `build/FvsVerify`, which `dev.cmd`'s `VERIFY_PATCHES` runs, and by `ArchitectureRuleTests.EveryFixSentinelStillResolves` in CI). Distinguish mechanical fix-sentinels (`build/sentinels.txt`) from spec section anchors (`docs/INDEX.md` `{#ANCHOR}` tags): mechanical sentinels fail compilation/verification if their in-code token is deleted, while spec anchors route architectural requirements. That section also states why a correct source file is not evidence that the running binary contains the fix.

---
## 5. Repository Map  {#GOV-REPOMAP}

Moved verbatim from `project_structure.txt` (ONBOARD_01), which now only points to this file. Keep it in
step with the code (§0.3 rule 3). Paths are relative to the repository root.

```text
Solution:             .\FreeVideoStudio.sln
Application project:  .\src\FreeVideoStudio.App\FreeVideoStudio.App.csproj
Core project:         .\src\FreeVideoStudio.Core\FreeVideoStudio.Core.csproj
Application tests:    .\tests\FreeVideoStudio.App.Tests\FreeVideoStudio.App.Tests.csproj
Core tests:           .\tests\FreeVideoStudio.Core.Tests\FreeVideoStudio.Core.Tests.csproj
Local packaging:      .\dev_build.cmd
Verification tool:    .\build\FvsVerify\FvsVerify.csproj
Sentinel registry:     .\build\sentinels.txt
Standalone output:    .\compiled\FreeVideoStudio-Setup.exe  (Inno Setup package, NODROPPER_01; holds exactly one local installer; GitHub retains latest 5 releases)
Installer script:     .\build\installer\FreeVideoStudio.iss  (docs/09_DISTRIBUTION_AND_RELEASE.md, DIST-INSTALLER)
AV hardening:         NODROPPER_01, NOTEMPEXEC_01, PEIDENTITY_01, UPDATECONSENT_01 (docs/05 SYS-AVCLEAN)
Release gate:         .\build\FvsBuild\ReleaseGate.cs (RELGATE_01: clean + pushed + linted notes + typed PUBLISH,
                      version commit + tag, undo on failure) -> GitHubReleasePublisher.cs (--verify-tag, RETAIN_01,
                      BETA_01 pre-releases). Spec: docs/09 DIST-RELEASEGATE.
Release notes:        .\docs\changes.md (technical, '## Unreleased') -> .\docs\CHANGELOG.md (users, '## Upcoming');
                      parser/lint/promotion .\src\FreeVideoStudio.Core\Infrastructure\ReleaseChangelog.cs
                      (RELNOTES_01..03). Spec: docs/09 DIST-RELEASENOTES.
Update trust:         elevated worker runs only from a protected %ProgramData% copy:
                      .\src\FreeVideoStudio.App\Services\ProtectedWorkerStage.cs (WORKERSTAGE_01, docs/05
                      SYS-UPGRADE-TRUST); Debug-only unsigned override (UNSIGNEDDEV_01); asset hashes + capped
                      decompression (ASSETHASH_01); per-drive space check (UPDATESPACE_01); published patch
                      bases (DELTABASE_01); 6-hourly re-check (RECHECK_01). Tests: ReleaseHardeningTests.cs,
                      ReleaseChangelogTests.cs.

Product display name: Free Video Studio
Naming rule:          "Free Video Studio" = user-visible text (title bars, About, dialogs,
                      shortcuts, install folder). "FreeVideoStudio" = files, folders,
                      projects, namespaces, mutexes, registry/IDs.
Application assembly: FreeVideoStudio (root namespace: FreeVideoStudio.App)
Core assembly:        FreeVideoStudio.Core (root namespace: FreeVideoStudio.Core)
Internal tooling retains FvsBuild, FvsVerify and the FVS environment variables.
GitHub repository:    alonreich/Free_Video_Studio (updater + meme sync; old address redirects)
Rebrand record:       docs/REBRAND_MIGRATION.md (old->new map, update flow, output naming)
Previous-brand guard: tests\FreeVideoStudio.Core.Tests\RebrandTests.cs (old name allowed ONLY in
                      LegacyAppDataNames.txt and docs/REBRAND_MIGRATION.md)
Legacy identity:      .\src\FreeVideoStudio.Core\Infrastructure\LegacyAppDataNames.txt (migration only)
Storage migration:    docs/05_SYSTEM_LIFECYCLE_STORAGE.md, SYS-REBRAND
Residue removal:      docs/05_SYSTEM_LIFECYCLE_STORAGE.md, SYS-REBRAND-SWEEP (REBRAND_02,
                      .\src\FreeVideoStudio.Core\Infrastructure\LegacyResidueSweep.cs)
Packaging identity:   docs/09_DISTRIBUTION_AND_RELEASE.md, DIST-IDENTITY
Old-client alias:     docs/09_DISTRIBUTION_AND_RELEASE.md, DIST-SPLIT (REBRAND_03, created
                      transiently by .\build\FvsBuild\GitHubReleasePublisher.cs, never kept in obj\)
Output file names:    docs/03_FFMPEG_EXPORT_PIPELINE.md, FFM-OUTNAME (OUTNAME_01,
                      .\src\FreeVideoStudio.Core\Media\OutputFileNaming.cs)
                      Defaults: FreeVideoStudio-N.mp4 (Main App), Merged-Videos-N.mp4 (Merger);
                      rescued: FreeVideoStudio-RECOVERED-*, Merged-Videos-RECOVERED-*
Settings › Files & Folders: per-tool save folder + file name (settings schema v11:
                      MainOutputBaseName, MergerOutputBaseName), docs/04_UI_UX_AVALONIA_SPEC.md,
                      UI-SETTINGS-ABOUT (.\src\FreeVideoStudio.App\Controls\SettingsWindow.Output.cs)
New sentinels:        REBRAND_02, REBRAND_03, OUTNAME_01 (registered in .\build\sentinels.txt)
Meme library:         .\meme\ holds ALL memes (videos + pictures); .\mp4\ and .\jpeg\ are gone
                      (MEMEFOLDER_01). Cloud sync lists repo meme/ via git/trees (MEMESYNC_01..03);
                      starter set = .\src\FreeVideoStudio.App\Infrastructure\MemeAssets.cs StarterFiles
                      == .\build\FvsBuild\Staging.cs StarterMeme (STARTERLIST_01, MemeLibraryTests).
                      Spec: docs/03_FFMPEG_EXPORT_PIPELINE.md, FFM-MEMELIB.
Song library:         .\mp3\ holds background music tracks and companion attribution notes (.txt).
                      Downloadable library songs prefixed with '!_' or accompanied by .txt credentials
                      provide copyright attribution for YouTube/TikTok to prevent automatic audio muting
                      (ATTRIBUTION_01, MusicAttributionHelper.cs, MusicAttributionDialogWindow.axaml).
Folder names:         never contain spaces - %ProgramFiles%\FreeVideoStudio, Videos\FreeVideoStudio\Memes
                      (NOSPACE_01, MEMEFOLDER_02). Old spaced folders are migrated automatically.
                      Spec: docs/05_SYSTEM_LIFECYCLE_STORAGE.md, SYS-NOSPACE.
Add Music wizard:     .\src\FreeVideoStudio.App\MusicWizardWindow.axaml(.cs) + .Playlist.cs (song list, coverage)
                      + .SongLane.cs (song blocks on the music lane, song card) + .History.cs (undo)
                      + .AiAutoFit.cs (cloud AI song auto-fit: AI picks a song by NUMBER, start point measured
                      locally from the song's loudness, visible steps + seconds counter + cancel, 45 s / 120 s
                      limits — AIMUSIC_HONEST_01 / AIMUSIC_START_01 / AIMUSIC_PROGRESS_01) + .Scan.cs (folder scan & probes) +
                      ViewModels\MusicWizardViewModel.cs (bound state). Two steps: Choose music -> Fit & mix
                      (WIZ2STEP_01). One song list, every song with its chosen part (PLAYLIST_01, MUSICWIN_01),
                      laid out by Core\Media\MusicBedPlan.cs with the export's crossfade from
                      Core\Media\MusicCrossfade.cs (XFADEPLAN_01); pure list maths in Core\Media\MusicPlaylist.cs.
                      Saved windows: MusicWizardResult.Plan.cs (MusicStartsSeconds / MusicEndsSeconds, ToBedTracks).
                      Merger EDIT/REMOVE + named song blocks: VideoMergerWindow.MusicButton.cs (MERGERMUSIC_01/02).
                      Specs: docs/02 AUD-DIALOGS / AUD-MUSICWIN / AUD-MUSICFADE, docs/04 UI-MUSICWIZARD.
Meme picker:          .\src\FreeVideoStudio.App\Controls\MemePickerWindow.axaml(.cs) (MEMEPICK_01..05)
                      + Controls\MemePlacementPreview.cs (live preview, drag position) + ViewModels\MemeChoiceViewModel.cs
                      + Infrastructure\MemePickerPrefsStore.cs (favourites, recent, last choice); docs/04 UI-MEMESELECT.
Meme audit (2026-10-17, MEMEAUDIT_01): Core\Media\MemeTransitions.cs (full-screen cut/fade/zoom, corner fade/slide),
                      GranularSpeedEditorWindow.Memes.cs / .MemeBands.cs (editor memes, corner drag, list, keys),
                      MainWindow.Memes.cs (main-screen meme as a placement, markers, music question);
                      tests: Core MemeAuditTests.cs, App MemeAuditAppTests.cs. Specs: docs/01 TL-MEME, docs/03 FFM-MEMECONCAT /
                      FFM-MEMECORNER / FFM-MEMEPREVIEW, docs/04 UI-MEMESELECT, docs/06 PROJ-SCHEMA (schema 7).
AI features:          AI SMART ZOOM (Granular editor) + Magic Wand AI (Crop Tool) + AI Commentary (captions)
                      + AI Music Auto-Fit (Add Music wizard: Services\AiMusicAutoFitService.cs)
                      + Modern Viral Post Helper (Export finished screen: Services\AiViralPostService.cs, Controls\FinishedDialogWindow.AiViral.cs)
                      + AI Kill Tracker / Elimination Tracker (red ✕ badges over the timeline + result notice, also pushed into an
                      open speed editor; 1280-px stills, own kills only, 24-60 moments by length, honest "no sound" —
                      AIELIM_03: MainWindow.AiElimination.cs, Services\AiEliminationTrackerService.cs). Zero local models.
                      Multi-vendor transport: Google Gemini, Anthropic Claude, OpenAI (IAiVisionProvider.cs).
                      Schema translator for Claude/OpenAI: Services\AiJsonSchema.cs (AISCHEMA_01); standard JSON Schema
                      -> Gemini's upper-case dialect (AISCHEMA_02). Temperature rules per vendor (AITEMP_02).
                      AI helper FFmpeg jobs (stills, probe, PCM; timeout + kill on cancel): Services\AiFfmpeg.cs (AIFFMPEG_01).
                      Settings: ONE "AI Features" tab (the old "AI Providers" tab is nested in it); one default
                      AI for all features + per-feature overrides (AIDEFAULT_01, AppSettings.AiDefaultUsage / AiUsageOverrides).
                      Key sealed with DPAPI in settings.json (KEYPROTECT_01, SecretProtector.cs).
                      Plain-English map of every AI feature + 2026-10-16 audit closure: docs/05 SYS-AISETTINGS §7
                      (AIAUDIT_01). Defaults gemini-3.8-flash / claude-sonnet-5-5 / gpt-6-luna, retired names
                      migrated on load, one 404 fallback for all vendors (AIMODELS_02, AIMODELFALLBACK_02 in
                      Services\AiVisionProviders.cs). Commentary captions reach the export via ExportPayload.CaptionTrack
                      (AICAPTION_EXPORT_02). One "Connect Your AI" window for every feature (AISETUP_02).
                      Caption burn-in: Core\Media\CaptionLayoutEngine.cs + CaptionAssWriter.cs — 76/64 px text centred on
                      the 2/3 line, kept clear of the user's Crop Tool HUD (MobileFilterBuilder.HudCanvasRects), shifted
                      for spliced memes (AICAPTION_LAYOUT_02, AICAPTION_MEMESHIFT_01); one caption track in the preview
                      (AICAPTION_PREVIEWSUB_01). Commentary options window remembers tone, style, placement, language,
                      scope and notes (AICOMMENTARY_REMEMBER_01, AppSettings.AiCommentaryLast*) and shares the game type
                      with Settings (AIPROFILE_ONE_01). Cloud consents are per AI company (AICONSENT_02,
                      AppSettings.Ai*CloudConsentVendor).
                      Commentary options (COMMENTARYUI_02): basic = 4 one-click styles, Upper/Middle/Lower, language
                      (incl. Hebrew), range; Advanced (always closed) = 4 tone sliders, text look/size, how often,
                      swearing, animation, game type, notes. Brain: Services\CommentaryPersonality.cs (COMMENTARYMIX_01,
                      TACTICAL_01..03, SWEARING_01); bound state: ViewModels\AiCommentaryOptionsViewModel.cs; caption
                      look/size/Middle/Hebrew: Core\Media\CaptionAssWriter.cs + CaptionLayoutEngine.cs (CAPTIONLOOK_01,
                      CAPTIONSIZE_01, CAPTIONPLACE_01, CAPTIONRTL_01); right-click "Rewrite this line" (REWRITELINE_01).
                      Per-task AI usage report (tokens + allowance before/after) for Smart Zoom and Commentary:
                      Services\AiUsageMeter.cs (AIUSAGE_01, docs/05 SYS-AISETTINGS §7.5).
                      Tests: tests\FreeVideoStudio.App.Tests\AiAuditFixTests.cs, tests\FreeVideoStudio.App.Tests\AiUsageMeterTests.cs,
                      tests\FreeVideoStudio.App.Tests\CommentaryPersonalityTests.cs, tests\FreeVideoStudio.Core.Tests\CaptionAuditFixTests.cs,
                      tests\FreeVideoStudio.Core.Tests\CaptionLookTests.cs.
                      Specs: docs/01 TL-AITRACKING / TL-AIHUD / TL-CAPTIONS, docs/02 AUD-MUSICWIN, docs/03 FFM-CAPTIONS,
                      docs/04 UI-AIZOOM / UI-AIHUDWAND / UI-AICOMMENTARY / UI-MUSICWIZARD / UI-MERGER-MULTISELECT,
                      docs/05 SYS-AISETTINGS. Secret guard: tests\FreeVideoStudio.App.Tests\SecretLeakTests.cs
                      (SECRETSCAN_01) — never commit keys, .pfx, *.key.pem or *.password.txt.
```
