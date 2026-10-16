# AI AGENT GOVERNANCE & EXECUTION DIRECTIVE

Every autonomous AI agent (Google Antigravity, Claude, Cursor, GitHub Copilot workspace agent, etc.) operating in this repository is strictly bound by the following non-negotiable operational invariants:

## 1. Zero-Leakage Architecture Routing
- **Mandatory Entry Point:** Always read [project_structure.txt](file:///c:/FreeVideoStudio/project_structure.txt) and [docs/SPEC_GOVERNANCE.md](file:///c:/FreeVideoStudio/docs/SPEC_GOVERNANCE.md) before inspecting or editing code.
- **Stop-and-Read Mandate:** Never edit code without reading and citing its mapped specification in `docs/`.
- **In-Code Spec Contract:** Every new production source file must carry the `[SPEC CONTRACT]` sentinel block at Line 1.

## 2. Mandatory End-User Changelog Staging (CHANGELOG_01)
Before concluding any task that modifies application logic, UI elements, workflows, or project structure:
- **Target File:** [docs/CHANGELOG.md](file:///c:/FreeVideoStudio/docs/CHANGELOG.md).
- **Format:** Add or update bullet points under the topmost `## v... (Upcoming)` release header.
- **Language:** Plain, short, oversimplified English for end users. Technical jargon, internal method names, and class names are strictly forbidden. Explain the actual capability or bug fix directly.
- **5-Release Retention:** Maintain strictly the 5 latest releases. When adding a new release header, prune the oldest.
- **Automation Pipeline:** This file is read directly by `build/FvsBuild/GitHubReleasePublisher.cs` during official releases and powers the in-app update notification card.
- **Project Structure Sync:** If files or architectural components were added or removed, update [project_structure.txt](file:///c:/FreeVideoStudio/project_structure.txt) and [docs/INDEX.md](file:///c:/FreeVideoStudio/docs/INDEX.md).
- **Prohibition:** An AI agent is strictly forbidden from finishing a task or committing code without completing this changelog staging step.

## 3. Strict Ban on `Build.cmd`
- **Never Run `.\Build.cmd`:** It publishes live production releases to GitHub Cloud.
- **Safe Local Testing:** Always use `.\dev_build.cmd` or `dotnet test` / `dotnet build`.
