using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

[CollectionDefinition("Development environment", DisableParallelization = true)]
public sealed class DevelopmentEnvironmentCollection { }

[Collection("Development environment")]
public sealed class DevelopmentEnvironmentTests
{
    [Fact]
    public void ProductionPreferencesStayPersistentWhileVoiceAndLaneCacheUseOriginalTemp()
    {
        string? previous = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        string? previousTemp = Environment.GetEnvironmentVariable(DevelopmentEnvironment.SystemTempEnvironmentVariable);
        string temp = Path.Combine(Path.GetTempPath(), "FvsStorageTests", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SystemTempEnvironmentVariable, temp);
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeVideoStudio");
            var paths = new ApplicationPaths(root);
            Assert.Equal(Path.Combine(root, "settings.json"), paths.SettingsFile);
            Assert.Equal(Path.Combine(root, "session_state.json"), paths.SessionStateFile);
            Assert.Equal(Path.Combine(root, "logs"), paths.LogsDirectory);
            Assert.Equal(Path.Combine(root, "uistate"), paths.UiStateDirectory);
            Assert.Equal(Path.Combine(root, "recovery_v2.json"), paths.RecoveryStateFile);
            Assert.Equal(Path.Combine(temp, "FreeVideoStudio", "voiceovers"), paths.VoiceOverDirectory);
            Assert.Equal(Path.Combine(temp, "FreeVideoStudio", "cache", "lanes"), paths.LaneCacheDirectory);
            Assert.Equal(Path.Combine(temp, "FreeVideoStudio"), DevelopmentEnvironment.HelperTempDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, previous);
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SystemTempEnvironmentVariable, previousTemp);
        }
    }

    [Fact]
    public void RevisedStorageSeparatesTemporaryPreferencesAndVoiceFromPersistentRecovery()
    {
        string? oldRoot = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        string? oldShared = Environment.GetEnvironmentVariable(DevelopmentEnvironment.SharedMediaEnvironmentVariable);
        string root = Path.Combine(Path.GetTempPath(), "SharedMediaTest", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, root);
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SharedMediaEnvironmentVariable, "1");
            var paths = ApplicationPaths.CreateDefault();
            Assert.Equal(Path.Combine(DevelopmentEnvironment.SystemTempRoot, "FreeVideoStudio_DEV", "voiceovers"), paths.VoiceOverDirectory);
            Assert.Equal(Path.Combine(DevelopmentEnvironment.TemporaryDataRoot, "session_state.json"), paths.SessionStateFile);
            Assert.Equal(Path.Combine(DevelopmentEnvironment.TemporaryDataRoot, "settings.json"), paths.SettingsFile);
            Assert.Equal(Path.Combine(DevelopmentEnvironment.TemporaryDataRoot, "scratch", "work"), paths.TempDirectory);
            Assert.Equal(Path.Combine(DevelopmentEnvironment.TemporaryDataRoot, "cache", "lanes"), paths.LaneCacheDirectory);
            Assert.Equal(Path.Combine(root, "crops_coordinations.conf"), paths.CropCoordinatesFile);
            Assert.StartsWith(root, paths.RecoveryStateFile);
            Assert.Equal(Path.Combine(root + "-other", "voiceovers"), new ApplicationPaths(root + "-other").VoiceOverDirectory);
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SharedMediaEnvironmentVariable, null);
            Assert.Equal(Path.Combine(root, "voiceovers"), paths.VoiceOverDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, oldRoot);
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SharedMediaEnvironmentVariable, oldShared);
        }
    }

    [Fact]
    public void PreferenceMigrationCopiesOnceAndDoesNotResurrectDataAfterTempLoss()
    {
        string root = Path.Combine(Path.GetTempPath(), "FvsStorageTests", Guid.NewGuid().ToString("N"));
        string old = Path.Combine(root, "persistent");
        string current = Path.Combine(root, "temporary");
        Directory.CreateDirectory(Path.Combine(old, "uistate"));
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(old, "settings.json"), "old preferences");
        File.WriteAllText(Path.Combine(old, "session_state.json"), "old session");
        File.WriteAllText(Path.Combine(old, "uistate", "meme_picker.json"), "favourites");
        File.WriteAllText(Path.Combine(old, "uistate", "meme_dimensions.json"), "stale cache");
        File.WriteAllText(Path.Combine(current, "settings.json"), "new preferences");
        DevelopmentEnvironment.MigratePreferencesToTemporaryStorage(old, current);
        Assert.Equal("new preferences", File.ReadAllText(Path.Combine(current, "settings.json")));
        Assert.Equal("old session", File.ReadAllText(Path.Combine(current, "session_state.json")));
        Assert.Equal("favourites", File.ReadAllText(Path.Combine(current, "uistate", "meme_picker.json")));
        Assert.False(File.Exists(Path.Combine(current, "uistate", "meme_dimensions.json")));
        Assert.Equal("old preferences", File.ReadAllText(Path.Combine(old, "settings.json")));
        Directory.Move(current, current + "-previous");
        DevelopmentEnvironment.MigratePreferencesToTemporaryStorage(old, current);
        Assert.False(Directory.Exists(current));
    }

    [Fact]
    public void HelperTempRedirectionDoesNotNestOnRepeatedOrCompanionInitialization()
    {
        string[] names = { ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable,
            DevelopmentEnvironment.SharedMediaEnvironmentVariable, DevelopmentEnvironment.SystemTempEnvironmentVariable,
            DevelopmentEnvironment.HelperTempEnvironmentVariable, "TMP", "TEMP", "FVS_DEV_LOG_DIR" };
        var before = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        string root = Path.Combine(Path.GetTempPath(), "FvsStorageTests", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, Path.Combine(root, "persistent"));
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SharedMediaEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.SystemTempEnvironmentVariable, root);
            Environment.SetEnvironmentVariable(DevelopmentEnvironment.HelperTempEnvironmentVariable, null);
            DevelopmentEnvironment.InitializeFromRuntimeMarker();
            string firstHelper = DevelopmentEnvironment.HelperTempDirectory;
            DevelopmentEnvironment.InitializeFromRuntimeMarker();
            Assert.Equal(firstHelper, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            Assert.Equal(Path.Combine(root, "Local", "FreeVideoStudio_DEV", "data", "scratch", "os"), Path.GetDirectoryName(firstHelper));
            Assert.True(Guid.TryParseExact(Path.GetFileName(firstHelper), "N", out _));
            Assert.Equal(Path.Combine(root, "FreeVideoStudio_DEV", "data", "scratch", "work"), ApplicationPaths.CreateDefault().TempDirectory);
            Assert.Equal(root, DevelopmentEnvironment.SystemTempRoot);
        }
        finally { foreach (var pair in before) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    [Fact]
    public void ProductionNamesStayUnchangedAndSandboxesHaveSeparateStateAndPipes()
    {
        string? previous = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        string root = Path.Combine(Path.GetTempPath(), "DevIsolationTests", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, null);
            Assert.False(DevelopmentEnvironment.IsActive);
            Assert.Equal($"FreeVideoStudio_StateIpc_{IpcProtocol.UserScope}", IpcProtocol.PipeName);
            string productionMutex = NamedSystemMutex.UserScopedName("test");
            string productionLease = IpcProtocol.ServerMutexName;

            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, root);
            Assert.True(DevelopmentEnvironment.IsActive);
            Assert.Equal(root, ApplicationPaths.CreateDefault().ProgramDataRoot);
            Assert.Equal(root, AppDataPaths.LocalCacheDir);
            Assert.StartsWith(root, AppDataPaths.AppDataDir);
            Assert.StartsWith(root, ApplicationPaths.CreateDefault().TempDirectory);
            string firstPipe = IpcProtocol.PipeName;
            Assert.NotEqual(productionLease, IpcProtocol.ServerMutexName);
            Assert.NotEqual(productionMutex, NamedSystemMutex.UserScopedName("test"));
            Assert.Equal(DevelopmentEnvironment.ScopeFor(root), DevelopmentEnvironment.ScopeFor(root + Path.DirectorySeparatorChar));

            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, root + "-second");
            Assert.NotEqual(firstPipe, IpcProtocol.PipeName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, previous);
        }
    }
}
