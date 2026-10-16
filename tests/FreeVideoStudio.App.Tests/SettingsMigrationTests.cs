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

        Assert.Equal(14, SettingsManager.CurrentSchemaVersion);
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

        Assert.Equal(14, SettingsManager.CurrentSchemaVersion);
        Assert.Equal("gemini-2.5-flash", SettingsManager.Instance.GeminiModelName);
        Assert.Equal(2.2, SettingsManager.Instance.AiZoomBaseScale);
        Assert.Equal(1.3, SettingsManager.Instance.AiZoomMinScale);
        Assert.True(SettingsManager.Instance.AiZoomAvoidHud);
        Assert.Equal(2.0, SettingsManager.Instance.AiZoomDeadbandPercent);
    }

    /// <summary>KEYPROTECT_01 — a plain-text key on disk is sealed on load and the clear key leaves the file.</summary>
    [Fact]
    public void MigrateFromSchema13_SealsAPlainTextApiKeyOnDisk()
    {
        const string key = "AIzaSyFAKE-MIGRATION-ONLY-00000000000"; // SECRETSCAN_ALLOW (fake)
        File.WriteAllText(_settingsFile, "{ \"SchemaVersion\": 13, \"AutoUpdateChecks\": true, \"GeminiApiKey\": \"" + key + "\" }");

        SettingsManager.Load();

        Assert.Equal(key, SettingsManager.Instance.GeminiApiKey);
        string saved = File.ReadAllText(_settingsFile);
        Assert.DoesNotContain(key, saved);
        Assert.Contains("\"SchemaVersion\": 14", saved);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("dpapi1:", saved);
        }
    }

    [Fact]
    public void SecretProtector_RoundTripsOnWindows_AndProducesDpapiPrefix()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string key = "AIzaSyFAKE-TEST-KEY-12345678901234567"; // SECRETSCAN_ALLOW
        string sealedKey = SecretProtector.Protect(key);
        Assert.StartsWith("dpapi1:", sealedKey);
        Assert.True(SecretProtector.IsProtectedForm(sealedKey));
        string unsealed = SecretProtector.Unprotect(sealedKey);
        Assert.Equal(key, unsealed);
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

    [Fact]
    public void MigrateFromSchema14_SealsPlainTextClaudeAndOpenAiKeysOnDisk()
    {
        const string claudeKey = "sk-ant-api03-FAKE-TEST-CLAUDE-KEY-0000000000"; // SECRETSCAN_ALLOW
        const string openAiKey = "sk-proj-FAKE-TEST-OPENAI-KEY-0000000000000000"; // SECRETSCAN_ALLOW

        File.WriteAllText(_settingsFile, $$"""
        {
            "SchemaVersion": 14,
            "AutoUpdateChecks": true,
            "ClaudeApiKey": "{{claudeKey}}",
            "OpenAiApiKey": "{{openAiKey}}"
        }
        """);

        SettingsManager.Load();

        Assert.Equal(claudeKey, SettingsManager.Instance.ClaudeApiKey);
        Assert.Equal(openAiKey, SettingsManager.Instance.OpenAiApiKey);

        string saved = File.ReadAllText(_settingsFile);
        Assert.DoesNotContain(claudeKey, saved);
        Assert.DoesNotContain(openAiKey, saved);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("dpapi1:", saved);
        }
    }

    [Fact]
    public void Migrate_PreservesCustomGeminiModelName_AcrossUsageProfiles()
    {
        File.WriteAllText(_settingsFile, """
        {
            "SchemaVersion": 14,
            "AutoUpdateChecks": true,
            "GeminiModelName": "gemini-3.8-flash"
        }
        """);

        SettingsManager.Load();

        Assert.Equal("gemini-3.8-flash", SettingsManager.Instance.GeminiModelName);
        Assert.Equal("gemini-3.8-flash", SettingsManager.Instance.AiUsageSubjectDiscovery.Model);
        Assert.Equal("gemini-3.8-flash", SettingsManager.Instance.AiUsageTracking.Model);
        Assert.Equal("gemini-3.8-flash", SettingsManager.Instance.AiUsageMagicWand.Model);
    }

    [Fact]
    public void MultiVendorSettings_RoundTripAndPersistCorrectly()
    {
        const string claudeKey = "sk-ant-FAKE-ROUNDTRIP-CLAUDE-KEY-1111111"; // SECRETSCAN_ALLOW
        const string openAiKey = "sk-FAKE-ROUNDTRIP-OPENAI-KEY-222222222222"; // SECRETSCAN_ALLOW

        SettingsManager.Load();

        Assert.True(SettingsManager.Update(s =>
        {
            s.ClaudeApiKey = claudeKey;
            s.OpenAiApiKey = openAiKey;
            s.SetUsageConfig(FreeVideoStudio.App.Services.AiUsage.SubjectDiscovery, new FreeVideoStudio.App.Services.AiUsageConfig(FreeVideoStudio.App.Services.AiVendor.Anthropic, "claude-sonnet-custom", FreeVideoStudio.App.Services.AiReasoningLevel.Medium));
            s.SetUsageConfig(FreeVideoStudio.App.Services.AiUsage.DenseTracking, new FreeVideoStudio.App.Services.AiUsageConfig(FreeVideoStudio.App.Services.AiVendor.OpenAI, "o3-mini-custom", FreeVideoStudio.App.Services.AiReasoningLevel.High));
        }));

        string onDisk = File.ReadAllText(_settingsFile);
        Assert.DoesNotContain(claudeKey, onDisk);
        Assert.DoesNotContain(openAiKey, onDisk);

        // Load again from disk
        SettingsManager.Load();

        Assert.Equal(claudeKey, SettingsManager.Instance.ClaudeApiKey);
        Assert.Equal(openAiKey, SettingsManager.Instance.OpenAiApiKey);
        Assert.Equal(claudeKey, SettingsManager.Instance.GetApiKey(FreeVideoStudio.App.Services.AiVendor.Anthropic));
        Assert.Equal(openAiKey, SettingsManager.Instance.GetApiKey(FreeVideoStudio.App.Services.AiVendor.OpenAI));

        var u1 = SettingsManager.Instance.GetUsageConfig(FreeVideoStudio.App.Services.AiUsage.SubjectDiscovery);
        Assert.Equal(FreeVideoStudio.App.Services.AiVendor.Anthropic, u1.Vendor);
        Assert.Equal("claude-sonnet-custom", u1.Model);
        Assert.Equal(FreeVideoStudio.App.Services.AiReasoningLevel.Medium, u1.ReasoningLevel);

        var u2 = SettingsManager.Instance.GetUsageConfig(FreeVideoStudio.App.Services.AiUsage.DenseTracking);
        Assert.Equal(FreeVideoStudio.App.Services.AiVendor.OpenAI, u2.Vendor);
        Assert.Equal("o3-mini-custom", u2.Model);
        Assert.Equal(FreeVideoStudio.App.Services.AiReasoningLevel.High, u2.ReasoningLevel);
    }

    [Fact]
    public void GetEffectiveModel_ResolvesVendorDefaults_WhenEmpty()
    {
        var settings = new AppSettings();
        settings.AiUsageSubjectDiscovery = new(FreeVideoStudio.App.Services.AiVendor.Google, "");
        settings.AiUsageTracking = new(FreeVideoStudio.App.Services.AiVendor.Anthropic, "");
        settings.AiUsageMagicWand = new(FreeVideoStudio.App.Services.AiVendor.OpenAI, "");

        Assert.Equal(FreeVideoStudio.App.Services.GeminiClient.DefaultModel, settings.GetEffectiveModel(FreeVideoStudio.App.Services.AiUsage.SubjectDiscovery));
        Assert.Equal(FreeVideoStudio.App.Services.ClaudeProvider.DefaultModelName, settings.GetEffectiveModel(FreeVideoStudio.App.Services.AiUsage.DenseTracking));
        Assert.Equal(FreeVideoStudio.App.Services.OpenAiProvider.DefaultModelName, settings.GetEffectiveModel(FreeVideoStudio.App.Services.AiUsage.MagicWand));
    }
}
