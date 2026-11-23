using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FreeVideoStudio.Core.Media;

public enum CaptionStyle
{
    Karaoke,
    PopIn,
    Typewriter
}

public enum CaptionPosition
{
    BottomContent,
    BottomVoid,
    Top,
    Middle
}

public enum CaptionLook
{
    Outline,

    Box
}

public enum CaptionSize
{
    Small,
    Medium,
    Large
}

public enum CaptionTone
{
    Hype,
    Chill,
    Funny,
    Tactical
}

public sealed record CaptionWord(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("start")] double StartSec,
    [property: JsonPropertyName("end")] double EndSec,
    [property: JsonPropertyName("syllables")] int Syllables = 1);

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

public sealed record MappedCaptionWord(
    string Text,
    double OutputStartSec,
    double OutputEndSec,
    int DurationCentisec,
    bool IsEmphasized);

public sealed record MappedCaptionLine(
    int Id,
    double OutputStartSec,
    double OutputEndSec,
    string Text,
    IReadOnlyList<MappedCaptionWord> Words,
    IReadOnlyList<string> Emphasis,
    double Confidence);

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
