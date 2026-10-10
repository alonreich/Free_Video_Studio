using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Abstractions;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Project;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// AUTOSAVEBG_01 — <see cref="ProjectSession.AutosaveTick"/> must never perform the atomic write,
/// the hardware flush or the sidecar serialisation on the calling (UI) thread, and must never
/// throw there. The disk work is proven to be off-thread by a store that BLOCKS: if the tick were
/// synchronous it could not return while the store is still holding.
/// </summary>
public sealed class AutosaveBackgroundTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsAutosaveBg_" + Guid.NewGuid().ToString("N"));
    private readonly string _video;
    private readonly string _project;

    public AutosaveBackgroundTests()
    {
        Directory.CreateDirectory(_root);
        _video = Path.Combine(_root, "clip.mp4");
        File.WriteAllBytes(_video, new byte[] { 0, 1, 2, 3 });
        _project = Path.Combine(_root, "clip.fvsproj");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [AvaloniaFact]
    public async Task AutosaveTick_ReturnsBeforeTheDiskWrite_AndWritesOffTheCallingThread()
    {
        var store = new GatedStore();
        var faults = new RecordingFaults();
        (ProjectSession session, ManualClock clock) = await DirtySessionWithPathAsync(store, faults);

        store.Block();
        clock.Advance(TimeSpan.FromSeconds(ProjectSession.AutosaveIntervalSeconds + 1));
        int callingThread = Environment.CurrentManagedThreadId;

        session.AutosaveTick();   // must return while the store is still blocked

        Assert.True(store.Entered.Wait(TimeSpan.FromSeconds(10)), "The background write never started.");
        Assert.True(session.IsDirty, "Dirty must not clear before the write has landed.");
        Assert.NotEqual(callingThread, store.LastSaveThread);

        store.Release();
        await session.PendingAutosave.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(session.IsDirty);
        Assert.Empty(faults.Fatals);
        Assert.Equal(2, store.SaveCount);   // the initial Save As + the autosave
    }

    [AvaloniaFact]
    public async Task AutosaveTick_StoreThrows_DoesNotThrowOnCaller_AndReportsFatal()
    {
        var store = new GatedStore();
        var faults = new RecordingFaults();
        (ProjectSession session, ManualClock clock) = await DirtySessionWithPathAsync(store, faults);

        store.ThrowOnSave = true;
        clock.Advance(TimeSpan.FromSeconds(ProjectSession.AutosaveIntervalSeconds + 1));

        Exception? thrown = Record.Exception(session.AutosaveTick);
        Assert.Null(thrown);

        await session.PendingAutosave.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(session.IsDirty, "A failed autosave must leave the project dirty.");
        Assert.Single(faults.Fatals);
    }

    [AvaloniaFact]
    public async Task AutosaveTick_EditDuringWrite_KeepsProjectDirty()
    {
        var store = new GatedStore();
        var faults = new RecordingFaults();
        (ProjectSession session, ManualClock clock) = await DirtySessionWithPathAsync(store, faults);

        store.Block();
        clock.Advance(TimeSpan.FromSeconds(ProjectSession.AutosaveIntervalSeconds + 1));
        session.AutosaveTick();
        Assert.True(store.Entered.Wait(TimeSpan.FromSeconds(10)));

        // An edit lands while the snapshot is on its way to disk.
        _vm!.IsPortraitMode = !_vm.IsPortraitMode;
        session.PushEdit("toggle portrait");

        store.Release();
        await session.PendingAutosave.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(session.IsDirty, "The file on disk is behind the screen; dirty must survive.");
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private MainViewModel? _vm;

    private async Task<(ProjectSession, ManualClock)> DirtySessionWithPathAsync(GatedStore store, RecordingFaults faults)
    {
        var paths = new ApplicationPaths(_root);
        _vm = new MainViewModel(paths) { LoadedVideoPath = _video };
        Assert.True(_vm.HasLoadedVideo);

        var clock = new ManualClock();
        var session = new ProjectSession(
            store,
            new FixedPicker(_project),
            new SilentNotifier(),
            faults,
            clock,
            _vm,
            probeVideoMetrics: static () => (1920, 1080, 60.0),
            hasUnsavedWork: static () => true,
            sidecar: new UndoSidecarStore(Path.Combine(_root, "History")));

        session.BeginHistory();
        Assert.True(await session.SaveAsAsync());   // binds CurrentPath through the real write path

        _vm.IsPortraitMode = !_vm.IsPortraitMode;
        session.PushEdit("toggle portrait");
        Assert.True(session.IsDirty);

        return (session, clock);
    }

    private sealed class GatedStore : IProjectStore
    {
        private readonly ManualResetEventSlim _gate = new(initialState: true);
        private int _saveCount;

        public ManualResetEventSlim Entered { get; } = new(initialState: false);
        public volatile bool ThrowOnSave;
        public int LastSaveThread { get; private set; }
        public int SaveCount => Volatile.Read(ref _saveCount);

        public void Block() { Entered.Reset(); _gate.Reset(); }
        public void Release() => _gate.Set();

        public ProjectIoResult Save(ProjectDocument document, string path)
        {
            LastSaveThread = Environment.CurrentManagedThreadId;
            Entered.Set();
            if (ThrowOnSave) throw new IOException("Simulated disk failure.");
            _gate.Wait(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref _saveCount);
            return ProjectIoResult.Ok(path);
        }

        public ProjectDocument? Load(string path, out string? error, out bool loadedFromBackup)
        {
            error = "not supported";
            loadedFromBackup = false;
            return null;
        }

        public string NormalizeExtension(string path) => ProjectStore.NormalizeExtension(path);
    }

    private sealed class FixedPicker(string path) : IFilePickerService
    {
        public Task<string?> SaveFileAsync(FilePickerRequest request) => Task.FromResult<string?>(path);
        public Task<string?> OpenFileAsync(FilePickerRequest request) => Task.FromResult<string?>(path);
    }

    private sealed class SilentNotifier : IUserNotifier
    {
        public void Notify(string text, NoticeKind kind = NoticeKind.Info) { }
        public void Alert(string title, string message) { }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) => Task.FromResult(false);
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
}
