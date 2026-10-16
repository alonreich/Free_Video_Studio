// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS), docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AICOMMENTARY_SERVICE_01 — system instructions and structured schemas for AI live commentary generation.
/// </summary>
public static class AiCommentaryPrompts
{
    public const string CommentaryJsonSchema = "{\"type\":\"ARRAY\",\"items\":{\"type\":\"OBJECT\",\"properties\":{\"start\":{\"type\":\"NUMBER\"},\"end\":{\"type\":\"NUMBER\"},\"text\":{\"type\":\"STRING\"},\"emphasis\":{\"type\":\"ARRAY\",\"items\":{\"type\":\"STRING\"}},\"confidence\":{\"type\":\"NUMBER\"}},\"required\":[\"start\",\"end\",\"text\"]}}";

    public static string BuildPrompt(CaptionTone tone, AiGameProfile profile, string language, string? customNotes = null, string? playbackNotes = null, bool canHearAudio = true)
    {
        // AIVENDORCAPS_01 — Claude / OpenAI get silent stills: never ask them to narrate sounds they cannot hear.
        string senses = canHearAudio
            ? "You receive this clip as video WITH its game sound."
            : "You receive this clip as still pictures, 2 per second, each labelled with its time. There is NO sound: judge only what is visible.";
        string onlyWhat = canHearAudio ? "visible or audible" : "visible";
        string toneInstruction = tone switch
        {
            CaptionTone.Hype => "Tone: HYPE & CELEBRATORY. High energy, exciting reactions, hype up good plays, celebrate eliminations with intensity.",
            CaptionTone.Chill => "Tone: CHILL & CASUAL. Relaxed, calm, effortless vibe, nonchalant commentary as if chatting casually with friends.",
            CaptionTone.Funny => "Tone: FUNNY & SARCASTIC. Witty, sarcastic, playfully highlighting mistakes, fails, silly moments or dramatic overreactions.",
            CaptionTone.Tactical => "Tone: TACTICAL & ANALYTICAL. Focused callouts, strategy explanations, damage assessments, repositioning decisions.",
            _ => "Tone: Natural, engaging gaming commentary."
        };

        string perspective = profile switch
        {
            AiGameProfile.FirstPerson => "Perspective: First-person camera. You are the player; the camera view is your eyes; visible hands and held weapon are yours.",
            AiGameProfile.ThirdPerson or AiGameProfile.Fortnite => "Perspective: Third-person camera. You are the player controlling the main character/avatar visible in the lower-center.",
            _ => "Perspective: You are the player in the video narrating what you are doing in first person."
        };

        string notes = !string.IsNullOrWhiteSpace(customNotes)
            ? $"Additional user context: {customNotes.Trim()}\n"
            : string.Empty;

        string playback = !string.IsNullOrWhiteSpace(playbackNotes)
            ? $"How this clip plays in the finished video (times from the start of this clip):\n{playbackNotes.Trim()}\nKeep every line readable at the speed it is shown.\n"
            : string.Empty;

        return $"""
You are the gamer playing this video game. Narrate what you are doing in real time in the FIRST PERSON ("I", "me", "my", "we").
{senses}
{perspective}
{toneInstruction}
Language: {language}
{notes}{playback}
Rules:
1. Speak as the player experiencing the moment, not as a passive observer or esports caster.
2. Keep each commentary line SHORT: at most 8 words per line.
3. Keep each line at least 1.2 seconds long so it can be easily read on screen.
4. Timestamps (start and end) must be in seconds relative to the beginning of this clip (0.0s) and must not go past its end.
5. Only narrate what is actually {onlyWhat} on screen (hits, movements, shots, retreats, eliminations). NEVER invent kills or damage not shown.
6. Leave silence/gaps when nothing notable happens; do not chatter non-stop.
7. List 1 to 2 key action words in "emphasis" for each line (e.g. "headshot", "reposition", "lasered").

Return a JSON array of objects with keys: "start" (number), "end" (number), "text" (string), "emphasis" (array of strings), "confidence" (number 0.0 to 1.0).
""";
    }
}
