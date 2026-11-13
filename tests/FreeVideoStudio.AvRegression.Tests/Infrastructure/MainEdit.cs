using FreeVideoStudio.App;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// The main screen's edit state, as the user builds it: marks, speed, speed blocks, freeze, cuts,
/// memes, music notes, voice-over. <see cref="Compose"/> builds the export payload the way
/// <c>MainWindow.ComposeExportPayloadAsync</c> does (guarded by S03's source-shape check), and the
/// preview-side questions are answered by the app's own <see cref="TimelineViewModel"/>.
/// </summary>
internal sealed class MainEdit
{
    public required Clip Source { get; init; }
    public double TrimStartMs { get; set; }
    public double TrimEndMs { get; set; }
    public double BaseSpeed { get; set; } = 1.1;
    public List<SpeedSegment> Segments { get; } = new();
    public List<CutRange> Cuts { get; } = new();
    public double FreezeTimeMs { get; set; } = -1;
    public double FreezeDurationS { get; set; }
    public List<MemePlacement> Memes { get; } = new();
    public MusicWizardResult? Music { get; set; }
    public bool KeepMusicDuringMeme { get; set; }
    public bool Fades { get; set; }
    public List<VoiceOverTake>? VoiceTakes { get; set; }
    public CaptionTrack? Captions { get; set; }
    /// <summary>9:16 export (the crop/HUD path).</summary>
    public bool Portrait { get; set; }

    public double EndMs => TrimEndMs > 0 ? TrimEndMs : Source.DurationSec * 1000;

    public TimelineViewModel Timeline()
    {
        var t = new TimelineViewModel
        {
            LoadedVideoDurationMs = Source.DurationSec * 1000,
            TrimStartMs = TrimStartMs, IsTrimStartSet = true,
            TrimEndMs = EndMs, IsTrimEndSet = true,
            BaseSpeed = BaseSpeed,
            FreezeTimeMs = FreezeTimeMs, FreezeDurationS = FreezeDurationS,
        };
        t.SpeedSegments.AddRange(Segments);
        t.Cuts.AddRange(Cuts);
        t.MemePlacements.AddRange(Memes);
        return t;
    }

    /// <summary>Preview clock: finished-video seconds of a source moment, memes NOT counted (MUSICSYNC_01).</summary>
    public double PreviewOut(double sourceSec) => Timeline().PreviewSourceToOutputSeconds(sourceSec * 1000);

    /// <summary>Full-screen meme seconds inserted before preview-output second <paramref name="u"/>.</summary>
    public double MemesBefore(double u, bool inclusive = false) =>
        MemePlacement.InlineOnly(Memes).Where(m =>
        {
            double at = PreviewOut(TrimStartMs / 1000 + m.AtSourceSecRelative);
            return inclusive ? at <= u + 1e-6 : at < u - 1e-6;
        }).Sum(m => m.DurationSec);

    /// <summary>Export clock (relative to the start of the gameplay body): preview clock + memes before it.</summary>
    public double ExportOut(double sourceSec)
    {
        double u = PreviewOut(sourceSec);
        return u + MemesBefore(u, inclusive: true);
    }

    /// <summary>Source times where the picture is ambiguous (edges of blocks, cuts, freeze, memes, marks).</summary>
    public IEnumerable<double> EdgesSec()
    {
        yield return TrimStartMs / 1000; yield return EndMs / 1000;
        foreach (var s in Segments) { yield return s.StartMs / 1000.0; yield return s.EndMs / 1000.0; }
        foreach (var c in Cuts) { yield return c.StartMs / 1000.0; yield return c.EndMs / 1000.0; }
        if (FreezeTimeMs >= 0) yield return FreezeTimeMs / 1000;
        foreach (var m in Memes) yield return TrimStartMs / 1000 + m.AtSourceSecRelative;
    }

    /// <summary>Gameplay markers that survive the edit, away from every edge, and where the export must show them.</summary>
    public List<(double Source, double ExportBody)> ExpectedGameplay(double margin = 0.12)
    {
        var edges = EdgesSec().ToArray();
        var result = new List<(double, double)>();
        foreach (double e in Source.Events)
        {
            if (e < TrimStartMs / 1000 || e > EndMs / 1000) continue;
            if (Cuts.Any(c => e * 1000 >= c.StartMs - margin * 1000 && e * 1000 <= c.EndMs + margin * 1000)) continue;
            if (edges.Any(x => Math.Abs(x - e) < margin)) continue;
            result.Add((e, ExportOut(e)));
        }
        return result;
    }

    /// <summary>The music bed as the preview plays it (MusicBedPlan, shared with the export): segments in preview-output seconds.</summary>
    public (IReadOnlyList<MusicBedSegment> Plan, double StartDelay, double Duration) MusicPlan()
    {
        var t = Timeline();
        var all = t.BuildExportSpeedSegments();
        double musicStartMs = Math.Max(TrimStartMs, Music!.TimelineStartSeconds * 1000.0);
        double musicEndMs = Math.Min(EndMs, Music.TimelineEndSeconds * 1000.0);
        double startDelay = t.SourceMsToOutputSeconds(musicStartMs, all);
        double dur = t.SourceMsToOutputSeconds(musicEndMs, all) - startDelay;
        if (dur <= 0) dur = 1.0;
        return (MusicBedPlan.Build(Music.ToBedTracks(), dur, Music.LoopMusic, startDelay), startDelay, dur);
    }

    /// <summary>The export payload, composed like MainWindow.ComposeExportPayloadAsync.</summary>
    public ExportPayload Compose(string outputDirectory)
    {
        var t = Timeline();
        var allSegments = t.BuildExportSpeedSegments();
        bool lead = true, tail = true;
        List<MusicTrack>? tracks = null;
        System.Text.Json.Nodes.JsonObject music;
        if (Music != null && File.Exists(Music.MusicFilePath))
        {
            double musicStartMs = Math.Max(TrimStartMs, Music.TimelineStartSeconds * 1000.0);
            double musicEndMs = Math.Min(EndMs, Music.TimelineEndSeconds * 1000.0);
            double startDelay = t.SourceMsToOutputSeconds(musicStartMs, allSegments);
            double outputEnd = t.SourceMsToOutputSeconds(musicEndMs, allSegments);
            double dur = outputEnd - startDelay;
            if (dur <= 0) dur = 1.0;
            lead = Music.TimelineStartSeconds * 1000.0 <= TrimStartMs + 50;
            tail = Music.TimelineEndSeconds * 1000.0 <= EndMs + 50;
            var bed = Music.ToBedTracks();
            tracks = MusicBedPlan.Build(bed, dur, Music.LoopMusic, startDelay)
                .Select(s => new MusicTrack(s.Path, s.FileOffsetSec, s.DurationSec, startDelay, true)).ToList();
            music = new System.Text.Json.Nodes.JsonObject
            {
                ["ducking_enabled"] = Music.EnableDucking, ["main_vol"] = Music.VideoVolume, ["music_vol"] = Music.MusicVolume,
                ["carving_enabled"] = Music.EnableCarving,
                ["ducking_threshold"] = Music.EnableDucking ? SidechainCompressNode.TunedThreshold : SidechainCompressNode.BypassThreshold,
                ["ducking_ratio"] = Music.EnableDucking ? SidechainCompressNode.TunedRatio : SidechainCompressNode.BypassRatio,
                ["ducking_strength"] = 50.0, ["carving_strength"] = 50.0,
            };
        }
        else music = new System.Text.Json.Nodes.JsonObject { ["main_vol"] = Music?.VideoVolume ?? 1.0 };

        return new ExportPayload
        {
            InputPath = Source.Path, OutputDirectory = outputDirectory, OutputBaseName = "avreg",
            TrimStartMs = TrimStartMs, TrimEndMs = EndMs, LoadedVideoDurationMs = Source.DurationSec * 1000,
            BaseSpeed = BaseSpeed, HardwareMode = "CPU", ApplyPeakFlattening = false,
            IsMobileFormat = Portrait, EnableFades = Fades, ShowSpectating = false,
            SpeedSegments = allSegments, Cuts = new List<CutRange>(Cuts),
            MemePlacements = Memes.Count > 0 ? new List<MemePlacement>(Memes) : null,
            QualityLevel = 20,
            MusicLeadFadeIn = lead, MusicTailFadeOut = tail, MusicTracks = tracks, MusicConfig = music,
            KeepMusicDuringMeme = KeepMusicDuringMeme,
            VoiceOverTakes = VoiceTakes,
            CaptionTrack = Captions,
        };
    }

    /// <summary>The project document the main window saves (mirrors ProjectSession.Capture).</summary>
    public ProjectDocument ToDocument() => new()
    {
        Source = SourceClip.Probe(Source.Path, Source.DurationSec * 1000, Source.Width, Source.Height, Source.Fps),
        BaseSpeed = BaseSpeed,
        SourceCutStartMs = TrimStartMs,
        TrimmedDurationMs = EndMs - TrimStartMs,
        Segments = Segments.ToArray(),
        Cuts = CutRange.ToClipRelative(Cuts, TrimStartMs).ToArray(),
        Memes = Memes.ToArray(),
    };

    /// <summary>A reopened project (mirrors ProjectSession.Apply).</summary>
    public static MainEdit FromDocument(ProjectDocument d, Clip source)
    {
        var e = new MainEdit { Source = source, TrimStartMs = d.SourceCutStartMs, TrimEndMs = d.SourceCutStartMs + d.TrimmedDurationMs, BaseSpeed = d.BaseSpeed };
        e.Segments.AddRange(d.Segments);
        foreach (var c in d.Cuts) e.Cuts.Add(new CutRange(d.SourceCutStartMs + c.StartSec * 1000, d.SourceCutStartMs + c.EndSec * 1000));
        e.Memes.AddRange(d.Memes);
        return e;
    }

    /// <summary>A music bed placed by the music notes (source seconds), songs given in order.</summary>
    public static MusicWizardResult MusicAt(double startSourceSec, double endSourceSec, string path, double fileSeconds, double offsetSec = 0, bool loop = false) => new()
    {
        MusicFilePath = path, MusicFilePaths = new List<string> { path }, MusicDurationsSeconds = new List<double> { fileSeconds },
        MusicDurationSeconds = fileSeconds, OffsetSeconds = offsetSec,
        TimelineStartSeconds = startSourceSec, TimelineEndSeconds = endSourceSec,
        EnableDucking = false, EnableCarving = false, LoopMusic = loop,
    };
}
