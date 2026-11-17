using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FreeVideoStudio.Core.Media;

public sealed record CornerMemeInput(
    int InputIndex,
    bool IsImage,
    bool HasAudio,
    double StartSec,
    double EndSec,
    MemeOverlayCorner Corner,
    MemeOverlaySize Size,
    bool PlaySound,
    double GainDb = 0,
    double PosX = -1,
    double PosY = -1,
    MemeOverlayAnimation Animation = MemeOverlayAnimation.None);

public sealed record CornerMemeOverlayResult(IReadOnlyList<string> Filters, string VideoLabel, string AudioLabel);

public static class CornerMemeOverlayGraph
{
    public const double DuckGain = 0.5;

    public static CornerMemeOverlayResult Build(
        string videoIn,
        string audioIn,
        IReadOnlyList<CornerMemeInput> memes,
        int frameW,
        int frameH,
        string fps,
        string labelPrefix)
    {
        var filters = new List<string>();
        if (memes == null || memes.Count == 0) return new CornerMemeOverlayResult(filters, videoIn, audioIn);

        string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
        string currentV = videoIn;
        var audioPads = new List<string>();

        for (int k = 0; k < memes.Count; k++)
        {
            var m = memes[k];
            double len = m.EndSec - m.StartSec;
            if (!(len > 0.001)) continue;

            string ov = $"[{labelPrefix}cm{k}_v]";
            string outV = $"[{labelPrefix}cm{k}_o]";
            var (x, y) = MemeOverlayLayout.OverlayPosition(frameW, frameH, m.Corner, m.PosX, m.PosY);
            bool fromRight = m.PosX >= 0 && m.PosY >= 0
                ? m.PosX >= 0.5
                : m.Corner is MemeOverlayCorner.TopRight or MemeOverlayCorner.BottomRight;
            string ox = MemeTransitions.CornerOverlayX(m.Animation, x, fromRight, m.StartSec, m.EndSec);

            filters.Add(
                $"[{m.InputIndex}:v]trim=duration={F(len)},setpts=PTS-STARTPTS,fps={fps}:round=near," +
                $"{MemeOverlayLayout.ScaleFilter(frameW, frameH, m.Size)},format=yuva420p" +
                $"{MemeTransitions.CornerAlphaFilters(m.Animation, len)}," +
                $"setpts=PTS+{F(m.StartSec)}/TB{ov}");
            filters.Add(
                $"{currentV}{ov}overlay=x={ox}:y={y}:eof_action=pass:" +
                $"enable='between(t,{F(m.StartSec)},{F(m.EndSec)})'{outV}");
            currentV = outV;

            if (m.PlaySound && m.HasAudio && !m.IsImage)
            {
                string a = $"[{labelPrefix}cm{k}_a]";
                long delayMs = (long)Math.Round(m.StartSec * 1000.0);
                filters.Add(
                    $"[{m.InputIndex}:a]atrim=duration={F(len)},asetpts=PTS-STARTPTS,aresample=48000," +
                    $"aformat=sample_fmts=fltp:channel_layouts=stereo:sample_rates=48000" +
                    $"{MemeLoudness.Chain(m.GainDb)}{MemeLoudness.SpliceFade(len)}," +
                    $"adelay=delays={delayMs}:all=1{a}");
                audioPads.Add(a);
            }
        }

        string currentA = audioIn;
        if (audioPads.Count > 0)
        {
            var windows = new List<string>();
            foreach (var m in memes)
                if (m.PlaySound && m.HasAudio && !m.IsImage && m.EndSec - m.StartSec > 0.001)
                    windows.Add($"between(t,{F(m.StartSec)},{F(m.EndSec)})");
            string ducked = $"[{labelPrefix}cm_duck]";
            filters.Add($"{audioIn}volume=volume='if({string.Join("+", windows)},{DuckGain.ToString("0.###", CultureInfo.InvariantCulture)},1)':eval=frame{ducked}");
            string mixed = $"[{labelPrefix}cm_amix]";
            filters.Add($"{ducked}{string.Concat(audioPads)}amix=inputs={audioPads.Count + 1}:normalize=0:duration=first:dropout_transition=0{mixed}");
            currentA = mixed;
        }

        return new CornerMemeOverlayResult(filters, currentV, currentA);
    }

    public static IEnumerable<string> InputArgs(string path, bool isImage, double durationSec, string fps, double clipInSec = 0)
    {
        if (isImage)
        {
            yield return "-loop"; yield return "1";
            yield return "-framerate"; yield return fps;
            yield return "-t"; yield return durationSec.ToString("F3", CultureInfo.InvariantCulture);
        }
        else if (durationSec > 0.001)
        {
            if (clipInSec > 0.0005) { yield return "-ss"; yield return clipInSec.ToString("F3", CultureInfo.InvariantCulture); }
            yield return "-t"; yield return durationSec.ToString("F3", CultureInfo.InvariantCulture);
        }
        yield return "-i"; yield return path;
    }
}
