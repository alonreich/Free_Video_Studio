// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/01_TIMELINE_COORDINATE_MATH.md, docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using SourceRect = FreeVideoStudio.Core.Editing.CropSourceRect;   // EDITSTATE_01
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

// ══════════════════════════════════════════════════════════════════════════════════════════════
// MAGICWAND_02 — AUTOMATIC HUD DETECTION.  (Moved verbatim in intent from CropToolWindow.axaml.cs
// by AIHUD_01 so the window code-behind can SHRINK under MVVM_02 while the wand gains AI help.)
//
// WHAT WAS HERE BEFORE. ShowMagicWandCandidates() built six CandidateSpecs from hardcoded
// fractions of the frame — CandidateFromRatio("stats", 0.65, 0.02, 0.32, 0.28) and five more —
// drew them as pink rectangles and told the user they had been "detected". Nothing in that
// method ever looked at a pixel. MAGICWAND_01 hid the button rather than ship it.
//
// WHAT REPLACED IT. FreeVideoStudio.Core.Media.HudAutoDetector, the C# port of the old Python
// tool's developer_tools/magic_wand.py. It samples frames across the WHOLE clip, takes the temporal
// median and the temporal standard deviation, and scores real contours per HUD role.
//
// AIHUD_01 — OPTIONAL AI ASSISTANCE. When a Gemini key is configured AND the user has agreed to it,
// ONE frozen frame is also sent to the AI provider, and its validated suggestions are fused with
// the local ones by HudDetectionCoordinator (Core). The local detector is unchanged and always
// runs; with no key, no consent, no network, a timeout, a 429 or a bad answer, the user gets the
// local result exactly as before. NOTHING here commits a crop: every candidate is a suggestion the
// user clicks, moves/resizes as a normal selection, and confirms themselves.
//
// THE INTERACTION, which is the old Python tool's (app_handlers.on_magic_wand_clicked) because it
// was right: the FIRST press runs the analysis and shows every candidate at once. Each press after
// that steps through them one at a time as a live selection; after the last one it wraps back to
// showing them all.
// ══════════════════════════════════════════════════════════════════════════════════════════════
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
            () => SettingsManager.Instance.GeminiApiKey,
            () => SettingsManager.Instance.GeminiModelName,
            Faults.Sink),
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
        if (SettingsManager.Instance.AiMagicWandCloudConsent) return true;

        string provider = WandDetection.AiService?.ProviderName ?? "the AI provider";
        bool yes = await Controls.ConfirmDialogWindow.AskAsync(
            this,
            $"The Magic Wand can ask {provider} for extra HUD suggestions.\n\n" +
            $"To do that, ONE still image — the frozen frame you are looking at — will be sent to {provider} " +
            "(the AI provider set in Settings › AI Tracking). Your video is never uploaded.\n\n" +
            "The offline Magic Wand runs either way, and nothing is applied until you pick a box yourself.\n\n" +
            "Send this frame to the AI provider?",
            "Magic Wand: send one frame?",
            "Send one frame",
            "Offline only");

        if (yes)
        {
            SettingsManager.Update(s => s.AiMagicWandCloudConsent = true);
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

        // AIHUD_04 — AI boxes describe ONE frame. A different frame with no AI to ask again falls
        // back to the clip's local candidates rather than reusing another frame's AI answer.
        if (_wandCandidates != null && _wandCandidatesFrame != null && _wandCandidatesFrame != frameIdentity && !aiAvailable)
        {
            _wandCandidates = _wandLocalCandidates is { } localOnly ? ToCandidateSpecs(localOnly) : null;
            _wandCandidatesFrame = null;
            _wandPreviewIndex = -1;
        }

        // ── Already analysed for this clip (and, if AI is on, for this frame): step the preview.
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

            // WANDPROGRESS_01 — raise the progress panel BEFORE the next await.
            ShowWandOverlay();

            string ffmpeg = ResolveBinaryPath("ffmpeg.exe", "backend");
            string video = _videoPath!;   // File.Exists checked above
            string snapshot = _snapshotPath!;
            int sourceW = _edit.SnapshotWidth;
            int sourceH = _edit.SnapshotHeight;
            double totalMs = _durationMs;

            // WANDPROGRESS_01 — Progress<T> captures the UI synchronisation context it is CONSTRUCTED on.
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
                    // Any failure here costs only the AI half; the offline wand still runs below.
                    Faults.Degraded("CROP", "The frozen frame could not be prepared for AI suggestions. The offline Magic Wand still runs.", ex);
                }
            }

            HudDetectionResult? result = await WandDetection.RunAsync(
                new HudDetectionRequest(localDetect, frame, sourceW, sourceH), wandProgress);

            // AIHUD_03 — cancelled, superseded, window closed, clip swapped or frame unfrozen: publish NOTHING.
            if (result == null || _wandDetection == null || !_wandDetection.IsCurrent(result.Generation)
             || _sourceCanvas == null || _snapshotPath == null || !string.Equals(_videoPath, video, StringComparison.Ordinal))
            {
                if (_wandStopRequested && _sourceCanvas != null)
                    SetWizardState(3, "Refine Box", "Magic Wand stopped. Drag a box round a HUD piece yourself.");
                return;
            }

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
            _wandPreviewIndex = -1;
            ShowMagicWandCandidates();

            int aiOnly = candidates.Count(c => c.Source == HudCandidateSource.Ai);
            int confirmed = candidates.Count(c => c.Source == HudCandidateSource.Fused);
            RuntimeLog.Info("CROP", $"Magic Wand found {candidates.Count} candidate region(s) (AI outcome {result.AiOutcome?.ToString() ?? "off"}, AI-only {aiOnly}, AI-confirmed {confirmed}).");

            string aiNote = result.AiOutcome == AiHudOutcome.Success && (aiOnly + confirmed) > 0
                ? $" The AI confirmed {confirmed} and suggested {aiOnly} more (dashed)."
                : string.Empty;
            SetStatusSuccess($"Found {candidates.Count} HUD piece{(candidates.Count == 1 ? "" : "s")}.{aiNote} " +
                             "Click a box to select it, adjust it, then label it — nothing is added until you do.");
        }
        catch (OperationCanceledException swallowed2)
        {
            SetWizardState(3, "Refine Box", "Magic Wand stopped. Drag a box round a HUD piece yourself.");
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
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
            SetEnabled("MagicWandButton", true);
            SetContent("MagicWandButton", "\U0001FA84 MAGIC WAND");
        }
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

            // A null RoleKey means the generic/circle fallback (or an AI label with no built-in
            // role) found this. QuadrantGuess is the same position heuristic the manual path uses.
            string roleKey = c.RoleKey is { Length: > 0 } key && _edit.TryGetRole(key, out _)
                ? key
                : _edit.QuadrantGuess(clamped).Key;

            specs.Add(new CandidateSpec(roleKey, clamped, c.Label, c.Source));
        }
        return specs;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // WANDPROGRESS_01 — THE PANEL THAT SAYS WHAT THE WAND IS DOING.
    // A bar that MOVES, a percentage, a sentence naming what is being looked at, and a way to STOP.
    // The elapsed clock makes HudAutoDetector.MaxSeconds visible.
    // ══════════════════════════════════════════════════════════════════════════════════════════

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

        // Stays quiet for the first couple of seconds: a timer that appears instantly on a job that
        // finishes in three seconds is itself a small alarm.
        elapsed.Text = seconds < 2 ? "" : $"{seconds}s of up to {ceiling}s";
    }

    /// <summary>WANDPROGRESS_01 — one beat from the detector (or the coordinator's "waiting for the
    /// AI" beat). Always on the UI thread; see where the Progress&lt;T&gt; is constructed.</summary>
    private void ReportWandProgress(HudAutoDetector.DetectionProgress beat)
    {
        if (!_wandRunning) return;   // a beat that lands after the run ended must not repaint the status
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
            HudCandidateSource.Ai => name + " · AI",
            HudCandidateSource.Fused => name + " · AI+local",
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

        // Stroke and caption are divided by the zoom for the same reason the selection rectangle's
        // is (CROPCANVAS_01): at Fit on a 4K capture an unscaled 3px stroke is a hairline.
        double zoom = Math.Max(0.01, CurrentZoom());
        double stroke = 3.0 / zoom;

        foreach (CandidateSpec candidate in _wandCandidates)
        {
            var rect = new Rectangle
            {
                Width = candidate.Rect.Width,
                Height = candidate.Rect.Height,
                Stroke = new SolidColorBrush(Color.Parse("#e91e63")),
                StrokeThickness = stroke,
                StrokeDashArray = candidate.Source == HudCandidateSource.Ai ? new Avalonia.Collections.AvaloniaList<double> { 4, 2 } : null,
                Fill = new SolidColorBrush(Color.FromArgb(24, 233, 30, 99)),
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
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(200, 233, 30, 99)),
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
