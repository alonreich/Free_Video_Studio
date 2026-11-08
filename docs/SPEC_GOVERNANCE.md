# SPECIFICATION GOVERNANCE: ZERO-LEAKAGE CONTRACT

## Code Mini-Map: Bound Source Files & Governance Symbols
| Source File Path | Key Classes, Records & Controls | Core Bound Methods, Properties & Symbols | Subsystem Domain Role |
| :--- | :--- | :--- | :--- |
| `docs/SPEC_GOVERNANCE.md` | Governance Protocol | `Zero-Leakage`, `Proof-of-Read`, `In-Code Sentinel` | Master Governance Contract |
| `docs/README.md` | Master Architectural Router | `9 North Star Invariants`, `Domain Routing` | Architectural Entry Point |
| `docs/INDEX.md` | Flat Symbol & File Lookup | `Symbol -> Spec Section`, `Stable {#ANCHOR} ids`, `CO-GOVERNED marks` | Zero-Cost Routing Lookup |
| `src/FreeVideoStudio.Core/Infrastructure/ApplicationPaths.cs` | `ApplicationPaths` | `ProgramDataRoot`, `RecoveryStateFile`, `SessionStateFile`, `AppDirectoryName` | System Path Governance |
| `src/FreeVideoStudio.Core/Infrastructure/RecoveryManager.cs` | `RecoveryManager` | `SaveState`, `LoadState`, `CheckFault`, `IsSafeModeActive` | Session State & Fault Governance |
| `src/FreeVideoStudio.App/MainWindow.axaml.cs` | `MainWindow` (partial class) | `AttachPreviewMonitor`, `OnSuccessAction` (this file); the recovery hooks live in partials governed by their own headers — `SaveRecoveryState` in `MainWindow.Recovery.cs`, the startup `RecoveryManager.CheckFault()` call in `MainWindow.Wireup.cs` | Main Process Governance Root |

---

## 0. Routing (do this first, it is nearly free)
Do NOT read every spec. Route once, read one.
* Know the FILE -> `docs/README.md` §3.
* Know only a SYMBOL, CONSTANT or ENGINEERING TAG -> grep `docs/INDEX.md`.
* Landed on a row marked `⚠` -> that file is CO-GOVERNED; read every spec the row names.

## 0a. CRITICAL AGENT MANDATE: STRICT BAN ON `Build.cmd` (CLOUD PUBLISH)
* **Agents must never execute `Build.cmd`:** it runs the production release pipeline, creates Git tags and publishes publicly to GitHub. Agent execution is a severe operational violation.
* For local NativeAOT/package validation, use `dev_build.cmd`, which passes `--dev` to `FvsBuild` and neither creates tags nor publishes or updates GitHub releases. Use `dotnet build` / `dotnet test` for compilation and tests.
* `RELGATE_01` checks clean, pushed, on-branch state and release-note lint, and requires typed `PUBLISH` (09 DIST-RELEASEGATE). These safeguards do not authorize agent execution.

## 1. Absolute Stop-and-Read Mandate
Developers and autonomous AI agents are strictly forbidden from inspecting, generating, refactoring, or modifying code in any source file (`.cs`, `.axaml`, `.cmd`, `.ps1`) without first loading, reading, and citing its mapped specification file from `docs/`. Speculative code modifications without active specification verification are classified as critical system defects.

## 2. Mid-Session Context Boundary Rule
If a debugging session or feature implementation crosses from its initial domain into another (e.g., an FFmpeg export task requiring adjustments to `OutputTimeline.cs`, audio mastering filters, Avalonia UI styles, or disk recovery serialization), EXECUTION MUST HALT IMMEDIATELY.
* Conversational memory, assumed defaults, or heuristic extrapolations are strictly prohibited.
* **CO-GOVERNED FILES:** A source file listed in the Code Mini-Map of MORE THAN ONE spec (marked `⚠ CO-GOVERNED BY` on its row, and `⚠` in `docs/INDEX.md`) is bound by ALL of them simultaneously. Reading one of them is NOT compliance. The authoritative list of multi-domain files is every `⚠` row in `docs/INDEX.md` §1 (kept in step with the spec mini-maps) — there are dozens. Examples only, not the list: `MainWindow.axaml.cs`, `GranularSpeedEditorWindow.axaml.cs`, `MergeEdl.cs`, `ProjectSession.cs`.
* The agent must load and parse the governing `docs/0X_*.md` file before inspecting or touching logic in that secondary domain.
* Cross-domain modifications executed without verifying the target domain's governing specification will be rejected and reverted.

## 3. Mandatory Proof-of-Read Protocol
Every code edit proposal, refactor plan, or architectural implementation block submitted by an AI agent must begin with the following three-line verification header:
```text
[SPEC VERIFIED]: docs/0X_<DOMAIN_SPEC_NAME>.md#<STABLE-ANCHOR>   (anchor, e.g. FFM-BINPATH — never a bare section number)
[INVARIANT QUOTED]: <Exact formula, mathematical constraint, threshold, or boundary rule quoted from that spec>
[SCOPE CLAMP]: <Explicit confirmation of the precise method, struct, or file altered with zero outside side-effects>
```

## 4. Specification Routing Without Repeated Source Headers
`SPECROUTING_02` (user-approved 2026-11-01): production files do not require repeated
`[SPEC CONTRACT]` headers. Keep the governing specifications in `docs/INDEX.md`, the domain
mini-maps and `docs/README.md`; read and cite them before code changes (§1–§3 still apply).
Do not add boilerplate headers to satisfy a test. The former header-presence architecture test
is retired by this explicit rule change. Fix-specific sentinel markers in `build/sentinels.txt`
and behavioral tests remain required; they are distinct from the removed routing boilerplate.

## 5. Specification Invariant Audit Verification
When audits or tests reveal discrepancies between text documentation and active C# implementation:
1. Ground truth resides in mathematical laws, thread safety boundaries, and hardware protection safeguards.
2. Obsolete workflows (e.g., legacy checkmark-based zoom models) must be purged from documentation and replaced with live production invariants (e.g., ZOOMLIVE continuous gestures).
3. Critical unwritten defenses present in source code (e.g., WASAPI 3-second preview clock aborts, render thread OpenGL deadlock mitigations, sub-pixel pointer capture releases, atomic file replacement safeguards) must be codified directly into the governing specifications.

## 6. Mandatory Change Logging & Project Map Staging (CHANGELOG_01 / RELNOTES_01)
Every autonomous AI agent modifying application source code, UI elements, workflows, build tooling, tests or architectural structure must record the change before concluding (`docs/README.md` §0.3, `09_DISTRIBUTION_AND_RELEASE.md` DIST-RELEASENOTES):
1. **Technical log:** an entry under `## Unreleased` in `docs/changes.md` — date, engineering tag, what changed and why, files touched.
2. **User notes:** under the FIXED staging heading `## Upcoming` at the top of `docs/CHANGELOG.md` (never a guessed `## vYYYY.MM.DD.xxxx` heading — the build picks the version at release time). Plain, short, oversimplified English for the end user, `**Headline:** one or two sentences`. Technical jargon, internal method names, class names, file names and engineering tags are strictly forbidden. Merge with an existing bullet rather than adding a near-duplicate; at most 15 bullets. Purely internal changes need no user bullet.
3. **Lint & retention are mechanical:** the release gate refuses notes that fail `ReleaseChangelog.LintUserNotes` (RELNOTES_02); an official release promotes `## Upcoming` / `## Unreleased` to `## v<version> — <date>` and keeps 5 releases in `docs/CHANGELOG.md` and 10 in `docs/changes.md`, moving older ones to `docs/CHANGELOG_ARCHIVE.md` / `docs/changes_ARCHIVE.md` (RELRECORD_01). Never prune or rename sections by hand. CI's `Release records` job fails a code change that does not touch `docs/changes.md` (CHANGELOG_02).
4. **Automated Release Pipeline Integration:** When an official release is published via `Build.cmd` / `FvsBuild`, `ReleaseGate.PrepareNotes` takes the `## Upcoming` bullets as the GitHub release notes, which the in-app update card (`UpdateToastWindow`, RELNOTES_03) shows as "What's new".
5. **Project Map Sync:** If new files, classes, or architecture rules are introduced or removed, the repository map (`docs/README.md` §5 — formerly `project_structure.txt`, which is now only a pointer) and `docs/INDEX.md` must be updated in tandem.
6. **Mandatory Completion Block:** An AI agent is strictly forbidden from finishing a task or committing code without completing this change-logging step.

## 7. Mandatory Storage Inventory (STORAGEPATHS_01)

Any new or changed persistent/temporary storage, credentials/API keys, cache, media, profiles or recovery data must update the three-column [storage table](05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-STORAGE-TABLE) and retention rules in the same change. Include defaults for production and development, override behavior, TEMP-loss behavior and path/lifecycle tests. Never document credential values.
