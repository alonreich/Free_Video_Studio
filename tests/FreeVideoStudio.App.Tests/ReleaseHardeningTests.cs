using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// WORKERSTAGE_01, UNSIGNEDDEV_01, BUILDKILL_01, UPDATESPACE_01, ASSETHASH_01, BETA_01, RECHECK_01,
/// RELNOTES_03 — the update and release hardening from the 2026-10-17 release audit
/// (05 SYS-UPGRADE-TRUST, SYS-AUTOUPDATE; 09 DIST-SMART, DIST-RELEASEGATE).
/// </summary>
public sealed class ReleaseHardeningTests
{
    private static string Source(params string[] relative) => File.ReadAllText(RepoRoot.SourcePath(relative));

    [Fact]
    public void WorkerStage01_OnlyTheExactProtectedPathIsAccepted()
    {
        string root = ProtectedWorkerStage.Root;
        string id = Guid.NewGuid().ToString("N");
        string good = Path.Combine(root, ProtectedWorkerStage.FolderPrefix + id, "FreeVideoStudio.exe");
        Assert.True(ProtectedWorkerStage.IsProtectedWorkerPath(good));

        Assert.False(ProtectedWorkerStage.IsProtectedWorkerPath(null));
        Assert.False(ProtectedWorkerStage.IsProtectedWorkerPath(Path.Combine(root, ProtectedWorkerStage.FolderPrefix + id, "Other.exe")));
        Assert.False(ProtectedWorkerStage.IsProtectedWorkerPath(Path.Combine(root, ProtectedWorkerStage.FolderPrefix + "short", "FreeVideoStudio.exe")));
        Assert.False(ProtectedWorkerStage.IsProtectedWorkerPath(Path.Combine(root, "x", ProtectedWorkerStage.FolderPrefix + id, "FreeVideoStudio.exe")));
        Assert.False(ProtectedWorkerStage.IsProtectedWorkerPath(Path.Combine(DeploymentFootprint.UpdateStageRoot, id, "FreeVideoStudio.exe")));
        Assert.False(ProtectedWorkerStage.IsProtectedWorkerPath(Path.Combine(root, ProtectedWorkerStage.FolderPrefix + new string('z', 32), "FreeVideoStudio.exe")));
    }

    [Fact]
    public void WorkerStage01_TheElevatedWorkerStartsOnlyFromTheProtectedCopy_AndRefusesAnyOtherFolder()
    {
        string coordinator = Source("src", "FreeVideoStudio.App", "Services", "UpgradeCoordinator.cs");
        Assert.Contains("ProtectedWorkerStage.Create(", coordinator, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"new ProcessStartInfo\(workerPath\)\s*\{\s*UseShellExecute = true, Verb = ""runas"""), coordinator);
        Assert.Contains("string workerPath = protectedWorker.ExecutablePath;", coordinator, StringComparison.Ordinal);

        string worker = Source("src", "FreeVideoStudio.App", "Services", "UpgradeInstallWorker.cs");
        int run = worker.IndexOf("public static async Task<int> RunAsync", StringComparison.Ordinal);
        int check = worker.IndexOf("ProtectedWorkerStage.IsProtectedWorkerPath(Environment.ProcessPath)", run, StringComparison.Ordinal);
        int firstAdminWork = worker.IndexOf("RecoverInterruptedAsync()", run, StringComparison.Ordinal);
        Assert.True(run >= 0 && check > run && check < firstAdminWork, "The worker must check where it runs from before any administrator work.");

        string program = Source("src", "FreeVideoStudio.App", "Program.cs");
        int dispatch = program.IndexOf("\"--upgrade-worker\"", StringComparison.Ordinal);
        Assert.True(program.IndexOf("RestrictDllSearchToSystem32()", dispatch, StringComparison.Ordinal) <
                    program.IndexOf("UpgradeInstallWorker.RunAsync(args)", dispatch, StringComparison.Ordinal));
        Assert.Contains("/DEPENDENTLOADFLAG:0x800", Source("src", "FreeVideoStudio.App", "FreeVideoStudio.App.csproj"), StringComparison.Ordinal);
    }

    [Fact]
    public void UnsignedDev01_ThePublisherCheckBypassExistsOnlyInDebugBuilds()
    {
        foreach (string file in new[] { "UpdatePreparer.cs", "ProtectedWorkerStage.cs" })
        {
            string code = Source("src", "FreeVideoStudio.App", "Services", file);
            foreach (Match bypass in Regex.Matches(code, @"TrustVerdict\.NoAnchor"))
            {
                string before = code[..bypass.Index];
                Assert.True(before.LastIndexOf("#if DEBUG", StringComparison.Ordinal) > before.LastIndexOf("#endif", StringComparison.Ordinal),
                    $"{file}: a NoAnchor (unsigned) acceptance outside #if DEBUG would let a released build run unsigned code.");
            }
        }
        Assert.Contains("UpdateService.DevReleaseFolder is not null", Source("src", "FreeVideoStudio.App", "Services", "UpdatePreparer.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildKill01_NoBuildScriptKillsTheProductByName()
    {
        string staging = Source("build", "FvsBuild", "Staging.cs");
        Assert.DoesNotContain("\"/IM\"", staging, StringComparison.Ordinal);
        string devCmd = Source("dev.cmd");
        Assert.DoesNotMatch(new Regex(@"(?im)^\s*taskkill\b[^\r\n]*FreeVideoStudio"), devCmd);
        Assert.Contains("StartsWith(root, StringComparison.OrdinalIgnoreCase)", staging, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateSpace01_SpaceIsCheckedPerDrive_ForWhatIsActuallyWritten()
    {
        string drive = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        string a = Path.Combine(drive, "fvs-a"), b = Path.Combine(drive, "fvs-b");
        long free = 100L * 1024 * 1024 + UpdatePreparer.SafetyMarginBytes;

        UpdatePreparer.EnsureDiskSpace([(a, 60L * 1024 * 1024), (b, 40L * 1024 * 1024)], _ => free);
        Assert.Throws<UpdatePreparer.InsufficientDiskSpaceException>(() =>
            UpdatePreparer.EnsureDiskSpace([(a, 60L * 1024 * 1024), (b, 41L * 1024 * 1024)], _ => free));
        UpdatePreparer.EnsureDiskSpace([(a, long.MaxValue / 4)], _ => null);
    }

    [Fact]
    public void AssetHash01_APatchMustPromiseExactlyTheSignedProgram()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fvs-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] oldFile = new byte[4096], newFile = new byte[4096];
            new Random(1).NextBytes(oldFile);
            oldFile.CopyTo(newFile, 0);
            newFile[100] ^= 0xFF;
            string patch = Path.Combine(folder, "p.patch");
            using (var output = File.Create(patch)) BinaryDelta.Create(oldFile, newFile, output);
            string oldSha = SmartUpdateManifest.Sha256Hex(oldFile), newSha = SmartUpdateManifest.Sha256Hex(newFile);

            UpdatePreparer.RequirePatchHeader(patch, oldSha, new SmartFile { Length = newFile.Length, Sha256 = newSha });
            Assert.Throws<InvalidDataException>(() => UpdatePreparer.RequirePatchHeader(patch, oldSha, new SmartFile { Length = newFile.Length + 1, Sha256 = newSha }));
            Assert.Throws<InvalidDataException>(() => UpdatePreparer.RequirePatchHeader(patch, newSha, new SmartFile { Length = newFile.Length, Sha256 = newSha }));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Beta01_TheBetaChannelTakesTheHighestVersion_NotTheFirstListed()
    {
        var releases = JsonNode.Parse("""
            [
              {"tag_name":"v2026.10.15.0900","draft":false,"prerelease":false},
              {"tag_name":"v2026.10.17.0900","draft":true,"prerelease":true},
              {"tag_name":"v2026.10.16.0900","draft":false,"prerelease":true},
              {"tag_name":"latest-docs","draft":false,"prerelease":false}
            ]
            """)!.AsArray();
        Assert.Equal("v2026.10.16.0900", UpdateService.PickBetaRelease(releases)?["tag_name"]?.GetValue<string>());
        Assert.Null(UpdateService.PickBetaRelease(new JsonArray()));
    }

    [Fact]
    public void Recheck01_ALongSessionChecksAgain_AfterTheConsentAndTheGracePeriod()
    {
        Assert.True(UpdateService.RecheckInterval >= TimeSpan.FromHours(1));
        string code = Source("src", "FreeVideoStudio.App", "Services", "UpdateService.cs");
        int start = code.IndexOf("public static async Task RunStartupCheckAsync", StringComparison.Ordinal);
        int grace = code.IndexOf("StartupGracePeriod", start, StringComparison.Ordinal);
        int loop = code.IndexOf("RecheckInterval", grace, StringComparison.Ordinal);
        Assert.True(start >= 0 && grace > start && loop > grace);
    }

    [Fact]
    public void RelNotes03_TheUpdateCardShowsWhatsNew_OnlyWhileItMatters()
    {
        var vm = new UpdateToastViewModel();
        vm.Working("2026.10.17.0900", "Downloading…", 0.5);
        Assert.False(vm.HasWhatsNew);
        vm.SetReleaseNotes(["Faster export", "Fixed a freeze"]);
        Assert.True(vm.HasWhatsNew);
        Assert.Contains("Faster export", vm.WhatsNew, StringComparison.Ordinal);
        vm.Failed("no network");
        Assert.False(vm.HasWhatsNew);
        Assert.Contains("HasWhatsNew", Source("src", "FreeVideoStudio.App", "Controls", "UpdateToastWindow.axaml"), StringComparison.Ordinal);
    }
}
