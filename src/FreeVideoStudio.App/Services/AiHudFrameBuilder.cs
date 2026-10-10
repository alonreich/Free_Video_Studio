// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FreeVideoStudio.Core.Media;
using SkiaSharp;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIHUD_01 — turns the Crop Tool's ONE frozen snapshot into the <see cref="AiHudFrame"/> an AI
/// provider is allowed to see. No UI types; call it off the UI thread.
///
/// <para><b>Only this frame.</b> The input is the snapshot file the user is looking at — the same
/// pixels the source canvas shows. The video file is touched only for its size and timestamp, to
/// fingerprint it; its contents are never read here and never sent.</para>
///
/// <para><b>Size.</b> The JPEG is downscaled so its long edge is at most <see cref="MaxLongEdge"/>.
/// The AI answers in normalized 0..1 coordinates, so downscaling cannot shift a box; the mapping
/// back to pixels uses the SOURCE dimensions, never the JPEG's.</para>
///
/// <para><b>Identity.</b> <see cref="AiHudFrame.FrameKey"/> is a hash of the snapshot's own bytes,
/// so two different frames can never share a cached answer. <see cref="AiHudFrame.SourceFingerprint"/>
/// is a hash of the clip's full path, length and write time — opaque, no path text.</para>
/// </summary>
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

    /// <summary>Opaque clip identity: hash of full path + length + last write (UTC ticks). No path text leaves.</summary>
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
