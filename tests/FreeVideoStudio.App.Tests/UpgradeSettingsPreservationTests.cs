using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

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
        Assert.Throws<FreeVideoStudio.App.Infrastructure.UserFacingIOException>(() => UpgradeCoordinator.ValidateSettingsFile(path));
        Assert.Equal(contents, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
