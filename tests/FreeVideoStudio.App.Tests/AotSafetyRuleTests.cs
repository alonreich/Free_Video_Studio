using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AotSafetyRuleTests
{
    private static XDocument LoadAppProject()
        => XDocument.Load(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "FreeVideoStudio.App.csproj"));

    [Fact]
    public void TrimAndAotAnalysersStayOn()
    {
        var doc = LoadAppProject();
        foreach (string name in new[] { "SuppressTrimAnalysisWarnings", "SuppressAotAnalysisWarnings" })
        {
            var values = doc.Descendants(name).Select(e => e.Value.Trim()).ToList();
            Assert.NotEmpty(values);
            Assert.All(values, v => Assert.Equal("false", v, ignoreCase: true));
        }
    }

    [Fact]
    public void NoTrimOrAotCodeIsNonFatal()
    {
        var doc = LoadAppProject();
        var codes = doc.Descendants("WarningsNotAsErrors")
            .SelectMany(e => e.Value.Split(';'))
            .Select(c => c.Trim())
            .Where(c => c.StartsWith("IL", System.StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(codes);
    }

    [Fact]
    public void NoTrimOrAotCodeIsMuted()
    {
        var doc = LoadAppProject();
        var muted = doc.Descendants("NoWarn")
            .SelectMany(e => e.Value.Split(';'))
            .Select(c => c.Trim())
            .Where(c => c.StartsWith("IL", System.StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(muted);
    }

    [Fact]
    public void ThirdPartyFindingsAreNeverCollapsed()
    {
        var doc = LoadAppProject();
        Assert.DoesNotContain(doc.Descendants("TrimmerSingleWarn"), e => e.Value.Trim().Equals("true", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("NAudio")]
    [InlineData("NAudio.Wasapi")]
    [InlineData("Vortice.Direct3D11")]
    [InlineData("Vortice.DXGI")]
    public void AotHostilePackagesStayOut(string package)
    {
        foreach (string project in new[] { "FreeVideoStudio.App", "FreeVideoStudio.Core" })
        {
            var doc = XDocument.Load(Path.Combine(RepoRoot.Path, "src", project, project + ".csproj"));
            Assert.DoesNotContain(doc.Descendants("PackageReference"), e => (string?)e.Attribute("Include") == package);
        }
    }

    [Fact]
    public void NAudioWinMmStaysAtTheNativeAotFixedRelease()
    {
        var minimum = new System.Version(3, 1, 0);
        var versions = new System.Collections.Generic.List<string>();
        foreach (string project in new[] { "FreeVideoStudio.App", "FreeVideoStudio.Core" })
        {
            var doc = XDocument.Load(Path.Combine(RepoRoot.Path, "src", project, project + ".csproj"));
            foreach (string package in new[] { "NAudio.Core", "NAudio.WinMM" })
            {
                var reference = doc.Descendants("PackageReference").SingleOrDefault(e => (string?)e.Attribute("Include") == package);
                Assert.True(reference != null, $"{project} must reference {package} directly.");
                string version = (string?)reference!.Attribute("Version") ?? "";
                Assert.True(System.Version.TryParse(version, out var parsed) && parsed >= minimum,
                    $"{project} references {package} {version}; NativeAOT microphone capture needs >= {minimum}.");
                versions.Add(version);
            }
        }
        Assert.Single(versions.Distinct());
    }

    [Fact]
    public void UnusedAvaloniaControlPackagesStayOutOfRelease()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "FreeVideoStudio.App.csproj"));
        Assert.DoesNotMatch(new Regex(@"<(PackageReference|TrimmerRootAssembly)\s+Include=""Avalonia\.Controls\.DataGrid"""), text);

        var diagnostics = LoadAppProject().Descendants("PackageReference")
            .Where(e => (string?)e.Attribute("Include") == "Avalonia.Diagnostics")
            .ToList();
        Assert.All(diagnostics, e => Assert.Contains("Debug", (string?)e.Attribute("Condition") ?? string.Empty));
    }
}
