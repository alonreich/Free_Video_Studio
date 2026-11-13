# REPOSITORY MAP  {#GOV-REPOMAP-FILE}

Where things live. Paths are relative to the repository root. Rules and reading order: [`README.md`](README.md) §0.
File → spec routing: [`INDEX.md`](INDEX.md) §1. Keep this map in step with the code (README §0.3 rule 3).

```text
Solution:             .\FreeVideoStudio.sln
Application project:  .\src\FreeVideoStudio.App\FreeVideoStudio.App.csproj
Core project:         .\src\FreeVideoStudio.Core\FreeVideoStudio.Core.csproj
Application tests:    .\tests\FreeVideoStudio.App.Tests\FreeVideoStudio.App.Tests.csproj
Core tests:           .\tests\FreeVideoStudio.Core.Tests\FreeVideoStudio.Core.Tests.csproj
FvsVerify tests:      .\tests\FvsVerify.Tests\FvsVerify.Tests.csproj (sentinel checker; CI)
Media checks:         .\tests\MediaPipelineChecks (console: real FFmpeg/GPU export checks; README.md inside)
Voice-over smoke:     .\tests\VoiceOverNativeSmoke (console: native voice capture smoke)
AI bake-off tool:     .\tools\AiBakeOff\AiBakeOff.csproj (AIBAKEOFF_01: developer-only console comparing AI vendors on subject
                      discovery, dense tracking and HUD detection; keys from environment variables only;
                      not in the solution, not shipped)
A/V regression gate:  .\tests\run-av-tests.cmd -> .\tests\FreeVideoStudio.AvRegression.Tests (AVREG_01, section 0.3a):
                      38 scenarios S01-S38 on the real player (PlayerRig/PlaybackProbe) and real exports
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
Developer helpers:    .\developer_tools\ BuildLog.ps1, Find_and_Remove_Comments.py, Manual_UnInstall.bat,
                      Redundant_Code_Finder.py, SetConsoleFont.ps1 (developer-only; not shipped; no spec)
Verification tool:    .\build\FvsVerify\FvsVerify.csproj; .\dev.cmd verify checks without launch cleanup (DEVVERIFY_01)
Sentinel registry:     .\build\sentinels.txt
AI source exporter:   .\developer_tools\Pull_Code_files.py (CODEEXPORT_01; docs/05 SYS-CODEEXPORT).
                      At most ten attachments in a new dated Downloads\free_video_studio subfolder;
                      full text/index, installer and tests, explicit omissions and secret filtering.
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
                      NOLOCALPORT_01, NOLURKER_01 (docs/05 SYS-AVCLEAN); SIGNDEPS_01, FREETRUST_01, SIGNPATH_01 (docs/05 SYS-SIGNING)
Licence & policies:   .\LICENSE (GPL-3.0), .\PRIVACY.md, .\CODE_SIGNING_POLICY.md, .\THIRD_PARTY_NOTICES.md (SIGNPATH_01)
Controlled folders:   .\src\FreeVideoStudio.Core\Infrastructure\ControlledFolderAccess.cs (read-only) +
                      .\src\FreeVideoStudio.App\Infrastructure\ProtectedFolderGate.cs (CFASAFE_01, docs/05 SYS-CFASAFE)
Release gate:         .\build\FvsBuild\ReleaseGate.cs (RELGATE_01: clean + pushed + linted notes + green CI + local
                      tests (RELQA_01) + protected root key (ROOTKEY_01) + typed PUBLISH, version commit + tag, undo
                      on failure; --resume finishes a failed publish, RELRESUME_01) -> GitHubReleasePublisher.cs
                      (--verify-tag, RETAIN_01/02 old files removed, pages + notes kept, BETA_01 pre-releases).
                      Withdrawn signers: .\build\revoked-signers.txt (REVOKE_01). Upgrade smoke test:
                      .\.github\workflows\upgrade-smoke.yml (UPGRADESMOKE_01). Spec: docs/09 DIST-RELEASEGATE.
Release notes:        .\docs\changes.md (technical, '## Unreleased') -> .\docs\CHANGELOG.md (users, '## Upcoming');
                      older releases in .\docs\CHANGELOG_ARCHIVE.md / changes_ARCHIVE.md (RELRECORD_01);
                      parser/lint/promotion .\src\FreeVideoStudio.Core\Infrastructure\ReleaseChangelog.cs
                      (RELNOTES_01..03). Spec: docs/09 DIST-RELEASENOTES.
Update trust:         elevated worker runs only from a protected %ProgramData% copy:
                      .\src\FreeVideoStudio.App\Services\ProtectedWorkerStage.cs (WORKERSTAGE_01, docs/05
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
Project persistence:  ProjectSession.cs ordered background save/load queue;
                      tests\FreeVideoStudio.App.Tests\AutosaveBackgroundTests.cs (06 PROJECTIO_01).
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
                      starter set = .\src\FreeVideoStudio.App\Infrastructure\MemeAssets.cs StarterFiles
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
Add Music wizard:     .\src\FreeVideoStudio.App\MusicWizardWindow.axaml(.cs) + .Playlist.cs (song list, coverage)
                      + .SongLane.cs (song blocks on the music lane, song card) + .History.cs (undo)
                      + .AiAutoFit.cs (cloud AI song auto-fit: AI picks a song by NUMBER, start point measured
                      locally from the song's loudness, visible steps + seconds counter + cancel, 45 s / 120 s
                      limits — AIMUSIC_HONEST_01 / AIMUSIC_START_01 / AIMUSIC_PROGRESS_01) + .Scan.cs (folder scan & probes) +
                      ViewModels\MusicWizardViewModel.cs (bound state). Two steps: Choose music -> Fit & mix
                      (WIZ2STEP_01). One song list, every song with its chosen part (PLAYLIST_01, MUSICWIN_01),
                      laid out by Core\Media\MusicBedPlan.cs with the export's crossfade from
                      Core\Media\MusicCrossfade.cs (XFADEPLAN_01); pure list maths in Core\Media\MusicPlaylist.cs.
                      Coverage handoff: .Playlist.cs maps output coverage to source endpoints (MUSICEND_01).
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
Freeze sound:         Core/Media/FreezeSoundEffect.cs (slow-motion boom + music dip, FREEZESFX_01, docs/02 AUD-FREEZESFX);
                      Services/FreezeSoundPreview.cs plays it in the Speed Editor preview (FREEZESFX_02).
Voiceover gain:       Core/Media/VoiceOverLevel.cs; Controls/VoiceOverGainSlider.cs + VoiceOverWindow.Volume.cs.
                      0–150% across previews/export; schema 8 take gain (VOLEVEL_01, docs/02 AUD-VOICEOVER, docs/06 PROJ-SCHEMA).
AI features:          Services\Ai*.cs (AiVisionProviders.cs, AiHttpHelper.cs, AiCostEstimator.cs, AiMusicAutoFitService.cs,
                      AiViralPostService.cs, AiEliminationTrackerService.cs, AiUsageMeter.cs, AiJsonSchema.cs, AiFfmpeg.cs,
                      AiAnswerGuard.cs, AiReasoningPolicy.cs, AiRunApproval.cs, AiPreflight.cs, AiResultCache.cs,
                      AiAnswerDiskStore.cs, AiDiagnostics.cs — audit 2026-11-13, AIAUDIT_04, docs/05 SYS-AISETTINGS §7.7),
                      Core\Diagnostics\SecretRedactor.cs, IAiVisionProvider.cs, CommentaryPersonality.cs, SecretProtector.cs (DPAPI, KEYPROTECT_01);
                      Core\Media\Caption*.cs. Zero local models; vendors Gemini / Claude / OpenAI; default models: AIMODELS_02.
                      Canonical feature map + tags: docs/05 SYS-AISETTINGS §7 (AIAUDIT_01).
                      Specs: 01 TL-AITRACKING / TL-AIHUD / TL-CAPTIONS, 02 AUD-MUSICWIN, 03 FFM-CAPTIONS,
                      04 UI-AIZOOM / UI-AIHUDWAND / UI-AICOMMENTARY / UI-MUSICWIZARD / UI-MERGER-MULTISELECT, 05 SYS-AISETTINGS.
                      Secret guard: tests\FreeVideoStudio.App.Tests\SecretLeakTests.cs (SECRETSCAN_01) — never commit
                      keys, .pfx, *.key.pem or *.password.txt.
```
