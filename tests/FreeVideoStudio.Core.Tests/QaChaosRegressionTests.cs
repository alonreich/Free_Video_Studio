using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class QaChaosRegressionTests
{
    private static string Work(string name)
    {
        string dir = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "QaChaos", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ProjectDocument Doc(string title, double baseSpeed = 1.0) => new()
    {
        Source = new SourceClip { FilePath = @"C:\clips\game.mp4", DurationMs = 10_000, Width = 1920, Height = 1080, Fps = 60 },
        Title = title,
        BaseSpeed = baseSpeed,
    };

    [Fact]
    public void Qa02_LockedLiveProjectFile_IsNotOpenedFromTheOlderBackup()
    {
        string path = Path.Combine(Work("locked"), "montage.fvsproj");
        Assert.True(ProjectStore.Save(Doc("v1", 1.0), path).Success);
        Assert.True(ProjectStore.Save(Doc("v2", 1.5), path).Success);
        Assert.True(File.Exists(path + ProjectStore.BackupSuffix));

        ProjectDocument? loaded;
        string? error;
        bool fromBackup;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            loaded = ProjectStore.Load(path, out error, out fromBackup);
        }

        Assert.False(fromBackup);
        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.DoesNotContain("damaged", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Qa02_DamagedLiveProjectFile_StillOpensFromBackupAndSaysSo()
    {
        string path = Path.Combine(Work("damaged"), "montage.fvsproj");
        Assert.True(ProjectStore.Save(Doc("v1", 1.0), path).Success);
        Assert.True(ProjectStore.Save(Doc("v2", 1.5), path).Success);
        File.WriteAllText(path, "{ this is not json");

        ProjectDocument? loaded = ProjectStore.Load(path, out string? error, out bool fromBackup);

        Assert.NotNull(loaded);
        Assert.True(fromBackup);
        Assert.Null(error);
        Assert.Equal(1.0, loaded!.BaseSpeed);
    }

    [Fact]
    public void ProjectSaveLoad_RoundTripsAndKeepsOneBackupGeneration()
    {
        string path = Path.Combine(Work("roundtrip"), "montage");
        ProjectIoResult first = ProjectStore.Save(Doc("v1", 1.25), path);
        Assert.True(first.Success);
        Assert.EndsWith(ProjectDocument.FileExtension, first.Path);
        Assert.True(ProjectStore.Save(Doc("v2", 2.0), first.Path!).Success);

        ProjectDocument? live = ProjectStore.Load(first.Path!, out string? error, out bool fromBackup);
        Assert.Null(error);
        Assert.False(fromBackup);
        Assert.Equal(2.0, live!.BaseSpeed);

        ProjectDocument? backup = ProjectStore.Load(first.Path! + ProjectStore.BackupSuffix, out _, out _);
        Assert.Equal(1.25, backup!.BaseSpeed);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(first.Path!)!, "*.tmp"));
    }

    [Fact]
    public void ProjectSave_IntoMissingParentOfAFile_FailsWithMessageAndLeavesNoTemp()
    {
        string dir = Work("blocked");
        string blocker = Path.Combine(dir, "not-a-folder");
        File.WriteAllText(blocker, "x");

        ProjectIoResult result = ProjectStore.Save(Doc("v1"), Path.Combine(blocker, "montage.fvsproj"));

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Single(Directory.GetFiles(dir));
    }

    [Fact]
    public void Qa03_OutputNameTruncation_NeverSplitsASurrogatePair()
    {
        string typed = new string('a', OutputFileNaming.MaxBaseNameLength - 1) + "\U0001F600";

        string safe = OutputFileNaming.Sanitize(typed, OutputFileNaming.MainDefaultBaseName);

        Assert.False(char.IsHighSurrogate(safe[^1]), "the name ends in half an emoji");
        Assert.Equal(new string('a', OutputFileNaming.MaxBaseNameLength - 1), safe);
        Assert.Equal("clip😀", OutputFileNaming.Sanitize("clip😀", OutputFileNaming.MainDefaultBaseName));
        for (int i = 0; i < safe.Length; i++)
        {
            if (char.IsHighSurrogate(safe[i])) Assert.True(i + 1 < safe.Length && char.IsLowSurrogate(safe[i + 1]));
            if (char.IsLowSurrogate(safe[i])) Assert.True(i > 0 && char.IsHighSurrogate(safe[i - 1]));
        }
        string merged = OutputFileNaming.MergerBaseFor(new string('b', 71) + "\U0001F600");
        Assert.False(merged.Any(char.IsSurrogate) && !merged.Where((c, i) => char.IsHighSurrogate(c) && i + 1 < merged.Length && char.IsLowSurrogate(merged[i + 1])).Any());
    }

    private static async Task MakeClipAsync(string ffmpeg, string output)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-y", "-hide_banner", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                     "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", output })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
    }

    [MediaFact]
    public async Task Qa01_UnusableOutputFolder_KeepsTheFinishedRenderSafe()
    {
        string work = Work("outdir");
        string source = Path.Combine(work, "source.mp4");
        await MakeClipAsync(MediaFactAttribute.Ffmpeg!, source);
        string outDir = Directory.CreateDirectory(Path.Combine(work, "export")).FullName;

        var paths = new ApplicationPaths(Path.Combine(work, "worker"));
        string[] before = Directory.Exists(paths.TempDirectory)
            ? Directory.GetFiles(paths.TempDirectory, OutputFileNaming.MainRecoveredPrefix + "*.mp4") : Array.Empty<string>();

        using var worker = new ProcessWorker(paths)
        {
            InputPath = source, OutputDirectory = outDir,
            StartTimeMs = 0, EndTimeMs = 1800, OriginalResolution = "160x90",
            IsMobileFormat = false, HardwareStrategy = "CPU", QualityLevel = 20,
            EnableFades = false, AutoSpikeFlattening = false
        };
        bool broke = false;
        worker.PhaseUpdate += (_, phase, _) =>
        {
            if (broke || phase != "Finalizing") return;
            broke = true;
            Directory.Delete(outDir, recursive: true);
            File.WriteAllText(outDir, "the output drive went away");
        };
        bool success = false; string message = "";
        worker.Finished += (ok, text) => { success = ok; message = text; };
        await worker.RunAsync();

        string[] rescued = (Directory.Exists(paths.TempDirectory)
            ? Directory.GetFiles(paths.TempDirectory, OutputFileNaming.MainRecoveredPrefix + "*.mp4") : Array.Empty<string>())
            .Except(before).ToArray();
        try
        {
            Assert.True(broke, "the export never reached Finalizing");
            bool delivered = success && File.Exists(message);
            Assert.True(delivered || rescued.Any(f => new FileInfo(f).Length > 0),
                $"the finished render was lost: success={success}, message={message}, failure={worker.LastFailure?.Category}");
            if (!delivered) Assert.Equal(ExportFailureCategory.DestinationError, worker.LastFailure?.Category);
        }
        finally
        {
            foreach (string f in rescued) AtomicJsonFile.TryDelete(f);
        }
    }

    [MediaFact]
    public async Task MainExport_IntoAWritableFolder_DeliversANumberedFile()
    {
        string work = Work("export-ok");
        string source = Path.Combine(work, "source.mp4");
        await MakeClipAsync(MediaFactAttribute.Ffmpeg!, source);
        string outDir = Directory.CreateDirectory(Path.Combine(work, "export")).FullName;

        using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "worker")))
        {
            InputPath = source, OutputDirectory = outDir,
            StartTimeMs = 0, EndTimeMs = 1800, OriginalResolution = "160x90",
            IsMobileFormat = false, HardwareStrategy = "CPU", QualityLevel = 20,
            EnableFades = false, AutoSpikeFlattening = false, OutputBaseName = "qa"
        };
        bool success = false; string message = "";
        worker.Finished += (ok, text) => { success = ok; message = text; };
        await worker.RunAsync();

        Assert.True(success, worker.FailureDetail ?? message);
        Assert.Equal(Path.Combine(outDir, "qa-1.mp4"), message);
        Assert.True(new FileInfo(message).Length > 0);
    }
}
