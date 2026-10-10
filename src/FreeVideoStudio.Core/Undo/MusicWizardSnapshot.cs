// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FreeVideoStudio.Core.Undo;

/// <summary>
/// UNDO_28 — everything in the Add Music wizard that reaches the export (<c>MusicWizardResult</c>):
/// the song, the auto-fill queue in order, where the song starts, the two balance faders and the
/// three mix switches. Plain data — no waveform, preview player, analysis or cache.
///
/// <para>
/// Equality is tolerant where the controls are continuous (a slider that settles 1e-9 away is not
/// an edit, U4) and case-insensitive on paths, matching every other path comparison in the wizard.
/// </para>
/// </summary>
public sealed record MusicWizardSnapshot(
    string? TrackPath,
    ImmutableArray<string> QueuePaths,
    double SongStartSeconds,
    double VideoVolumePercent,
    double MusicVolumePercent,
    bool Ducking,
    bool Carving,
    bool Loop)
{
    /// <summary>Song start resolution: 1ms. Below that the export cannot tell the difference.</summary>
    public const double SecondsTolerance = 0.0005;

    /// <summary>Fader resolution: a twentieth of a percent.</summary>
    public const double PercentTolerance = 0.05;

    public static MusicWizardSnapshot Create(string? trackPath, IEnumerable<string>? queue, double songStart,
        double videoPercent, double musicPercent, bool ducking, bool carving, bool loop) =>
        new(string.IsNullOrWhiteSpace(trackPath) ? null : trackPath,
            (queue ?? Array.Empty<string>()).ToImmutableArray(),
            songStart, videoPercent, musicPercent, ducking, carving, loop);

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
               && Math.Abs(SongStartSeconds - other.SongStartSeconds) <= SecondsTolerance
               && Math.Abs(VideoVolumePercent - other.VideoVolumePercent) <= PercentTolerance
               && Math.Abs(MusicVolumePercent - other.MusicVolumePercent) <= PercentTolerance
               && Ducking == other.Ducking
               && Carving == other.Carving
               && Loop == other.Loop;
    }

    /// <summary>Consistent with the tolerant equality: only the exact-compared fields.</summary>
    public override int GetHashCode() =>
        HashCode.Combine((TrackPath ?? string.Empty).ToUpperInvariant(), QueuePaths.IsDefault ? 0 : QueuePaths.Length, Ducking, Carving, Loop);

    /// <summary>
    /// UNDO_28 — names the step between two states and says whether it belongs to a continuous
    /// gesture (U1). The ONE place the wizard's labels come from, so a new control cannot be
    /// recorded as a bare "Undo". Priority follows causality: choosing a song resets the queue, and
    /// moving the song start resets it too, so the cause wins over its side effect.
    /// </summary>
    public static (string Label, string? GestureKey) DescribeChange(MusicWizardSnapshot before, MusicWizardSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (!SamePath(before.TrackPath, after.TrackPath))
            return ("choose song", "choose-song");   // arrowing down the list is one choice, not twelve

        if (Math.Abs(before.SongStartSeconds - after.SongStartSeconds) > SecondsTolerance)
            return ("move song start", "song-start");

        if (!before.SameQueue(after))
        {
            int b = before.QueuePaths.IsDefault ? 0 : before.QueuePaths.Length;
            int a = after.QueuePaths.IsDefault ? 0 : after.QueuePaths.Length;
            if (a == 0) return ("clear song queue", null);
            if (b == 0 || a > b) return ("auto-fill songs", null);
            if (a < b) return ("remove queued song", null);
            return ("reorder songs", null);
        }

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

/// <summary>
/// UNDO_28 — which history command a key press asks for. Shared by the Crop Tool and the Music
/// wizard so Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z mean the same thing in both, and so the "a focused
/// text field keeps its own undo" rule is decided in exactly one place.
/// </summary>
public enum HistoryCommand
{
    None,
    Undo,
    Redo,
}

public static class HistoryShortcut
{
    /// <param name="key">The key name (<c>Avalonia.Input.Key.ToString()</c>), e.g. "Z".</param>
    /// <param name="control">Ctrl held.</param>
    /// <param name="shift">Shift held.</param>
    /// <param name="textInputFocused">A text-editing control owns focus: its own undo wins.</param>
    public static HistoryCommand Classify(string key, bool control, bool shift, bool textInputFocused)
    {
        if (textInputFocused || !control) return HistoryCommand.None;
        if (string.Equals(key, "Z", StringComparison.Ordinal)) return shift ? HistoryCommand.Redo : HistoryCommand.Undo;
        if (string.Equals(key, "Y", StringComparison.Ordinal)) return HistoryCommand.Redo;   // Ctrl+Shift+Y too, as the Crop Tool always allowed
        return HistoryCommand.None;
    }
}
