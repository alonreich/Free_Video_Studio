using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public class RecoveryManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly ApplicationPaths _paths;
    private readonly RecoveryManager _recovery;

    public RecoveryManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "FreeVideoStudio_RecoveryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _paths = new ApplicationPaths(_testDir);
        _recovery = new RecoveryManager(_paths);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void SaveState_And_LoadState_RoundTripsSuccessfully()
    {
        var state = new JsonObject
        {
            ["loadedVideoPath"] = "C:\\test\\video.mp4",
            ["trimStartMs"] = 1000.0,
            ["trimEndMs"] = 5000.0,
            ["baseSpeed"] = 1.25,
            ["freezeTimeMs"] = 2500.0,
            ["freezeDurationS"] = 1.5
        };

        _recovery.SaveState(state);

        var loaded = _recovery.LoadState();
        Assert.NotNull(loaded);
        Assert.Equal("C:\\test\\video.mp4", loaded["loadedVideoPath"]?.ToString());
        Assert.Equal(1000.0, loaded["trimStartMs"]?.GetValue<double>());
        Assert.Equal(5000.0, loaded["trimEndMs"]?.GetValue<double>());
        Assert.Equal(1.25, loaded["baseSpeed"]?.GetValue<double>());
        Assert.Equal(2500.0, loaded["freezeTimeMs"]?.GetValue<double>());
        Assert.Equal(1.5, loaded["freezeDurationS"]?.GetValue<double>());
    }

    [Fact]
    public void SaveState_PreservesExistingGranularSession()
    {
        var initialState = new JsonObject
        {
            ["loadedVideoPath"] = "C:\\test\\video.mp4",
            ["baseSpeed"] = 1.0,
            ["granular_session"] = new JsonObject
            {
                ["open"] = true,
                ["video_path"] = "C:\\test\\video.mp4",
                ["trim_start_ms"] = 0.0,
                ["trim_end_ms"] = 10000.0,
                ["freeze_time_ms"] = 3000.0,
                ["freeze_duration_s"] = 2.0
            }
        };

        _recovery.SaveState(initialState);

        var appSaveState = new JsonObject
        {
            ["loadedVideoPath"] = "C:\\test\\video.mp4",
            ["baseSpeed"] = 1.0,
            ["volume"] = 0.8
        };

        _recovery.SaveState(appSaveState);

        var reloaded = _recovery.LoadState();
        Assert.NotNull(reloaded);
        Assert.True(reloaded.ContainsKey("granular_session"));

        var granNode = reloaded["granular_session"]?.AsObject();
        Assert.NotNull(granNode);
        Assert.True(granNode["open"]?.GetValue<bool>());
        Assert.Equal(3000.0, granNode["freeze_time_ms"]?.GetValue<double>());
        Assert.Equal(2.0, granNode["freeze_duration_s"]?.GetValue<double>());
    }

    [Fact]
    public void LoadState_WithCorruptedJson_ReturnsNullWithoutThrowing()
    {
        _paths.EnsureWritableDirectories();
        File.WriteAllText(_paths.RecoveryStateFile, "{\"invalid_json\": [ unfinished...");

        var loaded = _recovery.LoadState();
        Assert.Null(loaded);
    }

    [Fact]
    public void GranularSession_SpeedSegmentWithZoom_RoundTripsAccurately()
    {
        var seg = new SpeedSegment(
            StartMs: 2500,
            EndMs: 6000,
            Speed: 0.25,
            ZoomX: 120,
            ZoomY: 80,
            ZoomW: 1280,
            ZoomH: 720,
            ZoomOrigRes: "1920x1080",
            ZoomSlow: true,
            ZoomStartMs: 2700,
            ZoomEndMs: 5800);

        var segNode = new JsonObject
        {
            ["start_ms"] = seg.StartMs,
            ["end_ms"] = seg.EndMs,
            ["speed"] = seg.Speed,
            ["zoom_x"] = seg.ZoomX,
            ["zoom_y"] = seg.ZoomY,
            ["zoom_w"] = seg.ZoomW,
            ["zoom_h"] = seg.ZoomH,
            ["zoom_orig_res"] = seg.ZoomOrigRes,
            ["zoom_slow"] = seg.ZoomSlow,
            ["zoom_start_ms"] = seg.ZoomStartMs,
            ["zoom_end_ms"] = seg.ZoomEndMs
        };

        var segsArray = new JsonArray { segNode };
        var root = new JsonObject
        {
            ["granular_session"] = new JsonObject
            {
                ["open"] = true,
                ["segments"] = segsArray
            }
        };

        _recovery.SaveState(root);

        var loaded = _recovery.LoadState();
        Assert.NotNull(loaded);
        var loadedSegs = loaded["granular_session"]?["segments"]?.AsArray();
        Assert.NotNull(loadedSegs);
        Assert.Single(loadedSegs);

        var item = loadedSegs[0]?.AsObject();
        Assert.NotNull(item);
        Assert.Equal(2500, item["start_ms"]?.GetValue<double>());
        Assert.Equal(6000, item["end_ms"]?.GetValue<double>());
        Assert.Equal(0.25, item["speed"]?.GetValue<double>());
        Assert.Equal(120, item["zoom_x"]?.GetValue<int>());
        Assert.Equal(80, item["zoom_y"]?.GetValue<int>());
        Assert.Equal(1280, item["zoom_w"]?.GetValue<int>());
        Assert.Equal(720, item["zoom_h"]?.GetValue<int>());
        Assert.Equal("1920x1080", item["zoom_orig_res"]?.GetValue<string>());
        Assert.True(item["zoom_slow"]?.GetValue<bool>());
        Assert.Equal(2700, item["zoom_start_ms"]?.GetValue<double>());
        Assert.Equal(5800, item["zoom_end_ms"]?.GetValue<double>());
    }


    [Fact]
    public void WriteOrder_AQueuedSaveThatLosesTheRaceToAClearIsDropped()
    {
        long queued = RecoveryManager.ReserveVersionForTests();
        _recovery.ClearState();
        _recovery.ApplySaveForTests(new JsonObject { ["stale"] = true }, queued);

        Assert.False(File.Exists(_paths.RecoveryStateFile));
    }

    [Fact]
    public void WriteOrder_OrderingSpansInstances()
    {
        var other = new RecoveryManager(_paths);
        long queued = RecoveryManager.ReserveVersionForTests();
        other.SaveState(new JsonObject { ["newer"] = true });
        _recovery.ApplySaveForTests(new JsonObject { ["older"] = true }, queued);

        var loaded = _recovery.LoadState();
        Assert.NotNull(loaded);
        Assert.True(loaded!.ContainsKey("newer"));
        Assert.False(loaded.ContainsKey("older"));
    }

    [Fact]
    public void WriteOrder_GranularMergeDoesNotStaleAQueuedMainSave()
    {
        long queued = RecoveryManager.ReserveVersionForTests();
        _recovery.UpdateGranularSession(new JsonObject { ["open"] = true });
        _recovery.ApplySaveForTests(new JsonObject { ["main"] = 1 }, queued);

        var loaded = _recovery.LoadState()!;
        Assert.True(loaded.ContainsKey("main"));
        Assert.NotNull(loaded["granular_session"]);
    }


    private const int DeadPid = 2147483645;

    private byte[] WriteSnapshot()
    {
        _recovery.SaveState(new JsonObject { ["loadedVideoPath"] = "C:\\test\\video.mp4", ["trimStartMs"] = 1234.0 });
        return File.ReadAllBytes(_paths.RecoveryStateFile);
    }

    /// <summary>Rewrites the live restore marker as if its owner process had died, keeping the attempt number.</summary>
    private void KillRestoreOwner()
    {
        string[] parts = File.ReadAllText(_paths.SafeModeSentinelFile).Trim().Split(':');
        parts[0] = DeadPid.ToString();
        File.WriteAllText(_paths.SafeModeSentinelFile, string.Join(":", parts));
    }

    [Fact]
    public void RestoreCrash_DeadRestoreOwner_PreservesSnapshotBytes_ClearsOnlyTheMarker()
    {
        byte[] before = WriteSnapshot();
        File.WriteAllText(_paths.SafeModeSentinelFile, $"{DeadPid}:638000000000000000:1");
        File.WriteAllText(_paths.AppSessionLockFile, $"{DeadPid}:638000000000000000");

        var next = new RecoveryManager(_paths);
        Assert.True(next.CheckFault());
        Assert.True(next.RestoreCrashDetected);
        Assert.Equal(1, next.CrashedRestoreAttempts);
        Assert.False(File.Exists(_paths.SafeModeSentinelFile));
        Assert.True(File.Exists(_paths.RecoveryStateFile));
        Assert.Equal(before, File.ReadAllBytes(_paths.RecoveryStateFile));
        Assert.NotNull(next.LoadState());
    }

    [Fact]
    public void RestoreCrash_DetectedEvenWithoutSessionLock()
    {
        byte[] before = WriteSnapshot();
        File.WriteAllText(_paths.SafeModeSentinelFile, $"{DeadPid}:638000000000000000:1");

        var next = new RecoveryManager(_paths);
        Assert.True(next.CheckFault());
        Assert.Equal(before, File.ReadAllBytes(_paths.RecoveryStateFile));
    }

    [Fact]
    public void RestoreCrash_LegacyOwnerlessMarker_OldEnough_PreservesSnapshot()
    {
        byte[] before = WriteSnapshot();
        File.WriteAllText(_paths.SafeModeSentinelFile, "");
        File.SetLastWriteTimeUtc(_paths.SafeModeSentinelFile, DateTime.UtcNow.AddHours(-1));

        var next = new RecoveryManager(_paths);
        Assert.False(next.IsSafeModeActive());
        Assert.Equal(1, next.CrashedRestoreAttempts);
        Assert.Equal(before, File.ReadAllBytes(_paths.RecoveryStateFile));
    }

    [Fact]
    public void RestoreCrash_LiveOwner_IsHonoured_AndNothingIsTouched()
    {
        byte[] before = WriteSnapshot();
        Assert.True(_recovery.ActivateSafeMode());

        var other = new RecoveryManager(_paths);
        Assert.True(other.IsSafeModeActive());
        Assert.False(other.CheckFault());
        Assert.False(other.RestoreCrashDetected);
        Assert.True(File.Exists(_paths.SafeModeSentinelFile));
        Assert.Equal(before, File.ReadAllBytes(_paths.RecoveryStateFile));
    }

    [Fact]
    public void RestoreCrash_Repeated_CountsUp_AndStopsRetryingAtTheLimit()
    {
        byte[] before = WriteSnapshot();
        File.WriteAllText(_paths.SafeModeSentinelFile, $"{DeadPid}:638000000000000000:1");

        for (int attempt = 1; attempt <= RecoveryManager.MaxRestoreAttempts; attempt++)
        {
            var launch = new RecoveryManager(_paths);
            Assert.True(launch.CheckFault());
            Assert.Equal(attempt, launch.CrashedRestoreAttempts);
            Assert.Equal(before, File.ReadAllBytes(_paths.RecoveryStateFile));

            if (attempt < RecoveryManager.MaxRestoreAttempts)
            {
                Assert.True(launch.CanRetryRestore);
                Assert.True(launch.ActivateSafeMode());
                KillRestoreOwner();
            }
            else
            {
                Assert.False(launch.CanRetryRestore);
                Assert.False(launch.ActivateSafeMode());
                Assert.False(File.Exists(_paths.SafeModeSentinelFile));

                Assert.True(launch.QuarantineSnapshot());
                Assert.False(File.Exists(_paths.RecoveryStateFile));
                Assert.Equal(before, File.ReadAllBytes(launch.QuarantinedSnapshotFile!));
            }
        }
    }

    [Fact]
    public void RestoreCrash_OneAttemptPerProcess()
    {
        WriteSnapshot();
        Assert.True(_recovery.ActivateSafeMode());
        Assert.False(_recovery.CanRetryRestore);
        Assert.False(_recovery.ActivateSafeMode());
    }

    [Fact]
    public void RestoreCrash_SuccessfulRetry_ClearsStreak_KeepsSnapshot()
    {
        byte[] before = WriteSnapshot();
        File.WriteAllText(_paths.SafeModeSentinelFile, $"{DeadPid}:638000000000000000:1");

        var launch = new RecoveryManager(_paths);
        Assert.True(launch.CheckFault());
        Assert.True(launch.ActivateSafeMode());
        Assert.StartsWith($"{Environment.ProcessId}:", File.ReadAllText(_paths.SafeModeSentinelFile));
        Assert.EndsWith(":2", File.ReadAllText(_paths.SafeModeSentinelFile).Trim());

        Assert.NotNull(launch.LoadState());
        launch.DeactivateSafeMode();

        Assert.False(launch.RestoreCrashDetected);
        Assert.False(File.Exists(_paths.SafeModeSentinelFile));
        Assert.Equal(before, File.ReadAllBytes(_paths.RecoveryStateFile));
        Assert.False(new RecoveryManager(_paths).CheckFault());
    }

    [Fact]
    public void RestoreCrash_ExplicitDiscard_IsTheOnlyDelete_AndBeatsAQueuedSave()
    {
        WriteSnapshot();
        File.WriteAllText(_paths.SafeModeSentinelFile, $"{DeadPid}:638000000000000000:1");
        var launch = new RecoveryManager(_paths);
        Assert.True(launch.CheckFault());
        Assert.True(File.Exists(_paths.RecoveryStateFile));

        long queued = RecoveryManager.ReserveVersionForTests();
        launch.DiscardRecoveryState();
        launch.ApplySaveForTests(new JsonObject { ["stale"] = true }, queued);

        Assert.False(File.Exists(_paths.RecoveryStateFile));
        Assert.False(launch.RestoreCrashDetected);
    }

    [Fact]
    public void RestoreCrash_QuarantineBeatsAQueuedSave()
    {
        byte[] before = WriteSnapshot();
        long queued = RecoveryManager.ReserveVersionForTests();
        Assert.True(_recovery.QuarantineSnapshot());
        _recovery.ApplySaveForTests(new JsonObject { ["stale"] = true }, queued);

        Assert.False(File.Exists(_paths.RecoveryStateFile));
        Assert.Equal(before, File.ReadAllBytes(_recovery.QuarantinedSnapshotFile!));
    }

    [Fact]
    public void RestoreFail_SecondQuarantineNeverOverwritesTheFirst()
    {
        byte[] first = WriteSnapshot();
        Assert.True(_recovery.QuarantineSnapshot());
        string firstPath = _recovery.QuarantinedSnapshotFile!;

        _recovery.SaveState(new JsonObject { ["second"] = true });
        byte[] second = File.ReadAllBytes(_paths.RecoveryStateFile);
        Assert.True(_recovery.QuarantineSnapshot());
        string secondPath = _recovery.QuarantinedSnapshotFile!;

        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal(first, File.ReadAllBytes(firstPath));
        Assert.Equal(second, File.ReadAllBytes(secondPath));
    }

    [Fact]
    public void RestoreFail_NothingToQuarantine_ReturnsFalse()
    {
        Assert.False(_recovery.QuarantineSnapshot());
        Assert.Null(_recovery.QuarantinedSnapshotFile);
    }

    /// <summary>RESTOREFAIL_01 regression sentinel: a failed (non-crashing) restore must not ClearState() a valid snapshot.</summary>
    [Fact]
    public void RestoreFail_RegressionSentinel_RestorePathNeverClearsAfterApplyOrException()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "FreeVideoStudio.sln"))) dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        string src = File.ReadAllText(Path.Combine(dir!, "src", "FreeVideoStudio.App", "MainWindow.Restore.cs"));

        int apply = src.IndexOf("OpenDocument(document", StringComparison.Ordinal);
        Assert.True(apply >= 0);
        string afterApply = src.Substring(apply);
        int finallyAt = afterApply.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyAt > 0);
        string applyToFinally = afterApply.Substring(0, finallyAt);

        Assert.Contains("RESTOREFAIL_01", applyToFinally);
        Assert.DoesNotContain("ClearState()", applyToFinally);
    }

    /// <summary>RESTORECRASH_01 regression sentinel: the stale-owner branch must never delete the snapshot again.</summary>
    [Fact]
    public void RestoreCrash_RegressionSentinel_StaleOwnerBranchNeverDeletesSnapshot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "FreeVideoStudio.sln"))) dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        string src = File.ReadAllText(Path.Combine(dir!, "src", "FreeVideoStudio.Core", "Infrastructure", "RecoveryManager.cs"));

        int start = src.IndexOf("public bool IsSafeModeActive()", StringComparison.Ordinal);
        int end = src.IndexOf("private static bool TryParseOwner", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string body = src.Substring(start, end - start);

        Assert.Contains("RESTORECRASH_01", body);
        Assert.DoesNotContain("RecoveryStateFile", body);
        Assert.DoesNotContain("AppSessionLockFile", body);
    }
}
