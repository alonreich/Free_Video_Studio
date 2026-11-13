using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// REBRAND_02 / REBRAND_03 / OUTNAME_01 — docs/REBRAND_MIGRATION.md.
/// </summary>
public sealed class RebrandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsRebrandTests_" + Guid.NewGuid().ToString("N"));

    public RebrandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData(null, "FreeVideoStudio")]
    [InlineData("", "FreeVideoStudio")]
    [InlineData("   ", "FreeVideoStudio")]
    [InlineData("My Clips", "My Clips")]
    [InlineData("  Trim me  ", "Trim me")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("..\\..\\escape", "_.._escape")]
    [InlineData("name...", "name")]
    [InlineData("name- ", "name")]
    [InlineData("CON", "FreeVideoStudio")]
    [InlineData("nul.txt", "FreeVideoStudio")]
    [InlineData("///", "FreeVideoStudio")]
    public void SanitizeKeepsTheBaseNameInsideItsFolder(string? input, string expected) =>
        Assert.Equal(expected, OutputFileNaming.Sanitize(input, OutputFileNaming.MainDefaultBaseName));

    [Fact]
    public void SanitizeClampsLength() =>
        Assert.Equal(OutputFileNaming.MaxBaseNameLength,
            OutputFileNaming.Sanitize(new string('x', 500), OutputFileNaming.MainDefaultBaseName).Length);

    [Fact]
    public void DefaultNamesFollowTheProductConvention()
    {
        Assert.Equal("FreeVideoStudio-1.mp4", OutputFileNaming.NumberedFileName(OutputFileNaming.MainDefaultBaseName, 1));
        Assert.Equal("FreeVideoStudio-3.mp4", OutputFileNaming.NumberedFileName(OutputFileNaming.MainDefaultBaseName, 3));
        Assert.Equal("FreeVideoStudio-merged-2.mp4", OutputFileNaming.NumberedFileName(OutputFileNaming.MergerDefaultBaseName, 2));
        Assert.Equal("Clips-merged", OutputFileNaming.MergerBaseFor("Clips"));
        Assert.Equal("FreeVideoStudio-merged", OutputFileNaming.MergerBaseFor("   "));
        Assert.True(OutputFileNaming.MergerBaseFor(new string('x', 500)).Length <= OutputFileNaming.MaxBaseNameLength);
        Assert.NotEqual(OutputFileNaming.MainRecoveredPrefix, OutputFileNaming.MergerRecoveredPrefix);
    }

    [Fact]
    public void SweepMovesRescuedRendersAndKeepsOtherLegacyTemp()
    {
        string legacyTemp = Path.Combine(_root, "legacy-temp");
        string currentTemp = Path.Combine(_root, "current-temp");
        Directory.CreateDirectory(Path.Combine(legacyTemp, "staging"));
        File.WriteAllText(Path.Combine(legacyTemp, "Old-Video-RECOVERED-20260101-1.mp4"), "main");
        File.WriteAllText(Path.Combine(legacyTemp, "Merged-Videos-RECOVERED-20260101-1.mp4"), "merge");
        File.WriteAllText(Path.Combine(legacyTemp, "staging", "junk.tmp"), "x");

        var result = LegacyResidueSweep.Run([legacyTemp], currentTemp, [], blockedReason: null);

        Assert.Equal(2, result.RescuedRendersMoved);
        Assert.True(Directory.Exists(legacyTemp));
        Assert.Equal("x", File.ReadAllText(Path.Combine(legacyTemp, "staging", "junk.tmp")));
        Assert.Equal("main", File.ReadAllText(Path.Combine(currentTemp, "FreeVideoStudio-RECOVERED-20260101-1.mp4")));
        Assert.Equal("merge", File.ReadAllText(Path.Combine(currentTemp, "Merged-Videos-RECOVERED-20260101-1.mp4")));
    }

    [Fact]
    public void SweepNeverOverwritesAnExistingRescue()
    {
        string legacyTemp = Path.Combine(_root, "legacy-temp");
        string currentTemp = Path.Combine(_root, "current-temp");
        Directory.CreateDirectory(legacyTemp);
        Directory.CreateDirectory(currentTemp);
        File.WriteAllText(Path.Combine(legacyTemp, "Old-RECOVERED-1.mp4"), "legacy");
        File.WriteAllText(Path.Combine(currentTemp, "FreeVideoStudio-RECOVERED-1.mp4"), "current");

        LegacyResidueSweep.Run([legacyTemp], currentTemp, [], blockedReason: null);

        Assert.Equal("current", File.ReadAllText(Path.Combine(currentTemp, "FreeVideoStudio-RECOVERED-1.mp4")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(currentTemp, "FreeVideoStudio-RECOVERED-1-2.mp4")));
    }

    [Fact]
    public void SweepPreservesConflictsAndUnmigratedLegacyRoots()
    {
        string mirrored = Path.Combine(_root, "legacy-a");
        string partial = Path.Combine(_root, "legacy-b");
        string current = Path.Combine(_root, "current");
        Directory.CreateDirectory(Path.Combine(mirrored, "profiles"));
        Directory.CreateDirectory(partial);
        Directory.CreateDirectory(Path.Combine(current, "profiles"));
        File.WriteAllText(Path.Combine(mirrored, "profiles", "crop.json"), "old");
        File.WriteAllText(Path.Combine(current, "profiles", "crop.json"), "new");
        File.WriteAllText(Path.Combine(partial, "only-here.json"), "unmigrated");

        var result = LegacyResidueSweep.Run([], Path.Combine(_root, "t"), [(mirrored, current), (partial, current)], blockedReason: null);

        Assert.Equal(0, result.LegacyRootsRemoved);
        Assert.Equal(2, result.LegacyRootsKept);
        Assert.Equal("old", File.ReadAllText(Path.Combine(mirrored, "profiles", "crop.json")));
        Assert.True(File.Exists(Path.Combine(partial, "only-here.json")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(current, "profiles", "crop.json")));
    }

    [Fact]
    public void MigrationAndSweepKeepBothSameNamedVersionsAndReferencedVoiceAudio()
    {
        foreach (string name in new[] { "recent", "older" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(_root, name, "settings.json"), name);
        }
        string current = AppDataPaths.MigrateDirectory(_root, "recent", "older");
        string temp = Path.Combine(_root, "old-temp");
        Directory.CreateDirectory(Path.Combine(temp, "voiceovers"));
        File.WriteAllText(Path.Combine(temp, "voiceovers", "take.wav"), "referenced take");
        var roots = new[] { (Path.Combine(_root, "recent"), current), (Path.Combine(_root, "older"), current) };
        for (int i = 0; i < 2; i++)
        {
            var result = LegacyResidueSweep.Run([temp], Path.Combine(_root, "new-temp"), roots, null);
            Assert.Equal(0, result.LegacyRootsRemoved);
            Assert.Equal(3, result.LegacyRootsKept);
        }
        Assert.Equal("recent", File.ReadAllText(Path.Combine(current, "settings.json")));
        Assert.Equal("older", File.ReadAllText(Path.Combine(_root, "older", "settings.json")));
        Assert.Equal("referenced take", File.ReadAllText(Path.Combine(temp, "voiceovers", "take.wav")));
    }

    [Fact]
    public void SweepKeepsLegacyRootWhenCurrentRootIsMissing()
    {
        string legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacy);
        var result = LegacyResidueSweep.Run([], Path.Combine(_root, "t"), [(legacy, Path.Combine(_root, "absent"))], null);
        Assert.Equal(1, result.LegacyRootsKept);
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void ClosedSafetyGateTouchesNothing()
    {
        string legacyTemp = Path.Combine(_root, "legacy-temp");
        Directory.CreateDirectory(legacyTemp);
        var result = LegacyResidueSweep.Run([legacyTemp], Path.Combine(_root, "t"), [], "a previous-brand installation is still present");
        Assert.Equal("a previous-brand installation is still present", result.SkippedReason);
        Assert.True(Directory.Exists(legacyTemp));
    }

    /// <summary>
    /// REBRAND_03 — the previous product name may appear ONLY in the two allow-listed files: the
    /// embedded migration identity resource and the rebrand record. Build output, logs and the
    /// user's own media are not scanned.
    /// </summary>
    [Fact]
    public void PreviousBrandNameAppearsOnlyInTheMigrationAllowList()
    {
        string repo = FindRepoRoot();
        string old = "Fort" + "nite";
        var pattern = new Regex(old + @"[\s_\-\.]?Video[\s_\-\.]?(Software|RECOVERED)|" + old + @"-Video-|" + old + "_Video_Software_C",
            RegexOptions.IgnoreCase);
        string[] allowed =
        [
            Path.Combine("src", "FreeVideoStudio.Core", "Infrastructure", "LegacyAppDataNames.txt"),
            Path.Combine("docs", "REBRAND_MIGRATION.md"),
        ];
        string[] scanned = ["src", "tests", "build", "docs", "scripts", "developer_tools", ".github", "ssl-certificate"];
        string[] extensions = [".cs", ".axaml", ".csproj", ".props", ".sln", ".md", ".txt", ".cmd", ".bat", ".ps1", ".py", ".yml", ".json", ".manifest"];

        var offenders = new List<string>();
        IEnumerable<string> files = scanned.Select(d => Path.Combine(repo, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(repo, "*", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetExtension(f) is ".md" or ".cmd" or ".sln" or ".props" || Path.GetFileName(f) == "project_structure.txt"));
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(repo, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => p is "bin" or "obj")) continue;
            if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            if (allowed.Any(a => string.Equals(a, relative, StringComparison.OrdinalIgnoreCase))) continue;
            if (pattern.IsMatch(Path.GetFileName(file)) || pattern.IsMatch(File.ReadAllText(file))) offenders.Add(relative);
        }
        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FreeVideoStudio.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
