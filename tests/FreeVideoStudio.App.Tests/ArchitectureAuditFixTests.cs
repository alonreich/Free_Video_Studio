using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// Architecture audit fixes: ASYNCSAFE_01, SETTINGSFREEZE_01, MEMEPURE_01, SETTINGSDI_01,
/// AIKEYCANCEL_01, SETBACKUPIO_01, CTSSLOT_01, SHELLLAUNCH_01, SETTINGSTYPES_01.
/// </summary>
public sealed class ArchitectureAuditFixTests
{

    [Fact]
    public void FrozenSettings_RejectEveryWrite_IncludingNestedObjects()
    {
        var s = new AppSettings();
        s.Freeze();
        Assert.Throws<InvalidOperationException>(() => s.Volume = 5);
        Assert.Throws<InvalidOperationException>(() => s.Defaults.DefaultSpeed = 2.0);
        Assert.Throws<InvalidOperationException>(() => s.KeyBinds.PlayPause = Avalonia.Input.Key.A);
        Assert.Throws<InvalidOperationException>(() => s.AiUsageTracking.Model = "x");
        Assert.Throws<InvalidOperationException>(() => s.SetApiKey(AiVendor.Google, "k"));
    }

    [Fact]
    public void PublishedSnapshot_IsFrozen_AndSnapshotIsAWritableCopy()
    {
        Assert.True(SettingsManager.Instance.IsFrozen);
        var copy = SettingsManager.Snapshot();
        Assert.False(copy.IsFrozen);
        copy.Volume = 7;
        Assert.Equal(7, copy.Volume);
        Assert.NotSame(copy, SettingsManager.Instance);
    }

    [Fact]
    public void JsonClone_OfAFrozenSnapshot_IsWritable()
    {
        var s = new AppSettings();
        s.Freeze();
        AppSettings clone = SettingsViewModel.Clone(s);
        clone.Defaults.DefaultSpeed = 2.5;
        Assert.Equal(2.5, clone.Defaults.DefaultSpeed);
    }


    private static AppSettings Parse(string json) =>
        JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;

    [Fact]
    public void TypedSettings_ReadLegacyStrings_AnyCase()
    {
        var s = Parse("{\"UpdateChannel\":\"beta\",\"VideoEncoderOverride\":\"nvidia\",\"AiZoomCloudConsentVendor\":\"openai\"," +
                      "\"AiTrackingGameProfile\":\"FirstPerson\",\"AiUsageOverrides\":[\"Commentary\",\"commentary\",\"NoSuchFeature\"]}");
        Assert.Equal(EncoderPreference.NVIDIA, s.VideoEncoderOverride);
        Assert.Equal(AiVendor.OpenAI, s.AiZoomCloudConsentVendor);
        Assert.Equal(AiGameProfile.FirstPerson, s.AiTrackingGameProfile);
        Assert.Equal(new[] { AiUsage.Commentary }, s.AiUsageOverrides);
    }

    [Fact]
    public void TypedSettings_UnknownValues_FallBack_InsteadOfBreakingTheFile()
    {
        var s = Parse("{\"UpdateChannel\":\"nightly\",\"VideoEncoderOverride\":\"\",\"AiMagicWandCloudConsentVendor\":\"\",\"AiTrackingGameProfile\":\"Martian\"}");
        Assert.Equal(EncoderPreference.Auto, s.VideoEncoderOverride);
        Assert.Null(s.AiMagicWandCloudConsentVendor);
        Assert.Equal(AiGameProfiles.Default, s.AiTrackingGameProfile);
    }

    [Fact]
    public void TypedSettings_WriteTheSameStringsOldBuildsRead()
    {
        var s = new AppSettings
        {
            VideoEncoderOverride = EncoderPreference.INTEL,
            AiCommentaryCloudConsentVendor = AiVendor.Anthropic,
            AiUsageOverrides = new[] { AiUsage.ViralPost },
        };
        JsonObject node = JsonSerializer.SerializeToNode(s, SettingsJsonContext.Default.AppSettings)!.AsObject();
        Assert.False(node.ContainsKey("UpdateChannel"));
        Assert.Equal("INTEL", (string?)node["VideoEncoderOverride"]);
        Assert.Equal("Anthropic", (string?)node["AiCommentaryCloudConsentVendor"]);
        Assert.Equal("", (string?)node["AiZoomCloudConsentVendor"]);
        Assert.Equal("ViralPost", (string?)node["AiUsageOverrides"]![0]);
    }

    [Theory]
    [InlineData(null, AiVendor.Google, true)]
    [InlineData(null, AiVendor.OpenAI, false)]
    [InlineData(AiVendor.OpenAI, AiVendor.OpenAI, true)]
    [InlineData(AiVendor.OpenAI, AiVendor.Anthropic, false)]
    public void ConsentCovers_IsTyped_AndNoStampMeansGoogle(AiVendor? stamp, AiVendor current, bool expected)
        => Assert.Equal(expected, AppSettings.ConsentCovers(true, stamp, current));


    [Fact]
    public void MemeFolderLookup_IsPure_AndCreatesNothing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "FvsMemePure_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(missing, MemeDirectory.ResolveActive(missing));
        Assert.Equal(MemeDirectory.GetDefault(), MemeDirectory.ResolveActive("  "));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void MemeFolderGetActive_HasNoHiddenMigrationOrDiskWork()
    {
        string src = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Infrastructure", "SettingsManager.cs"));
        string getActive = src[src.IndexOf("public static string GetActive()", StringComparison.Ordinal)..];
        getActive = getActive[..getActive.IndexOf('\n')];
        Assert.DoesNotContain("Migrat", getActive);
        Assert.DoesNotContain("CreateDirectory", getActive);
    }


    [Fact]
    public void CtsSlot_RenewCancelsThePreviousOperation_AndDisposeCancelsTheLast()
    {
        var slot = new CtsSlot();
        CancellationToken first = slot.Renew();
        CancellationToken second = slot.Renew();
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        slot.Dispose();
        Assert.True(second.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => slot.Renew());
    }

    [Fact]
    public void ShellLauncher_RefusesAnEmptyTarget_WithoutThrowing()
    {
        Assert.False(ShellLauncher.TryOpen("", "TEST"));
        Assert.False(ShellLauncher.TryOpen("   ", "TEST"));
    }

    [Fact]
    public void NoDiscardedProcessStartRemainsInTheApp()
    {
        var offenders = new List<string>();
        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            if (!file.Replace('\\', '/').Contains("/src/FreeVideoStudio.App/", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.EndsWith("ShellLauncher.cs", StringComparison.OrdinalIgnoreCase)) continue;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].TrimStart();
                if (t.StartsWith("//")) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(System\.Diagnostics\.)?Process\.Start\(") && !t.Contains("?.Dispose()"))
                    offenders.Add($"{file}:{i + 1}");
            }
        }
        Assert.True(offenders.Count == 0, "SHELLLAUNCH_01 — discarded Process.Start handles:\n" + string.Join("\n", offenders));
    }


    [Fact]
    public void DispatcherHook_LetsOnlyProcessCorruptingFaultsThrough()
    {
        Assert.True(AvaloniaApp.IsProcessCorrupting(new OutOfMemoryException()));
        Assert.True(AvaloniaApp.IsProcessCorrupting(new AccessViolationException()));
        Assert.False(AvaloniaApp.IsProcessCorrupting(new InvalidOperationException()));
        Assert.False(AvaloniaApp.IsProcessCorrupting(new IOException()));
    }

    [Fact]
    public void AuditedMainWindowHandlers_AreNoLongerAsyncVoid()
    {
        string code = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.axaml.cs"));
        foreach (string name in new[] { "OnVideoDrop", "RdpFixButton_Click", "InitializeMpv" })
            Assert.DoesNotMatch(@"async\s+void\s+" + name + @"\b", code);
    }


    private sealed class FakeStore : ISettingsStore
    {
        public AppSettings Current { get; set; } = Frozen(new AppSettings());
        public int Transactions;
        public bool Result = true;
        public AppSettings? LastDraft;
        public bool TryUpdate(Action<AppSettings> patch)
        {
            Transactions++;
            var draft = SettingsViewModel.Clone(Current);
            patch(draft);
            LastDraft = draft;
            return Result;
        }
        private static AppSettings Frozen(AppSettings s) { s.Freeze(); return s; }
    }

    private sealed class FakeState : ISessionStateSink
    {
        public readonly List<JsonObject> Writes = new();
        public bool IsPrimed => true;
        public JsonObject ReadSnapshot() => new();
        public void Enqueue(JsonObject updates) => Writes.Add(updates);
    }

    /// <summary>Google's test waits until released or cancelled; every other company answers at once.</summary>
    private sealed class GatedKeys : IAiKeyValidator
    {
        public readonly TaskCompletionSource GoogleGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<KeyTestResult> TestAsync(AiVendor vendor, string key, string googleModel, CancellationToken ct)
        {
            if (vendor != AiVendor.Google) return new KeyTestResult(true, "ok");
            try { await GoogleGate.Task.WaitAsync(ct); return new KeyTestResult(true, "google ok"); }
            catch (OperationCanceledException) { return new KeyTestResult(false, "Not tested yet", Cancelled: true); }
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(AiVendor vendor, string key, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "m1" });
    }

    private static SettingsWindowServices Services(FakeStore store, FakeState state, IAiKeyValidator? keys = null) =>
        SettingsWindowServices.CreateDefault(new ApplicationPaths(Path.Combine(Path.GetTempPath(), "FvsDi_" + Guid.NewGuid().ToString("N"))))
            with { Store = store, State = state, Keys = keys ?? AiKeyValidator.Instance };

    [Fact]
    public async Task KeyTests_ForDifferentCompanies_NoLongerCancelEachOther()
    {
        var keys = new GatedKeys();
        var vm = SettingsViewModel.Open(Services(new FakeStore(), new FakeState(), keys), Array.Empty<string>());
        vm.GeminiApiKey = "g";
        vm.ClaudeApiKey = "c";

        Task google = vm.TestKeyAsync(AiVendor.Google);
        await vm.TestKeyAsync(AiVendor.Anthropic);
        Assert.Equal(StatusTone.Success, vm.ClaudeKeyStatus.Tone);
        Assert.True(vm.GeminiKeyStatus.IsBusy);

        keys.GoogleGate.SetResult();
        await google;
        Assert.Equal("google ok", vm.GeminiKeyStatus.Text);
        Assert.Equal(StatusTone.Success, vm.GeminiKeyStatus.Tone);
        vm.Dispose();
    }

    [Fact]
    public void Commit_IsOneTransactionThroughTheInjectedStore_ThenUpdatesTheStateStore()
    {
        var store = new FakeStore();
        var state = new FakeState();
        var vm = SettingsViewModel.Open(Services(store, state), Array.Empty<string>());
        vm.SaveFolder = Path.GetTempPath();
        Assert.True(vm.Commit());
        Assert.Equal(1, store.Transactions);
        Assert.Equal(Path.GetTempPath(), store.LastDraft!.MergerOutputDirectory);
        Assert.Contains(state.Writes, w => w.ContainsKey("MergerOutputDirectory"));

        store.Result = false;
        state.Writes.Clear();
        Assert.False(vm.Commit());
        Assert.Empty(state.Writes);
    }


    [Fact]
    public async Task ViralPost_ShowsTheHonestOutcome_AndPassesTheOldTitleOnRegenerate()
    {
        string? avoided = "unset";
        var vm = new ViralPostViewModel(new FakeStore(), (path, avoid, ct) =>
        {
            avoided = avoid;
            var post = new ViralPostResult("Title " + path, "Caption", new[] { "#a" });
            return Task.FromResult((new ViralPostOutcome(ViralPostStatus.AiGenerated, post, null), " (5 tokens)"));
        });
        await vm.StartAsync("v.mp4");
        Assert.Equal("AI WRITTEN (5 tokens)", vm.View.Badge);
        Assert.True(vm.View.ShowText);
        Assert.Null(avoided);
        await vm.RegenerateAsync();
        Assert.Equal("Title v.mp4", avoided);
    }

    [Fact]
    public async Task ViralPost_ACancelledRun_NeverOverwritesTheCard()
    {
        var gate = new TaskCompletionSource();
        var vm = new ViralPostViewModel(new FakeStore(), async (path, avoid, ct) =>
        {
            await gate.Task;
            return (new ViralPostOutcome(ViralPostStatus.AiGenerated, new ViralPostResult("late", "c", Array.Empty<string>()), null), "");
        });
        Task run = vm.StartAsync("v.mp4");
        Assert.Equal("WRITING…", vm.View.Badge);
        vm.Cancel();
        gate.SetResult();
        await run;
        Assert.Equal("CANCELLED", vm.View.Badge);
    }

    [Fact]
    public void ViralPost_NoKeyAndFailure_AreNamedHonestly()
    {
        Assert.Equal("NO AI KEY", ViralPostViewModel.ToView(new ViralPostOutcome(ViralPostStatus.NoKey, null, "add a key")).Badge);
        var failed = ViralPostViewModel.ToView(new ViralPostOutcome(ViralPostStatus.Failed, null, null));
        Assert.Equal("AI FAILED", failed.Badge);
        Assert.Equal("↻ RETRY", failed.RegenerateLabel);
    }

    [Fact]
    public async Task BackupStore_RefusesAnOversizedFile_BeforeReadingIt()
    {
        string path = Path.Combine(Path.GetTempPath(), "FvsBackup_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var fs = File.Create(path)) fs.SetLength(FileSettingsBackupStore.MaxBackupBytes + 1);
            var (json, error) = await FileSettingsBackupStore.Instance.ReadAsync(path, CancellationToken.None);
            Assert.Null(json);
            Assert.Contains("too big", error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BackupFile_RoundTrips_AsyncAndAtomic()
    {
        string path = Path.Combine(Path.GetTempPath(), "FvsBackup_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = SettingsViewModel.Open(Services(new FakeStore(), new FakeState()), Array.Empty<string>());
            vm.UiSoundVolume = 33;
            Assert.StartsWith("Saved:", await vm.WriteBackupFileAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-*"));

            var other = SettingsViewModel.Open(Services(new FakeStore(), new FakeState()), Array.Empty<string>());
            var (ok, _) = await other.ImportBackupFileAsync(path);
            Assert.True(ok);
            Assert.Equal(33, other.UiSoundVolume);
        }
        finally { File.Delete(path); }
    }
}
