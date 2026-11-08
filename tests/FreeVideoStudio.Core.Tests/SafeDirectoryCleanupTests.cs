using System.Diagnostics;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class SafeDirectoryCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsCleanupBoundary_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];

    public SafeDirectoryCleanupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DeletesOrdinaryTreeAndReadonlyFiles()
    {
        string tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "nested"));
        string file = Path.Combine(tree, "nested", "read-only.txt");
        File.WriteAllText(file, "owned");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        SafeDirectoryCleanup.DeleteTree(tree);
        Assert.False(Directory.Exists(tree));
        SafeDirectoryCleanup.DeleteTree(tree); // already absent
    }

    [Theory]
    [InlineData("root")]
    [InlineData("child")]
    [InlineData("ancestor")]
    public void RejectsJunctionAtEveryBoundaryWithoutTouchingTarget(string placement)
    {
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "subdir"));
        string sentinel = Path.Combine(outside, "subdir", "keep.txt");
        File.WriteAllText(sentinel, "not owned by cleanup");
        string tree = Path.Combine(_root, "tree");
        string target;
        if (placement == "child")
        {
            Directory.CreateDirectory(tree);
            Junction(Path.Combine(tree, "link"), outside);
            target = tree;
        }
        else
        {
            Junction(tree, outside);
            target = placement == "ancestor" ? Path.Combine(tree, "subdir") : tree;
        }
        Assert.Throws<IOException>(() => SafeDirectoryCleanup.DeleteTree(target));
        Assert.Equal("not owned by cleanup", File.ReadAllText(sentinel));
    }

    [Fact]
    public void PinnedAncestorsAndFilesCannotBeRenamedDuringDeletion()
    {
        string tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(tree);
        string file = Path.Combine(tree, "owned.txt");
        File.WriteAllText(file, "owned");
        bool checkedFile = false;
        SafeDirectoryCleanup.DeleteTree(tree, pinned =>
        {
            Assert.Throws<IOException>(() => Directory.Move(tree, tree + "-moved"));
            if (pinned == file)
            {
                checkedFile = true;
                Assert.Throws<IOException>(() => File.Move(file, file + "-moved"));
                Assert.Throws<IOException>(() => File.WriteAllText(file, "substituted content"));
            }
        });
        Assert.True(checkedFile);
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void JunctionInsertedAfterRootValidationIsStillRejected()
    {
        string tree = Path.Combine(_root, "tree"), outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Assert.Throws<IOException>(() => SafeDirectoryCleanup.DeleteTree(tree, pinned =>
        {
            if (pinned == tree) Junction(Path.Combine(tree, "late"), outside);
        }));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void LockedFileIsPreservedWithoutAnUnsafeFallback()
    {
        string file = Path.Combine(_root, "locked.txt");
        File.WriteAllText(file, "keep");
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => SafeDirectoryCleanup.DeleteFile(file));
        Assert.Equal(4, held.Length);
    }

    [Fact]
    public void RefusesRelativeAndVolumeRoots()
    {
        Assert.Throws<IOException>(() => SafeDirectoryCleanup.DeleteTree("relative"));
        Assert.Throws<IOException>(() => SafeDirectoryCleanup.DeleteTree(Path.GetPathRoot(_root)!));
    }

    private void Junction(string link, string target)
    {
        // Fixed fixture paths only; junction creation requires neither elevation nor Developer Mode.
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"";
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        _links.Add(link);
    }

    public void Dispose()
    {
        // Unlink fixtures explicitly before recursively cleaning the test-owned directory.
        foreach (string link in _links) if (Directory.Exists(link)) Directory.Delete(link);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
