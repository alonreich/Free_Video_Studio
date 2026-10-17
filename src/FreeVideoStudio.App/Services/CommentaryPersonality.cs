// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS), docs/04_UI_UX_AVALONIA_SPEC.md (UI-AICOMMENTARY)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

/// <summary>COMMENTARYMIX_01 — the four one-click styles of the options window.</summary>
public enum CommentaryPreset
{
    HypeStreamer,
    ChillFriend,
    Coach,
    RoastMe
}

/// <summary>COMMENTARYMIX_01 — how often the commentary speaks.</summary>
public enum CommentaryChattiness
{
    Quiet,
    Normal,
    Busy
}

/// <summary>
/// COMMENTARYMIX_01 — the commentary "brain": four amounts from 0 (Off) to 4 (Max) — Funny, Sarcastic,
/// Energetic (0 = calm and quiet … 4 = maximum hype; it never means "technical") and Tactical — plus how
/// often it talks and whether swearing is allowed. The AI receives each amount as WORDS ("A lot"), never as
/// a number: models follow "strongly" far more reliably than "3 of 4".
/// </summary>
public sealed record CommentaryPersonality(
    int Funny,
    int Sarcastic,
    int Energy,
    int Tactical,
    CommentaryChattiness Chattiness = CommentaryChattiness.Normal,
    bool AllowSwearing = false)
{
    public const int MinLevel = 0;
    public const int MaxLevel = 4;

    /// <summary>COMMENTARYMIX_01 — the five steps every slider has, in order.</summary>
    public static readonly string[] LevelNames = ["Off", "A little", "Some", "A lot", "Max"];

    /// <summary>The Energetic slider's own five steps (calm … hype).</summary>
    public static readonly string[] EnergyNames = ["Calm", "Relaxed", "Normal", "Excited", "Max hype"];

    /// <summary>TACTICAL_02 — from this Tactical amount on, lines may be longer and stay on screen longer.</summary>
    public const int LongLinesFromTactical = 3;

    /// <summary>TACTICAL_02 — words per line: 8 normally, 12 when Tactical is "A lot" or "Max".</summary>
    public int MaxWordsPerLine => Tactical >= LongLinesFromTactical ? 12 : 8;

    /// <summary>TACTICAL_02 — shortest time a line stays on screen in the finished video.</summary>
    public double MinReadableSec => Tactical >= LongLinesFromTactical ? 2.0 : CommentaryService.MinReadableOutputSec;

    /// <summary>The same amounts, each clamped into 0…4.</summary>
    public CommentaryPersonality Normalized() => this with
    {
        Funny = Clamp(Funny),
        Sarcastic = Clamp(Sarcastic),
        Energy = Clamp(Energy),
        Tactical = Clamp(Tactical),
    };

    public static int Clamp(int level) => Math.Clamp(level, MinLevel, MaxLevel);

    public static string LevelName(int level) => LevelNames[Clamp(level)];

    public static string EnergyName(int level) => EnergyNames[Clamp(level)];

    /// <summary>COMMENTARYMIX_01 — the slider amounts each one-click style sets.</summary>
    public static CommentaryPersonality FromPreset(CommentaryPreset preset) => preset switch
    {
        CommentaryPreset.ChillFriend => new(Funny: 2, Sarcastic: 1, Energy: 0, Tactical: 1),
        CommentaryPreset.Coach => new(Funny: 0, Sarcastic: 0, Energy: 2, Tactical: 4),
        CommentaryPreset.RoastMe => new(Funny: 3, Sarcastic: 4, Energy: 2, Tactical: 0),
        _ => new(Funny: 1, Sarcastic: 0, Energy: 4, Tactical: 1),
    };

    /// <summary>The style that matches an older single-tone choice (used before COMMENTARYMIX_01 existed).</summary>
    public static CommentaryPreset PresetForTone(CaptionTone tone) => tone switch
    {
        CaptionTone.Chill => CommentaryPreset.ChillFriend,
        CaptionTone.Tactical => CommentaryPreset.Coach,
        CaptionTone.Funny => CommentaryPreset.RoastMe,
        _ => CommentaryPreset.HypeStreamer,
    };

    /// <summary>
    /// The single tone that best describes this mix — kept on the caption track for older readers
    /// (<see cref="CaptionTrack.Tone"/>) and the "last tone" setting.
    /// </summary>
    public CaptionTone DominantTone
    {
        get
        {
            int funny = Math.Max(Funny, Sarcastic);
            if (Energy == 0 && Tactical < LongLinesFromTactical && funny < LongLinesFromTactical) return CaptionTone.Chill;
            if (Tactical >= funny && Tactical >= Energy && Tactical > 0) return CaptionTone.Tactical;
            if (funny >= Energy && funny > 0) return CaptionTone.Funny;
            return Energy <= 1 ? CaptionTone.Chill : CaptionTone.Hype;
        }
    }

    /// <summary>
    /// COMMENTARYMIX_01 — the prompt paragraph describing this mix in plain words for the AI. Pure.
    /// </summary>
    public string DescribeForPrompt()
    {
        var p = Normalized();
        string energy = p.Energy switch
        {
            0 => "very calm, quiet and relaxed: no shouting, no exclamation marks",
            1 => "calm and easygoing, a little upbeat at the big moments",
            2 => "natural, normal energy",
            3 => "excited and upbeat, punchy reactions",
            _ => "MAXIMUM HYPE: loud, explosive excitement at every good play",
        };
        return "Personality mix (follow these amounts exactly):\n"
             + $"- Funny ({LevelName(p.Funny)}): {Amount(p.Funny, "jokes and playful reactions to fails, near-misses and silly moments")}\n"
             + $"- Sarcastic ({LevelName(p.Sarcastic)}): {Amount(p.Sarcastic, "dry, ironic remarks and teasing yourself or the enemy")}\n"
             + $"- Energy ({EnergyName(p.Energy)}): {energy}\n"
             + $"- Tactical ({LevelName(p.Tactical)}): {Amount(p.Tactical, "saying WHY you move, chase, hide, heal or reposition")}";
    }

    private static string Amount(int level, string what) => level switch
    {
        0 => "none at all",
        1 => $"a light touch, now and then — {what}",
        2 => $"regularly — {what}",
        3 => $"strongly, in most lines — {what}",
        _ => $"the main focus of almost every line — {what}",
    };
}

/// <summary>REWRITELINE_01 — the commentary mix the options window remembered last time.</summary>
public static class CommentaryPersonalitySettings
{
    /// <summary>
    /// The remembered style + slider amounts + how often + swearing. Never chosen → the one-click style matching
    /// <paramref name="fallbackTone"/>.
    /// </summary>
    public static CommentaryPersonality Remembered(Infrastructure.AppSettings s, CaptionTone fallbackTone)
    {
        CommentaryPreset preset = Enum.TryParse(s.AiCommentaryLastPreset, true, out CommentaryPreset p)
            ? p
            : CommentaryPersonality.PresetForTone(fallbackTone);
        var mix = CommentaryPersonality.FromPreset(preset);
        return new CommentaryPersonality(
            s.AiCommentaryLastFunny ?? mix.Funny,
            s.AiCommentaryLastSarcastic ?? mix.Sarcastic,
            s.AiCommentaryLastEnergy ?? mix.Energy,
            s.AiCommentaryLastTactical ?? mix.Tactical,
            Enum.TryParse(s.AiCommentaryLastChattiness, true, out CommentaryChattiness c) ? c : CommentaryChattiness.Normal,
            s.AiCommentaryLastAllowSwearing ?? false).Normalized();
    }
}
