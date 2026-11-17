using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardResult
{
    public List<double> MusicStartsSeconds { get; set; } = new();

    public List<double> MusicEndsSeconds { get; set; } = new();

    public List<string> OrderedPaths()
    {
        var paths = new List<string>();
        if (MusicFilePaths != null && MusicFilePaths.Count > 0) paths.AddRange(MusicFilePaths);
        else if (!string.IsNullOrWhiteSpace(MusicFilePath)) paths.Add(MusicFilePath);
        return paths;
    }

    public List<MusicBedTrack> ToBedTracks(Func<string, bool>? exists = null)
    {
        exists ??= System.IO.File.Exists;
        var paths = OrderedPaths();
        var tracks = new List<MusicBedTrack>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (string.IsNullOrWhiteSpace(path) || !exists(path)) continue;

            double known = MusicDurationsSeconds != null && i < MusicDurationsSeconds.Count
                ? MusicDurationsSeconds[i]
                : (i == 0 ? MusicDurationSeconds : 0.0);
            double inSec = MusicStartsSeconds != null && i < MusicStartsSeconds.Count
                ? MusicStartsSeconds[i]
                : (i == 0 ? OffsetSeconds : 0.0);
            double outSec = MusicEndsSeconds != null && i < MusicEndsSeconds.Count ? MusicEndsSeconds[i] : 0.0;

            tracks.Add(new MusicBedTrack(path, known, Math.Max(0.0, inSec), Math.Max(0.0, outSec)));
        }
        return tracks;
    }

    public MusicPreviewBed PreviewBed(double trimStartMs, double videoEndMs, Func<double, double> sourceMsToOutputSec,
        bool keepMusicDuringMeme = false, IReadOnlyList<(double AnchorOutputSec, double DurationSec)>? inlineMemes = null)
    {
        double musicStartMs = Math.Max(trimStartMs, TimelineStartSeconds * 1000.0);
        double musicEndMs = Math.Min(videoEndMs, TimelineEndSeconds * 1000.0);
        double startDelay = sourceMsToOutputSec(musicStartMs);
        double outputEnd = sourceMsToOutputSec(musicEndMs);
        double bedDuration = outputEnd - startDelay;
        if (bedDuration <= 0) bedDuration = 1.0;
        double stretch = 0;
        if (keepMusicDuringMeme && inlineMemes != null)
            foreach (var (anchor, duration) in inlineMemes)
                if (duration > 0 && anchor >= startDelay - 1e-3 && anchor <= outputEnd + 1e-3) stretch += duration;
        return new MusicPreviewBed(MusicBedPlan.Build(ToBedTracks(), bedDuration + stretch, LoopMusic, startDelay), startDelay, bedDuration);
    }

    public string DescribeSongs()
    {
        var paths = OrderedPaths();
        if (paths.Count == 0) return "No music";
        if (paths.Count == 1) return System.IO.Path.GetFileNameWithoutExtension(paths[0]);
        return $"{paths.Count} songs";
    }
}

public readonly record struct MusicPreviewBed(IReadOnlyList<MusicBedSegment> Plan, double StartDelaySec, double DurationSec);
