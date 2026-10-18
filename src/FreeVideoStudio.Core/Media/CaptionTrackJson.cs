using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// CAPTIONS_01 — NativeAOT-safe, reflection-free JSON serialization and deserialization for
/// <see cref="CaptionTrack"/> and nested elements. Used for .fvsproj project document persistence
/// and crash recovery snapshots.
/// </summary>
public static class CaptionTrackJson
{
    public static JsonObject? Write(CaptionTrack? track)
    {
        if (track == null) return null;

        var root = new JsonObject
        {
            ["style"] = track.Style.ToString(),
            ["position"] = track.Position.ToString(),
            ["tone"] = track.Tone.ToString(),
            ["language"] = track.Language,
            ["look"] = track.Look.ToString(),
            ["size"] = track.Size.ToString(),
        };

        if (track.Lines != null)
        {
            var linesArray = new JsonArray();
            foreach (var line in track.Lines)
            {
                var lineObj = new JsonObject
                {
                    ["id"] = line.Id,
                    ["start_sec"] = line.SourceStartSec,
                    ["end_sec"] = line.SourceEndSec,
                    ["text"] = line.Text,
                    ["confidence"] = line.Confidence,
                };

                if (line.Emphasis != null && line.Emphasis.Count > 0)
                {
                    var emphasisArray = new JsonArray();
                    foreach (var e in line.Emphasis)
                    {
                        if (!string.IsNullOrWhiteSpace(e))
                            emphasisArray.AddNode(JsonValue.Create(e));
                    }
                    lineObj["emphasis"] = emphasisArray;
                }

                if (line.Words != null && line.Words.Count > 0)
                {
                    var wordsArray = new JsonArray();
                    foreach (var w in line.Words)
                    {
                        wordsArray.AddNode(new JsonObject
                        {
                            ["text"] = w.Text,
                            ["start_sec"] = w.StartSec,
                            ["end_sec"] = w.EndSec,
                            ["syllables"] = w.Syllables,
                        });
                    }
                    lineObj["words"] = wordsArray;
                }

                linesArray.AddNode(lineObj);
            }
            root["lines"] = linesArray;
        }

        return root;
    }

    public static CaptionTrack? Read(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;

        CaptionStyle style = Enum.TryParse<CaptionStyle>(ReadString(obj, "style"), ignoreCase: true, out var s) ? s : CaptionStyle.Karaoke;
        CaptionPosition pos = Enum.TryParse<CaptionPosition>(ReadString(obj, "position"), ignoreCase: true, out var p) ? p : CaptionPosition.BottomContent;
        CaptionTone tone = Enum.TryParse<CaptionTone>(ReadString(obj, "tone"), ignoreCase: true, out var t) ? t : CaptionTone.Hype;
        string lang = ReadString(obj, "language") ?? "en";
        CaptionLook look = Enum.TryParse<CaptionLook>(ReadString(obj, "look"), ignoreCase: true, out var lk) ? lk : CaptionLook.Outline;
        CaptionSize size = Enum.TryParse<CaptionSize>(ReadString(obj, "size"), ignoreCase: true, out var sz) ? sz : CaptionSize.Medium;

        List<CaptionLine>? lines = null;
        if (obj["lines"] is JsonArray linesArray)
        {
            lines = new List<CaptionLine>(linesArray.Count);
            foreach (var item in linesArray)
            {
                if (item is not JsonObject lineObj) continue;
                int id = ReadInt(lineObj, "id", 0);
                double start = ReadDouble(lineObj, "start_sec", 0);
                double end = ReadDouble(lineObj, "end_sec", 0);
                string? text = ReadString(lineObj, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                double conf = ReadDouble(lineObj, "confidence", 1.0);

                List<string>? emphasis = null;
                if (lineObj["emphasis"] is JsonArray empArr)
                {
                    emphasis = new List<string>();
                    foreach (var ev in empArr)
                    {
                        if (ev is JsonValue jv && jv.TryGetValue(out string? es) && !string.IsNullOrWhiteSpace(es))
                            emphasis.Add(es);
                    }
                }

                List<CaptionWord>? words = null;
                if (lineObj["words"] is JsonArray wArr)
                {
                    words = new List<CaptionWord>(wArr.Count);
                    foreach (var wv in wArr)
                    {
                        if (wv is not JsonObject wObj) continue;
                        string? wText = ReadString(wObj, "text");
                        if (string.IsNullOrWhiteSpace(wText)) continue;
                        double wStart = ReadDouble(wObj, "start_sec", 0);
                        double wEnd = ReadDouble(wObj, "end_sec", 0);
                        int syllables = ReadInt(wObj, "syllables", 1);
                        words.Add(new CaptionWord(wText, wStart, wEnd, syllables));
                    }
                }

                lines.Add(new CaptionLine(id, start, end, text, emphasis, conf, words));
            }
        }

        return new CaptionTrack(style, pos, tone, lang, lines, look, size);
    }

    private static string? ReadString(JsonObject o, string key)
    {
        if (o[key] is JsonValue v && v.TryGetValue(out string? s)) return s;
        return null;
    }

    private static int ReadInt(JsonObject o, string key, int fallback)
    {
        if (o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (v.TryGetValue(out double d) && !double.IsNaN(d)) return (int)Math.Round(d);
        if (v.TryGetValue(out string? s) && int.TryParse(s, out int parsed)) return parsed;
        return fallback;
    }

    private static double ReadDouble(JsonObject o, string key, double fallback)
    {
        if (o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)) return parsed;
        return fallback;
    }
}
