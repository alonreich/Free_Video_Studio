using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FreeVideoStudio.Core.Undo;

public sealed record MusicWizardSnapshot(
    string? TrackPath,
    ImmutableArray<string> QueuePaths,
    double SongStartSeconds,
    double VideoVolumePercent,
    double MusicVolumePercent,
    bool Ducking,
    bool Carving,
    bool Loop,
    ImmutableArray<double> Trims = default)
{
    public const double SecondsTolerance = 0.0005;

    public const double PercentTolerance = 0.05;

    public static MusicWizardSnapshot Create(string? trackPath, IEnumerable<string>? queue, double songStart,
        double videoPercent, double musicPercent, bool ducking, bool carving, bool loop,
        IEnumerable<double>? trims = null) =>
        new(string.IsNullOrWhiteSpace(trackPath) ? null : trackPath,
            (queue ?? Array.Empty<string>()).ToImmutableArray(),
            songStart, videoPercent, musicPercent, ducking, carving, loop,
            (trims ?? Array.Empty<double>()).ToImmutableArray());

    internal bool SameTrims(MusicWizardSnapshot other)
    {
        ImmutableArray<double> a = Trims.IsDefault ? ImmutableArray<double>.Empty : Trims;
        ImmutableArray<double> b = other.Trims.IsDefault ? ImmutableArray<double>.Empty : other.Trims;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > SecondsTolerance) return false;
        return true;
    }

    internal static bool SamePath(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    internal bool SameQueue(MusicWizardSnapshot other)
    {
        ImmutableArray<string> a = QueuePaths.IsDefault ? ImmutableArray<string>.Empty : QueuePaths;
        ImmutableArray<string> b = other.QueuePaths.IsDefault ? ImmutableArray<string>.Empty : other.QueuePaths;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!SamePath(a[i], b[i])) return false;
        return true;
    }

    public bool Equals(MusicWizardSnapshot? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return SamePath(TrackPath, other.TrackPath)
               && SameQueue(other)
               && SameTrims(other)
               && Math.Abs(SongStartSeconds - other.SongStartSeconds) <= SecondsTolerance
               && Math.Abs(VideoVolumePercent - other.VideoVolumePercent) <= PercentTolerance
               && Math.Abs(MusicVolumePercent - other.MusicVolumePercent) <= PercentTolerance
               && Ducking == other.Ducking
               && Carving == other.Carving
               && Loop == other.Loop;
    }

    public override int GetHashCode() =>
        HashCode.Combine((TrackPath ?? string.Empty).ToUpperInvariant(), QueuePaths.IsDefault ? 0 : QueuePaths.Length, Ducking, Carving, Loop);

    public static (string Label, string? GestureKey) DescribeChange(MusicWizardSnapshot before, MusicWizardSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (!SamePath(before.TrackPath, after.TrackPath))
            return ("choose song", "choose-song");

        if (Math.Abs(before.SongStartSeconds - after.SongStartSeconds) > SecondsTolerance)
            return ("move song start", "song-start");

        if (!before.SameQueue(after))
        {
            int b = before.QueuePaths.IsDefault ? 0 : before.QueuePaths.Length;
            int a = after.QueuePaths.IsDefault ? 0 : after.QueuePaths.Length;
            if (a == 0) return ("clear song list", null);
            if (b == 0 || a > b) return (a - b > 1 ? "add songs" : "add song", null);
            if (a < b) return ("remove song", null);
            return ("reorder songs", null);
        }
        if (!before.SameTrims(after))
            return ("trim song", "song-trim");

        if (Math.Abs(before.MusicVolumePercent - after.MusicVolumePercent) > PercentTolerance)
            return ("change music balance", "music-balance");

        if (Math.Abs(before.VideoVolumePercent - after.VideoVolumePercent) > PercentTolerance)
            return ("change video balance", "video-balance");

        if (before.Ducking != after.Ducking)
            return (after.Ducking ? "turn on ducking" : "turn off ducking", null);

        if (before.Carving != after.Carving)
            return (after.Carving ? "turn on smooth blend" : "turn off smooth blend", null);

        if (before.Loop != after.Loop)
            return (after.Loop ? "turn on looping" : "turn off looping", null);

        return ("change music", null);
    }
}

public enum HistoryCommand
{
    None,
    Undo,
    Redo,
}

public static class HistoryShortcut
{
    public static HistoryCommand Classify(string key, bool control, bool shift, bool textInputFocused)
    {
        if (textInputFocused || !control) return HistoryCommand.None;
        if (string.Equals(key, "Z", StringComparison.Ordinal)) return shift ? HistoryCommand.Redo : HistoryCommand.Undo;
        if (string.Equals(key, "Y", StringComparison.Ordinal)) return HistoryCommand.Redo;
        return HistoryCommand.None;
    }
}
