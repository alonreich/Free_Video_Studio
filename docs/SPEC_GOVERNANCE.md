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

## 0. Routing, Build.cmd Ban
Route once, read one: grep `docs/INDEX.md` for the file, symbol or tag; `⚠` rows are co-governed (§2).
Never run `Build.cmd` and how to build/test instead: `docs/README.md` §0.1 and §0.4 (canonical).

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
mini-maps; read and cite them before code changes (§1–§3 still apply).
Do not add boilerplate headers to satisfy a test. The former header-presence architecture test
is retired by this explicit rule change. Fix-specific sentinel markers in `build/sentinels.txt`
and behavioral tests remain required; they are distinct from the removed routing boilerplate.

## 5. Specification Invariant Audit Verification
When audits or tests reveal discrepancies between text documentation and active C# implementation:
1. Ground truth resides in mathematical laws, thread safety boundaries, and hardware protection safeguards.
2. Obsolete workflows (e.g., legacy checkmark-based zoom models) must be purged from documentation and replaced with live production invariants (e.g., ZOOMLIVE continuous gestures).
3. Critical unwritten defenses present in source code (e.g., WASAPI 3-second preview clock aborts, render thread OpenGL deadlock mitigations, sub-pixel pointer capture releases, atomic file replacement safeguards) must be codified directly into the governing specifications.

## 6. Change Logging, Map Sync & Storage Inventory
Canonical in `docs/README.md` §0.1 (STORAGEPATHS_01) and §0.3 (CHANGELOG_01 / RELNOTES_01 / RELRECORD_01):
`changes.md` `## Unreleased` + `CHANGELOG.md` `## Upcoming`, `docs/REPO_MAP.md` + `docs/INDEX.md` §1 in step,
storage table 05 SYS-STORAGE-TABLE. Finishing a task without them is forbidden.
