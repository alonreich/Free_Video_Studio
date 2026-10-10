// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using NAudio.Wave;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// VOAPPLY_01 / VOCAPTURE_02 / SYS-WINSTATE — VoiceOverWindow Apply &amp; Finish transaction tests.
/// Verifies single-flight apply, capture settlement, rollback on persistence failure/timeout,
/// preservation of original takes, and close-guarding while saving is in-flight.
/// </summary>
public sealed class VoiceOverApplyTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "FvsVoApplyTest_" + Guid.NewGuid().ToString("N"));

    public VoiceOverApplyTests()
    {
        Directory.CreateDirectory(_tempDir);
        // VORECOVERY_01 — isolated recovery storage BEFORE any production window is constructed.
        VoiceOverWindow.RecoveryDirectorySeam = () => _tempDir;
        VoiceOverWindow.RecoveryStoreSeam = null;
    }

    public void Dispose()
    {
        VoiceOverWindow.TrimRunnerSeam = null;
        VoiceOverWindow.RecoveryDirectorySeam = null;
        VoiceOverWindow.RecoveryStoreSeam = null;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string CreateTestWav(string fileName, double seconds = 1.0)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        int frames = (int)(44100 * seconds);
        var buffer = new byte[frames * 2];
        writer.Write(buffer, 0, buffer.Length);
        return path;
    }

    [AvaloniaFact]
    public async Task ApplyWhileRecording_CapturesAndIncludesFinalizedTakeOnce_AndClosesWindow()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string wavPath = CreateTestWav("take_live.wav", 1.0);
        var open = session.StartRecordingAsync(wavPath, 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        window.IsRecordingLive = true;
        window.CurrentSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 0.0
        };

        await window.ApplyAndCloseAsync().WaitAsync(Timeout);

        Assert.NotNull(window.Result);
        var result = window.Result!;
        Assert.Single(result.VoiceOverTakes);
        Assert.NotNull(result.VoiceOverWavPath);
        Assert.True(File.Exists(result.VoiceOverWavPath));
        Assert.True(window.IsSafeToClose);
        Assert.False(window.IsApplyingInFlight);
    }

    [AvaloniaFact]
    public async Task DoubleApply_RejectedBySingleFlightGuard()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string wavPath = CreateTestWav("take_guard.wav", 1.0);
        window.Sessions.Add(new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 1.0
        });

        // 1. When an apply is already marked in-flight, second call must return immediately
        window.IsApplyingInFlight = true;
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.Null(window.Result);

        // 2. When guard is clear, apply proceeds
        window.IsApplyingInFlight = false;
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.NotNull(window.Result);
        Assert.Single(window.Result!.VoiceOverTakes);
    }

    [AvaloniaFact]
    public async Task PersistenceFailure_PreservesOriginalRecordings_AndCleansPartialNewFiles()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string originalWav = CreateTestWav("take_original.wav", 2.0);
        window.Sessions.Add(new VoiceOverWindow.VoiceOverSession
        {
            WavPath = originalWav,
            StartSec = 0.0,
            EndSec = 2.0,
            TrimLeftSec = 0.5
        });

        VoiceOverWindow.TrimRunnerSeam = (info, ct) => throw new IOException("Disk failure during FFmpeg trim.");

        try
        {
            await window.ApplyAndCloseAsync().WaitAsync(Timeout);

            // Transaction rolled back:
            Assert.Null(window.Result);
            // Original file preserved untouched:
            Assert.True(File.Exists(originalWav));
            // Controls restored to usable state:
            Assert.False(window.IsApplyingInFlight);
            Assert.True(window.ApplyButtonControl?.IsEnabled);
            Assert.Equal("APPLY & CLOSE", window.ApplyButtonControl?.Content);
        }
        finally
        {
            VoiceOverWindow.TrimRunnerSeam = null;
        }
    }

    [AvaloniaFact]
    public async Task TrimProcessTimeout_RestoresRecoverableUiState()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string originalWav = CreateTestWav("take_timeout.wav", 2.0);
        window.Sessions.Add(new VoiceOverWindow.VoiceOverSession
        {
            WavPath = originalWav,
            StartSec = 0.0,
            EndSec = 2.0,
            TrimLeftSec = 0.5
        });

        VoiceOverWindow.TrimRunnerSeam = (info, ct) => throw new TimeoutException("FFmpeg trim timed out after 15 seconds.");

        try
        {
            await window.ApplyAndCloseAsync().WaitAsync(Timeout);

            Assert.Null(window.Result);
            Assert.True(File.Exists(originalWav));
            Assert.False(window.IsApplyingInFlight);
            Assert.True(window.ApplyButtonControl?.IsEnabled);
            Assert.Equal("APPLY & CLOSE", window.ApplyButtonControl?.Content);
        }
        finally
        {
            VoiceOverWindow.TrimRunnerSeam = null;
        }
    }

    [AvaloniaFact]
    public void WindowClosing_WhileApplyInFlight_IsCancelled()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        // Close while applying must be cancelled
        window.IsApplyingInFlight = true;
        var e = new CancelEventArgs();
        window.TriggerClosing(e);
        Assert.True(e.Cancel);

        // Safe to close after apply completes
        window.IsApplyingInFlight = false;
        window.IsSafeToClose = true;
        var e2 = new CancelEventArgs();
        window.TriggerClosing(e2);
        Assert.False(e2.Cancel);
    }

    [AvaloniaFact]
    public async Task ApplyAndClose_ModalWindow_ClosesSuccessfully_AndDeliversResultToOwner()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var owner = new Avalonia.Controls.Window();
        owner.Show();

        var window = new VoiceOverWindow(session);
        string wavPath = CreateTestWav("take_modal.wav", 1.0);
        window.Sessions.Add(new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 1.0
        });

        bool closedFired = false;
        window.Closed += (s, e) => closedFired = true;

        var showDialogTask = window.ShowDialog(owner);

        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        await showDialogTask.WaitAsync(Timeout);

        Assert.True(closedFired);
        Assert.NotNull(window.Result);
        Assert.Single(window.Result!.VoiceOverTakes);
        Assert.True(File.Exists(window.Result.VoiceOverTakes[0].Path));
        Assert.True(window.IsSafeToClose);
        Assert.False(window.IsApplyingInFlight);

        owner.Close();
    }

    [AvaloniaFact]
    public async Task Apply_WhenCaptureDrainTimesOut_AbortsCommitAndPreservesTakes_RecoversOnSubsequentApply()
    {
        var devices = new BlockingTestDevices();
        using var session = new VoiceCaptureSession(devices, a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var window = new VoiceOverWindow(session);

        // 1. Supply one existing take
        string wav1 = CreateTestWav("take_existing.wav", 1.0);
        window.Sessions.Add(new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wav1,
            StartSec = 0.0,
            EndSec = 1.0
        });

        // 2. Start a second take whose finalization will block
        string wav2 = CreateTestWav("take_delayed.wav", 1.0);
        var open = session.StartRecordingAsync(wav2, 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        window.IsRecordingLive = true;
        window.CurrentSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wav2,
            StartSec = 1.0,
            EndSec = 2.0
        };

        VoiceOverWindow.CaptureDrainTimeout = TimeSpan.FromMilliseconds(50);

        try
        {
            // Apply while drain is blocked
            await window.ApplyAndCloseAsync().WaitAsync(Timeout);

            // Assert commit aborted: no result, window did NOT close
            Assert.Null(window.Result);
            Assert.False(window.IsSafeToClose);
            Assert.False(window.IsApplyingInFlight);
            // Existing take preserved
            Assert.Single(window.Sessions);

            // 3. Now release the blocked finalization
            devices.ReleaseGate.Set();

            // Wait for finalization to settle and post CompleteTake to UI thread
            await session.WhenFinalizationsSettled().WaitAsync(Timeout);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });

            // The second take is now added to sessions
            Assert.Equal(2, window.Sessions.Count);

            // 4. Retry Apply now that device operation settled
            VoiceOverWindow.CaptureDrainTimeout = TimeSpan.FromSeconds(5);
            await window.ApplyAndCloseAsync().WaitAsync(Timeout);

            // Both takes committed successfully!
            Assert.NotNull(window.Result);
            Assert.Equal(2, window.Result!.VoiceOverTakes.Count);
            Assert.True(window.IsSafeToClose);
            Assert.False(window.IsApplyingInFlight);
        }
        finally
        {
            devices.ReleaseGate.Set();
            VoiceOverWindow.CaptureDrainTimeout = TimeSpan.FromSeconds(5);
        }
    }

    [AvaloniaFact]
    public async Task Apply_AdversarialMutationsDuringTrim_RefusedOrIsolatedFromCommittedSnapshot()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string originalWav = CreateTestWav("take_adversarial.wav", 2.0);
        var initialSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = originalWav,
            StartSec = 1.0,
            EndSec = 3.0,
            TrimLeftSec = 0.5,
            TrimRightSec = 0.5
        };
        window.Sessions.Add(initialSession);

        var trimStarted = new TaskCompletionSource();
        var trimGate = new TaskCompletionSource();

        VoiceOverWindow.TrimRunnerSeam = async (info, ct) =>
        {
            trimStarted.TrySetResult();
            await trimGate.Task;
            string src = info.ArgumentList[2];
            string dst = info.ArgumentList[info.ArgumentList.Count - 1];
            File.Copy(src, dst, overwrite: true);
        };

        try
        {
            var applyTask = window.ApplyAndCloseAsync();

            // Wait until Apply reaches the trim runner and suspends
            await trimStarted.Task.WaitAsync(Timeout);

            Assert.True(window.IsApplyingInFlight);

            // 1. Attempt to start recording via trigger -> refused
            window.TriggerToggleRecord();
            Assert.False(window.IsRecordingLive);

            // 2. Attempt to start recording via StartRecordingAndPlayback -> refused
            window.TriggerStartRecordingAndPlayback();
            Assert.False(window.IsRecordingLive);

            // 3. Attempt to pause/resume recording -> refused
            window.TriggerToggleRecordPause();
            Assert.False(window.IsRecordingLive);

            // 4. Attempt to delete session via TimelineSurface_KeyDown -> refused
            window.SelectedSession = initialSession;
            var deleteKeyArgs = new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Key = Avalonia.Input.Key.Delete
            };
            window.TriggerTimelineKeyDown(deleteKeyArgs);
            Assert.Single(window.Sessions); // Still present

            // 5. Attempt adversarial live object mutation: change StartSec on the live session object
            initialSession.StartSec = 999.0;

            // 6. Release trim seam so persistence can finish
            trimGate.SetResult();
            await applyTask.WaitAsync(Timeout);

            // Result must be committed from the snapshot captured at the start of transaction
            Assert.NotNull(window.Result);
            Assert.Single(window.Result!.VoiceOverTakes);
            var committedTake = window.Result.VoiceOverTakes[0];
            // The snapshot had RenderStartSec = 1.0 + 0.5 = 1.5, NOT 999.0 + 0.5 = 999.5
            Assert.Equal(1.5, committedTake.StartSec);
            Assert.True(File.Exists(committedTake.Path));
        }
        finally
        {
            trimGate.TrySetResult();
            VoiceOverWindow.TrimRunnerSeam = null;
        }
    }

    [AvaloniaFact]
    public async Task Apply_CaptureDrainTimeout_ButtonRestoresToApplyAndCloseAfterLateSettlement()
    {
        var devices = new BlockingTestDevices();
        using var session = new VoiceCaptureSession(devices, a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var window = new VoiceOverWindow(session);

        string wav1 = CreateTestWav("take_existing_drain.wav", 1.0);
        window.Sessions.Add(new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wav1,
            StartSec = 0.0,
            EndSec = 1.0
        });

        string wav2 = CreateTestWav("take_blocking_drain.wav", 1.0);
        var open = session.StartRecordingAsync(wav2, 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        window.IsRecordingLive = true;
        window.CurrentSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wav2,
            StartSec = 1.0,
            EndSec = 2.0
        };

        VoiceOverWindow.CaptureDrainTimeout = TimeSpan.FromMilliseconds(50);

        try
        {
            // Apply while drain is blocked -> times out
            await window.ApplyAndCloseAsync().WaitAsync(Timeout);

            // Assert button shows DRAINING...
            Assert.Null(window.Result);
            Assert.False(window.IsApplyingInFlight);
            Assert.Equal("DRAINING...", window.ApplyButtonControl?.Content);

            // Now release the blocked recorder drain
            devices.ReleaseGate.Set();

            // Wait for session finalizations to settle
            await session.WhenFinalizationsSettled().WaitAsync(Timeout);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });

            // Button content MUST automatically restore to "APPLY & CLOSE" without another click
            Assert.Equal("APPLY & CLOSE", window.ApplyButtonControl?.Content);
            Assert.True(window.ApplyButtonControl?.IsEnabled);
        }
        finally
        {
            devices.ReleaseGate.Set();
            VoiceOverWindow.CaptureDrainTimeout = TimeSpan.FromSeconds(5);
        }
    }

    [AvaloniaFact]
    public async Task ApplyDuringDeferredClose_MustPreserveCommittedResult()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var window = new VoiceOverWindow(session);

        string wavPath = CreateTestWav("take_deferred.wav", 1.0);
        window.Sessions.Add(new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 1.0
        });

        // First apply succeeds
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.NotNull(window.Result);
        var originalResult = window.Result!;
        var originalPath = originalResult.VoiceOverWavPath;
        Assert.True(window.IsCommitted);
        Assert.True(window.IsSafeToClose);

        // While close is deferred, attempt repeated ApplyAndCloseAsync
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);

        // Result MUST remain intact and not reset to null!
        Assert.NotNull(window.Result);
        Assert.Same(originalResult, window.Result);
        Assert.Equal(originalPath, window.Result!.VoiceOverWavPath);
    }

    [AvaloniaFact]
    public async Task FailedApply_MustRetainActionableErrorHint()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var window = new VoiceOverWindow(session);

        string wavPath = CreateTestWav("take_disk_err.wav", 1.0);
        window.Sessions.Add(new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 1.0,
            TrimLeftSec = 0.1
        });

        VoiceOverWindow.TrimRunnerSeam = (info, ct) => throw new IOException("audit-disk-failure");

        try
        {
            await window.ApplyAndCloseAsync().WaitAsync(Timeout);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });

            // Must preserve actionable failure detail, NOT generic ready text!
            var hint = window.HintTextControl;
            Assert.NotNull(hint);
            Assert.Contains("audit-disk-failure", hint!.Text);
            Assert.Contains("could not be saved", hint.Text);
        }
        finally
        {
            VoiceOverWindow.TrimRunnerSeam = null;
        }
    }

    [AvaloniaFact]
    public async Task Apply_WithFailedDeviceFinalization_MustNotCommit()
    {
        var devices = new FaultingTestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string wavPath = CreateTestWav("take_fault.wav", 1.0);
        var open = session.StartRecordingAsync(wavPath, 0, _ => { });
        Assert.NotNull(open);
        var openResult = await open!.WaitAsync(Timeout);
        Assert.Equal(RecordingOpenOutcome.Opened, openResult.Outcome);

        window.IsRecordingLive = true;
        window.CurrentSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 1.0
        };

        await window.ApplyAndCloseAsync().WaitAsync(Timeout);

        Assert.Null(window.Result);
        Assert.False(window.IsSafeToClose);
        Assert.True(session.HasUnreleasedDevice);
        Assert.True(File.Exists(wavPath));
    }

    [AvaloniaFact]
    public async Task ExistingValidTake_WithFailedNewestTake_AbortsApply_PreservesWav_NoCommit()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        // Add an existing valid take to window.Sessions
        string validWav = CreateTestWav("take_valid.wav", 2.0);
        var validTake = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = validWav,
            StartSec = 0,
            EndSec = 2.0
        };
        window.Sessions.Add(validTake);

        // Simulate a new take whose finalization failed
        string failedWav = CreateTestWav("take_failed.wav", 1.5);
        var failedTake = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = failedWav,
            StartSec = 2.0,
            EndSec = 3.5
        };

        var failedOutcome = new CapturedTake(
            Bytes: 44100 * 3,
            Buffers: 10,
            Peak: 0.5f,
            IsSuccess: false,
            EndpointReleased: false,
            FileFinalized: false,
            Error: new InvalidOperationException("Endpoint finalization error")
        );

        window.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 44100 * 3, capturedBuffers: 10, capturedPeak: 0.5f, isTakeValid: false, takeOutcome: failedOutcome);
        await window.RecoveryManager.WhenStoreIdle();

        Assert.True(window.LastTakeFailedFinalization);
        Assert.True(File.Exists(failedWav), "Failed take WAV must be preserved on disk.");

        await window.ApplyAndCloseAsync();

        Assert.False(window.IsCommitted, "Apply must abort and not commit when newest take failed finalization.");
        Assert.Null(window.Result);
        Assert.True(File.Exists(validWav), "Existing valid take WAV must be preserved.");
        Assert.True(File.Exists(failedWav), "Failed take WAV must remain preserved after aborted Apply.");
    }

    [AvaloniaFact]
    public async Task InterruptedPartialTake_WithCleanReleaseAndFinalization_CommitsSuccessfully()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string wavPath = CreateTestWav("take_partial.wav", 1.5);
        var partialTake = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 1.0,
            EndSec = 0
        };

        // Capture had an interruption error, but endpoint was released and file finalized safely
        var outcome = new CapturedTake(
            Bytes: 44100 * 3, // ~1.5s
            Buffers: 15,
            Peak: 0.6f,
            IsSuccess: false,
            EndpointReleased: true,
            FileFinalized: true,
            Error: new InvalidOperationException("Buffer underrun during recording")
        );

        window.TriggerCompleteTake(partialTake, micWasOpen: true, capturedBytes: 44100 * 3, capturedBuffers: 15, capturedPeak: 0.6f, isTakeValid: true, takeOutcome: outcome);

        Assert.Single(window.Sessions);
        Assert.False(window.HasUnresolvedFailedTakes);

        await window.ApplyAndCloseAsync().WaitAsync(Timeout);

        Assert.NotNull(window.Result);
        Assert.Single(window.Result!.VoiceOverTakes);
        Assert.True(window.IsCommitted);
    }

    [AvaloniaFact]
    public async Task FailedTake_FollowedBySuccessfulTake_RetainsPendingFailure_AndBlocksApplyUntilDiscarded()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        // 1. Take 1 fails finalization (unreleased endpoint)
        string failedWav = CreateTestWav("take_failed.wav", 1.0);
        var failedTake = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = failedWav,
            StartSec = 0,
            EndSec = 0
        };
        var failedOutcome = new CapturedTake(
            Bytes: 44100 * 2,
            Buffers: 10,
            Peak: 0.5f,
            IsSuccess: false,
            EndpointReleased: false,
            FileFinalized: false,
            Error: new InvalidOperationException("Endpoint hang")
        );
        window.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 44100 * 2, capturedBuffers: 10, capturedPeak: 0.5f, isTakeValid: false, takeOutcome: failedOutcome);
        await window.RecoveryManager.WhenStoreIdle();

        Assert.True(window.HasUnresolvedFailedTakes);
        Assert.Single(window.PendingFailedTakes);
        Assert.Empty(window.Sessions);

        // 2. Take 2 succeeds completely
        string successWav = CreateTestWav("take_success.wav", 2.0);
        var successTake = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = successWav,
            StartSec = 1.0,
            EndSec = 3.0
        };
        var successOutcome = new CapturedTake(
            Bytes: 44100 * 4,
            Buffers: 20,
            Peak: 0.7f,
            IsSuccess: true,
            EndpointReleased: true,
            FileFinalized: true,
            Error: null
        );
        window.TriggerCompleteTake(successTake, micWasOpen: true, capturedBytes: 44100 * 4, capturedBuffers: 20, capturedPeak: 0.7f, isTakeValid: true, takeOutcome: successOutcome);

        // Subsequent success must NOT erase the pending failed take!
        Assert.Single(window.Sessions);
        Assert.True(window.HasUnresolvedFailedTakes);

        // 3. Apply must be blocked by the unresolved failed take!
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.Null(window.Result);
        Assert.False(window.IsCommitted);
        Assert.True(File.Exists(failedWav), "Failed take WAV must be preserved.");
        Assert.True(File.Exists(successWav), "Successful take WAV must be preserved.");

        // 4. Discard the failed take
        await window.DiscardAllPendingTakes();
        Assert.False(window.HasUnresolvedFailedTakes);

        // 5. Apply now succeeds
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.NotNull(window.Result);
        Assert.True(window.IsCommitted);
    }

    [AvaloniaFact]
    public async Task FailedTake_BlockedApply_ResolvedViaRealUiDiscardButton_AppliesSuccessfully()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.TestManifestDirectory = _tempDir;

        // 1. Success take
        string successWav = CreateTestWav("take_ok.wav", 1.5);
        var successTake = new VoiceOverWindow.VoiceOverSession { WavPath = successWav, StartSec = 0.0, EndSec = 1.5 };
        var okOutcome = new CapturedTake(44100 * 3, 15, 0.5f, IsSuccess: true, EndpointReleased: true, FileFinalized: true, Error: null);
        window.TriggerCompleteTake(successTake, micWasOpen: true, capturedBytes: 44100 * 3, capturedBuffers: 15, capturedPeak: 0.5f, isTakeValid: true, takeOutcome: okOutcome);

        // 2. Failed take
        string failedWav = CreateTestWav("take_bad.wav", 1.0);
        var failedTake = new VoiceOverWindow.VoiceOverSession { WavPath = failedWav, StartSec = 1.5, EndSec = 2.5 };
        var badOutcome = new CapturedTake(44100 * 2, 10, 0.4f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException("Failed take"));
        window.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 44100 * 2, capturedBuffers: 10, capturedPeak: 0.4f, isTakeValid: false, takeOutcome: badOutcome);
        await window.RecoveryManager.WhenStoreIdle();

        // Verify UI controls reflect unresolved failed take
        Assert.True(window.HasUnresolvedFailedTakes);
        Assert.NotNull(window.DiscardFailedTakesButtonControl);
        Assert.True(window.DiscardFailedTakesButtonControl!.IsVisible);
        Assert.NotNull(window.RecoverFailedTakesButtonControl);
        Assert.True(window.RecoverFailedTakesButtonControl!.IsVisible);

        // Apply is blocked
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.Null(window.Result);
        Assert.False(window.IsCommitted);

        // 3. User clicks Discard button
        bool discardResult = await window.DiscardFailedTakesAction();
        Assert.True(discardResult);
        Assert.False(window.HasUnresolvedFailedTakes);
        Assert.False(window.DiscardFailedTakesButtonControl.IsVisible);
        Assert.False(window.RecoverFailedTakesButtonControl.IsVisible);
        Assert.False(File.Exists(failedWav), "Discarded audio file must be deleted upon proven endpoint release.");

        // 4. Apply now unblocks and succeeds
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.NotNull(window.Result);
        Assert.True(window.IsCommitted);
        Assert.Single(window.Result!.VoiceOverTakes);
    }

    [AvaloniaFact]
    public async Task FailedTake_ResolvedViaRealUiRecoverButton_IntegratesSessionAndApplies()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.TestManifestDirectory = _tempDir;

        // Take completed with non-zero audio (> 44 bytes), but flagged with finalization error
        string wavPath = CreateTestWav("take_to_recover.wav", 2.0);
        var sessionTake = new VoiceOverWindow.VoiceOverSession { WavPath = wavPath, StartSec = 0.5, EndSec = 2.5 };
        var outcome = new CapturedTake(44100 * 4, 20, 0.7f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException("Dropped packet"));
        window.TriggerCompleteTake(sessionTake, micWasOpen: true, capturedBytes: 44100 * 4, capturedBuffers: 20, capturedPeak: 0.7f, isTakeValid: false, takeOutcome: outcome);
        await window.RecoveryManager.WhenStoreIdle();

        Assert.True(window.HasUnresolvedFailedTakes);
        Assert.Empty(window.Sessions);
        Assert.True(window.RecoverFailedTakesButtonControl!.IsVisible);

        // User clicks Recover button
        bool recovered = await window.RecoverFailedTakesAction();
        Assert.True(recovered);
        Assert.False(window.HasUnresolvedFailedTakes);
        Assert.Single(window.Sessions);
        Assert.Equal(wavPath, window.Sessions[0].WavPath);
        Assert.False(window.RecoverFailedTakesButtonControl.IsVisible);

        // Apply commits the recovered session
        await window.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.NotNull(window.Result);
        Assert.True(window.IsCommitted);
        Assert.Single(window.Result!.VoiceOverTakes);
        Assert.Equal(0.5, window.Result.VoiceOverTakes[0].StartSec);
    }

    [AvaloniaFact]
    public async Task FailedTake_PendingEndpointRelease_PreventsUnsafeFileDeletion()
    {
        var devices = new FaultingTestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.TestManifestDirectory = _tempDir;

        string wavPath = CreateTestWav("take_unreleased.wav", 1.0);
        var open = session.StartRecordingAsync(wavPath, 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        var finalizeTask = session.FinalizeRecordingAsync(_ => { });
        Assert.NotNull(finalizeTask);
        await finalizeTask!.WaitAsync(Timeout);

        Assert.True(session.HasUnreleasedDevice);

        var failedTake = new VoiceOverWindow.VoiceOverSession { WavPath = wavPath, StartSec = 0, EndSec = 1.0 };
        var outcome = new CapturedTake(44100 * 2, 10, 0.5f, IsSuccess: false, EndpointReleased: false, FileFinalized: false, Error: new InvalidOperationException("Endpoint busy"));
        window.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 44100 * 2, capturedBuffers: 10, capturedPeak: 0.5f, isTakeValid: false, takeOutcome: outcome);
        await window.RecoveryManager.WhenStoreIdle();

        Assert.True(window.HasUnresolvedFailedTakes);

        // Attempting to discard must be rejected while endpoint is unreleased
        bool discarded = await window.DiscardFailedTakesAction();
        Assert.False(discarded);
        Assert.True(File.Exists(wavPath), "File deletion must be rejected when endpoint release is unproven.");
        Assert.True(window.HasUnresolvedFailedTakes);
    }

    [AvaloniaFact]
    public async Task FailedTake_DurableRecoveryManifest_SurvivesCloseAndReopen()
    {
        var devices = new TestDevices();
        using var session1 = new VoiceCaptureSession(devices, a => a());
        var window1 = new VoiceOverWindow(session1);
        window1.TestManifestDirectory = _tempDir;

        string wavPath = CreateTestWav("take_manifest_test.wav", 1.2);
        var failedTake = new VoiceOverWindow.VoiceOverSession { WavPath = wavPath, StartSec = 1.0, EndSec = 2.2 };
        var badOutcome = new CapturedTake(44100 * 2, 10, 0.4f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException("Unexpected shutdown"));
        window1.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 44100 * 2, capturedBuffers: 10, capturedPeak: 0.4f, isTakeValid: false, takeOutcome: badOutcome);
        await window1.RecoveryManager.WhenStoreIdle();

        string manifestPath = window1.GetRecoveryManifestPath();
        Assert.True(File.Exists(manifestPath), "Manifest must be written when failed take occurs.");
        string manifestContent = File.ReadAllText(manifestPath);
        Assert.Contains(Path.GetFileName(wavPath), manifestContent);

        // Now simulate reopening a fresh window
        using var session2 = new VoiceCaptureSession(devices, a => a());
        var window2 = new VoiceOverWindow(session2);
        window2.TestManifestDirectory = _tempDir;
        window2.CheckAndOfferReopenRecovery();

        Assert.True(window2.HasUnresolvedFailedTakes, "Reopened window must discover unresolved failed take from manifest.");
        Assert.Single(window2.PendingFailedTakes);
        Assert.Equal(wavPath, window2.PendingFailedTakes[0].Session.WavPath);
        Assert.True(window2.RecoverFailedTakesButtonControl!.IsVisible);

        // Operator recovers the take in window2
        bool recovered = await window2.RecoverFailedTakesAction();
        Assert.True(recovered);
        Assert.Single(window2.Sessions);
        Assert.False(window2.HasUnresolvedFailedTakes);
        Assert.True(File.Exists(manifestPath), "Manifest must remain durable while take is recovered but not yet committed.");

        // Apply commits the recovered session and removes the recovery manifest
        await window2.ApplyAndCloseAsync().WaitAsync(Timeout);
        Assert.NotNull(window2.Result);
        Assert.True(window2.IsCommitted);
        Assert.False(File.Exists(manifestPath), "Manifest must be removed once all failed takes are committed.");
    }

    [AvaloniaFact]
    public async Task RecoverFailedTakes_CorruptOrTruncatedWav_RejectsWithoutDeletingFile_AndKeepsTakeUnresolved()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.TestManifestDirectory = _tempDir;

        // Truncated file (< 44 bytes)
        string truncatedPath = Path.Combine(_tempDir, "truncated_take.wav");
        File.WriteAllBytes(truncatedPath, new byte[20]);

        var failedTake = new VoiceOverWindow.VoiceOverSession { WavPath = truncatedPath, StartSec = 0.0, EndSec = 1.0 };
        var badOutcome = new CapturedTake(20, 1, 0.1f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException("Endpoint crash"));
        window.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 20, capturedBuffers: 1, capturedPeak: 0.1f, isTakeValid: false, takeOutcome: badOutcome);
        await window.RecoveryManager.WhenStoreIdle();

        Assert.True(window.HasUnresolvedFailedTakes);

        // Attempt recovery
        bool recovered = await window.RecoverFailedTakesAction();
        Assert.False(recovered, "Recovery must fail for truncated audio.");
        Assert.True(File.Exists(truncatedPath), "Truncated take audio file must NEVER be deleted during recovery attempt.");
        Assert.True(window.HasUnresolvedFailedTakes, "Take must remain unresolved after failed recovery.");
        Assert.Empty(window.Sessions);
        Assert.NotNull(window.LastApplyErrorMessage);

        // Now test corrupted header (non-RIFF, 128 bytes of garbage)
        string corruptPath = Path.Combine(_tempDir, "corrupt_take.wav");
        File.WriteAllBytes(corruptPath, new byte[128]);

        var corruptTake = new VoiceOverWindow.VoiceOverSession { WavPath = corruptPath, StartSec = 1.0, EndSec = 2.0 };
        window.TriggerCompleteTake(corruptTake, micWasOpen: true, capturedBytes: 128, capturedBuffers: 1, capturedPeak: 0.1f, isTakeValid: false, takeOutcome: badOutcome);
        await window.RecoveryManager.WhenStoreIdle();

        bool recovered2 = await window.RecoverFailedTakesAction();
        Assert.False(recovered2, "Recovery must fail for corrupted header.");
        Assert.True(File.Exists(corruptPath), "Corrupt take file must NEVER be deleted during recovery attempt.");
        Assert.True(window.HasUnresolvedFailedTakes);
    }

    [AvaloniaFact]
    public async Task RecoverFailedTakes_ZeroDurationOrZeroSamples_RejectsWithoutDeletingFile()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.TestManifestDirectory = _tempDir;

        // Valid WAV header (44 bytes) but 0 audio data bytes (0 samples, 0 duration)
        string zeroWav = Path.Combine(_tempDir, "zero_sample.wav");
        using (var writer = new WaveFileWriter(zeroWav, new WaveFormat(44100, 16, 1)))
        {
            // Write 0 samples
        }

        var failedTake = new VoiceOverWindow.VoiceOverSession { WavPath = zeroWav, StartSec = 0.0, EndSec = 0.0 };
        var badOutcome = new CapturedTake(0, 0, 0f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException("Empty take"));
        window.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 0, capturedBuffers: 0, capturedPeak: 0f, isTakeValid: false, takeOutcome: badOutcome);
        await window.RecoveryManager.WhenStoreIdle();

        Assert.True(window.HasUnresolvedFailedTakes);
        bool recovered = await window.RecoverFailedTakesAction();
        Assert.False(recovered, "Recovery must reject audio with zero duration/samples.");
        Assert.True(File.Exists(zeroWav), "Zero duration WAV must NOT be deleted during recovery.");
        Assert.True(window.HasUnresolvedFailedTakes);
        Assert.Empty(window.Sessions);
    }

    [AvaloniaFact]
    public async Task RecoveryManifest_WithSpecialCharactersAndQuotesInError_PersistsAndParsesSafely()
    {
        var devices = new TestDevices();
        using var session1 = new VoiceCaptureSession(devices, a => a());
        var window1 = new VoiceOverWindow(session1);
        window1.TestManifestDirectory = _tempDir;

        string wavPath = CreateTestWav("take_quotes_test.wav", 1.0);
        var failedTake = new VoiceOverWindow.VoiceOverSession { WavPath = wavPath, StartSec = 0.5, EndSec = 1.5 };
        string nastyError = "Driver \"Realtek HD Audio\" reported error: code \\0x80004005\\\r\nDevice unseated / Unicode: 日本語 🎧";
        var badOutcome = new CapturedTake(44100 * 2, 10, 0.5f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException(nastyError));
        window1.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 44100 * 2, capturedBuffers: 10, capturedPeak: 0.5f, isTakeValid: false, takeOutcome: badOutcome);
        await window1.RecoveryManager.WhenStoreIdle();

        string manifestPath = window1.GetRecoveryManifestPath();
        Assert.True(File.Exists(manifestPath));

        // Reopen in window2
        using var session2 = new VoiceCaptureSession(devices, a => a());
        var window2 = new VoiceOverWindow(session2);
        window2.TestManifestDirectory = _tempDir;
        window2.CheckAndOfferReopenRecovery();

        Assert.True(window2.HasUnresolvedFailedTakes);
        Assert.Single(window2.PendingFailedTakes);
        Assert.Contains("Realtek HD Audio", window2.PendingFailedTakes[0].FailureReason);
        Assert.Contains("0x80004005", window2.PendingFailedTakes[0].FailureReason);
    }

    [AvaloniaFact]
    public async Task RecoveryManifest_KeyedByCanonicalVideoPath_IsolatesDifferentVideos()
    {
        var devices = new TestDevices();
        string videoA = Path.Combine(_tempDir, "FolderA", "clip.mp4");
        string videoB = Path.Combine(_tempDir, "FolderB", "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(videoA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(videoB)!);
        File.WriteAllText(videoA, "dummy video a");
        File.WriteAllText(videoB, "dummy video b");

        // Window for Video A
        using var sessionA = new VoiceCaptureSession(devices, a => a());
        var windowA = new VoiceOverWindow(sessionA, videoA, 0.0);
        windowA.TestManifestDirectory = _tempDir;

        string wavA = CreateTestWav("take_video_a.wav", 1.0);
        var takeA = new VoiceOverWindow.VoiceOverSession { WavPath = wavA, StartSec = 0.0, EndSec = 1.0 };
        var badOutcome = new CapturedTake(44100 * 2, 10, 0.5f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException("Fail A"));
        windowA.TriggerCompleteTake(takeA, micWasOpen: true, capturedBytes: 44100 * 2, capturedBuffers: 10, capturedPeak: 0.5f, isTakeValid: false, takeOutcome: badOutcome);
        await windowA.RecoveryManager.WhenStoreIdle();

        string manifestA = windowA.GetRecoveryManifestPath();
        Assert.True(File.Exists(manifestA));

        // Window for Video B in the same test directory
        using var sessionB = new VoiceCaptureSession(devices, a => a());
        var windowB = new VoiceOverWindow(sessionB, videoB, 0.0);
        windowB.TestManifestDirectory = _tempDir;
        windowB.CheckAndOfferReopenRecovery();

        // Window B must NOT inherit Video A's failed take!
        Assert.False(windowB.HasUnresolvedFailedTakes, "Video B must not discover Video A's failed takes despite identical file name.");
        Assert.Empty(windowB.PendingFailedTakes);

        // Window A re-opened must discover it
        using var sessionA2 = new VoiceCaptureSession(devices, a => a());
        var windowA2 = new VoiceOverWindow(sessionA2, videoA, 0.0);
        windowA2.TestManifestDirectory = _tempDir;
        windowA2.CheckAndOfferReopenRecovery();

        Assert.True(windowA2.HasUnresolvedFailedTakes, "Reopened Video A window must find its own manifest.");
        Assert.Single(windowA2.PendingFailedTakes);
        Assert.Equal(wavA, windowA2.PendingFailedTakes[0].Session.WavPath);
    }

    [AvaloniaFact]
    public async Task RealUiButtons_RecoverAndDiscard_TriggerExpectedLifecycle()
    {
        var devices = new TestDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.TestManifestDirectory = _tempDir;

        string wavPath = CreateTestWav("ui_button_take.wav", 1.0);
        var failedTake = new VoiceOverWindow.VoiceOverSession { WavPath = wavPath, StartSec = 0.0, EndSec = 1.0 };
        var badOutcome = new CapturedTake(44100 * 2, 10, 0.5f, IsSuccess: false, EndpointReleased: true, FileFinalized: false, Error: new InvalidOperationException("Endpoint drop"));
        window.TriggerCompleteTake(failedTake, micWasOpen: true, capturedBytes: 44100 * 2, capturedBuffers: 10, capturedPeak: 0.5f, isTakeValid: false, takeOutcome: badOutcome);
        await window.RecoveryManager.WhenStoreIdle();

        Assert.True(window.HasUnresolvedFailedTakes);
        Assert.True(window.DiscardFailedTakesButtonControl!.IsVisible);
        Assert.True(window.RecoverFailedTakesButtonControl!.IsVisible);

        // Discard via DiscardFailedTakesAction
        bool discarded = await window.DiscardFailedTakesAction();
        Assert.True(discarded);
        Assert.False(File.Exists(wavPath), "Discard must delete the audio file.");
        Assert.False(window.HasUnresolvedFailedTakes);
        Assert.False(window.DiscardFailedTakesButtonControl.IsVisible);
        Assert.False(window.RecoverFailedTakesButtonControl.IsVisible);
    }

    [Fact]
    public void NinthAudit_RecoveredButUncommittedTakeMustSurviveReopen()
    {
        var manager = new VoiceOverRecoveryManager(() => @"C:\clips\same.mp4", () => _tempDir);
        string wavPath = CreateTestWav("recover_survive.wav", 1.0);
        var take = new VoiceOverWindow.VoiceOverSession { WavPath = wavPath, StartSec = 1, EndSec = 2 };
        manager.RegisterFailedTake(take, null, "writer failure");
        using var capture = new VoiceCaptureSession(new TestDevices(), a => a());
        Assert.Equal(1, manager.RecoverTakes(capture, _ => { }).RecoveredCount);
        var reopened = new VoiceOverRecoveryManager(() => @"C:\clips\same.mp4", () => _tempDir);
        reopened.CheckAndOfferReopenRecovery();
        Assert.Contains(reopened.PendingFailedTakes, t => t.Session.WavPath == take.WavPath);
    }

    [Fact]
    public void NinthAudit_SameVideoSessionsMustNotOverwriteEachOther()
    {
        var first = new VoiceOverRecoveryManager(() => @"C:\clips\same.mp4", () => _tempDir);
        var second = new VoiceOverRecoveryManager(() => @"C:\clips\same.mp4", () => _tempDir);
        string wavA = CreateTestWav("a_session.wav", 1.0);
        string wavB = CreateTestWav("b_session.wav", 1.0);
        var a = new VoiceOverWindow.VoiceOverSession { WavPath = wavA, StartSec = 0, EndSec = 1 };
        var b = new VoiceOverWindow.VoiceOverSession { WavPath = wavB, StartSec = 1, EndSec = 2 };
        first.RegisterFailedTake(a, null, "first failure");
        second.RegisterFailedTake(b, null, "second failure");
        var reopened = new VoiceOverRecoveryManager(() => @"C:\clips\same.mp4", () => _tempDir);
        reopened.CheckAndOfferReopenRecovery();
        Assert.Contains(reopened.PendingFailedTakes, t => t.Session.WavPath == a.WavPath);
        Assert.Contains(reopened.PendingFailedTakes, t => t.Session.WavPath == b.WavPath);
    }

    [Fact]
    public void NinthAudit_FailedPublicationMustRemainPendingAndDurable()
    {
        var manager = new VoiceOverRecoveryManager(() => @"C:\clips\same.mp4", () => _tempDir);
        string wavPath = CreateTestWav("publish_fail.wav", 1.0);
        var take = new VoiceOverWindow.VoiceOverSession { WavPath = wavPath, StartSec = 0, EndSec = 1 };
        manager.RegisterFailedTake(take, null, "writer failure");
        using var capture = new VoiceCaptureSession(new TestDevices(), a => a());
        var result = manager.RecoverTakes(capture, _ => throw new InvalidOperationException("UI publication rejected"));
        Assert.Equal(1, result.FailedCount);
        Assert.True(manager.HasUnresolvedFailedTakes, "A failed callback left take resolved and removed its recovery record.");
        Assert.True(File.Exists(manager.GetRecoveryManifestPath()));
    }

    private sealed class FaultingTestDevices : IVoiceCaptureDeviceFactory
    {
        public bool HasInputDevice => true;
        public int DeviceCount => 1;
        public IReadOnlyList<string> GetDeviceNames() => new[] { "Faulting Microphone" };
        public IMicMonitorDevice CreateMonitor() => new TestMonitor();
        public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber) => new FaultingTestRecorder();
    }

    private sealed class FaultingTestRecorder : IVoiceRecorderDevice
    {
        public event EventHandler<float>? VolumeChanged { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public bool IsRecording => true;
        public long BytesCaptured => 88200;
        public int BuffersSeen => 20;
        public float PeakSeen => 0.5f;
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() => throw new InvalidOperationException("Injected native endpoint release failure");
    }

    internal sealed class BlockingTestDevices : IVoiceCaptureDeviceFactory
    {
        public bool HasInputDevice => true;
        public int DeviceCount => 1;
        public IReadOnlyList<string> GetDeviceNames() => new[] { "Blocking Microphone" };
        public ManualResetEventSlim ReleaseGate { get; } = new(false);
        public IMicMonitorDevice CreateMonitor() => new TestMonitor();
        public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber) => new BlockingTestRecorder(ReleaseGate);
    }

    internal sealed class BlockingTestRecorder(ManualResetEventSlim releaseGate) : IVoiceRecorderDevice
    {
        public event EventHandler<float>? VolumeChanged { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public bool IsRecording => true;
        public long BytesCaptured => 88200;
        public int BuffersSeen => 20;
        public float PeakSeen => 0.5f;
        public void StartRecording() { }
        public void StopRecording()
        {
            releaseGate.Wait(TimeSpan.FromSeconds(5));
        }
        public void Dispose() { }
    }

    internal sealed class TestDevices : IVoiceCaptureDeviceFactory
    {
        public bool HasInputDevice => true;
        public int DeviceCount => 1;
        public IReadOnlyList<string> GetDeviceNames() => new[] { "Test Microphone" };
        public IMicMonitorDevice CreateMonitor() => new TestMonitor();
        public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber) => new TestRecorder();
    }

    internal sealed class TestMonitor : IMicMonitorDevice
    {
        public bool IsRunning => false;
        public int BuffersSeen => 0;
        public event EventHandler<float>? LevelChanged { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public void Start(int deviceNumber) { }
        public void Stop() { }
        public void Dispose() { }
    }

    internal sealed class TestRecorder : IVoiceRecorderDevice
    {
        public event EventHandler<float>? VolumeChanged { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public bool IsRecording => true;
        public long BytesCaptured => 88200;
        public int BuffersSeen => 20;
        public float PeakSeen => 0.5f;
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }
    }
}
