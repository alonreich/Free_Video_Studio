using System.Collections.Concurrent;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public static class VideoLoudnessCache
{
    private static readonly ConcurrentDictionary<string, double> Readings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<double?>> Pending = new(StringComparer.OrdinalIgnoreCase);

    public static double? Latest { get; private set; }

    public static void Set(string? path, double? lufs)
    {
        if (string.IsNullOrWhiteSpace(path) || lufs is not double v || !double.IsFinite(v)) return;
        Readings[path] = v;
        Latest = v;
    }

    public static double? TryGet(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Readings.TryGetValue(path, out double v) ? v : null;

    public static Task<double?> EnsureAsync(string ffmpegPath, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<double?>(null);
        if (TryGet(path) is double known) return Task.FromResult<double?>(known);
        return Pending.GetOrAdd(path, p => Task.Run(async () =>
        {
            try
            {
                var reading = await AudioLoudnessProbe.MeasureAsync(ffmpegPath, p, CancellationToken.None).ConfigureAwait(false);
                Set(p, reading?.IntegratedLufs);
                return reading?.IntegratedLufs;
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
                return null;
            }
            finally
            {
                Pending.TryRemove(p, out _);
            }
        }));
    }
}
