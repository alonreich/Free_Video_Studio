using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>VORECOVERY_03 test seam: allows testing deliberate deletion confirmation without blocking UI.</summary>
    internal static Func<VoiceOverWindow, string, string, Task<bool>>? ConfirmDeleteSeam { get; set; }

    /// <summary>VORECOVERY_03 — true while the delete confirmation dialog is open.</summary>
    private bool _isConfirmingDelete;

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
        bool IsMuted,
        double OutputOffsetSec = 0,
        double? CapturedDurationSec = null,
        double RealTrimLeft = 0,
        double RealTrimRight = 0);

    private async void ApplyAndClose() => await ApplyAndCloseAsync();

    internal async Task ApplyAndCloseAsync()
    {
        if (_isCommitted || _isApplying || _isClosing) return;
        if (!CanAdmit(VoiceOverSessionOwner.Apply))
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
            if (_sessions.Any(s => !s.IsMuted && s.NeedsTimingReview))
            {
                UpdateApplyState("Review the marked takes. Play each one, then choose Keep this timing, or trim or replace it.");
                return;
            }
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
                    s.IsMuted,
                    s.OutputOffsetSec,
                    s.CapturedDurationSec,
                    SessionTrimLeft(s),
                    SessionTrimRight(s)));
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
                            double realTrimLeft = session.RealTrimLeft;
                            double realTrimRight = session.RealTrimRight;
                            double totalRealDur = session.CapturedDurationSec ?? (_timeline != null ? Math.Max(0, _timeline.SourceToOutput(session.EndSec) - _timeline.SourceToOutput(session.StartSec)) : (session.EndSec - session.StartSec));
                            double realDur = Math.Max(0.001, totalRealDur - realTrimLeft - realTrimRight);

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
                            startInfo.ArgumentList.Add("pcm_s16le");
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
                        double Out(double source) => _timeline?.SourceToOutput(source) ?? source;
                        double duration = Math.Max(0, (session.CapturedDurationSec ??
                            Math.Max(0, Out(session.EndSec) - Out(session.StartSec))) - session.RealTrimLeft - session.RealTrimRight);
                        double offset = Out(session.StartSec) + session.OutputOffsetSec + session.RealTrimLeft - Out(session.RenderStartSec);
                        double end = SourceEndForCapturedAudio(session.RenderStartSec, duration, offset);
                        string? signature = _timeline == null ? null : VoiceOverTiming.Signature(
                            _timeline, _trimStartSec, _trimEndSec, session.RenderStartSec, end);
                        persistedTakes.Add(new VoiceOverTake(persistedPath, session.RenderStartSec, offset, duration, end, signature));
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
            if (!keepPaths.Contains(session.WavPath) &&
                InitialState?.VoiceOverTakes.Any(t => string.Equals(t.Path, session.WavPath, StringComparison.OrdinalIgnoreCase)) != true &&
                !string.Equals(InitialState?.VoiceOverWavPath, session.WavPath, StringComparison.OrdinalIgnoreCase))
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
        return _isRecording || _capture.HasPendingFinalizations || HasSavedVoiceOverSession();
    }

    private void UpdateApplyState(string? message = null)
    {
        if (message != null)
        {
            _lastApplyErrorMessage = message;
        }
        bool canApply = HasApplicableVoiceEffect() && !_capture.HasUnreleasedDevice && !_lastTakeFailedFinalization && !HasUnresolvedFailedTakes && !_recovery.IsOperationInFlight;
        bool needsReview = _sessions.Any(s => !s.IsMuted && s.NeedsTimingReview);
        canApply &= !needsReview;
        string? effectiveMessage = message ?? _lastApplyErrorMessage;
        if (effectiveMessage == null && needsReview)
            effectiveMessage = "Timing changed. Select each marked take, play it, then Keep this timing—or trim or replace it.";
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
            if (_recovery.IsOperationInFlight || _isConfirmingDelete) return VoiceOverSessionOwner.Recovery;
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
        UpdateTransportState();
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

    /// <summary>VORECOVERY_01 / VORECOVERY_03 — DELETE unsaved voice recordings with deliberate confirmation.
    /// Snapshots intended recovery identities before asynchronous confirmation and revalidates
    /// ownership and take existence before deletion. Deletes only confirmed items.</summary>
    internal async Task<bool> DiscardFailedTakesAction()
    {
        if (_isConfirmingDelete || !CanStartRecoveryOperation()) return false;

        var unresolved = _recovery.PendingFailedTakes.Where(t => t.BlocksApply).ToList();
        if (unresolved.Count == 0) return false;

        var snapshotIds = unresolved.Select(t => t.Id).ToList();

        _isConfirmingDelete = true;
        BeginRecoveryOperationUi();

        bool confirmed = false;
        try
        {
            int count = snapshotIds.Count;
            string title = count > 1
                ? $"Delete {count} Unsaved Voice Recordings"
                : "Delete Unsaved Voice Recording";
            string message = count > 1
                ? $"Permanently delete all {count} unsaved voice recordings?\n\nThis action applies to all {count} offered recordings and will permanently remove their audio files from disk."
                : $"Permanently delete this unsaved voice recording ({unresolved[0].Session.StartSec:0.0}s..{unresolved[0].Session.EndSec:0.0}s)?\n\nThe audio file will be permanently removed from disk and cannot be recovered.";

            if (ConfirmDeleteSeam != null)
            {
                confirmed = await ConfirmDeleteSeam(this, title, message);
            }
            else if (IsVisible)
            {
                var dlg = new Controls.ConfirmDialogWindow();
                dlg.SetTitle(title);
                dlg.SetMessage(message);
                dlg.SetButtonText("DELETE", "CANCEL");
                dlg.UseDestructiveStyling();
                await dlg.ShowDialog(this);
                confirmed = dlg.Result;
            }
            else
            {
                confirmed = false;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"VORECOVERY_03 delete confirmation prompt faulted: {ex.Message}");
            confirmed = false;
        }
        finally
        {
            _isConfirmingDelete = false;
        }

        if (!confirmed)
        {
            EndRecoveryOperationUi();
            UpdateFailedTakesUi();
            return false;
        }

        if (!CanStartRecoveryOperation() || _closeRequestedDuringRecovery || _isClosing || _isCommitted || _isApplying)
        {
            EndRecoveryOperationUi();
            UpdateFailedTakesUi();
            return false;
        }

        var targets = _recovery.PendingFailedTakes.Where(t => t.BlocksApply && snapshotIds.Contains(t.Id)).ToList();
        if (targets.Count == 0)
        {
            EndRecoveryOperationUi();
            UpdateFailedTakesUi();
            return false;
        }

        var targetIds = targets.Select(t => t.Id).ToList();
        var pending = _recovery.DiscardTakesAsync(_capture, targetIds);
        BeginRecoveryOperationUi();
        (bool success, string? error) = (false, null);
        try
        {
            (success, error) = await pending;
        }
        finally
        {
            EndRecoveryOperationUi();
        }

        if (!success)
        {
            _lastApplyErrorMessage = error;
            Controls.FloatingNotice.Error(this, error ?? "Failed to delete one or more audio files.");
            UpdateApplyState(error);
            return false;
        }

        _lastTakeFailedFinalization = _recovery.HasUnresolvedFailedTakes;
        _lastApplyErrorMessage = null;
        UpdateFailedTakesUi();
        UpdateApplyState();
        return true;
    }

    /// <summary>VORECOVERY_01 / VORECOVERY_03 — RESTORE unsaved audio into session.
    /// WAV validation runs off the dispatcher; publication into _sessions happens here, on UI thread.</summary>
    internal async Task<bool> RecoverFailedTakesAction()
    {
        if (_isConfirmingDelete || !CanStartRecoveryOperation()) return false;
        var pending = _recovery.RecoverTakesAsync(_capture, session =>
        {
            if (_isCommitted || _isClosing || _isApplying) return false;
            if (session.CapturedDurationSec is double duration)
                session.EndSec = Math.Max(session.StartSec + 0.001,
                    SourceEndForCapturedAudio(session.StartSec, duration, session.OutputOffsetSec));
            _sessions.Add(session);
            return true;
        });
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
            UpdateFailedTakesUi();
            UpdateApplyState(result.StorageError);
            Controls.FloatingNotice.Success(this, "Recovered audio added to your voiceover session.");
            return true;
        }

        if (result.ErrorMessage != null)
        {
            _lastApplyErrorMessage = result.ErrorMessage;
            Controls.FloatingNotice.Error(this, result.ErrorMessage);
            UpdateFailedTakesUi();
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
            var unresolved = _recovery.PendingFailedTakes.Where(t => t.BlocksApply).ToList();
            int count = unresolved.Count;
            bool busy = !CanAdmit(VoiceOverSessionOwner.Recovery);
            if (_discardFailedTakesButton != null)
            {
                _discardFailedTakesButton.IsEnabled = !busy;
                _discardFailedTakesButton.Content = "DELETE";
                ToolTip.SetTip(_discardFailedTakesButton, count > 1
                    ? $"Delete all {count} unsaved voice recordings"
                    : "Delete unsaved voice recording");
            }
            if (_recoverFailedTakesButton != null)
            {
                _recoverFailedTakesButton.IsEnabled = !busy;
                _recoverFailedTakesButton.Content = "RESTORE";
                ToolTip.SetTip(_recoverFailedTakesButton, count > 1
                    ? $"Restore all {count} unsaved voice recordings into session"
                    : "Restore unsaved voice recording into session");
            }

            if (_voiceOverHintText != null && count > 0)
            {
                if (_lastApplyErrorMessage != null)
                {
                    _voiceOverHintText.Text = _lastApplyErrorMessage;
                    _voiceOverHintText.Foreground = GetAppBrush("AppDangerBrush", Brushes.Red);
                }
                else if (count == 1)
                {
                    _voiceOverHintText.Text = "We found an unsaved voice recording.";
                    _voiceOverHintText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Orange);
                }
                else
                {
                    _voiceOverHintText.Text = $"We found {count} unsaved voice recordings. RESTORE and DELETE apply to all {count} recordings.";
                    _voiceOverHintText.Foreground = GetAppBrush("AppWarningBrush", Brushes.Orange);
                }

                string takeDetails = string.Join("; ", unresolved.ConvertAll(t =>
                    $"{System.IO.Path.GetFileName(t.Session.WavPath)} ({t.Session.StartSec:0.0}s..{t.Session.EndSec:0.0}s: {t.FailureReason})"));
                ToolTip.SetTip(_voiceOverHintText, $"Unsaved recording details: {takeDetails}");
                RuntimeLog.Info("VoiceOver", $"[VORECOVERY_03] Unsaved recordings offer ({count} take(s)): {takeDetails}");
            }
        }
        else
        {
            if (_voiceOverHintText != null && _lastApplyErrorMessage == null)
            {
                ToolTip.SetTip(_voiceOverHintText, null);
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
        ClearLiveZoomCrop();

        if ((_recovery.IsOperationInFlight || _isConfirmingDelete) && !_isCommitted)
        {
            e.Cancel = true;
            _closeRequestedDuringRecovery = true;
            return;
        }

        if (_recovery.HasDurableTakes)
        {
            _ = ObserveRecoveryStoreAsync(_recovery.PersistAsync());
        }

        if (_isSafeToClose || _isCommitted)
        {
            if (!_isClosing) WindowBoundsHelper.SaveBoundsOnClose(this, BoundsKey);
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
        WindowBoundsHelper.SaveBoundsOnClose(this, BoundsKey);
    }
}
