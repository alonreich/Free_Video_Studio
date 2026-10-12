# NEW RELEASE — WHAT TO DO NOW (about 10 minutes, free)

**Why:** every new release is a brand-new file, and Windows has never seen it before.
Microsoft Defender's AI may call it a virus for the first few days. Telling Microsoft
"this is my app, it is clean" gets the warning removed sooner. Free, no account needed
(a free Microsoft account only lets you track the answer).

Two tabs just opened in Notepad++:
- **this checklist**
- **submission-text.txt**: every answer for Microsoft's form, ready to copy

Your files are ready in `C:\FreeVideoStudio\obj\ReleaseSubmit\`.

---

## STEP 1 — Scan the new file at VirusTotal (2 min)

1. Open **https://www.virustotal.com**
2. Drag in `C:\FreeVideoStudio\compiled\FreeVideoStudio-Setup.exe` (it takes files up to 650 MB).
   VirusTotal unpacks the Inno Setup package and scans every file inside it separately (NODROPPER_01).
3. Wait for the result. Find the row named **Microsoft**.
4. Write down every antivirus name that shows a red warning. You'll need them in Step 3.
5. Copy the page address (link) and keep it.

> 0 red rows? Still do Step 2. It's a free head start.

## STEP 2 — Tell Microsoft (5 min)

1. Open **https://www.microsoft.com/en-us/wdsi/filesubmission**
2. Choose **Software developer**, then **Continue**.
3. Fill in the form by copying from the **submission-text.txt** tab, field by field.
4. **Select the file:** `C:\FreeVideoStudio\obj\ReleaseSubmit\FreeVideoStudio-program-<version>.zip`
   - Microsoft only accepts files up to 50 MB. The installer is too big, so this zip holds the program file from inside it.
5. Optional: paste the VirusTotal link from Step 1 at the end of **Additional information**.
6. Tick the "I'm not a robot" check, then **Submit**.
7. When it asks you to sign in: **Sign in** with your free Microsoft account (recommended), or **Skip**.

## STEP 3 — Other antivirus names from Step 1 (only if there were any)

For each name (for example Avast, Kaspersky, Bitdefender):
1. Search Google for: `<name> false positive report`
2. Open that company's **own** official page.
3. Paste the GitHub release link and the VirusTotal link (both are in Step 1 and in submission-text.txt).

## STEP 4 — Wait, then check (1 to 3 days later)

1. Look at your answer at **https://www.microsoft.com/en-us/wdsi/submissionhistory** (you must have signed in for it to show there).
2. When Microsoft says the file is clean, update your own PC:
   **Windows Security → Virus & threat protection → Protection updates → Check for updates**.
3. Download the release from GitHub again. It should no longer be flagged.

---

## Good habits (they make every next release safer)

- **Release less often.** Every release starts from zero trust. Bundle fixes into fewer releases.
- **Never change the app's name, publisher (Alon Reich), or certificate.** Trust builds up on these.
- **Never upload the same release twice.** One submission per new version is enough.
- **Never tell users to turn off their antivirus.** Point them to the GitHub release page instead.

Honest limits: this improves your odds, it doesn't guarantee anything. Microsoft reviews by hand
and gives no deadline. Only a paid, publicly trusted signing certificate removes the "unknown
publisher" screen.
