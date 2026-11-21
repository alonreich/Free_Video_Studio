# REPOSITORY MAP  {#GOV-REPOMAP-FILE}

Where things live. Paths are relative to the repository root. Rules and reading order: [`README.md`](README.md) §0.
File → spec routing: [`INDEX.md`](INDEX.md) §1. Keep this map in step with the code (README §0.3 rule 3).

Session lifecycle:    new video over unsaved edits -> three-way question (NEWVIDEOGUARD_01, MainWindow.Project.cs);
                      after PROCESS / MERGE with no edit since -> close without asking, next start empty
                      (DONECLEAN_01: MainWindow.Project.cs, VideoMergerWindow.Session.cs; companion tool clean exit via
                      Services\ToolLaunchContracts.cs ToolNavigationResult.ExitApplication -> ToolNavigator.cs -> MainWindow.MarkExportedCleanExitForTool);
                      crash restore keeps playhead, kill marks and audio choices (RECOVERYFULL_01: MainWindow.Recovery.cs / MainWindow.Restore.cs);
                      a new video starts clean: no captions, cuts or memes survive (NEWVIDEOCLEAN_01, tests NewVideoCleanTests.cs).
                      Spec: docs/05 SYS-RECOVERY, docs/06 NEWVIDEOGUARD_01, docs/08 COMP-ROOT.

```text
Solution:             .\FreeVideoStudio.sln
Application project:  .\src\FreeVideoStudio.App\FreeVideoStudio.App.csproj
Core project:         .\src\FreeVideoStudio.Core\FreeVideoStudio.Core.csproj
Core layout:          Core\Project\ (.fvsproj model, 06) · Core\Undo\ (UndoStack, 07) · Core\Editing\ (edit state, 07 UNDO-EDITSTATE)
                      Core\Ipc\ (named pipes, PipeAccess PIPEAUTH_01, 05 SYS-IPCLIFETIME) · Core\Abstractions\ + App\Abstractions\ (IFaultSink, seams, 08)
                      App\Interop\ (D3D11/WGL interop, 04 UI-GPUSLOT)
Application tests:    .\tests\FreeVideoStudio.App.Tests\FreeVideoStudio.App.Tests.csproj
Core tests:           .\tests\FreeVideoStudio.Core.Tests\FreeVideoStudio.Core.Tests.csproj
FvsVerify tests:      .\tests\FvsVerify.Tests\FvsVerify.Tests.csproj (sentinel checker; CI)
Media checks:         .\tests\MediaPipelineChecks (console: real FFmpeg/GPU export checks; README.md inside)
Voice-over smoke:     .\tests\VoiceOverNativeSmoke (console: native voice capture smoke)
AI bake-off tool:     .\developer_tools\AiBakeOff\AiBakeOff.csproj (AIBAKEOFF_01: developer-only console comparing AI vendors on subject
                      discovery, dense tracking and HUD detection; keys from environment variables only;
                      not in the solution, not shipped)
A/V regression gate:  .\tests\run-av-tests.cmd -> .\tests\FreeVideoStudio.AvRegression.Tests (AVREG_01, section 0.3a):
                      45 scenarios S01-S45 on the real player (PlayerRig/PlaybackProbe) and real exports
                      (ExportRig/Verify/MergerRig) of generated marker media; output in logs\AV_Results (git-ignored).
                      TestHarness.ps1 validates nonempty, complete TRX results and required scenarios; skipped tests fail the gate.
                      TestHarness.Tests.ps1 guards parsing, exclusive runs and allowlisted output cleanup.
A/V gate runner:      .\tests\run-av-tests.ps1 (called by run-av-tests.cmd)
Test output:          .\logs\<Suite>_Results\ ONLY (console.log, <Suite>.trx, work\), one latest copy per suite;
                      verdict in .\logs\Gate_Results\LAST_RESULT.txt (TESTOUT_01, README.md section 0.3b).
                      Helpers: .\tests\TestArtifactDirectory.cs (C#), Enter-SuiteOutput in .\tests\TestHarness.ps1.
All-suite runner:     .\tests\run-all-tests.cmd adds MediaPipelineChecks exports/decoder soak and DesktopPreviewSmoke.
Desktop preview:      .\tests\DesktopPreviewSmoke (real WGL/D3D pixels, seek/resize/detach/shutdown;
                      --merger runs the actual VideoMergerWindow across three clips added during playback;
                      --silent-middle also covers a clip without audio). Requires an unobscured interactive Windows desktop.
                      Output: logs\DesktopPreview_Results, logs\MergerDesktop_Results, logs\MergerSilentDesktop_Results.
Export save retry:    .\src\FreeVideoStudio.Core\Media\FinalOutputCommit.cs (03 FFM-EXPORTLIFETIME).
Voice gain binding:   .\src\FreeVideoStudio.App\ViewModels\VoiceOverGainViewModel.cs (02 AUD-VOICEOVER, 04 UI-THEME, 08 COMP-MVVM).
Development runtime:  .\dev.cmd -> build\DevRun.ps1 + build\DevSandbox.ps1; .dev-run\app (incremental Release managed; `dev fresh`/`dev rebuild` for full clean).
                      Core\Infrastructure\DevelopmentEnvironment.cs isolates paths/IPC and blocks installation.
                      Storage defaults and retention: 05 SYS-STORAGE-TABLE (STORAGEPATHS_01). Shared Music/Memes/Downloads; separate TEMP voice files.
                      Helper TEMP: fresh launch GUID folder; locked leftovers deferred (DEVHELPERTEMP_01).
                      Shared production staging: FvsBuild --dev-runtime (DEVPARITY_01); 05 SYS-DEVBUILD.
                      Tests: DevelopmentEnvironmentTests.cs, DevelopmentLaunchTests.cs, tests\DevHarness.Tests\DevSandbox.Tests.ps1.
Local packaging:      .\dev_build.cmd
CI:                   .\.github\workflows\ci.yml (COMP-CI, Release records), lfs-guard.yml (DIST-REPOWEIGHT),
                      upgrade-smoke.yml (UPGRADESMOKE_01). Specs: docs/08 COMP-CI, docs/09 DIST-CIWATCH.
Root config:          .\version.txt, .\Directory.Build.props, .\.agignore
Comment removal:      .\developer_tools\CommentRemover\CommentRemover.csproj (folder name differs from the launcher
                      so tab completion of Fin... finds the .cmd)
                      (NOCOMMENTS_02/03; run: .\developer_tools\Find_and_Remove_Comments.cmd from anywhere or double-click it
                      (launcher: cd to the repo root, dotnet run -c Release, pauses only when opened from Explorer,
                      returns the tool's exit code); no arguments; lists every comment (full path, line, text) and asks Y/N
                      before writing; exits 0 done or nothing to do / 1 refused / 2 answered N;
                      all code languages + extra blank lines; Program.cs, Runner.cs, TextRewriter.cs, Languages.cs,
                      CommentScanner.cs (C#), YamlLanguage.cs, ExternalTokenizers.cs (PowerShell/Python);
                      not in the solution, not shipped; removed text in .\docs\CODE_NOTES_App.md, CODE_NOTES_Core.md,
                      CODE_NOTES_Tests.md, CODE_NOTES_Tools.md; tests ArchitectureRuleTests.NoCSharpFileContainsAComment,
                      .\tests\FreeVideoStudio.App.Tests\FvsCommentsToolTests.cs)
Developer helpers:    .\developer_tools\ holds EVERY developer-only tool (DEVTOOLS_01; there is no tools\ folder):
                      BuildLog.ps1, Manual_UnInstall.bat, Redundant_Code_Finder.py, SetConsoleFont.ps1,
                      Pull_Code_files.py, AiBakeOff\, Find_and_Remove_Comments.cmd -> CommentRemover\, local_ai_pack\
                      (developer-only; not shipped; no spec)
Verification tool:    .\build\FvsVerify\FvsVerify.csproj; .\dev.cmd verify checks without launch cleanup (DEVVERIFY_01)
Sentinel registry:     .\build\sentinels.txt
AI source exporter:   .\developer_tools\Pull_Code_files.py (CODEEXPORT_01; docs/05 SYS-CODEEXPORT).
                      At most ten attachments in a new dated Downloads\free_video_studio subfolder;
                      full text/index, installer and tests, explicit omissions and secret filtering;
                      binary placeholder blocks, git-ignored paths skipped, CODE_NOTES opt-in (CODEEXPORT_02).
                      Known legacy song attribution decoded with its declared encoding; other errors reported.
                      Tests: .\tests\DeveloperTools.Tests\test_pull_code_files.py (Python unittest; CI).
Codec binaries:       .\binaries\ (ffmpeg/ffprobe/mpv, libmpv-2 + av* DLLs; LFS; search order 03 FFM-BINPATH;
                      staged by .\build\FvsBuild\Staging.cs)
Dev signing:          .\ssl-certificate\ (dev root/leaf scripts only; keys and passwords never tracked,
                      05 SIGNLOCAL_01 / SECRETSCAN_01)
Standalone output:    .\compiled\FreeVideoStudio.exe  (Inno Setup package, NODROPPER_01; holds exactly one local installer; GitHub retains latest 5 releases)
Installer script:     .\build\installer\FreeVideoStudio.iss  (docs/09_DISTRIBUTION_AND_RELEASE.md, DIST-INSTALLER)
App icon:             .\assets\icons\FreeVideoStudio.ico (APPICON_01, docs/09 DIST-IDENTITY)
AV hardening:         NODROPPER_01, NOTEMPEXEC_01, PEIDENTITY_01, UPDATECONSENT_01, NOHIDDENTOOLS_01, NOLOGONHOOK_01,
                      NOMEMREAD_01 (.\src\FreeVideoStudio.Core\Infrastructure\ProcessImage.cs), OPENWITHHOME_01, NOMUIWIPE_01,
                      NOLOCALPORT_01, NOLURKER_01 (docs/05 SYS-AVCLEAN); SIGNDEPS_01, FREETRUST_01, SIGNPATH_01 (docs/09 SYS-SIGNING)
Licence & policies:   .\LICENSE (GPL-3.0), .\PRIVACY.md, .\CODE_SIGNING_POLICY.md, .\THIRD_PARTY_NOTICES.md (SIGNPATH_01)
Controlled folders:   .\src\FreeVideoStudio.Core\Infrastructure\ControlledFolderAccess.cs (read-only) +
                      .\src\FreeVideoStudio.App\Infrastructure\ProtectedFolderGate.cs (CFASAFE_01, docs/09 SYS-CFASAFE)
Release gate:         .\build\FvsBuild\ReleaseGate.cs (RELGATE_01: clean + pushed + linted notes + green CI + local
                      tests (RELQA_01) + protected root key (ROOTKEY_01) + typed PUBLISH, version commit + tag, undo
                      on failure; --resume finishes a failed publish, RELRESUME_01) -> GitHubReleasePublisher.cs
                      (--verify-tag, RETAIN_01/02 old files removed, pages + notes kept, BETA_01 pre-releases).
                      Withdrawn signers: .\build\revoked-signers.txt (REVOKE_01). Upgrade smoke test:
                      .\.github\workflows\upgrade-smoke.yml (UPGRADESMOKE_01). Spec: docs/09 DIST-RELEASEGATE.
Specs (docs/):        01 timeline · 02 audio · 03 FFmpeg export · 04 UI · 05 lifecycle & storage · 06 project document ·
                      07 undo · 08 composition · 09 distribution, signing, updates & install · 10 AI features
                      (routing: README.md §3, INDEX.md).
Release notes:        .\docs\changes.md (technical, '## Unreleased') -> .\docs\CHANGELOG.md (users, '## Upcoming');
                      older releases in .\docs\CHANGELOG_ARCHIVE.md / changes_ARCHIVE.md (RELRECORD_01);
                      parser/lint/promotion .\src\FreeVideoStudio.Core\Infrastructure\ReleaseChangelog.cs
                      (RELNOTES_01..03). Spec: docs/09 DIST-RELEASENOTES.
Update trust:         elevated worker runs only from a protected %ProgramData% copy:
                      .\src\FreeVideoStudio.App\Services\ProtectedWorkerStage.cs (WORKERSTAGE_01, docs/09
                      SYS-UPGRADE-TRUST); Debug-only unsigned override (UNSIGNEDDEV_01); asset hashes + capped
                      decompression (ASSETHASH_01); per-drive space check (UPDATESPACE_01); published patch
                      bases (DELTABASE_01); 6-hourly re-check (RECHECK_01). Tests: ReleaseHardeningTests.cs,
                      ReleaseChangelogTests.cs.

Product display name: Free Video Studio
Naming rule:          Display text uses "Free Video Studio". The installation folder,
                      executable and namespaces use "FreeVideoStudio".
                      Registry identifiers are defined individually by DeploymentFootprint;
                      the application and uninstall keys retain "Free Video Studio".
Application assembly: FreeVideoStudio (root namespace: FreeVideoStudio.App)
Core assembly:        FreeVideoStudio.Core (root namespace: FreeVideoStudio.Core)
Internal tooling retains FvsBuild, FvsVerify and the FVS environment variables.
GitHub repository:    alonreich/Free_Video_Studio (updater + meme sync; old address redirects)
Rebrand record:       docs/REBRAND_MIGRATION.md (old->new map, update flow, output naming)
Previous-brand guard: tests\FreeVideoStudio.Core.Tests\RebrandTests.cs (old name allowed ONLY in
                      LegacyAppDataNames.txt and docs/REBRAND_MIGRATION.md)
Cleanup boundary:     .\src\FreeVideoStudio.Core\Infrastructure\SafeDirectoryCleanup.cs
                      + tests\FreeVideoStudio.Core.Tests\SafeDirectoryCleanupTests.cs (05 CLEANUPBOUND_01).
Temp cleanup:         .\src\FreeVideoStudio.Core\Infrastructure\TempHousekeeper.cs (startup/exit junk cleanup, TEMPCLEAN_01)
                      + tests\FreeVideoStudio.Core.Tests\TempHousekeeperTests.cs.
Project persistence:  ProjectSession.cs ordered background save/load queue;
                      tests\FreeVideoStudio.App.Tests\AutosaveBackgroundTests.cs (06 PROJECTIO_01).
UI/UX audit tests:    tests\FreeVideoStudio.App.Tests\UxAuditTests.cs (04 EXPORTCLOSE_01, CROPSAVEERR_01, SEGROWTHEME_01/SEGROWA11Y_01).
                      tests\FreeVideoStudio.App.Tests\UiAuditFixTests.cs (04 THEMETOKENS_01, FONTTOKENS_01, CAPTIONEDIT_01, ERRPLAIN_01; 02 VOFAST_01).
                      Plain-English errors: src\FreeVideoStudio.App\Infrastructure\UserFacingError.cs (04 UI-SAFEGUARDS).
Legacy identity:      .\src\FreeVideoStudio.Core\Infrastructure\LegacyAppDataNames.txt (migration only)
Storage migration:    docs/05_SYSTEM_LIFECYCLE_STORAGE.md, SYS-REBRAND
Residue removal:      docs/05_SYSTEM_LIFECYCLE_STORAGE.md, SYS-REBRAND-SWEEP (REBRAND_02,
                      .\src\FreeVideoStudio.Core\Infrastructure\LegacyResidueSweep.cs)
Packaging identity:   docs/09_DISTRIBUTION_AND_RELEASE.md, DIST-IDENTITY
Old-client alias:     docs/09_DISTRIBUTION_AND_RELEASE.md, DIST-SPLIT (REBRAND_03, created
                      transiently by .\build\FvsBuild\GitHubReleasePublisher.cs, never kept in obj\)
Output file names:    docs/03_FFMPEG_EXPORT_PIPELINE.md, FFM-OUTNAME (OUTNAME_01,
                      .\src\FreeVideoStudio.Core\Media\OutputFileNaming.cs)
                      Defaults: FreeVideoStudio-N.mp4 (Main App), FreeVideoStudio-merged-N.mp4 (Merger, SETNAMEONE_01);
                      rescued: FreeVideoStudio-RECOVERED-*, Merged-Videos-RECOVERED-* (fixed prefixes, unchanged)
Settings › Files & Folders: ONE save folder + ONE file name for both tools (SETFOLDERONE_01,
                      SETNAMEONE_01; MainOutputBaseName), docs/04_UI_UX_AVALONIA_SPEC.md,
                      UI-SETTINGS-ABOUT (.\src\FreeVideoStudio.App\Controls\SettingsWindow.Output.cs)
Meme library:         .\meme\ holds ALL memes (videos + pictures); .\mp4\ and .\jpeg\ are gone
                      (MEMEFOLDER_01). Cloud sync lists repo meme/ via git/trees (MEMESYNC_01..03);
                      synthetic repo starter meme fixtures live in .\build\starter_meme\ (separate from user .\meme\)
                      and match .\src\FreeVideoStudio.App\Infrastructure\MemeAssets.cs StarterFiles
                      == .\build\FvsBuild\Staging.cs StarterMeme (STARTERLIST_01, MemeLibraryTests).
                      Spec: docs/03_FFMPEG_EXPORT_PIPELINE.md, FFM-MEMELIB.
Song library:         .\mp3\ holds ALL songs (openly licensed / no-copyright only), plus attribution notes (.txt)
                      where a credit exists; the installer ships only the openly licensed starter songs (OSSMEDIA_01, docs/03 STARTER_01).
                      Downloadable library songs prefixed with '!_' or accompanied by .txt credentials
                      provide copyright attribution for YouTube/TikTok to prevent automatic audio muting
                      (ATTRIBUTION_01, MusicAttributionHelper.cs, MusicAttributionDialogWindow.axaml).
Folder names:         never contain spaces - %ProgramFiles%\FreeVideoStudio, Videos\FreeVideoStudio\Memes
                      (NOSPACE_01, MEMEFOLDER_02). Old spaced folders are migrated automatically.
                      Spec: docs/05_SYSTEM_LIFECYCLE_STORAGE.md, SYS-NOSPACE.
Add Music wizard:     .\src\FreeVideoStudio.App\MusicWizardWindow.axaml(.cs) + .Playlist.cs (song browser, midpoint audition, stale-load guards, coverage)
                      + .SongLane.cs (song bar with START/END edges; hidden lane) + .History.cs (undo)
                      + .Steps.cs (three-step navigation, per-occurrence part editing, coverage question, Sound reset)
                      + .SoundPreview.cs (export-rendered sound on Check & finish, SOUNDMIX_01)
                      + .PartAxis.cs (proportional video/music lanes, animated cuts, PARTAXIS_01 / PARTCUT_01)
                      + .Guide.cs (gap warning overlay, first-time start-point demo, GAPWARN_01 / PARTDEMO_01)
Background filmstrip: .\src\FreeVideoStudio.App\Services\WholeVideoFilmstrip.cs (whole-video quick + fine takes, WHOLESTRIP_01).
Review timeline:      .\src\FreeVideoStudio.App\Controls\ReviewTimelineRuler.cs (Check & finish time ruler + song blocks).
Dev player log cap:   .\src\FreeVideoStudio.App\Infrastructure\MpvLogCap.cs stops mpv_debug_*.log at 64 MB (MPVLOGCAP_01).
                      + .AiAutoFit.cs (cloud AI song auto-fit: AI picks a song by NUMBER, start point measured
                      locally from the song's loudness, visible steps + seconds counter + cancel, 45 s / 120 s
                      limits — AIMUSIC_HONEST_01 / AIMUSIC_START_01 / AIMUSIC_PROGRESS_01) + .Scan.cs (folder scan & probes) +
                      .Controls.cs + .Merger.cs (Merger-launched wizard) +
                      ViewModels\MusicWizardViewModel.cs (bound state). Three steps: Choose a song -> Select music starting point
                      -> Check & finish (MUSICWIZ3_01); repeated songs are separate occurrences (MUSICOCC_01). One song list, every song with its chosen part (PLAYLIST_01, MUSICWIN_01),
                      laid out by Core\Media\MusicBedPlan.cs with the export's crossfade from
                      Core\Media\MusicCrossfade.cs (XFADEPLAN_01); pure list maths in Core\Media\MusicPlaylist.cs.
                      Coverage handoff: .Playlist.cs maps output coverage to source endpoints (MUSICEND_01).
                      Saved windows: MusicWizardResult.Plan.cs (MusicStartsSeconds / MusicEndsSeconds, ToBedTracks).
                      Merger EDIT/REMOVE + named song blocks: VideoMergerWindow.MusicButton.cs (MERGERMUSIC_01/02).
                      Specs: docs/02 AUD-DIALOGS / AUD-MUSICWIN / AUD-MUSICFADE, docs/04 UI-MUSICWIZARD.
Window placement:     .\src\FreeVideoStudio.App\WindowPlacement.cs (maths), WindowBoundsHelper.cs (remembered windows),
                      Controls\DialogFit.cs (every dialog); defaults table 04 UI-WINPLACE (WINPLACE_01);
                      tests\FreeVideoStudio.App.Tests\WindowPlacementTests.cs.
Meme picker:          .\src\FreeVideoStudio.App\Controls\MemePickerWindow.axaml(.cs) (MEMEPICK_01..05)
                      + Controls\MemePlacementPreview.cs (live preview, drag position) + ViewModels\MemeChoiceViewModel.cs
                      + Infrastructure\MemePickerPrefsStore.cs (favourites, recent, last choice); docs/04 UI-MEMESELECT.
Memes (MEMEAUDIT_01): Core\Media\MemeTransitions.cs (full-screen cut/fade/zoom, corner fade/slide),
                      GranularSpeedEditorWindow.Memes.cs / .MemeBands.cs (editor memes, corner drag, list, keys),
                      MainWindow.Memes.cs (main-screen meme as a placement, markers, music question);
                      MainWindow.QuickStart.cs (start screen: drop-card click, AI Kill Tracker switch, AI setup tip; docs/04 UI-AIEASE);
                      tests: Core MemeAuditTests.cs, App MemeAuditAppTests.cs. Specs: docs/01 TL-MEME, docs/03 FFM-MEMECONCAT /
                      FFM-MEMECORNER / FFM-MEMEPREVIEW, docs/04 UI-MEMESELECT, docs/06 PROJ-SCHEMA (schema 7).
Speed Editor desktop: tests/DesktopPreviewSmoke/SpeedEditorSmokeApp.cs (--speed-editor; real GPU preview of the Speed Editor: plays, closes, main plays after; PREVIEWSPEED_02).
Freeze sound:         Core/Media/FreezeSoundEffect.cs + Core/Assets/FreezeSounds/*.wav (Church_Bell with ring-out, default; Cinematic_Dramatic_Slow-Down
                      spliced to the hold; picked in Settings › Sound & Music; one render for export and preview; music duck/carve/underwater,
                      FREEZESFX_05 / FREEZEMUSIC_01, docs/02 AUD-FREEZESFX; gate AV S44-S45);
                      Services/FreezeSoundPreview.cs plays it in the Speed Editor, the Voice-Over Studio live hold and the Settings Play button (FREEZESFX_02/05, FREEZEPARITY_01);
                      Core/Media/VideoLoudnessCache.cs keeps the uploaded video's loudness so the sound sits at 83 % of it (FREEZELEVEL_01);
                      MainWindow.FreezeSound.cs plays it in the Main App's live preview (FREEZEPARITY_01);
                      MusicWizardWindow.FreezeSound.cs and VideoMergerWindow.EdlPreview.cs play it in Add Music and the Video Merger (FREEZEPARITY_02).
UI stall watchdog:    App/Infrastructure/UiStallWatchdog.cs + Core/Infrastructure/BlockingCallTracker.cs → <Logs>\ui_stalls.log (UIWATCHDOG_01, docs/05 SYS-LOGGING).
Voiceover gain:       Core/Media/VoiceOverLevel.cs; Controls/VoiceOverGainSlider.cs + VoiceOverWindow.Volume.cs.
Preview layers:       Controls/MusicBedPreviewPlayer.cs (music in Main/Speed Editor/Voice-Over previews), Models/EditorPreviewLayers.cs,
                      Core/Media/BedPreviewFollower.cs, AudioPlayerClock.cs, PreviewSpeedGovernor.cs, PreviewTimeMap.cs
                      (PREVIEWLAYERS_01 / PREVIEWSPEED_01 / MUSICSYNC_03, docs/02 AUD-PREVIEWSYNC); gate AV S39-S43
                      (tests/FreeVideoStudio.AvRegression.Tests/Scenarios/S39_S43_*.cs, Infrastructure/Screens.cs).
                      0–150% across previews/export; schema 8 take gain (VOLEVEL_01, docs/02 AUD-VOICEOVER, docs/06 PROJ-SCHEMA).
AI features:          Services\Ai*.cs (AiVisionProviders.cs, AiHttpHelper.cs, AiCostEstimator.cs, AiMusicAutoFitService.cs,
                      AiViralPostService.cs, AiEliminationTrackerService.cs, AiUsageMeter.cs, AiJsonSchema.cs, AiFfmpeg.cs,
                      AiAnswerGuard.cs, AiReasoningPolicy.cs, AiRunApproval.cs, AiPreflight.cs, AiResultCache.cs,
                      AiAnswerDiskStore.cs, AiDiagnostics.cs, AiKeyHealth.cs (AIKEYHEALTH_01), AiAccountFix.cs (AIFIXFLOW_01), AiStatusLine.cs (AISTATUSLINE_01), AiKeyStartupCheck.cs (AIKEYSTARTUP_01), AiThinkingCalibration.cs (AITHINKCAL_01), ChatGptAuth.cs + ChatGptPlanTransport.cs (Sign in with ChatGPT, CHATGPTSIGNIN_01); AIAUDIT_04 docs/10 SYS-AIAUDIT4;
                      AIAUDIT_05 docs/10 SYS-AIAUDIT5, tests AiAudit5Tests.cs; AIAUDIT_06 docs/10 SYS-AIAUDIT6: AiFrameCrop.cs (pop-up close-ups, off unless FVS_AI_KILLCROP=1), AiUsageReport.cs (estimate vs used; developer_tools\AiBakeOff --usage-report), tests AiAudit6Tests.cs + Core PopupRegionTests.cs; AIQA_07 docs/10 SYS-AIQA7: tests AiQaAudit7Tests.cs: refusals, ChatGPT stream end, decimal boxes, song tags, Local AI processor fallback); AIQA_08 docs/10 SYS-AIQA8: tests AiQaAudit8Tests.cs: ChatGPT-plan refusals and in-stream failures, unrecognised commentary answers (AIPLANREFUSAL_01, AIPLANFAIL_01, AICOMMSHAPE_01, LOCALAISWAP_01; fixed); AIINTEG_01 docs/10 SYS-AIINTEG: tests AiIntegrationFixTests.cs: plan sign-in texts, PC tracking in every mode, bigger commentary parts, cloud-only look, busy fallback, free Google gate,
                      Core\Diagnostics\SecretRedactor.cs; Services\IAiVisionProvider.cs, Services\CommentaryPersonality.cs, Infrastructure\SecretProtector.cs (DPAPI, KEYPROTECT_01);
                      Core\Media\Caption*.cs. Vendors Gemini / Claude / OpenAI; default models: AIMODELS_02.
                      Canonical feature map + tags: docs/10_AI_FEATURES.md SYS-AISETTINGS (AIAUDIT_01).
                      Specs: 01 TL-AITRACKING / TL-AIHUD / TL-CAPTIONS, 02 AUD-MUSICWIN, 03 FFM-CAPTIONS,
                      04 UI-AIZOOM / UI-AIHUDWAND / UI-AICOMMENTARY / UI-MUSICWIZARD / UI-MERGER-MULTISELECT, 10 SYS-AISETTINGS.
                      Secret guard: tests\FreeVideoStudio.App.Tests\SecretLeakTests.cs (SECRETSCAN_01) — never commit
                      keys, .pfx, *.key.pem or *.password.txt.
Local AI (optional):  Core\LocalAi\ (OrtRuntime.cs ONNX Runtime C API, LocalAiPack*.cs pinned pack + check,
                      Sam2BoxTracker.cs, LocalSubjectTracker.cs, LocalTrackingMath.cs, LocalSubjectDiscovery.cs, YoloOnnxDetector.cs), Core\Media\SceneCutDetector.cs,
                      RawVideoFrameReader.cs, HudOverlayEventDetector.cs (Kill Tracker on-screen check);
                      Services\LocalAiPackService.cs, LocalTrackingService.cs; Controls\SettingsWindow.LocalAi.cs,
                      pack builder developer_tools\local_ai_pack\ (Make_Local_AI_Pack.cmd -> make_pack.ps1,
                      export_sam2_onnx.py, build_pack.py; output in out\, git-ignored, used by the A/V gate). Installed pack: %LocalAppData%\FreeVideoStudio\LocalAi\v1.
                      Specs: 01 TL-LOCALTRACK, 10 SYS-LOCALAI, 09 DIST-LOCALAIPACK.
AI ease of use:       Controls\AiZoomStartWindow.cs (clean Smart Zoom start choices: Cloud AI Tracker vs 100% Free Local AI, zero text box),
                      Controls\AiSubjectPickerWindow.axaml(.cs/.Correction.cs) (AIZOOMWIZARD_01 3-stage wizard, AIPICKERELLIPSE_01 glowing red oval target selection & guided AskManualDescriptionAsync failover),
                      Controls\AiMultiBlockZoomWindow.cs & Services\MultiBlockZoomPlan.cs (multi-block batch queue, atomic apply),
                      Controls\AiZoomPreviewModalWindow.cs (dedicated Smart Zoom preview modal dialog, Stage 3 review Keep/Fix Tracking/Discard, AIPREVIEW_01),
                      Controls\DialogChrome.cs (title bar for code-built dialogs), tests\FreeVideoStudio.App.Tests\AiWorkflowFixTests.cs.
                      Spec: 04 UI-AIEASE / UI-AIZOOM, 07 AIUNDO_01.
AI setup:             Controls\AiModeWindow.cs ("How should AI work?": PC only / Online only / Both; opens over its owner, not remembered), Services\AiSetup.cs
                      (AiMode, AiCapabilityPolicy, AfterPackRemoved, setup-complete check), Controls\AiSetupWizardWindow.axaml(.cs)
                      (resizable FitWizardWindowBounds, tracks AiSetupWizardBounds, key step, no company pre-picked);
                      Settings › AI › How AI works — Change…. Spec: 04 UI-AIMODE (AIMODE_01), 10 SYS-AICAPABILITY (AICAPABILITY_01).
```
Smart Zoom bounds/framing: GeminiTrackingService.CandidateFrameTimes and GranularSpeedEditorWindow.AiZoom enforce the selected interval; LocalTrackingService bounds motion bursts; AiTrajectorySmoother shares tighter zoom and bottom-HUD clearance across all engines (AISEGMENTONLY_01 / AIHUDCAMERA_01); AiZoomPreviewModalWindow provides dedicated preview dialog (AIPREVIEW_01).

MUSICBROWSE_01: Add Music Choose screen has a full-width list, left USE SONG → buttons, no right pane, midpoint autoplay and ±20s transport (song list only). Tests: tests/FreeVideoStudio.App.Tests/MusicBrowserTests.cs, MusicWizardStepsTests.cs, MusicWizardPartAxisTests.cs.

NAUDIO3OUTPUT_01: audio output consumers (PreviewAudioSync, VoiceOverPreviewPlayer, FreezeSoundPreview, UiSoundEffect, MainWindow, VoiceOverWindow) use NAudio 3 WaveOut. UiThreadBlockingTests guards supported event-driven output, buffer settings and off-thread disposal; 02 AUD-IPCSTATE / 05 SYS-WINSTATE.

FRAMECANCELISO_01: tests/FreeVideoStudio.Core.Tests/LibAvFrameDecodeTests.cs checks cancellation cleanup against a private copy of the selected media under Core suite work (03 FFM-LIBAVFRAME).
