using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public class MediaProber
{
    private readonly string _ffprobePath;
    private readonly string _videoPath;
    private readonly MediaProbeBackend _backend;
    private JsonObject? _probeData;
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    public MediaProber(string ffprobePath, string videoPath)
        : this(ffprobePath, videoPath, MediaProbeBackend.Auto)
    {
    }

    public MediaProber(string ffprobePath, string videoPath, MediaProbeBackend backend)
    {
        _ffprobePath = ffprobePath;
        _videoPath = videoPath;
        _backend = backend;
    }

    public string? AnsweredBy { get; private set; }

    public async Task<JsonObject> ProbeAsync(CancellationToken ct = default)
    {
        if (_probeData != null) return _probeData;
        await _probeLock.WaitAsync(ct);
        try
        {
            if (_probeData != null) return _probeData;

            try
            {
                var result = await MediaMetadataProbe.ProbeAsync(_ffprobePath, _videoPath, TimeSpan.FromSeconds(15), ct, _backend);
                if (result.Ok)
                {
                    _probeData = result.Data;
                    AnsweredBy = result.Backend;
                    return _probeData ?? new JsonObject();
                }

                CoreLogger.Fail("FFprobe", $"{result.Backend}: {result.Error}");
                return new JsonObject();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                CoreLogger.Fail("FFprobe", "FFprobe timed out after 15 seconds.");
                return new JsonObject();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Exception swallowed)
        {
            _probeData = new JsonObject();
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
        }
        finally
        {
            _probeLock.Release();
        }
        return _probeData;
    }

    public async Task<double> GetDurationAsync(CancellationToken ct = default)
    {
        var data = await ProbeAsync(ct);
        var format = data["format"]?.AsObject();
        if (format != null && format["duration"] != null)
        {
            if (double.TryParse(format["duration"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
                return dur;
        }
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "video")
                {
                    var durNode = stream["duration"];
                    if (durNode != null && double.TryParse(durNode.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
                        return dur;
                }
            }
        }
        return 0;
    }

    public async Task<(int width, int height)> GetResolutionAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "video")
                {
                    int w = ParseInt(stream["width"]);
                    int h = ParseInt(stream["height"]);
                    if (w > 0 && h > 0) return (w, h);
                }
            }
        }
        return (0, 0);
    }

    public async Task<string> GetResolutionStringAsync()
    {
        var (w, h) = await GetResolutionAsync();
        return $"{w}x{h}";
    }

    public async Task<VideoColorInfo> GetVideoColorInfoAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "video") return VideoColorInfo.FromStream(stream);
            }
        }
        return VideoColorInfo.Unknown;
    }

    public async Task<ExportTiming?> GetExportTimingAsync()
    {
        var data = await ProbeAsync();
        double duration = await GetDurationAsync();
        return ExportTimingTag.Read(data["format"]?["tags"], duration);
    }

    public async Task<bool> HasAudioAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "audio") return true;
            }
        }
        return false;
    }

    public async Task<int> GetAudioBitrateAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "audio")
                {
                    int br = ParseInt(stream["bit_rate"]);
                    if (br > 0) return br / 1000;
                }
            }
        }
        var format = data["format"]?.AsObject();
        if (format != null)
        {
            int br = ParseInt(format["bit_rate"]);
            if (br > 0) return br / 1000;
        }
        return 192;
    }

    public async Task<double> GetVideoBitrateKbpsAsync()
    {
        var data = await ProbeAsync();
        var streams = data["streams"]?.AsArray();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream?["codec_type"]?.ToString() == "video")
                {
                    int br = ParseInt(stream["bit_rate"]);
                    if (br > 0) return br / 1000.0;
                }
            }
        }
        var format = data["format"]?.AsObject();
        if (format != null)
        {
            int br = ParseInt(format["bit_rate"]);
            if (br > 0)
            {
                double audioKbps = await GetAudioBitrateAsync();
                return Math.Max(0, (br / 1000.0) - audioKbps);
            }
        }
        return 0;
    }

    private int ParseInt(JsonNode? node)
    {
        if (node == null) return 0;
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out int i)) return i;
            if (val.TryGetValue(out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return parsed;
        }
        return 0;
    }

    public static int CalculateVideoBitrate(
        double durationSec, int audioKbps, double? targetMb,
        bool keepHighestRes, int qualityLevel,
        string outputResolution = "1080x1920", string targetFps = "60")
    {
        if (!targetMb.HasValue || durationSec <= 0) return 0;

        double targetBytes = targetMb.Value * 1024 * 1024;
        double audioBytesPerSec = audioKbps * 1000.0 / 8.0;
        double audioTotalBytes = audioBytesPerSec * durationSec;
        double videoBudgetBytes = targetBytes - audioTotalBytes;
        double videoKbps = (videoBudgetBytes * 8.0 / 1000.0) / durationSec;

        return Math.Max(300, Math.Min(EncoderManager.MaxBitrateKbps, (int)videoKbps));
    }

    public static int ChooseAudioBitrate(int sourceAudioKbps, double durationSec, double? targetMb)
    {
        if (targetMb.HasValue && targetMb < 10)
            return 96;
        if (sourceAudioKbps <= 0) return 192;
        return Math.Min(Math.Max(96, sourceAudioKbps), 320);
    }
}
