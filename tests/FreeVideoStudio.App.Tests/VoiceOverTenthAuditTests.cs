using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

// Audit regressions retained for implementation. Store is entirely in memory; no file cleanup.
public sealed class VoiceOverTenthAuditTests
{
    private static VoiceCaptureSession Capture() => new(new VoiceOverApplyTests.TestDevices(), a => a());

    [Fact]
    public void StaleManagerMustNotResurrectCommittedTake()
    {
        var store = new MemoryStore();
        string root = @"C:\FreeVideoStudio\audit-memory-" + Guid.NewGuid().ToString("N");
        VoiceOverRecoveryManager Manager() => new(() => @"C:\clips\same.mp4", () => root, store);
        var original = Manager();
        original.RegisterFailedTake(new VoiceOverWindow.VoiceOverSession { WavPath = "memory.wav", StartSec = 1, EndSec = 2 }, null, "failure");
        var stale = Manager(); stale.CheckAndOfferReopenRecovery();
        var committing = Manager(); committing.CheckAndOfferReopenRecovery();
        using var capture = Capture();
        Assert.Equal(1, committing.RecoverTakes(capture, _ => { }).RecoveredCount);
        Assert.True(committing.CommitAllRecovered().Success);
        Assert.True(stale.SaveManifest().Success);
        var reopened = Manager(); reopened.CheckAndOfferReopenRecovery();
        Assert.Empty(reopened.PendingFailedTakes);
    }

    [AvaloniaFact]
    public async Task RecordingMustNotStartWhileRecoveryOwnsSession()
    {
        var store = new MemoryStore { BlockValidation = true };
        string root = @"C:\FreeVideoStudio\audit-memory-" + Guid.NewGuid().ToString("N");
        VoiceOverWindow.RecoveryStoreSeam = store;
        VoiceOverWindow.RecoveryDirectorySeam = () => root;
        using var capture = Capture();
        var window = new VoiceOverWindow(capture);
        Task<bool>? recovery = null;
        try
        {
            window.RecoveryManager.RegisterFailedTake(new VoiceOverWindow.VoiceOverSession { WavPath = "memory.wav", StartSec = 1, EndSec = 2 }, null, "failure");
            recovery = window.RecoverFailedTakesAction();
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.TriggerStartRecordingAndPlayback();
            Assert.False(window.IsRecordingLive, "Recording started while recovery validation still owned the take list.");
        }
        finally
        {
            store.Release.Set();
            if (recovery != null) await recovery;
            VoiceOverWindow.RecoveryStoreSeam = null;
            VoiceOverWindow.RecoveryDirectorySeam = null;
        }
    }

    private sealed class MemoryStore : IVoiceOverRecoveryStore
    {
        private readonly Dictionary<string,string> files = new(StringComparer.OrdinalIgnoreCase);
        public bool BlockValidation;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release = new(false);
        public IReadOnlyList<string> EnumerateManifests(string directory, string pattern)
        {
            var prefix = pattern.Split('*')[0];
            lock (files) return files.Keys.Where(p => Path.GetDirectoryName(p) == directory && Path.GetFileName(p).StartsWith(prefix)).ToArray();
        }
        public bool ManifestExists(string path) { lock(files) return files.ContainsKey(path); }
        public string ReadManifest(string path) { lock(files) return files[path]; }
        public void WriteManifestAtomic(string path, byte[] content) { lock(files) files[path] = Encoding.UTF8.GetString(content); }
        public void DeleteManifest(string path) { lock(files) files.Remove(path); }
        public bool AudioExists(string path) => true;
        public void DeleteAudio(string path) { }
        public RecoveryAudioVerdict ValidateAudio(string path)
        {
            Entered.TrySetResult();
            if (BlockValidation && !Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test gate");
            return new(true, 1, null);
        }
    }
}
