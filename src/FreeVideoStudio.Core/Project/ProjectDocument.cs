using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Project;

public sealed record ProjectDocument
{
    public const string FormatId = "fvsproj";

    public const int SchemaVersion = 9;

    public const int MinimumReadableSchemaVersion = 1;

    public const string FileExtension = ".fvsproj";

    public required SourceClip Source { get; init; }

    public double BaseSpeed { get; init; } = 1.0;

    public double SourceCutStartMs { get; init; }

    public double TrimmedDurationMs { get; init; }

    public IReadOnlyList<SpeedSegment> Segments { get; init; } = Array.Empty<SpeedSegment>();

    public IReadOnlyList<OutputTimeline.Cut> Cuts { get; init; } = Array.Empty<OutputTimeline.Cut>();

    public IReadOnlyList<MemePlacement> Memes { get; init; } = Array.Empty<MemePlacement>();

    public ProjectAudio Audio { get; init; } = new();

    public ProjectExport Export { get; init; } = new();

    public ProjectMask? Mask { get; init; }

    public ProjectMerge? Merge { get; init; }

    public CaptionTrack? Captions { get; init; }

    public double FreezeTimeMs { get; init; } = -1;

    public double FreezeDurationS { get; init; } = 1.0;

    public string OverlayText { get; init; } = string.Empty;

    public string Title { get; init; } = "Untitled";

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ModifiedUtc { get; init; } = DateTimeOffset.UtcNow;

    public JsonObject? UnknownFields { get; init; }

    public double EffectiveDurationMs =>
        TrimmedDurationMs > 0.001 ? TrimmedDurationMs : Source.DurationMs;

    public OutputTimeline BuildTimeline()
    {
        var insertions = MemePlacement.ToInsertions(Memes);

        return OutputTimeline.Create(
            EffectiveDurationMs,
            Segments,
            BaseSpeed,
            SourceCutStartMs,
            insertions,
            Cuts);
    }

    public SourceIntegrity CheckSource()
    {
        if (string.IsNullOrWhiteSpace(Source.FilePath)) return SourceIntegrity.Missing;

        FileInfo info;
        try
        {
            info = new FileInfo(Source.FilePath);
            if (!info.Exists) return SourceIntegrity.Missing;
        }
        catch (Exception ex)
        {
            Infrastructure.CoreLogger.Swallowed(ex);
            return SourceIntegrity.Missing;
        }

        if (Source.SizeBytes <= 0) return SourceIntegrity.Unknown;

        if (info.Length != Source.SizeBytes) return SourceIntegrity.Changed;

        long stored = Source.ModifiedUtcSeconds;
        if (stored > 0)
        {
            long actual = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            if (Math.Abs(actual - stored) > 2) return SourceIntegrity.Changed;
        }

        return SourceIntegrity.Intact;
    }

    public bool Equals(ProjectDocument? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        return Equals(Source, other.Source)
            && BaseSpeed.Equals(other.BaseSpeed)
            && SourceCutStartMs.Equals(other.SourceCutStartMs)
            && TrimmedDurationMs.Equals(other.TrimmedDurationMs)
            && SequenceEqual(Segments, other.Segments)
            && SequenceEqual(Cuts, other.Cuts)
            && SequenceEqual(Memes, other.Memes)
            && Equals(Audio, other.Audio)
            && Equals(Export, other.Export)
            && string.Equals(Mask?.ProfileName, other.Mask?.ProfileName, StringComparison.Ordinal)
            && string.Equals(Mask?.Fingerprint, other.Mask?.Fingerprint, StringComparison.Ordinal)
            && Equals(Merge, other.Merge)
            && Equals(Captions, other.Captions)
            && FreezeTimeMs.Equals(other.FreezeTimeMs)
            && FreezeDurationS.Equals(other.FreezeDurationS)
            && string.Equals(OverlayText ?? string.Empty, other.OverlayText ?? string.Empty, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Source);
        hash.Add(BaseSpeed);
        hash.Add(SourceCutStartMs);
        hash.Add(TrimmedDurationMs);
        hash.Add(Segments.Count);
        hash.Add(Cuts.Count);
        hash.Add(Memes.Count);
        hash.Add(Audio);
        hash.Add(Export);
        hash.Add(Mask?.Fingerprint);
        hash.Add(Merge);
        return hash.ToHashCode();
    }

    internal static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return (left?.Count ?? 0) == (right?.Count ?? 0);
        if (left.Count != right.Count) return false;
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < left.Count; i++)
        {
            if (!comparer.Equals(left[i], right[i])) return false;
        }
        return true;
    }
}

public enum SourceIntegrity
{
    Intact,

    Changed,

    Missing,

    Unknown,
}

public sealed record SourceClip
{
    public required string FilePath { get; init; }

    public double DurationMs { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double Fps { get; init; }

    public long SizeBytes { get; init; }

    public long ModifiedUtcSeconds { get; init; }

    public static SourceClip Probe(string filePath, double durationMs, int width, int height, double fps)
    {
        long size = 0;
        long modified = 0;
        try
        {
            FileInfo info = new(filePath);
            if (info.Exists)
            {
                size = info.Length;
                modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            }
        }
        catch (Exception ex)
        {
            Infrastructure.CoreLogger.Swallowed(ex);
        }

        return new SourceClip
        {
            FilePath = filePath,
            DurationMs = durationMs,
            Width = width,
            Height = height,
            Fps = fps,
            SizeBytes = size,
            ModifiedUtcSeconds = modified,
        };
    }
}

public sealed record ProjectMask(string ProfileName, string Fingerprint, JsonObject? Config)
{
    public static string ComputeFingerprint(JsonObject? config)
    {
        if (config is null) return string.Empty;

        string canonical = Canonicalise(config);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash, 0, 8);
    }

    private static string Canonicalise(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";

            case JsonObject o:
            {
                var keys = new List<string>();
                foreach (KeyValuePair<string, JsonNode?> kvp in o) keys.Add(kvp.Key);
                keys.Sort(StringComparer.Ordinal);

                var sb = new System.Text.StringBuilder("{");
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(keys[i]).Append(':').Append(Canonicalise(o[keys[i]]));
                }
                return sb.Append('}').ToString();
            }

            case JsonArray a:
            {
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Canonicalise(a[i]));
                }
                return sb.Append(']').ToString();
            }

            default:
                return node.ToJsonString();
        }
    }

    public bool MatchesLive(JsonObject? liveConfig)
        => string.Equals(Fingerprint, ComputeFingerprint(liveConfig), StringComparison.Ordinal);
}

public readonly record struct MergeClip(string FilePath, double StartSec, double EndSec);

public sealed record ProjectMerge
{
    public IReadOnlyList<MergeClip> Clips { get; init; } = Array.Empty<MergeClip>();

    public double BaseSpeed { get; init; } = 1.0;

    public MergeEdl? Edl { get; init; }

    public bool HasClips => Clips.Count > 0 || Edl is { Clips.Count: > 0 };

    public MergeEdl ToEdl()
    {
        if (Edl is not null) return Edl;
        var clips = new List<EdlClip>(Clips.Count);
        for (int i = 0; i < Clips.Count; i++)
        {
            var c = Clips[i];
            clips.Add(new EdlClip
            {
                ClipId = new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                Path = c.FilePath,
                InUs = SecToUs(c.StartSec),
                OutUs = SecToUs(c.EndSec),
            });
        }
        return new MergeEdl { Clips = clips, BaseSpeed = BaseSpeed > 0 ? BaseSpeed : 1.0 };
    }

    private static long SecToUs(double sec) => double.IsFinite(sec) && sec > 0 ? (long)Math.Round(sec * 1_000_000.0) : 0;

    public bool Equals(ProjectMerge? other)
        => other is not null
        && BaseSpeed.Equals(other.BaseSpeed)
        && ProjectDocument.SequenceEqual(Clips, other.Clips)
        && Equals(Edl, other.Edl);

    public override int GetHashCode() => HashCode.Combine(BaseSpeed, Clips.Count);
}

public sealed record ProjectMusicSong(string Path, double FullSec, double InSec, double OutSec, bool UserTrimmed);

public sealed record ProjectAudio
{
    public string? MusicFilePath { get; init; }

    public double MusicStartSec { get; init; }

    public IReadOnlyList<ProjectMusicSong> MusicSongs { get; init; } = Array.Empty<ProjectMusicSong>();

    public double MusicTimelineStartSec { get; init; }

    public double MusicTimelineEndSec { get; init; }

    public bool MusicLoop { get; init; }

    public bool MusicCarving { get; init; }

    public double MusicVolume { get; init; } = 1.0;

    public double VideoVolume { get; init; } = 1.0;

    public bool SidechainDucking { get; init; }

    public string? VoiceOverFilePath { get; init; }

    public double VoiceOverAtOutputSec { get; init; }

    public double VoiceOverVolume { get; init; } = 1.0;

    public IReadOnlyList<VoiceOverTake> VoiceOverTakes { get; init; } = Array.Empty<VoiceOverTake>();
    public bool VoiceOverDuckAudio { get; init; }
    public bool VoiceOverProtectFromMusic { get; init; } = true;

    public bool Equals(ProjectAudio? other) => other != null &&
        MusicFilePath == other.MusicFilePath && MusicStartSec == other.MusicStartSec &&
        MusicVolume == other.MusicVolume && VideoVolume == other.VideoVolume && SidechainDucking == other.SidechainDucking &&
        VoiceOverFilePath == other.VoiceOverFilePath && VoiceOverAtOutputSec == other.VoiceOverAtOutputSec &&
        VoiceOverVolume == other.VoiceOverVolume && VoiceOverDuckAudio == other.VoiceOverDuckAudio &&
        VoiceOverProtectFromMusic == other.VoiceOverProtectFromMusic && VoiceOverTakes.SequenceEqual(other.VoiceOverTakes) &&
        MusicSongs.SequenceEqual(other.MusicSongs) && MusicTimelineStartSec == other.MusicTimelineStartSec &&
        MusicTimelineEndSec == other.MusicTimelineEndSec && MusicLoop == other.MusicLoop && MusicCarving == other.MusicCarving;

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(MusicFilePath); h.Add(MusicStartSec); h.Add(MusicVolume); h.Add(VideoVolume); h.Add(SidechainDucking);
        h.Add(VoiceOverFilePath); h.Add(VoiceOverAtOutputSec); h.Add(VoiceOverVolume);
        h.Add(VoiceOverDuckAudio); h.Add(VoiceOverProtectFromMusic);
        foreach (var take in VoiceOverTakes) h.Add(take);
        foreach (var song in MusicSongs) h.Add(song);
        h.Add(MusicTimelineStartSec); h.Add(MusicTimelineEndSec); h.Add(MusicLoop); h.Add(MusicCarving);
        return h.ToHashCode();
    }
}

public sealed record ProjectExport
{
    public int QualityIndex { get; init; } = -1;

    public double? TargetMegabytes { get; init; }

    public string HardwareMode { get; init; } = "Auto";

    public string? OutputDirectory { get; init; }

    public bool PortraitMode { get; init; } = true;
}
