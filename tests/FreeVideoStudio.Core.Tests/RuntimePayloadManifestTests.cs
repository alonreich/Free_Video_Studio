using System;
using System.IO;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class RuntimePayloadManifestTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fvs_payload_" + Guid.NewGuid().ToString("N"));

    public RuntimePayloadManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) {  }
    }

    private string Folder(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Write(string folder, string name, int bytes)
        => File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);

    [Fact]
    public void IdenticalRuntimesProduceTheSameFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Write(a, "avcodec-62.dll", 1024); Write(a, "ffmpeg.exe", 512);
        Write(b, "avcodec-62.dll", 1024); Write(b, "ffmpeg.exe", 512);

        Assert.Equal(
            RuntimePayloadManifest.FromFolder(a).Fingerprint,
            RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    [Fact]
    public void AChangedBinaryChangesTheFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Write(a, "avcodec-62.dll", 1024);
        Write(b, "avcodec-62.dll", 1025);

        Assert.NotEqual(
            RuntimePayloadManifest.FromFolder(a).Fingerprint,
            RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    [Fact]
    public void AnAddedOrRemovedBinaryChangesTheFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Write(a, "avcodec-62.dll", 1024);
        Write(b, "avcodec-62.dll", 1024); Write(b, "avfilter-11.dll", 64);

        Assert.NotEqual(
            RuntimePayloadManifest.FromFolder(a).Fingerprint,
            RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    [Fact]
    public void ReusedStarterAssetsAffectTheFingerprint()
    {
        string a = Folder("a"), b = Folder("b");
        Write(a, "avcodec-62.dll", 1024);
        Write(b, "avcodec-62.dll", 1024);
        File.WriteAllText(Path.Combine(b, "readme.txt"), "not part of the runtime");
        File.WriteAllText(Path.Combine(b, "starter.mp3"), "nor this");

        Assert.NotEqual(
            RuntimePayloadManifest.FromFolder(a).Fingerprint,
            RuntimePayloadManifest.FromFolder(b).Fingerprint);
    }

    [Fact]
    public void EnumerationOrderDoesNotAffectTheFingerprint()
    {
        string a = Folder("a");
        Write(a, "z.dll", 10); Write(a, "a.dll", 20); Write(a, "m.dll", 30);

        string first = RuntimePayloadManifest.FromFolder(a).Fingerprint;
        string second = RuntimePayloadManifest.FromFolder(a).Fingerprint;

        Assert.Equal(first, second);
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        string a = Folder("a");
        Write(a, "avcodec-62.dll", 4096);

        RuntimePayloadManifest written = RuntimePayloadManifest.FromFolder(a);
        written.Write(a);

        RuntimePayloadManifest? read = RuntimePayloadManifest.Read(a);

        Assert.NotNull(read);
        Assert.Equal(written.Fingerprint, read!.Fingerprint);
        Assert.Equal(written.FileCount, read.FileCount);
        Assert.Equal(written.TotalBytes, read.TotalBytes);
    }

    [Fact]
    public void AMissingManifestReadsAsNull()
        => Assert.Null(RuntimePayloadManifest.Read(Folder("empty")));

    [Fact]
    public void FormatBytesReadsInTheUnitsAUserThinksIn()
    {
        Assert.Equal("322 MB", RuntimePayloadManifest.FormatBytes(322L * 1024 * 1024));
        Assert.Equal("8 MB", RuntimePayloadManifest.FormatBytes(8L * 1024 * 1024));
        Assert.Equal("512 KB", RuntimePayloadManifest.FormatBytes(512L * 1024));
    }
}
