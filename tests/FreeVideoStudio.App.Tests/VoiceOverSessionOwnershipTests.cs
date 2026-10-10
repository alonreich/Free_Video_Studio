// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using NAudio.Wave;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SESSIONOWNER_01 — one admission policy for capture / recovery / Apply / close (T1), and
/// VORECOVERY_02 — terminal resolutions dominate stale snapshots (T2).
/// </summary>
public sealed class VoiceOverSessionOwnershipTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string Video = @"C:\clips\owner.mp4";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FvsVoOwner_" + Guid.NewGuid().ToString("N"));

    public VoiceOverSessionOwnershipTests()
    {
        Directory.CreateDirectory(_dir);
        VoiceOverWindow.RecoveryDirectorySeam = () => _dir;
        VoiceOverWindow.RecoveryStoreSeam = null;
    }

    public void Dispose()
    {
        VoiceOverWindow.RecoveryDirectorySeam = null;
        VoiceOverWindow.RecoveryStoreSeam = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Wav(string name)
    {
        string path = Path.Combine(_dir, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        var buffer = new byte[44100 * 2];
        writer.Write(buffer, 0, buffer.Length);
        return path;
    }

    private static VoiceOverWindow.VoiceOverSession Take(string wav) => new() { WavPath = wav, StartSec = 1, EndSec = 2 };
    private static VoiceCaptureSession Capture() => new(new VoiceOverApplyTests.TestDevices(), a => a());
    private VoiceOverRecoveryManager Manager(IVoiceOverRecoveryStore? store = null) => new(() => Video, () => _dir, store);

    private static KeyEventArgs VKey() => new() { Key = Key.V, RoutedEvent = InputElement.KeyDownEvent };

    // ───────────────────────────── T1 ─────────────────────────────

    [AvaloniaFact]
    public async Task RecordClickKeyAndDirectEntry_AreRejected_WhileValidationOwnsTheSession()
    {
        var store = new GatedStore { GateValidation = true };
        VoiceOverWindow.RecoveryStoreSeam = store;
        using var capture = Capture();
        var window = new VoiceOverWindow(capture) { IsMpvReady = true };
        window.RecoveryManager.RegisterFailedTake(Take(Wav("t1.wav")), null, "failure");

        var recovery = window.RecoverFailedTakesAction();
        await store.Entered.Task.WaitAsync(Timeout);
        Assert.Equal(VoiceOverWindow.VoiceOverSessionOwner.Recovery, window.CurrentSessionOwner);

        window.TriggerKeyDown(VKey());                    // keyboard path → ToggleRecord
        Assert.False(window.IsRecordingLive);
        window.TriggerStartRecordingAndPlayback();        // direct entry
        Assert.False(window.IsRecordingLive);
        Assert.False(window.RecordButtonControl!.IsEnabled, "RECORD is disabled while recovery owns the session");

        store.Release.Set();
        Assert.True(await recovery.WaitAsync(Timeout));
        Assert.Equal(VoiceOverWindow.VoiceOverSessionOwner.None, window.CurrentSessionOwner);
        Assert.True(window.RecordButtonControl.IsEnabled, "the guard is released exactly when the operation ends");
        window.TriggerStartRecordingAndPlayback();
        Assert.True(window.IsRecordingLive, "recording is admitted again once recovery has finished");
    }

    [AvaloniaFact]
    public async Task Record_IsRejected_WhileDiscoveryAndDiscardAreGated()
    {
        var seed = new VoiceOverRecoveryManager(() => "", () => _dir);
        seed.RegisterFailedTake(Take(Wav("seed.wav")), null, "previous crash");

        var store = new GatedStore { GateRead = true };
        VoiceOverWindow.RecoveryStoreSeam = store;
        using var capture = Capture();
        var window = new VoiceOverWindow(capture) { IsMpvReady = true };
        var discovery = window.CheckAndOfferReopenRecoveryAsync();
        await store.Entered.Task.WaitAsync(Timeout);
        window.TriggerKeyDown(VKey());
        Assert.False(window.IsRecordingLive);
        store.Release.Set();
        await discovery.WaitAsync(Timeout);
        Assert.True(window.HasUnresolvedFailedTakes);

        store.Reset(gateDelete: true);
        var discard = window.DiscardFailedTakesAction();
        await store.Entered.Task.WaitAsync(Timeout);
        window.TriggerStartRecordingAndPlayback();
        Assert.False(window.IsRecordingLive);
        store.Release.Set();
        Assert.True(await discard.WaitAsync(Timeout));
        Assert.Equal(VoiceOverWindow.VoiceOverSessionOwner.None, window.CurrentSessionOwner);
    }

    [AvaloniaFact]
    public async Task RecoveryAndDiscard_AreRefused_WhileArmingOrLive_AndAdmittedAfterStop()
    {
        using var capture = Capture();
        var window = new VoiceOverWindow(capture) { IsMpvReady = true };
        string wav = Wav("pending.wav");
        window.RecoveryManager.RegisterFailedTake(Take(wav), null, "failure");

        window.TriggerStartRecordingAndPlayback();   // arming
        Assert.True(window.IsRecordingLive);
        Assert.Equal(VoiceOverWindow.VoiceOverSessionOwner.Capture, window.CurrentSessionOwner);
        Assert.False(await window.RecoverFailedTakesAction());
        Assert.False(await window.DiscardFailedTakesAction());
        Assert.True(File.Exists(wav));
        Assert.Empty(window.Sessions);

        window.TriggerStopRecording();
        Assert.Equal(VoiceOverWindow.VoiceOverSessionOwner.None, window.CurrentSessionOwner);
        Assert.True(await window.RecoverFailedTakesAction());
        Assert.Single(window.Sessions);
    }

    [AvaloniaFact]
    public async Task RecoveryIsRefused_WhileATakeIsDraining_AndAdmittedAfterSettlement()
    {
        var devices = new VoiceOverApplyTests.BlockingTestDevices();
        using var capture = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(capture);
        window.RecoveryManager.RegisterFailedTake(Take(Wav("drain.wav")), null, "failure");

        var open = capture.StartRecordingAsync(Path.Combine(_dir, "live.wav"), 0, _ => { });
        await open!.WaitAsync(Timeout);
        var drain = capture.FinalizeRecordingAsync(_ => { });
        Assert.True(capture.HasPendingFinalizations);
        Assert.Equal(VoiceOverWindow.VoiceOverSessionOwner.Capture, window.CurrentSessionOwner);
        Assert.False(await window.RecoverFailedTakesAction());

        devices.ReleaseGate.Set();
        await drain!.WaitAsync(Timeout);
        await capture.WhenFinalizationsSettled().WaitAsync(Timeout);
        Assert.True(await window.RecoverFailedTakesAction());
    }

    // ───────────────────────────── T2 ─────────────────────────────

    private (VoiceOverRecoveryManager Stale, VoiceOverRecoveryManager Other, string Wav) TwoHolders(IVoiceOverRecoveryStore? store = null)
    {
        string wav = Wav("held.wav");
        Manager(store).RegisterFailedTake(Take(wav), null, "failure");
        var stale = Manager(store); stale.CheckAndOfferReopenRecovery();
        var other = Manager(store); other.CheckAndOfferReopenRecovery();
        Assert.Single(stale.PendingFailedTakes);
        Assert.Single(other.PendingFailedTakes);
        return (stale, other, wav);
    }

    private void AssertNotRediscovered()
    {
        var fresh = Manager();
        fresh.CheckAndOfferReopenRecovery();
        Assert.Empty(fresh.PendingFailedTakes);
    }

    [Fact]
    public void StaleSave_AfterDiscard_DoesNotResurrect_AndAdoptsTheTerminalState()
    {
        var (stale, other, _) = TwoHolders();
        using var capture = Capture();
        Assert.True(other.DiscardTakes(capture, out string? error), error);

        var result = stale.SaveManifest();
        Assert.True(result.Success);
        Assert.NotNull(result.ResolvedElsewhere);
        Assert.Equal(TakeRecoveryState.Discarded, stale.PendingFailedTakes[0].State);
        Assert.False(stale.HasUnresolvedFailedTakes);
        AssertNotRediscovered();
    }

    [Fact]
    public void OppositeOrder_StaleSaveFirst_ThenCommit_ResolvesExactlyOnce()
    {
        var (stale, other, wav) = TwoHolders();
        Assert.True(stale.SaveManifest().Success);
        using var capture = Capture();
        Assert.Equal(1, other.RecoverTakes(capture, _ => { }).RecoveredCount);
        Assert.True(other.CommitTakes(new[] { wav }).Success);
        Assert.True(stale.SaveManifest().Success);
        AssertNotRediscovered();
    }

    [Fact]
    public async Task ConcurrentGatedSaves_CommitAndStale_NeverResurrect()
    {
        var store = new GatedStore();
        var (stale, other, wav) = TwoHolders(store);
        using var capture = Capture();
        Assert.Equal(1, other.RecoverTakes(capture, _ => { }).RecoveredCount);

        store.Reset(gateWrite: true);
        var commit = other.CommitTakesAsync(new[] { wav });
        await store.Entered.Task.WaitAsync(Timeout);   // commit holds the store mid-write
        var staleSave = stale.PersistAsync();          // queued behind it
        store.Release.Set();
        Assert.True((await commit.WaitAsync(Timeout)).Success);
        Assert.True((await staleSave.WaitAsync(Timeout)).Success);
        Assert.Equal(TakeRecoveryState.Committed, stale.PendingFailedTakes[0].State);
        AssertNotRediscovered();
    }

    [Fact]
    public void StaleRecordRejected_NewRecordFromTheSameManagerPreserved()
    {
        var (stale, other, wav) = TwoHolders();
        using var capture = Capture();
        other.RecoverTakes(capture, _ => { });
        other.CommitTakes(new[] { wav });

        string fresh = Wav("fresh.wav");
        Assert.True(stale.RegisterFailedTake(Take(fresh), null, "new failure").Success);
        var reopened = Manager();
        reopened.CheckAndOfferReopenRecovery();
        var only = Assert.Single(reopened.PendingFailedTakes);
        Assert.Equal(fresh, only.Session.WavPath);
    }

    [Fact]
    public void ManifestStillListingAResolvedTake_IsNotAdoptedAfterRestart()
    {
        var (stale, other, wav) = TwoHolders();
        string manifest = File.ReadAllText(stale.PendingFailedTakes[0].SourceManifestPath!);
        using var capture = Capture();
        other.RecoverTakes(capture, _ => { });
        other.CommitTakes(new[] { wav });
        // Simulate a cleanup write that never landed: the old manifest is back on disk.
        File.WriteAllText(stale.PendingFailedTakes[0].SourceManifestPath!, manifest);
        AssertNotRediscovered();
    }

    [Fact]
    public void FailedResolutionWrite_RemovesNothing_AndReportsFailure()
    {
        var store = new GatedStore();
        var (_, other, wav) = TwoHolders(store);
        using var capture = Capture();
        other.RecoverTakes(capture, _ => { });
        store.FailTombstoneWrite = true;
        var result = other.CommitTakes(new[] { wav });
        Assert.False(result.Success);
        var fresh = Manager();
        fresh.CheckAndOfferReopenRecovery();
        Assert.Single(fresh.PendingFailedTakes);   // record kept: no resolution without a durable tombstone
    }

    /// <summary>Real file system with explicit gates on read/validate/delete/write.</summary>
    private sealed class GatedStore : IVoiceOverRecoveryStore
    {
        private readonly FileVoiceOverRecoveryStore _inner = FileVoiceOverRecoveryStore.Instance;
        public bool GateValidation, GateRead, GateDelete, GateWrite, FailTombstoneWrite;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release = new(false);

        public void Reset(bool gateDelete = false, bool gateWrite = false)
        {
            GateValidation = GateRead = false;
            GateDelete = gateDelete;
            GateWrite = gateWrite;
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(false);
        }

        private void Gate(bool enabled)
        {
            if (!enabled) return;
            Entered.TrySetResult();
            if (!Release.Wait(Timeout)) throw new TimeoutException("test gate");
        }

        public IReadOnlyList<string> EnumerateManifests(string directory, string searchPattern) { Gate(GateRead); return _inner.EnumerateManifests(directory, searchPattern); }
        public bool ManifestExists(string path) => _inner.ManifestExists(path);
        public string ReadManifest(string path) => _inner.ReadManifest(path);

        public void WriteManifestAtomic(string path, byte[] content)
        {
            if (FailTombstoneWrite && Path.GetFileName(path).StartsWith("voiceover_tombstones_", StringComparison.Ordinal))
                throw new IOException("injected tombstone write failure");
            Gate(GateWrite);
            _inner.WriteManifestAtomic(path, content);
        }

        public void DeleteManifest(string path) => _inner.DeleteManifest(path);
        public bool AudioExists(string path) => _inner.AudioExists(path);
        public void DeleteAudio(string path) { Gate(GateDelete); _inner.DeleteAudio(path); }
        public RecoveryAudioVerdict ValidateAudio(string path) { Gate(GateValidation); return _inner.ValidateAudio(path); }
        public IDisposable? AcquireStoreLock(string directory) => _inner.AcquireStoreLock(directory);
    }
}
