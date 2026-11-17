using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using SourceRect = FreeVideoStudio.Core.Editing.CropSourceRect;
using HudRole = FreeVideoStudio.Core.Editing.CropHudRole;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App;

public partial class CropToolWindow
{
    private List<CandidateSpec>? _wandCandidates;

    private List<HudCandidate>? _wandLocalCandidates;

    private string? _wandCandidatesFrame;

    private int _wandPreviewIndex = -1;

    private bool _wandRunning;

    private bool _wandStopRequested;

    private bool _wandSnapshotShown;

    private const string WandSetupTag = "WANDSETUP_01";

    private bool _wandAiPending;

    private static bool s_wandCloudDeclinedThisSession;

    private HudDetectionCoordinator? _wandDetection;

    private HudDetectionCoordinator WandDetection => _wandDetection ??= new HudDetectionCoordinator(
        new GeminiHudDetectionService(
            () => SettingsManager.Instance.GetApiKey(SettingsManager.Instance.GetUsageConfig(AiUsage.MagicWand).Vendor),
            () => SettingsManager.Instance.GetEffectiveModel(AiUsage.MagicWand),
            Faults.Sink,
            vendorProvider: () => SettingsManager.Instance.GetUsageConfig(AiUsage.MagicWand).Vendor),
        Faults.Sink);

    private void CancelMagicWand() => _wandDetection?.Cancel();

    private void DisposeMagicWand()
    {
        _wandDetection?.Dispose();
        _wandDetection = null;
    }

    private void ResetMagicWandForNewClip()
    {
        CancelMagicWand();
        _wandCandidates = null;
        _wandLocalCandidates = null;
        _wandCandidatesFrame = null;
        _wandPreviewIndex = -1;
    }

    private string? CurrentWandFrameIdentity()
    {
        if (_snapshotPath == null) return null;
        try
        {
            return _snapshotPath + "|" + File.GetLastWriteTimeUtc(_snapshotPath).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
            return _snapshotPath;
        }
    }

    private bool WandAiAvailable()
        => !s_wandCloudDeclinedThisSession && WandDetection.AiService is { IsConfigured: true };

    private Task<bool> ConfirmWandCloudConsentAsync()
    {
        var wandSettings = SettingsManager.Instance;
        var cfg = wandSettings.GetUsageConfig(AiUsage.MagicWand);
        if (!AiAccountFix.IsUsable(wandSettings, cfg.Vendor))
            return Task.FromResult(false);

        if (!AppSettings.ConsentCovers(wandSettings.AiMagicWandCloudConsent, wandSettings.AiMagicWandCloudConsentVendor, cfg.Vendor))
        {
            SettingsManager.Update(s =>
            {
                s.AiMagicWandCloudConsent = true;
                s.AiMagicWandCloudConsentVendor = cfg.Vendor;
            });
        }
        return Task.FromResult(true);
    }

    internal void UpdateMagicWandAvailabilityUi()
    {
        var s = SettingsManager.Instance;
        var cfg = s.GetUsageConfig(AiUsage.MagicWand);
        bool hasKey = AiAccountFix.IsUsable(s, cfg.Vendor);
        bool broken = AiAccountFix.IsBroken(s, cfg.Vendor);
        string providerName = AiVisionProviders.DisplayName(cfg.Vendor);
        SyncWandFixButton();
        if (!_wandRunning)
        {
            SetStatus(hasKey
                ? $"Magic Wand ready, with extra suggestions from {providerName}. Press MAGIC WAND to find the HUD pieces."
                : broken && s.AiMode != AiMode.LocalOnly
                    ? $"Magic Wand ready (PC finds only): your {providerName} key does not work. Press Fix AI key… to connect it again."
                : s.AiMode == AiMode.LocalOnly
                    ? "Magic Wand ready. Press MAGIC WAND to find the HUD pieces."
                    : "Magic Wand ready. Press MAGIC WAND to find the HUD pieces. (Press Add AI suggestions… for extra ideas from an AI.)");
        }
    }

    private void SyncWandFixButton()
    {
        var s = SettingsManager.Instance;
        var cfg = s.GetUsageConfig(AiUsage.MagicWand);
        bool broken = AiAccountFix.IsBroken(s, cfg.Vendor);
        SetVisible("WandAddAiButton", _wandSnapshotShown && !AiAccountFix.IsUsable(s, cfg.Vendor) && s.AiMode != AiMode.LocalOnly);
        SetContent("WandAddAiButton", broken ? "Fix AI key…" : "Add AI suggestions…");
    }

    private async Task RunMagicWandAsync()
    {
        if (_wandAiPending)
        {
            RuntimeLog.Info("CROP", "User stopped the pending AI suggestions; offline finds stay on screen.");
            _wandStopRequested = true;
            CancelMagicWand();
            return;
        }
        if (_wandRunning) return;

        if (_snapshotPath == null || _sourceCanvas == null)
        {
            SetStatus("Freeze a frame first — press START CROPPING.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
        {
            SetStatus("Open a video first.");
            return;
        }

        string? frameIdentity = CurrentWandFrameIdentity();
        bool aiAvailable = WandAiAvailable();

        if (_wandCandidates != null && _wandCandidatesFrame != null && _wandCandidatesFrame != frameIdentity && !aiAvailable)
        {
            _wandCandidates = _wandLocalCandidates is { } localOnly ? ToCandidateSpecs(localOnly) : null;
            _wandCandidatesFrame = null;
            _wandPreviewIndex = -1;
        }

        bool cacheCoversFrame = !aiAvailable || _wandCandidatesFrame == frameIdentity;
        if (_wandCandidates is { Count: > 0 } cached && cacheCoversFrame)
        {
            if (_candidateControls.Count == 0 && _wandPreviewIndex < 0)
            {
                ClearSourceSelection();
                ShowMagicWandCandidates();
                SetWizardState(3, "Refine Box", $"{cached.Count} pieces found. Click one to label it.");
                return;
            }

            StepMagicWandPreview();
            return;
        }

        var wandSettings = SettingsManager.Instance;
        var wandCfg = wandSettings.GetUsageConfig(AiUsage.MagicWand);
        string wandModel = wandSettings.GetEffectiveModel(AiUsage.MagicWand);
        string wandProviderName = AiVisionProviders.DisplayName(wandCfg.Vendor);
        AiEstimatedUsage? wandEstimate = null;

        _wandRunning = true;
        _wandStopRequested = false;
        SetEnabled("MagicWandButton", false);

        using var usage = AiUsageMeter.BeginTask("Magic Wand", wandCfg.Vendor);

        try
        {
            bool useAi = aiAvailable && await ConfirmWandCloudConsentAsync();
            if (useAi)
            {
                wandEstimate = AiCostEstimator.EstimateMagicWand(wandCfg.Vendor, wandModel);
                usage.SetEstimate(wandEstimate);
                _wandActiveAiLabel = wandProviderName;
                _wandActiveEstimateBanner = AiCostEstimator.PlainCostShort(wandEstimate, wandCfg.Vendor, wandModel);
            }
            else
            {
                _wandActiveAiLabel = null;
                _wandActiveEstimateBanner = null;
            }

            SetContent("MagicWandButton", "ANALYSING…");
            SetWizardState(3, "Refine Box", useAi
                ? $"Step 1: Looking for HUD pieces on this PC, then asking {_wandActiveAiLabel} ({_wandActiveEstimateBanner})…"
                : "Looking for HUD pieces on this PC…");

            ShowWandOverlay();

            string ffmpeg = ResolveBinaryPath("ffmpeg.exe", "backend");
            string video = _videoPath!;
            string snapshot = _snapshotPath!;
            int sourceW = _edit.SnapshotWidth;
            int sourceH = _edit.SnapshotHeight;
            double totalMs = _durationMs;

            var wandProgress = new Progress<HudAutoDetector.DetectionProgress>(ReportWandProgress);

            List<HudCandidate>? cachedLocal = _wandLocalCandidates;
            Func<CancellationToken, Task<IReadOnlyList<HudCandidate>>> localDetect = cachedLocal != null
                ? _ => Task.FromResult<IReadOnlyList<HudCandidate>>(cachedLocal)
                : async token =>
                {
                    IReadOnlyList<HudAutoDetector.DetectionRect> found = await Task.Run(
                        () => HudAutoDetector.DetectAsync(ffmpeg, video, sourceW, sourceH, totalMs, token, wandProgress),
                        token).ConfigureAwait(false);
                    return found
                        .Select(r => new HudCandidate(r.X, r.Y, r.Width, r.Height, r.RoleKey, string.Empty, 0.0, HudCandidateSource.Local))
                        .ToList();
                };

            AiHudFrame? frame = null;
            if (useAi)
            {
                try
                {
                    frame = await Task.Run(() => AiHudFrameBuilder.FromSnapshotFile(snapshot, sourceW, sourceH, video));
                }
                catch (Exception ex)
                {
                    Faults.Degraded("CROP", "The frozen frame could not be prepared for AI suggestions. The offline Magic Wand still runs.", ex);
                }
            }

            var localReady = new Progress<HudLocalPreview>(preview => OnWandLocalReady(preview, video));
            HudDetectionResult? result = await WandDetection.RunAsync(
                new HudDetectionRequest(localDetect, frame, sourceW, sourceH), wandProgress, localReady);

            if (result == null || _wandDetection == null || !_wandDetection.IsCurrent(result.Generation)
             || _sourceCanvas == null || _snapshotPath == null || !string.Equals(_videoPath, video, StringComparison.Ordinal))
            {
                if (_wandAiPending && _sourceCanvas != null && _wandCandidates is { Count: > 0 } shown)
                    SetWizardState(3, "Refine Box", $"AI suggestions stopped. The {shown.Count} offline find{(shown.Count == 1 ? " is" : "s are")} still shown.");
                else if (_wandStopRequested && _sourceCanvas != null)
                    SetWizardState(3, "Refine Box", "Magic Wand stopped. Drag a box round a HUD piece yourself.");
                return;
            }

            bool userIsReviewing = _wandAiPending && (_sourceSelection != null || _candidateControls.Count == 0 || _wandPreviewIndex >= 0);

            if (result.LocalSucceeded) _wandLocalCandidates = result.LocalCandidates.ToList();

            List<CandidateSpec> candidates = ToCandidateSpecs(result.Candidates);
            if (candidates.Count == 0)
            {
                string why = result.LocalTimedOut
                    ? "The Magic Wand ran out of time on this clip. Drag a box round a HUD piece yourself."
                    : !result.LocalSucceeded
                        ? "The Magic Wand could not read this clip. Drag a box yourself."
                        : "The Magic Wand could not find anything it was sure about. Drag a box round a HUD piece yourself.";
                RuntimeLog.Info("CROP", $"Magic Wand found nothing (local ok={result.LocalSucceeded}, ai={result.AiOutcome?.ToString() ?? "off"}).");
                if (result.AiOutcome is { } none && none != AiHudOutcome.Success && none != AiHudOutcome.NotConfigured)
                    why += $" (AI suggestions failed. Reference: {usage.Id})";
                SetWizardState(3, "Refine Box", why);
                return;
            }

            _wandCandidates = candidates;
            _wandCandidatesFrame = frame != null ? frameIdentity : null;
            if (userIsReviewing)
            {
                if (result.AiOutcome is { } reviewOutcome && reviewOutcome != AiHudOutcome.Success && reviewOutcome != AiHudOutcome.NotConfigured)
                {
                    SetStatus($"AI suggestions failed; the PC finds stay on screen. {usage.PlainSummary()} (Reference: {usage.Id})");
                    return;
                }
                int added = candidates.Count(c => c.Source != HudCandidateSource.Local);
                ClearMagicWandCandidates();
                _wandPreviewIndex = -1;
                string prov = WandDetection.AiService?.ProviderName ?? "The AI";
                SetStatusSuccess(added > 0
                    ? $"{prov} added {added} suggestion{(added == 1 ? "" : "s")}. Press MAGIC WAND to see all {candidates.Count} pieces."
                    : $"{prov} had nothing to add. Press MAGIC WAND to see all pieces again.");
                return;
            }
            _wandPreviewIndex = -1;
            ShowMagicWandCandidates();

            int aiOnly = candidates.Count(c => c.Source == HudCandidateSource.Ai);
            int confirmed = candidates.Count(c => c.Source == HudCandidateSource.Fused);
            RuntimeLog.Info("CROP", $"Magic Wand found {candidates.Count} candidate region(s) (AI outcome {result.AiOutcome?.ToString() ?? "off"}, AI-only {aiOnly}, AI-confirmed {confirmed}).");

            string shortProv = _wandActiveAiLabel ?? WandDetection.AiService?.ProviderName ?? "AI";
            string aiNote = result.AiOutcome == AiHudOutcome.Success && (aiOnly + confirmed) > 0
                ? $" {shortProv} agreed on {confirmed} (✓) and suggested {aiOnly} more (dashed)."
                : string.Empty;
            bool aiFailed = result.AiOutcome is { } outcome && outcome != AiHudOutcome.Success && outcome != AiHudOutcome.NotConfigured;
            string usageNote = usage.Tally.Requests > 0 || aiFailed ? " " + usage.PlainSummary() : string.Empty;
            if (aiFailed) usageNote += $" (AI suggestions failed. Reference: {usage.Id})";
            SetStatusSuccess($"Found {candidates.Count} HUD piece{(candidates.Count == 1 ? "" : "s")}.{aiNote}{usageNote} " +
                             "Click a box to select it, adjust it, then label it — nothing is added until you do.");
        }
        catch (OperationCanceledException swallowed2)
        {
            SetWizardState(3, "Refine Box", "Magic Wand stopped. Drag a box round a HUD piece yourself.");
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("CROP", $"Magic Wand failed: {ex.Message}");
            SetWizardState(3, "Refine Box", "The Magic Wand could not read this clip. Drag a box yourself.");
        }
        finally
        {
            HideWandOverlay();
            _wandRunning = false;
            _wandAiPending = false;
            SyncWandFixButton();
            SetEnabled("MagicWandButton", true);
            SetContent("MagicWandButton", "\U0001FA84 MAGIC WAND");
        }
    }

    private void OnWandLocalReady(HudLocalPreview preview, string video)
    {
        if (!_wandRunning || _wandDetection == null || !_wandDetection.IsCurrent(preview.Generation)) return;
        if (_sourceCanvas == null || _snapshotPath == null || !string.Equals(_videoPath, video, StringComparison.Ordinal)) return;

        _wandLocalCandidates = preview.Candidates.ToList();
        List<CandidateSpec> local = ToCandidateSpecs(preview.Candidates);
        if (local.Count == 0) return;

        _wandCandidates = local;
        _wandCandidatesFrame = null;
        _wandPreviewIndex = -1;
        _wandAiPending = true;
        _wandLocalCount = local.Count;
        _wandAiWaitStartedUtc ??= DateTime.UtcNow;
        HideWandOverlay();
        _wandElapsedTimer?.Start();
        ClearSourceSelection();
        ShowMagicWandCandidates();
        SetEnabled("MagicWandButton", true);
        SetContent("MagicWandButton", "\u23F9 STOP AI SUGGESTIONS");
        string providerName = _wandActiveAiLabel ?? WandDetection.AiService?.ProviderName ?? "The AI";
        string estNote = string.IsNullOrEmpty(_wandActiveEstimateBanner) ? "" : $" ({_wandActiveEstimateBanner})";
        SetStatusSuccess($"Found {local.Count} HUD piece{(local.Count == 1 ? "" : "s")} on this PC. Step 2: Waiting for {providerName}{estNote}. You can start checking them now.");
    }

    private string? _wandActiveAiLabel;
    private string? _wandActiveEstimateBanner;

    private List<CandidateSpec> ToCandidateSpecs(IEnumerable<HudCandidate> found)
    {
        var specs = new List<CandidateSpec>();
        foreach (HudCandidate c in found)
        {
            SourceRect clamped = _edit.ClampSourceRect(new SourceRect(c.X, c.Y, c.Width, c.Height));
            if (clamped.Width < HudDetectionCoordinator.MinSourcePixels || clamped.Height < HudDetectionCoordinator.MinSourcePixels) continue;

            string roleKey = c.RoleKey is { Length: > 0 } key && _edit.TryGetRole(key, out _)
                ? key
                : _edit.QuadrantGuess(clamped).Key;

            specs.Add(new CandidateSpec(roleKey, clamped, c.Label, c.Source));
        }
        return specs;
    }

    private DispatcherTimer? _wandElapsedTimer;
    private DateTime _wandStartedUtc;
    private DateTime? _wandAiWaitStartedUtc;
    private int _wandLocalCount;
    internal const string AiWaitClockTag = "WANDAICLOCK_01";

    internal static string AiWaitText(int seconds, int limitSeconds, string provider)
        => $"{seconds}s of up to {limitSeconds}s for {provider}";

    private int _wandHighWaterPercent;

    private void ShowWandOverlay()
    {
        _wandHighWaterPercent = 0;
        _wandStartedUtc = DateTime.UtcNow;
        _wandAiWaitStartedUtc = null;

        if (this.FindControl<ProgressBar>("WandProgressBar") is { } bar) bar.Value = 0;
        if (this.FindControl<TextBlock>("WandPercentText") is { } pct) pct.Text = "0%";
        if (this.FindControl<TextBlock>("WandStageText") is { } stage) stage.Text = "Starting…";
        if (this.FindControl<TextBlock>("WandElapsedText") is { } elapsed) elapsed.Text = "";

        SetVisible("WandOverlay", true);

        _wandElapsedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _wandElapsedTimer.Tick -= WandElapsedTimer_Tick;
        _wandElapsedTimer.Tick += WandElapsedTimer_Tick;
        _wandElapsedTimer.Start();
    }

    private void HideWandOverlay()
    {
        _wandElapsedTimer?.Stop();
        SetVisible("WandOverlay", false);
    }

    private void WandElapsedTimer_Tick(object? sender, EventArgs e)
    {
        string provider = _wandActiveAiLabel ?? WandDetection.AiService?.ProviderName ?? "the AI";
        int aiLimit = (int)GeminiHudDetectionService.DefaultTimeout.TotalSeconds;
        int aiSeconds = _wandAiWaitStartedUtc is { } aiStart ? (int)Math.Max(0, (DateTime.UtcNow - aiStart).TotalSeconds) : -1;
        if (_wandAiPending)
        {
            if (aiSeconds >= 0 && _sourceSelection == null && _wandPreviewIndex < 0)
            {
                string estNote = string.IsNullOrEmpty(_wandActiveEstimateBanner) ? "" : $" ({_wandActiveEstimateBanner})";
                SetStatus($"Found {_wandLocalCount} HUD piece{(_wandLocalCount == 1 ? "" : "s")} on this PC. Step 2: waiting for {provider}{estNote} · {AiWaitText(aiSeconds, aiLimit, provider)}. ⏹ STOP AI SUGGESTIONS keeps only the PC finds.");
            }
            return;
        }
        if (this.FindControl<TextBlock>("WandElapsedText") is not { } elapsed) return;
        int seconds = (int)Math.Max(0, (DateTime.UtcNow - _wandStartedUtc).TotalSeconds);
        elapsed.Text = aiSeconds >= 0
            ? AiWaitText(aiSeconds, aiLimit, provider)
            : seconds < 2 ? "" : $"{seconds}s of up to {HudAutoDetector.MaxSeconds}s";
    }

    private void ReportWandProgress(HudAutoDetector.DetectionProgress beat)
    {
        if (!_wandRunning) return;
        _wandHighWaterPercent = Math.Clamp(Math.Max(_wandHighWaterPercent, beat.Percent), 0, 100);

        string stageText = beat.Stage;
        if (stageText.StartsWith("Waiting for ", StringComparison.Ordinal)) _wandAiWaitStartedUtc ??= DateTime.UtcNow;
        if (stageText.StartsWith("Waiting for ", StringComparison.Ordinal) && !string.IsNullOrEmpty(_wandActiveAiLabel))
        {
            string est = string.IsNullOrEmpty(_wandActiveEstimateBanner) ? "" : $" ({_wandActiveEstimateBanner})";
            stageText = $"Step 2: Waiting for {_wandActiveAiLabel}{est}…";
        }

        if (this.FindControl<ProgressBar>("WandProgressBar") is { } bar) bar.Value = _wandHighWaterPercent;
        if (this.FindControl<TextBlock>("WandPercentText") is { } pct) pct.Text = _wandHighWaterPercent + "%";
        if (this.FindControl<TextBlock>("WandStageText") is { } stage) stage.Text = stageText;

        SetStatus(stageText);
    }

    private void StepMagicWandPreview()
    {
        if (_wandCandidates is not { Count: > 0 } candidates) return;

        _wandPreviewIndex++;
        if (_wandPreviewIndex >= candidates.Count)
        {
            _wandPreviewIndex = -1;
            ClearSourceSelection();
            ShowMagicWandCandidates();
            SetWizardState(3, "Refine Box",
                $"{candidates.Count} pieces found. Click one to label it.");
            return;
        }

        CandidateSpec candidate = candidates[_wandPreviewIndex];
        ClearMagicWandCandidates();
        SetSourceSelection(candidate.Rect, candidate.RoleKey);
        AutoZoomToSelection();

        SetWizardState(3, "Refine Box",
            $"Piece {_wandPreviewIndex + 1} of {candidates.Count} ({CandidateCaption(candidate)}). Press Enter to label it, or MAGIC WAND again for the next one.");
    }

    private string CandidateCaption(CandidateSpec candidate)
    {
        string name = !string.IsNullOrWhiteSpace(candidate.Label)
            ? candidate.Label
            : _edit.TryGetRole(candidate.RoleKey, out HudRole role) ? role.DisplayName : candidate.RoleKey;
        return candidate.Source switch
        {
            HudCandidateSource.Ai => name + " · AI suggestion",
            HudCandidateSource.Fused => name + " · ✓ AI agrees",
            _ => name,
        };
    }

    private void ShowMagicWandCandidates()
    {
        if (_sourceCanvas == null || _wandCandidates == null) return;

        ClearMagicWandCandidates();

        double zoom = Math.Max(0.01, CurrentZoom());
        double stroke = 3.0 / zoom;
        IBrush strokeBrush = ThemeResources.Brush(this, "AppWandCandidateBrush", Brushes.DeepPink);
        IBrush fillBrush = ThemeResources.Brush(this, "AppWandCandidateFillBrush", Brushes.Transparent);
        IBrush captionBrush = ThemeResources.Brush(this, "AppWandCaptionBrush", Brushes.DeepPink);
        IBrush captionText = ThemeResources.Brush(this, "AppOnAccentTextBrush", Brushes.White);
        foreach (CandidateSpec candidate in _wandCandidates)
        {
            var rect = new Rectangle
            {
                Width = candidate.Rect.Width,
                Height = candidate.Rect.Height,
                Stroke = strokeBrush,
                StrokeThickness = stroke,
                StrokeDashArray = candidate.Source == HudCandidateSource.Ai ? new Avalonia.Collections.AvaloniaList<double> { 4, 2 } : null,
                Fill = fillBrush,
                Tag = candidate,
                Cursor = new Cursor(StandardCursorType.Hand),
                ZIndex = 450
            };

            Canvas.SetLeft(rect, candidate.Rect.X);
            Canvas.SetTop(rect, candidate.Rect.Y);
            _sourceCanvas.Children.Add(rect);
            _candidateControls.Add(rect);

            var caption = new TextBlock
            {
                Text = CandidateCaption(candidate),
                FontSize = WandCaptionFontSize / zoom,
                Foreground = captionText,
                Background = captionBrush,
                Padding = new Thickness(3 / zoom, 1 / zoom),
                MaxWidth = Math.Max(candidate.Rect.Width, 40 / zoom),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Tag = candidate,
                Cursor = new Cursor(StandardCursorType.Hand),
                ZIndex = 451
            };
            Canvas.SetLeft(caption, candidate.Rect.X);
            Canvas.SetTop(caption, candidate.Rect.Y);
            _sourceCanvas.Children.Add(caption);
            _candidateControls.Add(caption);
        }
    }

    private const double WandCaptionFontSize = 12.0;

    private void ClearMagicWandCandidates()
    {
        if (_sourceCanvas == null)
        {
            _candidateControls.Clear();
            return;
        }

        foreach (Control control in _candidateControls)
        {
            _sourceCanvas.Children.Remove(control);
        }
        _candidateControls.Clear();
    }
}
