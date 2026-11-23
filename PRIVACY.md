# Privacy Policy — Free Video Studio

Free Video Studio is a desktop video editor. It has **no accounts, no analytics, no telemetry and no ads**.
Your videos, projects and settings stay on your computer.

The app connects to the internet **only** in these cases:

| When | Where it connects | What is sent | How to turn it off |
| :--- | :--- | :--- | :--- |
| Checking for updates — only if you answered **Yes** to "Check for updates automatically?" on first start, or when you click *Check for Updates* | `api.github.com`, `github.com` (the project's release page) | A normal web request for the latest release. No personal data, no file names, no usage data. | Answer **No** on first start, or turn it off any time in **Settings › General**. |
| Downloading an update you approved | GitHub release files | Nothing besides the download request. | Same as above. |
| **Download more** memes or songs (only when you click it) | `raw.githubusercontent.com`, `media.githubusercontent.com` (this project's own repository) | Nothing besides the download request. | Don't click it. |
| AI features (Smart Zoom, Commentary, HUD detection, Music Auto-Fit, Viral Post helper) — only after **you** paste your own API key | The AI company **you** chose: Google (`generativelanguage.googleapis.com`), Anthropic (`api.anthropic.com`) or OpenAI (`api.openai.com`) | The frames, audio summary or text that the feature needs, sent with your own key. That company's privacy policy applies. | Don't enter a key, or remove it in **Settings › AI**. |
| Links you click (YouTube Studio, TikTok, Instagram, WhatsApp, key pages, song credits) | That website, in your browser | Whatever your browser sends. | Don't click them. |

The app never uploads your videos anywhere by itself, never sells or shares data, and keeps your API keys
encrypted on this computer only (Windows DPAPI, readable only by your Windows account).
While the "Connect your AI" window is open and you have pressed its key-page button, it reads your
clipboard on this computer to pick up a key you copy; it uses only text that looks like an AI key, never logs it,
and stops when the window closes.
If you choose **Continue with ChatGPT**, you sign in on OpenAI's own page in your browser. The app keeps the sign-in
(tokens and your ChatGPT account e-mail) encrypted on this computer only, uses it only to send the AI features' requests
to OpenAI on your ChatGPT plan, and deletes the tokens when you press Sign out.

**Uninstalling** removes the program, its shortcuts and its settings (Windows › Settings › Apps › Free Video Studio › Uninstall).

Questions: open an issue at https://github.com/alonreich/Free_Video_Studio/issues
