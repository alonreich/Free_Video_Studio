# Free Video Studio — What's New & Release Changelog

What people who use Free Video Studio read: plain-English notes about what changed for them. The latest 5 releases are kept here; older ones move to [`CHANGELOG_ARCHIVE.md`](CHANGELOG_ARCHIVE.md) and are never deleted. Every version heading here is a published release (RELRECORD_01).

> **How this file works (RELNOTES_01 — full rules in [`09_DISTRIBUTION_AND_RELEASE.md`](09_DISTRIBUTION_AND_RELEASE.md#DIST-RELEASENOTES)):**
> new notes go under `## Upcoming` — never under a guessed version number. One bullet per user-visible change, `**Headline:** one or two plain sentences`; merge with an existing bullet instead of adding a near-duplicate; at most 15 bullets, each under 320 characters; no backticks, code, file names or engineering tags.
> When an official release is published, `## Upcoming` becomes that release's GitHub notes and the "What's new" on the in-app update card, is renamed to `## v<version> — <date>`, and a fresh `## Upcoming` appears. Technical details belong in [`changes.md`](changes.md).

---

## Upcoming

- **Better Previews:** In portrait mode the preview marks where each in-game overlay of your mask will appear, including Teammate HP and Spectating eye when they are on. Left and Right now jump 2 seconds instead of 5; Ctrl or Shift with the arrows still steps one frame.

- **Full Thumbnail in Chats:** Portrait videos now start with your whole thumbnail slightly smaller on a soft blurred border, so WhatsApp and text messages no longer cut off its top.

- **Easier Speed Editor:** The zoom box shows gentle centre lines while you move it and clicks into the exact middle of the picture. Dragging along the top lane now just moves the playhead; new speed blocks come from MARK START and MARK END only, so that lane is now half as tall.

- **Simpler AI Smart Zoom:** Opens as big as the Speed Editor. Each picture starts zoomed in; zoom with the mouse wheel or the slider beside it, and in portrait mode it looks like your final video. Mark the enemy in each picture; it zooms in so you can fine-tune. Can't see them? Press Next Image.

- **Straight to the Video Merger:** After making two or more videos, the finished screen offers VIDEO MERGER, which opens them in the order you made them. Once a video is done, switching tools or exiting no longer warns you about leaving the editor.

- **Add Music in the Merger:** Scrubbing the music screens no longer turns the video black after the first clip. Every clip now plays in the preview.

- **Keyboard, screen readers and switches:** Speed and quality controls identify themselves and report their values. Thumbnail, voice-over and music buttons have clear names. The tour keeps focus inside it; Back and Skip do what they say. Switches no longer leave a shaded box after a click.

- **Project guide:** The guide now distinguishes routine builds and tests from local installer checks and public releases. It also points directly to the settings, preview, and logging rules.

- **Smaller logs:** Each diagnostic log keeps recent messages in the same file and stays within 5 MB, even when the preview player repeatedly reports an error.

- **Speed Editor preview:** Fixed a startup race that could leave the preview black when opening the Speed Editor with a video already loaded. Resizing or recovering the preview now lets the current frame finish before replacing its video buffers.

- **Meme Gallery:** Memes now show as a picture gallery with video memes and picture memes in their own sections, each with its length. The main screen uses the same gallery. One button downloads the whole free meme library at once.

- **Music Under Each Meme:** Each meme says "Music continues to play" or "Music stops!". Pictures keep the music by default, videos stop it, and your choice is remembered for that meme. No more popup question.

- **Safer Work:** After an unexpected close, your chosen meme comes back with the rest of your work. If the save folder fails at the end of an export, the finished video is kept safe. A project another program is briefly using no longer opens an older backup, and long names with emoji stay readable.

- **Simpler Memes:** One Add meme button opens the same choices everywhere, remembers each style and shows where the meme will land. Choose Start, Here or End; drag two handles to keep the funny part. Preview with sound before adding. Select a meme in the Speed Editor for Preview, Change or Remove.

- **Cleaner Church Bell:** The freeze-frame bell no longer starts with a doorbell-like double ding. Two more bell choices, Mid Start and Late Start, are in Settings › Sound & Music for an even softer strike. Freeze sound exports use consistent file names across regional settings.

## v2026.11.23.0836 — 2026-11-23

- **AI Commentary Captions, Clarified:** Writes timed on-screen captions in fewer, bigger pieces, so it is faster and follows the whole story. It says plainly what the AI is doing and why it waits, shows whether your AI hears game audio, and right-click rewrites a line. Online-only buttons are dimmed in PC-only mode.
- **AI Smart Zoom:** Pick who to follow across three clear pictures, check, then Keep, Fix Tracking or Discard. Following now always runs on your PC (faster, steadier, any length), the camera no longer pumps on tracker wobbles, lost targets are searched for again, and the bottom HUD stays clear.
- **One Simple AI Setup, Windows That Fit:** Three steps: open the key page, come back, connected. The free Google key is recommended; Claude Pro/Max and ChatGPT plans cannot be used here yet. Windows fit your screen, open where you work and return if a monitor is unplugged. Video Quality starts at Good+.
- **AI Kill Tracker:** Marks your kills with a red ✕, fast: your PC finds the likely moments and the AI only confirms them. Cheap scans start by themselves, except on Google keys, which wait for your click so your free daily requests are kept, and show how many you used today. Failures say why.
- **Viral Post Helper:** Activates only when you click Instagram, YouTube, or TikTok on the finished screen (never on WhatsApp or by itself when the popup opens), can be turned off in one click or in Settings, and follows your exact chosen AI model. A share button that cannot open the site now says what to do instead.
- **AI Music Auto-Fit:** Shows the cost first, live steps and seconds, and can be cancelled. Your PC listens to your songs first (energy, beat), so the AI picks by sound, not just by name. Works with Merger timelines, and Ctrl+Z puts your songs back.
- **Simpler Add Music:** Pick a song, pick where it starts, done. The whole song and video are always shown to scale, side by side. Mark Start, Play and Mark End sit under them; Undo glows after a cut. Each step remembers its own window size and place.
- **What You Hear Is What You Get:** Every screen plays your music, voice and memes together, like the saved video. Speed changes no longer make music or voice stutter. Voice slider goes to 150%. Film pictures load in the background, so tools open fast. The Speed Editor no longer locks up after an update.
- **Video Merger Upgrades:** Drag clips to reorder. AI captions stay with the merge and burn into exports. Selected clips move or delete together, blocks across two clips split cleanly for AI tools, and choosing WhatsApp or another finish action closes cleanly without reopening the main window behind it.
- **Timeline Markers, Menus & Credits:** Mark End pauses on the chosen point and Play continues past it; markers scrub smoothly. File › Open Recent lists your projects, each menu command appears once, labels are bigger, Esc closes error and finished dialogs, and videos are opened, never uploaded.
- **A Much Shorter Settings Window:** One simple choice per idea: updates, click sounds, how new videos start, one save folder and file name, one AI company. Light theme is readable (timeline clocks too), and errors say what to do in plain words. Old temporary files are now cleared automatically.
- **Know What's New, Before and After You Update:** The update card lists what the new version brings, the first start after updating shows it again, and Settings › About opens the full notes. A ready update installs when you close the app and Pause stays paused. An update that can't download by itself says so.
- **Open Source, Safer Updates, No Lost Work:** Free software (GPL v3), no tracking. Updates verify and roll back on faults. You are asked before losing edits, crashes restore your work, a newer setting is never undone, and saved projects reopen with their own video, music, freeze frame and text.
- **Movie-Style Freeze Frames:** Freezes ring the Church Bell, which fades as the picture moves on, or pick Cinematic Dramatic Slow-Down in Settings › Sound & Music (press Play to hear it). It plays just below your video's own volume, the same on every screen and in the export. Your last freeze length is remembered.
- **Memes, Rebuilt:** Memes stay where you put them, match the final video, and play on the main screen too. The picker previews, trims, positions and styles memes; corner memes drag and lower game sound; kept music starts and stops on time. Starter memes and songs are now original or Creative-Commons, credited.
