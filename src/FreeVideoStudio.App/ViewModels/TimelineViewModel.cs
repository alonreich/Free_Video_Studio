
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

public sealed class TimelineViewModel : ViewModelBase
{
    private double _loadedVideoDurationMs;
    private double _currentPositionMs;
    private double _trimStartMs;
    private double _trimEndMs;
    private bool _trimStartSet;
    private bool _trimEndSet;
    private double _thumbnailPosMs;
    private bool _thumbnailSet;
    private double _baseSpeed = SpeedPresetButtons.NativeDefaultSpeed;
    private int _mainSpeedSliderValue = 11;
    private double _freezeTimeMs = -1;
    private double _freezeDurationS = 1.0;
    private bool _isCurrentlyFrozen;
    private string _timeElapsedText = "00:00:00";
    private string _timeRemainingText = "-00:00:00";
    private string _speedLabelText = "1.1x — Normal";
    private string _speedLabelColor = "White";

    public List<CutRange> Cuts { get; } = new();
    public List<SpeedSegment> SpeedSegments { get; } = new();
    public List<MemePlacement> MemePlacements { get; } = new();

    public double LoadedVideoDurationMs
    {
        get => _loadedVideoDurationMs;
        set => SetProperty(ref _loadedVideoDurationMs, value);
    }

    public double DurationMs
    {
        get => _loadedVideoDurationMs;
        set => SetProperty(ref _loadedVideoDurationMs, value);
    }

    public double CurrentPositionMs
    {
        get => _currentPositionMs;
        set
        {
            if (SetProperty(ref _currentPositionMs, value))
            {
                UpdateFormattedTimes();
            }
        }
    }

    public double TrimStartMs
    {
        get => _trimStartMs;
        set => SetProperty(ref _trimStartMs, value);
    }

    public double TrimEndMs
    {
        get => _trimEndMs;
        set => SetProperty(ref _trimEndMs, value);
    }

    public bool IsTrimStartSet
    {
        get => _trimStartSet;
        set => SetProperty(ref _trimStartSet, value);
    }

    public bool IsTrimEndSet
    {
        get => _trimEndSet;
        set => SetProperty(ref _trimEndSet, value);
    }

    public double ThumbnailPosMs
    {
        get => _thumbnailPosMs;
        set => SetProperty(ref _thumbnailPosMs, value);
    }

    public bool IsThumbnailSet
    {
        get => _thumbnailSet;
        set => SetProperty(ref _thumbnailSet, value);
    }

    public double BaseSpeed
    {
        get => _baseSpeed;
        set
        {
            if (SetProperty(ref _baseSpeed, Math.Clamp(value, 0.1, 4.0)))
            {
                UpdateSpeedLabel();
            }
        }
    }

    public int MainSpeedSliderValue
    {
        get => _mainSpeedSliderValue;
        set
        {
            if (SetProperty(ref _mainSpeedSliderValue, value))
            {
                BaseSpeed = value / 10.0;
            }
        }
    }

    public double FreezeTimeMs
    {
        get => _freezeTimeMs;
        set => SetProperty(ref _freezeTimeMs, value);
    }

    public double FreezeDurationS
    {
        get => _freezeDurationS;
        set => SetProperty(ref _freezeDurationS, value);
    }

    public bool IsCurrentlyFrozen
    {
        get => _isCurrentlyFrozen;
        set => SetProperty(ref _isCurrentlyFrozen, value);
    }

    public string TimeElapsedText
    {
        get => _timeElapsedText;
        set => SetProperty(ref _timeElapsedText, value);
    }

    public string TimeRemainingText
    {
        get => _timeRemainingText;
        set => SetProperty(ref _timeRemainingText, value);
    }

    public string SpeedLabelText
    {
        get => _speedLabelText;
        set => SetProperty(ref _speedLabelText, value);
    }

    public string SpeedLabelColor
    {
        get => _speedLabelColor;
        set => SetProperty(ref _speedLabelColor, value);
    }

    public void SetTrimStart(double valueMs)
    {
        TrimStartMs = Math.Max(0, valueMs);
        IsTrimStartSet = true;
        if (!IsTrimEndSet || TrimEndMs <= TrimStartMs)
        {
            TrimEndMs = LoadedVideoDurationMs > 0 ? LoadedVideoDurationMs : TrimStartMs + 1000.0;
            IsTrimEndSet = true;
        }
    }

    public void EnsureTrimPointsSet()
    {
        if (!IsTrimEndSet || TrimEndMs <= 0)
        {
            TrimEndMs = LoadedVideoDurationMs;
            IsTrimEndSet = true;
        }
        if (!IsTrimStartSet)
        {
            TrimStartMs = 0;
            IsTrimStartSet = true;
        }
    }

    public void UpdateFormattedTimes(double? curSec = null, double? durSec = null)
    {
        double cur = curSec ?? (CurrentPositionMs / 1000.0);
        double dur = durSec ?? (LoadedVideoDurationMs / 1000.0);
        double rem = Math.Max(0, dur - cur);

        TimeElapsedText = FormatTime(TimeSpan.FromSeconds(cur), dur);
        TimeRemainingText = "-" + FormatTime(TimeSpan.FromSeconds(rem), dur);
    }

    public static string FormatTime(TimeSpan time, double durationSeconds = 0, bool includeMilliseconds = false)
    {
        bool showHours = durationSeconds >= 3600 || time.TotalHours >= 1;
        if (showHours)
            return includeMilliseconds ? time.ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture) : time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        else
            return includeMilliseconds ? time.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture) : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    public void ApplyMainSpeedPreset(double speed)
    {
        BaseSpeed = Math.Clamp(speed, 0.1, 4.0);
        MainSpeedSliderValue = (int)Math.Round(BaseSpeed * 10.0, MidpointRounding.AwayFromZero);
    }

    private void UpdateSpeedLabel()
    {
        double speed = BaseSpeed;
        string desc;
        string color;

        if (speed <= 0.5) { desc = "Slow Motion"; color = "#3498db"; }
        else if (speed <= 0.8) { desc = "Cinematic"; color = "#3498db"; }
        else if (speed < 1.05) { desc = "Normal"; color = "White"; }
        else if (speed <= 1.2) { desc = "Slight Boost"; color = "#f1c40f"; }
        else if (speed <= 1.5) { desc = "Fast"; color = "#f39c12"; }
        else if (speed <= 2.0) { desc = "Very Fast"; color = "#e67e22"; }
        else if (speed <= 3.0) { desc = "Turbo"; color = "#e74c3c"; }
        else { desc = "Extreme"; color = "#e74c3c"; }

        SpeedLabelText = $"{speed:F1}x — {desc}";
        SpeedLabelColor = color;
    }

    public List<SpeedSegment> BuildExportSpeedSegments()
    {
        var segments = new List<SpeedSegment>(SpeedSegments);
        if (FreezeTimeMs >= 0)
        {
            segments.Add(new SpeedSegment((int)FreezeTimeMs, (int)(FreezeTimeMs + FreezeDurationS * 1000.0), 0.0));
        }
        return segments;
    }

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// QUALITY_05 — NO MARKS SET MEANS THE WHOLE VIDEO, NOT NOTHING.
    ///
    /// Before MARK START or MARK END is pressed, `TrimEndMs` is 0, so this returned the floor of
    /// 1ms and the size estimate read as good as nothing on a freshly loaded clip — the one moment
    /// a user most wants to know what they are in for.
    ///
    /// The fallback is the same rule `EnsureTrimPointsSet` applies (start = 0, end = the whole
    /// video), but READ-ONLY. EnsureTrimPointsSet MUTATES: it stamps IsTrimStartSet / IsTrimEndSet
    /// true, which changes what the marker buttons and the export do next. A passive calculation
    /// that silently marked the clip as trimmed would be a far worse bug than the blank label it
    /// was fixing — so this resolves the same two numbers locally and writes nothing.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public double CalculateEffectiveDurationMs()
    {
        double startMs = IsTrimStartSet ? TrimStartMs : 0.0;
        double endMs = (IsTrimEndSet && TrimEndMs > startMs) ? TrimEndMs : LoadedVideoDurationMs;

        return CalculateEffectiveDurationMs(startMs, endMs, BaseSpeed, BuildExportSpeedSegments(), Cuts);
    }

    public static double CalculateEffectiveDurationMs(
        double trimStartMs,
        double trimEndMs,
        double baseSpeed,
        IReadOnlyList<SpeedSegment>? speedSegments = null,
        IReadOnlyList<CutRange>? cuts = null)
    {
        if (speedSegments == null || speedSegments.Count == 0)
        {
            double raw = (trimEndMs - trimStartMs) / Math.Max(0.001, baseSpeed);
            if (cuts != null && cuts.Count > 0)
            {
                double spanMs = Math.Max(0, trimEndMs - trimStartMs);
                var relative = CutRange.ToClipRelative(cuts, trimStartMs);
                var normalized = OutputTimeline.NormalizeCuts(relative, spanMs / 1000.0);
                double cutMs = 0;
                foreach (var c in normalized) cutMs += c.LengthSec * 1000.0;
                raw = Math.Max(0, spanMs - cutMs) / Math.Max(0.001, baseSpeed);
            }
            return Math.Max(1.0, raw);
        }

        double totalMs = 0.0;
        double cursor = trimStartMs;

        var sortedSegments = new List<SpeedSegment>(speedSegments);
        sortedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        foreach (var seg in sortedSegments)
        {
            double segStart = Math.Max(trimStartMs, Math.Max(seg.StartMs, cursor));
            double segEnd = Math.Min(trimEndMs, seg.EndMs);
            if (segEnd <= segStart) continue;

            if (segStart > cursor)
            {
                totalMs += (segStart - cursor) / Math.Max(0.001, baseSpeed);
            }
            if (Math.Abs(seg.Speed) < 0.001)
            {
                totalMs += (segEnd - segStart);
            }
            else
            {
                totalMs += (segEnd - segStart) / Math.Max(0.001, seg.Speed);
            }
            cursor = Math.Max(cursor, segEnd);
        }
        if (cursor < trimEndMs)
        {
            totalMs += (trimEndMs - cursor) / Math.Max(0.001, baseSpeed);
        }
        return Math.Max(1.0, totalMs);
    }

    /// <summary>
    /// QUALITY_03 — of the finished video's milliseconds, how many are a HELD STILL FRAME.
    ///
    /// Deliberately mirrors CalculateEffectiveDurationMs's own loop, clamp for clamp, including
    /// its `Math.Abs(seg.Speed) &lt; 0.001` test for a freeze and its overlap handling. The two
    /// numbers are only meaningful together — a freeze total measured by different rules than the
    /// duration it is subtracted from would drift, and the export would aim at a size derived from
    /// two disagreeing timelines.
    /// ⚠️ If CalculateEffectiveDurationMs's segment walk ever changes, this must change with it.
    /// </summary>
    public double CalculateFreezeOutputMs()
    {
        var speedSegments = BuildExportSpeedSegments();
        if (speedSegments == null || speedSegments.Count == 0) return 0.0;

        double trimStartMs = IsTrimStartSet ? TrimStartMs : 0.0;
        double trimEndMs = (IsTrimEndSet && TrimEndMs > trimStartMs) ? TrimEndMs : LoadedVideoDurationMs;
        double frozenMs = 0.0;
        double cursor = trimStartMs;

        var sorted = new List<SpeedSegment>(speedSegments);
        sorted.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        foreach (var seg in sorted)
        {
            double segStart = Math.Max(trimStartMs, Math.Max(seg.StartMs, cursor));
            double segEnd = Math.Min(trimEndMs, seg.EndMs);
            if (segEnd <= segStart) continue;

            if (Math.Abs(seg.Speed) < 0.001)
            {
                frozenMs += (segEnd - segStart);
            }
            cursor = Math.Max(cursor, segEnd);
        }

        return Math.Max(0.0, frozenMs);
    }

    private (double StartMs, double EndMs, double BaseSpeed, int Segments, int Cuts)? _previewTimelineKey;
    private OutputTimeline? _previewTimeline;

    /// <summary>
    /// MUSICSYNC_01 — source ms to FINISHED-VIDEO seconds for LIVE PREVIEW use (called every UI
    /// tick while playing).
    /// <para>
    /// Same mapping as <see cref="SourceMsToOutputSeconds"/> (North Star #2: OutputTimeline is the
    /// only authority for time), with two differences that matter on a hot path:
    ///   • it NEVER calls <see cref="EnsureTrimPointsSet"/>. Unset marks resolve read-only to "the
    ///     whole video", the QUALITY_05 rule, so previewing cannot stamp the clip as trimmed;
    ///   • the OutputTimeline is rebuilt only when its inputs change (trim, base speed, segments,
    ///     freeze, cuts), not once per frame.
    /// </para>
    /// </summary>
    public double PreviewSourceToOutputSeconds(double sourceMs)
    {
        double startMs = IsTrimStartSet ? TrimStartMs : 0.0;
        double endMs = (IsTrimEndSet && TrimEndMs > startMs) ? TrimEndMs : LoadedVideoDurationMs;
        double spanMs = Math.Max(0, endMs - startMs);

        var segments = BuildExportSpeedSegments();
        var hashSeg = new HashCode();
        foreach (var seg in segments) hashSeg.Add(seg);
        var hashCut = new HashCode();
        foreach (var cut in Cuts) hashCut.Add(cut);
        var key = (startMs, endMs, BaseSpeed, hashSeg.ToHashCode(), hashCut.ToHashCode());

        if (_previewTimeline == null || _previewTimelineKey != key)
        {
            _previewTimeline = OutputTimeline.Create(
                spanMs,
                segments,
                BaseSpeed,
                startMs,
                null,
                CutRange.ToClipRelative(Cuts, startMs));
            _previewTimelineKey = key;
        }

        double absSourceSec = Math.Clamp(sourceMs, startMs, startMs + spanMs) / 1000.0;
        return Math.Max(0.0, _previewTimeline.SourceToOutput(absSourceSec));
    }

    /// <summary>
    /// MEMEMARK_02 — the inverse of <see cref="PreviewSourceToOutputSeconds"/>: the ABSOLUTE source ms
    /// the finished video (memes not counted) shows at <paramref name="outputSec"/>. Used to draw how
    /// far a corner meme reaches on the main screen's source-time ruler.
    /// </summary>
    public double PreviewOutputToSourceMs(double outputSec)
    {
        double startMs = IsTrimStartSet ? TrimStartMs : 0.0;
        PreviewSourceToOutputSeconds(startMs);
        if (_previewTimeline == null) return startMs;
        double rel = _previewTimeline.NextSurvivingSource(_previewTimeline.OutputToSourceRelative(outputSec));
        return startMs + rel * 1000.0;
    }

    public double SourceMsToOutputSeconds(double sourceMs, IReadOnlyList<SpeedSegment>? segments = null)
    {
        EnsureTrimPointsSet();
        double spanMs = Math.Max(0, TrimEndMs - TrimStartMs);

        var timeline = OutputTimeline.Create(
            spanMs,
            segments ?? BuildExportSpeedSegments(),
            BaseSpeed,
            TrimStartMs,
            null,
            CutRange.ToClipRelative(Cuts, TrimStartMs));

        double absSourceSec = Math.Clamp(sourceMs, TrimStartMs, TrimStartMs + spanMs) / 1000.0;
        return Math.Max(0.0, timeline.SourceToOutput(absSourceSec));
    }

    public void NormalizeCutsInPlace()
    {
        EnsureTrimPointsSet();
        double spanMs = Math.Max(0, TrimEndMs - TrimStartMs);

        var relative = CutRange.ToClipRelative(Cuts, TrimStartMs);
        var normalized = OutputTimeline.NormalizeCuts(relative, spanMs / 1000.0);

        Cuts.Clear();
        foreach (var c in normalized)
        {
            Cuts.Add(new CutRange(TrimStartMs + c.StartSec * 1000.0, TrimStartMs + c.EndSec * 1000.0));
        }
    }

    public double SurvivingMsFor(IReadOnlyList<CutRange> cuts)
    {
        EnsureTrimPointsSet();
        double spanMs = Math.Max(0, TrimEndMs - TrimStartMs);

        var relative = CutRange.ToClipRelative(cuts, TrimStartMs);
        var normalized = OutputTimeline.NormalizeCuts(relative, spanMs / 1000.0);

        double removedMs = 0;
        foreach (var c in normalized) removedMs += c.LengthSec * 1000.0;
        return Math.Max(0, spanMs - removedMs);
    }

    public double SurvivingMsFor(double startMs, double endMs)
    {
        double span = Math.Max(0, endMs - startMs);
        if (Cuts.Count == 0) return span;
        var relative = CutRange.ToClipRelative(Cuts, startMs);
        var normalized = OutputTimeline.NormalizeCuts(relative, span / 1000.0);
        double removedMs = 0;
        foreach (var c in normalized) removedMs += c.LengthSec * 1000.0;
        return Math.Max(0, span - removedMs);
    }

    public double RemovedCutSeconds()
    {
        EnsureTrimPointsSet();
        double spanMs = Math.Max(0, TrimEndMs - TrimStartMs);
        return Math.Max(0, (spanMs - SurvivingMsFor(Cuts)) / 1000.0);
    }

    /// <summary>
    /// MEMETRIM_02 — the MARK START / MARK END the stored memes are measured from. Memes are kept in
    /// CLIP-RELATIVE seconds (from MARK START) while cuts, speed blocks and the freeze are kept in
    /// source milliseconds, so moving MARK START used to slide every meme onto a different gameplay
    /// frame while nothing else moved. NaN = not anchored yet (the next rebase only records it).
    /// </summary>
    public double MemeOriginMs { get; private set; } = double.NaN;
    private double _memeOriginEndMs = double.NaN;

    private double EffectiveTrimStartMs => IsTrimStartSet ? TrimStartMs : 0.0;
    private double EffectiveTrimEndMs => (IsTrimEndSet && TrimEndMs > EffectiveTrimStartMs) ? TrimEndMs : LoadedVideoDurationMs;

    /// <summary>MEMETRIM_02 — the memes were just (re)built against the CURRENT trim: remember it.</summary>
    public void MarkMemesAnchored()
    {
        MemeOriginMs = EffectiveTrimStartMs;
        _memeOriginEndMs = EffectiveTrimEndMs;
    }

    /// <summary>
    /// MEMETRIM_02 — call after MARK START or MARK END moved. Every meme keeps its GAMEPLAY frame:
    /// its clip-relative second is shifted by how far MARK START moved. A meme that sat on the old
    /// end follows the new end (it was placed "after my video"); one that now falls outside the kept
    /// part is removed and named in the result, so the caller can say so.
    /// </summary>
    public List<string> RebaseMemesToTrim()
    {
        var dropped = new List<string>();
        double newStart = EffectiveTrimStartMs, newEnd = EffectiveTrimEndMs;
        if (double.IsNaN(MemeOriginMs) || MemePlacements.Count == 0)
        {
            MarkMemesAnchored();
            return dropped;
        }

        double shiftSec = (MemeOriginMs - newStart) / 1000.0;
        double oldSpanSec = Math.Max(0, (_memeOriginEndMs - MemeOriginMs) / 1000.0);
        double newSpanSec = Math.Max(0, (newEnd - newStart) / 1000.0);
        if (Math.Abs(shiftSec) < 0.0005 && Math.Abs(oldSpanSec - newSpanSec) < 0.0005)
        {
            MarkMemesAnchored();
            return dropped;
        }

        var kept = new List<MemePlacement>(MemePlacements.Count);
        foreach (var m in MemePlacements)
        {
            bool wasAtEnd = !double.IsNaN(_memeOriginEndMs) && m.AtSourceSecRelative >= oldSpanSec - 0.05;
            double at = wasAtEnd ? newSpanSec : m.AtSourceSecRelative + shiftSec;
            if (at < -0.0005 || at > newSpanSec + 0.0005)
            {
                dropped.Add(System.IO.Path.GetFileName(m.FilePath));
                continue;
            }
            kept.Add(m with { AtSourceSecRelative = Math.Clamp(at, 0, newSpanSec) });
        }
        MemePlacements.Clear();
        MemePlacements.AddRange(kept);
        MarkMemesAnchored();
        return dropped;
    }

    public List<string> DropMemesInsideCuts()
    {
        var dropped = new List<string>();
        if (MemePlacements.Count == 0 || Cuts.Count == 0) return dropped;

        EnsureTrimPointsSet();
        var relative = CutRange.ToClipRelative(Cuts, TrimStartMs);
        var survivors = new List<MemePlacement>();

        foreach (var m in MemePlacements)
        {
            bool inHole = false;
            foreach (var c in relative)
            {
                if (m.AtSourceSecRelative > c.StartSec + 0.0005 && m.AtSourceSecRelative < c.EndSec - 0.0005)
                {
                    inHole = true;
                    break;
                }
            }

            if (inHole)
                dropped.Add(System.IO.Path.GetFileName(m.FilePath));
            else
                survivors.Add(m);
        }

        if (dropped.Count > 0)
        {
            MemePlacements.Clear();
            foreach (var s in survivors) MemePlacements.Add(s);
        }

        return dropped;
    }

    public void Reset()
    {
        _loadedVideoDurationMs = 0;
        _currentPositionMs = 0;
        _trimStartMs = 0;
        _trimEndMs = 0;
        _trimStartSet = false;
        _trimEndSet = false;
        _thumbnailPosMs = 0;
        _thumbnailSet = false;
        _freezeTimeMs = -1;
        _freezeDurationS = 1.0;
        _isCurrentlyFrozen = false;
        ApplyMainSpeedPreset(SpeedPresetButtons.NativeDefaultSpeed);
        Cuts.Clear();
        SpeedSegments.Clear();
        MemePlacements.Clear();
        MemeOriginMs = double.NaN;
        _memeOriginEndMs = double.NaN;
        TimeElapsedText = "00:00:00";
        TimeRemainingText = "-00:00:00";
    }
}
