using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MUSICWIN_01 — EVERY SONG'S CHOSEN PART, AND ONE WAY TO READ IT.
///
/// The Add Music wizard now lets the user choose which part of EVERY song plays (drag the song's
/// edges on the Fit &amp; Mix screen), not only where the first song starts. Those choices travel
/// in <see cref="MusicStartsSeconds"/> / <see cref="MusicEndsSeconds"/>, index-aligned with
/// <see cref="MusicWizardResult.MusicFilePaths"/>.
///
/// The Main App export, the Main App preview, the Video Merger export and the Video Merger preview
/// each used to rebuild "paths + durations + first offset" by hand, and each filtered missing files
/// and then indexed the durations by the FILTERED position — so one missing song shifted every
/// length after it onto the wrong file. <see cref="ToBedTracks"/> is the one reader now: it keeps
/// the original index for every lookup and only then drops missing files.
///
/// Older results (and saved sessions) carry no windows: the first song then starts at
/// <see cref="MusicWizardResult.OffsetSeconds"/>, every other song at 0, and every song plays to
/// its end — exactly the layout they were saved with.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class MusicWizardResult
{
    /// <summary>MUSICWIN_01 — where each song's chosen part starts inside its file.</summary>
    public List<double> MusicStartsSeconds { get; set; } = new();

    /// <summary>MUSICWIN_01 — where each song's chosen part ends inside its file; 0 = to the end.</summary>
    public List<double> MusicEndsSeconds { get; set; } = new();

    /// <summary>The songs in play order: <see cref="MusicFilePaths"/>, or the single <see cref="MusicFilePath"/>.</summary>
    public List<string> OrderedPaths()
    {
        var paths = new List<string>();
        if (MusicFilePaths != null && MusicFilePaths.Count > 0) paths.AddRange(MusicFilePaths);
        else if (!string.IsNullOrWhiteSpace(MusicFilePath)) paths.Add(MusicFilePath);
        return paths;
    }

    /// <summary>
    /// The plan tracks for this result, in play order, skipping songs <paramref name="exists"/> rejects
    /// (default: <see cref="System.IO.File.Exists(string)"/>). Every per-song value is looked up by the
    /// song's ORIGINAL index, before any file is skipped.
    /// </summary>
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

    /// <summary>A short, user-facing name for what is on the music button: the song, or "3 songs".</summary>
    public string DescribeSongs()
    {
        var paths = OrderedPaths();
        if (paths.Count == 0) return "No music";
        if (paths.Count == 1) return System.IO.Path.GetFileNameWithoutExtension(paths[0]);
        return $"{paths.Count} songs";
    }
}
