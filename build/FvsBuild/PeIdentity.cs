using System.Text;

namespace FvsBuild;

internal static class PeIdentity
{
    public static bool FixOriginalFilename(string exePath, BuildLog log)
    {
        try
        {
            byte[] image = File.ReadAllBytes(exePath);
            int replaced = Patch(image, Path.GetFileName(exePath), out string? problem);
            if (problem is not null)
            {
                log.Error("ERROR: [PE identity] " + problem);
                return false;
            }
            if (replaced > 0) File.WriteAllBytes(exePath, image);
            log.Info(replaced > 0
                ? $"[PE identity] OriginalFilename/InternalName set to {Path.GetFileName(exePath)} ({replaced} value(s) corrected)."
                : $"[PE identity] OriginalFilename/InternalName already say {Path.GetFileName(exePath)}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("ERROR: [PE identity] could not rewrite the version resource: " + ex.Message);
            return false;
        }
    }

    internal static int Patch(byte[] image, string fileName, out string? problem)
    {
        problem = null;
        if (!TryFindVersionBlock(image, out int start, out int length, out problem)) return 0;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        byte[] wrong = Encoding.Unicode.GetBytes(stem + ".dll\0");
        byte[] right = Encoding.Unicode.GetBytes(fileName + "\0");
        if (wrong.Length != right.Length)
        {
            problem = $"{fileName} has a different length than {stem}.dll; the value cannot be rewritten in place.";
            return 0;
        }

        int replaced = 0;
        Span<byte> block = image.AsSpan(start, length);
        int at;
        while ((at = block.IndexOf(wrong)) >= 0)
        {
            right.CopyTo(block[at..]);
            replaced++;
        }

        if (block.IndexOf(Encoding.Unicode.GetBytes("OriginalFilename\0")) < 0 || block.IndexOf(right) < 0)
            problem = $"the version resource does not name {fileName} after the rewrite.";
        return replaced;
    }

    private static bool TryFindVersionBlock(byte[] image, out int start, out int length, out string? problem)
    {
        start = length = 0;
        problem = null;
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            problem = "not a Windows executable (no MZ header).";
            return false;
        }
        int pe = BitConverter.ToInt32(image, 0x3C);
        if (pe < 0 || pe + 24 > image.Length || BitConverter.ToUInt32(image, pe) != 0x00004550)
        {
            problem = "not a Windows executable (no PE header).";
            return false;
        }
        int sections = BitConverter.ToUInt16(image, pe + 6);
        int optionalSize = BitConverter.ToUInt16(image, pe + 20);
        int table = pe + 24 + optionalSize;
        for (int i = 0; i < sections; i++)
        {
            int entry = table + i * 40;
            if (entry + 40 > image.Length) break;
            string name = Encoding.ASCII.GetString(image, entry, 8).TrimEnd('\0');
            if (name != ".rsrc") continue;
            int rawSize = BitConverter.ToInt32(image, entry + 16);
            int rawPointer = BitConverter.ToInt32(image, entry + 20);
            if (rawPointer <= 0 || rawSize <= 0 || (long)rawPointer + rawSize > image.Length) break;

            int key = image.AsSpan(rawPointer, rawSize).IndexOf(Encoding.Unicode.GetBytes("VS_VERSION_INFO\0"));
            if (key < 6) break;
            start = rawPointer + key - 6;
            length = BitConverter.ToUInt16(image, start);
            if (length < 6 || start + length > rawPointer + rawSize) break;
            return true;
        }
        problem = "no VS_VERSION_INFO version resource was found in the .rsrc section.";
        return false;
    }
}
