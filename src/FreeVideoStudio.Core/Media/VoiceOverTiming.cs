using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FreeVideoStudio.Core.Media;

/// <summary>VOTIMING_01: output placement and the timing of the picture a take was spoken over.</summary>
public static class VoiceOverTiming
{
    public static double OutputStart(VoiceOverTake take, Func<double, double> mapper) =>
        mapper(take.StartSec) + take.OutputOffsetSec;

    /// <summary>
    /// Only timing inside this take matters. A speed change before the take can safely move its
    /// source anchor; zoom, colour, volume and changes after the take cannot desynchronise speech.
    /// </summary>
    public static string Signature(OutputTimeline timeline, double trimStartSec, double trimEndSec,
        double startSec, double endSec)
    {
        var b = new StringBuilder("vo1");
        void N(double x) => b.Append('|').Append(x.ToString("0.######", CultureInfo.InvariantCulture));
        N(Math.Max(startSec, trimStartSec));
        N(Math.Min(endSec, trimEndSec));
        double? lastStart = null, lastEnd = null, lastSpeed = null;
        void Flush()
        {
            if (lastStart.HasValue) { b.Append("|play"); N(lastStart.Value); N(lastEnd!.Value); N(lastSpeed!.Value); }
            lastStart = lastEnd = lastSpeed = null;
        }
        foreach (var c in timeline.Chunks)
        {
            double a = c.SourceStartSec + trimStartSec;
            double z = c.SourceEndSec + trimStartSec;
            if (c.IsFreeze)
            {
                if (a >= startSec - 0.000001 && a <= endSec + 0.000001)
                { Flush(); b.Append("|hold"); N(a); N(c.FreezeHoldSec); }
                continue;
            }
            a = Math.Max(a, startSec); z = Math.Min(z, endSec);
            if (z <= a) continue;
            if (c.IsCut) { Flush(); b.Append("|cut"); N(a); N(z); continue; }
            if (lastEnd.HasValue && Math.Abs(lastEnd.Value - a) < 0.000001 && lastSpeed == c.Speed)
                lastEnd = z;
            else { Flush(); lastStart = a; lastEnd = z; lastSpeed = c.Speed; }
        }
        Flush();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString())));
    }

    public static bool NeedsReview(VoiceOverTake take, OutputTimeline timeline,
        double trimStartSec, double trimEndSec) => take.NeedsTimingReview ||
        (take.TimingSignature != null && take.RecordedEndSec is double end &&
         take.TimingSignature != Signature(timeline, trimStartSec, trimEndSec, take.StartSec, end));
}
