using System;
using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AICOMMENTARY_SERVICE_01 — system instructions and structured schemas for AI live commentary generation.
/// </summary>
public static class AiCommentaryPrompts
{
    public const string CommentaryJsonSchema = "{\"type\":\"ARRAY\",\"items\":{\"type\":\"OBJECT\",\"properties\":{\"start\":{\"type\":\"NUMBER\"},\"end\":{\"type\":\"NUMBER\"},\"text\":{\"type\":\"STRING\"},\"emphasis\":{\"type\":\"ARRAY\",\"items\":{\"type\":\"STRING\"}},\"confidence\":{\"type\":\"NUMBER\"}},\"required\":[\"start\",\"end\",\"text\"]}}";

    /// <summary>Older single-tone entry point: the tone is turned into its one-click style (COMMENTARYMIX_01).</summary>
    public static string BuildPrompt(CaptionTone tone, AiGameProfile profile, string language, string? customNotes = null, string? playbackNotes = null, bool canHearAudio = true)
        => BuildPrompt(CommentaryPersonality.FromPreset(CommentaryPersonality.PresetForTone(tone)), profile, language,
            customNotes, playbackNotes, canHearAudio);

    /// <summary>
    /// COMMENTARYMIX_01 — the commentary prompt for one part of the video.
    /// <paramref name="killTimesInPart"/> (TACTICAL_03): confirmed eliminations from the kill tracker, in seconds
    /// from the part's start; null = the tracker did not run (nothing is said about kills).
    /// <paramref name="rewriteOf"/> (REWRITELINE_01): write exactly ONE new line that replaces this text.
    /// </summary>
    public static string BuildPrompt(
        CommentaryPersonality personality,
        AiGameProfile profile,
        string language,
        string? customNotes = null,
        string? playbackNotes = null,
        bool canHearAudio = true,
        IReadOnlyList<double>? killTimesInPart = null,
        string? rewriteOf = null)
    {
        ArgumentNullException.ThrowIfNull(personality);
        var mix = personality.Normalized();
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        string senses = canHearAudio
            ? "You receive this clip as video WITH its game sound."
            : "You receive this clip as still pictures, 2 per second, each labelled with its time. There is NO sound: judge only what is visible.";
        string onlyWhat = canHearAudio ? "visible or audible" : "visible";

        string perspective = profile switch
        {
            AiGameProfile.FirstPerson => "Perspective: First-person camera. You are the player; the camera view is your eyes; visible hands and held weapon are yours.",
            AiGameProfile.ThirdPerson or AiGameProfile.Fortnite => "Perspective: Third-person camera. You are the player controlling the main character/avatar visible in the lower-center.",
            _ => "Perspective: You are the player in the video narrating what you are doing in first person."
        };

        string languageLine = string.Equals(language, "Hebrew", StringComparison.OrdinalIgnoreCase)
            ? "Language: Hebrew. Write every line in Hebrew letters, in natural spoken Hebrew the way Israeli gamers talk. Keep game, weapon and item names as they appear in the game."
            : $"Language: {language}. Write every line in {language}.";

        string notes = !string.IsNullOrWhiteSpace(customNotes)
            ? $"Additional user context: {customNotes.Trim()}\n"
            : string.Empty;

        string playback = !string.IsNullOrWhiteSpace(playbackNotes)
            ? $"How this clip plays in the finished video (times from the start of this clip):\n{playbackNotes.Trim()}\nKeep every line readable at the speed it is shown.\n"
            : string.Empty;

        string kills = killTimesInPart is null
            ? string.Empty
            : killTimesInPart.Count == 0
                ? "Kill tracker: it found NO eliminations by you in this clip. Do not claim one.\n"
                : "Kill tracker: these eliminations by YOU really happened, at "
                  + string.Join(", ", killTimesInPart.Select(t => t.ToString("0.0", ci) + "s"))
                  + " (from the start of this clip). React to them at those times. Do not claim any other elimination.\n";

        string tactical = mix.Tactical == 0
            ? string.Empty
            : $"""
When you say WHY you do something (tactics), the reason MUST come from something {onlyWhat} in the clip: your health or shield bar going down, how many enemies you can see, how far away the enemy is, a big hit landing, the enemy running low, the storm or zone edge, reloading or running out of ammo. Say the reason as a plain, short observation ("taking damage — repositioning", "two of them, I'm alone — hiding", "he's weak — chasing for the knock").
NEVER invent thoughts, hopes, plans or feelings you cannot see evidence for. If you cannot see a reason, describe only the action, or write nothing. Give such a line a low "confidence".

""";

        string chatter = mix.Chattiness switch
        {
            CommentaryChattiness.Quiet => "Write FEW lines: only the 1 or 2 biggest moments of this clip. Long silences are good.",
            CommentaryChattiness.Busy => "Talk often: a line for most actions, with only short gaps (lines never overlap).",
            _ => "Leave silence/gaps when nothing notable happens; do not chatter non-stop.",
        };

        string language2 = mix.AllowSwearing
            ? "Casual swearing is allowed when it fits the moment; never slurs, hate, or attacks on real people."
            : "Keep it clean: no swearing, no profanity, no slurs.";

        string rewrite = string.IsNullOrWhiteSpace(rewriteOf)
            ? string.Empty
            : $"REWRITE: return exactly ONE line for this moment that replaces: \"{rewriteOf.Trim()}\". Make it clearly different and better, and follow every rule above.\n";

        return $"""
You are the gamer playing this video game. Narrate what you are doing in real time in the FIRST PERSON ("I", "me", "my", "we").
{senses}
{perspective}
{mix.DescribeForPrompt()}
{languageLine}
{notes}{playback}{kills}{tactical}Rules:
1. Speak as the player experiencing the moment, not as a passive observer or esports caster.
2. Keep each commentary line SHORT: at most {mix.MaxWordsPerLine} words per line.
3. Keep each line at least {mix.MinReadableSec.ToString("0.0", ci)} seconds long so it can be easily read on screen.
4. Timestamps (start and end) must be in seconds relative to the beginning of this clip (0.0s) and must not go past its end.
5. Only narrate what is actually {onlyWhat} on screen (hits, movements, shots, retreats, eliminations). NEVER invent kills or damage not shown.
6. {chatter}
7. {language2}
8. List 1 to 2 key action words in "emphasis" for each line (e.g. "headshot", "reposition", "lasered").
{rewrite}
Return a JSON array of objects with keys: "start" (number), "end" (number), "text" (string), "emphasis" (array of strings), "confidence" (number 0.0 to 1.0).
""";
    }
}
