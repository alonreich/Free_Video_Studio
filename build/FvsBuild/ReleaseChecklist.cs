using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using FreeVideoStudio.Core.Infrastructure;
using Microsoft.Win32;

namespace FvsBuild;

/// <summary>
/// NEWRELEASE_01 — after a SUCCESSFUL publish (Build.cmd only; never --dev or --no-publish), prepare
/// everything docs\New-Release.md asks the human to do, then open that checklist in Notepad++.
///
/// <para>
/// Why: a brand-new file has no reputation, so Microsoft Defender's cloud AI may flag it in its first
/// days. The free remedy is a manual false-positive submission to Microsoft (WDSI), which has no free
/// API and accepts at most 50 MB per file. The 300 MB installer cannot be sent; the installed program
/// (~62 MB, ~25 MB zipped) can. This class makes that upload a two-minute job:
///   obj\ReleaseSubmit\FreeVideoStudio-program-&lt;version&gt;.zip   — the file to upload to Microsoft
///   obj\ReleaseSubmit\submission-text.txt                     — every form field, ready to copy
/// </para>
///
/// Nothing here can fail the build: a release is already public when this runs. Any problem is a
/// warning, and the checklist still opens.
/// </summary>
internal static class ReleaseChecklist
{
    private const string Repository = "alonreich/Free_Video_Studio";
    private const string SubmitDir = @".\obj\ReleaseSubmit";
    private const string HistoryDir = @".\obj\ReleaseHistory";
    private const string ChecklistPath = @".\docs\New-Release.md";
    private const long WdsiMaxBytes = 50L * 1024 * 1024;

    public static void PrepareAndOpen(string buildVersion, string tag, string installerPath, BuildLog log)
    {
        string? textPath = null;
        try
        {
            textPath = Prepare(buildVersion, tag, installerPath, log);
        }
        catch (Exception ex)
        {
            log.Warn("[NEW RELEASE] Could not prepare the Microsoft submission files: " + ex.Message);
        }

        Open(textPath, log);
    }

    private static string Prepare(string buildVersion, string tag, string installerPath, BuildLog log)
    {
        if (Directory.Exists(SubmitDir)) Directory.Delete(SubmitDir, recursive: true);
        Directory.CreateDirectory(SubmitDir);

        string program = Path.Combine(HistoryDir, buildVersion, InstallPayload.ExecutableName);
        string zipNote;
        string zipPath = Path.Combine(SubmitDir, $"FreeVideoStudio-program-{buildVersion}.zip");
        if (!File.Exists(program))
        {
            zipNote = "NOT CREATED - the program file was missing: " + program;
            log.Warn("[NEW RELEASE] " + zipNote);
        }
        else
        {
            string? sevenZip = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
            }.FirstOrDefault(File.Exists);

            if (sevenZip != null &&
                Cli.RunQuiet(sevenZip, ["a", "-tzip", "-mx=9", "-pinfected", Path.GetFullPath(zipPath), Path.GetFullPath(program)]) == 0)
            {
                zipNote = "password: infected";
            }
            else
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(program, InstallPayload.ExecutableName, CompressionLevel.SmallestSize);
                zipNote = "no password (7-Zip is not installed)";
            }

            long size = new FileInfo(zipPath).Length;
            zipNote += $", {size / (1024.0 * 1024.0):0.0} MB";
            if (size > WdsiMaxBytes)
            {
                zipNote += " - TOO BIG for Microsoft's 50 MB limit";
                log.Warn("[NEW RELEASE] The program zip is over Microsoft's 50 MB upload limit.");
            }
        }

        string installerHash = File.Exists(installerPath) ? Sha256(installerPath) : "(installer not found)";
        string programHash = File.Exists(program) ? Sha256(program) : "(program not found)";
        string releaseUrl = $"https://github.com/{Repository}/releases/tag/{tag}";

        string text = $"""
            FREE VIDEO STUDIO {buildVersion} - TEXT FOR THE MICROSOFT FORM
            Form: https://www.microsoft.com/en-us/wdsi/filesubmission
            Copy each value into the field with the same name.
            ==========================================================================

            Submit file as a:      Software developer

            File to upload:        {Path.GetFullPath(zipPath)}
                                   ({zipNote})

            Microsoft security product used to scan the file:
                                   Microsoft Defender Antivirus (Windows 11)
                                   (or "Microsoft Defender Smartscreen" if only SmartScreen warned)

            Company Name:          Alon Reich

            What do you believe this file is?
                                   Incorrectly detected as malware/malicious

            Detection name:        the exact name Windows Security showed
                                   (example: Trojan:Win32/Wacatac.B!ml)
                                   If nothing was detected yet, write: none - new release, preventive

            Additional information (copy everything between the lines):
            --------------------------------------------------------------------------
            Free Video Studio {buildVersion} is a free video editor for gameplay clips, published by Alon Reich.
            Official download: {releaseUrl}
            The release installer is too big for this form (50 MB limit), so this zip contains the installed
            program file, which is part of the same release.
            SHA-256 of the program file (FreeVideoStudio.exe, inside this zip): {programHash}
            SHA-256 of the release installer (FreeVideoStudio-Setup.exe on GitHub, an Inno Setup package): {installerHash}
            Please review the file and the installer and remove the false detection. Thank you.
            --------------------------------------------------------------------------
            """;
        string textPath = Path.Combine(SubmitDir, "submission-text.txt");
        File.WriteAllText(textPath, text.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\r\n", "\r\n", StringComparison.Ordinal));
        log.Info($"[NEW RELEASE] Microsoft submission files are ready in {Path.GetFullPath(SubmitDir)}");
        return textPath;
    }

    private static void Open(string? textPath, BuildLog log)
    {
        string checklist = Path.GetFullPath(ChecklistPath);
        if (!File.Exists(checklist))
        {
            log.Warn("[NEW RELEASE] docs\\New-Release.md is missing - nothing to open.");
            return;
        }

        var files = new List<string> { checklist };
        if (textPath != null && File.Exists(textPath)) files.Add(Path.GetFullPath(textPath));

        try
        {
            string? notepadPlusPlus = FindNotepadPlusPlus();
            var start = new ProcessStartInfo(notepadPlusPlus ?? "notepad.exe") { UseShellExecute = false };
            if (notepadPlusPlus != null)
            {
                start.ArgumentList.Add("-multiInst");
                foreach (string file in files) start.ArgumentList.Add(file);
            }
            else
            {
                start.ArgumentList.Add(checklist);
                log.Warn("[NEW RELEASE] Notepad++ was not found; opened the checklist in Notepad instead.");
            }

            Process.Start(start)?.Dispose();
            log.Info("[NEW RELEASE] Opened docs\\New-Release.md - follow it now.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn("[NEW RELEASE] Could not open the checklist: " + ex.Message + "  Open docs\\New-Release.md yourself.");
        }
    }

    private static string? FindNotepadPlusPlus()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe"),
        };
        try
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = root.OpenSubKey(@"SOFTWARE\Notepad++");
                if (key?.GetValue(null) is string folder && folder.Length > 0)
                    candidates.Insert(0, Path.Combine(folder, "notepad++.exe"));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return candidates.FirstOrDefault(File.Exists) ?? Cli.FindOnPath("notepad++.exe");
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
