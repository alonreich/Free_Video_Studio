using System;
using System.IO;
using FreeVideoStudio.Core.Media;
using SkiaSharp;

namespace FreeVideoStudio.App.Services;

internal static class AiFrameCrop
{
    public const string Tag = "AIKILLCROP_01";

    public const int JpegQuality = 85;

    public const double Margin = 0.5;

    public const double MinSide = 0.18;

    public static PopupRegion Expand(PopupRegion r)
    {
        double w = Math.Clamp(Math.Max(MinSide, r.W * (1 + 2 * Margin)), 0, 1);
        double h = Math.Clamp(Math.Max(MinSide, r.H * (1 + 2 * Margin)), 0, 1);
        double cx = r.X + r.W / 2, cy = r.Y + r.H / 2;
        double x = Math.Clamp(cx - w / 2, 0, 1 - w);
        double y = Math.Clamp(cy - h / 2, 0, 1 - h);
        return new PopupRegion(x, y, w, h);
    }

    public static byte[] Crop(byte[] jpeg, PopupRegion region)
    {
        using SKBitmap src = SKBitmap.Decode(jpeg) ?? throw new IOException("Could not read the picture to crop.");
        var r = Expand(region);
        int x = (int)Math.Floor(r.X * src.Width), y = (int)Math.Floor(r.Y * src.Height);
        int w = Math.Max(16, Math.Min(src.Width - x, (int)Math.Ceiling(r.W * src.Width)));
        int h = Math.Max(16, Math.Min(src.Height - y, (int)Math.Ceiling(r.H * src.Height)));
        using var cropped = new SKBitmap(w, h);
        if (!src.ExtractSubset(cropped, new SKRectI(x, y, x + w, y + h)))
            throw new IOException("Could not crop the picture.");
        return Encode(cropped);
    }

    public static byte[] Downscale(byte[] jpeg, int width)
    {
        using SKBitmap src = SKBitmap.Decode(jpeg) ?? throw new IOException("Could not read the picture to shrink.");
        if (src.Width <= width) return jpeg;
        int h = Math.Max(1, (int)Math.Round(src.Height * (double)width / src.Width));
        using SKBitmap small = src.Resize(new SKImageInfo(width, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
                               ?? throw new IOException("Could not shrink the picture.");
        return Encode(small);
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality) ?? throw new IOException("Could not encode the picture.");
        return data.ToArray();
    }
}
