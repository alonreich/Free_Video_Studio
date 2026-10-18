using System.Globalization;

namespace FreeVideoStudio.Core.Media;

/// <summary>VOSPLICE_01: narration pauses for a cutaway even when the music keeps playing.</summary>
public static class VoiceOverSplice
{
    public readonly record struct Piece(double AudioStartSec, double OutputStartSec, double DurationSec);

    public static IReadOnlyList<Piece> Place(double takeStartSec, double takeDurationSec, double bodyDurationSec,
        double introSec, IEnumerable<(double BodySec, double DurationSec)> insertions)
    {
        var result = new List<Piece>();
        double cursor = Math.Max(0, takeStartSec), end = Math.Min(bodyDurationSec, takeStartSec + takeDurationSec);
        double shift = introSec;
        foreach (var insertion in insertions.OrderBy(m => m.BodySec))
        {
            if (insertion.BodySec <= cursor + 1e-9) { shift += insertion.DurationSec; continue; }
            if (insertion.BodySec >= end - 1e-9) break;
            result.Add(new(cursor - takeStartSec, cursor + shift, insertion.BodySec - cursor));
            cursor = insertion.BodySec;
            shift += insertion.DurationSec;
        }
        if (end > cursor) result.Add(new(cursor - takeStartSec, cursor + shift, end - cursor));
        return result;
    }

    public static IEnumerable<string> Filters(int inputIndex, string outputLabel, string prefix, IReadOnlyList<Piece> pieces)
    {
        string F(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);
        if (pieces.Count == 0)
        {
            yield return $"anullsrc=r=48000:cl=stereo,atrim=duration=0.001{outputLabel}";
            yield break;
        }
        string input = $"[{inputIndex}:a]";
        if (pieces.Count > 1)
            yield return input + $"asplit={pieces.Count}" + string.Concat(Enumerable.Range(0, pieces.Count).Select(i => $"[{prefix}_src{i}]"));
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            string source = pieces.Count == 1 ? input : $"[{prefix}_src{i}]";
            string target = pieces.Count == 1 ? outputLabel : $"[{prefix}_part{i}]";
            long samples = (long)Math.Round(p.OutputStartSec * 48000);
            yield return source + $"aresample=48000,atrim=start={F(p.AudioStartSec)}:duration={F(p.DurationSec)}," +
                $"asetpts=PTS-STARTPTS,adelay={samples}S:all=1{target}";
        }
        if (pieces.Count > 1)
            yield return string.Concat(Enumerable.Range(0, pieces.Count).Select(i => $"[{prefix}_part{i}]")) +
                $"amix=inputs={pieces.Count}:duration=longest:dropout_transition=0:normalize=0{outputLabel}";
    }
}
