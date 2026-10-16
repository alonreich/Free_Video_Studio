// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class SettingsMusicFolderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevOverride;
    private readonly ApplicationPaths _paths;
    private readonly string _testFolder;

    public SettingsMusicFolderTests()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();
        _tempDir = Path.Combine(Path.GetTempPath(), "FvsMusicFolderTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _prevOverride = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _tempDir);

        _paths = ApplicationPaths.CreateDefault();
        _paths.EnsureWritableDirectories();

        _testFolder = Path.Combine(_tempDir, "UserMusic");
        Directory.CreateDirectory(_testFolder);

        // Ensure SettingsManager is loaded with a fresh file in _tempDir
        string settingsFile = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsFile, "{ \"SchemaVersion\": " + SettingsManager.CurrentSchemaVersion + ", \"AutoUpdateChecks\": true }");
        SettingsManager.Load();
    }

    public void Dispose()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _prevOverride);
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch (Exception) { /* best-effort cleanup */ }
    }

    private sealed class GatedCommit
    {
        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonObject? LastBatch { get; private set; }
        public int CommitCount => _commitCount;
        private int _commitCount;

        public async Task<bool> CommitAsync(JsonObject batch, CancellationToken ct)
        {
            Interlocked.Increment(ref _commitCount);
            LastBatch = batch.DeepClone().AsObject();
            await Gate.Task;
            return true;
        }
    }

    [AvaloniaFact]
    public async Task Save_ImmediateReopen_DelayedPersistence_RetainsMusicFolder_AndDoesNotOverwriteWithBlank()
    {
        var gated = new GatedCommit();
        var store = new StateTransferStore(_paths);
        var writer = new SessionStateWriter(
            async (batch, ct) =>
            {
                await gated.CommitAsync(batch, ct);
                return await store.UpdatePropertiesAsync(batch, ct);
            },
            ct => store.LoadAsync(ct));

        writer.Prime(new JsonObject { ["CustomMusicDirectory"] = "" });

        // 1. First window: user sets folder and saves
        var win1 = new SettingsWindow(_paths, writer);
        win1.DefaultMusicFolder = _testFolder;
        win1.SaveAndClose();

        // The commit is delayed by gated.Gate; disk has NOT caught up yet!
        Assert.False(File.Exists(_paths.SessionStateFile) &&
            File.ReadAllText(_paths.SessionStateFile).Contains(_testFolder.Replace("\\", "\\\\")));

        // 2. Immediate reopen: win2 must read from SessionStateWriter snapshot, not stale disk
        var win2 = new SettingsWindow(_paths, writer);
        Assert.Equal(_testFolder, win2.DefaultMusicFolder);

        // 3. Unrelated Save in win2 (e.g. user toggles a keybind or just clicks save):
        // Must NOT overwrite the pending music folder with blank!
        win2.SaveAndClose();
        Assert.Equal(_testFolder, writer.ReadSnapshot()["CustomMusicDirectory"]?.GetValue<string>());

        // 4. Release the gate so background commit completes
        gated.Gate.SetResult(true);
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));

        // 5. Verify disk was updated with testFolder
        JsonObject disk = (await store.LoadAsync()).AsObject();
        Assert.Equal(_testFolder, disk["CustomMusicDirectory"]?.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Restart_Priming_LoadsPersistedFolder()
    {
        var store = new StateTransferStore(_paths);
        await store.UpdatePropertiesAsync(new JsonObject { ["CustomMusicDirectory"] = _testFolder });

        // Simulate new process: load state and prime writer (as Program.BootstrapAsync does)
        var writer = SessionStateWriter.For(_paths);
        JsonObject loaded = await store.LoadAsync();
        writer.Prime(loaded);

        var win = new SettingsWindow(_paths, writer);
        Assert.Equal(_testFolder, win.DefaultMusicFolder);
    }

    [AvaloniaFact]
    public void Cancel_Reopen_DoesNotPersistModifiedFolder()
    {
        var writer = SessionStateWriter.For(_paths);
        writer.Prime(new JsonObject { ["CustomMusicDirectory"] = _testFolder });

        var win1 = new SettingsWindow(_paths, writer);
        string otherFolder = Path.Combine(_tempDir, "OtherMusic");
        Directory.CreateDirectory(otherFolder);

        win1.DefaultMusicFolder = otherFolder;
        win1.Close(); // Cancel

        var win2 = new SettingsWindow(_paths, writer);
        Assert.Equal(_testFolder, win2.DefaultMusicFolder);
    }

    [AvaloniaFact]
    public void FolderValidation_NonExistentFolder_ThrowsArgumentException()
    {
        var writer = SessionStateWriter.For(_paths);
        writer.Prime(new JsonObject { ["CustomMusicDirectory"] = _testFolder });

        var win = new SettingsWindow(_paths, writer);
        string nonExistent = Path.Combine(_tempDir, "DoesNotExist_" + Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentException>(() => win.DefaultMusicFolder = nonExistent);
        Assert.Equal(_testFolder, win.DefaultMusicFolder);
    }

    [AvaloniaFact]
    public void RemovedFolder_DisplaysSavedPath_WithoutCrashing()
    {
        string removedPath = Path.Combine(_tempDir, "RemovedFolder");
        // Folder does not exist on disk
        var writer = SessionStateWriter.For(_paths);
        writer.Prime(new JsonObject { ["CustomMusicDirectory"] = removedPath });

        var win = new SettingsWindow(_paths, writer);
        Assert.Equal(removedPath, win.DefaultMusicFolder);
    }

    [AvaloniaFact]
    public void UnprimedWriter_UnrelatedSave_DoesNotOverwriteCustomMusicDirectory()
    {
        // Gated writer that is UNPRIMED
        var writer = new SessionStateWriter((_, _) => Task.FromResult(true));
        Assert.False(writer.IsPrimed);

        var win = new SettingsWindow(_paths, writer);
        // User does not touch music folder; clicks save
        win.SaveAndClose();

        // Writer should not have queued an empty CustomMusicDirectory
        Assert.Null(writer.ReadSnapshot()["CustomMusicDirectory"]);
    }

    [AvaloniaFact]
    public void ScraperWording_SettingsWindow_LabelsAndTooltips()
    {
        var win = new SettingsWindow(_paths);
        var cb = win.FindControl<CheckBox>("MergerThumbnailScraperCheckBox");
        Assert.NotNull(cb);

        Assert.Equal("Skip cover pictures", cb.Content);
        Assert.Equal("Skip cover pictures", AutomationProperties.GetName(cb));

        string? tip = ToolTip.GetTip(cb)?.ToString();
        Assert.NotNull(tip);
        Assert.StartsWith("Removes the tiny cover picture added to the start of some clips, so it does not flash between videos.", tip);
        Assert.Contains("Eligible cover pictures are cut from the 2nd clip onward.", tip);
        Assert.Contains("The first clip keeps its cover picture unless you pick a custom thumbnail in the merger.", tip);
    }

    [AvaloniaFact]
    public void ScraperWording_VideoMergerWindow_LabelsAndTooltips()
    {
        var win = new VideoMergerWindow();
        try
        {
            var cb = win.FindControl<CheckBox>("ThumbnailScraperCheckBox");
            Assert.NotNull(cb);

            Assert.Equal("Skip cover pictures", AutomationProperties.GetName(cb));

            var tb = cb.Content as TextBlock;
            Assert.NotNull(tb);
            Assert.Equal("Skip cover pictures", tb.Text);

            string? tip = ToolTip.GetTip(cb)?.ToString();
            Assert.NotNull(tip);
            Assert.StartsWith("Removes the tiny cover picture added to the start of some clips, so it does not flash between videos.", tip);
            Assert.Contains("Eligible cover pictures are cut from the 2nd clip onward.", tip);
            Assert.Contains("The first clip keeps its cover picture unless you set a custom thumbnail.", tip);
        }
        finally
        {
            win.Close();
        }
    }
}
