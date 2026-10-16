// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md (#UI-MUSICWIZARD)
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md (#SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    private void OnAiAutoFitClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            _ = ApplyAiAutoFitAsync(button);
        }
    }

    /// <summary>
    /// AI_AUTOFIT_01 — Cloud AI vision analyzes video scene keyframes, matches pacing, mood, and genre
    /// against available songs in the library, and automatically sets the recommended song and timing offset.
    /// Invariant: Zero local AI models run locally on the user machine (pure cloud vision + local FFmpeg math).
    /// </summary>
    private async Task ApplyAiAutoFitAsync(Button button)
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
        {
            ShowToast("⚠ No video is loaded to analyze.");
            return;
        }

        if (AvailableTracks.Count == 0)
        {
            ShowToast("⚠ No songs found. Add songs or click 'Get more songs' first.");
            return;
        }

        var settings = SettingsManager.Instance;
        var usageConfig = settings.GetUsageConfig(AiUsage.MusicAutoFit);
        var vendor = usageConfig.Vendor;

        if (!await Controls.AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, vendor))
            return;

        string apiKey = settings.GetApiKey(vendor);
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        string model = settings.GetEffectiveModel(AiUsage.MusicAutoFit);

        button.IsEnabled = false;
        string originalContent = button.Content?.ToString() ?? "✨ AI Auto-Fit";
        button.Content = "✨ Analyzing...";
        ShowToast("🤖 AI is analyzing video scenes and selecting the best background track...");

        try
        {
            double duration = GetPhase3SourceDurationSeconds();
            if (duration <= 0.001)
            {
                duration = (_trimEndMs > _trimStartMs) ? (_trimEndMs - _trimStartMs) / 1000.0 : 30.0;
            }

            string ffmpegPath = ResolveFfmpegPath();
            var recommendation = await AiMusicAutoFitService.AutoFitMusicAsync(
                ffmpegPath,
                _videoPath,
                _trimStartMs / 1000.0,
                duration,
                AvailableTracks.ToList(),
                vendor,
                apiKey,
                model,
                status => Dispatcher.UIThread.Post(() => ShowToast($"🤖 {status}")));

            if (recommendation == null)
            {
                ShowToast("⚠ AI could not recommend a track for this video.");
                return;
            }

            var track = AvailableTracks.FirstOrDefault(t => string.Equals(t.FilePath, recommendation.TrackFilePath, StringComparison.OrdinalIgnoreCase))
                     ?? AvailableTracks.FirstOrDefault();

            if (track != null)
            {
                _highlightedTrack = track;
                var listbox = MusicListBoxCtl;
                if (listbox != null)
                {
                    listbox.SelectedItem = track;
                    listbox.ScrollIntoView(track);
                }

                _playlist.Clear();
                double full = track.DurationSec > 0 ? track.DurationSec : recommendation.OutSec;
                var entry = new MusicPlaylistEntry(track.FilePath, full, recommendation.InSec, recommendation.OutSec, UserTrimmed: true);
                _playlist.Add(entry);
                _selectedSongIndex = 0;
                _songStartSeconds = recommendation.InSec;

                StopPreview();

                await EnsurePlaylistDurationsAsync();
                if (_playlist[0].FullSec > 0) _trackDuration = _playlist[0].FullSec;
                _lastConfiguredTrackPath = _playlist[0].Path;
                QueueSilenceAnalysis();
                SyncWizardHistoryBaseline();

                CancelPhase3Load();
                _phase3Ready = false;
                _previewCurrentOffset = _songStartSeconds;
                _currentStep = StepFit;
                _visitedFit = true;
                OnPlaylistChanged();
                UpdateStepVisibility();
                UpdateNextButtonState();

                SetSmartFitStatus($"AI matched '{Path.GetFileName(recommendation.TrackFilePath)}': {recommendation.Reason}");

                _phase3LoadCts = new CancellationTokenSource();
                int loadVersion = ++_phase3LoadVersion;
                await LoadPhase3DataAsync(_phase3LoadCts.Token, loadVersion);

                ShowToast($"✨ AI selected '{Path.GetFileName(recommendation.TrackFilePath)}'! {recommendation.Reason}");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"AI Auto-Fit failed: {ex.Message}");
            ShowToast($"⚠ AI Auto-Fit failed: {ex.Message}");
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }
}
