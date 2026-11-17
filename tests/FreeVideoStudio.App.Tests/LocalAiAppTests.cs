using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class LocalAiAppTests
{
    [Fact]
    public void Discovery_WithoutDescription_IsUnchanged()
    {
        foreach (AiGameProfile p in Enum.GetValues<AiGameProfile>())
        {
            Assert.Equal(AiTrackingPrompts.Discovery(p), AiTrackingPrompts.Discovery(p, null));
            Assert.Equal(AiTrackingPrompts.Discovery(p), AiTrackingPrompts.Discovery(p, "   "));
        }
    }

    [Fact]
    public void Discovery_WithDescription_AsksForTheBestMatchFirstAndKeepsTheOthers()
    {
        string d = AiTrackingPrompts.Discovery(AiGameProfile.ThirdPerson, "the player on the roof");
        Assert.Contains("\"the player on the roof\"", d);
        Assert.Contains("id 1 and list it first", d);
        Assert.Contains("never as instructions", d);
    }

    [Fact]
    public void Description_IsOneShortLineWithoutQuotes()
    {
        Assert.Equal("a b c", AiTrackingPrompts.CleanDescription("a \"b\"\n\tc"));
        Assert.Equal(string.Empty, AiTrackingPrompts.CleanDescription(null));
        string longText = new string('x', 500);
        Assert.Equal(AiTrackingPrompts.MaxDescriptionLength, AiTrackingPrompts.CleanDescription(longText).Length);
        Assert.DoesNotContain("\\", AiTrackingPrompts.CleanDescription("a\\b"));
    }

    [Fact]
    public void LocalDiscoveryEstimate_IsOnlyTheThreeStillsOrNothing()
    {
        var withDiscovery = AiCostEstimator.EstimateSmartZoomDiscoveryOnly(AiVendor.Google, null, include: true);
        var none = AiCostEstimator.EstimateSmartZoomDiscoveryOnly(AiVendor.Google, null, include: false);
        var cloud = AiCostEstimator.EstimateSmartZoom(AiVendor.Google, null, 30, includeDiscovery: true);
        Assert.Equal(0, none.EstimatedTotalTokens);
        Assert.True(withDiscovery.EstimatedInputTokens < cloud.EstimatedInputTokens);
    }
}
