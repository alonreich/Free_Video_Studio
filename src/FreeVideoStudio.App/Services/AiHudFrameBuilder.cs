using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FreeVideoStudio.Core.Media;
using SkiaSharp;

namespace FreeVideoStudio.App.Services;

public static class AiHudFrameBuilder
{
    public const int MaxLongEdge = 1600;
    public const int JpegQuality = 85;

    public static AiHudFrame FromSnapshotFile(string snapshotPath, int sourceWidth, int sourceHeight, string videoPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
            throw new FileNotFoundException("Frozen frame not found.", snapshotPath);
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source frame dimensions are unknown.");

        byte[] snapshotBytes = File.ReadAllBytes(snapshotPath);
        string frameKey = "sha256:" + ShortHash(snapshotBytes);

        using SKBitmap decoded = SKBitmap.Decode(snapshotBytes) ?? throw new IOException("Could not decode the frozen frame.");
        byte[] jpeg = EncodeJpeg(decoded);

        return new AiHudFrame(jpeg, sourceWidth, sourceHeight, SourceFingerprint(videoPath), frameKey);
    }

    public static byte[] EncodeJpeg(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int longEdge = Math.Max(bitmap.Width, bitmap.Height);
        double scale = longEdge > MaxLongEdge ? (double)MaxLongEdge / longEdge : 1.0;

        SKBitmap? resized = null;
        try
        {
            SKBitmap toEncode = bitmap;
            if (scale < 1.0)
            {
                int w = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                int h = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                resized = bitmap.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
                          ?? throw new IOException("Could not downscale the frozen frame.");
                toEncode = resized;
            }

            using SKImage image = SKImage.FromBitmap(toEncode);
            using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                               ?? throw new IOException("Could not encode the frozen frame.");
            return data.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }
    }

    public static string SourceFingerprint(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return "unknown";
        var info = new FileInfo(videoPath);
        string identity = string.Create(CultureInfo.InvariantCulture,
            $"{info.FullName.ToUpperInvariant()}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}");
        return "src:" + ShortHash(Encoding.UTF8.GetBytes(identity));
    }

    private static string ShortHash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes))[..24].ToLowerInvariant();
}
