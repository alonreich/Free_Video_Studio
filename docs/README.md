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
* **Spec routing:** maintain domain mini-maps and `INDEX.md`; repeated source headers are not required
  (`SPEC_GOVERNANCE.md` §4, SPECROUTING_02).
* **No comments in code (NOCOMMENTS_01, user decision 2026-11-13):** `.cs` files carry no `//` or `/* */` comments, tests included; `developer_tools/Find_and_Remove_Comments.py` strips them before a commit. Anything worth explaining (why a rule exists, a tag, a measured number) goes in the owning spec in `docs/`. Consequences: a sentinel tag must sit in code that is not such a comment (sentinels whose tag only lived in a `//` comment were retired on 2026-11-13); fake test keys are written split (`"sk-" + "ant-dummy…"`) so the secret scan does not match them, instead of carrying a `SECRETSCAN_ALLOW` comment. The tool treats ANY `//` outside a quoted string as a comment, including inside multi-line raw strings (base64, URLs): after running it, check that `SmartUpdateTests.ThePinnedRootIsIntact` still passes.
* **Land the sentinel with the fix:** a guarded fix adds its `TAG=path` line to `build/sentinels.txt` in the
  same change (checked by `build/FvsVerify` via `dev.cmd`, DEVVERIFY_01, and by
  `ArchitectureRuleTests.EveryFixSentinelStillResolves` in CI). Sentinels fail verification when their
  in-code token disappears; spec anchors (`{#ANCHOR}`) only route requirements. A correct source file does
  not prove the running binary contains the fix (05 SYS-VERIFYTOOL).
* **Storage inventory (STORAGEPATHS_01):** every new/changed storage category (including API keys) must update the production/development table and retention rules in [05 SYS-STORAGE-TABLE](05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-STORAGE-TABLE), with path/lifecycle tests.
* **Never commit secrets:** no keys, `.pfx`, `*.key.pem` or `*.password.txt` (`05` SECRETSCAN_01).
* **Test output lives ONLY in `logs\<Suite>_Results\` (TESTOUT_01, §0.3b):** no test, script, runner or CI
  step may write results, fixtures, media or logs anywhere else in the repository.

### 0.2 Reading order (the agent route)

1. This file, §0 (rules) → §2 (the 9 invariants).
2. Know a FILE, SYMBOL, CONSTANT or TAG → grep [`INDEX.md`](INDEX.md) (§1 files, §2 symbols/tags). Know only
   the domain → §3 below.
3. Read the ONE spec you land on (all of them for a `⚠` file), then the code. Do not pre-load the others.
4. Need a path or a component's location? [`REPO_MAP.md`](REPO_MAP.md).

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
3. **Map sync:** new or removed files, components or rules → update [`REPO_MAP.md`](REPO_MAP.md) and
   [`INDEX.md`](INDEX.md) §1 (and the owning spec's Code Mini-Map).
4. **Retention is automatic:** the official release promotes both staging sections and keeps 5 releases in
   `CHANGELOG.md` and 10 in `changes.md`; older ones move to `CHANGELOG_ARCHIVE.md` / `changes_ARCHIVE.md`.
   Do not prune or rename sections by hand.
5. **Enforced:** CI (`Release records`) fails a push that changes `src/`, `build/`, `tests/` or the `.cmd`
   scripts without changing `changes.md` (CHANGELOG_02; `[no-changelog]` in a commit message for a deliberate
   exception).

### 0.3a The A/V regression gate — NOT DONE UNTIL IT PASSES (AVREG_01)  {#GOV-AVGATE}

> **No developer or AI agent may declare ANY change done, fixed or successful until `tests\run-av-tests.cmd`
> has run and printed `PASS`.** It checks the playhead, scrubbing, click-to-seek, marks, thumbnails, memes
> (full-screen and corner), music, voice-over, the Speed Editor, the Video Merger and the other windows for
> stalls, stutter, hangs, A/V desync and preview-vs-export drift, using the real player and real exports.

1. Run `tests\run-av-tests.cmd` from the repo root (5–10 min). Wait. Do not read logs while it runs.
2. Read ONLY what it prints (the same text is kept in `logs\Gate_Results\LAST_RESULT.txt`): `PASS`, or one block per failure saying what the user would see, the drift
   in ms, and the scenario's `file:line`. Go to that file:line, and nowhere else, to start.
3. Fix the PRODUCT. Never loosen `Budget` (tests/FreeVideoStudio.AvRegression.Tests/Infrastructure/Repo.cs)
   or delete a scenario to turn a run green; that needs a human's explicit OK.
4. `tests\run-av-tests.cmd quick` is for iterating only; it is not the gate.
5. A failing `S03` means the app code a rig mirrors changed: update that rig (named in the message).
6. Environment: `FVS_AV_AUDIO=null` forces the simulated clock (no sound device); `FVS_AV_SEED=<n>` replays
   S35's random session with the seed printed in its failure; `FVS_TEST_OUTPUT_MANAGED` is set by
   the runner only (never by hand); `FVS_TEST_FFMPEG=<path>` names FFmpeg for Core media unit tests (else they skip).

`tests\run-all-tests.cmd` additionally exercises actual desktop pixels and the real merger window,
standalone media exports and decoder soak. Its desktop tier requires an interactive, unobscured
Windows desktop with hardware preview available; a model-only or null-video run cannot certify
displayed-frame transitions. Each suite's output is replaced right before that suite runs, under an
exclusive test-run lock and a checked directory allowlist (`TESTGATE_01`, `TESTCLEAN_01/02`, §0.3b).

### 0.3b Where test results go — ONE place, ONE latest copy per suite (TESTOUT_01)  {#GOV-TESTOUT}

> **Every test result, from every suite, however it is started (`run-av-tests.cmd`, `run-all-tests.cmd`,
> `dotnet test`, Visual Studio, `python -m unittest`, a `.ps1` test, CI) goes into `.\logs\<Suite>_Results\`
> and NOWHERE else.** `TestResults\`, `artifacts\`, `tests\AvRegression\`, `tests\MediaPipelineChecks\artifacts\`,
> and per-project `TestResults\` are retired; a new one is a defect. `%TEMP%` may hold only scratch the test
> deletes before it ends, never a result.

```text
logs\
  Gate_Results\LAST_RESULT.txt        verdict of the last tests\run-av-tests.cmd run (any mode)
  <Suite>_Results\                    one folder per suite, e.g. AV, Core, App, Verifier, Sentinels,
    console.log                       Python, DevSandbox, Harness, DesktopPreview, MergerDesktop,
    <Suite>.trx                       MergerSilentDesktop, Media, DecoderSoak, NativeSmoke
    work\                             everything the suite generates (media, app state, exports, fixtures)
```

* **One latest result per suite, no junk.** Running a suite replaces ITS folder (and only its folder) before
  it starts; other suites keep their last result. Running suite A twice leaves one A result; running B
  afterwards leaves one A and one B result.
* **Who replaces what.** The gate empties each suite folder right before that suite and writes
  `console.log` there (`FVS_TEST_OUTPUT_MANAGED=1`; the suite then replaces only `work\`). A suite started on
  its own replaces its whole folder: C#/xUnit through a module initializer (`TestOutputSuite`,
  `TestArtifactDirectory.SuiteWork`; each test project sets `VSTestResultsDirectory` to its folder),
  PowerShell through `Enter-SuiteOutput` (`tests\TestHarness.ps1`), Python in `setUpModule`, standalone
  programs through `TestArtifactDirectory.Begin`. A child process a test starts never wipes the live run.
* **Adding a suite or a test that writes files:** use the suite's `work\` folder from those helpers (never
  build a path to the repository root, `TestResults`, `artifacts` or `%TEMP%` for results), pick a letters-only
  suite name, and run it from `tests\run-av-tests.ps1` with `Invoke-Suite` / `Invoke-Check`.
* **Enforced:** cleanup refuses anything that is not `logs\<Letters>_Results[\...]`, any link/junction and any
  traversal; `TestArtifactDirectoryTests.NoTestWritesOutsideTheLogsSuiteFolders` fails a test, build or CI file
  that names a retired output folder; the code exporter never reads `logs\` (opt in with `--test-result`).

### 0.4 Build, test, release

| Goal | Command | Who |
| :--- | :--- | :--- |
| Run the app from source | `.\dev.cmd` (incremental Release managed run; settings preserved; `dev fresh` for full-clean troubleshooting) | anyone |
| Full local packaging check (NativeAOT + installer, no GitHub) | `.\dev_build.cmd` | anyone |
| A/V regression gate (before declaring ANY change done) | `tests\run-av-tests.cmd` (§0.3a) | everyone |
| Compile / test | `dotnet build FreeVideoStudio.sln -c Release`, `dotnet test tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj`, `dotnet test tests/FreeVideoStudio.App.Tests/FreeVideoStudio.App.Tests.csproj` | anyone |
| Fix sentinels | `dotnet run --project build/FvsVerify/FvsVerify.csproj` | anyone |
| Official stable release | `.\Build.cmd` (release gate, `09` DIST-RELEASEGATE) | **human only** |
| Beta (pre-release, manual download only: the app never offers it, SETNOBETA_01) | `.\Build.cmd --beta` | **human only** |

### 0.5 Which document answers what

| Document | Read it when |
| :--- | :--- |
| [`SPEC_GOVERNANCE.md`](SPEC_GOVERNANCE.md) | Before any code inspection or change: the zero-leakage contract. |
| [`INDEX.md`](INDEX.md) | You know a file, symbol, constant or tag and need its spec (grep it; never read it whole). |
| [`REPO_MAP.md`](REPO_MAP.md) | You need where a component, tool, test suite or asset folder lives. |
| [`01_TIMELINE_COORDINATE_MATH.md`](01_TIMELINE_COORDINATE_MATH.md) … [`08_APPLICATION_COMPOSITION.md`](08_APPLICATION_COMPOSITION.md) | The domain specs (§3 routes you to one). |
| [`09_DISTRIBUTION_AND_RELEASE.md`](09_DISTRIBUTION_AND_RELEASE.md) | Installer, smart updates, release gate, release notes, retention. |
| [`05_SYSTEM_LIFECYCLE_STORAGE.md`](05_SYSTEM_LIFECYCLE_STORAGE.md) | Install/update transaction and its trust, auto-update, signing, dev harness. |
| [`CHANGELOG.md`](CHANGELOG.md) / [`changes.md`](changes.md) | User release notes / technical change log (§0.3). |
| [`New-Release.md`](New-Release.md) | The human checklist after a public release (antivirus submissions). |
| [`REBRAND_MIGRATION.md`](REBRAND_MIGRATION.md) | Old→new product identity and its migration. |


> ### ✅ REBRAND COMPLETE — read [`REBRAND_MIGRATION.md`](REBRAND_MIGRATION.md)
> This product was renamed to **Free Video Studio** (`FreeVideoStudio`). The rebrand record lists the
> full old→new identity map, exactly what happens when an old-brand install updates, the automatic
> output naming (`FreeVideoStudio-N.mp4` / `FreeVideoStudio-merged-N.mp4`, one name editable in Settings › Files & Folders)
> and the only two files allowed to contain the previous name.

## 1. Mission Architecture
Free Video Studio is a specialized, hardware-accelerated desktop video editing suite built with C# and Avalonia UI on .NET 9 (Native AOT compatible). The system transforms raw 16:9 widescreen gameplay footage into master-quality 9:16 portrait montages, mobile highlights, and social video deliverables with zero manual keyframing.

### Product and repository identity

`FreeVideoStudio.sln` contains `src/FreeVideoStudio.App`, `src/FreeVideoStudio.Core`, `build/FvsVerify`, `tests/FreeVideoStudio.App.Tests`, `tests/FreeVideoStudio.Core.Tests`, `tests/FvsVerify.Tests` and `tests/FreeVideoStudio.AvRegression.Tests` (`build/FvsBuild` is built separately). The application assembly is `FreeVideoStudio`; its root namespace remains `FreeVideoStudio.App`. The core assembly and namespace are `FreeVideoStudio.Core`. `FvsBuild`, `FvsVerify`, `.fvsproj` and the existing `FVS_*` environment variables retain their internal names.

Use [distribution identity](09_DISTRIBUTION_AND_RELEASE.md#DIST-IDENTITY) for packaging names and local validation commands, and [storage migration](05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-REBRAND) for canonical AppData paths and legacy compatibility. `project_structure.txt` and `AGENTS.md` at the repository root only point to THIS file, the single entry point; the repository map that used to live in `project_structure.txt` is [`REPO_MAP.md`](REPO_MAP.md) (§5). Repository links are relative to the checkout; the GitHub repository is `alonreich/Free_Video_Studio` ([`REBRAND_MIGRATION.md`](REBRAND_MIGRATION.md)).

---

## 2. The 9 North Star Architectural Invariants
All subsystems, controls, and rendering components across `src/` must strictly enforce these nine non-negotiable architectural pillars:

1. **Single Binary Executable Mandate:** Zero loose companion assets (`.gif`, `.png`, `.wav`, `.ico`) alongside the output binary. All UI overlays, guide indicators, brand icons, and animations must be generated dynamically via code or vector path geometry (`PathGeometry`) in memory. The user downloads ONE file, the Inno Setup package `FreeVideoStudio.exe`; the program itself never carries other executables inside its resources (NODROPPER_01, `09` DIST-INSTALLER).
2. **Absolute Authority for Time:** `src/FreeVideoStudio.Core/Media/OutputTimeline.cs` is the authoritative mathematical model for single-clip linear edits and per-clip segment durations. For multi-clip NLE virtual timelines, `src/FreeVideoStudio.Core/Media/CompositeTimeline.cs` and `MergeEdl.cs` constitute the sole mathematical authority mapping source µs, 60 fps merged frames, and output seconds across `edl://` libmpv preview playback and FFmpeg multi-clip rendering.
3. **Strict A/V Process Isolation:** Master preview volume (Windows OS PID session) and preview playback remain completely decoupled from FFmpeg export filtergraphs. Master preview slider adjustments must never alter export loudness.
4. **Leak-Free Render Pipelines:** The deprecated FFmpeg `zoompan` filter is banned suite-wide due to fatal native heap leaks. Dynamic zooms must be achieved via frame-evaluated padding, dynamic scaling, cropping, and contrast-adaptive sharpening (`cas=0.5`).
5. **Zero Raw Hex Styling:** All Avalonia styles, controls, and dynamic templates must resolve colors exclusively through named `DynamicResource` tokens in `AvaloniaApp.axaml`. Hardcoded hex values in shared styling are strictly prohibited.
6. **Thread-Bound Safety Contracts & GPU Preview Lock:** UI dispatchers must never block on native audio/video subsystem calls. WASAPI audio capture lifecycles run on an isolated serialized worker thread; SkiaSharp snapshot encoding and heavy image decodes execute off the UI thread.
   * ⚠️ **MANDATORY ARCHITECTURAL LOCK — libmpv PREVIEW ENGINE:** libmpv's Render API (`mpv_render_context_create`) does NOT support Direct3D 11 (`"d3d11"` returns `-19 / MPV_ERROR_NOT_IMPLEMENTED`). The `WGL_NV_DX_interop` bridge (rendering into OpenGL FBOs shared directly with Direct3D 11 textures in GPU VRAM) combined with non-blocking 0 ms 16-slot ring probing is the **frozen, permanently locked hardware architecture**. It provides zero-copy VRAM throughput, unlocked 120/240 FPS playback, and unobstructed Avalonia XAML overlays without Win32 HWND airspace occlusion. Attempting to bypass WGL with non-existent libmpv D3D11 APIs guarantees a pitch-black screen (audio-only), and re-introducing blocking KeyedMutex timeouts (e.g. 1,000 ms `AcquireSync`) reintroduces 1 FPS lock contention. Modifying this architecture or reintroducing blocking mutex waits is strictly prohibited.
7. **Monotonic Progress Guarantee:** Render progress tracking must be cost-weighted and mathematically monotonic (P(n+1) >= P(n)). Progress bars may never snap, stutter, or lerp backward across multi-pass operations.
8. **Testable rules are tests, and CI runs them:** mechanically checkable rules (one activation path per control, no raw hex, no `zoompan`, no silent catch) live in `tests/FreeVideoStudio.App.Tests/ArchitectureRuleTests.cs`; prose explains why. Sentinels prove a fix still exists; tests prove it still works; `.github/workflows/ci.yml` (`SYS-CI`) runs both (history: `UNDO_23`, `PROJ_10` stayed red unnoticed before CI).
9. **No Failure Is Silent:** Every caught exception is classified through `IFaultSink` as Recoverable, Degraded or Fatal (08 [COMP-FAULTS](08_APPLICATION_COMPOSITION.md#COMP-FAULTS)). `catch { }` and `catch (Exception ex) { Log(ex); }` are not error handling — they leave the user to guess whether they mis-clicked.

---

## 3. Domain Routing

One spec per domain. File-level routing (including every `⚠` co-governed file) is canonical in
[`INDEX.md`](INDEX.md) §1; each spec's Code Mini-Map lists its bound files and symbols.

| Spec | Domain |
| :--- | :--- |
| [`01`](01_TIMELINE_COORDINATE_MATH.md) | Timeline & coordinate math — 16:9→9:16 geometry, OutputTimeline chunk model, markers, freezes, cuts, zoom spans, meme anchors, hitboxes, AI tracking/HUD, captions timing, merger EDL time. |
| [`02`](02_AUDIO_ENGINE_MASTERING.md) | Audio engine & mastering — PID preview volume, LUFS targets, sidechain ducking, Voice Over Studio, WASAPI threading, music bed fades. |
| [`03`](03_FFMPEG_EXPORT_PIPELINE.md) | FFmpeg export — encoder discovery, zoom filtergraph, concat/bitrate, memes (concat, corner, preview), fades, progress, binary paths, meme library, output names, caption burn-in. |
| [`04`](04_UI_UX_AVALONIA_SPEC.md) | UI/UX & Avalonia — design tokens, high-DPI layout, tooltips, confirmations, coach tours, editors' layout, detachable previews, merger queue, Settings, wizards. |
| [`05`](05_SYSTEM_LIFECYCLE_STORAGE.md) | System lifecycle & storage — mutexes, logging, window bounds, deferred close, crash recovery, atomic writes, storage table, install/update transaction & trust, signing, AV hygiene, dev harness & sentinels, AI settings/keys. |
| [`06`](06_PROJECT_DOCUMENT_MODEL.md) | Project document — `.fvsproj`, schema versioning & amputation rule, AOT-safe JSON, atomic persistence & backup, source integrity, recent projects. |
| [`07`](07_UNDO_AND_HISTORY.md) | Undo/redo — gesture coalescing, ceiling on push, redo invalidation, no-op rejection, immutable state, re-entrancy guard, persistence. |
| [`08`](08_APPLICATION_COMPOSITION.md) | Composition root, service seams, fault tiers (Recoverable/Degraded/Fatal), architecture tests, CI. |
| [`09`](09_DISTRIBUTION_AND_RELEASE.md) | Inno Setup installer, smart (per-file, content-hashed) updates, release gate, release notes & retention, LFS enforcement and the history-rewrite runbook. |
| [`REBRAND_MIGRATION.md`](REBRAND_MIGRATION.md) | Old→new identity map, old-brand update flow, residue removal, automatic output names. |

---

## 5. Repository Map  {#GOV-REPOMAP}

Moved to [`REPO_MAP.md`](REPO_MAP.md) (paths, tools, test suites, asset folders). Keep it in step with the
code (§0.3 rule 3).
