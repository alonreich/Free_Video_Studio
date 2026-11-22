using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Abstractions;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using FreeVideoStudio.Core.Undo;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class ProjectSafetyQaTests : IDisposable
{
    private readonly string _root;
    private readonly IDisposable _programData;
    private readonly ApplicationPaths _paths;
    private readonly ScriptedPicker _picker = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingFaults _faults = new();
    private readonly ManualClock _clock = new();
    private bool _servicesInstalled;

    public ProjectSafetyQaTests()
    {
        _root = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "ProjectSafetyQa", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _programData = LiveCaptureDriver.IsolateProgramData(_root);
        _paths = new ApplicationPaths(_root);
    }

    public void Dispose()
    {
        if (_servicesInstalled) AppServices.ResetForTests();
        _programData.Dispose();
    }

    [AvaloniaFact]
    public async Task EditedVideo_ClosingTheAppAsksToSaveChanges()
    {
        var editor = await EditorWithVideoAsync();
        MarkEndAt(editor, 12);
        bool closes = await (Task<bool>)Call(editor.Window, "ConfirmProjectDiscardOnCloseAsync")!;
        Assert.True(_notifier.SavePrompts == 1, "Closing after an edit must ask Save / Back to editing / Discard; the app closed silently and the edit was lost.");
        Assert.False(closes);
    }

    [AvaloniaFact]
    public async Task EditedVideo_OpeningAnotherVideoAsksFirst()
    {
        var editor = await EditorWithVideoAsync();
        MarkEndAt(editor, 12);
        bool replaces = await (Task<bool>)Call(editor.Window, "ConfirmReplaceLoadedVideoAsync", NewFile("other.mp4"))!;
        Assert.True(_notifier.SavePrompts == 1, "NEWVIDEOGUARD_01: loading another video over edits must ask first; the edits were wiped without a question.");
        Assert.False(replaces);
    }

    [AvaloniaFact]
    public async Task EditedVideo_IsKeptForCrashRecovery()
    {
        var editor = await EditorWithVideoAsync();
        MarkEndAt(editor, 12);
        Call(editor.Window, "SaveRecoveryState", true, false, "flush", null);
        string recoveryFile = ApplicationPaths.CreateDefault().RecoveryStateFile;
        Assert.True(File.Exists(recoveryFile), "RECOVERYFULL_01: after an edit the crash-recovery file must hold the work; it was cleared instead.");
        Assert.Contains(Path.GetFileName(editor.Video), File.ReadAllText(recoveryFile), StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task EditedVideo_SaveProjectWritesAProjectForThatVideo()
    {
        var editor = await EditorWithVideoAsync();
        MarkEndAt(editor, 12);
        string project = Path.Combine(_root, "montage.fvsproj");
        _picker.NextSave = project;
        bool saved = await Session(editor.Window).SaveAsAsync();
        Assert.True(saved, "Ctrl+Shift+S with a loaded, edited video must save; it answered: " + string.Join(" | ", _notifier.Notices));
        ProjectDocument? reopened = ProjectStore.Load(project, out _, out _);
        Assert.NotNull(reopened);
        Assert.Equal(editor.Video, reopened!.Source.FilePath, ignoreCase: true);
    }

    [AvaloniaFact]
    public async Task UndoingAddMusic_LeavesNoMusicInTheExport()
    {
        var editor = await EditorWithVideoAsync();
        editor.Vm.MusicWizardResult = Music(NewFile("song.mp3"), 0, 30);
        Call(editor.Window, "SetMusicButtonActive", true);
        ProjectSession session = Session(editor.Window);
        Assert.True(session.CanUndo);
        session.Undo();
        Assert.False(editor.Vm.IsMusicActive);
        var payload = await (Task<ExportPayload>)Call(editor.Window, "ComposeExportPayloadAsync", _root, null, 3, null, null)!;
        Assert.True(payload.MusicTracks is null || payload.MusicTracks.Count == 0,
            $"Ctrl+Z removed the music from the editor, but the export still carries {payload.MusicTracks?.Count} music segment(s).");
    }

    [AvaloniaFact]
    public async Task NewVideoAfterSavingAProject_SavingNeverOverwritesThatProject()
    {
        string videoA = NewFile("a.mp4");
        string projectA = Path.Combine(_root, "a.fvsproj");
        var (session, vm) = NewSession(videoA);
        _picker.NextSave = projectA;
        Assert.True(await session.SaveAsAsync());
        vm.ResetEditingStateForNewVideo();
        vm.LoadedVideoPath = NewFile("c.mp4");
        session.BeginHistory();
        vm.Timeline.Cuts.Add(new CutRange(1000, 2000));
        session.PushEdit("cut");
        _clock.Advance(TimeSpan.FromSeconds(ProjectSession.AutosaveIntervalSeconds + 1));
        session.AutosaveTick();
        await session.PendingAutosave;
        _picker.NextSave = Path.Combine(_root, "c.fvsproj");
        await session.SaveAsync();
        ProjectDocument? stillA = ProjectStore.Load(projectA, out _, out _);
        Assert.NotNull(stillA);
        Assert.True(string.Equals(videoA, stillA!.Source.FilePath, StringComparison.OrdinalIgnoreCase),
            $"a.fvsproj was overwritten with another video's edit (now points at '{stillA.Source.FilePath}').");
        Assert.Empty(stillA.Cuts);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpeningAProject_SavingKeepsThatProjectsVideo(bool anotherVideoLoaded)
    {
        string videoA = NewFile("a.mp4");
        string projectA = Path.Combine(_root, "a.fvsproj");
        Assert.True(ProjectStore.Save(Document(videoA) with { Cuts = new[] { new OutputTimeline.Cut(2.0, 3.0) } }, projectA).Success);
        var (session, _) = NewSession(anotherVideoLoaded ? NewFile("b.mp4") : null);
        _picker.Opens.Enqueue(projectA);
        Assert.True(await session.OpenAsync());
        Assert.True(await session.SaveAsync());
        ProjectDocument? reopened = ProjectStore.Load(projectA, out _, out _);
        Assert.NotNull(reopened);
        Assert.True(string.Equals(videoA, reopened!.Source.FilePath, StringComparison.OrdinalIgnoreCase),
            $"Open then Save re-pointed the project from '{videoA}' to '{reopened.Source.FilePath}'.");
    }

    [AvaloniaFact]
    public async Task SavedProject_KeepsTheFreezeFrameAndOverlayText()
    {
        string video = NewFile("a.mp4");
        string project = Path.Combine(_root, "a.fvsproj");
        var (session, vm) = NewSession(video);
        vm.Timeline.FreezeTimeMs = 5000;
        vm.Timeline.FreezeDurationS = 2.0;
        vm.OverlayText = "GG EZ";
        session.PushEdit("freeze");
        _picker.NextSave = project;
        Assert.True(await session.SaveAsAsync());
        var (reopenedSession, reopenedVm) = NewSession(video);
        _picker.Opens.Enqueue(project);
        Assert.True(await reopenedSession.OpenAsync());
        Assert.Equal(5000, reopenedVm.Timeline.FreezeTimeMs);
        Assert.Equal(2.0, reopenedVm.Timeline.FreezeDurationS);
        Assert.Equal("GG EZ", reopenedVm.OverlayText);
    }

    [AvaloniaFact]
    public async Task CaptionOnlyChange_IsUnsavedAndSurvivesUndoOfAnEarlierEdit()
    {
        string video = NewFile("a.mp4");
        var (session, vm) = NewSession(video);
        _picker.NextSave = Path.Combine(_root, "a.fvsproj");
        Assert.True(await session.SaveAsAsync());
        vm.IsPortraitMode = !vm.IsPortraitMode;
        session.PushEdit("toggle portrait");
        Assert.True(await session.SaveAsync());
        Assert.False(session.IsDirty);
        vm.CaptionTrack = new CaptionTrack(Lines: new[] { new CaptionLine(1, 1.0, 2.5, "gg") });
        session.PushEdit("granular speed edits");
        bool dirtyAfterCaptions = session.IsDirty;
        vm.Timeline.TrimEndMs = 8000;
        vm.Timeline.IsTrimEndSet = true;
        session.PushEdit("mark end");
        session.Undo();
        Assert.True(vm.CaptionTrack is { Lines.Count: 1 }, "Undoing 'mark end' also deleted the captions that were added before it.");
        Assert.True(dirtyAfterCaptions, "Adding captions must mark the project as unsaved.");
    }

    [AvaloniaFact]
    public async Task OpeningAnOlderProjectWithMusic_UsesThatProjectsSong()
    {
        string video = NewFile("a.mp4");
        string hero = NewFile("hero.mp3");
        string project = Path.Combine(_root, "old.fvsproj");
        JsonObject json = ProjectSerializer.Write(Document(video) with
        {
            Audio = new ProjectAudio { MusicFilePath = hero, MusicStartSec = 4.5, MusicVolume = 0.7 }
        });
        json["schema_version"] = 8;
        var audio = (JsonObject)json["audio"]!;
        foreach (string key in new[] { "music_songs", "music_timeline_start_sec", "music_timeline_end_sec", "music_loop", "music_carving" })
            audio.Remove(key);
        AtomicJsonFile.WriteObject(project, json);
        var (session, vm) = NewSession(video);
        vm.MusicWizardResult = Music(NewFile("previous.mp3"), 0, 30);
        vm.IsMusicActive = true;
        _picker.Opens.Enqueue(project);
        Assert.True(await session.OpenAsync());
        Assert.True(vm.IsMusicActive);
        Assert.Equal(hero, vm.MusicWizardResult?.MusicFilePath);
    }

    [AvaloniaFact]
    public async Task AutosaveDuringADialDrag_KeepsTheProjectUnsavedUntilTheFinalValueIsWritten()
    {
        string video = NewFile("a.mp4");
        string project = Path.Combine(_root, "a.fvsproj");
        var (session, vm) = NewSession(video);
        _picker.NextSave = project;
        Assert.True(await session.SaveAsAsync());
        _clock.Advance(TimeSpan.FromSeconds(ProjectSession.AutosaveIntervalSeconds + 1));
        vm.Timeline.BaseSpeed = 1.5;
        session.PushEdit("change speed", "speed-dial");
        session.AutosaveTick();
        vm.Timeline.BaseSpeed = 2.0;
        session.PushEdit("change speed", "speed-dial");
        session.EndGesture();
        await session.PendingAutosave;
        Assert.Equal(1.5, ProjectStore.Load(project, out _, out _)!.BaseSpeed);
        Assert.True(session.IsDirty, "The file holds 1.5x but the editor shows 2.0x; the project must stay unsaved so closing asks to save.");
    }

    [AvaloniaFact]
    public async Task OpeningAProjectWhoseVideoMoved_StillOpens()
    {
        string project = Path.Combine(_root, "moved.fvsproj");
        Assert.True(ProjectStore.Save(Document(Path.Combine(_root, "moved-away", "a.mp4")) with
        {
            Cuts = new[] { new OutputTimeline.Cut(2.0, 3.0) }
        }, project).Success);
        var (session, vm) = NewSession(null);
        _picker.Opens.Enqueue(project);
        bool opened = await session.OpenAsync();
        Assert.True(opened, "06 PROJ-INTEGRITY: 'Missing never blocks opening. The project loads so the user can relink.'");
        Assert.Empty(_faults.Fatals);
        Assert.Single(vm.Timeline.Cuts);
    }

    [AvaloniaFact]
    public async Task SavingAProject_AddsItToTheRecentProjectsList()
    {
        var (session, _) = NewSession(NewFile("a.mp4"));
        string project = Path.Combine(_root, "a.fvsproj");
        _picker.NextSave = project;
        Assert.True(await session.SaveAsAsync());
        Assert.Contains(new RecentProjects(_paths).Read(), r => string.Equals(r.Path, project, StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task SavedProjectWithTheSameSongTwice_ReopensWithBothParts()
    {
        string video = NewFile("a.mp4");
        string song = NewFile("song.mp3");
        string project = Path.Combine(_root, "a.fvsproj");
        var (session, vm) = NewSession(video);
        vm.MusicWizardResult = new MusicWizardResult
        {
            MusicFilePath = song,
            MusicFilePaths = new List<string> { song, song },
            MusicDurationsSeconds = new List<double> { 60, 60 },
            MusicStartsSeconds = new List<double> { 5, 30 },
            MusicEndsSeconds = new List<double> { 15, 45 },
            OffsetSeconds = 5,
            MusicDurationSeconds = 60,
            TimelineStartSeconds = 0,
            TimelineEndSeconds = 20,
            MusicVolume = 0.6,
            VideoVolume = 0.9,
            EnableCarving = true,
        };
        vm.IsMusicActive = true;
        session.PushEdit("music");
        _picker.NextSave = project;
        Assert.True(await session.SaveAsAsync());
        var (reopenedSession, reopenedVm) = NewSession(video);
        _picker.Opens.Enqueue(project);
        Assert.True(await reopenedSession.OpenAsync());
        MusicWizardResult music = Assert.IsType<MusicWizardResult>(reopenedVm.MusicWizardResult);
        Assert.True(reopenedVm.IsMusicActive);
        Assert.Equal(new[] { song, song }, music.MusicFilePaths);
        Assert.Equal(new[] { 5.0, 30.0 }, music.MusicStartsSeconds);
        Assert.Equal(new[] { 15.0, 45.0 }, music.MusicEndsSeconds);
        Assert.Equal(0.6, music.MusicVolume, 6);
        Assert.Equal(0.9, music.VideoVolume, 6);
        Assert.True(music.EnableCarving);
    }

    [AvaloniaFact]
    public void UndoThenRedoOfAddMusic_RestoresTheSameSongAndPart()
    {
        string song = NewFile("song.mp3");
        var (session, vm) = NewSession(NewFile("a.mp4"));
        vm.MusicWizardResult = Music(song, 7, 19);
        vm.IsMusicActive = true;
        session.PushEdit("music");
        session.Undo();
        Assert.False(vm.IsMusicActive);
        session.Redo();
        Assert.True(vm.IsMusicActive);
        Assert.Equal(song, vm.MusicWizardResult!.MusicFilePath);
        Assert.Equal(7.0, vm.MusicWizardResult.MusicStartsSeconds[0]);
        Assert.Equal(19.0, vm.MusicWizardResult.MusicEndsSeconds[0]);
    }

    private (ProjectSession Session, MainViewModel Vm) NewSession(string? video)
    {
        var vm = new MainViewModel(_paths) { LoadedVideoPath = video };
        var session = new ProjectSession(
            FileProjectStore.Instance,
            _picker,
            _notifier,
            _faults,
            _clock,
            vm,
            probeVideoMetrics: static () => (1920, 1080, 60.0),
            hasUnsavedWork: static () => true,
            sidecar: new UndoSidecarStore(Path.Combine(_root, "History")));
        session.BeginHistory();
        return (session, vm);
    }

    private async Task<Editor> EditorWithVideoAsync()
    {
        AppServices.InitializeForTests(_paths, _clock, new NoActiveWindow(), _notifier, _faults, FileProjectStore.Instance, _picker);
        _servicesInstalled = true;
        VideoRenderMode.Initialize();
        var window = new MainWindow();
        var ipc = new MpvIpcClient();
        ipc.SetStateForTesting(0, 20, isPaused: true, isEof: false);
        var host = new MpvVideoView();
        host.SetIpcClientForTesting(ipc);
        window.SetActiveVideoHostForTesting(host);
        string video = NewFile("clip.mp4");
        await (Task)Call(window, "LoadVideoIntoEditorAsync", video, "uploaded")!;
        return new Editor(window, (MainViewModel)window.DataContext!, ipc, video);
    }

    private static void MarkEndAt(Editor editor, double seconds)
    {
        editor.Ipc.SetStateForTesting(seconds, 20, isPaused: true, isEof: false);
        editor.Window.ExecuteMarkEndForTesting();
        Assert.True(editor.Window.TrimEndSetForTesting);
    }

    private static ProjectSession Session(MainWindow window) =>
        (ProjectSession)typeof(MainWindow).GetField("_projectSession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private string NewFile(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 24 });
        return path;
    }

    private static ProjectDocument Document(string video) => new()
    {
        Source = SourceClip.Probe(video, 20_000, 1920, 1080, 60),
        Title = Path.GetFileNameWithoutExtension(video),
    };

    private static MusicWizardResult Music(string song, double inSec, double outSec) => new()
    {
        MusicFilePath = song,
        MusicFilePaths = new List<string> { song },
        MusicDurationsSeconds = new List<double> { 60 },
        MusicStartsSeconds = new List<double> { inSec },
        MusicEndsSeconds = new List<double> { outSec },
        OffsetSeconds = inSec,
        MusicDurationSeconds = 60,
        TimelineStartSeconds = 0,
        TimelineEndSeconds = 20,
    };

    private sealed record Editor(MainWindow Window, MainViewModel Vm, MpvIpcClient Ipc, string Video);

    private sealed class ScriptedPicker : IFilePickerService
    {
        public string? NextSave;
        public readonly Queue<string> Opens = new();
        public Task<string?> SaveFileAsync(FilePickerRequest request) => Task.FromResult(NextSave);
        public Task<string?> OpenFileAsync(FilePickerRequest request) => Task.FromResult<string?>(Opens.Count > 0 ? Opens.Dequeue() : null);
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        private int _savePrompts;
        public readonly List<string> Notices = new();
        public int SavePrompts => Volatile.Read(ref _savePrompts);
        public void Notify(string text, NoticeKind kind = NoticeKind.Info) { lock (Notices) Notices.Add(text); }
        public void Alert(string title, string message) { lock (Notices) Notices.Add(title + ": " + message); }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) => Task.FromResult(false);
        public Task<ConfirmDialogWindow.SaveChangesChoice> AskSaveChangesAsync(string title, string message)
        {
            Interlocked.Increment(ref _savePrompts);
            return Task.FromResult(ConfirmDialogWindow.SaveChangesChoice.BackToEditing);
        }
    }

    private sealed class RecordingFaults : IFaultSink
    {
        private readonly List<Fault> _faults = new();
        public IReadOnlyList<Fault> Fatals { get { lock (_faults) return _faults.Where(f => f.Tier == FaultTier.Fatal).ToArray(); } }
        public void Report(Fault fault) { lock (_faults) _faults.Add(fault); }
    }

    private sealed class ManualClock : IClock
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NoActiveWindow : IActiveWindowProvider
    {
        public Window? ActiveWindow => null;
    }
}
