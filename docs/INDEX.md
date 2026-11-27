# SYMBOL & FILE INDEX (routing lookup)

> 🛑 Rules (never `Build.cmd`, Proof-of-Read, sentinels, change records, test output): [`README.md`](README.md) §0 — read it first.

Flat lookup. Grep for a filename, symbol, constant or engineering tag; read ONLY the spec it names.
Notation: `03 FFM-BINPATH` = `03_FFMPEG_EXPORT_PIPELINE.md`, section 9, stable anchor `FFM-BINPATH`.
Section NUMBERS shift as specs grow. The `{#ANCHOR}` ids are STABLE — quote anchors in Proof-of-Read headers, never bare numbers.
`mini-map only` = the symbol is bound to the spec but not discussed in its prose body; read that spec's Code Mini-Map row.
`[code-only: File]` = the tag/symbol is named in NO spec's text; since NOCOMMENTS_02 a tag's original wording is under that file in `docs/CODE_NOTES_*.md` and its sentinel (if any) is `TAG=path#Symbol` in `build/sentinels.txt`; the route given is the closest spec section, or just the spec governing that file. Treat the missing prose as a documentation gap, not as permission.
`Mechanical Sentinels vs Spec Anchors`: Engineering tags in `build/sentinels.txt` are compile/verify gates enforced by `build/FvsVerify`. Tags listed below without a `build/sentinels.txt` line serve as specification section anchors and architectural invariant markers.

---

## 1. Source File -> Spec

`⚠` = CO-GOVERNED. Reading one listed spec is NOT compliance; read them all (`SPEC_GOVERNANCE.md` §2).

⚠️ **Canonical but may lag.** Source-file routing headers (`[SPEC CONTRACT]`) and their test were retired (SPECROUTING_02, `SPEC_GOVERNANCE.md` §4). An unlisted file is a routing gap, not permission: grep the spec Code Mini-Maps (`docs/0*_*.md`, `docs/10_*.md`) and add its row here in the same change (README §0.3 rule 3).

```
  AVREG_01 · tests/run-av-tests.cmd · tests/FreeVideoStudio.AvRegression.Tests/ (PlayerRig, PlaybackProbe, ExportRig, Verify, MergerRig, MainEdit, TestMedia, Budget)  ->  README.md GOV-AVGATE (§0.3a)
  developer_tools/Find_and_Remove_Comments.cmd, developer_tools/CommentRemover/ (CommentRemover.csproj, Runner, TextRewriter, Languages, CommentScanner, YamlLanguage, ExternalTokenizers), docs/CODE_NOTES_*.md, NOCOMMENTS_01, NOCOMMENTS_02, NOCOMMENTS_03, NOCOMMENTS_04, DataFilesWithHashComments, NoCSharpFileContainsAComment, tests/FreeVideoStudio.App.Tests/FvsCommentsToolTests.cs  ->  README.md GOV-START; 05 SYS-VERIFYTOOL (TAG=path#Symbol)
  developer_tools/Sanitize.cmd, developer_tools/Sanitize/ (Sanitize.ps1, allowlist.txt; ai-sanitize.exe git-ignored), SANITIZE_01, ReleaseGate.RequireNoLeaks  ->  05 SYS-SETTINGSSCHEMA (SANITIZE_01); 09 DIST-RELEASEGATE
  developer_tools/Pull_Code_files.py, tests/DeveloperTools.Tests/test_pull_code_files.py, CODEEXPORT_01, CODEEXPORT_02  ->  05 SYS-CODEEXPORT; offline ten-attachment AI export, coverage, secrets, encoding, snapshot/index and preservation
  Core/Media/VoiceOverPlaybackPlan.cs  ->  01 TL-OUTPUTTIMELINE; 03 FFM-TEMPO; VOCLOCK_01; 02 AUD-FREEZESFX (FREEZEPARITY_01, FreezeVideoLufs)
  Core/Media/VoiceCaptureBudget.cs, VOSAMPLE_01  ->  01 TL-OUTPUTTIMELINE; 02 AUD-VOICEOVER; sample-frame endpoint and immutable opening anchor
  VOWRITE_01, VoiceCaptureBudgetTests  ->  02 AUD-VOICEOVER; terminal WAV-write faults and acknowledged PCM prefix
  Core/Media/VoiceOverClockGuard.cs, Core/Media/VoiceOverTiming.cs  ->  01 TL-OUTPUTTIMELINE; 02 AUD-VOICEOVER; VOSTALL_01, VOTIMING_01
  Core/Media/VoiceOverSplice.cs  ->  02 AUD-VOICEOVER; 03 FFM-TEMPO; VOSPLICE_01
  Core/Media/VoiceOverTakeJson.cs  ->  02 AUD-VOICEOVER; 06 PROJ-SCHEMA; VOTIMING_01
  App/Services/VoiceOverPreviewRenderer.cs  ->  02 AUD-VOICEOVER; 03 FFM-TEMPO; VOCLOCK_01
  App/VoiceOverWindow.PlaybackClock.cs  ->  01 TL-OUTPUTTIMELINE; 02 AUD-VOICEOVER; 03 FFM-TEMPO
  App/MainWindow.VoiceTiming.cs  ->  01 TL-OUTPUTTIMELINE; 02 AUD-VOICEOVER; 04 UI-VOICE-TIMING; 06 PROJ-SCHEMA; 07 UNDO-VOICE-TIMING
  VODEFAULTS_01, VoiceOverPreparedClockTests, VoiceOverEditWarningTests, VoiceOverExportTimingTests, VoiceOverTimingTests  ->  02 AUD-VOICEOVER
  AiSetupWizardWindow.axaml(.cs)             04 UI-AIMODE | 04 UI-AIEASE (AISETUP_02) | 05 SYS-WINSTATE | 10
  AiModeWindow.cs                            04 UI-AIMODE | 05 SYS-WINSTATE
  AiMultiBlockZoomWindow.cs                  04 UI-AIZOOM (AIMULTIBLOCK_01) | 07 UNDO-GRANULAR-APPLY
  MultiBlockZoomPlan.cs                      04 UI-AIZOOM (AIMULTIBLOCK_01) | 07 UNDO-GRANULAR-APPLY
  DialogChrome.cs                            04 UI-AIMODE
  AiZoomStartWindow.cs                       04 UI-AIZOOM (AIZOOMSTART_01) | 10
  AiEaseOfUseTests.cs                        04 UI-AIEASE
  AiWorkflowFixTests.cs                      01 TL-LOCALTRACK | 04 UI-AIZOOM / UI-AICOMMENTARY | 10 SYS-AICAPABILITY
  AiCameraSmoothnessTests.cs                 01 TL-AITRACKING (AICAMSMOOTH_01)
  AiSetup.cs                                 04 UI-AIMODE | 10 SYS-AICAPABILITY (AICAPABILITY_01)
  AiModeTests.cs                             04 UI-AIMODE
  AiSubjectPickerWindow.axaml(.cs/.Correction.cs) 04 UI-AIZOOM (AIPICKERELLIPSE_01, AIZOOMWIZARD_01, AIPICKFOCUS_01) | 10
  AiZoomPreviewModalWindow.cs                04 UI-AIZOOM (AIPREVIEW_01)
⚠ AiTrajectorySmoother.cs                    01 03
  AGENTS.md                                  GOV | README.md §0 (pointer only, ONBOARD_01)
  project_structure.txt                      GOV | README.md §0 (pointer only, ONBOARD_01)
  README.md                                  GOV | GOV-START (single entry point: rules, invariants, domain table)
  REPO_MAP.md                                GOV | GOV-REPOMAP-FILE (repository map)
  10_AI_FEATURES.md                          10 (AI features spec, split from 05)
  CHANGELOG.md                               GOV | CHANGELOG_01 | 09 DIST-RELEASENOTES
  changes.md                                 GOV | CHANGELOG_01 | 09 DIST-RELEASENOTES (technical log)
  AiGameProfile.cs                           01
  AiTrackingPrompts.cs                       01
  GeminiClient.cs                            10
  AiCommentaryPrompts.cs                     01 TL-CAPTIONS
  CaptionModel.cs                            01 TL-CAPTIONS
  CaptionTimeline.cs                         01 TL-CAPTIONS
  CaptionLayoutEngine.cs                     01 TL-CAPTIONS | 03 FFM-CAPTIONS
  CaptionAssWriter.cs                        01 TL-CAPTIONS | 03 FFM-CAPTIONS
  AiCommentaryDialogWindow.axaml(.cs)        04 UI-AICOMMENTARY
  IAiVisionProvider.cs                       10 SYS-AISETTINGS
  GeminiProvider.cs                          10 SYS-AISETTINGS
  ClaudeProvider.cs                          10 SYS-AISETTINGS
  OpenAiProvider.cs                          10 SYS-AISETTINGS
  AiVisionProviders.cs                       10 SYS-AISETTINGS (AIMODELS_02, AIMODELFALLBACK_02)
  AiHttpHelper.cs                            10 SYS-AISETTINGS
  AiCostEstimator.cs                         10 SYS-AIAUDIT4 (AIUSAGE_04)
  AiAnswerGuard.cs                           10 SYS-AIAUDIT4 (AIANSWER_01)
  AiReasoningPolicy.cs                       10 SYS-AIAUDIT4 (AIREASONING_02)
  AiRunApproval.cs                           04 UI-AIAPPROVE | 10 SYS-AIAUDIT4 (AIAPPROVE_01)
  AiAnswerDiskStore.cs                       10 SYS-AIAUDIT4 | 05 SYS-STORAGE-TABLE (AIREUSE_04)
  AiPreflight.cs                             10 SYS-AIAUDIT4 (AIPREFLIGHT_03)
  AiResultCache.cs                           10 SYS-AIAUDIT4 (AIREUSE_03)
  AiDiagnostics.cs                           10 SYS-AISETTINGS (AIDIAGNOSTICS_03)
  AiAudit3Tests.cs                           10 SYS-AIAUDIT4 (AIAUDIT_04)
  AiAudit5Tests.cs                           10 SYS-AIAUDIT5 (AIAUDIT_05)
  AiAudit6Tests.cs                           10 SYS-AIAUDIT6 (AIAUDIT_06)
  AiQaAudit7Tests.cs                         10 SYS-AIQA7 (AIQA_07)
  AiQaAudit8Tests.cs                         10 SYS-AIQA8 (AIQA_08)
  AiIntegrationFixTests.cs                   10 SYS-AIINTEG (AIINTEG_01)
  NewVideoCleanTests.cs                      05 SYS-RECOVERY (NEWVIDEOCLEAN_01)
  ProjectSafetyQaTests.cs                    06 PROJ-DISK | 06 PROJ-CLOSEGUARD | 06 PROJ-INTEGRITY | 06 PROJ-RECENT | 07 | 05 SYS-RECOVERY | 04 UI-MEMEFLOW (QAREG_01, picker through project reload)
  ProjectSafetyQaTests.MemeExport.cs         04 UI-MEMEFLOW | 06 PROJ-DISK | 03 FFM-MEMECONCAT / FFM-MEMECORNER / FFM-FADES (Add, Change, Apply, reopen and final export)
  UiQaFixTests.cs                            04 UI-SAFEGUARDS (SHAREFAIL_01, ESCDIALOG_01, FONTMIN_01, MENUDEDUPE_01, OPENVIDEO_01)
  AiFrameCrop.cs                             10 SYS-AIAUDIT6 (AIKILLCROP_01) | 01 TL-AITRACKING
  AiUsageReport.cs                           10 SYS-AIAUDIT6 (AIUSAGEREPORT_01)
  PopupRegionTests.cs                        01 TL-AITRACKING (AIKILLCROP_01) | 10 SYS-AIAUDIT6
  UxAuditTests.cs                            04 UI-SAFEGUARDS | 04 UI-THEME (EXPORTCLOSE_01, CROPSAVEERR_01, SEGROWTHEME_01)
  AiFrameSequenceExtractor.cs                10 SYS-AISETTINGS
  CommentaryService.cs                       10 SYS-AISETTINGS
  FvsBuild/Program.cs                        05 09
  SecretProtector.cs                         10
  SecretLeakTests.cs                         05 SYS-SETTINGSSCHEMA (SECRETSCAN_01)
  AmbientBubblesBackground.cs                04
  AppDataPaths.cs                            05 SYS-REBRAND
  AppDataPathsTests.cs                       05 SYS-REBRAND
  LegacyAppDataNames.txt                     05 SYS-REBRAND
  SafeDirectoryCleanup.cs                   05 SYS-AVCLEAN (CLEANUPBOUND_01)
  SafeDirectoryCleanupTests.cs              05 SYS-AVCLEAN
  TempHousekeeper.cs                         05 SYS-STORAGE-TABLE (TEMPCLEAN_01)
  TempHousekeeperTests.cs                    05 SYS-STORAGE-TABLE (TEMPCLEAN_01)
  LegacyResidueSweep.cs                      05 SYS-REBRAND-SWEEP
  RebrandTests.cs                            05 SYS-REBRAND-SWEEP | 03 FFM-OUTNAME
  REBRAND_MIGRATION.md                       rebrand record (REBRAND)
  ApplicationPaths.cs                        05
⚠ AtomicJsonFile.cs                          05 06
  AudioFilterChain.cs                        02
  MemeMusicStop.cs                           02 AUD-MUSICFADE (MEMEMUSIC_03)
  AudioLoudnessProbe.cs                      02
  AudioTempoFilterBuilder.cs                 03
  CornerMemeOverlayGraph.cs                  03
  AvaloniaApp.axaml                          04
⚠ Build.cmd                                  05 09 GOV
  dev_build.cmd                              05 09 GOV
  CanvasMath.cs                              01
  UiAccessibilityAuditTests.cs               04 UI-SAFEGUARDS / UI-COACH (DIALA11Y_01, COACHFOCUS_01, ACTIONNAME_01)
  CoachOverlay.cs                            04 UI-COACH (COACHFOCUS_01)
⚠ CompositeTimeline.cs                       01 03
  ConfirmDialogWindow.axaml.cs               04
  CoordinateMath.cs                          01
  DeploymentLifecycle.cs                     05
⚠ dev.cmd                                    05 08
⚠ ExportCoordinator.cs                       03 08
  ExportTimingTag.cs                         03
  FfmpegDiagnosticCollector.cs               03
  ConfirmFlyoutGate.cs                       04 UI-SAFEGUARDS (CONFIRMFLYOUT_01)
  FloatingNotice.cs                          04
⚠ FluidVolumeSlider.cs                       02 04
  FramePtsProbe.cs                           03
⚠ GeminiTrackingService.cs                   01 03 04 10
  GpuCapabilityProbe.cs                      03
  GranularSpeedBuilder.cs                    03
⚠ GranularSpeedEditorWindow.axaml.cs         01 04 05 07
⚠ GranularSpeedEditorWindow.AiZoom.cs        01 04 07
⚠ GranularSpeedEditorWindow.AiCommentary.cs  01 04 07
⚠ GranularSpeedEditorWindow.CaptionsLane.cs  01 04 07
  EditCaptionDialogWindow.axaml.cs           04
  CaptionTrackJson.cs                        06
⚠ GranularSpeedEditorWindow.Merge.cs         01 04
  HardwareScanner.cs                         03
  KineticScrubController.cs                  01
⚠ IExportCoordinator.cs                     03 08
  LatestEstimateWorker.cs                    05
  MainMediaController.cs                     03
  MainWindow.Canvas.cs                       01
  MainWindow.Export.cs                       03 | 05 SYS-RECOVERY (MERGESESSIONVIDEOS_01)
  MainWindow.SizeEstimate.cs                 03
  MainWindow.Shortcuts.cs                    01
⚠ MainWindow.ToolReturn.cs                   05 08
  MainWindow.Wireup.cs                       01
⚠ MainWindow.axaml.cs                        01 02 04
  MaskOverlayManager.cs                      05
⚠ MemeLoudness.cs                            02 03
⚠ MasterVolumeUi.cs                          02 04
⚠ MixProtectionViewModel.cs                  02 04
⚠ AudioGraphPruner.cs                        02 03
  PeakSafety.cs                              02
  WindowsAudioSessionSync.cs                 02
⚠ MainWindow.PreviewMix.cs                   01 02
  RenderedMixGate.cs                         02
⚠ FreezeSoundEffect.cs                       02 AUD-FREEZESFX (FREEZESFX_05) | 03
  FreezeSoundPreview.cs                      02
  MpvLogCap.cs                               05 SYS-LOGGING | 05 SYS-STORAGE-TABLE
  ReviewTimelineRuler.cs                     04 UI-MUSICWIZARD
  UiStallWatchdog.cs                         05 (UIWATCHDOG_01)
  BlockingCallTracker.cs                     05 (UIWATCHDOG_01) | 02 AUD-IPCSTATE
  UiThreadBlockingTests.cs                   02 AUD-IPCSTATE (UIBLOCK_01, NAUDIO3OUTPUT_01) | 05
  FreezeSoundParityTests.cs                  02 AUD-FREEZESFX (FREEZEPARITY_01)
  MainWindow.FreezeSound.cs                  02 AUD-FREEZESFX (FREEZEPARITY_01)
  MusicWizardWindow.FreezeSound.cs           02 AUD-FREEZESFX (FREEZEPARITY_02)
  FreezeSoundPreviewSliceTests.cs            02 AUD-FREEZESFX (FREEZEPARITY_02)
⚠ VoiceOverWindow.PreviewProtection.cs       01 02
⚠ PreviewFidelity.cs                         03 04
  PreviewFidelityBadge.cs                    04
⚠ MainWindow.PreviewFidelity.cs              03 04
  MemePreviewDirector.cs                     03
⚠ MemePlacement.cs                           01 03
  MemePresentationJson.cs                    06
⚠ MemeChoiceViewModel.cs                     04 01
⚠ CornerMemeOverlayPresenter.cs              04 03
⚠ GranularSpeedEditorWindow.Memes.cs          01 03 04 07 (MEMEMODE_01, MEMESTRIP_02, CORNERPARITY_02, MERGEMEME_02, MEMELIST_01)
⚠ MainWindow.CornerMemes.cs                  01 04
⚠ VideoMergerWindow.CornerMemes.cs           01 04
⚠ MergeClipGraph.cs                          01 03
⚠ MergeEditorBridge.cs                       01 03
⚠ MergeEdl.cs                                01 03 06
  MergerAutosaveStore.cs                     05
⚠ MergerPreviewPlan.cs                       01 03
⚠ MergerSession.cs                           01 05
  MergerWorker.cs                            03 (SHARECOVER_01 portrait custom-thumbnail cover)
  MicLevelMonitor.cs                         02
  MicrophoneSpectrumAnalyzer.cs              02 AUD-VOICEOVER (SPECTRUM_01)
⚠ MicrophoneSpectrumControl.cs               02 04 (SPECTRUM_03)
  MobileFilterBuilder.cs                     03 | 04 UI-GRANULAR (FINALLAYOUT_01 HudCanvasLayers)
  IpcProtocol.cs                             05
  MpvIpcClient.cs                            02
⚠ MpvVideoView.cs                            04 05 (GPUPRESENT_04 live texture rebuild)
  NamedPipeStateServer.cs                    05
  IpcTests.cs                                05 SYS-IPCLIFETIME | 05 SYS-WRITEORDER
  IpcChildProcess.cs                         05 SYS-IPCLIFETIME
⚠ MusicWizardWindow.axaml.cs                 01 02 | 04 UI-MUSICWIZARD
⚠ MusicWizardWindow.axaml                    04 UI-MUSICWIZARD | 02 AUD-DIALOGS
⚠ MusicWizardWindow.Playlist.cs              02 AUD-MUSICWIN / AUD-DIALOGS | 04 UI-MUSICWIZARD (MUSICBROWSE_01) | 07 UNDO-MIGRATION
⚠ MusicWizardWindow.SongLane.cs              01 | 02 AUD-MUSICWIN | 04 UI-MUSICWIZARD
⚠ MusicWizardWindow.Steps.cs                 04 UI-MUSICWIZARD | 02 AUD-DIALOGS / AUD-MUSICWIN | 07 UNDO-MIGRATION
⚠ MusicWizardWindow.SoundPreview.cs          02 AUD-SIDECHAIN / AUD-PREVIEWSYNC | 04 UI-MUSICWIZARD
  MusicWizardWindow.AiAutoFit.cs             02 AUD-MUSICWIN | 04 UI-MUSICWIZARD | 10 SYS-AISETTINGS
  MusicWizardWindow.Scan.cs                  02 AUD-MUSICWIN | 04 UI-MUSICWIZARD
  MusicWizardWindow.Guide.cs                 04 UI-MUSICWIZARD (GAPWARN_01 / PARTDEMO_01)
  MusicWizardWindow.PartAxis.cs              04 UI-MUSICWIZARD (PARTAXIS_01 / PARTCUT_01) | 03 FFM-THUMBSTRIP
  MusicWizardPartAxisTests.cs                04 UI-MUSICWIZARD | 03 FFM-THUMBSTRIP
  MusicWizardFlowTests.cs                    04 UI-MUSICWIZARD (MUSICWIZTEST_01)
  WholeVideoFilmstrip.cs                     03 FFM-THUMBSTRIP (WHOLESTRIP_01)
  AiMusicAutoFitService.cs                   02 AUD-MUSICWIN | 10 SYS-AISETTINGS
  AiMusicAutoFitTests.cs                     02 AUD-MUSICWIN | 10 SYS-AISETTINGS
  AiViralPostService.cs                      04 UI-MERGER-MULTISELECT | 10 SYS-AISETTINGS
  AiViralPostTests.cs                        04 UI-MERGER-MULTISELECT | 10 SYS-AISETTINGS
  AiEliminationTrackerService.cs             01 TL-AITRACKING (KILLGATE_01) | 10 SYS-AISETTINGS
  AiKeyHealth.cs                             10 SYS-AIAUDIT4 (AIKEYHEALTH_01)
  AiAccountFix.cs                            10 SYS-AISETTINGS, 04 UI-AIEASE (AIFIXFLOW_01)
  AiStatusLine.cs                            10 SYS-AISETTINGS, 04 UI-AIEASE (AISTATUSLINE_01)
  AiKeyStartupCheck.cs                       10 SYS-AISETTINGS (AIKEYSTARTUP_01)
  AiThinkingCalibration.cs                   10 SYS-AISETTINGS (AITHINKCAL_01)
  ChatGptAuth.cs                             10 SYS-AIAUDIT4 (CHATGPTSIGNIN_01) | 05 SYS-STORAGE-TABLE
  ChatGptPlanTransport.cs                    10 SYS-AIAUDIT4 (CHATGPTPLAN_01)
  ChatGptSignInTests.cs                      10 SYS-AIAUDIT4
  AiReviewFixTests.cs                        10 SYS-AIAUDIT4
  QaAuditFixTests.cs                         10 SYS-AIAUDIT4 | 07 UNDO-RULES
  HudOverlayEventDetector.cs                 01 TL-AITRACKING (KILLGATE_01, AIELIM_06)
⚠ LocalTrackingService.cs                   01 TL-LOCALTRACK | 04 UI-AIZOOM | 10 SYS-LOCALAI
  LocalTrackingMath.cs                       01 TL-LOCALTRACK
⚠ LocalSubjectDiscovery.cs                  01 TL-LOCALTRACK | 10 SYS-LOCALAI (LOCALDISCOVER_01)
⚠ YoloOnnxDetector.cs                       01 TL-LOCALTRACK | 10 SYS-LOCALAI (LOCALDETECT_01)
⚠ LocalSubjectTracker.cs                    01 TL-LOCALTRACK | 10 SYS-LOCALAI
⚠ Sam2BoxTracker.cs                         01 TL-LOCALTRACK | 10 SYS-LOCALAI
  SceneCutDetector.cs                        01 TL-LOCALTRACK (SCENECUT_01)
  RawVideoFrameReader.cs                     01 TL-LOCALTRACK
  OrtRuntime.cs                              10 SYS-LOCALAI
⚠ LocalAiPack.cs                            10 SYS-LOCALAI | 09 DIST-LOCALAIPACK
⚠ LocalAiPackManifest.cs                    10 SYS-LOCALAI | 09 DIST-LOCALAIPACK
⚠ LocalAiPackFiles.cs                       10 SYS-LOCALAI | 09 DIST-LOCALAIPACK
⚠ LocalAiPackService.cs                     10 SYS-LOCALAI | 09 DIST-LOCALAIPACK
⚠ SettingsWindow.LocalAi.cs                 04 UI-AIZOOM (UI-SETTINGS-LOCALAI) | 10 SYS-LOCALAI
  developer_tools/local_ai_pack/*.py         09 DIST-LOCALAIPACK
  LocalAiTests.cs, OrtRuntimeTests           01 TL-LOCALTRACK | 10 SYS-LOCALAI
  LocalAiAppTests.cs                         04 UI-AIZOOM | 01 TL-LOCALTRACK
  AiEliminationTrackerTests.cs               01 TL-AITRACKING | 10 SYS-AISETTINGS
  MainWindow.AiElimination.cs                01 TL-AITRACKING | 10 SYS-AISETTINGS
  MainWindow.QuickStart.cs                   04 UI-SAFEGUARDS (DROPCLICK_01) | 04 UI-AIEASE (KILLQUICK_01, AIHINT_01, AIHINT_02 — start-screen AI hint removed)
  AiOnboardingTests.cs                       04 UI-SAFEGUARDS | 04 UI-AIEASE
  FinishedDialogWindow.AiViral.cs            04 UI-MERGER-MULTISELECT | 10 SYS-AISETTINGS
  AiJsonSchema.cs                            10 SYS-AISETTINGS (AISCHEMA_01)
  AiJsonSchemaTests.cs                       10 SYS-AISETTINGS (AISCHEMA_01)
  AiFfmpeg.cs                                10 SYS-AISETTINGS (AIFFMPEG_01)
  AiAuditFixTests.cs                         10 SYS-AISETTINGS (AIAUDIT_01)
  AiUsageMeter.cs                            10 SYS-AISETTINGS (AIUSAGE_01)
  AiUsageMeterTests.cs                       10 SYS-AISETTINGS (AIUSAGE_01)
  CaptionAuditFixTests.cs                    01 TL-CAPTIONS | 03 FFM-CAPTIONS (AIAUDIT_01)
  QaAuditRegressionTests.cs                  01 TL-CAPTIONS | 03 FFM-CAPTIONS | 06 (recent projects) | 03 RESCUE_02 (CAPTIONEMPH_02, CAPTIONFIT_02, PROJ_09B)
  QaChaosRegressionTests.cs                  03 RESCUE_01 | 06 (backup fallback) | 03 OUTNAME_01 (QAAUDIT_03)
⚠ MusicWizardResult.Plan.cs                  02 AUD-PREVIEWSYNC | 03 (mini-map only)
⚠ MusicWizardViewModel.cs                    04 UI-MUSICWIZARD | 02 AUD-MUSICWIN
⚠ VideoMergerWindow.MusicButton.cs           02 AUD-DIALOGS | 04 UI-MUSICWIZARD
  MusicCrossfade.cs                          02 AUD-MUSICFADE | XFADEPLAN_01
⚠ MusicPlaylist.cs                           02 AUD-MUSICWIN | 04 UI-MUSICWIZARD
⚠ MusicBedPlan.cs                            02 AUD-PREVIEWSYNC | 03 (mini-map only)
  MusicPlaylistTests.cs                      02 AUD-MUSICWIN
⚠ MusicBrowserTests.cs                     04 UI-MUSICWIZARD | 02 AUD-DIALOGS (MUSICBROWSE_01)
  MusicWizardResultPlanTests.cs              02 AUD-PREVIEWSYNC
  MusicAttributionDialogWindow.axaml.cs      04 UI-MERGER-MULTISELECT
  MusicAttributionHelper.cs                  02 AUD-DIALOGS | ATTRIBUTION_01
⚠ OutputTimeline.cs                          01 06
  OutputFileSize.cs                          03
  OutputFileNaming.cs                        03 FFM-OUTNAME
  OutputSizeEstimator.cs                     03
  MediaProber.cs                             03 FFM-LIBAVPROBE
  MediaMetadataProbe.cs                      03 FFM-LIBAVPROBE
  LibAvMediaBackend.cs                       03 FFM-LIBAVPROBE
  LibAvLibrary.cs                            03 FFM-LIBAVPROBE
  LibAvInterop.cs                            03 FFM-LIBAVPROBE
  LibAvMetadataProbeTests.cs                 03 FFM-LIBAVPROBE
  VideoFrameGrabber.cs                       03 FFM-LIBAVFRAME
  LibAvFrameDecoder.cs                       03 FFM-LIBAVFRAME
  LibAvFrameDecodeTests.cs                   03 FFM-LIBAVFRAME (FRAMECANCELISO_01)
  NativeFrameCallerTests.cs                  03 04
⚠ PhoneFrameMockup.axaml.cs                  01 04 (FINALLAYOUT_01 SetFinalLayout)
  ExportViewModel.cs                         03
  ProcessWorker.cs                           03 (SHARECOVER_01 cover applied last)
  ProgressiveLanes.cs                        04
  QualityLadder.cs                           03
  ProjectRecoveryService.cs                  05
  RecoveryManager.cs                         05
  RuntimeLog.cs                              05
  BoundedLogFile.cs                          05 SYS-LOGGING (LOGCAP_02, shared runtime/build/test byte ceiling)
  SettingsWindow.axaml.cs                    04
  SettingsWindow.AiTracking.cs               04
  SettingsWindow.Behavior.cs                 04 UI-SETTINGS-ABOUT (SETCHIPS_01, SETREVEAL_01, FILEBACKUP_01)
  SettingsWindow.Save.cs                     04 UI-SETTINGS-ABOUT; 05 SETTX_01
  FreezeSoundEffectTests.cs                  02 AUD-FREEZESFX
  Church_Bell.wav, Church_Bell_Mid.wav, Church_Bell_Late.wav, Cinematic_Dramatic_Slow-Down.wav      02 AUD-FREEZESFX (FREEZESFX_05, FREEZEBELL_01)
  VideoLoudnessCache.cs                      02 AUD-FREEZESFX (FREEZELEVEL_01)
  S44_S45_FreezeSoundAndMusicDipTests.cs     README GOV-AVGATE | 02 AUD-FREEZESFX
  SpeedEditorSmokeApp.cs                     README GOV-AVGATE | 02 AUD-PREVIEWSYNC | 04 UI-GPUPRESENT2 (PREVIEWSPEED_02, live zoom and play/pause)
  SettingsWindow.Help.cs                     04 UI-SETTINGS-ABOUT (SETHELP_01)
  SettingsMusicFolderTests.cs                04 UI-SETTINGS-ABOUT | 05 SYS-IPCLIFETIME
  SettingsWindow.Output.cs                   04 UI-SETTINGS-ABOUT | 03 FFM-OUTNAME
  SettingsWindow.Tools.cs                    04 UI-SETTINGS-ABOUT
⚠ SettingsViewModel.cs                       04 UI-SETTINGS-ABOUT | 05
⚠ SettingsViewModel.Services.cs              04 UI-SETTINGS-ABOUT | 08 (SETTINGSDI_01, AIKEYCANCEL_01, SETBACKUPIO_01)
⚠ SettingsWindowServices.cs                  04 08 COMP-ROOT (SETTINGSDI_01, SETBACKUPIO_01)
  SettingsTypes.cs                           05 (SETTINGSTYPES_01)
  ViralPostViewModel.cs                      04 08 (VIRALVM_01)
  SettingsAuditTests.cs                      04 UI-SETTINGS-ABOUT
  ArchitectureAuditFixTests.cs               05 08 04 (architecture audit 2026-11-11)
  SpinningWheelSlider.cs                     04 UI-SAFEGUARDS (DIALA11Y_01)
  TextOverlayGenerator.cs                    03
  TimelineKnob.cs                            01
  TimelineLanesControl.axaml.cs              01
  TimelineReorder.cs                         04
  UiStateStore.cs                            05
  UpdateToastWindow.axaml.cs                 04 UI-SETTINGS-ABOUT | 09 SYS-AUTOUPDATE
⚠ VideoMergerWindow.ClipActions.cs           01 04
⚠ VideoMergerWindow.EdlPreview.cs            01 02 03 04
⚠ VideoMergerWindow.Effects.cs               01 04
⚠ VideoMergerWindow.History.cs               01 06
⚠ VideoMergerWindow.Lanes.cs                 01 04
⚠ VideoMergerWindow.Session.cs               01 04 05
⚠ VideoMergerWindow.TimelineSelect.cs        01 04
  WindowResizeGrip.cs                        04
  DialogFit.cs                               04 UI-WINPLACE
  WindowPlacementTests.cs                    04 UI-WINPLACE
  VoiceOverLevel.cs                          02 AUD-VOICEOVER
⚠ VoiceOverGainSlider.cs                    02 AUD-VOICEOVER; 04 UI-PROMPTS
⚠ VoiceOverWindow.Volume.cs                 02 AUD-VOICEOVER; 04 UI-PROMPTS
  VoiceOverLevelTests.cs                     02 AUD-VOICEOVER
  VoiceOverGainUiTests.cs                    02 AUD-VOICEOVER; 04 UI-PROMPTS
  VoiceOverPreviewPlayer.cs                  02
  MusicBedPreviewPlayer.cs                   02 AUD-PREVIEWSYNC (PREVIEWLAYERS_01)
  EditorPreviewLayers.cs                     02 AUD-PREVIEWSYNC (PREVIEWLAYERS_01)
  BedPreviewFollower.cs                      02 AUD-PREVIEWSYNC (MUSICSYNC_03)
  AudioPlayerClock.cs                        02 AUD-PREVIEWSYNC (MUSICSYNC_03)
⚠ PreviewSpeedGovernor.cs                    02 AUD-PREVIEWSYNC (PREVIEWSPEED_01) | 01
⚠ PreviewTimeMap.cs                          02 AUD-PREVIEWSYNC (PREVIEWLAYERS_01) | 01
  S39_S43_EveryScreenPreviewsEveryLayerTests.cs  README GOV-AVGATE | 02 AUD-PREVIEWSYNC
  Screens.cs (AvRegression Infrastructure)   README GOV-AVGATE | 02 AUD-PREVIEWSYNC
⚠ VoiceOverWindow.axaml.cs                   01 02
⚠ VoiceOverWindow.RecordingState.cs          01 02 04 (VOREC_01..03)
  LiveTakeMeter.cs                           02 (VOLIVE_01)
  VoiceRecorder.cs                           02
  VoiceCaptureSession.cs                     02
⚠ VoiceOverRecoveryManager.cs                02 05
⚠ VoiceOverWindow.Apply.cs                   01 02 04 05
⚠ WindowBoundsHelper.cs                      04 05
⚠ WindowPlacement.cs                         04 UI-WINPLACE | 05 SYS-WINSTATE
  ZoomPreviewSimulator.cs                    03
  FvsBuild Program.cs                        05
⚠ ProjectDocument.cs                         06 07
  ProjectSerializer.cs                       06
  ProjectStore.cs                            06
  RecentProjects.cs                          06
  AotJson.cs                                 06
  UndoStack.cs                               07
  GranularEditHistory.cs                     07 UNDO-MIGRATION | 04 UI-GRANULAR
  CropLayoutSnapshot.cs                      07 UNDO-MIGRATION
  MusicWizardSnapshot.cs                     07 UNDO-MIGRATION
  CropToolWindow.History.cs                  07 04
⚠ GranularEditSession.cs                     01 04 07 08 COMP-MVVM | 07 UNDO-EDITSTATE (EDITSTATE_01)
  GranularEditSession.History.cs             07 UNDO-EDITSTATE | 07 UNDO-MIGRATION
⚠ GranularEditSession.Segments.cs            01 04 07
⚠ GranularEditSession.Memes.cs               01 04 07 (MEMEMODE_01, MEMESNAP_02/03, MEMESEP_02, MEMECUT_02, MEMEDRAG_02/03)
⚠ GranularSpeedEditorWindow.MemeBands.cs      01 03 04 (MEME_06..09, MEMEDRAG_02, MEMEDRAG_03)
⚠ MainWindow.Memes.cs                         01 02 03 04 (MEMEMAIN_02, MEMEMARK_02, MEMEMUSIC_03)
  MemeTransitions.cs                          03 FFM-MEMECONCAT | 03 FFM-MEMECORNER (MEMETRANS_01, MEMEANIM_01)
⚠ MemePlacementPreview.cs                     03 FFM-MEMECORNER | 04 UI-MEMESELECT (MEMEPICK_03, MEMEPICK_06, MEMEPOS_01)
⚠ MemePickerPrefsStore.cs                     04 UI-MEMESELECT | 05 (MEMEFAV_01, MEMELAST_01)
  GranularRecoveryCodec.cs                   05 (RECOVERY_03)
  GranularJsonRead.cs                        05 (GRANJSON_01)
⚠ CropEditSession.cs                         01 04 05 07 | 08 COMP-MVVM (EDITSTATE_01)
  CropLayer.cs                               04 07 UNDO-EDITSTATE
⚠ CropProfileCodec.cs                        01 05
  CropConfigJson.cs                          04 05 (CROPJSON_01)
  EditStateExtractionTests.cs                08 COMP-MVVM | 07 UNDO-EDITSTATE
  MusicWizardWindow.History.cs               07 04
  GranularSpeedEditorWindow.History.cs       07 04
⚠ ProjectSession.cs                          06 07 08
⚠ MainWindow.Project.cs                      06 07 08
⚠ ToolNavigator.cs                           05 08
  ToolLaunchContracts.cs                     08 COMP-ROOT | 05 SYS-RECOVERY (DONECLEAN_01, MERGESESSIONVIDEOS_01 InitialClips)
⚠ FfmpegJobLifetime.cs                       03 08
  AppServices.cs                             08
  CtsSlot.cs                                 08 (CTSSLOT_01)
  ShellLauncher.cs                           08 (SHELLLAUNCH_01)
  Fault.cs                                   08
  IFaultSink.cs                              08
  IClock.cs                                  08
⚠ IProjectStore.cs                           06 08
⚠ IUserNotifier.cs                           04 08
  IFilePickerService.cs                      08
⚠ StorageProviderFilePicker.cs               05 08
  UserFacingFaultSink.cs                     08
  UserFacingError.cs                         04 UI-SAFEGUARDS (ERRPLAIN_01, ERRPLAIN_02)
  UiAuditFixTests.cs                         04 (THEMETOKENS_01, THEMEPRECEDENCE_01, FONTTOKENS_01, CAPTIONEDIT_01, CAPTIONEDIT_02, ERRPLAIN_01, ERRPLAIN_02); 02 (VOFAST_01, VOCLOSE_01, VOCLOSE_02)
  ArchitectureRuleTests.cs                   08
⚠ CodeSigning.cs                             09 08
  SettingsManager.cs                         05 (SETTX_01/SETTX_02: all settings mutation via one Update transaction)
  HardwareTelemetrySampler.cs                04
  AuthenticodeVerifier.cs                    05
  FreeVideoStudio.App.csproj           06 (AOTSAFETY_01) | 09 SYS-SIGNING
  RuntimePayloadManifest.cs                  09
  DiagnosticReport.cs                        05
  SecretRedactor.cs                          05 SYS-DIAGREPORT
  DiagnosticBundle.cs                        05
  UndoSidecarStore.cs                        07
  Faults.cs                                  08
  SentinelList.cs                            05
  FvsVerify Program.cs                       05
  build/sentinels.txt                        05
⚠ .github/workflows/ci.yml                   08 09
  .github/workflows/lfs-guard.yml            09
⚠ UpdateService.cs                           05 09
  DevSandbox.ps1                             05 SYS-DEVBUILD
  DevRun.ps1                                 05 SYS-DEVBUILD
  DevelopmentEnvironment.cs                  05 SYS-DEVBUILD
  KnownFolders.cs                            05 SYS-DEVBUILD / SYS-WINSTATE
  NamedSystemMutex.cs                        05 SYS-MUTEX / SYS-DEVBUILD
  DevelopmentEnvironmentTests.cs             05 SYS-DEVBUILD
  DevelopmentLaunchTests.cs                  05 SYS-DEVBUILD
  DevHarness.Tests/DevSandbox.Tests.ps1       05 SYS-DEVBUILD
  UpgradeCoordinator.cs                      09 SYS-UPGRADE | SYS-UPGRADEUX | SYS-UPGRADE-TRUST
  ProtectedWorkerStage.cs                    09 SYS-UPGRADE-TRUST (WORKERSTAGE_01)
  CommentaryPersonality.cs                   01 TL-CAPTIONS | 04 UI-AICOMMENTARY (COMMENTARYMIX_01, TACTICAL_01..03)
  AiCommentaryOptionsViewModel.cs            04 UI-AICOMMENTARY (COMMENTARYUI_02) | 01 TL-CAPTIONS
  CommentaryPersonalityTests.cs              04 UI-AICOMMENTARY | 01 TL-CAPTIONS
  CaptionLookTests.cs                        01 TL-CAPTIONS (CAPTIONPLACE_01, CAPTIONLOOK_01, CAPTIONSIZE_01, CAPTIONRTL_01)
  UpgradeInstallWorker.cs                    09 SYS-UPGRADE | SYS-UPGRADEUX
  UpgradeChannel.cs                          09 SYS-UPGRADE | SYS-UPGRADEUX
  InstallDiscovery.cs                        09 SYS-UPGRADE | SYS-UPGRADEUX
  UpgradeProgress.cs                         09 SYS-UPGRADEUX
  UpgradeBrokerHost.cs                       09 SYS-UPGRADEUX
  UpgradeFinishedNotice.cs                   09 SYS-UPGRADEUX
  UpgradeProgressWindow.axaml(.cs)           09 SYS-UPGRADEUX
  UpgradeProgressViewModel.cs                09 SYS-UPGRADEUX
  UpdateFinishedViewModel.cs                 09 SYS-UPGRADEUX
  UpdateFinishedWindow.axaml(.cs)            04 UI-SETTINGS-ABOUT | 09 SYS-UPGRADEUX
⚠ UpdateToastViewModel.cs                    04 09 SYS-AUTOUPDATE (BGUPDATE_01)
⚠ UpdateService.Background.cs                05 09 SYS-AUTOUPDATE (BGUPDATE_01)
⚠ UpdatePreparer.cs                          05 09 DIST-SMART
⚠ UpdateDownloader.cs                        05 09 DLRESUME_01
  SmartUpdateManifest.cs                     09 DIST-SMART
  BinaryDelta.cs                             09 DIST-SMART (BINDELTA_01, BINDELTA_02)
  BrotliFile.cs                              09 DIST-SMART (ASSETHASH_01)
  SmartUpdateBuilder.cs                      09 DIST-SMART (DELTABASE_01, ASSETHASH_01)
  ReleaseChangelog.cs                        09 DIST-RELEASENOTES (RELNOTES_01..03)
  ReleaseGate.cs                             09 DIST-RELEASEGATE | 09 SYS-AUTOUPDATE (RELGATE_01)
  ReleaseChangelogTests.cs                   09 DIST-RELEASENOTES | DIST-RELEASEGATE | DIST-RETENTION
  ReleaseHardeningTests.cs                   09 SYS-UPGRADE-TRUST | SYS-AUTOUPDATE | 09 DIST-SMART
  PublisherRoot.cs                           09 SYS-SIGNING (UPDATEPIN_01, REVOKE_01)
  StartupCrashGuard.cs                       09 SYS-UPGRADE (CRASHLOOP_01)
  StartupCrashGuardTests.cs                  09 SYS-UPGRADE (CRASHLOOP_01)
  UpgradeSettingsPreservationTests.cs        09 SYS-UPGRADE (was CompactUpdateTests.cs)
  ReleaseIntegrityTests.cs                   09 DIST-RELEASEGATE | DIST-RETENTION | 09 SYS-SIGNING
  build/revoked-signers.txt                  09 SYS-SIGNING (REVOKE_01)
  CHANGELOG_ARCHIVE.md / changes_ARCHIVE.md  09 DIST-RETENTION (RELRECORD_01)
  .github/workflows/upgrade-smoke.yml        09 DIST-UPGRADESMOKE (UPGRADESMOKE_01)
  HardLink.cs                                09 SYS-UPGRADE (FASTINSTALL_01)
  SmartUpdateTests.cs                        09 DIST-SMART
  BinaryDeltaTests.cs                        09 DIST-SMART (BINDELTA_01, BINDELTA_02)
  VersionAndUpdaterTests.cs                  09 SYS-AUTOUPDATE | SYS-UPGRADEUX
  UpgradeProgressReportingTests.cs           09 SYS-UPGRADEUX
  Staging.cs                                 09
  build/starter_meme/                        03 FFM-MEMELIB (STARTER_01, STARTERLIST_01) | 09
  CHANGELOG.md                               09 DIST-RELEASENOTES (user notes, `## Upcoming`, latest 5 releases)
  GitHubReleasePublisher.cs                  09 DIST-SPLIT | DIST-RELEASEGATE | DIST-RETENTION
  InnoInstaller.cs                           09 DIST-INSTALLER (NODROPPER_01)
  FreeVideoStudio.iss (build/installer)      09 DIST-INSTALLER (NODROPPER_01)
⚠ PeIdentity.cs                              05 09 SYS-SIGNING (PEIDENTITY_01)
  PeIdentityTests.cs                         09 SYS-SIGNING (PEIDENTITY_01)
  DeploymentFootprint.cs                     05 SYS-AVCLEAN (NOTEMPEXEC_01)
⚠ GranularSpeedEditorWindow.History.cs       04 07
⚠ MainWindow.Controls.cs                     01 02 04 GOV
  GranularSpeedEditorWindow.Controls.cs      04
  MusicWizardWindow.Controls.cs              04
  CropToolWindow.Controls.cs                 04
  VideoMergerWindow.Controls.cs              03
  PhaseOverlayControl.Controls.cs            04
  TimelineLanesControl.Controls.cs           04
  ConfirmDialogWindow.Controls.cs            04
  WindowsOnlyFactAttribute.cs                08
  TimelineViewModel.cs                       01
⚠ MergedTimeline.cs                          01 03
⚠ MusicPadAlignment.cs                       01 02 03
⚠ VideoMergerWindow.Timeline.cs              01 03
  EncoderManager.cs                          03
  TwoPassEncoding.cs                         03
  ExportColorPolicy.cs                       03
  IntroTag.cs                                03
  MergeClipAnalyzer.cs                       03
  GrabCursors.cs                             04
  VideoMergerWindow.Playhead.cs              04
⚠ LaneDiskCache.cs                           04 05
  WaveformPeaks.cs                           04
  PcmStream.cs                               04
  MusicEnergyAnalysis.cs                     02
  WaveformEnvelope.cs                        04
  WaveformLanePresenter.cs                   04
  WaveformPeakSource.cs                      04
  FilmLanePresenter.cs                       04
  FilmStripSource.cs                         04
  CoreLogger.cs                              05
  CropConfigStore.cs                         05
⚠ MainWindow.Recovery.cs                     05 07
  FaultCounters.cs                           08
  .gitattributes                             09
  CooperativeShutdownGate.cs                 05
  CrashLogDigest.cs                          05 SYS-AVCLEAN (NOHIDDENTOOLS_01)
  LegacyScheduledTask.cs                     05 SYS-AVCLEAN (NOHIDDENTOOLS_01)
  ProcessImage.cs                            05 SYS-AVCLEAN (NOMEMREAD_01)
  SignPathTrustTests.cs                      09 SYS-SIGNING (SIGNPATH_01)
  ControlledFolderAccess.cs                  09 SYS-CFASAFE (CFASAFE_01)
  ProtectedFolderGate.cs                     09 SYS-CFASAFE (CFASAFE_01)
  AvFriendlyBehaviourTests.cs                05 SYS-AVCLEAN | SYS-CFASAFE
  CropConfigDefaults.cs                      05
  DiskSpaceGuard.cs                          05
  HardwareCapability.cs                      03
  HudAutoDetector.cs                         01
  HudConfig.cs                               01
  HudImageOps.cs                             01
  AiHudDetection.cs                          01
  AiHudResponseParser.cs                     01
  AiHudResultCache.cs                        01
  HudCandidateFusion.cs                      01
⚠ HudDetectionCoordinator.cs                 01 08
⚠ GeminiHudDetectionService.cs               01 05 08
  AiHudFrameBuilder.cs                       01
⚠ CropToolWindow.MagicWand.cs                04 01 08
  MainViewModel.cs                           04 08
  MemeAssets.cs                              03
  ZoomBoxSnap.cs                             01 TL-ZOOM (ZOOMSNAP_01)
  PreviewKeySeek.cs                          04 UI-GRANULAR (ARROWSTEP_01)
  FinalLayoutPreview.cs                      04 UI-GRANULAR (FINALLAYOUT_01)
  ShareCover.cs                              03 FFM-SCRAPER (SHARECOVER_01)
  ShareCoverTests.cs                         03 FFM-SCRAPER (SHARECOVER_01)
  MainWindow.FinalLayout.cs                  04 UI-GRANULAR (FINALLAYOUT_01)
  OwnerRequestBatchTests.cs                  01 TL-ZOOM | 04 UI-AIZOOM / UI-GRANULAR | 05 SYS-RECOVERY | 02 AUD-MUSICWIN
  MemeCatalog.cs                             03
  MemePickerWindow.axaml                     04
  MemePickerWindow.axaml.cs                  04 UI-MEMEFLOW
  MemePartRange.cs                           04 UI-MEMEFLOW (MEMEPART_02)
⚠ MemePickerContext.cs                       03 FFM-MEMEPREVIEW | 04 UI-MEMEFLOW
⚠ MemePickerWindow.Preview.cs                 03 FFM-MEMEPREVIEW | 04 UI-MEMEFLOW | 05 SYS-STORAGE-TABLE
⚠ VideoMergerWindow.MemePreview.cs            01 TL-COMPOSITE | 02 AUD-PREVIEWSYNC | 03 FFM-MEMEPREVIEW | 04 UI-MEMEFLOW
  MemeWorkflowTests.cs                       04 UI-MEMEFLOW
⚠ MemeResultRenderTests.cs                   04 UI-MEMEFLOW | 03 FFM-MEMEPREVIEW
  MemeMusicPreview.cs                        02 AUD-MUSICFADE (MEMEMUSIC_03)
  MemeThumbnailCache.cs                      04
  OwnedLruCache.cs                           04
  ViewportThumbnailLoader.cs                 04
  NamedPipeStateClient.cs                    05
  PipeAccess.cs                              05
  PreviewDetachController.cs                 04
  PreviewMonitorWindow.axaml                 04
  SingleInstanceGuard.cs                     05
  StateTransferStore.cs                      05
  SessionStateWriter.cs                      05
  ViewModelBase.cs                           04
  WavAudioReader.cs                          02
  WaveformGenerator.cs                       02
  App/Program.cs                             08 COMP-ROOT | 09 SYS-UPGRADE (command dispatch: run-ui, --install/--install-worker/--uninstall/--cleanup-worker/--crop-tool/--merger; diagnostic verbs paths, read-state/write-state/clear-state, read-crops/write-crops-defaults, phase1-gate .. phase4-gate)
  Phase1Gate.cs .. Phase4Gate.cs             08 COMP-ROOT (console self-test verbs: IPC workers, crash/recovery, MPVSafetyManager scrubbing, FFmpeg math)
  LibMpvInterop.cs                           04 UI-GPUPRESENT2 | README invariant 6 (libmpv render API P/Invoke)
  MpvCommandQueue.cs                         05 SYS-IPCLIFETIME (MpvCommandStatus: Accepted/Rejected/Superseded/Unavailable/Disposed/QueueFull/TimedOut)
  FfmpegErrorClassifier.cs                   03 | 08 COMP-FAULTS (Classify / ClassifyException -> ExportFailure)
  FilterGraphBuilder.cs                      03 FFM-ZOOMGRAPH (CasFilterNode, invariant 4) | 02 AUD-MASTERING (SidechainCompressNode.Duck)
  GracefulProcessTerminator.cs               05 SYS-IPCLIFETIME | 08 COMP-CI (shutdown ladder; tests need real Windows processes)
  App/Infrastructure/BinaryPathProbe.cs      03 FFM-BINPATH; BINPATH_01 (Crop Tool / Voice Over search orders, unresolved divergence)
  FvsBuild/Cli.cs                            09 DIST-RELEASEGATE
  Core/Media/RescuedOutputPath.cs            03 FFM-OUTNAME (RESCUE_01)
  App/Services/EditorRecoveryWriter.cs       05 SYS-RECOVERY; GRANULARPERF_01
  App/OpenWithLaunch.cs                      08 COMP-ROOT | 05 SYS-AVCLEAN (Open With launch argument; registration is OPENWITHHOME_01)
  App/Infrastructure/OutputFolderResolver.cs 04 UI-SETTINGS-ABOUT (SETFOLDERONE_01 save folder; ISSUE_04 order: saved folder -> real Downloads -> ask once)
  App/MusicWizardWindow.Merger.cs            02 AUD-MUSICWIN (WIZMERGEPREVIEW_01)
  Core/Infrastructure/BinaryPathResolver.cs  03 FFM-BINPATH
⚠ Core/Media/ExportVideoPipeline.cs          03 FFM-HWENC (resident CUDA route, CUDATAG_01) | 02 AUD-FREEZESFX (audio filter table)
  Core/Infrastructure/UserDataUpgrade.cs     09 SYS-UPGRADE
  Core/Infrastructure/UpgradeFiles.cs        09 SYS-UPGRADE
⚠ Core/Infrastructure/ChildProcessTracker.cs 03 FFM-EXPORTLIFETIME | 04 UI-GRANOPEN
⚠ Core/Media/AsyncProcessRunner.cs           03 FFM-LIBAVPROBE | 04 UI-GRANOPEN
  Core/Media/VideoConfig.cs                  03 FFM-QUALITY
  App/Infrastructure/DebouncedStateWriter.cs 05 SYS-IPCLIFETIME
  App/Infrastructure/LiveMaskCache.cs        05 SYS-EDITHOT
  App/ShellLinkWriter.cs                     05 SYS-AVCLEAN
  App/Infrastructure/ThemeManager.cs         04 UI-THEME
```

Full paths: see each spec's Code Mini-Map.

---

## 2. Symbol / Constant / Tag -> Spec Section

```
ZOOMSNAP_01                                  01 TL-ZOOM
AIPICKFOCUS_01                               04 UI-AIZOOM
NODRAGCREATE_01                              04 UI-GRANULAR
ARROWSTEP_01                                 04 UI-GRANULAR
MERGESESSIONVIDEOS_01                        05 SYS-RECOVERY
DONECLEAN_02                                 05 SYS-RECOVERY | 04 UI-SAFEGUARDS
WIZMERGEPREVIEW_01                           02 AUD-MUSICWIN
FINALLAYOUT_01                               04 UI-GRANULAR | 03 (HudCanvasLayers)
SHARECOVER_01                                03 FFM-SCRAPER
MEMEFLOW_01                                 04 UI-MEMEFLOW | 06 PROJ-SCHEMA | 07 UNDO-MIGRATION
MEMEMODEPREF_01                             04 UI-MEMEFLOW | 05 SYS-STORAGE-TABLE
MEMEPART_02                                 04 UI-MEMEFLOW | 03 FFM-MEMECORNER
MEMELANDING_01                              01 TL-MEME | 04 UI-MEMEFLOW | 07 UNDO-MIGRATION
MEMERESULT_01                               03 FFM-MEMEPREVIEW | 04 UI-MEMEFLOW | 05 SYS-STORAGE-TABLE | 02 AUD-PREVIEWMIX
MEMEACTIONS_01                              04 UI-MEMEFLOW
MemePartRange                               04 UI-MEMEFLOW
MemePickerContext                           04 UI-MEMEFLOW | 03 FFM-MEMEPREVIEW
MemeLanding                                 04 UI-MEMEFLOW
MemePlacementPoint                          04 UI-MEMEFLOW
_isSafeToClose                               05 SYS-WINSTATE
SIZEESTIMATE_01                              03 FFM-SIZEESTIMATE | 04 UI-THEME | 05 SYS-SIZEESTIMATE
MERGEQUALITY_01                              03 FFM-SIZEESTIMATE
MERGESIZE_01                                 03 FFM-SIZEESTIMATE
MEMELEVEL_01                                 03 FFM-SCRAPER
CLIPFRAMES_01                                03 FFM-SCRAPER | 02 AUD-CONCAT
MemeLoudness                                 02 AUD-MASTERING | 03 FFM-SCRAPER
CANCELREG_01                                 03 FFM-EXPORTLIFETIME
EXPORTSESSION_01                             03 FFM-EXPORTLIFETIME
EXPORTSESSION_02                             03 FFM-EXPORTLIFETIME
FLUSHCEILING_01                              05 SYS-IPCLIFETIME
FlushMaxWaitMs                               05 SYS-IPCLIFETIME
GPUPRESENT_01                                04 UI-GPUSLOT
GPUSLOT_01                                   04 UI-GPUSLOT
GRANPROBE_01                                 04 UI-GRANOPEN
ImportedImageSlot                            04 UI-GPUSLOT
IPCLEASE_01                                  05 SYS-IPCLIFETIME
IPCTEARDOWN_01                               05 SYS-IPCLIFETIME
IPCOWNER_01                                  05 SYS-IPCLIFETIME | 05 SYS-WRITEORDER
DeliveryBudget                               05 SYS-IPCLIFETIME
CommitAsync                                  05 SYS-IPCLIFETIME (StateTransferStore)
ServerMutexName                              05 SYS-IPCLIFETIME | 05 SYS-MUTEX
OUTPATH_01                                   03 FFM-EXPORTLIFETIME
OUTNAME_01                                   03 FFM-OUTNAME | 04 UI-SETTINGS-ABOUT
MainOutputBaseName                           03 FFM-OUTNAME
MergerBaseFor                                03 FFM-OUTNAME  [SETNAMEONE_01; MergerOutputBaseName removed]
AiZoomTuning                                 04 UI-SETTINGS-ABOUT | 10 SYS-AISETTINGS
PIPEDRAIN_01                                 03 FFM-EXPORTLIFETIME
PROCGATE_01                                  03 FFM-EXPORTLIFETIME
PROCGATE_02                                  03 FFM-EXPORTLIFETIME
ProcessVideoCoreAsync                        03 FFM-EXPORTLIFETIME
SETTINGSATOMIC_01                            05 SYS-ATOMICWRITE
TryRetireSlot                                04 UI-GPUSLOT
WORKERLIFETIME_01                            03 FFM-EXPORTLIFETIME
WORKERLIFETIME_02                            03 FFM-EXPORTLIFETIME
ExportCoordinator                            03 FFM-EXPORTLIFETIME
_presentGates                                04 UI-GPUSLOT
_procGate                                    03 FFM-EXPORTLIFETIME
_lastFreezeTriggerMs                         01 TL-FREEZE
_mainEndParkIssued                           01 TL-ENDSTOP
_previewParkedAtEnd                          01 TL-ENDSTOP
_recalculatingTrackColumns                   04 UI-DPI
_timeline                                    01 (mini-map only)
AiSetupWizardWindow                          04 UI-AIZOOM | 04 UI-AIEASE (AISETUP_02) | 05 SYS-WINSTATE
AiSmartZoomBtnCtl                            04 UI-AIZOOM
AiSubjectPickerWindow                        04 UI-AIZOOM
AiTrackingPreviewBarCtl                      04 UI-AIZOOM
AiTrajectorySmoother                         01 TL-AITRACKING | 03 FFM-ZOOMGRAPH
AmbientBubblesBackground                     04 (mini-map only)
AppContext.BaseDirectory                     03 FFM-BINPATH
AppDangerBrush                               04 UI-THEME
ApplicationPaths                             05 (mini-map only)
ApplyFirstRunDisplayFit                      05 SYS-WINSTATE
ApplyProfile                                 05 (mini-map only)
ApplyTrackFilterAndSort                      02 AUD-DIALOGS
AppPrimaryButtonGradient                     04 UI-THEME
AppTableHairlineBrush                        04 UI-THEME
AppZoomBrush                                 01 TL-ZOOM | 04 UI-THEME
AskEditOrRemoveAsync                         02 AUD-DIALOGS | 04 (mini-map only)
ATOMICTEXT_01                                05 SYS-ATOMICWRITE
AtomicJsonFile                               05 SYS-RECOVERY
AtomicJsonFile.WriteText                     05 SYS-ATOMICWRITE
AtomicJsonFile.WriteBytes                    05 SYS-ATOMICWRITE
ATOMICBYTES_01                               05 SYS-ATOMICWRITE
AudioFilterChain                             02 AUD-MUSICFADE
AudioLoudnessProbe                           02 AUD-MASTERING
BeginMoveDrag                                04 UI-BORDERLESS
BillableSeconds                              03 FFM-QUALITY
BlockingCollection<string>                   05 SYS-LOGGING
BubbleCount                                  04 UI-BORDERLESS  [= 35]
Build                                        01 TL-PORTRAIT | 03 (mini-map only) | 09 SYS-SIGNING
BuildAtempoChain                             03 FFM-TEMPO
AudioTempoFilterBuilder                      03 FFM-TEMPO
AudioTempoEngine                             03 FFM-TEMPO
TEMPO_01                                     03 FFM-TEMPO
AVSYNC_02                                    03 FFM-TEMPO
AVSYNC_03                                    02 AUD-MASTERING
AVSYNC_04                                    03 FFM-TEMPO
CUDATAG_01                                   03 FFM-HWENC
MERGESCRIPT_01                               03 FFM-HWENC
MEMEMODE_01                                  01 TL-MEME | 03 FFM-MEMECORNER | 04 UI-MEMESELECT | 06 PROJ-SCHEMA | 07 UNDO-MIGRATION
AICOMMENTARY_CLOSE_01                        04 UI-AICOMMENTARY
AICOMMENTARY_LOG_01                          04 UI-AICOMMENTARY
MEMEAUDIT_01                                 01 TL-MEME | 03 FFM-MEMECONCAT / FFM-MEMECORNER / FFM-MEMEPREVIEW | 04 UI-MEMESELECT | 06 PROJ-SCHEMA (schema 7)
MEMETRIM_01                                  03 FFM-MEMECONCAT | 04 UI-MEMESELECT
MEMETRIM_02                                  01 TL-MEME
MEMERULER_01                                 01 TL-MEME
MEMEMAIN_02                                  01 TL-MEME
MEMEMARK_02                                  01 TL-MEME | 04 UI-MEMESELECT
MEMEMUSIC_02                                 02 AUD-MUSICFADE | 04 UI-MEMESELECT (retired)
MEMEMUSIC_03                                 02 AUD-MUSICFADE | 04 UI-MEMESELECT
MEMEGALLERY_01                               04 UI-MEMESELECT
MEMEGET_01                                   04 UI-MEMESELECT
MEMERECOVER_01                               05 SYS-RECOVERY
MEMECUT_02                                   01 TL-MEME
MEMESNAP_02                                  01 TL-MEME
MEMESNAP_03                                  01 TL-MEME
MEMESEP_02                                   01 TL-MEME
MEMEDRAG_02                                  01 TL-MEME
MEMEDRAG_03                                  01 TL-MEME | 04 UI-MEMESELECT
MEMESTRIP_02                                 01 TL-MEME
CORNERPARITY_02                              01 TL-MEME | 03 FFM-MEMECORNER
CORNERPARITY_03                              03 FFM-MEMECORNER
MERGEMEME_02                                 01 TL-MEME
MEMEPREV_02                                  03 FFM-MEMEPREVIEW
MEMELEVEL_03                                 03 FFM-MEMEPREVIEW
MEMESOUND_02                                 03 FFM-MEMEPREVIEW | 04 UI-MEMESELECT
MEMEEND_01                                   03 FFM-MEMECORNER
MEMEPOS_01                                   03 FFM-MEMECORNER | 04 UI-MEMESELECT
MEMEANIM_01                                  03 FFM-MEMECORNER | 04 UI-MEMESELECT
MEMEDUCK_01                                  03 FFM-MEMECORNER | 02 AUD-MUSICFADE
MEMETRANS_01                                 03 FFM-MEMECONCAT | 04 UI-MEMESELECT
MEMEPICK_03                                  04 UI-MEMESELECT
MEMEPICK_04                                  04 UI-MEMESELECT
MEMEPICK_05                                  04 UI-MEMESELECT
MEMEPICK_06                                  04 UI-MEMESELECT
MEMEPICK_07                                  04 UI-MEMESELECT
MEMEFAV_01                                   04 UI-MEMESELECT
MEMELAST_01                                  04 UI-MEMESELECT
MEMEWARN_02                                  04 UI-MEMESELECT
MEMELIST_01                                  04 UI-MEMESELECT
MEMEKEYS_01                                  04 UI-MEMESELECT
MemePresentationMode                         01 TL-MEME
MemeOverlayCorner                            01 TL-MEME  [default BottomRight]
MemeOverlaySize                              01 TL-MEME  [default Medium]
MemeOverlayLayout                            03 FFM-MEMECORNER
VisibleInterval                              01 TL-MEME
OutputDurationSec                            01 TL-MEME
CornerMemeOverlayGraph                       03 FFM-MEMECORNER
CornerOverlays                               01 TL-MEME  [MergerPreviewPlan]
GameplaySecAt                                01 TL-MEME  [MergerPreviewPlan]
MemeChoiceViewModel                          04 UI-MEMESELECT
CornerMemeOverlayPresenter                   04 UI-MEMESELECT
MemePresentationJson                         06 PROJ-SCHEMA
RubberbandOptions                            03 FFM-TEMPO  [= transients=mixed:window=short]
EnsureProbedAsync                            03 FFM-TEMPO
HelpListsRequiredOptions                     03 FFM-TEMPO
CalculateEffectiveDurationMs                 03 FFM-QUALITY
CanvasMath                                   01 (mini-map only)
CheckFault                                   05 SYS-RECOVERY
CHANGELOG_01                                 GOV §6 | build/sentinels.txt
ChunkSpec                                    03 (mini-map only)
ClampZoomInsideItsBlock                      01 (mini-map only)
ClearLiveZoomCrop                            04 UI-GRANULAR   [code-only: counterpart of UpdateLiveZoomCrop]
CoachOverlay                                 04 UI-COACH
CoachTours                                   04 UI-COACH
Compute                                      03 (mini-map only)
ConfirmDialogWindow                          02 AUD-DIALOGS | 04 (mini-map only)
CoordinateMath                               01 TL-PORTRAIT
CoreLogger                                   05 (mini-map only)
Create                                       01 (mini-map only)
Cut                                          01 TL-OUTPUTTIMELINE, 01 TL-CUTS | 02 AUD-MASTERING, 02 AUD-VOICEOVER | 04 UI-SAFEGUARDS
CUTS_02                                      02 AUD-VOICEOVER
DebounceMs                                   03  [= 450]   [code-only: FilmstripPrewarm.cs]
DefaultValues.QualityIndex                   03 FFM-QUALITY
DeploymentLifecycle                          05 SYS-MUTEX
DOUBLEFIRE_01                                04 UI-SAFEGUARDS
EnableGlobalRipple                           04 UI-THEME
EndThumbnailMarkerDrag                       01 TL-HITBOX
EndUndoGesture                               01 (mini-map only)
EnsureDefaults                               05 (mini-map only)
EnsureStep2WaveformPresent                   02 AUD-DIALOGS
EnsureWritableDirectories                    05 (mini-map only)
ExportPayload                                03   [code-only: ExportPayload.cs]
FFM-QUALITY                                  03 FFM-QUALITY
FfmpegDiagnosticCollector                    03 (mini-map only)
File.Move                                    05 SYS-RECOVERY
FileOptions.WriteThrough                     05 SYS-RECOVERY
FIRSTFIT_01                                  05 SYS-WINSTATE
FirstRunAspect                               05 SYS-WINSTATE
FirstRunCoverage                             05 SYS-WINSTATE
fitDisplayOnFirstRun                         05 SYS-WINSTATE
FloatingNotice                               04 UI-COACH
FluidVolumeSlider                            02 (mini-map only) | 04 (mini-map only)
FormatDiagnosticReport                       03   [code-only: ExportFailure.cs]
FREEZE_01                                    01 TL-FREEZE
FreezeSecondCostFactor                       03 FFM-QUALITY
FVS_DEV_LOG_DIR                              05 SYS-DEVBUILD
FVS_SIGN_PASS                                09 SYS-SIGNING
FVS_SIGN_PFX                                 09 SYS-SIGNING
GeneratePng                                  03 (mini-map only)
GeminiTrackingService                        01 TL-AITRACKING | 03 FFM-ZOOMGRAPH | 04 UI-AIZOOM | 10 SYS-AISETTINGS
GetQualitySettings                           03 FFM-QUALITY
GlobalMasterVolume                           02 AUD-MASTERVOL
GpuCapabilityProbe                           03 FFM-HWENC
GranularSpeedBuilder                         03 (mini-map only) | 05 SYS-WINSTATE
GranularSpeedEditorWindow                    01 (mini-map only) | 04 (mini-map only) | 05 (mini-map only)
GRIP_01                                      04 UI-RESIZEGRIP
HardwareScanner                              03 FFM-HWENC
HasUnsavedWork                               05 (mini-map only)
HudAutoDetector                              01 TL-HUDSTREAMING
HudDetectionCoordinator                      01 TL-AIHUD | 08 COMP-FAULTS
HudCandidateFusion                           01 TL-AIHUD
AiHudResponseParser                          01 TL-AIHUD
AiHudGeometry                                01 TL-AIHUD
IAiHudDetectionService                       01 TL-AIHUD
GeminiHudDetectionService                    01 TL-AIHUD | 10 SYS-AISETTINGS
AiHudFrameBuilder                            01 TL-AIHUD
AiHudResultCache                             01 TL-AIHUD
AiMagicWandCloudConsent                      10 SYS-AISETTINGS | 04 UI-AIHUDWAND
AIHUD_01                                     01 TL-AIHUD | 04 UI-AIHUDWAND
AIHUD_02                                     01 TL-AIHUD
AIHUD_03                                     01 TL-AIHUD
AIHUD_04                                     01 TL-AIHUD
AIHUD_05                                     04 UI-AIHUDWAND | 10 SYS-AISETTINGS
AIGAME_01                                    01 TL-AITRACKING | 10 SYS-AISETTINGS
AIEXCLUDE_02                                 01 TL-AITRACKING
AICHUNK_01                                   01 TL-AITRACKING | 04 UI-AIZOOM
AIFPS_01                                     01 TL-AITRACKING
AITIME_01                                    01 TL-AITRACKING
AIRESMOOTH_01                                01 TL-AITRACKING | 04 UI-AIZOOM
AIHUDZONE_01                                 01 TL-AITRACKING
AILOOKAHEAD_01                               01 TL-AITRACKING | 04 UI-AIZOOM
AIMERGE_01                                   01 TL-AITRACKING | 04 UI-AIZOOM
AIOWNBOX_01                                  04 UI-AIZOOM
AIZOOMCONSENT_01                             04 UI-AIZOOM | 10 SYS-AISETTINGS
AIUNDO_01                                    07 UNDO-GRANULAR-APPLY | 04 UI-AIZOOM
AIMODELFALLBACK_01                           10 SYS-AISETTINGS
GEMINICLIENT_01                              10 SYS-AISETTINGS | 04 UI-AIZOOM
AIPROVIDER_01                                10 SYS-AISETTINGS
CLAUDEPROVIDER_01                            10 SYS-AISETTINGS
OPENAIPROVIDER_01                            10 SYS-AISETTINGS
AIFRAMESEQ_01                                 10 SYS-AISETTINGS
AISETTINGS_01                                 10 SYS-AISETTINGS
AIPROVIDER_UI_01                             04 UI-AIZOOM | 10 SYS-AISETTINGS
AIPROVIDER_ROUTING_01                        01 TL-AITRACKING | 04 UI-AIZOOM | 10 SYS-AISETTINGS
AICAPTION_MODEL_01                           01 TL-CAPTIONS
CaptionLine                                  01 TL-CAPTIONS
CaptionTrack                                 01 TL-CAPTIONS
CaptionTimeline                              01 TL-CAPTIONS
AICAPTION_LAYOUT_01                          01 TL-CAPTIONS
CaptionLayoutEngine                          01 TL-CAPTIONS
AICAPTION_ASS_01                             01 TL-CAPTIONS
CaptionAssWriter                             01 TL-CAPTIONS
AICAPTION_EXPORT_01                          03 FFM-CAPTIONS
AICOMMENTARY_SERVICE_01                      01 TL-CAPTIONS | 10 SYS-AISETTINGS
AICOMMENTARY_UI_01                           04 UI-AICOMMENTARY
AICAPTION_LANE_01                            04 UI-AICOMMENTARY | 01 TL-CAPTIONS
CAPTIONS_01                                  06 PROJ-SCHEMA | 01 TL-CAPTIONS
CaptionTrackJson                             06 PROJ-SCHEMA
EditCaptionDialogWindow                      04 UI-AICOMMENTARY
CommentaryService                            01 TL-CAPTIONS | 10 SYS-AISETTINGS
IAiVisionProvider                            10 SYS-AISETTINGS
GeminiProvider                               10 SYS-AISETTINGS
ClaudeProvider                               10 SYS-AISETTINGS
OpenAiProvider                               10 SYS-AISETTINGS
AiProviderException                          10 SYS-AISETTINGS
AiFrameSequenceExtractor                     10 SYS-AISETTINGS
AiUsageConfig                                10 SYS-AISETTINGS
KEYPROTECT_01                                10 SYS-AISETTINGS
SECRETSCAN_01                                05 SYS-SETTINGSSCHEMA
SANITIZE_01                                  05 SYS-SETTINGSSCHEMA | 09 DIST-RELEASEGATE   (developer_tools\Sanitize.cmd; release refuses a leak)
WANDPROGRESSIVE_01                           01 TL-AIHUD | 04 UI-AIHUDWAND
WANDCOLOR_01                                 04 UI-AIHUDWAND
AiGameProfile                                01 TL-AITRACKING
AiHudAvoidZones                              01 TL-AITRACKING
AiTrackingPrompts                            01 TL-AITRACKING
AiTrackingGameProfile                        10 SYS-AISETTINGS
AiZoomCloudConsent                           10 SYS-AISETTINGS | 04 UI-AIZOOM
GeminiClient                                 10 SYS-AISETTINGS
GeminiException                              10 SYS-AISETTINGS | 04 UI-AIZOOM
GeminiModelSession                           10 SYS-AISETTINGS
SecretProtector                              10 SYS-AISETTINGS
HudLocalPreview                              01 TL-AIHUD
InsertionAt                                  01 (mini-map only)
IsActive                                     02 AUD-VOICEOVER | 03 FFM-MEMEPREVIEW
IsEof                                        02 AUD-IPCSTATE
IsMainPlayerCharacter                        01 TL-AITRACKING | 04 UI-AIZOOM
IsMainPreviewAtEnd                           01 TL-ENDSTOP
IsSafeModeActive                             05 (mini-map only)
KineticScrubController                       01 (mini-map only)
LAYOUT_03                                    02 AUD-DIALOGS
LIST_05                                      02 AUD-DIALOGS
LIST_06                                      04 UI-DPI
LIST_07                                      02 AUD-DIALOGS
LoadState                                    05 (mini-map only)
LogMutexName                                 05 (mini-map only)
MAINEND_01                                   01 TL-ENDSTOP
MainTimelineEndSeconds                       01 TL-ENDSTOP
MainWindow                                   01 TL-OUTPUTTIMELINE | 02 (mini-map only) | 04 (mini-map only) | 05 SYS-WINSTATE
MaskOverlayManager                           05 (mini-map only)
MaxUndoDepth                                 RETIRED by UNDO_26 -> GranularEditHistory.MaxDepth [= 40] (07 UNDO-MIGRATION)
MEME_06                                      01 TL-MEME
MEME_07                                      02 AUD-VOICEOVER | 03 FFM-MEMEPREVIEW
MemePreviewDirector                          02 AUD-VOICEOVER | 03 FFM-MEMEPREVIEW
MemeRebuildOverlay                           03 FFM-MEMEPREVIEW
MemeSwapOverlay                              03 FFM-MEMEPREVIEW
MergerWorker                                 03 (mini-map only)
MicLevelMonitor                              02 AUD-VOICEOVER
MicrophoneSpectrumAnalyzer                   02 AUD-VOICEOVER
MicrophoneSpectrumSnapshot                   02 AUD-VOICEOVER
MicrophoneSpectrumControl                    02 AUD-VOICEOVER | 04 (mini-map only)
SpectrumMeterBallistics                      02 AUD-VOICEOVER
SpectrumBand                                 02 AUD-VOICEOVER
PcmCaptureFormat                             02 AUD-VOICEOVER
PcmBuffer                                    02 AUD-VOICEOVER
PcmAvailable                                 02 AUD-VOICEOVER
LatestSpectrum                               02 AUD-VOICEOVER
UpdateSpectrumMeter                          02 AUD-VOICEOVER
AppMeterNormalBrush                          02 AUD-VOICEOVER | 04 (mini-map only)
SPECTRUM_01                                  02 AUD-VOICEOVER
SPECTRUM_02                                  02 AUD-VOICEOVER
SPECTRUM_03                                  02 AUD-VOICEOVER | 04 (mini-map only)
SPECTRUM_04                                  02 AUD-VOICEOVER
SPECTRUM_05                                  02 AUD-VOICEOVER
IsSpectrumSourceEligibleLocked               02 AUD-VOICEOVER
SpectrumAdmittedForTesting                   02 AUD-VOICEOVER
VOREC_01                                     02 AUD-VOICEOVER | 04 UI-THEME
VOREC_02                                     01 TL-OUTPUTTIMELINE | 02 AUD-VOICEOVER
VOREC_03                                     02 AUD-VOICEOVER
VOLIVE_01                                    02 AUD-VOICEOVER
RefreshRecordingIndicator                    02 AUD-VOICEOVER
ResolveRecordingIndicator                    02 AUD-VOICEOVER
RecordingPhase                               02 AUD-VOICEOVER
RecordingPulseLimit                          02 AUD-VOICEOVER | 04 UI-THEME
SourceEndForCapturedAudio                    01 TL-OUTPUTTIMELINE
BuildStudioTimeline                          01 TL-OUTPUTTIMELINE
LiveRecordingCanvas                          02 AUD-VOICEOVER | 04 UI-THEME
LiveTakeMeter                                02 AUD-VOICEOVER
LiveTakeSnapshot                             02 AUD-VOICEOVER
LiveTake                                     02 AUD-VOICEOVER
MinHeight                                    04 UI-DPI | 05 SYS-WINSTATE
MinWidth                                     04 UI-DPI | 05 SYS-WINSTATE
MixFader                                     04 UI-THEME
MobileFilterBuilder                          01 TL-PORTRAIT | 03 (mini-map only)
MPVEOF_01                                    02 AUD-IPCSTATE
SEEKSETTLE_01                                02 AUD-IPCSTATE
IsSeeking                                   02 AUD-IPCSTATE
MPVSHUTDOWN_01                               05 SYS-WINSTATE
MpvIpcClient                                 02 AUD-MASTERVOL | 03 FFM-BINPATH
MusicVolSlider                               02 AUD-DIALOGS
MusicWizardWindow                            01 (mini-map only) | 02 (mini-map only)
MusicAttributionHelper                       02 AUD-DIALOGS | ATTRIBUTION_01
MusicAttributionDialogWindow                 04 UI-MERGER-MULTISELECT | ATTRIBUTION_01
NormalizeCuts                                01 (mini-map only)
Notify                                       08 (mini-map only)
NotifyError                                  04   [code-only: GranularSpeedEditorWindow.axaml.cs]
ObserveProperty                              02 (mini-map only)
OnClosed                                     05 SYS-WINSTATE
OnClosing                                    05 SYS-WINSTATE
OnPointerMoved                               02 (mini-map only) | 04 (mini-map only)
OnPointerPressed                             02 | 04   [code-only: FluidVolumeSlider.cs, SpinningWheelSlider.cs]
OnPointerReleased                            02 | 04   [code-only: FluidVolumeSlider.cs, SpinningWheelSlider.cs]
OnPointerWheelChanged                        04 (mini-map only)
OnTick                                       01 (mini-map only)
OnTrackSelected                              02 AUD-DIALOGS
OutputTimeline                               01 TL-OUTPUTTIMELINE | 02 AUD-DIALOGS
OverlayCanvas                                04 UI-COACH
P3ASYNC_01                                   02 AUD-DIALOGS
PhoneFrameMockup                             01 TL-PORTRAIT | 04 (mini-map only)
GenerateAsync (GeminiClient)                 10 SYS-AISETTINGS   (replaces PostWithRetryAsync, removed)
ProcessWorker                                03 FFM-BINPATH
ProgramDataRoot                              05 (mini-map only)
ProjectRecoveryService                       05 (mini-map only)
PushUndo                                     04 (mini-map only)
QUALITY_01                                   03 FFM-QUALITY
QUALITY_02                                   03 FFM-QUALITY
QUALITY_03                                   03 FFM-QUALITY
QUALITY_04                                   04 UI-SAFEGUARDS
QUALITY_05                                   03 FFM-QUALITY
QualityLabel                                 04 UI-SAFEGUARDS
QualityLadder                                03 FFM-QUALITY
QualitySliderValue                           03 FFM-QUALITY
QuantizeItemSize                             01 TL-PORTRAIT
ReadInt                                      05 (mini-map only)
ReadObject                                   05 (mini-map only)
ReadString                                   06   [code-only: ProjectSerializer.cs]
RecoveryManager                              05 SYS-RECOVERY
RecoveryStateFile                            05 (mini-map only)
Redo                                         04 UI-GRANULAR
Register                                     04 (mini-map only)
RelayoutFrameLane                            01 (mini-map only)
Reload                                       02 (mini-map only)
ReportMicHealth                              02 AUD-VOICEOVER
ResizeGrip                                   04 UI-RESIZEGRIP
ResolveHostPanel                             04 UI-COACH
ResultSegments                               05 SYS-WINSTATE
RESUME_01                                    02 AUD-DIALOGS
ResumeFromInitialStateAsync                  02 AUD-DIALOGS
RewindFromTimelineEnd                        01 TL-ENDSTOP
RotateBackupsUnlocked                        05 SYS-RECOVERY
RuntimeLog                                   05 (mini-map only)
SampleRate                                   04 (mini-map only)  [= 4000, WaveformPeaks]
SaveBoundsSync                               05 SYS-WINSTATE (removed by UIIPC_01; replaced by SaveBoundsOnClose)
SaveRecoveryState                            02 (mini-map only) | 05 SYS-EDITHOT | 07 UNDO-EQUALITY
SaveState                                    05 (mini-map only)
CurrentSchemaVersion                         05 (mini-map only)  [= 14, SettingsManager]
SEAM_01                                      01 TL-MARKERS | 04 UI-GRANULAR
SEEKSTORM_01                                 04 UI-GRANULAR
SETTX_01                                     05 SYS-ATOMICWRITE (settings transaction API — Update/SetAutoUpdateChecks; mutation guard)
SETTX_02                                     05 SYS-ATOMICWRITE (one named-mutex acquisition spans read-modify-write; lock order; unknown/future settings preserved)
SegDragMode                                  01 (mini-map only)
SelectSegment                                01 (mini-map only)
SerializeState                               05 (mini-map only)
SessionStateFile                             05 (mini-map only)
SetButtonText                                04 (mini-map only)
SetGlobalMasterVolume                        02 AUD-MASTERVOL
SizeToContent                                04 UI-DPI
SLIDER_06                                    02 AUD-DIALOGS
SLIDER_07                                    04 UI-THEME | 02 AUD-DIALOGS
SnapInsertionPoint                           01 TL-MEME
SourceMsToOutputSeconds                      01 TL-OUTPUTTIMELINE
SourceToOutput                               01 TL-MEME
SpinningWheelSlider                          04 (mini-map only)
SPLICE_01                                    03 FFM-CONCAT
SPLICE_02                                    03 FFM-CONCAT
StartRecording                               02 (mini-map only)
StopRecording                                02 (mini-map only)
SurvivingSourceWidth                         01 TL-PORTRAIT  [= 720]   [formula term, not a code symbol]
SYS-DEVBUILD                                 05 SYS-DEVBUILD
Tactile                                      04 UI-THEME
TargetMbFor                                  03 FFM-QUALITY
TemporalStabilityMask                        01 TL-HUDSTREAMING (Welford variance, inline in HudAutoDetector.SampleFramesAsync)
TextOverlayGenerator                         03 (mini-map only)
THUMB_01                                     01 TL-HITBOX
THUMB_02                                     01 TL-HITBOX
TimelineKnob                                 01 (mini-map only)
TimelineLanesControl                         01 (mini-map only)
TimelineStartSeconds                         01 (mini-map only)
CurrentTime                                  02 AUD-IPCSTATE | 02 AUD-VOICEOVER
TL-ENDSTOP                                   01 TL-ENDSTOP
TogglePlayPauseTransport                     01 TL-ENDSTOP
ToWorkerQualityLevel                         03 FFM-QUALITY
Track                                        04 (mini-map only) | 05 SYS-WINSTATE
TrackScrollGutterPx                          02 AUD-DIALOGS
TrackTableWidth                              02 AUD-DIALOGS
TRANSPORT_TRACE_01                           05 SYS-DEVBUILD
TransportToggleCoalesceMs                    04 UI-SAFEGUARDS
TransportTrace                               05 SYS-DEVBUILD
TryExecutePlayPause                          04 UI-SAFEGUARDS
UI-RESIZEGRIP                                04 UI-RESIZEGRIP
UI-WINPLACE                                  04 UI-WINPLACE
UiStateStore                                 04 UI-COACH | 05 (mini-map only)
Undo                                         04 UI-GRANULAR
Uninstall                                    05 SYS-MUTEX
UpdateEstimatedQuality                       03 FFM-QUALITY
UpdateReadyLamp                              02 AUD-VOICEOVER
UpdateThumbnailButtonState                   01 (mini-map only)
UpdateTimelineMarkers                        01 (mini-map only)
VideoVolSlider                               02 AUD-DIALOGS
VOEND_01                                     01 TL-ENDSTOP
VOLEVEL_01                                  02 AUD-VOICEOVER; 04 UI-PROMPTS; 06 PROJ-SCHEMA (schema 8)
VoiceOverPreviewPlayer                       02 AUD-VOICEOVER
VoiceOverWindow                              01 (mini-map only) | 02 (mini-map only)
VoiceRecorder                                02 AUD-VOICEOVER
VoiceCaptureSession                          02 AUD-VOICEOVER
IVoiceCaptureSession                         02 AUD-VOICEOVER
VOCAPTURE_01                                 02 AUD-VOICEOVER
VOCAPTURE_02                                 02 AUD-VOICEOVER
VOASYNC_02                                   02 AUD-VOICEOVER
VORECOVERY_01                                02 AUD-VOICEOVER | 05 SYS-WRITEORDER (WRITEORDER_03)
MICHEALTH_02                                 02 AUD-VOICEOVER | 06 PROJ-AOTPOLICY
MICSMOKE_01                                  02 AUD-VOICEOVER
MICSMOKE_02                                  02 AUD-VOICEOVER
VORECOVERY_02                                02 AUD-VOICEOVER
VORECOVERY_03                                02 AUD-VOICEOVER | 04 UI-SAFEGUARDS
SESSIONOWNER_01                              02 AUD-VOICEOVER
VoiceOverRecoveryManager                     02 AUD-VOICEOVER
IVoiceOverRecoveryStore                      02 AUD-VOICEOVER
VolumeChanged                                02   [code-only: VoiceRecorder.cs, VoiceCaptureSession.cs]
VolumeSlider                                 02 (mini-map only)
VOMON_02                                     02 AUD-VOICEOVER
WindowBoundsHelper                           04 UI-DETACH | 04 UI-WINPLACE | 05 SYS-WINSTATE
WindowPlacement                              04 UI-WINPLACE | 05 SYS-WINSTATE
DialogFit                                    04 UI-WINPLACE
WINPLACE_01                                  04 UI-WINPLACE | 05 SYS-WINSTATE
WINPLACE_02                                  04 UI-WINPLACE | 05 SYS-WINSTATE
WINPLACE_03                                  04 UI-WINPLACE
WINPLACE_04                                  04 UI-DETACH | 04 UI-WINPLACE
WindowResizeGrip                             04 UI-RESIZEGRIP
WINSEED_01                                   05 SYS-WINSTATE
WorkerConstantQualityLevel                   03 FFM-QUALITY
WrapText                                     03 (mini-map only)
WriteInt                                     05 (mini-map only)
WriteObject                                  05 SYS-RECOVERY
ZOOMLIVE_07                                  04 UI-GRANULAR
ZOOMCARD_01                                  04 UI-GRANULAR
ZOOMANTS_01                                  04 UI-GRANULAR
ZOOMANTS_02                                  04 UI-GRANULAR
ZoomBandThicknessPx                          04 UI-GRANULAR  [= 2.5]
ZoomAntsBrush                                04 UI-GRANULAR
_marchingAntsOffset                          04 UI-GRANULAR
ZOOMSTYLE_02                                 04 UI-GRANULAR   [spec-only: no in-code tag]
ZOOMPREVIEW_01                               04 UI-GRANULAR   [spec-only: no in-code tag]
ZOOMCOMMIT_01                                04 UI-GRANULAR   [spec-only: no in-code tag]
SLOWZOOMGLIDE_01                             04 UI-GRANULAR | 01 TL-ZOOM
SlowZoomGlideAvailability                    03 FFM-ZOOMGRAPH | 04 UI-GRANULAR
CheckManualAsync                             09 SYS-AUTOUPDATE
ShowAboutAsync                               04 UI-SETTINGS-ABOUT
GetCurrentVersion                            09 SYS-AUTOUPDATE
TryParseVersion                              09 SYS-AUTOUPDATE
SynchronizeVersionFiles                      09 SYS-AUTOUPDATE | 09 DIST-RELEASEGATE   (ReleaseGate; official releases only)
ZoomPreviewSimulator                         02 AUD-VOICEOVER | 03 (mini-map only)
ZoomRampSeconds                              03  [= 0.5]   [code-only: GranularSpeedBuilder.cs, ZoomPreviewSimulator.cs]
AOTSAFETY_01                                 06 PROJ-AOT
EndGesture                                   07 UNDO-RULES
GestureIdleMs                                07 UNDO-RULES  [= 900; GranularEditHistory.GestureIdleMs = 700]
GestureIdle                                  07 UNDO-RULES   (per-instance U1 window, UNDO_26)
TouchGesture                                 07 UNDO-RULES   (UNDO_26)
UNDO_26                                      07 UNDO-MIGRATION | 04 UI-GRANULAR
UNDO_27                                      07 UNDO-MIGRATION
UNDO_28                                      07 UNDO-MIGRATION
GranularEditHistory                          07 UNDO-MIGRATION
GranularEditSession                          07 UNDO-EDITSTATE | 08 COMP-MVVM (EDITSTATE_01)
GranularHistoryParking                       07 UNDO-EDITSTATE (UNDO_25 slot)
CropEditSession                              07 UNDO-EDITSTATE | 08 COMP-MVVM (EDITSTATE_01)
CropLayer                                    07 UNDO-EDITSTATE
EDITSTATE_01                                 07 UNDO-EDITSTATE | 08 COMP-MVVM
GranularEditorSnapshot                       07 UNDO-MIGRATION
ParkedGranularHistory                        07 UNDO-MIGRATION
CropLayoutSnapshot                           07 UNDO-MIGRATION
CropHistoryLabels                            07 UNDO-MIGRATION
MusicWizardSnapshot                          07 UNDO-MIGRATION
HistoryShortcut                              07 UNDO-MIGRATION
U1                                           07 UNDO-RULES
U2                                           07 UNDO-RULES
U3                                           07 UNDO-RULES
U4                                           07 UNDO-RULES
UNDO_10                                      07 UNDO-WHY | 07 UNDO-STATE   [code-only: UndoStack.cs]
UNDO_11                                      07 UNDO-RULES   [code-only: UndoStack.cs — U2 ceiling]
UndoEntry                                    07 UNDO-PERSIST
UndoStack                                    07 UNDO-RULES
AOTSAFETY_02                                 06 PROJ-AOT
AOTSAFETY_04                                 06 PROJ-AOT
AOTSAFETY_05                                 06 PROJ-AOT | 09 SYS-SIGNING
AddNode                                      06 PROJ-AOT
AotJson                                      06 PROJ-AOT
IndentedContext                              06 PROJ-AOT
BackupSuffix                                 06 PROJ-DISK
BuildTimeline                                06 PROJ-INPUTS
CheckSource                                  06 PROJ-INTEGRITY
EffectiveDurationMs                          06 PROJ-INPUTS
MinimumReadableSchemaVersion                 06 PROJ-SCHEMA
PROJ_01                                      06 PROJ-INPUTS
PROJ_02                                      06 PROJ-SCHEMA | 06 PROJ-AOT
PROJ_03                                      06 PROJ-SCHEMA
PROJ_04                                      06 PROJ-INPUTS
PROJ_05                                      06 PROJ-INTEGRITY
PROJ_08                                      06 PROJ-DISK
PROJ_09                                      06 PROJ-RECENT
ProjectDocument                              06 PROJ-INPUTS
ProjectIoResult                              06 PROJ-DISK
ProjectSerializer                            06 PROJ-AOT
ProjectStore                                 06 PROJ-DISK
RecentProjects                               06 PROJ-RECENT
SchemaVersion                                06 PROJ-SCHEMA
SourceClip                                   06 PROJ-INTEGRITY
SourceIntegrity                              06 PROJ-INTEGRITY
SuppressAotAnalysisWarnings                  06 PROJ-AOT
SuppressTrimAnalysisWarnings                 06 PROJ-AOT
UnknownFields                                06 PROJ-SCHEMA
ARCHTEST_01                                  08 COMP-ARCHTEST
ASYNCUI_01                                   08 COMP-ARCHTEST
ASYNCUI_02                                   08 COMP-ARCHTEST
BATCHPARENS_01                               05 SYS-DEVBUILD
LISTCOMMENT_01                               05 SYS-DEVBUILD
BATCHPARENS_02                               05 SYS-DEVBUILD
COMPOSITION_01                               08 COMP-ROOT
COMPOSITION_02                               08 COMP-ROOT
FAULTSTORM_01                                08 COMP-FAULTS
FAULTTIER_01                                 08 COMP-FAULTS
INJSEAM_01                                   08 COMP-ROOT   [IProjectStore]
INJSEAM_02                                   08 COMP-ROOT   [IClock]
INJSEAM_03                                   08 COMP-ROOT   [IUserNotifier]
INJSEAM_04                                   08 COMP-ROOT   [IFilePickerService]
MVVM_01                                      08 COMP-MVVM
MVVM_02                                      08 COMP-MVVM
PICKERMEMORY_01                              08 (mini-map only) | 05 SYS-WINSTATE
PIPELIFE_01                                  03 (mini-map only)
PIPELIFE_02                                  03 | 08   [code-only: FfmpegJobLifetime.cs, MergerWorker.cs]
PROJSESSION_01                               08 (mini-map only)
PROJSESSION_02                               06   [code-only: ProjectSession.cs]
PROJSESSION_03                               06 | 05 SYS-RECOVERY   [code-only: ProjectSession.cs]
PROJSESSION_04                               06 PROJ-DISK
PROJSESSION_05                               06 PROJ-DISK
PROJSESSION_06                               07 UNDO-STATE   [code-only: MainWindow.Project.cs]
PROJSESSION_07                               05 SYS-WINSTATE
PROJSESSION_08                               06 PROJ-CLOSEGUARD
PROJSESSION_09                               06 PROJ-CLOSEGUARD
SCRIM_01                                     04 UI-THEME
SIGNMANDATE_01                               09 SYS-SIGNING
TOOLNAV_01                                   08 (mini-map only)
TOOLNAV_02                                   05 SYS-WINSTATE
TOOLNAV_03                                   05 SYS-WINSTATE
TOOLNAV_04                                   05 SYS-WINSTATE   [code-only: ToolNavigator.cs, CropToolWindow.axaml.cs, VideoMergerWindow.axaml.cs]
UNDO_20                                      07 UNDO-RULES
UNDO_21                                      07 UNDO-STATE   [code-only: ProjectSession.cs]
UNDO_22                                      04 UI-GRANULAR   [code-only: MainWindow.Project.cs]
UPDATETRUST_02                               09 SYS-SIGNING
UPDATEUX_01                                  RETIRED — superseded by BGUPDATE_01
UPDATEUX_02                                  09 SYS-AUTOUPDATE   (size + time in the prompt)
UPDATEUX_03                                  09 SYS-AUTOUPDATE   (download window stages, speed, safety check)
UPDATEUX_04                                  09 SYS-AUTOUPDATE   (Restart & update now — now on the update card, BGUPDATE_01)
UPDATEUX_05                                  RETIRED — superseded by UPDATEPIN_01
UPDATEUX_06                                  09 SYS-AUTOUPDATE | 04 UI-SETTINGS-ABOUT   (Last checked)
UPGRADEUX_01                                 09 SYS-UPGRADEUX   (broker progress window, worker progress)
UPGRADEUX_02                                 09 SYS-UPGRADEUX   (UAC explained first)
UPGRADEUX_03                                 09 SYS-UPGRADEUX   (close open app before UAC)
UPGRADEUX_04                                 09 SYS-UPGRADEUX   (plain failures, previous version reopened)
UPGRADEUX_05                                 09 SYS-UPGRADEUX   (first-launch "Finishing… / Updated")
UPGRADEUX_06                                 09 SYS-UPGRADEUX   (installer window first, worker copy in background)
DEVDATA_01                                   05 SYS-DEVBUILD   (dev sandbox in %LOCALAPPDATA%, AI keys kept)
DEVUPDATE_01                                 05 SYS-DEVBUILD | §6 SYS-AUTOUPDATE   (legacy explicit updater tests only; disabled for ordinary dev launches)
DEVVERIFY_01                                 05 SYS-DEVBUILD / SYS-VERIFYTOOL (direct dev.cmd verifier invocation before cleanup; dev verify)
DEVPARITY_01 / DEVISOLATION_01 / DEVCLEAN_01   05 SYS-DEVBUILD (current-source Release runtime, isolated resources, preserved settings)
DEVHELPERTEMP_01                            05 SYS-DEVBUILD / SYS-STORAGE-TABLE (fresh helper TEMP; defer locked leftovers)
STORAGEPATHS_01                             05 SYS-STORAGE-TABLE (authoritative storage inventory; mandatory update on storage changes)
DEVSHAREMEDIA_01                            05 SYS-DEVBUILD / SYS-STORAGE-TABLE (shared music/memes/Downloads; separate TEMP voice)
DEVINCREMENTAL_01                           05 SYS-DEVBUILD (incremental compiler outputs, hash-verified assets, full-clean fallback)
DEVFAST_01                                   05 SYS-DEVBUILD   (retired hot reload; DEVINCREMENTAL_01 now retains compiler outputs without hot reload)
UPDATEPIN_01                                 09 SYS-SIGNING   (publisher root built into the app)
UPDATEPIN_02                                 09 SYS-SIGNING   (leaf renews itself from the root)
UPDATEPIN_PEM_01                             09 SYS-SIGNING   (preserve public PEM data and verify its pin)
BGUPDATE_01                                  09 SYS-AUTOUPDATE | 04 UI-SETTINGS-ABOUT   (background download, one question)
DLRESUME_01                                  09 SYS-AUTOUPDATE   (resumable downloads)
DIST-SMART                                   09 DIST-SMART   (per-file smart updates)
BINDELTA_01                                  09 DIST-SMART   (binary patch of the main program)
BINDELTA_02                                  09 DIST-SMART   (streaming patch apply, 64 KiB buffers, bounded memory)
DELTABASE_01                                 09 DIST-SMART   (patch bases = verified programs of the last 3 published releases)
ASSETHASH_01                                 09 DIST-SMART | 09 SYS-AUTOUPDATE   (asset SHA-256 before use, capped decompression, patch header check)
RELGATE_01                                   09 DIST-RELEASEGATE   (release transaction: preflight, version commit + tag, undo, push)
RELTAG_01                                    09 DIST-RELEASEGATE | DIST-SPLIT   (release created from the pushed tag, --verify-tag)
RETAIN_01                                    09 DIST-RETENTION   (5 stable + newer betas kept; tags never deleted)
RETAIN_02                                    09 DIST-RETENTION   (retention removes old FILES only; release pages + notes kept)
RELQA_01                                     09 DIST-RELEASEGATE   (green CI on the commit + local sentinels/tests before stamping)
RELNOPROMPT_01                               09 DIST-RELEASEGATE   (no PUBLISH prompt; RELQA_02 retired)
RELRECORD_01                                 09 DIST-RETENTION | §3e   (every CHANGELOG version heading is a tag; pruned sections archived)
RELRECORD_02                                 09 DIST-RELEASEGATE   (notes equal to the last release's = unpromoted release)
RELRESUME_01                                 09 DIST-RELEASEGATE   (Build.cmd --resume finishes a pushed-but-unpublished release)
CHANGELOG_02                                 09 DIST-RELEASENOTES   (CI: code change without docs/changes.md fails)
UPGRADESMOKE_01                              09 DIST-UPGRADESMOKE   (old public install → new setup, free Windows runner)
COMPACTGONE_01                               09 DIST-SPLIT   (dormant app-only client code removed)
ROOTKEY_01                                   09 SYS-SIGNING   (root key's own passphrase; --protect-root-key; offline via FVS_ROOT_KEY)
REVOKE_01                                    09 SYS-SIGNING   (withdrawn publisher leaves: compiled + signed-list + per-user)
SIGNEXPIRY_01                                09 SYS-SIGNING   (in-app updater requires a leaf valid now for update.json)
HEALTHWIN_01                                 09 SYS-UPGRADE   (--no-launch builds MainWindow; probe loads libav)
CRASHLOOP_01                                 09 SYS-UPGRADE   (repeated failed starts → offer the previous version)
INSTALLONEXIT_01                             09 SYS-AUTOUPDATE   (a ready update installs when the app closes)
RESTARTSAFE_01                               09 SYS-AUTOUPDATE   (Restart & update closes only if the installer runs)
PAUSESTICK_01                                09 SYS-AUTOUPDATE   (a paused download is not resumed by the re-check)
WHATSNEW_01                                  09 SYS-AUTOUPDATE | 04 | 09 DIST-RELEASENOTES   ("What's new" after the update + About link)
BETA_01                                      09 DIST-RELEASEGATE   (pre-release publishing only; the app never offers betas, SETNOBETA_01)
RELNOTES_01                                  09 DIST-RELEASENOTES   (changes.md → CHANGELOG.md, fixed staging headings, promotion)
RELNOTES_02                                  09 DIST-RELEASENOTES   (user-notes lint)
RELNOTES_03                                  09 DIST-RELEASENOTES | 09 SYS-AUTOUPDATE | 04   ("What's new" on the update card)
WORKERSTAGE_01                               09 SYS-UPGRADE-TRUST   (elevated worker only from a protected %ProgramData% copy)
UNSIGNEDDEV_01                               09 SYS-SIGNING   (unsigned-update override is Debug-only)
BUILDKILL_01                                 05 SYS-DEVBUILD   (build scripts stop only repository processes)
UPDATESPACE_01                               09 SYS-AUTOUPDATE   (per-drive free-space check for what is written)
RECHECK_01                                   09 SYS-AUTOUPDATE   (re-check every 6 h while open)
ONBOARD_01                                   GOV README.md §0   (single entry point; project_structure.txt / AGENTS.md are pointers)
GOV-START                                    README.md §0
GOV-REPOMAP                                  REPO_MAP.md (GOV-REPOMAP-FILE)
SYS-UPGRADE-TRUST                            09 SYS-UPGRADE-TRUST
DIST-RELEASEGATE                             09 DIST-RELEASEGATE
DIST-RELEASENOTES                            09 DIST-RELEASENOTES
COMMENTARYMIX_01                             01 TL-CAPTIONS | 04 UI-AICOMMENTARY   (Funny/Sarcastic/Energetic/Tactical amounts, 4 styles)
COMMENTARYUI_02                              04 UI-AICOMMENTARY   (basic + always-closed Advanced options window)
TACTICAL_01                                  01 TL-CAPTIONS   (every "why" must be visible; unsure tactical lines dropped)
TACTICAL_02                                  01 TL-CAPTIONS   (12 words / 2.0 s at high Tactical)
TACTICAL_03                                  01 TL-CAPTIONS   (kill-tracker marks sent as facts)
SWEARING_01                                  01 TL-CAPTIONS   (clean by default)
REWRITELINE_01                               04 UI-AICOMMENTARY | 01 TL-CAPTIONS   (right-click "Rewrite this line")
CAPTIONPLACE_01                              01 TL-CAPTIONS   (Upper · Middle · Lower; Middle just under the centre)
CAPTIONLOOK_01                               01 TL-CAPTIONS | 03 FFM-CAPTIONS   (Outline vs dark box; stronger outline)
CAPTIONSIZE_01                               01 TL-CAPTIONS   (Small 0.8 · Medium 1.0 · Large 1.25)
CAPTIONRTL_01                                01 TL-CAPTIONS | 03 FFM-CAPTIONS   (Hebrew: auto base direction, one plain RTL run)
CommentaryPersonality                        01 TL-CAPTIONS
AiCommentaryOptionsViewModel                 04 UI-AICOMMENTARY
MiddleTopFraction                            01 TL-CAPTIONS   [= 0.54]
TacticalMinConfidence                        01 TL-CAPTIONS   [= 0.4]
FASTINSTALL_01                               09 SYS-UPGRADE   (build the new folder, link unchanged files)
ONEBACKUP_01                                 09 SYS-UPGRADE   (keep one backup)
NOSYSTEMTASK_01                              09 SYS-UPGRADE | 05 SYS-AVCLEAN   (no SYSTEM scheduled task)
NOPOWERSHELL_01                              05 SYS-AVCLEAN   (shortcuts via IShellLinkW, ShellLinkWriter)
NOHIDDENTOOLS_01                             05 SYS-AVCLEAN   (no hidden event/task/force-kill tools; Event Log API, LegacyScheduledTask)
NOLOGONHOOK_01                               05 SYS-AVCLEAN   (no Active Setup StubPath; old entry removed)
CFASAFE_01                                   09 SYS-CFASAFE   (Controlled folder access: ControlledFolderAccess, ProtectedFolderGate, own Media folder)
CFAFFMPEG_01                                 09 SYS-CFASAFE   (FFmpeg write probe before exporting into a protected folder; FfmpegCanWrite)
NOMEMREAD_01                                 05 SYS-AVCLEAN   (ProcessImage: program path without reading process memory; own names in the install folder only)
OPENWITHHOME_01                              05 SYS-AVCLEAN   ("Open with" registered only by the installed program)
NOMUIWIPE_01                                 05 SYS-AVCLEAN   (uninstall leaves Windows MuiCache alone)
NOLOCALPORT_01                               05 SYS-AVCLEAN   (installer channels are named pipes, not TCP ports; PipeAccess.CreateUpgradeServer)
NOLURKER_01                                  05 SYS-AVCLEAN   ("Later" arms the exit hook; no waiting installer process)
SIGNDEPS_01                                  09 SYS-SIGNING   (bundled FFmpeg/mpv signed only with a public certificate)
FREETRUST_01                                 09 SYS-SIGNING   (free routes to a trusted signature: SignPath Foundation, Microsoft Store, WDSI)
SIGNPATH_01                                  09 SYS-SIGNING   (SignPath Foundation certificate trusted only with the listed SHA-256; licence, privacy and signing-policy files)
OSSMEDIA_01                                  03 starter set   (installer ships Creative-Commons songs only)
NOPOWERSHELL_02                              03 RDP Auto-Fix | 05 SYS-AVCLEAN   (--apply-rdp-gpu-policy)
NOSELFDELETE_01                              05 SYS-AVCLEAN   (uninstaller cleanup, MoveFileEx)
NODROPPER_01                                 09 DIST-INSTALLER | 05 SYS-AVCLEAN   (Inno Setup installer, no embedded payload)
NOTEMPEXEC_01                                05 SYS-AVCLEAN   (no self-copy to %TEMP%; updates under %LOCALAPPDATA%\FreeVideoStudioUpdates)
PEIDENTITY_01                                09 SYS-SIGNING   (OriginalFilename = file name, manifest version)
APPICON_01                                   09 DIST-IDENTITY   (padding-free app/installer icon, assets/icons/FreeVideoStudio.ico)
UPDATECONSENT_01                             09 SYS-AUTOUPDATE | 04 UI-SETTINGS-ABOUT   (ask once, first check after 5 min)
DIST-INSTALLER                               09 DIST-INSTALLER
NOCMD_01                                     05 SYS-AVCLEAN   (whatsapp:// via shell, no cmd /c)
AvCleanRuleTests                             05 SYS-AVCLEAN
NEWRELEASE_01                                09 DIST-NEWRELEASE   (Build.cmd → docs/New-Release.md)
ReleaseChecklist                             09 DIST-NEWRELEASE
VERIFYHALT_01                                05 SYS-DEVBUILD
SYS-VERIFYTOOL                               05 SYS-VERIFYTOOL
SYS-CI                                       08 COMP-CI | 09 DIST-CIWATCH
SYS-DIAGREPORT                               05 SYS-DIAGREPORT
SYS-PAYLOADSPLIT                             09 DIST-SPLIT
SYS-REPOWEIGHT                               09 DIST-REPOWEIGHT
DIST-AGENTS                                  09 DIST-AGENTS | GOV §0a
CITEST_01                                    08 COMP-CI
FAULTTIER_02                                 08 COMP-FAULTCHANNEL
MVVM_03                                      08 COMP-ARCHTEST | 01 (mini-map only)
UNDO_23                                      07 UNDO-STATE
UNDO_24                                      07 UNDO-PERSIST
UNDO_25                                      07 UNDO-GRANULAR-APPLY | 04 UI-GRANULAR
PROJ_10                                      06 PROJ-INTEGRITY
PROJ_11                                      06 PROJ-SCHEMA
PROJ_12                                      06 PROJ-SCHEMA
AOTSAFETY_06                                 06 PROJ-AOT   [code-only: ProjectSerializer.cs, UndoSidecarStore.cs]
ProjectMask                                  06 PROJ-SCHEMA
ProjectMerge                                 06 PROJ-SCHEMA | 05 SYS-RECOVERY
MergeClip                                    06 PROJ-SCHEMA   [code-only: ProjectDocument.cs]
RuntimePayloadManifest                       09 DIST-SPLIT
UndoSidecarStore                             07 UNDO-PERSIST
DiagnosticReport                             05 SYS-DIAGREPORT
Faults.Install                               08 COMP-FAULTS
AOTSAFETY_03                                 06 PROJ-AOTPOLICY
AOTCLEAN_01                                  06 PROJ-AOTPOLICY
AOTCLEAN_02                                  06 PROJ-AOTPOLICY
AOTCLEAN_03                                  06 PROJ-AOTPOLICY | 04 UI-GPUPRESENT2
AOTCLEAN_04                                  06 PROJ-AOTPOLICY
WavAudioReader                               06 PROJ-AOTPOLICY
D3D11Interop                                 06 PROJ-AOTPOLICY
SIGNLOCAL_01                                 09 SYS-SIGNING
SIGNLOCAL_02                                 09 SYS-SIGNING
ILCCRASH_01                                  06 PROJ-AOTPOLICY
RELEASEASSETS_01                             09 DIST-SPLIT
GPUPRESENT_02                                04 UI-GPUPRESENT2
GPUPRESENT_03                                04 UI-GPUPRESENT2 | 05 SYS-WINSTATE (WGL startup handoff and skipped frames)
TryAcquireProducerKey                        04 UI-GPUPRESENT2
EDITHOT_01                                   05 SYS-EDITHOT
EDITHOT_02                                   05 SYS-EDITHOT
LiveMaskCache                                05 SYS-EDITHOT
MainWindow.Recovery.cs                       05 SYS-EDITHOT | 07 UNDO-EQUALITY
UNDOEQ_01                                    07 UNDO-EQUALITY
UNDOEQ_02                                    07 UNDO-EQUALITY
MUSICSYNC_01                                 02 AUD-PREVIEWSYNC
MUSICPAD_01                                  02 AUD-PREVIEWSYNC
MusicPadAlignment                            02 AUD-PREVIEWSYNC | 03 FFM-FADES
MUSICSYNC_02                                 02 AUD-PREVIEWSYNC
MusicBedPlan                                 02 AUD-PREVIEWSYNC | 02 AUD-MUSICWIN
MusicBedTrack                                02 AUD-PREVIEWSYNC
LeadInSec                                    02 AUD-MUSICFADE
OutgoingGainAt                               02 AUD-MUSICFADE
MusicCrossfade                               02 AUD-MUSICFADE
XFADEPLAN_01                                 02 AUD-MUSICFADE
MUSICEND_01                                  02 AUD-MUSICWIN (wizard output coverage -> source endpoint; no double speed conversion)
MUSICWIN_01                                  02 AUD-PREVIEWSYNC | 02 AUD-MUSICWIN
MusicStartsSeconds                           02 AUD-MUSICWIN
MusicEndsSeconds                             02 AUD-MUSICWIN
ToBedTracks                                  02 AUD-PREVIEWSYNC
MusicTracksArePlanned                        03 (mini-map only) | 02 AUD-PREVIEWSYNC
MusicPlaylistEntry                           02 AUD-MUSICWIN
MusicPlaylistMath                            02 AUD-MUSICWIN
MusicCoverage                                02 AUD-MUSICWIN
PLAYLIST_01                                  02 AUD-DIALOGS | 04 UI-MUSICWIZARD
SILENCETRIM_01                               02 AUD-DIALOGS
WIZ2STEP_01                                  02 AUD-DIALOGS | 04 UI-MUSICWIZARD (superseded → MUSICWIZ3_01)
PREVIEWSTOP_01                               02 AUD-DIALOGS | 04 UI-MUSICWIZARD
PREVIEW1_03                                  02 AUD-DIALOGS | 04 UI-MUSICWIZARD
COVERSTRIP_01                                02 AUD-DIALOGS | 04 UI-MUSICWIZARD
COVERBAR_01                                  04 UI-MUSICWIZARD
SONGLANE_01                                  04 UI-MUSICWIZARD | 02 AUD-MUSICWIN
SONGLANE_02                                  04 UI-MUSICWIZARD
SONGCARD_01                                  04 UI-MUSICWIZARD | 02 AUD-MUSICWIN (retired → PARTEDIT_01)
SOUNDOPT_01                                  04 UI-MUSICWIZARD (retired → SOUNDPANEL_01)
MUSICWIZ3_01                                 04 UI-MUSICWIZARD | 02 AUD-DIALOGS
MUSICWIZSIZE_01                              04 UI-MUSICWIZARD
PARTLANES_01                                 04 UI-MUSICWIZARD
PARTAXIS_01                                  04 UI-MUSICWIZARD
PARTCUT_01                                   04 UI-MUSICWIZARD
WIZNAV_01                                    04 UI-MUSICWIZARD
GAPWARN_01                                   04 UI-MUSICWIZARD
PARTDEMO_01                                  04 UI-MUSICWIZARD
PASTEND_01                                   04 UI-MUSICWIZARD
PARTLANEH_01                                 04 UI-MUSICWIZARD
PARTLAYOUT_01                                04 UI-MUSICWIZARD
UNDOGLOW_01                                  04 UI-MUSICWIZARD
MUSICWIZSIZE_02                              04 UI-MUSICWIZARD | 04 UI-WINPLACE
MUSICWIZTEST_01                              04 UI-MUSICWIZARD
WHOLESTRIP_01                                03 FFM-THUMBSTRIP
TEMPCLEAN_01                                 05 SYS-STORAGE-TABLE
REVIEWRULER_01                               04 UI-MUSICWIZARD
WIZPAUSE_01                                  02 AUD-PREVIEWSYNC
RESIZEASPECT_01                              04 UI-DPI
MPVLOGCAP_01                                 05 SYS-STORAGE-TABLE
MEMEENDMUSIC_01                              02 AUD-DIALOGS
MUSICOCC_01                                  02 AUD-DIALOGS | 04 UI-MUSICWIZARD
PARTEDIT_01                                  04 UI-MUSICWIZARD | 02 AUD-DIALOGS
SOUNDPANEL_01                                04 UI-MUSICWIZARD | 02 AUD-SIDECHAIN
SOUNDMIX_01                                  02 AUD-SIDECHAIN | 04 UI-MUSICWIZARD
PROJMUSIC_01                                 06 PROJ-SCHEMA | 02 AUD-MUSICWIN
MusicWizardView                              04 UI-MUSICWIZARD
FirstSegmentOfTrack                          02 AUD-DIALOGS
ProjectMusicSong                             06 PROJ-SCHEMA
LIST_08                                      02 AUD-DIALOGS | 04 UI-MUSICWIZARD
MERGERMUSIC_01                               02 AUD-DIALOGS | 04 UI-MUSICWIZARD
MERGERMUSIC_02                               04 UI-MUSICWIZARD
MusicWizardViewModel                         04 UI-MUSICWIZARD
PreviewAudioSync                             02 AUD-PREVIEWSYNC
PREVIEWLAYERS_01                             02 AUD-PREVIEWSYNC
FREEZESFX_05                                 02 AUD-FREEZESFX
FREEZEBELL_01                                02 AUD-FREEZESFX
FREEZESFX_04                                 02 AUD-FREEZESFX (retired: synthesised sound)
FREEZESFX_03                                 02 AUD-FREEZESFX (retired: SoundExpr deleted)
FreezeSound                                  02 AUD-FREEZESFX
FREEZELEVEL_01                               02 AUD-FREEZESFX
UIBLOCK_01                                   02 AUD-IPCSTATE
UIBLOCK_02                                   05 (UI stall watchdog)
FREEZEPARITY_01                              02 AUD-FREEZESFX
FREEZEPARITY_02                              02 AUD-FREEZESFX
PreviewSlice                                 02 AUD-FREEZESFX
ClipBodyOutputEndSec                         02 AUD-FREEZESFX
UIWATCHDOG_01                                05 (UI stall watchdog)
ReadPropertyAsync                            02 AUD-IPCSTATE
AIHINT_02                                    04 (start-screen AI entry points)
VideoLoudnessCache                           02 AUD-FREEZESFX
GainDbFor                                    02 AUD-FREEZESFX
FreezeSoundCacheDirectory                    02 AUD-FREEZESFX | 05 SYS-STORAGE-TABLE
FREEZEMUSIC_01                               02 AUD-FREEZESFX
MusicDipChains                               02 AUD-FREEZESFX
PREVIEWSPEED_01                              02 AUD-PREVIEWSYNC
PREVIEWSPEED_02                              02 AUD-PREVIEWSYNC
MUSICSYNC_03                                 02 AUD-PREVIEWSYNC
EditorPreviewLayers                          02 AUD-PREVIEWSYNC
MusicBedPreviewPlayer                        02 AUD-PREVIEWSYNC
BedPreviewFollower                           02 AUD-PREVIEWSYNC
PreviewSpeedGovernor                         02 AUD-PREVIEWSYNC
AudioPlayerClock                             02 AUD-PREVIEWSYNC
PreviewSourceToOutputSeconds                 02 AUD-PREVIEWSYNC
COLOR_01                                     03 FFM-COLOR
ExportColorPolicy                            03 FFM-COLOR
VideoColorInfo                               03 FFM-COLOR
LOGVIS_01                                    08 COMP-LOGVIS
FaultCounters                                08 COMP-LOGVIS
WarnThrottled                                08 COMP-LOGVIS
WRITEORDER_01                                05 SYS-WRITEORDER
WRITEORDER_02                                05 SYS-WRITEORDER
USERSCOPE_01                                 05 SYS-USERSCOPE
DefaultUserRoot                              05 SYS-USERSCOPE
UserScopedName                               05 SYS-USERSCOPE
SCRAPER_01                                   03 FFM-SCRAPER
TIMINGTAG_02                                 03 FFM-SCRAPER
FRAMESNAP_01                                 03 FFM-SCRAPER
ExportTimingTag                              03 FFM-SCRAPER
SCRAPER_02                                   03 FFM-SCRAPER
SCRAPER_03                                   03 FFM-SCRAPER
LIBAVPROBE_01                                03 FFM-LIBAVPROBE
LIBAVPROBE_04                                03 FFM-LIBAVPROBE
NOCOMMENTS_01                                README §0.1 GOV-START
LIBAVPROBE_02                                03 FFM-LIBAVPROBE
LIBAVPROBE_03                                03 FFM-LIBAVPROBE
MediaMetadataProbe                           03 FFM-LIBAVPROBE
FVS_MEDIA_PROBE                              03 FFM-LIBAVPROBE
LIBAVFRAME_01                                03 FFM-LIBAVFRAME
LIBAVFRAME_02                                03 FFM-LIBAVFRAME
LIBAVFRAME_03                                03 FFM-LIBAVFRAME
LIBAVFRAME_04                                03 FFM-LIBAVFRAME
LIBAVFRAME_05                                03 FFM-LIBAVFRAME
VideoFrameGrabber                            03 FFM-LIBAVFRAME
FVS_FRAME_DECODE                             03 FFM-LIBAVFRAME
SCRAPER_04                                   03 FFM-SCRAPER
SCRAPER_05                                   03 FFM-SCRAPER
IntroTag                                     03 FFM-SCRAPER
MergedTimeline                               03 FFM-SCRAPER
MergeClipAnalyzer                            03 FFM-SCRAPER
MergerThumbnailScraper                       03 FFM-SCRAPER
MERGEEDL_01                                  01 TL-COMPOSITE
EDLNULL_01                                   01 TL-COMPOSITE
RESTOREMISS_01                               05 SYS-RECOVERY
RESTORECRASH_01                              05 SYS-RECOVERY
RESTOREFAIL_01                               05 SYS-RECOVERY
MergeEdl                                     01 TL-COMPOSITE
COMPOSITE_01                                 01 TL-COMPOSITE
MERGESESSION_01                              05 SYS-RECOVERY
MergerAutosaveStore                          05 SYS-RECOVERY
MergerSession                                05 SYS-RECOVERY
ClipIdList                                   05 SYS-RECOVERY
MERGEUNDO_01                                 07 UNDO-MIGRATION | 07 UNDO-RULES
LANES_01                                     04 UI-MERGERQUEUE
LANECACHE_02                                 04 UI-MERGERQUEUE
ThumbGrid                                    04 UI-MERGERQUEUE
WaveformPeaks                                04 UI-MERGERQUEUE
LaneDiskCache                                04 UI-MERGERQUEUE | 05 SYS-ATOMICWRITE
LaneDiskStore                                04 UI-MERGERQUEUE | 05 SYS-ATOMICWRITE
CACHEBOUND_01                                04 UI-MERGERQUEUE | 05 SYS-ATOMICWRITE
CACHEBOUND_02                                04 UI-MEMESELECT
CACHEBOUND_03                                04 UI-MEMESELECT
CACHEBOUND_04                                04 UI-MERGERQUEUE
CACHEBOUND_05                                04 UI-MEMESELECT | 03 FFM-MEMECORNER
WAVEPIXEL_01                                 04 UI-MERGERQUEUE
WaveformEnvelope                             04 UI-MERGERQUEUE
WaveformLanePresenter                        04 UI-MERGERQUEUE
WaveformPeakSource                           04 UI-MERGERQUEUE
FILMPIXEL_01                                 04 UI-MERGERQUEUE
FilmLanePresenter                            04 UI-MERGERQUEUE
LANEINSTANCE_01                              04 UI-MERGERQUEUE
PCMSTREAM_01                                 04 UI-MERGERQUEUE (LANECACHE_02)
PCMSTREAM_02                                 04 UI-MERGERQUEUE | 02 AUD-DIALOGS
PCMSTREAM_03                                 02 AUD-DIALOGS
FfmpegPcmPipe                                04 UI-MERGERQUEUE | 02 AUD-DIALOGS
MusicEnergyAnalysis                          02 AUD-DIALOGS
UIIPC_01                                     05 SYS-IPCLIFETIME | 05 SYS-WINSTATE
IPCDEADLINE_01                               05 SYS-IPCLIFETIME
PIPEAUTH_01                                  05 SYS-IPCLIFETIME
SessionStateWriter                           05 SYS-IPCLIFETIME
SaveBoundsOnClose                            05 SYS-WINSTATE
FilmStripSource                              04 UI-MERGERQUEUE
FilmCacheBudgetBytes                         04 UI-MERGERQUEUE
ReleaseFilmLeases                            04 UI-MERGERQUEUE
OwnedLruCache                                04 UI-MEMESELECT
ViewportThumbnailLoader                      04 UI-MEMESELECT
REMOVEUX_01                                  05 SYS-SETTINGSSCHEMA (schema v9)
EMPTYQUEUE_01                                04 UI-MERGERQUEUE
LanePlanner                                  04 UI-MERGERQUEUE
ProgressiveLaneRunner                        04 UI-MERGERQUEUE
ANTS_01                                      04 UI-MERGERQUEUE
TimelineReorder                              04 UI-MERGERQUEUE
CompositeTimeline                            01 TL-COMPOSITE | 02 AUD-CONCAT
MERGEEDIT_01                                 01 TL-COMPOSITE
MergeEditorSource                            01 TL-COMPOSITE
MERGEPREVIEW_EDL_01                          01 TL-COMPOSITE | 04 UI-MERGERQUEUE
MERGEPREVIEW_01                              01 TL-COMPOSITE | 04 UI-MERGERQUEUE
MergerPreviewPlan                            01 TL-COMPOSITE
MERGEEDIT_02                                 04 UI-MERGERQUEUE
CLIPACTIONS_01                               04 UI-MERGERQUEUE
MERGERUX_01                                  04 UI-MERGERQUEUE
MERGERPLAYHEAD_01                            04 UI-MERGERQUEUE
AppPlayheadBrush                             04 UI-MERGERQUEUE
AntsThickness                                04 UI-MERGERQUEUE
GRABCURSOR_01                                04 UI-MERGERQUEUE
GrabCursors                                  04 UI-MERGERQUEUE
FramePtsProbe                                03 FFM-SCRAPER
MERGEGRAPH_01                                03 FFM-SCRAPER
MergeClipGraph                               03 FFM-SCRAPER
MUSICMAP_01                                  03 FFM-SCRAPER | 02 AUD-CONCAT
OUTTAG_01                                    03 FFM-SCRAPER
OutputToSourceRelative                       01 TL-OUTPUTTIMELINE
CoordinateConstants                          01 (mini-map only)
MaxZoomUpscale                               01 TL-ZOOM  [= 8]
ReplaceCurrent                               07 (mini-map only) | 07 UNDO-MIGRATION
NextUndoLabel                                07 (mini-map only) | 07 UNDO-TODO
NextRedoLabel                                07 (mini-map only) | 07 UNDO-TODO
TimePosChanged                               02 AUD-VOICEOVER
DUCKOFF_01                                   02 AUD-SIDECHAIN
carving_enabled                              02 AUD-SIDECHAIN
DUCKMB_01                                    02 AUD-SIDECHAIN
DUCKSTRENGTH_01                              02 AUD-SIDECHAIN
RatioFor                                     02 AUD-SIDECHAIN
DefaultStrength                              02 AUD-SIDECHAIN  [= 50]
MixProtectionViewModel                       02 AUD-SIDECHAIN
SETTINGSVM_01                                04 UI-SETTINGS-ABOUT
SETTINGSIA_01                                04 UI-SETTINGS-ABOUT
SETUNSAVED_01                                04 UI-SETTINGS-ABOUT
SETPREVIEW_01                                04 UI-SETTINGS-ABOUT
SETKEYS_01                                   04 UI-SETTINGS-ABOUT
SETKEYS_02                                   04 UI-SETTINGS-ABOUT
SETLASTTAB_01                                04 UI-SETTINGS-ABOUT
SETSAVEFAIL_01                               04 UI-SETTINGS-ABOUT
SETTIPS_01                                   04 UI-SETTINGS-ABOUT
SETDEPS_01                                   04 UI-SETTINGS-ABOUT
SETLOUD_01                                   04 UI-SETTINGS-ABOUT
SETZOOM_01                                   04 UI-SETTINGS-ABOUT  [retired → SETZOOMONE_01]
SETGAME_01                                   04 UI-SETTINGS-ABOUT
SETMUSICDIR_01                               04 UI-SETTINGS-ABOUT
MEMEPENDING_01                               04 UI-SETTINGS-ABOUT
SETSEARCH_01                                 04 UI-SETTINGS-ABOUT
SETBACKUP_01                                 04 UI-SETTINGS-ABOUT
SETABOUTSAVE_01                              04 UI-SETTINGS-ABOUT
SETLASTALWAYS_01                             04 UI-SETTINGS-ABOUT
SETFREEZEDEF_01                              04 UI-SETTINGS-ABOUT
SETKEYSFIRST_01                              04 UI-SETTINGS-ABOUT
SETCHOICES_01                                04 UI-SETTINGS-ABOUT  [retired → SETSTARTONE_01]
SETUPDCOPY_01                                04 UI-SETTINGS-ABOUT
SETZOOMCAP_01                                04 UI-SETTINGS-ABOUT
SETNOMETERED_01                              04 UI-SETTINGS-ABOUT
SETNAMEWARN_01                               04 UI-SETTINGS-ABOUT
SETRESTORECONFIRM_01                         04 UI-SETTINGS-ABOUT
SETLABELFIT_01                               04 UI-SETTINGS-ABOUT
SETDECLUTTER_01                              04 UI-SETTINGS-ABOUT
SETUPDATEONE_01                              04 UI-SETTINGS-ABOUT
SETNOBETA_01                                 04 UI-SETTINGS-ABOUT
SETCLICKONE_01                               04 UI-SETTINGS-ABOUT
SETADVANCED_01                               04 UI-SETTINGS-ABOUT
SETSTARTONE_01                               04 UI-SETTINGS-ABOUT
SETSAFEONE_01                                04 UI-SETTINGS-ABOUT
SETEDITORMEMORY_01                           04 UI-SETTINGS-ABOUT
SETMERGERMOVED_01                            04 UI-SETTINGS-ABOUT
SETFOLDERONE_01                              04 UI-SETTINGS-ABOUT
SETSOUNDONE_01                               04 UI-SETTINGS-ABOUT
SETMUSICVOL_01                               04 UI-SETTINGS-ABOUT
SETVOICEONE_01                               04 UI-SETTINGS-ABOUT
SETNOCONSENTUI_01                            04 UI-SETTINGS-ABOUT
SETKEYONE_01                                 04 UI-SETTINGS-ABOUT
SETAIONE_01                                  04 UI-SETTINGS-ABOUT
SETAIMODEL_01                                04 UI-SETTINGS-ABOUT
SETZOOMONE_01                                04 UI-SETTINGS-ABOUT
SETCAMONE_01                                 04 UI-SETTINGS-ABOUT
SETNAMEONE_01                                04 UI-SETTINGS-ABOUT | 03 FFM-OUTNAME
FREEZESFX_01                                 02 AUD-FREEZESFX
FREEZESFX_02                                 02 AUD-FREEZESFX
SETRESTORE_01                                04 UI-SETTINGS-ABOUT
SETDANGER_01                                 04 UI-SETTINGS-ABOUT
SETAITEST_01                                 04 UI-SETTINGS-ABOUT
SETWIDTH_01                                  04 UI-SETTINGS-ABOUT
SETCONSENTVENDOR_01                          04 UI-SETTINGS-ABOUT  [retired → SETNOCONSENTUI_01]
FILEBACKUP_01                                04 UI-SETTINGS-ABOUT
SETKEYS_03                                   04 UI-SETTINGS-ABOUT
SETENTER_01                                  04 UI-SETTINGS-ABOUT
SETLASTTAB_02                                04 UI-SETTINGS-ABOUT
SETAIROWS_01                                 04 UI-SETTINGS-ABOUT  [retired → SETAIONE_01]
SETGAME_02                                   04 UI-SETTINGS-ABOUT
SETREVEAL_01                                 04 UI-SETTINGS-ABOUT
KEYLABEL_01                                  04 UI-SETTINGS-ABOUT | 04 UI-AIEASE
NEWVIDEOGUARD_01                             06 PROJ | 04 UI-SAFEGUARDS
DONECLEAN_01                                 05 SYS-RECOVERY | 06 PROJ | 04 UI-SAFEGUARDS
RECOVERYFULL_01                              05 SYS-RECOVERY
NOTICEREAD_01                                04 UI-COACH
OVERLAYTEXT_01                               04 UI-THEME
CONFIRMFLYOUT_01                             04 UI-SAFEGUARDS
MICPLAIN_01                                  04 UI-SAFEGUARDS
HANDOFFFAIL_01                               04 UI-SAFEGUARDS
SETHELP_01                                   04 UI-SETTINGS-ABOUT
SETCHIPS_01                                  04 UI-SETTINGS-ABOUT
SETSAFETY_01                                 04 UI-SETTINGS-ABOUT  [retired → SETSAFEONE_01]
SETCENTER_01                                 04 UI-SETTINGS-ABOUT
SETCOPY_01                                   04 UI-SETTINGS-ABOUT
SETABOUT_01                                  04 UI-SETTINGS-ABOUT
SETTINGSDI_01                                04 UI-SETTINGS-ABOUT | 08 COMP-ROOT
AIKEYCANCEL_01                               04 UI-SETTINGS-ABOUT
SETBACKUPIO_01                               04 UI-SETTINGS-ABOUT
SETTINGSTYPES_01                             05 (settings) | 04 UI-SETTINGS-ABOUT
SETTINGSFREEZE_01                            05 (settings, SETTX)
MEMEPURE_01                                  03 FFM-MEMELIB
RENDERIMPORT_01                              04 UI-GPUSLOT (supersedes ZOOMHANG_01's wait)
ASYNCSAFE_01                                 08 COMP-ROOT
CTSSLOT_01                                   08 COMP-ROOT
SHELLLAUNCH_01                               08 COMP-ROOT
VIRALVM_01                                   08 COMP-ROOT | 04
SETWIDE_01                                   04 UI-SETTINGS-ABOUT  [retired]
SETPRIORITY_01                               04 UI-SETTINGS-ABOUT
SettingsViewModel                            04 UI-SETTINGS-ABOUT
CopySection                                  04 UI-SETTINGS-ABOUT
SETDEFAULTS_01                               04 UI-SETTINGS-ABOUT | 03 FFM-QUALITY
SETCONFIRM_01                                04 UI-SAFEGUARDS
FILEDELETE_01                                04 UI-SAFEGUARDS  [retired]
SETMERGERDIR_01                              04 UI-SETTINGS-ABOUT
DuckingStrength                              02 AUD-SIDECHAIN
CarvingStrength                              02 AUD-SIDECHAIN
CrossoverLowHz                               02 AUD-SIDECHAIN  [= 250]
CrossoverHighHz                              02 AUD-SIDECHAIN  [= 2900]
CarveRatio                                   02 AUD-SIDECHAIN  [= 2.5]
TunedRatio                                   02 AUD-SIDECHAIN  [= 4.0]
TunedThreshold                               02 AUD-SIDECHAIN  [= 0.1]
LOUDSTD_REMOVED_01                           02 AUD-MASTERING
PEAKSAFE_01                                  02 AUD-MASTERING
PeakSafety                                   02 AUD-MASTERING
SafetyCeilingDbtp                            02 AUD-MASTERING  [= -2.0]
SafetyLimiterLimitDb                         02 AUD-MASTERING  [= -2.3]
TamerHeadroomLu                              02 AUD-MASTERING  [= 9.0]
HasHarshPeaks                                02 AUD-MASTERING
MEMELEVEL_02                                 02 AUD-MASTERING
SPLICE_03                                    02 AUD-MASTERING
CLIPLEVEL_01                                 02 AUD-MASTERING
MergerMatchClipLoudness                      02 AUD-MASTERING
PREVIEWMIX_01                                02 AUD-PREVIEWMIX
VOPREVIEW_01                                 02 AUD-PREVIEWMIX
AudioGraphPruner                             02 AUD-PREVIEWMIX
AudioPreviewMap                              02 AUD-PREVIEWMIX
AudioPreviewOutputPath                       02 AUD-PREVIEWMIX
PREVIEWMIX_02                                02 AUD-PREVIEWMIX
THUMBAUDIO_01                                02 AUD-PREVIEWMIX
RenderedMixGate                              02 AUD-PREVIEWMIX
PreviewMixPolicy                             02 AUD-PREVIEWMIX | 03 FFM-TEMPO
RefreshPreviewMixState                       02 AUD-PREVIEWMIX
VOPREVIEW_02                                 02 AUD-PREVIEWMIX
VoiceProtectionPulseAt                       02 AUD-PREVIEWMIX
UpdatePreviewVoiceProtection                 02 AUD-PREVIEWMIX
AIPARITY_01                                  03 FFM-ZOOMGRAPH | 01 TL-ZOOM
EvaluateExportCrop                           03 FFM-ZOOMGRAPH
AiOriginSec                                  03 FFM-ZOOMGRAPH  [code-only: GranularSpeedBuilder.cs]
CORNERPARITY_01                              03 FFM-MEMECORNER | 01 TL-MEME | 04 UI-MEMESELECT
PREVIEWFIDELITY_01                           04 UI-PREVIEWFIDELITY | 03 FFM-ZOOMGRAPH | 03 FFM-COLOR
PreviewFidelity                              04 UI-PREVIEWFIDELITY
PreviewFidelityBadge                         04 UI-PREVIEWFIDELITY
EdgeClamped                                  03 FFM-ZOOMGRAPH
AnyEdgePadding                               03 FFM-ZOOMGRAPH
MaterialEdgeFraction                         03 FFM-ZOOMGRAPH  [= 0.01]
VOLSHARED_01                                 02 AUD-MASTERVOL
VOLMUTE_01                                   02 AUD-MASTERVOL
VOLCURVE_01                                  02 AUD-MASTERVOL
VOLSYNC_01                                   02 AUD-MASTERVOL
VOLWHEEL_01                                  02 AUD-MASTERVOL
PreviewMuted                                 02 AUD-MASTERVOL
PlayerMpvVolume                              02 AUD-MASTERVOL
ApplyPreviewGainAsync                        02 AUD-MASTERVOL
UISND_01                                     02 (mini-map only)
VOPROT_01                                    02 AUD-VOICEOVER
VOGATE_01                                    02 AUD-VOICEOVER
VOPRIO_01                                    02 AUD-PREVIEWMIX | 02 AUD-VOICEOVER
granularTimeMapper                           02 AUD-VOICEOVER
StreamGeometry                               02 AUD-VOICEOVER
FITEND_01                                    02 AUD-DIALOGS
SLIDER_08                                    02 AUD-DIALOGS | 04 UI-THEME
SLIDER_09                                    02 AUD-DIALOGS | 04 UI-THEME
ISSUE_04                                     02 AUD-MUSICFADE
CrossfadeOutSec                              02 AUD-MUSICFADE
EdgeFadeSec                                  02 AUD-MUSICFADE
KeepMusicDuringMeme                          02 AUD-MUSICFADE
EncoderManager                               03 FFM-HWENC
TwoPassEncoding                              03 FFM-HWENC
BinaryPathResolver                           03 FFM-BINPATH
ThumbnailStripGenerator                      03 FFM-THUMBSTRIP | 04 UI-MERGERQUEUE
InstallerGateName                            05 SYS-MUTEX
RunUninstallWorkerAsync                      05 SYS-MUTEX
EveryFixSentinelStillResolves                05 SYS-VERIFYTOOL | 08 COMP-ARCHTEST
DevCmdDelegatesTheSentinelCheckRatherThanParsingIt  05 SYS-VERIFYTOOL | 08 COMP-ARCHTEST
EveryCatchBlockReportsSomewhere              08 COMP-FAULTCHANNEL | 08 COMP-ARCHTEST
R9                                           09 DIST-SPLIT
PROJ_06                                      06 PROJ-INPUTS   [code-only: ProjectDocument.cs]
PROJ_07                                      07 UNDO-STATE   [code-only: ProjectDocument.cs]
SWITCHPROMPT_01                              06 PROJ-CLOSEGUARD
LAYOUTLOOP_01                                01   [code-only: TimelineLanesControl.axaml.cs]
ZOOMSIZE_02                                  01   [code-only: TimelineLanesControl.axaml.cs]
LAYOUTLOOP_02                                04 UI-GRANULAR   [code-only: GranularSpeedEditorWindow.axaml.cs — same defect as LAYOUTLOOP_01]
EDGEGUARD_01                                 01 TL-MARKERS   [code-only: GranularSpeedEditorWindow.axaml.cs]
DRAGCOST_01                                  04 UI-GRANULAR   [code-only: GranularSpeedEditorWindow.axaml.cs]
TRACEFLOOD_01                                05 SYS-DEVBUILD   [code-only: Program.cs]
LAYOUT_01                                    04   [code-only: CropToolWindow.axaml]
GATE_01                                      04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
ZOOM_01                                      04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
CROPZOOMRESET_01                             04 UI-SAFEGUARDS
CROPSAVEPROMPT_02                            04 UI-SAFEGUARDS
CROPUNSAVED_01                               04 UI-SAFEGUARDS | 06 PROJ-DISK
UNSAVEDPROJ_01                               06 PROJ-CLOSEGUARD | 04 UI-SAFEGUARDS
PROJECTLABEL_01                              06 PROJ-CLOSEGUARD
EXPORTCLOSE_01                               04 UI-SAFEGUARDS   [StopExportDecisionSeam]
DIALOGTIP_01                                 04 UI-SAFEGUARDS   [ConfirmDialogWindow.SetButtonText]
CROPSAVEERR_01                               04 UI-SAFEGUARDS   [CropToolWindow.DescribeSaveFailure]
SEGROWTHEME_01                               04 UI-THEME        [GranularSpeedEditorWindow.PaintToken]
SEGROWA11Y_01                                04 UI-THEME        [GranularSpeedEditorWindow.SegmentRowBorder]
CROPEDIT_01                                  05 SYS-RECOVERY
CROPPROFILE_01                               05 SYS-RECOVERY
CROPCONFIRM_01                               04 UI-SAFEGUARDS
CROPFIRSTBOOT_01                             05 SYS-RECOVERY
FORTNITEDEFAULT_02                           05 SYS-RECOVERY
CROPFALLBACK_02                              05 SYS-RECOVERY
SPECTATINGDEFAULT_01                         04 UI-SAFEGUARDS
NO_BOSS_HP_01                                04 UI-SAFEGUARDS
SAVECONFIRM_01                               04   [code-only: CropToolWindow.axaml.cs]
MAGICWAND_01                                 04 UI-AIHUDWAND   [code-only: CropToolWindow.MagicWand.cs]
DELETEBTN_01                                 04   [code-only: CropToolWindow.axaml.cs]
DELETESET_01                                 04   [code-only: CropToolWindow.axaml.cs]
CROPCANVAS_01                                04   [code-only: CropToolWindow.axaml.cs]
AUTOZOOM_01                                  04   [code-only: CropToolWindow.axaml.cs]
WHEELZOOM_01                                 04   [code-only: CropToolWindow.axaml.cs]
PAN_01                                       04   [code-only: CropToolWindow.axaml.cs]
BEZEL_01                                     04   [code-only: CropToolWindow.axaml]
SPLIT_01                                     04   [code-only: CropToolWindow.axaml]
LAYERSPANE_01                                04   [code-only: CropToolWindow.axaml]
ROLEPOPUP_01                                 04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
PLAYICON_01                                  04   [code-only: CropToolWindow.axaml]
TICKRULER_01                                 04   [code-only: CropToolWindow.axaml.cs]
AUTOPLAY_01                                  04   [code-only: CropToolWindow.axaml.cs]
ITEMMENU_01                                  04   [code-only: CropToolWindow.axaml.cs]
ITEMHIT_01                                   04   [code-only: CropToolWindow.axaml.cs]
GHOSTKILL_01                                 04   [code-only: CropToolWindow.axaml.cs]
CANCELSEL_01                                 04   [code-only: CropToolWindow.axaml.cs]
BACKTOVIDEO_01                               04   [code-only: CropToolWindow.axaml.cs]
WIZCOLLAPSE_01                               04   [code-only: CropToolWindow.axaml.cs]
PLAYOVERLAY_01                               04   [code-only: CropToolWindow.axaml]
ZOOMBAR_01                                   04   [code-only: CropToolWindow.axaml]
WIZCOMPACT_01                                04   [code-only: CropToolWindow.axaml]
TIMELINESLIM_01                              04   [code-only: CropToolWindow.axaml]
RESETMOVE_01                                 04   [code-only: CropToolWindow.axaml]
ORDERICONS_01                                04   [code-only: CropToolWindow.axaml]
RELAUNCHARG_01                               04   [code-only: CropToolWindow.axaml.cs]
HANDLECURSOR_01                              04   [code-only: CropToolWindow.axaml.cs]
NODUPES_01                                   04   [code-only: CropToolWindow.axaml.cs]
COMPOSERDIM_01                               04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
HEADERMERGE_01                               04   [code-only: CropToolWindow.axaml]
TIMELINESLIM_02                              04   [code-only: CropToolWindow.axaml]
PLAYROW_01                                   04   [code-only: CropToolWindow.axaml]
POPUPCLEAR_01                                04   [code-only: CropToolWindow.axaml, CropToolWindow.axaml.cs]
AUTOZOOM_02                                  04   [code-only: CropToolWindow.axaml.cs]
CROSSHAIR_01                                 04   [code-only: CropToolWindow.axaml.cs]
TOOLRETURN_01                                05 SYS-WINSTATE
AIAUDIT_01                                   10 SYS-AISETTINGS (plain-English map of all AI features + audit closure)
AIAUDIT_02                                   10 SYS-AISETTINGS (audit fixes 6-10: window cancellation, multi-vendor 404 fallback, token toasts, onboarding)
AIMODELS_02                                  10 SYS-AISETTINGS
AIMODELFALLBACK_02                           10 SYS-AISETTINGS
AITEMP_02                                    10 SYS-AISETTINGS
AISCHEMA_02                                  10 SYS-AISETTINGS
AIMUSIC_HONEST_01                            02 AUD-MUSICWIN
AIMUSIC_START_01                             02 AUD-MUSICWIN
AIMUSIC_PROGRESS_01                          04 UI-MUSICWIZARD
AIELIM_03                                    01 TL-AITRACKING | 04 UI-SETTINGS-ABOUT
AICAPTION_PREVIEWSUB_01                      03 FFM-CAPTIONS | 04 UI-AICOMMENTARY
AICAPTION_EXPORT_02                          03 FFM-CAPTIONS
AICAPTION_LAYOUT_02                          01 TL-CAPTIONS | 03 FFM-CAPTIONS
AICAPTION_MEMESHIFT_01                       03 FFM-CAPTIONS
AISETUP_02                                   04 UI-AIZOOM
AICONSENT_02                                 04 UI-AIZOOM | 10 SYS-AISETTINGS
AIVENDORCAPS_01                              10 SYS-AISETTINGS
AIPROFILE_ONE_01                             04 UI-AICOMMENTARY
AIREASONING_01                               10 SYS-AISETTINGS
AIZEROFRAMES_01                              10 SYS-AISETTINGS
AICOMMENTARY_ESTIMATE_01                     04 UI-AICOMMENTARY
AIUSAGE_01                                   10 SYS-AISETTINGS | 04 UI-AICOMMENTARY
AIAUDIT_04                                   10 SYS-AIAUDIT4
AIANSWER_01                                  10 SYS-AIAUDIT4
AICLAUDETOOL_01                              10 SYS-AIAUDIT4
AIREASONING_02                               10 SYS-AIAUDIT4
AIFPSFALLBACK_02                             10 SYS-AIAUDIT4 | 01 TL-AITRACKING
AIPREFLIGHT_03                               10 SYS-AIAUDIT4
AIREUSE_03                                   10 SYS-AIAUDIT4
AIREUSE_04                                   10 SYS-AIAUDIT4 | 05 SYS-STORAGE-TABLE
AIPARTRETRY_01                               10 SYS-AIAUDIT4 | 04 UI-AIAPPROVE
AICOMMENTARY_KEEP_01                         10 SYS-AIAUDIT4 | 04 UI-AICOMMENTARY
AICOMMENTARY_ONCE_01                         10 SYS-AIAUDIT4
AICOMMENTARY_CUTS_01                         10 SYS-AIAUDIT4
AIUSAGE_04                                   10 SYS-AIAUDIT4
AIAPPROVE_01                                 04 UI-AIAPPROVE | 10 SYS-AIAUDIT4
AIHUDTRANSPORT_01                            10 SYS-AIAUDIT4 | 01 TL-AIHUD
AIHUDERRORS_01                               10 SYS-AIAUDIT4 | 04 UI-AIHUDWAND
AITRACE_01                                   10 SYS-AIAUDIT4
AIELIM_04                                    10 SYS-AIAUDIT4 | 04 UI-AIZOOM
AIELIM_05                                    10 SYS-AIAUDIT4
AIELIM_06                                    01 TL-AITRACKING (Kill Tracker on-screen check)
AIELIMERR_01                                 10 SYS-AIAUDIT4 (Kill Tracker connection-check errors)
AIELIMOWNER_01                               10 SYS-AIAUDIT4 (scan owns its scanning state)
AIREFOWN_01                                  10 SYS-AIAUDIT4 (own task Reference)
AIMUSICDEADLINE_01                           04 UI-MUSICWIZARD
AIREWRITEKILLS_01                            04 UI-MERGER-KILLS
MUSICREMOVEUNDO_01                           07 UNDO-RULES (UNDO_20)
AIKEYHEALTH_01                               10 SYS-AIAUDIT4 (rejected keys blocked)
AIFIXFLOW_01                                 10 SYS-AISETTINGS / 04 UI-AIEASE (broken key = no key; account errors open the fix)
AINOCHARGE_01                                10 SYS-AISETTINGS (refused request: nothing was charged)
AISTATUSLINE_01                              04 UI-AIEASE (Settings AI status line + Test)
AIKEYSTARTUP_01                              10 SYS-AISETTINGS (startup key check)
AIFIXREASON_01                               10 SYS-AISETTINGS (country / model / daily limit get their own fix)
AIDAILYLIMIT_01                              10 SYS-AISETTINGS (Google free daily limit: wait until reset)
AIPRICEAGE_01                                10 SYS-AISETTINGS (old price table flagged)
AITHINKCAL_01                                10 SYS-AISETTINGS (thinking allowance measured from real requests)
LOCALAIPACK_02                               09 DIST-LOCALAIPACK (one-command pack build + verified publish)
AIGEMINIMODELS_02                            10 SYS-AIAUDIT4 (Google model list and prices)
AIELIMFAST_01                                10 SYS-AIAUDIT4 (faster Kill Tracker)
AIELIMASK_01                                 10 SYS-AIAUDIT4 (ask before a dear scan)
AIELIMRESCAN_01                              10 SYS-AIAUDIT4 (scan again)
AIMERGESTOP_01                               10 SYS-AIAUDIT4 | 04 UI-MERGER-KILLS
AIASKTOGGLE_01                               04 UI-AIAPPROVE
AIVIRALPCONLY_01                             10 SYS-AIAUDIT4
AICONNECTCLIP_01                             04 UI-AIEASE (one-click connect)
CHATGPTSIGNIN_01                             10 SYS-AIAUDIT4 | 04 UI-AIEASE | 05 SYS-STORAGE-TABLE
CHATGPTPLAN_01                               10 SYS-AIAUDIT4
KILLGATE_01                                  01 TL-AITRACKING
LOCALAI_01                                   10 SYS-LOCALAI | 04 UI-AIZOOM
LOCALAIPACK_01                               09 DIST-LOCALAIPACK | 10 SYS-LOCALAI
LOCALTRACK_01                                01 TL-LOCALTRACK
LOCALMEMORY_01                               01 TL-LOCALTRACK
LOCALTRACK_02                                01 TL-LOCALTRACK
LOCALTRACK_03                                01 TL-LOCALTRACK
NEWVIDEOCLEAN_01                             05 SYS-RECOVERY | 06 NEWVIDEOGUARD_01
AIZOOMHYST_01                                01 TL-LOCALTRACK
AIINTEG_01                                   10 SYS-AIINTEG
AIPCTRACK_01                                 10 SYS-AIINTEG
AICOMMBIGPARTS_01                            10 SYS-AIINTEG
CHATGPTAPPROVAL_01                           10 SYS-AIINTEG
AICLAUDEPLAN_01                              10 SYS-AIINTEG
AICLOUDGATE_01                               10 SYS-AIINTEG
AIBUSYFALLBACK_01                            10 SYS-AIINTEG
AIPREFLIGHTSESSION_01                        10 SYS-AIINTEG
AIFREEGATE_01                                10 SYS-AIINTEG
AIFREECOUNT_01                               10 SYS-AIINTEG
AIMODE_01                                    04 UI-AIMODE ("How should AI work?")
AIMODEHEAL_01                                04 UI-AIEASE
AIZOOMSTART_01                               04 UI-AIEASE | 04 UI-AIZOOM
AICAMSMOOTH_01                               01 TL-AITRACKING
OWNBODY_01                                   01 TL-LOCALTRACK | 01 TL-AITRACKING
AIPLACEMENT_01                               10 SYS-AISETTINGS (where AI work runs: local vs online)
SCENECUT_01                                  01 TL-LOCALTRACK
TEXTTARGET_01                                04 UI-AIZOOM  [retired → AskManualDescriptionAsync in AiSubjectPickerWindow]
FVS_LOCAL_AI_PACK_DIR                        10 SYS-LOCALAI (development only)
FVS_TEST_ORT_DIR                             10 SYS-LOCALAI (opt-in interop test)
FVS_TEST_LOCALAI_DIR                         10 SYS-LOCALAI (opt-in real-model test)
LOCALAITEST_01                               10 SYS-LOCALAI | README GOV-AVGATE
LocalAiPackFiles.Released                    09 DIST-LOCALAIPACK | 10 SYS-LOCALAI
AIMERGEKILLS_01                              04 UI-MERGER-KILLS
AIPROGRESS_02                                10 SYS-AIAUDIT4 | 04 UI-AIZOOM
AIPARITY_02                                  03 FFM-ZOOMGRAPH | 01 TL-AITRACKING
AICOMMENTARY_REMEMBER_01                     04 UI-AICOMMENTARY
VOAPPLY_02                                   05 SYS-WINSTATE (MPVSHUTDOWN_01 phases B1/B2) | 02 AUD-VOICEOVER
AISPEEDMAP_01                                01 TL-OUTPUTTIMELINE (DescribeSourceRange / PlaybackSpan)
BINPATH_01                                   03 FFM-BINPATH (unresolved search-order divergence)
AICAPABILITY_01                              10 SYS-AICAPABILITY | 04 UI-AIMODE
AIPICKERELLIPSE_01                           04 UI-AIZOOM | 10 SYS-AISETTINGS
AIPICKERELLIPSE_02                           04 UI-AIZOOM | 10 SYS-AISETTINGS
AIZOOMWIZARD_01                              04 UI-AIZOOM
AITRACKING_02                                01 TL-LOCALTRACK | 01 TL-AITRACKING
AskManualDescriptionAsync                    04 UI-AIZOOM | 10 SYS-AISETTINGS
AskCorrectionAsync                           04 UI-AIZOOM
AIMULTIBLOCK_01                              04 UI-AIZOOM | 07 UNDO-GRANULAR-APPLY
LOCALDISCOVER_01                             01 TL-LOCALTRACK | 10 SYS-LOCALAI
LOCALDETECT_01                               01 TL-LOCALTRACK | 10 SYS-LOCALAI
FitWizardWindowBounds / AiSetupWizardBounds  04 UI-AIEASE | 05 SYS-WINSTATE
AiModeBounds                                 04 UI-AIMODE | 05 SYS-WINSTATE
ToolNavigationResult.ExitApplication         08 COMP-ROOT | 05 SYS-RECOVERY (DONECLEAN_01)
AiViralPostHelperEnabled                     04 UI-MERGER-MULTISELECT | 04 UI-AIZOOM | 10 SYS-AISETTINGS

Code-only tags (were in src/ or build/ comments, in no spec text; since NOCOMMENTS_02 their text is in docs/CODE_NOTES_*.md; route = spec(s) whose text names the file; 'unrouted' = no spec names it):
AIUSAGE_03                                   [code-only: App/Services/AiCostEstimator.cs] 04 05
AIZOOMUX_01                                  [code-only: App/Controls/ConfirmDialogWindow.axaml.cs] 04
ALWAYSLIVE_01                                [code-only: App/CropToolWindow.axaml] 04 08
ANCHOR_01                                    [code-only: App/Controls/FloatingNotice.cs +1] 04
ATOMICSTATE_01                               [code-only: App/Infrastructure/GranularEditorVisuals.cs +3] unrouted
AUDIOCHK_01                                  [code-only: Core/Media/AudioFilterChain.cs] 02
AUDIO_01                                     [code-only: App/UiSoundEffect.cs +2] 02
AUDIO_02                                     [code-only: App/UiSoundEffect.cs] 02
AUDIO_03                                     [code-only: App/UiSoundEffect.cs] 02
AUDIO_04                                     [code-only: App/UiSoundRouter.cs] unrouted
AUDIO_05                                     [code-only: App/UiSoundEffect.cs] 02
AUDIO_06                                     [code-only: App/Controls/SettingsWindow.axaml +2] 04 05
AUDIO_07                                     [code-only: App/UiSoundRouter.cs] unrouted
AUDIO_08                                     [code-only: App/FreeVideoStudio.App.csproj +1] 05 06 09
AUDIO_09                                     [code-only: App/Infrastructure/SettingsManager.cs +3] 05
AUDIO_10                                     [code-only: App/UiSoundRouter.cs] unrouted
AUDIO_11                                     [code-only: App/UiSoundRouter.cs] unrouted
AUDIO_12                                     [code-only: App/UiSoundEffect.cs] 02
AUDIO_13                                     [code-only: App/UiSoundEffect.cs] 02
AUDIO_14                                     [code-only: App/UiSoundEffect.cs] 02
AUTOPREVIEW_01                               [code-only: App/VideoMergerWindow.MusicPreview.cs +2] 01 02 03 04 05 06 08
BANDCONTRAST_01                              [code-only: App/CropToolWindow.axaml.cs +1] 08
BEDSEG_01                                    [code-only: Core/Media/AudioLoudnessProbe.cs] 02
BOUNDS_01                                    [code-only: App/WindowBoundsHelper.cs] 04 05
BTNDEPTH_01                                  [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
BTNHUE_01                                    [code-only: App/AvaloniaApp.axaml] 02 04 05 08
CANCEL_01                                    [code-only: App/Infrastructure/VoiceOverAudioTools.cs] unrouted
CAPTIONWIPE_01                               [code-only: App/Controls/SettingsWindow.axaml +2] 04 05
COLORMATH_01                                 [code-only: App/Infrastructure/ColorMath.cs] unrouted
CONFIGVAL_01                                 [code-only: Core/Media/HudConfig.cs] 01
CROPCHK_01                                   [code-only: Core/Media/HudConfig.cs] 01
CROPGEOM_01                                  [code-only: App/Infrastructure/CropGeometry.cs] unrouted
CUTS_03                                      [code-only: App/MusicWizardWindow.axaml.cs] 01 02
DEDUPE_02                                    [code-only: Core/Media/EncoderManager.cs] 03
DETACH_01                                    [code-only: App/GranularSpeedEditorWindow.axaml +7] 01 04 05 07 08
DIALOG_02                                    [code-only: App/Controls/ConfirmDialogWindow.axaml.cs +1] 04
DOWNLOAD_01                                  [code-only: App/MemeCatalog.cs +1] 03
DRAGFREE_01                                  [code-only: App/CropToolWindow.axaml.cs] 08
DRIFT_01                                     [code-only: Core/Media/CoordinateMath.cs] 01
DUCKNAME_01                                  [code-only: App/MusicWizardWindow.axaml] 01 02 04
EDIT3_01                                     [code-only: App/Controls/ConfirmDialogWindow.axaml.cs +3] 04
EDIT3_02                                     [code-only: App/MainWindow.axaml.cs] 01 02 04 08
EMPTYSAVE_01                                 [code-only: App/CropToolWindow.axaml.cs +1] 08
FFMPEGSTOP_01                                [code-only: Core/Infrastructure/CooperativeShutdownGate.cs +3] 05
FOCUS_01                                     [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
FREEZEDIAG_02                                [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
FREEZEDIAG_03                                [code-only: App/MpvVideoView.cs] 04 05
FREEZEDIAG_04                                [code-only: App/MpvVideoView.cs] 04 05
FREEZEDIAG_05                                [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
FREEZE_CLEAR_01                              [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
GRAB_01                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
GRANVIS_01                                   [code-only: App/Infrastructure/GranularEditorVisuals.cs] unrouted
GUIDE_01                                     [code-only: App/Controls/CoachOverlay.cs +2] 04
HITBOX_01                                    [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
HITBOX_02                                    [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
IDEA_01                                      [code-only: App/MainWindow.axaml] 01 02 04 08
IDEA_02                                      [code-only: App/MainWindow.axaml] 01 02 04 08
IDEA_03                                      [code-only: App/MainWindow.axaml] 01 02 04 08
IDEA_04                                      [code-only: App/MainWindow.axaml] 01 02 04 08
IDEA_05                                      [code-only: App/MainWindow.axaml] 01 02 04 08
IDEA_07                                      [code-only: App/MainWindow.axaml] 01 02 04 08
IDEA_10                                      [code-only: App/MainWindow.axaml] 01 02 04 08
INSTALL_02                                   [code-only: App/DeploymentLifecycle.cs] 05
ISSUE_06                                     [code-only: App/AvaloniaApp.axaml +10] 02 04 05 08
ISSUE_07                                     [code-only: App/CropToolWindow.axaml +5] 04 08
ISSUE_08                                     [code-only: App/AvaloniaApp.axaml +8] 02 04 05 08
ISSUE_09                                     [code-only: App/AvaloniaApp.axaml +14] 02 04 05 08
ISSUE_10                                     [code-only: App/AvaloniaApp.axaml +10] 02 04 05 08
ISSUE_12                                     [code-only: App/Controls/PhaseOverlayControl.axaml +4] unrouted
ISSUE_13                                     [code-only: App/AvaloniaApp.axaml +7] 02 04 05 08
ISSUE_14                                     [code-only: App/AvaloniaApp.axaml +4] 02 04 05 08
ISSUE_15                                     [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
ISSUE_16                                     [code-only: App/AvaloniaApp.axaml] 02 04 05 08
ISSUE_21                                     [code-only: App/AvaloniaApp.axaml] 02 04 05 08
ISSUE_23                                     [code-only: App/AvaloniaApp.axaml] 02 04 05 08
ISSUE_27                                     [code-only: App/AvaloniaApp.axaml] 02 04 05 08
ISSUE_30                                     [code-only: App/AvaloniaApp.axaml] 02 04 05 08
ISSUE_34                                     [code-only: App/Controls/TimelineLanesControl.axaml] 01
ISSUE_35                                     [code-only: App/MainWindow.axaml] 01 02 04 08
KEYCASE_01                                   [code-only: Core/Editing/CropConfigJson.cs +3] 05
LANES_02                                     [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
LANES_03                                     [code-only: App/Controls/TimelineLanesControl.axaml +3] 01
LANES_04                                     [code-only: App/MusicWizardWindow.axaml] 01 02 04
LAYOUT_02                                    [code-only: App/GranularSpeedEditorWindow.axaml +1] 01 04 05 07 08
LEAK_01                                      [code-only: App/ThumbnailStripGenerator.cs] 03 04
LEAK_02                                      [code-only: App/Infrastructure/VoiceOverAudioTools.cs] unrouted
LEVEL_01                                     [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
LOOP_01                                      [code-only: App/MainWindow.PreviewAudio.cs +1] 01 02 03 04 05 06 07 08
MARKER_01                                    [code-only: App/Controls/TimelineLanesControl.axaml +2] 01
MEMECOMBO_01                                 [code-only: App/MainWindow.axaml.cs] 01 02 04 08
MEMESCAN_01                                  [code-only: App/Infrastructure/MemeDimensionCache.cs +1] unrouted
MEMESWAP_01                                  [code-only: App/GranularSpeedEditorWindow.MemeBands.cs +3] 04
MEME_02                                      [code-only: App/Infrastructure/MemeAssets.cs +4] 03
MEME_03                                      [code-only: App/Models/ExportPayload.cs +2] 03 05
MEME_04                                      [code-only: Core/Media/OutputTimeline.cs] 01 06
MEME_05                                      [code-only: Core/Media/OutputTimeline.cs +1] 01 06
MEME_08                                      [code-only: App/GranularSpeedEditorWindow.MemeBands.cs +1] 04
MEME_09                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
MERGERBOTTOM_01                              [code-only: App/AvaloniaApp.axaml +2] 02 04 05 08
MERGERESTIMATE_01                            [code-only: App/VideoMergerWindow.axaml.cs] 04
MERGERESTIMATE_02                            [code-only: App/VideoMergerWindow.axaml] 04
MERGERMAIN_01                                [code-only: App/VideoMergerWindow.axaml] 04
MERGERQUEUE_01                               [code-only: App/VideoMergerWindow.axaml] 04
MERGERQUEUE_02                               [code-only: App/VideoMergerWindow.axaml] 04
MERGERRETURN_01                              [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
MERGERROWS_01                                [code-only: App/VideoMergerWindow.axaml] 04
MINLEN_01                                    [code-only: Core/Editing/GranularEditSession.Segments.cs +1] 01
MPVQUEUE_01                                  [code-only: Core/Media/MpvCommandQueue.cs +1] unrouted
MPVSAFETY_01                                 [code-only: Core/Media/MPVSafetyManager.cs] unrouted
MWDRAW_01                                    [code-only: App/Infrastructure/MusicWizardDraw.cs] unrouted
NODUPES_02                                   [code-only: App/CropToolWindow.axaml.cs +2] 08
NOMASK_01                                    [code-only: App/CropToolWindow.axaml.cs +7] 08
NOMASK_02                                    [code-only: App/CropToolWindow.axaml.cs] 08
OVERLAYHOST_01                               [code-only: App/Controls/CoachOverlay.cs +2] 04
PEAKMATH_01                                  [code-only: App/Infrastructure/AudioPeakMath.cs] unrouted
POPSICLE_01                                  [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
PREVIEW1_02                                  [code-only: App/MusicWizardWindow.axaml.cs] 01 02
PREVIEWFAULT_01                              [code-only: App/VideoMergerWindow.Preview.cs +1] 01 02 03 04 05 06 08
PREWARM_01                                   [code-only: App/MainWindow.axaml.cs +1] 01 02 04 08
PROBE_01                                     [code-only: Core/Media/ProcessWorker.cs] 03
QUEUEFRAME_01                                [code-only: App/VideoMergerWindow.axaml] 04
QUEUEID_01                                   [code-only: App/VideoMergerWindow.ClipActions.cs] 04
QUEUEMENU_01                                 [code-only: App/VideoMergerWindow.axaml +1] 04
QUEUEOCCURRENCE_01                           [code-only: App/VideoMergerWindow.ClipActions.cs +1] 04
RECOVERYDOC_02                               [code-only: Core/Project/RecoveryEnvelope.cs] 05 06
RECOVERYDOC_03                               [code-only: Core/Project/RecoveryEnvelope.cs] 05 06
RECOVERYDOC_04                               [code-only: App/Services/ProjectRecoveryService.cs] 05
RECOVERYDOC_06                               [code-only: App/Services/ProjectRecoveryService.cs] 05
RECOVERYDOC_07                               [code-only: App/Services/ProjectRecoveryService.cs] 05
RECOVERYDOC_08                               [code-only: App/Services/LegacyRecoveryMigrator.cs] 05 06
RECOVERYDOC_09                               [code-only: App/Services/ProjectSession.cs] 06 07 08
RECOVERYDOC_10                               [code-only: App/MainWindow.Restore.cs] 05 06
RECOVERY_02                                  [code-only: Core/Infrastructure/ApplicationPaths.cs +1] 05
RECOVERY_04                                  [code-only: App/MainWindow.axaml.cs] 01 02 04 08
RECOVERY_05                                  [code-only: Core/Infrastructure/RecoveryManager.cs] 02 05
RESIZEFEEL_01                                [code-only: App/CropToolWindow.axaml.cs +2] 08
RETURN_01                                    [code-only: App/MainWindow.axaml.cs] 01 02 04 08
SCHEMALEGACY_01                              [code-only: App/Infrastructure/SettingsManager.cs] 05
SCISSORS_01                                  [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
SILENTRESET_01                               [code-only: Core/Ipc/CropConfigStore.cs] 05
SLIDER_01                                    [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
SLIDER_03                                    [code-only: App/MusicWizardWindow.axaml] 01 02 04
SLIDER_04                                    [code-only: App/AvaloniaApp.axaml] 02 04 05 08
SLIDER_05                                    [code-only: App/MusicWizardWindow.axaml] 01 02 04
SPACING_01                                   [code-only: App/MainWindow.axaml] 01 02 04 08
SPEEDLABEL_01                                [code-only: App/Infrastructure/SpeedLabel.cs +2] unrouted
STRIP_03                                     [code-only: App/Services/FilmstripPrewarm.cs +1] 03
TELEMETRY_01                                 [code-only: App/Controls/HardwareTelemetrySampler.cs +1] 06
TEXTCLIP_01                                  [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
TEXTGEN_01                                   [code-only: Core/Media/TextOverlayGenerator.cs] 03
THEME_01                                     [code-only: App/Infrastructure/ThemeResources.cs] 04
THROTTLE_01                                  [code-only: App/Services/FilmstripPrewarm.cs +2] 03
TIMELINEDRAW_01                              [code-only: App/MainWindow.Canvas.cs +1] 01
TIME_01                                      [code-only: App/MusicWizardWindow.axaml.cs +2] 01 02
TIME_02                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
TONE_01                                      [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
TOOLNAV_05                                   [code-only: App/CropToolWindow.Navigation.cs +4] 01 02 04 05 07 08
TRACKSEARCH_01                               [code-only: App/Infrastructure/TrackSearch.cs] unrouted
UISTATE_01                                   [code-only: App/Services/InProcessSessionState.cs] 05
UNDOHINT_01                                  [code-only: App/GranularSpeedEditorWindow.History.cs +1] 04 07
UNDO_02                                      [code-only: App/GranularSpeedEditorWindow.History.cs +3] 04 07
UNITS_01                                     [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
UPGRADE_01                                   [code-only: Core/Infrastructure/LegacyProductIdentity.cs] 09
UPGRADE_03                                   [code-only: Core/Infrastructure/DirectoryUpgrade.cs] 05
UPGRADE_06                                   [code-only: Core/Infrastructure/InstallPayload.cs +1] 05 09
UPGRADE_08                                   [code-only: App/Services/UpgradeRegistration.cs] 05
UPGRADE_09                                   [code-only: App/Services/UpgradeInstallWorker.cs] 03 05
UXQA_01                                      [code-only: App/MainWindow.axaml.cs +2] 01 02 04 08
UXQA_02                                      [code-only: App/GranularSpeedEditorWindow.axaml +1] 01 04 05 07 08
UXQA_04                                      [code-only: App/VideoMergerWindow.axaml] 04
VOAPPLY_04                                   [code-only: build/sentinels.txt] 05 06 08
VOASYNC_01                                   [code-only: App/Services/VoiceCaptureSession.cs +1] 02
VODIAG_01                                    [code-only: App/Services/VoiceCaptureSession.cs] 02
VOICE_02                                     [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
VOLEVEL_BIND_01                              [code-only: App/ViewModels/VoiceOverGainViewModel.cs] 02 04 08
VOMON_01                                     [code-only: App/Services/VoiceCaptureSession.cs +2] 02
VOPROT_02                                    [code-only: App/Infrastructure/SettingsManager.cs +1] 05
VOROW_01                                     [code-only: App/VoiceOverWindow.axaml +1] 01 02
VOTAKE_01                                    [code-only: App/Infrastructure/VoiceOverAudioTools.cs +2] unrouted
VOTL_01                                      [code-only: App/Infrastructure/VoiceOverAudioTools.cs +2] unrouted
VOTOOLS_01                                   [code-only: App/Infrastructure/VoiceOverAudioTools.cs] unrouted
WHEELDRAG_01                                 [code-only: App/CropToolWindow.axaml.cs] 08
WHEEL_01                                     [code-only: App/Controls/SpinningWheelSlider.cs] 04
ZCOLLIDE_01                                  [code-only: App/CropToolWindow.axaml.cs +1] 08
ZEROSCALE_01                                 [code-only: Core/Media/HudConfig.cs] 01
ZOOMKEEP_01                                  [code-only: App/CropToolWindow.axaml.cs] 08
ZOOMLABEL_01                                 [code-only: App/CropToolWindow.axaml] 04 08
ZOOMLIVE_01                                  [code-only: App/AvaloniaApp.axaml +1] 02 04 05 08
ZOOMLIVE_02                                  [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOMLIVE_03                                  [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOMLIVE_04                                  [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOMLIVE_05                                  [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
ZOOMLIVE_06                                  [code-only: App/VoiceOverWindow.axaml.cs] 01 02
ZOOMMAP_01                                   [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOMPOP_01                                   [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOM_02                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOM_03                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOM_04                                      [code-only: App/GranularSpeedEditorWindow.axaml +2] 01 04 05 07 08
ZOOM_05                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
ZOOM_06                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOM_07                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs] 01 04 05 07 08
ZOOM_09                                      [code-only: App/GranularSpeedEditorWindow.axaml.cs +1] 01 04 05 07 08
ZTIEBREAK_01                                 [code-only: Core/Editing/CropEditSession.cs] 07
```


## Test audit and regression routes

| File, symbol or tag | Specification |
| :--- | :--- |
| `tests/TestHarness.ps1`, `TestHarness.Tests.ps1`, `run-av-tests.ps1`, `run-all-tests.cmd`, `TESTGATE_01`, `TESTCLEAN_01` | `README.md` GOV-AVGATE; `08` COMP-ARCHTEST; `05` SYS-STORAGE-TABLE |
| `MERGERPROBE_01` (`VideoMergerWindow.axaml.cs` `_ffprobePath`, `MergerSmokeApp.cs`) | `03` FFM-BINPATH; `04` UI-MERGERQUEUE |
| `tests/TestArtifactDirectory.cs`, `TESTCLEAN_02` | `05` SYS-STORAGE-TABLE; `08` COMP-ARCHTEST |
| `TESTOUT_01`, `logs\<Suite>_Results`, `TestOutputSuite.cs`, `Enter-SuiteOutput`, `SuiteWork`, `VSTestResultsDirectory` | `README.md` GOV-TESTOUT (the ONLY test-output folder); `05` SYS-STORAGE-TABLE |
| `tests/DesktopPreviewSmoke/Program.cs`, `MergerSmokeApp.cs`, `TESTDESKTOP_01`, `MERGERBOUNDARY_01` | `04` UI-GPUPRESENT2, UI-MERGERQUEUE; `01` TL-COMPOSITE; `08` COMP-ARCHTEST |
| `ProbeRegressionTests.cs`, `S37_SeekEvidenceTests.cs`, `S38_MutedAndDeletedMemeTests.cs`, `NativeTestBootstrap.cs` | `08` COMP-ARCHTEST; `02` AUD-IPCSTATE; `03` FFM-MEMECONCAT |
| `FinalOutputCommit.cs`, `FinalOutputCommitTests.cs` | `03` FFM-EXPORTLIFETIME |
| `VoiceOverGainViewModel.cs` | `02` AUD-VOICEOVER; `04` UI-THEME; `08` COMP-MVVM |
| `MERGERGAPLESS_01`, `MergerRig.PlayerOptions` | `01` TL-COMPOSITE; `02` AUD-IPCSTATE |

## Rebrand identity and verification routes

| File, symbol or tag | Specification |
| :--- | :--- |
| `FreeVideoStudio.sln`, application/core assembly identities, `FreeVideoStudio.exe`, retired `FreeVideoStudio.App.update.zip` | `09` DIST-IDENTITY |
| `REBRAND_01`, `AppDataDir`, `LocalCacheDir`, `MigrateDirectory`, `FvsFreeVideoStudioMutex` | `05` SYS-REBRAND |
| `NOSPACE_01`, `LegacyInstallFolder`, `KnownDestinations`, `ReuseRoot`, `RetargetUserLinksAsync` | `05` SYS-NOSPACE |
| `MEMEFOLDER_01`, `MEMEFOLDER_02`, `CloudMemeFolder`, `MemeCategory`, `MEMECAT_01`, `MEMESYNC_01`..`03`, `STARTERLIST_01`, `ResolveSavedFile` | `03` FFM-MEMELIB |
| `MEMEPICK_01`, `MEMEPICK_02`, `MEMEWALL_01`, `MEMEMUSIC_01`, `MemeThumbnailCache`, `CACHEBOUND_02`, `CACHEBOUND_03`, `CACHEBOUND_05`, `OwnedLruCache`, `ViewportThumbnailLoader` | `04` UI-MEMESELECT |
| `REBRAND_02`, `LegacyResidueSweep`, `RunForCurrentUser`, `LegacyTempNames`, `LegacyStorageNames` | `05` SYS-REBRAND-SWEEP |
| `REBRAND_03`, `GitHubReleasePublisher.ReleaseAssets`, previous-brand release alias | `09` DIST-SPLIT |
| Old→new identity map, old-brand update flow, allow list | `REBRAND_MIGRATION.md` |
| `ProductionNamespacesUseTheProductRoot` | `08` COMP-ARCHTEST |
<!-- WGLUNBIND_01: MpvVideoView.cs, Interop/WglInterop.cs and Interop/NativeTeardownInvoker.cs — 05 SYS-WINSTATE; WglInterop.cs also 04 UI-GPUPRESENT2. -->

| `CLEANUPBOUND_01`, `SafeDirectoryCleanup` | `05` SYS-AVCLEAN |
| `LEGACYKEEP_01` | `05` SYS-REBRAND-SWEEP |
| `PROJECTIO_01` | `06` PROJ-DISK / PROJ-CLOSEGUARD; `07` UNDO-STATE; `08` COMP-FAULTS |
AISEGMENTONLY_01 / AIHUDCAMERA_01 / AIPREVIEW_01 01 TL-AITRACKING | 04 UI-AIZOOM | 10 SYS-AISETTINGS

MUSICBROWSE_01 / MusicBrowserTests.cs / PlayChosenSongAsync / ProceedWithSong — 04 UI-MUSICWIZARD | 02 AUD-DIALOGS (midpoint audition, stale-load guards, ±20s, no right pane).

NAUDIO3OUTPUT_01                             02 AUD-IPCSTATE | 05 SYS-WINSTATE (NAudio 3 event-driven WaveOut, background disposal).
