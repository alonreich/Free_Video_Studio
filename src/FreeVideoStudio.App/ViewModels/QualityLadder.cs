using System;

namespace FreeVideoStudio.App.ViewModels;

public static class QualityLadder
{
    public readonly record struct Tier(string Name, double Bpp);

    public static readonly Tier[] Tiers =
    {
        new("Pixelated",  0.024),
        new("Blurry",     0.036),
        new("Low",        0.048),
        new("Okay",       0.060),
        new("Good −",     0.070),
        new("Good",       0.081),
        new("Good +",     0.093),
        new("Sharp −",    0.108),
        new("Sharp",      0.125),
        new("Sharp +",    0.145),
        new("High −",     0.168),
        new("High",       0.195),
        new("High +",     0.226),
        new("Ultra −",    0.265),
        new("Ultra",      0.315),
        new("Ultra +",    0.380),
        new("Premium HQ", 0.470),
        new("Original",   0.0),
    };

    public static readonly int OriginalIndex = Tiers.Length - 1;

    public static readonly int MaxIndex = Tiers.Length - 1;

    public static readonly int DefaultIndex = 6;

    public static int ClampIndex(int index) => Math.Clamp(index, 0, MaxIndex);

    public const int WorkerConstantQualityLevel = 20;

    public static int ToWorkerQualityLevel(int tierIndex)
        => IsOriginal(tierIndex) ? WorkerConstantQualityLevel : ClampIndex(tierIndex);

    public const double FreezeSecondCostFactor = 0.15;

    public static double BillableSeconds(double totalSec, double freezeSec)
    {
        if (totalSec <= 0) return 0;
        freezeSec = Math.Clamp(freezeSec, 0, totalSec);
        return (totalSec - freezeSec) + (freezeSec * FreezeSecondCostFactor);
    }

    public static string NameOf(int index) => Tiers[ClampIndex(index)].Name;

    public static bool IsOriginal(int index) => ClampIndex(index) == OriginalIndex;

    public static double? TargetMbFor(int index, double durationSec, int width, int height, bool isPortrait, double freezeSec = 0)
    {
        index = ClampIndex(index);
        if (index == OriginalIndex) return null;
        if (durationSec <= 0 || width <= 0 || height <= 0) return null;

        double labelBpp = Tiers[index].Bpp;

        double rawBpp = isPortrait ? labelBpp : labelBpp * 1.5;

        double videoKbps = rawBpp * width * height * 60.0 / 1000.0;
        if (videoKbps < 100) videoKbps = 100;

        double audioKbps = 192;

        double videoSec = BillableSeconds(durationSec, freezeSec);

        double targetMb = ((videoKbps * videoSec) + (audioKbps * durationSec)) / 8192.0;
        if (targetMb * 1024 < durationSec * 48)
        {
            audioKbps = 64;
            targetMb = ((videoKbps * videoSec) + (audioKbps * durationSec)) / 8192.0;
        }

        return targetMb;
    }

    public static string FormatSize(double megabytes)
        => FreeVideoStudio.Core.Media.OutputFileSize.FormatMegabytes(megabytes);
}
