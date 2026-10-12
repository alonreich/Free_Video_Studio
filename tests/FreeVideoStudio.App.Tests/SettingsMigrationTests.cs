using System;
using System.IO;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevOverride;
    private readonly string _settingsFile;

    public SettingsMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FvsSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _prevOverride = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _tempDir);
        _settingsFile = Path.Combine(_tempDir, "settings.json");
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

    [Fact]
    public void MigrateFromSchema5_SetsAutoUpdateChecksTrue_AndPersistsSchema7()
    {
        string v5Json = """
        {
            "SchemaVersion": 5,
            "MainOutputDirectory": "C:\\Videos",
            "MergerOutputDirectory": "C:\\Merged",
            "AutoUpdateChecks": false
        }
        """;
        File.WriteAllText(_settingsFile, v5Json);

        SettingsManager.Load();

        Assert.Equal(SettingsManager.CurrentSchemaVersion, SettingsManager.Instance.SchemaVersion);
        Assert.True(SettingsManager.Instance.AutoUpdateChecks);

        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", savedJson);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void UpgradeFromOlderConfigLackingAutoUpdate_SetsAutoUpdateChecksTrue_AndPersists()
    {
        string olderJson = """
        {
            "SchemaVersion": 6,
            "MainOutputDirectory": "C:\\Videos",
            "MergerOutputDirectory": "C:\\Merged"
        }
        """;
        File.WriteAllText(_settingsFile, olderJson);

        SettingsManager.Load();

        Assert.True(SettingsManager.Instance.AutoUpdateChecks);
        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", savedJson);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void FreshInstallWithoutConfig_CreatesInitialConfigFile_WithAutoUpdateChecksTrue()
    {
        if (File.Exists(_settingsFile)) File.Delete(_settingsFile);

        SettingsManager.Load();

        Assert.True(File.Exists(_settingsFile));
        Assert.True(SettingsManager.Instance.AutoUpdateChecks);
        string savedJson = File.ReadAllText(_settingsFile);
        Assert.Contains("\"AutoUpdateChecks\": true", savedJson);
    }

    [Fact]
    public void MigrateFromSchema7_ForcesThumbnailScraperOn()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 7,
            "AutoUpdateChecks": true,
            "MergerThumbnailScraper": false
        }
        """);

        SettingsManager.Load();

        Assert.Equal(13, SettingsManager.CurrentSchemaVersion);
        Assert.True(SettingsManager.Instance.MergerThumbnailScraper);
        Assert.Contains("\"MergerThumbnailScraper\": true", File.ReadAllText(_settingsFile));
    }

    [Fact]
    public void MigrateFromSchema10_SeedsTheFreeVideoStudioOutputNames_AndKeepsFolders()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 10,
            "AutoUpdateChecks": true,
            "MainOutputDirectory": "C:\\Videos",
            "MergerOutputDirectory": "C:\\Merged"
        }
        """);

        SettingsManager.Load();

        Assert.Equal("FreeVideoStudio", SettingsManager.Instance.MainOutputBaseName);
        Assert.Equal("Merged-Videos", SettingsManager.Instance.MergerOutputBaseName);
        Assert.Equal("C:\\Videos", SettingsManager.Instance.MainOutputDirectory);
        Assert.Equal("C:\\Merged", SettingsManager.Instance.MergerOutputDirectory);
        string saved = File.ReadAllText(_settingsFile);
        Assert.Contains("\"MainOutputBaseName\": \"FreeVideoStudio\"", saved);
        Assert.Contains("\"MergerOutputBaseName\": \"Merged-Videos\"", saved);
    }

    [Fact]
    public void Schema11_InvalidOutputNameFallsBackToTheDefault()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 10,
            "AutoUpdateChecks": true,
            "MainOutputBaseName": "..\\..\\CON",
            "MergerOutputBaseName": "   "
        }
        """);

        SettingsManager.Load();

        Assert.DoesNotContain("\\", SettingsManager.Instance.MainOutputBaseName);
        Assert.Equal("Merged-Videos", SettingsManager.Instance.MergerOutputBaseName);
    }

    [Fact]
    public void MigrateFromSchema12_SplitsAudioProtection_AndKeepsUserStrengths()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 12,
            "AutoUpdateChecks": true,
            "Defaults": { "AudioProtection": false }
        }
        """);
        SettingsManager.Load();
        Assert.False(SettingsManager.Instance.Defaults.DuckingEnabled);
        Assert.False(SettingsManager.Instance.Defaults.CarvingEnabled);
        Assert.Equal(50, SettingsManager.Instance.Defaults.DuckingStrength);
        Assert.Equal(50, SettingsManager.Instance.Defaults.CarvingStrength);

        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 13,
            "AutoUpdateChecks": true,
            "Defaults": { "AudioProtection": true, "DuckingEnabled": false, "CarvingEnabled": true, "DuckingStrength": 61, "CarvingStrength": 44 }
        }
        """);
        SettingsManager.Load();
        Assert.False(SettingsManager.Instance.Defaults.DuckingEnabled);
        Assert.True(SettingsManager.Instance.Defaults.CarvingEnabled);
        Assert.Equal(61, SettingsManager.Instance.Defaults.DuckingStrength);
        Assert.Equal(44, SettingsManager.Instance.Defaults.CarvingStrength);
    }

    [Fact]
    public void MigrateFromSchema9_InitializesAiTrackingDefaults()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 9,
            "AutoUpdateChecks": true
        }
        """);

        SettingsManager.Load();

        Assert.Equal(13, SettingsManager.CurrentSchemaVersion);
        Assert.Equal("gemini-2.5-flash", SettingsManager.Instance.GeminiModelName);
        Assert.Equal(2.2, SettingsManager.Instance.AiZoomBaseScale);
        Assert.Equal(1.3, SettingsManager.Instance.AiZoomMinScale);
        Assert.True(SettingsManager.Instance.AiZoomAvoidHud);
        Assert.Equal(2.0, SettingsManager.Instance.AiZoomDeadbandPercent);
    }

    [Fact]
    public void MigrateFromSchema8_TurnsTheMergerRemoveConfirmOff_Once()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 8,
            "AutoUpdateChecks": true,
            "ConfirmVideoMergerRemove": true
        }
        """);
        SettingsManager.Load();
        Assert.False(SettingsManager.Instance.ConfirmVideoMergerRemove);

        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 9,
            "AutoUpdateChecks": true,
            "ConfirmVideoMergerRemove": true
        }
        """);
        SettingsManager.Load();
        Assert.True(SettingsManager.Instance.ConfirmVideoMergerRemove);
    }

    [Fact]
    public void Schema8_UserChoiceOff_IsRespected()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 8,
            "AutoUpdateChecks": true,
            "MergerThumbnailScraper": false
        }
        """);

        SettingsManager.Load();

        Assert.False(SettingsManager.Instance.MergerThumbnailScraper);
    }

    [Fact]
    public void FileWithoutSchemaVersion_IsTreatedAsV1_AndWalksEveryMigration()
    {
        const string legacy = """
        {
            "AutoUpdateChecks": false,
            "MergerThumbnailScraper": false,
            "ConfirmVideoMergerRemove": true,
            "VideoEncoderOverride": "",
            "MainOutputBaseName": "..\\bad\\name",
            "Defaults": { "AudioProtection": false, "DuckingEnabled": true, "CarvingEnabled": true }
        }
        """;
        File.WriteAllText(_settingsFile, legacy);

        SettingsManager.Load();

        AppSettings s = SettingsManager.Instance;
        Assert.Equal(SettingsManager.CurrentSchemaVersion, s.SchemaVersion);
        Assert.True(s.AutoUpdateChecks);
        Assert.Equal("Auto", s.VideoEncoderOverride);
        Assert.True(s.MergerThumbnailScraper);
        Assert.False(s.ConfirmVideoMergerRemove);
        Assert.DoesNotContain("\\", s.MainOutputBaseName);
        Assert.False(s.Defaults.DuckingEnabled);
        Assert.False(s.Defaults.CarvingEnabled);

        string saved = File.ReadAllText(_settingsFile);
        Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", saved);
        Assert.Contains("\"MergerThumbnailScraper\": true", saved);
        Assert.Contains("\"ConfirmVideoMergerRemove\": false", saved);

        File.WriteAllText(_settingsFile, legacy);
        Assert.True(SettingsManager.Update(x => x.Volume = 33));
        Assert.Equal(SettingsManager.CurrentSchemaVersion, SettingsManager.Instance.SchemaVersion);
        Assert.True(SettingsManager.Instance.MergerThumbnailScraper);
        Assert.False(SettingsManager.Instance.ConfirmVideoMergerRemove);
        Assert.False(SettingsManager.Instance.Defaults.DuckingEnabled);
        Assert.Equal(33, SettingsManager.Instance.Volume);
    }

    [Fact]
    public void ExplicitCurrentSchema_IsNotReMigrated()
    {
        File.WriteAllText(_settingsFile, $$"""
        {
            "SchemaVersion": {{SettingsManager.CurrentSchemaVersion}},
            "AutoUpdateChecks": false,
            "MergerThumbnailScraper": false,
            "ConfirmVideoMergerRemove": true
        }
        """);

        SettingsManager.Load();

        Assert.False(SettingsManager.Instance.AutoUpdateChecks);
        Assert.False(SettingsManager.Instance.MergerThumbnailScraper);
        Assert.True(SettingsManager.Instance.ConfirmVideoMergerRemove);
    }

    [Fact]
    public void FreshInstall_ThumbnailScraperOn()
    {
        SettingsManager.Load();
        Assert.True(SettingsManager.Instance.MergerThumbnailScraper);
    }
}
