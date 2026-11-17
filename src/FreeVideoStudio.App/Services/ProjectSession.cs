using System;
using System.Collections.Generic;
using System.IO;
using FreeVideoStudio.App.Abstractions;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.App.Services;

public sealed class ProjectSession
{
    public const int AutosaveIntervalSeconds = 30;

    private readonly IProjectStore _store;
    private readonly IFilePickerService _picker;
    private readonly IUserNotifier _notifier;
    private readonly IFaultSink _faults;
    private readonly IClock _clock;
    private readonly MainViewModel _viewModel;
    private readonly Func<(int Width, int Height, double Fps)> _probeVideoMetrics;

    private readonly Func<ProjectMask?> _readLiveMask;

    private readonly Func<ProjectMerge?> _readMergeQueue;

    private readonly UndoSidecarStore _sidecar;

    private readonly Func<bool> _hasUnsavedWork;
    private readonly Func<ProjectMask?> _readLiveMaskFast;

    private (string Path, long Size, long Modified)? _probedSource;

    private UndoStack<ProjectDocument>? _history;
    private DateTimeOffset _lastAutosaveUtc;

    private long _editGeneration;

    private Task? _autosaveIo;

    private Task? _autosaveCompletion;

    private Task _ioTail = Task.CompletedTask;
    private bool _explicitIoPending;
    private readonly bool _hasFastMask;
    private long _writeSequence;
    private long _publishedSequence;
    private long _sessionGeneration;
    private long _navigationGeneration;

    private bool _applying;

    public ProjectSession(
        IProjectStore store,
        IFilePickerService picker,
        IUserNotifier notifier,
        IFaultSink faults,
        IClock clock,
        MainViewModel viewModel,
        Func<(int Width, int Height, double Fps)> probeVideoMetrics,
        Func<bool> hasUnsavedWork,
        Func<ProjectMask?>? readLiveMask = null,
        Func<ProjectMerge?>? readMergeQueue = null,
        UndoSidecarStore? sidecar = null,
        Func<ProjectMask?>? readLiveMaskFast = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _probeVideoMetrics = probeVideoMetrics ?? throw new ArgumentNullException(nameof(probeVideoMetrics));
        _hasUnsavedWork = hasUnsavedWork ?? throw new ArgumentNullException(nameof(hasUnsavedWork));

        _readLiveMask = readLiveMask ?? (static () => null);
        _readLiveMaskFast = readLiveMaskFast ?? (static () => null);
        _hasFastMask = readLiveMaskFast is not null;
        _readMergeQueue = readMergeQueue ?? (static () => null);

        _sidecar = sidecar ?? UndoSidecarStore.CreateDefault(Core.Infrastructure.ApplicationPaths.CreateDefault());

        _lastAutosaveUtc = _clock.UtcNow;
    }

    public string? CurrentPath { get; private set; }

    private bool _dirty;

    public bool IsDirty => _dirty && HasUnsavedWorkSafely();

    public bool CanUndo => _history?.CanUndo == true;
    public bool CanRedo => _history?.CanRedo == true;
    public string? NextUndoLabel => _history?.NextUndoLabel;
    public string? NextRedoLabel => _history?.NextRedoLabel;

    public event EventHandler? StateChanged;

    public event EventHandler<ProjectDocument>? DocumentApplied;

    public void BeginHistory()
    {
        _sessionGeneration++;
        _history = new UndoStack<ProjectDocument>(Capture());
        _history.Changed += (_, _) => Raise();
        Raise();
    }

    public void PushEdit(string label, string? gestureKey = null)
    {
        if (_applying || _history is null) return;

        if (_history.Apply(Capture(), label, gestureKey))
        {
            _dirty = true;
            _editGeneration++;
            Raise();
        }
    }

    public void EndGesture() => _history?.EndGesture();

    public void MarkRecovered()
    {
        _dirty = true;
        _editGeneration++;
        Raise();
    }

    public void Undo()
    {
        if (_history is null) return;

        ProjectDocument? previous = _history.Undo();
        if (previous is null)
        {
            _notifier.Notify("Nothing left to undo", NoticeKind.Info);
            return;
        }

        Apply(previous);
        _notifier.Notify($"Undid: {_history.NextRedoLabel ?? "last change"} — Ctrl+Y to redo", NoticeKind.Info);
    }

    public void Redo()
    {
        if (_history is null) return;

        ProjectDocument? next = _history.Redo();
        if (next is null)
        {
            _notifier.Notify("Nothing left to redo", NoticeKind.Info);
            return;
        }

        Apply(next);
        _notifier.Notify($"Redid: {_history.NextUndoLabel ?? "last change"}", NoticeKind.Info);
    }

    public Task<bool> SaveAsync() => RunCommandAsync(SaveCoreAsync);

    private async Task<bool> SaveCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath)) return await SaveAsCoreAsync();
        return await WriteToAsync(CurrentPath!, announce: true);
    }

    public Task<bool> SaveAsAsync() => RunCommandAsync(SaveAsCoreAsync);

    private async Task<bool> SaveAsCoreAsync()
    {
        if (!_viewModel.HasLoadedVideo)
        {
            _notifier.Notify("Load a video before saving a project.", NoticeKind.Warning);
            return false;
        }

        long session = _sessionGeneration;
        string suggested = SuggestFileName();

        string? chosen = await _picker.SaveFileAsync(new FilePickerRequest(
            Title: "Save project",
            SuggestedFileName: suggested,
            ExtensionLabel: "Free Video Studio project",
            Extension: "fvsproj",
            StartDirectoryKey: "last_project_dir.txt"));

        if (chosen is null || session != _sessionGeneration) return false;

        return await WriteToAsync(_store.NormalizeExtension(chosen), announce: true);
    }

    public Task<bool> OpenAsync() => RunCommandAsync(OpenCoreAsync);

    private async Task<bool> OpenCoreAsync()
    {
        long navigation = ++_navigationGeneration;
        if (!await ConfirmDiscardAsync("Open another project")) return false;
        long session = _sessionGeneration, generation = _editGeneration;

        string? chosen = await _picker.OpenFileAsync(new FilePickerRequest(
            Title: "Open project",
            SuggestedFileName: null,
            ExtensionLabel: "Free Video Studio project",
            Extension: "fvsproj",
            StartDirectoryKey: "last_project_dir.txt"));

        if (chosen is null) return false;

        var loaded = await EnqueueIo(() =>
        {
            ProjectDocument? value = _store.Load(chosen, out string? problem, out bool backup);
            return (Document: value, Error: problem, FromBackup: backup,
                Integrity: value?.CheckSource() ?? SourceIntegrity.Unknown,
                History: value is null ? null : _sidecar.Load(chosen, HistoryFingerprint(value)));
        });
        if (navigation != _navigationGeneration || session != _sessionGeneration || generation != _editGeneration)
        {
            _notifier.Notify("The editor changed while that project was opening. Open it again when you are ready.", NoticeKind.Warning);
            return false;
        }
        var (document, error, fromBackup, check, history) = loaded;

        if (document is null)
        {
            _faults.Fatal("PROJECT",
                "That project file could not be opened, so nothing was changed." +
                Environment.NewLine + Environment.NewLine + (error ?? "The file is not readable."));
            return false;
        }

        if (fromBackup)
        {
            _notifier.Notify(
                "The main project file was damaged, so the backup was opened instead. " +
                "Any changes made after that backup are not in this version.",
                NoticeKind.Warning);
        }

        if (check == SourceIntegrity.Missing)
        {
            _faults.Fatal("PROJECT",
                "The video this project was built from is no longer at:" + Environment.NewLine +
                document.Source.FilePath + Environment.NewLine + Environment.NewLine +
                "Move it back, or load the video first and re-apply your edits.");
            return false;
        }

        if (check == SourceIntegrity.Changed)
        {
            _notifier.Notify(
                "The source video has changed since this project was saved — cuts and speed ramps " +
                "may no longer line up with the footage.",
                NoticeKind.Warning);
        }

        _sessionGeneration++;
        _probedSource = (document.Source.FilePath, document.Source.SizeBytes, document.Source.ModifiedUtcSeconds);
        Apply(document);
        CurrentPath = chosen;
        _dirty = false;
        _history = new UndoStack<ProjectDocument>(document);
        _history.Changed += (_, _) => Raise();

        if (history is not null)
        {
            _history.Restore(history.Undo, history.Redo);
            RuntimeLog.Info("UNDO",
                $"Restored {history.Undo.Count} undo / {history.Redo.Count} redo step(s) from the sidecar (UNDO_24).");
        }

        Raise();

        _notifier.Notify($"Opened {Path.GetFileNameWithoutExtension(chosen)}", NoticeKind.Success);
        return true;
    }

    public void AutosaveTick()
    {
        if (!IsDirty || string.IsNullOrWhiteSpace(CurrentPath)) return;
        if ((_clock.UtcNow - _lastAutosaveUtc).TotalSeconds < AutosaveIntervalSeconds) return;
        if (_autosaveIo is { IsCompleted: false }) return;

        _lastAutosaveUtc = _clock.UtcNow;

        string savePath = CurrentPath!;
        long generation = _editGeneration, session = _sessionGeneration;
        ProjectDocument snapshot = Capture(forExplicitSave: false);

        string? fingerprint = null;
        UndoEntry<ProjectDocument>[]? undo = null;
        UndoEntry<ProjectDocument>[]? redo = null;
        if (_history is { } history)
        {
            fingerprint = HistoryFingerprint(snapshot);
            undo = history.UndoEntries.ToArray();
            redo = history.RedoEntries.ToArray();
        }

        SynchronizationContext? ui = SynchronizationContext.Current;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _autosaveCompletion = completion.Task;
        Task<ProjectIoResult> io = EnqueueIo(() =>
        {
            ProjectIoResult result = SaveSnapshot(snapshot, savePath);
            if (result.Success && fingerprint is not null) _sidecar.Save(savePath, fingerprint, undo!, redo!);
            return result;
        });
        _autosaveIo = io;

        io.ContinueWith(t =>
        {
            ProjectIoResult result = t.Status == TaskStatus.RanToCompletion
                ? t.Result
                : ProjectIoResult.Fail(t.Exception?.GetBaseException().Message ?? "The autosave was interrupted.");

            void Finish()
            {
                try { CompleteAutosave(result, savePath, generation, session); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                finally { completion.TrySetResult(); }
            }

            if (ui is null) Finish();
            else ui.Post(static state => ((Action)state!)(), (Action)Finish);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void CompleteAutosave(ProjectIoResult result, string savePath, long generation, long session)
    {
        if (!result.Success)
        {
            _faults.Fatal("PROJECT",
                "Your project could not be autosaved, so nothing was written." + Environment.NewLine +
                Environment.NewLine + (result.Error ?? "The file could not be written."));
            return;
        }

        if (session != _sessionGeneration || generation != _editGeneration) return;
        if (!string.Equals(CurrentPath, savePath, StringComparison.OrdinalIgnoreCase)) return;

        _dirty = false;
        Raise();
    }

    internal Task PendingAutosave => _autosaveCompletion ?? Task.CompletedTask;

    public bool OpenDocument(ProjectDocument document, bool allowMissingSource = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        SourceIntegrity check = document.CheckSource();
        if (check == SourceIntegrity.Missing)
        {
            if (!allowMissingSource)
            {
                _faults.Fatal("PROJECT",
                    "The video this project was built from is no longer at:" + Environment.NewLine +
                    document.Source.FilePath + Environment.NewLine + Environment.NewLine +
                    "Move it back, or load the video first and re-apply your edits.");
                return false;
            }
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Info("Project", "Recovered project's source clip is missing; applying the edit state without video.");
        }

        if (check == SourceIntegrity.Changed)
        {
            _notifier.Notify(
                "The source video has changed since this project was saved — cuts and speed ramps " +
                "may no longer line up with the footage.",
                NoticeKind.Warning);
        }

        _sessionGeneration++;
        _probedSource = (document.Source.FilePath, document.Source.SizeBytes, document.Source.ModifiedUtcSeconds);
        Apply(document);
        CurrentPath = null;
        _dirty = false;
        _history = new UndoStack<ProjectDocument>(document);
        _history.Changed += (_, _) => Raise();
        Raise();
        return true;
    }

    public async Task<bool> ConfirmDiscardAsync(string action)
    {
        try
        {
            try { await _ioTail; }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            await PendingAutosave;
            if (!IsDirty) return true;

            var choice = await _notifier.AskSaveChangesAsync(
                "Save your changes?",
                $"You have changes that are not saved. Save them before you {action.ToLowerInvariant()}?");

            return choice switch
            {
                ConfirmDialogWindow.SaveChangesChoice.Discard => true,
                ConfirmDialogWindow.SaveChangesChoice.Save => await SaveForExitAsync() && !IsDirty,
                _ => false
            };
        }
        catch (Exception ex)
        {
            _faults.Fatal("PROJECT", $"Your work could not be saved before '{action}'. The editor remains open; retry or explicitly discard changes.", ex);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    private async Task<bool> SaveForExitAsync()
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            return await WriteToAsync(CurrentPath!, announce: true);
        string? video = _viewModel.LoadedVideoPath;
        if (string.IsNullOrWhiteSpace(video)) return true;
        return await WriteToAsync(null, announce: true, closeVideo: video);
    }

    private bool HasUnsavedWorkSafely()
    {
        try
        {
            return _hasUnsavedWork();
        }
        catch (Exception ex)
        {
            _faults.Recoverable("PROJECT", $"HasUnsavedWork check failed, assuming nothing to save: {ex.Message}", ex);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    public ProjectDocument Capture() => Capture(forExplicitSave: false);

    public ProjectDocument Capture(bool forExplicitSave)
    {
        var timeline = _viewModel.Timeline;
        string path = _viewModel.LoadedVideoPath ?? string.Empty;
        (int width, int height, double fps) = ProbeMetricsSafely();

        double cutStartMs = timeline.IsTrimStartSet ? timeline.TrimStartMs : 0;
        double trimmedMs = timeline.IsTrimEndSet && timeline.TrimEndMs > cutStartMs
            ? timeline.TrimEndMs - cutStartMs
            : 0;

        MusicWizardResult? music = _viewModel.MusicWizardResult;
        VoiceOverWindow.VoiceOverResult? voice = _viewModel.VoiceOverResult;

        return new ProjectDocument
        {
            Source = ProbeSourceCached(path, timeline.LoadedVideoDurationMs, width, height, fps),
            BaseSpeed = timeline.BaseSpeed,
            SourceCutStartMs = cutStartMs,
            TrimmedDurationMs = trimmedMs,
            Segments = timeline.SpeedSegments.ToArray(),
            Cuts = CutRange.ToClipRelative(timeline.Cuts, cutStartMs).ToArray(),
            Memes = timeline.MemePlacements.ToArray(),
            Audio = new ProjectAudio
            {
                MusicFilePath = _viewModel.IsMusicActive ? music?.MusicFilePath : null,
                MusicStartSec = music?.OffsetSeconds ?? 0,
                MusicVolume = music?.MusicVolume ?? 1.0,
                VideoVolume = music?.VideoVolume ?? 1.0,
                SidechainDucking = music?.EnableDucking ?? false,
                VoiceOverFilePath = voice?.VoiceOverWavPath,
                VoiceOverAtOutputSec = voice?.VoiceOverStartTimestampSec ?? 0,
                VoiceOverVolume = 1.0,
                VoiceOverTakes = voice?.VoiceOverTakes.ToArray() ?? [],
                VoiceOverDuckAudio = voice?.DuckAudio ?? false,
                VoiceOverProtectFromMusic = voice?.ProtectFromMusic ?? true,
            },
            Export = new ProjectExport
            {
                QualityIndex = _viewModel.QualitySliderValue,
                PortraitMode = _viewModel.IsPortraitMode,
                HardwareMode = "Auto",
            },
            Mask = ReadLiveMaskFastSafely(),
            Merge = ReadMergeQueueSafely(),
            Captions = _viewModel.CaptionTrack,
            Title = string.IsNullOrWhiteSpace(path) ? "Untitled" : Path.GetFileNameWithoutExtension(path),
            ModifiedUtc = _clock.UtcNow,
        };
    }

    private void Apply(ProjectDocument document)
    {
        _applying = true;
        try
        {
            var timeline = _viewModel.Timeline;

            timeline.BaseSpeed = document.BaseSpeed;

            timeline.SpeedSegments.Clear();
            timeline.SpeedSegments.AddRange(document.Segments);

            timeline.Cuts.Clear();
            foreach (OutputTimeline.Cut cut in document.Cuts)
            {
                timeline.Cuts.Add(new CutRange(
                    document.SourceCutStartMs + (cut.StartSec * 1000.0),
                    document.SourceCutStartMs + (cut.EndSec * 1000.0)));
            }

            timeline.MemePlacements.Clear();
            timeline.MemePlacements.AddRange(document.Memes);

            if (document.SourceCutStartMs > 0.001)
            {
                timeline.TrimStartMs = document.SourceCutStartMs;
                timeline.IsTrimStartSet = true;
            }

            if (document.TrimmedDurationMs > 0.001)
            {
                timeline.TrimEndMs = document.SourceCutStartMs + document.TrimmedDurationMs;
                timeline.IsTrimEndSet = true;
            }
            timeline.MarkMemesAnchored();

            _viewModel.IsPortraitMode = document.Export.PortraitMode;
            if (document.Export.QualityIndex >= 0)
                _viewModel.QualitySliderValue = document.Export.QualityIndex;

            _viewModel.IsMusicActive = !string.IsNullOrWhiteSpace(document.Audio.MusicFilePath);
            _viewModel.VoiceOverResult = document.Audio.VoiceOverTakes.Count > 0 || document.Audio.VoiceOverFilePath != null
                ? new VoiceOverWindow.VoiceOverResult
                {
                    VoiceOverWavPath = document.Audio.VoiceOverFilePath,
                    VoiceOverStartTimestampSec = document.Audio.VoiceOverAtOutputSec,
                    VoiceOverTakes = document.Audio.VoiceOverTakes.ToList(),
                    DuckAudio = document.Audio.VoiceOverDuckAudio,
                    ProtectFromMusic = document.Audio.VoiceOverProtectFromMusic
                } : null;
            _viewModel.CaptionTrack = document.Captions;
        }
        finally
        {
            _applying = false;
        }

        ReportMaskDriftIfAny(document);

        _dirty = true;
        _editGeneration++;
        DocumentApplied?.Invoke(this, document);
        Raise();
    }

    private void ReportMaskDriftIfAny(ProjectDocument document)
    {
        if (document.Mask is not { } saved) return;

        ProjectMask? live = ReadLiveMaskFastSafely();

        if (live is null) return;

        if (saved.MatchesLive(live.Config)) return;

        bool renamed = !string.Equals(saved.ProfileName, live.ProfileName, StringComparison.OrdinalIgnoreCase);

        _faults.Degraded("PROJECT",
            renamed
                ? $"This project was built with the \"{saved.ProfileName}\" HUD mask, but \"{live.ProfileName}\" "
                + "is active now — the export will look different. Editing and export still work; switch the "
                + "mask in the Crop Tool if you want the original look."
                : $"The \"{saved.ProfileName}\" HUD mask has been edited since this project was saved, so the "
                + "export will look different. Editing and export still work; the project remembers the mask "
                + "it was built with.",
            technicalDetail: $"saved fingerprint {saved.Fingerprint}, live {live.Fingerprint}");
    }

    private SourceClip ProbeSourceCached(string path, double durationMs, int width, int height, double fps)
    {
        var cached = _probedSource;
        bool matches = cached.HasValue && string.Equals(cached.Value.Path, path, StringComparison.OrdinalIgnoreCase);
        return new SourceClip
        {
            FilePath = path, DurationMs = durationMs, Width = width, Height = height, Fps = fps,
            SizeBytes = matches ? cached!.Value.Size : 0,
            ModifiedUtcSeconds = matches ? cached!.Value.Modified : 0,
        };
    }

    private ProjectMask? ReadLiveMaskFastSafely()
        => _faults.GuardValue<ProjectMask?>(
            "PROJECT",
            "The HUD mask could not be read, so this save will not record which mask you were using. "
          + "Your edit is saved and export still works — reopening will just use whichever mask is active then.",
            _readLiveMaskFast,
            fallback: null);

    private async Task<bool> WriteToAsync(string? path, bool announce, string? closeVideo = null)
    {
        ProjectDocument document = Capture();
        long session = _sessionGeneration, generation = _editGeneration, sequence = ++_writeSequence;
        var undo = _history?.UndoEntries.ToArray();
        var redo = _history?.RedoEntries.ToArray();
        string? fingerprint = _history is { } history ? HistoryFingerprint(history.Current) : null;
        var saved = await EnqueueIo(() =>
        {
            string? target = path;
            string? reserved = null;
            try
            {
                if (target is null)
                {
                    string directory = Path.GetDirectoryName(closeVideo!) ?? ".";
                    string stem = Path.GetFileNameWithoutExtension(closeVideo!);
                    for (int i = 1; i <= 1000; i++)
                    {
                        string candidate = Path.Combine(directory, stem + (i == 1 ? "" : $" ({i})") + ProjectDocument.FileExtension);
                        try
                        {
                            using var claim = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            reserved = target = candidate;
                            break;
                        }
                        catch (IOException ex) when (File.Exists(candidate)) { RuntimeLog.Swallowed(ex); }
                    }
                    if (target is null) throw new IOException("No unused project name was available beside the video.");
                }
                SourceClip source = document.Source;
                ProjectDocument refreshed = document with
                {
                    Source = SourceClip.Probe(source.FilePath, source.DurationMs, source.Width, source.Height, source.Fps),
                    Mask = _hasFastMask ? document.Mask : ReadLiveMaskSafely(),
                };
                ProjectIoResult result = SaveSnapshot(refreshed, target);
                if (result.Success)
                {
                    reserved = null;
                    if (fingerprint is not null) _sidecar.Save(target, HistoryFingerprint(refreshed), undo!, redo!);
                }
                return (Result: result, Document: refreshed);
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
                return (Result: ProjectIoResult.Fail(ex.Message), Document: document);
            }
            finally
            {
                if (reserved is not null)
                {
                    try { if (new FileInfo(reserved).Length == 0) File.Delete(reserved); }
                    catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
        });
        if (!saved.Result.Success)
        {
            _faults.Fatal("PROJECT", "Your project could not be saved. Your edits remain open." + Environment.NewLine + saved.Result.Error);
            return false;
        }
        if (session == _sessionGeneration && sequence >= _publishedSequence)
        {
            _publishedSequence = sequence;
            CurrentPath = saved.Result.Path;
            _probedSource = (saved.Document.Source.FilePath, saved.Document.Source.SizeBytes, saved.Document.Source.ModifiedUtcSeconds);
            if (generation == _editGeneration) _dirty = false;
            _lastAutosaveUtc = _clock.UtcNow;
            Raise();
        }
        if (announce) _notifier.Notify($"Saved {Path.GetFileNameWithoutExtension(saved.Result.Path)}", NoticeKind.Success);
        return true;
    }

    private ProjectIoResult SaveSnapshot(ProjectDocument document, string path)
    {
        try { return _store.Save(document, path); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return ProjectIoResult.Fail(ex.Message); }
    }

    private async Task<bool> RunCommandAsync(Func<Task<bool>> command)
    {
        if (_explicitIoPending)
        {
            _notifier.Notify("A project operation is still running. Please try again when it finishes.", NoticeKind.Info);
            return false;
        }
        _explicitIoPending = true;
        try { return await command(); }
        catch (Exception ex)
        {
            _faults.Fatal("PROJECT", "The project operation could not finish. Your edits remain open.", ex);
            return false;
        }
        finally { _explicitIoPending = false; }
    }

    private Task<T> EnqueueIo<T>(Func<T> work)
    {
        Task previous = _ioTail;
        Task<T> next = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            return work();
        });
        _ioTail = next;
        return next;
    }

    private string SuggestFileName()
    {
        string? video = _viewModel.LoadedVideoPath;
        string stem = string.IsNullOrWhiteSpace(video) ? "Untitled" : Path.GetFileNameWithoutExtension(video);
        return stem + ProjectDocument.FileExtension;
    }

    private static string HistoryFingerprint(ProjectDocument document)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{document.Source.FilePath}|{document.Source.SizeBytes}|{document.Source.ModifiedUtcSeconds}|{document.SourceCutStartMs:F3}|{document.TrimmedDurationMs:F3}|{document.Segments.Count}|{document.Cuts.Count}|{document.Memes.Count}");

    private ProjectMask? ReadLiveMaskSafely()
        => _faults.GuardValue<ProjectMask?>(
            "PROJECT",
            "The HUD mask could not be read, so this save will not record which mask you were using. "
          + "Your edit is saved and export still works — reopening will just use whichever mask is active then.",
            _readLiveMask,
            fallback: null);

    private ProjectMerge? ReadMergeQueueSafely()
        => _faults.GuardValue<ProjectMerge?>(
            "PROJECT",
            "The merge queue could not be read, so this save will not record the clips you queued. "
          + "Everything else in the project is saved.",
            _readMergeQueue,
            fallback: null);

    private (int Width, int Height, double Fps) ProbeMetricsSafely()
        => _faults.GuardValue("PROJECT",
            "The video's size and frame rate could not be read, so they were left out of the saved project.",
            _probeVideoMetrics,
            (0, 0, 0.0),
            FaultTier.Recoverable);

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
}
