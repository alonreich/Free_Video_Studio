using System.Globalization;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

[Collection(AvCollection.Name)]
public sealed class S37_SeekEvidenceTests
{
    [Theory, Trait("Tier", "Quick")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S37_SeekAcknowledgementRequiresFreshNativePosition(bool paused)
    {
        var clip = await TestMedia.GameplayAsync();
        await using var player = PlayerRig.Create();
        await player.LoadAsync(clip.Path, 1, paused);
        int completions = 0;
        player.Ipc.SeekCompleted += () => Interlocked.Increment(ref completions);
        foreach (double target in new[] { 12.5, 2.25, 16.75, 4.5 })
        {
            int before = Volatile.Read(ref completions);
            double elapsed = await player.SeekAsync(target);
            Av.Latency("fresh decoded seek", elapsed);
            Assert.True(Volatile.Read(ref completions) > before, "Seek returned without a fresh completion.");
            string? native = player.Ipc.GetPropertyString("time-pos");
            Assert.True(double.TryParse(native, NumberStyles.Float, CultureInfo.InvariantCulture, out double position));
            Assert.InRange(position, target - Budget.FrameSec * 2, target + elapsed / 1000 + Budget.FrameSec * 2);
            Assert.False(player.Ipc.IsSeeking);
            Assert.Equal(paused, player.Ipc.IsPaused);
        }
    }
}
