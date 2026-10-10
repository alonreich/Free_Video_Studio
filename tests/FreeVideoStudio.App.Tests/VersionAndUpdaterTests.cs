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
        // Act: Initially empty
        UpdateService.ClearSkippedVersion();
        string initial = UpdateService.GetSkippedVersion();
        Assert.Equal(string.Empty, initial);

        // Act: Store skipped tag into state
        UiStateStore.WriteText("update_skipped_tag.txt", "v2026.09.99.9999");
        string readBack = UpdateService.GetSkippedVersion();
        Assert.Equal("v2026.09.99.9999", readBack);

        // Act: Clear skipped tag
        UpdateService.ClearSkippedVersion();
        string cleared = UpdateService.GetSkippedVersion();
        Assert.Equal(string.Empty, cleared);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // UPDATEUX_01..06 and UPGRADEUX_01..05 — the update and install flows always say what is
    // happening. These pin the wording rules and the state machines behind the windows.
    // ════════════════════════════════════════════════════════════════════════════════════════

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
    public void RemindMeLaterIsItsOwnChoiceAndStoresNothing()
    {
        var vm = new UpdateAvailableViewModel(new Version(1, 0), "v2.0", null, "Download size: 1 MB", canInstallItself: true);
        bool closed = false;
        vm.CloseRequested += () => closed = true;
        vm.LaterCommand.Execute(null);
        Assert.True(closed);
        Assert.Equal(UpdateChoice.NotNow, vm.Choice);
    }

    [Fact]
    public void UnsignedCopyIsToldUpFrontAndOffersTheDownloadPage()
    {
        var vm = new UpdateAvailableViewModel(new Version(1, 0), "v2.0", "notes", null, canInstallItself: false);
        Assert.True(vm.CannotInstallItself);
        Assert.Contains("not digitally signed", vm.DownloadSizeText);
        Assert.Contains("download page", vm.UpdateButtonText);
    }

    [Fact]
    public void DownloadWindowStagesDisableCancelDuringTheSafetyCheckAndEndInAChoice()
    {
        var vm = new UpdateDownloadViewModel();
        Assert.True(vm.IsIndeterminate);                 // opens immediately, before hashing starts
        vm.Stage("Downloading", "…", 0.5, canCancel: true);
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.Stage("Checking the download is safe…", "…", null, canCancel: false);
        Assert.False(vm.CancelCommand.CanExecute(null));
        vm.Ready();
        Assert.True(vm.IsReady);
        Assert.False(vm.IsWorking);

        bool? restart = null;
        vm.ReadyChoice += r => restart = r;
        vm.RestartNowCommand.Execute(null);
        Assert.True(restart);
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

    [Fact]
    public void StagingFolderArgumentIsAcceptedOnlyInsideTheUpgradeTempRoot()
    {
        // UPGRADEUX_06 — the elevated worker is started from this folder, so an argument must never
        // be able to point it anywhere else.
        string root = Path.Combine(Path.GetTempPath(), "FVS_Upgrade");
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
        // DEVUPDATE_01 — production (no FVS_DEV_LOG_DIR / FVS_DEV_UPDATE_SOURCE) never reads a local folder.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR")))
            Assert.Null(UpdateService.DevReleaseFolder);

        string repo = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repo);
        (UpdateService.UpdateRelease? release, string problem) = await UpdateService.QueryLocalDevReleaseAsync(repo);
        Assert.Null(release);
        Assert.Contains("dev_build.cmd", problem);

        Directory.CreateDirectory(Path.Combine(repo, "compiled"));
        File.WriteAllText(Path.Combine(repo, "compiled", "FreeVideoStudio.exe"), "not a real exe");
        (release, problem) = await UpdateService.QueryLocalDevReleaseAsync(repo);
        Assert.Null(release);
        Assert.Contains("could not read the version", problem);
    }
}
