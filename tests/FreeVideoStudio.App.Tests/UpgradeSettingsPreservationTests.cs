using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SYS-UPGRADE — unreadable or newer settings stop migration without touching them. (This file was
/// CompactUpdateTests; its archive tests went with the retired app-only package, COMPACTGONE_01.)
/// </summary>
public sealed class UpgradeSettingsPreservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fvs-patch-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":99999}")]
    public void DamagedOrNewerSettingsStopMigrationWithoutChangingTheirBytes(string contents)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "settings.json"); File.WriteAllText(path, contents);
        Assert.Throws<IOException>(() => UpgradeCoordinator.ValidateSettingsFile(path));
        Assert.Equal(contents, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
