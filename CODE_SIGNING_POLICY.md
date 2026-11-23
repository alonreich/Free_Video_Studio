# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> Status: the application to the SignPath Foundation open-source program is being prepared. Until it is
> accepted, releases are signed with the project's own certificate, and Windows shows "Unknown publisher".

## What is signed

- `FreeVideoStudio.exe` — the program, and `FreeVideoStudio.exe` on the release page — the installer.
- Once SignPath signing is active, both are built from this public repository by GitHub Actions, and every
  signing request is approved by hand.
- Bundled third-party programs (FFmpeg, ffprobe, mpv and their libraries) are upstream builds; see
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Team roles

| Role | Members |
| :--- | :--- |
| Committers and reviewers | [alonreich](https://github.com/alonreich) |
| Approvers (approve each signing request) | [alonreich](https://github.com/alonreich) |

All members use multi-factor authentication on GitHub and SignPath.

## Privacy

This program does not transfer information to other networked systems unless the user asks for it
(update checks the user allowed, downloads the user starts, AI features that use the user's own key).
Full details: [Privacy policy](PRIVACY.md).

## Install and uninstall

The installer asks Windows for permission once and installs to `C:\Program Files\FreeVideoStudio`.
Uninstall from **Windows Settings › Apps › Free Video Studio › Uninstall**; it removes the program, its shortcuts and its settings.
