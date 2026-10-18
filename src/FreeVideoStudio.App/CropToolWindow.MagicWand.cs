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
    /// <summary>
    /// Candidates being shown/stepped, from the last successful run. Cleared by a new clip
    /// (<see cref="ResetMagicWandForNewClip"/>). Freezing a different frame, committing an element
    /// or resetting the working state only rewind <see cref="_wandPreviewIndex"/>; local candidates
    /// describe the CLIP. AI ones describe a FRAME — see <see cref="_wandCandidatesFrame"/>.
    /// </summary>
    private List<CandidateSpec>? _wandCandidates;

    /// <summary>AIHUD_04 — the offline detector's answer for THIS clip, so a new frozen frame only
    /// costs an AI round-trip, never another 20-second local scan.</summary>
    private List<HudCandidate>? _wandLocalCandidates;

    /// <summary>
    /// AIHUD_04 — the frozen frame <see cref="_wandCandidates"/> includes AI input for, or null if
    /// the set is local-only. AI boxes are never reused for a different frame: when the frame
    /// changes, the set is either rebuilt with a fresh AI answer or reduced back to local-only.
    /// </summary>
    private string? _wandCandidatesFrame;

    /// <summary>Where the "press again to step through them" cursor is. -1 means "showing all".</summary>
    private int _wandPreviewIndex = -1;

    /// <summary>Guards against a second press while the analysis is still running.</summary>
    private bool _wandRunning;

    /// <summary>Set by STOP so a cancelled run says "stopped" rather than nothing.</summary>
    private bool _wandStopRequested;

    /// <summary>
    /// WANDPROGRESSIVE_01 — the offline finds are already on screen and only the AI is still pending.
    /// The progress overlay is gone, the user can review, and MAGIC WAND reads "STOP AI SUGGESTIONS".
    /// </summary>
    private bool _wandAiPending;

    /// <summary>AIHUD_05 — the user said "offline only" in this session; do not ask again until restart.</summary>
    private static bool s_wandCloudDeclinedThisSession;

    /// <summary>
    /// AIHUD_03 — owns run generations and cancellation. Built on first use; the window cannot take
    /// constructor parameters (COMPOSITION_02), so the provider is constructed here directly and the
    /// ambient diagnostic channel (FAULTTIER_02) is passed in — not AppServices.Current.
    /// </summary>
    private HudDetectionCoordinator? _wandDetection;

    private HudDetectionCoordinator WandDetection => _wandDetection ??= new HudDetectionCoordinator(
        new GeminiHudDetectionService(
            () => SettingsManager.Instance.GetApiKey(SettingsManager.Instance.GetUsageConfig(AiUsage.MagicWand).Vendor),
            () => SettingsManager.Instance.GetEffectiveModel(AiUsage.MagicWand),
            Faults.Sink,
            vendorProvider: () => SettingsManager.Instance.GetUsageConfig(AiUsage.MagicWand).Vendor),
        Faults.Sink);

    /// <summary>STOP button, clip change: cancel and invalidate whatever is in flight.</summary>
    private void CancelMagicWand() => _wandDetection?.Cancel();

    private void DisposeMagicWand()
    {
        _wandDetection?.Dispose();
        _wandDetection = null;
    }

    /// <summary>MAGICWAND_02 — a new clip invalidates everything the wand learned from the old one.</summary>
    private void ResetMagicWandForNewClip()
    {
        CancelMagicWand();
        _wandCandidates = null;
        _wandLocalCandidates = null;
        _wandCandidatesFrame = null;
        _wandPreviewIndex = -1;
    }

    /// <summary>Cheap identity of the frozen frame on screen (path + write time). The AI cache key
    /// itself uses a content hash — see <see cref="AiHudFrameBuilder"/>.</summary>
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

    /// <summary>
    /// AIHUD_05 — the privacy notice. Shown before the FIRST cloud transmission, and again until
    /// the user agrees; "offline only" is remembered for the session. A dialog that cannot be shown
    /// is treated as "no" (ConfirmDialogWindow.AskAsync's own rule).
    /// </summary>
    private async Task<bool> ConfirmWandCloudConsentAsync()
    {
        var wandSettings = SettingsManager.Instance;
        var vendor = wandSettings.GetUsageConfig(FreeVideoStudio.App.Services.AiUsage.MagicWand).Vendor;
        if (AppSettings.ConsentCovers(wandSettings.AiMagicWandCloudConsent, wandSettings.AiMagicWandCloudConsentVendor, vendor)) return true;
        string provider = FreeVideoStudio.App.Services.AiVisionProviders.DisplayName(vendor);
        bool yes = await Controls.ConfirmDialogWindow.AskAsync(
            this,
            $"The Magic Wand can ask {provider} for extra HUD suggestions.\n\n" +
            $"To do that, ONE still image — the frozen frame you are looking at — will be sent to {provider} " +
            "(the AI provider set in Settings › AI Features). Your video is never uploaded.\n\n" +
            "The offline Magic Wand runs either way, and nothing is applied until you pick a box yourself.\n\n" +
            $"Send this frame to {provider}?",
            $"Magic Wand: send one frame to {provider}?",
            "Send one frame",
            "Offline only");

        if (yes)
        {
            SettingsManager.Update(s => { s.AiMagicWandCloudConsent = true; s.AiMagicWandCloudConsentVendor = vendor.ToString(); });
            RuntimeLog.Info("CROP", $"User allowed the Magic Wand to send one frame to {provider}.");
        }
        else
        {
            s_wandCloudDeclinedThisSession = true;
            RuntimeLog.Info("CROP", "User chose offline-only Magic Wand for this session.");
        }
        return yes;
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
        bool aiConfigured = WandDetection.AiService is { IsConfigured: true };
        if (!aiConfigured && !s_wandCloudDeclinedThisSession)
        {
            var choice = await Controls.ConfirmDialogWindow.AskThreeWayAsync(
                this,
                "AI HUD Detection",
                "AI HUD detection gives more accurate suggestions for game interfaces.\n\n" +
                "An AI provider key is not configured yet. Would you like to set up an AI key now, or run offline HUD detection only?",
                "Set up AI Key",
                "Run Offline");
            if (choice == Controls.ConfirmDialogWindow.ConfirmDialogResult.Yes)
            {
                var vendor = SettingsManager.Instance.GetUsageConfig(Services.AiUsage.MagicWand).Vendor;
                await Controls.AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, vendor);
            }
            else
            {
                s_wandCloudDeclinedThisSession = true;
            }
        }
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

        _wandRunning = true;
        _wandStopRequested = false;
        SetEnabled("MagicWandButton", false);

        try
        {
            bool useAi = aiAvailable && await ConfirmWandCloudConsentAsync();

            SetContent("MagicWandButton", "ANALYSING…");
            SetWizardState(3, "Refine Box", useAi
                ? "Looking through the clip for HUD pieces, and asking the AI about this frame."
                : "Looking through the clip for HUD pieces.");

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
                SetWizardState(3, "Refine Box", why);
                return;
            }

            _wandCandidates = candidates;
            _wandCandidatesFrame = frame != null ? frameIdentity : null;
            if (userIsReviewing)
            {
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

            string shortProv = WandDetection.AiService?.ProviderName ?? "AI";
            string aiNote = result.AiOutcome == AiHudOutcome.Success && (aiOnly + confirmed) > 0
                ? $" {shortProv} agreed on {confirmed} (✓) and suggested {aiOnly} more (dashed)."
                : string.Empty;
            SetStatusSuccess($"Found {candidates.Count} HUD piece{(candidates.Count == 1 ? "" : "s")}.{aiNote} " +
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
            SetEnabled("MagicWandButton", true);
            SetContent("MagicWandButton", "\U0001FA84 MAGIC WAND");
        }
    }

    /// <summary>
    /// WANDPROGRESSIVE_01 — the offline scan is done and the AI is still thinking: show the offline
    /// finds NOW (suggestions only, AIHUD_01) and let the user start reviewing. The final fused answer
    /// replaces them only if the user has not started working on one (see RunMagicWandAsync).
    /// </summary>
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
        HideWandOverlay();
        ClearSourceSelection();
        ShowMagicWandCandidates();
        SetEnabled("MagicWandButton", true);
        SetContent("MagicWandButton", "\u23F9 STOP AI SUGGESTIONS");
        string providerName = WandDetection.AiService?.ProviderName ?? "The AI";
        SetStatusSuccess($"Found {local.Count} HUD piece{(local.Count == 1 ? "" : "s")} offline. {providerName} is still looking for more — you can start reviewing now.");
    }

    /// <summary>
    /// Source-pixel candidates -> drawable specs. Runs on the UI thread because role lookup reads
    /// this window's custom-role list. Coordinates are already SOURCE pixels (AI ones were converted
    /// exactly once in the coordinator); this only clamps to the frame and picks a role.
    /// </summary>
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

    /// <summary>Highest percentage reported so far; the bar never goes backwards.</summary>
    private int _wandHighWaterPercent;

    private void ShowWandOverlay()
    {
        _wandHighWaterPercent = 0;
        _wandStartedUtc = DateTime.UtcNow;

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
        if (this.FindControl<TextBlock>("WandElapsedText") is not { } elapsed) return;

        int seconds = (int)Math.Max(0, (DateTime.UtcNow - _wandStartedUtc).TotalSeconds);
        int ceiling = HudAutoDetector.MaxSeconds;

        elapsed.Text = seconds < 2 ? "" : $"{seconds}s of up to {ceiling}s";
    }

    /// <summary>WANDPROGRESS_01 — one beat from the detector (or the coordinator's "waiting for the
    /// AI" beat). Always on the UI thread; see where the Progress&lt;T&gt; is constructed.</summary>
    private void ReportWandProgress(HudAutoDetector.DetectionProgress beat)
    {
        if (!_wandRunning) return;
        _wandHighWaterPercent = Math.Clamp(Math.Max(_wandHighWaterPercent, beat.Percent), 0, 100);

        if (this.FindControl<ProgressBar>("WandProgressBar") is { } bar) bar.Value = _wandHighWaterPercent;
        if (this.FindControl<TextBlock>("WandPercentText") is { } pct) pct.Text = _wandHighWaterPercent + "%";
        if (this.FindControl<TextBlock>("WandStageText") is { } stage) stage.Text = beat.Stage;

        SetStatus(beat.Stage);
    }

    /// <summary>
    /// MAGICWAND_02 — the "press again" behaviour. Cycles: all shown -> candidate 1 selected -> ...
    /// -> all shown again. Selecting one only SELECTS it (normal move/resize applies); the user still
    /// presses Enter / labels it to add it. AIHUD_01: this path never commits.
    /// </summary>
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

    /// <summary>Concise on-canvas caption: what it is, and where the suggestion came from.</summary>
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

    /// <summary>
    /// Draws the cached candidates onto the frozen frame, each with a concise label. Pure rendering.
    /// Rectangles and labels carry their CandidateSpec in Tag, which SourceCanvas_PointerPressed's
    /// branch 3 reads to turn a click into a SELECTION (never a commit).
    /// AI-only suggestions are dashed so the user can tell them from detector finds at a glance.
    /// </summary>
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

    /// <summary>Caption size in screen pixels; divided by the zoom wherever it is applied.</summary>
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
