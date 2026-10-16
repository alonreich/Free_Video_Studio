
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
    /// <summary>
    /// MUSICWIN_01 — the song list is the queue (all songs when there are two or more), the first
    /// song is the track, and every song's chosen part rides along as in/out pairs.
    /// </summary>
    private MusicWizardSnapshot CaptureWizardSnapshot() => MusicWizardSnapshot.Create(
        _playlist.Count > 0 ? _playlist[0].Path : null,
        _playlist.Count > 1 ? _playlist.Select(e => e.Path) : null,
        _songStartSeconds,
        VideoVolSliderCtl?.Value ?? 100.0,
        MusicVolSliderCtl?.Value ?? 100.0,
        DuckingCheckBoxCtl?.IsChecked ?? true,
        CarvingCheckBoxCtl?.IsChecked ?? true,
        LoopMusicCheckBoxCtl?.IsChecked ?? false,
        _playlist.SelectMany(e => new[] { e.InSec, e.OutSec }));


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
            slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => EndWizardGesture(),
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }

        foreach (CheckBox? box in new[] { DuckingCheckBoxCtl, CarvingCheckBoxCtl, LoopMusicCheckBoxCtl })
        {
            if (box != null) box.IsCheckedChanged += (_, _) => RecordWizardEdit();
        }

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
        if (Equals(next, _history.Current)) return;

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
        if (command == HistoryCommand.None) return;

        e.Handled = true;
        _ = StepWizardHistoryAsync(command == HistoryCommand.Undo);
    }

    private async Task StepWizardHistoryAsync(bool undo)
    {
        if (_restoringHistory || !_historyReady) return;

        RecordWizardEdit();
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
    /// Makes the wizard show <paramref name="target"/>: the song list is rebuilt from the snapshot
    /// (songs in order, each with its chosen part), then the faders and switches. Songs never
    /// require the Fit &amp; Mix video to be rebuilt — only the music changes — so this returns
    /// false unless the list became empty, which returns the user to the song list.
    /// </summary>
    private Task<bool> ApplyWizardSnapshotAsync(MusicWizardSnapshot target)
    {
        _restoringHistory = true;
        try
        {
            var paths = !target.QueuePaths.IsDefault && target.QueuePaths.Length > 0
                ? target.QueuePaths.ToArray()
                : (string.IsNullOrWhiteSpace(target.TrackPath) ? Array.Empty<string>() : new[] { target.TrackPath! });
            var trims = target.Trims.IsDefault ? Array.Empty<double>() : target.Trims.ToArray();

            var rebuilt = new System.Collections.Generic.List<FreeVideoStudio.Core.Media.MusicPlaylistEntry>(paths.Length);
            for (int i = 0; i < paths.Length; i++)
            {
                int existing = IndexOfPath(paths[i]);
                var baseEntry = existing >= 0 ? _playlist[existing] : NewEntry(TrackFor(paths[i]));
                double inSec = 2 * i + 1 < trims.Length ? trims[2 * i] : baseEntry.InSec;
                double outSec = 2 * i + 1 < trims.Length ? trims[2 * i + 1] : baseEntry.OutSec;
                rebuilt.Add(baseEntry with { InSec = inSec, OutSec = outSec, UserTrimmed = true });
            }
            if (rebuilt.Count > 0)
                rebuilt[0] = rebuilt[0] with { InSec = Math.Max(0, target.SongStartSeconds) };
            _playlist.Clear();
            _playlist.AddRange(rebuilt);
            if (_playlist.Count > 1) _playlistExplicit = true;

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

        OnPlaylistChanged();
        if (_playlist.Count == 0 && _currentStep != StepChoose)
            _ = OnBackClickedAsync(this, new RoutedEventArgs());

        _history.ReplaceCurrent(CaptureWizardSnapshot());
        return Task.FromResult(false);
    }
}
