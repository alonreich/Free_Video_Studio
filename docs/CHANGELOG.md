# Free Video Studio — What's New & Release Changelog

What people who use Free Video Studio read: plain-English notes about what changed for them. The latest 5 releases are kept here; older ones move to [`CHANGELOG_ARCHIVE.md`](CHANGELOG_ARCHIVE.md) and are never deleted. Every version heading here is a published release (RELRECORD_01).

> **How this file works (RELNOTES_01 — full rules in [`09_DISTRIBUTION_AND_RELEASE.md`](09_DISTRIBUTION_AND_RELEASE.md#DIST-RELEASENOTES)):**
> new notes go under `## Upcoming` — never under a guessed version number. One bullet per user-visible change, `**Headline:** one or two plain sentences`; merge with an existing bullet instead of adding a near-duplicate; at most 15 bullets; no code, file names or engineering tags.
> When an official release is published, `## Upcoming` becomes that release's GitHub notes and the "What's new" on the in-app update card, is renamed to `## v<version> — <date>`, and a fresh `## Upcoming` appears. Technical details belong in [`changes.md`](changes.md).

---

## Upcoming
- **AI Commentary, Upgraded:** Pick or mix caption styles, see live progress, elapsed time, and cost/token estimates, and right-click lines to rewrite them. Portrait captions avoid HUD overlays and inline memes across merged clips and Hebrew text.
- **AI Works With Google, Anthropic and OpenAI:** Every AI tool works with all three (newest Claude models included), says plainly when an answer was cut off, lets you retry just the part of a long job that failed, and reuses earlier answers instead of paying again.
- **One Simple AI Setup:** The setup wizard compares providers plainly and verifies your key step by step. Before a longer AI job the app checks your key in seconds and asks once, showing what is sent and the estimated cost. Settings shows what AI used this session.
- **AI Kill Tracker:** When turned on, the app marks each of your kills with a red ✕, shows live progress and lets you Cancel anytime. Results are remembered after a restart, failures say why, and kills also show for Video Merger clips in the Speed Editor.
- **Viral Post Helper:** When your export finishes, the AI shows live progress steps, elapsed time, and token estimates, reuses video frames across new versions so regenerations are fast, lets you edit the title and caption before copying, and says honestly when something fails.
- **AI Music Auto-Fit:** The Add Music wizard shows pre-run estimates, live step-by-step progress, active provider/model details, and elapsed seconds, caches extracted frames and song loudness locally, honors edited merger timelines, can be cancelled anytime, and supports Ctrl+Z undo.
- **Simpler Add Music:** Two steps, multiple songs with resizable blocks, quiet intros and endings trimmed automatically, and a clear coverage bar. Music no longer ends early because the video speed changed; it keeps its short closing fade.
- **What You Hear Is What You Get:** Voice recordings no longer lose volume when saved to video. A new voice slider goes from silent to 150%, snaps to normal at 100%, and carries your choice through previews and saved videos. Song previews stop when you move on and no longer freeze while scrubbing.
- **Video Merger Upgrades:** Drag clip blocks on the timeline to reorder videos with instant queue list updates. AI commentary captions stay saved with the merge and burn into exported videos. Preview playback seamlessly continues across joined clips, and multi-selected clips move or delete together.
- **Timeline Markers & Music Credits:** Setting Mark End pauses preview at the chosen point, and clicking Play continues past the mark to inspect cut footage. Dragging markers scrubs the playhead smoothly without getting stuck when focus changes. Song credits open easily from export cards.
- **A Much Shorter Settings Window:** One simple choice per idea: updates, click sounds, how new videos start, one safety switch, one save folder and file name, "keep game sounds clear" and one AI company. Saving from About works, and "Same as last time" means your last video.
- **Know What's New, Before and After You Update:** The update card lists what the new version brings, the first start after updating shows it again, and Settings › About opens the full notes. A ready update installs when you close the app and Pause stays paused.
- **Open Source, Safer Updates:** Now free software (GNU GPL v3) with a plain privacy policy: no tracking. Updates download only changes, check certificates and roll back on faults. Antivirus and firewalls block it less. With folder protection on, the app saves where it can and says why.
- **Movie-Style Freeze Frames:** A freeze now plays a deep slow-motion "boom" for its whole length, the music dips under it and fades back after. The Speed Editor remembers your last zoom style and freeze length.
- **Memes, Rebuilt:** Memes stay where you put them, match the final video, and play on the main screen too. The picker previews, trims, positions and styles memes; corner memes drag and lower game sound; kept music starts and stops on time. Starter memes and songs are now original or Creative-Commons, credited.
