using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FreeVideoStudio.Core.Media;

public enum EdlMemePlacement
{
    Mid = 0,
    AtStart = 1,
    AtEnd = 2,
}

public readonly record struct EdlAnchor(Guid ClipId, long SourceUs);

public sealed record EdlZoom(int X, int Y, int W, int H, bool Slow, long? StartUs = null, long? EndUs = null, int SourceW = 0, int SourceH = 0, string? AiTrackingTrajectory = null);

public sealed record EdlSpeedSegment(long StartUs, long EndUs, double Speed, EdlZoom? Zoom = null);

public sealed record EdlFreeze(long AtUs, double DurationSec);

public sealed record EdlMeme(string Id, string FilePath, EdlMemePlacement Placement, long AtUs, double DurationSec,
    MemePresentationMode Mode = MemePresentationMode.InlineFullScreen,
    MemeOverlayCorner Corner = MemeOverlayCorner.BottomRight,
    MemeOverlaySize Size = MemeOverlaySize.Medium,
    bool PlaySound = true,
    double ClipInSec = 0,
    double PosX = -1,
    double PosY = -1,
    MemeOverlayAnimation Animation = MemeOverlayAnimation.None,
    MemeTransition Transition = MemeTransition.Cut)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCornerOverlay => Mode == MemePresentationMode.CornerOverlay;
}

public readonly record struct EdlCut(long StartUs, long EndUs);

public sealed record EdlEffects
{
    private readonly IReadOnlyList<EdlSpeedSegment> _speed = Array.Empty<EdlSpeedSegment>();
    public IReadOnlyList<EdlSpeedSegment> Speed { get => _speed; init => _speed = value ?? Array.Empty<EdlSpeedSegment>(); }
    private readonly IReadOnlyList<EdlFreeze> _freezes = Array.Empty<EdlFreeze>();
    public IReadOnlyList<EdlFreeze> Freezes { get => _freezes; init => _freezes = value ?? Array.Empty<EdlFreeze>(); }
    private readonly IReadOnlyList<EdlMeme> _memes = Array.Empty<EdlMeme>();
    public IReadOnlyList<EdlMeme> Memes { get => _memes; init => _memes = value ?? Array.Empty<EdlMeme>(); }
    private readonly IReadOnlyList<EdlCut> _cuts = Array.Empty<EdlCut>();
    public IReadOnlyList<EdlCut> Cuts { get => _cuts; init => _cuts = value ?? Array.Empty<EdlCut>(); }

    public static readonly EdlEffects None = new();

    [JsonIgnore]
    public bool IsEmpty => Speed.Count == 0 && Freezes.Count == 0 && Memes.Count == 0 && Cuts.Count == 0;

    public bool Equals(EdlEffects? other)
        => other is not null
           && Speed.SequenceEqual(other.Speed)
           && Freezes.SequenceEqual(other.Freezes)
           && Memes.SequenceEqual(other.Memes)
           && Cuts.SequenceEqual(other.Cuts);

    public override int GetHashCode() => HashCode.Combine(EdlHash.Of(Speed, Freezes, Memes), EdlHash.Of(Cuts, Array.Empty<int>(), Array.Empty<int>()));
}

public sealed record EdlClip
{
    public Guid ClipId { get; init; } = Guid.NewGuid();
    private readonly string _path = string.Empty;
    public string Path { get => _path; init => _path = value ?? string.Empty; }

    public long SizeBytes { get; init; }
    public long LastWriteUtcTicks { get; init; }

    public long DurationUs { get; init; }

    public long InUs { get; init; }
    public long OutUs { get; init; }

    public ExportTiming? Timing { get; init; }

    public long IntroCutUs { get; init; }

    private readonly EdlEffects _effects = EdlEffects.None;
    public EdlEffects Effects { get => _effects; init => _effects = value ?? EdlEffects.None; }
}

public sealed record EdlThumbnail(EdlAnchor At);

public sealed record EdlMusic
{
    private readonly IReadOnlyList<string> _filePaths = Array.Empty<string>();
    public IReadOnlyList<string> FilePaths { get => _filePaths; init => _filePaths = value ?? Array.Empty<string>(); }
    private readonly IReadOnlyList<double> _durationsSec = Array.Empty<double>();
    public IReadOnlyList<double> DurationsSec { get => _durationsSec; init => _durationsSec = value ?? Array.Empty<double>(); }
    public double OffsetSec { get; init; }
    public EdlAnchor Start { get; init; }
    public EdlAnchor End { get; init; }
    public double MusicVolume { get; init; } = 1.0;
    public double VideoVolume { get; init; } = 1.0;
    public bool Loop { get; init; }
    public bool Ducking { get; init; } = true;
    public bool Carving { get; init; } = true;

    private readonly IReadOnlyList<double> _startsSec = Array.Empty<double>();
    public IReadOnlyList<double> StartsSec { get => _startsSec; init => _startsSec = value ?? Array.Empty<double>(); }

    private readonly IReadOnlyList<double> _endsSec = Array.Empty<double>();
    public IReadOnlyList<double> EndsSec { get => _endsSec; init => _endsSec = value ?? Array.Empty<double>(); }

    public bool Equals(EdlMusic? other)
        => other is not null
           && FilePaths.SequenceEqual(other.FilePaths)
           && DurationsSec.SequenceEqual(other.DurationsSec)
           && StartsSec.SequenceEqual(other.StartsSec)
           && EndsSec.SequenceEqual(other.EndsSec)
           && OffsetSec == other.OffsetSec
           && Start == other.Start && End == other.End
           && MusicVolume == other.MusicVolume && VideoVolume == other.VideoVolume
           && Loop == other.Loop && Ducking == other.Ducking && Carving == other.Carving;

    public override int GetHashCode()
        => HashCode.Combine(EdlHash.Of(FilePaths, DurationsSec, StartsSec), EdlHash.Of(EndsSec, Array.Empty<int>(), Array.Empty<int>()), OffsetSec, Start, End, MusicVolume, VideoVolume, HashCode.Combine(Loop, Ducking, Carving));
}

public sealed record MergeEdl
{
    public int Version { get; init; } = 1;

    private readonly IReadOnlyList<EdlClip> _clips = Array.Empty<EdlClip>();
    public IReadOnlyList<EdlClip> Clips { get => _clips; init => _clips = value ?? Array.Empty<EdlClip>(); }
    public bool ScraperEnabled { get; init; } = true;
    public double BaseSpeed { get; init; } = 1.0;
    public EdlThumbnail? Thumbnail { get; init; }
    public EdlMusic? Music { get; init; }
    public CaptionTrack? Captions { get; init; }

    public static readonly MergeEdl Empty = new();

    public bool Equals(MergeEdl? other)
        => other is not null
           && Version == other.Version
           && Clips.SequenceEqual(other.Clips)
           && ScraperEnabled == other.ScraperEnabled
           && BaseSpeed == other.BaseSpeed
           && Equals(Thumbnail, other.Thumbnail)
           && Equals(Music, other.Music)
           && Equals(Captions, other.Captions);

    public override int GetHashCode()
        => HashCode.Combine(Version, EdlHash.Of(Clips, Array.Empty<int>(), Array.Empty<int>()), ScraperEnabled, BaseSpeed, Thumbnail, Music, Captions);

    public int IndexOf(Guid clipId)
    {
        for (int i = 0; i < Clips.Count; i++) if (Clips[i].ClipId == clipId) return i;
        return -1;
    }

    public string ToJson() => JsonSerializer.Serialize(this, MergeEdlJsonContext.Default.MergeEdl);

    public static MergeEdl? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            FillMissing(root, Template.Edl);
            if (root["Clips"] is JsonArray clips)
            {
                foreach (var c in clips)
                {
                    if (c is not JsonObject clip) continue;
                    FillMissing(clip, Template.Clip, "ClipId");
                    if (clip["ClipId"] is null) clip["ClipId"] = Guid.NewGuid().ToString();
                    if (clip["Effects"] is JsonObject fx)
                    {
                        FillMissing(fx, Template.Effects);
                        if (fx["Memes"] is JsonArray memeArray)
                            foreach (var mm in memeArray)
                                if (mm is JsonObject memeObj) FillMissing(memeObj, Template.Meme);
                    }
                }
            }
            if (root["Music"] is JsonObject music) FillMissing(music, Template.Music);
            return root.Deserialize(MergeEdlJsonContext.Default.MergeEdl);
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static void FillMissing(JsonObject target, JsonObject defaults, string? skip = null)
    {
        foreach (var kv in defaults)
        {
            if (kv.Key == skip || target.ContainsKey(kv.Key)) continue;
            target[kv.Key] = kv.Value?.DeepClone();
        }
    }

    private static class Template
    {
        public static readonly JsonObject Edl = Of(JsonSerializer.SerializeToNode(new MergeEdl(), MergeEdlJsonContext.Default.MergeEdl));
        public static readonly JsonObject Clip = Of(JsonSerializer.SerializeToNode(new EdlClip(), MergeEdlJsonContext.Default.EdlClip));
        public static readonly JsonObject Effects = Of(JsonSerializer.SerializeToNode(new EdlEffects(), MergeEdlJsonContext.Default.EdlEffects));
        public static readonly JsonObject Music = Of(JsonSerializer.SerializeToNode(new EdlMusic(), MergeEdlJsonContext.Default.EdlMusic));
        public static readonly JsonObject Meme = Of(JsonSerializer.SerializeToNode(new EdlMeme("", "", EdlMemePlacement.Mid, 0, 0), MergeEdlJsonContext.Default.EdlMeme));

        private static JsonObject Of(JsonNode? node)
        {
            var o = node as JsonObject ?? new JsonObject();
            o.Remove("Clips");
            return o;
        }
    }
}

internal static class EdlHash
{
    public static int Of<TA, TB, TC>(IReadOnlyList<TA> a, IReadOnlyList<TB> b, IReadOnlyList<TC> c)
    {
        var h = new HashCode();
        h.Add(a.Count); foreach (var x in a) h.Add(x);
        h.Add(b.Count); foreach (var x in b) h.Add(x);
        h.Add(c.Count); foreach (var x in c) h.Add(x);
        return h.ToHashCode();
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MergeEdl))]
[JsonSerializable(typeof(EdlClip))]
[JsonSerializable(typeof(EdlEffects))]
[JsonSerializable(typeof(EdlMusic))]
[JsonSerializable(typeof(EdlMeme))]
[JsonSerializable(typeof(CaptionTrack))]
[JsonSerializable(typeof(CaptionLine))]
[JsonSerializable(typeof(CaptionWord))]
[JsonSerializable(typeof(CaptionStyle))]
[JsonSerializable(typeof(CaptionPosition))]
[JsonSerializable(typeof(CaptionTone))]
[JsonSerializable(typeof(CaptionLook))]
[JsonSerializable(typeof(CaptionSize))]
[JsonSerializable(typeof(IReadOnlyList<CaptionLine>))]
[JsonSerializable(typeof(IReadOnlyList<CaptionWord>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<CaptionLine>))]
[JsonSerializable(typeof(List<CaptionWord>))]
[JsonSerializable(typeof(List<string>))]
public partial class MergeEdlJsonContext : JsonSerializerContext { }
