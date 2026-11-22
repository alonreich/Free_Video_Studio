using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using SkiaSharp;

namespace FreeVideoStudio.App.Services;

public class DiscoveredSubject
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("ymin")]
    public int Ymin { get; set; }

    [JsonPropertyName("xmin")]
    public int Xmin { get; set; }

    [JsonPropertyName("ymax")]
    public int Ymax { get; set; }

    [JsonPropertyName("xmax")]
    public int Xmax { get; set; }

    [JsonPropertyName("frame_index")]
    public int FrameIndex { get; set; } = 2;

    [JsonIgnore]
    public bool IsUserBox { get; set; }

    [JsonIgnore]
    public bool IsDetectorMatch { get; set; }

    [JsonIgnore]
    public List<DiscoveredSubject>? AdditionalAnchors { get; set; }

    [JsonIgnore]
    public double? ExactTimeSec { get; set; }
}

[JsonSerializable(typeof(List<RawSubjectWaypoint>))]
[JsonSerializable(typeof(DiscoveredSubject))]
[JsonSerializable(typeof(List<DiscoveredSubject>))]
internal partial class GeminiApiJsonContext : JsonSerializerContext { }

public sealed record AiDiscoveryResult(
    List<DiscoveredSubject> Subjects,
    string F1, string F2, string F3,
    double T1, double T2, double T3,
    double LookaheadOffsetSec)
{
    public int ProbesRun { get; init; }
    public FreeVideoStudio.Core.LocalAi.LocalDetectorStatus? DetectorStatus { get; init; }
    public bool UsedLocalFallback { get; init; }
    public string? FallbackReason { get; init; }
}

public enum AiPartFailureChoice
{
    Retry,
    Skip,
    Stop,
}

public sealed record AiTrackingRequest(
    string VideoPath,
    double SourceStartSec,
    double SourceDurationSec,
    int SourceWidth,
    int SourceHeight,
    AiGameProfile Profile,
    DiscoveredSubject Target,
    string? ReferenceFramePath,
    double ReferenceTimeInSegmentSec,
    double BaseScale,
    double MinScale,
    double DeadbandPercent,
    bool AvoidHud,
    bool PortraitMode,
    IReadOnlyList<AiNormalizedRect>? HudZones);

public sealed class GeminiModelSession
{
    private readonly string _apiKey;

    public AiVendor Vendor { get; }
    public IAiVisionProvider Provider { get; }
    public string ApiKey => _apiKey;
    public string VendorName => Vendor switch
    {
        AiVendor.Anthropic => "Anthropic Claude",
        AiVendor.OpenAI => "OpenAI",
        _ => "Google Gemini",
    };

    public GeminiModelSession(
        string apiKey,
        string? model,
        Action<string>? log = null,
        AiVendor vendor = AiVendor.Google,
        IAiVisionProvider? provider = null)
    {
        _apiKey = apiKey ?? string.Empty;
        Vendor = vendor;
        IsInjected = provider != null;
        Provider = provider ?? AiVisionProviders.Get(vendor);
        string resolved = vendor switch
        {
            AiVendor.Anthropic => ClaudeProvider.ResolveModel(model),
            AiVendor.OpenAI => OpenAiProvider.ResolveModel(model),
            _ => GeminiClient.ResolveModel(model),
        };
        Model = AiVisionProviders.EffectiveModel(vendor, resolved, _apiKey);
    }

    public string Model { get; }

    public bool IsInjected { get; }

    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey);

    public string Redact(string? text) => GeminiClient.Redact(text, _apiKey);
}

public static class GeminiTrackingService
{
    public const double MaxChunkSec = 8.0;

    public const double MaxTrackSec = 40.0;

    public const double MinSliceSec = 1.0;

    public const double TrackingFps = 2.0;

    public const double ClipFps = TrackingFps * 4;

    internal const string ClipFpsTag = "AICLIPFPS_01";

    public static async Task<(string F1, string F2, string F3, double T1, double T2, double T3)> ExtractThreeCandidateFramesAsync(
        string videoPath,
        double sourceStartSec,
        double sourceDurationSec,
        CancellationToken cancellationToken = default,
        int attempt = 0)
    {
        string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);

        var (t1, t2, t3) = CandidateFrameTimes(sourceStartSec, sourceDurationSec);
        if (attempt > 0)
            (t1, t2, t3) = CandidateFrameTimes(sourceStartSec, sourceDurationSec, attempt);

        string f1Path = Path.Combine(tempDir, $"cand_f1_{Guid.NewGuid():N}.jpg");
        string f2Path = Path.Combine(tempDir, $"cand_f2_{Guid.NewGuid():N}.jpg");
        string f3Path = Path.Combine(tempDir, $"cand_f3_{Guid.NewGuid():N}.jpg");

        await ExtractStillAsync(videoPath, t1, f1Path, cancellationToken).ConfigureAwait(false);
        await ExtractStillAsync(videoPath, t2, f2Path, cancellationToken).ConfigureAwait(false);
        await ExtractStillAsync(videoPath, t3, f3Path, cancellationToken).ConfigureAwait(false);

        return (f1Path, f2Path, f3Path, t1, t2, t3);
    }

    public const string SegmentBoundsTag = "AISEGMENTONLY_01";
    internal static (double T1, double T2, double T3) CandidateFrameTimes(double start, double duration, int attempt = 0)
    {
        if (!double.IsFinite(start) || start < 0 || !double.IsFinite(duration) || duration <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Select a non-empty video segment.");
        if (attempt == 0)
            return (start + duration * 0.15, start + duration * 0.50, start + duration * 0.85);
        (double f1, double f2, double f3) = (attempt % 4) switch
        {
            1 => (0.30, 0.65, 0.95),
            2 => (0.05, 0.35, 0.70),
            3 => (0.22, 0.55, 0.80),
            _ => (0.15, 0.50, 0.85),
        };
        return (start + duration * f1, start + duration * f2, start + duration * f3);
    }
    public const int CandidateMaxDimension = 1280;

    internal static async Task ExtractStillAsync(string videoPath, double sec, string outPath, CancellationToken token)
    {
        string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        string seek = sec.ToString("0.000", CultureInfo.InvariantCulture);
        await VideoFrameGrabber.GrabAsync<string>(
            ffmpeg, videoPath, VideoFrameRequest.AtFfmpegSeek(seek), StillTimeout,
            frame =>
            {
                File.WriteAllBytes(outPath, EncodeCandidateJpeg(frame));
                return outPath;
            },
            async (budget, ct) =>
            {
                await ExtractStillWithFfmpegAsync(ffmpeg, videoPath, seek, outPath, budget, ct).ConfigureAwait(false);
                return outPath;
            },
            token).ConfigureAwait(false);
    }

    private static readonly TimeSpan StillTimeout = TimeSpan.FromSeconds(10);

    internal const int CandidateJpegQuality = 95;

    internal static byte[] EncodeCandidateJpeg(DecodedVideoFrame frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using SKData pixels = SKData.CreateCopy(frame.Pixels);
        using SKImage original = SKImage.FromPixels(info, pixels, frame.Stride)
                              ?? throw new IOException("Could not wrap the decoded frame.");

        SKImage imageToEncode = original;
        SKBitmap? scaledBitmap = null;
        try
        {
            if (frame.Width > CandidateMaxDimension || frame.Height > CandidateMaxDimension)
            {
                int targetW = frame.Width;
                int targetH = frame.Height;
                if (targetW >= targetH)
                {
                    targetH = Math.Max(1, (int)Math.Round((double)targetH * CandidateMaxDimension / targetW));
                    targetW = CandidateMaxDimension;
                }
                else
                {
                    targetW = Math.Max(1, (int)Math.Round((double)targetW * CandidateMaxDimension / targetH));
                    targetH = CandidateMaxDimension;
                }
                if ((targetW & 1) != 0) targetW++;
                if ((targetH & 1) != 0) targetH++;

                var scaledInfo = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Opaque);
                scaledBitmap = new SKBitmap(scaledInfo);
                using var origBitmap = SKBitmap.FromImage(original);
                if (origBitmap != null && origBitmap.ScalePixels(scaledBitmap, new SKSamplingOptions(SKFilterMode.Linear)))
                {
                    imageToEncode = SKImage.FromBitmap(scaledBitmap);
                }
            }

            using SKData jpeg = imageToEncode.Encode(SKEncodedImageFormat.Jpeg, CandidateJpegQuality)
                                ?? throw new IOException("Could not encode the decoded frame.");
            return jpeg.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(imageToEncode, original)) imageToEncode.Dispose();
            scaledBitmap?.Dispose();
        }
    }

    private static async Task ExtractStillWithFfmpegAsync(string ffmpeg, string videoPath, string seek, string outPath, TimeSpan budget, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(seek);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale='min({CandidateMaxDimension},iw)':-2");
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add(outPath);

        await AsyncProcessRunner.RunAsync(psi, budget, token).ConfigureAwait(false);
    }

    private static void Typed(Utf8JsonWriter w, string name, string type, bool nullable = false)
    {
        w.WriteStartObject(name);
        w.WriteString("type", type);
        if (nullable) w.WriteBoolean("nullable", true);
        w.WriteEndObject();
    }

    private static void Required(Utf8JsonWriter w, params string[] names)
    {
        w.WriteStartArray("required");
        foreach (string n in names) w.WriteStringValue(n);
        w.WriteEndArray();
    }

    internal static void WriteTrackingSchema(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "ARRAY");
        w.WriteStartObject("items");
        w.WriteString("type", "OBJECT");
        w.WriteStartObject("properties");
        Typed(w, "time", "NUMBER");
        Typed(w, "visible", "BOOLEAN");
        Typed(w, "cx", "INTEGER", nullable: true);
        Typed(w, "cy", "INTEGER", nullable: true);
        Typed(w, "w", "INTEGER", nullable: true);
        Typed(w, "h", "INTEGER", nullable: true);
        w.WriteEndObject();
        Required(w, "time", "visible");
        w.WriteEndObject();
        w.WriteEndObject();
    }

    public const string DiscoveryJsonSchema = "{\"type\":\"ARRAY\",\"items\":{\"type\":\"OBJECT\",\"properties\":{\"id\":{\"type\":\"INTEGER\"},\"label\":{\"type\":\"STRING\"},\"frame_index\":{\"type\":\"INTEGER\"},\"ymin\":{\"type\":\"INTEGER\"},\"xmin\":{\"type\":\"INTEGER\"},\"ymax\":{\"type\":\"INTEGER\"},\"xmax\":{\"type\":\"INTEGER\"}},\"required\":[\"id\",\"label\",\"frame_index\",\"ymin\",\"xmin\",\"ymax\",\"xmax\"]}}";

    public static async Task<List<DiscoveredSubject>> DiscoverSubjectsAsync(
        string frame1Path, string frame2Path, string frame3Path,
        GeminiModelSession session, AiGameProfile profile,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default,
        string? targetDescription = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        consoleLog?.Invoke($"Sending 3 candidate frames to {session.VendorName} for subject discovery…");
        var media = new List<(string, byte[], double?)>(3);
        foreach (string f in new[] { frame1Path, frame2Path, frame3Path })
        {
            if (string.IsNullOrEmpty(f) || !File.Exists(f))
                throw new InvalidOperationException("A candidate frame could not be extracted from the video.");
            media.Add(("image/jpeg", await File.ReadAllBytesAsync(f, cancellationToken).ConfigureAwait(false), null));
        }

        string discoveryPrompt = AiTrackingPrompts.Discovery(profile, targetDescription);
        string reuseKey = AiResultCache.Key("discovery-v1", session.Vendor.ToString(), session.Model, discoveryPrompt,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media[0].Item2)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media[1].Item2)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media[2].Item2)));
        string text = await AiResultCache.RunAsync(reuseKey, async () =>
        {
            var provider = session.Provider;
            var images = new List<AiImageInput>(3);
            for (int i = 0; i < media.Count; i++)
            {
                images.Add(new AiImageInput(media[i].Item2, "image/jpeg", $"[Candidate Still {i + 1}]"));
            }
            return await provider.GenerateJsonAsync(session.ApiKey, session.Model, discoveryPrompt, images, null, DiscoveryJsonSchema, AiReasoningPolicy.For(AiUsage.SubjectDiscovery), GeminiCallOptions.Tracking, consoleLog, cancellationToken).ConfigureAwait(false);
        }, answer => AiAnswerGuard.IsReadableJson(answer), cancellationToken, consoleLog, refresh: session.IsInjected).ConfigureAwait(false);
        CoreLogger.Info("AI-Discovery", session.Redact($"{session.VendorName} discovery answer ({text.Length} chars): {GeminiClient.Truncate(text, 800)}"));

        List<DiscoveredSubject> raw = ParseDiscovery(text, session.VendorName);
        var valid = new List<DiscoveredSubject>();
        foreach (DiscoveredSubject sub in raw)
        {
            if (IsMainPlayerCharacter(sub, profile))
            {
                consoleLog?.Invoke($"Excluded '{sub.Label}' — the player's own character is never a target.");
                continue;
            }
            if (IsInanimateScenery(sub))
            {
                consoleLog?.Invoke($"Excluded scenery '{sub.Label}'.");
                continue;
            }
            sub.Id = valid.Count + 1;
            valid.Add(sub);
        }
        consoleLog?.Invoke($"Found {valid.Count} target candidate(s).");
        return valid;
    }

    internal static List<DiscoveredSubject> ParseDiscovery(string? text, string vendorName = "AI")
    {
        string json = GeminiClient.StripCodeFence(text);
        List<DiscoveredSubject>? subjects;
        try
        {
            subjects = JsonSerializer.Deserialize(RoundWholeNumberFields(json, DiscoveryIntegerFields), GeminiApiJsonContext.Default.ListDiscoveredSubject);
        }
        catch (JsonException ex)
        {
            throw new GeminiException(GeminiErrorKind.MalformedResponse,
                $"{vendorName}'s list of subjects could not be read. Please try again.", GeminiClient.Truncate(ex.Message, 200));
        }
        var result = new List<DiscoveredSubject>();
        if (subjects == null) return result;
        foreach (DiscoveredSubject s in subjects)
        {
            if (s == null) continue;
            s.Xmin = Math.Clamp(s.Xmin, 0, 1000);
            s.Xmax = Math.Clamp(s.Xmax, 0, 1000);
            s.Ymin = Math.Clamp(s.Ymin, 0, 1000);
            s.Ymax = Math.Clamp(s.Ymax, 0, 1000);
            if (s.Xmax - s.Xmin < 4 || s.Ymax - s.Ymin < 4) continue;
            s.FrameIndex = Math.Clamp(s.FrameIndex, 1, 3);
            s.Label = (s.Label ?? string.Empty).Trim();
            result.Add(s);
        }
        return result;
    }

    public static async Task<AiDiscoveryResult> DiscoverSubjectsWithLookaheadAsync(
        string videoPath,
        double sourceStartSec,
        double sourceDurationSec,
        double sourceEndLimitSec,
        GeminiModelSession session,
        AiGameProfile profile,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default,
        string? targetDescription = null,
        int attempt = 0)
    {
        var (f1, f2, f3, t1, t2, t3) = await ExtractThreeCandidateFramesAsync(
            videoPath, sourceStartSec, sourceDurationSec, cancellationToken, attempt).ConfigureAwait(false);

        var subjects = await DiscoverSubjectsAsync(f1, f2, f3, session, profile, consoleLog, cancellationToken, targetDescription).ConfigureAwait(false);
        return new AiDiscoveryResult(subjects, f1, f2, f3, t1, t2, t3, 0);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    public static IReadOnlyList<(double Start, double End)> PlanChunks(double durationSec)
    {
        var list = new List<(double, double)>();
        if (durationSec <= 0) return list;
        int n = durationSec <= MaxChunkSec + 0.5 ? 1 : (int)Math.Ceiling(durationSec / MaxChunkSec);
        double len = durationSec / n;
        for (int k = 0; k < n; k++)
            list.Add((k * len, k == n - 1 ? durationSec : (k + 1) * len));
        return list;
    }

    public static async Task<AiTrajectorySmoother.SmoothedTrajectory> TrackSubjectAsync(
        AiTrackingRequest req,
        GeminiModelSession session,
        IProgress<string>? statusProgress = null,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default,
        Func<int, int, AiProviderException, Task<AiPartFailureChoice>>? onPartFailed = null,
        IProgress<double>? fraction = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(session);
        if (!File.Exists(req.VideoPath)) throw new FileNotFoundException("Video file not found", req.VideoPath);
        if (!session.HasKey) throw new GeminiException(GeminiErrorKind.InvalidKey, $"No {session.VendorName} API key is configured. Add one in Settings › AI.");
        if (req.SourceDurationSec > MaxTrackSec + 0.01)
            throw new InvalidOperationException($"AI SMART ZOOM can follow a target for up to {MaxTrackSec:0} seconds. Split this segment into shorter blocks and track each one.");

        DiscoveredSubject target = req.Target;
        byte[]? refCrop = null;
        if (!string.IsNullOrEmpty(req.ReferenceFramePath) && File.Exists(req.ReferenceFramePath))
        {
            refCrop = CropSubjectJpeg(req.ReferenceFramePath, target.Ymin, target.Xmin, target.Ymax, target.Xmax);
            if (refCrop != null)
                consoleLog?.Invoke($"Attached a close-up of '{(string.IsNullOrWhiteSpace(target.Label) ? "the target" : target.Label)}' ({refCrop.Length / 1024} KB) as the visual reference.");
        }

        bool directFrameSampling = !AiVisionProviders.WatchesVideoWithAudio(session.Vendor);
        var chunks = PlanChunks(req.SourceDurationSec);
        var all = new List<RawSubjectWaypoint>();
        (int ymin, int xmin, int ymax, int xmax) box = (target.Ymin, target.Xmin, target.Ymax, target.Xmax);
        double observedAt = Math.Max(0, req.ReferenceTimeInSegmentSec);
        bool boxAnchoredAtChunkStart = false;
        bool useFps = true;

        for (int k = 0; k < chunks.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (cs, ce) = chunks[k];
            double sliceLen = Math.Max(ce - cs, MinSliceSec);
            string part = chunks.Count > 1 ? $" (part {k + 1} of {chunks.Count})" : "";

            byte[]? mp4 = null;
            IReadOnlyList<AiImageInput>? preSampledFrames = null;
            if (directFrameSampling)
            {
                statusProgress?.Report($"Step 3 of 4 · Taking pictures{part}…");
                consoleLog?.Invoke($"Phase 3: Extracting {TrackingFps:0.#} fps JPEG frames directly for {session.VendorName}{part} (skipping MP4 re-encode)...");
                preSampledFrames = await AiFrameSequenceExtractor.SampleFramesAsync(
                    req.VideoPath,
                    req.SourceStartSec + cs,
                    sliceLen,
                    TrackingFps,
                    targetWidth: 640,
                    log: consoleLog,
                    ct: cancellationToken).ConfigureAwait(false);
                if (preSampledFrames.Count == 0)
                    throw new AiProviderException(session.Vendor, AiProviderErrorKind.MalformedResponse,
                        "No pictures could be read from this part of the block, so it was not sent to the AI. Try again or pick a slightly different block.",
                        "frame sampling produced 0 frames");
            }
            else
            {
                statusProgress?.Report($"Step 3 of 4 · Preparing the clip{part}…");
                mp4 = await SliceAsync(req.VideoPath, req.SourceStartSec + cs, sliceLen, consoleLog, cancellationToken).ConfigureAwait(false);
            }

            fraction?.Report((k + 0.25) / chunks.Count);
            double observedInChunk = boxAnchoredAtChunkStart
                ? 0.0
                : Math.Clamp(observedAt - cs, 0.0, sliceLen);
            bool anchorInThisSlice = boxAnchoredAtChunkStart || (observedAt >= cs - 0.05 && observedAt <= ce + 0.05);
            statusProgress?.Report($"Step 3 of 4 · Following '{(string.IsNullOrWhiteSpace(target.Label) ? "the target" : target.Label)}'{part}…");

            List<RawSubjectWaypoint>? waypoints = null;
            for (int partAttempt = 0; waypoints == null; partAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    waypoints = await TrackChunkAsync(session, req.Profile, target.Label, box, observedInChunk, anchorInThisSlice, refCrop, mp4, preSampledFrames, sliceLen, useFps, consoleLog, cancellationToken).ConfigureAwait(false);
                }
                catch (GeminiException ex) when (useFps && !directFrameSampling && AiAnswerGuard.IsFpsRejection(ex))
                {
                    useFps = false;
                    consoleLog?.Invoke($"{session.VendorName} refused the custom frame rate; retrying at its default 1 frame per second.");
                    CoreLogger.Info("AI-Tracking", session.Redact($"fps metadata refused: {ex.Detail}"));
                }
                catch (AiProviderException ex) when (IsPartFailure(ex))
                {
                    CoreLogger.Warn("AI-Tracking", AiDiagnostics.Tag + session.Redact($"Part {k + 1}/{chunks.Count} failed ({ex.Kind}): {ex.Message} {ex.Detail}"));
                    if (partAttempt == 0 && AiAnswerGuard.IsFeatureRetryable(ex))
                    {
                        consoleLog?.Invoke($"Part {k + 1} of {chunks.Count} failed ({ex.Message}) — trying it once more…");
                        continue;
                    }
                    AiPartFailureChoice choice = onPartFailed == null
                        ? AiPartFailureChoice.Stop
                        : await onPartFailed(k + 1, chunks.Count, ex).ConfigureAwait(false);
                    if (choice == AiPartFailureChoice.Retry) continue;
                    if (choice == AiPartFailureChoice.Stop) throw;
                    consoleLog?.Invoke($"Part {k + 1} of {chunks.Count} skipped — the camera stays wide for that stretch.");
                    waypoints = new List<RawSubjectWaypoint>
                    {
                        new() { Time = 0, Visible = false },
                        new() { Time = Math.Round(sliceLen, 3), Visible = false },
                    };
                }
            }

            fraction?.Report((k + 1.0) / chunks.Count);
            int sanitized = SanitizeWaypointsAgainstLocalPlayer(waypoints, k == 0 ? target : null, box, req.Profile, consoleLog);
            if (sanitized > 0) consoleLog?.Invoke($"Ignored {sanitized} point(s) that jumped onto the player's own character.");

            foreach (RawSubjectWaypoint wp in waypoints)
            {
                double t = wp.Time + cs;
                if (t < cs - 0.01 || t > ce + 0.01) continue;
                if (k > 0 && t <= cs + 0.001 && all.Count > 0) continue;
                all.Add(wp with { Time = Math.Round(Math.Clamp(t, 0, req.SourceDurationSec), 3) });
            }

            boxAnchoredAtChunkStart = false;
            for (int i = waypoints.Count - 1; i >= 0; i--)
            {
                var wp = waypoints[i];
                if (!wp.Visible || wp.Cx is not int cx || wp.Cy is not int cy) continue;
                int hw = Math.Max(10, wp.W.GetValueOrDefault(120) / 2);
                int hh = Math.Max(10, wp.H.GetValueOrDefault(260) / 2);
                box = (Math.Clamp(cy - hh, 0, 1000), Math.Clamp(cx - hw, 0, 1000), Math.Clamp(cy + hh, 0, 1000), Math.Clamp(cx + hw, 0, 1000));
                boxAnchoredAtChunkStart = true;
                break;
            }
        }

        if (all.Count == 0)
            throw new GeminiException(GeminiErrorKind.MalformedResponse, $"{session.VendorName} returned no tracking points for this target. Try a clearer segment.");

        cancellationToken.ThrowIfCancellationRequested();
        statusProgress?.Report("Step 4 of 4 · Smoothing the camera path…");
        var trajectory = AiTrajectorySmoother.SmoothTrajectory(
            all, req.SourceDurationSec, req.SourceWidth, req.SourceHeight,
            req.BaseScale, req.MinScale, req.DeadbandPercent, req.AvoidHud, req.PortraitMode, req.HudZones);
        consoleLog?.Invoke($"Built {trajectory.Keyframes.Count} camera keyframes from {all.Count} tracking points.");
        return trajectory;
    }

    internal static bool IsPartFailure(AiProviderException ex)
        => ex.Kind is AiProviderErrorKind.MalformedResponse or AiProviderErrorKind.Blocked or AiProviderErrorKind.Timeout
            or AiProviderErrorKind.Overloaded or AiProviderErrorKind.Network or AiProviderErrorKind.RateLimited
            or AiProviderErrorKind.Truncated or AiProviderErrorKind.BadRequest;

    public const string OneProviderTag = "AIGEMINIONE_01";

    public static string? TrackingMediaResolution { get; set; }

    internal static readonly string GeminiTrackingSchemaText = SchemaText(WriteTrackingSchema);

    private static string SchemaText(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer)) write(w);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public const string TrackingJsonSchema = "{\"type\":\"ARRAY\",\"items\":{\"type\":\"OBJECT\",\"properties\":{\"time\":{\"type\":\"NUMBER\"},\"visible\":{\"type\":\"BOOLEAN\"},\"cx\":{\"type\":\"INTEGER\"},\"cy\":{\"type\":\"INTEGER\"},\"w\":{\"type\":\"INTEGER\"},\"h\":{\"type\":\"INTEGER\"}},\"required\":[\"time\",\"visible\"]}}";

    private static async Task<List<RawSubjectWaypoint>> TrackChunkAsync(
        GeminiModelSession session, AiGameProfile profile, string label,
        (int ymin, int xmin, int ymax, int xmax) box, double observedAt, bool anchorInThisSlice, byte[]? refCrop,
        byte[]? mp4, IReadOnlyList<AiImageInput>? preSampledFrames, double sliceLen, bool useFps, Action<string>? consoleLog, CancellationToken ct)
    {
        double interval = useFps ? 1.0 / TrackingFps : 1.0;
        string prompt = AiTrackingPrompts.Tracking(profile, label, box, observedAt, refCrop != null, interval, sliceLen, anchorInThisSlice);

        string text;
        if (AiVisionProviders.WatchesVideoWithAudio(session.Vendor))
        {
            var refImages = refCrop != null
                ? new[] { new AiImageInput(refCrop, "image/jpeg") }
                : Array.Empty<AiImageInput>();
            var clip = new AiVideoInput(mp4 ?? throw new InvalidOperationException("MP4 slice missing for Gemini tracking."), "video/mp4",
                useFps ? TrackingFps : 0, TrackingMediaResolution, AllowFpsFallback: false);
            text = await session.Provider.GenerateJsonAsync(session.ApiKey, session.Model, prompt, refImages, clip,
                GeminiTrackingSchemaText, null, GeminiCallOptions.Tracking, consoleLog, ct).ConfigureAwait(false);
        }
        else
        {
            var provider = session.Provider;
            var sampledFrames = preSampledFrames
                ?? (mp4 != null
                    ? await AiFrameSequenceExtractor.SampleFramesFromMp4BytesAsync(mp4, TrackingFps, targetWidth: 640, log: consoleLog, ct: ct).ConfigureAwait(false)
                    : Array.Empty<AiImageInput>());
            if (sampledFrames.Count == 0)
                throw new AiProviderException(session.Vendor, AiProviderErrorKind.MalformedResponse,
                    "No pictures could be read from this part of the block, so it was not sent to the AI. Try again or pick a slightly different block.",
                    "frame sampling produced 0 frames");

            var images = new List<AiImageInput>(sampledFrames.Count + (refCrop != null ? 1 : 0));
            if (refCrop != null)
            {
                images.Add(new AiImageInput(refCrop, "image/jpeg", "[Reference Target Crop]"));
            }
            foreach (var frame in sampledFrames) images.Add(frame with { Detail = AiReasoningPolicy.ImageDetail(AiUsage.DenseTracking) });

            text = await provider.GenerateJsonAsync(session.ApiKey, session.Model, prompt, images, null, TrackingJsonSchema, null, GeminiCallOptions.Tracking, consoleLog, ct).ConfigureAwait(false);
        }

        CoreLogger.Info("AI-Tracking", session.Redact($"{session.VendorName} tracking answer ({text.Length} chars): {GeminiClient.Truncate(text, 800)}"));
        consoleLog?.Invoke($"Received {text.Length} characters of tracking data from {session.VendorName}.");
        return ParseWaypoints(text, session.VendorName);
    }

    internal const string DecimalBoxTag = "AIDECIMALBOX_01";

    private static readonly HashSet<string> WaypointIntegerFields = new(StringComparer.Ordinal) { "cx", "cy", "w", "h" };

    private static readonly HashSet<string> DiscoveryIntegerFields = new(StringComparer.Ordinal) { "id", "frame_index", "ymin", "xmin", "ymax", "xmax" };

    internal static string RoundWholeNumberFields(string json, IReadOnlySet<string> fields)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var buffer = new ArrayBufferWriter<byte>(json.Length + 16);
            using (var w = new Utf8JsonWriter(buffer)) WriteRounded(w, doc.RootElement, fields, null);
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
            return json;
        }
    }

    private static void WriteRounded(Utf8JsonWriter w, JsonElement e, IReadOnlySet<string> fields, string? property)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    WriteRounded(w, p.Value, fields, p.Name);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (JsonElement item in e.EnumerateArray()) WriteRounded(w, item, fields, null);
                w.WriteEndArray();
                break;
            case JsonValueKind.Number when property != null && fields.Contains(property) && !e.TryGetInt32(out _) && e.TryGetDouble(out double d) && double.IsFinite(d):
                w.WriteNumberValue((int)Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue));
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    internal static List<RawSubjectWaypoint> ParseWaypoints(string? text, string vendorName = "AI")
    {
        string json = GeminiClient.StripCodeFence(text);
        List<RawSubjectWaypoint>? raw;
        try
        {
            raw = JsonSerializer.Deserialize(RoundWholeNumberFields(json, WaypointIntegerFields), GeminiApiJsonContext.Default.ListRawSubjectWaypoint);
        }
        catch (JsonException ex)
        {
            CoreLogger.Warn("AI-Tracking", AiDiagnostics.Tag + FreeVideoStudio.Core.Diagnostics.SecretRedactor.RedactSecrets(
                $"{vendorName} tracking JSON parse error ({ex.Message}). Raw answer: {GeminiClient.Truncate(text, 800)}"));
            throw new GeminiException(GeminiErrorKind.MalformedResponse,
                $"{vendorName}'s tracking answer could not be read. Please try again.", GeminiClient.Truncate(ex.Message, 200));
        }
        var result = new List<RawSubjectWaypoint>();
        if (raw == null) return result;
        foreach (RawSubjectWaypoint wp in raw)
        {
            if (wp == null || !double.IsFinite(wp.Time) || wp.Time < 0) continue;
            bool visible = wp.Visible && wp.Cx.HasValue && wp.Cy.HasValue;
            result.Add(visible
                ? wp with
                {
                    Cx = Math.Clamp(wp.Cx!.Value, 0, 1000),
                    Cy = Math.Clamp(wp.Cy!.Value, 0, 1000),
                    W = wp.W is int w ? Math.Clamp(w, 1, 1000) : null,
                    H = wp.H is int h ? Math.Clamp(h, 1, 1000) : null,
                }
                : wp with { Visible = false, Cx = null, Cy = null, W = null, H = null });
        }
        return result;
    }

    private static async Task<byte[]> SliceAsync(string videoPath, double startSec, double durationSec, Action<string>? consoleLog, CancellationToken token)
    {
        string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);
        string tempMp4Path = Path.Combine(tempDir, $"ai_track_{Guid.NewGuid():N}.mp4");
        var ci = CultureInfo.InvariantCulture;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in new[]
            {
                "-y", "-ss", startSec.ToString("0.000", ci), "-t", durationSec.ToString("0.000", ci),
                "-i", videoPath, "-vf", "scale=640:-2", "-r", ClipFps.ToString("0.###", ci), "-c:v", "libx264", "-preset", "ultrafast",
                "-crf", "28", "-an", tempMp4Path
            }) psi.ArgumentList.Add(a);

            var (exitCode, _, stderr) = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(tempMp4Path))
                throw new InvalidOperationException($"The segment could not be prepared for the AI (FFmpeg exit {exitCode}): {GeminiClient.Truncate(stderr, 400)}");

            byte[] bytes = await File.ReadAllBytesAsync(tempMp4Path, token).ConfigureAwait(false);
            consoleLog?.Invoke($"Prepared a {durationSec.ToString("0.0", ci)}s preview clip ({bytes.Length / 1024} KB).");
            return bytes;
        }
        finally
        {
            DeleteQuietly(tempMp4Path);
        }
    }

    private static readonly Regex SelfLabel = new(
        @"\b(main player|local player|primary character|self|yourself|protagonist|user character|camera character|own character|my player|my character|player's own|own avatar|first[- ]person (hands|arms|weapon)|held weapon|player'?s (hands|arms|weapon))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HostileLabel = new(
        @"\b(opponent|enemy|enemies|rival|combatant|hostile|attacker|target|foe)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GenericCharacterLabel = new(
        @"^(the\s+)?(player|character|avatar|person|skin|player character|player avatar)$|foreground|closest|back view|from behind|seen from behind",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsMainPlayerCharacter(DiscoveredSubject subject, AiGameProfile profile = AiGameProfiles.Default)
    {
        if (subject == null) return false;
        string label = (subject.Label ?? string.Empty).Trim();
        if (SelfLabel.IsMatch(label)) return true;
        if (!profile.ExcludesThirdPersonAvatar()) return false;
        if (HostileLabel.IsMatch(label)) return false;

        int cx = (subject.Xmin + subject.Xmax) / 2;
        int height = subject.Ymax - subject.Ymin;
        bool foregroundAnchor = subject.Ymax >= 750 && cx >= 250 && cx <= 650 && height >= 300;
        return foregroundAnchor && GenericCharacterLabel.IsMatch(label);
    }

    internal static bool IsInanimateScenery(DiscoveredSubject subject)
    {
        if (subject == null) return false;

        string label = subject.Label.Trim().ToLowerInvariant();

        if (label.Contains("opponent") ||
            label.Contains("player") ||
            label.Contains("enemy") ||
            label.Contains("combatant") ||
            label.Contains("character") ||
            label.Contains("rival") ||
            label.Contains("figure") ||
            label.Contains("glider") ||
            label.Contains("crouch") ||
            label.Contains("jump") ||
            label.Contains("aim") ||
            label.Contains("shoot") ||
            label.Contains("weapon"))
        {
            return false;
        }

        if (label.Contains("building") ||
            label.Contains("structure") ||
            label.Contains("house") ||
            label.Contains("tower") ||
            label.Contains("statue") ||
            label.Contains("monument") ||
            label.Contains("scenery") ||
            label.Contains("landscape") ||
            label.Contains("terrain") ||
            label.Contains("mountain") ||
            label.Contains("hill") ||
            label.Contains("rock") ||
            label.Contains("boulder") ||
            label.Contains("tree") ||
            label.Contains("bush") ||
            label.Contains("wall") ||
            label.Contains("fence") ||
            label.Contains("ramp") ||
            label.Contains("floor") ||
            label.Contains("roof") ||
            label.Contains("pier"))
        {
            return true;
        }

        return false;
    }

    public static byte[]? CropSubjectJpeg(string framePath, int ymin, int xmin, int ymax, int xmax, int paddingPercent = 15)
    {
        if (string.IsNullOrEmpty(framePath) || !File.Exists(framePath)) return null;
        try
        {
            using var original = SKBitmap.Decode(framePath);
            if (original == null) return null;

            int frameW = original.Width;
            int frameH = original.Height;

            int pxMin = (int)Math.Round((xmin / 1000.0) * frameW);
            int pxMax = (int)Math.Round((xmax / 1000.0) * frameW);
            int pyMin = (int)Math.Round((ymin / 1000.0) * frameH);
            int pyMax = (int)Math.Round((ymax / 1000.0) * frameH);

            int targetW = Math.Max(1, pxMax - pxMin);
            int targetH = Math.Max(1, pyMax - pyMin);

            int padX = (int)Math.Round(targetW * (paddingPercent / 100.0));
            int padY = (int)Math.Round(targetH * (paddingPercent / 100.0));

            int cropX = Math.Clamp(pxMin - padX, 0, frameW - 1);
            int cropY = Math.Clamp(pyMin - padY, 0, frameH - 1);
            int cropW = Math.Clamp(pxMax + padX - cropX, 1, frameW - cropX);
            int cropH = Math.Clamp(pyMax + padY - cropY, 1, frameH - cropY);

            using var cropped = new SKBitmap(cropW, cropH);
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.DrawBitmap(original, new SKRect(cropX, cropY, cropX + cropW, cropY + cropH), new SKRect(0, 0, cropW, cropH));
            }

            using var image = SKImage.FromBitmap(cropped);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    internal static int SanitizeWaypointsAgainstLocalPlayer(
        List<RawSubjectWaypoint> waypoints,
        DiscoveredSubject? confirmedTarget,
        (int ymin, int xmin, int ymax, int xmax) initialBox,
        AiGameProfile profile = AiGameProfiles.Default,
        Action<string>? consoleLog = null)
    {
        if (waypoints == null || waypoints.Count == 0 || !profile.ExcludesThirdPersonAvatar()) return 0;

        int initYmin = confirmedTarget?.Ymin ?? initialBox.ymin;
        int initYmax = confirmedTarget?.Ymax ?? initialBox.ymax;
        int initH = initYmax - initYmin;
        if (initH >= 260 && initYmax >= 720) return 0;

        int count = 0;
        for (int i = 0; i < waypoints.Count; i++)
        {
            var wp = waypoints[i];
            if (!wp.Visible || !wp.Cx.HasValue || !wp.Cy.HasValue) continue;

            int cx = wp.Cx.Value;
            int cy = wp.Cy.Value;
            int h = wp.H.GetValueOrDefault(200);
            int ymax = cy + h / 2;

            if (ymax >= 720 && cx >= 240 && cx <= 660 && h >= 260)
            {
                consoleLog?.Invoke($"Point at t={wp.Time.ToString("0.00", CultureInfo.InvariantCulture)}s ({cx},{cy}) looks like the player's own character — holding the previous framing.");
                waypoints[i] = wp with { Visible = false, Cx = null, Cy = null, W = null, H = null };
                count++;
            }
        }
        return count;
    }
}
