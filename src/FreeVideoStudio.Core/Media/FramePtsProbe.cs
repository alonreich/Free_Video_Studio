using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public static class FramePtsProbe
{
    private const int PacketMargin = 16;

    public static async Task<IReadOnlyList<long>> ProbeAsync(string ffprobePath, string videoPath, int frames)
    {
        if (frames <= 0) return Array.Empty<long>();
        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "frame=best_effort_timestamp_time:format=start_time",
            "-of", "json",
            "-read_intervals", "%+#" + (frames + PacketMargin).ToString(CultureInfo.InvariantCulture),
            videoPath,
        };
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        try
        {
            var result = await AsyncProcessRunner.RunAsync(psi, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                CoreLogger.Fail("FramePts", $"ffprobe exit {result.ExitCode}: {result.StandardError}");
                return Array.Empty<long>();
            }
            return Parse(result.StandardOutput);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return Array.Empty<long>();
        }
    }

    public static IReadOnlyList<long> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<long>();
        try
        {
            var root = JsonNode.Parse(json);
            double start = ParseSec(root?["format"]?["start_time"]) ?? 0;
            var list = new List<long>();
            if (root?["frames"] is JsonArray arr)
            {
                foreach (var f in arr)
                {
                    if (ParseSec(f?["best_effort_timestamp_time"]) is double t)
                        list.Add((long)Math.Round((t - start) * 1_000_000.0));
                }
            }
            list.Sort();
            return list;
        }
        catch (JsonException) { return Array.Empty<long>(); }
    }

    public static long? IntroCutUs(IReadOnlyList<long> pts, int introFrames)
    {
        if (introFrames <= 0) return 0;
        if (pts.Count <= introFrames) return null;
        return Math.Max(0, pts[introFrames]);
    }

    private static double? ParseSec(JsonNode? node)
    {
        if (node is null) return null;
        string s = node.ToString();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
    }
}
