
using System.Text;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// Plans video memory and filtering together. A hardware encoder alone does not
/// imply GPU filtering. Unknown effects retain the original software graph.
/// Audio filters always remain on the CPU and do not download video frames.
/// </summary>
public sealed partial class ExportVideoPipeline
{
    private static readonly HashSet<string> FrameMetadataFilters =
        ["setpts", "fps", "trim", "select", "loop", "split", "null", "nullsink", "setsar", "concat"];
    private static readonly HashSet<string> AudioFilters =
        ["anull", "anullsrc", "atrim", "asetpts", "aresample", "aformat", "atempo", "rubberband", "afade",
         "volume", "asplit", "amix", "adelay", "apad", "loudnorm", "alimiter", "equalizer",
         "highpass", "lowpass", "agate", "acrossover", "sidechaincompress"];

    private ExportVideoPipeline(string encoder, string graph, bool gpu, string reason)
    {
        FilterGraph = graph;
        UsesGpuFrames = gpu;
        DecodeFlags = EncoderManager.GetDecodeFlags(encoder, gpu);
        DeviceFlags = gpu ? ["-init_hw_device", "cuda=exportgpu"] : [];
        DecoderDescription = EncoderManager.DescribeDecoder(encoder, gpu);
        Description = gpu
            ? "Video: GPU decode, GPU filtering, GPU encode; frames stay in VRAM. Audio: CPU."
            : $"Video: CPU decode/filtering, {EncoderManager.DescribeEncoder(encoder)} encode. " +
              $"{reason} Audio: CPU.";
    }

    public string FilterGraph { get; }
    public bool UsesGpuFrames { get; }
    public IReadOnlyList<string> DecodeFlags { get; }
    public IReadOnlyList<string> DeviceFlags { get; }
    public string DecoderDescription { get; }
    public string Description { get; }

    /// <summary>
    /// CUDATAG_01 — FFmpeg 7.1+ negotiates colour space on every filter link. The encoder-side
    /// output tags (<see cref="ExportColorPolicy.OutputTagArgs"/>, passed here as
    /// <paramref name="codecFlags"/>) become a buffersink constraint, and untagged CUDA frames
    /// cannot meet it: FFmpeg inserts a software auto_scale after the last scale_cuda and the
    /// graph fails ("Impossible to convert between the formats supported by the filter
    /// 'Parsed_scale_cuda_N' and the filter 'auto_scale_0'"). The CPU route gets the same
    /// relabel for free from swscale. The resident route therefore stamps the identical
    /// metadata on each decoded input with <c>setparams</c> (a metadata-only filter that
    /// accepts hardware frames), so every link agrees and frames never leave VRAM.
    /// </summary>
    public static ExportVideoPipeline Create(string encoder, string softwareGraph, bool allowGpuFrames = true,
        IReadOnlyList<string>? codecFlags = null)
    {
        if (encoder != "h264_nvenc" || !allowGpuFrames)
            return new(encoder, softwareGraph, false, allowGpuFrames
                ? "No supported resident video-filter route for this encoder."
                : "Using compatibility processing after a GPU pipeline failure.");

        var output = new StringBuilder();
        int start = 0;
        char quote = '\0';
        bool escaped = false;
        for (int i = 0; i <= softwareGraph.Length; i++)
        {
            if (i < softwareGraph.Length)
            {
                char c = softwareGraph[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '\'' or '"') { quote = c; continue; }
                if (c is not (',' or ';')) continue;
            }

            string token = softwareGraph[start..i];
            if (!TryGpuFilter(token, out string replacement, out string reason))
                return new(encoder, softwareGraph, false, reason);
            output.Append(replacement);
            if (i < softwareGraph.Length) output.Append(softwareGraph[i]);
            start = i + 1;
        }
        if (quote != '\0' || escaped)
            return new(encoder, softwareGraph, false, "Unrecognised filter syntax.");
        string? colourTag = ColourTagFilter(codecFlags);
        return new(encoder, colourTag == null ? output.ToString() : TagVideoInputs(output.ToString(), colourTag), true, "");
    }

    /// <summary>CUDATAG_01 — the setparams equivalent of the encoder's colour flags, or null when there are none.</summary>
    private static string? ColourTagFilter(IReadOnlyList<string>? codecFlags)
    {
        if (codecFlags == null) return null;
        var parts = new List<string>();
        foreach (var (flag, option) in new[] { ("-color_range", "range"), ("-color_primaries", "color_primaries"),
                     ("-color_trc", "color_trc"), ("-colorspace", "colorspace") })
        {
            int i = -1;
            for (int k = 0; k < codecFlags.Count - 1; k++)
                if (codecFlags[k] == flag) i = k;
            if (i >= 0 && SafeValue().IsMatch(codecFlags[i + 1])) parts.Add($"{option}={codecFlags[i + 1]}");
        }
        return parts.Count == 0 ? null : "setparams=" + string.Join(':', parts);
    }

    /// <summary>
    /// CUDATAG_01 — every decoded video input pad (<c>[N:v]</c>, <c>[N:v:M]</c>) is routed through
    /// one setparams chain before its first consumer. Labels are only rewritten outside quotes.
    /// </summary>
    private static string TagVideoInputs(string graph, string colourTag)
    {
        var prefix = new StringBuilder();
        var body = new StringBuilder(graph.Length);
        var seen = new Dictionary<string, int>();
        char quote = '\0';
        bool escaped = false;
        for (int i = 0; i < graph.Length; i++)
        {
            char c = graph[i];
            if (escaped) { escaped = false; body.Append(c); continue; }
            if (c == '\\') { escaped = true; body.Append(c); continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; body.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; body.Append(c); continue; }
            if (c == '[')
            {
                int end = graph.IndexOf(']', i);
                if (end > i && InputVideoLabel().IsMatch(graph[(i + 1)..end]))
                {
                    string label = graph[(i + 1)..end];
                    seen[label] = seen.TryGetValue(label, out int n) ? n + 1 : 0;
                    string tagged = $"fvs_cudatag_{label.Replace(':', '_')}_{seen[label]}";
                    prefix.Append('[').Append(label).Append(']').Append(colourTag).Append('[').Append(tagged).Append("];");
                    body.Append('[').Append(tagged).Append(']');
                    i = end;
                    continue;
                }
            }
            body.Append(c);
        }
        return prefix.Append(body).ToString();
    }

    private static bool TryGpuFilter(string token, out string replacement, out string reason)
    {
        replacement = token;
        reason = "Unrecognised filter syntax.";
        int start = 0;
        while (start < token.Length && char.IsWhiteSpace(token[start])) start++;
        while (start < token.Length && token[start] == '[')
        {
            int end = token.IndexOf(']', start);
            if (end < 0) return false;
            start = end + 1;
        }
        int nameEnd = start;
        while (nameEnd < token.Length && (char.IsAsciiLetterOrDigit(token[nameEnd]) || token[nameEnd] == '_')) nameEnd++;
        string name = token[start..nameEnd];
        if (FrameMetadataFilters.Contains(name) || AudioFilters.Contains(name)) return true;

        int labels = token.IndexOf('[', nameEnd);
        if (labels < 0) labels = token.Length;
        string filter = token[start..labels];
        string? gpuFilter = null;
        if (filter == "format=yuv420p")
            gpuFilter = "scale_cuda=format=yuv420p:passthrough=0";
        else if (name == "scale")
        {
            var match = FixedScale().Match(filter);
            if (match.Success)
                gpuFilter = $"scale_cuda={match.Groups[1].Value}:{match.Groups[2].Value}:interp_algo=lanczos:format=yuv420p:passthrough=0";
        }
        if (gpuFilter == null)
        {
            reason = $"Effect '{name}' needs CPU processing; no GPU-to-RAM decode round trip is used.";
            return false;
        }
        replacement = token[..start] + gpuFilter + token[labels..];
        return true;
    }

    public void ApplyCodecFlags(List<string> codecFlags)
    {
        if (!UsesGpuFrames) return;
        int pixelFormat = codecFlags.IndexOf("-pix_fmt");
        if (pixelFormat >= 0 && pixelFormat + 1 < codecFlags.Count)
            codecFlags[pixelFormat + 1] = "cuda";
    }

    [GeneratedRegex(@"^scale=(\d+):(\d+):flags=lanczos$", RegexOptions.CultureInvariant)]
    private static partial Regex FixedScale();

    [GeneratedRegex(@"^\d+:v(:\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex InputVideoLabel();

    [GeneratedRegex(@"^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeValue();
}
