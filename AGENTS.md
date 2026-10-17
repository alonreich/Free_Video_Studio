# AI AGENT ENTRY POINT

Every autonomous AI agent (Claude, Cursor, GitHub Copilot, Google Antigravity, …) working in this repository starts at **[docs/README.md](docs/README.md)** and reads its **§0 "Start Here"** before inspecting or editing anything. All rules, the reading order, the change-logging duty and the repository map live there (ONBOARD_01); this file only points to it.

The three rules that must survive even a skipped read:

1. **Never run `.\Build.cmd`** — it publishes a public release. Use `.\dev_build.cmd`, `dotnet build` or `dotnet test`.
2. **Read and cite the governing spec before changing code** — [docs/SPEC_GOVERNANCE.md](docs/SPEC_GOVERNANCE.md).
3. **Before finishing a task, record it** — technical entry in [docs/changes.md](docs/changes.md) (`## Unreleased`) and plain-English user bullet in [docs/CHANGELOG.md](docs/CHANGELOG.md) (`## Upcoming`), and keep [docs/INDEX.md](docs/INDEX.md) and the repository map (docs/README.md §5) in step (CHANGELOG_01).
