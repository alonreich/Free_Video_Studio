
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FreeVideoStudio.App.Services;

/// <summary>Progress of one download, for the update toast.</summary>
internal readonly record struct DownloadProgress(long Done, long Total, double BytesPerSecond, string? Problem = null);

/// <summary>
/// DLRESUME_01 — DOWNLOADS THAT SURVIVE A BAD CONNECTION.
///
/// <para>A dropped connection at 90 % used to fail the update and start again from zero on the next
/// launch. Now the partial file (<c>.part</c>) is kept in a folder named after the release, a broken
/// transfer is retried by itself (2 s, 5 s, 15 s), and every retry — including one after an app
/// restart — continues from the last byte with an HTTP Range request. A server that ignores Range
/// (answers 200 instead of 206) restarts the file cleanly; nothing is ever glued together wrongly.</para>
///
/// <para>⚠️ Resuming is only about bytes on the wire. The finished file is ALWAYS hashed against the
/// expected SHA-256 before it is moved into place, and a mismatch deletes it.</para>
/// </summary>
internal static class UpdateDownloader
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Downloads <paramref name="source"/> (an https URL, or a local file in the dev environment) to
    /// <paramref name="target"/>, resuming any earlier partial copy. Throws when it cannot finish.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, string source, string? localSource, string target,
        long expectedLength, string? expectedSha256, Action<DownloadProgress>? progress, CancellationToken cancel)
    {
        if (File.Exists(target) && (expectedSha256 is null || HashMatches(target, expectedSha256)))
        {
            long length = new FileInfo(target).Length;
            progress?.Invoke(new DownloadProgress(length, length, 0));
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string part = target + ".part";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await TransferAsync(http, source, localSource, part, expectedLength, progress, cancel).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !cancel.IsCancellationRequested && attempt < RetryDelays.Length)
            {
                TimeSpan wait = RetryDelays[attempt];
                RuntimeLog.Info("UPDATE", $"Download interrupted ({ex.Message}); retry {attempt + 1} in {wait.TotalSeconds:0}s, resuming.");
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                progress?.Invoke(new DownloadProgress(have, expectedLength, 0,
                    $"Connection problem — trying again in {wait.TotalSeconds:0} seconds (keeping what was already downloaded)…"));
                await Task.Delay(wait, cancel).ConfigureAwait(false);
            }
        }

        if (expectedSha256 is not null && !HashMatches(part, expectedSha256))
        {
            TryDelete(part);
            throw new IOException("A downloaded file did not match its published fingerprint and was deleted. Nothing was installed.");
        }
        File.Move(part, target, overwrite: true);
    }

    private static async Task TransferAsync(HttpClient http, string source, string? localSource, string part,
        long expectedLength, Action<DownloadProgress>? progress, CancellationToken cancel)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expectedLength > 0 && have > expectedLength) { TryDelete(part); have = 0; }
        if (expectedLength > 0 && have == expectedLength) return;

        Stream input;
        HttpResponseMessage? response = null;
        long total = expectedLength;
        if (localSource is not null)
        {
            var file = new FileStream(localSource, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            total = file.Length;
            if (have > total) { have = 0; TryDelete(part); }
            file.Seek(have, SeekOrigin.Begin);
            input = file;
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                TryDelete(part);
                throw new IOException("The partial download did not match the server's file; starting it again.");
            }
            response.EnsureSuccessStatusCode();
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                have = 0;
            }
            total = (response.Content.Headers.ContentLength ?? Math.Max(0, expectedLength - have)) + have;
            input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        }

        try
        {
            await using var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            byte[] buffer = new byte[1 << 20];
            long done = have;
            var meter = new SpeedMeter();
            DateTime lastReport = DateTime.MinValue;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                stall.CancelAfter(StallLimit);
                int read;
                try
                {
                    read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new IOException("No data arrived for 45 seconds.");
                }
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                meter.Add(done);
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 250)
                {
                    lastReport = DateTime.UtcNow;
                    progress?.Invoke(new DownloadProgress(done, total, meter.BytesPerSecond));
                }
            }
            await output.FlushAsync(cancel).ConfigureAwait(false);
            progress?.Invoke(new DownloadProgress(done, total, meter.BytesPerSecond));
            if (expectedLength > 0 && done != expectedLength)
                throw new IOException($"The download ended early ({done} of {expectedLength} bytes).");
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
            response?.Dispose();
        }
    }

    public static bool HashMatches(string path, string expectedSha256)
    {
        using FileStream stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>UPDATEUX_03 — download speed averaged over the last few seconds, for "time left".</summary>
    internal sealed class SpeedMeter
    {
        private readonly Queue<(DateTime At, long Bytes)> _samples = new();

        public double BytesPerSecond { get; private set; }

        public void Add(long totalBytes)
        {
            DateTime now = DateTime.UtcNow;
            _samples.Enqueue((now, totalBytes));
            while (_samples.Count > 2 && now - _samples.Peek().At > TimeSpan.FromSeconds(5)) _samples.Dequeue();
            (DateTime firstAt, long firstBytes) = _samples.Peek();
            double seconds = (now - firstAt).TotalSeconds;
            if (seconds >= 0.5) BytesPerSecond = (totalBytes - firstBytes) / seconds;
        }
    }
}
