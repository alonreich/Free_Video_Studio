using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Diagnostics;
using FreeVideoStudio.Core.Infrastructure;
namespace FreeVideoStudio.App.Infrastructure;
internal static class MpvLogCap
{
    private static readonly Channel<(string Path, string Message)> Messages = Channel.CreateBounded<(string, string)>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    static MpvLogCap()
    {
        var worker = Task.Run(DrainAsync);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Messages.Writer.TryComplete();
            worker.Wait(2000);
        };
    }
    internal static void Enqueue(string path, string message)
    {
        string safe = SecretRedactor.RedactSecrets(message);
        if (safe.Length > 8192) safe = safe[..8192] + "... [truncated]" + Environment.NewLine;
        Messages.Writer.TryWrite((path, safe));
    }
    private static async Task DrainAsync()
    {
        while (await Messages.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batches = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 128 && Messages.Reader.TryRead(out var entry); i++)
            {
                if (!batches.TryGetValue(entry.Path, out var batch)) batches[entry.Path] = batch = new StringBuilder();
                batch.Append(entry.Message);
            }
            foreach (var batch in batches)
            {
                try { BoundedLogFile.Append(batch.Key, batch.Value.ToString()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.SwallowedThrottled(ex); }
            }
        }
    }
}
