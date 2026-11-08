using System.Diagnostics;

using System.Globalization;


namespace FreeVideoStudio.AvRegression.Tests;


/// <summary>

/// Samples the playhead every few milliseconds on its own thread, so a stuck UI or a stuck player

/// cannot hide a stall from it. <see cref="Check"/> turns a stretch of samples into a verdict:

/// stall (frozen playhead), stutter (backward steps), wrong rate, A/V offset, hang.

/// </summary>

internal sealed class PlaybackProbe(PlayerRig rig)

{

    public readonly record struct Sample(double WallMs, double Pos, bool Paused, bool Seeking, bool Eof, double Duration);

    public readonly record struct Marker(double WallMs, string Label, bool Seek);


    private readonly List<Sample> _samples = new();

    private readonly List<Marker> _markers = new();

    private readonly List<(double WallMs, double AvSync)> _avsync = new();

    private readonly Stopwatch _clock = new();

    private CancellationTokenSource? _cts;

    private Task? _loop;


    public double NowMs => _clock.Elapsed.TotalMilliseconds;


    public void Start()

    {

        _clock.Start();

        _cts = new CancellationTokenSource();

        _loop = Task.Factory.StartNew(() => Loop(_cts.Token), TaskCreationOptions.LongRunning);

    }


    public Sample[] Samples(double fromMs, double toMs) { lock (_samples) return _samples.Where(x => x.WallMs >= fromMs && x.WallMs <= toMs).ToArray(); }


    public void Mark(string label, bool seek = false) { lock (_markers) _markers.Add(new(NowMs, label, seek)); }


    private void Loop(CancellationToken ct)

    {

        int n = 0;

        while (!ct.IsCancellationRequested)

        {

            var ipc = rig.Ipc;

            var s = new Sample(NowMs, ipc.CurrentTime, ipc.IsPaused, ipc.IsSeeking, ipc.IsEof, ipc.Duration);

            lock (_samples) _samples.Add(s);

            if (++n % 10 == 0 && !s.Paused)

            {

                string? av = ipc.GetPropertyString("avsync");

                if (double.TryParse(av, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))

                    lock (_avsync) _avsync.Add((NowMs, v));

            }

            Thread.Sleep(4);

        }

    }


    public async Task StopAsync()

    {

        _cts?.Cancel();

        if (_loop != null) await _loop;

    }


    /// <summary>

    /// Verifies the playhead between <paramref name="fromMs"/> and <paramref name="toMs"/> (probe

    /// clock) moved forward smoothly at <paramref name="speed"/> while playing (NaN = speed varies, rate not checked). Throws a one-line

    /// <see cref="AvDefect"/> naming the first problem found.

    /// </summary>

    public void Check(string phase, double fromMs, double toMs, double speed = 1.0, bool expectMoving = true, bool allowEnd = false)

    {

        try { CheckCore(phase, fromMs, toMs, speed, expectMoving, allowEnd); }

        catch (AvDefect ex)

        {


            string file = Path.Combine(Repo.WorkDir("probe"), "trace.txt");

            File.WriteAllLines(file, Samples(fromMs - 200, toMs).Select(x =>

                $"{x.WallMs:0} pos {x.Pos:0.000} {(x.Paused ? "paused " : "")}{(x.Seeking ? "seeking " : "")}{(x.Eof ? "eof" : "")}"));

            throw new AvDefect(ex.Message + $" — trace: {ExportRig.Rel(file)}");

        }

    }


    private void CheckCore(string phase, double fromMs, double toMs, double speed, bool expectMoving, bool allowEnd)

    {

        Sample[] s;

        lock (_samples) s = _samples.Where(x => x.WallMs >= fromMs && x.WallMs <= toMs).ToArray();

        Marker[] seeks;

        lock (_markers) seeks = _markers.Where(m => m.Seek || m.Label.StartsWith("load", StringComparison.Ordinal)).ToArray();

        (double WallMs, double AvSync)[] av;

        lock (_avsync) av = _avsync.ToArray();



        Analyze(phase, s, seeks, av, fromMs, toMs, speed, expectMoving, allowEnd, requireAvSync: false);

    }


    internal static void Analyze(string phase, Sample[] s, Marker[] seeks,

        (double WallMs, double AvSync)[] av, double fromMs, double toMs,

        double speed = 1, bool expectMoving = true, bool allowEnd = false, bool requireAvSync = true)

    {

        bool NearSeek(double wall) => seeks.Any(m => wall >= m.WallMs && wall - m.WallMs < Budget.StartGraceMs);

        bool AtEnd(Sample x) => x.Eof || (x.Duration > 0 && x.Pos >= x.Duration - 0.12);


        if (s.Length < 5) throw new AvDefect($"{phase}: probe saw no playhead samples (player dead?)");


        if (s.Any(x => !double.IsFinite(x.Pos) || !double.IsFinite(x.WallMs)))

            throw new AvDefect($"{phase}: invalid playhead telemetry");

        if (s[^1].WallMs - s[0].WallMs < Math.Min(100, (toMs - fromMs) / 2))

            throw new AvDefect($"{phase}: insufficient observation time");

        var moving = s.Where(x => !x.Paused && !x.Seeking).ToArray();

        if (expectMoving && (moving.Length < 5 || moving.Max(x => x.Pos) - moving.Min(x => x.Pos) <= 1e-6))

            throw new AvDefect($"{phase}: STALL — requested playback produced no moving samples");

        if (expectMoving && !allowEnd && s.Any(AtEnd))

            throw new AvDefect($"{phase}: playback reached an unexpected end");



        double stallStart = double.NaN, lastPos = double.NaN, worstStall = 0, worstAt = 0;

        foreach (var x in s)

        {

            bool isLive = !x.Paused && !x.Seeking && !AtEnd(x) && !NearSeek(x.WallMs) && !double.IsNaN(x.Pos);

            if (!isLive) { stallStart = double.NaN; lastPos = x.Pos; continue; }

            if (double.IsNaN(lastPos) || Math.Abs(x.Pos - lastPos) > 1e-6) { stallStart = x.WallMs; lastPos = x.Pos; continue; }

            if (double.IsNaN(stallStart)) stallStart = x.WallMs;

            double frozen = x.WallMs - stallStart;

            if (frozen > worstStall) { worstStall = frozen; worstAt = x.Pos; }

        }

        if (expectMoving && worstStall > Budget.StallMs)

            throw new AvDefect($"{phase}: STALL — playhead frozen {worstStall:0}ms at {worstAt:0.00}s while playing (budget {Budget.StallMs}ms)");



        for (int i = 1; i < s.Length; i++)

        {

            if (s[i].Paused || s[i].Seeking || NearSeek(s[i].WallMs) || NearSeek(s[i - 1].WallMs)) continue;

            double back = s[i - 1].Pos - s[i].Pos;

            if (back > Budget.MaxBackStepSec)

                throw new AvDefect($"{phase}: STUTTER — playhead jumped back {back * 1000:0}ms at {s[i].Pos:0.00}s during playback");

        }



        var live = s.Where(x => !x.Paused && !x.Seeking && !AtEnd(x) && !NearSeek(x.WallMs) && !double.IsNaN(x.Pos)).ToArray();

        if (expectMoving && !double.IsNaN(speed) && live.Length > 20)

        {

            double wall = (live[^1].WallMs - live[0].WallMs) / 1000.0, moved = live[^1].Pos - live[0].Pos;

            if (wall > 0.6)

            {

                double rate = moved / wall;

                if (Math.Abs(rate - speed) > speed * Budget.RateTolerance)

                    throw new AvDefect($"{phase}: WRONG RATE — playhead ran at {rate:0.00}x, expected {speed:0.00}x (±{Budget.RateTolerance:P0})");

            }

        }

        if (!expectMoving && live.Length > 20 && live[^1].Pos - live[0].Pos > 0.05)

            throw new AvDefect($"{phase}: playhead moved {live[^1].Pos - live[0].Pos:0.00}s while it should hold still");




        av = av.Where(a => a.WallMs >= fromMs + Budget.StartGraceMs && a.WallMs <= toMs && !NearSeek(a.WallMs)).ToArray();

        if (requireAvSync && (av.Length < 2 || av.Any(a => !double.IsFinite(a.AvSync))))

            throw new AvDefect($"{phase}: missing or invalid A/V telemetry; synchronization is unverified");

        double runStart = double.NaN;

        foreach (var a in av)

        {

            if (Math.Abs(a.AvSync) > Budget.HugeAvSyncSec)

                throw new AvDefect($"{phase}: A/V DESYNC — audio and picture {a.AvSync * 1000:+0;-0}ms apart (hard limit {Budget.HugeAvSyncSec * 1000:0}ms)");

            if (Math.Abs(a.AvSync) <= Budget.MaxAvSyncSec) { runStart = double.NaN; continue; }

            if (double.IsNaN(runStart)) runStart = a.WallMs;

            if (a.WallMs - runStart >= Budget.AvSyncRecoverMs)

                throw new AvDefect($"{phase}: A/V DESYNC — audio and picture stayed {a.AvSync * 1000:+0;-0}ms apart for {a.WallMs - runStart:0}ms (budget {Budget.MaxAvSyncSec * 1000:0}ms, must recover within {Budget.AvSyncRecoverMs}ms)");

        }

    }


    /// <summary>Runs the player for <paramref name="ms"/> and checks that stretch.</summary>

    public async Task PlayFor(string phase, int ms, double speed = 1.0, bool allowEnd = false)

    {

        double from = NowMs;

        await Task.Delay(ms);

        Check(phase, from, NowMs, speed, allowEnd: allowEnd);

    }


    /// <summary>Holds for <paramref name="ms"/> and checks the playhead did NOT move (paused / frozen).</summary>

    public async Task HoldFor(string phase, int ms)

    {

        double from = NowMs;

        await Task.Delay(ms);

        Sample[] s;

        lock (_samples) s = _samples.Where(x => x.WallMs >= from + 60 && x.WallMs <= NowMs).ToArray();

        AnalyzeHold(phase, s);

    }


    internal static void AnalyzeHold(string phase, Sample[] s)

    {

        if (s.Length < 3 || s.Any(x => !double.IsFinite(x.Pos)))

            throw new AvDefect($"{phase}: insufficient valid paused telemetry");

        if (s.Any(x => !x.Paused || x.Seeking))

            throw new AvDefect($"{phase}: player did not remain paused and settled");

        if (s.Max(x => x.Pos) - s.Min(x => x.Pos) > 0.03)

            throw new AvDefect($"{phase}: playhead moved while paused");

    }

}

