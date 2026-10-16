using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// AIFRAMESEQ_01 — unit tests for AiFrameSequenceExtractor.
/// </summary>
public sealed class AiFrameSequenceExtractorTests
{
    [Fact]
    public void Partition_EmptyReturnsEmpty()
    {
        var result = AiFrameSequenceExtractor.Partition(Array.Empty<AiImageInput>());
        Assert.Empty(result);
    }

    [Fact]
    public void Partition_UnderLimitReturnsSingleChunk()
    {
        var list = new List<AiImageInput>
        {
            new(new byte[] { 1 }, "image/jpeg", "t1"),
            new(new byte[] { 2 }, "image/jpeg", "t2"),
        };
        var result = AiFrameSequenceExtractor.Partition(list, 10);
        Assert.Single(result);
        Assert.Equal(2, result[0].Count);
    }

    [Fact]
    public void Partition_OverLimitSplitsEvenly()
    {
        var list = new List<AiImageInput>();
        for (int i = 0; i < 25; i++)
        {
            list.Add(new AiImageInput(new byte[] { (byte)i }, "image/jpeg", $"t{i}"));
        }

        var chunks = AiFrameSequenceExtractor.Partition(list, 10);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(10, chunks[0].Count);
        Assert.Equal(10, chunks[1].Count);
        Assert.Equal(5, chunks[2].Count);
    }

    [Theory]
    [InlineData(AiVendor.Google, 100)]
    [InlineData(AiVendor.Anthropic, 60)]
    [InlineData(AiVendor.OpenAI, 50)]
    public void GetMaxImagesPerRequest_MatchesVendorRules(AiVendor vendor, int expected)
    {
        Assert.Equal(expected, AiFrameSequenceExtractor.GetMaxImagesPerRequest(vendor));
    }

    [Fact]
    public async Task SampleFramesAsync_ThrowsFileNotFound_ForMissingVideo()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            AiFrameSequenceExtractor.SampleFramesAsync("C:\\nonexistent\\fake_video.mp4", 0, 5));
    }

    [Fact]
    public async Task SampleFramesAsync_ReturnsEmpty_ForZeroDuration()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            var frames = await AiFrameSequenceExtractor.SampleFramesAsync(tempFile, 0, 0);
            Assert.Empty(frames);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
