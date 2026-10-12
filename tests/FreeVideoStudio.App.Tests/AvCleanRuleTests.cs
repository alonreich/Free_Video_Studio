
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SYS-AVCLEAN — production code must not use the techniques that made antivirus report new
/// releases as a trojan: PowerShell or cmd.exe as a helper (NOPOWERSHELL_01/02), a "ping … del"
/// self-delete chain (NOSELFDELETE_01), or a recurring SYSTEM scheduled task (NOSYSTEMTASK_01).
/// Scans every .cs file under src/. Comments are stripped first so the spec tags may name them.
/// </summary>
public sealed class AvCleanRuleTests
{
    private static readonly (string Name, Regex Pattern)[] Forbidden =
    {
        ("powershell.exe helper (NOPOWERSHELL_01/02)", new Regex(@"""[^""\r\n]*powershell(\.exe)?[^""\r\n]*""", RegexOptions.IgnoreCase)),
        ("cmd.exe helper (NOSELFDELETE_01)", new Regex(@"""[^""\r\n]*\bcmd(\.exe)?""", RegexOptions.IgnoreCase)),
        ("-ExecutionPolicy Bypass", new Regex(@"ExecutionPolicy\s+Bypass", RegexOptions.IgnoreCase)),
        ("-EncodedCommand", new Regex(@"""-EncodedCommand""", RegexOptions.IgnoreCase)),
        ("ping delay chain", new Regex(@"ping\s+127\.0\.0\.1", RegexOptions.IgnoreCase)),
        ("SYSTEM scheduled task (NOSYSTEMTASK_01)", new Regex(@"""/RU""\s*,\s*""SYSTEM""", RegexOptions.IgnoreCase)),
        ("schtasks /Create (NOSYSTEMTASK_01)", new Regex(@"""/Create""", RegexOptions.IgnoreCase)),
        ("embedded payload read (NODROPPER_01)", new Regex(@"GetManifestResourceStream\(\s*""[^""]*payload\.zip""", RegexOptions.IgnoreCase)),
    };

    [Fact]
    public void ProductionCodeUsesNoAntivirusTriggeringTechniques()
    {
        var offenders = new List<string>();
        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string relative = RepoRoot.Relative(file).Replace('\\', '/');
            if (!relative.StartsWith("src/", StringComparison.Ordinal)) continue;
            string code = StripComments(File.ReadAllText(file));
            foreach (var (name, pattern) in Forbidden)
            {
                foreach (Match m in pattern.Matches(code))
                {
                    int line = code.Take(m.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{relative}:{line}  {name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "SYS-AVCLEAN (docs/05_SYSTEM_LIFECYCLE_STORAGE.md) — antivirus-triggering technique found:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    /// NODROPPER_01 — the program never carries the release inside its own resources again; the
    /// Inno Setup script stays a non-elevated unpacker that installs nothing by itself and forwards
    /// only the known updater switches.
    /// </summary>
    [Fact]
    public void NoDropper01_TheReleaseIsAnInnoSetupPackageNotAnEmbeddedPayload()
    {
        string csproj = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "FreeVideoStudio.App.csproj"));
        Assert.DoesNotMatch(new Regex(@"<EmbeddedResource\s+Include=""[^""]*\.(zip|exe|dll)""", RegexOptions.IgnoreCase), csproj);

        string iss = File.ReadAllText(Path.Combine(RepoRoot.Path, "build", "installer", "FreeVideoStudio.iss"));
        foreach (string required in new[]
                 {
                     "PrivilegesRequired=lowest", "Uninstallable=no", "CreateAppDir=no",
                     "VersionInfoOriginalFileName=FreeVideoStudio-Setup.exe", "OutputBaseFilename=FreeVideoStudio-Setup",
                     @"DestDir: ""{tmp}\fvs""", "--install --smart-update", "runasoriginaluser",
                 })
            Assert.Contains(required, iss);
        Assert.DoesNotContain("PrivilegesRequired=admin", iss);
        Assert.DoesNotMatch(new Regex(@"^\s*\[(Registry|Icons|UninstallRun|UninstallDelete)\]", RegexOptions.Multiline), iss);
    }

    /// <summary>
    /// NOTEMPEXEC_01 — the uninstaller does not copy itself to %TEMP%, and nothing that is started
    /// elevated by the updater is staged in %TEMP%.
    /// </summary>
    [Fact]
    public void NoTempExec01_NothingElevatedIsStagedInTemp()
    {
        foreach (string relative in new[]
                 {
                     "src/FreeVideoStudio.App/DeploymentLifecycle.cs",
                     "src/FreeVideoStudio.App/Services/UpgradeCoordinator.cs",
                     "src/FreeVideoStudio.App/Services/UpdateService.cs",
                     "src/FreeVideoStudio.App/Services/UpdateService.Background.cs",
                     "src/FreeVideoStudio.App/Services/UpdatePreparer.cs",
                 })
        {
            string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot.Path, relative)));
            Assert.False(code.Contains("GetTempPath", StringComparison.Ordinal), $"{relative} stages files in %TEMP% (NOTEMPEXEC_01).");
        }
        string lifecycle = StripComments(File.ReadAllText(Path.Combine(RepoRoot.Path, "src/FreeVideoStudio.App/DeploymentLifecycle.cs")));
        Assert.DoesNotMatch(new Regex(@"File\.Copy\([^;\n]*[Tt]emp"), lifecycle);
        Assert.DoesNotContain("SessionTempFolder", lifecycle);
    }

    [Theory]
    [InlineData(@"C:\Program Files\FreeVideoStudio\FreeVideoStudio.exe,0", @"C:\Program Files\FreeVideoStudio\FreeVideoStudio.exe", 0)]
    [InlineData(@"C:\a,b\app.exe,3", @"C:\a,b\app.exe", 3)]
    [InlineData(@"C:\app.exe", @"C:\app.exe", 0)]
    [InlineData(@"C:\a,b\app.exe", @"C:\a,b\app.exe", 0)]
    public void IconLocationSplitsLikeWScript(string location, string path, int index)
    {
        Assert.Equal((path, index), FreeVideoStudio.App.ShellLinkWriter.SplitIconLocation(location));
    }

    /// <summary>Removes // and /* */ comments; keeps line breaks so line numbers stay right.</summary>
    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        return Regex.Replace(code, @"(?m)(?<!:)//.*$", string.Empty);
    }
}
