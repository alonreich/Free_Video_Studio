using System.Text;
using FvsBuild;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// PEIDENTITY_01 — the shipped program's version resource must name the program's own file, and the
/// rewrite must touch nothing but the VS_VERSION_INFO block.
/// </summary>
public sealed class PeIdentityTests
{
    private const int RsrcRaw = 0x200;

    private static byte[] FakeExecutable(out int blockStart, out int blockLength, string value = "FreeVideoStudio.dll")
    {
        var image = new byte[0x800];
        image[0] = (byte)'M'; image[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(image, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(image, 0x40);
        BitConverter.GetBytes((ushort)1).CopyTo(image, 0x40 + 6);
        BitConverter.GetBytes((ushort)0).CopyTo(image, 0x40 + 20);
        int section = 0x40 + 24;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(image, section);
        BitConverter.GetBytes(0x400).CopyTo(image, section + 16);
        BitConverter.GetBytes(RsrcRaw).CopyTo(image, section + 20);

        var block = new List<byte>();
        block.AddRange(new byte[6]);
        foreach (string text in new[] { "VS_VERSION_INFO", "CompanyName", "Alon Reich", "InternalName", value, "OriginalFilename", value })
            block.AddRange(Encoding.Unicode.GetBytes(text + "\0"));
        byte[] blockBytes = block.ToArray();
        BitConverter.GetBytes((ushort)blockBytes.Length).CopyTo(blockBytes, 0);
        blockStart = RsrcRaw + 0x20;
        blockLength = blockBytes.Length;
        blockBytes.CopyTo(image, blockStart);

        Encoding.Unicode.GetBytes("FreeVideoStudio.dll\0").CopyTo(image, 0x100);
        return image;
    }

    [Fact]
    public void OriginalFilenameAndInternalNameNameTheExecutable()
    {
        byte[] image = FakeExecutable(out int start, out int length);
        byte[] before = (byte[])image.Clone();

        Assert.Equal(2, PeIdentity.Patch(image, "FreeVideoStudio.exe", out string? problem));
        Assert.Null(problem);

        string block = Encoding.Unicode.GetString(image, start, length);
        Assert.Contains("OriginalFilename\0FreeVideoStudio.exe\0", block);
        Assert.Contains("InternalName\0FreeVideoStudio.exe\0", block);
        Assert.DoesNotContain("FreeVideoStudio.dll", block);

        for (int i = 0; i < image.Length; i++)
            if (i < start || i >= start + length)
                Assert.True(before[i] == image[i], $"byte {i} outside the version block changed");
    }

    [Fact]
    public void ACorrectFileIsLeftAloneAndAFileWithoutVersionInfoIsAnError()
    {
        byte[] image = FakeExecutable(out _, out _, "FreeVideoStudio.exe");
        Assert.Equal(0, PeIdentity.Patch(image, "FreeVideoStudio.exe", out string? problem));
        Assert.Null(problem);

        Assert.Equal(0, PeIdentity.Patch(new byte[0x400], "FreeVideoStudio.exe", out problem));
        Assert.NotNull(problem);
    }
}
