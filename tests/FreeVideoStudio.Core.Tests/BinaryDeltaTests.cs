using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>BINDELTA_01 — a patch rebuilds exactly the new file, or fails; it never builds a wrong one.</summary>
public sealed class BinaryDeltaTests
{
    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new System.Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Simulates a recompiled program: inserted code shifts everything, and many 4-byte addresses change.</summary>
    private static byte[] Recompile(byte[] old, int seed)
    {
        var rng = new System.Random(seed);
        var list = new List<byte>(old);
        for (int i = 0; i < 20; i++)
        {
            int at = rng.Next(list.Count);
            list.InsertRange(at, Random(rng.Next(10, 3000), seed + i));
        }
        byte[] neu = list.ToArray();
        for (int i = 0; i + 4 < neu.Length; i += rng.Next(40, 400)) neu[i] += 7;
        return neu;
    }

    [Fact]
    public void PatchRebuildsTheExactNewFileAndIsSmall()
    {
        byte[] old = Random(2_000_000, 1);
        byte[] neu = Recompile(old, 2);
        using var patch = new MemoryStream();
        BinaryDelta.Create(old, neu, patch);
        patch.Position = 0;
        Assert.Equal(neu, BinaryDelta.Apply(old, patch));
        Assert.True(patch.Length < neu.Length / 5, $"patch {patch.Length} bytes for a {neu.Length}-byte file");
    }

    [Fact]
    public void UnrelatedFilesStillProduceACorrectPatch()
    {
        byte[] old = Random(300_000, 3), neu = Random(250_000, 4);
        using var patch = new MemoryStream();
        BinaryDelta.Create(old, neu, patch);
        patch.Position = 0;
        Assert.Equal(neu, BinaryDelta.Apply(old, patch));
    }

    [Fact]
    public void EmptyAndTinyFilesWork()
    {
        foreach ((byte[] old, byte[] neu) in new[] { (Array.Empty<byte>(), Random(100, 5)), (Random(100, 6), Array.Empty<byte>()), (Random(10, 7), Random(10, 7)) })
        {
            using var patch = new MemoryStream();
            BinaryDelta.Create(old, neu, patch);
            patch.Position = 0;
            Assert.Equal(neu, BinaryDelta.Apply(old, patch));
        }
    }

    [Fact]
    public void AWrongOldFileIsRefused()
    {
        byte[] old = Random(100_000, 8);
        byte[] neu = Recompile(old, 9);
        using var patch = new MemoryStream();
        BinaryDelta.Create(old, neu, patch);
        byte[] other = (byte[])old.Clone();
        other[500] ^= 1;
        patch.Position = 0;
        Assert.Throws<InvalidDataException>(() => BinaryDelta.Apply(other, patch));
    }

    [Fact]
    public void ADamagedPatchNeverProducesAFile()
    {
        byte[] old = Random(100_000, 10);
        byte[] neu = Recompile(old, 11);
        using var patch = new MemoryStream();
        BinaryDelta.Create(old, neu, patch);
        byte[] bytes = patch.ToArray();
        bytes[^20] ^= 0x55;
        Assert.ThrowsAny<Exception>(() => BinaryDelta.Apply(old, new MemoryStream(bytes)));
    }

    /// <summary>The pre-BINDELTA_02 whole-array decoder, verbatim — the compatibility reference.</summary>
    private static byte[] LegacyApply(byte[] oldFile, Stream patch)
    {
        BinaryDelta.Header header = BinaryDelta.ReadHeader(patch);
        if (oldFile.LongLength != header.OldLength ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(oldFile)), header.OldSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This patch was made for a different version of the file.");
        byte[] result = new byte[header.NewLength];
        using var body = new BrotliStream(patch, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BufferedStream(body, 1 << 20);
        long oldPos = 0;
        int newPos = 0;
        while (newPos < result.Length)
        {
            ulong diff = LegacyVarint(reader), extra = LegacyVarint(reader), zz = LegacyVarint(reader);
            long seek = (long)(zz >> 1) ^ -(long)(zz & 1);
            if (diff > (ulong)(result.Length - newPos) || extra > (ulong)(result.Length - newPos) - diff ||
                oldPos < 0 || oldPos + (long)diff > oldFile.LongLength)
                throw new InvalidDataException("The patch is damaged.");
            reader.ReadExactly(result, newPos, (int)diff);
            for (int i = 0; i < (int)diff; i++) result[newPos + i] += oldFile[oldPos + i];
            newPos += (int)diff;
            oldPos += (long)diff;
            reader.ReadExactly(result, newPos, (int)extra);
            newPos += (int)extra;
            oldPos += seek;
        }
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(result)), header.NewSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The patched file does not match the expected result.");
        return result;
    }

    private static ulong LegacyVarint(Stream s)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            int b = s.ReadByte();
            if (b < 0) throw new InvalidDataException("The patch ended early.");
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("The patch is damaged.");
    }

    private static void Varint(Stream s, ulong value)
    {
        while (value >= 0x80) { s.WriteByte((byte)(value | 0x80)); value >>= 7; }
        s.WriteByte((byte)value);
    }

    private static void Block(Stream s, ulong diff, ulong extra, long seek)
    {
        Varint(s, diff);
        Varint(s, extra);
        Varint(s, (ulong)((seek << 1) ^ (seek >> 63)));
    }

    private static void WriteHeader(Stream s, long oldLength, byte[] oldSha, long newLength, byte[] newSha)
    {
        s.Write("FVSDELTA1"u8);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(number, oldLength); s.Write(number);
        s.Write(oldSha);
        BinaryPrimitives.WriteInt64LittleEndian(number, newLength); s.Write(number);
        s.Write(newSha);
    }

    private static byte[] MakePatch(byte[] old, byte[] neu)
    {
        using var patch = new MemoryStream();
        BinaryDelta.Create(old, neu, patch);
        return patch.ToArray();
    }

    private static byte[] StreamApply(byte[] old, byte[] patch, CancellationToken cancel = default)
    {
        using var output = new MemoryStream();
        BinaryDelta.Apply(new MemoryStream(old, writable: false), new MemoryStream(patch), output, cancel);
        return output.ToArray();
    }

    /// <summary>Returns at most a few bytes per Read — a network-like, fragmented stream.</summary>
    private sealed class TrickleStream(Stream inner, int seed, Action? onRead = null) : Stream
    {
        private readonly System.Random _rng = new(seed);
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            onRead?.Invoke();
            return inner.Read(buffer, offset, Math.Min(count, _rng.Next(1, 8)));
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fvs-bindelta-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void StreamingApplyMatchesTheLegacyDecoderOnExistingFormatPatches()
    {
        foreach ((byte[] old, byte[]? neu) in new (byte[], byte[]?)[]
                 {
                     (Random(2_000_000, 20), null),
                     (Random(300_000, 21), Random(250_000, 22)),
                     (Array.Empty<byte>(), Random(100, 23)),
                     (Random(100, 24), Array.Empty<byte>()),
                 })
        {
            byte[] target = neu ?? Recompile(old, 25);
            byte[] patch = MakePatch(old, target);
            byte[] legacy = LegacyApply(old, new MemoryStream(patch));
            Assert.Equal(target, legacy);
            Assert.Equal(legacy, StreamApply(old, patch));
            Assert.Equal(legacy, BinaryDelta.Apply(old, new MemoryStream(patch)));
        }
    }

    /// <summary>A hand-written FVSDELTA1 patch (not made by Create): negative seek, zero-length blocks, wraparound.</summary>
    [Fact]
    public void AHandWrittenFvsDelta1PatchDecodes()
    {
        byte[] old = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 + 7)).ToArray();
        var expected = new MemoryStream();
        var body = new MemoryStream();

        Block(body, 200_000, 5, -150_000);
        for (int i = 0; i < 200_000; i++) { body.WriteByte(0xFF); expected.WriteByte((byte)(old[i] - 1)); }
        body.Write("HELLO"u8); expected.Write("HELLO"u8);
        Block(body, 0, 3, 1);
        body.Write("abc"u8); expected.Write("abc"u8);
        Block(body, 70_000, 0, 0);
        for (int i = 0; i < 70_000; i++) { body.WriteByte(200); expected.WriteByte((byte)(old[50_001 + i] + 200)); }

        byte[] neu = expected.ToArray();
        var patch = new MemoryStream();
        WriteHeader(patch, old.Length, SHA256.HashData(old), neu.Length, SHA256.HashData(neu));
        BrotliFile.Compress(body.ToArray(), patch);
        byte[] bytes = patch.ToArray();

        Assert.Equal(neu, LegacyApply(old, new MemoryStream(bytes)));
        Assert.Equal(neu, StreamApply(old, bytes));
    }

    [Fact]
    public void FragmentedOldAndPatchStreamsProduceTheSameFile()
    {
        byte[] old = Random(400_000, 30);
        byte[] neu = Recompile(old, 31);
        byte[] patch = MakePatch(old, neu);
        using var output = new MemoryStream();
        var oldStream = new TrickleStream(new MemoryStream(old, writable: false), 32);
        var patchStream = new TrickleStream(new MemoryStream(patch), 33);
        BinaryDelta.Apply(oldStream, patchStream, output);
        Assert.Equal(neu, output.ToArray());
        Assert.True(patchStream.Reads > patch.Length / 8, "the patch really arrived in small fragments");
    }

    [Theory]
    [InlineData(9 + 8)]
    [InlineData(9 + 8 + 32 + 8)]
    [InlineData(9)]
    public void ABadHeaderHashOrLengthIsRefusedAndLeavesNoFile(int offset)
    {
        byte[] old = Random(100_000, 40);
        byte[] patch = MakePatch(old, Recompile(old, 41));
        patch[offset] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => StreamApply(old, patch));

        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("old.exe"), old);
        File.WriteAllBytes(dir.File("p.patch"), patch);
        Assert.Throws<InvalidDataException>(() => BinaryDelta.ApplyFile(dir.File("old.exe"), dir.File("p.patch"), dir.File("new.partial")));
        Assert.False(File.Exists(dir.File("new.partial")));
    }

    [Fact]
    public void AWrongResultHashIsRefusedAfterStreaming()
    {
        byte[] old = Random(100_000, 42);
        byte[] neu = Recompile(old, 43);
        byte[] patch = MakePatch(old, neu);
        byte[] lying = (byte[])patch.Clone();
        byte[] wrongSha = SHA256.HashData(Random(10, 44));
        wrongSha.CopyTo(lying, 9 + 8 + 32 + 8);
        var ex = Assert.Throws<InvalidDataException>(() => StreamApply(old, lying));
        Assert.Contains("expected result", ex.Message);
    }

    [Fact]
    public void ATruncatedPatchNeverProducesAFile()
    {
        byte[] old = Random(200_000, 50);
        byte[] patch = MakePatch(old, Recompile(old, 51));
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("old.exe"), old);
        foreach (int keep in new[] { 0, 5, 9 + 8 + 32, 89, 90, patch.Length / 2, patch.Length - 1 })
        {
            File.WriteAllBytes(dir.File("p.patch"), patch[..keep]);
            Exception ex = Assert.ThrowsAny<Exception>(() => BinaryDelta.ApplyFile(dir.File("old.exe"), dir.File("p.patch"), dir.File("new.partial")));
            Assert.True(ex is InvalidDataException or IOException, $"keep={keep}: {ex.GetType().Name}");
            Assert.False(File.Exists(dir.File("new.partial")), $"keep={keep}");
        }
    }

    [Fact]
    public void CancellationStopsTheApplyAndRemovesThePartialFile()
    {
        byte[] old = Random(1_000_000, 60);
        byte[] patch = MakePatch(old, Recompile(old, 61));
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("old.exe"), old);
        File.WriteAllBytes(dir.File("p.patch"), patch);

        using var already = new CancellationTokenSource();
        already.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BinaryDelta.ApplyFile(dir.File("old.exe"), dir.File("p.patch"), dir.File("new.partial"), already.Token));
        Assert.False(File.Exists(dir.File("new.partial")));

        using var midway = new CancellationTokenSource();
        using var output = new MemoryStream();
        int reads = 0;
        var patchStream = new TrickleStream(new MemoryStream(patch), 62, () => { if (++reads == 200) midway.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() =>
            BinaryDelta.Apply(new MemoryStream(old, writable: false), patchStream, output, midway.Token));
    }

    /// <summary>
    /// BINDELTA_02 regression: a 192 MiB program is rebuilt from disk to disk while the managed heap
    /// allocates well under 1 % of it. The whole-array path allocated ≥ old + new (≥ 384 MiB here).
    /// </summary>
    [Fact]
    public void ALargeFileIsRebuiltWithBoundedMemory()
    {
        const long OldLength = 192L << 20;
        const int Diff = 1 << 20, Extra = 4096;
        static byte OldByte(long p) => (byte)((ulong)p * 2654435761UL >> 13);

        using var dir = new TempDir();
        string oldPath = dir.File("old.exe"), bodyPath = dir.File("body.bin"), patchPath = dir.File("p.patch"), newPath = dir.File("new.partial");
        byte[] chunk = new byte[1 << 16];

        using (var oldHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            using (var f = new FileStream(oldPath, FileMode.CreateNew))
                for (long p = 0; p < OldLength; p += chunk.Length)
                {
                    for (int i = 0; i < chunk.Length; i++) chunk[i] = OldByte(p + i);
                    f.Write(chunk); oldHash.AppendData(chunk);
                }
            byte[] oldSha = oldHash.GetHashAndReset();

            using var newHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long oldPos = 0, newLength = 0;
            using (var file = new FileStream(bodyPath, FileMode.CreateNew))
            using (var body = new BrotliStream(file, CompressionLevel.Fastest))
            {
                byte[] diff = new byte[Diff], extra = new byte[Extra], built = new byte[Diff];
                for (int k = 0; newLength < OldLength; k++)
                {
                    long seek = k % 3 == 0 ? -(3L * Diff / 2) : Diff / 4;
                    long next = Math.Clamp(oldPos + Diff + seek, 0, OldLength - Diff);
                    seek = next - (oldPos + Diff);
                    Block(body, Diff, Extra, seek);
                    for (int i = 0; i < Diff; i++)
                    {
                        diff[i] = (byte)(i % 4093 == 0 ? 7 : 0);
                        built[i] = (byte)(OldByte(oldPos + i) + diff[i]);
                    }
                    for (int i = 0; i < Extra; i++) extra[i] = (byte)(k + i);
                    body.Write(diff); body.Write(extra);
                    newHash.AppendData(built); newHash.AppendData(extra);
                    newLength += Diff + Extra;
                    oldPos = next;
                }
            }

            using (var patch = new FileStream(patchPath, FileMode.CreateNew))
            {
                WriteHeader(patch, OldLength, oldSha, newLength, newHash.GetHashAndReset());
                using var bodyIn = File.OpenRead(bodyPath);
                bodyIn.CopyTo(patch);
            }

            GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            BinaryDelta.Header header = BinaryDelta.ApplyFile(oldPath, patchPath, newPath);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(newLength, new FileInfo(newPath).Length);
            Assert.Equal(newLength, header.NewLength);
            Assert.True(allocated < 2L << 20, $"apply allocated {allocated:N0} managed bytes for a {newLength:N0}-byte file");
        }
    }
}
