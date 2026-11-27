using System.Globalization;

namespace FreeVideoStudio.Core.Media;

public static class ShareCover
{
    public const string Tag = "SHARECOVER_01";

    public const double Fraction = 0.92;

    public static string ShrinkFrames(string inLabel, string outLabel, string prefix)
    {
        string f = Fraction.ToString("0.####", CultureInfo.InvariantCulture);
        return $"{inLabel}split=2[{prefix}_bg][{prefix}_fg];" +
               $"[{prefix}_bg]boxblur=luma_radius=40:luma_power=2:chroma_radius=20:chroma_power=2,eq=brightness=-0.12[{prefix}_bgb];" +
               $"[{prefix}_fg]scale=w='trunc(iw*{f}/2)*2':h='trunc(ih*{f}/2)*2':flags=lanczos[{prefix}_fgs];" +
               $"[{prefix}_bgb][{prefix}_fgs]overlay=x=(W-w)/2:y=(H-h)/2,setsar=1{outLabel}";
    }

    public static string ShrinkLeading(string inLabel, string outLabel, double coverSec)
    {
        string d = coverSec.ToString("F4", CultureInfo.InvariantCulture);
        return $"{inLabel}split=2[sc_base][sc_src];" +
               $"[sc_src]trim=duration={d},setpts=PTS-STARTPTS[sc_cover_in];" +
               ShrinkFrames("[sc_cover_in]", "[sc_cover]", "sc") + ";" +
               $"[sc_base][sc_cover]overlay=x=0:y=0:eof_action=pass,format=yuv420p{outLabel}";
    }
}
