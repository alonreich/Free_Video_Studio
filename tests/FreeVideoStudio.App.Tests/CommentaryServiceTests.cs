using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class CommentaryServiceTests
{
    [Fact]
    public void PlanChunks_ShortDuration_YieldsSingleChunk()
    {
        var chunks = CommentaryService.PlanChunks(6.5);
        Assert.Single(chunks);
        Assert.Equal(0.0, chunks[0].Start);
        Assert.Equal(6.5, chunks[0].End);
    }

    [Fact]
    public void PlanChunks_LongDuration_YieldsOverlappingChunks()
    {
        // 20s with 8s chunks and 1s overlap:
        // Chunk 0: 0.0 - 8.0
        // Chunk 1: 7.0 - 15.0
        // Chunk 2: 14.0 - 20.0
        var chunks = CommentaryService.PlanChunks(20.0);
        Assert.True(chunks.Count >= 3);
        Assert.Equal(0.0, chunks[0].Start);
        Assert.Equal(8.0, chunks[0].End);
        Assert.Equal(7.0, chunks[1].Start);
        Assert.Equal(15.0, chunks[1].End);
        Assert.Equal(20.0, chunks[^1].End);
    }

    [Fact]
    public void ParseCommentaryResponse_ParsesValidJsonWithTimeOffsets()
    {
        string json = """
[
  { "start": 0.5, "end": 2.5, "text": "I am repositioning to heal", "emphasis": ["repositioning"], "confidence": 0.95 },
  { "start": 3.0, "end": 5.2, "text": "Got him right there", "emphasis": ["Got"], "confidence": 0.88 }
]
""";

        int idCounter = 1;
        double chunkOffset = 10.0;
        var lines = CommentaryService.ParseCommentaryResponse(json, chunkOffset, 18.0, ref idCounter);

        Assert.Equal(2, lines.Count);
        Assert.Equal(1, lines[0].Id);
        Assert.Equal(10.5, lines[0].SourceStartSec, precision: 2);
        Assert.Equal(12.5, lines[0].SourceEndSec, precision: 2);
        Assert.Equal("I am repositioning to heal", lines[0].Text);
        Assert.Contains("repositioning", lines[0].Emphasis!);

        Assert.Equal(2, lines[1].Id);
        Assert.Equal(13.0, lines[1].SourceStartSec, precision: 2);
        Assert.Equal(15.2, lines[1].SourceEndSec, precision: 2);
    }

    [Fact]
    public void ParseCommentaryResponse_MalformedJson_ReturnsEmptyWithoutThrowing()
    {
        int idCounter = 1;
        var lines = CommentaryService.ParseCommentaryResponse("not valid json {", 0, 8.0, ref idCounter);
        Assert.Empty(lines);
    }

    [Fact]
    public void DeduplicateLines_MergesOverlappingChunkSeams()
    {
        var line1 = new CaptionLine(1, 1.0, 3.5, "Pushing forward now", new[] { "Pushing" }, 0.9);
        var line2 = new CaptionLine(2, 6.5, 7.8, "He is low on health", new[] { "low" }, 0.85);
        // Overlapping duplicate from next chunk:
        var line2Dup = new CaptionLine(3, 6.8, 8.2, "He is low on health", new[] { "health" }, 0.95);
        var line3 = new CaptionLine(4, 10.0, 13.0, "Victory achieved", new[] { "Victory" }, 0.99);

        var deduplicated = CommentaryService.DeduplicateLines(new[] { line1, line2, line2Dup, line3 });

        Assert.Equal(3, deduplicated.Count);
        Assert.Equal("Pushing forward now", deduplicated[0].Text);
        Assert.Equal("He is low on health", deduplicated[1].Text);
        // Combined span: 6.5 to 8.2
        Assert.Equal(6.5, deduplicated[1].SourceStartSec, precision: 2);
        Assert.Equal(8.2, deduplicated[1].SourceEndSec, precision: 2);
        Assert.Equal("Victory achieved", deduplicated[2].Text);
    }

    [Fact]
    public async Task GenerateCommentaryAsync_WithMockProvider_ReturnsPopulatedCaptionTrack()
    {
        string fakeJson = """
[
  { "start": 0.5, "end": 2.5, "text": "He chased me so I repositioned", "emphasis": ["chased", "repositioned"], "confidence": 0.96 }
]
""";
        var mockProvider = new MockAiVisionProvider(fakeJson);

        // Create temporary dummy video file to satisfy File.Exists check
        string tempVideo = Path.Combine(Path.GetTempPath(), $"test_video_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempVideo, new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 });

        try
        {
            var req = new CommentaryRequest(
                tempVideo,
                StartSec: 0.0,
                DurationSec: 5.0,
                Vendor: AiVendor.Anthropic,
                Model: "claude-sonnet-5-5",
                Tone: CaptionTone.Tactical,
                Profile: AiGameProfile.ThirdPerson,
                Language: "en",
                Provider: mockProvider);

            var track = await CommentaryService.GenerateCommentaryAsync(req, "test_api_key"); // SECRETSCAN_ALLOW

            Assert.NotNull(track);
            Assert.Equal(CaptionTone.Tactical, track.Tone);
            Assert.Single(track.Lines!);

            var line = track.Lines![0];
            Assert.Equal("He chased me so I repositioned", line.Text);
            Assert.NotEmpty(line.Words!);
            Assert.Contains("chased", line.Emphasis!);
        }
        finally
        {
            if (File.Exists(tempVideo)) File.Delete(tempVideo);
        }
    }

    private sealed class MockAiVisionProvider : IAiVisionProvider
    {
        private readonly string _responseJson;

        public MockAiVisionProvider(string responseJson)
        {
            _responseJson = responseJson;
        }

        public AiVendor Vendor => AiVendor.Anthropic;
        public string DefaultModel => "claude-sonnet-5-5";

        public Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "claude-sonnet-5-5" });

        public Task<string> GenerateJsonAsync(
            string apiKey,
            string model,
            string prompt,
            IReadOnlyList<AiImageInput> images,
            AiVideoInput? video,
            string jsonSchema,
            AiReasoningConfig? reasoning = null,
            GeminiCallOptions? options = null,
            Action<string>? consoleLog = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_responseJson);
        }
    }
}
