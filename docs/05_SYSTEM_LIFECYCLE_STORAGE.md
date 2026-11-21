# SPECIFICATION 05: SYSTEM LIFECYCLE & STORAGE

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `developer_tools/Pull_Code_files.py`, `tests/DeveloperTools.Tests/test_pull_code_files.py` | `run_aggregator`, `collect_entries`, `read_entry`, `ExportTests` | `CODEEXPORT_01`, `SYS-CODEEXPORT` | Offline AI attachment exports, coverage accounting, encoding, credential filtering, non-destructive output and regression tests. |
| `src/FreeVideoStudio.App/DeploymentLifecycle.cs` | `DeploymentLifecycle` | `RunUninstallWorkerAsync`, `ShouldHandle`, `RunAsync`, `QueueSelfCleanup`, `NOTEMPEXEC_01`, `NOSELFDELETE_01` | OS installation/uninstallation installer-gate semaphore and elevated install/uninstall workers (the uninstall worker runs in place from the program folder). **⚠ CO-GOVERNED BY: 09** |
| `src/FreeVideoStudio.Core/Infrastructure/TempHousekeeper.cs` | `TempHousekeeper` | `RunAtStartup`, `RunAtShutdown`, `StartupInterval`, `TEMPCLEAN_01` | Startup/exit removal of known scratch, idle job folders, old freeze renders and stale undo histories (SYS-STORAGE-TABLE); tests in `tests/FreeVideoStudio.Core.Tests/TempHousekeeperTests.cs`. |
| `src/FreeVideoStudio.Core/Infrastructure/SafeDirectoryCleanup.cs` | `SafeDirectoryCleanup` | `DeleteTree`, `DeleteFile`, `CLEANUPBOUND_01` | Windows handle-bound deletion; junction/ancestor/rename/locked-file coverage in `tests/FreeVideoStudio.Core.Tests/SafeDirectoryCleanupTests.cs`. |
| `src/FreeVideoStudio.App/DeploymentFootprint.cs` | `DeploymentFootprint` | `UpdatesRoot`, `UpdateDownloadsRoot`, `UpdateStageRoot`, `LegacyUpdateDownloadsRoot`, `LegacyUpdateStageRoot`, `GetDirectoryPurgeTargets`, `NOTEMPEXEC_01` | Install, data, update-scratch and legacy paths; what the uninstaller removes. |
| `src/FreeVideoStudio.App/RuntimeLog.cs` | `RuntimeLog` | `LogMutexName`, `InitializeAppName`, `ResetForProcess`, `Info` | Decoupled asynchronous producer-consumer logging pipeline. **⚠ CO-GOVERNED BY: 10** |
| `src/FreeVideoStudio.App/Infrastructure/UiStallWatchdog.cs` | `UiStallWatchdog` | `Start`, `Stop`, `ReportAfterMs`, `MaxReportsPerStall`, `FileName` | UIWATCHDOG_01 — app-wide UI-thread stall reports to the runtime log and `<Logs>\ui_stalls.log`. |
| `src/FreeVideoStudio.Core/Infrastructure/BlockingCallTracker.cs` | `BlockingCallTracker` | `Enter`, `Describe` | UIWATCHDOG_01 — names the blocking call in progress per thread for stall reports. |
| `src/FreeVideoStudio.App/Infrastructure/PreviewAudioSync.cs` | `PreviewAudioSync` | `CreateVoicePlayer`, `SyncVoiceTake`, `DisposeOffUiThread` | UIBLOCK_02 — voice-over preview takes on `WaveOut`, released off the UI thread. **⚠ CO-GOVERNED BY: 02** |
| `src/FreeVideoStudio.Core/Infrastructure/CoreLogger.cs` | `CoreLogger` | `InfoAction`, `FailAction`, `Warn`, `Swallowed` | Core-side logging facade; the App wires its actions to `RuntimeLog` at startup. |
| `src/FreeVideoStudio.Core/Infrastructure/RecoveryManager.cs` | `RecoveryManager` | `SaveState`, `LoadState`, `CheckFault`, `IsSafeModeActive` | Continuous project session serialization, crash detection, and safe-mode recovery.|
| `src/FreeVideoStudio.Core/Infrastructure/AtomicJsonFile.cs` | `AtomicJsonFile` | `WriteObject`, `WriteText`, `WriteBytes`, `WriteCore`, `ReadObject`, `ATOMICTEXT_01`, `ATOMICBYTES_01` | Thread-safe, power-outage-safe atomic JSON (and opaque byte) file writing and parsing. |
| `src/FreeVideoStudio.App/Infrastructure/SettingsManager.cs` | `SettingsManager` | `Update(Action<AppSettings>)`, `SetAutoUpdateChecks`, `SetUpdateConsent`, `UpdateConsentAsked`, `UPDATECONSENT_01`, `Committed`, `Load`, `SettingsMutexName`, `SerializeGate`, `CurrentSchemaVersion` (14), `SETTINGSATOMIC_01`, `SETTX_01`, `SETTX_02` | Cross-process settings persistence under a named mutex and the atomic write protocol; the ONLY settings mutation path is `Update`. **⚠ CO-GOVERNED BY: 09, 10** |
| `src/FreeVideoStudio.Core/Ipc/NamedPipeStateServer.cs` | `NamedPipeStateServer` | `ScheduleDiskFlush`, `FlushToDiskSafe`, `IPCLEASE_01`, `IPCTEARDOWN_01`, `IPCOWNER_01` | In-memory session state server, bounded flush scheduling and ordered teardown. |
| `src/FreeVideoStudio.Core/Ipc/NamedPipeStateClient.cs` | `NamedPipeStateClient` | `GetStateAsync`, `SaveStateAsync`, `UpdatePropertiesAsync`, `SendHandoffAsync`, `FastProbeTimeout`, `DefaultTimeout`, `IPCDEADLINE_01` | IPC client communicating with the running session state server. Async only; every exchange is bounded end to end. |
| `src/FreeVideoStudio.Core/Ipc/SessionStateWriter.cs` | `SessionStateWriter` | `For`, `Enqueue`, `FlushAsync`, `FlushAllAsync`, `ReadSnapshot`, `Prime`, `PrimeAsync`, `CommitFailed`, `UIIPC_01` | The only UI path to session state: coalesced, ordered, latest-wins background commits; memory-only reads. |
| `src/FreeVideoStudio.Core/Ipc/StateTransferStore.cs` | `StateTransferStore` | `LoadAsync`, `SaveAsync`, `UpdatePropertiesAsync`, `SendHandoffAsync`, `ClearAsync`, `SchemaVersion` (1), `DeliveryBudget`, `IPCOWNER_01` (no `*Sync` members, UIIPC_01) | Cross-process state persistence and named mutex coordination. |
| `tests/FreeVideoStudio.Core.Tests/IpcTests.cs` | `IpcTests` | `IpcOwner_*`, `IpcServer_ConcurrentFlushes_DiskEndsAtTheLatestState` | IPC framing, ownership (IPCOWNER_01) and write-order (WRITEORDER_02) regression coverage. |
| `tests/FreeVideoStudio.Core.Tests/IpcChildProcess.cs` | `IpcChildProcess` | `Main` (`--ipc-child update`) | Test-assembly entry point that runs real cross-process IPC clients for `IpcTests`. |
| `src/FreeVideoStudio.Core/Ipc/PipeAccess.cs` | `PipeAccess` | `CreateServer`, `CreateOwnerOnlySecurity`, `IsServerTrusted`, `CreateUpgradeServer`, `NewUpgradePipeName`, `IsUpgradePipeName`, `PIPEAUTH_01`, `NOLOCALPORT_01` | Owner-only pipe DACL and client-side verification that the pipe server runs as the same user. |
| `src/FreeVideoStudio.Core/Ipc/IpcProtocol.cs` | `IpcProtocol`, `IpcFrame`, `IpcOpcode` | `WriteFrameAsync`, `ReadFrameAsync`, `ExchangeAsync` (IPCDEADLINE_01), `PipeName`, `ServerMutexName` | Wire framing protocol and serialization context for IPC pipes. |
| `src/FreeVideoStudio.Core/Ipc/CropConfigDefaults.cs` | `CropConfigDefaults` | `Create`, `CreateNoMask`, `SchemaVersion` (4), `MinimumUsableSchemaVersion` (3) | Factory for default HUD crop profiles and schema version definitions. |
| `src/FreeVideoStudio.Core/Infrastructure/CooperativeShutdownGate.cs` | `CooperativeShutdownGate` | `Begin`, `AwaitActiveAsync`, `ReadExitCodeSafely` | Single-flight gate around `GracefulProcessTerminator` for stopping an FFmpeg child, shared by `ProcessWorker` and `MergerWorker` (PIPEDEDUP_01). |
| `src/FreeVideoStudio.Core/Infrastructure/DiskSpaceGuard.cs` | `DiskSpaceGuard` | `Check`, `HasRoomFor`, `EstimateOutputBytes`, `EstimateTwoPassMasterBytes`, `Result` | Pre-render disk space verification preventing corrupt half-written files. |
| `src/FreeVideoStudio.App/CrashLogDigest.cs` | `CrashLogDigest` | `RunAsync`, `ReadEvents`, `CondenseXml`, `Mentions`, `NOHIDDENTOOLS_01` | Folds this app's Windows crash events into the log through the Event Log API (no helper program). |
| `src/FreeVideoStudio.App/Services/LegacyScheduledTask.cs` | `LegacyScheduledTask` | `Exists`, `IsPlainName`, `NOHIDDENTOOLS_01` | Read-only "is an old release's scheduled task still there?" (SYS-AVCLEAN). |
| `src/FreeVideoStudio.Core/Infrastructure/ProcessImage.cs` | `ProcessImage` | `GetPath`, `TryGetPath`, `NOMEMREAD_01` | Program path of another process with query-only rights (no memory read) (SYS-AVCLEAN). |
| `src/FreeVideoStudio.App/SingleInstanceGuard.cs` | `SingleInstanceGuard` | `TryAcquire`, `Release`, `AppliesTo` | Single application instance enforcement and focus delegation via named mutex. |
| `src/FreeVideoStudio.Core/Infrastructure/AppDataPaths.cs` | `AppDataPaths` | `AppDataDir`, `LocalCacheDir`, `MigrateDirectory`, `REBRAND_01` | Copy-only rebrand migration before default state initialization. |
| `src/FreeVideoStudio.Core/Infrastructure/LegacyAppDataNames.txt` | Embedded legacy directory identities | `LegacyAppDataNames.txt` | Historical names retained only as migration data. |
| `src/FreeVideoStudio.Core/Infrastructure/LegacyResidueSweep.cs` | `LegacyResidueSweep` | `RunForCurrentUser`, `Run`, `LegacyTempNames`, `LegacyStorageNames`, `REBRAND_02` | Rescues renders and retains legacy data (LEGACYKEEP_01). |
| `tests/FreeVideoStudio.Core.Tests/RebrandTests.cs` | `RebrandTests` | sweep, naming and brand-allow-list tests | Rebrand residue and output-name regression coverage. |
| `tests/FreeVideoStudio.Core.Tests/AppDataPathsTests.cs` | `AppDataPathsTests` | migration, retry, collision and existing-destination tests | Rebrand data-preservation regression coverage. |
| `src/FreeVideoStudio.Core/Infrastructure/ApplicationPaths.cs` | `ApplicationPaths` | `ProgramDataRoot`, `DefaultUserRoot`, `RecoveryStateFile`, `SessionStateFile`, `MergerSessionFile`, `LaneCacheDirectory`, `EnsureWritableDirectories` | System directory resolution, temp workspace paths, and sentinel lock files. **⚠ CO-GOVERNED BY: 10**|
| `src/FreeVideoStudio.App/WindowBoundsHelper.cs` | `WindowBoundsHelper` | `Track` (`capFraction`), `ApplyBounds`, `EnqueueReturn`, `SaveBoundsOnClose` (UIIPC_01, replaced `SaveBoundsSync`), `SaveBoundsAsync`, `CloseFlushBudget`, `Capture` | Multi-display window geometry tracking and per-screen bounds persistence (`Display`/`Return`, WINPLACE_01). **⚠ CO-GOVERNED BY: 04**|
| `src/FreeVideoStudio.App/WindowPlacement.cs` | `WindowPlacement`, `DisplayArea` | `GrabbableDisplay`, `HomeDisplay`, `FitInside`, `KeepInside`, `FirstRunSize`, `LowerMinimumsToFit` | Placement maths for every window (WINPLACE_01; table in `04` UI-WINPLACE). **⚠ CO-GOVERNED BY: 04**|
| `src/FreeVideoStudio.App/GranularSpeedEditorWindow.axaml.cs` | `GranularSpeedEditorWindow` | `OnClosing`, `OnClosed`, `_isSafeToClose`, `ResultSegments` | Deferred-close dispatcher contract governing dialog resolution and edit hand-off. **⚠ CO-GOVERNED BY: 01, 04, 10**|
| `src/FreeVideoStudio.App/Infrastructure/MaskOverlayManager.cs` | `MaskOverlayManager` | `ApplyProfile`, `EnsureDefaults`, `IsNoMask`, `SanitizeProfileName` | HUD profile configuration and first-run crop defaults. |
| `src/FreeVideoStudio.Core/Ipc/CropConfigStore.cs` | `CropConfigStore` | `LoadAsync`, `SaveAsync`, `RotateBackupsUnlocked`, `IsUsableConfig` | Crop configuration persistence and the 5-tier `.bak` rotation cascade. |
| `src/FreeVideoStudio.Core/Editing/CropProfileCodec.cs` | `CropProfileCodec`, `CropConfigJson` | `ReadSavedLayers`, `WriteLayers` (crops_source drift trap, RESGUESS_01 position-only, DELETESET_01 tombstones) | The Crop Tool's layers as the crop-config document and back; pure, the window keeps the store I/O (EDITSTATE_01). **⚠ CO-GOVERNED BY: 01**|
| `src/FreeVideoStudio.Core/Editing/GranularRecoveryCodec.cs` | `GranularRecoveryCodec`, `GranularJsonRead` | `Build`, `TryApply`, `Key = "granular_session"`, `SchemaVersion = 1` | RECOVERY_03 values of the Granular editor's live session; identity = video path + trim window; merge mode never restores (EDITSTATE_01). |
| `src/FreeVideoStudio.App/Services/ProjectRecoveryService.cs` | `ProjectRecoveryService`, `RecoverySnapshot` | `SaveState` (canonical `ProjectDocument` + transient), `LoadSnapshot`, `HasUnsavedWork`, `ClearState` | Recovery BRIDGE over the canonical document (RECOVERYDOC): one envelope, no parallel project schema. |
| `src/FreeVideoStudio.App/Services/LatestEstimateWorker.cs` | `LatestEstimateWorker` | `Request`, `RunAsync`, `Dispose`, `Completion` | Bounded background estimates, cancellation and stale UI result rejection. |
| `src/FreeVideoStudio.App/Services/UpdateService.cs` | `UpdateService` | `RunStartupCheckAsync`, `AskUpdateConsentAsync`, `StartupGracePeriod`, `SetupAssetName`, `UPDATECONSENT_01`, `NODROPPER_01`, `CheckManualAsync`, `GetSkippedVersion`, `ClearSkippedVersion`, `DescribeLastCheck`, `DescribeDownloadProgress`, `RestartToUpdate`, `UPDATEUX_01`..`06` | Background GitHub release query, 15-minute startup throttle, SHA-256 verification, and quiet updater. **⚠ CO-GOVERNED BY: 09** |
| `src/FreeVideoStudio.App/Services/UpdateService.Background.cs` | `UpdateService` (partial) | `StartBackgroundUpdate`, `LaunchInstaller`, `SkipPendingVersion`, `PendingVersion`, `BGUPDATE_01` | Background download + the update card (R2/R3). **⚠ CO-GOVERNED BY: 09** |
| `src/FreeVideoStudio.Core/Infrastructure/HardLink.cs` | `HardLink` | `TryCreate`, `FASTINSTALL_01` | Instant reuse of unchanged installed files during an install. |
| `src/FreeVideoStudio.App/Services/UpgradeProgress.cs` | `IUpgradeProgress`, `HeadlessUpgradeProgress`, `UpgradeText`, `UpgradeStep`, `UpgradeKind` | `Step`, `AskToCloseRunningAppAsync`, `Succeeded`, `FailedAsync` | Display-only progress contract of the install/update broker and all its wording. |
| `src/FreeVideoStudio.App/ViewModels/UpgradeProgressViewModel.cs` | `UpgradeProgressViewModel`, `UpgradeStepItem` | `SetSteps`, `MoveTo`, `Ask`, `Succeed`, `Fail` | State of the checklist window. |
| `build/FvsBuild/Program.cs` | `Program` | `RunPipeline`, `FailRelease`, `RunNativeAotStages`, `RELGATE_01`, `PEIDENTITY_01` | Unified build pipeline; an official release runs inside the release gate (09 DIST-RELEASEGATE). **⚠ CO-GOVERNED BY: 09** |
| `build/FvsBuild/Staging.cs` | `Staging` | `Publish`, `RunPublish`, `StageDependencies`, `TerminateProcesses`, `BUILDKILL_01` | Build output staging, packaging, and publish validation; stops only processes that run from the repository. |
| `Build.cmd` | Production Release Script | `dotnet run build\FvsBuild` | **STRICT AGENT BAN**: Thin entry-point triggering production release, git tagging, and live GitHub Cloud release deployment. Never execute by autonomous agents. **⚠ CO-GOVERNED BY: 09** |
| `dev_build.cmd` | Dev Compile Harness | `dotnet run build\FvsBuild -- --dev` | Safe local-only build and NativeAOT publish compilation test harness. Zero git tags, zero GitHub Cloud publishing. Mandatory for agent build tests. **⚠ CO-GOVERNED BY: 09** |
| `dev.cmd`, `build/DevRun.ps1`, `build/DevSandbox.ps1` | Developer Harness | `DEVPARITY_01`, `DEVCLEAN_01`, `DEVDATA_01`, `DEVVERIFY_01` | Incremental Release managed launch, shared production staging, safe cache cleanup and preserved development preferences. **⚠ dev.cmd CO-GOVERNED BY: 08, 09, 10** |
| `tests/FreeVideoStudio.Core.Tests/DevelopmentEnvironmentTests.cs`, `tests/FreeVideoStudio.App.Tests/DevelopmentLaunchTests.cs`, `tests/DevHarness.Tests/DevSandbox.Tests.ps1` | Development regression tests | `DEVPARITY_01`, `DEVISOLATION_01`, `DEVCLEAN_01` | Isolation, update guards, shared staging, real cleanup and preservation checks. **⚠ CO-GOVERNED BY: 10** |
| `src/FreeVideoStudio.Core/Infrastructure/MergerAutosaveStore.cs` | `MergerAutosaveStore` | `Schedule`, `FlushAsync`, `Clear`, `Load`, `DefaultDebounce` | Video Merger edit-list autosave (`merger_session.json`, MERGESESSION_01). |
| `src/FreeVideoStudio.Core/Media/MergerSession.cs` | `MergerSession`, `ClipIdList` | `Capture`, `Plan`, `DescribeRestoreProblems`, `SyncQueue` | Merger session capture and restore planning (MERGESESSION_01, RESTOREMISS_01). **⚠ CO-GOVERNED BY: 01**|
| `src/FreeVideoStudio.App/MainWindow.ToolReturn.cs` | `MainWindow` | `RestoreVideoPipelineAfterTool`, `StartVideoHostAsync`, `BindFallbackBadge`, `TOOLRETURN_01` | Main App preview revival after a companion tool closes. **⚠ CO-GOVERNED BY: 08**|
| `src/FreeVideoStudio.App/Infrastructure/LaneDiskCache.cs` | `LaneDiskCache`, `LaneDiskStore` | `PathFor`, `TryRead`, `Write`, `MaxFiles` (800), `MaxBytes` (512 MiB), `WhenIdleAsync` | Best-effort on-disk lane cache under `ApplicationPaths.LaneCacheDirectory`; atomic byte writes, continuous serialized LRU prune (CACHEBOUND_01, SYS-ATOMICWRITE). **⚠ CO-GOVERNED BY: 04**|
| `src/FreeVideoStudio.Core/Infrastructure/DevelopmentEnvironment.cs` | `DevelopmentEnvironment` | `DataRoot`, `ScopeSuffix`, `InitializeFromRuntimeMarker`, `DEVISOLATION_01` | Central development paths and IPC identity; marker protects direct/companion launches. **⚠ CO-GOVERNED BY: 10** |
| `src/FreeVideoStudio.Core/Infrastructure/UiStateStore.cs` | `UiStateStore` | `ReadInt`, `WriteInt`, `MigrateLegacyFilesOnce`, `ReadText` | Lightweight persistent key-value configuration and coach tour launch counts. **⚠ CO-GOVERNED BY: 09** |

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
  * Disk writes execute in `ProcessLogQueue`, started with `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)`.
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
  * Export `INFO` logs currently include complete FFmpeg commands, filter graphs and output paths; production logs are not uniformly path-redacted.
  * `DEBUG` gating (enabled via `FVS_DEV_LOG_DIR`) applies only to calls explicitly using `Debug`.
* **Progress Spam Filtering:** Filters out FFmpeg `frame=... size=... time=... bitrate=...` progress stderr spam to protect log bounds while preserving initialization probes and error banners.
* **Error Isolation:** Disk write paths swallow I/O exceptions; `LogAppended` event callbacks are exception-guarded to ensure logging failures never crash host worker threads.

---

## 3. Window State & Directory Memory  {#SYS-WINSTATE}
* **Window State Persistence:**
  * `WindowBoundsHelper.Track()` manages window geometry with a 700ms-debounced background save.
  * Granular Speed Editor enforces a minimum floor of `MinWidth=900` / `MinHeight=600` with no hardcoded fixed size.
  * Window repositioning re-applies after the Avalonia `Opened` event to counter per-monitor DPI scaling handshakes.
  * Off-screen window recovery (WINPLACE_01, `04` UI-WINPLACE): a saved spot is kept only when at least 100 DIP of the 30 DIP title bar lies inside a display's WORKING area (`WindowPlacement.GrabbableDisplay`, each display's own scaling). Otherwise the window is centred in its home display's working area (opener's display → the Main App's saved display → primary) and shrunk to the cap (95% editors, 90% Settings/AI setup). A window larger than the whole desktop is shrunk and pulled inside. Each save stores `Display` (`X,Y,W,H` of the display bounds); a forced move stores `Return` (old X/Y + Display) and the window goes back there once that display is connected again.
  * Small screens: `MinWidth`/`MinHeight` are lowered to the cap when the display is smaller (`WindowPlacement.LowerMinimumsToFit`).
* **First-Run Geometry Is Derived From The Display (FIRSTFIT_01):** A window with nothing saved must NOT open at whatever its content and `MinWidth`/`MinHeight` happen to add up to — that number was chosen against one developer's monitor, and it fills a 1080p laptop while opening as a small box in the middle of a 4K desktop.
  On the first open only, `Track(..., fitDisplayOnFirstRun: true)` sizes the window to cover **80% of the home display's WORKING AREA in a landscape 16:9 rectangle**, centred on that area (home display: the active app window's display, else the Main App's saved display, else primary — so a tool opened from the Main App opens on the Main App's monitor; WINPLACE_01):
  $$\text{bound} = (0.8\,W_{\text{usable}},\; 0.8\,H_{\text{usable}}), \qquad w = \text{bound}_W,\; h = \frac{w}{16/9}$$
  $$\text{if } h > \text{bound}_H: \quad h = \text{bound}_H,\; w = h \times \tfrac{16}{9}$$
  Fitting INSIDE the 80% box (rather than scaling a full-screen 16:9 fit) is what holds on both monitor shapes: on a 16:9 display the height binds and the window is exactly 80% tall; on an ultrawide the width binds first and the window stays 16:9 instead of becoming a letterbox slot.
  * **Precedence, in order:** the window's OWN saved bounds → the seed window's bounds (WINSEED_01) → the display fit. It is a first-run DEFAULT, not a policy: the moment the user moves or resizes, the debounced save writes that window's key and step 1 wins forever after.
  * **`WorkingArea`, not `Bounds`.** `Bounds` includes the taskbar, so 80% of it can still put an edge underneath it.
  * **DIPs, not pixels.** `Width`/`Height` are device-independent; `WorkingArea` is physical pixels. The area is divided by the screen's scaling going in and multiplied back to centre. Skipping that makes the window 150% too large on a 150%-scaled display — the exact machines this exists for.
  * **`MinWidth`/`MinHeight` still win.** A floor larger than the computed rectangle (the Music Wizard's 1100x760 on a small laptop) clamps up and stops being 16:9. Correct: a usable window in the wrong ratio beats a correctly-shaped one that cannot lay out its contents.
  * **Opt-IN.** `fitDisplayOnFirstRun: true` applies to the Main App, Granular Speed Editor, Voice Over Studio, Add Music wizard, Video Merger and Crop Tools (NOT to Settings, `SettingsBounds`, `04` §11, or the AI setup dialog `AiSetupWizardBounds` — which calls `WindowBoundsHelper.Track` without `fitDisplayOnFirstRun` to persist position/size within working area; "How should AI work?" is no longer remembered and opens over its owner (WINPLACE_01) — and never to the detached preview console, which opens over its owning parent by UI-DETACH).
* **Deferred-Close Dispatcher Contract (⚠️ EXPORT-CRITICAL):**
  Every tracked window closes in two turns: `OnClosing` sets `e.Cancel = true`, saves bounds, then — **MANDATORY** — sets `_isSafeToClose = true` and re-posts `Close()` on the NEXT dispatcher turn. A window that cancels the close without re-posting it only HIDES.
  * **Saving bounds on close never blocks (UIIPC_01).** `WindowBoundsHelper.SaveBoundsOnClose` captures an immutable snapshot on the UI thread, builds the entry against the MEMORY state and `SessionStateWriter.Enqueue`s it — it returns immediately. A bounded background `FlushAsync` (`CloseFlushBudget` = 3 s) logs `Fail` if the owner does not acknowledge; the values stay queued and `Program.RunUiAsync` performs a bounded FINAL flush (3 s) after the dispatcher has stopped, then `FlushToDiskSafe` on the in-process owner. Re-entrancy: a nested call is ignored and an identical second snapshot (the deferred-close second `Closing`) is not enqueued twice. `SaveBoundsSync` (which blocked the dispatcher on the commit) no longer exists.
  * **The historic defect this encodes:** the Granular Speed Editor was the one sibling that never re-posted. `await editor.ShowDialog(this)` therefore never returned, the `MainWindow` continuation that copies `editor.ResultSegments` into `_speedSegments` never ran, and **EVERY granular edit — speed segments, freezes AND zooms — was silently discarded at export**, with FFmpeg receiving a uniform base-speed graph. Nothing failed loudly; the feature simply did nothing.
  * **Required chain:** `OnClosing` (`_isSafeToClose = true` + re-post `Close`) → `ShowDialog` resolves → `ResultSegments` → `_speedSegments` → `BuildExportSpeedSegments()` → `worker.SpeedSegments` → `GranularSpeedBuilder` split/concat graph.
  * **Teardown ordering:** re-posting also lets `OnClosed` actually run, which disposes the editor's mpv preview host instead of leaking it on every open. The preview is shut down BEFORE the window is hidden, and every render-thread/UI-thread hand-off is bounded by a timeout with a defined give-up behaviour — this is what closes the OpenGL teardown deadlock window.
  * **MPVSHUTDOWN_01 / VOAPPLY_01 / VOAPPLY_02 — preview teardown is two-phase, awaitable, non-blocking, and cannot lie (2026-09-29, updated 2026-10-07).** (B1 `FinalizeUiResourcesOnUiThreadAsync` on the UI thread; B2 `ExecuteDetachedNativeTeardown` on a worker)
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
       - **WGLUNBIND_01 — UI binding is thread-local:** Phase B1 checks `wglGetCurrentContext()` and unbinds only if the current context equals the preview's `_hglrc`. Startup already releases the UI binding; a nonzero stored handle does not prove a current UI binding. The redundant unbind on an empty UI thread failed in production and blocked Main App → Video Merger navigation. A different component's context is left untouched. Failure to unbind the preview's actual current context still retains its handles and aborts transfer; Phase B2 still requires a successful worker-thread bind. `wglMakeCurrent` preserves Win32 last-error information. Regression: `PreviewShutdownTests.UiTeardown_UnbindsOnlyItsCurrentContext` covers absent, foreign, successfully released and failed bindings.
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

* **App-wide UI stall watchdog (UIWATCHDOG_01, 2026-11-17).** `Infrastructure/UiStallWatchdog` starts with the app (`AvaloniaApp.OnFrameworkInitializationCompleted`, stopped on exit): a 1 s thread-pool timer posts a Background-priority heartbeat to the UI thread; when the heartbeat is older than 2 s it writes up to 6 reports per stall — `UI THREAD STALLED for <ms>`, the blocking calls in progress (`Core/Infrastructure/BlockingCallTracker`: mpv synchronous reads, owned-player teardown, freeze sound start/stop) and the preview's last UI/render/lock steps — to `RuntimeLog.EmergencyWrite` AND to `<Logs>\ui_stalls.log`. Reason: the Speed Editor's own watchdog starts only once the editor exists and its emergency writer may fall back to the install-report path, so an owner-reported freeze left no evidence. The Speed Editor's watchdog report also lists the blocking calls. Every NAudio output in the app is `WaveOut` (own audio thread, no window-message callback): freeze sound, voice-over preview takes (`PreviewAudioSync.CreateVoicePlayer`, Main App / Voice-Over Studio / Speed Editor) and click sounds (`UiSoundEffect`); their Stop/Dispose run off the UI thread (`PreviewAudioSync.DisposeOffUiThread`, click engine teardown on a background task) — UIBLOCK_02.
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
  * **RECOVERYFULL_01 — after a crash the editor carries on as if nothing happened.** Besides the document, the transient block keeps `playheadSec` (the playhead at the last snapshot), `aiEliminations` (the AI kill marks, so no paid re-scan), `applyPeakFlattening` and `gameplayLoudnessLufs` (the harsh-peak answer and the loudness reading, so the export sounds the same and the question is not asked again). Restore awaits the video load like a fresh upload: pauses, waits for metadata, sets the loaded duration, shows the timeline through `EnableEditingControls` (the timeline used to stay hidden after a restore), seeks to the saved playhead, re-measures loudness only when no reading was saved, and re-runs the kill scan only when no marks were saved and the tracker is on (`MainWindow.RestoreSessionExtras`). `ProjectSession.MarkRecovered` marks the restored project unsaved, so closing it asks "Save your changes?" instead of silently dropping the recovered work.
* **A new video starts clean (`NEWVIDEOCLEAN_01`, user report 2026-11-20).** `MainWindow.ResetEditingStateForNewVideo` (after the NEWVIDEOGUARD_01 question) clears every per-video edit: speed blocks and AI/manual zoom blocks, cuts and meme placements (`TimelineViewModel.ClearPerVideoEdits`), AI captions (`CaptionTrack = null`), kill marks, music, voice-over, trim, thumbnail, freeze and the live zoom crop; `MainViewModel.ResetEditingStateForNewVideo` clears captions too. Crash recovery keeps all of them (they are in the project document), and captions alone count as unsaved work (`ProjectRecoveryService.HasUnsavedWork`), so a captions-only edit is recovered and protected by the replace-video question.
* **Finished work leaves nothing behind (`DONECLEAN_01`, user decision 2026-11-16).** After PROCESS succeeds the recovery file is cleared and the project counts as clean until the next edit (`ExportedCleanSinceLastEdit`); closing then never asks and the next start is empty (`MainWindow.WipeFinishedSessionForExit`). The Merger does the same after MERGE (see MERGESESSION_01), and when a finished action in `FinishedDialogWindow` (WhatsApp share, social launch, Open Folder, or Exit; `DialogResult == 1`) closes `VideoMergerWindow`, `ToolNavigationResult.ExitApplication = true` signals `ToolNavigator` to exit cleanly via `MainWindow.MarkExportedCleanExitForTool()` without showing `MainWindow` or prompting. An edit after the export or merge re-arms recovery and the close question.
* **Video Merger session (MERGESESSION_01):** the Merger's edit list (`MergeEdl`) is autosaved to
  `merger_session.json` by `MergerAutosaveStore`: 750 ms write-behind debounce after the last edit,
  process-wide ordered versions (a queued write can never resurrect a cleared file), `AtomicJsonFile.WriteText`,
  all I/O on the thread pool, `Load()` never throws. An empty queue clears it; a MERGE alone does not, but closing the
  Merger after a finished merge with no edit since does (DONECLEAN_01: `MarkMergeFinishedClean` / `WipeFinishedMergeForExit`
  clear the autosave and the project's merge queue, so the next open is empty; any queue change after the merge re-arms it). Every
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
* **Verified Profile Loading & Explicit Target Identity (CROPPROFILE_01 / CROPEDIT_01):** Profile loading via `MaskOverlayManager.LoadProfile` returns an explicit `ProfileLoadResult` with validated configuration or clear error diagnostics. Missing, sentinel ("No Mask"), unparseable or schema-invalid profiles are rejected without wiping live state, resetting work, or relabeling old data. Editing profiles are saved directly to their explicit verified identity (`SyncProfileFromCurrentConfig(targetProfile)`), never silently falling back to the global `ActiveMaskOverlay`. A separate deliberate editing choice is persisted in `AppSettings.LastEditedCropProfile` only after successful verified selection and is reopened on launch only if still valid.

---

## 5. Developer Build Harness & Fix Sentinels  {#SYS-DEVBUILD}

**DEVPARITY_01 / DEVISOLATION_01 / DEVCLEAN_01 (2026-10-20)** supersede the old hot-reload
DEVFAST_01 and ordinary launch's DEVUPDATE_01 behaviour. **DEVINCREMENTAL_01 (2026-10-21)**
retains compiler intermediates and verified dependency assets. Development runs current source, never an installer test.

* **Never execute `Build.cmd` as an agent.** It publishes a public release. `dev_build.cmd` remains
  the separate local NativeAOT/installer check, not the normal development launcher.
* **DEVVERIFY_01 / VERIFYHALT_01 / SYS-VERIFYTOOL:** `dev.cmd` runs `FvsVerify` first and propagates
  failure. `dev verify` then exits without process, cache or settings changes. The sentinel registry
  is `build/sentinels.txt`; never move it back into batch parsing (VERIFYLOOP_01, LISTCOMMENT_01,
  BATCHPARENS_01/02). A sentinel proves a fix was not deleted; a behavioural test proves it still works.
* **One runtime, one staging implementation:** `build/DevRun.ps1` publishes the current app project and
  its references using Release, win-x64, self-contained managed .NET (`PublishAot=false`,
  `PublishTrimmed=false`) into `.dev-run/app`. Normal launches retain isolated compiler intermediates in `.dev-run/build`; MSBuild rebuilds
  changed projects and their dependants. Old `src/bin`, `src/obj`, `compiled` and release stages are
  not inputs. Before publishing, remove managed output from `.dev-run/app`, retaining only
  `backend`, `frontend` and `starter` directories, so removed packages/assemblies cannot linger.
  No hot reload or launch of an old executable after failure. `FvsBuild --dev-runtime` invokes the SAME `Staging.StageDependencies`
  function used by the installer, with a different destination, including codecs, player and starter
  media, and writes the same runtime fingerprint. For development destinations, compare source and
  destination SHA-256 content (not timestamps alone), copy missing/changed/corrupted assets, and
  remove obsolete files from those three stager-owned directories. Missing required sources fail
  even when an older staged copy exists. Reject linked staging paths before synchronization.
  Production staging retains its existing unconditional-copy behavior. This branch cannot publish, sign, install or enter
  the release pipeline; additional arguments are refused. Application bootstrap, UI, settings schema
  migration, preview and export code remain shared. AOT/trimming, signatures, installer transactions,
  shortcuts and machine registration still require the separate packaging/installation checks.
* **BUILDKILL_01:** the launcher refuses while a repository-owned app/media process is still running;
  it never force-kills unsaved work, production processes or shared MSBuild/Roslyn servers. A launch
  lock serializes development builds. A cleanup, compile or staging failure stops the launch: there is
  no fallback to the last executable. `run` (the default) and `trace` use incremental compilation and verified retained assets;
  `build` does the same but stops after staging. `fresh` and `rebuild` first remove all of `.dev-run`
  for a full-clean build and launch; `clean` removes only `.dev-run`; `restore` only restores packages.
  Incremental builds depend on MSBuild input tracking: unusual timestamps, custom targets or damaged
  intermediates may require `dev fresh`. Hash checks still read asset bytes, so copying savings do not
  eliminate staging I/O. Retained intermediates use disk space; the first/full-clean build stays slow.
* **DEVDATA_01 / STORAGEPATHS_01:** the stable development identity, profiles and recovery root is
  `%LOCALAPPDATA%/FreeVideoStudio_DEV/data`. Preferences/session/UI state and logs instead use
  `%TMP%/FreeVideoStudio_DEV/data`. Normal launches and `fresh` preserve those preferences while
  TEMP exists. Clearing TEMP intentionally resets them, including stored API credentials.
  On the first revised launch, copy absent settings/session/UI/roaming files from the stable
  development root, without overwriting destinations or deleting sources. A persistent
  `.temporary-preferences-v1` marker prevents stale preferences being restored after a later TEMP
  purge. The migration is serialized with a named mutex and rejects linked paths. No production
  preferences are imported. Earlier development backups can seed the stable root before this copy.
* **Disposable data:** clear temporary dev `cache`, `updates`, `logs`, `uistate/meme_dimensions.json`,
  cached update status/notes and processing scratch on launch. Preserve voice WAVs, failed-take
  indexes and rescued exports. **DEVHELPERTEMP_01:** helper scratch uses a fresh GUID subfolder
  per launch under its documented root. Cleanup removes unlocked older helper files, but defers
  sharing/lock violations (Windows errors 32/33) until a later launch. Old console/helper handles
  must not block relaunch or cause old helper files to be reused. Other errors still fail explicitly.
  Validate absolute child paths and reject reparse points before cleanup. Locked app caches still fail launch.
  Never delete a whole settings root or a directory chosen by the user as media/output storage.
* **Isolated resources / DEVSHAREMEDIA_01:** stable data-root-derived pipes, leases, mutexes and
  single-instance identities remain separate. `FVS_DEV_SHARED_MEDIA=1` selects the normal development
  storage table below: music, memes and Downloads defaults are shared; new voice recordings are
  separate TEMP files. Custom media/output choices still win. Generic test/sandbox overrides without
  this flag keep their own paths. Existing recordings remain at their referenced paths.
  Legacy production UI-state migration and Explorer registration are skipped. The runtime marker
  protects direct and companion launches. Capture `FVS_SYSTEM_TEMP_ROOT` before redirecting TEMP/TMP;
  inherit it in children so repeated initialization cannot nest directories. `FVS_DEV_HELPER_TEMP`
  carries the launch GUID folder to companions; a direct runtime-marker launch creates one when absent.
  `DevRun.ps1` also sets `FVS_DEV_HOME` (`%LOCALAPPDATA%\FreeVideoStudio_DEV`) and clears
  `FVS_DEV_UPDATE_SOURCE`; the latter (dev-only, with `FVS_DEV_LOG_DIR` and `FVS_ALLOW_UNSIGNED_UPDATE`
  in `UpdateService.DevOnlyEnvironment`) points the updater at a repository's `.\compiled` when set.
* **Authoritative path map:** [SYS-STORAGE-TABLE](#SYS-STORAGE-TABLE) below supersedes the earlier
  single-root and shared-voice path maps. `dev trace` still explicitly uses `<repo>/.devlogs`.
* **No installation from development:** automatic/manual update checks, background updates and
  installer launch are blocked by the data-root boundary, independently of logging. Deployment worker
  arguments are refused before dispatch. The launcher clears the old local-update and unsigned-update
  overrides. Update scratch has a sandbox location as defence in depth. The old DEVUPDATE_01 local
  release-reader remains for explicit updater tests; ordinary dev launches cannot enter it.
* **TRANSPORT_TRACE_01:** `dev trace` uses `.devlogs` for diagnostic logs; normal logs live under the
  temporary development preferences root. Both modes publish current source with the same Release settings. Logs must never be committed.
* **Regression coverage:** `DevelopmentEnvironmentTests` checks production identity compatibility and
  separate sandbox paths/pipes; `DevelopmentLaunchTests` checks update guards and shared staging and
  runs `tests/DevHarness.Tests/DevSandbox.Tests.ps1` against synthetic data to verify preservation,
  repeat launches, retained compiler/assets versus removed managed output, locked caches,
  traversal/prefix rejection and junction rejection. Dependency tests exercise unchanged-file reuse,
  same-size/timestamp source changes, damaged destinations, renamed assets and missing required inputs. The existing
  verifier-order test guards the batch entry point. These run with the normal CI test suites.

---

## 6. Storage locations — production vs development {#SYS-STORAGE-TABLE}

**STORAGEPATHS_01 — mandatory maintained inventory.** Every agent/developer adding or changing any
stored file, cache, secret/API credential, profile, output, recovery record or storage lifecycle MUST
update this table and its retention rules in the same change, with corresponding path/lifecycle tests.
Add a row for new categories; do not leave storage locations documented only in code. Never put actual
credentials in documentation, test fixtures or logs. This section is the authoritative default layout.

`C:\Users\<user>` is the example user profile from the approved table, never a hardcoded application path.
LocalAppData/Roaming/Music/Videos use Windows folder resolution. `%TMP%` below means the ORIGINAL
system temporary folder captured before application helper redirection. Downloads uses the Windows
Known Folder API, including GPO/OneDrive relocation; saved explicit choices override defaults.

| Files / purpose | Production | `.\dev.cmd` |
| --- | --- | --- |
| Settings | `C:\Users\<user>\AppData\Local\FreeVideoStudio\settings.json` | `%TMP%\FreeVideoStudio_DEV\data\settings.json` |
| Saved session / folder choices | `C:\Users\<user>\AppData\Local\FreeVideoStudio\session_state.json` | `%TMP%\FreeVideoStudio_DEV\data\session_state.json` |
| Window sizes / UI preferences | `C:\Users\<user>\AppData\Local\FreeVideoStudio\uistate` (window bounds in session state) | `%TMP%\FreeVideoStudio_DEV\data\uistate` (window bounds in session state) |
| Music | `C:\Users\<user>\Music` | `C:\Users\<user>\Music` |
| Saved voice recordings | `%TMP%\FreeVideoStudio\voiceovers` | `%TMP%\FreeVideoStudio_DEV\voiceovers` |
| Memes | `C:\Users\<user>\Videos\FreeVideoStudio\Memes` | `C:\Users\<user>\Videos\FreeVideoStudio\Memes` |
| Memes / starter songs while Windows "Controlled folder access" refuses the app (CFASAFE_01; kept with user data) | `C:\Users\<user>\AppData\Local\FreeVideoStudio\Media\Memes` and `\Media\Music` | Not used (development media rules above) |
| Remembered "Controlled folder access" refusal (CFASAFE_01; rechecked after 7 days or from the notice) | `C:\Users\<user>\AppData\Local\FreeVideoStudio\controlled_folder_denied.txt` | `%TMP%\FreeVideoStudio_DEV\data\controlled_folder_denied.txt` |
| Mask overlay profiles | `C:\Users\<user>\AppData\Local\FreeVideoStudio\MaskProfiles` | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\MaskProfiles` |
| Active crop configuration | `C:\Users\<user>\AppData\Local\FreeVideoStudio\crops_coordinations.conf` | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\crops_coordinations.conf` |
| Output videos | `%USERPROFILE%\Downloads` or Windows-redirected Downloads | `%USERPROFILE%\Downloads` or Windows-redirected Downloads |
| Temporary processing / unfinished recordings | `%TMP%\FreeVideoStudio` | `%TMP%\FreeVideoStudio_DEV\data\scratch\work` |
| Add Music sound preview (SOUNDMIX_01: `fvs_wizard_mix_<pid>_<slot>.wav`, the export-rendered sound of the pending music; two slots, deleted when the wizard closes) | `%TMP%\FreeVideoStudio` | `%TMP%\FreeVideoStudio_DEV\data\scratch\work` |
| Helper-program temporary files | `%TMP%\FreeVideoStudio` | `%TMP%\Local\FreeVideoStudio_DEV\data\scratch\os\<launch-guid>` |
| Thumbnail / waveform cache | `%TMP%\FreeVideoStudio\cache\lanes` | `%TMP%\FreeVideoStudio_DEV\data\cache\lanes` |
| Background whole-video film pictures (WHOLESTRIP_01; in memory only, two videos, never written to disk) | Not stored | Not stored |
| Cleanup stamp (TEMPCLEAN_01: time of the last startup cleanup) | `%TMP%\FreeVideoStudio\cache\housekeeping.stamp` | `%TMP%\FreeVideoStudio_DEV\data\cache\housekeeping.stamp` |
| Freeze-sound renders (FREEZESFX_05: one float WAV per sound and hold length, read by the export's `amovie`; regenerated when missing; disposable) | `%TMP%\FreeVideoStudio\cache\freeze` | `%TMP%\FreeVideoStudio_DEV\data\cache\freeze` |
| Saved AI answers (AIREUSE_04: Kill Tracker scans per file + company + model; 200 files max, oldest removed; disposable, no keys) | `%TMP%\FreeVideoStudio\cache\ai` | `%TMP%\FreeVideoStudio_DEV\data\cache\ai` |
| Merger preview silence (MERGEPREVIEW_SILENT_01; one ~10 KB FLAC, rewritten when missing) | `%TMP%\FreeVideoStudio\preview-silence` | `%TMP%\FreeVideoStudio_DEV\data\scratch\work\preview-silence` |
| Crash recovery | `C:\Users\<user>\AppData\Local\FreeVideoStudio\recovery_v2.json` | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\recovery_v2.json` |
| Merger recovery | `C:\Users\<user>\AppData\Local\FreeVideoStudio\merger_session.json` | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\merger_session.json` |
| Logs (including `ui_stalls.log`, UIWATCHDOG_01; dev `mpv_debug_*.log` is stopped at 64 MB, MPVLOGCAP_01) | `C:\Users\<user>\AppData\Local\FreeVideoStudio\logs` | `%TMP%\FreeVideoStudio_DEV\data\logs`; explicit `dev trace`: `C:\FreeVideoStudio\.devlogs` |
| Generated test fixtures, results, logs and isolated test state (TESTOUT_01, TESTCLEAN_01/02) | Not created by the installed application | ONLY repository `logs\<Suite>_Results\` (`console.log`, `<Suite>.trx`, `work\`; gate verdict `logs\Gate_Results\LAST_RESULT.txt`), one latest copy per suite — README GOV-TESTOUT. No other folder (`TestResults`, `artifacts`, `tests/AvRegression`, `tests/MediaPipelineChecks/artifacts`, `%TEMP%`) may hold test results. Each suite replaces only its own folder when it runs; the gate does so under an exclusive `tests/.test-run.lock`. No user media is removed; linked ancestors and escaping paths are refused. TEMP loss requires regenerating fixtures, never restoring user data. |
| Roaming application data | `C:\Users\<user>\AppData\Roaming\FreeVideoStudio` | `%TMP%\FreeVideoStudio_DEV\data\roaming` |
| Saved projects / project autosaves | User-selected project location; first close-time save reserves an unused name beside the source | Same behavior |
| Legacy migration sources (LEGACYKEEP_01) | Original legacy Local/Roaming and TEMP roots; retained until explicit removal, including conflicts and referenced media | Production sweep disabled with development override; original legacy TEMP remains subject to OS/user TEMP clearing |
| API keys (Gemini, Claude, OpenAI) | DPAPI-protected values in production `settings.json` above | DPAPI-protected values in development `settings.json` above; lost if TEMP is cleared |
| ChatGPT sign-in (CHATGPTSIGNIN_01: host id, issued client id, account id/e-mail, ID/access/refresh tokens; whole file DPAPI-sealed, atomic write; tokens cleared on sign-out or a terminal refresh error, host + client id kept for the next sign-in) | `C:\Users\<user>\AppData\Local\FreeVideoStudio\chatgpt_signin.dat` | `%TMP%\FreeVideoStudio_DEV\data\chatgpt_signin.dat`; lost if TEMP is cleared |
| Undo history | `C:\Users\<user>\AppData\Local\FreeVideoStudio\History` | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\History` |
| Recent projects | `C:\Users\<user>\AppData\Local\FreeVideoStudio\recent_projects.json` | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\recent_projects.json` |
| Favourite memes / remembered placement / delivery markers | Files inside production `uistate` above | Files inside temporary development `uistate` above |
| Crop backups | Beside active crop configuration, `.bak1` through `.bak5` | Beside active crop configuration, `.bak1` through `.bak5` |
| Failed voice-take recovery indexes | `voiceover_recovery_*.json` in processing folder above | `voiceover_recovery_*.json` in processing folder above |
| Session lock / safe-mode / clean-exit markers | `C:\Users\<user>\AppData\Local\FreeVideoStudio` | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data` |
| Local AI pack (LOCALAI_01; optional, installed by choosing "On this PC only" or "Both" in "How should AI work?"; about 108 MB: ONNX Runtime, DirectML, SAM 2.1 model; kept until Remove or uninstall; `v1.download` = resumable partial download; `v1.old-*` = previous copy, deleted after a swap) | `C:\Users\<user>\AppData\Local\FreeVideoStudio\LocalAi\v1` | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\LocalAi\v1`, or the folder named by `FVS_LOCAL_AI_PACK_DIR` (development only, unverified) |
| Update downloads / staging | `C:\Users\<user>\AppData\Local\FreeVideoStudioUpdates` | Disabled; reserved `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\updates` |
| Installed executable / codecs / starter assets | `C:\Program Files\FreeVideoStudio` | `C:\FreeVideoStudio\.dev-run\app` |
| Compiler intermediates / build tools | Not needed by installed app | `C:\FreeVideoStudio\.dev-run\build`; `C:\FreeVideoStudio\build\FvsBuild\bin` and `obj`; `C:\FreeVideoStudio\build\FvsVerify\bin` and `obj` |
| One-time development preferences migration marker | Not applicable | `C:\Users\<user>\AppData\Local\FreeVideoStudio_DEV\data\.temporary-preferences-v1` |

**Retention and deletion:**
- **Automatic cleanup (TEMPCLEAN_01, owner 2026-11-21, `TempHousekeeper`):** the main app (never a sibling process) runs a startup pass 45 s after launch, at most once every 12 h, within 8 s, and an exit pass within 1.5 s. Only these are removed, by exact name pattern, from the processing folder's top level: scratch images/PCM (`fvs_thumb_`, `fvs_wave_`, `fvs_wave_sequence_`, `fvs_music_frame_`, `fvs_viral_frame_`, `fvs_elim_frame_`, `fvs_elim_pcm_`, `fvs_pcm_`, `fvs_captions_live_`, `fvs_cudatag_`, `cand_f1/2/3_<guid>.jpg`) older than 6 h (exit: 15 min); `fvs_preview_mix_<pid>_*` / `fvs_wizard_mix_<pid>_*` when that process is gone (exit: this process's own); and, at startup only, `fvs_job_` / `fvs_merger_` / `ai_frames_` folders whose every entry is older than 6 h. Startup also drops freeze renders older than 30 days and undo histories older than 90 days or whose project file was deleted from a folder that still exists (an unplugged drive keeps them). Links are skipped, deletion is handle-bound (`SafeDirectoryCleanup`), locked files are left. Voice recordings, recovery files, `voiceovers`, settings and anything not on this list are never touched.
- Export success does NOT make voice source files disposable. Preserve recordings referenced by saved
  projects, the open editor, undo or recovery. Existing ownership-based studio cleanup can remove
  uncommitted/discarded session copies or rollback files after a failed Apply. Do not sweep applied
  recordings merely because they are absent from the recent-project list: projects may live anywhere.
  If lack of references cannot be proved, retain the file. No deletion of old recordings during path migration.
- User/OS deletion of TEMP can still remove referenced voice audio and development preferences.
  This layout does not protect TEMP from external cleanup. A later launch recreates working directories;
  missing media requires relinking/re-recording. Persistent recovery JSON cannot reconstruct lost WAVs.
- Never clear all of the production processing root: it now also holds applied voice recordings.
  Caches may be regenerated; active operations can fail if TEMP is removed during execution.
- Existing custom paths and older recording paths remain valid; source files are not moved/deleted.
  Sharing music/memes/output defaults deliberately shares those files, not settings or IPC identity.

---

## 7. Output Estimate Worker Lifetime {#SYS-SIZEESTIMATE}
* `SIZEESTIMATE_01`: each editing window owns one worker and one pending immutable snapshot. An 80ms throttle coalesces pointer/slider events while still updating during continuous dragging.
* Filesystem access, ffprobe and estimate calculations run off the UI thread. Metadata caches hold at most 128 entries, keyed by path, size and modification time. A changed file is re-probed; failures are not cached. UI-owned queues and duration dictionaries are never mutated from a worker.
* Both quick and refined results carry a request version. The version and disposal state are checked again INSIDE the UI callback; stale callbacks cannot overwrite newer state or touch a closed window.
* Closing cancels the worker, completes its queue, and asynchronously allows up to one second for shutdown before continuing window teardown. ffprobe uses `AsyncProcessRunner` with a 15-second timeout and lifetime cancellation, which terminates the process and drains both pipes. The worker disposes its own cancellation source only after completion; no synchronous waits on the UI thread.

## 8. Atomic Persistence Is Not Optional, And It Is Not Per-Caller  {#SYS-ATOMICWRITE}
* **`SETTINGSATOMIC_01` — every shared-state file goes through `AtomicJsonFile`, under a named mutex.**
  `settings.json` lives in `ApplicationPaths.PreferencesDirectory` (SYS-STORAGE-TABLE), which the Main App, the Video Merger (`--merger`) and
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
* **`SETTINGSFREEZE_01` — the published snapshot is read-only.** `AppSettings`, `KeyBinds`,
  `DefaultValues` and `AiUsageConfig` derive from `FreezableSettings`; every property setter goes
  through `Set(ref field, value)`, which throws (naming the property) once the object is frozen.
  `Update` and `Load` freeze the snapshot (recursively) before publishing it to `Instance` and
  `Committed`; drafts (the patch target, JSON clones, `SettingsManager.Snapshot()`, a view-model's
  pending copy) stay writable. Tests publish in-memory snapshots through `ReplaceInstanceForTests`.
* **`SETTINGSTYPES_01` — closed sets are enums, the file format is unchanged.** `UpdateChannel` (removed 2026-11-12, SETNOBETA_01),
  `VideoEncoderOverride` (`EncoderPreference`, member names = the strategy strings), the three
  `Ai*CloudConsentVendor` stamps (`AiVendor?`, empty = no stamp = legacy Google), `AiTrackingGameProfile`
  (`AiGameProfile`) and `AiUsageOverrides` (`IReadOnlyList<AiUsage>`, immutable) use lenient per-property
  converters (`Infrastructure/SettingsTypes.cs`): any case reads, unknown values fall back to the
  default and are logged, and the values are written as the same strings older builds read. No schema bump.
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
  **SETTINGSVM_01:** the Settings window holds a private clone of `AppSettings` as its pending state and
  writes it through `SettingsViewModel.WriteTo` inside its ONE `Update`; it copies only the fields its
  tabs own (`CopySection`), never remembered "last" values, window state or first-run answers.
  **SETDEFAULTS_01 (additive, no schema bump):** `DefaultValues.LastSpeed` / `LastQualityIndex` hold the
  last exported speed/quality; `PortraitMode` / `ShowTeammates` / `EnableFade` now mean "last choice" for
  the `RememberLast` behaviour. `Start*` resolves Always On/Off/Remember for every fresh-video path, and
  an export records its choices (`RememberChoices`) only when a start value follows the last choice.

* **`CACHEBOUND_01` / `ATOMICBYTES_01` — disposable cache bytes use the same publish sequence, without the mutex.**
  `LaneDiskStore.Write` (the Merger's lane cache, `ApplicationPaths.LaneCacheDirectory`, SYS-STORAGE-TABLE) calls
  `AtomicJsonFile.WriteBytes` — the one `WriteCore` (GUID temp in the target folder, `WriteThrough`,
  `Flush(flushToDisk: true)`, `File.Move(overwrite: true)` with its bounded retry, temp deleted on failure).
  No named mutex is taken because a lost or duplicate cache write is a miss, never lost user state. Readers open with Read|Write|Delete sharing; the background prune deletes
  LRU files and temps orphaned for more than 10 min. Budgets and pruning: `04` UI-MERGERQUEUE.

## 9. Bounded Flush Scheduling & Ordered IPC Teardown  {#SYS-IPCLIFETIME}
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
  explicit — awaits one background task chain (`_ioTail`, PROJECTIO_01), so an older autosave
  finishes before a newer Ctrl+S; the dispatcher never waits on a disk-held monitor (ASYNCUI_01).
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
* **`PIPEAUTH_01` — both ends of every local pipe prove who the other end is.** The pipe names carry the user's SID, which is not a secret, so another account could pre-create the pipe and receive this user's handoff paths and session state. `PipeAccess.CreateServer` gives the state pipe and the single-instance handoff pipe a protected DACL granting only the current user and LocalSystem; `NamedPipeStateServer` adds `FirstPipeInstance` whenever none of its own instances is live, the handoff pipe (one instance) always. Before writing a byte, `IpcProtocol.ExchangeAsync` and `SingleInstanceGuard.TrySendHandoff` call `PipeAccess.IsServerTrusted` (`GetNamedPipeServerProcessId` → process token SID == current user) and refuse otherwise; every failure to establish identity fails CLOSED. Tests: `PipeAccessTests`.

* **In-Process Navigation vs. Standalone CLI & Named Pipe Architecture (`TOOLNAV_01`–`TOOLNAV_04`):**
  Companion tools (Crop Tool, Video Merger) were originally launched via "process suicide": `CompanionAppService` serialized state, invoked `Process.Start(sameExe, "--crop-tool")`, and terminated the host via `Environment.Exit(0)`.
  This was replaced by in-process navigation via `ToolNavigator.cs`:
  1. **In-Process Modal Windowing (`TOOLNAV_01`):** Companion tools open within the same application process.
  2. **Main Window Hidden, Not Closed (`TOOLNAV_03`):** The main editor window is hidden to keep view-models, the document session (`ProjectSession`), and undo history intact.
  3. **Video Pipeline Teardown & Revival (`TOOLNAV_02` / `TOOLRETURN_01`):** The mpv playback pipeline is cleanly torn down before the tool opens to avoid D3D11/OpenGL GPU lock contention, and reconstituted via `MainWindow.ToolReturn.cs` upon the tool's close.
  4. **Standalone CLI & Named Pipe Persistence:** Standalone execution via CLI flags (`--crop-tool`, `--merger`, `--install-worker`, `--cleanup-worker` routed in `Program.cs` and `AvaloniaApp.axaml.cs`) and named pipe state synchronization (`NamedPipeStateServer.cs`, `NamedPipeStateClient.cs`, `StateTransferStore.cs`, `CropConfigStore.cs`) remain active for installer workers, test automation, and independent tool invocations.

---

## 10. Settings Schema Migrations & Secret Hygiene  {#SYS-SETTINGSSCHEMA}
Settings-file schema steps (`SettingsManager`) and the secret scan. Moved out of the former auto-update section; update behaviour is `09` SYS-AUTOUPDATE, AI settings are `10` SYS-AISETTINGS.
* **Schema v13 (DUCKSTRENGTH_01):** adds `Defaults.DuckingEnabled` / `CarvingEnabled` (seeded ONCE from the legacy `Defaults.AudioProtection`, so a user who had protection off keeps it off) and `Defaults.DuckingStrength` / `CarvingStrength` (default 50 = tuned). Values the user sets are never reset by a later migration.
* **Schema v12 (LOUDSTD_REMOVED_01 / CLIPLEVEL_01 / VOLMUTE_01):** removes `LoudnessNormalizationPrompt` and `Defaults.AutoVoiceNormalization` (old keys are ignored on read and dropped on the next write); adds `MergerMatchClipLoudness` (default OFF) and `PreviewMuted` (default OFF). The master LEVEL stays in the session state (`MainVolume`), written by `MasterVolumePersistence` off the UI thread.
* **`SECRETSCAN_01` — nothing secret reaches git.** `tests/FreeVideoStudio.App.Tests/SecretLeakTests.cs` (run by CI on every push) scans every file git tracks or would add (`git ls-files --cached --others --exclude-standard`; without git: `src`, `tests`, `docs`, `build`, `.github`) for Google API keys, private-key blocks, GitHub tokens and `sk-…` keys, fails if a `.pfx`/`.p12`/`.key.pem`/`.password.txt`/`.snk` file is visible to git, and checks that `.gitignore` covers those types. Git-ignored files (the developer's own `ssl-certificate/*.pfx`, `*.key.pem`, `*.password.txt`) are deliberately NOT read. Fake test keys are written split (`"sk-" + "ant-dummy…"`) so the patterns never match (NOCOMMENTS_01). The scanner still honours a `SECRETSCAN_ALLOW` marker on a line (`SecretLeakTests.cs`, the `SECRETSCAN_ALLOW` line check), but no tracked file uses it.
* **Schema v9 (REMOVEUX_01):** upgrading from v8 or older sets `ConfirmVideoMergerRemove = false` once (product decision 2026-09-27: removal is instant and undoable); a v9 file keeps the user's choice. `ApplicationPaths.LaneCacheDirectory` (see SYS-STORAGE-TABLE) holds the Merger's per-clip filmstrip PNGs and waveform peaks (LANECACHE_02), best-effort, pruned to 800 files.
* **Schema v7 Migration Invariant:** When upgrading from older application installations lacking update checking (or whenever `settings.json` lacks an explicit `AutoUpdateChecks` configuration), `SettingsManager` automatically initializes and persists `AutoUpdateChecks = true`. On fresh installs without an existing config file, default settings with `AutoUpdateChecks = true` are saved immediately to disk, ensuring new releases are never silently missed. Since UPDATECONSENT_01 that default only takes effect after the user's yes.

## 11. The Fix-Sentinel Check Is Code Now  {#SYS-VERIFYTOOL}

The former `VERIFY_PATCHES` batch subroutine has been removed. The list lives in
`build/sentinels.txt` and the checker is `build/FvsVerify`, with its own tests in
`tests/FvsVerify.Tests`. `dev.cmd` invokes the checker directly and propagates failure
before launch cleanup (`DEVVERIFY_01` / `VERIFYHALT_01`); it contains no sentinel list.

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

**Retiring a sentinel:** when its defect becomes structurally unreachable, retire it with a note instead of leaving a stale tag (STRIPCOST_01: the per-slot `Image` path was replaced by `Controls/TimelineFilmstrip`, which draws with explicit source/destination rects, so no oversized layout box or 32768-px bitmap can exist; precedent WIZPROGRESS_01).

**To add a sentinel: add one `TAG=path` line to `build/sentinels.txt`.** That is the whole procedure. NOCOMMENTS_02 (2026-11-15): code carries no comments, so a tag that is not in real code (a string, an identifier) cannot be found in the file. Such a line names the member the fix lives in instead: `TAG=path#Symbol`, checked as a whole-word match of `Symbol` (`SentinelFailure.SymbolMissing`). The comment remover (now `developer_tools/Find_and_Remove_Comments.cmd` -> `developer_tools/CommentRemover`) re-pointed 697 lines this way; the comment text they replaced is in `docs/CODE_NOTES_*.md`. Weaker than a tag: a member name survives a reverted fix, and about 230 lines name a whole type because the tag only appeared on the type. Prefer a behavioural test for anything that matters.

DEVVERIFY_01: sentinel annotations are functional verification inputs; comment removal must re-point them
(`TAG=path#Symbol`, NOCOMMENTS_02), never silently drop them. The 2026-10-18 cleanup removed 256 existing references and
omitted tags for newer fixes. Never restore them as code comments (NOCOMMENTS_01/02); retain the strict checker and retire
only superseded paths with an explicit `# RETIRED` reason. Never strip `//` inside string literals (UPDATEPIN_PEM_01).


**Comment remover (NOCOMMENTS_02 / NOCOMMENTS_03, moved from README §0.1).** `.\developer_tools\Find_and_Remove_Comments.cmd` (or double-click it; it runs the C# project `developer_tools/CommentRemover/CommentRemover.csproj` from the repository root) takes no arguments and reads every code file git tracks or would add, first PRINTS a preview (full file path, line number and text of every comment it would remove, the number of lines it would delete per file, and any sentinel it would re-point) and asks `[Y/N]`; only Y writes (N, or no input, exits 2 with nothing written). On Y it removes comments and extra blank lines, appends the removed text word for word to `docs/CODE_NOTES_App.md` / `CODE_NOTES_Core.md` / `CODE_NOTES_Tests.md` / `CODE_NOTES_Tools.md` (grep-only history, the spec wins), re-points affected sentinels to `TAG=path#Symbol`, and exits 0. It checks every file with that language's own parser (Roslyn, `XmlReader`, YamlDotNet, the PowerShell parser, Python `tokenize`) and refuses (exit 1, nothing written) if any file would change meaning, does not parse, or a sentinel cannot be re-pointed; all files are written together or none (a file changed meanwhile, or a failed write, puts every file back). `ArchitectureRuleTests.NoCSharpFileContainsAComment` enforces the `.cs` part in CI.
Blank lines: none at the start or end of a file; at most one in a row (Python: two before top-level code); in `.cs` none right after `{` or before `}`; never inside a string, here-string or block scalar.

## 12. AI source attachment exporter {#SYS-CODEEXPORT}

`CODEEXPORT_01`: `developer_tools/Pull_Code_files.py` exports the current working tree for an
advanced online AI GUI (including GPT-6 Astra with max reasoning). It is offline: it neither uploads
files nor selects a model. Run `python -B developer_tools/Pull_Code_files.py`.

* **Preserve previous work:** create a unique UTC-dated child under
  `%USERPROFILE%\Downloads\free_video_studio` (or `--output-parent`). Never delete old exports,
  unrelated text files, or Python caches. Use exclusive writes and write the manifest last.
  The output parent must be outside the repository to prevent self-inclusion.
* **Ten attachments maximum:** one manifest, one complete path/status/bundle-line index and up to
  eight text bundles. Keep source files whole. Include `.iss` installer source and every eligible
  test file; classify the whole `tests/` subtree before build-name matching. Assets/certificates
  have metadata only. Unknown formats remain visible as metadata, never silently disappear.
  `CODEEXPORT_02`: every metadata-only file also gets a placeholder block in the bundles at its
  path (type, image size for PNG/JPEG/GIF/BMP, source bytes, SHA256 of the file bytes, reason).
  Bundles are balanced: the smallest largest-bundle split into at most eight ordered parts.
  `docs/CODE_NOTES_*.md` (grep-only removed-comment history) is a placeholder unless
  `--include-code-notes` is given.
* **Account for gaps:** the index records skipped files and excluded subtrees, encoding errors,
  redactions and metadata-only assets. Reconcile tracked paths against the traversal; missing
  tracked files/read failures produce an INCOMPLETE warning and exit 2; a tracked file deleted in
  the working tree (`git ls-files --deleted`) is `DELETED`, not an error. Links/reparse points are
  not followed. Generated/private directories are excluded as entire subtrees, with their reason;
  so is everything git ignores (`git ls-files --others --ignored --exclude-standard --directory`,
  e.g. `.dev-run/`), listed as `SKIPPED` with reason `git-ignored`.
* **Living context:** derive groups/top-level entries from this snapshot; route architecture and
  framework facts to included specs and project files instead of copying stale prose. Put the
  entry documents first. Record UTC, Git commit/local changes and exported-text SHA256 values.
  This is a live working-tree read, not an atomic Git snapshot. Report observed concurrent changes.
* **GUI limits are unknown unless supplied:** never substitute an API model limit for a GUI/account
  limit or claim that max reasoning guarantees full attachment ingestion. Count actual UTF-8 bytes
  of all final attachments, including indexes/headers; label bytes/4 token estimates as heuristics.
  `--ui-context-tokens`, `--reserve-tokens`, and `--max-file-bytes` accept user-confirmed budgets
  for advisory warnings only; never truncate to meet a guessed budget. Optional repeated
  `--focus "src/.../*"` globs retain all docs/configs/tests but explicitly omit other source and
  warn that dependencies may be missing. The full export remains the default.
  No vendor publishes a reliable upload/context limit for an online chat session (checked 2026-10-19),
  so budgets stay advisory and are never used to truncate.
* **Sensitive data:** exclude private-key/password/environment filenames before reading; redact
  recognizable tokens, private-key blocks and literal credential assignments. A credential
  assignment is redacted only when its value looks like a secret literal (no spaces, 6+ characters,
  not a call, variable, `a.b` member path, ALL_CAPS name, plain word or `Verb-Noun`); unquoted
  `key=value` assignments are redacted only in script/config files (`.cmd`, `.bat`, `.ps1`, `.ini`,
  `.toml`, `.env`, …), never in source code, so `apiKey = input;` reaches the AI intact. Record redactions
  without echoing values. Scan generated metadata too. Detection is heuristic, not a guarantee;
  review locally before upload. Exported hashes identify filtered decoded text, not secrets.
* **Encoding:** strict UTF-8/BOM-aware UTF-16/UTF-32; detect strongly indicated BOM-less UTF-16.
  Never silently replace invalid characters. `--legacy-encoding cp1252` explicitly enables a
  strict, labelled fallback. The existing `mp3/!_System Recall - Aftertune (No CopyRights).txt`
  uses byte `0x97` for dashes; the exporter declares `cp1252` for that exact path, labels it in the
  export and includes it with no command-line flags. Other invalid text still fails unless the
  user explicitly chooses a fallback. Do not generalize this exception to arbitrary files.
* **Test evidence:** optional repeated `--test-result PATH` copies existing reports through the
  same filters. Clearly mark them potentially stale; never infer passing tests from source files.
  The exporter never executes app builds/tests. Run its own regression suite with
  `python -B -m unittest discover -s tests/DeveloperTools.Tests -v`; CI runs it in the sentinel job.
  Its fixtures live in `logs\Python_Results\work` and are replaced on every run (TESTOUT_01); the exporter
  never reads `logs\` unless a report is passed with `--test-result`.

---

## 13. A Bundle The User Can Actually Send  {#SYS-DIAGREPORT}

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

## 14. Antivirus-Clean Deployment Behaviour  {#SYS-AVCLEAN}

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
  and no copy is ever made. Update downloads and the broker's staging folder moved from
  `%TEMP%\FVS_AutoUpdate` / `%TEMP%\FVS_Upgrade` to `%LOCALAPPDATA%\FreeVideoStudioUpdates\Downloads` /
  `\Stage` (the elevated worker itself runs from a protected `%ProgramData%` copy since WORKERSTAGE_01,
  SYS-UPGRADE-TRUST) (`DeploymentFootprint.UpdatesRoot`, deliberately OUTSIDE `%LOCALAPPDATA%\FreeVideoStudio`,
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
* **NOSELFDELETE_01 / CLEANUPBOUND_01 — fail-closed uninstall cleanup.** All recursive purge,
  shortcut deletion and final self-cleanup in `DeploymentLifecycle` use `SafeDirectoryCleanup`.
  It pins each ancestor and target without write/delete sharing, rejects reparse points, and sets
  delete disposition on the validated handle. Linked, locked or unprovable entries are retained
  and reported; there is no path-based recursive retry, attribute mutation or reboot deletion queue.
  Read-only hard links are refused rather than changing attributes shared by another pathname.
  A failed subtree stops that subtree's traversal. Running binaries may remain after uninstall;
  close the app and retry removal. No script host or delayed mutable pathname performs cleanup.
* **NOCMD_01 — links open through the shell.** The WhatsApp share (`MainWindow.OnSuccessAction`,
  `VideoMergerWindow.OnSuccessAction`) starts the `whatsapp://` URI with `UseShellExecute = true` instead of a
  hidden `cmd /c start`.
* **NOHIDDENTOOLS_01 — no hidden Windows tools (2026-11-11).** Three were left after NOPOWERSHELL/NOCMD:
  the event tool started hidden at EVERY launch (`CrashLogDigest`), the task tool started hidden at every
  install, uninstall and maintenance run, and a force-kill tool as a second attempt in
  `DeploymentLifecycle.TerminateProcessAsync`. Now: `CrashLogDigest` reads the Application log through the
  Windows Event Log API (`wevtapi.dll`: `EvtQuery`/`EvtNext`/`EvtRender`, direct `LibraryImport`, NativeAOT-safe)
  and condenses each event's XML (`CondenseXml`, `Mentions`); the task tool runs ONLY when
  `LegacyScheduledTask.Exists` (task file or Task Scheduler registry index, read-only) says a task of an old
  release is still there; the force-kill fallback is gone (a process Windows will not end is logged and the
  locked file is reported by the file step). ⚠️ The log line no longer carries Windows' formatted description,
  only the recorded values (program, module, exception code).
* **NOLOGONHOOK_01 — no Active Setup entry (2026-11-11).** The machine install wrote
  `HKLM\…\Active Setup\Installed Components\{24CA6D78-…}` with a `StubPath`, so Windows started the program at
  every user's next sign-in (run-at-logon persistence, MITRE T1547.014). It was redundant: every normal start
  from the install folder already runs `UpgradeCoordinator.CompleteUserAsync` (`Program.cs`).
  `UpgradeRegistration.ApplyAsync` now deletes the entry of an older release; the key stays in `KeyPaths`, so a
  rollback restores what was captured. Consequence: another user's shortcuts and file associations are
  completed when THEY first start the app, not at their sign-in. `--complete-user-migration` is still accepted
  (entries of older installs until their next update).
* **NOMEMREAD_01 — no other process's memory is opened (2026-11-11).** The elevated uninstaller read `Process.MainModule`
  of EVERY running process (`PROCESS_VM_READ`), antivirus and `lsass.exe` included — the credential-dumping signature
  (MITRE T1003.001) — and ended every process named `Uninstall` anywhere on the PC. `ProcessImage.GetPath`
  (`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW`) replaces every `MainModule` read
  (`DeploymentLifecycle.KillAllProcessesAsync`, `InstallDiscovery.EnsureIdle`/`FindOpenApps`, the worker's first-launch
  check). The uninstaller looks only at processes whose name is in `DeploymentFootprint.ProcessNames` (the program, the
  uninstaller, `ffmpeg`, `ffprobe`, `mpv`) AND that run from the install folder. Side effect: the elevated idle check can
  now read another account's copy instead of refusing with "could not be checked".
* **OPENWITHHOME_01 — "Open with" points only at the installed program (2026-11-11).** `ShellFileAssociation.EnsureRegistered`
  ran in every UI mode, so the installer's unpacked copy (`%TEMP%\is-…\fvs`), the `%ProgramData%` worker and the
  uninstaller each wrote `HKCU\Software\Classes\FreeVideoStudio.Video\shell\open\command` pointing at themselves — an
  open command into a temporary folder (T1546.001), broken once the folder was gone. It now returns unless the running
  file IS `DeploymentFootprint.InstallPath` (`IsInstalledProgram`). A portable copy elsewhere no longer registers.
* **NOMUIWIPE_01 — Windows' run caches are not edited (2026-11-11).** Uninstall deleted this app's values from the three
  `MuiCache` keys (HKCU and HKLM) — trace clearing (T1070). Removed; the values are Windows' own display-name cache.
* **NOLOCALPORT_01 — no TCP port (2026-11-11).** The broker ↔ elevated worker and broker ↔ first-launch health channels
  (`UpgradeChannel`) were `TcpListener`s on 127.0.0.1; third-party firewalls prompt for or block an unknown program that
  listens. They are named pipes (`PipeAccess.CreateUpgradeServer`): random `FreeVideoStudio-Upgrade-<32 hex>` name,
  created BEFORE the other process starts, one instance + `FirstPipeInstance`, DACL = current user + LocalSystem +
  Administrators (the worker may run as another administrator account, UPGRADE_07), 256 KiB buffers (a sender never
  waits for the reader, like the socket did). Exactly one connection is accepted; its first message must be the 64-hex
  token, otherwise the upgrade stops ("An unexpected program answered the installer"). The pipe is never re-created, so
  no other program can own the name while the worker looks for it. The health client also checks that the server runs
  as the same user (PIPEAUTH_01). `--upgrade-worker <pipe> <token>` / `--upgrade-health <pipe> <token>` replace the port.
* **NOLURKER_01 — no installer waiting in the background (2026-11-11).** "Later" on the update card started the verified
  installer at once with `--wait-pid`, so a hidden process sat until the app closed and then raised a UAC prompt by
  itself. "Later" now only arms INSTALLONEXIT_01's exit hook, which starts the same installer when the app closes.
  Same result for the user.
* **Deliberately unchanged (2026-11-11 audit):** (a) the install still unpacks to Inno's `{tmp}` and runs the elevated
  worker from `%ProgramData%\FreeVideoStudio-Worker-<id>` (WORKERSTAGE_01): letting Inno elevate would run the program
  elevated from `%TEMP%` (NOTEMPEXEC_01) or require rewriting the transactional installer; (b) the RDP Auto-Fix
  (NOPOWERSHELL_02) keeps its one-click elevated policy write: it runs only when the user clicks it and confirms UAC,
  and the alternative is several manual Group Policy / Registry Editor steps.
* No `powershell.exe`, `cmd.exe /c`, `-ExecutionPolicy Bypass`, `-WindowStyle Hidden` or `-EncodedCommand`
  in production code under `src/`. Enforced by `AvCleanRuleTests` and `AvFriendlyBehaviourTests` (App tests).

## 15. No Disk, No Named Mutex On The Edit Path  {#SYS-EDITHOT}
* **EDITHOT_01:** `ProjectSession.Capture()` runs on every edit tick. It now reads the HUD mask from `LiveMaskCache.Current` (a memory snapshot refreshed on the thread pool by a FileSystemWatcher and by profile-name changes) and the source fingerprint from a per-path cache. Only a user-initiated save (`Capture(forExplicitSave: true)`) touches the disk and the mutex (`LiveMaskCache.ReadNow`). Autosave uses the snapshots.
* **EDITHOT_02:** `SaveRecoveryState` pushes undo immediately (memory) and writes the recovery file 750 ms after the LAST change (`MainWindow.Recovery.cs`). `sync: true` callers write immediately.

## 16. Persistence Never Goes Backwards  {#SYS-WRITEORDER}
* **WRITEORDER_01 (`RecoveryManager`):** every whole-file save and clear takes a version from a STATIC counter at call time and applies only if it is newer than the last one applied to that file. Before, a `SaveStateAsync` queued before `ClearState()` (undo-to-empty, clean shutdown) could resurrect the file, and ordering was per instance. `UpdateGranularSession` is a serialised sub-key merge and is not versioned.
* **WRITEORDER_02 (`NamedPipeStateServer`):** `_flushGate` is held from snapshot to rename, `_stateVersion` makes writes monotonic, and a failed write re-arms `_isDirty`. Clients never race these flushes with their own file writes: see [IPCOWNER_01](#SYS-IPCLIFETIME).
* **WRITEORDER_03 (`VoiceOverRecoveryManager`, VORECOVERY_01):** voiceover recovery indexes are per (canonical video, session) `voiceover_recovery_{videoKey}_{sessionId}.json`, written off the UI thread, process-serialized, merged by take id (records another owner holds are preserved), via a unique `{path}.{guid}.tmp` + write-through flush + atomic replace; an unreadable existing index is never overwritten or deleted. Uncommitted takes remain durable across unexpected close or crash until an explicit Apply commit (`CommitTakesAsync`, committed ids only) or a proven operator Discard. A file is deleted only when no durable record — this manager's or anyone else's — remains in it. Full contract: [AUD-VOICEOVER](02_AUDIO_ENGINE_MASTERING.md#AUD-VOICEOVER).

## 17. Mutable State Is Per Windows User  {#SYS-USERSCOPE}
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

* **LEGACYKEEP_01:** Legacy `%TEMP%` roots (`LegacyTempNames`) may contain referenced audio and are retained. Rescued renders (`*-RECOVERED-*.mp4`) are moved into `ApplicationPaths.TempDirectory` first, renamed to `FreeVideoStudio-RECOVERED-…` (Merger rescues keep `Merged-Videos-RECOVERED-…`), never overwriting (a `-2`, `-3` … suffix is added). If any rescue cannot be moved, the folder is kept.
* Legacy Local/Roaming roots are always retained automatically. A same-named destination proves neither equal content nor that saved projects no longer reference the original. Conflicting versions remain at their original paths even across repeated launches; removal requires explicit uninstall or manual cleanup.
* The shared `%ProgramData%` legacy root is never deleted here. The elevated uninstaller removes it with every legacy user root when user data is removed (`DeploymentFootprint.GetDirectoryPurgeTargets`, `includeUserData`).

The sweep never throws; failures are logged under the `Rebrand` tag. `RebrandTests` covers rescue moves, collision-free renames, same-name conflicts, repeated-sweep retention, referenced audio, the closed safety gate, and the repository-wide previous-name allow list.

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
