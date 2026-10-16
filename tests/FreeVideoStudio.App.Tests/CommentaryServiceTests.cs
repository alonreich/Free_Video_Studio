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
    static CommentaryServiceTests()
    {
        // AIZEROFRAMES_01 — the fake video below decodes to no pictures; give the stills path two fake ones so the
        // mock provider is exercised (an empty sample is now refused before any AI call, by design).
        CommentaryService.FrameSamplerForTests = (_, start, dur, _) => Task.FromResult<IReadOnlyList<AiImageInput>>(new[]
        {
            new AiImageInput(new byte[] { 1 }, "image/jpeg", "[Timestamp: 0.00s]"),
            new AiImageInput(new byte[] { 2 }, "image/jpeg", "[Timestamp: 0.50s]"),
        });
    }

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

/// <summary>AICOMMENTARY_SPEED_01 / MERGE_01 / REPORT_01 — absolute times, speed-aware readability, segment merge, part retry.</summary>
public sealed class CommentaryTimingTests
{
    static CommentaryTimingTests()
    {
        // AIZEROFRAMES_01 — the fake video below decodes to no pictures; give the stills path two fake ones so the
        // mock provider is exercised (an empty sample is now refused before any AI call, by design).
        CommentaryService.FrameSamplerForTests = (_, start, dur, _) => Task.FromResult<IReadOnlyList<AiImageInput>>(new[]
        {
            new AiImageInput(new byte[] { 1 }, "image/jpeg", "[Timestamp: 0.00s]"),
            new AiImageInput(new byte[] { 2 }, "image/jpeg", "[Timestamp: 0.50s]"),
        });
    }

    [Fact]
    public void Parse_KeepsLinesInsideThePart_AndDropsLinesPastIt()
    {
        string json = """[{"start":1,"end":30,"text":"way too long"},{"start":9,"end":10,"text":"past the end"}]""";
        int id = 1;
        Assert.True(CommentaryService.TryParseCommentaryResponse(json, 20.0, 28.0, ref id, out var lines));
        Assert.Single(lines);
        Assert.Equal(21.0, lines[0].SourceStartSec, 3);
        Assert.Equal(28.0, lines[0].SourceEndSec, 3);
    }

    [Fact]
    public void TryParse_FalseForUnreadable_TrueForEmptyArray()
    {
        int id = 1;
        Assert.False(CommentaryService.TryParseCommentaryResponse("{oops", 0, 8, ref id, out _));
        Assert.True(CommentaryService.TryParseCommentaryResponse("[]", 0, 8, ref id, out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void ReadableOnScreen_LengthensLinesInFastForward()
    {
        // Trim-relative timeline: 0..10 s at 1x, 10..20 s at 4x. Captions are ABSOLUTE; trim starts at 100 s.
        var tl = OutputTimeline.Create(20000, new[] { new SpeedSegment(10000, 20000, 4.0) });
        var line = new CaptionLine(1, 112.0, 113.2, "quick one");
        var fixedLines = CommentaryService.EnsureReadableOnScreen(new[] { line }, tl, 100.0, 100.0, 120.0);
        double outLen = tl.SourceToOutput(fixedLines[0].SourceEndSec - 100) - tl.SourceToOutput(fixedLines[0].SourceStartSec - 100);
        Assert.True(outLen >= CommentaryService.MinReadableOutputSec - 0.01, $"on screen {outLen:0.00}s");
    }

    [Fact]
    public void ReadableOnScreen_NeverRunsIntoNextLineOrPastRange()
    {
        var tl = OutputTimeline.Create(20000, new[] { new SpeedSegment(0, 20000, 4.0) });
        var a = new CaptionLine(1, 1.0, 1.4, "first");
        var b = new CaptionLine(2, 2.0, 3.0, "second");
        var r = CommentaryService.EnsureReadableOnScreen(new[] { a, b }, tl, 0, 0, 2.5);
        Assert.True(r[0].SourceEndSec <= 2.0 + 1e-6);
        Assert.True(r[1].SourceEndSec <= 2.5 + 1e-6);
    }

    [Fact]
    public void DescribePlayback_ReportsSlowFastAndCut()
    {
        var tl = OutputTimeline.Create(20000, new[] { new SpeedSegment(2000, 4000, 0.5), new SpeedSegment(4000, 6000, 2.0) });
        string? notes = CommentaryService.DescribePlayback(tl, 50.0, 50.0, 58.0);
        Assert.NotNull(notes);
        Assert.Contains("0.5x", notes);
        Assert.Contains("2x", notes);
        Assert.Contains("2.0s to 4.0s", notes);
        Assert.Null(CommentaryService.DescribePlayback(OutputTimeline.Create(20000, null), 0, 0, 8));
    }

    [Fact]
    public void MergeSegment_ReplacesOnlyInsideTheRange()
    {
        var existing = new CaptionTrack(Lines: new[]
        {
            new CaptionLine(1, 1, 3, "before"),
            new CaptionLine(2, 11, 13, "inside"),
            new CaptionLine(3, 9, 12, "crosses the edge"),
            new CaptionLine(4, 25, 27, "after"),
        });
        var generated = new CaptionTrack(CaptionStyle.PopIn, Lines: new[] { new CaptionLine(1, 10.5, 12, "new one") });
        var merged = CommentaryService.MergeSegment(existing, generated, 10, 20);
        Assert.Equal(new[] { "before", "crosses the edge", "new one", "after" }, merged.Lines!.Select(l => l.Text));
        Assert.Equal(10.0, merged.Lines![1].SourceEndSec, 3);
        Assert.Equal(new[] { 1, 2, 3, 4 }, merged.Lines!.Select(l => l.Id));
        Assert.Equal(CaptionStyle.PopIn, merged.Style);
    }

    [Fact]
    public async Task Generate_AddsSegmentStart_AndRetriesUnreadablePartOnce()
    {
        var provider = new SequenceProvider("not json", """[{"start":0.5,"end":2.5,"text":"Got him"}]""");
        string video = Path.Combine(Path.GetTempPath(), $"fvs_c_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(video, new byte[] { 0 });
        try
        {
            var req = new CommentaryRequest(video, 42.0, 5.0, AiVendor.Anthropic, "m", Provider: provider);
            var result = await CommentaryService.GenerateCommentaryWithReportAsync(req, "k"); // SECRETSCAN_ALLOW
            Assert.Equal(0, result.FailedChunks);
            Assert.Equal(2, provider.Calls);
            Assert.Equal(42.5, result.Track.Lines![0].SourceStartSec, 3);
        }
        finally { File.Delete(video); }
    }

    [Fact]
    public async Task Generate_CountsPartsThatStillFail()
    {
        var provider = new SequenceProvider("bad", "bad", """[{"start":0.5,"end":2,"text":"ok"}]""");
        string video = Path.Combine(Path.GetTempPath(), $"fvs_c_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(video, new byte[] { 0 });
        try
        {
            var req = new CommentaryRequest(video, 0, 15.0, AiVendor.Anthropic, "m", Provider: provider);
            var result = await CommentaryService.GenerateCommentaryWithReportAsync(req, "k"); // SECRETSCAN_ALLOW
            Assert.Equal(2, result.TotalChunks);
            Assert.Equal(1, result.FailedChunks);
            Assert.Single(result.Track.Lines!);
        }
        finally { File.Delete(video); }
    }

    private sealed class SequenceProvider : IAiVisionProvider
    {
        private readonly Queue<string> _answers;
        public int Calls;
        public SequenceProvider(params string[] answers) => _answers = new Queue<string>(answers);
        public AiVendor Vendor => AiVendor.Anthropic;
        public string DefaultModel => "m";
        public Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string> GenerateJsonAsync(string apiKey, string model, string prompt, IReadOnlyList<AiImageInput> images,
            AiVideoInput? video, string jsonSchema, AiReasoningConfig? reasoning = null, GeminiCallOptions? options = null,
            Action<string>? userLog = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : "[]");
        }
    }
}
