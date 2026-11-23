using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private const int MixDebounceMs = 800;
    private const int MixSyncSettleMs = 350;

    private DispatcherTimer? _mixTimer;
    private FreeVideoStudio.Core.Media.MpvIpcClient? _mixClient;
    private readonly AudioPlayerClock _mixClock = new();
    private readonly RenderedMixGate _mixGate = new();
    private long _mixObservedAtTicks;
    private CancellationTokenSource? _mixCts;
    private bool _mixPlaying;
    private string? _mixLoadedPath;
    private long _mixDriftSinceMs;
    private long _mixHoldUntilTicks;
    private int _mixSlot;
    private double _previewVoicePulse;
    private string? _liveGameFilter;

    private bool PreviewMixActive => _mixGate.IsActive;

    private void RefreshPreviewMixState()
    {
        long now = Environment.TickCount64;
        if (now - _mixObservedAtTicks < 20) return;
        _mixObservedAtTicks = now;

        bool wasActive = PreviewMixActive;
        if (_mixGate.Observe(PreviewMixSignature(), now) && wasActive != PreviewMixActive) OnPreviewMixActiveChanged();
    }

    private double PreviewGameDuckGain()
    {
        if (PreviewMixActive) return 0.0;
        return _voiceOverResult?.DuckAudio == true ? 1.0 - AudioFilterChain.VoiceDuckDepth * _previewVoicePulse : 1.0;
    }

    private double PreviewMusicDuckGain()
    {
        if (PreviewMixActive) return 0.0;
        return _voiceOverResult?.ProtectFromMusic == true ? 1.0 - AudioFilterChain.VoiceDuckDepth * _previewVoicePulse : 1.0;
    }

    private void UpdatePreviewVoicePulse(double outputSec)
    {
        double pulse = outputSec < 0 ? 0 : AudioFilterChain.VoiceProtectionPulseAt(outputSec,
            _voiceOverPreviewTakes.Select(take => (take.StartProjectSec, take.StartProjectSec + take.Reader.TotalTime.TotalSeconds)));
        if (Math.Abs(pulse - _previewVoicePulse) < 0.02 && !(pulse == 0 && _previewVoicePulse != 0)) return;
        _previewVoicePulse = pulse;
        if (_voiceOverResult?.DuckAudio == true || _voiceOverResult?.ProtectFromMusic == true) ApplyPreviewPlayersVolume();
    }

    private void ApplyLivePreviewFilters()
    {
        var settings = Infrastructure.SettingsManager.Instance;
        bool wanted = settings.Defaults.AutoSpikeFlattening
                      && (_applyPeakFlattening ?? settings.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.NeverApply);
        string filter = wanted && PeakSafety.TamerFilter(_gameplayLoudnessLufs) is string t ? $"lavfi=[{t}]" : "";
        if (filter == _liveGameFilter) return;
        _liveGameFilter = filter;
        _ = ActiveVideoHost?.IpcClient?.SetPropertyAsync("af", filter);
    }

    private void EnsurePreviewMixScheduler()
    {
        if (_mixTimer != null) return;
        _mixTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mixTimer.Tick += (_, _) => PreviewMixSchedulerTick();
        _mixTimer.Start();
    }

    private void PreviewMixSchedulerTick()
    {
        ApplyLivePreviewFilters();
        RefreshPreviewMixState();
        UpdatePreviewFidelity();

        string? sig = _mixGate.CurrentSignature;
        if (sig == null || !_mixGate.ShouldStart(Environment.TickCount64, MixDebounceMs)) return;
        _ = RenderPreviewMixAsync(sig);
    }

    private async Task RenderPreviewMixAsync(string sig)
    {
        try { _mixCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        var cts = new CancellationTokenSource();
        _mixCts = cts;
        string dir = _paths.TempDirectory;
        _mixSlot ^= 1;
        string wav = Path.Combine(dir, $"fvs_preview_mix_{Environment.ProcessId}_{_mixSlot}.wav");
        bool wasActive = PreviewMixActive;
        var ticket = _mixGate.Begin(sig, wav);
        if (wasActive != PreviewMixActive) OnPreviewMixActiveChanged();
        bool failed = false;
        AudioPreviewMap? map = null;
        try
        {
            Directory.CreateDirectory(dir);
            if (string.Equals(wav, _mixLoadedPath, StringComparison.OrdinalIgnoreCase))
            {
                await StopPreviewMixPlaybackAsync();
            }

            var payload = await ComposeExportPayloadAsync(dir, SelectedLegacyMemeFile(), 20, null);
            if (PreviewMixSignature() != sig)
            {
                _mixGate.TryComplete(ticket, null, null, cancelled: true);
                return;
            }
            map = await Task.Run(() => Services.MainMediaController.RenderAudioPreviewAsync(payload, wav, cts.Token), cts.Token);
            failed = map == null && !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            failed = true;
            RuntimeLog.WarnThrottled("PreviewMix", $"Rendered preview mix unavailable: {ex.Message}");
        }

        if (failed)
        {
            _mixGate.Fail(ticket);
            RuntimeLog.WarnThrottled("PreviewMix", "Rendered preview mix failed; the live players stay in charge until the next edit.");
            UpdatePreviewFidelity();
            return;
        }

        _mixObservedAtTicks = 0;
        RefreshPreviewMixState();
        if (_mixGate.TryComplete(ticket, map, PreviewMixSignature(), cts.IsCancellationRequested))
        {
            RuntimeLog.Info("PreviewMix", "Rendered mix is live in the preview (the export's own audio graph).");
            OnPreviewMixActiveChanged();
        }
        UpdatePreviewFidelity();
    }

    private async Task<AudioPreviewMap?> RenderWizardSoundPreviewAsync(MusicWizardResult music, string wavPath, CancellationToken ct)
    {
        NormalizeMusicPlacement(music);
        var payload = await ComposeExportPayloadAsync(_paths.TempDirectory, SelectedLegacyMemeFile(), 20, null, music);
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => Services.MainMediaController.RenderAudioPreviewAsync(payload, wavPath, ct), ct);
    }

    private void OnPreviewMixActiveChanged()
    {
        if (PreviewMixActive)
        {
            _freezeSound.Stop();
            StopMusicPreview();
        }
        else
        {
            _ = StopPreviewMixPlaybackAsync();
        }
        ApplyPreviewPlayersVolume();
    }

    private void UpdatePreviewMix(double sourceTimeSec, bool videoEnded)
    {
        EnsurePreviewMixScheduler();
        RefreshPreviewMixState();
        if (!PreviewMixActive)
        {
            if (_mixPlaying) _ = StopPreviewMixPlaybackAsync();
            return;
        }

        var ipc = ActiveVideoHost?.IpcClient;
        bool isPaused = ipc?.IsPaused ?? true;
        if (_isCurrentlyFrozen) isPaused = false;
        if (isPaused || videoEnded)
        {
            if (_mixPlaying)
            {
                _mixPlaying = false;
                _ = _mixClient?.SetPropertyAsync("pause", "yes");
            }
            return;
        }

        if (ipc?.IsSeeking == true) return;
        double want = _mixGate.Map!.MixSecFor(PreviewOutputSeconds(sourceTimeSec));
        if (!_mixPlaying || !string.Equals(_mixLoadedPath, _mixGate.RenderedPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = StartPreviewMixPlaybackAsync(_mixGate.RenderedPath!, want);
            return;
        }

        if (Environment.TickCount64 < _mixHoldUntilTicks || _mixClient == null || _mixClient.IsSeeking) return;
        if (Math.Abs(_mixClock.PositionSec(!_mixClient.IsPaused) - want) <= Infrastructure.PreviewAudioSync.DriftToleranceSec)
        {
            _mixDriftSinceMs = 0;
            return;
        }
        long nowMs = Environment.TickCount64;
        if (_mixDriftSinceMs <= 0) { _mixDriftSinceMs = nowMs; return; }
        if (nowMs - _mixDriftSinceMs < Infrastructure.PreviewAudioSync.DriftConfirmMs) return;
        _mixDriftSinceMs = 0;
        _mixClock.Reset();
        _ = _mixClient.SendCommandAsync("seek", want, "absolute");
        _mixHoldUntilTicks = Environment.TickCount64 + MixSyncSettleMs;
    }

    private async Task StartPreviewMixPlaybackAsync(string path, double positionSec)
    {
        _mixPlaying = true;
        try
        {
            if (_mixClient == null)
            {
                _mixClient = new FreeVideoStudio.Core.Media.MpvIpcClient();
                await _mixClient.StartAudioOnlyAsync("");
                _mixClock.Attach(_mixClient);
            }
            long decidedAt = Environment.TickCount64;
            var client = _mixClient;
            await client.ApplyPreviewGainAsync();
            await client.SetPropertyDoubleAsync("speed", 1.0);
            await client.SetPropertyAsync("pause", "yes");
            await client.SetPropertyDoubleAsync("start", Math.Max(0, positionSec));
            await client.SendCommandAsync("loadfile", path);
            long deadline = Environment.TickCount64 + 1500;
            while (Environment.TickCount64 < deadline &&
                   !(client.Duration > 0 && string.Equals(await client.ReadPropertyAsync("path"), path, StringComparison.OrdinalIgnoreCase)))
                await Task.Delay(5);
            if (!_mixPlaying) return;
            double catchUp = Math.Max(0, (Environment.TickCount64 - decidedAt) / 1000.0);
            await client.SendCommandAsync("seek", Math.Max(0, positionSec + catchUp), "absolute+exact");
            _mixClock.Reset();
            await client.SetPropertyAsync("pause", "no");
            _mixLoadedPath = path;
            _mixDriftSinceMs = 0;
            _mixHoldUntilTicks = Environment.TickCount64 + MixSyncSettleMs;
        }
        catch (Exception ex)
        {
            _mixPlaying = false;
            RuntimeLog.WarnThrottled("PreviewMix", $"Rendered mix could not play: {ex.Message}");
        }
    }

    private async Task StopPreviewMixPlaybackAsync()
    {
        _mixPlaying = false;
        if (_mixClient == null) return;
        try
        {
            await _mixClient.SetPropertyAsync("pause", "yes");
            await _mixClient.SendCommandAsync("stop");
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mixLoadedPath = null;
    }

    private void ApplyPreviewMixVolume() => _ = _mixClient?.ApplyPreviewGainAsync();

    private void DisposePreviewMix()
    {
        try { _mixCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mixClock.Attach(null);
        try { _mixClient?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mixClient = null;
        _mixPlaying = false;
        _mixLoadedPath = null;
        _mixGate.Reset();
    }

    private string? PreviewMixSignature()
    {
        if (string.IsNullOrEmpty(_loadedVideoPath) || _loadedVideoDurationMs <= 0) return null;

        var settings = Infrastructure.SettingsManager.Instance;
        bool tamer = settings.Defaults.AutoSpikeFlattening
                     && (_applyPeakFlattening ?? settings.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.NeverApply);
        var music = _musicWizardResult;
        var vo = _voiceOverResult;
        string? legacyMeme = SelectedLegacyMemeFile();
        bool hasVo = vo != null && (vo.VoiceOverTakes.Count > 0 || !string.IsNullOrEmpty(vo.VoiceOverWavPath));
        var exportSegments = BuildExportSpeedSegments();
        if (!PreviewMixPolicy.NeedsRenderedMix(music != null, hasVo, _memePlacements.Count > 0 || legacyMeme != null, tamer,
                _baseSpeed, exportSegments?.Select(s => s.Speed))) return null;

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(_loadedVideoPath).Append('|').Append(_trimStartMs.ToString("F1", ci)).Append('|').Append(_trimEndMs.ToString("F1", ci))
          .Append('|').Append(_baseSpeed.ToString("F4", ci))
          .Append('|').Append(exportSegments == null ? "" : string.Join(",", exportSegments))
          .Append('|').Append(string.Join(",", _cuts))
          .Append('|').Append(string.Join(",", _memePlacements))
          .Append('|').Append(legacyMeme).Append(legacyMeme != null ? Infrastructure.MemePlacementStore.Get(legacyMeme).ToString() : "")
          .Append('|').Append(this.FindControl<Avalonia.Controls.ToggleSwitch>("EnableFadeCheckbox")?.IsChecked)
          .Append('|').Append(_keepMusicDuringMeme)
          .Append('|').Append(tamer).Append(_gameplayLoudnessLufs?.ToString("F2", ci))
          .Append('|').Append(settings.Defaults.DuckingEnabled).Append(settings.Defaults.DuckingStrength)
          .Append(settings.Defaults.CarvingEnabled).Append(settings.Defaults.CarvingStrength)
          .Append('|').Append(FreezeSoundEffect.NameOf(FreezeSoundEffect.Selected))
          .Append('|').Append(VideoLoudnessCache.TryGet(_loadedVideoPath)?.ToString("F2", ci));
        if (music != null)
        {
            sb.Append("|M:").Append(music.MusicFilePath).Append(string.Join(",", music.MusicFilePaths))
              .Append(music.OffsetSeconds.ToString("F3", ci)).Append(',').Append(music.TimelineStartSeconds.ToString("F3", ci))
              .Append(',').Append(music.TimelineEndSeconds.ToString("F3", ci)).Append(',').Append(music.VideoVolume.ToString("F3", ci))
              .Append(',').Append(music.MusicVolume.ToString("F3", ci)).Append(music.EnableDucking).Append(music.EnableCarving)
              .Append(music.LoopMusic)
              .Append(',').Append(string.Join(";", (music.MusicDurationsSeconds ?? new()).Select(d => d.ToString("F3", ci))))
              .Append(',').Append(music.MusicDurationSeconds.ToString("F3", ci))
              .Append(",S:").Append(string.Join(";", (music.MusicStartsSeconds ?? new()).Select(d => d.ToString("F3", ci))))
              .Append(",E:").Append(string.Join(";", (music.MusicEndsSeconds ?? new()).Select(d => d.ToString("F3", ci))));
        }
        if (hasVo)
        {
            sb.Append("|V:").Append(vo!.DuckAudio).Append(vo.ProtectFromMusic).Append(vo.VoiceOverWavPath)
              .Append(vo.VoiceOverStartTimestampSec.ToString("F3", ci));
            foreach (var t in vo.VoiceOverTakes)
            {
                long len = 0;
                try { len = new FileInfo(t.Path).Length; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                sb.Append(';').Append(t.Path).Append('@').Append(t.StartSec.ToString("F3", ci))
                  .Append('+').Append(t.OutputOffsetSec.ToString("F6", ci)).Append('#').Append(len)
                  .Append("g").Append(VoiceOverLevel.Clamp(t.Gain).ToString("R", ci));
            }
        }
        return sb.ToString();
    }
}
