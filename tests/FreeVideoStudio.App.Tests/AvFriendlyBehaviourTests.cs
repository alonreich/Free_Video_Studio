using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SYS-AVCLEAN / SYS-CFASAFE (docs/05_SYSTEM_LIFECYCLE_STORAGE.md) — NOHIDDENTOOLS_01 (no hidden
/// Windows tools), NOLOGONHOOK_01 (no run-at-logon registry entry) and CFASAFE_01 (the app keeps
/// working, quietly, when Windows' "Controlled folder access" refuses it).
/// </summary>
public sealed class AvFriendlyBehaviourTests
{
    private static string Source(string relative) =>
        StripComments(File.ReadAllText(Path.Combine(RepoRoot.Path, relative.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public void NoHiddenTools01_NoForceKillOrEventToolIsStarted()
    {
        var offenders = RepoRoot.SourceFiles(".cs")
            .Where(f => RepoRoot.Relative(f).Replace('\\', '/').StartsWith("src/", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(StripComments(File.ReadAllText(f)), @"""[^""\r\n]*(taskkill|wevtutil)[^""\r\n]*""", RegexOptions.IgnoreCase))
            .Select(RepoRoot.Relative)
            .ToList();
        Assert.True(offenders.Count == 0, "NOHIDDENTOOLS_01 — a hidden Windows tool is started again:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NoHiddenTools01_TheTaskToolRunsOnlyWhenAnOldTaskExists()
    {
        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string relative = RepoRoot.Relative(file).Replace('\\', '/');
            if (!relative.StartsWith("src/", StringComparison.Ordinal)) continue;
            string code = StripComments(File.ReadAllText(file));
            if (!code.Contains("\"schtasks.exe\"", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.True(code.Contains("LegacyScheduledTask.Exists(", StringComparison.Ordinal),
                $"{relative} starts the Windows task tool without asking LegacyScheduledTask.Exists first (NOHIDDENTOOLS_01).");
        }

        string lifecycle = Source("src/FreeVideoStudio.App/DeploymentLifecycle.cs");
        Assert.Matches(new Regex(@"if \(Services\.LegacyScheduledTask\.Exists\([^\n]*\)\)\s*\n\s*await RunHiddenProcessAsync\(""schtasks\.exe"""), lifecycle);
    }

    [Theory]
    [InlineData("FreeVideoStudio", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(@"Folder\Task", false)]
    [InlineData("..", true)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    public void NoHiddenTools01_OnlyPlainTaskNamesAreLookedUp(string name, bool plain) =>
        Assert.Equal(plain, LegacyScheduledTask.IsPlainName(name));

    [Fact]
    public void NoHiddenTools01_AnEventIsCondensedFromItsXml()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>" +
            "<Provider Name='Application Error'/><EventID Qualifiers='0'>1000</EventID>" +
            "<TimeCreated SystemTime='2026-11-11T08:00:00.000Z'/></System>" +
            "<EventData><Data>FreeVideoStudio.exe</Data><Data>  </Data><Data>c0000005\r\n  again</Data></EventData></Event>";

        Assert.Equal("Source: Application Error | Event ID: 1000 | Date: 2026-11-11T08:00:00.000Z | FreeVideoStudio.exe | c0000005 again",
            FreeVideoStudio.App.CrashLogDigest.CondenseXml(xml));
        Assert.True(FreeVideoStudio.App.CrashLogDigest.Mentions(xml, "Other.exe"));
        Assert.True(FreeVideoStudio.App.CrashLogDigest.Mentions("<Data>FFMPEG.EXE</Data>", "Other.exe"));
        Assert.False(FreeVideoStudio.App.CrashLogDigest.Mentions("<Data>notepad.exe</Data>", "Other.exe"));
    }

    [Fact]
    public void NoHiddenTools01_TheEventLogIsReadThroughTheWindowsApi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var events = FreeVideoStudio.App.CrashLogDigest.ReadEvents("*", 3);
        Assert.InRange(events.Count, 1, 3);
        Assert.All(events, xml => Assert.StartsWith("<Event", xml));
        Assert.All(events, xml => Assert.Contains("Source: ", FreeVideoStudio.App.CrashLogDigest.CondenseXml(xml)));
        Assert.Empty(FreeVideoStudio.App.CrashLogDigest.ReadEvents("*[System[Provider[@Name='fvs-no-such-source']]]", 3));
    }

    [Fact]
    public void NoHiddenTools01_UnreadableEventXmlIsKeptAsTextAndCapped()
    {
        Assert.Equal("not xml at all", FreeVideoStudio.App.CrashLogDigest.CondenseXml("not   xml\r\nat all"));
        string longXml = "<Event><EventData><Data>" + new string('x', 5000) + "</Data></EventData></Event>";
        Assert.EndsWith("…(truncated)", FreeVideoStudio.App.CrashLogDigest.CondenseXml(longXml));
    }

    [Fact]
    public void NoLogonHook01_TheInstallerWritesNoActiveSetupEntryAndRemovesAnOldOne()
    {
        string registration = Source("src/FreeVideoStudio.App/Services/UpgradeRegistration.cs");
        Assert.DoesNotContain("StubPath", registration);
        Assert.DoesNotMatch(new Regex(@"CreateSubKey\(\s*ActiveSetupKey"), registration);
        Assert.Contains("root.DeleteSubKeyTree(ActiveSetupKey", registration);

        string program = Source("src/FreeVideoStudio.App/Program.cs");
        Assert.Matches(new Regex(@"IsRunningFromInstallPath\(\)[\s\S]{0,400}await UpgradeCoordinator\.CompleteUserAsync\(\)"), program);
    }

    [Theory]
    [InlineData(@"C:\Users\a\Videos", true)]
    [InlineData(@"C:\Users\a\Videos\", true)]
    [InlineData(@"c:\users\A\videos\FreeVideoStudio\Memes", true)]
    [InlineData(@"C:\Users\a\VideosOther", false)]
    [InlineData(@"C:\Users\a\Downloads", false)]
    [InlineData(@"C:\Users\a\Music\x.mp3", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CfaSafe01_ProtectedMeansTheFolderItselfOrAnythingInside(string? path, bool expected) =>
        Assert.Equal(expected, ControlledFolderAccess.IsUnder(path, new[] { @"C:\Users\a\Videos", @"C:\Users\a\Music\", "" }));

    [Fact]
    public void CfaSafe01_ARefusalIsRememberedForThisProgramAndOnlyForAWhile()
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        const string exe = @"C:\Program Files\FreeVideoStudio\FreeVideoStudio.exe";
        string Marker(DateTime when, string program) => when.ToString("o") + "\n" + program;

        Assert.True(ProtectedFolderGate.DenialIsFresh(Marker(now.AddDays(-1), exe), exe, now));
        Assert.True(ProtectedFolderGate.DenialIsFresh(Marker(now.AddDays(-1), exe.ToUpperInvariant()) + "\r\n", exe, now));
        Assert.False(ProtectedFolderGate.DenialIsFresh(Marker(now - ProtectedFolderGate.RecheckAfter, exe), exe, now));
        Assert.False(ProtectedFolderGate.DenialIsFresh(Marker(now.AddMinutes(5), exe), exe, now));
        Assert.False(ProtectedFolderGate.DenialIsFresh(Marker(now.AddDays(-1), @"C:\Other\FreeVideoStudio.exe"), exe, now));
        Assert.False(ProtectedFolderGate.DenialIsFresh("garbage", exe, now));
        Assert.False(ProtectedFolderGate.DenialIsFresh(null, exe, now));
        Assert.False(ProtectedFolderGate.DenialIsFresh(Marker(now.AddDays(-1), exe), null, now));
    }

    [Fact]
    public void CfaSafe01_UnprotectedFoldersAreNeverProbedOrRefused()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fvs-cfa-" + Guid.NewGuid().ToString("N"));
        Assert.False(ControlledFolderAccess.IsProtectedPath(folder));
        Assert.True(ProtectedFolderGate.CanWrite(folder));
        Assert.True(ProtectedFolderGate.CanWriteNow(folder));
        Assert.False(Directory.Exists(folder));
        Assert.False(ProtectedFolderGate.CanWrite(" "));
    }

    [Fact]
    public void CfaSafe01_TheAppOnlyReadsWindowsSecurityAndRoutesProtectedWritesThroughTheGate()
    {
        string access = Source("src/FreeVideoStudio.Core/Infrastructure/ControlledFolderAccess.cs");
        Assert.DoesNotMatch(new Regex(@"\b(SetValue|CreateSubKey|DeleteValue|DeleteSubKey\w*)\("), access);
        Assert.DoesNotMatch(new Regex(@"writable:\s*true"), access);

        string settings = Source("src/FreeVideoStudio.App/Infrastructure/SettingsManager.cs");
        Assert.Contains("ProtectedFolderGate.CanWrite(music)", settings);
        Assert.Contains("ControlledFolderAccess.MayBlock(inVideos)", settings);
        Assert.Contains("ProtectedFolderGate.CanWrite(configured)", Source("src/FreeVideoStudio.App/Infrastructure/OutputFolderResolver.cs"));
        Assert.Contains("DesktopWritable(desktop)", Source("src/FreeVideoStudio.App/Services/UpgradeRegistration.cs"));
        Assert.Contains("if (failed > 0)", Source("src/FreeVideoStudio.App/Infrastructure/MemeAssets.cs"));

        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string relative = RepoRoot.Relative(file).Replace('\\', '/');
            if (!relative.StartsWith("src/", StringComparison.Ordinal)) continue;
            Assert.False(Regex.IsMatch(StripComments(File.ReadAllText(file)), @"Add-MpPreference|Set-MpPreference|ControlledFolderAccessAllowedApplications", RegexOptions.IgnoreCase),
                $"{relative} tries to change Windows Security (CFASAFE_01: only the user may).");
        }
    }

    /// <summary>NOMEMREAD_01 — no process is opened with memory-read rights to learn its program path.</summary>
    [Fact]
    public void NoMemRead01_ProgramPathsAreAskedWithoutReadingProcessMemory()
    {
        var offenders = RepoRoot.SourceFiles(".cs")
            .Where(f => RepoRoot.Relative(f).Replace('\\', '/').StartsWith("src/", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(StripComments(File.ReadAllText(f)), @"\.MainModule\b|Process\.GetProcesses\(\)"))
            .Select(RepoRoot.Relative)
            .ToList();
        Assert.True(offenders.Count == 0, "NOMEMREAD_01 — MainModule / a whole-machine process scan is back:\n" + string.Join("\n", offenders));

        Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(ProcessImage.GetPath(Environment.ProcessId)), ignoreCase: true);
        Assert.Null(ProcessImage.TryGetPath(int.MaxValue - 3));

        string lifecycle = Source("src/FreeVideoStudio.App/DeploymentLifecycle.cs");
        Assert.Contains("ProcessImage.TryGetPath(process.Id)", lifecycle);
        Assert.Contains("installRoot + Path.DirectorySeparatorChar", lifecycle);
    }

    /// <summary>OPENWITHHOME_01 — only the installed program registers Explorer's "Open with" command.</summary>
    [Fact]
    public void OpenWithHome01_OnlyTheInstalledProgramRegistersOpenWith()
    {
        Assert.True(ShellFileAssociation.IsInstalledProgram(DeploymentFootprint.InstallPath));
        Assert.False(ShellFileAssociation.IsInstalledProgram(Path.Combine(Path.GetTempPath(), "is-ABCDE.tmp", "fvs", "FreeVideoStudio.exe")));
        Assert.False(ShellFileAssociation.IsInstalledProgram(DeploymentFootprint.UninstallPath));
        Assert.Contains("if (!IsInstalledProgram(exePath)) return;", Source("src/FreeVideoStudio.App/ShellFileAssociation.cs"));
    }

    /// <summary>NOMUIWIPE_01 — Windows' own run caches are never edited.</summary>
    [Fact]
    public void NoMuiWipe01_WindowsRunCachesAreLeftAlone()
    {
        var offenders = RepoRoot.SourceFiles(".cs")
            .Where(f => RepoRoot.Relative(f).Replace('\\', '/').StartsWith("src/", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(StripComments(File.ReadAllText(f)), @"MuiCache|UserAssist|\\\\Prefetch", RegexOptions.IgnoreCase))
            .Select(RepoRoot.Relative)
            .ToList();
        Assert.True(offenders.Count == 0, "NOMUIWIPE_01 — Windows trace caches are edited again:\n" + string.Join("\n", offenders));
    }

    /// <summary>NOLURKER_01 — "Later" does not leave an installer waiting in the background.</summary>
    [Fact]
    public void NoLurker01_LaterStartsNoWaitingInstaller()
    {
        string background = Source("src/FreeVideoStudio.App/Services/UpdateService.Background.cs");
        int later = background.IndexOf("vm.LaterRequested", StringComparison.Ordinal);
        int skip = background.IndexOf("vm.SkipRequested", StringComparison.Ordinal);
        Assert.True(later > 0 && skip > later);
        string block = background[later..skip];
        Assert.DoesNotContain("LaunchInstaller(", block);
        Assert.Contains("EnsureInstallOnExit", block);
    }

    /// <summary>SIGNDEPS_01 — bundled tools are signed only with a publicly trusted certificate.</summary>
    [Fact]
    public void SignDeps01_BundledToolsAreSignedOnlyWithAPublicCertificate()
    {
        string signing = Source("build/FvsBuild/CodeSigning.cs");
        int method = signing.IndexOf("SignDependenciesWhenPubliclyTrusted(", StringComparison.Ordinal);
        Assert.True(method > 0);
        int end = signing.IndexOf("LoadManifestSigner()", method, StringComparison.Ordinal);
        Assert.True(end > method);
        string body = signing[method..end];
        Assert.Contains("GetEnvironmentVariable(\"FVS_SIGN_PFX\")", body);
        Assert.DoesNotContain("LocalPfx", body);
        Assert.Contains("CodeSigning.SignDependenciesWhenPubliclyTrusted(Staging.StagingDirPath", Source("build/FvsBuild/Program.cs"));
    }

    /// <summary>CFAFFMPEG_01 — an unprotected folder is never probed with FFmpeg.</summary>
    [Fact]
    public void CfaFfmpeg01_UnprotectedFoldersAreNotProbed()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fvs-cfa-ff-" + Guid.NewGuid().ToString("N"));
        Assert.True(ProtectedFolderGate.FfmpegCanWrite(folder));
        Assert.False(Directory.Exists(folder));
        Assert.False(ProtectedFolderGate.FfmpegCanWrite(""));
        Assert.Contains("ProtectedFolderGate.FfmpegCanWrite(configured)", Source("src/FreeVideoStudio.App/Infrastructure/OutputFolderResolver.cs"));
    }

    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        return Regex.Replace(code, @"(?m)(?<!:)//.*$", string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
