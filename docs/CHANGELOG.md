# Free Video Studio — What's New & Release Changelog

What people who use Free Video Studio read: plain-English notes about what changed for them. The latest 5 releases are kept.

> **How this file works (RELNOTES_01 — full rules in [`09_DISTRIBUTION_AND_RELEASE.md`](09_DISTRIBUTION_AND_RELEASE.md#DIST-RELEASENOTES)):**
> new notes go under `## Upcoming` — never under a guessed version number. One bullet per user-visible change, `**Headline:** one or two plain sentences`; merge with an existing bullet instead of adding a near-duplicate; at most 15 bullets; no code, file names or engineering tags.
> When an official release is published, `## Upcoming` becomes that release's GitHub notes and the "What's new" on the in-app update card, is renamed to `## v<version> — <date>`, and a fresh `## Upcoming` appears. Technical details belong in [`changes.md`](changes.md).

---

## Upcoming
- **AI Commentary, Upgraded:** Pick a style (Hype, Chill, Coach, Roast) or mix Funny, Sarcastic, Energetic and Tactical yourself. Captions can sit high, middle or low, speak Hebrew, read clearer, land right on the action, and a right-click rewrites one line. Your choices are remembered.
- **AI Works With Google, Anthropic and OpenAI, Everywhere:** Every AI feature works with whichever company you choose, uses their current models, and switches to a working model by itself if one is retired.
- **One Simple AI Setup:** One "Connect Your AI" window and one AI Features tab in Settings cover every AI feature, and the privacy questions say exactly what each company receives.
- **See What Each AI Job Used:** After AI Smart Zoom or AI Commentary, the app shows how many AI requests and tokens the job used and how much of your allowance was left.
- **AI Kill Tracker:** When turned on, the app marks each of your kills with a red ✕ on the timeline and in the speed editor. Click a ✕ to jump straight to it.
- **Viral Post Helper:** When your export finishes, the AI writes a title, caption and hashtags with one-click copy buttons, and says honestly when it cannot.
- **AI Music Auto-Fit:** The Add Music wizard can pick the best song for your video's mood, shows each step it takes, can be cancelled, and one Ctrl+Z brings your old songs back.
- **Simpler Add Music:** Two steps instead of three, as many songs as you like, each shown as a named block you can resize, quiet intros and endings trimmed automatically, and a clear bar that tells you whether the music covers your whole video.
- **What You Hear Is What You Get:** Song previews stop when you move on, play exactly like the saved video, and the preview player no longer freezes or turns black while you scrub.
- **Video Merger Upgrades:** Drag a box to select several videos, move or delete them together, and edit or remove music just like in the main app.
- **Music Credits Made Easy:** Free background songs come with their credit text, and the export screen copies it in one click so YouTube and TikTok do not mute your video.
- **A Clearer Settings Window:** 6 simple tabs, a search bar, plain help on every option, Backup and Import, and Restore buttons. It asks before throwing away changes, and starting choices like speed and phone format now really work. Updates keep your window sizes and preferences, and random volume drops are gone.
- **Know What's New Before You Update:** The update card now lists the highlights of the new version, the app checks again during long sessions, and the Beta choice in Settings › General now really gets test versions first.
- **Smaller, Safer Updates:** Updates download only the parts that changed and check every piece before using it, and the free-space check now asks only for the space an update really needs.
- **Safer Installation:** The part of an update that needs administrator permission now runs only from a folder other programs cannot change.

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
