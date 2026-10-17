# SPECIFICATION 09: DISTRIBUTION, UPDATE SIZE & REPOSITORY WEIGHT

## Code Mini-Map: Bound Source Files & Symbols

> **⚠ CO-GOVERNED rows are bound by EVERY spec listed on them.** Reading only this one is not compliance (`SPEC_GOVERNANCE.md` §2).

| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `src/FreeVideoStudio.Core/Infrastructure/RuntimePayloadManifest.cs` | `RuntimePayloadManifest` | `FromFolder`, `Read`, `Write`, `SYS-PAYLOADSPLIT` | Runtime fingerprint. |
| `src/FreeVideoStudio.App/Services/UpdateService.cs` | `UpdateService` | `AppOnlyAssetName`, `RuntimeAlreadyMatchesAsync`, `SYS-PAYLOADSPLIT` | Which package to download. **⚠ CO-GOVERNED BY: 05** |
| `src/FreeVideoStudio.Core/Infrastructure/SmartUpdateManifest.cs` | `SmartUpdateManifest`, `SmartPlan`, `SmartFile`, `SmartPatch` | `Parse`, `Plan`, `Sign`, `VerifySignature`, `AssetSha256`, `DIST-SMART`, `ASSETHASH_01` | The signed per-file list of a release. |
| `src/FreeVideoStudio.Core/Infrastructure/BinaryDelta.cs` | `BinaryDelta` | `Create`, `Apply`, `ApplyFile`, `StreamBufferSize`, `BINDELTA_01`, `BINDELTA_02` | Binary patch for the main program. |
| `src/FreeVideoStudio.Core/Infrastructure/BrotliFile.cs` | `BrotliFile` | `CompressFile`, `DecompressFile(maxBytes)`, `ASSETHASH_01` | Per-file asset compression; size-capped decompression. |
| `src/FreeVideoStudio.Core/Infrastructure/ReleaseChangelog.cs` | `ReleaseChangelog`, `ReleaseChangelog.Section` | `Parse`, `Find`, `Promote`, `LintUserNotes`, `PlainHighlights`, `UpcomingTitle`, `UnreleasedTitle`, `RELNOTES_01`, `RELNOTES_02`, `RELNOTES_03` | The one parser of docs/CHANGELOG.md and docs/changes.md (§3f DIST-RELEASENOTES). |
| `build/FvsBuild/SmartUpdateBuilder.cs` | `SmartUpdateBuilder` | `Prepare`, `Finish`, `ReleaseFiles`, `MarkReleased`, `EnsurePublishedBases`, `DIST-SMART`, `NODROPPER_01`, `DELTABASE_01`, `ASSETHASH_01` | Builds the per-file assets, patches, the signed list packed into the installer and the release update.json. |
| `build/installer/FreeVideoStudio.iss` | Inno Setup script | `ForwardedArguments`, `NODROPPER_01`, `PEIDENTITY_01` | The single download: a non-elevated Inno Setup package that unpacks the release beside its signed list and starts the transactional installer. |
| `build/FvsBuild/InnoInstaller.cs` | `InnoInstaller` | `Build`, `SetupFileName`, `NODROPPER_01` | Finds ISCC.exe (Inno Setup 6.3+, `FVS_ISCC`) and compiles the script into `compiled\FreeVideoStudio-Setup.exe`. |
| `build/FvsBuild/PeIdentity.cs` | `PeIdentity` | `FixOriginalFilename`, `Patch`, `PEIDENTITY_01` | Rewrites OriginalFilename/InternalName to the shipped file name before signing. **⚠ CO-GOVERNED BY: 05** |
| `src/FreeVideoStudio.App/Services/UpdatePreparer.cs` | `UpdatePreparer` | `PrepareAsync`, `EnsureDiskSpace`, `RequirePatchHeader`, `UPDATESPACE_01`, `ASSETHASH_01`, `UNSIGNEDDEV_01` | Client side of DIST-SMART. **⚠ CO-GOVERNED BY: 05** |
| `build/FvsBuild/GitHubReleasePublisher.cs` | `GitHubReleasePublisher` | `Publish`, `ReleaseAssets`, `VerifyAssets`, `PruneOldReleases`, `ReleasesToPrune`, `ReleaseTitle`, `PreSetupDownloadName`, `REBRAND_03`, `NODROPPER_01`, `RELTAG_01`, `RETAIN_01`, `BETA_01` | Draft-verify-publish from the pushed tag; creates the previous-brand download alias transiently; retention. |
| `build/FvsBuild/ReleaseGate.cs` | `ReleaseGate`, `ReleasePlan` | `Preflight`, `PrepareNotes`, `CommitVersion`, `Undo`, `TreeStillClean`, `PushRelease`, `RecordReleaseNotes`, `RELGATE_01`, `RELNOTES_01` | The release transaction (§3e DIST-RELEASEGATE). **⚠ CO-GOVERNED BY: 05** |
| `build/FvsBuild/Program.cs` | `Program` | `RunPipeline`, `FailRelease`, `RELGATE_01` | Orders the gate, the build and the publish. **⚠ CO-GOVERNED BY: 05** |
| `docs/CHANGELOG.md` | User-facing release notes | `## Upcoming`, `RELNOTES_01`, `CHANGELOG_01` | What users read; becomes the GitHub release notes and the update card's "What's new". |
| `docs/changes.md` | Technical change log | `## Unreleased`, `RELNOTES_01` | What engineers and agents read; promoted with every stable release. |
| `build/FvsBuild/Staging.cs` | `Staging` | `WriteManifests`, `ValidateCompiledOutput`, `SYS-PAYLOADSPLIT`, `NODROPPER_01` | Stages the release folder and writes its manifests (nothing is zipped or embedded); `obj\ReleaseAssets\runtime.manifest.json`. |
| `.github/workflows/ci.yml` | CI | `SYS-CI`, `aot-publish` | Runs the ratchets; reports the app publish size. **⚠ CO-GOVERNED BY: 08** |
| `.github/workflows/lfs-guard.yml` | LFS guard | `SYS-REPOWEIGHT` | Proves LFS is real and stops new large files. |
| `.gitattributes` | EOL + LFS policy | `EOL_01`, `SYS-REPOWEIGHT` | What is stored where. |
| `Build.cmd` | Production Release Script | `dotnet run build\FvsBuild`, `--beta`, `--yes`, `--no-publish`, `RELGATE_01` | **STRICT AGENT BAN**: Deploys release assets and publishes publicly to GitHub Cloud, behind the release gate. Never run by autonomous agents. **⚠ CO-GOVERNED BY: 05, GOV** |
| `dev_build.cmd` | Local Dev Build Harness | `dotnet run build\FvsBuild -- --dev` | Local compilation check and NativeAOT publish verification. Zero git tags, zero GitHub Cloud publishing. **⚠ CO-GOVERNED BY: 05, GOV** |

---

## 0. Build Harness Disclaimers & Agent Execution Rules  {#DIST-AGENTS}

> [!CAUTION]
> ### STRICT BAN ON `Build.cmd` FOR AI AGENTS
> **Autonomous AI agents are strictly forbidden from executing `.\Build.cmd` or `Build.cmd`.**
> `Build.cmd` is reserved exclusively for intentional human-triggered production deployments. Running `Build.cmd` compiles `FreeVideoStudio.exe` and **immediately deploys/replaces the public release on GitHub Cloud**, making the resulting binary live and downloadable to all end users.
>
> **For all local compilation checks and build verification, agents MUST use `.\dev_build.cmd`:**
> * `.\dev_build.cmd` passes the `--dev` flag to `FvsBuild`.
> * It performs local compilation only.
> * It never creates git release tags and **never touches GitHub or publishes to the cloud**.
> * Testing compilation with `.\dev_build.cmd` is permitted; running `.\Build.cmd` is never permitted under any circumstances.
>
> Since RELGATE_01 (§3e) `Build.cmd` also refuses to start unless the checkout is clean, pushed and on the default branch, the release notes pass the lint, and a human types `PUBLISH` (or passes `--yes`). Those checks protect the release; they are NOT permission for an agent to run it.

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
| Standalone installer output | `compiled/FreeVideoStudio-Setup.exe` — an Inno Setup package (NODROPPER_01, §3c DIST-INSTALLER) |
| Installed executable | `FreeVideoStudio.exe` (version resource `OriginalFilename`/`InternalName` = `FreeVideoStudio.exe`, PEIDENTITY_01) |
| Embedded payload resource | **none** — retired by NODROPPER_01; `AvCleanRuleTests` fails if one comes back |
| App-only archive | **retired** (`FreeVideoStudio.App.update.zip` is no longer built or published; smart updates replace it) |
| Runtime manifest | `runtime.manifest.json` in the staged release and under `obj/ReleaseAssets` |
| Release-asset aliases | `FreeVideoStudio.exe` (same bytes as the setup, for updaters released before NODROPPER_01) and the previous-brand name (REBRAND_03); both created transiently at publish time |

The internal tool names `FvsBuild` and `FvsVerify` are unchanged. The GitHub repository is `alonreich/Free_Video_Studio`; GitHub redirects the previous repository address, so old clients still reach `releases/latest`. Game-specific crop profiles and recording-discovery folders remain gameplay identifiers, not product branding. Historical storage names are compatibility data in `LegacyAppDataNames.txt` (`05` SYS-REBRAND). The full old→new map and update flow are in `REBRAND_MIGRATION.md`.

Run the local verification gates from the repository root:

```powershell
.\dev_build.cmd
dotnet build FreeVideoStudio.sln -c Release -warnaserror
dotnet test tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj
dotnet test tests/FreeVideoStudio.App.Tests/FreeVideoStudio.App.Tests.csproj
dotnet run --project build/FvsVerify/FvsVerify.csproj -v q
dotnet test tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj --filter RebrandTests
```

`RebrandTests.PreviousBrandNameAppearsOnlyInTheMigrationAllowList` fails if the previous product name appears outside `LegacyAppDataNames.txt` and `REBRAND_MIGRATION.md`. Project GUIDs stay unchanged; solution paths, project references, source-generated serializer types, XAML namespaces and sentinel paths use the renamed projects. `ProductionNamespacesUseTheProductRoot` enforces production namespace declarations; `AppDataPathsTests` guards migration behavior. Build output must contain only the Inno Setup installer (`FreeVideoStudio-Setup.exe`), with release sidecars under `obj/ReleaseAssets`.

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

* **Every patch costs 322 MB.** `UpdateService` fetches one release asset — the whole installer —
  and reinstalls. A one-line typo fix therefore reships FFmpeg. The download timeout is thirty
  minutes, which is an admission of how long this takes.

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
cause. ~~`payload.zip` (FFmpeg, libmpv, starter media) is embedded as a managed resource in a
NativeAOT executable and extracted to the install folder on first run.~~ **Superseded by
NODROPPER_01 (2026-10-12, §3c):** the single download is now an Inno Setup package; nothing is
embedded in the program. The size (FFmpeg + libmpv + starter media) is unchanged — LZMA2 packs it
somewhat tighter than deflate did.

⚠️ **The mandate is worth re-examining, and this spec does not pretend otherwise.** It buys "one
file on disk", a property no user of a video editor has asked for. It costs: a 322 MB download, a
322 MB re-download per patch, an extract-on-first-run step with its own failure modes, the ban on a
DI container (`COMPOSITION_01` cites `TrimMode=full` as the reason), and hand-written AOT-safe JSON
(`PROJ-AOT`). Four architectural taxes for one cosmetic win.

**This spec does not repeal it.** Repealing it is a product decision with a migration behind it —
installer, upgrade path, signing story. What this spec does is remove the part of the cost that
recurs: the *repeat* download.

---

## 3. The Split: One Runtime, Many App Patches  {#DIST-SPLIT}

* **`SYS-PAYLOADSPLIT` — a release may publish two packages and one tiny sidecar.**

  | Asset | Contents | When it is used |
  | :--- | :--- | :--- |
  | `FreeVideoStudio.exe` | everything (322 MB) | first install, or the runtime changed |
  | `FreeVideoStudio.App.update.zip` | the application only | the runtime already matches |
  | `runtime.manifest.json` | a fingerprint, a few hundred bytes | always read first |

* **The fingerprint includes content hashes of reusable payload files.** It is computed during
  packaging, over ordinal-sorted paths, lengths and SHA-256 hashes. Application executables,
  manifests and debug symbols are excluded; codec libraries and starter assets are included.
  An equal-size binary or asset change therefore selects the full installer. Startup checks read
  the stored sidecar; the user-approved update also verifies installed file bytes before reuse.
* **⚠️ EVERY UNCERTAINTY RESOLVES TO THE BIG DOWNLOAD.** `RuntimeAlreadyMatchesAsync` returns true
  only when the release published a small package AND advertised a fingerprint AND it matches what
  is installed. No small package, no sidecar, an unreadable local manifest, a network failure, a
  cancel, or a genuine mismatch — all return false.

  The asymmetry is the whole design. Wrongly choosing the big download costs bandwidth. Wrongly
  choosing the small one installs an application against codec binaries it was not built for, and
  that fails **at export time, on the user's machine, after they have done the work**.

* **The fingerprint decides WHAT to fetch and never WHETHER TO TRUST IT.** Whatever is downloaded
  is still SHA-256 verified against the release digest and still Authenticode-checked
  (`05` §5 SYS-SIGNING, `UPDATETRUST_02`). A manifest is a hint, and hints are not a security
  boundary.

* **Current packaging behavior (NODROPPER_01).** `Staging.WriteManifests` writes `runtime.manifest.json` and `install.manifest.json` into the staging folder, so fresh installs receive the fingerprint, and writes `obj/ReleaseAssets/runtime.manifest.json`. **The compact package is no longer built or published**: it was a second NativeAOT program carrying the app inside its own resources — the same dropper pattern §3c removes — and smart updates (§3a) already download only what changed. `compiled/` contains only `FreeVideoStudio-Setup.exe`. The client code that reads a compact package stays dormant (no published asset ⇒ `RuntimeAlreadyMatchesAsync` is false ⇒ the full installer), so an older release that still carries one is handled as before.

* **Operational compact updates (dormant since NODROPPER_01 — kept for releases that still publish the package).** `FromFolder` excludes the root application and uninstaller.
  Before selecting a compact update, the updater verifies the installed file manifest, requires
  the canonical install location, a published package digest and a matching runtime fingerprint.
  It fetches the selected asset and verifies its digest. The archive must contain exactly one
  installer executable, which is Authenticode-checked before launch. The installer embeds the
  new complete manifest and refuses to reuse any installed file with different bytes.
* **Build ordering (NODROPPER_01).** ONE NativeAOT publish of the program into staging; rewrite its
  PE identity (PEIDENTITY_01); sign it; stage dependencies; write the manifests; build the smart
  assets and the signed file list for the installer (`SmartUpdateBuilder.Prepare`); compile the Inno
  Setup installer into `compiled` (`InnoInstaller.Build`); sign it; write the release `update.json`
  that names it (`SmartUpdateBuilder.Finish`); validate that `compiled` holds exactly
  `FreeVideoStudio-Setup.exe`. The two extra NativeAOT publishes (embedding installer, compact
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
  them, because they do not pass `/VERYSILENT`). Its version resource says `FreeVideoStudio-Setup.exe` —
  the one place a file name and its `OriginalFilename` differ, and only for that download. Remove the
  alias only together with automatic updates for copies released before 2026-10-12.
* **Publication transaction.** Upload all assets (setup, its two aliases, runtime manifest, smart files) to a draft created FROM THE PUSHED
  TAG (`--verify-tag`, RELTAG_01), verify every asset by name and SHA-256, then publish it as latest (a beta: as a pre-release, BETA_01).
  Failed uploads stay unpublished. Publication never deletes an existing release first; after a successful
  publish, releases beyond the retention window are deleted and their TAGS ARE KEPT (RETAIN_01, §3d).

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
  — to the compact package (when a release still publishes one; none does since NODROPPER_01), then the
  full installer. If the smart download would be ≥ 90 % of the full installer, the full installer is used.
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
  (05 SYS-AUTOUPDATE). Older lists without the field still work (size cap + final hash). Measured on a synthetic 120 MB
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
* **D1 — dev.** `dev_build.cmd` produces the same assets (patch bases: any of the last 6 builds) and the
  dev updater (DEVUPDATE_01) reads them from `obj\ReleaseAssets`, so `dev.cmd` exercises the smart,
  compact and full paths exactly like production.

## 3b. After Every Public Release  {#DIST-NEWRELEASE}

* **NEWRELEASE_01.** After a SUCCESSFUL publish only (not `--dev`, not `--no-publish`, not exit 2),
  `build/FvsBuild/ReleaseChecklist.cs` prepares `obj/ReleaseSubmit/`: `FreeVideoStudio-program-<version>.zip`
  (the signed program from `obj/ReleaseHistory/<version>/`, password `infected` when 7-Zip is installed)
  and `submission-text.txt` (every Microsoft WDSI form field, SHA-256 of program and installer, release
  URL). It then opens `docs/New-Release.md` and that text file in Notepad++ (`-multiInst`; Notepad as fallback).
* Why the program and not the installer: the WDSI form accepts at most **50 MB**; the installer (`FreeVideoStudio-Setup.exe`) is ~300 MB.
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
    installer (`05` SYS-UPGRADE): broker window, one UAC prompt, elevated worker (run from a protected
    `%ProgramData%\FreeVideoStudio-Worker-<id>` copy, WORKERSTAGE_01 / `05` SYS-UPGRADE-TRUST), which re-verifies the signed list
    itself, copies every file into its protected folder and hashes the copy
    (`InstallPayload.ExtractFromSmartUpdate`, which accepts real paths as well as content names), shortcuts,
    Apps & features entry, first-launch health check, rollback;
  * forwards ONLY `--auto-update`, `--source-current`, `--no-launch`, `--quiet` and `--wait-pid <number>`
    (`ForwardedArguments`), so a command line can never steer the unpacked program into another mode.
* **Trust is unchanged.** `UpgradeCoordinator.SmartFromArgs` accepts the folder because it is the folder
  the running program was unpacked into; the elevated worker never trusts that check and verifies the
  list's signature against the pinned root (UPDATEPIN_01) and every byte against the list. A program
  started without a signed list refuses to install ("This program is not an installer. Download
  FreeVideoStudio-Setup.exe…").
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

* **Local Output Directory (`.\compiled\`) Invariant:** `.\compiled\` strictly holds **only the single latest installer** (`FreeVideoStudio-Setup.exe`). Any stray files, extra sidecars or older binaries are purged and treated as build failures (`Staging.ValidateCompiledOutput`).
* **RETAIN_01 — Official GitHub Release Retention (last 5 stable releases, tags kept forever):** after a successful publish `GitHubReleasePublisher.PruneOldReleases` keeps the newest **5 stable releases** (the new one included) and the newest **3 betas that are still newer than the newest stable** (`ReleasesToPrune`, ordered by VERSION, drafts ignored); every other release is deleted with `gh release delete --yes` — **without `--cleanup-tag`**. The old `--cleanup-tag` deleted the git tag too, so any version a user still ran stopped being traceable to its source after the sixth release, contradicting the publisher's own promise. Tags are never deleted.
* **Rolling Changelog Contract (`docs/CHANGELOG.md` + `docs/changes.md`):** see §3f DIST-RELEASENOTES. The user changelog keeps exactly the 5 latest releases plus `## Upcoming`; the technical log keeps 10 plus `## Unreleased`. Both are pruned by the release itself (`ReleaseChangelog.Promote`), not by memory.
* **Smart Update Delta Continuity:** DELTABASE_01 (§3a): every release build makes sure the last 3 published programs are patch bases, verified against their signed update lists.
* **Pre-Flight Disk Space & Connection Awareness:** UPDATESPACE_01 (05 SYS-AUTOUPDATE): free space is checked per drive for what an update really writes (smart: the changed files; full: the download plus what the installer unpacks), and metered connections are honoured according to user preferences.

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
     `## Unreleased` section. It then prints the version, branch, commit and the notes and asks the human to
     type `PUBLISH`. With no terminal (redirected input) it refuses unless `--yes` is given on purpose.
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
     `## Upcoming` for the next attempt.
  7. Stable only, after success: `RecordReleaseNotes` promotes `## Upcoming` (CHANGELOG.md) and
     `## Unreleased` (changes.md) to `## v<version> — <date>`, prunes both, commits
     `docs: release notes for v<version>` and pushes. Never fails the release (it is public); problems are
     warnings with the manual step named. Then NEWRELEASE_01 (§3b).
* **Modes.** `Build.cmd` = stable release (gate + publish). `Build.cmd --beta` = pre-release for the Beta
  channel, from any pushed branch; its notes are the current `## Upcoming` prefixed with a beta line, and
  nothing is promoted. `Build.cmd --no-publish` and `dev_build.cmd` (`--dev`) never touch git, GitHub or the
  version files (the version reaches the compiler through `-p:Version=`). `--stage-only` is a diagnostic.
* **Versioning.** Calendar version `yyyy.MM.dd.HHmm` (UTC). Why not SemVer: 05 SYS-AUTOUPDATE.
* Tests: `ReleaseChangelogTests` (promotion, retention, lint, `ReleasesToPrune`, `NewVersion`). The git
  and GitHub steps need a real repository and are verified by running `Build.cmd` (human only).

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
  changes.md, and every GitHub release body for as long as the release exists (RETAIN_01).
* **RELNOTES_03 — users see them.** The update card shows up to four headlines as "What's new"
  (`ReleaseChangelog.PlainHighlights(…, headlinesOnly: true)`, 04 / 05 SYS-AUTOUPDATE).

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
size on every push to `main`.

⚠️ **It measures only the app's `dotnet publish` folder, NOT the user download.** CI never runs
`FvsBuild` (and since NODROPPER_01 nothing is embedded in the program at all). The printed total is the app alone — roughly what an app-only
patch would cost — while the 322 MB installer (FFmpeg, libmpv, starter media) is not measured
anywhere in CI. A number nobody prints is a number nobody defends; this one is only half printed.
