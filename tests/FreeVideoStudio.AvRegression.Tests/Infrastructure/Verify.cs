using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// The preview-vs-export contract for a Main-screen edit. Each check compares what the export
/// CONTAINS (decoded markers) with what the preview SHOWS (the app's own timeline model):
/// gameplay picture, gameplay sound, music (pulses and the continuous bed), full-screen memes,
/// corner memes, voice-over. The first difference throws one plain sentence with the drift in ms.
/// </summary>
internal static class Verify
{
    private const double Ambiguity = 0.20;

    /// <summary>Picture and gameplay sound: every surviving marker where the preview shows it. Returns the body offset.</summary>
    public static double Gameplay(MainEdit edit, ExportScan scan)
    {
        var expected = edit.ExpectedGameplay();
        Av.True("test edit leaves gameplay markers to check", expected.Count >= 3, $"only {expected.Count}");
        double c = scan.BodyOffset(expected);
        Av.True("export starts where the preview starts", c > -0.03 && c < 1.3, $"gameplay is offset by {c:0.000}s from the start of the file (intro still + fade-in pad is at most ~1.1s)");

        var fuzzy = edit.Source.Events.Where(e => e >= edit.TrimStartMs / 1000 && e <= edit.EndMs / 1000
                                                  && !expected.Any(x => x.Source == e)
                                                  && !edit.Cuts.Any(cut => e * 1000 > cut.StartMs + 120 && e * 1000 < cut.EndMs - 120))
                                      .Select(e => c + edit.ExportOut(e)).ToList();
        double bodyStart = c + edit.ExportOut(edit.TrimStartMs / 1000) + 0.05, bodyEnd = c + edit.ExportOut(edit.EndMs / 1000) - 0.05;
        bool Keep(double t) => t >= bodyStart && t <= bodyEnd && !fuzzy.Any(f => Math.Abs(f - t) < Ambiguity) && !InMeme(scan, t);

        var want = expected.Select(e => e.ExportBody).ToList();
        Dump(scan, "gameplay.txt", $"offset {c:0.000}", want.Select(w => w + c), scan.Flashes, scan.GameplayPulses);
        if (!edit.Portrait) Picture(edit, scan, c, bodyStart, bodyEnd);
        Av.Markers("GAMEPLAY PICTURE vs preview", want, scan.Flashes.Where(Keep).ToList(), Budget.ExportSyncSec + Budget.FrameSec, c);
        var flashTimes = want.Select(w => scan.Flashes.OrderBy(f => Math.Abs(f - w - c)).First()).ToList();
        Av.Markers("GAMEPLAY SOUND vs picture (A/V sync)", flashTimes, scan.GameplayPulses.Where(Keep).ToList(), Budget.ExportSyncSec);
        Av.True("export is not shorter than the edit", scan.DurationSec >= bodyEnd - 0.05, $"file {scan.DurationSec:0.000}s, edit needs {bodyEnd:0.000}s");
        return c;
    }

    /// <summary>
    /// Frame by frame: the source frame each exported frame shows (its barcode) is the one the
    /// preview shows at that moment. Catches skipped frames (a jump), repeated/stuck frames (a hold
    /// where the preview moves) and slow picture drift, everywhere — not only at the flashes.
    /// </summary>
    private static void Picture(MainEdit edit, ExportScan scan, double c, double bodyStart, double bodyEnd)
    {
        var t = edit.Timeline();
        var edges = edit.EdgesSec().Select(e => c + edit.ExportOut(e)).ToList();
        var memes = MemePlacement.InlineOnly(edit.Memes).Select(m =>
        {
            double end = c + edit.ExportOut(edit.TrimStartMs / 1000 + m.AtSourceSecRelative);
            return (Start: end - m.DurationSec, End: end);
        }).ToList();
        int checkedFrames = 0;
        var lines = new List<string>();
        for (int f = 0; f < scan.FrameCodes.Length; f++)
        {
            double at = f / 60.0;
            if (at < bodyStart + 0.05 || at > bodyEnd - 0.05) continue;
            if (edges.Any(e => Math.Abs(e - at) < 0.07) || memes.Any(m => at > m.Start - 0.07 && at < m.End + 0.07) || InMeme(scan, at)) continue;
            int code = scan.FrameCodes[f];
            if (code < 0) continue;
            double u = at - c - memes.Where(m => m.End <= at).Sum(m => m.End - m.Start);
            double want = t.PreviewOutputToSourceMs(u) / 1000.0;
            if (edit.Segments.Any(z => z.ZoomW != null && want * 1000 > z.StartMs - 100 && want * 1000 < z.EndMs + 100)) continue;
            double shown = code / edit.Source.Fps;
            double speed = Math.Max(1, SpeedAt(edit, want));
            checkedFrames++;
            lines.Add($"{at:0.000} shows {shown:0.000} preview {want:0.000}");
            if (Math.Abs(shown - want) > (2.5 * speed) / edit.Source.Fps + 0.01)
            {
                File.WriteAllLines(Path.Combine(scan.Work, "frames.txt"), lines);
                string kind = shown > want ? "SKIPS AHEAD" : "LAGS/STICKS";
                throw new AvDefect($"PICTURE {kind} at {at:0.000}s of the export: shows source {shown:0.000}s, the preview shows {want:0.000}s (off {(shown - want) * 1000:+0;-0}ms) — frames.txt in {ExportRig.Rel(scan.Work)}");
            }
        }
        File.WriteAllLines(Path.Combine(scan.Work, "frames.txt"), lines);
        Av.True("export frames carry readable frame numbers", checkedFrames > 30, $"only {checkedFrames} frames could be checked");
    }

    /// <summary>
    /// Where the bed's fade-out crosses HALF its steady level (measured in [steadyFrom, steadyTo]):
    /// the first point after the steady stretch where a 20 ms average stays below half for 50 ms.
    /// </summary>
    private static double HalfLevelEnd(double[] env, double steadyFrom, double steadyTo)
    {
        int a = Math.Clamp((int)(steadyFrom * 1000), 0, env.Length - 1), b = Math.Clamp((int)(steadyTo * 1000), a + 1, env.Length);
        var steady = env[a..b].OrderBy(v => v).ToArray();
        double level = steady[steady.Length / 2] * 0.5;
        var smooth = new double[env.Length];
        double acc = 0;
        for (int i = 0; i < env.Length; i++) { acc += env[i]; if (i >= 20) acc -= env[i - 20]; smooth[i] = acc / Math.Min(i + 1, 20); }
        for (int i = b; i + 50 < smooth.Length; i++)
            if (smooth[i] < level && smooth.Skip(i).Take(50).All(v => v < level)) return (i - 10) / 1000.0;
        return double.NaN;
    }

    private static double SpeedAt(MainEdit edit, double sourceSec)
    {
        var seg = edit.Segments.FirstOrDefault(s => sourceSec * 1000 >= s.StartMs && sourceSec * 1000 < s.EndMs);
        return seg?.Speed ?? edit.BaseSpeed;
    }

    /// <summary>Side-by-side expected/found lists next to the export, for the human who opens the work folder.</summary>
    private static void Dump(ExportScan scan, string name, string head, IEnumerable<double> expected, IEnumerable<double> found, IEnumerable<double> also)
    {
        static string L(IEnumerable<double> v) => string.Join(" ", v.Select(x => x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)));
        File.WriteAllLines(Path.Combine(scan.Work, name), new[] { head, "expected: " + L(expected), "found:    " + L(found), "also:     " + L(also) });
    }

    private static bool InMeme(ExportScan scan, double t) => scan.MemePicture.Any(m => t > m.Start - 0.05 && t < m.End + 0.05);

    /// <summary>
    /// Music: it starts on the START note, stops on the END note, keeps the song in step with the
    /// gameplay the preview pairs it with, pauses under full-screen memes (unless "keep music during
    /// memes" is on, then it runs on unbroken), and never plays past its END note.
    /// </summary>
    public static void Music(MainEdit edit, ExportScan scan, double c)
    {
        var music = edit.Music ?? throw new InvalidOperationException("no music in this edit");
        var payload = edit.Compose(scan.Work);
        var tracks = payload.MusicTracks!;
        double startSrc = Math.Max(edit.TrimStartMs / 1000, music.TimelineStartSeconds), endSrc = Math.Min(edit.EndMs / 1000, music.TimelineEndSeconds);
        double startOut = c + edit.ExportOut(startSrc), endOut = c + edit.ExportOut(endSrc);
        double delay = tracks[0].TimelineStartDelay;
        bool keep = edit.KeepMusicDuringMeme;

        var memesInside = MemePlacement.InlineOnly(edit.Memes)
            .Select(m => c + edit.ExportOut(edit.TrimStartMs / 1000 + m.AtSourceSecRelative) - m.DurationSec)
            .Zip(MemePlacement.InlineOnly(edit.Memes), (s, m) => (Start: s, End: s + m.DurationSec))
            .Where(m => m.Start > startOut + 0.2 && m.End < endOut - 0.2).OrderBy(m => m.Start).ToList();
        var expectedSpans = new List<(double Start, double End)>();
        double cursor = startOut;
        if (!keep) foreach (var m in memesInside) { expectedSpans.Add((cursor, m.Start)); cursor = m.End; }
        expectedSpans.Add((cursor, endOut));
        var bed = scan.MusicBed.Where(s => s.End > startOut - 1.2).ToList();
        Dump(scan, "music.txt", $"offset {c:0.000} start {startOut:0.000} end {endOut:0.000} expected stretches " + string.Join(" ", expectedSpans.Select(b => $"{b.Start:0.000}-{b.End:0.000}"))
            + " found " + string.Join(" ", scan.MusicBed.Select(b => $"{b.Start:0.000}-{b.End:0.000}")), Array.Empty<double>(), scan.MusicPulses, Array.Empty<double>());
        Av.True("music is audible in the export", bed.Count > 0, $"expected from {startOut:0.000}s to {endOut:0.000}s");
        bool leadIn = payload.MusicLeadFadeIn && edit.Fades && delay < 0.05;
        double startErr = 0;
        if (!leadIn)
            startErr = bed[0].Start - expectedSpans[0].Start;
        bool tailThroughPad = edit.Fades && payload.MusicTailFadeOut && endSrc >= edit.EndMs / 1000 - 0.05;
        double wantEnd = tailThroughPad ? scan.DurationSec : expectedSpans[^1].End;
        double fade = payload.MusicTailFadeOut ? Math.Min(1.5, (expectedSpans[^1].End - expectedSpans[^1].Start) / 2) : 0;
        double halfAt = HalfLevelEnd(scan.BedEnvelope, wantEnd - fade - 1.2, wantEnd - fade - 0.1);
        double endErr = halfAt - (wantEnd - fade / 2), endBudget = tailThroughPad ? 0.25 : Budget.MusicEndSec;
        if (Math.Abs(startErr) > 0.08 || double.IsNaN(endErr) || Math.Abs(endErr) > endBudget)
            throw new AvDefect($"MUSIC DRIFT vs the music notes: starts {startErr * 1000:+0;-0}ms (budget ±80ms), ends {endErr * 1000:+0;-0}ms (budget ±{endBudget * 1000:0}ms) — music.txt in {ExportRig.Rel(scan.Work)}");
        if (!keep && memesInside.Count > 0)
            Av.True("music PAUSES under a full-screen meme (keep-music is off)", bed.Count >= expectedSpans.Count,
                $"expected {expectedSpans.Count} music stretches split by memes, found {bed.Count}: " + string.Join(", ", bed.Select(b => $"{b.Start:0.00}-{b.End:0.00}")));
        if (keep && memesInside.Count > 0)
            Av.True("music keeps playing under a full-screen meme (keep-music is on)", bed.Count == 1,
                $"music broke into {bed.Count} pieces: " + string.Join(", ", bed.Select(b => $"{b.Start:0.00}-{b.End:0.00}")));

        double[] events = TestMedia.MusicEventsFor(music.MusicFilePath);
        var (plan, _, _) = edit.MusicPlan();
        var overlaps = plan.Where(sg => sg.LeadInSec > 0.01).Select(sg => (From: sg.OutputStartSec - sg.LeadInSec - 0.1, To: sg.OutputStartSec + 0.1)).ToList();
        double Placed(double u) => keep ? c + u + edit.MemesBefore(delay, inclusive: true) : c + u + edit.MemesBefore(u);
        var want = new List<double>();
        double checkFrom = startOut + (leadIn ? 1.6 : 0.15), checkTo = endOut - 1.6;
        foreach (var sg in plan)
        {
            double at = sg.FileOffsetSec + sg.LeadInSec;
            foreach (double m in events.Where(m => m >= at && m < at + sg.DurationSec))
            {
                double u = sg.OutputStartSec + (m - at);
                double t = Placed(u);
                if (t < checkFrom || t > checkTo || overlaps.Any(o => u > o.From && u < o.To)) continue;
                if (!keep && memesInside.Any(x => Math.Abs(t - x.Start) < 0.1)) continue;
                want.Add(t);
            }
        }
        bool Skip(double t) => t < checkFrom || t > checkTo
                               || overlaps.Any(o => t > Placed(o.From) && t < Placed(o.To))
                               || (!keep && memesInside.Any(x => Math.Abs(t - x.Start) < 0.1));
        var got = scan.MusicPulses.Where(t => !Skip(t)).ToList();
        Dump(scan, "music.txt", $"offset {c:0.000} start {startOut:0.000} end {endOut:0.000} bed " + string.Join(" ", scan.MusicBed.Select(b => $"{b.Start:0.000}-{b.End:0.000}")), want, scan.MusicPulses, Array.Empty<double>());
        Av.Markers("MUSIC BEATS vs the gameplay the preview pairs them with", want, got, Budget.ExportSyncSec);
        var late = scan.MusicPulses.Where(t => t > (tailThroughPad ? scan.DurationSec : endOut) + 0.12).ToList();
        if (late.Count > 0) throw new AvDefect($"MUSIC PLAYS PAST ITS END NOTE: {late.Count} beat(s) after it, the last {late[^1] - endOut:0.000}s late");
    }

    /// <summary>Full-screen memes: shown and heard as one block, exactly where the preview cuts away.</summary>
    public static void InlineMemes(MainEdit edit, ExportScan scan, double c)
    {
        MemeSound(edit, scan, c);
        foreach (var m in MemePlacement.InlineOnly(edit.Memes))
        {
            double anchor = edit.PreviewOut(edit.TrimStartMs / 1000 + m.AtSourceSecRelative);
            double start = c + anchor + edit.MemesBefore(anchor), end = start + m.DurationSec;
            var pic = scan.MemePicture.OrderBy(s => Math.Abs(s.Start - start)).FirstOrDefault((Start: double.NaN, End: double.NaN));
            Av.Near($"FULL-SCREEN MEME '{m.Id}' picture start", start, pic.Start, Budget.ExportSyncSec + Budget.FrameSec);
            Av.Near($"FULL-SCREEN MEME '{m.Id}' picture length", m.DurationSec, pic.End - pic.Start, 0.07);
            if (!m.PlaySound) continue;
            var snd = scan.MemeSound.OrderBy(s => Math.Abs(s.Start - start)).FirstOrDefault((Start: double.NaN, End: double.NaN));
            Av.Near($"FULL-SCREEN MEME '{m.Id}' sound start vs its picture (A/V sync)", pic.Start, snd.Start, Budget.ExportSyncSec + 0.02);
            Av.Near($"FULL-SCREEN MEME '{m.Id}' sound end vs its picture", pic.End, snd.End, 0.08);
        }
        Av.True("no meme picture where no full-screen meme was placed",
            scan.MemePicture.Count == MemePlacement.InlineOnly(edit.Memes).Count,
            $"{MemePlacement.InlineOnly(edit.Memes).Count} placed, {scan.MemePicture.Count} found: " + string.Join(", ", scan.MemePicture.Select(s => $"{s.Start:0.00}-{s.End:0.00}")));
    }

    /// <summary>Corner memes: over the gameplay from their moment for their length, adding no time.</summary>
    public static void CornerMemes(MainEdit edit, ExportScan scan, double c)
    {
        MemeSound(edit, scan, c);
        double total = c + edit.ExportOut(edit.EndMs / 1000);
        var expected = MemePlacement.CornerOnly(edit.Memes).Select(m =>
        {
            double start = c + edit.ExportOut(edit.TrimStartMs / 1000 + m.AtSourceSecRelative);
            return (Start: start, End: Math.Min(total, start + m.DurationSec));
        }).ToList();
        foreach (var m in MemePlacement.InlineOnly(edit.Memes))
        {
            double anchor = edit.PreviewOut(edit.TrimStartMs / 1000 + m.AtSourceSecRelative);
            double start = c + anchor + edit.MemesBefore(anchor);
            expected.Add((start, start + m.DurationSec));
        }
        MatchSpans("CORNER MEME picture (including unwanted overlays)", expected, scan.CornerPicture, 0.07);
        foreach (var m in MemePlacement.CornerOnly(edit.Memes))
        {
            double start = c + edit.ExportOut(edit.TrimStartMs / 1000 + m.AtSourceSecRelative);
            double end = Math.Min(total, start + m.DurationSec);
            var span = scan.CornerPicture.OrderBy(s => Math.Abs(s.Start - start)).FirstOrDefault((Start: double.NaN, End: double.NaN));
            Av.Near($"CORNER MEME '{m.Id}' appears at its moment", start, span.Start, Budget.ExportSyncSec + Budget.FrameSec);
            Av.Near($"CORNER MEME '{m.Id}' disappears after its length", end, span.End, 0.07);
            if (m.PlaySound)
            {
                var snd = scan.MemeSound.OrderBy(s => Math.Abs(s.Start - start)).FirstOrDefault((Start: double.NaN, End: double.NaN));
                Av.Near($"CORNER MEME '{m.Id}' sound with its picture (A/V sync)", span.Start, snd.Start, Budget.ExportSyncSec + 0.02);
            }
        }
    }

    private static void MemeSound(MainEdit edit, ExportScan scan, double c)
    {
        double total = c + edit.ExportOut(edit.EndMs / 1000);
        var expected = new List<(double Start, double End)>();
        foreach (var m in MemePlacement.InlineOnly(edit.Memes).Where(m => m.PlaySound))
        {
            double anchor = edit.PreviewOut(edit.TrimStartMs / 1000 + m.AtSourceSecRelative);
            double start = c + anchor + edit.MemesBefore(anchor);
            expected.Add((start, start + m.DurationSec));
        }
        foreach (var m in MemePlacement.CornerOnly(edit.Memes).Where(m => m.PlaySound))
        {
            double start = c + edit.ExportOut(edit.TrimStartMs / 1000 + m.AtSourceSecRelative);
            expected.Add((start, Math.Min(total, start + m.DurationSec)));
        }
        MatchSpans("MEME SOUND (muted/deleted memes must be silent)", expected, scan.MemeSound, 0.08);
    }

    internal static void MatchSpans(string label, IEnumerable<(double Start, double End)> expected,
        IReadOnlyList<(double Start, double End)> actual, double tolerance)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var span in expected.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            else merged.Add(span);
        }
        var got = actual.OrderBy(s => s.Start).ToArray();
        Av.True(label, merged.Count == got.Length, $"{merged.Count} expected intervals, {got.Length} found");
        for (int i = 0; i < got.Length; i++)
        {
            Av.Near(label + " start", merged[i].Start, got[i].Start, tolerance);
            Av.Near(label + " end", merged[i].End, got[i].End, tolerance);
        }
    }

    /// <summary>Voice-over pulses (recorded against the preview's output clock) land on the picture they were spoken over.</summary>
    public static void Voice(MainEdit edit, ExportScan scan, double c, IReadOnlyList<double> spokenAtPreviewOut)
    {
        var want = spokenAtPreviewOut.Select(u => c + u + edit.MemesBefore(u)).ToList();
        Av.Markers("VOICE-OVER vs the picture it was recorded over", want, scan.VoicePulses.Where(t => !InMeme(scan, t)).ToList(), Budget.ExportSyncSec);
    }
}
