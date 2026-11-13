using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

[CollectionDefinition("Development launch", DisableParallelization = true)]
public sealed class DevelopmentLaunchCollection { }

[Collection("Development launch")]
public sealed class DevelopmentLaunchTests
{
    [Fact]
    public void NormalDevSharesMusicMemesAndRedirectedDownloadsButNotPreferences()
    {
        string? oldRoot = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        string? oldShared = Environment.GetEnvironmentVariable(DevelopmentEnvironment.SharedMediaEnvironmentVariable);
        string root = Path.Combine(Path.GetTempPath(), "DevMusicTest", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, null);
            string? productionDownloads = KnownFolders.GetDownloads();
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, root);
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SharedMediaEnvironmentVariable, "1");
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Infrastructure.MemeDirectory.GetMusicRoot());
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FreeVideoStudio", "Memes"), Infrastructure.MemeDirectory.GetDefault());
            Assert.Equal(productionDownloads, KnownFolders.GetDownloads());
            Assert.StartsWith(DevelopmentEnvironment.TemporaryDataRoot, ApplicationPaths.CreateDefault().SettingsFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, oldRoot);
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SharedMediaEnvironmentVariable, oldShared);
        }
    }

    [Fact]
    public void RetainedDependenciesMatchCurrentSourcesEvenWhenTimestampsMatch()
    {
        var assembly = System.Reflection.Assembly.Load("FvsBuild");
        var staging = assembly.GetType("FvsBuild.Staging", true)!;
        var stage = staging.GetMethod("StageDependencies")!;
        string root = Path.Combine(FreeVideoStudio.Testing.TestArtifactDirectory.SuiteWork(FreeVideoStudio.Testing.TestOutputSuite.Name), "dev-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            void Put(string path, string value = "original")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, value);
            }
            foreach (var (field, folder) in new[] { ("BackendExact", "binaries"), ("FrontendExact", "binaries"),
                         ("StarterMp3", "mp3"), ("StarterMeme", "meme") })
                foreach (string name in (string[])staging.GetField(field,
                             System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!)
                    Put(Path.Combine(root, folder, name));
            foreach (string name in new[] { "av-test.dll", "sw-test.dll", "postproc-test.dll" })
                Put(Path.Combine(root, "binaries", name));
            using var log = (IDisposable)Activator.CreateInstance(assembly.GetType("FvsBuild.BuildLog", true)!, Path.Combine(root, "test.log"))!;
            string destination = Path.Combine(root, "app");
            bool Run() => (bool)stage.Invoke(null, new object[] { log, destination })!;
            Assert.True(Run());
            string target = Path.Combine(destination, "backend", "av-test.dll");
            var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(target, stamp);
            Assert.True(Run());
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(target));
            string source = Path.Combine(root, "binaries", "av-test.dll");
            Put(source, "modified");
            File.SetLastWriteTimeUtc(source, stamp);
            Assert.True(Run());
            Assert.Equal("modified", File.ReadAllText(target));
            Put(target, "damaged!");
            Assert.True(Run());
            Assert.Equal("modified", File.ReadAllText(target));
            File.Move(source, Path.Combine(root, "binaries", "av-new.dll"));
            Assert.True(Run());
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(Path.Combine(destination, "backend", "av-new.dll")));
            File.Move(Path.Combine(root, "binaries", "ffmpeg.exe"), Path.Combine(root, "missing-ffmpeg.exe"));
            Assert.False(Run());
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Fact]
    public void UpdateDirectoriesFollowTheCurrentBoundaryWithoutCachingAnotherEnvironment()
    {
        string? previous = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, null);
            string production = DeploymentFootprint.UpdateDownloadsRoot;
            string sandbox = Path.Combine(Path.GetTempPath(), "DevPathTests", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, sandbox);
            Assert.StartsWith(sandbox, DeploymentFootprint.UpdateDownloadsRoot);
            Assert.StartsWith(sandbox, DeploymentFootprint.UpdateStageRoot);
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, null);
            Assert.Equal(production, DeploymentFootprint.UpdateDownloadsRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task CleanupPreservesUserWorkAndRejectsUnsafePaths()
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(RepoRoot.Path, "tests", "DevHarness.Tests", "DevSandbox.Tests.ps1") })
            start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await errors);
    }

    [Fact]
    public async Task SandboxSkipsAutomaticAndManualUpdatesBeforeUsingUiOrNetwork()
    {
        string? previous = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable,
                Path.Combine(Path.GetTempPath(), "DevUpdateTests", Guid.NewGuid().ToString("N")));
            await UpdateService.RunStartupCheckAsync(null!);
            string? status = null;
            await UpdateService.CheckManualAsync(null!, value => status = value);
            Assert.Contains("dev.cmd", status);
            Assert.Contains("dev.cmd", UpdateService.DescribeLastCheck());
            Assert.Null(UpdateService.PendingVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void SourceLaunchUsesReleaseAndTheSameStagerAsProduction()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot.Path, "build", "DevRun.ps1"));
        Assert.Contains("'Release'", script);
        Assert.Contains("'--artifacts-path'", script);
        Assert.Contains("'-p:PublishAot=false'", script);
        Assert.Contains("'--dev-runtime'", script);
        Assert.DoesNotContain("dotnet watch", script);
        string stager = File.ReadAllText(Path.Combine(RepoRoot.Path, "build", "FvsBuild", "Program.cs"));
        Assert.Contains("Staging.StageDependencies(runtimeLog, destination)", stager);
        Assert.Contains("Staging.StageDependencies(log)", stager);
        string program = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Program.cs"));
        Assert.Contains("OperatingSystem.IsWindows() && !DevelopmentEnvironment.IsActive", program);
        string sandbox = File.ReadAllText(Path.Combine(RepoRoot.Path, "build", "DevSandbox.ps1"));
        Assert.DoesNotContain("$ProdData", sandbox);
    }
}
