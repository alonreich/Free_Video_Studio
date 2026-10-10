// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using NAudio.Wave;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// VORECOVERY_01 — Voice Over failed-take recovery transaction: durable per-session identity,
/// merge-on-write ownership, truthful storage results, off-dispatcher I/O and UI exclusion.
/// Real WAVs are test-owned in a unique temp directory; gated/failing stores use the
/// <see cref="IVoiceOverRecoveryStore"/> seam, never sleeps.
/// </summary>
public sealed class VoiceOverRecoveryTransactionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string Video = @"C:\clips\same.mp4";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FvsVoRecoveryTx_" + Guid.NewGuid().ToString("N"));

    public VoiceOverRecoveryTransactionTests()
    {
        Directory.CreateDirectory(_dir);
        VoiceOverWindow.RecoveryDirectorySeam = () => _dir;
        VoiceOverWindow.RecoveryStoreSeam = null;
    }

    public void Dispose()
    {
        VoiceOverWindow.RecoveryDirectorySeam = null;
        VoiceOverWindow.RecoveryStoreSeam = null;
        VoiceOverWindow.TrimRunnerSeam = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private VoiceOverRecoveryManager Manager(string video = Video, IVoiceOverRecoveryStore? store = null)
        => new(() => video, () => _dir, store);

    private string Wav(string name, double seconds = 1.0)
    {
        string path = Path.Combine(_dir, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        var buffer = new byte[(int)(44100 * seconds) * 2];
        writer.Write(buffer, 0, buffer.Length);
        return path;
    }

    private static VoiceOverWindow.VoiceOverSession Take(string wav, double start = 1, double end = 2)
        => new() { WavPath = wav, StartSec = start, EndSec = end };

    private static VoiceCaptureSession Capture() => new(new VoiceOverApplyTests.TestDevices(), a => a());

    private string[] Manifests() => Directory.GetFiles(_dir, "voiceover_recovery_*.json");

    private static CapturedTake FailedOutcome(string message = "Endpoint hang")
        => new(44100 * 2, 10, 0.5f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException(message));

    // ───────────────────────── storage / identity ─────────────────────────

    [Fact]
    public void TwoSessions_ResolvingOne_LeavesTheOtherSessionsFileAndRecordIntact()
    {
        var first = Manager();
        var second = Manager();
        string a = Wav("a.wav"), b = Wav("b.wav");
        Assert.True(first.RegisterFailedTake(Take(a), null, "first").Success);
        Assert.True(second.RegisterFailedTake(Take(b), null, "second").Success);
        Assert.Equal(2, Manifests().Length);

        using var capture = Capture();
        Assert.True(first.DiscardTakes(capture, out string? error), error);

        Assert.False(File.Exists(first.GetRecoveryManifestPath()), "resolved session's index is removed");
        Assert.True(File.Exists(second.GetRecoveryManifestPath()), "other session's index is untouched");
        Assert.True(File.Exists(b));
        var reopened = Manager();
        reopened.CheckAndOfferReopenRecovery();
        Assert.Single(reopened.PendingFailedTakes);
        Assert.Equal(b, reopened.PendingFailedTakes[0].Session.WavPath);
    }

    [Fact]
    public void Discovery_IsIdempotent_AndRoundTripsStableIdTimingTrimAndMute()
    {
        var writer = Manager();
        var session = new VoiceOverWindow.VoiceOverSession { WavPath = Wav("rt.wav"), StartSec = 3.25, EndSec = 5.5, TrimLeftSec = 0.25, TrimRightSec = 0.5, IsMuted = true };
        writer.RegisterFailedTake(session, null, "writer failure");
        string id = writer.PendingFailedTakes[0].Id;

        var reopened = Manager();
        reopened.CheckAndOfferReopenRecovery();
        reopened.CheckAndOfferReopenRecovery();
        var t = Assert.Single(reopened.PendingFailedTakes);
        Assert.Equal(id, t.Id);
        Assert.Equal(3.25, t.Session.StartSec);
        Assert.Equal(5.5, t.Session.EndSec);
        Assert.Equal(0.25, t.Session.TrimLeftSec);
        Assert.Equal(0.5, t.Session.TrimRightSec);
        Assert.True(t.Session.IsMuted);
        Assert.Equal(TakeRecoveryState.Pending, t.State);

        // A third manager after reopened rewrote nothing still sees the same single identity.
        var third = Manager();
        third.CheckAndOfferReopenRecovery();
        Assert.Equal(id, Assert.Single(third.PendingFailedTakes).Id);
    }

    [Fact]
    public void FailedAtomicWrite_ReturnsFailure_AndPreservesPreviousValidIndex()
    {
        var store = new ScriptedStore();
        var manager = Manager(store: store);
        string first = Wav("first.wav"), second = Wav("second.wav");
        Assert.True(manager.RegisterFailedTake(Take(first), null, "one").Success);
        string path = manager.GetRecoveryManifestPath();
        string before = File.ReadAllText(path);

        store.FailWrites = true;
        var result = manager.RegisterFailedTake(Take(second), null, "two");

        Assert.False(result.Success);
        Assert.Contains("could not be saved", result.Error);
        Assert.False(manager.LastStoreResult.Success);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(manager.HasUnresolvedFailedTakes, "in-memory takes are never dropped by a storage failure");
    }

    [Fact]
    public void Writes_UseUniqueTempFiles()
    {
        var store = new ScriptedStore();
        var a = Manager(store: store);
        var b = Manager(store: store);
        a.RegisterFailedTake(Take(Wav("ua.wav")), null, "a");
        b.RegisterFailedTake(Take(Wav("ub.wav")), null, "b");
        a.RegisterFailedTake(Take(Wav("uc.wav")), null, "c");
        Assert.Equal(3, store.TempPaths.Count);
        Assert.Equal(3, store.TempPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void UnreadableIndex_IsReported_AndNeverDeletedBySubsequentSaves()
    {
        string key = VoiceOverRecoveryManager.ComputeVideoKey(Video);
        string corrupt = Path.Combine(_dir, $"voiceover_recovery_{key}_deadbeef.json");
        File.WriteAllText(corrupt, "{ not json");

        var manager = Manager();
        manager.CheckAndOfferReopenRecovery();
        Assert.NotEmpty(manager.LoadErrors);
        manager.RegisterFailedTake(Take(Wav("x.wav")), null, "x");
        using var capture = Capture();
        manager.DiscardTakes(capture, out _);

        Assert.True(File.Exists(corrupt), "evidence of an unreadable index must not be deleted");
        Assert.Equal("{ not json", File.ReadAllText(corrupt));
    }

    [Fact]
    public void MissingAudio_RemainsAnActionableRecord_UntilExplicitDiscard()
    {
        var writer = Manager();
        string wav = Wav("gone.wav");
        writer.RegisterFailedTake(Take(wav), null, "writer failure");
        File.Delete(wav);

        var reopened = Manager();
        reopened.CheckAndOfferReopenRecovery();
        var t = Assert.Single(reopened.PendingFailedTakes);
        Assert.Equal(RecoveryErrorKind.Storage, t.ErrorKind);
        Assert.Contains("missing", t.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(reopened.HasUnresolvedFailedTakes);

        using var capture = Capture();
        var recovery = reopened.RecoverTakes(capture, _ => { });
        Assert.Equal(0, recovery.RecoveredCount);
        Assert.True(File.Exists(writer.GetRecoveryManifestPath()), "a failed recovery keeps the record");

        Assert.True(reopened.DiscardTakes(capture, out _));
        Assert.Empty(Manifests());
    }

    [Fact]
    public void UnboundVideo_NeverAdoptsBoundRecords_AndViceVersa()
    {
        var bound = Manager();
        bound.RegisterFailedTake(Take(Wav("bound.wav")), null, "bound");
        var unbound = Manager(video: "");
        unbound.RegisterFailedTake(Take(Wav("unbound.wav")), null, "unbound");
        Assert.Contains($"_{VoiceOverRecoveryManager.UnboundVideoKey}_", unbound.GetRecoveryManifestPath());

        var reUnbound = Manager(video: "");
        reUnbound.CheckAndOfferReopenRecovery();
        Assert.Equal(Path.Combine(_dir, "unbound.wav"), Assert.Single(reUnbound.PendingFailedTakes).Session.WavPath);

        var reBound = Manager();
        reBound.CheckAndOfferReopenRecovery();
        Assert.Equal(Path.Combine(_dir, "bound.wav"), Assert.Single(reBound.PendingFailedTakes).Session.WavPath);
    }

    [Fact]
    public void LegacyEntryWithoutId_IsAdoptedIdempotently_AndMergeDoesNotDuplicateIt()
    {
        string key = VoiceOverRecoveryManager.ComputeVideoKey(Video);
        string legacy = Path.Combine(_dir, $"voiceover_recovery_{key}.json");
        string wav = Wav("legacy.wav");
        string canonical = Path.GetFullPath(Video);
        File.WriteAllText(legacy, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["videoPath"] = canonical,
            ["failedTakes"] = new[] { new Dictionary<string, object> { ["wavPath"] = wav, ["startSec"] = 1.0, ["endSec"] = 2.0, ["reason"] = "old" } }
        }));

        var m = Manager();
        m.CheckAndOfferReopenRecovery();
        m.CheckAndOfferReopenRecovery();
        string id = Assert.Single(m.PendingFailedTakes).Id;
        Assert.StartsWith("legacy-", id);
        m.RegisterFailedTake(Take(Wav("new.wav")), null, "new");   // rewrites every tracked index
        using (var doc = JsonDocument.Parse(File.ReadAllText(legacy)))
            Assert.Single(doc.RootElement.GetProperty("failedTakes").EnumerateArray());

        var again = Manager();
        again.CheckAndOfferReopenRecovery();
        Assert.Equal(2, again.PendingFailedTakes.Count);
        Assert.Single(again.PendingFailedTakes, t => t.Id == id);
    }

    [Fact]
    public void ForeignRecordsNotHeld_ArePreservedByMergeOnWrite()
    {
        var owner = Manager();
        owner.RegisterFailedTake(Take(Wav("o1.wav")), null, "o1");
        string path = owner.GetRecoveryManifestPath();

        // Another writer appends an entry this manager does not hold.
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["failedTakes"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "foreign-1", ["wavPath"] = Wav("f1.wav"), ["startSec"] = 4.0, ["endSec"] = 5.0, ["reason"] = "foreign"
        });
        File.WriteAllText(path, json.ToJsonString());

        owner.RegisterFailedTake(Take(Wav("o2.wav")), null, "o2");
        var reopened = Manager();
        reopened.CheckAndOfferReopenRecovery();
        Assert.Equal(3, reopened.PendingFailedTakes.Count);
        Assert.Contains(reopened.PendingFailedTakes, t => t.Id == "foreign-1");
    }

    [Fact]
    public void Commit_RemovesOnlyCommittedIds()
    {
        var other = Manager();
        other.RegisterFailedTake(Take(Wav("other.wav")), null, "other session");
        var mine = Manager();
        string recovered = Wav("mine.wav");
        mine.RegisterFailedTake(Take(recovered), null, "mine");
        string pendingWav = Wav("mine_pending.wav");
        using var capture = Capture();
        Assert.Equal(1, mine.RecoverTakes(capture, _ => { }).RecoveredCount);
        mine.RegisterFailedTake(Take(pendingWav, 3, 4), null, "still pending");

        Assert.True(mine.CommitTakes(new[] { recovered, pendingWav }).Success);

        Assert.Equal(TakeRecoveryState.Committed, mine.PendingFailedTakes.Single(t => t.Session.WavPath == recovered).State);
        Assert.Equal(TakeRecoveryState.Pending, mine.PendingFailedTakes.Single(t => t.Session.WavPath == pendingWav).State);
        var reopened = Manager();
        reopened.CheckAndOfferReopenRecovery();
        Assert.DoesNotContain(reopened.PendingFailedTakes, t => t.Session.WavPath == recovered);
        Assert.Contains(reopened.PendingFailedTakes, t => t.Session.WavPath == pendingWav);
        Assert.Contains(reopened.PendingFailedTakes, t => t.Session.WavPath == Path.Combine(_dir, "other.wav"));
    }

    [Fact]
    public void RejectedPublication_IsNotACorruptAudioDiagnosis()
    {
        var manager = Manager();
        manager.RegisterFailedTake(Take(Wav("pub.wav")), null, "writer failure");
        using var capture = Capture();
        var result = manager.RecoverTakes(capture, _ => throw new InvalidOperationException("UI publication rejected"));
        Assert.Equal(1, result.FailedCount);
        var t = manager.PendingFailedTakes[0];
        Assert.Equal(TakeRecoveryState.Pending, t.State);
        Assert.Equal(RecoveryErrorKind.Publication, t.ErrorKind);
        Assert.DoesNotContain("Corrupt", t.FailureReason);
        Assert.True(File.Exists(manager.GetRecoveryManifestPath()));
    }

    [Fact]
    public async Task AsyncDiscard_DeleteFailure_KeepsRecordAndReportsError()
    {
        var store = new ScriptedStore { FailAudioDelete = true };
        var manager = Manager(store: store);
        string wav = Wav("locked.wav");
        manager.RegisterFailedTake(Take(wav), null, "x");
        using var capture = Capture();
        var (ok, error) = await manager.DiscardTakesAsync(capture);
        Assert.False(ok);
        Assert.Contains("locked", error);
        Assert.True(manager.HasUnresolvedFailedTakes);
        Assert.True(File.Exists(manager.GetRecoveryManifestPath()));
        Assert.True(File.Exists(wav));
    }

    // ───────────────────────── UI transaction ─────────────────────────

    private VoiceOverWindow WindowWithFailedTake(VoiceCaptureSession capture, out string wav)
    {
        var window = new VoiceOverWindow(capture);
        wav = Wav("ui_failed.wav", 1.5);
        window.TriggerCompleteTake(Take(wav, 0.5, 2.0), micWasOpen: true, capturedBytes: 44100 * 3, capturedBuffers: 10,
            capturedPeak: 0.5f, isTakeValid: false, takeOutcome: FailedOutcome());
        return window;
    }

    [AvaloniaFact]
    public async Task RecoverButton_ValidationRunsOffDispatcher_AndExcludesApplyDiscardAndClose()
    {
        var store = new ScriptedStore();
        VoiceOverWindow.RecoveryStoreSeam = store;
        using var capture = Capture();
        var window = WindowWithFailedTake(capture, out string wav);
        await window.RecoveryManager.WhenStoreIdle();

        store.ValidationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recover = window.RecoverFailedTakesAction();
        Assert.False(recover.IsCompleted);
        Assert.True(window.RecoveryManager.IsOperationInFlight);
        await store.ValidationEntered.Task.WaitAsync(Timeout);

        // Dispatcher heartbeat while validation is blocked on the store.
        bool heartbeat = false;
        Dispatcher.UIThread.Post(() => heartbeat = true);
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
        Assert.True(heartbeat, "the dispatcher must keep running while recovery I/O is blocked");

        // Conflicting operations are refused while the recovery owns the take list.
        Assert.False(await window.DiscardFailedTakesAction());
        Assert.True(File.Exists(wav));
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.Null(window.Result);
        Assert.False(window.IsCommitted);
        Assert.False(window.RecoverFailedTakesButtonControl!.IsEnabled);
        var closing = new CancelEventArgs();
        window.TriggerClosing(closing);
        Assert.True(closing.Cancel, "close is deferred while a recovery operation is in flight");

        store.ValidationGate.SetResult();
        Assert.True(await recover.WaitAsync(Timeout));
        Assert.False(window.RecoveryManager.IsOperationInFlight);
        Assert.Single(window.Sessions);
        Assert.False(window.HasUnresolvedFailedTakes);
        Assert.Equal(1, store.ValidateCalls);
    }

    [AvaloniaFact]
    public async Task CloseDuringRecovery_IsReissuedAfterCompletion_AndLatePublicationIsRefused()
    {
        var store = new ScriptedStore();
        VoiceOverWindow.RecoveryStoreSeam = store;
        using var capture = new VoiceCaptureSession(new VoiceOverApplyTests.TestDevices(), a => Dispatcher.UIThread.Post(a));
        var owner = new Avalonia.Controls.Window();
        owner.Show();
        var window = WindowWithFailedTake(capture, out string wav);
        await window.RecoveryManager.WhenStoreIdle();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var dialog = window.ShowDialog(owner);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        while (window.RecoveryManager.IsOperationInFlight) { await Task.Delay(10); }   // reopen discovery

        store.ValidationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recover = window.RecoverFailedTakesAction();
        await store.ValidationEntered.Task.WaitAsync(Timeout);
        window.IsSafeToClose = true;   // e.g. the user confirmed DISCARD in the close prompt
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(closed, "the close must wait for the in-flight recovery");

        store.ValidationGate.SetResult();
        await recover.WaitAsync(Timeout);
        await dialog.WaitAsync(Timeout);
        Assert.True(closed, "the deferred close is re-issued once the operation ends");
        Assert.True(File.Exists(wav), "the take stays on disk; nothing was committed");
        owner.Close();
    }

    [AvaloniaFact]
    public async Task FailedIndexWrite_IsSurfacedToTheUser_WithAudioRetained()
    {
        var store = new ScriptedStore { FailWrites = true };
        VoiceOverWindow.RecoveryStoreSeam = store;
        using var capture = Capture();
        var window = WindowWithFailedTake(capture, out string wav);
        var result = await window.RecoveryManager.WhenStoreIdle();
        Assert.False(result.Success);
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("recovery record could not be saved", window.LastApplyErrorMessage);
        Assert.True(File.Exists(wav));
        Assert.True(window.HasUnresolvedFailedTakes);
    }

    [AvaloniaFact]
    public async Task ReopenDiscovery_ThroughWindowCoordinator_IsAsync_AndBlocksApplyUntilLoaded()
    {
        var seed = Manager(video: "");
        seed.RegisterFailedTake(Take(Wav("seed.wav")), null, "previous crash");

        var store = new ScriptedStore { ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        VoiceOverWindow.RecoveryStoreSeam = store;
        using var capture = Capture();
        var window = new VoiceOverWindow(capture);
        window.Sessions.Add(Take(Wav("ok.wav"), 0, 1));

        var discovery = window.CheckAndOfferReopenRecoveryAsync();
        await store.ReadEntered.Task.WaitAsync(Timeout);
        Assert.True(window.RecoveryManager.IsOperationInFlight);
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.Null(window.Result);   // cannot apply past an undisclosed failed take

        store.ReadGate.SetResult();
        await discovery.WaitAsync(Timeout);
        Assert.True(window.HasUnresolvedFailedTakes);
        Assert.True(window.RecoverFailedTakesButtonControl!.IsVisible);
    }

    [AvaloniaFact]
    public async Task RecoverThenFailedApply_RetainsDurableRecord_ReopenSeesSameTake()
    {
        using var capture = Capture();
        var window = WindowWithFailedTake(capture, out string wav);
        await window.RecoveryManager.WhenStoreIdle();
        string id = window.PendingFailedTakes[0].Id;
        Assert.True(await window.RecoverFailedTakesAction());
        window.Sessions[0].TrimLeftSec = 0.2;   // forces the trim path
        VoiceOverWindow.TrimRunnerSeam = (_, _) => throw new IOException("disk full");

        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.Null(window.Result);
        Assert.False(window.IsCommitted);
        Assert.True(File.Exists(wav));

        var reopened = Manager(video: "");
        reopened.CheckAndOfferReopenRecovery();
        var t = Assert.Single(reopened.PendingFailedTakes);
        Assert.Equal(id, t.Id);
        Assert.Equal(wav, t.Session.WavPath);
        Assert.Equal(0.5, t.Session.StartSec);
    }

    [AvaloniaFact]
    public async Task RecoverThenSuccessfulApply_CleansOnlyCommittedRecord()
    {
        var otherSession = Manager(video: "");
        otherSession.RegisterFailedTake(Take(Wav("other_session.wav")), null, "other");

        using var capture = Capture();
        var window = WindowWithFailedTake(capture, out _);
        await window.RecoveryManager.WhenStoreIdle();
        Assert.True(await window.RecoverFailedTakesAction());
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.NotNull(window.Result);
        Assert.Single(window.Result!.VoiceOverTakes);

        Assert.False(File.Exists(window.GetRecoveryManifestPath()));
        Assert.True(File.Exists(otherSession.GetRecoveryManifestPath()));
        var reopened = Manager(video: "");
        reopened.CheckAndOfferReopenRecovery();
        Assert.Equal(Path.Combine(_dir, "other_session.wav"), Assert.Single(reopened.PendingFailedTakes).Session.WavPath);
    }

    [AvaloniaFact]
    public async Task DoubleRecoverClick_PublishesExactlyOnce()
    {
        using var capture = Capture();
        var window = WindowWithFailedTake(capture, out string wav);
        await window.RecoveryManager.WhenStoreIdle();
        var first = window.RecoverFailedTakesAction();
        var second = window.RecoverFailedTakesAction();
        Assert.False(await second);
        Assert.True(await first);
        Assert.False(await window.RecoverFailedTakesAction());
        Assert.Single(window.Sessions, s => s.WavPath == wav);
        Assert.Single(window.PendingFailedTakes);
    }

    /// <summary>Real file system with scriptable failures and explicit gates.</summary>
    private sealed class ScriptedStore : IVoiceOverRecoveryStore
    {
        private readonly FileVoiceOverRecoveryStore _inner = FileVoiceOverRecoveryStore.Instance;
        public volatile bool FailWrites;
        public volatile bool FailAudioDelete;
        public TaskCompletionSource? ValidationGate;
        public TaskCompletionSource? ReadGate;
        public readonly TaskCompletionSource ValidationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> TempPaths = new();
        public int ValidateCalls;

        public IReadOnlyList<string> EnumerateManifests(string directory, string searchPattern)
        {
            ReadEntered.TrySetResult();
            ReadGate?.Task.Wait(Timeout);
            return _inner.EnumerateManifests(directory, searchPattern);
        }

        public bool ManifestExists(string path) => _inner.ManifestExists(path);
        public string ReadManifest(string path) => _inner.ReadManifest(path);

        public void WriteManifestAtomic(string path, byte[] content)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            lock (TempPaths) TempPaths.Add(tmp);
            if (FailWrites) throw new IOException("injected write failure");
            File.WriteAllBytes(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }

        public void DeleteManifest(string path) => _inner.DeleteManifest(path);
        public bool AudioExists(string path) => _inner.AudioExists(path);

        public void DeleteAudio(string path)
        {
            if (FailAudioDelete) throw new IOException("Audio file locked by another process");
            _inner.DeleteAudio(path);
        }

        public RecoveryAudioVerdict ValidateAudio(string path)
        {
            Interlocked.Increment(ref ValidateCalls);
            ValidationEntered.TrySetResult();
            ValidationGate?.Task.Wait(Timeout);
            return _inner.ValidateAudio(path);
        }
    }
}
