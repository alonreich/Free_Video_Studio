using System;
using System.IO;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using System.Net;
using System.Net.Sockets;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class VersionAndUpdaterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevOverride;

    public VersionAndUpdaterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FvsUpdaterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _prevOverride = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _prevOverride);
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Theory]
    [InlineData("v2026.09.12.0159", 2026, 9, 12, 159)]
    [InlineData("V2026.09.17.0026", 2026, 9, 17, 26)]
    [InlineData("2026.09.17.0026", 2026, 9, 17, 26)]
    [InlineData("2026.09.17.0026+abc123commit", 2026, 9, 17, 26)]
    [InlineData("1.0.0.0", 1, 0, 0, 0)]
    public void TryParseVersion_ValidVersions_ParsesCorrectly(string input, int major, int minor, int build, int rev)
    {
        bool success = DeploymentLifecycle.TryParseVersion(input, out Version parsed);

        Assert.True(success);
        Assert.NotNull(parsed);
        Assert.Equal(major, parsed.Major);
        Assert.Equal(minor, parsed.Minor);
        Assert.Equal(build, parsed.Build);
        Assert.Equal(rev, parsed.Revision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void TryParseVersion_InvalidVersions_ReturnsFalse(string? input)
    {
        bool success = DeploymentLifecycle.TryParseVersion(input, out Version parsed);

        Assert.False(success);
        Assert.Equal(new Version(0, 0), parsed);
    }

    [Fact]
    public void TryParseVersion_Comparison_StrictlyNewerBehavesAsExpected()
    {
        Assert.True(DeploymentLifecycle.TryParseVersion("v2026.09.17.0026", out Version newer));
        Assert.True(DeploymentLifecycle.TryParseVersion("v2026.09.12.0159", out Version older));
        Assert.True(DeploymentLifecycle.TryParseVersion("2026.09.12.0159", out Version sameAsOlder));

        Assert.True(newer.CompareTo(older) > 0);
        Assert.True(older.CompareTo(newer) < 0);
        Assert.Equal(0, older.CompareTo(sameAsOlder));
    }

    [Theory]
    [InlineData("v2026.10.14", "v2026.10.14-beta.1", 1)]
    [InlineData("v2026.10.14-beta.1", "v2026.10.14", -1)]
    [InlineData("v2026.10.14-beta.2", "v2026.10.14-beta.1", 1)]
    [InlineData("v2026.10.14-rc.1", "v2026.10.14-beta.9", 1)]
    [InlineData("2026.10.14", "v2026.10.14", 0)]
    [InlineData("v2026.10.15", "v2026.10.14", 1)]
    public void CompareVersions_SemVerPrereleasePrecedence_IsRespected(string a, string b, int expectedSign)
    {
        int actual = DeploymentLifecycle.CompareVersions(a, b);
        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Fact]
    public void GetCurrentVersion_ReturnsValidParsableVersion()
    {
        string current = DeploymentLifecycle.GetCurrentVersion();

        Assert.False(string.IsNullOrWhiteSpace(current));
        bool success = DeploymentLifecycle.TryParseVersion(current, out Version parsed);
        Assert.True(success);
        Assert.True(parsed.Major >= 1);
    }

    [Fact]
    public void SkippedVersion_GetAndClear_WorksWithUiStateStore()
    {
        UpdateService.ClearSkippedVersion();
        string initial = UpdateService.GetSkippedVersion();
        Assert.Equal(string.Empty, initial);

        UiStateStore.WriteText("update_skipped_tag.txt", "v2026.09.99.9999");
        string readBack = UpdateService.GetSkippedVersion();
        Assert.Equal("v2026.09.99.9999", readBack);

        UpdateService.ClearSkippedVersion();
        string cleared = UpdateService.GetSkippedVersion();
        Assert.Equal(string.Empty, cleared);
    }


    [Fact]
    public void DownloadProgressShowsPercentSizeSpeedAndTimeLeft()
    {
        const long Mb = 1024 * 1024;
        string text = UpdateService.DescribeDownloadProgress(140 * Mb, 322 * Mb, 1.0 * Mb);
        Assert.Contains("43%", text);
        Assert.Contains("140 of 322 MB", text);
        Assert.Contains("1.0 MB/s", text);
        Assert.Contains("about 4 min left", text);
        Assert.Contains("seconds left", UpdateService.DescribeDownloadProgress(300 * Mb, 322 * Mb, 1.0 * Mb));
    }

    [Fact]
    public void DownloadProgressSaysWhenNoDataArrives()
    {
        Assert.Contains("waiting for data", UpdateService.DescribeDownloadProgress(0, 100 * 1024 * 1024, 0));
    }

    [Theory]
    [InlineData(10L * 1024 * 1024, "less than a minute")]
    [InlineData(322L * 1024 * 1024, "about 3 minutes")]
    public void PromptStatesSizeAndRoughTime(long bytes, string expected)
    {
        string? text = UpdateService.DescribeDownload(bytes);
        Assert.NotNull(text);
        Assert.Contains("Download size:", text);
        Assert.Contains(expected, text);
        Assert.Null(UpdateService.DescribeDownload(0));
    }

    [Fact]
    public void LastCheckAgeReadsLikeEnglish()
    {
        Assert.Equal("just now", UpdateService.DescribeAgo(TimeSpan.FromSeconds(20), DateTime.UtcNow));
        Assert.Equal("1 minute ago", UpdateService.DescribeAgo(TimeSpan.FromMinutes(1.5), DateTime.UtcNow));
        Assert.Equal("5 hours ago", UpdateService.DescribeAgo(TimeSpan.FromHours(5.2), DateTime.UtcNow));
        Assert.StartsWith("on ", UpdateService.DescribeAgo(TimeSpan.FromDays(3), DateTime.UtcNow.AddDays(-3)));
    }

    [Fact]
    public void UpdateCardAsksOneQuestionWithTwoButtonsOnlyWhenReady()
    {
        var vm = new UpdateToastViewModel();
        vm.Working("2.0", "Downloading…", 0.4);
        Assert.True(vm.IsWorking);
        Assert.False(vm.IsReady);
        Assert.Equal(40, vm.Percent, 3);

        vm.Ready("2.0", "Smart update: 2 changed files.");
        Assert.True(vm.IsReady);
        Assert.False(vm.IsWorking);
        Assert.Contains("installs by itself the next time you close", vm.Status);

        bool restart = false, later = false, skip = false;
        vm.RestartRequested += () => restart = true;
        vm.LaterRequested += () => later = true;
        vm.SkipRequested += () => skip = true;
        vm.RestartCommand.Execute(null);
        vm.LaterCommand.Execute(null);
        vm.SkipCommand.Execute(null);
        Assert.True(restart);
        Assert.True(later);
        Assert.True(skip);
    }

    [Fact]
    public void UpdateCardFailureAndPauseSayNothingWasChanged()
    {
        var vm = new UpdateToastViewModel();
        vm.Failed("No internet.");
        Assert.True(vm.IsFailed);
        Assert.Contains("Nothing was changed", vm.Status);
        vm.Paused();
        Assert.True(vm.IsPaused);
        Assert.Contains("continues", vm.Status);
    }

    [Fact]
    public void ProgressWindowTicksEarlierStepsAndMarksTheFailedOne()
    {
        var vm = new UpgradeProgressViewModel();
        vm.SetSteps(UpgradeKind.Update, waitsForApp: true);
        Assert.Equal(UpgradeStep.CloseOldVersion, vm.Steps[0].Step);

        vm.MoveTo(UpgradeStep.Unpack, null, 0.45);
        UpgradeStepItem unpack = vm.Steps.Single(s => s.Step == UpgradeStep.Unpack);
        Assert.True(unpack.IsActive);
        Assert.All(vm.Steps.TakeWhile(s => s != unpack), s => Assert.True(s.IsDone));
        Assert.Equal("45%", vm.PercentText);
        Assert.False(vm.IsIndeterminate);

        vm.Fail("The update could not finish", "Reason");
        Assert.True(unpack.IsFailed);
        Assert.True(vm.IsFailed);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public void FreshInstallListsNoCloseOrSettingsStep()
    {
        var vm = new UpgradeProgressViewModel();
        vm.SetSteps(UpgradeKind.Install, waitsForApp: false);
        Assert.DoesNotContain(vm.Steps, s => s.Step is UpgradeStep.CloseOldVersion or UpgradeStep.Settings);
        Assert.Equal("Installing Free Video Studio", vm.Title);
    }

    [Fact]
    public void PermissionStepExplainsTheWindowsPromptBeforeItAppears()
    {
        Assert.Contains("Click YES", UpgradeText.PermissionDetail(UpgradeKind.Update));
        Assert.Contains("nothing was changed", UpgradeText.UacDeclined(UpgradeKind.Update));
        Assert.Contains("nothing was installed", UpgradeText.UacDeclined(UpgradeKind.Install));
    }

    [Fact]
    public void FirstLaunchNoticeSaysWhatChanged()
    {
        string[] update = ["run-ui", "--upgrade-health", "1", "t", UpgradeFinishedNotice.KindArgument, "update", UpgradeFinishedNotice.FromArgument, "2000.1.1.1"];
        (string headline, string message) = UpgradeFinishedNotice.Describe(update);
        Assert.Contains("Update complete", headline);
        Assert.Contains("from version 2000.1.1.1", message);

        (string installed, _) = UpgradeFinishedNotice.Describe(["run-ui", UpgradeFinishedNotice.KindArgument, "install"]);
        Assert.Contains("installed", installed);
    }

    [Fact]
    public async Task ProgressMessagesNeverSatisfyOrBreakAnExpectedReply()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using TcpClient serverSide = await listener.AcceptTcpClientAsync();
            using var sender = new UpgradeChannel(client);
            using var receiver = new UpgradeChannel(serverSide);
            var progress = new List<string>();
            receiver.Progress = progress.Add;

            await sender.SendAsync(UpgradeChannel.ProgressKind, "unpack|0.250");
            await sender.SendAsync(UpgradeChannel.ProgressKind, "unpack|0.500");
            await sender.SendAsync("installed");

            Assert.Equal(string.Empty, await receiver.ExpectAsync("installed", TimeSpan.FromSeconds(5)));
            Assert.Equal(["unpack|0.250", "unpack|0.500"], progress);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// UPDATECONSENT_01 — the startup check asks first (nothing is sent before the answer) and never
    /// contacts GitHub sooner than five minutes after the window opens.
    /// </summary>
    [Fact]
    public void UpdateConsent01_NoNetworkAtStartupBeforeConsentAndBeforeFiveMinutes()
    {
        Assert.True(UpdateService.StartupGracePeriod >= TimeSpan.FromMinutes(5));
        Assert.False(new FreeVideoStudio.App.Infrastructure.AppSettings().UpdateConsentAsked);

        string code = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Services", "UpdateService.cs"));
        int start = code.IndexOf("public static async Task RunStartupCheckAsync", StringComparison.Ordinal);
        int consent = code.IndexOf("UpdateConsentAsked", start, StringComparison.Ordinal);
        int delay = code.IndexOf("StartupGracePeriod", start, StringComparison.Ordinal);
        int query = code.IndexOf("QueryLatestReleaseAsync()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && consent > start && delay > consent && query > delay,
            "RunStartupCheckAsync must ask for consent, then wait, then query GitHub.");
    }

    [Fact]
    public void StagingFolderArgumentIsAcceptedOnlyInsideTheUpgradeStageRoot()
    {
        string root = UpgradeCoordinator.StageRoot;
        Assert.False(root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "NOTEMPEXEC_01: the worker is never staged in %TEMP%.");
        string good = Path.Combine(root, "t" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(good);
        try
        {
            Assert.Equal(Path.GetFullPath(good), UpgradeCoordinator.StageFromArgs(["--install", UpgradeCoordinator.StageArgument, good]));
            Assert.Null(UpgradeCoordinator.StageFromArgs([UpgradeCoordinator.StageArgument, Path.GetTempPath()]));
            Assert.Null(UpgradeCoordinator.StageFromArgs([UpgradeCoordinator.StageArgument, Path.Combine(good, "..", "..")]));
            Assert.Null(UpgradeCoordinator.StageFromArgs([UpgradeCoordinator.StageArgument, Path.Combine(root, "missing" + Guid.NewGuid().ToString("N"))]));
            Assert.Null(UpgradeCoordinator.StageFromArgs([UpgradeCoordinator.StageArgument]));
            Assert.Null(UpgradeCoordinator.StageFromArgs(["--install"]));
        }
        finally
        {
            Directory.Delete(good, recursive: true);
        }
    }

    [Fact]
    public async Task DevUpdateSourceIsOffOutsideDevCmdAndExplainsAMissingLocalBuild()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR")))
            Assert.Null(UpdateService.DevReleaseFolder);

        string repo = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repo);
        (UpdateService.UpdateRelease? release, string problem) = await UpdateService.QueryLocalDevReleaseAsync(repo);
        Assert.Null(release);
        Assert.Contains("dev_build.cmd", problem);

        Directory.CreateDirectory(Path.Combine(repo, "compiled"));
        File.WriteAllText(Path.Combine(repo, "compiled", UpdateService.SetupAssetName), "not a real exe");
        (release, problem) = await UpdateService.QueryLocalDevReleaseAsync(repo);
        Assert.Null(release);
        Assert.Contains("could not read the version", problem);
    }

    [Fact]
    public async Task ADownloadResumesFromWhatWasAlreadyReceivedAndIsStillHashChecked()
    {
        byte[] data = new byte[3 * 1024 * 1024];
        new Random(5).NextBytes(data);
        string source = Path.Combine(_tempDir, "source.bin");
        File.WriteAllBytes(source, data);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        string target = Path.Combine(_tempDir, "download", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target + ".part", data[..(1024 * 1024)]);

        long firstReported = -1;
        using var http = new System.Net.Http.HttpClient();
        await UpdateDownloader.DownloadAsync(http, new Uri(source).AbsoluteUri, source, target, data.Length, sha,
            p => { if (firstReported < 0) firstReported = p.Done; }, CancellationToken.None);

        Assert.Equal(data, File.ReadAllBytes(target));
        Assert.True(firstReported >= 1024 * 1024, $"the download restarted from {firstReported} instead of resuming");
        Assert.False(File.Exists(target + ".part"));

        string wrong = Path.Combine(_tempDir, "download", "wrong.bin");
        await Assert.ThrowsAsync<IOException>(() => UpdateDownloader.DownloadAsync(http, new Uri(source).AbsoluteUri, source, wrong,
            data.Length, new string('0', 64), null, CancellationToken.None));
        Assert.False(File.Exists(wrong));
        Assert.False(File.Exists(wrong + ".part"));
    }
}
