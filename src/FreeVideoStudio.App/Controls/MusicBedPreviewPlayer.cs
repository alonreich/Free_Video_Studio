using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public readonly record struct MusicPreviewFrame(
    double TrimStartMs,
    double VideoEndMs,
    Func<double, double> SourceMsToOutputSec,
    double OutputSec,
    bool Playing,
    bool VideoSeeking,
    bool AllowStart,
    bool MemeActive,
    IReadOnlyList<(double AnchorOutputSec, double DurationSec)>? InlineMemes,
    bool MemePlaying = false,
    IReadOnlyList<(double StartOutputSec, double EndOutputSec)>? FreezeWindows = null,
    bool MemeStopsMusic = false);

public sealed class MusicBedPreviewPlayer : IDisposable
{
    private readonly BedPreviewFollower _follower = new();
    private readonly AudioPlayerClock _clock = new();
    private readonly Func<MpvIpcClient> _factory;
    private MpvIpcClient? _client;
    private Task? _clientReady;
    private MusicWizardResult? _result;
    private string? _bedKey;
    private MusicPreviewBed _bed = new(Array.Empty<MusicBedSegment>(), 0, 0);
    private bool _disposed;
    private bool _heldForMeme;
    private string _appliedAf = "";
    private string _wantedAf = "";

    public MusicBedPreviewPlayer(Func<MpvIpcClient>? factory = null)
    {
        _factory = factory ?? (() => new MpvIpcClient());
        MpvIpcClient.GlobalMasterVolumeChanged += OnMasterVolumeChanged;
    }

    public MusicWizardResult? Result
    {
        get => _result;
        set
        {
            if (ReferenceEquals(value, _result)) return;
            _result = value;
            _bedKey = null;
            Stop();
        }
    }

    public bool KeepMusicDuringMeme { get; set; }

    public Func<double>? GainSource { get; set; }

    public bool HasMusic => _result != null && !string.IsNullOrEmpty(_result.MusicFilePath);

    public bool IsPlaying => _follower.IsPlaying;

    public BedPreviewFollower Follower => _follower;

    public MpvIpcClient? Client => _client;

    public MusicPreviewBed CurrentBed => _bed;

    public string AppliedAudioFilter => _appliedAf;

    public static string FreezeAf(IReadOnlyList<MusicBedSegment> plan, int segmentIndex,
        IReadOnlyList<(double StartOutputSec, double EndOutputSec)>? freezeWindows, Func<double, double>? toBedClock = null)
    {
        if (freezeWindows == null || freezeWindows.Count == 0 || segmentIndex < 0 || segmentIndex >= plan.Count) return "";
        var seg = plan[segmentIndex];
        double shift = seg.FileOffsetSec + seg.LeadInSec - seg.OutputStartSec;
        var inFileTime = new List<(double, double)>();
        foreach (var (s, e) in freezeWindows)
        {
            double bs = toBedClock?.Invoke(s) ?? s, be = toBedClock?.Invoke(e) ?? e;
            inFileTime.Add((bs + shift, be + shift));
        }
        string? pulse = FreezeSoundEffect.MusicDipPulse(inFileTime);
        if (pulse == null) return "";
        string graph = FreezeSoundEffect.PreviewMusicGraph(pulse);
        return $"@fvsfreeze:lavfi=graph=%{Encoding.UTF8.GetByteCount(graph)}%{graph}";
    }

    public double EstimatedPositionSec => _client == null ? double.NaN
        : _clock.PositionSec(_follower.IsPlaying && !_heldForMeme && !_client.IsPaused && !_client.IsSeeking);

    public double LastBedClockSec { get; private set; } = double.NaN;

    public MusicPreviewBed Bed(double trimStartMs, double videoEndMs, Func<double, double> sourceMsToOutputSec,
        IReadOnlyList<(double AnchorOutputSec, double DurationSec)>? inlineMemes)
    {
        var result = _result;
        if (result == null) return _bed = new(Array.Empty<MusicBedSegment>(), 0, 0);
        double musicStartMs = Math.Max(trimStartMs, result.TimelineStartSeconds * 1000.0);
        double musicEndMs = Math.Min(videoEndMs, result.TimelineEndSeconds * 1000.0);
        string key = BedKey(result, sourceMsToOutputSec(musicStartMs), sourceMsToOutputSec(musicEndMs), inlineMemes);
        if (key == _bedKey) return _bed;
        _bed = result.PreviewBed(trimStartMs, videoEndMs, sourceMsToOutputSec, KeepMusicDuringMeme, inlineMemes);
        _bedKey = key;
        return _bed;
    }

    private string BedKey(MusicWizardResult r, double startOut, double endOut,
        IReadOnlyList<(double AnchorOutputSec, double DurationSec)>? memes)
    {
        var ci = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        b.Append(startOut.ToString("R", ci)).Append('|').Append(endOut.ToString("R", ci)).Append('|').Append(KeepMusicDuringMeme)
         .Append('|').Append(r.MusicFilePath).Append('|').Append(string.Join(";", r.MusicFilePaths ?? new()))
         .Append('|').Append(r.OffsetSeconds.ToString("R", ci)).Append('|').Append(r.LoopMusic)
         .Append('|').Append(r.MusicDurationSeconds.ToString("R", ci))
         .Append('|').Append(string.Join(";", r.MusicDurationsSeconds ?? new()))
         .Append('|').Append(string.Join(";", r.MusicStartsSeconds ?? new()))
         .Append('|').Append(string.Join(";", r.MusicEndsSeconds ?? new()));
        if (KeepMusicDuringMeme && memes != null)
            foreach (var (a, d) in memes) b.Append('|').Append(a.ToString("R", ci)).Append('+').Append(d.ToString("R", ci));
        return b.ToString();
    }

    public void Update(in MusicPreviewFrame frame)
    {
        if (_disposed) return;
        if (!HasMusic)
        {
            if (_follower.IsPlaying) Stop();
            return;
        }
        if (frame.MemeActive)
        {
            if (!BedPreviewFollower.PlaysDuringMeme(KeepMusicDuringMeme)) { if (_follower.IsPlaying) Stop(); }
            else
            {
                SetMemeMuted(frame.MemeStopsMusic);
                if (_follower.IsPlaying) HoldForMeme(!frame.MemePlaying);
            }
            return;
        }
        SetMemeMuted(false);
        if (_heldForMeme)
        {
            _heldForMeme = false;
            _follower.RequestResync();
        }

        var bed = Bed(frame.TrimStartMs, frame.VideoEndMs, frame.SourceMsToOutputSec, frame.InlineMemes);
        double clock = BedPreviewFollower.BedClock(frame.OutputSec, bed.StartDelaySec, KeepMusicDuringMeme, frame.InlineMemes);
        LastBedClockSec = clock;
        var memes = frame.InlineMemes;
        bool keep = KeepMusicDuringMeme;
        double delay = bed.StartDelaySec;
        _wantedAf = FreezeAf(bed.Plan, MusicBedPlan.Locate(bed.Plan, clock)?.SegmentIndex ?? -1, frame.FreezeWindows,
            w => BedPreviewFollower.BedClock(w, delay, keep, memes));
        var client = _client;
        if (client != null && _follower.IsPlaying && _wantedAf != _appliedAf)
        {
            _appliedAf = _wantedAf;
            _ = client.SetPropertyAsync("af", _wantedAf);
        }
        var decision = _follower.Decide(bed.Plan, clock, frame.Playing, frame.VideoSeeking, frame.AllowStart,
            EstimatedPositionSec, client?.IsSeeking ?? false, Environment.TickCount64);
        switch (decision.Action)
        {
            case BedPreviewAction.Start:
                _ = StartAsync(decision.Path!, decision.PositionSec);
                break;
            case BedPreviewAction.Seek:
                _clock.Reset();
                _ = client?.SendCommandAsync("seek", decision.PositionSec, "absolute");
                break;
            case BedPreviewAction.Stop:
                _ = StopPlaybackAsync();
                break;
        }
    }

    private Task EnsureClientAsync()
    {
        if (_clientReady != null) return _clientReady;
        var client = _factory();
        _client = client;
        _clock.Attach(client);
        _clientReady = client.StartAudioOnlyAsync("");
        return _clientReady;
    }

    private async Task StartAsync(string path, double positionSec)
    {
        long decidedAt = Environment.TickCount64;
        try
        {
            await EnsureClientAsync();
            var client = _client;
            if (client == null || _disposed) return;
            await client.ApplyPreviewGainAsync(CurrentGain());
            await client.SetPropertyDoubleAsync("speed", 1.0);
            _appliedAf = _wantedAf;
            await client.SetPropertyAsync("af", _wantedAf);
            await client.SetPropertyAsync("pause", "yes");
            _clock.Reset();
            bool loaded = client.Duration > 0 && string.Equals(await client.ReadPropertyAsync("path"), path, StringComparison.OrdinalIgnoreCase)
                && !client.IsEof;
            if (!loaded)
            {
                await client.SetPropertyDoubleAsync("start", Math.Max(0, positionSec));
                await client.SendCommandAsync("loadfile", path);
            }
            long deadline = Environment.TickCount64 + LoadWaitMs;
            while (Environment.TickCount64 < deadline && !_disposed &&
                   !(client.Duration > 0 && string.Equals(await client.ReadPropertyAsync("path"), path, StringComparison.OrdinalIgnoreCase)))
                await Task.Delay(5);
            if (_disposed || !_follower.IsPlaying || !string.Equals(_follower.Path, path, StringComparison.OrdinalIgnoreCase)) return;
            double catchUp = Math.Max(0, (Environment.TickCount64 - decidedAt) / 1000.0);
            await client.SendCommandAsync("seek", Math.Max(0, positionSec + catchUp), "absolute+exact");
            _clock.Reset();
            await client.SetPropertyAsync("pause", "no");
        }
        catch (Exception ex)
        {
            _follower.NotifyStopped();
            RuntimeLog.WarnThrottled("MusicPreview", $"Music preview could not play: {ex.Message}");
        }
    }

    private const int LoadWaitMs = 1500;

    private async Task StopPlaybackAsync()
    {
        var client = _client;
        if (client == null) return;
        try
        {
            await client.SetPropertyAsync("pause", "yes");
            await client.SendCommandAsync("stop");
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public bool IsHeldForMeme => _heldForMeme;

    private void HoldForMeme(bool hold)
    {
        if (hold == _heldForMeme) return;
        _heldForMeme = hold;
        _ = _client?.SetPropertyAsync("pause", hold ? "yes" : "no");
    }

    public void Stop()
    {
        _heldForMeme = false;
        bool wasPlaying = _follower.IsPlaying;
        _follower.NotifyStopped();
        if (wasPlaying) _ = StopPlaybackAsync();
    }

    private bool _memeMuted;

    public bool IsMutedForMeme => _memeMuted;

    private void SetMemeMuted(bool muted)
    {
        if (muted == _memeMuted) return;
        _memeMuted = muted;
        ApplyGain();
    }

    private double CurrentGain() => _memeMuted ? 0.0 : GainSource?.Invoke() ?? _result?.MusicVolume ?? 1.0;

    public void ApplyGain() => _ = _client?.ApplyPreviewGainAsync(CurrentGain());

    private void OnMasterVolumeChanged(int volume) => ApplyGain();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MpvIpcClient.GlobalMasterVolumeChanged -= OnMasterVolumeChanged;
        _follower.NotifyStopped();
        _clock.Dispose();
        try { _client?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _client = null;
        _clientReady = null;
    }
}
