# SPECIFICATION 09: DISTRIBUTION, SIGNING, UPDATES, INSTALL & REPOSITORY WEIGHT

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).

| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FreeVideoStudio.Core/Infrastructure/RuntimePayloadManifest.cs` | `RuntimePayloadManifest` | `FromFolder`, `Read`, `Write`, `SYS-PAYLOADSPLIT` | Runtime fingerprint. |
| `src/FreeVideoStudio.App/Services/UpdateService.cs` | `UpdateService` | `QueryLatestReleaseAsync`, `ReleasesPageUrl`, `SYS-PAYLOADSPLIT` (retired here, COMPACTGONE_01) | Which release to update to. **⚠ CO-GOVERNED BY: 05** |
| `src/FreeVideoStudio.Core/LocalAi/LocalAiPackManifest.cs`, `LocalAiPackFiles.cs`, `developer_tools/local_ai_pack/*.py` | `LocalAiPackManifest`, `LocalAiPackFile` | `ReleaseTag`, `ReleaseBaseUrl`, `PackVersion`, `Files`, `LOCALAIPACK_01`, `DIST-LOCALAIPACK` | Pinned optional Local AI pack and its builder. **⚠ CO-GOVERNED BY: 05** |
| `src/FreeVideoStudio.Core/Infrastructure/SmartUpdateManifest.cs` | `SmartUpdateManifest`, `SmartPlan`, `SmartFile`, `SmartPatch` | `Parse`, `Plan`, `Sign`, `VerifySignature(requireCurrentSigner)`, `AssetSha256`, `RevokedSigners`, `DIST-SMART`, `ASSETHASH_01`, `REVOKE_01`, `SIGNEXPIRY_01` | The signed per-file list of a release. |
| `src/FreeVideoStudio.Core/Infrastructure/BinaryDelta.cs` | `BinaryDelta` | `Create`, `Apply`, `ApplyFile`, `StreamBufferSize`, `BINDELTA_01`, `BINDELTA_02` | Binary patch for the main program. |
| `src/FreeVideoStudio.Core/Infrastructure/BrotliFile.cs` | `BrotliFile` | `CompressFile`, `DecompressFile(maxBytes)`, `ASSETHASH_01` | Per-file asset compression; size-capped decompression. |
| `src/FreeVideoStudio.Core/Infrastructure/ReleaseChangelog.cs` | `ReleaseChangelog`, `ReleaseChangelog.Section` | `Parse`, `Find`, `Promote` (returns what it removes), `Archive`, `RELRECORD_01`, `LintUserNotes`, `PlainHighlights`, `UpcomingTitle`, `UnreleasedTitle`, `RELNOTES_01`, `RELNOTES_02`, `RELNOTES_03` | The one parser of docs/CHANGELOG.md and docs/changes.md (§3f DIST-RELEASENOTES). |
| `build/FvsBuild/SmartUpdateBuilder.cs` | `SmartUpdateBuilder` | `Prepare`, `Finish`, `ReleaseFiles`, `MarkReleased`, `EnsurePublishedBases`, `DIST-SMART`, `NODROPPER_01`, `DELTABASE_01`, `ASSETHASH_01` | Builds the per-file assets, patches, the signed list packed into the installer and the release update.json. |
| `build/installer/FreeVideoStudio.iss` | Inno Setup script | `ForwardedArguments`, `NODROPPER_01`, `PEIDENTITY_01` | The single download: a non-elevated Inno Setup package that unpacks the release beside its signed list and starts the transactional installer. |
| `build/FvsBuild/InnoInstaller.cs` | `InnoInstaller` | `Build`, `SetupFileName`, `NODROPPER_01` | Finds ISCC.exe (Inno Setup 6.3+, `FVS_ISCC`) and compiles the script into `compiled\FreeVideoStudio.exe`. |
| `build/FvsBuild/PeIdentity.cs` | `PeIdentity` | `FixOriginalFilename`, `Patch`, `PEIDENTITY_01` | Rewrites OriginalFilename/InternalName to the shipped file name before signing. |
| `src/FreeVideoStudio.App/Services/UpdatePreparer.cs` | `UpdatePreparer` | `PrepareAsync`, `EnsureDiskSpace`, `RequirePatchHeader`, `UPDATESPACE_01`, `ASSETHASH_01`, `UNSIGNEDDEV_01` | Client side of DIST-SMART. |
| `build/FvsBuild/GitHubReleasePublisher.cs` | `GitHubReleasePublisher` | `Publish`, `ReleaseAssets`, `VerifyAssets`, `PruneOldReleases`, `ReleasesToPrune`, `ReleaseTitle`, `PreSetupDownloadName`, `REBRAND_03`, `NODROPPER_01`, `RELTAG_01`, `RETAIN_01`, `RETAIN_02`, `BETA_01`, `RELRESUME_01` | Draft-verify-publish from the pushed tag (replacing an earlier unfinished draft); creates the previous-brand download alias transiently; retention removes old FILES only. |
| `build/FvsBuild/ReleaseGate.cs` | `ReleaseGate`, `ReleasePlan`, `ResumePlan`, `ResumeState` | `Preflight`, `PreflightResume`, `PrepareNotes`, `CommitVersion`, `Undo`, `TreeStillClean`, `PushRelease`, `RecordReleaseNotes`, `CiProblems`, `RequiredChecks`, `HeadingsWithoutTags`, `SameNotes`, `RELGATE_01`, `RELNOTES_01`, `RELQA_01`, `RELQA_02`, `RELRECORD_01`, `RELRECORD_02`, `RELRESUME_01`, `ROOTKEY_01` | The release transaction (§3e DIST-RELEASEGATE). |
| `build/FvsBuild/Program.cs` | `Program` | `RunPipeline`, `RunResume`, `FailRelease`, `RELGATE_01`, `RELRESUME_01`, `--protect-root-key` | Orders the gate, the build and the publish. **⚠ CO-GOVERNED BY: 05** |
| `docs/CHANGELOG.md` | User-facing release notes | `## Upcoming`, `RELNOTES_01`, `CHANGELOG_01` | What users read; becomes the GitHub release notes and the update card's "What's new". |
| `docs/changes.md` | Technical change log | `## Unreleased`, `RELNOTES_01` | What engineers and agents read; promoted with every stable release. |
| `docs/CHANGELOG_ARCHIVE.md` / `docs/changes_ARCHIVE.md` | Release-notes archives | `RELRECORD_01` | Sections beyond the retention move here; nothing is deleted (the technical archive appears with the first pruning). |
| `build/revoked-signers.txt` | Withdrawn publisher leaves | `REVOKE_01` | Public SHA-256 thumbprints; copied into every signed list; must equal `PublisherRoot.RevokedSignerThumbprints`. |
| `.github/workflows/upgrade-smoke.yml` | Upgrade smoke (manual) | `UPGRADESMOKE_01` | Installs an old public release on a free Windows runner and updates it to a new release or draft (§3g). |
| `build/FvsBuild/Staging.cs` | `Staging` | `WriteManifests`, `ValidateCompiledOutput`, `StarterMeme`, `SYS-PAYLOADSPLIT`, `NODROPPER_01` | Stages the release folder (including `build\starter_meme\` → `starter\meme` under `STARTERLIST_01`) and writes its manifests (nothing is zipped or embedded); development destinations reuse content-verified dependencies (DEVINCREMENTAL_01, 05 SYS-DEVBUILD); `obj\ReleaseAssets\runtime.manifest.json`. |
| `.github/workflows/ci.yml` | CI | `SYS-CI`, `aot-publish`, `records` (`RELRECORD_01`, `CHANGELOG_02`) | Runs the ratchets; checks the release records and the change-log duty; reports the app publish size. **⚠ CO-GOVERNED BY: 08** |
| `.github/workflows/lfs-guard.yml` | LFS guard | `SYS-REPOWEIGHT` | Proves LFS is real and stops new large files. |
| `.gitattributes` | EOL + LFS policy | `EOL_01`, `SYS-REPOWEIGHT` | What is stored where. |
| `Build.cmd` | Production Release Script | `dotnet run build\FvsBuild`, `--beta`, `--yes` (ignored, RELNOPROMPT_01), `--no-publish`, `--resume`, `RELGATE_01`, `RELQA_01`, `RELRESUME_01` | **STRICT AGENT BAN**: Deploys release assets and publishes publicly to GitHub Cloud, behind the release gate. Never run by autonomous agents. **⚠ CO-GOVERNED BY: 05** |
| `dev_build.cmd` | Local Dev Build Harness | `dotnet run build\FvsBuild -- --dev` | Local compilation check and NativeAOT publish verification. Zero git tags, zero GitHub Cloud publishing. **⚠ CO-GOVERNED BY: 05** |
| `src/FreeVideoStudio.Core/Infrastructure/ControlledFolderAccess.cs` | `ControlledFolderAccess` | `Mode`, `IsBlocking`, `ProtectedRoots`, `IsProtectedPath`, `MayBlock`, `IsUnder`, `CFASAFE_01` | Read-only view of Windows "Controlled folder access" (SYS-CFASAFE). |
| `src/FreeVideoStudio.App/Infrastructure/ProtectedFolderGate.cs` | `ProtectedFolderGate` | `CanWrite`, `CanWriteNow`, `FfmpegCanWrite`, `DenialIsFresh`, `ForgetDenial`, `TakePendingNotice`, `ShowNoticeAsync`, `CFASAFE_01`, `CFAFFMPEG_01` | One write attempt per protected folder, remembered refusal, the one notice (SYS-CFASAFE). |
| `src/FreeVideoStudio.App/Services/UpdateDownloader.cs` | `UpdateDownloader` | `DownloadAsync`, `DLRESUME_01` | Resumable, retried, hash-checked downloads. |
| `src/FreeVideoStudio.Core/Infrastructure/PublisherRoot.cs` | `PublisherRoot` | `Vouches`, `Sha256Thumbprint`, `UPDATEPIN_01` | The publisher root compiled into every copy. |
| `src/FreeVideoStudio.App/Services/UpgradeBrokerHost.cs` | `UpgradeBrokerHost` | `RunAsync`, `Attach`, `IsActive`, `UPGRADEUX_01` | Runs the upgrade broker with a progress window; headless message-box fallback. |
| `src/FreeVideoStudio.App/UpgradeProgressWindow.axaml.cs` | `UpgradeProgressWindow` | `AllowClose`, `OnClosing` | The install/update checklist window (UPGRADEUX_01). |
| `src/FreeVideoStudio.App/Services/UpgradeFinishedNotice.cs` | `UpgradeFinishedNotice` | `Attach`, `Describe`, `KindArgument`, `FromArgument`, `UPGRADEUX_05` | First launch after install/update: "Finishing…" then "Updated from X to Y". |
| `src/FreeVideoStudio.App/Services/ProtectedWorkerStage.cs` | `ProtectedWorkerStage` | `Create`, `IsProtectedWorkerPath`, `ExistingFolders`, `SweepStale`, `WORKERSTAGE_01` | The protected copy the elevated update worker runs from (SYS-UPGRADE-TRUST). |
| `build/FvsBuild/CodeSigning.cs` | `CodeSigning` | `SignIfNeeded`, `FVS_SIGN_PFX`, `FVS_SIGN_PASS`, `FVS_ALLOW_UNSIGNED` | Mandatory Authenticode digital signing of the release executable. **⚠ CO-GOVERNED BY: 08**|

---

## 0. Build Harness Disclaimers & Agent Execution Rules  {#DIST-AGENTS}

> Agents never run `.\Build.cmd` (it publishes with no prompt, RELNOPROMPT_01); use `.\dev_build.cmd`. Canonical: [README §0.1](README.md#GOV-START).

---

## Product and build identity  {#DIST-IDENTITY}

| Scope | Canonical identity |
| :--- | :--- |
| Display name and Win32 product metadata | `Free Video Studio` |
| Install, data and media folder names | `FreeVideoStudio` — never the spaced display name (`05` SYS-NOSPACE) |
| Solution | `FreeVideoStudio.sln` |
| App project | `src/FreeVideoStudio.App/FreeVideoStudio.App.csproj` |
| App assembly / package ID / manifest identity | `FreeVideoStudio` |
| App C# and XAML root namespace | `FreeVideoStudio.App` |
| Core project | `src/FreeVideoStudio.Core/FreeVideoStudio.Core.csproj` |
| Core assembly / package ID / root namespace | `FreeVideoStudio.Core` |
| Test projects and assemblies | `FreeVideoStudio.App.Tests`, `FreeVideoStudio.Core.Tests` |
| Standalone installer output | `compiled/FreeVideoStudio.exe` — an Inno Setup package (NODROPPER_01, §3c DIST-INSTALLER) |
| Installed executable | `FreeVideoStudio.exe` (version resource `OriginalFilename`/`InternalName` = `FreeVideoStudio.exe`, PEIDENTITY_01) |
| Application / installer icon | `assets/icons/FreeVideoStudio.ico` (csproj `ApplicationIcon` + `InnoInstaller.IconPath`), no outer padding, transparent rounded corners (its source picture is no longer kept in the repository); sizes 16–256 px (APPICON_01) |
| Embedded payload resource | **none** — retired by NODROPPER_01; `AvCleanRuleTests` fails if one comes back |
| App-only archive | **retired** (`FreeVideoStudio.App.update.zip` is no longer built or published; smart updates replace it; the client code that could read one was removed too, COMPACTGONE_01) |
| Runtime manifest | `runtime.manifest.json` in the staged release and under `obj/ReleaseAssets` |
| Release download | `FreeVideoStudio.exe` (the single Inno Setup installer package) |

The internal tool names `FvsBuild` and `FvsVerify` are unchanged. The GitHub repository is `alonreich/Free_Video_Studio`; GitHub redirects the previous repository address, so old clients still reach `releases/latest`. Game-specific crop profiles and recording-discovery folders remain gameplay identifiers, not product branding. Historical storage names are compatibility data in `LegacyAppDataNames.txt` (`05` SYS-REBRAND). The full old→new map and update flow are in `REBRAND_MIGRATION.md`.

Local verification: the commands are canonical in [README §0.4](README.md#GOV-START); nothing is done until `tests\run-av-tests.cmd` prints `PASS` ([GOV-AVGATE](README.md#GOV-AVGATE)). Rebrand check: `dotnet test tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj --filter RebrandTests`.

`RebrandTests.PreviousBrandNameAppearsOnlyInTheMigrationAllowList` fails if the previous product name appears outside `LegacyAppDataNames.txt` and `REBRAND_MIGRATION.md`. Project GUIDs stay unchanged; solution paths, project references, source-generated serializer types, XAML namespaces and sentinel paths use the renamed projects. `ProductionNamespacesUseTheProductRoot` enforces production namespace declarations; `AppDataPathsTests` guards migration behavior. Build output must contain only the Inno Setup installer (`FreeVideoStudio.exe`), with release sidecars under `obj/ReleaseAssets`.

---

## 1. The Measured Problem  {#DIST-PROBLEM}

Numbers taken on 2026-09-21, against `arch/04-mvvm`:

| Thing | Size | What it means |
| :--- | ---: | :--- |
| `compiled/FreeVideoStudio.exe` | **322 MB** | what a user downloads to install |
| `binaries/` (FFmpeg + libmpv) | 368 MB | of which `avcodec-62.dll` alone is 97 MB |
| `mp3/` + `meme/` in the repo (`meme/` replaced `mp4/` + `jpeg/`, MEMEFOLDER_01) | 269 MB | starter media, committed |
| `.git` | **2.7 GB** | a fresh clone |

Two separate defects hide in that table, and they need separate fixes.

* **Every patch cost 322 MB (2026-09-21 baseline; fixed by [DIST-SMART](#DIST-SMART)).** `UpdateService`
  fetched one release asset — the whole installer — and reinstalled, so a one-line fix reshipped FFmpeg.

  ⚠️ The damage is not bandwidth, it is **delivery**. On a metered or slow connection the rational
  response is to turn updates off, and a user who has turned updates off does not receive the next
  fix either. Every shipped fix becomes a fix most users never get.

* **`git-lfs` is declared and was not installed.** `.gitattributes` routes `*.dll`, `*.exe`,
  `*.zip`, `*.mp3`, `*.mp4` through LFS. On a machine without `git-lfs` that declaration does
  nothing except break: `git status` in this working tree failed with
  `git-lfs filter-process: 1: git-lfs: not found`. A clone in that state gets pointer files, and
  the build fails later with a missing codec rather than with the real reason.

---

## 2. Why The Binary Is 322 MB  {#DIST-WHYBIG}

Invariant #1 — *Single Binary Executable Mandate, zero loose companion assets* — is the direct
cause. **NODROPPER_01 (2026-10-12, §3c):** the single download is now an Inno Setup package; nothing is
embedded in the program. The size (FFmpeg + libmpv + starter media) is unchanged — LZMA2 packs it
somewhat tighter than deflate did.

⚠️ **The mandate is worth re-examining, and this spec does not pretend otherwise.** It buys "one
file on disk", a property no user of a video editor has asked for. Remaining costs: a 322 MB first
install, the ban on a DI container (`COMPOSITION_01` cites `TrimMode=full`) and hand-written AOT-safe
JSON (`PROJ-AOT`). The repeat download is removed by [DIST-SMART](#DIST-SMART); the first-run
extraction by NODROPPER_01.

**This spec does not repeal it.** Repealing it is a product decision with a migration behind it —
installer, upgrade path, signing story.

---

## 3. Packaging, Manifests & Old-Client Bridges  {#DIST-SPLIT}

* **Current packaging (NODROPPER_01).** `Staging.WriteManifests` writes `runtime.manifest.json` (runtime
  fingerprint: ordinal-sorted paths, lengths and SHA-256 of codec libraries and starter assets; app
  executables, manifests and debug symbols excluded) and `install.manifest.json` into staging, plus
  `obj/ReleaseAssets/runtime.manifest.json`. `compiled/` contains only `FreeVideoStudio.exe`. The client
  chooses smart ([DIST-SMART](#DIST-SMART)), else the full installer; any doubt resolves to the full installer.
* **A manifest is a hint, never a trust decision.** Whatever is downloaded is SHA-256 verified against the
  signed list and Authenticode-checked (09 SYS-SIGNING, `UPDATETRUST_02`).
* **History — `SYS-PAYLOADSPLIT` / COMPACTGONE_01.** Releases before NODROPPER_01 also published
  `FreeVideoStudio.App.update.zip` (app-only package, chosen when the fingerprint matched). It was a second
  NativeAOT program carrying the app in its resources (the dropper pattern §3c removes). The package stopped
  being built at NODROPPER_01; the client code (`RuntimeAlreadyMatchesAsync`, `ExtractCompactInstaller`,
  app-only `UpdateRelease` fields) was removed on 2026-10-18, since a client only updates to the NEWEST
  release. Do not reintroduce it: choosing an app-only package against mismatched codecs fails at export
  time on the user's machine.

* **Build ordering (NODROPPER_01).** ONE NativeAOT publish of the program into staging; rewrite its
  PE identity (PEIDENTITY_01); sign it; stage dependencies; write the manifests; build the smart
  assets and the signed file list for the installer (`SmartUpdateBuilder.Prepare`); compile the Inno
  Setup installer into `compiled` (`InnoInstaller.Build`); sign it; write the release `update.json`
  that names it (`SmartUpdateBuilder.Finish`); validate that `compiled` holds exactly
  `FreeVideoStudio.exe`. The two extra NativeAOT publishes (embedding installer, compact
  installer) are gone, so a release build is also several minutes faster. The legacy filename aliases
  are never staged (REBRAND_03): `Staging` deletes any stale copy, and `GitHubReleasePublisher` creates
  them in a private temp folder for the upload only and deletes them afterwards.
* **Old updater bridge (REBRAND_03).** The legacy executable filename, taken from `LegacyProductIdentity`,
  remains a release asset containing identical bytes to the full installer (created transiently at publish time). This lets old
  clients discover the rebrand without maintaining a separate app. Do not remove this alias
  until dropping automatic migration from those released clients is an explicit product decision.
* **Old updater bridge (NODROPPER_01).** Every release before the Inno Setup installer looks for a full
  installer asset named `FreeVideoStudio.exe` and starts it with `--install --auto-update --source-current
  --wait-pid <id>`. The release therefore also carries the setup's bytes under that name
  (`GitHubReleasePublisher.PreSetupDownloadName`), and the script forwards exactly those switches to the
  program it unpacks, so those copies update normally (Inno shows its own small "Unpacking…" window for
  them, because they do not pass `/VERYSILENT`). Its version resource says `FreeVideoStudio.exe` —
  the one place a file name and its `OriginalFilename` differ, and only for that download. Remove the
  alias only together with automatic updates for copies released before 2026-10-12.
* **Publication transaction.** Upload all assets (setup, its two aliases, runtime manifest, smart files) to a draft created FROM THE PUSHED
  TAG (`--verify-tag`, RELTAG_01), verify every asset by name and SHA-256, then publish it as latest (a beta: as a pre-release, BETA_01; manual download only, the app reads `/releases/latest` and never offers it, SETNOBETA_01).
  Failed uploads stay unpublished. Publication never deletes an existing release first; after a successful
  publish, releases beyond the retention window lose only their downloadable files; pages, notes and TAGS ARE KEPT (RETAIN_01/RETAIN_02, §3d).

---

## 3a. Smart Updates: Only What Changed  {#DIST-SMART}

* **What a release publishes in addition (all verified by name + SHA-256 after upload):**
  * `update.json` — every installed file (path, length, SHA-256) with the content-addressed asset that
    holds it (`f-<sha256>.br`, Brotli, 16 MB window), plus binary patches for the main program;
  * `update.sig.json` — RSA-SHA256 signature of `update.json` by the publisher leaf, with the leaf
    certificate. Believed only when the leaf chains to the pinned root (UPDATEPIN_01);
  * `f-*.br` and `p-<from16>-<to16>.patch` files.
* **Content decides, never names.** `SmartUpdateManifest.Plan`: a file is reused only when SOME installed
  file has exactly its SHA-256 (a moved or renamed identical file is reused too); the same name with
  different bytes is downloaded. The client hashes the installed folder, downloads only the changed
  files (each decompressed file re-hashed against the signed list), rebuilds the main program from a
  patch made from exactly the installed program's SHA-256 when one exists, and falls back — on ANY doubt
  — to the full installer (the compact step is gone, COMPACTGONE_01). If the smart download would be ≥ 90 % of the full installer, the full installer is used.
* **Who installs it.** The new main program itself (Authenticode-checked against the pinned root) runs
  as the installer with `--smart-update <folder>`. The elevated worker re-verifies the signed list
  ITSELF, requires its version to equal its own, copies each downloaded file into its protected staging
  folder and hashes the copy there (so nothing in the user-writable folder is trusted after the check),
  hard-links identical installed files, and verifies the whole result (`InstallPayload.ExtractFromSmartUpdate`).
* **BINDELTA_01 — patches.** bsdiff-style (exact matches found through a hash index of the old file,
  extended with byte differences stored as new−old; shifted code becomes long zero runs) in a Brotli
  stream. The header names old and new by length and SHA-256; `Apply` refuses another old file and
  re-hashes its result, so a wrong or damaged patch is an error, never a wrong file. Bases: the last 3
  PUBLISHED versions (`obj\ReleaseHistory\<version>\`, marked `released.txt` after a successful publish);
  a patch larger than 60 % of the compressed program is dropped.
* **DELTABASE_01 — the bases are guaranteed, not hoped for.** A release build (`SmartUpdateBuilder.EnsurePublishedBases`)
  lists the last 3 published stable releases, downloads each one's signed `update.json` (+ signature, verified
  against the pinned root), takes the program's SHA-256 from it, and either marks a matching local copy
  `released.txt` or downloads that release's own content-addressed program asset (`f-<sha>.br`) and verifies
  it byte for byte. Before this, the history was consulted only when `obj\ReleaseHistory` was EMPTY, and then
  the asset named `FreeVideoStudio.exe` was fetched — the INSTALLER under its pre-Inno alias (DIST-SPLIT), not
  the program — so no installed copy could ever use the patch; and on the build machine the history held
  only unmarked builds, so release builds made NO patches at all and every user downloaded the whole
  compressed program on every update. Without the GitHub CLI the build warns and uses local history only.
* **ASSETHASH_01 — every asset is checked before it is used.** `update.json` carries the SHA-256 of each
  compressed asset and patch (`AssetSha256`); the client verifies the download against it, decompression is
  capped at the signed size, and a patch header must name exactly the signed program and the installed one
  (09 SYS-AUTOUPDATE). Older lists without the field still work (size cap + final hash). Measured on a synthetic 120 MB
  "recompiled" file: 1.7 MB patch, 6 s to build, 0.4 s to apply (in-memory apply, before BINDELTA_02;
  streamed disk-to-disk: 160 MB in 1.2 s incl. both SHA-256 passes).
* **BINDELTA_02 — applying streams, it never loads a program file.** `BinaryDelta.ApplyFile` /
  `Apply(Stream old, Stream patch, Stream output, …)`: the old EXE is length-checked and SHA-256-hashed
  from disk, the FVSDELTA1 blocks are decoded unchanged, the old file is seeked only when a block needs
  it, `new = old + diff (mod 256)` is computed in `StreamBufferSize` = 64 KiB chunks and written (and
  hashed) straight to a `.partial` file. Managed memory: 3 × 64 KiB, plus the Brotli decoder window
  (≤ 16 MB, format-fixed) — independent of program size (the old path held old EXE + new EXE in RAM).
  Any failure or cancellation deletes the partial. `UpdatePreparer` promotes it (`File.Move`, same
  folder) only after length + SHA-256 match the signed update.json AND the publisher check passes.
  `Apply(byte[], Stream)` remains for small inputs and runs the same decoder. Tests: `BinaryDeltaTests`
  (legacy-decoder parity, hand-written FVSDELTA1 patch, fragmented streams, bad hashes, truncation,
  cancellation, 192 MiB bounded-memory run). Measured: 160 MB file, 260 KB managed allocations,
  +26 MB peak working set.
* **Build cost.** Compressed assets are cached by content in `obj\ReleaseAssets\smart` (an unchanged file
  is not compressed again); assets no longer listed are removed.
* **D1 — dev.** `dev_build.cmd` produces local installer/update assets. Ordinary `dev.cmd` never consumes them or installs anything (05 SYS-DEVBUILD, DEVISOLATION_01). It uses Release managed code and the same `Staging.StageDependencies` function through `FvsBuild --dev-runtime`, keeping native binaries and starter media aligned with production (DEVPARITY_01). NativeAOT and installation still require their own validation.

* **UPDATESTATUS_01 — never announce a download that did not start.** `StartBackgroundUpdate` returns whether a background download started (false in dev or for a release tag that is not a safe folder name). A manual check says "downloading in the background" only on true; otherwise it records and shows that the update can't be downloaded automatically.

## 3b. After Every Public Release  {#DIST-NEWRELEASE}

* **NEWRELEASE_01.** After a SUCCESSFUL publish only (not `--dev`, not `--no-publish`, not exit 2),
  `build/FvsBuild/ReleaseChecklist.cs` prepares `obj/ReleaseSubmit/`: `FreeVideoStudio-program-<version>.zip`
  (the signed program from `obj/ReleaseHistory/<version>/`, password `infected` when 7-Zip is installed)
  and `submission-text.txt` (every Microsoft WDSI form field, SHA-256 of program and installer, release
  URL). It then opens `docs/New-Release.md` and that text file in Notepad++ (`-multiInst`; Notepad as fallback).
* Why the program and not the installer: the WDSI form accepts at most **50 MB**; the installer (`FreeVideoStudio.exe`) is ~300 MB.
* It never fails the build: the release is already public when it runs. Problems are warnings.
* The steps are manual by necessity: Microsoft's free submission form has no API (the API needs a paid
  Defender for Endpoint licence). `docs/New-Release.md` is the human checklist.

## 3c. The Installer Is An Inno Setup Package  {#DIST-INSTALLER}

* **NODROPPER_01 — WHY.** The release used to be one NativeAOT program that carried FFmpeg, libmpv, the
  starter media and a copy of itself as a deflate-compressed zip inside its managed resources, unpacked
  them on first run and started them. *An executable whose resources hold compressed executables that it
  writes to disk and runs* is the definition of a dropper; Defender's machine-learning detections
  (`Trojan:Win32/Wacatac.*!ml`) and other engines scored every new, low-reputation release that way. An
  Inno Setup package is a container every antivirus engine unpacks natively: each file in it is scanned
  and reputation-checked on its own, under its real name, and the Inno engine is one of the most common
  installers on Windows. The user still downloads ONE file.
* **What the installer does** (`build/installer/FreeVideoStudio.iss`, compiled by `InnoInstaller`):
  * runs WITHOUT administrator rights (`PrivilegesRequired=lowest`), creates no program folder, no
    registry entries and no uninstaller of its own (`CreateAppDir=no`, `Uninstallable=no`);
  * unpacks the staged release with its real layout into Inno's private `{tmp}\fvs` (Inno deletes `{tmp}`
    when it exits), beside the signed file list (`update.json` + `update.sig.json`, built by
    `SmartUpdateBuilder.Prepare` WITHOUT the full-installer entry, which cannot name its own hash);
  * starts `{tmp}\fvs\FreeVideoStudio.exe --install --smart-update "{tmp}\fvs"` as the user who started
    Setup (`runasoriginaluser`) and waits for it. Everything after that is the existing transactional
    installer (`09` SYS-UPGRADE): broker window, one UAC prompt, elevated worker (run from a protected
    `%ProgramData%\FreeVideoStudio-Worker-<id>` copy, WORKERSTAGE_01 / `09` SYS-UPGRADE-TRUST), which re-verifies the signed list
    itself, copies every file into its protected folder and hashes the copy
    (`InstallPayload.ExtractFromSmartUpdate`, which accepts real paths as well as content names), shortcuts,
    Apps & features entry, first-launch health check, rollback;
  * forwards ONLY `--auto-update`, `--source-current`, `--no-launch`, `--quiet` and `--wait-pid <number>`
    (`ForwardedArguments`), so a command line can never steer the unpacked program into another mode.
* **Trust is unchanged.** `UpgradeCoordinator.SmartFromArgs` accepts the folder because it is the folder
  the running program was unpacked into; the elevated worker never trusts that check and verifies the
  list's signature against the pinned root (UPDATEPIN_01) and every byte against the list. A program
  started without a signed list refuses to install ("This program is not an installer. Download
  FreeVideoStudio.exe…").
* **In-app full updates** start the setup with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` plus the usual
  switches, so the user sees only the update window (UPGRADEUX_01). The publisher check
  (`AuthenticodeVerifier`) is applied to the setup exactly as it was to the old installer.
* **Signing.** `CodeSigning.SignIfNeeded` signs the program in staging and then the finished setup. The
  small Inno engine Setup unpacks for itself (`is-*.tmp`) is Inno's own, byte-identical in every
  installer built with the same Inno version — left as shipped so it keeps that reputation.
* **Build requirement.** Inno Setup 6.3 or newer (free): `winget install JRSoftware.InnoSetup`, or set
  `FVS_ISCC` to `ISCC.exe`. Missing → the build fails with that instruction. A build without the
  update-list signing certificate fails too (the installer installs only from a signed list).
* **Measured / to verify on Windows.** Compression `lzma2/max`, solid. Real-Windows checks still needed:
  double-click install, in-app full update (`/VERYSILENT`), an old client updating through the
  `FreeVideoStudio.exe` alias, UAC No, and a Defender scan of the setup.

---

## 3d. Release Retention Policy, Local Artifacts & Rolling Changelog  {#DIST-RETENTION}

* **Local Output Directory (`.\compiled\`) Invariant:** `.\compiled\` strictly holds **only the single latest installer** (`FreeVideoStudio.exe`). Any stray files, extra sidecars or older binaries are purged and treated as build failures (`Staging.ValidateCompiledOutput`).
* **RETAIN_02 — retention removes FILES, never history (2026-10-18).** `PruneOldReleases` used to run `gh release delete`, which deletes the release PAGE and its NOTES with the files. It now removes only the downloadable files of a release beyond the window (`gh release delete-asset`, oldest first, only after the new release is verified and public); the release, its title, its notes and its tag stay forever, and a release that already has no files is skipped. The decision (`ReleasesToPrune`) is unchanged. The bullet below is the older rule it refines.
* **RETAIN_01 — Official GitHub Release Retention (last 5 stable releases, tags kept forever):** after a successful publish `GitHubReleasePublisher.PruneOldReleases` keeps the newest **5 stable releases** (the new one included) and the newest **3 betas that are still newer than the newest stable** (`ReleasesToPrune`, ordered by VERSION, drafts ignored); every other release is pruned — files only since RETAIN_02 (`gh release delete-asset`), never the tag. The old `--cleanup-tag` deleted the git tag too, so any version a user still ran stopped being traceable to its source after the sixth release, contradicting the publisher's own promise. Tags are never deleted.
* **Rolling Changelog Contract (`docs/CHANGELOG.md` + `docs/changes.md`):** see §3f DIST-RELEASENOTES. The user changelog keeps exactly the 5 latest releases plus `## Upcoming`; the technical log keeps 10 plus `## Unreleased`. Both are pruned by the release itself (`ReleaseChangelog.Promote`), not by memory. **RELRECORD_01: pruned sections move to `docs/CHANGELOG_ARCHIVE.md` / `docs/changes_ARCHIVE.md` (`ReleaseChangelog.Archive`, newest first, written before the main file, never duplicated); full history is kept.** Every `## v…` heading in `docs/CHANGELOG.md` must be a pushed tag (CI job `Release records`, and the gate). The four headings that named versions never published (v2026.10.04.1120 – v2026.10.13.2238) were moved to the archive on 2026-10-18.
* **Smart Update Delta Continuity:** DELTABASE_01 (§3a): every release build makes sure the last 3 published programs are patch bases, verified against their signed update lists.
* **Pre-Flight Disk Space & Connection Awareness:** UPDATESPACE_01 (09 SYS-AUTOUPDATE): free space is checked per drive for what an update really writes (smart: the changed files; full: the download plus what the installer unpacks). (The metered-connection preference was removed, SETNOMETERED_01.)

---

## 3e. The Release Gate  {#DIST-RELEASEGATE}

* **RELGATE_01 — WHY.** `Build.cmd` used to publish whatever was on disk: it never checked that the
  working tree was committed or pushed; it rewrote five version files before building and left them
  uncommitted (a failed build left them changed too); and `gh release create <tag>` without a target
  tagged the REMOTE default branch — not the commit that was built. A release could ship code that exists
  in no commit under a tag pointing at different code, while the repository's version fell behind
  every release (the checked-in version said 2026.10.13.2238 while later builds were published).
* **The transaction** (`build/FvsBuild/ReleaseGate.cs`, ordered by `Program.RunPipeline`):
  1. `Preflight` — changes NOTHING. Refuses, listing every reason at once, unless: `git status --porcelain`
     is empty (tracked AND untracked); HEAD is a branch, and for a stable release it is the default branch
     (`origin/HEAD`, else `main`); after `git fetch` the branch equals `origin/<branch>` exactly (pushed, not
     behind); the tag `v<version>` exists neither locally nor on GitHub (versions are the UTC minute — two
     releases in one minute: wait a minute); the GitHub CLI is signed in; `docs/CHANGELOG.md` has a
     `## Upcoming` section whose bullets pass `ReleaseChangelog.LintUserNotes`; `docs/changes.md` has its
     `## Unreleased` section. It then prints the version, branch, commit and the notes and publishes.
     **RELNOPROMPT_01 (owner decision 2026-11-22):** there is no `PUBLISH` prompt; running `Build.cmd` is
     the confirmation. `--yes` is still accepted (ignored) for old scripts; `FVS_RELEASE_UNATTENDED` and
     RELQA_02 are retired. Every other gate check above and below still refuses a bad release.
     **RELQA_01 — before that question:** the GitHub check runs of the starting commit must be green —
     the latest run of each of `Fix sentinels`, `Release records`, `Build + test (Windows)`,
     `LFS pointers resolve`, and for a stable release `NativeAOT publish` (`RequiredChecks`, `AotCheck`,
     `CiProblems`; read with `gh api repos/{owner}/{repo}/commits/<sha>/check-runs`). Then the fix
     sentinels and both test suites run on this machine (`RunLocalChecks`) and must pass without touching
     tracked files. It used to compile, sign and publish without running a single test.
     **ROOTKEY_01:** the publisher root key must not open with the password stored beside it
     (`CodeSigning.RootKeyIsProtected`; a key kept offline counts as protected).
     **RELRECORD_01/02:** every version heading in `docs/CHANGELOG.md` is a tag on GitHub
     (`HeadingsWithoutTags`), and (stable) the `## Upcoming` notes are not the newest published release's
     notes again (`SameNotes`) — that is the "publish worked, promotion failed" state, finished by `--resume`.
  2. `CommitVersion` — stamps the five version files (`SynchronizeVersionFiles`), commits ONLY them as
     `release: v<version>` and creates the annotated tag on that commit. The build compiles that commit.
  3. Build. Any failure → `Undo`: `git reset --hard <starting commit>` + tag deleted. Safe by construction:
     step 1 proved the tree was clean, so only the stamp is discarded.
  4. `TreeStillClean` — the build must not have changed a tracked file (else the binary would not be the
     tagged source) → `Undo`.
  5. `PushRelease` — `git push --atomic origin HEAD:<branch> refs/tags/<tag>`. Failure (someone pushed
     meanwhile, no network) → `Undo`, exit 2, nothing published.
  6. `GitHubReleasePublisher.Publish` — draft from the pushed tag (`--verify-tag`, RELTAG_01), verify every
     asset by SHA-256, publish (stable: `--latest`; `--beta`: `--prerelease`, BETA_01), prune (RETAIN_01).
     A failure here leaves the commit and tag on GitHub without a release: exit 2, and the notes stay in
     `## Upcoming`. **RELRESUME_01 — `Build.cmd --resume` finishes THAT release** (it used to be a dead end: a
     new run could only build a NEW version, leaving a tag with no release and version files naming a
     download that never existed). `PreflightResume` changes nothing until confirmed and proves: clean tree,
     HEAD is exactly the commit of `v<version.txt>`, the tag is on GitHub, `compiled\FreeVideoStudio.exe`
     and `obj\ReleaseAssets\update.json` are that version. Then it publishes (an earlier unfinished draft of the
     tag is deleted first — never published as it is; the tag stays), marks the patch base, and promotes the
     notes; or, when the release is already public, only promotes the notes. It never builds or stamps.
  7. Stable only, after success: `RecordReleaseNotes` promotes `## Upcoming` (CHANGELOG.md) and
     `## Unreleased` (changes.md) to `## v<version> — <date>`, prunes both, commits
     `docs: release notes for v<version>` and pushes. Never fails the release (it is public); problems are
     warnings with the manual step named. Then NEWRELEASE_01 (§3b).
* **Modes.** `Build.cmd --resume` = finish the last release (RELRESUME_01). `FvsBuild --protect-root-key` = give the root key its own passphrase (ROOTKEY_01, `09` SYS-SIGNING). `Build.cmd` = stable release (gate + publish). `Build.cmd --beta` = pre-release for the Beta
  channel, from any pushed branch; its notes are the current `## Upcoming` prefixed with a beta line, and
  nothing is promoted. `Build.cmd --no-publish` and `dev_build.cmd` (`--dev`) never touch git, GitHub or the
  version files (the version reaches the compiler through `-p:Version=`). `--stage-only` is a diagnostic.
* **Versioning.** Calendar version `yyyy.MM.dd.HHmm` (UTC). Why not SemVer: 09 SYS-AUTOUPDATE.
* Tests: `ReleaseChangelogTests` (promotion, retention, lint, `ReleasesToPrune`, `NewVersion`),
  `ReleaseIntegrityTests` (CI verdicts incl. reruns, the gate's job names exist in the workflows, headings
  without tags, notes comparison, archive, revocation list, root-key protection). The git and GitHub steps
  need a real repository and are verified by running `Build.cmd` (human only).

## 3f. Release Notes: Technical Log → User Notes  {#DIST-RELEASENOTES}

* **RELNOTES_01 — two logs, one parser (`Core/Infrastructure/ReleaseChangelog.cs`).**
  * `docs/changes.md` — the TECHNICAL change log. Every change an agent or developer makes is recorded
    under `## Unreleased` as it happens: date, engineering tag, what changed and why, files touched.
    Jargon is welcome here. Kept: 10 releases.
  * `docs/CHANGELOG.md` — what USERS read. Plain, short, oversimplified English under `## Upcoming`:
    one bullet per user-visible capability or fix, `**Headline:** one or two sentences`. Kept: 5 releases.
  * The staging headings are FIXED (`## Upcoming`, `## Unreleased`) — never a version. The old staging
    heading was a GUESSED version (`## v2026.10.16.1230 (Upcoming)`) while the build takes its version from
    the clock, so the publisher never found the notes and every release went out with the one-line
    placeholder "Free Video Studio vX. Existing settings are preserved during migration." — and the update
    card, documented as showing them, never displayed release notes at all.
* **The translation step (technical → user) is a person's or an agent's job, done continuously.** With each
  change, the author also adds or MERGES one user bullet in `## Upcoming` (update an existing bullet
  rather than adding a near-duplicate). Before a release, the human preparing it reads `## Unreleased`,
  checks every user-visible item there is represented, and condenses `## Upcoming` to at most 15 bullets.
* **RELNOTES_02 — the lint (`ReleaseChangelog.LintUserNotes`), enforced by the release gate and by
  `ArchitectureRuleTests.ChangelogEnforcesTopStagingAndFiveReleaseLimit`:** at least 1 and at most 15
  bullets; each under 320 characters; no backticks; no engineering tags (`ABC_01`); no source file names
  (`.cs`, `.axaml`, `.cmd`, …); no code (`Class.Member`, `Method()`); no repeated headline.
* **What happens at release:** the `## Upcoming` bullets become the GitHub release body (`--notes-file`);
  after a successful stable publish both staging sections are promoted to `## v<version> — <date>` with a
  fresh empty staging section above, and the oldest releases beyond the retention are removed
  (`ReleaseChangelog.Promote`). History is preserved: 5 public releases in CHANGELOG.md, 10 in
  changes.md, everything older in the two archives (RELRECORD_01), and every GitHub release body forever
  (RETAIN_02 keeps the release pages).
* **CHANGELOG_02 — the duty is enforced, not only asked for.** CI (`ci.yml` › `Release records`) fails a push
  or pull request that changes `src/`, `build/`, `tests/` or the `.cmd` scripts without changing
  `docs/changes.md`. FvsBuild's own release commits are exempt; `[no-changelog]` in a commit message exempts
  a change on purpose. The user bullet in `## Upcoming` stays a judgement call (internal changes need none).
* **RELNOTES_03 — users see them.** The update card shows up to four headlines as "What's new"
  (`ReleaseChangelog.PlainHighlights(…, headlinesOnly: true)`, 04 / 09 SYS-AUTOUPDATE).
* **WHATSNEW_01 — and see them again after updating.** The downloaded release's notes are remembered
  (`update_pending_notes.txt`); the new version's first-launch card lists up to six of them once the update is
  confirmed (and then waits for OK), and Settings › About has "What's new in this version", which opens that
  version's GitHub release page (or the list of releases). 09 SYS-AUTOUPDATE.

## 3g. Upgrade Smoke Test  {#DIST-UPGRADESMOKE}

* **UPGRADESMOKE_01.** `.github/workflows/upgrade-smoke.yml` (manual, free GitHub-hosted Windows runner):
  installs the installer of an OLD public release (default `v2026.09.26.1215`, the only one published before
  the rebrand/Inno/pinned-root changes) the way its own updater starts it (`--install --auto-update`), then
  runs the NEW release's (or draft's) `FreeVideoStudio.exe` exactly as the in-app updater does
  (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART --install --auto-update --source-current --no-launch`) and fails
  unless `Program Files\FreeVideoStudio\FreeVideoStudio.exe` is the new version; logs are uploaded. Run it
  before making a draft public. It writes nothing. ⚠️ Its first run is itself the verification of the
  workflow (a hosted runner has no prior install, UAC is off there): read the logs once.

## 3h. The Optional Local AI Pack  {#DIST-LOCALAIPACK}
* **`LOCALAIPACK_01`.** The Local AI pack (`10` SYS-LOCALAI) is NOT part of the installer, the smart update or the program: North Star #1 and NODROPPER_01 stand. The user downloads it on purpose from Settings › AI.
* **Where it is published:** the GitHub release `localai-v1` of this repository, created as a **pre-release** so `/releases/latest` (the app's update channel, SETNOBETA_01) can never pick it up. A new pack = a new tag (`localai-v2`) and `LocalAiPackManifest.PackVersion` / `ReleaseTag` bumped together; old packs stay downloadable for older app versions.
* **Pinning:** the program carries every file's URL, byte length and SHA-256 (`LocalAiPackFiles.cs`, generated). A file whose bytes differ is deleted and nothing is installed; the installed copy is re-checked before each use. Replacing an asset on the release therefore breaks the pin on purpose — publish a new tag instead.
* **Building it (human, never CI, never `Build.cmd`):**
  1. `developer_tools/local_ai_pack/export_sam2_onnx.py --checkpoint sam2.1_hiera_small.pt --out <folder>` (needs PyTorch + the `sam2` package; writes the two float16 `.onnx` files in the model contract of `LocalAiPackManifest`).
  2. `developer_tools/local_ai_pack/build_pack.py --pack <folder>` downloads `onnxruntime.dll` (NuGet `Microsoft.ML.OnnxRuntime.DirectML` 1.22.0) and `DirectML.dll` (NuGet `Microsoft.AI.DirectML` 1.15.4) from nuget.org, refuses either unless its Authenticode signature is valid and Microsoft's, writes `LocalAiPackFiles.cs`, and prints the `gh release create … --prerelease` / `gh release upload` commands. It publishes nothing.
  0. One command does steps 1–3 (`LOCALAIPACK_02`): `developer_tools\local_ai_pack\Make_Local_AI_Pack.cmd` builds a private Python environment and the pack under `developer_tools\local_ai_pack\out\` (git-ignored), writes the pins and the runtime test folder `out\ort` (DLLs + `tiny.onnx`), then asks **Publish now? [Y/N]**; only Y creates the pre-release and uploads, downloads every file back and compares size and SHA-256, and only then writes `Released = true`. `-NoPublish` stops after the local build. Once `out\pack` and `out\ort` exist, `tests\run-av-tests.cmd` runs the Local AI real-model tests by itself (LOCALAITEST_01). `build_pack.py` writes `LocalAiPackFiles.cs` without comments (NOCOMMENTS_01).
  3. A human runs those two commands, then runs `build_pack.py --pack <folder> --released` (same files, same pins, `Released = true`) and commits `LocalAiPackFiles.cs` with a `changes.md` entry. Until `Released` is true Install is disabled and the pack state is unpublished; `LocalAiPack.CheckDetector` explicitly reports `LocalDetectorState` (`PackUnavailable`, `Absent`, `Damaged`, `Installed`) so local discovery falls back honestly to built-in motion/standout tracking without claiming neural object detection (`AICAPABILITY_01`).
* **Antivirus hygiene:** the DLLs keep their Microsoft signatures; the `.onnx` files are data; nothing is unpacked from an archive, embedded in the program or started as a process; files land in the user's own AppData folder after a user click, and are loaded by full path.

## 4. Repository Weight  {#DIST-REPOWEIGHT}

* **`SYS-REPOWEIGHT` — LFS is enforced, not just declared.** `.github/workflows/lfs-guard.yml`
  asserts three things on every push: `git lfs version` succeeds, no tracked file is still a
  pointer after checkout, and no file over 5 MB is committed outside LFS.

  ⚠️ The third check is the one that matters long-term. LFS only helps going FORWARD; a history
  rewrite cannot stop the next large file, and this can.

* **The 2.7 GB is history, and history is the user's decision.** The media in `mp3/`, `mp4/` and
  `jpeg/` (now merged into `meme/`, MEMEFOLDER_01) was committed before LFS was configured, so the blobs are in every clone forever. Fixing
  it means rewriting history, which changes every commit hash and breaks every existing clone,
  fork, branch and open PR.

  **This is deliberately not automated.** The runbook:

  1. Everyone pushes and merges outstanding work. A rewrite orphans anything not on the remote.
  2. `git clone --mirror` the repository and keep that copy untouched until the new one is proven.
  3. `git filter-repo --path mp3/ --path mp4/ --path jpeg/ --path meme/ --path binaries/ --invert-paths`
     (`filter-repo`, not `filter-branch` — the latter is slow and its author recommends against it).
  4. Re-add the media through LFS in a single fresh commit, or move it to release assets — the
     starter media is shipped in the installer and does not need to be in the source tree at all.
     `.gitattributes` now routes `meme/*.png`, `meme/*.jpg` and `meme/*.jpeg` through LFS as well
     (MEMEFOLDER_01); images elsewhere in the tree are deliberately not affected.
  5. `git push --force --all` and `--tags`, then everyone re-clones. Not "pulls" — **re-clones**.

  ⚠️ Expected result is a repository in the low hundreds of MB. ⚠️ Expected cost is that every
  existing clone must be discarded. Do not start this on a Friday.

---

## 5. What CI Watches  {#DIST-CIWATCH}

`SYS-CI`'s `aot-publish` job prints the fifteen largest files in the publish output and the total
size on every push to `main`. Its `records` job (`Release records`) checks RELRECORD_01 and CHANGELOG_02 on
every push and pull request; the release gate requires it green (RELQA_01).

⚠️ **It measures only the app's `dotnet publish` folder, NOT the user download.** CI never runs
`FvsBuild` (and since NODROPPER_01 nothing is embedded in the program at all). The printed total is the app alone — roughly what an app-only
patch would cost — while the 322 MB installer (FFmpeg, libmpv, starter media) is not measured
anywhere in CI. A number nobody prints is a number nobody defends; this one is only half printed.

---

## 6. Binary Metadata & Authenticode Signing Mandate  {#SYS-SIGNING}
* **Win32 Executable Metadata:** The compiled `.exe` embeds complete production metadata (Product Name, Publisher, Assembly Version, File Version, Legal Copyright).
* **PEIDENTITY_01 — the binary names its own file.** NativeAOT copies the version resource from the managed assembly, so the program said `OriginalFilename = InternalName = FreeVideoStudio.dll` while it is `FreeVideoStudio.exe` — the renamed-binary masquerading pattern (MITRE T1036.003) antivirus scores. The SDK has no property for these strings, so `FvsBuild` (`PeIdentity.FixOriginalFilename`) rewrites them in the VS_VERSION_INFO block of the `.rsrc` section, in place (`.dll` → `.exe` is length-preserving; no offset, size or checksum moves; nothing outside that block can change), right after the publish and BEFORE signing. Idempotent; a file without a version resource fails the build. Measured on the 2026.10.11.1008 program: exactly 6 bytes changed. `ReleaseGate.SynchronizeVersionFiles` (official releases only, inside the release commit — RELGATE_01) also stamps `app.manifest`'s `assemblyIdentity version` with the build version (it said `1.0.0.0`). The Inno Setup installer's own resource says `OriginalFilename = FreeVideoStudio.exe`, Company `Alon Reich`, the same version (09 DIST-INSTALLER). Tests: `PeIdentityTests`.
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
  * **`FVS_ALLOW_UNSIGNED_UPDATE=1`** restores the old behaviour for developers. It is read from the **environment** on purpose — it cannot be set by a downloaded payload, a settings file or a server response.
  * **`UNSIGNEDDEV_01` — and it exists ONLY in Debug builds.** The override used to be honoured by every build, released ones included, so any program running as the user could set one environment variable and make the user's installed copy run an unsigned "update" with administrator rights. The branch is now compiled only under `#if DEBUG` (explicit Debug updater tests only; `dev.cmd` now runs Release and disables updates) and additionally requires the dev update source (`UpdateService.DevReleaseFolder`). A Release build — every copy a user can download — has no code path that accepts `NoAnchor`. The same rule governs the protected worker copy (`ProtectedWorkerStage`, SYS-UPGRADE-TRUST). Enforced by `ReleaseHardeningTests.UnsignedDev01_*`.
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
* **SIGNDEPS_01 — bundled tools are signed only with a publicly trusted certificate (2026-11-11).** FFmpeg, ffprobe, mpv
  and their DLLs (12 files) ship unsigned. Smart App Control, the ASR "prevalence" rule and company allow-lists judge each
  file on its own, so an allowed app can still have its FFmpeg blocked (dead preview, failing export).
  `CodeSigning.SignDependenciesWhenPubliclyTrusted` (after the program is signed, before the manifests) signs every
  unsigned `.exe`/`.dll` in staging — but ONLY when `FVS_SIGN_PFX` is set. With the repository certificate they stay
  byte-identical to upstream: Microsoft's reputation is per file hash, and re-signing with a certificate nobody trusts
  would discard any reputation those exact bytes have and gain nothing. Already-signed files are never re-signed. The
  first release that signs them changes their hashes once (one larger smart update).
* **FREETRUST_01 — free routes to a trusted signature (no purchase).** Only a certificate from a public CA (or Microsoft's
  Store signing) clears Smart App Control and the "unknown publisher" screen. The no-cost options are listed in
  `docs/New-Release.md` › "Free ways to be trusted": (1) SignPath Foundation — free code signing for open-source
  projects (requires an OSI-approved licence and signing from GitHub Actions — CHOSEN 2026-11-11: the repository is
  now GPL-3.0, see SIGNPATH_01); (2) the Microsoft Store — individual developer registration is
  free and Store-delivered MSIX packages are signed by Microsoft (requires an MSIX package; the elevated transactional
  installer and self-update do not apply inside MSIX); (3) WDSI submissions per release (NEWRELEASE_01), which build file
  reputation but not publisher trust. A public certificate is accepted by every installed copy alongside the pinned
  root (UPDATEPIN_01); `update.json` stays signed by the repository leaf.
* **SIGNPATH_01 — accepting SignPath Foundation's certificate without trusting its name (2026-11-11).** The free
  open-source certificate's publisher is "SignPath Foundation" — shared by every project they sign — so "same publisher
  as the running program" proves nothing, and copies signed with the repository certificate (no public chain) would
  return `NoAnchor` and REFUSE the first SignPath-signed release. `AuthenticodeVerifier.EvaluateUpdateCandidate` now
  takes the SHA-256 from the publisher-signed update list (`listedSha256`): a file whose signer CN is exactly
  `SignPath Foundation` (`IsSharedPublisher`) is Trusted only when Windows fully trusts the signature AND its hash
  equals the listed hash (`EvaluateSharedPublisher`); otherwise Rejected — even when the running program is
  SignPath-signed too. Callers: `UpdatePreparer` (smart: the program's `exe.Sha256`; full: `FullInstaller.Sha256` of the
  verified list, null when no list could be verified → refused), `ProtectedWorkerStage` (the hash of the running
  program: the copy must be byte-identical). The shared certificate never vouches for `update.json`
  (`VerifySignature`'s `alsoTrustThumbprint` is withheld for it in `UpdatePreparer` and `UpgradeInstallWorker`); the list
  stays signed by the repository leaf under the pinned root. ⚠️ ORDER: at least one release carrying this code must be
  published (signed as today) BEFORE the first SignPath-signed release; copies older than that must reinstall by hand
  once. Other SignPath conditions met in the repository: `LICENSE` (GPL-3.0), `CODE_SIGNING_POLICY.md`, `PRIVACY.md`
  (linked from the first-start update question, `UpdateService.PrivacyPolicyUrl`), `THIRD_PARTY_NOTICES.md` (FFmpeg /
  mpv source offer), Creative-Commons starter songs (OSSMEDIA_01, `03`). Still to do after acceptance: a GitHub Actions
  release build with the SignPath signing step (verifiable builds, manual approval). Tests: `SignPathTrustTests`.
* **ROOTKEY_01 — the root key has its OWN passphrase and may live offline (2026-10-18, free).** The root
  private key (`ssl-certificate\fvs-dev-root-ca.key.pem`) was encrypted with the SAME password that sits in
  plain text in `fvs-codesign.password.txt` beside it, so a copy of that folder was a copy of the key every
  installed copy trusts until 2036. `dotnet run --project build\FvsBuild -- --protect-root-key` re-encrypts it
  (AES-256-CBC, PBKDF2-SHA256, 600 000 rounds) with a typed passphrase of ≥ 12 characters that is never stored,
  verifies the result and replaces the file; keep a copy offline (USB stick) and delete it from the PC. The
  key is needed only to issue a new leaf: `CodeSigning.OpenRootKey` takes it from `FVS_ROOT_KEY` (path) or the
  folder, unlocked by `FVS_ROOT_KEY_PASS`, a typed passphrase, or — legacy, warned — the signing password file.
  The release gate refuses (stable and beta) while the key opens with the stored password
  (`RootKeyIsProtected`; an absent key counts as protected). The LEAF key stays usable unattended (it is
  revocable, REVOKE_01).
* **REVOKE_01 — a leaked leaf can be switched off (free, no CA).** `build/revoked-signers.txt` (public SHA-256
  thumbprints, in git) = `PublisherRoot.RevokedSignerThumbprints` (compiled; a test keeps them equal).
  `PublisherRoot.Vouches` refuses a listed leaf — for installers (Authenticode) and update lists alike;
  `SmartUpdateManifest.VerifySignature` refuses it too. Every signed `update.json` carries the list
  (`RevokedSigners`); the in-app updater remembers it per user (`update_revoked_signers.txt`,
  `PublisherRoot.AddRevoked`) and never forgets. FvsBuild refuses to sign with a listed leaf. Procedure in the
  file's header: new leaf from the same root, list the old thumbprint in both places, release.
* **SIGNEXPIRY_01 — a list fetched now needs a leaf valid now.** The update-list signature is plain RSA with no
  timestamp, so "timestamped, expiry does not matter" (UPDATEPIN_01) does not apply to it. The in-app updater
  calls `VerifySignature(…, requireCurrentSigner: true, now)`; the elevated worker and an installer's packed
  list (legitimately run years later) do not. Consequence: renew the leaf before it expires (UPDATEPIN_02 does
  it at < 90 days) — a wrong PC clock shows a plain "check the date and time" reason.
* **UPDATEPIN_02 — the leaf renews itself.** Before signing, `CodeSigning.EnsureLocalLeaf` issues a new
  leaf from the SAME root (key `ssl-certificate\fvs-dev-root-ca.key.pem`, never in git) when the leaf is
  missing, unreadable, not from the pinned root, or within 90 days of expiry; old files are kept as
  `*.bak-<time>` (git-ignored). `build.cmd` and `dev_build.cmd` use the same certificate. Copies released
  BEFORE UPDATEPIN_01 still need one manual install of a newer version; after that, updates are automatic.
* **UPDATEPIN_PEM_01 — preserve the embedded public certificate verbatim.** Base64 inside the raw
  PEM string may contain `//`; it is data, not a C# comment. Removing those characters corrupts the
  trust anchor and prevents signing and update verification. `SmartUpdateTests.ThePinnedRootIsIntact`
  parses it and checks the unchanged SHA-256 pin.

---

## 7. Auto-Update Lifecycle & Universal Build Versioning  {#SYS-AUTOUPDATE}
* **Universal Build Version Synchronization:** Every `FvsBuild` run (dev, `--no-publish`, release) generates a uniform four-part calendar version (`yyyy.MM.dd.HHmm`, UTC minute — `ReleaseGate.NewVersion`) and passes it to the NativeAOT publish (`-p:Version=`, `-p:AssemblyVersion=`, `-p:FileVersion=`, `-p:InformationalVersion=`), the Inno Setup installer and the signed update list. **`RELGATE_01` — only an official release writes it into the repository**, and only inside the release commit `release: vX` that is tagged and built (09 DIST-RELEASEGATE). `ReleaseGate.SynchronizeVersionFiles` stamps:
  1. `version.txt` in the repository root.
  2. `Directory.Build.props` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>`, `<ProductVersion>`).
  3. `src/FreeVideoStudio.App/FreeVideoStudio.App.csproj`.
  4. `src/FreeVideoStudio.Core/FreeVideoStudio.Core.csproj`.
  5. `src/FreeVideoStudio.App/app.manifest` (`assemblyIdentity version`).
  Dev and `--no-publish` builds leave all five untouched (before RELGATE_01 a `--no-publish` build rewrote them and left the tree dirty, and a release left them uncommitted, so the repository's version fell behind every release).
* **Why calendar versions and not SemVer (decision, 2026-10-17).** Every installed copy compares versions numerically (`CompareVersions`); a switch to `1.x` SemVer would sort BELOW every installed `2026.x` copy and turn the next update into a refused downgrade. The calendar scheme stays; it is strictly increasing because the gate refuses a tag that already exists (two releases in the same UTC minute: wait a minute). What SemVer would add — "how big is this change" — is said in plain words in the release notes (RELNOTES_01).
* **Title Bar Version Invariant:** The running executable extracts its stamped version via `DeploymentLifecycle.GetCurrentVersion()` (reading Win32 `ProductVersion` and `FileVersion`, assembly metadata, and root `version.txt` fallbacks). Custom window title bars format `Free Video Studio v{version}` and `Free Video Studio - Video Merger v{version}` directly, ensuring zero discrepancies.
* **Version Parsing Robustness:** `DeploymentLifecycle.TryParseVersion` trims leading `'v'`/`'V'` prefixes before filtering numeric dot segments. Tags such as `v2026.09.12.0159` parse accurately into .NET `Version` objects (`2026.9.12.159`) with strict numerical comparison. Invalid or non-numeric inputs return `false` and guarantee a safe non-null `0.0` fallback.
* **UPDATECONSENT_01 — no network at startup, and only after a yes (2026-10-12).** An unknown program that contacts a server and downloads executables the moment it starts is what downloader malware does, and antivirus behaviour engines score it that way. `RunStartupCheckAsync` therefore (1) asks ONCE — "Check for updates automatically?" (`AskUpdateConsentAsync`, a `ConfirmDialogWindow` over the main window, 3 s after it opens) — while `AppSettings.UpdateConsentAsked` is false, and sends nothing before the answer; Yes/No are stored by `SettingsManager.SetUpdateConsent` (`AutoUpdateChecks` = the answer, `UpdateConsentAsked` = true); closing the question counts as "not now" and it is asked on the next start; (2) waits `StartupGracePeriod` = **5 minutes** after the window opens before the first request (dev source: 2 s). `UpdateConsentAsked` is additive (missing reads as false, no schema bump), so every existing install is asked once as well. Changing the Settings › General update checkbox also records the answer. Help › Check for Updates is unaffected. `--upgrade-health` launches never ask. Meme/song cloud sync and the AI features were already user-triggered only. Test: `VersionAndUpdaterTests.UpdateConsent01_*`.
* **Network Stall Guard:** `UpdateDownloader` wraps chunk stream reads in a 45-second stall cancellation timeout (`CancellationTokenSource.CreateLinkedTokenSource`). A frozen HTTP pipe stops the download and is REPORTED as a failure ("stopped receiving data for 45 seconds"); it is never treated as the user's own Cancel (UPDATEUX_03 — it used to close the window silently).
* **Release Notes (RELNOTES_03):** GitHub release `body` is read during the probe. It is the `## Upcoming` section of `docs/CHANGELOG.md` at release time (09 DIST-RELEASENOTES). The update card shows up to four of its headlines as "What's new" while the update downloads and when it is ready (`ReleaseChangelog.PlainHighlights`, `UpdateToastViewModel.SetReleaseNotes`). Before this the body was read and never shown, and it was a one-line placeholder anyway because the publisher never found the notes.
* **State Persistence Protocol:**
  * Last startup probe timestamp is recorded in `update_last_check_utc.txt` under `UiStateStore` enforcing a 15-minute rate limit (`MinimumIntervalBetweenChecks`) between startup probes.
  * UPDATEUX_06 — the time and plain-English result of every check, startup or manual (up to date / version X available / skipped / could not reach GitHub, and why), is written to `update_last_result.txt` and shown in Settings › About as "Last checked 5 minutes ago: …". The startup check still never interrupts the user over its own problems; it is no longer invisible.
  * Explicit version skips write the release tag to `update_skipped_tag.txt`. Users can inspect or clear this filter at any time via the About tab in Settings (the update preferences themselves are on the General tab, SETTINGSIA_01).
* **BGUPDATE_01 — background update and user control (R2/R3).** A newer version found by the startup
  check (or Help › Check for Updates) is downloaded in the BACKGROUND (`UpdateService.StartBackgroundUpdate`
  → `UpdatePreparer`) while the user keeps working; the update card (`UpdateToastWindow`, 04) shows
  progress. Background downloads follow the auto-download toggle (`AutoDownloadUpdates`; the metered-connection
  option was removed, SETNOMETERED_01), with per-drive disk space verification (UPDATESPACE_01, below).
  When everything is verified the installer is started and waits (`--wait-pid`, plus
  `--smart-update <folder>` for a smart update); the card then presents: **Restart & update**
  (closes the app through its NORMAL close path — unsaved work is still asked about, nothing is killed,
  and restarts into the new version), **Later** (installs with `--no-launch` by itself when the app is next closed,
  preventing surprise relaunches), and **Skip** (ignores this version). "Skip this version" also remains available
  in Settings › About (`SkipPendingVersion`: stops the download or kills the still-WAITING installer).
  This supersedes UPDATEUX_01/04/05; UPDATEUX_02 (size/time text), UPDATEUX_03 (progress wording) and UPDATEUX_06 (Last checked) remain.
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
* **INSTALLONEXIT_01 — a ready update installs when the app closes.** The status line said so, but the
  installer started only on RESTART or LATER; closing the card or quitting installed nothing. When an update
  is ready, the app's `Exit` starts the waiting installer with `--no-launch` (`InstallPendingOnExit`) unless one
  is already running or the version was skipped; closing the card says so ("installs when you close…").
* **RESTARTSAFE_01 — Restart & update closes the app only when the installer runs.** If the installer cannot
  start, the card shows the reason with TRY AGAIN and the app stays open (it used to close anyway).
* **PAUSESTICK_01 — a pause stands for the session.** PAUSE remembers the release (`_pausedTag`); the 6-hourly
  re-check (RECHECK_01) skips it and records "the download is paused". CONTINUE / TRY AGAIN and a manual check
  clear it.
* **WHATSNEW_01 — "What's new" after the update.** When an update is ready its GitHub notes are written to
  `update_pending_notes.txt` (tag + body). The new version's first-launch card (`UpgradeFinishedNotice`) shows up
  to six plain bullets when that tag equals the running version, and then waits for OK; Settings › About › "What's
  new in this version" opens `releases/tag/<tag>` (else the releases list, `UpdateService.ReleasesPageUrl`).
* **RECHECK_01 — a session that stays open checks again.** The automatic check used to run once, five
  minutes after start; a user who leaves the app open for days never heard of a new version. After the
  first check `RunStartupCheckAsync` repeats `RunAutomaticCheckAsync` every `RecheckInterval` (6 h)
  while the main window is open, Updates is not "Never check" (SETUPDATEONE_01) and no update is already downloading or waiting.
  Same consent, throttle, auto-download and "skipped" rules as the first check.
* **SETNOBETA_01 (2026-11-12) — there is one release line.** The Beta channel was removed: the app reads `/releases/latest` only and the Settings choice is gone (an old `UpdateChannel` value is ignored). `Build.cmd --beta` still publishes a pre-release for manual download (09 DIST-RELEASEGATE, BETA_01).
* **UPDATESPACE_01 — free space is checked for what is really written, per drive.** `UpdatePreparer`
  used to demand the FULL installer size + 256 MB on the downloads drive before every update, so a 9 MB
  smart update failed on a nearly full disk, while the full installer's own unpacking into `%TEMP%`
  was never checked. Now: 16 MB for the signed list up front; after the smart plan, the changed files
  compressed + unpacked, the patch and the rebuilt program; for the full installer, the download plus
  what the installer unpacks into the temp folder (the release's file total, or 3 × the download when
  unknown). Folders on one drive add up; each drive keeps a 128 MB margin
  (`EnsureDiskSpace(IEnumerable<(folder, bytes)>)`). A refusal is final for that attempt
  (`InsufficientDiskSpaceException`: no fallback to a bigger download) and the card says how much is
  needed and to choose TRY AGAIN after freeing space. The worker still checks Program Files itself.
* **ASSETHASH_01 — nothing is unpacked before it is checked.** The signed update list now also carries
  the SHA-256 of every compressed asset and patch (`SmartFile.AssetSha256`, `SmartPatch.AssetSha256`);
  `UpdateDownloader` checks it before the file is used. Decompression is capped at the signed file size
  (`BrotliFile.DecompressFile(…, maxBytes)`), and a patch header must promise exactly the signed program
  and start from exactly the installed one (`UpdatePreparer.RequirePatchHeader`) before it is decoded.
  A tampered asset can no longer expand on disk ("decompression bomb") before the final hash rejects it.
  Lists written before the field existed still work (size cap + final hash).

---

## 8. Transactional Install & Upgrade (incl. old-brand migration)  {#SYS-UPGRADE}

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
  **HEALTHWIN_01 (2026-10-18):** the `--no-launch` handshake used to skip the window entirely (only "settings
  loaded" was proven before the backup was given up); it now BUILDS `MainWindow` (never shown) first and exits
  non-zero if that fails. The worker's `--upgrade-probe` also loads the in-process libav decoder libraries
  (`LibAvMediaBackend.TryInitialize`, the expected versions), not only libmpv and ffprobe.
* **CRASHLOOP_01 — after the commit.** `StartupCrashGuard` counts main-window starts per version
  (`startup_attempts.txt`); 20 s open, or a normal exit, resets it. From the fourth start after three that did
  not stay open, the user is told plainly and offered the release page, which keeps the installers of the last
  five versions (RETAIN_02; an older installer asks before going back). Nothing is removed or downloaded by
  itself, and the app still starts.
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

### Elevation trust: the worker runs from a protected copy  {#SYS-UPGRADE-TRUST}

* **WORKERSTAGE_01 — the only administrator process of an install or update runs from a folder its own
  user cannot change.** The elevated worker used to be copied into
  `%LOCALAPPDATA%\FreeVideoStudioUpdates\Stage\<id>` and started from there with `Verb = "runas"`. Every
  program the user runs can write to that folder: between the copy and the user's "Yes" it could swap
  the file or drop a DLL beside it, and the user — who EXPECTS a prompt at that moment, and whose prompt
  says "Unknown publisher" for this certificate anyway — would hand it administrator rights.
* `ProtectedWorkerStage.Create` copies the broker's own program into
  `%ProgramData%\FreeVideoStudio-Worker-<32 hex>\FreeVideoStudio.exe`:
  * the folder is created with a PROTECTED access list: SYSTEM and Administrators full control, the
    user read/execute; the `OWNER RIGHTS` (S-1-3-4) entry removes the rights an owner keeps implicitly
    (changing the access list) and grants the creator only "add a file". Standard users have no "delete
    child" right on `%ProgramData%`, so the folder cannot be renamed or replaced;
  * the program file is created WITH its final access list (read/execute for the user); the creating
    handle is the only one that could ever write it;
  * the protection is PROVEN, never assumed: writing the file, changing the folder's access list,
    deleting the file and renaming the folder are attempted and must ALL be refused; the folder must
    hold exactly that file; the copy must hash to the bytes written and (Release builds) pass the
    publisher check (`AuthenticodeVerifier.EvaluateUpdateCandidate`). Any doubt stops the update before
    Windows is asked ("…not started with administrator rights… Nothing was changed"). There is no
    fallback to the user-writable folder;
  * a read handle that denies writers is held until `Process.Start` returns.
* The worker refuses to do anything as administrator unless it runs from such a path
  (`UpgradeInstallWorker.RunAsync` → `ProtectedWorkerStage.IsProtectedWorkerPath`), restricts its DLL
  search to System32 before anything else (`NativeHelpers.RestrictDllSearchToSystem32`,
  `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)`), and the program's static imports resolve
  from System32 only (linker `/DEPENDENTLOADFLAG:0x800`, App csproj). A file added to the folder is
  never loaded.
* The user cannot delete these folders. `UpgradeInstallWorker.MaintainAsync` (elevated, end of every
  install) removes all but the one in use (`SweepStale`); the uninstaller removes them all
  (`DeploymentLifecycle.PerformFullHostCleanupAsync`).
* Accepted consequence: on a machine where a standard user may not create folders in `%ProgramData%`
  (unusual hardening), installs and updates stop with that message instead of running elevated from a
  user-writable place. Tests: `ReleaseHardeningTests.WorkerStage01_*`. Real-Windows verification still
  required: standard user + UAC, admin account with UAC, and an AV-locked `%ProgramData%`.

### Working under Windows "Controlled folder access"  {#SYS-CFASAFE}

**CFASAFE_01 (2026-11-11).** With Windows Security › Ransomware protection › Controlled folder access ON,
Windows refuses writes into Documents, Pictures, Videos, Music, Desktop and Favorites from any program it does
not already trust, and shows a "blocked" notification per attempt. A new self-signed program is never trusted.
The app wrote into those folders at EVERY start, so a protected PC showed the notification on every launch and
had no starter memes or songs:

| Write | Before | Now |
| :--- | :--- | :--- |
| Starter memes → `Videos\FreeVideoStudio\Memes` | `CreateDirectory` refused, retried every start | `MemeDirectory.GetDefault` → the app's `Media\Memes` when refused and no folder exists yet |
| Starter songs → `Music` | every copy refused, yet "delivered" was recorded: never delivered | `MemeDirectory.GetMusicRoot` → the app's `Media\Music` when refused; `MemeAssets.DeliverStarter` records delivery only when no copy failed |
| Personal / public Desktop icon | threw; Start menu entry after it never written; retried every start | skipped when refused (`UpgradeRegistration.DesktopWritable`); the Start menu entry is always written |
| Saved output folder inside a protected folder | probed (one notification) at every export | asked through the gate; Downloads is used |
| Export folder that allows the app but not FFmpeg (CFAFFMPEG_01) | the export failed at the very end | FFmpeg writes, and the app deletes, a tiny probe once per folder and run (`ProtectedFolderGate.FfmpegCanWrite`); refused → Downloads + the notice |
| Legacy meme folder move out of `Videos` | each file refused | postponed while refused |

* **`ControlledFolderAccess` (Core) only READS**: the mode (`EnableControlledFolderAccess`, policy key first;
  `1` = block; readable by every user) and the default protected folders. With protection off or in audit mode
  every path returns exactly what it returned before, with no disk access.
* **`ProtectedFolderGate` (App)**: whether THIS program is on the allowed list is admin-only information, so the
  gate makes ONE real write attempt per protected folder. A refusal is remembered in
  `controlled_folder_denied.txt` (UTC time + program path); while it is younger than 7 days and names this
  program no further attempt is made (no notification). `CanWriteNow` (installer code) remembers nothing.
* **One notice** (`ShowNoticeAsync`, after the first refusal): what is happening, that nothing is lost, the two
  programs to allow (`FreeVideoStudio.exe`, `backend\ffmpeg.exe` — the allowed list is per program, and FFmpeg
  writes the exported video), and a button that opens `windowsdefender://ransomwareprotection` and forgets the
  remembered refusal so the next start checks again.
* **An existing folder is never abandoned**: an existing `Videos\FreeVideoStudio\Memes` stays the meme folder
  (reading is not blocked); the app's own folder is chosen only for a NEW one, and stays chosen once it exists.
* ⚠️ **The app never changes Windows Security and never adds itself to the allowed list** — that is tampering,
  and exactly what antivirus must stop. Only the user can allow it; only a publicly trusted signature with
  reputation makes Windows allow it by itself (SYS-SIGNING).
* ⚠️ Not covered: a custom meme/music/output folder the user points INTO a protected folder while only one of
  the two programs is allowed, and project files saved into a protected folder; those report their normal
  "could not save" error. Real-Windows verification with the protection in block mode is still required
  (this change was developed on a PC in audit mode).
* Tests: `AvFriendlyBehaviourTests.CfaSafe01_*`.

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
  `--upgrade-stage <folder>`. The broker sets `SetDllDirectory` to it, shows its window, and prepares
  the worker copy IN THE BACKGROUND (`PrepareWorkerCopy`, started at once — during the wait for the
  old app in an in-app update). **Since WORKERSTAGE_01 that copy is NOT in this per-user folder** — a
  folder every program the user runs can write to — but in a protected folder under `%ProgramData%`
  (SYS-UPGRADE-TRUST); the per-user stage keeps only the broker's two window libraries.
  `StageFromArgs` accepts the folder only as an existing direct child of
  `UpgradeCoordinator.StageRoot` (`%LOCALAPPDATA%\FreeVideoStudioUpdates\Stage`, NOTEMPEXEC_01). If the copy is still running when Windows is about to be asked, the window says
  "Getting the installer ready…". Recovery and installs from inside a program folder keep the old
  staged-broker path (a running executable cannot be moved).

Local regression coverage for this section: `VersionAndUpdaterTests` (UPDATEUX/UPGRADEUX section: wording, view-model state machines,
progress messages never satisfying a reply) and `UpgradeProgressReportingTests` (monotonic progress,
verification unchanged). Real-Windows verification still required: UAC Yes/No, close-it-for-me with
unsaved work, compact and full installer windows, first-launch notice, a double-clicked
installer from Downloads (window within a second, worker from `%ProgramData%\FreeVideoStudio-Worker-<id>`, WORKERSTAGE_01).

Local regression coverage: `UpgradeTransactionTests` injects failure at each directory rename,
checks source precedence, retention, conflicting files, journal traversal and compact runtime
corruption. `UpgradeSettingsPreservationTests` (formerly `CompactUpdateTests`; the archive tests left with the retired compact package, COMPACTGONE_01) checks settings preservation. `StartupCrashGuardTests` covers CRASHLOOP_01.
Before production rollout, exercise real Windows UAC (same/different admin), OneDrive and network
Desktop offline/online, two logged-in accounts, antivirus locks, power loss, and representative old
signed releases in disposable Windows installations. Unit tests do not establish those OS outcomes.
