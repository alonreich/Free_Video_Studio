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

#region Gemini JSON Models for Native AOT

/// <summary>One subject Gemini found in the discovery stills (0-1000 box on the frame named by <see cref="FrameIndex"/>).</summary>
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

    /// <summary>AIOWNBOX_01 — true for the box the user drew themselves (no AI discovery behind it).</summary>
    [JsonIgnore]
    public bool IsUserBox { get; set; }
}

[JsonSerializable(typeof(List<RawSubjectWaypoint>))]
[JsonSerializable(typeof(DiscoveredSubject))]
[JsonSerializable(typeof(List<DiscoveredSubject>))]
internal partial class GeminiApiJsonContext : JsonSerializerContext { }

#endregion

/// <summary>Discovery outcome: the subjects, the three stills they were found on, and where those stills sit.</summary>
/// <param name="LookaheadOffsetSec">0 when the stills are inside the segment; otherwise how far past the
/// segment START the forward probe that found them begins (AILOOKAHEAD_01).</param>
public sealed record AiDiscoveryResult(
    List<DiscoveredSubject> Subjects,
    string F1, string F2, string F3,
    double T1, double T2, double T3,
    double LookaheadOffsetSec);

/// <summary>Everything one AI SMART ZOOM tracking run needs. Times are SOURCE seconds of <see cref="VideoPath"/>.</summary>
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

/// <summary>
/// GEMINICLIENT_01 — one AI SMART ZOOM run's connection to Gemini: the key, the model, and the
/// AIMODELFALLBACK_01 rule — when the configured model is retired (HTTP 404) the newest available
/// "flash" model is used for the rest of THIS run, the user is told, and their setting is not changed.
/// AIPROVIDER_ROUTING_01 — routes U1 discovery and U2 dense tracking through configured IAiVisionProvider.
/// </summary>
public sealed class GeminiModelSession
{
    private readonly GeminiClient _client;
    private readonly string _apiKey;
    private readonly Action<string>? _log;
    private bool _fallbackTried;

    public AiVendor Vendor { get; }
    public IAiVisionProvider? Provider { get; }
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
        GeminiClient? client = null,
        AiVendor vendor = AiVendor.Google,
        IAiVisionProvider? provider = null)
    {
        _client = client ?? GeminiClient.Shared;
        _apiKey = apiKey ?? string.Empty;
        _log = log;
        Vendor = vendor;
        Provider = provider ?? (vendor != AiVendor.Google ? AiVisionProviders.Get(vendor) : null);
        Model = vendor switch
        {
            AiVendor.Anthropic => ClaudeProvider.ResolveModel(model),
            AiVendor.OpenAI => OpenAiProvider.ResolveModel(model),
            _ => AiVisionProviders.EffectiveModel(AiVendor.Google, GeminiClient.ResolveModel(model)),
        };
    }

    public string Model { get; private set; }

    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>Sanitises text bound for a log with THIS session's key.</summary>
    public string Redact(string? text) => GeminiClient.Redact(text, _apiKey);

    public async Task<string> GenerateAsync(byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GenerateAsync(_apiKey, Model, body, GeminiCallOptions.Tracking, _log, cancellationToken).ConfigureAwait(false);
        }
        catch (GeminiException ex) when (ex.Kind == GeminiErrorKind.ModelNotFound && !_fallbackTried)
        {
            _fallbackTried = true;
            IReadOnlyList<string> available;
            try
            {
                available = await _client.ListModelsAsync(_apiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (GeminiException listEx)
            {
                CoreLogger.Info("AI-Tracking", Redact($"Model list unavailable after 404: {listEx.Kind} {listEx.Detail}"));
                throw ex;
            }
            string? fallback = GeminiClient.PickFallbackModel(available, Model);
            if (fallback == null) throw;
            _log?.Invoke($"Model '{Model}' is not available for your key — using '{fallback}' for this run. Choose a model in Settings › AI Features to make this permanent.");
            Model = fallback;
            return await _client.GenerateAsync(_apiKey, Model, body, GeminiCallOptions.Tracking, _log, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Universal AI Subject Tracking using Google Gemini vision (docs/01 TL-AITRACKING, docs/04 UI-AIZOOM):
/// candidate still extraction, multi-angle subject discovery, chunked dense tracking and smoothing.
/// All transport goes through <see cref="GeminiClient"/> (header-only key, per-attempt timeout,
/// classified errors). Nothing here touches the UI.
/// </summary>
public static class GeminiTrackingService
{
    /// <summary>AICHUNK_01 — the longest video clip sent in ONE request.</summary>
    public const double MaxChunkSec = 8.0;

    /// <summary>AICHUNK_01 — the longest segment AI SMART ZOOM tracks (5 requests). Longer ones are refused with a plain reason.</summary>
    public const double MaxTrackSec = 40.0;

    /// <summary>Shortest clip sent; waypoints past the real segment end are dropped by the smoother (AITIME_01).</summary>
    public const double MinSliceSec = 1.0;

    /// <summary>AIFPS_01 — requested sampling rate for the tracking clip (Gemini's default is 1 frame per second).</summary>
    public const double TrackingFps = 2.0;

    /// <summary>Verifies a key with ONE tiny request against <paramref name="modelName"/>. Never throws.</summary>
    public static Task<(bool Success, string Message)> TestApiKeyAsync(
        string apiKey, string? modelName = null, CancellationToken cancellationToken = default)
        => GeminiClient.Shared.TestKeyAsync(apiKey, modelName, cancellationToken);

    /// <summary>
    /// Extracts 3 candidate still frames from start, middle, and end of the segment for multi-angle discovery.
    /// </summary>
    public static async Task<(string F1, string F2, string F3, double T1, double T2, double T3)> ExtractThreeCandidateFramesAsync(
        string videoPath,
        double sourceStartSec,
        double sourceDurationSec,
        CancellationToken cancellationToken = default)
    {
        string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);

        double t1 = sourceStartSec + Math.Max(0.05, 0.15 * sourceDurationSec);
        double t2 = sourceStartSec + 0.50 * sourceDurationSec;
        double t3 = sourceStartSec + Math.Min(sourceDurationSec - 0.05, 0.85 * sourceDurationSec);

        string f1Path = Path.Combine(tempDir, $"cand_f1_{Guid.NewGuid():N}.jpg");
        string f2Path = Path.Combine(tempDir, $"cand_f2_{Guid.NewGuid():N}.jpg");
        string f3Path = Path.Combine(tempDir, $"cand_f3_{Guid.NewGuid():N}.jpg");

        await ExtractStillAsync(videoPath, t1, f1Path, cancellationToken).ConfigureAwait(false);
        await ExtractStillAsync(videoPath, t2, f2Path, cancellationToken).ConfigureAwait(false);
        await ExtractStillAsync(videoPath, t3, f3Path, cancellationToken).ConfigureAwait(false);

        return (f1Path, f2Path, f3Path, t1, t2, t3);
    }

    /// <summary>
    /// LIBAVFRAME_05 — one candidate still at <paramref name="sec"/>, written to <paramref name="outPath"/> as JPEG.
    /// Native libav first (<see cref="VideoFrameGrabber"/>: the frame <c>ffmpeg -ss {sec:0.000} -i … -frames:v 1</c>
    /// would output, full size, encoded with SkiaSharp at <see cref="CandidateJpegQuality"/>), the ORIGINAL
    /// ffmpeg subprocess below once if native cannot answer. Timestamps, file names, the 10 s budget,
    /// cancellation and "no frame → no file" are unchanged; FVS_FRAME_DECODE=ffmpeg|native forces one path.
    /// </summary>
    private static async Task ExtractStillAsync(string videoPath, double sec, string outPath, CancellationToken token)
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

    /// <summary>
    /// ffmpeg's mjpeg encoder at <c>-q:v 2</c> is close to the top of the JPEG quality scale; 95 is the
    /// SkiaSharp setting that matches it within the parity tolerance (docs/03 FFM-LIBAVFRAME).
    /// </summary>
    internal const int CandidateJpegQuality = 95;

    /// <summary>Managed BGRA frame → JPEG bytes (SkiaSharp, the app's existing imaging library). Copies the pixels.</summary>
    internal static byte[] EncodeCandidateJpeg(DecodedVideoFrame frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using SKData pixels = SKData.CreateCopy(frame.Pixels);
        using SKImage image = SKImage.FromPixels(info, pixels, frame.Stride)
                              ?? throw new IOException("Could not wrap the decoded frame.");
        using SKData jpeg = image.Encode(SKEncodedImageFormat.Jpeg, CandidateJpegQuality)
                            ?? throw new IOException("Could not encode the decoded frame.");
        return jpeg.ToArray();
    }

    /// <summary>The pre-LIBAVFRAME subprocess path, unchanged: ffmpeg -y -ss T -i video -frames:v 1 -q:v 2 out.jpg.</summary>
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
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add(outPath);

        await AsyncProcessRunner.RunAsync(psi, budget, token).ConfigureAwait(false);
    }

    /// <summary>Width/height of a JPEG on disk (the full-size candidate still), or (0,0).</summary>
    public static (int Width, int Height) ReadImageSize(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return (0, 0);
        try
        {
            using var codec = SKCodec.Create(path);
            return codec == null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return (0, 0);
        }
    }


    /// <summary>B4 — generateContent body: prompt, media parts, temperature 0 (omitted for Gemini 3+, AITEMP_02), JSON MIME type and a response schema.</summary>
    internal static byte[] BuildRequest(string prompt, IReadOnlyList<(string MimeType, byte[] Data, double? Fps)> media, Action<Utf8JsonWriter> writeSchema, string? model = null)
    {
        int size = 4096 + prompt.Length * 2;
        foreach (var m in media) size += m.Data.Length * 4 / 3 + 64;
        var buffer = new ArrayBufferWriter<byte>(size);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteStartArray("parts");
            w.WriteStartObject();
            w.WriteString("text", prompt);
            w.WriteEndObject();
            foreach (var m in media)
            {
                w.WriteStartObject();
                w.WriteStartObject("inlineData");
                w.WriteString("mimeType", m.MimeType);
                w.WriteBase64String("data", m.Data);
                w.WriteEndObject();
                if (m.Fps is double fps)
                {
                    w.WriteStartObject("videoMetadata");
                    w.WriteNumber("fps", fps);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            if (!GeminiClient.PrefersDefaultTemperature(model)) w.WriteNumber("temperature", 0);
            w.WriteString("responseMimeType", "application/json");
            w.WritePropertyName("responseSchema");
            writeSchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
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

    internal static void WriteDiscoverySchema(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "ARRAY");
        w.WriteStartObject("items");
        w.WriteString("type", "OBJECT");
        w.WriteStartObject("properties");
        Typed(w, "id", "INTEGER");
        Typed(w, "label", "STRING");
        Typed(w, "frame_index", "INTEGER");
        Typed(w, "ymin", "INTEGER");
        Typed(w, "xmin", "INTEGER");
        Typed(w, "ymax", "INTEGER");
        Typed(w, "xmax", "INTEGER");
        w.WriteEndObject();
        Required(w, "id", "label", "frame_index", "ymin", "xmin", "ymax", "xmax");
        w.WriteEndObject();
        w.WriteEndObject();
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

    /// <summary>Queries the configured AI provider with 3 stills to discover subjects worth following (profile-aware exclusions applied).</summary>
    public static async Task<List<DiscoveredSubject>> DiscoverSubjectsAsync(
        string frame1Path, string frame2Path, string frame3Path,
        GeminiModelSession session, AiGameProfile profile,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default)
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

        string text;
        if (session.Vendor == AiVendor.Google && session.Provider == null)
        {
            byte[] body = BuildRequest(AiTrackingPrompts.Discovery(profile), media, WriteDiscoverySchema, session.Model);
            text = await session.GenerateAsync(body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var provider = session.Provider ?? AiVisionProviders.Get(session.Vendor);
            var images = new List<AiImageInput>(3);
            for (int i = 0; i < media.Count; i++)
            {
                images.Add(new AiImageInput(media[i].Item2, "image/jpeg", $"[Candidate Still {i + 1}]"));
            }
            string prompt = AiTrackingPrompts.Discovery(profile);
            text = await provider.GenerateJsonAsync(session.ApiKey, session.Model, prompt, images, null, DiscoveryJsonSchema, null, GeminiCallOptions.Tracking, consoleLog, cancellationToken).ConfigureAwait(false);
        }
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

    /// <summary>Parses and validates a discovery answer. Malformed JSON throws <see cref="GeminiException"/> (never silently "no targets").</summary>
    internal static List<DiscoveredSubject> ParseDiscovery(string? text, string vendorName = "AI")
    {
        string json = GeminiClient.StripCodeFence(text);
        List<DiscoveredSubject>? subjects;
        try
        {
            subjects = JsonSerializer.Deserialize(json, GeminiApiJsonContext.Default.ListDiscoveredSubject);
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

    /// <summary>
    /// Discovery with AILOOKAHEAD_01 forward probes: when the segment's own stills show nobody, up to two
    /// probes further into the video are tried. <paramref name="sourceEndLimitSec"/> is the last usable
    /// SOURCE second of THIS file (trim end, or the merge clip's kept end) — never another clip's time.
    /// </summary>
    public static async Task<AiDiscoveryResult> DiscoverSubjectsWithLookaheadAsync(
        string videoPath,
        double sourceStartSec,
        double sourceDurationSec,
        double sourceEndLimitSec,
        GeminiModelSession session,
        AiGameProfile profile,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default)
    {
        var (f1, f2, f3, t1, t2, t3) = await ExtractThreeCandidateFramesAsync(
            videoPath, sourceStartSec, sourceDurationSec, cancellationToken).ConfigureAwait(false);

        var subjects = await DiscoverSubjectsAsync(f1, f2, f3, session, profile, consoleLog, cancellationToken).ConfigureAwait(false);
        if (subjects.Count > 0)
            return new AiDiscoveryResult(subjects, f1, f2, f3, t1, t2, t3, 0);

        double[] lookaheadDeltas =
        {
            Math.Max(2.0, sourceDurationSec * 0.75),
            Math.Max(4.0, sourceDurationSec * 1.50)
        };

        foreach (double delta in lookaheadDeltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double probeStart = sourceStartSec + delta;
            double probeDuration = sourceDurationSec;
            if (sourceEndLimitSec > 0)
            {
                if (probeStart + 0.5 >= sourceEndLimitSec) break;
                probeDuration = Math.Min(probeDuration, sourceEndLimitSec - probeStart);
            }

            consoleLog?.Invoke($"Nobody found in the segment. Looking {delta.ToString("0.0", CultureInfo.InvariantCulture)}s further into the video…");
            string pf1 = string.Empty, pf2 = string.Empty, pf3 = string.Empty;
            try
            {
                var (n1, n2, n3, pt1, pt2, pt3) = await ExtractThreeCandidateFramesAsync(
                    videoPath, probeStart, probeDuration, cancellationToken).ConfigureAwait(false);
                pf1 = n1; pf2 = n2; pf3 = n3;

                var probeSubjects = await DiscoverSubjectsAsync(pf1, pf2, pf3, session, profile, consoleLog, cancellationToken).ConfigureAwait(false);
                if (probeSubjects.Count > 0)
                {
                    consoleLog?.Invoke($"Found {probeSubjects.Count} target(s) {delta.ToString("0.0", CultureInfo.InvariantCulture)}s after the segment start.");
                    DeleteQuietly(f1); DeleteQuietly(f2); DeleteQuietly(f3);
                    return new AiDiscoveryResult(probeSubjects, pf1, pf2, pf3, pt1, pt2, pt3, delta);
                }
                DeleteQuietly(pf1); DeleteQuietly(pf2); DeleteQuietly(pf3);
            }
            catch (OperationCanceledException)
            {
                DeleteQuietly(pf1); DeleteQuietly(pf2); DeleteQuietly(pf3);
                throw;
            }
            catch (AiProviderException ex) when (!ex.IsTransient && ex.Kind is not AiProviderErrorKind.MalformedResponse)
            {
                DeleteQuietly(pf1); DeleteQuietly(pf2); DeleteQuietly(pf3);
                throw;
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
                DeleteQuietly(pf1); DeleteQuietly(pf2); DeleteQuietly(pf3);
            }
        }

        return new AiDiscoveryResult(new List<DiscoveredSubject>(), f1, f2, f3, t1, t2, t3, 0);
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


    /// <summary>AICHUNK_01 — how a segment of <paramref name="durationSec"/> is split: even chunks of at most <see cref="MaxChunkSec"/>.</summary>
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

    /// <summary>
    /// Tracks <see cref="AiTrackingRequest.Target"/> through the WHOLE segment (AICHUNK_01: one request
    /// per chunk of at most 8 s, the identity carried from chunk to chunk) and returns the smoothed
    /// trajectory, raw waypoints included (AIRESMOOTH_01).
    /// </summary>
    public static async Task<AiTrajectorySmoother.SmoothedTrajectory> TrackSubjectAsync(
        AiTrackingRequest req,
        GeminiModelSession session,
        IProgress<string>? statusProgress = null,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(session);
        if (!File.Exists(req.VideoPath)) throw new FileNotFoundException("Video file not found", req.VideoPath);
        if (!session.HasKey) throw new GeminiException(GeminiErrorKind.InvalidKey, $"No {session.VendorName} API key is configured. Add one in Settings › AI Features.");
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

        var chunks = PlanChunks(req.SourceDurationSec);
        var all = new List<RawSubjectWaypoint>();
        (int ymin, int xmin, int ymax, int xmax) box = (target.Ymin, target.Xmin, target.Ymax, target.Xmax);
        double observedAt = Math.Max(0, req.ReferenceTimeInSegmentSec);
        bool useFps = true;

        for (int k = 0; k < chunks.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (cs, ce) = chunks[k];
            double sliceLen = Math.Max(ce - cs, MinSliceSec);
            string part = chunks.Count > 1 ? $" (part {k + 1} of {chunks.Count})" : "";

            statusProgress?.Report($"Phase 3: Preparing the clip{part}…");
            byte[] mp4 = await SliceAsync(req.VideoPath, req.SourceStartSec + cs, sliceLen, consoleLog, cancellationToken).ConfigureAwait(false);

            double observedInChunk = k == 0 ? Math.Clamp(observedAt - cs, 0, sliceLen) : 0;
            statusProgress?.Report($"Phase 3: Tracking '{(string.IsNullOrWhiteSpace(target.Label) ? "target" : target.Label)}'{part}…");

            List<RawSubjectWaypoint> waypoints;
            try
            {
                waypoints = await TrackChunkAsync(session, req.Profile, target.Label, box, observedInChunk, refCrop, mp4, sliceLen, useFps, consoleLog, cancellationToken).ConfigureAwait(false);
            }
            catch (GeminiException ex) when (useFps && ex.Kind == GeminiErrorKind.BadRequest)
            {
                useFps = false;
                consoleLog?.Invoke($"{session.VendorName} refused the custom frame rate; retrying at its default 1 frame per second.");
                CoreLogger.Info("AI-Tracking", session.Redact($"fps metadata refused: {ex.Detail}"));
                waypoints = await TrackChunkAsync(session, req.Profile, target.Label, box, observedInChunk, refCrop, mp4, sliceLen, useFps, consoleLog, cancellationToken).ConfigureAwait(false);
            }

            int sanitized = SanitizeWaypointsAgainstLocalPlayer(waypoints, k == 0 ? target : null, box, req.Profile, consoleLog);
            if (sanitized > 0) consoleLog?.Invoke($"Ignored {sanitized} point(s) that jumped onto the player's own character.");

            foreach (RawSubjectWaypoint wp in waypoints)
            {
                double t = wp.Time + cs;
                if (t < cs - 0.01 || t > ce + 0.01) continue;
                if (k > 0 && t <= cs + 0.001 && all.Count > 0) continue;
                all.Add(wp with { Time = Math.Round(Math.Clamp(t, 0, req.SourceDurationSec), 3) });
            }

            for (int i = waypoints.Count - 1; i >= 0; i--)
            {
                var wp = waypoints[i];
                if (!wp.Visible || wp.Cx is not int cx || wp.Cy is not int cy) continue;
                int hw = Math.Max(10, wp.W.GetValueOrDefault(120) / 2);
                int hh = Math.Max(10, wp.H.GetValueOrDefault(260) / 2);
                box = (Math.Clamp(cy - hh, 0, 1000), Math.Clamp(cx - hw, 0, 1000), Math.Clamp(cy + hh, 0, 1000), Math.Clamp(cx + hw, 0, 1000));
                break;
            }
        }

        if (all.Count == 0)
            throw new GeminiException(GeminiErrorKind.MalformedResponse, $"{session.VendorName} returned no tracking points for this target. Try a clearer segment.");

        cancellationToken.ThrowIfCancellationRequested();
        statusProgress?.Report("Phase 4: Smoothing the camera path…");
        var trajectory = AiTrajectorySmoother.SmoothTrajectory(
            all, req.SourceDurationSec, req.SourceWidth, req.SourceHeight,
            req.BaseScale, req.MinScale, req.DeadbandPercent, req.AvoidHud, req.PortraitMode, req.HudZones);
        consoleLog?.Invoke($"Built {trajectory.Keyframes.Count} camera keyframes from {all.Count} tracking points.");
        return trajectory;
    }

    public const string TrackingJsonSchema = "{\"type\":\"ARRAY\",\"items\":{\"type\":\"OBJECT\",\"properties\":{\"time\":{\"type\":\"NUMBER\"},\"visible\":{\"type\":\"BOOLEAN\"},\"cx\":{\"type\":\"INTEGER\"},\"cy\":{\"type\":\"INTEGER\"},\"w\":{\"type\":\"INTEGER\"},\"h\":{\"type\":\"INTEGER\"}},\"required\":[\"time\",\"visible\"]}}";

    private static async Task<List<RawSubjectWaypoint>> TrackChunkAsync(
        GeminiModelSession session, AiGameProfile profile, string label,
        (int ymin, int xmin, int ymax, int xmax) box, double observedAt, byte[]? refCrop,
        byte[] mp4, double sliceLen, bool useFps, Action<string>? consoleLog, CancellationToken ct)
    {
        double interval = useFps ? 1.0 / TrackingFps : 1.0;
        string prompt = AiTrackingPrompts.Tracking(profile, label, box, observedAt, refCrop != null, interval, sliceLen);

        string text;
        if (session.Vendor == AiVendor.Google && session.Provider == null)
        {
            var media = new List<(string, byte[], double?)>(2);
            if (refCrop != null) media.Add(("image/jpeg", refCrop, null));
            media.Add(("video/mp4", mp4, useFps ? TrackingFps : null));

            byte[] body = BuildRequest(prompt, media, WriteTrackingSchema, session.Model);
            text = await session.GenerateAsync(body, ct).ConfigureAwait(false);
        }
        else
        {
            var provider = session.Provider ?? AiVisionProviders.Get(session.Vendor);
            var sampledFrames = await AiFrameSequenceExtractor.SampleFramesFromMp4BytesAsync(
                mp4, TrackingFps, targetWidth: 640, log: consoleLog, ct: ct).ConfigureAwait(false);
            if (sampledFrames.Count == 0)
                throw new AiProviderException(session.Vendor, AiProviderErrorKind.MalformedResponse,
                    "No pictures could be read from this part of the block, so it was not sent to the AI. Try again or pick a slightly different block.",
                    "frame sampling produced 0 frames");

            var images = new List<AiImageInput>(sampledFrames.Count + (refCrop != null ? 1 : 0));
            if (refCrop != null)
            {
                images.Add(new AiImageInput(refCrop, "image/jpeg", "[Reference Target Crop]"));
            }
            images.AddRange(sampledFrames);

            text = await provider.GenerateJsonAsync(session.ApiKey, session.Model, prompt, images, null, TrackingJsonSchema, null, GeminiCallOptions.Tracking, consoleLog, ct).ConfigureAwait(false);
        }

        CoreLogger.Info("AI-Tracking", session.Redact($"{session.VendorName} tracking answer ({text.Length} chars): {GeminiClient.Truncate(text, 800)}"));
        consoleLog?.Invoke($"Received {text.Length} characters of tracking data.");
        return ParseWaypoints(text, session.VendorName);
    }

    /// <summary>Parses + validates a tracking answer: finite times, coordinates clamped to 0..1000.</summary>
    internal static List<RawSubjectWaypoint> ParseWaypoints(string? text, string vendorName = "AI")
    {
        string json = GeminiClient.StripCodeFence(text);
        List<RawSubjectWaypoint>? raw;
        try
        {
            raw = JsonSerializer.Deserialize(json, GeminiApiJsonContext.Default.ListRawSubjectWaypoint);
        }
        catch (JsonException ex)
        {
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

    /// <summary>The 640-wide, audio-free, ultrafast H.264 slice sent to Gemini (unchanged encoding).</summary>
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
                "-i", videoPath, "-vf", "scale=640:-2", "-c:v", "libx264", "-preset", "ultrafast",
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

    /// <summary>
    /// AIEXCLUDE_02 (docs/01 TL-AITRACKING) — is this candidate the player's OWN character?
    /// <list type="number">
    /// <item>A self-describing label (word match, so "shelf" is not "self") — every profile.</item>
    /// <item>The third-person position anchor (Ymax ≥ 750, 250 ≤ Cx ≤ 650, H ≥ 300) — ONLY for profiles that
    /// exclude the avatar, ONLY when the label is generic ("player", "character", "foreground"…), and NEVER
    /// when the label calls it an opponent. The old unconditional "large and bottom-centre ⇒ self" rule
    /// threw away close-range enemies, which are the best highlights.</item>
    /// </list>
    /// </summary>
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

    /// <summary>
    /// Evaluates if a detected candidate is scenery, landscape, buildings, or inanimate geometry
    /// rather than an active character, combatant, or player vehicle.
    /// </summary>
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

    /// <summary>
    /// Crops a candidate subject bounding box from a candidate frame using SkiaSharp with padding margin.
    /// Returns a high-quality JPEG byte array.
    /// </summary>
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

    /// <summary>
    /// Defensive sanitiser (third-person profiles only): suppresses waypoints that jump onto the player's
    /// own avatar position (Ymax ≥ 720, 240 ≤ Cx ≤ 660, H ≥ 260) when the confirmed target did not start
    /// there. Suppressed points become invisible, so the smoother's inertial hold carries the camera.
    /// </summary>
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
