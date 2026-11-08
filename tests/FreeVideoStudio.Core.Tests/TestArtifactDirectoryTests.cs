using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class TestArtifactDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fvs-artifact-tests-" + Guid.NewGuid().ToString("N"));
    private const string Relative = "tests/MediaPipelineChecks/artifacts/current";
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
        string sibling = Path.Combine(root, "user.mp4");
        File.WriteAllText(sibling, "keep");
        Assert.Equal(output, TestArtifactDirectory.Reset(root, Relative));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal("keep", File.ReadAllText(sibling));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("tests/MediaPipelineChecks/artifacts/current/../user")]
    [InlineData("tests/MediaPipelineChecks/artifacts")]
    public void UnknownOrTraversingTargetsAreRefused(string relative) =>
        Assert.Throws<InvalidOperationException>(() => TestArtifactDirectory.Reset(root, relative));

    [Fact]
    public void LockedOutputFailsInsteadOfStartingWithStaleArtifacts()
    {
        string output = TestArtifactDirectory.Reset(root, Relative);
        using var file = File.Open(Path.Combine(output, "locked"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => TestArtifactDirectory.Reset(root, Relative));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
