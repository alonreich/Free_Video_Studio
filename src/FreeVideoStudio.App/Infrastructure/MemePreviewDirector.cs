using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

using CoreMeme = FreeVideoStudio.Core.Media.MemePlacement;

namespace FreeVideoStudio.App.Infrastructure;

public sealed class MemePreviewDirector
{
    public enum Phase
    {
        Idle,
        LoadingMeme,
        PlayingMeme,
        Returning
    }

    private const double SeekJumpSec = 1.5;

    private const double CrossEps = 0.001;

    private const double ReArmGuardSec = 0.30;

    private const double FreshStartWindowSec = 0.15;

    private const double EndWindowSec = 0.15;

    private const int LoadTimeoutMs = 4000;
    private const int ReturnTimeoutMs = 5000;
    private const int PollMs = 25;

    private readonly Func<MpvIpcClient?> _client;
    private readonly Func<string?> _sourcePath;
    private readonly Func<double> _trimStartSec;
    private readonly Action<bool, string> _setBusy;
    private readonly string _logTag;

    private readonly List<CoreMeme> _memes = new();
    private readonly Stopwatch _memeClock = new();

    private double _lastSourceSec = double.NaN;
    private bool _lastWasPaused = true;
    private string? _savedVolume;
    private string? _savedVolumeMax;
    private string? _savedMute;
    private readonly Func<double>? _trimEndSec;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double?> _lufs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _measuring = new(StringComparer.OrdinalIgnoreCase);
    private string? _suppressId;
    private double _suppressUntilSourceSec;
    private bool _resumePaused;
    private string? _savedCrop;
    private string? _savedVf;
    private string? _savedImageDuration;
    private string? _savedSpeed;
    private int _inFlight;

    public event Action? MemeStarted;

    public event Action? MemeEnded;

    public Phase CurrentPhase { get; private set; } = Phase.Idle;

    public bool IsActive => CurrentPhase != Phase.Idle;

    public double AnchorAbsSourceSec { get; private set; }

    public double MemeElapsedSec { get; private set; }

    public double MemeDurationSec { get; private set; }

    public string? ActiveMemeId { get; private set; }
    public string? ActiveMemeFile { get; private set; }

    public bool Suspended { get; set; }

    public MemePreviewDirector(
        Func<MpvIpcClient?> client,
        Func<string?> sourcePath,
        Func<double> trimStartSec,
        Action<bool, string> setBusy,
        string logTag,
        Func<double>? trimEndSec = null)
    {
        _trimEndSec = trimEndSec;
        _client = client;
        _sourcePath = sourcePath;
        _trimStartSec = trimStartSec;
        _setBusy = setBusy;
        _logTag = logTag;
    }

    public bool SetMemes(IReadOnlyList<CoreMeme>? memes)
    {
        string before = Signature();
        _memes.Clear();
        if (memes != null)
        {
            foreach (var m in memes)
                if (m.DurationSec > 0.001 && !m.IsCornerOverlay) _memes.Add(m);
        }
        _memes.Sort((a, b) => a.AtSourceSecRelative.CompareTo(b.AtSourceSecRelative));

        if (_memes.Count > 0)
        {
            foreach (var m in _memes) if (m.PlaySound) MeasureInBackground(m.FilePath, isMeme: true);
            if (_sourcePath() is { Length: > 0 } src && !src.StartsWith("edl://", StringComparison.OrdinalIgnoreCase))
                MeasureInBackground(src, isMeme: false);
        }

        bool changed = Signature() != before;
        if (changed)
        {
            _suppressId = null;
            _suppressUntilSourceSec = 0;
        }
        return changed;
    }

    private string Signature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in _memes)
            sb.Append(m.Id).Append(':')
              .Append(m.AtSourceSecRelative.ToString("0.####", CultureInfo.InvariantCulture)).Append(':')
              .Append(m.DurationSec.ToString("0.####", CultureInfo.InvariantCulture)).Append(':')
              .Append(m.ClipInSec.ToString("0.####", CultureInfo.InvariantCulture)).Append(':')
              .Append(m.PlaySound ? '1' : '0').Append(';');
        return sb.ToString();
    }

    public void NotifySeek()
    {
        _lastSourceSec = double.NaN;
    }

    public void Tick()
    {
        var ipc = _client();
        if (ipc == null) return;

        try
        {
            switch (CurrentPhase)
            {
                case Phase.Idle: TickIdle(ipc); break;
                case Phase.PlayingMeme: TickPlaying(ipc); break;
                default: break;
            }
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    private void TickIdle(MpvIpcClient ipc)
    {
        double now = ipc.CurrentTime;
        double prev = _lastSourceSec;
        bool wasPaused = _lastWasPaused;
        _lastSourceSec = now;
        _lastWasPaused = ipc.IsPaused;

        if (_memes.Count == 0 || Suspended) return;
        if (double.IsNaN(prev) && double.IsNaN(now)) return;
        if (ipc.IsPaused) return;

        double delta = double.IsNaN(prev) ? double.NaN : now - prev;
        bool fresh = double.IsNaN(prev) || wasPaused || !(delta > 0) || delta > SeekJumpSec;
        double hi = now + CrossEps;
        double lo = fresh ? now - FreshStartWindowSec : prev + CrossEps;

        double trimStart = _trimStartSec();
        double end = _trimEndSec?.Invoke() ?? double.NaN;
        foreach (var m in _memes)
        {
            double anchor = trimStart + m.AtSourceSecRelative;
            bool atEnd = double.IsFinite(end) && anchor >= end - EndWindowSec && now >= anchor - EndWindowSec;
            if (!atEnd && (anchor <= lo || anchor > hi)) continue;
            if (_suppressId == m.Id && now < _suppressUntilSourceSec) continue;
            if (atEnd && _suppressId == m.Id) continue;

            CurrentPhase = Phase.LoadingMeme;
            _ = RunMemeAsync(ipc, m, anchor);
            return;
        }
    }

    private void TickPlaying(MpvIpcClient ipc)
    {
        if (ipc.IsPaused) { if (_memeClock.IsRunning) _memeClock.Stop(); }
        else if (!_memeClock.IsRunning) _memeClock.Start();

        double elapsed = _memeClock.Elapsed.TotalSeconds;
        MemeElapsedSec = Math.Min(elapsed, MemeDurationSec);

        bool done = elapsed >= MemeDurationSec - 0.03
                    || (ipc.IsEof && elapsed > 0.15)
                    || elapsed > MemeDurationSec + 5.0;

        if (!done) return;

        CurrentPhase = Phase.Returning;
        _ = ReturnToSourceAsync(ipc);
    }

    private async Task RunMemeAsync(MpvIpcClient ipc, CoreMeme meme, double anchorAbs)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        try
        {
            ActiveMemeId = meme.Id;
            ActiveMemeFile = meme.FilePath;
            MemeDurationSec = Math.Max(0.05, meme.DurationSec);
            MemeElapsedSec = 0;
            AnchorAbsSourceSec = anchorAbs;
            _resumePaused = ipc.IsPaused;

            _setBusy(true, "Loading the meme…");
            MemeStarted?.Invoke();

            await SafeSet(ipc, "pause", "yes");
            try { await ipc.SetPropertyDoubleAsync("time-pos", anchorAbs); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            _savedCrop = await SafeGet(ipc, "video-crop");
            _savedVf = await SafeGet(ipc, "vf");
            _savedImageDuration = await SafeGet(ipc, "image-display-duration");
            await SafeSet(ipc, "video-crop", "");
            await SafeSet(ipc, "vf", "");

            _savedSpeed = await SafeGet(ipc, "speed");
            await SafeSet(ipc, "speed", "1.0");

            _savedVolume = await SafeGet(ipc, "volume");
            _savedVolumeMax = await SafeGet(ipc, "volume-max");
            _savedMute = await SafeGet(ipc, "mute");
            if (!meme.PlaySound)
            {
                await SafeSet(ipc, "mute", "yes");
            }
            else if (GainDbFor(meme.FilePath) is double gainDb && Math.Abs(gainDb) > 0.05
                     && double.TryParse(_savedVolume, NumberStyles.Float, CultureInfo.InvariantCulture, out double vol))
            {
                double target = Math.Clamp(vol * Math.Pow(10, gainDb / 20.0), 0, 400);
                await SafeSet(ipc, "volume-max", "400");
                await SafeSet(ipc, "volume", target.ToString("0.##", CultureInfo.InvariantCulture));
            }

            await SafeSet(ipc, "image-display-duration",
                MemeDurationSec.ToString("F3", CultureInfo.InvariantCulture));

            await ipc.LoadFileAsync(meme.FilePath, Math.Max(0, meme.ClipInSec));
            await WaitUntilAsync(() => ipc.Duration > 0 || ipc.CurrentTime > 0.02, LoadTimeoutMs);

            _memeClock.Restart();
            CurrentPhase = Phase.PlayingMeme;
            _setBusy(false, "");
            await SafeSet(ipc, "pause", "no");

            RuntimeLog.Info(_logTag,
                $"Meme cutaway started: '{System.IO.Path.GetFileName(meme.FilePath)}' " +
                $"({MemeDurationSec:0.###}s) at {anchorAbs:0.###}s source.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(_logTag, $"Meme cutaway failed to start ({ex.Message}); returning to the video.");
            CurrentPhase = Phase.Returning;
            Interlocked.Exchange(ref _inFlight, 0);
            await ReturnToSourceAsync(ipc);
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private async Task ReturnToSourceAsync(MpvIpcClient ipc)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        try
        {
            _memeClock.Stop();
            _setBusy(true, "Back to your video…");

            await SafeSet(ipc, "pause", "yes");
            await SafeSet(ipc, "image-display-duration", _savedImageDuration ?? "1");
            await SafeSet(ipc, "video-crop", _savedCrop ?? "");
            await SafeSet(ipc, "vf", _savedVf ?? "");
            await SafeSet(ipc, "speed", _savedSpeed ?? "1.0");
            if (_savedVolume != null) await SafeSet(ipc, "volume", _savedVolume);
            if (_savedVolumeMax != null) await SafeSet(ipc, "volume-max", _savedVolumeMax);
            await SafeSet(ipc, "mute", _savedMute ?? "no");
            _savedVolume = _savedVolumeMax = _savedMute = null;

            string? src = _sourcePath();
            if (!string.IsNullOrWhiteSpace(src))
            {
                await ipc.LoadFileAsync(src!, AnchorAbsSourceSec);
                await WaitUntilAsync(
                    () => ipc.Duration > 0 && Math.Abs(ipc.CurrentTime - AnchorAbsSourceSec) < 0.75,
                    ReturnTimeoutMs);
            }

            _suppressId = ActiveMemeId;
            _suppressUntilSourceSec = AnchorAbsSourceSec + ReArmGuardSec;
            _lastSourceSec = ipc.CurrentTime;

            await SafeSet(ipc, "pause", _resumePaused ? "yes" : "no");

            RuntimeLog.Info(_logTag, $"Meme cutaway finished; gameplay resumed at {AnchorAbsSourceSec:0.###}s.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail(_logTag, $"Meme cutaway could not return cleanly: {ex.Message}");
        }
        finally
        {
            MemeElapsedSec = 0;
            ActiveMemeId = null;
            ActiveMemeFile = null;
            CurrentPhase = Phase.Idle;
            _setBusy(false, "");
            Interlocked.Exchange(ref _inFlight, 0);
            MemeEnded?.Invoke();
        }
    }

    public async Task AbortAsync()
    {
        if (CurrentPhase == Phase.Idle) return;
        var ipc = _client();
        if (ipc == null) { CurrentPhase = Phase.Idle; _setBusy(false, ""); return; }
        CurrentPhase = Phase.Returning;
        await ReturnToSourceAsync(ipc);
    }

    private double? GainDbFor(string memePath)
    {
        string? src = _sourcePath();
        if (string.IsNullOrEmpty(src)) return null;
        if (!_lufs.TryGetValue(memePath, out double? memeLufs) || !_lufs.TryGetValue(src, out double? gameLufs)) return null;
        return MemeLoudness.GainFor(memeLufs, gameLufs);
    }

    private static void MeasureInBackground(string path, bool isMeme)
    {
        if (string.IsNullOrWhiteSpace(path) || _lufs.ContainsKey(path) || !_measuring.TryAdd(path, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string ffmpeg = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
                double? lufs = isMeme
                    ? await MemeLoudness.MeasureLufsAsync(ffmpeg, path, CancellationToken.None).ConfigureAwait(false)
                    : (await AudioLoudnessProbe.MeasureAsync(ffmpeg, path).ConfigureAwait(false))?.IntegratedLufs;
                _lufs[path] = lufs;
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            finally { _measuring.TryRemove(path, out _); }
        });
    }

    private static async Task<string?> SafeGet(MpvIpcClient ipc, string name)
    {
        try { return await ipc.ReadPropertyAsync(name); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return null; }
    }

    private static async Task SafeSet(MpvIpcClient ipc, string name, string value)
    {
        try { await ipc.SetPropertyAsync(name, value); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static async Task WaitUntilAsync(Func<bool> ready, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool ok;
            try { ok = ready(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); ok = false; }
            if (ok) return;
            await Task.Delay(PollMs);
        }
    }
}
