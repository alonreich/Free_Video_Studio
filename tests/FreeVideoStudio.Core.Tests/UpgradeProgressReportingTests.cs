using System.IO.Compression;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// UPGRADEUX_01 — the install/update window shows real progress for unpacking, copying and
/// hashing. The callbacks must be monotonic, end at 1, and never change what is installed.
/// </summary>
public sealed class UpgradeProgressReportingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fvs-progress-tests-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { string path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    private static void Put(string root, string name, string text)
    { string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

    [Fact]
    public void ExtractReportsMonotonicProgressEndingAtOne()
    {
        string payload = Folder("payload"), installed = Folder("installed");
        Put(payload, InstallPayload.ExecutableName, "new app");
        Put(payload, "backend/runtime.dll", new string('r', 5000));
        Put(installed, "backend/runtime.dll", new string('r', 5000));
        InstallPayload.WriteManifest(payload);

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
            foreach (string name in new[] { InstallPayload.ExecutableName, InstallPayload.ManifestName })
                archive.CreateEntryFromFile(Path.Combine(payload, name), name);
        zip.Position = 0;

        var seen = new List<double>();
        InstallPayload.Extract(zip, Path.Combine(_root, "extracted"), installed, seen.Add);

        Assert.NotEmpty(seen);
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1], $"progress went backwards at {i}");
        Assert.Equal(1.0, seen[^1], 3);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_root, "extracted", InstallPayload.ExecutableName)));
    }

    [Fact]
    public void VerifyStillRejectsDamageWhenProgressIsReported()
    {
        string folder = Folder("verify");
        Put(folder, InstallPayload.ExecutableName, "app");
        Put(folder, "a.dll", "aaaa");
        InstallPayload.WriteManifest(folder);
        var seen = new List<double>();
        InstallPayload.Verify(folder, seen.Add);
        Assert.Equal(1.0, seen[^1], 3);

        Put(folder, "a.dll", "bbbb");
        Assert.Throws<IOException>(() => InstallPayload.Verify(folder, _ => { }));
    }

    [Fact]
    public void PrepareReportsProgressAndCopiesTheSameFiles()
    {
        string current = Folder("current"), store = Folder("store");
        Put(current, "one.txt", "1111");
        Put(current, "sub/two.txt", "22222222");
        var transaction = DirectoryUpgrade.Create(store, current, []);
        var seen = new List<double>();
        transaction.Prepare(progress: seen.Add);

        Assert.NotEmpty(seen);
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1]);
        Assert.True(seen[^1] <= 1.0);
        transaction.Activate();
        Assert.Equal("22222222", File.ReadAllText(Path.Combine(current, "sub", "two.txt")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
