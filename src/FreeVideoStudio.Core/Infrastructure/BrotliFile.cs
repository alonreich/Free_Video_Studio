
using System.IO.Compression;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// DIST-SMART — Brotli with an explicit quality and a 16 MB window. <see cref="BrotliStream"/> only
/// exposes coarse levels; large binaries compress noticeably better with the big window.
/// </summary>
public static class BrotliFile
{
    public const int Window = 24;

    public static void Compress(ReadOnlySpan<byte> input, Stream output, int quality = 9)
    {
        using var encoder = new BrotliEncoder(Math.Clamp(quality, 0, 11), Window);
        byte[] buffer = new byte[1 << 20];
        while (true)
        {
            var status = encoder.Compress(input, buffer, out int consumed, out int written, isFinalBlock: true);
            output.Write(buffer, 0, written);
            input = input[consumed..];
            if (status == System.Buffers.OperationStatus.Done) return;
            if (status is System.Buffers.OperationStatus.InvalidData)
                throw new InvalidDataException("Brotli compression failed.");
        }
    }

    public static void CompressFile(string source, string destination, int quality = 9)
    {
        byte[] data = File.ReadAllBytes(source);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        Compress(data, output, quality);
    }

    /// <summary>Decompresses <paramref name="source"/> into <paramref name="destination"/>.</summary>
    public static void DecompressFile(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        brotli.CopyTo(output);
        output.Flush(flushToDisk: true);
    }
}
