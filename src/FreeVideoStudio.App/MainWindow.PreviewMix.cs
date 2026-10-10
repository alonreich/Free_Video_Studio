// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

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

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PREVIEWMIX_01 — THE PREVIEW PLAYS THE EXPORT'S OWN AUDIO.
///
/// The live preview is three independent players (gameplay, music bed, voice-over takes), so it
/// could never reproduce what only exists in the export's filter graph: the multiband ducking and
/// speech carving (the GAME is the music's sidechain), the voice-over protection, the peak tamer,
/// the safety limiter and the meme levels. Whenever the edit has any of those, the export's audio is
/// rendered in the background — ProcessWorker builds the export graph itself and AudioGraphPruner
/// runs only its audio half (measured: the rendered mix and the exported file's audio differ by
/// -60 dB RMS, i.e. AAC noise) — and played here in place of the live players, following the video
/// in output time exactly like the music bed does (seek-only correction, MUSICSYNC_02).
///
/// After every edit the old mix is dropped at once (the live players take over, so nothing stale is
/// ever heard) and a new one is rendered ~0.8 s after the edits stop.
///
/// PREVIEWMIX_02 — which mix may be heard is decided by ONE gate (<see cref="RenderedMixGate"/>): the
/// signature is re-observed on every playback tick BEFORE any preview audio is driven (not only on the
/// 250 ms scheduler), every render is a numbered ticket that completes only if it is the newest and its
/// signature is still the edit's signature re-computed at completion, a WAV being overwritten stops
/// being a valid mix, and a failed render is latched instead of retried every 250 ms.
///
/// VOPREVIEW_01 — until a mix is ready (or when the edit has nothing the export would change), the
/// live players do what can be done live and exactly: the voice-over protection's 85% dip is a pure
/// time pulse (VOPRIO_01: voice first, then gameplay, then music), and the peak tamer is a static
/// filter on the gameplay player.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class MainWindow
{
    private const int MixDebounceMs = 800;
    private const int MixSyncSettleMs = 350;

    private DispatcherTimer? _mixTimer;
    private FreeVideoStudio.Core.Media.MpvIpcClient? _mixClient;
    private readonly RenderedMixGate _mixGate = new();   // PREVIEWMIX_02 — the one owner of "which mix is valid"
    private long _mixObservedAtTicks;
    private CancellationTokenSource? _mixCts;
    private bool _mixPlaying;
    private string? _mixLoadedPath;
    private int _mixDriftStrikes;
    private long _mixHoldUntilTicks;
    private int _mixSlot;
    private double _previewVoicePulse;
    private string? _liveGameFilter;

    /// <summary>True while the rendered mix matches the current edit and replaces the live players.</summary>
    private bool PreviewMixActive => _mixGate.IsActive;

    /// <summary>
    /// PREVIEWMIX_02 — re-observes the edit's signature. Called first thing on every playback tick (and by
    /// the scheduler), so an edit silences the old mix before any preview audio is driven again. Several
    /// callers in one tick share one computation.
    /// </summary>
    private void RefreshPreviewMixState()
    {
        long now = Environment.TickCount64;
        if (now - _mixObservedAtTicks < 20) return;
        _mixObservedAtTicks = now;

        bool wasActive = PreviewMixActive;
        if (_mixGate.Observe(PreviewMixSignature(), now) && wasActive != PreviewMixActive) OnPreviewMixActiveChanged();
    }

    // ── VOPREVIEW_01 — live gains ──────────────────────────────────────────────────────────────

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

    /// <summary>The voice-over pulse at output time <paramref name="outputSec"/>: the export's 0.3 s ramps, exactly.</summary>
    private void UpdatePreviewVoicePulse(double outputSec)
    {
        // VOPREVIEW_02 — the export's own pulse (summed ramps), shared with the Voice Over studio.
        double pulse = outputSec < 0 ? 0 : AudioFilterChain.VoiceProtectionPulseAt(outputSec,
            _voiceOverPreviewTakes.Select(take => (take.StartProjectSec, take.StartProjectSec + take.Reader.TotalTime.TotalSeconds)));
        if (Math.Abs(pulse - _previewVoicePulse) < 0.02 && !(pulse == 0 && _previewVoicePulse != 0)) return;
        _previewVoicePulse = pulse;
        if (_voiceOverResult?.DuckAudio == true || _voiceOverResult?.ProtectFromMusic == true) ApplyPreviewPlayersVolume();
    }

    /// <summary>PEAKSAFE_01 in the live preview: the gameplay player gets the same tamer as the export.</summary>
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

    // ── PREVIEWMIX_01 — rendered mix ──────────────────────────────────────────────────────────

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
        UpdatePreviewFidelity();   // PREVIEWFIDELITY_01

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
        // PREVIEWMIX_02 — the ticket supersedes any older render; a valid mix living in `wav` stops being valid.
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
            // The payload is read from the editor across an await (music probing): if the edit moved
            // meanwhile, this payload may not be `sig`'s — abandon it (the next tick renders the new edit).
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

        // Back on the UI thread: accept only the newest ticket, and only if the edit is STILL what was rendered.
        _mixObservedAtTicks = 0;
        RefreshPreviewMixState();
        if (_mixGate.TryComplete(ticket, map, PreviewMixSignature(), cts.IsCancellationRequested))
        {
            RuntimeLog.Info("PreviewMix", "Rendered mix is live in the preview (the export's own audio graph).");
            OnPreviewMixActiveChanged();
        }
        UpdatePreviewFidelity();
    }

    private void OnPreviewMixActiveChanged()
    {
        if (PreviewMixActive)
        {
            StopMusicPreview();
        }
        else
        {
            _ = StopPreviewMixPlaybackAsync();
        }
        ApplyPreviewPlayersVolume();
    }

    /// <summary>Called on every playback tick, after the live music/voice followers.</summary>
    private void UpdatePreviewMix(double sourceTimeSec, bool videoEnded)
    {
        EnsurePreviewMixScheduler();
        RefreshPreviewMixState();   // PREVIEWMIX_02 — never drive a mix the current edit no longer matches
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

        // Pausing wins over seek settling: never leave the soundtrack running during a paused scrub.
        if (ipc?.IsSeeking == true) return;   // SEEKSETTLE_01 — the old video clock is not a sync target
        double want = _mixGate.Map!.MixSecFor(PreviewOutputSeconds(sourceTimeSec));
        if (!_mixPlaying || !string.Equals(_mixLoadedPath, _mixGate.RenderedPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = StartPreviewMixPlaybackAsync(_mixGate.RenderedPath!, want);
            return;
        }

        if (Environment.TickCount64 < _mixHoldUntilTicks || _mixClient == null || _mixClient.IsSeeking) return;
        if (Math.Abs(_mixClient.CurrentTime - want) <= Infrastructure.PreviewAudioSync.DriftToleranceSec)
        {
            _mixDriftStrikes = 0;
            return;
        }
        if (++_mixDriftStrikes < Infrastructure.PreviewAudioSync.DriftStrikes) return;
        _mixDriftStrikes = 0;
        _ = _mixClient.SendCommandAsync("seek", want, "absolute");   // seek, never re-rate
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
            }
            await _mixClient.ApplyPreviewGainAsync();   // the master only: the mix already carries every fader
            await _mixClient.SetPropertyDoubleAsync("speed", 1.0);
            await _mixClient.LoadFileAsync(path, Math.Max(0, positionSec));
            _mixLoadedPath = path;
            _mixDriftStrikes = 0;
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

    /// <summary>Re-applies the master to the mix player (called with the other players).</summary>
    private void ApplyPreviewMixVolume() => _ = _mixClient?.ApplyPreviewGainAsync();

    private void DisposePreviewMix()
    {
        try { _mixCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _mixClient?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mixClient = null;
        _mixPlaying = false;
        _mixLoadedPath = null;
        _mixGate.Reset();
    }

    /// <summary>
    /// Everything the export's AUDIO depends on. Null when the edit has nothing the live players
    /// cannot already reproduce (no music, no voice-over, no meme, no peak tamer) — then no mix is
    /// rendered at all.
    /// </summary>
    private string? PreviewMixSignature()
    {
        if (string.IsNullOrEmpty(_loadedVideoPath) || _loadedVideoDurationMs <= 0) return null;

        var settings = Infrastructure.SettingsManager.Instance;
        // PREVIEWMIX_02 — the export applies the tamer whether or not the UPLOAD reading exists (it measures
        // the exported range itself), so the mix must not wait for that reading either.
        bool tamer = settings.Defaults.AutoSpikeFlattening
                     && (_applyPeakFlattening ?? settings.PeakFlatteningPrompt != Infrastructure.AudioFixPrompt.NeverApply);
        var music = _musicWizardResult;   // the export uses the result whenever it exists
        var vo = _voiceOverResult;
        string? legacyMeme = SelectedLegacyMemeFile();
        bool hasVo = vo != null && (vo.VoiceOverTakes.Count > 0 || !string.IsNullOrEmpty(vo.VoiceOverWavPath));
        var exportSegments = BuildExportSpeedSegments();
        // PREVIEWMIX_02 / TEMPO_01 — a non-1.0x rate is also something only the export graph reproduces.
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
          // The chosen cover frame changes pixels only; the intro's audio is always 0.1 s silence.
          // Setting/moving/removing that marker must not drop and reload an identical soundtrack.
          .Append('|').Append(this.FindControl<Avalonia.Controls.ToggleSwitch>("EnableFadeCheckbox")?.IsChecked)
          .Append('|').Append(_keepMusicDuringMeme)
          .Append('|').Append(tamer).Append(_gameplayLoudnessLufs?.ToString("F2", ci))
          .Append('|').Append(settings.Defaults.DuckingEnabled).Append(settings.Defaults.DuckingStrength)
          .Append(settings.Defaults.CarvingEnabled).Append(settings.Defaults.CarvingStrength);   // DUCKSTRENGTH_01
        if (music != null)
        {
            sb.Append("|M:").Append(music.MusicFilePath).Append(string.Join(",", music.MusicFilePaths))
              .Append(music.OffsetSeconds.ToString("F3", ci)).Append(',').Append(music.TimelineStartSeconds.ToString("F3", ci))
              .Append(',').Append(music.TimelineEndSeconds.ToString("F3", ci)).Append(',').Append(music.VideoVolume.ToString("F3", ci))
              .Append(',').Append(music.MusicVolume.ToString("F3", ci)).Append(music.EnableDucking).Append(music.EnableCarving)
              .Append(music.LoopMusic)
              .Append(',').Append(string.Join(";", (music.MusicDurationsSeconds ?? new()).Select(d => d.ToString("F3", ci))))
              .Append(',').Append(music.MusicDurationSeconds.ToString("F3", ci));
        }
        if (hasVo)
        {
            sb.Append("|V:").Append(vo!.DuckAudio).Append(vo.ProtectFromMusic).Append(vo.VoiceOverWavPath)
              .Append(vo.VoiceOverStartTimestampSec.ToString("F3", ci));
            foreach (var t in vo.VoiceOverTakes)
            {
                long len = 0;
                try { len = new FileInfo(t.Path).Length; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                sb.Append(';').Append(t.Path).Append('@').Append(t.StartSec.ToString("F3", ci)).Append('#').Append(len);
            }
        }
        return sb.ToString();
    }
}
