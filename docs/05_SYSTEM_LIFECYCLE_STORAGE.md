# SPECIFICATION 05: SYSTEM LIFECYCLE & STORAGE

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FreeVideoStudio.App/DeploymentLifecycle.cs` | `DeploymentLifecycle` | `RunUninstallWorkerAsync`, `ShouldHandle`, `RunAsync`, `QueueSelfCleanup`, `NOTEMPEXEC_01`, `NOSELFDELETE_01` | OS installation/uninstallation installer-gate semaphore and elevated install/uninstall workers (the uninstall worker runs in place from the program folder). |
| `src/FreeVideoStudio.App/DeploymentFootprint.cs` | `DeploymentFootprint` | `UpdatesRoot`, `UpdateDownloadsRoot`, `UpdateStageRoot`, `LegacyUpdateDownloadsRoot`, `LegacyUpdateStageRoot`, `GetDirectoryPurgeTargets`, `NOTEMPEXEC_01` | Install, data, update-scratch and legacy paths; what the uninstaller removes. |
| `src/FreeVideoStudio.App/RuntimeLog.cs` | `RuntimeLog` | `LogMutexName`, `InitializeAppName`, `ResetForProcess`, `Info` | Decoupled asynchronous producer-consumer logging pipeline. |
| `src/FreeVideoStudio.Core/Infrastructure/CoreLogger.cs` | `CoreLogger` | `InfoAction`, `FailAction`, `Warn`, `Swallowed` | Core-side logging facade; the App wires its actions to `RuntimeLog` at startup. |
| `src/FreeVideoStudio.Core/Infrastructure/RecoveryManager.cs` | `RecoveryManager` | `SaveState`, `LoadState`, `CheckFault`, `IsSafeModeActive` | Continuous project session serialization, crash detection, and safe-mode recovery. **⚠ CO-GOVERNED BY: GOV**|
| `src/FreeVideoStudio.Core/Infrastructure/AtomicJsonFile.cs` | `AtomicJsonFile` | `WriteObject`, `WriteText`, `WriteBytes`, `WriteCore`, `ReadObject`, `ATOMICTEXT_01`, `ATOMICBYTES_01` | Thread-safe, power-outage-safe atomic JSON (and opaque byte) file writing and parsing. |
| `src/FreeVideoStudio.App/Infrastructure/SettingsManager.cs` | `SettingsManager` | `Update(Action<AppSettings>)`, `SetAutoUpdateChecks`, `SetUpdateConsent`, `UpdateConsentAsked`, `UPDATECONSENT_01`, `Committed`, `Load`, `SettingsMutexName`, `SerializeGate`, `CurrentSchemaVersion` (13), `SETTINGSATOMIC_01`, `SETTX_01`, `SETTX_02` | Cross-process settings persistence under a named mutex and the atomic write protocol; the ONLY settings mutation path is `Update`. |
| `src/FreeVideoStudio.App/Services/GeminiTrackingService.cs` | `GeminiTrackingService` | `PostWithRetryAsync`, `TestApiKeyAsync` | Transient retry loop with exponential backoff and API key sanitization in logs. **⚠ CO-GOVERNED BY: 01, 03, 04**|
| `src/FreeVideoStudio.Core/Ipc/NamedPipeStateServer.cs` | `NamedPipeStateServer` | `ScheduleDiskFlush`, `FlushToDiskSafe`, `IPCLEASE_01`, `IPCTEARDOWN_01`, `IPCOWNER_01` | In-memory session state server, bounded flush scheduling and ordered teardown. |
| `src/FreeVideoStudio.Core/Ipc/NamedPipeStateClient.cs` | `NamedPipeStateClient` | `GetStateAsync`, `SaveStateAsync`, `UpdatePropertiesAsync`, `SendHandoffAsync`, `FastProbeTimeout`, `DefaultTimeout`, `IPCDEADLINE_01` | IPC client communicating with the running session state server. Async only; every exchange is bounded end to end. |
| `src/FreeVideoStudio.Core/Ipc/SessionStateWriter.cs` | `SessionStateWriter` | `For`, `Enqueue`, `FlushAsync`, `FlushAllAsync`, `ReadSnapshot`, `Prime`, `PrimeAsync`, `CommitFailed`, `UIIPC_01` | The only UI path to session state: coalesced, ordered, latest-wins background commits; memory-only reads. |
| `src/FreeVideoStudio.Core/Ipc/StateTransferStore.cs` | `StateTransferStore` | `LoadAsync`, `SaveAsync`, `UpdatePropertiesAsync`, `SendHandoffAsync`, `ClearAsync`, `SchemaVersion` (1), `DeliveryBudget`, `IPCOWNER_01` (no `*Sync` members, UIIPC_01) | Cross-process state persistence and named mutex coordination. |
| `tests/FreeVideoStudio.Core.Tests/IpcTests.cs` | `IpcTests` | `IpcOwner_*`, `IpcServer_ConcurrentFlushes_DiskEndsAtTheLatestState` | IPC framing, ownership (IPCOWNER_01) and write-order (WRITEORDER_02) regression coverage. |
| `tests/FreeVideoStudio.Core.Tests/IpcChildProcess.cs` | `IpcChildProcess` | `Main` (`--ipc-child update`) | Test-assembly entry point that runs real cross-process IPC clients for `IpcTests`. |
| `src/FreeVideoStudio.Core/Ipc/IpcProtocol.cs` | `IpcProtocol`, `IpcFrame`, `IpcOpcode` | `WriteFrameAsync`, `ReadFrameAsync`, `ExchangeAsync` (IPCDEADLINE_01), `PipeName`, `ServerMutexName` | Wire framing protocol and serialization context for IPC pipes. |
| `src/FreeVideoStudio.Core/Ipc/CropConfigDefaults.cs` | `CropConfigDefaults` | `Create`, `CreateNoMask`, `SchemaVersion` (4), `MinimumUsableSchemaVersion` (3) | Factory for default HUD crop profiles and schema version definitions. |
| `src/FreeVideoStudio.Core/Infrastructure/CooperativeShutdownGate.cs` | `CooperativeShutdownGate` | `BeginShutdown`, `RegisterTask`, `IsShutdown` | Coordinated graceful process and thread pool task shutdown gate. |
| `src/FreeVideoStudio.Core/Infrastructure/DiskSpaceGuard.cs` | `DiskSpaceGuard` | `CheckFreeSpaceBytes`, `MinimumFreeBytes` | Pre-render disk space verification preventing corrupt half-written files. |
| `src/FreeVideoStudio.App/CrashLogDigest.cs` | `CrashLogDigest` | `RunAsync`, `DigestLatestCrash` | Background crash report analyzer and automated log summary generator. |
| `src/FreeVideoStudio.App/SingleInstanceGuard.cs` | `SingleInstanceGuard` | `TryAcquire`, `Release`, `AppliesTo` | Single application instance enforcement and focus delegation via named mutex. |
| `src/FreeVideoStudio.Core/Infrastructure/AppDataPaths.cs` | `AppDataPaths` | `AppDataDir`, `LocalCacheDir`, `MigrateDirectory`, `REBRAND_01` | Copy-only rebrand migration before default state initialization. |
| `src/FreeVideoStudio.Core/Infrastructure/LegacyAppDataNames.txt` | Embedded legacy directory identities | `LegacyAppDataNames.txt` | Historical names retained only as migration data. |
| `src/FreeVideoStudio.Core/Infrastructure/LegacyResidueSweep.cs` | `LegacyResidueSweep` | `RunForCurrentUser`, `Run`, `LegacyTempNames`, `LegacyStorageNames`, `REBRAND_02` | Removes previous-brand temp/user folders once provably migrated. |
| `tests/FreeVideoStudio.Core.Tests/RebrandTests.cs` | `RebrandTests` | sweep, naming and brand-allow-list tests | Rebrand residue and output-name regression coverage. |
| `tests/FreeVideoStudio.Core.Tests/AppDataPathsTests.cs` | `AppDataPathsTests` | migration, retry, collision and existing-destination tests | Rebrand data-preservation regression coverage. |
| `src/FreeVideoStudio.Core/Infrastructure/ApplicationPaths.cs` | `ApplicationPaths` | `ProgramDataRoot`, `DefaultUserRoot`, `RecoveryStateFile`, `SessionStateFile`, `MergerSessionFile`, `LaneCacheDirectory`, `EnsureWritableDirectories` | System directory resolution, temp workspace paths, and sentinel lock files. **⚠ CO-GOVERNED BY: GOV**|
| `src/FreeVideoStudio.Core/Infrastructure/UiStateStore.cs` | `UiStateStore` | `ReadInt`, `WriteInt`, `MigrateLegacyFilesOnce`, `ReadText` | Lightweight persistent key-value configuration and coach tour launch counts. |
| `src/FreeVideoStudio.App/WindowBoundsHelper.cs` | `WindowBoundsHelper` | `Track`, `SaveBoundsOnClose` (UIIPC_01, replaced `SaveBoundsSync`), `SaveBoundsAsync`, `CloseFlushBudget`, `Capture` | Multi-display window geometry tracking and per-screen bounds persistence. **⚠ CO-GOVERNED BY: 04**|
| `src/FreeVideoStudio.App/GranularSpeedEditorWindow.axaml.cs` | `GranularSpeedEditorWindow` | `OnClosing`, `OnClosed`, `_isSafeToClose`, `ResultSegments` | Deferred-close dispatcher contract governing dialog resolution and edit hand-off. **⚠ CO-GOVERNED BY: 01, 04**|
| `src/FreeVideoStudio.App/Infrastructure/MaskOverlayManager.cs` | `MaskOverlayManager` | `ApplyProfile`, `EnsureDefaults`, `IsNoMask`, `SanitizeProfileName` | HUD profile configuration and first-run crop defaults. |
| `src/FreeVideoStudio.Core/Ipc/CropConfigStore.cs` | `CropConfigStore` | `LoadAsync`, `SaveAsync`, `RotateBackupsUnlocked`, `IsUsableConfig` | Crop configuration persistence and the 5-tier `.bak` rotation cascade. |
| `src/FreeVideoStudio.Core/Editing/CropProfileCodec.cs` | `CropProfileCodec`, `CropConfigJson` | `ReadSavedLayers`, `WriteLayers` (crops_source drift trap, RESGUESS_01 position-only, DELETESET_01 tombstones) | The Crop Tool's layers as the crop-config document and back; pure, the window keeps the store I/O (EDITSTATE_01). **⚠ CO-GOVERNED BY: 01**|
| `src/FreeVideoStudio.Core/Editing/GranularRecoveryCodec.cs` | `GranularRecoveryCodec`, `GranularJsonRead` | `Build`, `TryApply`, `Key = "granular_session"`, `SchemaVersion = 1` | RECOVERY_03 values of the Granular editor's live session; identity = video path + trim window; merge mode never restores (EDITSTATE_01). |
| `src/FreeVideoStudio.App/Services/ProjectRecoveryService.cs` | `ProjectRecoveryService`, `RecoverySnapshot` | `WireDocumentSource`, `SaveState` (canonical `ProjectDocument` + transient), `SaveCurrentState`, `LoadSnapshot`, `HasUnsavedWork`, `ClearState` | Recovery BRIDGE over the canonical document (RECOVERYDOC): one envelope, no parallel project schema. |
| `src/FreeVideoStudio.App/Services/LatestEstimateWorker.cs` | `LatestEstimateWorker` | `Request`, `RunAsync`, `Dispose`, `Completion` | Bounded background estimates, cancellation and stale UI result rejection. |
| `src/FreeVideoStudio.App/Services/UpdateService.cs` | `UpdateService` | `RunStartupCheckAsync`, `AskUpdateConsentAsync`, `StartupGracePeriod`, `SetupAssetName`, `UPDATECONSENT_01`, `NODROPPER_01`, `CheckManualAsync`, `GetSkippedVersion`, `ClearSkippedVersion`, `DescribeLastCheck`, `DescribeDownloadProgress`, `RestartToUpdate`, `UPDATEUX_01`..`06` | Background GitHub release query, 15-minute startup throttle, SHA-256 verification, and quiet updater. |
| `src/FreeVideoStudio.App/Services/UpdateService.Background.cs` | `UpdateService` (partial) | `StartBackgroundUpdate`, `LaunchInstaller`, `SkipPendingVersion`, `PendingVersion`, `BGUPDATE_01` | Background download + the update card (R2/R3). **⚠ CO-GOVERNED BY: 09** |
| `src/FreeVideoStudio.App/Services/UpdatePreparer.cs` | `UpdatePreparer`, `PreparedUpdate` | `PrepareAsync`, `DIST-SMART` | Smart → compact → full: downloads as little as possible, verifies everything. **⚠ CO-GOVERNED BY: 09** |
| `src/FreeVideoStudio.App/Services/UpdateDownloader.cs` | `UpdateDownloader` | `DownloadAsync`, `DLRESUME_01` | Resumable, retried, hash-checked downloads. **⚠ CO-GOVERNED BY: 09** |
| `src/FreeVideoStudio.Core/Infrastructure/PublisherRoot.cs` | `PublisherRoot` | `Vouches`, `Sha256Thumbprint`, `UPDATEPIN_01` | The publisher root compiled into every copy. |
| `src/FreeVideoStudio.Core/Infrastructure/HardLink.cs` | `HardLink` | `TryCreate`, `FASTINSTALL_01` | Instant reuse of unchanged installed files during an install. |
| `src/FreeVideoStudio.App/Services/UpgradeBrokerHost.cs` | `UpgradeBrokerHost` | `RunAsync`, `Attach`, `IsActive`, `UPGRADEUX_01` | Runs the upgrade broker with a progress window; headless message-box fallback. |
| `src/FreeVideoStudio.App/Services/UpgradeProgress.cs` | `IUpgradeProgress`, `HeadlessUpgradeProgress`, `UpgradeText`, `UpgradeStep`, `UpgradeKind` | `Step`, `AskToCloseRunningAppAsync`, `Succeeded`, `FailedAsync` | Display-only progress contract of the install/update broker and all its wording. |
| `src/FreeVideoStudio.App/UpgradeProgressWindow.axaml.cs` | `UpgradeProgressWindow` | `AllowClose`, `OnClosing` | The install/update checklist window (UPGRADEUX_01). |
| `src/FreeVideoStudio.App/ViewModels/UpgradeProgressViewModel.cs` | `UpgradeProgressViewModel`, `UpgradeStepItem` | `SetSteps`, `MoveTo`, `Ask`, `Succeed`, `Fail` | State of the checklist window. |
| `src/FreeVideoStudio.App/Services/UpgradeFinishedNotice.cs` | `UpgradeFinishedNotice` | `Attach`, `Describe`, `KindArgument`, `FromArgument`, `UPGRADEUX_05` | First launch after install/update: "Finishing…" then "Updated from X to Y". |
| `build/FvsBuild/Program.cs` | `Program` | `SynchronizeVersionFiles`, `RunPipeline`, `RunNativeAotStages`, `PEIDENTITY_01` | Unified build pipeline synchronizing version.txt, Directory.Build.props, project files and app.manifest. |
| `build/FvsBuild/PeIdentity.cs` | `PeIdentity` | `FixOriginalFilename`, `Patch`, `PEIDENTITY_01` | OriginalFilename/InternalName of the shipped program = its file name. **⚠ CO-GOVERNED BY: 09** |
| `build/FvsBuild/Staging.cs` | `Staging` | `Publish`, `RunPublish`, `StageDependencies` | Build output staging, packaging, and publish validation. |
| `build/FvsBuild/CodeSigning.cs` | `CodeSigning` | `SignIfNeeded`, `FVS_SIGN_PFX`, `FVS_SIGN_PASS`, `FVS_ALLOW_UNSIGNED` | Mandatory Authenticode digital signing of the release executable. **⚠ CO-GOVERNED BY: 08**|
| `Build.cmd` | Production Release Script | `dotnet run build\FvsBuild` | **STRICT AGENT BAN**: Thin entry-point triggering production release, git tagging, and live GitHub Cloud release deployment. Never execute by autonomous agents. **⚠ CO-GOVERNED BY: 09, GOV** |
| `dev_build.cmd` | Dev Compile Harness | `dotnet run build\FvsBuild -- --dev` | Safe local-only build and NativeAOT publish compilation test harness. Zero git tags, zero GitHub Cloud publishing. Mandatory for agent build tests. **⚠ CO-GOVERNED BY: 09, GOV** |
| `dev.cmd` | Developer Harness | `VERIFY_PATCHES`, `build/FvsVerify`, `NUKE_BUILD`, `KILL_STALE`, `TRACE`, `FVS_DEV_LOG_DIR`, `FVS_PROGRAMDATA_ROOT` | Sandboxed dev launch, stale-process purge, cache nuke, pre-build fix verification, and in-repo trace logging. |
| `src/FreeVideoStudio.Core/Infrastructure/MergerAutosaveStore.cs` | `MergerAutosaveStore` | `Schedule`, `FlushAsync`, `Clear`, `Load`, `DefaultDebounce` | Video Merger edit-list autosave (`merger_session.json`, MERGESESSION_01). |
| `src/FreeVideoStudio.Core/Media/MergerSession.cs` | `MergerSession`, `ClipIdList` | `Capture`, `Plan`, `DescribeRestoreProblems`, `SyncQueue` | Merger session capture and restore planning (MERGESESSION_01, RESTOREMISS_01). **⚠ CO-GOVERNED BY: 01**|
| `src/FreeVideoStudio.App/MainWindow.ToolReturn.cs` | `MainWindow` | `RestoreVideoPipelineAfterTool`, `StartVideoHostAsync`, `BindFallbackBadge`, `TOOLRETURN_01` | Main App preview revival after a companion tool closes. **⚠ CO-GOVERNED BY: 08**|
| `src/FreeVideoStudio.App/Infrastructure/LaneDiskCache.cs` | `LaneDiskCache`, `LaneDiskStore` | `PathFor`, `TryRead`, `Write`, `MaxFiles` (800), `MaxBytes` (512 MiB), `WhenIdleAsync` | Best-effort on-disk lane cache under `ApplicationPaths.LaneCacheDirectory`; atomic byte writes, continuous serialized LRU prune (CACHEBOUND_01, §4c). **⚠ CO-GOVERNED BY: 04**|

---

## 1. Concurrency & System Mutex Locks  {#SYS-MUTEX}
* **Deployment Installer Gate:** `DeploymentLifecycle` gates the elevated install/upgrade worker (`RunInstallAsync`, dispatched by `RunAsync`) and the uninstall worker (`RunUninstallWorkerAsync`) on a named machine-wide semaphore of count 1, `Global\FreeVideoStudio_InstallerGate` (`DeploymentFootprint.InstallerGateName`), preventing concurrent installation or uninstallation. A second operation waits up to 15 s for the gate, then fails with a `MUTEX` report and an error dialog.
* **Cross-Process Logging Mutex:** `Global\FreeVideoStudioLogMutex_<user SID>` (`NamedSystemMutex.UserScopedName`, USERSCOPE_01) serializes log appends across the Main App, Video Merger, and Crop Tool processes on a shared file append stream handle.

* **Session-State Ownership (IPCOWNER_01):** `SessionStateFile` has ONE writer at a time. The IPC server lease `Local\FreeVideoStudio_IpcServerMutex_<user scope>` (`IpcProtocol.ServerMutexName`, created-not-owned per IPCLEASE_01) marks the owner; `Global\FvsStateTransferMutex_<user SID>` (`StateTransferStore.MutexName`) serializes the file I/O. **Lock order:** lease handle → `FvsStateTransferMutex` → `AtomicJsonFile`. A non-server process may write the file only while it holds a lease handle it created NEW; while any other process holds the lease it retries the pipe within `DeliveryBudget` and then fails. Full contract: [SYS-IPCLIFETIME](#SYS-IPCLIFETIME).
---

## 2. High-Volume Asynchronous Logging Engine  {#SYS-LOGGING}
* **Pipeline Architecture:**
  * Decoupled producer-consumer pipeline using `BlockingCollection<string>` bounded at 10,000 entries.
  * Under saturation, excess log entries are dropped with a single consolidated dropped-events warning to prevent native memory exhaustion.
  * Disk writes execute on a dedicated background Task (`Task.Run`).
  * UI batches display logs through a thread-safe `ConcurrentQueue` on a 1-second dispatcher timer.
* **UI Memory Streams:** Visual logging textboxes in diagnostic dialogs rigidly enforce a 100-line bounded FIFO queue to prevent UI thread heap leaks.
* **Log Rotation & Retention:**
  * Active log file is capped at 10 MB.
  * Rotates automatically before overflow using Unix-millisecond timestamp + GUID naming:
    ```text
    FreeVideoStudio.log.{unixMs}.{Guid:N}.old
    ```
  * Retention policy trims oldest files when exceeding 5 files, 50 MB total directory size, or 14 days of age.
* **Privacy & Security Gating:**
  * Full FFmpeg command-line arguments and complete filesystem paths log at `DEBUG` level only (enabled via `FVS_DEV_LOG_DIR`).
  * Production `INFO` logs sanitize sensitive paths, recording file basenames, exit codes, GPU capability discovery, and encoder fallback events.
* **Progress Spam Filtering:** Filters out FFmpeg `frame=... size=... time=... bitrate=...` progress stderr spam to protect log bounds while preserving initialization probes and error banners.
* **Error Isolation:** Disk write paths swallow I/O exceptions; `LogAppended` event callbacks are exception-guarded to ensure logging failures never crash host worker threads.

---

## 3. Window State & Directory Memory  {#SYS-WINSTATE}
* **Window State Persistence:**
  * `WindowBoundsHelper.Track()` manages window geometry with a 700ms-debounced background save.
  * Granular Speed Editor enforces a minimum floor of `MinWidth=900` / `MinHeight=600` with no hardcoded fixed size.
  * Window repositioning re-applies after the Avalonia `Opened` event to counter per-monitor DPI scaling handshakes.
  * Off-screen window recovery: Recenters on the primary display only when the window's bounding box is fully outside all active virtual screen boundaries.
* **First-Run Geometry Is Derived From The Display (FIRSTFIT_01):** A window with nothing saved must NOT open at whatever its content and `MinWidth`/`MinHeight` happen to add up to — that number was chosen against one developer's monitor, and it fills a 1080p laptop while opening as a small box in the middle of a 4K desktop.
  On the first open only, `Track(..., fitDisplayOnFirstRun: true)` sizes the window to cover **80% of the primary display's WORKING AREA in a landscape 16:9 rectangle**, centred on that area:
  $$\text{bound} = (0.8\,W_{\text{usable}},\; 0.8\,H_{\text{usable}}), \qquad w = \text{bound}_W,\; h = \frac{w}{16/9}$$
  $$\text{if } h > \text{bound}_H: \quad h = \text{bound}_H,\; w = h \times \tfrac{16}{9}$$
  Fitting INSIDE the 80% box (rather than scaling a full-screen 16:9 fit) is what holds on both monitor shapes: on a 16:9 display the height binds and the window is exactly 80% tall; on an ultrawide the width binds first and the window stays 16:9 instead of becoming a letterbox slot.
  * **Precedence, in order:** the window's OWN saved bounds → the seed window's bounds (WINSEED_01) → the display fit. It is a first-run DEFAULT, not a policy: the moment the user moves or resizes, the debounced save writes that window's key and step 1 wins forever after.
  * **`WorkingArea`, not `Bounds`.** `Bounds` includes the taskbar, so 80% of it can still put an edge underneath it.
  * **DIPs, not pixels.** `Width`/`Height` are device-independent; `WorkingArea` is physical pixels. The area is divided by the screen's scaling going in and multiplied back to centre. Skipping that makes the window 150% too large on a 150%-scaled display — the exact machines this exists for.
  * **`MinWidth`/`MinHeight` still win.** A floor larger than the computed rectangle (the Music Wizard's 1300x730 on a small laptop) clamps up and stops being 16:9. Correct: a usable window in the wrong ratio beats a correctly-shaped one that cannot lay out its contents.
  * **Opt-IN.** Applied to the Main App, Granular Speed Editor, Voice Over Studio, Add Music wizard, Video Merger and Crop Tools. NOT to the Settings dialog, and never to the detached preview console, which opens over its owning parent by UI-DETACH.
* **Deferred-Close Dispatcher Contract (⚠️ EXPORT-CRITICAL):**
  Every tracked window closes in two turns: `OnClosing` sets `e.Cancel = true`, saves bounds, then — **MANDATORY** — sets `_isSafeToClose = true` and re-posts `Close()` on the NEXT dispatcher turn. A window that cancels the close without re-posting it only HIDES.
  * **Saving bounds on close never blocks (UIIPC_01).** `WindowBoundsHelper.SaveBoundsOnClose` captures an immutable snapshot on the UI thread, builds the entry against the MEMORY state and `SessionStateWriter.Enqueue`s it — it returns immediately. A bounded background `FlushAsync` (`CloseFlushBudget` = 3 s) logs `Fail` if the owner does not acknowledge; the values stay queued and `Program.RunUiAsync` performs a bounded FINAL flush (3 s) after the dispatcher has stopped, then `FlushToDiskSafe` on the in-process owner. Re-entrancy: a nested call is ignored and an identical second snapshot (the deferred-close second `Closing`) is not enqueued twice. `SaveBoundsSync` (which blocked the dispatcher on the commit) no longer exists.
  * **The historic defect this encodes:** the Granular Speed Editor was the one sibling that never re-posted. `await editor.ShowDialog(this)` therefore never returned, the `MainWindow` continuation that copies `editor.ResultSegments` into `_speedSegments` never ran, and **EVERY granular edit — speed segments, freezes AND zooms — was silently discarded at export**, with FFmpeg receiving a uniform base-speed graph. Nothing failed loudly; the feature simply did nothing.
  * **Required chain:** `OnClosing` (`_isSafeToClose = true` + re-post `Close`) → `ShowDialog` resolves → `ResultSegments` → `_speedSegments` → `BuildExportSpeedSegments()` → `worker.SpeedSegments` → `GranularSpeedBuilder` split/concat graph.
  * **Teardown ordering:** re-posting also lets `OnClosed` actually run, which disposes the editor's mpv preview host instead of leaking it on every open. The preview is shut down BEFORE the window is hidden, and every render-thread/UI-thread hand-off is bounded by a timeout with a defined give-up behaviour — this is what closes the OpenGL teardown deadlock window.
  * **MPVSHUTDOWN_01 / VOAPPLY_01 — preview teardown is two-phase, awaitable, non-blocking, and cannot lie (2026-09-29, updated 2026-10-07).**
    `MpvVideoView.Dispose()` was a UI-thread call that `Thread.Join`-ed the render/software workers
    (seconds of frozen UI per tool switch) and, when a worker would not stop, ABANDONED the render
    context / WGL+D3D / GCHandle / `mpv_terminate_destroy` — then the still-living process built a
    fresh `MpvVideoView` beside the abandoned stack on every in-process navigation (TOOLNAV_01).
    The supported interactive lifecycle is now `MpvVideoView.ShutdownAsync(CancellationToken)` →
    `Task<PreviewShutdownResult>` (`App/PreviewShutdown.cs`):
    1. **Phase A — async quiescence:** stop signals are synchronous and immediate; PROOF of worker
       exit is awaited via completion tasks the loops set in their `finally` blocks, bounded by
       `Task.WhenAny` (5 s). No `Thread.Join` from the UI thread, ever. Phase A additionally requires
       presentation permit quiescence across all 16 swap-chain slots (`QuiescePresentGatesAsync`)
       proving the compositor has finished presenting before any shared textures or mutexes are unmapped.
    2. **Phase B — two-stage teardown: UI detachment (Phase B1) and detached worker native release (Phase B2) (VOAPPLY_06):**
       - **Phase B1 (UI thread):** Driver interop work (render texture release, `wglDXUnregisterObjectNV`, `glDeleteFramebuffers`, D3D textures and mutexes) is completely removed from the UI dispatcher. The UI thread awaits compositor image disposal (`CompositorDisposalBatch` reuses pending `AsyncTask` promises to prevent double `DisposeAsync` invocations across timeouts), unbinds the UI WGL context (`wglMakeCurrent(0, 0)`), releases presentation permits (`ReleasePresentGates`), and packages all driver and native handles into `NativeDetachedResources` (`DxInteropObjects`, `GlFramebuffers`, `SharedTextures`, `SharedTextureMutexes`, `_renderContext`, `_hglrc`, `_dummyHdc`, `_dummyHwnd`, `_dxInteropDevice`, `IpcClient`, `_mpvHandle`, `_d3d11Context`, `_d3d11Device`, `_gcHandle`) to be torn down off the UI thread.
       - **Phase B2 (Detached background worker):** The background worker binds the GL context afresh on its executing thread (`wglMakeCurrent(dummyHdc, hglrc)`), unregisters DX interop objects (`wglDXUnregisterObjectNV` with return value verification), deletes FBOs (`glDeleteFramebuffers`), releases shared D3D textures and mutexes, and calls `mpv_render_context_free` and `glDeleteTextures`. Because WGL contexts are thread-local, binding cannot rely on a sticky flag: each attempt requiring GL operations establishes thread-local binding, and a `try ... finally` block guarantees that `wglMakeCurrent(0, 0)` is invoked on exit, resetting `ContextBound = false` prior to closing the DX device (`wglDXCloseDeviceNV`) and deleting the WGL context (`wglDeleteContext`). Win32 `ReleaseDC` and dummy HWND destruction are marshaled to the UI thread via `Dispatcher.UIThread.InvokeAsync` to guarantee Win32 thread affinity. It disposes IPC, validates that the handle was not abandoned, and calls `mpv_terminate_destroy`. Only when all prerequisites succeed are dependent resources (`FreeNativeLibrary`, D3D device disposal, and `GCHandle.Free`) executed. On any step failure (such as failed interop unregister), teardown returns `Failed` truthfully, retains failed handles in `_activeTeardownOperation`, and halts dependent frees.
       - **Retry & Permit Ownership:** If the caller times out or cancels while the background worker is in flight, the detached task is preserved in `_activeNativeReleaseTask`. Subsequent retry calls distinguish between an active in-flight worker (`!_activeNativeReleaseTask.IsCompleted`) and a completed failure: an active worker is attached to and awaited without re-entering Phase A or re-acquiring presentation gates; a completed failure reaches `_activeTeardownOperation` and initiates a fresh background retry worker instead of returning the stale failure.
    3. **Failure is a result, not a silence:** a timeout returns `FailedWorkerDidNotStop` or `Failed`
       and frees NOTHING; it is logged, sets `MpvVideoView.HasUnverifiedTeardown`, and the callers obey it —
       `ToolNavigator.OpenAsync` cancels the navigation instead of opening a second preview stack,
       and `RestoreVideoPipelineAfterTool` refuses to build a replacement until restart.
    4. **Idempotent and retryable:** repeated calls attach to the same in-flight operation or report `AlreadyStopped`.
       Synchronously completed failures clear `_pending` so subsequent attempts can be retried cleanly (`VOAPPLY_05`).
    5. `Dispose()` remains ONLY as the process-final fallback (callers are immediately followed by
       `Environment.Exit`); its bounded joins are annotated `SHUTDOWNFALLBACK_01`, and
       `PreviewShutdownTests` fails the build if a blocking join appears anywhere else. Handles detached
       by an async shutdown are zeroed so the fallback never races or double-frees them.

* **Tool return (TOOLRETURN_01):** opening Video Merger / Crop Tools disposes the Main App preview (TOOLNAV_02). A disposed `MpvVideoView` cannot restart, so on the tool's close `MainWindow.RestoreVideoPipelineAfterTool` swaps a NEW `MpvVideoView` into the old slot, starts it off the UI thread and reloads the open clip paused at MARK START. Passing `restoreVideoPipeline: null` left the player dead (Upload did nothing). The software-fallback badge is bound in code, never by `#VideoHost`.
* **Persistent Directory Memory:**
  1. `Upload Video`: Opens in the last-used folder (`UploadVideoDirectory` in `session_state.json`). If that is unset or gone, it probes, in order, `Videos\Fortnite` → `Videos\Highlights\Fortnite` → `LocalAppData\Temp\Highlights\Fortnite` → `LocalAppData\Temp\Highlights` → `LocalAppData\NVIDIA Corporation\GeForce Experience\Highlights` → `Videos\Highlights` → `Documents\Highlights`, and falls back to the Windows user `Videos` folder.
  2. `Background Music`: Defaults to Windows user `Music` folder.
  * User-selected directories are written to configuration immediately upon selection, even if the file picker dialog is subsequently cancelled. "Written" means ENQUEUED to `SessionStateWriter` on the UI thread (UIIPC_01): visible to every in-process read at once, committed to the owner in the background.

---

## 4. Crash Recovery & State Reset  {#SYS-RECOVERY}
* **Post-Processing Reset:** Clicking "New File" executes the "Upload Video" command, triggering garbage collection of prior timeline chunks, speed segments, audio waveforms, and markers before opening the file picker.
* **Continuous Session Serialization:**
  * `RecoveryManager` maintains continuous session state serialization to `recovery_v2.json`.
  * **RECOVERYDOC_01 — the payload IS the project.** The recovery payload is the `fvsrecovery` envelope (`Core/Project/RecoveryEnvelope.cs`): exactly ONE canonical `ProjectDocument` (captured by `ProjectSession.Capture(forExplicitSave: false)`, serialised by the canonical `ProjectSerializer`) plus crash-only `transient` metadata. There is NO second project schema; a project field missing from recovery is a missing document field. Legacy `schemaVersion:1` payloads are migrated one-way by `App/Services/LegacyRecoveryMigrator.cs` (the legacy schema never carried the HUD mask or the merger queue); the legacy format is never written.
  * Restore applies the recovered document through `ProjectSession.OpenDocument` — the same document-application path Open Project uses — and then restores only the crash-only transient state.
  * The boot sequence evaluates `CheckFault()` and prompts the user to restore unclosed sessions.
  * Corrupted, truncated, or incompatible JSON recovery files are discarded cleanly to prevent startup crash loops.
  * **RESTORECRASH_01 — a crashed restore never deletes the snapshot.** `ActivateSafeMode()` stamps `safe_mode.sentinel` as `pid:startTicks:attempt` and returns false (no restore) once this process already tried or `MaxRestoreAttempts` (3) consecutive restores crashed. When `IsSafeModeActive()` finds the owner dead it clears ONLY that stale marker, records the attempt, and leaves `recovery_v2.json` byte-for-byte intact; `CheckFault()` then reports the crash and the user chooses Retry (one consented attempt per launch) or Start Fresh (`DiscardRecoveryState()`, the only discard). Once the budget is spent, `QuarantineSnapshot()` moves the snapshot intact to `recovery_v2.json.crashed` (WRITEORDER_01-sequenced atomic rename) and the app starts fresh. `DeactivateSafeMode()` ends the streak. Each quarantine gets a unique `recovery_v2.json.crashed-<utc>-<id>` name, so an earlier kept copy is never overwritten.
  * **RESTOREFAIL_01 — a restore that fails without crashing never deletes the snapshot.** When `OpenDocument` returns false or the restore throws, `MainWindow.Restore.cs` calls `QuarantineSnapshot()` (not `ClearState()`) and tells the user where the intact copy is. Only corrupt or unusable snapshots (unmigratable legacy payload, no document) are discarded.
* **Video Merger session (MERGESESSION_01):** the Merger's edit list (`MergeEdl`) is autosaved to
  `merger_session.json` by `MergerAutosaveStore`: 750 ms write-behind debounce after the last edit,
  process-wide ordered versions (a queued write can never resurrect a cleared file), `AtomicJsonFile.WriteText`,
  all I/O on the thread pool, `Load()` never throws. An empty queue clears it; a MERGE does not. Every
  edit in `VideoMergerWindow` posts ONE coalesced capture (`MergerSession.Capture`) that also publishes
  `ProjectMerge{Clips, Edl}` to `ToolNavigator`; closing the window captures and flushes at once. Opening
  the Merger with an empty queue restores the project's edit list, else the autosave; files that are gone
  are left out and named, changed files are kept, re-analysed and named. Restoring sets the scraper for
  the session only (never rewrites the global setting). RESTOREMISS_01 (user decision): the restore is silent
  UNLESS files are missing or changed; then an approval dialog names every such file and its folder
  (`MergerSession.DescribeRestoreProblems`) with CONTINUE WITHOUT THEM / START FRESH (start fresh clears the saved
  session); if nothing is left it says so and clears it. Queue rows carry stable ids (`ClipIdList`).
* **Granular Session Preservation Invariant:**
  App-level background saves (e.g., volume slider changes or timeline scrubbing) must preserve any active `granular_session` sub-object present on disk, ensuring in-flight Granular Speed Editor edits are never wiped while open.
* **Atomic Persistence Protocol:**
  All disk saves execute via `AtomicJsonFile.WriteObject`:
  1. Write payload to a unique GUID temporary file in the target directory using `FileOptions.WriteThrough`.
  2. Flush file stream to physical disk: `stream.Flush(flushToDisk: true)`.
  3. Replace the target file atomically via `File.Move(tempPath, path, overwrite: true)`.
  Eliminates half-baked, partial, or corrupted states during power outages or system crashes.
* **Config Backup Cascade:** `CropConfigStore` (`RotateBackupsUnlocked`) enforces a 5-tier `.bak` cascade on `crops_coordinations.conf` prior to writes:
  $$\text{.bak4} \to \text{.bak5}, \quad \text{.bak3} \to \text{.bak4}, \quad \text{.bak2} \to \text{.bak3}, \quad \text{.bak1} \to \text{.bak2}, \quad \text{current} \to \text{.bak1}$$
* **Crop defaults and recovery (FORTNITEDEFAULT_02 / CROPFALLBACK_02):** The shipped Fortnite layout is the dev sandbox's saved Apex Legends layout from 2026-09-13, including exact rational scales and source rectangles, excluding Boss HP. `CropConfigDefaults.Create()` is the shared factory and final recovery fallback. A damaged live document first tries `.bak1` through `.bak5` without rotating backups. Malformed layer rectangles, scales, positions, or z orders are rejected along with malformed JSON; a rejected save leaves the live file and backups intact. Valid schema v3 and v4 profiles and explicitly disabled layers remain supported (`SchemaVersion = 4`, `MinimumUsableSchemaVersion = 3`). Schema v4 adds the optional `"crops_source"` section (`SourceCropsSection`) for unscaled source coordinate mapping without modifying content space geometry. Missing Fortnite profiles are seeded from the shipped defaults, never the shared active config; malformed Fortnite profile files are backed up before replacement, while valid user edits are preserved.

* **First-run crop initialization (CROPFIRSTBOOT_01):** `EnsureDefaults` seeds a missing live configuration directly under the config mutex from a valid active profile or the shipped fallback. It must not call `ApplyProfile`, which calls `EnsureDefaults` itself. A profile save failure must be reported to Crop Tools so unsaved edits remain open.

---

## 4a. Developer Build Harness & Fix Sentinels  {#SYS-DEVBUILD}

> [!CAUTION]
> ### STRICT BAN ON `Build.cmd` FOR AI AGENTS (USE `dev_build.cmd` FOR COMPILE TESTS)
> Autonomous AI agents are **STRICTLY FORBIDDEN from ever executing `.\Build.cmd` or `Build.cmd`**.
> `Build.cmd` compiles `FreeVideoStudio.exe` and **OFFICIALLY PUBLISHES A VERSION ON GIT CLOUD (GITHUB RELEASES)**, deploying downloadable binaries worldwide.
> If an agent needs to test or verify compilation, NativeAOT builds, or packaging, it **MUST ONLY RUN `.\dev_build.cmd`** (or `dotnet build` / `dotnet test`).
> `.\dev_build.cmd` passes `--dev` to `build\FvsBuild\FvsBuild.csproj`:
> * Builds purely locally.
> * Never pushes git tags.
> * Never publishes or uploads assets to GitHub Cloud.
> * Does not wipe `.\compiled` beforehand.

`dev.cmd` is the only supported way to run a development build. Three guarantees, in order, before every mode:

1. **KILL_STALE** — kills every process whose executable lives under the repo (the app, its companion windows, and orphaned `mpv.exe` / `ffmpeg.exe` children), then shuts down the Roslyn/MSBuild servers. An orphaned app holds a lock on `bin\`, which is what makes the next build silently reuse a stale binary.
2. **NUKE_BUILD** — deletes `bin` and `obj` for EVERY project under `src\` and `tests\`. Runs for `dev rebuild`, `dev fresh`, `dev run`, `dev build` and `dev clean`.
   * **`DEVFAST_01` — the default `dev` (hot reload) and `dev trace` are INCREMENTAL.** The wipe plus `dotnet clean` on every launch cost ~30 s of full restore and rebuild while the app itself starts in ~2 s. KILL_STALE (step 1) already removes the real cause of a stale binary — a running process locking `bin\` — and MSBuild rebuilds whatever changed. If a fix ever seems missing, `dev rebuild` is the full wipe; VERIFY_PATCHES (step 3) still runs on every mode.
3. **VERIFY_PATCHES** — runs `build/FvsVerify`, which checks every `build/sentinels.txt` entry for the **fix sentinel** tag that sits beside a specific fix, and halts loudly if one is absent (SYS-VERIFYTOOL). Fixes have been reverted between a commit and a build more than once, silently producing a binary without them and costing a full test cycle to discover.

* **Every fix that costs a test cycle to re-diagnose earns a sentinel.** Add a `TAG=path` line for its tag to `build/sentinels.txt` when the fix lands, in the same change — not later.
* **`VERIFYHALT_01` — THIS SUBROUTINE WAS A NO-OP AND HAD TO BE TAUGHT TO SPEAK.**
  `VERIFY_PATCHES` built its `MISSING` list correctly and then returned. The variable was **assigned in two places and read in none**, so every sentinel in the list (133 at the time it was found) was checked on every run and the answer thrown away. The guarantee stated above — *"halts loudly if one is absent"* — could neither halt nor be loud, for as long as the list has existed.
  Two defects, both now closed:
  1. The unread `MISSING` variable. A missing sentinel now prints the offending tags and exits non-zero.
  2. `exit /b 1` inside a `call`ed subroutine returns from the **subroutine**, not the script. The call site therefore tests `if errorlevel 1` immediately after `call :VERIFY_PATCHES`.
  **The damage was not hypothetical.** `STRIPCOST_01` pointed at a tag that no longer existed in `GranularSpeedEditorWindow.axaml.cs` and nothing ever said so. It was not a revert — commit `ad0b7bd` replaced the per-slot `Image` path with `Controls/TimelineFilmstrip`, which draws via `DrawingContext.DrawImage` with explicit source/destination rects, making the oversized-bitmap defect structurally unreachable. The sentinel is retired with a note, per the `WIZPROGRESS_01` precedent.
  ⚠️ `VERIFYLOOP_01` had already learned this lesson once, about two silently skipped entries, and its fix left the reporting half unwritten. **A guard that cannot fail is worse than no guard at all, because it is trusted.**
* **`LISTCOMMENT_01` — `REM` IS NOT A COMMENT INSIDE A `FOR` LIST.**
  `cmd.exe` tokenises everything between a list's brackets on whitespace. An unquoted
  `REM --- Crop Tools rework, phase 3 ---` inside `for %%P in ( … )` is **not skipped** — it
  becomes the list items `REM`, `---`, `Crop`, `Tools`, `rework`, `---`, and each is checked as
  though it were a sentinel. The 37 annotation lines in this list were producing roughly 400 bogus
  `[no-file]` entries.
  * ⚠️ **They had been mis-parsed for as long as they had existed.** It was invisible only because
    `MISSING` was never read (`VERIFYHALT_01`). Fixing the reporting is what surfaced it — which is
    the whole argument for guards that can actually fail.
  * Annotations are now **quoted**, so each is a single token, and the loop skips them by their
    `REM` prefix. They must contain no double quote (it would end the token) and no `=` (it would
    parse as a `TAG=path` entry).
  * `ArchitectureRuleTests.DevCmdSentinelListContainsOnlyQuotedTokens` asserted this invariant while the
    list lived in `dev.cmd`. It was retired when the list moved to `build/sentinels.txt` (SYS-VERIFYTOOL);
    `tests/FvsVerify.Tests` (`CommentsAndBlankLinesAreSkipped`, `AMalformedLineIsReportedRatherThanSkipped`) covers the new format.

* **`BATCHPARENS_01` — NO ROUND BRACKETS IN A `REM` INSIDE THE SENTINEL LIST.**
  `cmd.exe` counts `(` and `)` while scanning a parenthesised block **even inside a `REM`**. A
  comment added to the `for %%P in (` list reading *"phase 0 (foundation). docs/08_…"* closed the
  list at `(foundation)`, and the next token — a bare `.` — was then run as a command. The whole
  script died at parse time with `. was unexpected at this time.` **before a single sentinel was
  checked**, so the guard that had just been taught to halt could not even start.
  * ⚠️ This is `VERIFYLOOP_01`'s lesson a third time: that section already records the list being
    fragile to edits, and the failure mode is always the same shape — the check silently does not
    run. A comment is not inert inside a block.
  * `ArchitectureRuleTests.DevCmdSentinelListHasNoBracketsInComments` (`BATCHPARENS_01`) and
    `DevCmdBracketsBalance` (`BATCHPARENS_02`) asserted both cases while the list lived in `dev.cmd`;
    both were retired with it (SYS-VERIFYTOOL). `DevCmdDelegatesTheSentinelCheckRatherThanParsingIt`
    now fails if the list ever moves back into the script.
* **A sentinel proves a fix has not been DELETED; a test proves it has not been BROKEN.** Where a rule can be asserted, prefer `tests/FreeVideoStudio.App.Tests/ArchitectureRuleTests.cs` (`08_APPLICATION_COMPOSITION.md` §3). `EveryFixSentinelStillResolves` re-checks every `build/sentinels.txt` entry from CI, on any platform, naming the file and tag. It is the authority on the current count, not this paragraph.
* **DEVDATA_01 — the dev sandbox keeps your settings and AI keys.** `FVS_PROGRAMDATA_ROOT` is
  `%LOCALAPPDATA%\FreeVideoStudio_DEV\data`, no longer `%TMP%\FreeVideoStudio_DEV\.dev_data`:
  Windows Storage Sense and Disk Cleanup empty `%TMP%`, which silently reset the dev settings and the
  Gemini key. `build\DevSandbox.ps1` (called by `dev.cmd` after KILL_STALE, and again after the
  `dev fresh` wipe) moves the old `%TMP%` sandbox across once (`keep\moved_from_tmp.txt`), remembers
  the AI settings (`GeminiApiKey`, `GeminiModelName`, `AiMagicWandCloudConsent`, `AiZoom*`) in
  `keep\ai_settings.json` OUTSIDE the wiped folder, and puts them back when `settings.json` is missing
  or has no key. With nothing remembered, the key is borrowed once from the installed app's settings
  (`%LOCALAPPDATA%\FreeVideoStudio`). A missing settings file is recreated with only `SchemaVersion` and
  the AI fields; every other setting takes the app's default, so `dev fresh` is still a fresh boot. The
  script never deletes anything and never fails the run. Logs stay in `%TMP%` and are still cleared.
* **DEVUPDATE_01 — dev updates come from `.\compiled`, through the production flow.** `dev.cmd` sets
  `FVS_DEV_UPDATE_SOURCE` (the repo root) and the documented developer override
  `FVS_ALLOW_UNSIGNED_UPDATE=1` (the dev app is unsigned, UPDATETRUST_02). With both
  `FVS_DEV_LOG_DIR` and `FVS_DEV_UPDATE_SOURCE` set, `UpdateService.QueryLocalDevReleaseAsync` treats
  `.\compiled\FreeVideoStudio-Setup.exe` (NODROPPER_01) as the latest release (version from its file version, size, SHA-256
  taken like GitHub's digest) and compares it with the copy INSTALLED in Program Files (nothing
  installed = 0.0), because the dev app is stamped by the same `dev_build.cmd` run and would always be
  "up to date". Everything after that is the production flow: question, download window (read from disk
  with the same loop, hash and publisher check), Restart & update, installer window, first-launch
  notice. The startup check runs on every dev launch (no throttle). The installer is started WITHOUT
  the dev variables (`FVS_DEV_LOG_DIR`, `FVS_PROGRAMDATA_ROOT`, `FVS_DEV_UPDATE_SOURCE`,
  `FVS_ALLOW_UNSIGNED_UPDATE`), so it really installs to Program Files and the installed app opens
  with the real settings. A `file://` download URL is refused unless both dev variables are set.
* **`dev.cmd trace` — a log that can leave the machine (TRANSPORT_TRACE_01).** Identical to the default watch mode except `FVS_DEV_LOG_DIR` points at `.devlogs\` inside the repo instead of `%TMP%`. The rule that dev logs never land in the project tree exists so an ordinary run cannot litter it and so a log can never be committed; this mode is opt-in, announces itself, and `.devlogs/` is gitignored, so neither risk applies.
  It exists because **a log nobody can reach is a log nobody can read.** A fault that cannot be reproduced from source is diagnosed from a log, and a log sitting in a temp folder on one machine is unavailable to whoever is helping.
* **Instrument before the third guess.** The main window writes one `TRANSPORT` line for every play and pause it issues — who issued it, and the player state at that instant (`t`, `dur`, `eof`, `pausedBefore`, `frozen`, `freezeAt`, `freezeArmed`, `endParked`, `seeking`). It is per transport change, not per tick, so it is cheap enough to leave in permanently. A transport fault that survives two source-level fixes is not a reading problem; ship the trace and let the log name the line.
* ⚠️ **The default mode is `dotnet watch` hot reload, and hot reload cannot apply structural edits** — new fields, new methods, changed signatures. A fix that adds either is NOT in the running process until `dev.cmd` is stopped and restarted, however many times the file was saved. When a change does not appear, restart before re-diagnosing: the source on disk being correct is not evidence that the running binary contains it.

---

## 4b. Output Estimate Worker Lifetime {#SYS-SIZEESTIMATE}
* `SIZEESTIMATE_01`: each editing window owns one worker and one pending immutable snapshot. An 80ms throttle coalesces pointer/slider events while still updating during continuous dragging.
* Filesystem access, ffprobe and estimate calculations run off the UI thread. Metadata caches hold at most 128 entries, keyed by path, size and modification time. A changed file is re-probed; failures are not cached. UI-owned queues and duration dictionaries are never mutated from a worker.
* Both quick and refined results carry a request version. The version and disposal state are checked again INSIDE the UI callback; stale callbacks cannot overwrite newer state or touch a closed window.
* Closing cancels the worker, completes its queue, and asynchronously allows up to one second for shutdown before continuing window teardown. ffprobe uses `AsyncProcessRunner` with a 15-second timeout and lifetime cancellation, which terminates the process and drains both pipes. The worker disposes its own cancellation source only after completion; no synchronous waits on the UI thread.

## 4c. Atomic Persistence Is Not Optional, And It Is Not Per-Caller  {#SYS-ATOMICWRITE}
* **`SETTINGSATOMIC_01` — every shared-state file goes through `AtomicJsonFile`, under a named mutex.**
  `settings.json` lives in `ProgramDataRoot` (the per-user root, SYS-USERSCOPE), which the Main App, the Video Merger (`--merger`) and
  the Crop Tools (`--crop-tool`) all share. `SettingsManager.Save` previously did
  `File.WriteAllText(SettingsPath + ".tmp")` followed by `File.Move`. Three defects, all now closed:
  1. **A FIXED temp name.** Three processes wrote the same scrap file. The loser got an `IOException`
     that `Save()` swallowed while returning `false` — a value ten of its twelve call sites discarded.
     In the other interleaving one process published another's half-written payload.
  2. **No durability barrier.** `File.WriteAllText` returns at the OS cache, and `File.Move` maps to
     `MoveFileExW` with `MOVEFILE_REPLACE_EXISTING` only. NTFS journals the rename, not the data, so a
     power cut between them produced a correctly named, **zero-filled** `settings.json` — which `Load`
     then quarantined, resetting every preference the user had.
  3. **No lock on `Instance`.** A mutable static object graph was serialised while `UpdateService`'s
     background task mutated it.
* **The protocol is centralised, not copied.** `AtomicJsonFile.WriteText` (`ATOMICTEXT_01`) applies the
  identical GUID-temp → `WriteThrough` → `Flush(flushToDisk: true)` → atomic `File.Move` sequence to a
  caller-supplied JSON string, so a source-generated (NativeAOT) serializer's exact bytes reach disk
  without a `JsonNode` round-trip that could silently reshape them. `WriteObject` and `WriteText` share
  one `WriteCore`. `WriteBytes` (`ATOMICBYTES_01`) is the same `WriteCore` for an opaque byte payload
  (cache files). **Never reimplement this sequence at a call site.**
* **Lock waits are bounded and never span a UI `await`.** `Global\FvsFreeVideoStudioMutex_<user SID>`
  (USERSCOPE_01) is acquired with the 2-second `InteractiveMutexTimeout` so a wedged sibling process
  cannot freeze a click. A `LockException` is logged and reported as a failed save, not swallowed.
* **`SETTX_01` / `SETTX_02` — ONE transaction, ONE mutex acquisition.**
  All settings mutation goes through `SettingsManager.Update(Action<AppSettings>)` (plus the typed
  `SetAutoUpdateChecks` helper). SETTX_01 re-read the persisted file before patching, but released
  the mutex after the read and re-acquired it for the write; a sibling process could commit in that
  gap and be overwritten by the stale snapshot (lost update). SETTX_02 closes it:
  * **Lock order (the only order):** `SerializeGate` (in-process monitor) → settings named mutex →
    file I/O. The mutex is acquired exactly once per transaction and is held across
    read → deserialize → migrate/default → patch → serialize → `AtomicJsonFile.WriteText`.
    Helpers that touch the file (`ReadPersistedLocked`, `TryWriteLocked`) REQUIRE the caller to hold
    both locks and never acquire either (no recursion, including through corruption quarantine).
    `Load` follows the same order. No lock is held while `Committed` is raised.
  * **Publish only after durable success.** `Instance` changes and `Committed` is raised only after
    `AtomicJsonFile.WriteText` returned. A failed lock/read/patch/serialize/write returns false and
    leaves `Instance` and the file untouched.
  * **Forward compatibility.** Known fields are deserialized with the source-generated
    `SettingsJsonContext` (no reflection). Unknown ROOT properties (written by a newer build) are
    captured as `JsonNode`s, and after the patch merged back into the serialized known document;
    a known current field wins any name collision. With nothing to merge, the serializer's exact
    bytes are written (ATOMICTEXT_01). Unknown properties nested inside known objects are not
    preserved.
  * **Schema.** Older → migrated to `CurrentSchemaVersion`; current → stays current; FUTURE → never
    downgraded (the write carries `max(persisted, current)`), and `Load` leaves a future-schema file
    untouched.
  * **Corruption.** An unparseable file is quarantined (`settings.json.corrupt-*.bak`, max 5) and
    replaced with defaults + the patch — never with this process's stale in-memory snapshot. A file
    that exists but cannot be READ aborts the transaction and is left alone.
  External code may READ `SettingsManager.Instance.*` but must never ASSIGN through it (directly,
  by compound assignment/`++`/`--`, or through a local alias). There is no `Save()` any more. Guarded
  by `ArchitectureRuleTests.NoExternalDirectAssignmentThroughSettingsManagerInstance` (zero baseline)
  and behaviourally by `SettingsTransactionTests` (concurrent unrelated updates, cross-process writer
  blocked between read and write, unknown/future data preserved, failed write publishes nothing,
  corruption, SettingsWindow one-transaction, UpdateService helper usage).

* **`CACHEBOUND_01` / `ATOMICBYTES_01` — disposable cache bytes use the same publish sequence, without the mutex.**
  `LaneDiskStore.Write` (the Merger's lane cache, `ProgramDataRoot\cache\lanes`, SYS-USERSCOPE) calls
  `AtomicJsonFile.WriteBytes` — the one `WriteCore` (GUID temp in the target folder, `WriteThrough`,
  `Flush(flushToDisk: true)`, `File.Move(overwrite: true)` with its bounded retry, temp deleted on failure).
  No named mutex is taken because a lost or duplicate cache write is a miss, never lost user state. Readers open with Read|Write|Delete sharing; the background prune deletes
  LRU files and temps orphaned for more than 10 min. Budgets and pruning: `04` UI-MERGERQUEUE.

## 4d. Bounded Flush Scheduling & Ordered IPC Teardown  {#SYS-IPCLIFETIME}
* **`FLUSHCEILING_01` — a debounce without a maximum-wait ceiling is not a debounce.**
  `NamedPipeStateServer.ScheduleDiskFlush` is called from every mutating opcode and used to `Stop()`
  then `Start()` a 500 ms one-shot timer. A **continuous** update stream — a timeline scrub, a volume
  drag — restarted that clock before it could ever elapse, so the flush was postponed **indefinitely**.
  The app reported "continuous session serialization" while nothing reached disk for as long as the
  user kept working, and a crash during that drag — when a crash is most likely — lost all of it.
  * The trailing 500 ms edge (`FlushDebounceMs`) still coalesces bursts.
  * `_firstDirtyTicks` records when the oldest unflushed change appeared. Once it has waited
    `FlushMaxWaitMs` (3 s) the flush is **forced**, off the calling thread, regardless of traffic.
  * Worst case is therefore bounded at one write per 3 s during sustained editing, and coalescing is
    preserved everywhere else. Write amplification is the reason the debounce exists; do not remove
    either half.
* **`WIZVOLDEBOUNCE_01` — UI controls never touch the disk.** The Music Wizard VIDEO/MUSIC faders
  used to call `StateTransferStore.UpdatePropertiesSync` from every `Slider.ValueProperty` change,
  i.e. acquire `Global\FvsStateTransferMutex_<SID>` and run `AtomicJsonFile.WriteText` with
  `Flush(flushToDisk: true)` **on the UI dispatcher, per pointer-move**. The drag now updates only
  the in-memory slider value and the player gains; persistence is a **600 ms trailing-edge
  debounce** (`App/Infrastructure/DebouncedStateWriter`, `DefaultDebounceMs`) that hands the latest payload to
  `UpdatePropertiesAsync` on the thread pool. Writes are chained (never reordered on disk), the
  pending payload is a field (the LAST value persists, not the first), and the owner window's
  `Closed` event flushes a pending payload without awaiting it.
* **`AUTOSAVEBG_01` — project autosave is write-behind.** `ProjectSession.AutosaveTick` captures the
  document (in-memory projection) and copies the undo/redo branches on the UI thread, then runs
  `IProjectStore.Save` and `UndoSidecarStore.Save` on the thread pool. Only the dirty-flag update /
  Fatal report is marshalled back, and dirty is cleared only if no edit landed during the write
  (`_editGeneration`). One autosave in flight at a time. Every project write — background or
  explicit — passes one ordered gate (`_ioGate` + write sequence), so a stale autosave is skipped
  rather than landing on top of a newer Ctrl+S; no `Task.Wait` on the UI thread (ASYNCUI_01).
* **`IPCLEASE_01` — the single-server lease is a mutex that is NEVER OWNED.**
  A Win32 mutex is thread-affine: only the thread that acquired it may release it. The lease was
  taken on the startup thread and released in `Dispose` on another, so `ReleaseMutex` threw
  *"Object synchronization method was called from an unsynchronized block of code"* on **every
  clean shutdown**, was swallowed by a bare catch, and the handle was then closed while still
  owned — which marks the mutex **abandoned**. The next launch hit the `AbandonedMutexException`
  branch and logged the permanently false *"Prior server process exited abruptly"* after a
  completely normal exit.
  * **The fix is to stop owning it.** A named kernel object lives exactly as long as one handle to
    it remains open, so *holding a handle* is already a perfect lease.
    `new Mutex(initiallyOwned: false, name, out createdNew)` — `createdNew` is true only for the
    process that created it, which is the one that becomes the server. Nothing is ever acquired, so
    there is nothing to release, no thread affinity, and no abandoned state that can exist at all.
    A crashed server closes its handle with the process and the name frees itself.
  * ⚠️ **The NAME is deliberately UNCHANGED** (`IpcProtocol.ServerMutexName`). A pre-fix build still
    running OWNS this mutex, and because `initiallyOwned` is ignored when the object already exists,
    old and new builds still see each other's lease and exactly one of them serves. Renaming it
    would let two servers bind the same pipe and silently diverge the session state.
  * ⚠️ **Do not substitute a Semaphore.** `Mutex` is the only named primitive .NET implements on
    every platform; named `Semaphore` and `EventWaitHandle` are Windows-only and throw
    `PlatformNotSupportedException` elsewhere, which takes the IPC test suite with them. A semaphore
    also cannot share a name with a mutex, so it would break the in-place upgrade above.

  > **Correction (docs audit).** This section previously stated the opposite — that the lease *is*
  > a semaphore, and that `IpcProtocol.ServerLeaseName` was "deliberately distinct from the retired
  > `ServerMutexName`". No such symbol has ever existed; the code kept the mutex and kept the name,
  > for the two reasons above. The spec described an approach the implementation had considered and
  > explicitly rejected, and an agent following it would have broken the cross-platform test suite
  > and split the pipe.

* **`IPCTEARDOWN_01` — signal, then WAIT, then dispose.** `Dispose` previously cancelled and disposed
  its `CancellationTokenSource` while `ListenLoopAsync` was still inside `WaitForConnectionAsync`, so
  the in-flight `NamedPipeServerStream` was not deterministically closed; a restart inside that window
  hit `TryStart`'s `!createdNew` path and silently degraded every later `LoadSync`/`SaveState` to the
  slow direct-disk path. The order is now fixed and mandatory: stop the timer → **stop accepting
  writes** (IPCOWNER_01) → flush → **release persistence** (IPCOWNER_01: no flush after this) → cancel →
  **wait (bounded, 2 s) for `_listenTask`** → drop the lease → dispose the CTS and the ready event.
  (Since IPCOWNER_01 a client no longer degrades to a direct-disk WRITE while any lease exists; it
  retries the pipe and fails explicitly. Reads may still fall back to the file.)
  `DisposeAsync` awaits rather than blocks; the synchronous path must never be left without a wait.

* **`IPCOWNER_01` — one authoritative state owner; a timed-out pipe call never starts a second writer.**
  `SaveAsync`, `UpdatePropertiesAsync`, `UpdatePropertiesSync`, `SendHandoffAsync/Sync` and `ClearAsync` (the `*Sync` variants were removed by UIIPC_01)
  used to write `SessionStateFile` directly when one pipe call missed its 30–100 ms window. A live but
  slow server still held its own `_currentState` without that change, and its next `FlushToDiskSafe`
  replaced the file: the update was lost, and `SendHandoffAsync` had already returned `true`.
  * **Owner rule:** whoever holds the server lease (`IpcProtocol.ServerMutexName`, IPCLEASE_01 — name and
    created-not-owned protocol unchanged) is the ONLY writer of `SessionStateFile`.
  * **Commit path (`StateTransferStore.CommitAsync`, the only mutation path):** in-process apply →
    pipe (ACK = applied to the authoritative state) → disk ONLY after `new Mutex(false, ServerMutexName,
    out createdNew)` returns `createdNew == true`, i.e. no server (current or pre-fix build) exists; the
    handle is held across `Global\FvsStateTransferMutex_<SID>` → `AtomicJsonFile` so no server can start
    and load the file mid-write. Lock order: lease handle → state mutex → file.
  * **Live lease:** the pipe is retried with backoff (per attempt doubling to 500 ms) inside
    `DeliveryBudget` (2 s; sync callers: `mutexTimeout ?? DeliveryBudget`). On expiry the call returns
    **false** and logs Fail; the file is never touched.
  * **Success** (`true`) means acknowledged by the owner or durably committed — nothing else. All
    mutators return `bool`/`Task<bool>`; the commit always runs on the thread pool (no UI-context deadlock
    for sync callers, no named mutex on the UI thread). Since UIIPC_01 there ARE no sync callers: nothing may block on a commit.
  * **Server side:** an ACK is sent only after the change is applied; a refused change answers
    `IpcOpcode.Error`. `Dispose` clears `_acceptingWrites` before the final flush (nothing acknowledged
    can miss it; later callers get `false` and reroute) and sets `_persistenceReleased` under `_flushGate`
    before the lease handle closes (no queued flush writes after another process may own the file).
  * **Tests:** `IpcTests.IpcOwner_*` — delayed ACK, hung server + flush after client timeout, lease held
    without server, disposed server, and real concurrent client processes (`IpcChildProcess`) against a
    live server and against a server starting mid-run.

* **`UIIPC_01` — the UI never waits on IPC (2026-10-11).** Verified defects: `StateTransferStore.UpdatePropertiesSync` ran the commit on the pool but BLOCKED the caller on it (`StartCommit(...).GetAwaiter().GetResult()`, up to the 2 s delivery budget) and was called from `MainWindow` (UploadVideoDirectory), Settings, the Music Wizard and every `Window.Closing` (`WindowBoundsHelper.SaveBoundsSync`); `InProcessSessionState.ReadSnapshot` fell back to `NamedPipeStateClient.GetStateSync` during window construction.
  * **Writes:** UI code calls `SessionStateWriter.For(paths).Enqueue(updates)` — a deep clone merged into ONE pending batch (latest value per key wins; identical to applying UpdateProperties in order). One background drain per writer commits batches strictly one after another through `StateTransferStore.UpdatePropertiesAsync` (IPCOWNER_01 unchanged: in-process apply → pipe ACK → lease-guarded disk), so writes are ordered and coalesced.
  * **Failure is explicit and lossless:** an unacknowledged batch is logged `Fail`, raised on `CommitFailed`, and RETAINED under any newer values; the next commit or `FlushAsync` retries the latest state. `FlushAsync(timeout)` returns true only when everything enqueued before it was acknowledged, false (and `Fail`) on timeout — it never blocks a thread.
  * **Reads:** `InProcessSessionState.ReadSnapshot()` = `SessionStateWriter.ReadSnapshot()`: the in-process server's memory, or the snapshot primed at bootstrap (`Program.BootstrapAsync` → `LoadAsync` → `Prime`), overlaid with unacknowledged → in-flight → pending updates. No pipe, no mutex, no disk; an unprimed read starts an asynchronous prime.
  * **Removed APIs (enforced by `ArchitectureRuleTests.UiCodeNeverBlocksOnSessionStateIpc` and `IpcTests.UiIpc_NoSynchronousIpcEntryPointExists`):** `StateTransferStore.UpdatePropertiesSync/SendHandoffSync/LoadSync`, `NamedPipeStateClient.GetStateSync/UpdatePropertiesSync/SaveStateSync/ClearStateSync/SendHandoffSync`, `WindowBoundsHelper.SaveBoundsSync`. The Merger's standalone return handoff awaits `SendHandoffAsync` and logs a miss.
* **`IPCDEADLINE_01` — one end-to-end deadline per pipe exchange.** `IpcProtocol.ExchangeAsync` runs connect, write AND read under ONE linked token (`CancelAfter(deadline)`), and the token's registration also disposes the stream, so a peer that accepts the connection and never answers releases the caller at the deadline even if an I/O primitive ignored the token. Every `NamedPipeStateClient` call goes through it; there is no synchronous client. Wire protocol, opcodes, pipe name and lease name are unchanged. Tests: `IpcTests.IpcDeadline_*` (silent peer), `SessionWriter_*` (rapid updates → newest persists, hung owner, failure retained/retried, close during pending save, slow ACK), `AutosaveBackgroundTests.WindowClose_*` (dispatcher keeps processing while the owner is hung; re-entry; unacknowledged save reported).

* **In-Process Navigation vs. Standalone CLI & Named Pipe Architecture (`TOOLNAV_01`–`TOOLNAV_04`):**
  Companion tools (Crop Tool, Video Merger) were originally launched via "process suicide": `CompanionAppService` serialized state, invoked `Process.Start(sameExe, "--crop-tool")`, and terminated the host via `Environment.Exit(0)`.
  This was replaced by in-process navigation via `ToolNavigator.cs`:
  1. **In-Process Modal Windowing (`TOOLNAV_01`):** Companion tools open within the same application process.
  2. **Main Window Hidden, Not Closed (`TOOLNAV_03`):** The main editor window is hidden to keep view-models, the document session (`ProjectSession`), and undo history intact.
  3. **Video Pipeline Teardown & Revival (`TOOLNAV_02` / `TOOLRETURN_01`):** The mpv playback pipeline is cleanly torn down before the tool opens to avoid D3D11/OpenGL GPU lock contention, and reconstituted via `MainWindow.ToolReturn.cs` upon the tool's close.
  4. **Standalone CLI & Named Pipe Persistence:** Standalone execution via CLI flags (`--crop-tool`, `--merger`, `--install-worker`, `--cleanup-worker` routed in `Program.cs` and `AvaloniaApp.axaml.cs`) and named pipe state synchronization (`NamedPipeStateServer.cs`, `NamedPipeStateClient.cs`, `StateTransferStore.cs`, `CropConfigStore.cs`) remain active for installer workers, test automation, and independent tool invocations.

---

## 5. Binary Metadata & Authenticode Signing Mandate  {#SYS-SIGNING}
* **Win32 Executable Metadata:** The compiled `.exe` embeds complete production metadata (Product Name, Publisher, Assembly Version, File Version, Legal Copyright).
* **PEIDENTITY_01 — the binary names its own file.** NativeAOT copies the version resource from the managed assembly, so the program said `OriginalFilename = InternalName = FreeVideoStudio.dll` while it is `FreeVideoStudio.exe` — the renamed-binary masquerading pattern (MITRE T1036.003) antivirus scores. The SDK has no property for these strings, so `FvsBuild` (`PeIdentity.FixOriginalFilename`) rewrites them in the VS_VERSION_INFO block of the `.rsrc` section, in place (`.dll` → `.exe` is length-preserving; no offset, size or checksum moves; nothing outside that block can change), right after the publish and BEFORE signing. Idempotent; a file without a version resource fails the build. Measured on the 2026.10.11.1008 program: exactly 6 bytes changed. `SynchronizeVersionFiles` also stamps `app.manifest`'s `assemblyIdentity version` with the build version (it said `1.0.0.0`). The Inno Setup installer's own resource says `OriginalFilename = FreeVideoStudio-Setup.exe`, Company `Alon Reich`, the same version (09 DIST-INSTALLER). Tests: `PeIdentityTests`.
* **Authenticode Integrity Enforcement:**
  `Build.cmd` executes Authenticode signing when `FVS_SIGN_PFX` and `FVS_SIGN_PASS` environment variables are detected.
  ⚠️ **Agent Restriction**: `Build.cmd` triggers public GitHub Cloud release deployment and is strictly banned for autonomous agents. For local compilation checks and signing validation, agents must run `dev_build.cmd`.
* **`SIGNMANDATE_01` — AN ABSENT CERTIFICATE IS A BUILD FAILURE, NOT A DEFAULT.**
  `CodeSigning.SignIfNeeded` previously returned success with one informational line when `FVS_SIGN_PFX` was unset, so the normal outcome of running `Build.cmd` on a machine without a certificate was a **shipped, unsigned release** — and the line saying so scrolled past between two hundred others. Two controls depended on that signature and both were silently disarmed:
  1. **SmartScreen.** An unsigned download gets the full *"Windows protected your PC"* wall. The Win32 metadata block (`ISSUE_03`) exists precisely so the user has something reassuring to read at that moment; unsigned, it is a publisher field with no cryptographic backing.
  2. **`UPDATETRUST_01`.** The update pin anchors on the **running executable's** publisher. An unsigned running executable is not an anchor, so the pin degraded to hash-only — and the hash came from the same GitHub JSON document that supplied the download URL. Whoever controls that response controls the payload *and* its fingerprint in one move, and the payload is launched with `--install --auto-update`, i.e. elevated.
  An operator who genuinely wants an unsigned artifact (a local smoke test, a CI job that signs in a later stage) sets **`FVS_ALLOW_UNSIGNED=1`** and receives a loud, recorded acknowledgement. Absence of that variable **fails the build**.
* **`UPDATETRUST_02` — `NoAnchor` IS A REFUSAL, NOT A WARNING.**
  `UpdateService` previously logged one line on `TrustVerdict.NoAnchor` and fell through to `Process.Start`. Because production *was* the unsigned build, that degraded path was the **only** path that ever ran, and the attack `AuthenticodeVerifier` was written to close was never actually closed.
  The download is now deleted and the user is told to install the signed build by hand **once**; from then on they have an anchor and auto-update verifies normally.
  * ⚠️ This is a real, accepted regression: in-app auto-update stops working for installs that are themselves unsigned. Refusing to execute unverified code with elevation is the correct trade.
  * **`FVS_ALLOW_UNSIGNED_UPDATE=1`** restores the old behaviour for developers. It is read from the **environment** on purpose — it cannot be set by a downloaded payload, a settings file or a server response, so nothing an attacker controls can re-open the door.
  * The three-way `TrustVerdict` is unchanged: *what to do* about a missing anchor is policy, and policy belongs with the caller that owns the consequence, not with the primitive that reads the signature.
* **Mandatory Signing Failure Abort:**
  If certificate signing environment variables are present but the signing tool (`signtool.exe`) fails or returns a non-zero exit code, the build script MUST FAIL IMMEDIATELY. Silently producing or packaging an unsigned binary when signing was explicitly requested is classified as a severe security failure.
* **SIGNLOCAL_01 — local development certificate.** When `FVS_SIGN_PFX` is unset and `ssl-certificate\fvs-codesign.pfx` + `fvs-codesign.password.txt` exist, `CodeSigning` signs with that certificate (private root `FVS Local Development Root CA`; trust it once with `ssl-certificate\install-dev-root.cmd`). **SIGNLOCAL_02 (user decision 2026-09-26): `Program` PUBLISHES anyway**, with a warning. The private root is untrusted on every other machine, so the update NOTIFICATION works but in-app install is refused (UPDATETRUST_02, user is sent to the release page) and SmartScreen still warns. Release publishing requires a publicly trusted certificate via `FVS_SIGN_PFX`/`FVS_SIGN_PASS`, which always takes precedence. Secrets in that folder are excluded by its own `.gitignore`.
* **UPDATEPIN_01 — THE PUBLISHER ROOT IS BUILT INTO THE APP (no paid certificate).** The root's public
  certificate (`CN=FVS Local Development Root CA`, SHA-256 `C2E12D96…946A`, valid to 2036) is compiled
  into every copy (`Core/Infrastructure/PublisherRoot.cs`). An update is trusted when its Authenticode
  signature is INTACT (WinVerifyTrust: success, or only `CERT_E_UNTRUSTEDROOT`/`CERT_E_CHAINING` — a
  modified file reports `TRUST_E_BAD_DIGEST`) AND its signer chains to that root (custom root trust,
  code-signing EKU, timestamped so leaf expiry does not matter). The signed smart-update list is checked
  against the same root. This replaces SIGNLOCAL_02's consequence: in-app updates now install on EVERY
  PC, signed or unsigned running copy alike. A publicly trusted certificate (`FVS_SIGN_PFX`) remains
  accepted as before. What money still buys: no SmartScreen "unknown publisher" on a FIRST download.
  `CodeSigning` refuses to sign with a certificate that does not chain to the pinned root (a different
  root would make every installed copy refuse every future update).
* **UPDATEPIN_02 — the leaf renews itself.** Before signing, `CodeSigning.EnsureLocalLeaf` issues a new
  leaf from the SAME root (key `ssl-certificate\fvs-dev-root-ca.key.pem`, never in git) when the leaf is
  missing, unreadable, not from the pinned root, or within 90 days of expiry; old files are kept as
  `*.bak-<time>` (git-ignored). `build.cmd` and `dev_build.cmd` use the same certificate. Copies released
  BEFORE UPDATEPIN_01 still need one manual install of a newer version; after that, updates are automatic.

---

## 6. Auto-Update Lifecycle & Universal Build Versioning  {#SYS-AUTOUPDATE}
* **Universal Build Version Synchronization:** Every execution of `Build.cmd` via `FvsBuild` generates a uniform four-part timestamp version (`yyyy.MM.dd.HHmm`). `SynchronizeVersionFiles` synchronizes this exact version across:
  1. `version.txt` in the repository root.
  2. `Directory.Build.props` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>`, `<ProductVersion>`).
  3. `src/FreeVideoStudio.App/FreeVideoStudio.App.csproj`.
  4. `src/FreeVideoStudio.Core/FreeVideoStudio.Core.csproj`.
  5. NativeAOT compilation and publish flags (`-p:Version=`, `-p:AssemblyVersion=`, `-p:FileVersion=`, `-p:InformationalVersion=`).
* **Title Bar Version Invariant:** The running executable extracts its stamped version via `DeploymentLifecycle.GetCurrentVersion()` (reading Win32 `ProductVersion` and `FileVersion`, assembly metadata, and root `version.txt` fallbacks). Custom window title bars format `Free Video Studio v{version}` and `Free Video Studio - Video Merger v{version}` directly, ensuring zero discrepancies.
* **Version Parsing Robustness:** `DeploymentLifecycle.TryParseVersion` trims leading `'v'`/`'V'` prefixes before filtering numeric dot segments. Tags such as `v2026.09.12.0159` parse accurately into .NET `Version` objects (`2026.9.12.159`) with strict numerical comparison. Invalid or non-numeric inputs return `false` and guarantee a safe non-null `0.0` fallback.
* **Schema v13 (DUCKSTRENGTH_01):** adds `Defaults.DuckingEnabled` / `CarvingEnabled` (seeded ONCE from the legacy `Defaults.AudioProtection`, so a user who had protection off keeps it off) and `Defaults.DuckingStrength` / `CarvingStrength` (default 50 = tuned). Values the user sets are never reset by a later migration.
* **Schema v12 (LOUDSTD_REMOVED_01 / CLIPLEVEL_01 / VOLMUTE_01):** removes `LoudnessNormalizationPrompt` and `Defaults.AutoVoiceNormalization` (old keys are ignored on read and dropped on the next write); adds `MergerMatchClipLoudness` (default OFF) and `PreviewMuted` (default OFF). The master LEVEL stays in the session state (`MainVolume`), written by `MasterVolumePersistence` off the UI thread.
* **Schema v10 (SYS-AISETTINGS):** Introduces configuration for Universal AI Smart Tracking Zoom:
  * `GeminiApiKey` (string, default `""`): Google Gemini API key used for vision model calls, persisted under the settings mutex.
  * `GeminiModelName` (string, default `"gemini-2.5-flash"`): selected Gemini model endpoint.
  * `AiZoomBaseScale` (double, default `2.2`): camera scale during still or slow gameplay ($v \le v_{\text{slow}}$).
  * `AiZoomMinScale` (double, default `1.3`): minimum wide camera scale during high-velocity gameplay ($v \ge v_{\text{fast}}$).
  * `AiZoomAvoidHud` (bool, default `true`): steers camera crop bounds away from game HUD regions.
  * `AiZoomDeadbandPercent` (double, default `2.0`): suppresses micro-jitter below $2\%$ of frame dimensions.
  * `AiMagicWandCloudConsent` (bool, default `false`, `AIHUD_05`): the user's yes to the Crop Tool Magic Wand sending ONE frozen frame to the configured AI provider. Additive (missing reads as false), so no schema bump. Holds no credential.
* **Magic Wand AI key handling (`AIHUD_01`, `GeminiHudDetectionService`):** the key is read from settings at call time and sent ONLY in the `x-goog-api-key` header — never in a URL — so no URL it builds, logs or reports is secret-bearing. Every log line, fault detail and result detail passes through `GeminiHudDetectionService.Redact`. The key is never cached (`AiHudResultCache` keys are source/frame/model hashes) and never written to project (`.fvsproj`), recovery or crop-config files; `GeminiHudDetectionServiceTests.ApiKey_*` enforce this.
  * **Migration Invariant:** Upgrading from schema v9 or older preserves all existing preferences and initializes AI tracking fields to defaults. Files from a higher schema version are loaded best-effort without destructive rewrites.
* **API Key Redaction & Transient Network Retries (`PostWithRetryAsync`):**
  * HTTP request URLs containing API keys are redacted before writing to log sinks (`Regex.Replace(url, @"key=[^&]+", "key=REDACTED")`).
  * Network drops and transient HTTP status codes (`429`, `500`, `503`, `504`) execute up to 3 automatic retries with exponential backoff delays ($2\,\text{s}, 4\,\text{s}, 6\,\text{s}$). Terminal failures log full HTTP response bodies to `RuntimeLog.Fail("GEMINI_API", ...)` and `CoreLogger.Warn` for rapid diagnostics.
* **Schema v9 (REMOVEUX_01):** upgrading from v8 or older sets `ConfirmVideoMergerRemove = false` once (product decision 2026-09-27: removal is instant and undoable); a v9 file keeps the user's choice. `ApplicationPaths.LaneCacheDirectory` (`ProgramDataRoot/cache/lanes`) holds the Merger's per-clip filmstrip PNGs and waveform peaks (LANECACHE_02), best-effort, pruned to 800 files.
* **UPDATECONSENT_01 — no network at startup, and only after a yes (2026-10-12).** An unknown program that contacts a server and downloads executables the moment it starts is what downloader malware does, and antivirus behaviour engines score it that way. `RunStartupCheckAsync` therefore (1) asks ONCE — "Check for updates automatically?" (`AskUpdateConsentAsync`, a `ConfirmDialogWindow` over the main window, 3 s after it opens) — while `AppSettings.UpdateConsentAsked` is false, and sends nothing before the answer; Yes/No are stored by `SettingsManager.SetUpdateConsent` (`AutoUpdateChecks` = the answer, `UpdateConsentAsked` = true); closing the question counts as "not now" and it is asked on the next start; (2) waits `StartupGracePeriod` = **5 minutes** after the window opens before the first request (dev source: 2 s). `UpdateConsentAsked` is additive (missing reads as false, no schema bump), so every existing install is asked once as well. Changing the Settings › About checkbox also records the answer. Help › Check for Updates is unaffected. `--upgrade-health` launches never ask. Meme/song cloud sync and the AI features were already user-triggered only. Test: `VersionAndUpdaterTests.UpdateConsent01_*`.
* **Schema v7 Migration Invariant:** When upgrading from older application installations lacking update checking (or whenever `settings.json` lacks an explicit `AutoUpdateChecks` configuration), `SettingsManager` automatically initializes and persists `AutoUpdateChecks = true`. On fresh installs without an existing config file, default settings with `AutoUpdateChecks = true` are saved immediately to disk, ensuring new releases are never silently missed. Since UPDATECONSENT_01 that default only takes effect after the user's yes.
* **Network Stall Guard:** `UpdateDownloader` wraps chunk stream reads in a 45-second stall cancellation timeout (`CancellationTokenSource.CreateLinkedTokenSource`). A frozen HTTP pipe stops the download and is REPORTED as a failure ("stopped receiving data for 45 seconds"); it is never treated as the user's own Cancel (UPDATEUX_03 — it used to close the window silently).
* **Release Notes:** GitHub release `body` is read during the probe (the question window that showed it was removed with BGUPDATE_01).
* **State Persistence Protocol:**
  * Last startup probe timestamp is recorded in `update_last_check_utc.txt` under `UiStateStore` enforcing a 15-minute rate limit (`MinimumIntervalBetweenChecks`) between startup probes.
  * UPDATEUX_06 — the time and plain-English result of every check, startup or manual (up to date / version X available / skipped / could not reach GitHub, and why), is written to `update_last_result.txt` and shown in Settings › About as "Last checked 5 minutes ago: …". The startup check still never interrupts the user over its own problems; it is no longer invisible.
  * Explicit version skips write the release tag to `update_skipped_tag.txt`. Users can inspect or clear this filter at any time via the About tab in Settings.
* **BGUPDATE_01 — background update, one question (R2/R3).** A newer version found by the startup
  check (or Help › Check for Updates) is downloaded in the BACKGROUND (`UpdateService.StartBackgroundUpdate`
  → `UpdatePreparer`) while the user keeps working; the update card (`UpdateToastWindow`, 04) shows
  progress. When everything is verified the installer is started and waits (`--wait-pid`, plus
  `--smart-update <folder>` for a smart update); the card then asks ONE question: **Restart & update**
  (closes the app through its NORMAL close path — unsaved work is still asked about, nothing is killed)
  or **Later** (installs by itself when the app is next closed). `PAUSE` keeps the partial download.
  "Skip this version" is in Settings › About (`SkipPendingVersion`: stops the download or kills the
  still-WAITING installer, which has changed nothing yet). This supersedes UPDATEUX_01/04/05; UPDATEUX_02
  (size/time text), UPDATEUX_03 (progress wording) and UPDATEUX_06 (Last checked) remain.
* **DLRESUME_01 — downloads survive bad connections.** Each release downloads into
  `%LOCALAPPDATA%\FreeVideoStudioUpdates\Downloads\<tag>\` (NOTEMPEXEC_01; it was `%TEMP%\FVS_AutoUpdate`,
  which is removed on the next update and by the uninstaller; other tags' folders are removed). A broken transfer is retried by
  itself (2 s, 5 s, 15 s) and every retry — also after an app restart — continues from the last byte
  with an HTTP Range request; a server that ignores Range restarts the file cleanly. Every finished file
  is hashed before use; a mismatch deletes it.
* **BINDELTA_02 — smart-update patching uses bounded memory** (09 DIST-SMART). The new main program is
  rebuilt by streaming the installed EXE + patch through 64 KiB buffers into `<sha>.partial`; it is
  promoted only after length, patch-header hashes, the signed update.json hash and the publisher check
  pass. Any failure deletes the partial and falls back as before (compact, then full installer).
---

## SYS-VERIFYTOOL — The Fix-Sentinel Check Is Code Now  {#SYS-VERIFYTOOL}

`VERIFY_PATCHES` no longer parses anything. The list lives in `build/sentinels.txt` and the checker
is `build/FvsVerify`, with its own tests in `tests/FvsVerify.Tests`. `dev.cmd` runs it and reads the
exit code; the script went from 29KB to 10KB and contains no list at all.

⚠️ **Why it moved.** As a batch `FOR` list this check broke three separate times and every failure
was silent — `VERIFYLOOP_01` (byte-offset label seeks skipped two entries and reported a clean
pass), `LISTCOMMENT_01` (`REM` is not a comment inside a FOR list, so each annotation became six or
seven bogus sentinels), `BATCHPARENS_01/02` (one round bracket closed the list early and the next
word was executed as a command) — on top of `VERIFYHALT_01`, where the result was assigned and never
read, so for its entire existence the subroutine could not fail.

None of those are sentinel bugs. They are what a list of 201 strings, a comment syntax and a file
search cost in a language with no list type, no comments inside a list, no escaping and — decisively
— no way to write a test against the result. Invariant #8 says every rule that can be a test is a
test; this one now is, twice over: `FvsVerify.Tests` unit-tests the parser and checker, and
`ArchitectureRuleTests.EveryFixSentinelStillResolves` runs the same functions over the same file in
CI. `DevCmdDelegatesTheSentinelCheckRatherThanParsingIt` fails if the list is ever moved back.

**To add a sentinel: add one `TAG=path` line to `build/sentinels.txt`.** That is the whole procedure.

---

## SYS-DIAGREPORT — A Bundle The User Can Actually Send  {#SYS-DIAGREPORT}

The fault tiers route every classified failure to a rotating log under the per-user root (`%LOCALAPPDATA%\FreeVideoStudio\logs`, SYS-USERSCOPE). That is the
right destination for the failure and the wrong one for the DIAGNOSIS: nobody navigates there, finds
the right file among the rotation, and attaches it to a report.

⚠️ **This matters more here than in most applications.** The central risk in this product is
hardware it has never run on: `HardwareScanner` chooses between NVENC, AMF, QSV and d3d11va at
runtime against a matrix validated on one machine. "Export fails on some AMD cards" is unactionable.

`DiagnosticReport` (Core) builds a plain-text bundle — machine profile, the chosen encoder, the tail
of the log, the recovery state — and `DiagnosticBundle` (App) writes it under
`ProgramDataRoot\Diagnostics` (the per-user root, `%LOCALAPPDATA%\FreeVideoStudio\Diagnostics`).

* **Nothing uploads.** There is deliberately no network code. Auto-upload is a consent problem, a
  privacy problem and a hosting problem, and none of those need solving before the diagnosis problem
  is. A file the user can read in full and choose to send is the honest version of telemetry.
* **The user can read every byte, and that constrains what goes in.** `Redact` rewrites
  `C:\Users\someone\` to `C:\Users\<user>\`. No user name, no machine name. The log tail is a
  ring buffer of the last 400 lines, because a 40MB report is the same as no report.

---

## SYS-PAYLOADSPLIT — See `09_DISTRIBUTION_AND_RELEASE.md`  {#SYS-PAYLOADSPLIT}

The update path is bound by `09` §3 (DIST-SPLIT): a release may publish an app-only package beside
the full installer, and `UpdateService` takes it only when the installed runtime fingerprint matches
what the release advertises. Every uncertainty resolves to the full installer.
The compact installer uses the same transactional installation worker as the full installer;
missing runtime files are copied only when their SHA-256 matches the embedded signed manifest.
Since NODROPPER_01 no compact package is built or published (it embedded the app inside a second
program); the client side stays for releases that still carry one. See `09` DIST-SPLIT / DIST-INSTALLER.

⚠️ `UpdateService.cs` is CO-GOVERNED by this spec and `09`. Reading one is not compliance.

## Transactional brand migration {#SYS-UPGRADE}

`UpgradeCoordinator`, `UpgradeInstallWorker`, `UpgradeRegistration`, `InstallDiscovery`,
`UpgradeChannel`, `DirectoryUpgrade`, `UserDataUpgrade`, `InstallPayload` and `UpgradeFiles`
implement UPGRADE_02 through UPGRADE_10. `Program` dispatches deployment helpers before any
settings initialization. The install entry point always uses this flow; uninstall remains separate.

* The original, unelevated user owns the broker, settings migration and first launch. A helper
  elevated through Windows UAC exclusively owns machine files and registration. Running the broker
  with administrator credentials is refused with instructions to start the installer normally.
* Downloads do not interrupt work. The update helper waits for the originating process to exit
  normally. Other app processes, including other users' sessions, postpone installation. No
  pre-existing editing process is killed. A newly launched, disabled verification window can be
  terminated if verification fails, before restoring the old files.
* Every install comes from a SIGNED file list (NODROPPER_01): the Inno Setup installer unpacks the
  release beside its signed `update.json`, the in-app updater downloads the changed files beside it.
  The worker verifies the signature itself, copies each file into its protected folder and checks it
  by SHA-256 (`InstallPayload.ExtractFromSmartUpdate`); identical installed files are reused. A program
  started for `--install` without a signed list refuses ("This program is not an installer…"). There
  is no embedded payload any more. The raw installed executable is also signed,
  preserving the publisher trust anchor for the next update.
* Machine and per-user operations hold separate named gates. Every directory switch writes a
  durable journal before renaming. The machine's protected `Committed` record is the decision
  used to recover participating user-data journals after an interrupted broker. A `userReady`
  marker distinguishes fully prepared user data from an interrupted preparation.
* The updating app's settings win. Current-brand updaters send `--source-current`; older updaters
  use verified legacy installation discovery. Each conflicting file is preserved separately under
  `MigrationConflicts`, rather than merging arbitrary fields. Unreadable or newer settings stop
  migration without resetting preferences. Local and Roaming data move; personal media stays put.
* All roots must be non-overlapping, verified, plain directories on the transaction store's volume.
  Linked roots and cross-volume installations stop safely for manual recovery; no recursive drive
  scan or best-guess deletion is permitted. Existing unrelated destination folders are refused.
* First launch verifies payload hashes, native library loading, ffprobe startup, settings loading
  and a Loaded main window. Editing stays disabled until commit. A PID by itself is insufficient.
  `--no-launch` still performs the health handshake, then closes the verified window.
* User shell changes resolve the current user's Known Folders; machine changes use Public Desktop
  and common Start Menu. Owned shortcuts are identified by target. An unrelated name collision
  is preserved. A new shortcut is created before old shortcuts are removed. Offline user Desktops
  leave pending work for the next login/start; a completed operation does not keep recreating a
  subsequently deleted icon. Other accounts complete their own migration at login/start.
* Registry cleanup only removes captured legacy installation keys that have not subsequently
  changed. Default application selections are preserved. Rollback restores owned Open With values
  without replacing other applications' entries. Cleanup failures after commit cannot remove the
  working replacement.
* ONEBACKUP_01 — only the backup of the version just replaced is kept (at most 30 days); the backup of
  any OLDER update is removed as soon as a newer one is committed. Every update used to keep a full
  ~433 MB copy for 30 days. FASTINSTALL_01 — the candidate is no longer a copy of the whole old
  install: new files are moved in, unchanged installed files are hard-linked (same volume, admin-only
  folders), only stray old files are copied, and one full `InstallPayload.Verify` stays the authority.
  Space is checked for the new files only. Uncommitted and failed
  transactions are never automatically pruned. Conflicts in the active tree survive backup expiry.
  Machine maintenance is serialized with installation; user maintenance runs under that user.
  NOSYSTEMTASK_01 — machine maintenance runs ONLY at the end of an install or update
  (`UpgradeInstallWorker.MaintainAsync(ownsInstallerGate: true)`). There is no recurring scheduled task:
  the daily `SYSTEM` / `HIGHEST` task that used to run `--upgrade-maintenance` is no longer created, and
  `MaintainAsync` deletes the `FreeVideoStudio` task left by an older version (an old task that fires
  first lands in `MaintainAsync` and removes itself). Consequence, accepted: a backup's 30-day expiry is
  applied at the next update instead of on the day it expires.

### Antivirus-clean deployment behaviour  {#SYS-AVCLEAN}

New releases were reported as a trojan by Microsoft Defender and others. Besides the missing
publicly trusted signature (SYS-SIGNING), the binary used techniques malware uses. Each was replaced
with a plain Windows API doing the same job. Do NOT reintroduce any of them:

* **NODROPPER_01 — no program that carries programs in its own resources** (the dropper pattern behind
  `Trojan:Win32/Wacatac.*!ml`). The release is an Inno Setup package (`09` DIST-INSTALLER); the program has
  no embedded payload, and `UpgradeInstallWorker` installs only from a signed file list. Enforced by
  `AvCleanRuleTests.NoDropper01_*` (no `EmbeddedResource` of a zip/exe/dll, no payload resource read, the
  Inno script stays non-elevated with no registry/uninstall sections).
* **NOTEMPEXEC_01 — nothing elevated runs from `%TEMP%`, and nothing copies itself there.** The uninstaller
  used to copy itself and its window libraries into `%TEMP%` and start that copy as administrator; it now
  starts its elevated worker from where it is (Program Files) and that worker removes everything it can.
  What is still in use — the worker itself, its hard-linked twin `FreeVideoStudio.exe`, its two window
  libraries — and the then-empty program folder go on Windows' own delete-on-restart list
  (`QueueSelfCleanup`, `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`), so they disappear at the next restart
  and no copy is ever made. Update downloads and the staged copy of the elevated update worker moved from
  `%TEMP%\FVS_AutoUpdate` / `%TEMP%\FVS_Upgrade` to `%LOCALAPPDATA%\FreeVideoStudioUpdates\Downloads` /
  `\Stage` (`DeploymentFootprint.UpdatesRoot`, deliberately OUTSIDE `%LOCALAPPDATA%\FreeVideoStudio`,
  which the update transaction itself moves). The old folders are still accepted from older updaters,
  swept on the next update and by the uninstaller, which also removes `UpdatesRoot`. The cleanup worker
  no longer unpacks window libraries into `%TEMP%\FreeVideoStudio_SetupUI_*` (old ones are swept).
  Enforced by `AvCleanRuleTests.NoTempExec01_*` and `VersionAndUpdaterTests.StagingFolderArgumentIsAcceptedOnlyInsideTheUpgradeStageRoot`.
* **NOSYSTEMTASK_01 — no recurring SYSTEM scheduled task** (MITRE T1053.005). See SYS-UPGRADE.
* **NOPOWERSHELL_01 — no PowerShell for shortcuts** (MITRE T1059.001). `ShellLinkWriter.CreateAsync` /
  `ShellLinkWriter.ReadTargetAsync` call the Shell's `IShellLinkW` + `IPersistFile` COM objects through
  their vtables on a private STA thread (NativeAOT-safe, like `D3D11Interop`). `UpgradeRegistration.ReadTargetAsync`
  forwards to it; the `-EncodedCommand` PowerShell runner is gone. Every failure is an `IOException`
  (the upgrade code's "retry later"); a link without a file-system target reads as an empty string.
* **NOPOWERSHELL_02 — RDP Auto-Fix without a script host.** See `03` FFM RDP Auto-Fix.
* **NOSELFDELETE_01 — no "cmd /c ping … & del" self-deletion** (Sigma "ping + del combined execution",
  MITRE T1070.004). `DeploymentLifecycle.QueueSelfCleanup` deletes the uninstaller's temp folder and the
  `FVS_*`/`fvs_*` temp files directly; files still in use (the running uninstaller and its window
  libraries) and their folders are queued with `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`, files before
  folders. Since NOTEMPEXEC_01 there is no temporary copy: the queued items are the uninstaller itself,
  its hard-linked twin, its window libraries and the program folder, all removed at the next restart.
* **NOCMD_01 — links open through the shell.** The WhatsApp share (`MainWindow.OnSuccessAction`,
  `VideoMergerWindow.OnSuccessAction`) starts the `whatsapp://` URI with `UseShellExecute = true` instead of a
  hidden `cmd /c start`.
* No `powershell.exe`, `cmd.exe /c`, `-ExecutionPolicy Bypass`, `-WindowStyle Hidden` or `-EncodedCommand`
  in production code under `src/`. Enforced by `AvCleanRuleTests` (App tests).

### No silent moment during install or update  {#SYS-UPGRADEUX}

Every bullet below is display-only. None of them changes the protocol, the journals, the order of
steps, the gates, or whether a failure rolls back.

* **UPGRADEUX_01 — the broker has a window.** `--upgrade-broker` runs through `UpgradeBrokerHost`,
  which starts Avalonia (styles only: `AvaloniaApp` returns before `SettingsManager.Load`, theme and
  UI sounds when `UpgradeBrokerHost.IsActive`) and shows `UpgradeProgressWindow`: a checklist —
  Closing the old version → Making sure Free Video Studio is closed → Asking Windows for permission →
  Keeping your settings → Unpacking → Installing files → Testing that it works → Updating shortcuts →
  Opening the new version — with a live bar. The window stays hidden while the user is still working
  (`--wait-pid`) and appears the moment the app closes. The elevated worker reports
  `progress` messages ("phase|fraction", `UpgradeChannel.ProgressKind`) through `ProgressRelay`;
  `UpgradeChannel.ExpectAsync` hands them to `Progress` and never lets one satisfy or break an
  expected reply. `InstallPayload.Extract`/`Verify` and `DirectoryUpgrade.Prepare` take optional,
  monotonic progress callbacks. `LaunchAsync` stages `libSkiaSharp.dll`/`libHarfBuzzSharp.dll`
  beside the broker (from beside the executable — the installed app or the Inno Setup installer's
  unpacked release — or the installed folder; there is no embedded payload since NODROPPER_01); if they cannot be found or the window cannot start, the broker runs
  headless with native message boxes (`HeadlessUpgradeProgress`). Fresh installs use the same window.
  The per-user gate is taken AFTER the wait for the old app, so a Merger or Crop Tool started while
  an update waits no longer fails with "An update is finishing for this Windows account".
* **UPGRADEUX_02 — Windows' permission prompt is announced first.** Before `Verb = "runas"` the
  window says what Windows will ask and to click Yes. Clicking No (`ERROR_CANCELLED`, 1223) is
  reported as "Update cancelled … nothing was changed", not as a failure.
* **UPGRADEUX_03 — an open app is a question, not an error after UAC.** Before UAC the broker runs
  `InstallDiscovery.FindOpenApps` (non-elevated twin of `EnsureIdle`). If a copy is open:
  "CLOSE IT AND CONTINUE" (sends a normal close, so the app's own save question still appears;
  nothing is killed) or "CANCEL". A copy in another Windows account offers "TRY AGAIN". `EnsureIdle`
  in the worker remains the authority.
* **UPGRADEUX_04 — failures are plain and put the user back.** Every failure ends in the window with
  a title, a reason and what to do next. When nothing changed and no recovery is pending, the version
  the user was using is opened again ("Your previous version has been opened again"); never while a
  rollback is pending, because that would start recovery and a second UAC prompt at once. A second
  broker started while one runs says "Another update is already running" instead of exiting silently.
* **UPGRADEUX_05 — the first launch explains itself.** The new app still opens disabled until commit;
  `UpgradeFinishedNotice` shows "Finishing the update…" over it, then "✓ Update complete — updated
  from version X to version Y" (`--upgrade-kind`, `--upgrade-from`, passed by the broker), or
  "✓ Free Video Studio is installed". The broker's own window shows "✓ All done" and closes itself
  after three seconds.
* **UPGRADEUX_06 — no silent copy before the window.** The launcher used to copy its whole
  executable (322 MB for the full installer) into its staging folder (`%TEMP%\FVS_Upgrade\<id>` then;
  `%LOCALAPPDATA%\FreeVideoStudioUpdates\Stage\<id>` since NOTEMPEXEC_01) and verify it before
  starting the broker, so a double-clicked installer showed nothing for seconds. Now, when the
  executable is NOT inside a folder the update replaces (`RunsFromAnInstallRoot`: the install path,
  any discovered root, the legacy spaced folder; any doubt counts as inside), the launcher stages only
  the two window libraries and starts the broker straight from the original file with
  `--upgrade-stage <folder>`. The broker sets `SetDllDirectory` to it, shows its window, and copies the
  installer there IN THE BACKGROUND (`PrepareWorkerCopy`, started at once — during the wait for the
  old app in an in-app update). The elevated worker is still started only from that fresh staging
  copy, never from Downloads, so no planted DLL beside a downloaded file can reach an administrator
  process. `StageFromArgs` accepts the folder only as an existing direct child of
  `UpgradeCoordinator.StageRoot` (`%LOCALAPPDATA%\FreeVideoStudioUpdates\Stage`, NOTEMPEXEC_01). If the copy is still running when Windows is about to be asked, the window says
  "Getting the installer ready…". Recovery and installs from inside a program folder keep the old
  staged-broker path (a running executable cannot be moved).

Local regression coverage for this section: `VersionAndUpdaterTests` (UPDATEUX/UPGRADEUX section: wording, view-model state machines,
progress messages never satisfying a reply) and `UpgradeProgressReportingTests` (monotonic progress,
verification unchanged). Real-Windows verification still required: UAC Yes/No, close-it-for-me with
unsaved work, compact and full installer windows, first-launch notice, a double-clicked
installer from Downloads (window within a second, worker from `%LOCALAPPDATA%\FreeVideoStudioUpdates\Stage`).

Local regression coverage: `UpgradeTransactionTests` injects failure at each directory rename,
checks source precedence, retention, conflicting files, journal traversal and compact runtime
corruption. `CompactUpdateTests` rejects unexpected archive entries and checks settings preservation.
Before production rollout, exercise real Windows UAC (same/different admin), OneDrive and network
Desktop offline/online, two logged-in accounts, antivirus locks, power loss, and representative old
signed releases in disposable Windows installations. Unit tests do not establish those OS outcomes.

---

## SYS-EDITHOT — No Disk, No Named Mutex On The Edit Path  {#SYS-EDITHOT}
* **EDITHOT_01:** `ProjectSession.Capture()` runs on every edit tick. It now reads the HUD mask from `LiveMaskCache.Current` (a memory snapshot refreshed on the thread pool by a FileSystemWatcher and by profile-name changes) and the source fingerprint from a per-path cache. Only a user-initiated save (`Capture(forExplicitSave: true)`) touches the disk and the mutex (`LiveMaskCache.ReadNow`). Autosave uses the snapshots.
* **EDITHOT_02:** `SaveRecoveryState` pushes undo immediately (memory) and writes the recovery file 750 ms after the LAST change (`MainWindow.Recovery.cs`). `sync: true` callers write immediately.

## SYS-WRITEORDER — Persistence Never Goes Backwards  {#SYS-WRITEORDER}
* **WRITEORDER_01 (`RecoveryManager`):** every whole-file save and clear takes a version from a STATIC counter at call time and applies only if it is newer than the last one applied to that file. Before, a `SaveStateAsync` queued before `ClearState()` (undo-to-empty, clean shutdown) could resurrect the file, and ordering was per instance. `UpdateGranularSession` is a serialised sub-key merge and is not versioned.
* **WRITEORDER_02 (`NamedPipeStateServer`):** `_flushGate` is held from snapshot to rename, `_stateVersion` makes writes monotonic, and a failed write re-arms `_isDirty`. Clients never race these flushes with their own file writes: see [IPCOWNER_01](#SYS-IPCLIFETIME).
* **WRITEORDER_03 (`VoiceOverRecoveryManager`, VORECOVERY_01):** voiceover recovery indexes are per (canonical video, session) `voiceover_recovery_{videoKey}_{sessionId}.json`, written off the UI thread, process-serialized, merged by take id (records another owner holds are preserved), via a unique `{path}.{guid}.tmp` + write-through flush + atomic replace; an unreadable existing index is never overwritten or deleted. Uncommitted takes remain durable across unexpected close or crash until an explicit Apply commit (`CommitTakesAsync`, committed ids only) or a proven operator Discard. A file is deleted only when no durable record — this manager's or anyone else's — remains in it. Full contract: [AUD-VOICEOVER](02_AUDIO_ENGINE_MASTERING.md#AUD-VOICEOVER).

## SYS-USERSCOPE — Mutable State Is Per Windows User  {#SYS-USERSCOPE}
* **USERSCOPE_01:** the default root is `%LOCALAPPDATA%\FreeVideoStudio` (`ApplicationPaths.DefaultUserRoot`). The old `%ProgramData%` root was shared by every account, while the single-instance guard is per user.
  * First launch per user copies the legacy machine root once (`MigrateLegacyMachineRoot`, marker `.migrated_from_programdata`). It never copies locks, `recovery_v2.json`, `logs`, `Diagnostics` or `voiceovers`, and never modifies the legacy folder.
  * The installer no longer creates the ProgramData folder or grants `Users:F`. `EnsureWritableDirectories` no longer runs `icacls`.
  * Named mutexes guarding per-user files use `NamedSystemMutex.UserScopedName` (`Global\<name>_<SID>`). A mutex that cannot be opened raises `LockException`, never a raw `UnauthorizedAccessException`.

### REBRAND_01 — Free Video Studio storage migration  {#SYS-REBRAND}

Canonical per-user roots are `%APPDATA%\FreeVideoStudio` and `%LOCALAPPDATA%\FreeVideoStudio`. `AppDataPaths` reads the two historical names from its embedded `LegacyAppDataNames.txt` resource. Before creating either root, it copies existing files (including nested presets and recovery backups) into a sibling staging directory and atomically renames the complete directory into place. Existing destinations are untouched. Source directories are retained. Failed copies leave no partial destination, continue using the legacy root, and retry on the next launch. The spaced local legacy root takes precedence over the older compact root. `ApplicationPaths.CreateDefault` invokes this before the existing machine-to-user migration; the development/test override remains isolated. Settings locking uses the `Global\FvsFreeVideoStudioMutex_` prefix with the existing per-user scope.

The legacy resource contains the previous compact and spaced product names (listed only in `REBRAND_MIGRATION.md`). Roaming migration checks the compact name first; local migration checks the spaced name first. If both sources contain the same relative filename, the first source wins and the originals remain untouched. New destinations are never merged into or overwritten. If copying fails with an I/O or permission error, the resolver uses the first existing legacy source for that process and retries migration on the next launch; staging cleanup is best-effort.

This per-user rebrand migration copies recovery backups and is distinct from `MigrateLegacyMachineRoot`, which deliberately excludes another account's session/recovery files. `LocalCacheDir` is also the default mutable-state root exposed through the historically named `ApplicationPaths.ProgramDataRoot`; it is not limited to disposable caches. `LegacyRoamingUiStateDirectory` resolves to `AppDataDir/Settings` for the existing UI-state migration. `FVS_PROGRAMDATA_ROOT` bypasses default-root migration in development and tests. `REBRAND_01` is registered in `build/sentinels.txt`; `AppDataPathsTests` covers recursive copies, unchanged existing destinations, failed-copy retries and precedence between legacy roots.

### REBRAND_02 — previous-brand residue removal  {#SYS-REBRAND-SWEEP}

REBRAND_01 and UPGRADE_04 never delete their sources, so `Program` starts `LegacyResidueSweep.RunForCurrentUser` on a background task on every normal UI launch (not in sibling tool processes, deployment helpers, `--upgrade-health`, or under `FVS_PROGRAMDATA_ROOT`). It does nothing while a previous-brand executable exists in Program Files, a previous-brand process runs, or a journal under `%LOCALAPPDATA%`/`%APPDATA%\FreeVideoStudioMigration` is not `Committed`/`Pruned`. Otherwise:

* Legacy `%TEMP%` roots (`LegacyTempNames`) are scratch. Rescued renders (`*-RECOVERED-*.mp4`) are moved into `ApplicationPaths.TempDirectory` first, renamed to `FreeVideoStudio-RECOVERED-…` (Merger rescues keep `Merged-Videos-RECOVERED-…`), never overwriting (a `-2`, `-3` … suffix is added). If any rescue cannot be moved, the folder is kept.
* A legacy Local/Roaming root is deleted only when the current root exists and every legacy file exists at the same relative path in the current root. Otherwise it is kept and retried on the next launch.
* The shared `%ProgramData%` legacy root is never deleted here. The elevated uninstaller removes it with every legacy user root when user data is removed (`DeploymentFootprint.GetDirectoryPurgeTargets`, `includeUserData`).

The sweep never throws; failures are logged under the `Rebrand` tag. `RebrandTests` covers rescue moves, collision-free renames, mirrored versus partial roots, the closed safety gate, and the repository-wide previous-name allow list.

### NOSPACE_01 — Folder names on disk never contain spaces  {#SYS-NOSPACE}

"Free Video Studio" is the DISPLAY name (title bars, dialogs, Apps & features, shortcut labels such as `Free Video Studio.lnk`, the uninstall registry key). Every FOLDER the app creates uses `ApplicationPaths.AppDirectoryName` = `FreeVideoStudio`:

| Folder | Before | Now |
| :--- | :--- | :--- |
| Install | `%ProgramFiles%\Free Video Studio` | `%ProgramFiles%\FreeVideoStudio` (`DeploymentFootprint.InstallFolder`) |
| Machine data (legacy, purge only) | `%ProgramData%\Free Video Studio` | `%ProgramData%\FreeVideoStudio` |
| Installer scratch | `%TEMP%\FreeVideoStudio\Free Video Studio` | `%TEMP%\FreeVideoStudio\FreeVideoStudio` (`TempAppFolder`; a subfolder on purpose, because `%TEMP%\FreeVideoStudio` holds rescued renders) |
| Memes | `Videos\Free Video Studio\Memes` | `Videos\FreeVideoStudio\Memes` (MEMEFOLDER_02, `03` FFM-MEMELIB) |

Moving an existing install reuses the transactional upgrade (SYS-UPGRADE) unchanged:

* `InstallDiscovery.FindRoots` treats `%ProgramFiles%\Free Video Studio` (`DeploymentFootprint.LegacyInstallFolderName`) as a legacy root exactly like a previous-brand folder, so `DirectoryUpgrade` backs it up, builds the new folder and keeps the backup for 30 days. Shortcuts are owned by target, so the machine Start menu / Public Desktop and the installing user's Desktop are rewritten to the new path; the uninstall key's `InstallLocation` and Open With commands point at the new path.
* **Compact updates:** `InstallDiscovery.ReuseRoot` picks the first root holding `install.manifest.json` (Destination first). It used to be Destination only, which does not exist yet during the move, so every compact update of such a machine would have demanded the full installer.
* **Old journals:** machine journals written before NOSPACE_01 name the spaced folder as `Destination`. `UpgradeInstallWorker.Transactions` accepts both `InstallDiscovery.KnownDestinations` and builds the root set from the journal's own destination; rejecting them would have blocked every later install on that machine.
* **Previous-brand precedence:** the spaced current-brand root does not count as a "legacy" source for `preferLegacy`; only previous-brand roots do.
* **Other Windows accounts:** an account that already finished its migration never re-ran `CompleteUserAsync`. `RetargetAfterLaterMachineMoveAsync` now runs once per newer committed machine journal (`retargetedMachine` in the user session) and repoints that user's EXISTING app shortcuts (not uninstall shortcuts) to the new executable. It creates and deletes nothing and never blocks startup.
* Uninstall and the zero-footprint check include the old spaced install, ProgramData and scratch folders.

`MemeLibraryTests.AppFoldersHaveNoSpaces` guards the folder names. Real-Windows verification still required: upgrade from a spaced install (full and compact), a second account's Desktop icon, uninstall afterwards.
