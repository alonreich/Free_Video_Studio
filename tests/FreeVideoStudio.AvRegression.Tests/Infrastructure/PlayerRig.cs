using System.Diagnostics;

using System.Globalization;

using FreeVideoStudio.Core.Media;


namespace FreeVideoStudio.AvRegression.Tests;


/// <summary>

/// The app's REAL player engine — libmpv with the options of <c>MpvVideoView.CreateNativePlayer</c>,

/// driven through the app's own <see cref="MpvIpcClient"/> — with a null VIDEO output so it runs

/// unattended, and the real sound device MUTED (volume 0), because the audio clock is what paces a

/// real user's playback (mpv's simulated null audio clock stalls ~250 ms on big speed jumps that a

/// real device does not). FVS_AV_AUDIO=null forces the simulated clock (machines with no sound device).

/// A <see cref="PlaybackProbe"/> samples the playhead the whole time.

/// </summary>

internal sealed class PlayerRig : IAsyncDisposable

{

    private nint _handle;

    public MpvIpcClient Ipc { get; }

    public PlaybackProbe Probe { get; }

    public string Name { get; }


    private PlayerRig(string name, nint handle)

    {

        Name = name;

        _handle = handle;

        Ipc = new MpvIpcClient(handle);

        Probe = new PlaybackProbe(this);

    }


    public static PlayerRig Create(string name = "player", bool audioOnly = false, IDictionary<string, string>? extra = null)

    {

        nint h = MpvWrapper.mpv_create();

        if (h == nint.Zero) throw new InvalidOperationException("libmpv-2.dll could not create a player.");

        void Opt(string k, string v) => MpvWrapper.mpv_set_option_string(h, k, v);

        Opt("vo", "null");

        if (audioOnly) Opt("vid", "no");

        Opt("ao", Environment.GetEnvironmentVariable("FVS_AV_AUDIO") == "null" ? "null" : "wasapi,null");

        Opt("hwdec", "no");

        Opt("keep-open", "yes");

        Opt("idle", "yes");

        Opt("ytdl", "no");

        Opt("terminal", "no");

        Opt("msg-level", "all=warn");

        Opt("force-window", "no");

        Opt("osd-bar", "no");

        Opt("volume", "0");

        if (extra != null) foreach (var kv in extra) Opt(kv.Key, kv.Value);

        int err = MpvWrapper.mpv_initialize(h);

        if (err < 0) { MpvWrapper.mpv_terminate_destroy(h); throw new InvalidOperationException($"mpv_initialize failed ({err})."); }

        var rig = new PlayerRig(name, h);

        rig.Probe.Start();

        return rig;

    }


    /// <summary>Awaits a player command; one that never answers is reported as a HANG.</summary>

    public async Task<MpvCommandResult> Cmd(Task<MpvCommandResult> command, string what)

    {

        var done = await Task.WhenAny(command, Task.Delay(Budget.CommandTimeoutMs));

        if (done != command) throw new AvDefect($"HANG: player did not answer '{what}' within {Budget.CommandTimeoutMs} ms");

        var result = await command;

        RequireSuccess(result, what);

        return result;

    }


    internal static void RequireSuccess(MpvCommandResult result, string what)

    {

        if (!result.Succeeded)

            throw new AvDefect($"player rejected '{what}': {result.Status} ({result.NativeError})");

    }


    /// <summary>Loads a file (paused by default) and waits until it reports its duration.</summary>

    public async Task LoadAsync(string path, double start = 0, bool paused = true)

    {

        Probe.Mark("load " + System.IO.Path.GetFileName(path));

        var sw = Stopwatch.StartNew();

        await Cmd(Ipc.LoadFileAsync(path, start), "loadfile");

        if (paused) await Cmd(Ipc.SetPropertyAsync("pause", "yes"), "pause");



        bool Opened()

        {

            if (Ipc.GetPropertyString("path") != path) return false;

            if (!double.TryParse(Ipc.GetPropertyString("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d <= 0) return false;

            return Math.Abs(Ipc.Duration - d) < 0.01 && !double.IsNaN(Ipc.CurrentTime);

        }

        while (!Opened())

        {

            if (sw.ElapsedMilliseconds > Budget.CommandTimeoutMs) throw new AvDefect($"HANG: '{System.IO.Path.GetFileName(path)}' did not open within {Budget.CommandTimeoutMs} ms");

            await Task.Delay(10);

        }

        await WaitSettledAsync(start, 0.25, "open at " + start.ToString("0.00", CultureInfo.InvariantCulture));

    }


    public Task PlayAsync() { Probe.Mark("play"); return Cmd(Ipc.SetPropertyAsync("pause", "no"), "play"); }

    public Task PauseAsync() { Probe.Mark("pause"); return Cmd(Ipc.SetPropertyAsync("pause", "yes"), "pause"); }

    public Task SpeedAsync(double s) { Probe.Mark("speed " + s.ToString("0.###", CultureInfo.InvariantCulture)); return Cmd(Ipc.SetPropertyAsync("speed", s.ToString("0.0###", CultureInfo.InvariantCulture)), "speed"); }


    /// <summary>The app's click-to-seek (<c>seek &lt;t&gt; absolute</c>). Returns the latency in ms; fails if it never lands.</summary>

    public async Task<double> SeekAsync(double t, string? label = null, bool wait = true)

    {

        Probe.Mark(label ?? "seek " + t.ToString("0.00", CultureInfo.InvariantCulture), seek: true);

        var sw = Stopwatch.StartNew();

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted() => completed.TrySetResult();

        if (wait) Ipc.SeekCompleted += OnCompleted;

        try

        {

            await Cmd(Ipc.SendCommandAsync("seek", t, "absolute"), "seek");

            if (!wait) return 0;

            try { await completed.Task.WaitAsync(TimeSpan.FromMilliseconds(Budget.CommandTimeoutMs)); }

            catch (TimeoutException) { throw new AvDefect("HANG: seek acknowledgement never produced a fresh seek-completed event"); }

            await WaitSettledAsync(t, 0.12, label ?? "seek");

        }

        finally { if (wait) Ipc.SeekCompleted -= OnCompleted; }

        return sw.Elapsed.TotalMilliseconds;

    }


    /// <summary>Waits until the playhead reports <paramref name="target"/> and the seek finished.</summary>

    public async Task WaitSettledAsync(double target, double tolerance, string what)

    {

        var sw = Stopwatch.StartNew();

        double clamped = Math.Min(target, Math.Max(0, Ipc.Duration - 0.05));

        double speed = double.TryParse(Ipc.GetPropertyString("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 1;

        while (!HasLanded(Ipc.CurrentTime, clamped, tolerance, Ipc.IsSeeking, Ipc.IsPaused, sw.Elapsed.TotalSeconds, speed))

        {

            if (sw.ElapsedMilliseconds > Budget.CommandTimeoutMs)

                throw new AvDefect($"HANG: '{what}' never landed (wanted {clamped:0.000}s, playhead {Ipc.CurrentTime:0.000}s, seeking={Ipc.IsSeeking})");

            await Task.Delay(5);

        }

    }




    internal static bool HasLanded(double position, double target, double tolerance, bool seeking,

        bool paused, double elapsedSec, double speed) =>

        double.IsFinite(position) && !seeking && position >= target - tolerance &&

        position <= target + tolerance + (paused ? 0 : Math.Max(0, elapsedSec) * Math.Max(0, speed));


    /// <summary>Lets wall time pass while the probe watches.</summary>

    public static Task RunAsync(int ms) => Task.Delay(ms);


    public async ValueTask DisposeAsync()

    {

        await Probe.StopAsync();

        Ipc.Dispose();

        if (_handle != nint.Zero) { MpvWrapper.mpv_terminate_destroy(_handle); _handle = nint.Zero; }

    }

}


/// <summary>A user-visible audio/video defect found by a check. The message is the one-line verdict.</summary>

internal sealed class AvDefect(string message) : Exception(message);

