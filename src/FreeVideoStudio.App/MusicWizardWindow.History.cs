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

public partial class MusicWizardWindow
{
    private readonly UndoStack<MusicWizardSnapshot> _history =
        new(MusicWizardSnapshot.Create(null, null, 0, 100, 100, true, true, false));

    private bool _historyReady;

    private bool _restoringHistory;
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

    private void ResetWizardHistory()
    {
        _history.Reset(CaptureWizardSnapshot());
        _historyReady = true;
    }

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
