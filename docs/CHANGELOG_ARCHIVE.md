# Free Video Studio — Older Release Notes

Older release notes, moved here from `CHANGELOG.md` so it stays short (RELRECORD_01, `09_DISTRIBUTION_AND_RELEASE.md` DIST-RETENTION). Nothing here is ever deleted; the release build adds to the top of this file.

> **RELRECORD_01 — the sections v2026.10.04.1120, v2026.10.08.1415, v2026.10.11.1830 and v2026.10.13.2238 have NO tag and NO release on GitHub** (checked 2026-10-18: the only published release is `v2026.09.26.1215`). They were written under version headings before the release gate existed, so `CHANGELOG.md` described downloads that never existed. They are kept as history only; what they describe ships with the next real release.

---

## v2026.10.13.2238
**What's New in This Release:**
- **No Surprise Restarts:** Choosing "Later" when an update is ready now defers the update until you close the app without unexpectedly reopening it.
- **Skip Directly from Update Card:** Added a "SKIP" button directly to the update notification card so you can skip unwanted releases in one click.
- **Disk Space Pre-Flight Check:** The updater now verifies you have enough free drive space before downloading update packages, preventing failed or corrupted downloads.
- **Metered Connection & Data-Saver Aware:** Background updates check network cost and honor data-saver preferences to avoid consuming mobile or metered bandwidth.
- **Stable & Beta Update Channels:** You can now choose between Stable releases and Beta/Preview releases in Settings › About.
- **Clear Rate-Limit Messaging:** GitHub API rate limits are now explained in plain language instead of being misreported as an offline network connection.
- **Safe Version Downgrades:** Running an older installer now asks for confirmation to proceed safely instead of throwing a hard error.
- **Fast Smart Updates from Clean Builds:** Previous release binaries are automatically cached from GitHub so binary delta updates are always generated.
- **Clean Local Development Builds:** Building locally with `--dev` no longer alters tracked version and project files.
- **Official 5-Release Retention:** GitHub official releases retain the latest 5 versions for download; the local `compiled\` folder strictly holds only the latest installer.
- **Corrected System Path:** Fixed storage path display in Settings to accurately show `%LOCALAPPDATA%\FreeVideoStudio`.

---

## v2026.10.11.1830
**What's New in This Release:**
- **Smoother Timeline Playback:** Optimized the preview rendering pipeline for stable 60 FPS playback during scrubbing and edits.
- **Instant Audio Waveforms:** Waveform peaks render instantly during linear and granular speed edits.
- **Faster Export Times:** Enhanced FFmpeg export filtergraph for 9:16 mobile montages with hardware acceleration.

---

## v2026.10.08.1415
**What's New in This Release:**
- **AI Smart Zoom Tracking:** Added Google Gemini-powered subject tracking for intelligent portrait reframing of widescreen clips.
- **HUD Avoidance:** Smart framing automatically avoids vital on-screen elements such as health bars and mini-maps.
- **Offline Reliability:** Automatic offline fallback ensures edits continue smoothly even when internet access drops.

---

## v2026.10.04.1120
**What's New in This Release:**
- **Video Merger Loudness Leveling:** Auto-levels audio across multiple stitched video clips so volume remains consistent.
- **Thumbnail Intro Scraping:** Automatically removes cover frames when joining gameplay highlights.
- **Rock-Solid Session Recovery:** Crash recovery now preserves your complete project layout, trim points, and audio settings.
