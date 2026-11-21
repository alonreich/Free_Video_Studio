using System;
using System.IO;
using System.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

internal static class MpvLogCap
{
    internal const long CapBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    internal static bool OverCap(long lengthBytes) => lengthBytes > CapBytes;

    public static void Watch(MpvIpcClient client, string path)
    {
        Timer? timer = null;
        int done = 0;
        timer = new Timer(_ =>
        {
            if (Volatile.Read(ref done) != 0) return;
            long length;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                length = info.Length;
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
                return;
            }
            if (!OverCap(length) || Interlocked.Exchange(ref done, 1) != 0) return;
            _ = client.SetPropertyAsync("log-file", "");
            RuntimeLog.Warn("MPV", $"Player debug log reached {CapBytes / (1024 * 1024)} MB and was stopped so a repeating error cannot fill the disk: {Path.GetFileName(path)}.");
            timer?.Dispose();
        }, null, Interval, Interval);
    }
}
