using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    private CancellationTokenSource? _aiAutoFitCts;

    /// <summary>The status line under the toolbar — the AI button's sibling in the same grid (found once, no name lookup).</summary>
    private TextBlock? _aiAutoFitStatusText;

    /// <summary>Cancels any in-flight AI Auto-Fit background tasks and extractions.</summary>
    public void CancelAiAutoFit()
    {
        try
        {
            _aiAutoFitCts?.Cancel();
            SetAiAutoFitStatus("Stopping…");
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private void OnAiAutoFitClicked(object? sender, RoutedEventArgs e)
    {
        if (_aiAutoFitCts != null)
        {
            CancelAiAutoFit();
            return;
        }
        if (sender is Button button)
        {
            _aiAutoFitStatusText ??= (button.Parent as Panel)?.Children.OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "AiAutoFitStatusText");
            _ = ApplyAiAutoFitAsync(button);
        }
    }

    /// <summary>AIMUSIC_PROGRESS_01 — the persistent line under the toolbar (a toast vanishes; this stays).</summary>
    private void SetAiAutoFitStatus(string? text, string brushKey = "AppTextMutedBrush")
    {
        if (_aiAutoFitStatusText is not { } status) return;
        status.IsVisible = !string.IsNullOrWhiteSpace(text);
        status.Text = text ?? string.Empty;
        status.Foreground = ThemeResources.Brush(this, brushKey, status.Foreground ?? Avalonia.Media.Brushes.Gray);
    }

    /// <summary>
    /// AI_AUTOFIT_01 / AIMUSIC_PROGRESS_01 / AIMUSIC_HONEST_01 — Auto-Fit with a visible, honest run:
    /// <list type="bullet">
    /// <item>A status line names each step (pictures → AI → song analysis) with a live seconds counter, so the
    /// user always sees that something is happening; the button reads "✖ Cancel AI (12 s)" and cancels.</item>
    /// <item>Hard limits: 45 s per AI request, one retry, 120 s for the whole run. It used to wait up to four
    /// 180-second attempts (~12 minutes) behind a frozen "✨ Analyzing..." label.</item>
    /// <item>Every ending is reported in that line: the pick and why, "cancelled", "took too long", or the
    /// AI's own error. Nothing is changed unless a real song from the list was chosen.</item>
    /// </list>
    /// Invariant: zero local AI models (cloud vision + local FFmpeg maths only).
    /// </summary>
    private async Task ApplyAiAutoFitAsync(Button button)
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
        {
            SetAiAutoFitStatus("⚠ No video is loaded to analyze.", "AppWarningBrush");
            return;
        }

        if (AvailableTracks.Count == 0)
        {
            SetAiAutoFitStatus("⚠ No songs found. Add songs or click 'Get more songs' first.", "AppWarningBrush");
            return;
        }

        var settings = SettingsManager.Instance;
        var usageConfig = settings.GetUsageConfig(AiUsage.MusicAutoFit);
        var vendor = usageConfig.Vendor;

        if (!await Controls.AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, vendor, AiUsage.MusicAutoFit))
        {
            SetAiAutoFitStatus("AI Auto-Fit needs an AI key. Add one in Settings › AI.", "AppWarningBrush");
            return;
        }

        settings = SettingsManager.Instance;
        usageConfig = settings.GetUsageConfig(AiUsage.MusicAutoFit);
        vendor = usageConfig.Vendor;
        string apiKey = settings.GetApiKey(vendor);
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        string model = settings.GetEffectiveModel(AiUsage.MusicAutoFit);
        string vendorName = AiVisionProviders.DisplayName(vendor);

        string originalContent = button.Content?.ToString() ?? "✨ AI Auto-Fit";
        using var usage = AiUsageMeter.BeginTask("AI Music Auto-Fit", vendor);
        using var cts = new CancellationTokenSource(AiMusicAutoFitService.OverallDeadline);
        _aiAutoFitCts = cts;
        var started = DateTime.UtcNow;
        AiEstimatedUsage? est = null;
        bool suggestionApplied = false;
        string estBanner = string.Empty;
        string step = $"Preparing scenes for {vendorName} ({model})…";
        void Refresh()
        {
            int secs = (int)(DateTime.UtcNow - started).TotalSeconds;
            button.Content = $"✖ Cancel AI ({secs} s)";
            SetAiAutoFitStatus(string.IsNullOrEmpty(estBanner)
                ? $"🤖 {step}  ·  {secs} s"
                : $"🤖 {step}  ·  {secs} s  ·  {estBanner}");
        }
        string ReportUsageNotice()
        {
            if (usage.Tally.TotalTokens <= 0) return string.Empty;
            string line = usage.SummaryWithEstimate(est);
            FloatingNotice.Info(this, line);
            return line;
        }
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => Refresh();
        ticker.Start();
        Refresh();
        RuntimeLog.Info("MUSIC_WIZARD", $"AI Auto-Fit started ({vendorName}, model {model}, {AvailableTracks.Count} songs).");

        try
        {
            string ffmpegPath = ResolveFfmpegPath();
            step = "Step 1 of 3 — finding scenes across your finished video timeline…";
            Refresh();
            var (scenes, outputDurationSec) = await BuildAiMusicScenesAsync(ffmpegPath, cts.Token);
            est = AiCostEstimator.EstimateMusicAutoFit(vendor, model, Math.Max(1, scenes.Count), Math.Min(AvailableTracks.Count, AiMusicAutoFitService.MaxSongsInPrompt));
            estBanner = AiCostEstimator.FormatPreRunBanner(est, vendor, model);
            step = $"Step 1 of 3 — preparing {scenes.Count} scene frames for {vendorName} ({model})…";
            Refresh();
            var recommendation = await Task.Run(() => AiMusicAutoFitService.AutoFitMusicAsync(
                ffmpegPath,
                scenes,
                outputDurationSec,
                AvailableTracks.ToList(),
                vendor,
                apiKey,
                model,
                status =>
                {
                    RuntimeLog.Info("MUSIC_WIZARD", "AI Auto-Fit: " + status);
                    Dispatcher.UIThread.Post(() => { step = status; Refresh(); });
                },
                cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            if (recommendation == null)
            {
                ReportUsageNotice();
                SetAiAutoFitStatus($"⚠ {vendorName} could not recommend a song for this video. Nothing was changed.", "AppWarningBrush");
                return;
            }

            var track = AvailableTracks.FirstOrDefault(t => string.Equals(t.FilePath, recommendation.TrackFilePath, StringComparison.OrdinalIgnoreCase));
            if (track == null)
            {
                ReportUsageNotice();
                SetAiAutoFitStatus("⚠ The chosen song is no longer in the list. Nothing was changed.", "AppWarningBrush");
                return;
            }

            _highlightedTrack = track;
            var listbox = MusicListBoxCtl;
            if (listbox != null)
            {
                listbox.SelectedItem = track;
                listbox.ScrollIntoView(track);
            }

            bool useSong = await ConfirmDialogWindow.AskAsync(this,
                $"Use '{Path.GetFileNameWithoutExtension(track.FilePath)}'?\n\n{recommendation.Reason}",
                "Suggested song", "Use song", "Keep my songs");
            cts.Token.ThrowIfCancellationRequested();
            if (!useSong) { SetAiAutoFitStatus("Your songs were kept."); return; }
            _playlist.Clear();
            double full = track.DurationSec > 0 ? track.DurationSec : recommendation.OutSec;
            var entry = new MusicPlaylistEntry(track.FilePath, full, recommendation.InSec, recommendation.OutSec, UserTrimmed: true);
            _playlist.Add(entry);
            _selectedSongIndex = 0;
            _songStartSeconds = recommendation.InSec;
            suggestionApplied = true;

            StopPreview();

            await EnsurePlaylistDurationsAsync();
            cts.Token.ThrowIfCancellationRequested();
            if (_playlist[0].FullSec > 0) _trackDuration = _playlist[0].FullSec;
            _lastConfiguredTrackPath = _playlist[0].Path;
            QueueSilenceAnalysis();
            EndWizardGesture();

            CancelPhase3Load();
            _phase3Ready = false;
            _previewCurrentOffset = _songStartSeconds;
            _currentStep = StepFit;
            _visitedFit = true;
            OnPlaylistChanged();
            UpdateStepVisibility();
            UpdateNextButtonState();

            _phase3LoadCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            int loadVersion = ++_phase3LoadVersion;
            await LoadPhase3DataAsync(_phase3LoadCts.Token, loadVersion);
            cts.Token.ThrowIfCancellationRequested();

            string songName = Path.GetFileNameWithoutExtension(recommendation.TrackFilePath);
            string usageSummary = ReportUsageNotice();
            string done = $"✨ {vendorName} ({model}) picked '{songName}'. {recommendation.Reason}"
                + (string.IsNullOrEmpty(usageSummary) ? "" : $" [{usageSummary}]")
                + " (Ctrl+Z puts your previous songs back.)";
            SetSmartFitStatus(done);
            SetAiAutoFitStatus(done, "AppSuccessBrush");
            RuntimeLog.Info("MUSIC_WIZARD", $"AI Auto-Fit picked '{songName}' at {recommendation.InSec:0.0}s.");
            ShowToastSuccess($"✨ AI picked '{songName}'");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ReportUsageNotice();
            bool timedOut = DateTime.UtcNow - started >= AiMusicAutoFitService.OverallDeadline - TimeSpan.FromSeconds(1);
            string text = timedOut
                ? $"⚠ {vendorName} took longer than {(int)AiMusicAutoFitService.OverallDeadline.TotalSeconds} seconds, so AI Auto-Fit stopped. Nothing was changed — try again in a moment."
                : "AI Auto-Fit cancelled. Nothing was changed.";
            if (suggestionApplied) text = "Preview loading stopped. The song you approved is kept; Ctrl+Z restores your previous songs.";
            SetAiAutoFitStatus(text, timedOut ? "AppWarningBrush" : "AppTextMutedBrush");
            RuntimeLog.Info("MUSIC_WIZARD", timedOut ? "AI Auto-Fit timed out." : "AI Auto-Fit cancelled by the user.");
        }
        catch (AiMusicAutoFitException ex)
        {
            ReportUsageNotice();
            SetAiAutoFitStatus("⚠ " + ex.Message, "AppWarningBrush");
            RuntimeLog.Info("MUSIC_WIZARD", "AI Auto-Fit: " + ex.Message);
        }
        catch (AiProviderException ex)
        {
            ReportUsageNotice();
            SetAiAutoFitStatus($"⚠ AI Auto-Fit failed: {ex.Message} (Reference: {AiUsageMeter.LastTaskId})", "AppDangerBrush");
            RuntimeLog.Fail("MUSIC_WIZARD", GeminiClient.Redact($"AI Auto-Fit failed: {ex.Kind} {ex.Message} {ex.Detail}", apiKey));
        }
        catch (Exception ex)
        {
            ReportUsageNotice();
            RuntimeLog.Fail("MUSIC_WIZARD", GeminiClient.Redact($"AI Auto-Fit failed: {ex.Message}", apiKey));
            SetAiAutoFitStatus($"⚠ AI Auto-Fit failed: {ex.Message}", "AppDangerBrush");
        }
        finally
        {
            ticker.Stop();
            if (ReferenceEquals(_aiAutoFitCts, cts)) _aiAutoFitCts = null;
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// AIMUSIC_SCENES_01 — the frames Auto-Fit sends and the finished-video length.
    /// <list type="bullet">
    /// <item>Main App: 4 moments of the FINISHED video (12 / 35 / 60 / 85 %), each mapped back to the source
    /// second that shows then (speed changes, freezes and cuts honoured).</item>
    /// <item>Merger: when a CompositeTimeline is present, maps through speed ramps, freezes and cuts per clip;
    /// otherwise spreads frames over every clip's kept window in proportion to length (4–8 frames).</item>
    /// </list>
    /// </summary>
    private async Task<(System.Collections.Generic.List<AiMusicScene> Scenes, double OutputSec)> BuildAiMusicScenesAsync(
        string ffmpegPath,
        CancellationToken ct = default)
    {
        var scenes = new System.Collections.Generic.List<AiMusicScene>();
        double outputSec = GetPhase3VideoDurationSeconds();

        if (!_isMergerMode || _mergerVideos == null || _mergerVideos.Count == 0)
        {
            foreach (double f in new[] { 0.12, 0.35, 0.60, 0.85 })
            {
                ct.ThrowIfCancellationRequested();
                double outT = outputSec * f;
                double src = _trimStartMs / 1000.0 + MapPhase3OutputToSourceRelativeSeconds(outT);
                scenes.Add(new AiMusicScene(_videoPath, src, outT));
            }
            return (scenes, outputSec);
        }

        if (MergerComposite is { Clips.Count: > 0 } comp)
        {
            var active = comp.Clips.Where(c => c.OutputLengthSec > 0.05 && File.Exists(c.Path)).ToList();
            if (active.Count > 0)
            {
                double totalCompOut = active.Sum(c => c.OutputLengthSec);
                int frames = Math.Clamp(active.Count, 4, 8);
                var chosen = active.Count <= frames
                    ? Enumerable.Range(0, active.Count).ToList()
                    : Enumerable.Range(0, frames).Select(k => (int)Math.Round(k * (active.Count - 1) / (double)(frames - 1))).Distinct().ToList();

                for (int i = 0; i < active.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!chosen.Contains(i)) continue;
                    var cc = active[i];
                    int k = active.Count <= frames ? Math.Max(1, (int)Math.Round(frames * cc.OutputLengthSec / Math.Max(0.1, totalCompOut))) : 1;
                    double startSrcSec = cc.KeepInUs / 1_000_000.0;
                    for (int j = 0; j < k; j++)
                    {
                        double relOut = cc.OutputLengthSec * (j + 0.5) / k;
                        double relSrc = cc.Output.OutputToSourceRelative(relOut);
                        scenes.Add(new AiMusicScene(cc.Path, startSrcSec + relSrc, cc.OutputStartSec + relOut));
                    }
                }
                return (scenes, comp.TotalOutputSec > 0.1 ? comp.TotalOutputSec : outputSec);
            }
        }

        double speed = Math.Max(0.001, _phase3BaseSpeed);
        var clips = new System.Collections.Generic.List<(string Path, double Start, double Len)>();
        string ffprobe = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
        for (int i = 0; i < _mergerVideos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            double start = MergerWindowStart(i);
            double len = MergerWindowDuration(i) ?? 0;
            if (len <= 0)
            {
                try { len = await new FreeVideoStudio.Core.Media.MediaProber(ffprobe, _mergerVideos[i]).GetDurationAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); len = 0; }
            }
            if (len > 0.05) clips.Add((_mergerVideos[i], start, len));
        }
        if (clips.Count == 0) return (scenes, outputSec);

        double total = clips.Sum(c => c.Len);
        int flatFrames = Math.Clamp(clips.Count, 4, 8);
        var pick = clips.Count <= flatFrames
            ? Enumerable.Range(0, clips.Count).ToList()
            : Enumerable.Range(0, flatFrames).Select(k => (int)Math.Round(k * (clips.Count - 1) / (double)(flatFrames - 1))).Distinct().ToList();

        double outCursor = 0;
        for (int i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            if (pick.Contains(i))
            {
                int k = clips.Count <= flatFrames ? Math.Max(1, (int)Math.Round(flatFrames * c.Len / total)) : 1;
                for (int j = 0; j < k; j++)
                {
                    double pos = c.Len * (j + 0.5) / k;
                    scenes.Add(new AiMusicScene(c.Path, c.Start + pos, outCursor + pos / speed));
                }
            }
            outCursor += c.Len / speed;
        }
        return (scenes, outputSec > 0.1 ? outputSec : outCursor);
    }
}
