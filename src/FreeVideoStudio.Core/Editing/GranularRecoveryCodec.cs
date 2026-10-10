// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using static FreeVideoStudio.Core.Editing.GranularJsonRead;

namespace FreeVideoStudio.Core.Editing;

/// <summary>
/// RECOVERY_03 — the Granular editor's live session as the <c>"granular_session"</c> node of the
/// crash-recovery file, and back.
///
/// <para>
/// EDITSTATE_01 — the persistence-facing VALUES moved here from the window with the state they
/// describe. The window keeps what is genuinely its own: the 300ms debounce timer, the
/// <c>EditorRecoveryWriter</c> and the file read. This type is pure: a session in, a node out.
/// </para>
///
/// <para>
/// Segments and cuts are TRIM-RELATIVE ms and memes are CLIP-RELATIVE source seconds — the exact
/// frames of reference the session holds them in, so rehydration needs no translation. A node is
/// honoured only when its video path AND trim window match; anything else is a stale snapshot from
/// another clip (or an older trim) and is ignored. A malformed entry is dropped, never fatal.
/// </para>
/// </summary>
public static class GranularRecoveryCodec
{
    public const string Key = "granular_session";
    public const int SchemaVersion = 1;

    /// <summary>The live editor state as one JSON node. UI thread only (reads the lists).</summary>
    public static JsonObject Build(GranularEditSession s, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(s);

        var segments = new JsonArray();
        foreach (var seg in s.Segments)
        {
            segments.AddNode(new JsonObject   // AOTSAFETY_02
            {
                ["start_ms"] = seg.StartMs,
                ["end_ms"] = seg.EndMs,
                ["speed"] = seg.Speed,
                ["zoom_x"] = seg.ZoomX,
                ["zoom_y"] = seg.ZoomY,
                ["zoom_w"] = seg.ZoomW,
                ["zoom_h"] = seg.ZoomH,
                ["zoom_orig_res"] = seg.ZoomOrigRes,
                ["zoom_slow"] = seg.ZoomSlow,
                ["zoom_start_ms"] = seg.ZoomStartMs,
                ["zoom_end_ms"] = seg.ZoomEndMs
            });
        }

        var cuts = new JsonArray();
        foreach (var c in s.Cuts)
            cuts.AddNode(new JsonObject { ["start_ms"] = c.StartMs, ["end_ms"] = c.EndMs });   // AOTSAFETY_02

        var memes = new JsonArray();
        foreach (var m in s.Memes)
        {
            var memeObj = new JsonObject
            {
                ["file_path"] = m.FilePath,
                ["at_source_sec_relative"] = m.AtSourceSecRelative,
                ["duration_sec"] = m.DurationSec,
                ["id"] = m.Id
            };
            MemePresentationJson.Write(memeObj, m);   // MEMEMODE_01
            memes.AddNode(memeObj);   // AOTSAFETY_02
        }

        return new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["open"] = true,
            ["video_path"] = s.VideoPath,
            ["trim_start_ms"] = s.TrimStartMs,
            ["trim_end_ms"] = s.TrimEndMs,
            ["base_speed"] = s.BaseSpeed,
            ["freeze_time_ms"] = s.FreezeTimeMs,
            ["freeze_duration_s"] = s.FreezeDurationS,
            ["saved_at_utc"] = utcNow.ToString("O"),
            ["segments"] = segments,
            ["cuts"] = cuts,
            ["memes"] = memes
        };
    }

    /// <summary>
    /// RECOVERY_03 — replaces the session's edit with <paramref name="node"/> when it holds an
    /// unfinished session for THIS video and trim window. Returns whether it did. A merge never
    /// restores here (MERGEEDIT_02 — the Merger autosaves the merge itself).
    /// </summary>
    public static bool TryApply(JsonObject? node, GranularEditSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (node == null || s.IsMergeMode) return false;
        if (!GetJsonBool(node["open"], false)) return false;
        if (GetJsonIntOrNull(node["schema_version"]) != SchemaVersion) return false;

        // Session identity: same file, same trim window (1ms tolerance for the double round-trip).
        string? videoPath = GetJsonString(node["video_path"]);
        if (string.IsNullOrEmpty(videoPath) ||
            !string.Equals(videoPath, s.VideoPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (Math.Abs(GetJsonDouble(node["trim_start_ms"], double.MinValue) - s.TrimStartMs) > 1.0) return false;
        if (Math.Abs(GetJsonDouble(node["trim_end_ms"], double.MinValue) - s.TrimEndMs) > 1.0) return false;

        var segments = new List<SpeedSegment>();
        if (node["segments"] is JsonArray segArr)
        {
            foreach (var n in segArr)
            {
                if (n is not JsonObject o) continue;
                double start = GetJsonDouble(o["start_ms"], -1);
                double end = GetJsonDouble(o["end_ms"], -1);
                if (end <= start || start < -0.5) continue;
                segments.Add(new SpeedSegment(
                    start,
                    end,
                    GetJsonDouble(o["speed"], 1.1),
                    GetJsonIntOrNull(o["zoom_x"]),
                    GetJsonIntOrNull(o["zoom_y"]),
                    GetJsonIntOrNull(o["zoom_w"]),
                    GetJsonIntOrNull(o["zoom_h"]),
                    GetJsonString(o["zoom_orig_res"]),
                    GetJsonBool(o["zoom_slow"], false),
                    GetJsonDoubleOrNull(o["zoom_start_ms"]),
                    GetJsonDoubleOrNull(o["zoom_end_ms"])));
            }
        }

        var cuts = new List<CutRange>();
        if (node["cuts"] is JsonArray cutArr)
        {
            foreach (var n in cutArr)
            {
                if (n is not JsonObject o) continue;
                double start = GetJsonDouble(o["start_ms"], -1);
                double end = GetJsonDouble(o["end_ms"], -1);
                if (end <= start) continue;
                cuts.Add(new CutRange(start, end));
            }
        }

        var memes = new List<MemePlacement>();
        if (node["memes"] is JsonArray memeArr)
        {
            int i = 0;
            foreach (var n in memeArr)
            {
                if (n is not JsonObject o) continue;
                string? file = GetJsonString(o["file_path"]);
                if (file != null) file = MigrationPathResolver.ResolveSavedFile(file);   // MEMEFOLDER_02
                double at = GetJsonDouble(o["at_source_sec_relative"], -1);
                double dur = GetJsonDouble(o["duration_sec"], 0);
                if (string.IsNullOrEmpty(file) || at < 0 || dur <= 0) continue;
                memes.Add(MemePresentationJson.Apply(o, new MemePlacement(file!, at, dur,   // MEMEMODE_01
                    GetJsonString(o["id"]) is string id && id.Length > 0 ? id : MemePlacement.NewId(i))));
                i++;
            }
        }

        s.Segments.Clear();
        s.Segments.AddRange(segments);
        s.Cuts.Clear();
        s.Cuts.AddRange(cuts);
        s.Memes.Clear();
        s.Memes.AddRange(memes);
        s.BaseSpeed = Math.Clamp(GetJsonDouble(node["base_speed"], s.BaseSpeed), 1.0, 40.0);
        s.FreezeTimeMs = GetJsonDouble(node["freeze_time_ms"], -1.0);
        if (s.FreezeTimeMs < -0.5) s.FreezeTimeMs = -1.0;
        s.FreezeDurationS = Math.Max(0.1, GetJsonDouble(node["freeze_duration_s"], 1.0));

        CoreLogger.Info("Granular",
            "RECOVERY_03 - restored an unfinished granular session from the crash-recovery state: " +
            $"{segments.Count} segment(s), {cuts.Count} cut(s), {memes.Count} meme(s), " +
            $"freeze {(s.FreezeTimeMs >= 0 ? $"{s.FreezeDurationS:0.0}s" : "none")}.");
        return true;
    }
}
