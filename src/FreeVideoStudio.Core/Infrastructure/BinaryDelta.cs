
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// BINDELTA_01 — a binary patch: "the new file = the old file + these differences".
///
/// <para>The main program file changes in every release, but most of its bytes only MOVE (new code
/// shifts everything after it). A plain copy-or-insert diff loses those; this one follows bsdiff:
/// it finds long matches between old and new, then extends each match ALLOWING byte differences,
/// stored as (new − old). Shifted code turns into long runs of zero differences, which Brotli
/// compresses to almost nothing. Matches are found with a hash index over the old file instead of
/// bsdiff's suffix array (memory: one int per 4 old bytes).</para>
///
/// <para>⚠️ A patch is never trusted on its own. The header names the exact old and new files by
/// length and SHA-256; <see cref="Apply(Stream, Stream, Stream, CancellationToken)"/> refuses any
/// other old file and re-hashes what it built. A wrong, damaged or malicious patch therefore produces
/// an error, never a wrong file.</para>
///
/// <para>BINDELTA_02 — applying never holds a whole program file in memory: old file and patch are
/// streamed through 64 KiB buffers (<see cref="StreamBufferSize"/>) into the output stream.</para>
///
/// <para>Format: "FVSDELTA1", old length (8), old SHA-256 (32), new length (8), new SHA-256 (32),
/// then a Brotli stream of blocks: varint diffLength, varint extraLength, zig-zag varint seek,
/// diffLength difference bytes, extraLength literal bytes.</para>
/// </summary>
public static class BinaryDelta
{
    private static readonly byte[] Magic = "FVSDELTA1"u8.ToArray();
    private const int HeaderLength = 9 + 8 + 32 + 8 + 32;
    private const int Key = 16;
    private const int Step = 4;
    private const int MinMatch = 16;

    /// <summary>Header of a patch: which file it applies to and which file it makes.</summary>
    public readonly record struct Header(long OldLength, string OldSha256, long NewLength, string NewSha256);


    /// <summary>Writes a patch that turns <paramref name="oldFile"/> into <paramref name="newFile"/>.</summary>
    public static void Create(byte[] oldFile, byte[] newFile, Stream output, int brotliQuality = 9)
    {
        output.Write(Magic);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(number, oldFile.LongLength); output.Write(number);
        output.Write(SHA256.HashData(oldFile));
        BinaryPrimitives.WriteInt64LittleEndian(number, newFile.LongLength); output.Write(number);
        output.Write(SHA256.HashData(newFile));

        using var body = new MemoryStream(newFile.Length + 4096);
        WriteBlocks(oldFile, newFile, body);
        BrotliFile.Compress(body.GetBuffer().AsSpan(0, (int)body.Length), output, brotliQuality);
    }

    private static void WriteBlocks(byte[] old, byte[] neu, Stream body)
    {
        int[] table = BuildIndex(old, out int mask);
        int oldSize = old.Length, newSize = neu.Length;
        int scan = 0, len = 0, pos = 0, lastScan = 0, lastPos = 0, lastOffset = 0;

        while (scan < newSize)
        {
            int oldScore = 0;
            int scsc = scan += len;
            for (; scan < newSize; scan++)
            {
                (pos, len) = Search(old, neu, scan, table, mask, lastOffset);
                for (; scsc < scan + len; scsc++)
                    if (scsc + lastOffset >= 0 && scsc + lastOffset < oldSize && old[scsc + lastOffset] == neu[scsc]) oldScore++;
                if ((len == oldScore && len != 0) || len > oldScore + 8) break;
                if (scan + lastOffset >= 0 && scan + lastOffset < oldSize && old[scan + lastOffset] == neu[scan]) oldScore--;
            }

            if (len == oldScore && scan != newSize) continue;

            int s = 0, sf = 0, lenF = 0;
            for (int i = 0; lastScan + i < scan && lastPos + i < oldSize;)
            {
                if (old[lastPos + i] == neu[lastScan + i]) s++;
                i++;
                if (s * 2 - i > sf * 2 - lenF) { sf = s; lenF = i; }
            }

            int lenB = 0;
            if (scan < newSize)
            {
                int sb = 0; s = 0;
                for (int i = 1; scan >= lastScan + i && pos >= i; i++)
                {
                    if (old[pos - i] == neu[scan - i]) s++;
                    if (s * 2 - i > sb * 2 - lenB) { sb = s; lenB = i; }
                }
            }

            if (lastScan + lenF > scan - lenB)
            {
                int overlap = (lastScan + lenF) - (scan - lenB);
                s = 0; int ss = 0, lens = 0;
                for (int i = 0; i < overlap; i++)
                {
                    if (neu[lastScan + lenF - overlap + i] == old[lastPos + lenF - overlap + i]) s++;
                    if (neu[scan - lenB + i] == old[pos - lenB + i]) s--;
                    if (s > ss) { ss = s; lens = i + 1; }
                }
                lenF += lens - overlap;
                lenB -= lens;
            }

            int extra = (scan - lenB) - (lastScan + lenF);
            long seek = (long)(pos - lenB) - (lastPos + lenF);
            WriteVarint(body, (ulong)lenF);
            WriteVarint(body, (ulong)extra);
            WriteVarint(body, (ulong)((seek << 1) ^ (seek >> 63)));
            byte[] diff = new byte[lenF];
            for (int i = 0; i < lenF; i++) diff[i] = (byte)(neu[lastScan + i] - old[lastPos + i]);
            body.Write(diff);
            body.Write(neu, lastScan + lenF, extra);

            lastScan = scan - lenB;
            lastPos = pos - lenB;
            lastOffset = pos - scan;
        }
    }

    private static int[] BuildIndex(byte[] old, out int mask)
    {
        int entries = Math.Max(1, old.Length / Step);
        int size = 1 << Math.Clamp((int)Math.Ceiling(Math.Log2(entries * 2.0)), 10, 28);
        mask = size - 1;
        int[] table = new int[size];
        Array.Fill(table, -1);
        for (int p = 0; p + Key <= old.Length; p += Step)
            table[Hash(old, p) & mask] = p;
        return table;
    }

    /// <summary>The longest exact match for neu[scan..] among the indexed candidate and the last offset.</summary>
    private static (int Pos, int Len) Search(byte[] old, byte[] neu, int scan, int[] table, int mask, int lastOffset)
    {
        int bestPos = 0, bestLen = 0;

        int follow = scan + lastOffset;
        if (follow >= 0 && follow < old.Length)
        {
            int l = MatchLength(old, follow, neu, scan);
            if (l > bestLen) { bestPos = follow; bestLen = l; }
        }

        if (scan + Key <= neu.Length)
        {
            int candidate = table[Hash(neu, scan) & mask];
            if (candidate >= 0 && candidate != follow)
            {
                int l = MatchLength(old, candidate, neu, scan);
                if (l > bestLen) { bestPos = candidate; bestLen = l; }
            }
        }

        return bestLen >= MinMatch ? (bestPos, bestLen) : (bestPos, 0);
    }

    private static int MatchLength(byte[] old, int oldPos, byte[] neu, int newPos)
    {
        int max = Math.Min(old.Length - oldPos, neu.Length - newPos);
        return old.AsSpan(oldPos, max).CommonPrefixLength(neu.AsSpan(newPos, max));
    }

    private static int Hash(byte[] data, int p)
    {
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p, 8));
        ulong b = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p + 8, 8));
        ulong h = (a * 0x9E3779B97F4A7C15UL) ^ BitOperations.RotateLeft(b * 0xC2B2AE3D27D4EB4FUL, 31);
        return (int)(h ^ (h >> 29));
    }


    public static Header ReadHeader(Stream patch)
    {
        Span<byte> head = stackalloc byte[HeaderLength];
        patch.ReadExactly(head);
        if (!head[..Magic.Length].SequenceEqual(Magic)) throw new InvalidDataException("Not a Free Video Studio patch.");
        int at = Magic.Length;
        long oldLength = BinaryPrimitives.ReadInt64LittleEndian(head.Slice(at, 8)); at += 8;
        string oldSha = Convert.ToHexString(head.Slice(at, 32)).ToLowerInvariant(); at += 32;
        long newLength = BinaryPrimitives.ReadInt64LittleEndian(head.Slice(at, 8)); at += 8;
        string newSha = Convert.ToHexString(head.Slice(at, 32)).ToLowerInvariant();
        if (oldLength < 0 || newLength < 0 || newLength > int.MaxValue) throw new InvalidDataException("Patch sizes are invalid.");
        return new Header(oldLength, oldSha, newLength, newSha);
    }

    /// <summary>
    /// Rebuilds the new file in memory. Kept for small inputs and existing callers; it runs the SAME
    /// streaming decoder as <see cref="Apply(Stream, Stream, Stream, CancellationToken)"/>. Throws
    /// <see cref="InvalidDataException"/> when the old file is not the one the patch was made for,
    /// the patch is damaged, or the result is not the promised file.
    /// </summary>
    public static byte[] Apply(byte[] oldFile, Stream patch)
    {
        using var old = new MemoryStream(oldFile, writable: false);
        using var result = new MemoryStream();
        Apply(old, patch, result, CancellationToken.None);
        return result.ToArray();
    }


    /// <summary>
    /// BINDELTA_02 — size of every managed buffer the streaming apply uses. Peak memory is
    /// 3 × this + the Brotli decoder's window (≤ 16 MB, <see cref="BrotliFile.Window"/>), whatever
    /// the size of the program file. The old whole-file path held old EXE + new EXE in RAM.
    /// </summary>
    public const int StreamBufferSize = 64 * 1024;

    /// <summary>
    /// BINDELTA_02 — rebuilds the new file from a seekable <paramref name="oldFile"/> and
    /// <paramref name="patch"/> into <paramref name="output"/>, through bounded 64 KiB buffers.
    /// The old file is hashed and length-checked BEFORE anything is written; the result is hashed
    /// while it is written and checked against the header at the end. On any exception the
    /// output holds an unverified partial file and must be discarded (see <see cref="ApplyFile"/>).
    /// </summary>
    public static Header Apply(Stream oldFile, Stream patch, Stream output, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(oldFile);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(output);
        if (!oldFile.CanRead || !oldFile.CanSeek) throw new ArgumentException("The old file must be readable and seekable.", nameof(oldFile));
        if (!output.CanWrite) throw new ArgumentException("The output must be writable.", nameof(output));
        cancel.ThrowIfCancellationRequested();

        Header header;
        try { header = ReadHeader(patch); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("The patch ended early.", ex); }

        byte[] oldBuffer = new byte[StreamBufferSize];
        byte[] newBuffer = new byte[StreamBufferSize];

        if (oldFile.Length != header.OldLength)
            throw new InvalidDataException("This patch was made for a different version of the file.");
        oldFile.Position = 0;
        using (var oldHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            long left = header.OldLength;
            while (left > 0)
            {
                cancel.ThrowIfCancellationRequested();
                int n = (int)Math.Min(oldBuffer.Length, left);
                oldFile.ReadExactly(oldBuffer, 0, n);
                oldHash.AppendData(oldBuffer, 0, n);
                left -= n;
            }
            if (!HashEquals(oldHash, header.OldSha256))
                throw new InvalidDataException("This patch was made for a different version of the file.");
        }

        using var body = new BrotliStream(patch, CompressionMode.Decompress, leaveOpen: true);
        var reader = new PatchReader(body, StreamBufferSize);
        using var newHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = header.NewLength, newPos = 0, oldPos = 0, oldAt = oldFile.Position;
        while (newPos < total)
        {
            cancel.ThrowIfCancellationRequested();
            ulong diff = reader.ReadVarint(), extra = reader.ReadVarint(), zz = reader.ReadVarint();
            long seek = (long)(zz >> 1) ^ -(long)(zz & 1);
            if (diff > (ulong)(total - newPos) || extra > (ulong)(total - newPos) - diff ||
                oldPos < 0 || oldPos + (long)diff > header.OldLength)
                throw new InvalidDataException("The patch is damaged.");

            long diffLeft = (long)diff;
            if (diffLeft > 0 && oldAt != oldPos) { oldFile.Position = oldPos; oldAt = oldPos; }
            while (diffLeft > 0)
            {
                cancel.ThrowIfCancellationRequested();
                int n = (int)Math.Min(StreamBufferSize, diffLeft);
                reader.ReadExactly(newBuffer.AsSpan(0, n));
                oldFile.ReadExactly(oldBuffer, 0, n);
                AddModulo256(newBuffer.AsSpan(0, n), oldBuffer.AsSpan(0, n));
                Emit(output, newHash, newBuffer, n);
                diffLeft -= n;
                oldAt += n;
            }
            newPos += (long)diff;
            oldPos += (long)diff;

            long extraLeft = (long)extra;
            while (extraLeft > 0)
            {
                cancel.ThrowIfCancellationRequested();
                int n = (int)Math.Min(StreamBufferSize, extraLeft);
                reader.ReadExactly(newBuffer.AsSpan(0, n));
                Emit(output, newHash, newBuffer, n);
                extraLeft -= n;
            }
            newPos += (long)extra;
            oldPos += seek;
        }

        output.Flush();
        if (!HashEquals(newHash, header.NewSha256))
            throw new InvalidDataException("The patched file does not match the expected result.");
        return header;
    }

    /// <summary>
    /// BINDELTA_02 — streams <paramref name="oldPath"/> + <paramref name="patchPath"/> into a NEW file
    /// <paramref name="outputPath"/> (replaced if present) and flushes it to disk. When anything fails
    /// (damaged patch, wrong old file, wrong result, cancellation) the output file is deleted, so a
    /// file at <paramref name="outputPath"/> after a normal return is complete and header-verified.
    /// Callers still verify it against the signed manifest and publisher before promoting it.
    /// </summary>
    public static Header ApplyFile(string oldPath, string patchPath, string outputPath, CancellationToken cancel = default)
    {
        using var old = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
        using var patch = new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
        bool created = false, done = false;
        try
        {
            Header header;
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0))
            {
                created = true;
                header = Apply(old, patch, output, cancel);
                if (output.Length != header.NewLength) throw new InvalidDataException("The patched file has the wrong length.");
                output.Flush(flushToDisk: true);
            }
            done = true;
            return header;
        }
        finally
        {
            if (created && !done)
            {
                try { File.Delete(outputPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLogger.Swallowed(ex); }
            }
        }
    }

    private static void Emit(Stream output, IncrementalHash hash, byte[] buffer, int count)
    {
        hash.AppendData(buffer, 0, count);
        output.Write(buffer, 0, count);
    }

    /// <summary>target[i] = (byte)(target[i] + old[i]) — vectorised, wraps modulo 256.</summary>
    private static void AddModulo256(Span<byte> target, ReadOnlySpan<byte> old)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i + Vector<byte>.Count <= target.Length; i += Vector<byte>.Count)
                (new Vector<byte>(target[i..]) + new Vector<byte>(old[i..])).CopyTo(target[i..]);
        }
        for (; i < target.Length; i++) target[i] += old[i];
    }

    private static bool HashEquals(IncrementalHash hash, string expectedHex)
    {
        Span<byte> actual = stackalloc byte[32];
        hash.GetHashAndReset(actual);
        return string.Equals(Convert.ToHexString(actual), expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bounded read-ahead over the decompressed patch. Tolerates streams that return any number of
    /// bytes per Read (fragmented input); a premature end is a damaged patch, never a short file.
    /// </summary>
    private sealed class PatchReader(Stream source, int size)
    {
        private readonly byte[] _buffer = new byte[size];
        private int _at, _count;

        private bool Fill()
        {
            _at = 0;
            _count = source.Read(_buffer, 0, _buffer.Length);
            return _count > 0;
        }

        public int ReadByte()
        {
            if (_at == _count && !Fill()) return -1;
            return _buffer[_at++];
        }

        public void ReadExactly(Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                if (_at == _count && !Fill()) throw new InvalidDataException("The patch ended early.");
                int n = Math.Min(destination.Length, _count - _at);
                _buffer.AsSpan(_at, n).CopyTo(destination);
                _at += n;
                destination = destination[n..];
            }
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                int b = ReadByte();
                if (b < 0) throw new InvalidDataException("The patch ended early.");
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new InvalidDataException("The patch is damaged.");
        }
    }

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80) { s.WriteByte((byte)(value | 0x80)); value >>= 7; }
        s.WriteByte((byte)value);
    }
}
