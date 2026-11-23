using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.Core.Editing;

public sealed partial class GranularEditSession
{
    public const int SegMinWidthMs = 200;

    public const int SegGapMs = 0;

    public const double MinFreezeDurationS = 0.2;

    public const double MaxFreezeDurationS = 10.0;

    private readonly GranularHistoryParking _parking;

    public GranularEditSession(string videoPath, double trimStartMs, double trimEndMs, GranularHistoryParking parking)
    {
        ArgumentNullException.ThrowIfNull(parking);
        VideoPath = videoPath ?? string.Empty;
        TrimStartMs = trimStartMs;
        TrimEndMs = trimEndMs;
        _parking = parking;
    }

    public string VideoPath { get; }

    public double TrimStartMs { get; }

    public double TrimEndMs { get; private set; }

    public double ProbedDurationSec { get; private set; }

    public bool IsMergeMode => VideoPath.StartsWith("edl://", StringComparison.Ordinal);

    public bool IsMobileFormat { get; set; }

    public string OriginalResolution { get; set; } = "1920x1080";

    public List<SpeedSegment> Segments { get; } = new();

    public List<CutRange> Cuts { get; } = new();

    public List<MemePlacement> Memes { get; } = new();

    public double BaseSpeed { get; set; } = 1.1;

    public double FreezeTimeMs { get; set; } = -1;

    public double FreezeDurationS { get; set; } = 1.0;

    public double SelectedFreezePresetS { get; set; } = -1.0;

    public bool HasFreeze => FreezeTimeMs >= 0;

    public int SelectedSegmentIndex { get; set; } = -1;

    public string? SelectedMemeId { get; set; }

    public int NextMemeIdIndex { get; private set; }

    public int PendingStartMs { get; set; } = -1;

    public int PendingEndMs { get; set; } = -1;

    public double PendingSpeed { get; set; } = 1.1;

    public bool HasSelectedSegment => SelectedSegmentIndex >= 0 && SelectedSegmentIndex < Segments.Count;

    public SpeedSegment? SelectedSegment => HasSelectedSegment ? Segments[SelectedSegmentIndex] : null;

    public CaptionTrack? CaptionTrack { get; set; }

    public void AdoptClipDuration(double durationSec, bool fromProbe)
    {
        if (durationSec <= 0) return;
        if (fromProbe) ProbedDurationSec = durationSec;
        if (TrimEndMs <= 0) TrimEndMs = durationSec * 1000.0;
    }

    public void SeedCutsFromAbsolute(IEnumerable<CutRange>? absoluteCuts)
    {
        if (absoluteCuts == null) return;
        foreach (var c in absoluteCuts) Cuts.Add(new CutRange(c.StartMs - TrimStartMs, c.EndMs - TrimStartMs));
    }

    public void SeedMemes(IEnumerable<MemePlacement>? memes)
    {
        if (memes != null) Memes.AddRange(memes);
    }

    public void SeedSegmentsFromAbsolute(IEnumerable<SpeedSegment>? absoluteSegments)
    {
        if (absoluteSegments == null) return;
        foreach (var seg in absoluteSegments)
        {
            int relStart = (int)(seg.StartMs - TrimStartMs);
            int relEnd = (int)(seg.EndMs - TrimStartMs);
            if (relEnd > 0 && relStart < (int)(TrimEndMs - TrimStartMs))
            {
                relStart = Math.Max(0, relStart);
                int maxEnd = TrimEndMs > 0 ? (int)(TrimEndMs - TrimStartMs) : int.MaxValue;
                relEnd = Math.Min(relEnd, maxEnd);
                Segments.Add(new SpeedSegment(relStart, relEnd, seg.Speed,
                    seg.ZoomX, seg.ZoomY, seg.ZoomW, seg.ZoomH, seg.ZoomOrigRes, seg.ZoomSlow,
                    seg.ZoomStartMs.HasValue ? seg.ZoomStartMs.Value - TrimStartMs : (double?)null,
                    seg.ZoomEndMs.HasValue ? seg.ZoomEndMs.Value - TrimStartMs : (double?)null,
                    seg.AiTrackingTrajectory));
            }
        }
    }

    public IReadOnlyList<SpeedSegment> ResultSegments => Segments
        .Select(s => s with
        {
            StartMs = s.StartMs + TrimStartMs,
            EndMs = s.EndMs + TrimStartMs,
            ZoomStartMs = s.ZoomStartMs.HasValue ? s.ZoomStartMs.Value + TrimStartMs : (double?)null,
            ZoomEndMs = s.ZoomEndMs.HasValue ? s.ZoomEndMs.Value + TrimStartMs : (double?)null
        })
        .ToList()
        .AsReadOnly();

    public IReadOnlyList<MemePlacement> ResultMemes =>
        Memes.OrderBy(m => m.AtSourceSecRelative).ToList().AsReadOnly();

    public IReadOnlyList<CutRange> ResultCuts => Cuts
        .Select(c => new CutRange(c.StartMs + TrimStartMs, c.EndMs + TrimStartMs))
        .ToList()
        .AsReadOnly();

    private string _openingSignature = "";

    public void MarkOpened() => _openingSignature = BuildStateSignature();

    public string BuildStateSignature()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(BaseSpeed.ToString("0.###", ci)).Append('|');
        sb.Append(FreezeTimeMs.ToString("0.###", ci)).Append('|');
        sb.Append(FreezeDurationS.ToString("0.###", ci)).Append('|');
        foreach (var s in Segments)
        {
            sb.Append(s.StartMs.ToString("0.###", ci)).Append(',')
              .Append(s.EndMs.ToString("0.###", ci)).Append(',')
              .Append(s.Speed.ToString("0.###", ci)).Append(',')
              .Append(s.ZoomX?.ToString() ?? "-").Append(',')
              .Append(s.ZoomY?.ToString() ?? "-").Append(',')
              .Append(s.ZoomW?.ToString() ?? "-").Append(',')
              .Append(s.ZoomH?.ToString() ?? "-").Append(',')
              .Append(s.ZoomSlow ? "S" : "I").Append(',')
              .Append(s.ZoomStartMs?.ToString("0.###", ci) ?? "-").Append(',')
              .Append(s.ZoomEndMs?.ToString("0.###", ci) ?? "-").Append(',')
              .Append(string.IsNullOrEmpty(s.AiTrackingTrajectory) ? "-" : s.AiTrackingTrajectory.Length.ToString(ci)).Append(';');
        }
        return sb.ToString();
    }

    public bool IsDirty => BuildStateSignature() != _openingSignature || PendingStartMs >= 0 || PendingEndMs >= 0;

    public double SpeedAt(double relPosMs)
    {
        foreach (var seg in Segments)
            if (relPosMs >= seg.StartMs && relPosMs < seg.EndMs) return seg.Speed;
        return BaseSpeed;
    }

    public int? FindSegmentAt(int positionMs)
    {
        for (int i = 0; i < Segments.Count; i++)
            if (positionMs >= Segments[i].StartMs && positionMs <= Segments[i].EndMs) return i;
        return null;
    }

    public (double startMs, double endMs)? CurrentMarkedRange()
    {
        if (PendingStartMs >= 0 && PendingEndMs >= 0 && PendingEndMs > PendingStartMs)
            return (PendingStartMs, PendingEndMs);

        if (SelectedSegment is { } seg && seg.EndMs > seg.StartMs) return (seg.StartMs, seg.EndMs);
        return null;
    }

    public void ClearPendingMarks()
    {
        PendingStartMs = -1;
        PendingEndMs = -1;
    }

    public string NewMemeId()
    {
        string id = MemePlacement.NewId(NextMemeIdIndex++);
        while (Memes.Any(m => m.Id == id)) id = MemePlacement.NewId(NextMemeIdIndex++);
        return id;
    }

    public static string FormatMs(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    public string HistoryKey =>
        string.IsNullOrWhiteSpace(VideoPath) || IsMergeMode ? string.Empty : Path.GetFullPath(VideoPath).ToUpperInvariant();
}
