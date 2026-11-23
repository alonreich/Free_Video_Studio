using System.IO.Compression;

namespace FreeVideoStudio.Core.Infrastructure;

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

    public static void DecompressFile(string source, string destination, long maxBytes = long.MaxValue)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        bool complete = false;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[1 << 20];
            long written = 0;
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > maxBytes)
                    throw new InvalidDataException($"The compressed file expands beyond the expected {maxBytes} bytes.");
                output.Write(buffer, 0, read);
            }
            output.Flush(flushToDisk: true);
            complete = true;
        }
        finally
        {
            if (!complete)
            {
                try { if (File.Exists(destination)) File.Delete(destination); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLogger.Swallowed(ex); }
            }
        }
    }
}
