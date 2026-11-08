using System.IO;

namespace FreeVideoStudio.Testing;

// TESTCLEAN_02: shared by standalone native/desktop checks, never production code.
internal static class TestArtifactDirectory
{
    internal const string ManagedEnvironment = "FVS_TEST_OUTPUT_MANAGED";
    private static readonly string[] Owned =
    ["tests/MediaPipelineChecks/artifacts/current", "tests/MediaPipelineChecks/artifacts/soak", "tests/AvRegression/desktop"];

    internal static IDisposable Begin(string repository, string relative, out string output)
    {
        FileStream? lease = null;
        if (Environment.GetEnvironmentVariable(ManagedEnvironment) != "1")
            lease = File.Open(Path.Combine(repository, "tests", ".test-run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { output = Reset(repository, relative); return lease ?? (IDisposable)new NoLease(); }
        catch { lease?.Dispose(); throw; }
    }

    internal static string Reset(string repository, string relative)
    {
        string root = Path.GetFullPath(repository);
        if (!File.Exists(Path.Combine(root, "FreeVideoStudio.sln")) || !Owned.Contains(relative, StringComparer.Ordinal))
            throw new InvalidOperationException("Not a recognized test-output directory.");
        string output = Path.GetFullPath(Path.Combine(root, relative));
        if (!output.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test output escaped the repository.");
        for (var ancestor = new DirectoryInfo(output); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked test-output ancestor refused.");
        // Preflight without traversing links; a link is a refusal, never permission to follow it.
        if (Directory.Exists(output))
        {
            ValidateTree(new DirectoryInfo(output));
            Directory.Delete(output, recursive: true);
        }
        return Directory.CreateDirectory(output).FullName;
    }

    private static void ValidateTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.GetFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked test-output entry refused.");
            if (entry is DirectoryInfo child) ValidateTree(child);
        }
    }

    private sealed class NoLease : IDisposable { public void Dispose() { } }
}
