using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace FreeVideoStudio.Testing;

internal static class TestArtifactDirectory
{
    internal const string ManagedEnvironment = "FVS_TEST_OUTPUT_MANAGED";
    internal const string LogsFolder = "logs";
    private static readonly Regex SuiteName = new("^[A-Za-z]+$", RegexOptions.CultureInvariant);
    private static readonly Regex OwnedRelative =
        new(@"^logs/[A-Za-z]+_Results(/work)?$", RegexOptions.CultureInvariant);

    internal static bool Managed => Environment.GetEnvironmentVariable(ManagedEnvironment) == "1";

    internal static string SuiteRelative(string suite)
    {
        if (!SuiteName.IsMatch(suite)) throw new InvalidOperationException("Not a recognized test suite name.");
        return LogsFolder + "/" + suite + "_Results";
    }

    internal static IDisposable Begin(string repository, string suite, out string work)
    {
        FileStream? lease = null;
        if (!Managed)
            lease = File.Open(Path.Combine(repository, "tests", ".test-run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { work = PrepareSuite(repository, suite); return lease ?? (IDisposable)new NoLease(); }
        catch { lease?.Dispose(); throw; }
    }

    internal static string SuiteWork(string suite) =>
        LazyInitializer.EnsureInitialized(ref suiteWork, ref suiteWorkLock, () =>
        {
            string marker = "FVS_TEST_SUITE_READY_" + suite;
            string repository = FindRepository();
            if (Environment.GetEnvironmentVariable(marker) == "1")
                return Directory.CreateDirectory(Path.Combine(repository, LogsFolder, suite + "_Results", "work")).FullName;
            string work = PrepareSuite(repository, suite);
            Environment.SetEnvironmentVariable(marker, "1");
            return work;
        })!;

    private static string? suiteWork;
    private static object? suiteWorkLock;

    internal static string PrepareSuite(string repository, string suite)
    {
        string relative = SuiteRelative(suite);
        if (Managed)
        {
            Directory.CreateDirectory(Path.Combine(repository, LogsFolder, suite + "_Results"));
            return Reset(repository, relative + "/work");
        }
        Reset(repository, relative);
        return Directory.CreateDirectory(Path.Combine(repository, LogsFolder, suite + "_Results", "work")).FullName;
    }

    internal static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FreeVideoStudio.sln"))) return dir.FullName;
        throw new InvalidOperationException("FreeVideoStudio.sln not found above " + AppContext.BaseDirectory);
    }

    internal static string Reset(string repository, string relative)
    {
        string root = Path.GetFullPath(repository);
        if (!File.Exists(Path.Combine(root, "FreeVideoStudio.sln")) || !OwnedRelative.IsMatch(relative))
            throw new InvalidOperationException("Not a recognized test-output directory.");
        string output = Path.GetFullPath(Path.Combine(root, relative));
        if (!output.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test output escaped the repository.");
        for (var ancestor = new DirectoryInfo(output); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked test-output ancestor refused.");
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
