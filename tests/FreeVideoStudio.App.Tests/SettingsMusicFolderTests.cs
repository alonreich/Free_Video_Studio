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

        var win1 = new SettingsWindow(_paths, writer);
        win1.DefaultMusicFolder = _testFolder;
        win1.SaveAndClose();

        Assert.False(File.Exists(_paths.SessionStateFile) &&
            File.ReadAllText(_paths.SessionStateFile).Contains(_testFolder.Replace("\\", "\\\\")));

        var win2 = new SettingsWindow(_paths, writer);
        Assert.Equal(_testFolder, win2.DefaultMusicFolder);

        win2.SaveAndClose();
        Assert.Equal(_testFolder, writer.ReadSnapshot()["CustomMusicDirectory"]?.GetValue<string>());

        gated.Gate.SetResult(true);
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));

        JsonObject disk = (await store.LoadAsync()).AsObject();
        Assert.Equal(_testFolder, disk["CustomMusicDirectory"]?.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Restart_Priming_LoadsPersistedFolder()
    {
        var store = new StateTransferStore(_paths);
        await store.UpdatePropertiesAsync(new JsonObject { ["CustomMusicDirectory"] = _testFolder });

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
        win1.Close();

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
        var writer = SessionStateWriter.For(_paths);
        writer.Prime(new JsonObject { ["CustomMusicDirectory"] = removedPath });

        var win = new SettingsWindow(_paths, writer);
        Assert.Equal(removedPath, win.DefaultMusicFolder);
    }

    [AvaloniaFact]
    public void UnprimedWriter_UnrelatedSave_DoesNotOverwriteCustomMusicDirectory()
    {
        var writer = new SessionStateWriter((_, _) => Task.FromResult(true));
        Assert.False(writer.IsPrimed);

        var win = new SettingsWindow(_paths, writer);
        win.SaveAndClose();

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
