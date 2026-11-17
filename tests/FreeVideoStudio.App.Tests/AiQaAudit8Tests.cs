using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiQaAudit8Tests
{
    private const string FakeClaudeKey = "sk-" + "ant-dummy-qa8-claude-key-00000000";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public int Calls;
        public string? LastRequestBody;
        public RecordingHandler(string body) => _body = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static bool ReadsAsRefusal(string body)
        => AiAnswerGuard.IsRefusal(AiVendor.OpenAI, body)
           || (!OpenAiProvider.TryExtractContent(body, out _, out _, out bool refused) && refused);

    [Fact]
    public void PlanStream_RefusalEvents_AreReportedAsARefusal_NotAsAnUnreadableAnswer()
    {
        var result = ChatGptPlanTransport.ReadEvents(new[]
        {
            """{"type":"response.refusal.delta","delta":"I can't help with that."}""",
            """{"type":"response.refusal.done","refusal":"I can't help with that."}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":900,"output_tokens":8}}}""",
        });
        string body = ChatGptPlanTransport.ToChatCompletionBody(result);
        Assert.True(ReadsAsRefusal(body), "A ChatGPT-plan refusal must be a Blocked refusal (AIREFUSAL_01), not an empty answer that is re-asked and then called unreadable.");
    }

    [Fact]
    public void PlanStream_ContentFilterStop_IsARefusal_NotACutOffAnswer()
    {
        var result = ChatGptPlanTransport.ReadEvents(new[]
        {
            """{"type":"response.incomplete","response":{"incomplete_details":{"reason":"content_filter"},"usage":{"input_tokens":900,"output_tokens":0}}}""",
        });
        string body = ChatGptPlanTransport.ToChatCompletionBody(result);
        Assert.False(AiAnswerGuard.IsTruncated(AiVendor.OpenAI, body), "A content-filter stop must not tell the user to try a shorter block.");
        Assert.True(ReadsAsRefusal(body));
    }

    [Fact]
    public void PlanStream_LengthStop_StillReadsAsCutOff()
    {
        var result = ChatGptPlanTransport.ReadEvents(new[]
        {
            """{"type":"response.incomplete","response":{"incomplete_details":{"reason":"max_output_tokens"}}}""",
        });
        Assert.True(AiAnswerGuard.IsTruncated(AiVendor.OpenAI, ChatGptPlanTransport.ToChatCompletionBody(result)));
    }

    [Theory]
    [InlineData("server_error", AiProviderErrorKind.Overloaded)]
    [InlineData("rate_limit_exceeded", AiProviderErrorKind.RateLimited)]
    public void PlanStream_InStreamServerOrRateFailure_IsRetriable_EvenThoughTheHttpStatusWasOk(string code, AiProviderErrorKind expected)
    {
        var result = ChatGptPlanTransport.ReadEvents(new[]
        {
            "{\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"" + code + "\",\"message\":\"temporary problem\"}}}",
        });
        Assert.Equal(code, result.ErrorCode);
        AiProviderException ex = ChatGptPlanTransport.Classify(400, result.ErrorCode, result.ErrorMessage, result.ErrorParam, null);
        Assert.Equal(expected, ex.Kind);
        Assert.True(ex.IsTransient);
    }

    [Theory]
    [InlineData("""{"captions":[{"start":0.5,"end":2.5,"text":"Pushing left!"}]}""")]
    [InlineData("\"[{\\\"start\\\":0.5,\\\"end\\\":2.5,\\\"text\\\":\\\"Pushing left!\\\"}]\"")]
    [InlineData("42")]
    [InlineData("""{"note":"nothing happened"}""")]
    public void Commentary_UnrecognisedAnswerShape_IsUnusable_NotAnEmptySuccess(string json)
    {
        int id = 1;
        Assert.False(CommentaryService.TryParseCommentaryResponse(json, 0, 8, ref id, out var lines),
            "An answer that is neither a list of lines nor a single line must go down the unusable-answer path (retry, then Retry part / Keep / Stop), not count as a part with no lines.");
        Assert.Empty(lines);
    }

    [Fact]
    public void Commentary_EmptyListAndLinesWrapper_StayValid()
    {
        int id = 1;
        Assert.True(CommentaryService.TryParseCommentaryResponse("[]", 0, 8, ref id, out var none));
        Assert.Empty(none);
        Assert.True(CommentaryService.TryParseCommentaryResponse("""{"lines":[{"start":1,"end":3,"text":"Reloading."}]}""", 0, 8, ref id, out var one));
        Assert.Single(one);
    }

    [Fact]
    public void PlanStream_SkipsGarbageLines_AndKeepsTheFinishedAnswer()
    {
        var result = ChatGptPlanTransport.ReadEvents(new[]
        {
            ": keep-alive",
            "{not json",
            """{"type":"response.output_text.delta","delta":"{\"ok\":"}""",
            "",
            """{"type":"response.output_text.delta","delta":"true}"}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":5,"output_tokens":3}}}""",
        });
        Assert.True(result.Finished);
        Assert.Equal("{\"ok\":true}", result.Text);
        Assert.Equal(string.Empty, result.ErrorCode);
    }

    [Fact]
    public void PlanStream_WithoutAFinishEvent_IsNotFinished()
    {
        var result = ChatGptPlanTransport.ReadEvents(new[]
        {
            """{"type":"response.output_text.delta","delta":"{\"ok\":tr"}""",
        });
        Assert.False(result.Finished);
    }

    [Fact]
    public async Task Claude_Commentary_SendsAnObjectToolSchema_AndTheWrappedAnswerBecomesCaptionLines()
    {
        string answer = """
        {"id":"msg_1","type":"message","role":"assistant","stop_reason":"tool_use",
         "content":[{"type":"tool_use","id":"tu_1","name":"submit_result","input":{"items":[{"start":0.5,"end":2.5,"text":"Pushing left!","emphasis":["pushing"],"confidence":0.9}]}}],
         "usage":{"input_tokens":1200,"output_tokens":40}}
        """;
        var handler = new RecordingHandler(answer);
        var provider = new ClaudeProvider(new HttpClient(handler), _ => { }, (_, _) => Task.CompletedTask);
        var frame = new AiImageInput(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, "image/jpeg", "[t=0.0s]", "low");

        string json = await provider.GenerateJsonAsync(FakeClaudeKey, "claude-sonnet-5-5", "Narrate.", new[] { frame }, null,
            AiCommentaryPrompts.CommentaryJsonSchema, null, GeminiCallOptions.Creative, null, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        using (var sent = JsonDocument.Parse(handler.LastRequestBody!))
        {
            var tool = sent.RootElement.GetProperty("tools")[0];
            Assert.Equal("submit_result", tool.GetProperty("name").GetString());
            Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
            Assert.DoesNotContain(FakeClaudeKey, handler.LastRequestBody!, StringComparison.Ordinal);
        }
        int id = 1;
        Assert.True(CommentaryService.TryParseCommentaryResponse(json, 10, 18, ref id, out var lines));
        var line = Assert.Single(lines);
        Assert.Equal("Pushing left!", line.Text);
        Assert.Equal(10.5, line.SourceStartSec, 3);
    }

    [Fact]
    public void LocalAiPack_FailedSwap_PutsThePreviousPackBack()
    {
        string root = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "LocalAiSwap", Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "v1");
        string old = folder + ".old-1";
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "sam2_small_encoder.onnx"), "previous");
        LocalAiPackService.RestorePreviousPack(folder, old, hadOld: true, new IOException("locked by another program"));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(folder, "sam2_small_encoder.onnx")));
        Assert.False(Directory.Exists(old));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void LocalAiPack_FailedFirstInstall_LeavesNothingBehind()
    {
        string root = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "LocalAiSwap", Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "v1");
        LocalAiPackService.RestorePreviousPack(folder, folder + ".old-1", hadOld: false, new IOException("locked"));
        Assert.False(Directory.Exists(folder));
    }
}
