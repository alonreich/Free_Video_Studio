using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public static class TwoPassEncoding
{
    public const string MasterCrf = "14";

    public static List<string> MasterCodecArgs() =>
    [
        "-c:v", "libx264",
        .. ExportColorPolicy.OutputTagArgs,
        "-preset", "veryfast",
        "-crf", MasterCrf,
        "-pix_fmt", "yuv420p",
        "-g", "60", "-keyint_min", "1",
        "-profile:v", "high", "-level:v", "5.1",
    ];

    public static List<string> PassArgs(int bitrateKbps, int passNumber, string passLogPrefix)
    {
        int kbps = Math.Min(EncoderManager.MaxBitrateKbps, Math.Max(300, bitrateKbps));
        return
        [
            "-c:v", "libx264",
            .. ExportColorPolicy.OutputTagArgs,
            "-preset", "medium",
            "-b:v", $"{kbps}k",
            "-pass", passNumber.ToString(),
            "-passlogfile", passLogPrefix,
            "-bf", "2",
            "-profile:v", "high", "-level:v", "5.1",
            "-pix_fmt", "yuv420p",
        ];
    }

    public static void Cleanup(string masterPath, string passLogPrefix)
    {
        foreach (string path in new[]
                 {
                     masterPath,
                     passLogPrefix + "-0.log",
                     passLogPrefix + "-0.log.mbtree",
                     passLogPrefix + ".log",
                     passLogPrefix + ".log.mbtree",
                 })
        {
            if (string.IsNullOrEmpty(path)) continue;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        }
    }
}
