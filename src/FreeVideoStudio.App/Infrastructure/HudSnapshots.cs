using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

internal sealed record HudCrop(string Key, byte[] Bgra, int Width, int Height);

internal sealed record HudSnapshot(long Id, double TimeSec, IReadOnlyList<HudCrop> Crops);

internal sealed record HudSourceLayer(string Key, int SrcX, int SrcY, int SrcW, int SrcH, int OutW, int OutH);

internal sealed class HudSnapshotSet
{
    public static readonly HudSnapshotSet Empty = new(Array.Empty<HudSnapshot>());

    private readonly HudSnapshot[] _sorted;

    public HudSnapshotSet(IEnumerable<HudSnapshot> snapshots)
        => _sorted = snapshots.OrderBy(s => s.TimeSec).ToArray();

    public int Count => _sorted.Length;

    public IReadOnlyList<HudSnapshot> All => _sorted;

    public HudSnapshotSet With(IEnumerable<HudSnapshot> more) => new(_sorted.Concat(more));

    public HudSnapshot? Nearest(double timeSec)
    {
        if (_sorted.Length == 0) return null;
        int lo = 0, hi = _sorted.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_sorted[mid].TimeSec < timeSec) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(_sorted[lo - 1].TimeSec - timeSec) <= Math.Abs(_sorted[lo].TimeSec - timeSec)) lo--;
        return _sorted[lo];
    }
}

internal static class HudSnapshotPlan
{
    public const string Tag = "HUDSNAP_01";

    public const int Rounds = 3;

    public const int FirstRoundCount = 4;

    public static IReadOnlyList<double> RoundTimes(double durationSec, int round)
    {
        if (!(durationSec > 0) || round < 1 || round > Rounds) return Array.Empty<double>();
        var known = Enumerable.Range(0, FirstRoundCount).Select(k => (k + 0.5) / FirstRoundCount * durationSec).ToList();
        if (round == 1) return known;
        List<double> fresh = new();
        for (int r = 2; r <= round; r++)
        {
            known.Sort();
            fresh = new List<double>();
            for (int i = 0; i + 1 < known.Count; i++) fresh.Add((known[i] + known[i + 1]) / 2.0);
            known.AddRange(fresh);
        }
        return fresh;
    }

    public static IReadOnlyList<HudSourceLayer> SourceLayers(IReadOnlyList<MobileFilterBuilder.HudCanvasLayer> layers)
    {
        var list = new List<HudSourceLayer>();
        foreach (var l in layers)
        {
            if (l.SrcW <= 1 || l.SrcH <= 1) continue;
            var r = FinalLayoutPreview.ToPhone(l);
            int ow = Math.Max(2, (int)Math.Round(r.Width)), oh = Math.Max(2, (int)Math.Round(r.Height));
            list.Add(new HudSourceLayer(l.Key, l.SrcX, l.SrcY, l.SrcW, l.SrcH, ow, oh));
        }
        return list;
    }

    public static HudCrop? Crop(DecodedVideoFrame frame, HudSourceLayer layer, int sourceW, int sourceH)
    {
        if (sourceW <= 0 || sourceH <= 0) return null;
        double fx = frame.Width / (double)sourceW, fy = frame.Height / (double)sourceH;
        int x0 = (int)Math.Floor(layer.SrcX * fx), y0 = (int)Math.Floor(layer.SrcY * fy);
        int cw = (int)Math.Round(layer.SrcW * fx), ch = (int)Math.Round(layer.SrcH * fy);
        if (cw < 1 || ch < 1 || x0 < 0 || y0 < 0 || x0 >= frame.Width || y0 >= frame.Height) return null;
        cw = Math.Min(cw, frame.Width - x0);
        ch = Math.Min(ch, frame.Height - y0);
        int ow = Math.Min(layer.OutW, cw), oh = Math.Min(layer.OutH, ch);
        if (ow < 1 || oh < 1) return null;
        var outBuf = new byte[ow * oh * 4];
        byte[] src = frame.Pixels;
        for (int y = 0; y < oh; y++)
        {
            int sy = y0 + (int)((long)y * ch / oh);
            int rowIn = sy * frame.Stride, rowOut = y * ow * 4;
            for (int x = 0; x < ow; x++)
            {
                int si = rowIn + (x0 + (int)((long)x * cw / ow)) * 4;
                int di = rowOut + x * 4;
                outBuf[di] = src[si];
                outBuf[di + 1] = src[si + 1];
                outBuf[di + 2] = src[si + 2];
                outBuf[di + 3] = 255;
            }
        }
        return new HudCrop(layer.Key, outBuf, ow, oh);
    }

    public static bool LooksBlank(IReadOnlyList<HudCrop> crops)
    {
        long n = 0;
        double sum = 0, sumSq = 0;
        foreach (var c in crops)
        {
            for (int i = 0; i + 2 < c.Bgra.Length; i += 16)
            {
                double l = 0.114 * c.Bgra[i] + 0.587 * c.Bgra[i + 1] + 0.299 * c.Bgra[i + 2];
                sum += l;
                sumSq += l * l;
                n++;
            }
        }
        if (n == 0) return true;
        double mean = sum / n;
        return sumSq / n - mean * mean < 9.0;
    }
}

internal sealed class HudSnapshotWorker
{
    public const double BlankRetryOffsetSec = 3.0;

    public static readonly TimeSpan PauseBetweenGrabs = TimeSpan.FromMilliseconds(120);

    private static long s_nextId;

    private readonly Func<double, CancellationToken, Task<DecodedVideoFrame?>> _grab;
    private readonly IReadOnlyList<HudSourceLayer> _layers;
    private readonly int _sourceW;
    private readonly int _sourceH;
    private readonly double _durationSec;
    private readonly TimeSpan _pause;

    public HudSnapshotWorker(Func<double, CancellationToken, Task<DecodedVideoFrame?>> grab, IReadOnlyList<HudSourceLayer> layers,
        int sourceW, int sourceH, double durationSec, TimeSpan? pauseBetweenGrabs = null)
    {
        _grab = grab;
        _layers = layers;
        _sourceW = sourceW;
        _sourceH = sourceH;
        _durationSec = durationSec;
        _pause = pauseBetweenGrabs ?? PauseBetweenGrabs;
    }

    public async Task RunAsync(Action<HudSnapshotSet, int> publish, CancellationToken ct)
    {
        var set = HudSnapshotSet.Empty;
        for (int round = 1; round <= HudSnapshotPlan.Rounds; round++)
        {
            var fresh = new List<HudSnapshot>();
            foreach (double t in HudSnapshotPlan.RoundTimes(_durationSec, round))
            {
                ct.ThrowIfCancellationRequested();
                if (await SnapAsync(t, ct).ConfigureAwait(false) is { } snap) fresh.Add(snap);
                await Task.Delay(_pause, ct).ConfigureAwait(false);
            }
            if (fresh.Count == 0) continue;
            set = set.With(fresh);
            publish(set, round);
        }
    }

    public async Task<HudSnapshot?> SnapAsync(double timeSec, CancellationToken ct, bool retryBlank = true)
    {
        var crops = await CropsAtAsync(timeSec, ct).ConfigureAwait(false);
        if (crops != null && retryBlank && HudSnapshotPlan.LooksBlank(crops))
        {
            double alt = timeSec + BlankRetryOffsetSec <= _durationSec ? timeSec + BlankRetryOffsetSec : timeSec - BlankRetryOffsetSec;
            if (alt >= 0)
            {
                var retry = await CropsAtAsync(alt, ct).ConfigureAwait(false);
                if (retry != null && !HudSnapshotPlan.LooksBlank(retry)) crops = retry;
            }
        }
        if (crops == null || crops.Count == 0) return null;
        return new HudSnapshot(Interlocked.Increment(ref s_nextId), timeSec, crops);
    }

    private async Task<IReadOnlyList<HudCrop>?> CropsAtAsync(double timeSec, CancellationToken ct)
    {
        DecodedVideoFrame? frame;
        try
        {
            frame = await _grab(Math.Clamp(timeSec, 0, Math.Max(0, _durationSec - 0.05)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
        if (frame == null) return null;
        var crops = new List<HudCrop>(_layers.Count);
        foreach (var layer in _layers)
            if (HudSnapshotPlan.Crop(frame, layer, _sourceW, _sourceH) is { } c) crops.Add(c);
        return crops;
    }
}
