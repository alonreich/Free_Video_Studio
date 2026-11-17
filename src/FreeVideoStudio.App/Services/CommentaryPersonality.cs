using System;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

public enum CommentaryPreset
{
    HypeStreamer,
    ChillFriend,
    Coach,
    RoastMe
}

public enum CommentaryChattiness
{
    Quiet,
    Normal,
    Busy
}

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

    public static readonly string[] LevelNames = ["Off", "A little", "Some", "A lot", "Max"];

    public static readonly string[] EnergyNames = ["Calm", "Relaxed", "Normal", "Excited", "Max hype"];

    public const int LongLinesFromTactical = 3;

    public int MaxWordsPerLine => Tactical >= LongLinesFromTactical ? 12 : 8;

    public double MinReadableSec => Tactical >= LongLinesFromTactical ? 2.0 : CommentaryService.MinReadableOutputSec;

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

    public static CommentaryPersonality FromPreset(CommentaryPreset preset) => preset switch
    {
        CommentaryPreset.ChillFriend => new(Funny: 2, Sarcastic: 1, Energy: 0, Tactical: 1),
        CommentaryPreset.Coach => new(Funny: 0, Sarcastic: 0, Energy: 2, Tactical: 4),
        CommentaryPreset.RoastMe => new(Funny: 3, Sarcastic: 4, Energy: 2, Tactical: 0),
        _ => new(Funny: 1, Sarcastic: 0, Energy: 4, Tactical: 1),
    };

    public static CommentaryPreset PresetForTone(CaptionTone tone) => tone switch
    {
        CaptionTone.Chill => CommentaryPreset.ChillFriend,
        CaptionTone.Tactical => CommentaryPreset.Coach,
        CaptionTone.Funny => CommentaryPreset.RoastMe,
        _ => CommentaryPreset.HypeStreamer,
    };

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

public static class CommentaryPersonalitySettings
{
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
