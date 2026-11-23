using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FreeVideoStudio.Core.Media;

public sealed record MergeClipSource(string Path, double DurationSec, double TaggedIntroSec, double TrimStartSec = 0, double TrimEndSec = 0);

public readonly record struct MergedClip(
    int Index,
    string Path,
    double ContentStartSec,
    double ContentEndSec,
    double MergedStartSec,
    double RemovedIntroSec,
    double MergedLengthSec = -1)
{
    public double LengthSec => Math.Max(0, ContentEndSec - ContentStartSec);
    public double MergedEndSec => MergedStartSec + (MergedLengthSec >= 0 ? MergedLengthSec : LengthSec);
}

public sealed class MergedTimeline
{
    public IReadOnlyList<MergedClip> Clips { get; }
    public double TotalSec { get; }
    public bool ScraperEnabled { get; }
    public bool CustomThumbnail { get; }

    public double SyntheticIntroSec => CustomThumbnail ? IntroTag.StandardIntroSec : 0;

    public string Signature { get; }

    public CompositeTimeline Composite { get; }

    public static readonly MergedTimeline Empty = new(Array.Empty<MergedClip>(), false, false, CompositeTimeline.Build(MergeEdl.Empty));

    private MergedTimeline(IReadOnlyList<MergedClip> clips, bool scraper, bool custom, CompositeTimeline composite)
    {
        Composite = composite;
        Clips = clips;
        ScraperEnabled = scraper;
        CustomThumbnail = custom;
        double total = 0;
        var sig = new StringBuilder();
        foreach (var c in clips)
        {
            total = c.MergedEndSec;
            sig.Append(c.Path).Append('|')
               .Append(c.ContentStartSec.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
               .Append(c.ContentEndSec.ToString("F4", CultureInfo.InvariantCulture)).Append(';');
        }
        TotalSec = total;
        Signature = sig.ToString();
    }

    public static MergedTimeline Build(IReadOnlyList<MergeClipSource> sources, bool scraperEnabled, bool customThumbnail)
    {
        static long Us(double sec) => double.IsFinite(sec) && sec > 0 ? (long)Math.Round(sec * 1_000_000.0) : 0;

        var edlClips = new List<EdlClip>(sources.Count);
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            edlClips.Add(new EdlClip
            {
                ClipId = IndexId(i),
                Path = s.Path,
                DurationUs = Us(s.DurationSec),
                InUs = Us(s.TrimStartSec),
                OutUs = Us(s.TrimEndSec),
                IntroCutUs = Us(s.TaggedIntroSec),
            });
        }
        var edl = new MergeEdl
        {
            Clips = edlClips,
            ScraperEnabled = scraperEnabled,
            Thumbnail = customThumbnail && edlClips.Count > 0 ? new EdlThumbnail(new EdlAnchor(edlClips[0].ClipId, 0)) : null,
        };
        var composite = CompositeTimeline.Build(edl);

        var clips = new List<MergedClip>(sources.Count);
        foreach (var c in composite.Clips)
        {
            clips.Add(new MergedClip(
                c.Index, c.Path,
                c.KeepInUs / 1_000_000.0, c.KeepOutUs / 1_000_000.0,
                c.MergedStartFrame / (double)CompositeTimeline.MergeFps,
                c.RemovedIntroUs / 1_000_000.0,
                c.MergedFrames / (double)CompositeTimeline.MergeFps));
        }
        return new MergedTimeline(clips, scraperEnabled, customThumbnail, composite);
    }

    private static Guid IndexId(int index) => new(index, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public (int ClipIndex, double SourceSec) Locate(double mergedSec)
    {
        if (Clips.Count == 0) return (-1, 0);
        double t = Math.Clamp(mergedSec, 0, TotalSec);
        for (int i = 0; i < Clips.Count; i++)
        {
            var c = Clips[i];
            bool last = i == Clips.Count - 1;
            if (t < c.MergedEndSec || last)
                return (i, Math.Clamp(c.ContentStartSec + (t - c.MergedStartSec), c.ContentStartSec, c.ContentEndSec));
        }
        var tail = Clips[^1];
        return (tail.Index, tail.ContentEndSec);
    }

    public double ToMerged(int clipIndex, double sourceSec)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return clipIndex < 0 ? 0 : TotalSec;
        var c = Clips[clipIndex];
        return Math.Min(c.MergedEndSec, c.MergedStartSec + (Math.Clamp(sourceSec, c.ContentStartSec, c.ContentEndSec) - c.ContentStartSec));
    }

    public double Remap(MergedTimeline from, double mergedSec)
    {
        if (!SameQueue(from)) return Math.Clamp(mergedSec, 0, TotalSec);
        var (clip, src) = from.Locate(mergedSec);
        return ToMerged(clip, src);
    }

    public bool SameQueue(MergedTimeline other)
    {
        if (other.Clips.Count != Clips.Count) return false;
        for (int i = 0; i < Clips.Count; i++)
            if (!string.Equals(other.Clips[i].Path, Clips[i].Path, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public List<double> ContentStarts()
    {
        var list = new List<double>(Clips.Count);
        foreach (var c in Clips) list.Add(c.ContentStartSec);
        return list;
    }
}
