using System.Diagnostics;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class FinalOutputCommitTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fvs-output-commit-" + Guid.NewGuid().ToString("N"))).FullName;
    private string Source => Path.Combine(root, "finished.mp4");
    private string Destination => Path.Combine(root, "output.mp4");

    [Fact]
    public async Task ReleasedDestinationLockSavesOriginalBytesWithoutReencoding()
    {
        await File.WriteAllBytesAsync(Source, [1, 2, 3, 4]);
        using (var held = new FileStream(Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var save = FinalOutputCommit.MoveAsync(Source, Destination, overwrite: true);
            await Task.Delay(250);
            Assert.False(save.IsCompleted);
            held.Dispose();
            await save;
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Destination));
        Assert.False(File.Exists(Source));
    }

    [Fact]
    public async Task PersistentLockFailsBoundedlyAndRetainsFinishedSource()
    {
        await File.WriteAllBytesAsync(Source, [1, 2, 3]);
        using var held = new FileStream(Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var clock = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(() => FinalOutputCommit.MoveAsync(Source, Destination, true));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.4, 15);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Source));
        Assert.Equal(0, held.Length);
    }

    [Fact]
    public async Task ExistingDestinationIsNeverOverwrittenWithoutReservation()
    {
        await File.WriteAllTextAsync(Source, "new");
        await File.WriteAllTextAsync(Destination, "existing user export");
        await Assert.ThrowsAnyAsync<IOException>(() => FinalOutputCommit.MoveAsync(Source, Destination, false));
        Assert.Equal("existing user export", await File.ReadAllTextAsync(Destination));
        Assert.Equal("new", await File.ReadAllTextAsync(Source));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
