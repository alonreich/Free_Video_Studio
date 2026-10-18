using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>CRASHLOOP_01 — repeated failed starts of the same version lead to the way back (05 SYS-UPGRADE).</summary>
public sealed class StartupCrashGuardTests
{
    [Fact]
    public void CountsStartsPerVersion_AndOffersHelpOnlyAfterRepeatedFailures()
    {
        (string record, int attempts, bool offer) = StartupCrashGuard.NextAttempt("", "2026.10.18.1200");
        Assert.Equal(("2026.10.18.1200|1", 1, false), (record, attempts, offer));

        string stored = record;
        for (int i = 2; i <= StartupCrashGuard.OfferAfter; i++)
        {
            (stored, attempts, offer) = StartupCrashGuard.NextAttempt(stored, "2026.10.18.1200");
            Assert.False(offer);
        }
        (_, attempts, offer) = StartupCrashGuard.NextAttempt(stored, "2026.10.18.1200");
        Assert.Equal(StartupCrashGuard.OfferAfter + 1, attempts);
        Assert.True(offer);
    }

    [Fact]
    public void ANewVersionOrAGoodStartResetsTheCount()
    {
        Assert.Equal(1, StartupCrashGuard.NextAttempt("2026.10.01.0000|9", "2026.10.18.1200").Attempts);
        Assert.Equal(1, StartupCrashGuard.NextAttempt("2026.10.18.1200|0", "2026.10.18.1200").Attempts);
        Assert.Equal(1, StartupCrashGuard.NextAttempt("garbage", "2026.10.18.1200").Attempts);
        Assert.Equal(1, StartupCrashGuard.NextAttempt("2026.10.18.1200|-5", "2026.10.18.1200").Attempts);
    }
}
