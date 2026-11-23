using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeVideoStudio.Core.Media;

public sealed class OutputTimeline
{
    public readonly record struct Chunk(
        double SourceStartSec,
        double SourceEndSec,
        double Speed,
        double FreezeHoldSec,
        string? InsertionId = null,
        bool FromSegment = false,
        bool IsCut = false)
    {
        public bool IsFreeze => Math.Abs(Speed) < 0.001 && InsertionId == null && !IsCut;

        public bool IsInsertion => InsertionId != null;

        public bool HoldsSource => Math.Abs(Speed) < 0.001 && !IsCut;

        public double OutputLengthSec =>
            IsCut ? 0.0 : (HoldsSource ? FreezeHoldSec : (SourceEndSec - SourceStartSec) / Speed);
    }

    public readonly record struct Cut(double StartSec, double EndSec)
    {
        public double LengthSec => Math.Max(0, EndSec - StartSec);
    }

    public const double CutMergeGapSec = 0.30;

    public const double MinCutLengthSec = 0.04;

    public readonly record struct Insertion(double AtSourceSec, double DurationSec, string Id);

    private readonly List<Chunk> _chunks;
    private readonly double _totalSourceSec;
    private readonly double _originSec;
    private readonly List<Cut> _cuts;

    public double TotalSourceSeconds => _totalSourceSec;

    public double OriginSec => _originSec;

    public IReadOnlyList<PlaybackSpan> DescribeSourceRange(double absStartSec, double absEndSec)
    {
        var spans = new List<PlaybackSpan>();
        double a = ToClipRelative(absStartSec), b = ToClipRelative(absEndSec);
        if (b <= a) return spans;
        foreach (var ch in _chunks)
        {
            if (ch.IsInsertion) continue;
            if (ch.HoldsSource)
            {
                if (ch.SourceStartSec >= a - 0.001 && ch.SourceStartSec <= b + 0.001)
                    spans.Add(new PlaybackSpan(_originSec + ch.SourceStartSec, _originSec + ch.SourceStartSec, 0, ch.FreezeHoldSec, false));
                continue;
            }
            double s = Math.Max(a, ch.SourceStartSec), e = Math.Min(b, ch.SourceEndSec);
            if (e <= s + 0.001) continue;
            spans.Add(new PlaybackSpan(_originSec + s, _originSec + e, ch.IsCut ? 0 : ch.Speed, 0, ch.IsCut));
        }
        return spans;
    }

    public readonly record struct PlaybackSpan(double AbsStartSec, double AbsEndSec, double Speed, double FreezeHoldSec, bool IsCut);

    public double TotalOutputSeconds { get; }

    public IReadOnlyList<Chunk> Chunks => _chunks;

    private OutputTimeline(List<Chunk> chunks, double totalSourceSec, double originSec, List<Cut>? cuts = null)
    {
        _chunks = chunks;
        _totalSourceSec = totalSourceSec;
        _originSec = originSec;
        _cuts = cuts ?? new List<Cut>();

        double total = 0;
        foreach (var c in chunks) total += c.OutputLengthSec;
        TotalOutputSeconds = Math.Max(0, total);
    }

    public static OutputTimeline Create(
        double totalDurationMs,
        IReadOnlyList<SpeedSegment>? segments,
        double baseSpeed = 1.0,
        double sourceCutStartMs = 0,
        IReadOnlyList<Insertion>? insertions = null,
        IReadOnlyList<Cut>? cuts = null)
    {
        double totalDurationSec = totalDurationMs / 1000.0;
        double timelineOriginSec = sourceCutStartMs / 1000.0;

        double ToClipRel(double absSec)
        {
            double rel = absSec - timelineOriginSec;
            return Math.Max(0, Math.Min(rel, totalDurationSec));
        }

        var normalizedSegments = new List<(double start, double end, double speed)>();
        if (segments != null)
        {
            foreach (var seg in segments)
            {
                if (Math.Abs(seg.Speed) < 0.001)
                {
                    double fStart = ToClipRel(seg.StartMs / 1000.0);
                    double fDur = Math.Max(0.001, (seg.EndMs - seg.StartMs) / 1000.0);
                    normalizedSegments.Add((fStart, fStart + fDur, 0.0));
                    continue;
                }
                double start = ToClipRel(seg.StartMs / 1000.0);
                double end = ToClipRel(seg.EndMs / 1000.0);
                if (end <= start + 0.001) continue;
                normalizedSegments.Add((start, end, seg.Speed));
            }
        }
        normalizedSegments.Sort((a, b) => a.start.CompareTo(b.start));

        var sourceChunks = new List<(double start, double end, double speed, bool fromSeg)>();
        double currentSec = 0;
        foreach (var seg in normalizedSegments.Where(s => Math.Abs(s.speed) > 0.001))
        {
            double sStart = Math.Max(seg.start, currentSec);
            if (seg.end <= sStart + 0.001) continue;
            if (sStart > currentSec + 0.001)
                sourceChunks.Add((currentSec, sStart, baseSpeed, false));
            sourceChunks.Add((sStart, seg.end, seg.speed, true));
            currentSec = seg.end;
        }
        if (currentSec < totalDurationSec - 0.001)
            sourceChunks.Add((currentSec, totalDurationSec, baseSpeed, false));

        var normalizedCuts = NormalizeCuts(cuts, totalDurationSec);

        var chunks = new List<Chunk>();
        double sourceCursor = 0;

        bool InsideCut(double at)
        {
            foreach (var c in normalizedCuts)
                if (at > c.StartSec - 0.0005 && at < c.EndSec - 0.0005) return true;
            return false;
        }

        double PushPastCut(double at)
        {
            foreach (var c in normalizedCuts)
                if (at > c.StartSec - 0.0005 && at < c.EndSec - 0.0005) return c.EndSec;
            return at;
        }

        void AppendSourceRange(double rangeStart, double rangeEnd)
        {
            foreach (var sc in sourceChunks)
            {
                double overlapStart = Math.Max(rangeStart, sc.start);
                double overlapEnd = Math.Min(rangeEnd, sc.end);
                if (overlapEnd <= overlapStart + 0.001) continue;

                double cursor = overlapStart;
                foreach (var cut in normalizedCuts)
                {
                    if (cut.EndSec <= cursor + 0.0005) continue;
                    if (cut.StartSec >= overlapEnd - 0.0005) break;

                    double holeStart = Math.Max(cursor, cut.StartSec);
                    double holeEnd = Math.Min(overlapEnd, cut.EndSec);
                    if (holeEnd <= holeStart + 0.0005) continue;

                    if (holeStart > cursor + 0.001)
                        chunks.Add(new Chunk(cursor, holeStart, sc.speed, 0, null, sc.fromSeg));

                    chunks.Add(new Chunk(holeStart, holeEnd, sc.speed, 0, null, sc.fromSeg, IsCut: true));
                    cursor = holeEnd;
                }

                if (overlapEnd > cursor + 0.001)
                    chunks.Add(new Chunk(cursor, overlapEnd, sc.speed, 0, null, sc.fromSeg));
            }
        }

        foreach (var freeze in normalizedSegments.Where(s => Math.Abs(s.speed) < 0.001).OrderBy(f => f.start))
        {
            double fStart = Math.Max(sourceCursor, freeze.start);
            double fEnd = Math.Max(fStart, freeze.end);
            if (fEnd <= sourceCursor + 0.001) continue;

            if (InsideCut(fStart)) continue;

            if (fStart > sourceCursor + 0.001)
                AppendSourceRange(sourceCursor, fStart);

            double fDur = Math.Max(0.001, fEnd - fStart);
            chunks.Add(new Chunk(fStart, fStart + 0.001, 0, fDur));
            sourceCursor = fStart;
        }
        if (totalDurationSec > sourceCursor + 0.001)
            AppendSourceRange(sourceCursor, totalDurationSec);

        if (insertions != null && insertions.Count > 0)
        {
            var ordered = new List<Insertion>(insertions);
            ordered.Sort((a, b) => a.AtSourceSec.CompareTo(b.AtSourceSec));

            foreach (var ins in ordered)
            {
                if (ins.DurationSec <= 0.001) continue;
                double at = Math.Max(0, Math.Min(ins.AtSourceSec, totalDurationSec));

                at = PushPastCut(at);

                for (int i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    if (c.HoldsSource) continue;
                    if (at > c.SourceStartSec + 0.0005 && at < c.SourceEndSec - 0.0005)
                    {
                        chunks[i] = new Chunk(c.SourceStartSec, at, c.Speed, 0, null, c.FromSegment);
                        chunks.Insert(i + 1, new Chunk(at, c.SourceEndSec, c.Speed, 0, null, c.FromSegment));
                        break;
                    }
                }

                int idx = chunks.Count;
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (chunks[i].SourceStartSec >= at - 0.0005 && !chunks[i].HoldsSource) { idx = i; break; }
                }

                chunks.Insert(Math.Clamp(idx, 0, chunks.Count),
                    new Chunk(at, at + 0.001, 0, ins.DurationSec, ins.Id));
            }
        }

        return new OutputTimeline(chunks, totalDurationSec, timelineOriginSec, normalizedCuts);
    }

    public static List<Cut> NormalizeCuts(IReadOnlyList<Cut>? cuts, double totalDurationSec)
    {
        var result = new List<Cut>();
        if (cuts == null || cuts.Count == 0) return result;

        var ordered = new List<Cut>();
        foreach (var c in cuts)
        {
            double start = Math.Max(0, Math.Min(c.StartSec, totalDurationSec));
            double end = Math.Max(0, Math.Min(c.EndSec, totalDurationSec));
            if (end < start) (start, end) = (end, start);
            if (end - start < MinCutLengthSec) continue;
            ordered.Add(new Cut(start, end));
        }
        if (ordered.Count == 0) return result;

        ordered.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));

        var current = ordered[0];
        for (int i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.StartSec <= current.EndSec + CutMergeGapSec)
            {
                if (next.EndSec > current.EndSec) current = new Cut(current.StartSec, next.EndSec);
            }
            else
            {
                result.Add(current);
                current = next;
            }
        }
        result.Add(current);

        return result;
    }

    public double ToClipRelative(double absSourceSec)
    {
        double rel = absSourceSec - _originSec;
        return Math.Max(0, Math.Min(rel, _totalSourceSec));
    }

    public double SourceToOutput(double absSourceSec)
    {
        double target = ToClipRelative(absSourceSec);
        double mapped = 0;

        foreach (var ch in _chunks)
        {
            if (ch.IsCut)
            {
                if (target <= ch.SourceStartSec) break;
                continue;
            }

            if (ch.HoldsSource)
            {
                if (target >= ch.SourceStartSec) mapped += ch.FreezeHoldSec;
                continue;
            }

            if (target <= ch.SourceStartSec) break;
            if (target >= ch.SourceEndSec) mapped += (ch.SourceEndSec - ch.SourceStartSec) / ch.Speed;
            else
            {
                mapped += (target - ch.SourceStartSec) / ch.Speed;
                break;
            }
        }

        return Math.Max(0, mapped);
    }

    public double OutputToSourceRelative(double outputSec)
    {
        double target = Math.Max(0, outputSec);
        double acc = 0;

        foreach (var ch in _chunks)
        {
            if (ch.IsCut) continue;

            double outLen = ch.OutputLengthSec;
            if (target <= acc + outLen)
            {
                if (ch.HoldsSource) return Math.Clamp(ch.SourceStartSec, 0, _totalSourceSec);
                return Math.Clamp(ch.SourceStartSec + (target - acc) * ch.Speed, 0, _totalSourceSec);
            }
            acc += outLen;
        }

        return _totalSourceSec;
    }

    public bool IsHoldingFrameAt(double outputSec)
    {
        double target = Math.Max(0, outputSec);
        double acc = 0;

        foreach (var ch in _chunks)
        {
            if (ch.IsCut) continue;

            double outLen = ch.OutputLengthSec;
            if (target <= acc + outLen) return ch.IsFreeze;
            acc += outLen;
        }

        return false;
    }

    public string? InsertionAt(double outputSec)
    {
        double target = Math.Max(0, outputSec);
        double acc = 0;

        foreach (var ch in _chunks)
        {
            if (ch.IsCut) continue;

            double outLen = ch.OutputLengthSec;
            if (target <= acc + outLen) return ch.InsertionId;
            acc += outLen;
        }

        return null;
    }

    public double SnapInsertionPoint(double sourceRelSec)
    {
        double at = Math.Clamp(sourceRelSec, 0, _totalSourceSec);

        at = NextSurvivingSource(at);

        foreach (var ch in _chunks)
        {
            if (ch.IsInsertion || ch.IsFreeze || ch.IsCut || !ch.FromSegment) continue;
            if (at > ch.SourceStartSec + 0.0005 && at < ch.SourceEndSec - 0.0005)
                return ch.SourceEndSec;
        }

        return at;
    }

    public bool HasInsertionAtSource(double snappedSourceRelSec)
    {
        foreach (var ch in _chunks)
            if (ch.IsInsertion && Math.Abs(ch.SourceStartSec - snappedSourceRelSec) < 0.0015) return true;
        return false;
    }

    public IReadOnlyList<Cut> Cuts => _cuts;

    public bool HasCuts => _cuts.Count > 0;

    public double SurvivingSourceSeconds
    {
        get
        {
            double removed = 0;
            foreach (var c in _cuts) removed += c.LengthSec;
            return Math.Max(0, _totalSourceSec - removed);
        }
    }

    public double RemovedSourceSeconds => Math.Max(0, _totalSourceSec - SurvivingSourceSeconds);

    public bool IsCutAtSource(double sourceRelSec)
    {
        foreach (var c in _cuts)
            if (sourceRelSec > c.StartSec - 0.0005 && sourceRelSec < c.EndSec - 0.0005) return true;
        return false;
    }

    public double NextSurvivingSource(double sourceRelSec)
    {
        double at = Math.Clamp(sourceRelSec, 0, _totalSourceSec);
        foreach (var c in _cuts)
        {
            if (c.EndSec <= at + 0.0005) continue;
            if (at > c.StartSec - 0.0005 && at < c.EndSec - 0.0005) return Math.Min(c.EndSec, _totalSourceSec);
            if (c.StartSec > at) break;
        }
        return at;
    }

    public List<double> CutMarkerOutputPositions()
    {
        var marks = new List<double>();
        foreach (var c in _cuts) marks.Add(SourceToOutput(_originSec + c.StartSec));
        return marks;
    }

    public List<(string Id, double StartOutputSec, double EndOutputSec)> InsertionOutputRanges() => Ranges(ch => ch.IsInsertion && ch.InsertionId != null, ch => ch.InsertionId!);

    public List<(double StartOutputSec, double EndOutputSec)> FreezeOutputRanges() =>
        Ranges(ch => ch.IsFreeze, _ => "").Select(r => (r.StartOutputSec, r.EndOutputSec)).ToList();

    private List<(string Id, double StartOutputSec, double EndOutputSec)> Ranges(Func<Chunk, bool> pick, Func<Chunk, string> id)
    {
        var ranges = new List<(string, double, double)>();
        double acc = 0;

        foreach (var ch in _chunks)
        {
            if (ch.IsCut) continue;

            double outLen = ch.OutputLengthSec;
            if (pick(ch))
            {
                ranges.Add((id(ch), acc, acc + outLen));
            }
            acc += outLen;
        }

        return ranges;
    }

    public int ExtraChunkCost()
    {
        int cost = 0;
        foreach (var c in _cuts)
        {
            bool atStart = c.StartSec <= 0.001;
            bool atEnd = c.EndSec >= _totalSourceSec - 0.001;
            if (!atStart && !atEnd) cost++;
        }
        return cost;
    }
}
