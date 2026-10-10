// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;
using static FreeVideoStudio.App.Infrastructure.VoiceOverAudioTools;

namespace FreeVideoStudio.App;

/// <summary>
/// VOAPPLY_01 / VOCAPTURE_02 / SYS-WINSTATE — VoiceOverWindow Apply &amp; Finish transaction.
/// Coordinates single-flight apply, capture settlement, FFmpeg trimming, transactional persistence
/// with rollback on failure, file preservation, and safe closing.
/// </summary>
public partial class VoiceOverWindow
{
    internal static Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task>? TrimRunnerSeam { get; set; }
    internal static TimeSpan CaptureDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>VORECOVERY_01 test seams: isolated recovery storage BEFORE a window is constructed.</summary>
    internal static FreeVideoStudio.App.Services.IVoiceOverRecoveryStore? RecoveryStoreSeam { get; set; }
    internal static Func<string>? RecoveryDirectorySeam { get; set; }

    /// <summary>VORECOVERY_01 — a close requested while a recovery operation owns the take list is
    /// deferred, then re-issued when that operation completes (ownership is never abandoned).</summary>
    private bool _closeRequestedDuringRecovery;

    private FreeVideoStudio.App.Services.VoiceOverRecoveryManager CreateRecoveryManager()
        => new(() => _videoPath, () => RecoveryDirectorySeam?.Invoke() ?? _paths.TempDirectory, RecoveryStoreSeam);

    internal sealed record ApplyTakeSnapshot(
        string WavPath,
        double StartSec,
        double EndSec,
        double TrimLeftSec,
        double TrimRightSec,
        double RenderStartSec,
        bool IsMuted);

    private async void ApplyAndClose() => await ApplyAndCloseAsync();

    internal async Task ApplyAndCloseAsync()
    {
        if (_isCommitted || _isApplying || _isClosing) return;
        if (!CanAdmit(VoiceOverSessionOwner.Apply))   // SESSIONOWNER_01 — a recovery operation owns the session
        {
            UpdateApplyState("Recovered audio is still being checked. Apply when it finishes.");
            return;
        }
        _isApplying = true;
        _lastApplyErrorMessage = null;
        UpdateTransportState();

        RuntimeLog.Info("VoiceOver", "[Stage: ApplyStart] VOAPPLY_01 — Applying voiceover session...");

        try
        {
            if (_isRecording)
            {
                StopRecordingAndPlayback();
            }

            // VOASYNC_02 — the last take may still be draining on the audio chain.
            if (_capture.HasPendingFinalizations)
            {
                RuntimeLog.Info("VoiceOver", "[Stage: CaptureDrain] Draining active capture finalizations...");
                if (_applyButton != null) { _applyButton.IsEnabled = false; _applyButton.Content = "SAVING..."; }
                var settleTask = _capture.WhenFinalizationsSettled();
                var timeoutTask = Task.Delay(CaptureDrainTimeout);
                var finishedTask = await Task.WhenAny(settleTask, timeoutTask);
                if (finishedTask != settleTask)
                {
                    RuntimeLog.Fail("VoiceOver", "Voice capture finalization timed out while saving. Aborting commit to prevent data loss.");
                    Result = null;
                    UpdateApplyState("Voice capture finalization timed out. Your recordings are preserved. Please wait a moment and retry.");
                    return;
                }

                await settleTask;
                RuntimeLog.Info("VoiceOver", "[Stage: CaptureDrained] Finalizations settled.");
                if (_isClosing) return;
            }

            if (_capture.HasUnreleasedDevice || _lastTakeFailedFinalization || HasUnresolvedFailedTakes)
            {
                RuntimeLog.Fail("VoiceOver", "Voice capture finalization failed: endpoint release or take finalization could not be verified, or unresolved failed takes exist. Aborting commit to prevent data loss.");
                Result = null;
                UpdateApplyState(_lastApplyErrorMessage ?? "Capture device release failed or unresolved recording failure detected. Your recordings are preserved. Please review or retry once resolved.");
                return;
            }

            bool duckAudio = _duckAudioCb?.IsChecked == true;
            bool protectFromMusic = _duckMusicCb?.IsChecked == true;
            _ = Task.Run(() => RememberVoiceProtectionChoices(duckAudio, protectFromMusic));

            var snapshot = new List<ApplyTakeSnapshot>(_sessions.Count);
            for (int sIdx = 0; sIdx < _sessions.Count; sIdx++)
            {
                var s = _sessions[sIdx];
                snapshot.Add(new ApplyTakeSnapshot(
                    s.WavPath,
                    s.StartSec,
                    s.EndSec,
                    s.TrimLeftSec,
                    s.TrimRightSec,
                    s.RenderStartSec,
                    s.IsMuted));
            }

            bool hasValidTake = false;
            for (int sIdx = 0; sIdx < snapshot.Count; sIdx++)
            {
                var s = snapshot[sIdx];
                if (!s.IsMuted &&
                    s.EndSec > s.StartSec &&
                    !string.IsNullOrWhiteSpace(s.WavPath) &&
                    System.IO.File.Exists(s.WavPath))
                {
                    hasValidTake = true;
                    break;
                }
            }

            if (!hasValidTake)
            {
                Result = null;
                UpdateApplyState("Nothing to apply yet. Record a take before applying.");
                return;
            }

            var persistedTakes = new List<VoiceOverTake>();
            var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newlyCreatedFiles = new List<string>();

            if (snapshot.Count > 0)
            {
                RuntimeLog.Info("VoiceOver", $"[Stage: PersistStart] Persisting {snapshot.Count} takes...");
                if (_applyButton != null) { _applyButton.IsEnabled = false; _applyButton.Content = "SAVING..."; }

                for (int i = 0; i < snapshot.Count; i++)
                {
                    var session = snapshot[i];
                    if (session.IsMuted ||
                        session.EndSec <= session.StartSec ||
                        string.IsNullOrWhiteSpace(session.WavPath) ||
                        !System.IO.File.Exists(session.WavPath))
                    {
                        continue;
                    }

                    try
                    {
                        string persistedPath = CreatePersistedVoiceOverPath();
                        newlyCreatedFiles.Add(persistedPath);

                        if (session.TrimLeftSec > 0 || session.TrimRightSec > 0)
                        {
                            double realTrimLeft = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.StartSec + session.TrimLeftSec) - _timeline.SourceToOutput(session.StartSec)) : session.TrimLeftSec;
                            double realTrimRight = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.EndSec) - _timeline.SourceToOutput(session.EndSec - session.TrimRightSec)) : session.TrimRightSec;
                            double totalRealDur = _timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.EndSec) - _timeline.SourceToOutput(session.StartSec)) : (session.EndSec - session.StartSec);
                            double realDur = Math.Max(0.1, totalRealDur - realTrimLeft - realTrimRight);

                            var startInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = ResolveBinaryPath("ffmpeg.exe", "backend"),
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            startInfo.ArgumentList.Add("-y");
                            startInfo.ArgumentList.Add("-i");
                            startInfo.ArgumentList.Add(session.WavPath);
                            startInfo.ArgumentList.Add("-ss");
                            startInfo.ArgumentList.Add(realTrimLeft.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                            startInfo.ArgumentList.Add("-t");
                            startInfo.ArgumentList.Add(realDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                            startInfo.ArgumentList.Add("-c");
                            startInfo.ArgumentList.Add("copy");
                            startInfo.ArgumentList.Add(persistedPath);

                            if (TrimRunnerSeam != null)
                            {
                                await TrimRunnerSeam(startInfo, CancellationToken.None);
                            }
                            else
                            {
                                using var procCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                                using var proc = System.Diagnostics.Process.Start(startInfo);
                                if (proc == null) throw new InvalidOperationException($"FFmpeg trim process failed to start for take {i + 1}");
                                try
                                {
                                    await proc.WaitForExitAsync(procCts.Token);
                                }
                                catch (OperationCanceledException)
                                {
                                    try
                                    {
                                        if (!proc.HasExited)
                                        {
                                            proc.Kill(entireProcessTree: true);
                                            await proc.WaitForExitAsync();
                                        }
                                    }
                                    catch (Exception kex) { RuntimeLog.Swallowed(kex); }
                                    throw new TimeoutException($"FFmpeg trim timed out after 15 seconds for take {i + 1}.");
                                }

                                if (proc.ExitCode != 0 || !System.IO.File.Exists(persistedPath) || new System.IO.FileInfo(persistedPath).Length == 0)
                                {
                                    throw new Exception($"FFmpeg trim failed with code {proc.ExitCode} for take {i + 1}");
                                }
                            }
                        }
                        else
                        {
                            await Task.Run(() => File.Copy(session.WavPath, persistedPath, overwrite: true));
                        }
                        persistedTakes.Add(new VoiceOverTake(persistedPath, session.RenderStartSec));
                        keepPaths.Add(persistedPath);
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Fail("VoiceOver", $"Voiceover take {i + 1} could not be persisted. {ex.Message}");
                        foreach (var path in newlyCreatedFiles)
                        {
                            TryDeleteFile(path);
                        }
                        Result = null;
                        UpdateApplyState($"Voiceover take {i + 1} could not be saved ({ex.Message}). Recordings preserved; you can retry.");
                        return;
                    }
                }

                RuntimeLog.Info("VoiceOver", "[Stage: PersistEnd] Persistence complete.");
                // VORECOVERY_01 — commit boundary = every take is persisted to its new file. Only the
                // committed take ids leave the durable index (off-thread); other sessions' records and
                // still-pending takes are untouched. A failed index update does not undo a completed
                // save: the persisted copies are delivered in Result, and any stale record left behind
                // reopens as an explicit "audio missing" entry the user discards — never as silent loss.
                var commitStore = await _recovery.CommitTakesAsync(snapshot.ConvertAll(s => s.WavPath));
                if (!commitStore.Success)
                    RuntimeLog.Fail("VoiceOver", $"[Stage: RecoveryCommit] takes saved, recovery index cleanup failed: {commitStore.Error}");
                DeleteSessionFilesExcept(keepPaths);
            }

            if (persistedTakes.Count == 0)
            {
                Result = null;
                UpdateApplyState("Voiceover audio could not be prepared. Record another take.");
                return;
            }

            string? finalWav = persistedTakes.Count > 0 ? persistedTakes[0].Path : null;
            double finalStart = persistedTakes.Count > 0 ? persistedTakes[0].StartSec : 0;

            Result = new VoiceOverResult
            {
                VoiceOverWavPath = finalWav,
                VoiceOverStartTimestampSec = finalStart,
                VoiceOverTakes = persistedTakes,
                DuckAudio = duckAudio,
                ProtectFromMusic = protectFromMusic
            };

            RuntimeLog.Info("VoiceOver", "[Stage: ModalClose] VOAPPLY_01 — Closing VoiceOverWindow...");
            _isCommitted = true;
            _isSafeToClose = true;
            _isApplying = false;
            Close();
        }
        finally
        {
            if (!_isSafeToClose)
            {
                _isApplying = false;
                UpdateTransportState();
                UpdateApplyState();
            }
        }
    }

    private void DeleteSessionFilesExcept(IReadOnlySet<string> keepPaths)
    {
        foreach (var session in _sessions)
        {
            if (!keepPaths.Contains(session.WavPath))
            {
                TryDeleteFile(session.WavPath);
            }
        }
        if (!keepPaths.Contains(_outputWavPath))
        {
            TryDeleteFile(_outputWavPath);
        }
    }

    private void DeleteUnappliedVoiceOverFiles()
    {
        if (_capture.HasUnreleasedDevice || _lastTakeFailedFinalization || HasUnresolvedFailedTakes)
        {
            RuntimeLog.Info("VoiceOver", "Retaining unapplied audio files because capture device or take finalization has uncommitted/unreleased recordings or unresolved failed takes.");
            return;
        }

        var appliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Result?.VoiceOverWavPath))
        {
            appliedPaths.Add(Result!.VoiceOverWavPath!);
        }
        if (Result?.VoiceOverTakes != null)
        {
            foreach (var take in Result.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path))
                {
                    appliedPaths.Add(take.Path);
                }
            }
        }
        
        if (InitialState?.VoiceOverTakes != null)
        {
            foreach (var take in InitialState.VoiceOverTakes)
            {
                if (!string.IsNullOrWhiteSpace(take.Path))
                {
                    appliedPaths.Add(take.Path);
                }
            }
        }

        var durableWavs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _recovery.PendingFailedTakes)
        {
            if (t.MustRemainDurable && !string.IsNullOrWhiteSpace(t.Session.WavPath))
            {
                durableWavs.Add(t.Session.WavPath);
            }
        }

        foreach (var session in _sessions)
        {
            if (!appliedPaths.Contains(session.WavPath))
            {
                if (durableWavs.Contains(session.WavPath))
                {
                    continue;
                }
                TryDeleteFile(session.WavPath);
            }
        }

        if (_currentSession != null)
        {
            if (!appliedPaths.Contains(_currentSession.WavPath))
            {
                TryDeleteFile(_currentSession.WavPath);
            }
        }

        if (!appliedPaths.Contains(_outputWavPath))
        {
            TryDeleteFile(_outputWavPath);
        }
    }

    private string CreateTempVoiceOverPath()
    {
        Directory.CreateDirectory(_paths.TempDirectory);
        return System.IO.Path.Combine(_paths.TempDirectory, $"voiceover_{Guid.NewGuid():N}.wav");
    }

    private string CreatePersistedVoiceOverPath()
    {
        string voiceOverDir = System.IO.Path.Combine(_paths.ProgramDataRoot, "voiceovers");
        Directory.CreateDirectory(voiceOverDir);
        return System.IO.Path.Combine(voiceOverDir, $"voiceover_{Guid.NewGuid():N}.wav");
    }

    private bool HasSavedVoiceOverSession()
    {
        foreach (var session in _sessions)
        {
            if (!session.IsMuted &&
                session.EndSec > session.StartSec &&
                !string.IsNullOrWhiteSpace(session.WavPath) &&
                System.IO.File.Exists(session.WavPath))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasApplicableVoiceEffect()
    {
        // VOASYNC_02 — a take whose drain has not landed yet is still a take. Without this the
        // Apply button and the discard prompt would both go blind for the ~100 ms after stop.
        return _isRecording || _capture.HasPendingFinalizations || HasSavedVoiceOverSession();
    }

    private void UpdateApplyState(string? message = null)
    {
        if (message != null)
        {
            _lastApplyErrorMessage = message;
        }
        bool canApply = HasApplicableVoiceEffect() && !_capture.HasUnreleasedDevice && !_lastTakeFailedFinalization && !HasUnresolvedFailedTakes && !_recovery.IsOperationInFlight;
        string? effectiveMessage = message ?? _lastApplyErrorMessage;
        if (effectiveMessage == null && !canApply && !_capture.HasInputDevice)
        {
            effectiveMessage = "No microphone input detected. You must record a take to apply voiceover.";
        }

        if (_applyButton != null)
        {
            _applyButton.IsEnabled = canApply && !_isApplying && !_isCommitted;
            _applyButton.Content = _isApplying
                ? "SAVING..."
                : (_capture.HasPendingFinalizations ? "DRAINING..." : "APPLY & CLOSE");
            ToolTip.SetTip(_applyButton, canApply
                ? "Apply recorded voiceover"
                : (HasUnresolvedFailedTakes ? "Resolve or discard failed takes before applying" : "Record a take before applying"));
        }

        if (_voiceOverHintText != null)
        {
            _voiceOverHintText.Text = effectiveMessage ?? (canApply
                ? "Ready to apply the current voiceover changes. Cancel discards unapplied takes."
                : (HasUnresolvedFailedTakes ? "Failed recording must be discarded or recovered before applying." : "Record a take before applying. Cancel discards unapplied takes."));
            _voiceOverHintText.Foreground = canApply
                ? GetAppBrush("AppTextPrimaryBrush", Brushes.White)
                : GetAppBrush("AppTextMutedBrush", Brushes.Gray);
        }

        UpdateFailedTakesUi();
    }

    /// <summary>
    /// SESSIONOWNER_01 — ONE policy for who owns the voiceover session. Recording (arming, live,
    /// opening, draining), a recovery operation (discovery, validation, discard), Apply and closing
    /// are mutually exclusive transactions. The owner is DERIVED from the live state each time, so it
    /// is released exactly when the owning operation's own state clears (stop, failure, late drain
    /// verdict, recovery completion) — there is no separate flag to forget to reset.
    /// Stop / pause / cancel of the current owner are never gated.
    /// </summary>
    internal enum VoiceOverSessionOwner { None, Capture, Recovery, Apply, Closing }

    internal VoiceOverSessionOwner CurrentSessionOwner
    {
        get
        {
            if (_isCommitted || _isClosing) return VoiceOverSessionOwner.Closing;
            if (_isApplying) return VoiceOverSessionOwner.Apply;
            if (_recovery.IsOperationInFlight) return VoiceOverSessionOwner.Recovery;
            if (_isRecording || _recordArming || _capture.IsOpeningRecorder || _capture.HasPendingFinalizations)
                return VoiceOverSessionOwner.Capture;
            return VoiceOverSessionOwner.None;
        }
    }

    /// <summary>Admission matrix. Apply may take over a capture (it stops and drains it itself);
    /// nothing may take over a recovery operation; a new capture or recovery needs an idle session.</summary>
    internal bool CanAdmit(VoiceOverSessionOwner operation)
    {
        var owner = CurrentSessionOwner;
        return operation switch
        {
            VoiceOverSessionOwner.Capture => owner == VoiceOverSessionOwner.None,
            VoiceOverSessionOwner.Recovery => owner == VoiceOverSessionOwner.None,
            VoiceOverSessionOwner.Apply => owner is VoiceOverSessionOwner.None or VoiceOverSessionOwner.Capture,
            _ => false
        };
    }

    private bool TryAdmitCapture()
    {
        if (CanAdmit(VoiceOverSessionOwner.Capture)) return true;
        if (CurrentSessionOwner == VoiceOverSessionOwner.Recovery)
        {
            RuntimeLog.Info("VoiceOver", "[SESSIONOWNER_01] Record refused: a recovery operation owns the session.");
            Controls.FloatingNotice.Warn(this, "Recovered audio is still being checked. Record when it finishes.");
        }
        return false;
    }

    private bool CanStartRecoveryOperation() => CanAdmit(VoiceOverSessionOwner.Recovery);

    internal Button? RecordButtonControl => _micRecordButton;
    internal void TriggerStopRecording() => StopRecordingAndPlayback();

    /// <summary>The drain verdict is published BEFORE the finalization settles (VOCAPTURE_02), so the
    /// controls are refreshed again once capture ownership has actually ended.</summary>
    private async Task RefreshWhenCaptureSettledAsync(Task drain)
    {
        try { await drain; await _capture.WhenFinalizationsSettled(); }
        catch (Exception ex) { RuntimeLog.Fail("VoiceOver", $"[SESSIONOWNER_01] capture drain faulted: {ex.Message}"); }
        if (_isClosing) return;
        UpdateTransportState();
        UpdateApplyState();
    }

    private void BeginRecoveryOperationUi()
    {
        if (_discardFailedTakesButton != null) _discardFailedTakesButton.IsEnabled = false;
        if (_recoverFailedTakesButton != null) _recoverFailedTakesButton.IsEnabled = false;
        UpdateTransportState();   // SESSIONOWNER_01 — RECORD is unavailable while recovery owns the session
        UpdateApplyState();
    }

    /// <summary>Restores usable controls and re-issues a close that arrived mid-operation.</summary>
    private void EndRecoveryOperationUi()
    {
        if (_discardFailedTakesButton != null) _discardFailedTakesButton.IsEnabled = true;
        if (_recoverFailedTakesButton != null) _recoverFailedTakesButton.IsEnabled = true;
        _lastTakeFailedFinalization = _recovery.HasUnresolvedFailedTakes;
        UpdateTransportState();
        UpdateApplyState();
        if (_closeRequestedDuringRecovery && !_isClosing && IsVisible)
        {
            _closeRequestedDuringRecovery = false;
            Close();
        }
    }

    /// <summary>VORECOVERY_01 — DISCARD FAILED TAKE. Deletion runs off the dispatcher; only proven
    /// deletions become Discarded; a storage failure is reported, never hidden.</summary>
    internal async Task<bool> DiscardFailedTakesAction()
    {
        if (!CanStartRecoveryOperation()) return false;
        var pending = _recovery.DiscardTakesAsync(_capture);   // takes ownership synchronously
        BeginRecoveryOperationUi();
        (bool success, string? error) = (false, null);
        try { (success, error) = await pending; }
        finally { EndRecoveryOperationUi(); }

        if (!success)
        {
            _lastApplyErrorMessage = error;
            Controls.FloatingNotice.Error(this, error ?? "Failed to delete one or more audio files.");
            UpdateApplyState(error);
            return false;
        }

        _lastTakeFailedFinalization = _recovery.HasUnresolvedFailedTakes;
        _lastApplyErrorMessage = null;
        UpdateApplyState();
        return true;
    }

    /// <summary>VORECOVERY_01 — RECOVER AUDIO. WAV validation runs off the dispatcher; publication
    /// into <c>_sessions</c> happens here, on the UI thread, and is refused once the studio is
    /// applying/committed/closing (late completion cannot mutate a committed result).</summary>
    internal async Task<bool> RecoverFailedTakesAction()
    {
        if (!CanStartRecoveryOperation()) return false;
        var pending = _recovery.RecoverTakesAsync(_capture, session =>
        {
            if (_isCommitted || _isClosing || _isApplying) return false;
            _sessions.Add(session);
            return true;
        });   // takes ownership synchronously, before its first await
        BeginRecoveryOperationUi();
        FreeVideoStudio.App.Services.RecoveryBatchResult result;
        try
        {
            result = await pending;
        }
        finally { EndRecoveryOperationUi(); }

        if (result.StorageError != null)
        {
            Controls.FloatingNotice.Error(this, result.StorageError);
        }

        if (result.RecoveredCount > 0)
        {
            if (!_recovery.HasUnresolvedFailedTakes && result.StorageError == null) _lastApplyErrorMessage = null;
            _renderedSessionCount = -1;
            UpdatePlayheadUI();
            UpdateApplyState(result.StorageError);
            Controls.FloatingNotice.Success(this, "Recovered audio added to your voiceover session.");
            return true;
        }

        if (result.ErrorMessage != null)
        {
            _lastApplyErrorMessage = result.ErrorMessage;
            Controls.FloatingNotice.Error(this, result.ErrorMessage);
            UpdateApplyState(result.ErrorMessage);
        }
        return false;
    }

    internal async Task DiscardPendingTake(FreeVideoStudio.App.Services.PendingFailedTake take)
    {
        if (!CanStartRecoveryOperation() || _capture.HasUnreleasedDevice) return;
        var pending = _recovery.DiscardPendingTakeAsync(_capture, take);
        BeginRecoveryOperationUi();
        (bool success, string? error) = (false, null);
        try { (success, error) = await pending; }
        finally { EndRecoveryOperationUi(); }
        if (!success && error != null)
        {
            Controls.FloatingNotice.Error(this, error);
            UpdateApplyState(error);
            return;
        }
        if (!_recovery.HasUnresolvedFailedTakes) _lastApplyErrorMessage = null;
        UpdateApplyState();
    }

    internal Task<bool> DiscardAllPendingTakes() => DiscardFailedTakesAction();

    /// <summary>VORECOVERY_01 — an index write that fails is surfaced with the audio still on disk.</summary>
    private async Task ObserveRecoveryStoreAsync(Task<FreeVideoStudio.App.Services.RecoveryStoreResult> store)
    {
        FreeVideoStudio.App.Services.RecoveryStoreResult result;
        try { result = await store; }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"VORECOVERY_01 recovery index write faulted: {ex.Message}");
            result = new FreeVideoStudio.App.Services.RecoveryStoreResult(false, ex.Message);
        }
        if (result.Success || _isClosing) return;
        string message = $"The failed take's audio is still on disk, but its recovery record could not be saved ({result.Error}). Recover or discard it before closing.";
        Controls.FloatingNotice.Error(this, message);
        UpdateApplyState(message);
    }

    private void UpdateFailedTakesUi()
    {
        bool hasUnresolved = HasUnresolvedFailedTakes;
        if (_discardFailedTakesButton != null) _discardFailedTakesButton.IsVisible = hasUnresolved;
        if (_recoverFailedTakesButton != null) _recoverFailedTakesButton.IsVisible = hasUnresolved;

        if (hasUnresolved)
        {
            var unresolved = _recovery.PendingFailedTakes;
            bool busy = !CanAdmit(VoiceOverSessionOwner.Recovery);   // SESSIONOWNER_01 — also while capture owns the session
            if (_discardFailedTakesButton != null) _discardFailedTakesButton.IsEnabled = !busy;
            if (_recoverFailedTakesButton != null) _recoverFailedTakesButton.IsEnabled = !busy;
            FreeVideoStudio.App.Services.PendingFailedTake? lastUnresolved = null;
            for (int i = unresolved.Count - 1; i >= 0; i--)
            {
                if (!unresolved[i].IsResolved) { lastUnresolved = unresolved[i]; break; }
            }

            if (lastUnresolved != null && _voiceOverHintText != null)
            {
                string wavName = System.IO.Path.GetFileName(lastUnresolved.Session.WavPath);
                string takeInfo = $"Failed take {lastUnresolved.Session.StartSec:0.0}s..{lastUnresolved.Session.EndSec:0.0}s ({lastUnresolved.FailureReason}) preserved at {wavName}.";
                _voiceOverHintText.Text = $"{takeInfo} Click DISCARD FAILED TAKE to ignore, or RECOVER AUDIO to keep.";
                _voiceOverHintText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Orange);
            }
        }
    }

    /// <summary>Synchronous discovery (tests). Production opens with <see cref="CheckAndOfferReopenRecoveryAsync"/>.</summary>
    internal void CheckAndOfferReopenRecovery()
    {
        _recovery.CheckAndOfferReopenRecovery();
        PublishDiscoveredRecovery();
    }

    /// <summary>VORECOVERY_01 — reopen discovery reads the index off the dispatcher and blocks Apply
    /// (IsOperationInFlight) until every durable take of this video is on screen.</summary>
    internal async Task CheckAndOfferReopenRecoveryAsync()
    {
        if (!CanStartRecoveryOperation()) return;
        var pending = _recovery.CheckAndOfferReopenRecoveryAsync();
        BeginRecoveryOperationUi();
        try { await pending; }
        finally { EndRecoveryOperationUi(); }
        if (!_isClosing) PublishDiscoveredRecovery();
    }

    private void PublishDiscoveredRecovery()
    {
        if (_recovery.LoadErrors.Count > 0)
        {
            string message = "A voiceover recovery record could not be read and was left on disk: " + _recovery.LoadErrors[0];
            Controls.FloatingNotice.Error(this, message);
            _lastApplyErrorMessage = message;
        }
        if (_recovery.HasUnresolvedFailedTakes)
        {
            _lastTakeFailedFinalization = true;
            UpdateFailedTakesUi();
        }
        UpdateApplyState();
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // ZOOMLIVE_06 — this window pushes `video-crop` into a host the Main App owns and reuses.
        // Leaving a crop behind would zoom the main screen's preview after the studio closes.
        ClearLiveZoomCrop();

        // VORECOVERY_01 — a recovery operation owns the take list: defer the close, never abandon it.
        if (_recovery.IsOperationInFlight && !_isCommitted)
        {
            e.Cancel = true;
            _closeRequestedDuringRecovery = true;
            return;
        }

        // Every transition already persisted; this re-asserts durable records off the dispatcher.
        if (_recovery.HasDurableTakes)
        {
            _ = ObserveRecoveryStoreAsync(_recovery.PersistAsync());
        }

        if (_isSafeToClose || _isCommitted)
        {
            if (!_isClosing) WindowBoundsHelper.SaveBoundsSync(this, BoundsKey);
            return;
        }

        if (_isApplying)
        {
            e.Cancel = true;
            return;
        }

        if (HasApplicableVoiceEffect())
        {
            e.Cancel = true;
            var dialog = new FreeVideoStudio.App.Controls.ConfirmDialogWindow();
            dialog.SetTitle("DISCARD RECORDINGS?");
            dialog.SetMessage("You have unsaved voiceover takes. Are you sure you want to discard them and close?");
            dialog.SetButtonText("DISCARD", "KEEP EDITING");
            var top = Avalonia.Controls.TopLevel.GetTopLevel(this) as Window;
            if (top != null)
            {
                await dialog.ShowDialog(top);
                if (dialog.Result)
                {
                    _isSafeToClose = true;
                    Close();
                }
            }
            return;
        }

        if (_isClosing) return;
        WindowBoundsHelper.SaveBoundsSync(this, BoundsKey);
    }
}
