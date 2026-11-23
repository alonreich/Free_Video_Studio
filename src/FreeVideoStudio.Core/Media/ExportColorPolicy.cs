using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public sealed record VideoColorInfo(string? PixelFormat, string? Primaries, string? Transfer, string? Matrix, string? Range)
{
    public static readonly VideoColorInfo Unknown = new(null, null, null, null, null);

    public bool IsHdr => Transfer is "smpte2084" or "arib-std-b67";

    public bool IsFullRange =>
        string.Equals(Range, "pc", StringComparison.OrdinalIgnoreCase)
        || (PixelFormat?.StartsWith("yuvj", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool IsBt601Matrix => Matrix is "bt470bg" or "smpte170m";

    public static VideoColorInfo FromStream(JsonNode? stream)
    {
        if (stream is null) return Unknown;
        static string? Read(JsonNode? n)
        {
            string? s = n?.ToString();
            return string.IsNullOrWhiteSpace(s) || s == "unknown" ? null : s;
        }
        return new VideoColorInfo(
            Read(stream["pix_fmt"]),
            Read(stream["color_primaries"]),
            Read(stream["color_transfer"]),
            Read(stream["color_space"]),
            Read(stream["color_range"]));
    }

    public override string ToString()
        => $"pix_fmt={PixelFormat ?? "?"} primaries={Primaries ?? "?"} transfer={Transfer ?? "?"} matrix={Matrix ?? "?"} range={Range ?? "?"}";
}

public static class ExportColorPolicy
{
    public static readonly string[] OutputTagArgs =
        ["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv"];

    public const string HdrToneMapChain =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    public static string? BuildConversionChain(VideoColorInfo info, bool hasZscale, out string description, out string? degradedReason)
    {
        degradedReason = null;

        if (info.IsHdr)
        {
            if (hasZscale)
            {
                description = $"HDR source ({info.Transfer}) tone-mapped to SDR BT.709 (Hable, CPU).";
                return HdrToneMapChain;
            }

            description = $"HDR source ({info.Transfer}) exported WITHOUT tone mapping (bundled FFmpeg lacks zscale).";
            degradedReason =
                "This clip was recorded in HDR, and this build of the video engine cannot convert HDR colours, " +
                "so the export may look washed out. Trimming, speed, music and everything else are unaffected. " +
                "Recording with HDR off in your capture software avoids this.";
            return null;
        }

        if (info.IsFullRange || info.IsBt601Matrix)
        {
            string iall = info.Matrix switch
            {
                "bt470bg" => "bt601-6-625",
                "smpte170m" => "bt601-6-525",
                _ => "bt709",
            };
            string irange = info.IsFullRange ? "pc" : "tv";
            description = $"SDR source ({info}) converted to BT.709 TV range.";
            return $"colorspace=all=bt709:iall={iall}:irange={irange}:range=tv:format=yuv420p";
        }

        description = "SDR BT.709 source: colour passes through untouched (output tagged BT.709/TV).";
        return null;
    }

    private static readonly ConcurrentDictionary<string, Task<HashSet<string>>> FilterCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<bool> HasFilterAsync(string ffmpegPath, string filter)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)) return false;
        HashSet<string> filters = await FilterCache.GetOrAdd(ffmpegPath, ListFiltersAsync).ConfigureAwait(false);
        return filters.Contains(filter);
    }

    private static async Task<HashSet<string>> ListFiltersAsync(string ffmpegPath)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-filters");
            var result = await AsyncProcessRunner.RunAsync(psi, timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            foreach (string raw in result.StandardOutput.Split('\n'))
            {
                string[] parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 3) set.Add(parts[1]);
            }
            CoreLogger.Info("COLOR", $"FFmpeg filter inventory: {set.Count} filters (zscale={(set.Contains("zscale") ? "yes" : "no")}, tonemap={(set.Contains("tonemap") ? "yes" : "no")}).");
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("COLOR", $"Could not list FFmpeg filters ({ex.Message}); HDR tone mapping disabled for this session.");
        }
        return set;
    }
}
