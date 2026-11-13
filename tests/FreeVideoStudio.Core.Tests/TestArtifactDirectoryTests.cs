using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class TestArtifactDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fvs-artifact-tests-" + Guid.NewGuid().ToString("N"));
    private const string Relative = "logs/Media_Results";
    public TestArtifactDirectoryTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "FreeVideoStudio.sln"), "fixture");
    }

    [Fact]
    public void SecondRunReplacesOnlyOwnedOutput()
    {
        string output = TestArtifactDirectory.Reset(root, Relative);
        File.WriteAllText(Path.Combine(output, "previous.mp4"), "fixture");
        string other = TestArtifactDirectory.Reset(root, "logs/Core_Results");
        File.WriteAllText(Path.Combine(other, "Core.trx"), "other suite");
        string sibling = Path.Combine(root, "user.mp4");
        File.WriteAllText(sibling, "keep");
        Assert.Equal(output, TestArtifactDirectory.Reset(root, Relative));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal("keep", File.ReadAllText(sibling));
        Assert.Equal("other suite", File.ReadAllText(Path.Combine(other, "Core.trx")));
    }

    [Fact]
    public void WorkFolderResetKeepsTheGateConsoleLog()
    {
        string suite = TestArtifactDirectory.Reset(root, Relative);
        File.WriteAllText(Path.Combine(suite, "console.log"), "gate output");
        string work = TestArtifactDirectory.Reset(root, Relative + "/work");
        File.WriteAllText(Path.Combine(work, "old.mp4"), "previous");
        TestArtifactDirectory.Reset(root, Relative + "/work");
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        Assert.Equal("gate output", File.ReadAllText(Path.Combine(suite, "console.log")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("logs/Media_Results/../user")]
    [InlineData("logs")]
    [InlineData("logs/Media_Results/other")]
    [InlineData("logs/Bad_Name_Results")]
    [InlineData("tests/MediaPipelineChecks/artifacts")]
    [InlineData("TestResults")]
    public void UnknownOrTraversingTargetsAreRefused(string relative) =>
        Assert.Throws<InvalidOperationException>(() => TestArtifactDirectory.Reset(root, relative));

    [Fact]
    public void LockedOutputFailsInsteadOfStartingWithStaleArtifacts()
    {
        string output = TestArtifactDirectory.Reset(root, Relative);
        using var file = File.Open(Path.Combine(output, "locked"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => TestArtifactDirectory.Reset(root, Relative));
    }

    /// <summary>
    /// TESTOUT_01: logs\&lt;Suite&gt;_Results\ is the only place test output may go. Test and build code
    /// must not name a retired output folder again.
    /// </summary>
    [Fact]
    public void NoTestWritesOutsideTheLogsSuiteFolders()
    {
        string repo = TestArtifactDirectory.FindRepository();
        string self = Path.Combine(repo, "tests", "FreeVideoStudio.Core.Tests", "TestArtifactDirectoryTests.cs");
        string[] retired =
        [
            "\"TestResults\",", "'TestResults\\", "tests/AvRegression", "tests\\AvRegression", "\"AvRegression\")",
            "MediaPipelineChecks/artifacts", "MediaPipelineChecks\\artifacts", "'artifacts\\", "\"artifacts\",",
            "'artifacts' / 'code-export-tests'",
        ];
        string sep = Path.DirectorySeparatorChar.ToString();
        var offenders = new[] { "tests", "build", ".github" }
            .Select(r => Path.Combine(repo, r)).Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep))
            .Where(f => new[] { ".cs", ".ps1", ".cmd", ".yml" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                        || f.EndsWith("test_pull_code_files.py", StringComparison.OrdinalIgnoreCase))
            .Where(f => !string.Equals(f, self, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => retired.Where(r => File.ReadAllText(f).Contains(r, StringComparison.OrdinalIgnoreCase))
                .Select(r => Path.GetRelativePath(repo, f) + " names " + r))
            .Where(o => !(o.StartsWith(Path.Combine("tests", "DeveloperTools.Tests"), StringComparison.OrdinalIgnoreCase)
                          && o.EndsWith("tests/AvRegression", StringComparison.Ordinal)))
            .ToList();
        Assert.True(offenders.Count == 0, "Test output outside logs\\<Suite>_Results: " + string.Join("; ", offenders));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
