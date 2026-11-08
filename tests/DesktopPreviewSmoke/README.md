# Interactive desktop preview regressions

Run from the repository root on an unlocked Windows desktop with hardware preview available.
Keep the test window visible until it closes; another window covering the sampled preview makes
the merger check fail with an explicit obstruction message.

```powershell
dotnet run --project tests/DesktopPreviewSmoke/DesktopPreviewSmoke.csproj -c Release
dotnet run --project tests/DesktopPreviewSmoke/DesktopPreviewSmoke.csproj -c Release -- --merger
dotnet run --project tests/DesktopPreviewSmoke/DesktopPreviewSmoke.csproj -c Release -- --merger --silent-middle
```

`tests/run-all-tests.cmd` includes all three checks. The ordinary full A/V gate does not run this
interactive tier; its null-video player tests cannot prove which pixels the desktop displays.

- The default check exercises the production WGL/D3D video control, paused seeks, playback,
  resize, detach/reattach and repeated shutdown.
- The merger check opens the production `VideoMergerWindow`, adds three separate colored files
  during playback and observes its real timer, automatic selection and displayed frames across
  both joins. It does not seek or force a resume after adding the files. Fixtures differ in frame
  rate and dimensions; the second variant also has a soundless middle clip.
- Merger clock stalls and persistent wrong colors fail after 250 ms, with a 450 ms initial grace;
  backwards clock jumps over 20 ms fail. These match the corresponding A/V gate limits.
- These fixtures verify clip identity and clock continuity. They do not measure audible speaker
  output or prove that every possible user file works. Preserve real failing files as additional
  repro cases when a field report differs from these generated inputs.

The silent-middle regression found a join-time audio drain/WASAPI reopen under mpv's default
`gapless-audio=weak`. The production merger sets `gapless-audio=yes` before EDL loads so the
audio device stays open across decoder-format changes (`MERGERGAPLESS_01`).

Fixtures, logs and isolated application state live under `tests/AvRegression/desktop`.
`TestArtifactDirectory` replaces only that checked test directory and shares the suite's
exclusive lock. Test inputs never come from the user's saved merger session.

Governing specifications: `docs/README.md#GOV-AVGATE`, `01` TL-COMPOSITE,
`02` AUD-IPCSTATE and `04` UI-GPUPRESENT2/UI-MERGERQUEUE.
