using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>VOTIMING_01: one AOT-safe format for projects and crash recovery.</summary>
public static class VoiceOverTakeJson
{
    public static JsonArray Write(IEnumerable<VoiceOverTake> takes)
    {
        var array = new JsonArray();
        foreach (var t in takes)
            array.AddNode(new JsonObject
            {
                ["path"] = t.Path, ["startSec"] = t.StartSec, ["outputOffsetSec"] = t.OutputOffsetSec,
                ["durationSec"] = t.DurationSec, ["recordedEndSec"] = t.RecordedEndSec,
                ["timingSignature"] = t.TimingSignature, ["needsTimingReview"] = t.NeedsTimingReview
            });
        return array;
    }

    public static IReadOnlyList<VoiceOverTake> Read(JsonNode? node)
    {
        var takes = new List<VoiceOverTake>();
        if (node is not JsonArray array) return takes;
        foreach (var item in array)
        {
            if (item is not JsonObject o) throw new FormatException("Invalid voiceover take.");
            string? path = o["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path)) throw new FormatException("A voiceover take has no audio path.");
            double Number(string key, double fallback = 0)
            {
                double value = o[key]?.GetValue<double>() ?? fallback;
                if (!double.IsFinite(value)) throw new FormatException("Invalid voiceover timing: " + key);
                return value;
            }
            double? Optional(string key) => o[key] == null ? null : Number(key);
            var duration = Optional("durationSec");
            if (duration < 0) throw new FormatException("Invalid voiceover duration.");
            takes.Add(new VoiceOverTake(path, Number("startSec"), Number("outputOffsetSec"), duration,
                Optional("recordedEndSec"), o["timingSignature"]?.GetValue<string>(),
                o["needsTimingReview"]?.GetValue<bool>() ?? false));
        }
        return takes;
    }
}
