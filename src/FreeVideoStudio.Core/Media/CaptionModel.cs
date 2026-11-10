using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FreeVideoStudio.Core.Media;

/// <summary>AICAPTION_MODEL_01 — presentation style for live commentary captions.</summary>
public enum CaptionStyle
{
    Karaoke,
    PopIn,
    Typewriter
}

/// <summary>
/// AICAPTION_MODEL_01 — screen placement for live commentary captions. The options window offers three:
/// <see cref="Top"/> (upper third), <see cref="Middle"/> (just under the centre, CAPTIONPLACE_01) and
/// <see cref="BottomContent"/> (lower third). Values are persisted by NAME; new ones are appended.
/// </summary>
public enum CaptionPosition
{
    BottomContent,
    BottomVoid,
    Top,
    Middle
}

/// <summary>CAPTIONLOOK_01 — how caption text is separated from the game picture.</summary>
public enum CaptionLook
{
    /// <summary>Thick dark outline + strong shadow (the default).</summary>
    Outline,

    /// <summary>A see-through dark box behind each line.</summary>
    Box
}

/// <summary>CAPTIONSIZE_01 — caption text size; <see cref="Medium"/> is the size used before this option existed.</summary>
public enum CaptionSize
{
    Small,
    Medium,
    Large
}

/// <summary>AICAPTION_MODEL_01 — emotional/narration tone preset for commentary generation.</summary>
public enum CaptionTone
{
    Hype,
    Chill,
    Funny,
    Tactical
}

/// <summary>
/// AICAPTION_MODEL_01 — one word within a commentary caption line, with source timing and syllable count.
/// </summary>
public sealed record CaptionWord(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("start")] double StartSec,
    [property: JsonPropertyName("end")] double EndSec,
    [property: JsonPropertyName("syllables")] int Syllables = 1);

/// <summary>
/// AICAPTION_MODEL_01 — one narration line in source media time.
/// </summary>
public sealed record CaptionLine(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("start")] double SourceStartSec,
    [property: JsonPropertyName("end")] double SourceEndSec,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("emphasis")] IReadOnlyList<string>? Emphasis = null,
    [property: JsonPropertyName("confidence")] double Confidence = 1.0,
    [property: JsonPropertyName("words")] IReadOnlyList<CaptionWord>? Words = null)
{
    public bool Equals(CaptionLine? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id &&
               SourceStartSec.Equals(other.SourceStartSec) &&
               SourceEndSec.Equals(other.SourceEndSec) &&
               string.Equals(Text, other.Text, StringComparison.Ordinal) &&
               Confidence.Equals(other.Confidence) &&
               ListEquals(Emphasis, other.Emphasis) &&
               ListEquals(Words, other.Words);
    }

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Id);
        h.Add(SourceStartSec);
        h.Add(SourceEndSec);
        h.Add(Text, StringComparer.Ordinal);
        h.Add(Confidence);
        if (Emphasis != null)
        {
            h.Add(Emphasis.Count);
            foreach (var e in Emphasis) h.Add(e, StringComparer.Ordinal);
        }
        if (Words != null)
        {
            h.Add(Words.Count);
            foreach (var w in Words) h.Add(w);
        }
        return h.ToHashCode();
    }

    private static bool ListEquals<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
        }
        return true;
    }
}

/// <summary>
/// AICAPTION_MODEL_01 — full commentary track containing lines, style, placement, tone, and language.
/// </summary>
public sealed record CaptionTrack(
    [property: JsonPropertyName("style")] CaptionStyle Style = CaptionStyle.Karaoke,
    [property: JsonPropertyName("position")] CaptionPosition Position = CaptionPosition.BottomContent,
    [property: JsonPropertyName("tone")] CaptionTone Tone = CaptionTone.Hype,
    [property: JsonPropertyName("language")] string Language = "en",
    [property: JsonPropertyName("lines")] IReadOnlyList<CaptionLine>? Lines = null,
    [property: JsonPropertyName("look")] CaptionLook Look = CaptionLook.Outline,
    [property: JsonPropertyName("size")] CaptionSize Size = CaptionSize.Medium)
{
    public bool Equals(CaptionTrack? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Style == other.Style &&
               Position == other.Position &&
               Tone == other.Tone &&
               string.Equals(Language, other.Language, StringComparison.Ordinal) &&
               Look == other.Look &&
               Size == other.Size &&
               ListEquals(Lines, other.Lines);
    }

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Style);
        h.Add(Position);
        h.Add(Tone);
        h.Add(Language, StringComparer.Ordinal);
        h.Add(Look);
        h.Add(Size);
        if (Lines != null)
        {
            h.Add(Lines.Count);
            foreach (var l in Lines) h.Add(l);
        }
        return h.ToHashCode();
    }

    private static bool ListEquals<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
        }
        return true;
    }
}

/// <summary>
/// AICAPTION_MODEL_01 — one word mapped to finished-video output timeline with duration in centiseconds.
/// </summary>
public sealed record MappedCaptionWord(
    string Text,
    double OutputStartSec,
    double OutputEndSec,
    int DurationCentisec,
    bool IsEmphasized);

/// <summary>
/// AICAPTION_MODEL_01 — one narration line mapped to finished-video output timeline.
/// </summary>
public sealed record MappedCaptionLine(
    int Id,
    double OutputStartSec,
    double OutputEndSec,
    string Text,
    IReadOnlyList<MappedCaptionWord> Words,
    IReadOnlyList<string> Emphasis,
    double Confidence);

/// <summary>
/// AICAPTION_MODEL_01 — JSON serializer context for NativeAOT compliance.
/// </summary>
[JsonSerializable(typeof(CaptionWord))]
[JsonSerializable(typeof(List<CaptionWord>))]
[JsonSerializable(typeof(CaptionLine))]
[JsonSerializable(typeof(List<CaptionLine>))]
[JsonSerializable(typeof(CaptionTrack))]
[JsonSerializable(typeof(CaptionStyle))]
[JsonSerializable(typeof(CaptionPosition))]
[JsonSerializable(typeof(CaptionTone))]
[JsonSerializable(typeof(CaptionLook))]
[JsonSerializable(typeof(CaptionSize))]
public partial class CaptionJsonContext : JsonSerializerContext { }
