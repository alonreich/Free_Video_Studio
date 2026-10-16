# Free Video Studio — What's New & Release Changelog

This changelog records the latest 5 releases of Free Video Studio with plain-English summaries of user-facing improvements, new features, and reliability updates.

---

## v2026.10.16.1230 (Upcoming)
**What's New in This Release:**
- **No More Random Volume Drops:** Fixed an unexpected volume mute/drop bug across the Main App, Video Merger, and Crop tools; audio playback now remains steady and in sync.
- **Rubberband Drag Selection in Video Merger:** You can now drag a selection box across multiple videos in the queue to highlight and select them simultaneously.
- **Batch Queue Reorder & Delete:** Move or delete multiple selected videos at once using toolbar buttons or the `Delete` key, fully synchronized with timeline markers and preview.
- **Social Media Music Shield (YouTube & TikTok):** Downloadable background tracks now come with copyright attribution info to keep your videos from getting muted or blocked on social platforms.
- **1-Click Attribution Copy on Export:** The export completion screen features a 1-click **[COPY CREDIT TO CLIPBOARD]** button and automatically arms your clipboard when clicking YouTube or TikTok shortcuts.

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

