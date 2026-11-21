using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class TempHousekeeperTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    private static TempHousekeeper.Roots NewRoots(string name)
    {
        string dir = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "TempHousekeeper", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        var roots = new TempHousekeeper.Roots(Path.Combine(dir, "temp"), Path.Combine(dir, "temp", "cache", "freeze"), Path.Combine(dir, "data", "History"), Path.Combine(dir, "temp", "cache"));
        Directory.CreateDirectory(roots.TempRoot);
        Directory.CreateDirectory(roots.FreezeCacheDirectory!);
        Directory.CreateDirectory(roots.HistoryDirectory!);
        return roots;
    }

    private static string Touch(string dir, string name, TimeSpan age, string text = "x")
    {
        string path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    [Fact]
    public void StartupRemovesOldScratchButNeverUserFiles()
    {
        var roots = NewRoots("startup");
        string oldThumb = Touch(roots.TempRoot, "fvs_thumb_" + Guid.NewGuid().ToString("N") + ".png", TimeSpan.FromDays(2));
        string oldCand = Touch(roots.TempRoot, "cand_f1_" + Guid.NewGuid().ToString("N") + ".jpg", TimeSpan.FromDays(2));
        string freshWave = Touch(roots.TempRoot, "fvs_wave_" + Guid.NewGuid().ToString("N") + ".png", TimeSpan.FromMinutes(5));
        string voice = Touch(Path.Combine(roots.TempRoot, "voiceovers"), "take1.wav", TimeSpan.FromDays(400));
        string recovery = Touch(roots.TempRoot, "voiceover_recovery_1.json", TimeSpan.FromDays(400));
        string unknown = Touch(roots.TempRoot, "my_notes.txt", TimeSpan.FromDays(400));
        var report = TempHousekeeper.Run(roots, Now, shutdown: false, _ => false);
        Assert.False(report.Skipped);
        Assert.False(File.Exists(oldThumb));
        Assert.False(File.Exists(oldCand));
        Assert.True(File.Exists(freshWave));
        Assert.True(File.Exists(voice));
        Assert.True(File.Exists(recovery));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void MixFilesGoOnlyWhenTheirProcessIsGone()
    {
        var roots = NewRoots("mix");
        string dead = Touch(roots.TempRoot, "fvs_preview_mix_999991_0.wav", TimeSpan.FromMinutes(1));
        string alive = Touch(roots.TempRoot, "fvs_wizard_mix_999992_1.wav", TimeSpan.FromDays(3));
        string mine = Touch(roots.TempRoot, $"fvs_preview_mix_{Environment.ProcessId}_0.wav", TimeSpan.FromDays(3));
        TempHousekeeper.Run(roots, Now, shutdown: false, pid => pid == 999992);
        Assert.False(File.Exists(dead));
        Assert.True(File.Exists(alive));
        Assert.True(File.Exists(mine));
        TempHousekeeper.Run(roots, Now, shutdown: true, pid => pid == 999992);
        Assert.False(File.Exists(mine));
    }

    [Fact]
    public void JobFolderGoesOnlyWhenEveryFileIsIdle()
    {
        var roots = NewRoots("jobs");
        string idle = Path.Combine(roots.TempRoot, "fvs_job_" + Guid.NewGuid().ToString("N"));
        Touch(idle, "part.mp4", TimeSpan.FromDays(1));
        Directory.SetLastWriteTimeUtc(idle, Now - TimeSpan.FromDays(1));
        string busy = Path.Combine(roots.TempRoot, "fvs_merger_" + Guid.NewGuid().ToString("N"));
        Touch(busy, "old.mp4", TimeSpan.FromDays(1));
        Touch(busy, "writing.mp4", TimeSpan.FromMinutes(2));
        Directory.SetLastWriteTimeUtc(busy, Now - TimeSpan.FromDays(1));
        string other = Path.Combine(roots.TempRoot, "voiceovers");
        Directory.CreateDirectory(other);
        Directory.SetLastWriteTimeUtc(other, Now - TimeSpan.FromDays(100));
        var report = TempHousekeeper.Run(roots, Now, shutdown: false, _ => false);
        Assert.Equal(1, report.Folders);
        Assert.False(Directory.Exists(idle));
        Assert.True(Directory.Exists(busy));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void ShutdownIsQuickAndLeavesFoldersAndHistory()
    {
        var roots = NewRoots("shutdown");
        string job = Path.Combine(roots.TempRoot, "fvs_job_" + Guid.NewGuid().ToString("N"));
        Touch(job, "part.mp4", TimeSpan.FromDays(3));
        Directory.SetLastWriteTimeUtc(job, Now - TimeSpan.FromDays(3));
        string history = Touch(roots.HistoryDirectory!, "ABC.history.json", TimeSpan.FromDays(400), "{}");
        string scratch = Touch(roots.TempRoot, "fvs_pcm_" + Guid.NewGuid().ToString("N") + ".raw", TimeSpan.FromMinutes(30));
        TempHousekeeper.Run(roots, Now, shutdown: true, _ => false);
        Assert.True(Directory.Exists(job));
        Assert.True(File.Exists(history));
        Assert.False(File.Exists(scratch));
    }

    [Fact]
    public void HistoryGoesWhenOldOrItsProjectWasDeleted()
    {
        var roots = NewRoots("history");
        string projectDir = Path.Combine(Path.GetDirectoryName(roots.HistoryDirectory!)!, "projects");
        Directory.CreateDirectory(projectDir);
        string kept = Path.Combine(projectDir, "kept.fvsproj");
        File.WriteAllText(kept, "{}");
        string Json(string p) => $"{{\"project_path\":{System.Text.Json.JsonSerializer.Serialize(p)}}}";
        string live = Touch(roots.HistoryDirectory!, "LIVE.history.json", TimeSpan.FromDays(5), Json(kept));
        string gone = Touch(roots.HistoryDirectory!, "GONE.history.json", TimeSpan.FromDays(5), Json(Path.Combine(projectDir, "deleted.fvsproj")));
        string offline = Touch(roots.HistoryDirectory!, "OFFLINE.history.json", TimeSpan.FromDays(5), Json(@"Q:\unplugged\drive\p.fvsproj"));
        string old = Touch(roots.HistoryDirectory!, "OLD.history.json", TimeSpan.FromDays(120), Json(kept));
        TempHousekeeper.Run(roots, Now, shutdown: false, _ => false);
        Assert.True(File.Exists(live));
        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(offline));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void StartupRunsAtMostOncePerInterval()
    {
        var roots = NewRoots("stamp");
        Assert.False(TempHousekeeper.Run(roots, Now, shutdown: false, _ => false).Skipped);
        string thumb = Touch(roots.TempRoot, "fvs_thumb_" + Guid.NewGuid().ToString("N") + ".png", TimeSpan.FromDays(2));
        Assert.True(TempHousekeeper.Run(roots, Now + TimeSpan.FromHours(1), shutdown: false, _ => false).Skipped);
        Assert.True(File.Exists(thumb));
        Assert.False(TempHousekeeper.Run(roots, Now + TempHousekeeper.StartupInterval + TimeSpan.FromMinutes(1), shutdown: false, _ => false).Skipped);
        Assert.False(File.Exists(thumb));
    }

    [Fact]
    public void OldFreezeRendersAreDropped()
    {
        var roots = NewRoots("freeze");
        string old = Touch(roots.FreezeCacheDirectory!, "boom_1500.wav", TimeSpan.FromDays(45));
        string recent = Touch(roots.FreezeCacheDirectory!, "boom_900.wav", TimeSpan.FromDays(2));
        TempHousekeeper.Run(roots, Now, shutdown: false, _ => false);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
