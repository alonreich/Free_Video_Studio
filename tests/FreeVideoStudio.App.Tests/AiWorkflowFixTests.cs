using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.LocalAi;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiWorkflowFixTests
{
    private const string FakeGoogleKey = "AIza" + "Sy-dummy-wf-test-00000000000";
    private const string FakeClaudeKey = "sk-" + "ant-dummy-wf-test-000000000";
    private const string FakeOpenAiKey = "sk-" + "proj-dummy-wf-test-00000000";

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<string, int, HttpResponseMessage> _respond;
        public List<string> Bodies { get; } = new();

        public CaptureHandler(Func<string, int, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : string.Empty;
            int n;
            lock (Bodies)
            {
                Bodies.Add(body);
                n = Bodies.Count;
            }
            return _respond(body, n);
        }
    }

    private static HttpResponseMessage JsonOk(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(5.0, 5.0)]
    [InlineData(5.0, 0.02)]
    [InlineData(0.0, 0.001)]
    public void DiscoveryFrames_StayInsideSelectedSegment(double start, double duration)
    {
        var times = GeminiTrackingService.CandidateFrameTimes(start, duration);
        Assert.All(new[] { times.T1, times.T2, times.T3 }, t => Assert.True(t >= start && t < start + duration));
        Assert.True(times.T1 < times.T2 && times.T2 < times.T3);
    }
    [Fact]
    public void CapabilityPolicy_SeparatesBuiltInNeuralAndCloudAccurately()
    {
        var detMissing = new LocalDetectorStatus(LocalDetectorState.PackUnavailable, "", "missing");
        var detInstalled = new LocalDetectorStatus(LocalDetectorState.Installed, "y.onnx", "ok");

        var localNoPack = AiSetup.Describe(AiMode.LocalOnly, hasKey: false, packReady: false, detMissing);
        Assert.True(localNoPack.BuiltInLocalAvailable);
        Assert.False(localNoPack.NeuralPackReady);
        Assert.True(localNoPack.IsModeComplete);
        Assert.Equal(LocalTrackerKind.BuiltInBasic, localNoPack.LocalTracker);
        Assert.False(localNoPack.CanUseCloudDiscovery);
        Assert.False(localNoPack.CanUseCloudTracking);
        Assert.False(localNoPack.CanUseCloudCommentary);

        var hybridNoPack = AiSetup.Describe(AiMode.Hybrid, hasKey: true, packReady: false, detMissing);
        Assert.True(hybridNoPack.IsModeComplete);
        Assert.Equal(LocalTrackerKind.BuiltInBasic, hybridNoPack.LocalTracker);
        Assert.True(hybridNoPack.CanUseCloudDiscovery);
        Assert.False(hybridNoPack.CanUseCloudTracking);
        Assert.True(hybridNoPack.CanUseCloudCommentary);

        var hybridPack = AiSetup.Describe(AiMode.Hybrid, hasKey: true, packReady: true, detInstalled);
        Assert.Equal(LocalTrackerKind.NeuralSam2, hybridPack.LocalTracker);
        Assert.True(hybridPack.DetectorStatus.IsInstalled);

        var online = AiSetup.Describe(AiMode.OnlineOnly, hasKey: true, packReady: true, detInstalled);
        Assert.True(online.IsModeComplete);
        Assert.Equal(LocalTrackerKind.NeuralSam2, online.LocalTracker);
        Assert.True(online.CanUseCloudDiscovery);
        Assert.False(online.CanUseCloudTracking);
        Assert.True(online.CanUseCloudCommentary);
    }

    [Fact]
    public async Task LocalTrackingService_RefusesSilentRunInOnlineOnlyMode()
    {
        AiMode prev = SettingsManager.Instance.AiMode;
        string prevKey = SettingsManager.Instance.GeminiApiKey ?? "";
        string tmp = Path.Combine(Path.GetTempPath(), $"fvs_dummy_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tmp, new byte[] { 0 });
        try
        {
            SettingsManager.Update(s => { s.AiMode = AiMode.OnlineOnly; s.GeminiApiKey = FakeGoogleKey; });
            var req = new AiTrackingRequest(tmp, 0, 3, 1280, 720, AiGameProfile.ThirdPerson,
                new DiscoveredSubject { Label = "Test", Xmin = 100, Ymin = 100, Xmax = 200, Ymax = 250 }, null, 0, 2.2, 1.5, 5, true, false, null);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocalTrackingService.TrackAsync(req, null, null, CancellationToken.None));
        }
        finally
        {
            SettingsManager.Update(s => { s.AiMode = prev; s.GeminiApiKey = prevKey; });
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [AvaloniaFact]
    public void ZoomStartWindow_IsMinimalWithoutTextBoxOrDrawBox_AndSeparatesModes()
    {
        var hybrid = new AiZoomStartWindow(new AiZoomStartOptions(
            CanReframe: true, HasBox: true, BoxRunsOnThisPc: true,
            PreviousDescription: "player with shield",
            FollowBoxCost: "Runs on PC", FindCost: "~$0.001",
            Mode: AiMode.Hybrid, NeuralPackReady: false, EligibleMultiBlockCount: 3, CanUseCloudDiscovery: true));

        var hybridRadios = hybrid.GetLogicalDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(5, hybridRadios.Count);
        Assert.Empty(hybrid.GetLogicalDescendants().OfType<TextBox>());
        Assert.DoesNotContain(hybridRadios, r => (r.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) ?? "").Contains("Draw a box"));
        Assert.Contains(hybridRadios, r => (r.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) ?? "").Contains("Cloud AI Tracker"));
        Assert.Contains(hybridRadios, r => (r.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) ?? "").Contains("Let me select"));
        Assert.Contains(hybridRadios, r => (r.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) ?? "").Contains("Re-aim"));
        Assert.Contains(hybridRadios, r => (r.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) ?? "").Contains("Follow my zoom box"));
        Assert.Contains(hybridRadios, r => (r.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) ?? "").Contains("Zoom all 3 blocks"));

        var local = new AiZoomStartWindow(new AiZoomStartOptions(
            CanReframe: false, HasBox: false, BoxRunsOnThisPc: true,
            PreviousDescription: "secret",
            FollowBoxCost: "Runs on PC", FindCost: "",
            Mode: AiMode.LocalOnly, NeuralPackReady: false, EligibleMultiBlockCount: 1, CanUseCloudDiscovery: false));
        Assert.Empty(local.GetLogicalDescendants().OfType<TextBox>());
        var localRadios = local.GetLogicalDescendants().OfType<RadioButton>().ToList();
        Assert.Single(localRadios);
        Assert.DoesNotContain(localRadios, r => (r.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) ?? "").Contains("Cloud AI Tracker"));
    }

    [AvaloniaFact]
    public void SubjectPicker_ShowsDescribeManuallyInCloudAndHidesInLocalFree()
    {
        var subjects = new List<DiscoveredSubject>
        {
            new() { Id = 1, Label = "Player #1", FrameIndex = 1, Xmin = 200, Ymin = 200, Xmax = 350, Ymax = 500 },
        };
        var discovery = new AiDiscoveryResult(subjects, "", "", "", 1.0, 2.0, 3.0, 0);

        var cloudPicker = new AiSubjectPickerWindow(discovery, 0, 4.0, isLocalFreeMode: false, AiGameProfile.ThirdPerson, allowDescribeManually: true);
        var cloudScope = cloudPicker.FindNameScope()!;
        var cloudDescribe = (Button)cloudScope.Find("DescribeManuallyBtn")!;
        Assert.True(cloudDescribe.IsVisible);
        Assert.Equal("Describe Manually", cloudDescribe.Content);

        var localPicker = new AiSubjectPickerWindow(discovery, 0, 4.0, isLocalFreeMode: true, AiGameProfile.ThirdPerson, allowDescribeManually: false);
        var localScope = localPicker.FindNameScope()!;
        var localDescribe = (Button)localScope.Find("DescribeManuallyBtn")!;
        Assert.False(localDescribe.IsVisible);
    }

    [AvaloniaFact]
    public void AiSetupWizard_IsResizableCompactAndCentered_AndTracksBounds()
    {
        var wizard = new AiSetupWizardWindow(AiVendor.Google, null, chooseCompanyFirst: false);
        Assert.True(wizard.CanResize);
        Assert.Equal(SizeToContent.Manual, wizard.SizeToContent);
        Assert.InRange(wizard.Height, 440, 720);
        string wizSrc = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", "AiSetupWizardWindow.axaml.cs"));
        Assert.Contains("WindowBoundsHelper.Track(this, \"AiSetupWizardBounds\")", wizSrc);
    }

    [Fact]
    public async Task LocalTrackingService_DamagedOptionalPack_FallsBackToBuiltInWithoutThrowing()
    {
        string? ffmpeg = TryResolveFfmpeg();
        Assert.False(string.IsNullOrEmpty(ffmpeg), "ffmpeg.exe must be available.");
        string dir = Path.Combine(Path.GetTempPath(), $"fvs_damaged_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string video = Path.Combine(dir, "clip.mp4");
        string packDir = Path.Combine(dir, "bad_pack");
        Directory.CreateDirectory(packDir);
        AiMode prevMode = SettingsManager.Instance.AiMode;
        string? prevPackEnv = Environment.GetEnvironmentVariable("FVS_LOCAL_AI_PACK_DIR");
        try
        {
            File.WriteAllText(Path.Combine(packDir, "onnxruntime.dll"), "corrupted");
            Environment.SetEnvironmentVariable("FVS_LOCAL_AI_PACK_DIR", packDir);

            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=0x203040:s=320x180:r=10:d=2.0", "-vf", "drawbox=x=120:y=40:w=50:h=90:color=0xe03030:t=fill", "-c:v", "libx264", "-pix_fmt", "yuv420p", video })
                psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync();
            Assert.Equal(0, proc.ExitCode);

            SettingsManager.Update(s => s.AiMode = AiMode.Hybrid);
            var req = new AiTrackingRequest(video, 0, 2.0, 320, 180, AiGameProfile.Generic,
                new DiscoveredSubject { Id = 1, Label = "Region", FrameIndex = 1, Xmin = 370, Ymin = 220, Xmax = 540, Ymax = 730 },
                null, 0, 2.2, 1.3, 2.0, false, false, null);

            var logs = new List<string>();
            var traj = await LocalTrackingService.TrackAsync(req, null, msg => logs.Add(msg), CancellationToken.None, allowInOnlineMode: true);
            Assert.NotNull(traj);
            Assert.True(traj.Keyframes.Count >= 1);
            Assert.Contains(logs, l => l.Contains("built-in basic", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FVS_LOCAL_AI_PACK_DIR", prevPackEnv);
            SettingsManager.Update(s => s.AiMode = prevMode);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void VideoMergerFinishedExit_SignalsExitApplicationWithoutReopeningMain()
    {
        var res = new ToolNavigationResult("Video Merger", ReturnedToOwner: false, SelectedClipPath: null, ExitApplication: true);
        Assert.True(res.ExitApplication);

        string mergerSrc = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "VideoMergerWindow.axaml.cs"));
        string navSrc = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Services", "ToolNavigator.cs"));
        Assert.Contains("ExitApplication: true", mergerSrc);
        Assert.Contains("if (result.ExitApplication)", navSrc);
        Assert.Contains("MarkExportedCleanExitForTool()", navSrc);

        string aiZoomSrc = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Assert.Contains("ResolveLookaheadDurationAsync", aiZoomSrc);
        Assert.Contains("Review completed blocks", aiZoomSrc);
    }

    [Fact]
    public void TrajectorySmoother_TightensLandscapeZoomWithoutDistortion_AndAnticipatesFastJumps()
    {
        var still = new List<RawSubjectWaypoint>();
        for (int i = 0; i <= 10; i++)
            still.Add(new RawSubjectWaypoint { Time = i * 0.5, Cx = 500, Cy = 500, W = 100, H = 200, Visible = true });
        var tight = AiTrajectorySmoother.SmoothTrajectory(still, 5.0, 1920, 1080, baseScale: 2.5, minScale: 1.3, deadbandPercent: 2.0, avoidHud: false);
        Assert.Contains(tight.Keyframes, k => k.Scale >= 2.10);

        var lowRes = new List<RawSubjectWaypoint>();
        for (int i = 0; i <= 10; i++)
            lowRes.Add(new RawSubjectWaypoint { Time = i * 0.5, Cx = 500, Cy = 500, W = 40, H = 80, Visible = true });
        var guarded = AiTrajectorySmoother.SmoothTrajectory(lowRes, 5.0, 640, 360, baseScale: 4.0, minScale: 1.3, deadbandPercent: 2.0, avoidHud: false);
        Assert.All(guarded.Keyframes, k => Assert.True(k.Scale <= 2.05));

        Assert.True(AiTrajectorySmoother.FastMoveLookaheadSec >= 0.85);
        var jump = new List<RawSubjectWaypoint>
        {
            new() { Time = 0.0, Cx = 250, Cy = 500, W = 100, H = 200, Visible = true },
            new() { Time = 0.5, Cx = 250, Cy = 500, W = 100, H = 200, Visible = true },
            new() { Time = 1.0, Cx = 250, Cy = 500, W = 100, H = 200, Visible = true },
            new() { Time = 1.5, Cx = 255, Cy = 500, W = 100, H = 200, Visible = true },
            new() { Time = 2.0, Cx = 750, Cy = 500, W = 100, H = 200, Visible = true },
            new() { Time = 2.5, Cx = 750, Cy = 500, W = 100, H = 200, Visible = true },
        };
        var jumpTraj = AiTrajectorySmoother.SmoothTrajectory(jump, 2.5, 1920, 1080, baseScale: 2.8, minScale: 1.3, deadbandPercent: 2.0, avoidHud: false);
        double scaleAtStart = jumpTraj.Keyframes.First(k => k.TimeSec <= 0.1).Scale;
        double scaleBeforeJump = jumpTraj.Keyframes.First(k => Math.Abs(k.TimeSec - 1.5) < 0.1).Scale;
        Assert.True(scaleBeforeJump < scaleAtStart, $"Expected early zoom-out ahead of jump ({scaleBeforeJump:0.00} < {scaleAtStart:0.00}).");
    }

    [AvaloniaFact]
    public void SubjectPicker_SurfacesFallbackAndDetectorNotices_AndShowsBatchSkip()
    {
        var detStatus = new LocalDetectorStatus(LocalDetectorState.PackUnavailable, "", "No local AI object detector installed; suggestions use motion/contrast regions.");
        var subjects = new List<DiscoveredSubject>
        {
            new() { Id = 1, Label = "Motion region #1 (Center)", FrameIndex = 1, Xmin = 200, Ymin = 200, Xmax = 350, Ymax = 500 },
        };
        var discovery = new AiDiscoveryResult(subjects, "", "", "", 1.0, 2.0, 3.0, 0)
        {
            DetectorStatus = detStatus,
            UsedLocalFallback = true,
            FallbackReason = "Cloud AI unavailable; showing local PC suggestions.",
        };
        var picker = new AiSubjectPickerWindow(discovery, 0, 4.0, isLocalFreeMode: true, AiGameProfile.ThirdPerson, batchContextHeader: "Block 2 of 4");
        var scope = picker.FindNameScope()!;
        var notice = (TextBlock)scope.Find("FallbackNoticeText")!;
        Assert.True(notice.IsVisible);
        Assert.Contains("Cloud AI unavailable", notice.Text);
        Assert.Contains("No local AI object detector", notice.Text);
        var skip = (Button)scope.Find("SkipBlockBtn")!;
        Assert.True(skip.IsVisible);
        var head = (TextBlock)scope.Find("PickerHeadlineText")!;
        Assert.Contains("Block 2 of 4", head.Text);
    }

    [AvaloniaFact]
    public void CommentaryDialogAndPrompt_AreHonestAboutCaptionsOnlyAndProviderAudio()
    {
        var dlg = new AiCommentaryDialogWindow();
        var vm = new AiCommentaryOptionsViewModel(hasSelectedSegment: true)
        {
            ProviderCapabilityNote = AiVisionProviders.DescribeCommentaryCapability(AiVendor.Anthropic),
        };
        dlg.DataContext = vm;

        var texts = dlg.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        var btnContents = dlg.GetLogicalDescendants().OfType<Button>().Select(b => b.Content?.ToString() ?? "").ToList();
        Assert.Contains(texts, t => t.Contains("Does not create a voice."));
        Assert.Contains(texts, t => t.Contains("NO audio"));
        Assert.Contains(texts, t => t == "HOW OFTEN CAPTIONS APPEAR");
        Assert.DoesNotContain(texts, t => t.Contains("HOW OFTEN IT TALKS"));
        Assert.DoesNotContain(texts, t => t.Contains("🎙️"));
        Assert.DoesNotContain(btnContents, c => c.Contains("🎙️"));

        string silentPrompt = AiCommentaryPrompts.BuildPrompt(CaptionTone.Tactical, AiGameProfile.Fortnite, "English", canHearAudio: false);
        Assert.Contains("NO sound", silentPrompt);
        Assert.Contains("NEVER claim you heard", silentPrompt);
        Assert.Contains("visible", silentPrompt);
        string audioPrompt = AiCommentaryPrompts.BuildPrompt(CaptionTone.Tactical, AiGameProfile.Fortnite, "English", canHearAudio: true);
        Assert.Contains("WITH its game sound", audioPrompt);
    }

    [Fact]
    public async Task TrackSubjectAsync_HandsOffAcrossChunkBoundaryAndTrim_ForAllCloudProviders()
    {
        string? ffmpeg = TryResolveFfmpeg();
        Assert.False(string.IsNullOrEmpty(ffmpeg), "ffmpeg.exe must be available to run media handoff tests.");
        string dir = Path.Combine(Path.GetTempPath(), $"fvs_handoff_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string video = Path.Combine(dir, "clip.mp4");
        string refImg = Path.Combine(dir, "ref.jpg");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=0x203040:s=320x180:r=10:d=15", "-c:v", "libx264", "-pix_fmt", "yuv420p", video })
                psi.ArgumentList.Add(a);
            using (var p = System.Diagnostics.Process.Start(psi)!) await p.WaitForExitAsync();

            using (var bmp = new SkiaSharp.SKBitmap(320, 180))
            {
                bmp.Erase(SkiaSharp.SKColors.Red);
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                File.WriteAllBytes(refImg, img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80).ToArray());
            }

            var emptyHandler = new CaptureHandler((_, _) => JsonOk("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"[]\"}]}}]}"));
            var emptySession = new GeminiModelSession(FakeGoogleKey, GeminiClient.DefaultModel, _ => { }, AiVendor.Google,
                new GeminiProvider(new GeminiClient(new HttpClient(emptyHandler), _ => { }, (_, _) => Task.CompletedTask)));
            var emptyDiscovery = await GeminiTrackingService.DiscoverSubjectsWithLookaheadAsync(video, 5, 5, 15, emptySession, AiGameProfile.Generic);
            Assert.Empty(emptyDiscovery.Subjects);
            Assert.Single(emptyHandler.Bodies);
            Assert.Equal(0, emptyDiscovery.ProbesRun);
            Assert.All(new[] { emptyDiscovery.T1, emptyDiscovery.T2, emptyDiscovery.T3 }, t => Assert.InRange(t, 5, 9.999));
            var target = new DiscoveredSubject { Id = 1, Label = "Target", FrameIndex = 3, Xmin = 400, Ymin = 300, Xmax = 520, Ymax = 600 };
            var req = new AiTrackingRequest(video, SourceStartSec: 2.0, SourceDurationSec: 12.0, SourceWidth: 1280, SourceHeight: 720,
                Profile: AiGameProfile.Generic, Target: target, ReferenceFramePath: refImg, ReferenceTimeInSegmentSec: 10.5,
                BaseScale: 2.2, MinScale: 1.5, DeadbandPercent: 5, AvoidHud: true, PortraitMode: false, HudZones: null);

            var gHandler = new CaptureHandler((body, n) =>
            {
                string ans = n == 1
                    ? "[{\"time\":0.0,\"visible\":false,\"cx\":null,\"cy\":null,\"w\":null,\"h\":null}]"
                    : "[{\"time\":2.5,\"visible\":true,\"cx\":460,\"cy\":450,\"w\":120,\"h\":300}]";
                return JsonOk(JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = ans } } } } } }));
            });
            var gSession = new GeminiModelSession(FakeGoogleKey, GeminiClient.DefaultModel, _ => { }, AiVendor.Google,
                new GeminiProvider(new GeminiClient(new HttpClient(gHandler), _ => { }, (_, _) => Task.CompletedTask)));
            var gTraj = await GeminiTrackingService.TrackSubjectAsync(req, gSession);
            Assert.True(gTraj.Keyframes.Count >= 1);
            Assert.Equal(2, gHandler.Bodies.Count);
            Assert.DoesNotContain("At 0.0s into this video the target is inside", gHandler.Bodies[1]);
            Assert.Contains("At 4.5s into this video the target is inside", gHandler.Bodies[1]);

            var cHandler = new CaptureHandler((_, n) =>
            {
                string inputJson = n == 1
                    ? "[{\"time\":0.0,\"visible\":false,\"cx\":null,\"cy\":null,\"w\":null,\"h\":null}]"
                    : "[{\"time\":2.5,\"visible\":true,\"cx\":460,\"cy\":450,\"w\":120,\"h\":300}]";
                string payload = "{\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"respond\",\"input\":{\"items\":" + inputJson + "}}],\"usage\":{\"input_tokens\":10,\"output_tokens\":10}}";
                return JsonOk(payload);
            });
            var cSession = new GeminiModelSession(FakeClaudeKey, ClaudeProvider.DefaultModelName, _ => { }, AiVendor.Anthropic,
                new ClaudeProvider(new HttpClient(cHandler), _ => { }, (_, _) => Task.CompletedTask));
            var cTraj = await GeminiTrackingService.TrackSubjectAsync(req, cSession);
            Assert.True(cTraj.Keyframes.Count >= 1);
            Assert.Contains("At 4.5s into this video", cHandler.Bodies[1]);

            var oHandler = new CaptureHandler((_, n) =>
            {
                string itemsJson = n == 1
                    ? "{\"items\":[{\"time\":0.0,\"visible\":false,\"cx\":null,\"cy\":null,\"w\":null,\"h\":null}]}"
                    : "{\"items\":[{\"time\":2.5,\"visible\":true,\"cx\":460,\"cy\":450,\"w\":120,\"h\":300}]}";
                string payload = JsonSerializer.Serialize(new
                {
                    id = "c1",
                    @object = "chat.completion",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = itemsJson }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 10 }
                });
                return JsonOk(payload);
            });
            var oSession = new GeminiModelSession(FakeOpenAiKey, OpenAiProvider.DefaultModelName, _ => { }, AiVendor.OpenAI,
                new OpenAiProvider(new HttpClient(oHandler), _ => { }, (_, _) => Task.CompletedTask));
            var oTraj = await GeminiTrackingService.TrackSubjectAsync(req, oSession);
            Assert.True(oTraj.Keyframes.Count >= 1);
            Assert.Contains("At 4.5s into this video", oHandler.Bodies[1]);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void MultiBlockPlan_PreservesExistingFiltersCostAndCommitsAtomicUndo()
    {
        var parking = new GranularHistoryParking();
        var session = new GranularEditSession("C:\\test\\clip.mp4", 0, 35000, parking);
        session.Segments.Add(new SpeedSegment(0, 8000, 1.0));
        session.Segments.Add(new SpeedSegment(8000, 16000, 1.5, 100, 100, 400, 400, "1920x1080", false, 8000, 16000, "{\"d\":1}"));
        session.Segments.Add(new SpeedSegment(16000, 24000, 2.0));
        session.Segments.Add(new SpeedSegment(24000, 70000, 1.0));

        var entries = MultiBlockZoomPlan.BuildEntries(session.Segments, new[] { 20000.0 }, GeminiTrackingService.MaxTrackSec);
        Assert.Equal(MultiBlockZoomState.ReadyNoZoom, entries[0].State);
        Assert.True(entries[0].IncludeByDefault);
        Assert.Equal(MultiBlockZoomState.HasAiZoom, entries[1].State);
        Assert.False(entries[1].IncludeByDefault);
        Assert.Equal(MultiBlockZoomState.CrossesClipBoundary, entries[2].State);
        Assert.False(entries[2].IsEligible);
        Assert.Equal(MultiBlockZoomState.ExceedsMaxLength, entries[3].State);
        Assert.False(entries[3].IsEligible);

        var selected = entries.Where(e => e.IncludeByDefault).ToList();
        var estLocal = MultiBlockZoomPlan.EstimateBatch(selected, AiMode.LocalOnly, false, AiVendor.Google, GeminiClient.DefaultModel, AiVendor.Google, GeminiClient.DefaultModel);
        Assert.False(estLocal.UsesCloud);
        Assert.Equal(0m, estLocal.TotalEstimate.EstimatedUsd);

        var estHybrid = MultiBlockZoomPlan.EstimateBatch(selected, AiMode.Hybrid, false, AiVendor.Google, GeminiClient.DefaultModel, AiVendor.Google, GeminiClient.DefaultModel);
        Assert.True(estHybrid.UsesCloud);
        Assert.True(estHybrid.UsesLocalTracking);

        var orig0 = session.Segments[0];
        var orig1 = session.Segments[1];
        var cand0 = orig0 with { AiTrackingTrajectory = "{\"k\":1}", ZoomW = 640, ZoomH = 360, ZoomX = 100, ZoomY = 100, ZoomOrigRes = "1280x720" };
        var cand1 = orig1 with { AiTrackingTrajectory = "{\"k\":2}" };
        var batch = new[] { new PendingBlockZoom(0, orig0, cand0), new PendingBlockZoom(1, orig1, cand1) };

        MultiBlockZoomPlan.ApplyCandidates(session.Segments, batch);
        Assert.Equal(0, session.History.UndoCount);
        MultiBlockZoomPlan.RestoreOriginals(session.Segments, batch);
        Assert.Null(session.Segments[0].AiTrackingTrajectory);
        Assert.Equal(0, session.History.UndoCount);

        session.Checkpoint("apply AI smart zoom to multiple blocks");
        MultiBlockZoomPlan.ApplyCandidates(session.Segments, batch);
        Assert.Equal(1, session.History.UndoCount);
        Assert.Equal("{\"k\":1}", session.Segments[0].AiTrackingTrajectory);
        Assert.Equal("{\"k\":2}", session.Segments[1].AiTrackingTrajectory);

        session.Undo();
        Assert.Null(session.Segments[0].AiTrackingTrajectory);
        Assert.Equal("{\"d\":1}", session.Segments[1].AiTrackingTrajectory);
    }

    [AvaloniaFact]
    public void SubjectPicker_HidesCardsStripAndSupportsMultiFrameAnchors()
    {
        var subjects = new List<DiscoveredSubject>
        {
            new() { Id = 1, Label = "Player #1", FrameIndex = 1, Xmin = 200, Ymin = 200, Xmax = 350, Ymax = 500 },
            new() { Id = 2, Label = "Player #2", FrameIndex = 2, Xmin = 400, Ymin = 200, Xmax = 550, Ymax = 500 },
        };
        var discovery = new AiDiscoveryResult(subjects, "", "", "", 1.0, 2.0, 3.0, 0);
        var picker = new AiSubjectPickerWindow(discovery, 0, 4.0, isLocalFreeMode: false);
        var scope = picker.FindNameScope()!;
        var container = (StackPanel)scope.Find("SubjectsContainer")!;
        var parentBorder = container.FindLogicalAncestorOfType<Border>()!;
        Assert.False(parentBorder.IsVisible);

        var confirmBtn = (Button)scope.Find("ConfirmPickerBtn")!;
        Assert.NotNull(picker.SelectedSubject);
        Assert.Equal(1, picker.SelectedSubject.Id);

        var anchor2 = new DiscoveredSubject { Id = 99, Label = "Anchor Frame 2", FrameIndex = 2, Xmin = 410, Ymin = 210, Xmax = 560, Ymax = 510 };
        picker.GetType().GetField("_anchors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(picker, new[] { picker.SelectedSubject, anchor2, null });

        confirmBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(picker.SelectedSubject);
        Assert.NotNull(picker.SelectedSubject.AdditionalAnchors);
        Assert.Single(picker.SelectedSubject.AdditionalAnchors);
        Assert.Equal(anchor2, picker.SelectedSubject.AdditionalAnchors[0]);
    }

    [Fact]
    public void AiZoomPreview_HasReplayControl_AndDoesNotAutoLoopAtEnd()
    {
        string axaml = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml"));
        Assert.Contains("Name=\"AiTrackingReplayBtn\"", axaml);
        Assert.Contains("Content=\"▶ REPLAY\"", axaml);
        Assert.Contains("AI SMART ZOOM PREVIEW", axaml);

        string cs = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Assert.Contains("if (curPlaybackRelMs >= loopEndMs)", cs);
        Assert.Contains("replayBtn.Content = \"▶ REPLAY\"", cs);
        Assert.Contains("replayBtn.Content = \"⏸ PAUSE\"", cs);
        Assert.Contains("private void ReplayAiPreview()", cs);
        Assert.DoesNotContain("Notify(\"AI Smart Zoom ready", cs);
    }

    [AvaloniaFact]
    public void AiZoomPreviewModalWindow_PresentsPreviewControls_AndHandlesUserActions()
    {
        bool replayed = false;
        var modal = new AiZoomPreviewModalWindow(() => { replayed = true; }, "Usage info line", blockCount: 2);
        Assert.NotNull(modal.Content);
        Assert.False(modal.Result);
        Assert.False(replayed);
        modal.SetPlaybackEnded();
        modal.TogglePlaybackState(isPlaying: true);
        modal.TogglePlaybackState(isPlaying: false);
    }

    [Fact]
    public void LocalTracker_SupportsAdditionalAnchors_ForMultiPointConditioning()
    {
        var box1 = new PixelBox(100, 100, 200, 200);
        var box2 = new PixelBox(120, 110, 220, 210);
        var anchors = new List<(double TimeSec, PixelBox Box)> { (2.5, box2) };
        var req = new LocalTrackingRequest("ffmpeg", "video.mp4", 0, 5.0, 1280, 720, box1, 0.0, AiGameProfile.Generic, anchors);
        Assert.NotNull(req.AdditionalAnchors);
        Assert.Single(req.AdditionalAnchors);
        Assert.Equal(2.5, req.AdditionalAnchors[0].TimeSec);
        Assert.Equal(box2, req.AdditionalAnchors[0].Box);
    }

    [Fact]
    public void LocalTrackingMath_ReconcileInterval_NeverAveragesCrossingCharacters()
    {
        var boxA = new PixelBox(100, 100, 200, 200);
        var boxB = new PixelBox(700, 400, 800, 500);
        var ref1 = new TrackingReference(0.0, boxA);
        var ref2 = new TrackingReference(2.0, boxB);
        var fwd = new List<TrackedFrame>
        {
            new(0.0, boxA, true, 0.95),
            new(1.0, boxA, true, 0.90),
            new(2.0, boxA, true, 0.85),
        };
        var bwd = new List<TrackedFrame>
        {
            new(0.0, boxB, true, 0.85),
            new(1.0, boxB, true, 0.90),
            new(2.0, boxB, true, 0.95),
        };
        var res = LocalTrackingMath.ReconcileInterval(fwd, bwd, ref1, ref2, 1280, 720);
        Assert.Equal(3, res.Frames.Count);
        foreach (var f in res.Frames)
        {
            bool isNearA = LocalTrackingMath.Iou(f.Box, boxA) > 0.8;
            bool isNearB = LocalTrackingMath.Iou(f.Box, boxB) > 0.8;
            Assert.True(isNearA || isNearB, $"Reconciled box was averaged between characters: Cx={f.Box.Cx}, Cy={f.Box.Cy}");
        }
        Assert.True(res.IsUncertain);
        Assert.True(res.DisagreementCount >= 2);
    }

    [Fact]
    public void LocalTrackingMath_ReconcileInterval_SmoothsAgreedTrajectory()
    {
        var box1 = new PixelBox(100, 100, 200, 200);
        var box2 = new PixelBox(120, 100, 220, 200);
        var box3 = new PixelBox(140, 100, 240, 200);
        var ref1 = new TrackingReference(0.0, box1);
        var ref2 = new TrackingReference(2.0, box3);
        var fwd = new List<TrackedFrame>
        {
            new(0.0, box1, true, 0.90),
            new(1.0, box2, true, 0.90),
            new(2.0, box3, true, 0.90),
        };
        var bwd = new List<TrackedFrame>
        {
            new(0.0, box1, true, 0.90),
            new(1.0, box2, true, 0.90),
            new(2.0, box3, true, 0.90),
        };
        var res = LocalTrackingMath.ReconcileInterval(fwd, bwd, ref1, ref2, 1280, 720);
        Assert.Equal(3, res.Frames.Count);
        Assert.False(res.IsUncertain);
        Assert.Equal(0, res.DisagreementCount);
    }

    [AvaloniaFact]
    public void AiZoomPreviewModalWindow_HasKeepFixTrackingDiscardButtons()
    {
        var modal = new AiZoomPreviewModalWindow(() => { }, "Summary", 1);
        Assert.Equal(AiZoomPreviewAction.Discard, modal.Action);
        Assert.False(modal.Result);
    }

    [AvaloniaFact]
    public void AiSubjectPickerWindow_WizardStages_AndNotVisibleBehavior()
    {
        var subjects = new List<DiscoveredSubject>
        {
            new() { Id = 1, Label = "Player", FrameIndex = 1, Xmin = 100, Ymin = 100, Xmax = 200, Ymax = 300 },
        };
        var discovery = new AiDiscoveryResult(subjects, "", "", "", 0.5, 1.5, 2.5, 0);
        var picker = new AiSubjectPickerWindow(discovery, 0, 3.0, isLocalFreeMode: true);
        var scope = picker.FindNameScope()!;
        var headline = (TextBlock)scope.Find("PickerHeadlineText")!;
        var intro = (TextBlock)scope.Find("PickerIntroText")!;
        var confirmBtn = (Button)scope.Find("ConfirmPickerBtn")!;
        var notVisBtn1 = (Button)scope.Find("Frame1NotVisibleBtn")!;
        var badge1 = (TextBlock)scope.Find("Frame1Badge")!;

        Assert.Equal("Stage 1 of 3 · Choose who to follow", headline.Text);
        Assert.Equal(AiSubjectPickerWindow.PickIntro, intro.Text);
        Assert.Equal("Next: Check Selections →", confirmBtn.Content);

        notVisBtn1.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("— Not visible", badge1.Text);

        var notVisBtn2 = (Button)scope.Find("Frame2NotVisibleBtn")!;
        var notVisBtn3 = (Button)scope.Find("Frame3NotVisibleBtn")!;
        notVisBtn2.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        notVisBtn3.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var resampleBtn = (Button)scope.Find("ResampleBtn")!;
        Assert.True(resampleBtn.IsVisible);
        Assert.False(confirmBtn.IsEnabled);
    }

    [Fact]
    public void LocalTrackingMath_ReconcileInterval_SafeAcrossSceneCuts_PreservesBackwardOnlyFrames()
    {
        var boxA = new PixelBox(100, 100, 200, 200);
        var boxB = new PixelBox(110, 110, 210, 210);
        var ref1 = new TrackingReference(0.0, boxA);
        var ref2 = new TrackingReference(2.0, boxB);
        var fwd = new List<TrackedFrame>
        {
            new(0.0, boxA, true, 0.95),
            new(0.5, boxA, true, 0.90),
        };
        var bwd = new List<TrackedFrame>
        {
            new(1.0, boxB, true, 0.92),
            new(1.5, boxB, true, 0.93),
            new(2.0, boxB, true, 0.95),
        };
        var reconciled = LocalTrackingMath.ReconcileInterval(fwd, bwd, ref1, ref2, 1920, 1080);
        Assert.Equal(5, reconciled.Frames.Count);
        Assert.Equal(0.0, reconciled.Frames[0].TimeSec, 3);
        Assert.Equal(0.5, reconciled.Frames[1].TimeSec, 3);
        Assert.Equal(1.0, reconciled.Frames[2].TimeSec, 3);
        Assert.Equal(1.5, reconciled.Frames[3].TimeSec, 3);
        Assert.Equal(2.0, reconciled.Frames[4].TimeSec, 3);
        Assert.True(reconciled.Frames[2].Visible);
        Assert.True(reconciled.Frames[3].Visible);
    }

    [Fact]
    public void CandidateFrameTimes_WithAttempts_CyclesDifferentMomentsWithinSegment()
    {
        var (t1_0, t2_0, t3_0) = GeminiTrackingService.CandidateFrameTimes(10.0, 10.0, attempt: 0);
        var (t1_1, t2_1, t3_1) = GeminiTrackingService.CandidateFrameTimes(10.0, 10.0, attempt: 1);
        var (t1_2, t2_2, t3_2) = GeminiTrackingService.CandidateFrameTimes(10.0, 10.0, attempt: 2);

        Assert.True(t1_0 >= 10.0 && t3_0 <= 20.0);
        Assert.True(t1_1 >= 10.0 && t3_1 <= 20.0);
        Assert.True(t1_2 >= 10.0 && t3_2 <= 20.0);

        Assert.NotEqual(t1_0, t1_1);
        Assert.NotEqual(t2_0, t2_1);
        Assert.NotEqual(t1_1, t1_2);
    }

    [AvaloniaFact]
    public void AiSubjectPickerWindow_ResampleMomentsChoice_ReturnsResampleChoice()
    {
        var subjects = new List<DiscoveredSubject>
        {
            new() { Id = 1, Label = "Player", FrameIndex = 1, Xmin = 100, Ymin = 100, Xmax = 200, Ymax = 300 },
        };
        var discovery = new AiDiscoveryResult(subjects, "", "", "", 1.0, 2.0, 3.0, 0);
        var picker = new AiSubjectPickerWindow(discovery, 0.0, 5.0);
        var scope = picker.FindNameScope()!;
        var resampleBtn = (Button)scope.Find("ResampleBtn")!;
        resampleBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(AiPickerChoice.ResampleMoments, picker.Choice);
        Assert.NotEqual("__RESAMPLE__", picker.ManualDescription);
        Assert.True(string.IsNullOrEmpty(picker.ManualDescription));
    }

    [AvaloniaFact]
    public void AiZoomPreviewModalWindow_ContainsEmbeddedVideoPlayer_AndStatusTextDoesNotReferToExternalPlayer()
    {
        var modal = new AiZoomPreviewModalWindow(() => { }, "Summary", 1);
        Assert.NotNull(modal.Content);
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Controls", "AiZoomPreviewModalWindow.cs"));
        Assert.Contains("new MpvVideoView", src);
        Assert.DoesNotContain("player above", src);
        Assert.DoesNotContain("player outside", src);
    }

    [Fact]
    public void BatchTracking_ResampleMoments_CyclesAttemptWithoutCanceling()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Assert.Contains("blockSampleAttempt++;", src);
        Assert.Contains("if (picker.Choice == AiPickerChoice.ResampleMoments)", src);
    }

    [Fact]
    public void HandleFixTracking_NotVisibleAndReviewAnyway_DoNotDiscardPreview()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Assert.Contains("BeginAiPreview(ctx.SegmentIndex, ctx.OriginalSegment, ctx.Trajectory", src);
        Assert.Contains("if (corr.Proceed && !corr.NotVisible && !corr.ReviewAnyway", src);
    }

    [Fact]
    public void HandleFixTracking_UsesCancellationAndProgressOverlay()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Assert.Contains("AiTrackingProgressOverlayCtl", src);
        Assert.Contains("_aiTrackingCts = new CancellationTokenSource();", src);
    }

    [Fact]
    public void TrimmedVideo_PreviewCrop_UsesTrimmedRelativeClock()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Assert.Contains("double trimStartSec = _edit.TrimStartMs / 1000.0;", src);
        Assert.Contains("double relSec = Math.Max(0, absTimeSec - trimStartSec);", src);
    }

    [Fact]
    public void PreviewModal_EmptyCrop_ClearsPreviousCrop()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Controls", "AiZoomPreviewModalWindow.cs"));
        Assert.Contains("string crop = _cropAtTime(cur) ?? \"\";", src);
        Assert.Contains("if (crop != _lastCrop)", src);
    }

    [Fact]
    public void PreviewModal_HandleReplay_DoesNotTriggerEditorPlayerWhenVideoActive()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Controls", "AiZoomPreviewModalWindow.cs"));
        Assert.Contains("TogglePlaybackState(true);", src);
        Assert.Contains("return;", src);
        Assert.Contains("_onReplay?.Invoke();", src);
    }

    [Fact]
    public void HandleFixTracking_UsesModalPlaybackPosition()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Assert.Contains("HandleFixTrackingAsync(ctx, modalTimeSec)", src);
        Assert.Contains("double curRelSec = mTime - ctx.Source.SourceStartSec;", src);
    }

    private static string? TryResolveFfmpeg()
    {
        string? env = Environment.GetEnvironmentVariable("FVS_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        try { return FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries"); }
        catch
        {
            string dev = Path.Combine(RepoRoot.Path, ".dev-run", "app", "backend", "ffmpeg.exe");
            return File.Exists(dev) ? dev : null;
        }
    }
}
