using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeVideoStudio.App.Infrastructure;

public static class SecretProtector
{
    public const string DpapiPrefix = "dpapi1:";
    public const string PlainPrefix = "plain1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FreeVideoStudio.GeminiApiKey.v1");

    public static bool IsProtectedForm(string? stored)
        => stored != null && (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) || stored.StartsWith(PlainPrefix, StringComparison.Ordinal));

    public static string Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                byte[]? sealedBytes = Crypt(Encoding.UTF8.GetBytes(secret), protect: true);
                if (sealedBytes != null) return DpapiPrefix + Convert.ToBase64String(sealedBytes);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                RuntimeLog.Fail("Settings", $"Could not seal the API key with Windows data protection ({ex.GetType().Name}). It was not saved.");
                return string.Empty;
            }
            RuntimeLog.Fail("Settings", "Could not seal the API key with Windows data protection. It was not saved.");
            return string.Empty;
        }
        return PlainPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return string.Empty;
                byte[] blob = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                byte[]? clear = Crypt(blob, protect: false);
                return clear == null ? string.Empty : Encoding.UTF8.GetString(clear);
            }
            if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[PlainPrefix.Length..]));
        }
        catch (Exception ex) when (ex is FormatException or DllNotFoundException or EntryPointNotFoundException or ExternalException or ArgumentException)
        {
            RuntimeLog.Info("Settings", $"The saved API key could not be opened on this account ({ex.GetType().Name}); it will be asked for again.");
            return string.Empty;
        }
        return stored;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[]? Crypt(byte[] input, bool protect)
    {
        GCHandle inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        GCHandle entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var inBlob = new DataBlob { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entBlob = new DataBlob { cbData = Entropy.Length, pbData = entHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref inBlob, "FreeVideoStudio", ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok || output.pbData == IntPtr.Zero) return null;
            byte[] result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            inHandle.Free();
            entHandle.Free();
        }
    }
}
