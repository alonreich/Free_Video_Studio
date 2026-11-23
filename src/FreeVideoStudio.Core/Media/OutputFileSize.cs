namespace FreeVideoStudio.Core.Media;

public static class OutputFileSize
{
    public static string FormatMegabytes(double? megabytes)
    {
        if (megabytes is not { } mb || !double.IsFinite(mb) || mb < 0) return "—";
        if (mb >= 1024 * 1024) return $"{mb / (1024 * 1024):0.0} TB";
        if (mb >= 1024) return $"{mb / 1024:0.0} GB";
        return mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
    }

    public static int MergerConstantQuality(int percent)
        => percent >= 100 ? 15 : Math.Max(15, 35 - (int)((percent - 5) * 20.0 / 95.0));

    public static double MergerQualityRatio(int percent)
        => percent >= 100 ? 1.0 : Math.Pow(2, (15 - MergerConstantQuality(percent)) / 6.0);

    public static int MergerTargetKbps(double averageSourceKbps)
        => Math.Max(800, (int)Math.Min(EncoderManager.MaxBitrateKbps, averageSourceKbps));

    public static double FromBitrate(double videoKbps, double videoSeconds, double audioKbps, double audioSeconds)
        => (videoKbps * videoSeconds + audioKbps * audioSeconds) * 1000 / 8 / (1024 * 1024);
}
