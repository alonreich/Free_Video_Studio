// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// UNDO_28 — UNDO / REDO FOR THE ADD MUSIC WIZARD (docs/07 §5 "Still not done" item 2).
///
/// One <see cref="UndoStack{T}"/> of <see cref="MusicWizardSnapshot"/>: the song, the auto-fill
/// queue in order, the song start, both balance faders and the ducking / smooth-blend / loop
/// switches — exactly what <c>MusicWizardResult</c> hands to the export. NOT the waveform, the
/// preview player, the beat analysis, the phase-3 thumbnails or any other derived resource.
///
/// <list type="bullet">
///   <item><description>Every edit path calls <see cref="RecordWizardEdit"/> AFTER it changed the
///   state; <see cref="MusicWizardSnapshot.DescribeChange"/> names the step and supplies the U1
///   gesture key, so a fader sweep, a wheel spin or a waveform scrub is ONE step. Pointer release
///   closes the gesture (<see cref="EndWizardGesture"/>).</description></item>
///   <item><description>Restoring raises the very control events that record edits; the
///   <c>_restoringHistory</c> guard plus <see cref="UndoStack{T}"/>'s own re-entrancy guard keep a
///   restore from recording itself.</description></item>
///   <item><description>Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z via <see cref="HistoryShortcut"/>. A focused
///   text box (the song search) keeps its own text undo.</description></item>
/// </list>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class MusicWizardWindow
{
    private readonly UndoStack<MusicWizardSnapshot> _history =
        new(MusicWizardSnapshot.Create(null, null, 0, 100, 100, true, true, false));

    /// <summary>False until the window has settled on its opening state (resume included). Nothing before that is an edit.</summary>
    private bool _historyReady;

    private bool _restoringHistory;

    private MusicWizardSnapshot CaptureWizardSnapshot() => MusicWizardSnapshot.Create(
        _selectedTrack?.FilePath,
        _pendingAutoFillMusicPaths,
        _songStartSeconds,
        VideoVolSliderCtl?.Value ?? 100.0,
        MusicVolSliderCtl?.Value ?? 100.0,
        DuckingCheckBoxCtl?.IsChecked ?? true,
        CarvingCheckBoxCtl?.IsChecked ?? true,
        LoopMusicCheckBoxCtl?.IsChecked ?? false);

    private void WireWizardHistory()
    {
        AddHandler(InputElement.KeyDownEvent, WizardHistoryKeyDown, RoutingStrategies.Tunnel);

        foreach (Slider? slider in new[] { VideoVolSliderCtl, MusicVolSliderCtl })
        {
            if (slider == null) continue;
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty) RecordWizardEdit();
            };
            // U1 — the sweep ends when the pointer lets go, so the next drag is its own step.
            slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => EndWizardGesture(),
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }

        foreach (CheckBox? box in new[] { DuckingCheckBoxCtl, CarvingCheckBoxCtl, LoopMusicCheckBoxCtl })
        {
            if (box != null) box.IsCheckedChanged += (_, _) => RecordWizardEdit();
        }

        // A wizard opened WITHOUT a placement to resume is settled once it is on screen; one that
        // resumes is settled by the resume itself (the Loaded handler in the constructor).
        Loaded += (_, _) =>
        {
            if (InitialState == null && !_historyReady) ResetWizardHistory();
        };
    }

    /// <summary>The opening state: no history before it.</summary>
    private void ResetWizardHistory()
    {
        _history.Reset(CaptureWizardSnapshot());
        _historyReady = true;
    }

    /// <summary>Records whatever just changed as one named step (or folds it into the open gesture).</summary>
    private void RecordWizardEdit()
    {
        if (!_historyReady || _restoringHistory) return;

        MusicWizardSnapshot next = CaptureWizardSnapshot();
        if (Equals(next, _history.Current)) return;   // U4

        var (label, gesture) = MusicWizardSnapshot.DescribeChange(_history.Current, next);
        if (_history.Apply(next, label, gesture))
            RuntimeLog.Info("MUSIC_WIZARD", $"Undo step recorded: {label}.");
    }

    private void EndWizardGesture()
    {
        RecordWizardEdit();
        _history.EndGesture();
    }

    /// <summary>
    /// A DERIVED change, not an edit — e.g. the song start resetting to 0:00 when Next confirms a
    /// newly chosen song. It belongs to the "choose song" step already recorded, so it moves the
    /// baseline without adding an entry (and without burning redo).
    /// </summary>
    private void SyncWizardHistoryBaseline()
    {
        if (!_historyReady || _restoringHistory) return;
        _history.ReplaceCurrent(CaptureWizardSnapshot());
    }

    private void WizardHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        bool textFocused = KeyboardFocusPolicy.IsTextInputFocused(
            KeyboardFocusPolicy.GetFocusedElement(TopLevel.GetTopLevel(this)));
        HistoryCommand command = HistoryShortcut.Classify(
            e.Key.ToString(),
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            textFocused);
        if (command == HistoryCommand.None) return;   // text fields keep their own undo

        e.Handled = true;
        _ = StepWizardHistoryAsync(command == HistoryCommand.Undo);
    }

    private async Task StepWizardHistoryAsync(bool undo)
    {
        if (_restoringHistory || !_historyReady) return;

        RecordWizardEdit();   // anything not yet recorded becomes a step first, so undo removes the LATEST edit
        _history.EndGesture();

        string? label = undo ? _history.NextUndoLabel : _history.NextRedoLabel;
        MusicWizardSnapshot? target = undo ? _history.Undo() : _history.Redo();
        if (target == null || label == null)
        {
            Controls.FloatingNotice.Show(this, undo ? "Nothing to undo." : "Nothing to redo.", Controls.NoticeKind.Info);
            return;
        }

        try
        {
            bool reloadPhase3 = await ApplyWizardSnapshotAsync(target);
            RuntimeLog.Info("MUSIC_WIZARD", $"{(undo ? "Undo" : "Redo")}: {label}.");
            Controls.FloatingNotice.Show(this, $"{(undo ? "Undo" : "Redo")}: {label}", Controls.NoticeKind.Info);

            if (reloadPhase3 && _currentStep == 3)
            {
                // The final preview is built from the song; a different song needs it rebuilt —
                // the same transition the step-2 branch of OnNextClicked performs.
                StopPreview();
                CancelPhase3Load();
                _phase3Ready = false;
                UpdateNextButtonState();
                _phase3LoadCts = new CancellationTokenSource();
                int loadVersion = ++_phase3LoadVersion;
                await LoadPhase3DataAsync(_phase3LoadCts.Token, loadVersion);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"{(undo ? "Undo" : "Redo")} of '{label}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes the wizard show <paramref name="target"/>. Order matters for the same reason as
    /// EDIT3_01's resume: choosing a song clears the queue, and moving the song start clears it
    /// too, so the song goes first, then the start, then the queue, then faders and switches.
    /// Returns true when the song changed (phase 3 must be rebuilt).
    /// </summary>
    private async Task<bool> ApplyWizardSnapshotAsync(MusicWizardSnapshot target)
    {
        bool trackChanged;
        _restoringHistory = true;
        try
        {
            trackChanged = !string.Equals(_selectedTrack?.FilePath ?? string.Empty, target.TrackPath ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            if (trackChanged) await RestoreWizardTrackAsync(target.TrackPath);

            if (Math.Abs(_songStartSeconds - target.SongStartSeconds) > MusicWizardSnapshot.SecondsTolerance)
                ApplySongStartSeconds(target.SongStartSeconds, "");

            var queue = target.QueuePaths.IsDefault ? Array.Empty<string>() : target.QueuePaths.ToArray();
            bool sameQueue = queue.Length == _pendingAutoFillMusicPaths.Count
                && queue.Zip(_pendingAutoFillMusicPaths).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
            if (!sameQueue)
            {
                if (queue.Length == 0)
                {
                    ResetAutoFillQueueState();
                }
                else
                {
                    _pendingAutoFillMusicPaths.Clear();
                    _pendingAutoFillMusicPaths.AddRange(queue);
                    UpdateAutoFillQueuePreview();
                    if (AutoFillSongsBtnCtl is { } autoFillBtn) autoFillBtn.Content = $"Auto-Filled {queue.Length} Songs";
                }
                UpdateCoverageBar();
                UpdateFinalPlacementSummary();
                UpdateProblemFlags();
                DrawPhase3TimelineScale();
            }

            if (VideoVolSliderCtl is { } video && Math.Abs(video.Value - target.VideoVolumePercent) > MusicWizardSnapshot.PercentTolerance)
                video.Value = target.VideoVolumePercent;
            if (MusicVolSliderCtl is { } music && Math.Abs(music.Value - target.MusicVolumePercent) > MusicWizardSnapshot.PercentTolerance)
                music.Value = target.MusicVolumePercent;

            if (DuckingCheckBoxCtl is { } duck && (duck.IsChecked ?? true) != target.Ducking) duck.IsChecked = target.Ducking;
            if (CarvingCheckBoxCtl is { } carve && (carve.IsChecked ?? true) != target.Carving) carve.IsChecked = target.Carving;
            if (LoopMusicCheckBoxCtl is { } loop && (loop.IsChecked ?? false) != target.Loop) loop.IsChecked = target.Loop;
        }
        finally
        {
            _restoringHistory = false;
        }

        // Re-reading the window after a restore is an equivalent form, never a new step.
        _history.ReplaceCurrent(CaptureWizardSnapshot());
        return trackChanged;
    }

    private async Task RestoreWizardTrackAsync(string? path)
    {
        var listbox = MusicListBoxCtl;

        if (string.IsNullOrWhiteSpace(path))
        {
            OnTrackSelected(null);
            if (listbox != null) listbox.SelectedItem = null;
            if (_currentStep != 1)
            {
                // No song: the later steps have nothing to show.
                StopPreview();
                CancelPhase3Load();
                _phase3Ready = false;
                _currentStep = 1;
                UpdateStepVisibility();
                UpdateNextButtonState();
                UpdatePreviewControlsState();
            }
            return;
        }

        var track = FindTrackByPath(path)
                    ?? AvailableTracks.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    ?? new MusicTrackItem
                    {
                        Name = Path.GetFileNameWithoutExtension(path),
                        FilePath = path,
                        Title = Path.GetFileNameWithoutExtension(path),
                    };

        OnTrackSelected(track);
        if (listbox != null && AvailableTracks.Contains(track)) listbox.SelectedItem = track;

        if (_currentStep >= 2)
        {
            double duration = track.DurationSec;
            if (duration <= 0.001)
            {
                var prober = new FreeVideoStudio.Core.Media.MediaProber(ResolveFfprobePath(), path);
                duration = await prober.GetDurationAsync();
            }
            _trackDuration = Math.Max(1.0, duration > 0 ? duration : track.DurationSec);
            track.DurationSec = _trackDuration;
            _lastConfiguredTrackPath = path;
            _lastLoadedTrackPath = null;

            if (SelectedTrackLabelCtl is { } selectedLabel) selectedLabel.Text = track.Name;
            _ = RenderWaveformAsync(path);
            DrawTimelineScale();
            UpdatePlayhead();
        }
    }
}
