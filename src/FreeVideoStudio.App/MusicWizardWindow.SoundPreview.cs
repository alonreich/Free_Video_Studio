using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    private AudioPreviewMap? _mixMap;
    private string? _mixPath;
    private string? _mixSig;
    private CancellationTokenSource? _mixCts;
    private DispatcherTimer? _mixDebounce;
    private int _mixSlot;
    private bool _mixWasLive;

    private bool RenderedMixLive =>
        _mixMap != null && _mixPath != null && _currentStep == StepFit && Vm.IsReviewView &&
        _playlist.Count > 0 && string.Equals(_mixSig, CurrentSoundSignature(), StringComparison.Ordinal);

    internal static string SoundSignature(MusicWizardResult r)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(string.Join("|", r.OrderedPaths()))
          .Append("|S:").Append(string.Join(";", r.MusicStartsSeconds.Select(d => d.ToString("F3", ci))))
          .Append("|E:").Append(string.Join(";", r.MusicEndsSeconds.Select(d => d.ToString("F3", ci))))
          .Append("|D:").Append(string.Join(";", r.MusicDurationsSeconds.Select(d => d.ToString("F3", ci))))
          .Append('|').Append(r.TimelineStartSeconds.ToString("F3", ci)).Append(',').Append(r.TimelineEndSeconds.ToString("F3", ci))
          .Append('|').Append(r.VideoVolume.ToString("F3", ci)).Append(',').Append(r.MusicVolume.ToString("F3", ci))
          .Append('|').Append(r.EnableDucking).Append(r.EnableCarving).Append(r.LoopMusic);
        return sb.ToString();
    }

    private string? CurrentSoundSignature() => _playlist.Count == 0 ? null : SoundSignature(BuildResultFromPlaylist());

    private void QueueSoundPreviewRender()
    {
        if (RenderSoundPreview == null)
        {
            Vm.SoundPreviewStatus = "In this window the two switches are applied when you save; the volume levels are heard now.";
            return;
        }
        if (_currentStep != StepFit || !Vm.IsReviewView || _playlist.Count == 0) return;
        _mixDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _mixDebounce.Stop();
        _mixDebounce.Tick -= OnMixDebounceTick;
        _mixDebounce.Tick += OnMixDebounceTick;
        _mixDebounce.Start();
    }

    private void OnMixDebounceTick(object? sender, EventArgs e)
    {
        _mixDebounce?.Stop();
        _ = RenderSoundPreviewNowAsync();
    }

    private async Task RenderSoundPreviewNowAsync()
    {
        var render = RenderSoundPreview;
        if (render == null || _currentStep != StepFit || !Vm.IsReviewView || _playlist.Count == 0) return;
        string? sig = CurrentSoundSignature();
        if (sig == null || (sig == _mixSig && _mixMap != null)) return;
        try { _mixCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        var cts = new CancellationTokenSource();
        _mixCts = cts;
        _mixSlot ^= 1;
        string wav = Path.Combine(_paths.TempDirectory, $"fvs_wizard_mix_{Environment.ProcessId}_{_mixSlot}.wav");
        if (string.Equals(wav, _mixPath, StringComparison.OrdinalIgnoreCase))
        {
            _mixMap = null;
            _mixPath = null;
            _mixSig = null;
            ResyncPreviewMusic();
        }
        Vm.SoundPreviewStatus = "Updating the sound…";
        AudioPreviewMap? map = null;
        try
        {
            Directory.CreateDirectory(_paths.TempDirectory);
            map = await render(BuildResultFromPlaylist(), wav, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MUSIC_WIZARD", $"Sound preview could not be rendered: {ex.Message}");
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(_mixCts, cts)) return;
        if (map == null || !File.Exists(wav))
        {
            Vm.SoundPreviewStatus = "The finished sound could not be prepared; you hear the volume levels only.";
            return;
        }
        if (CurrentSoundSignature() != sig) return;
        _mixMap = map;
        _mixPath = wav;
        _mixSig = sig;
        Vm.SoundPreviewStatus = "You are hearing the finished sound, as it will be saved.";
        RuntimeLog.Info("MUSIC_WIZARD", "Rendered sound preview is live (the export's own audio graph).");
        ResyncPreviewMusic();
        TickSoundPreview();
    }

    private void TickSoundPreview()
    {
        bool live = RenderedMixLive;
        if (live == _mixWasLive) return;
        _mixWasLive = live;
        if (!live && _mixSig != null && Vm.IsReviewView) QueueSoundPreviewRender();
        if (WizardVideoHost?.IpcClient is { } video) _ = video.ApplyPreviewGainAsync(GetPreviewVideoBalance());
        _freezeSound.SetGameBalance(GetPreviewVideoBalance());
        ResyncPreviewMusic();
    }

    private void OnSoundSettingChanged()
    {
        TickSoundPreview();
        QueueSoundPreviewRender();
    }

    private async Task<bool> SyncRenderedMixAsync(double outputRelativeSec)
    {
        if (!RenderedMixLive || _audioIpcClient == null || _mixMap == null || _mixPath == null) return false;
        double want = Math.Max(0, _mixMap.MixSecFor(outputRelativeSec));
        string target = _mixPath.Replace("\\", "/");
        if (!string.Equals(_phase3PreviewMusicPath, target, StringComparison.OrdinalIgnoreCase))
        {
            await _audioIpcClient.SetPropertyAsync("start", want.ToString(CultureInfo.InvariantCulture));
            await _audioIpcClient.SendCommandAsync("loadfile", target, "replace");
            _phase3PreviewMusicPath = target;
            _lastLoadedTrackPath = target;
            _phase3PreviewMusicSegmentStartSec = 0;
        }
        else if (Math.Abs(_audioIpcClient.CurrentTime - want) > Infrastructure.PreviewAudioSync.DriftToleranceSec * 2)
        {
            await _audioIpcClient.SendCommandAsync("seek", want.ToString(CultureInfo.InvariantCulture), "absolute");
        }
        await _audioIpcClient.ApplyPreviewGainAsync(1.0);
        await _audioIpcClient.SetPropertyAsync("af", "");
        if (!PreviewStillWantsMusic()) { await _audioIpcClient.SetPropertyAsync("pause", "yes"); return true; }
        await _audioIpcClient.SetPropertyAsync("pause", "no");
        return true;
    }

    private void DisposeSoundPreview()
    {
        try { _mixCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mixDebounce?.Stop();
        _mixMap = null;
        _mixSig = null;
        _mixPath = null;
        for (int slot = 0; slot < 2; slot++)
        {
            string wav = Path.Combine(_paths.TempDirectory, $"fvs_wizard_mix_{Environment.ProcessId}_{slot}.wav");
            try { if (File.Exists(wav)) File.Delete(wav); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }
}
