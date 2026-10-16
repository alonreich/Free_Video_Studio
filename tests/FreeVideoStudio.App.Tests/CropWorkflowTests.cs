using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Editing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using SkiaSharp;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(FreeVideoStudio.App.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FreeVideoStudio.App.Tests;

public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class CropWorkflowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsCropUiTests_" + Guid.NewGuid().ToString("N"));
    private string? _previousRoot;
    private ApplicationPaths Paths => new(_root);

    public Task InitializeAsync()
    {
        _previousRoot = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _root);
        SettingsManager.Instance.ActiveMaskOverlay = "Fortnite";
        SettingsManager.Instance.LastEditedCropProfile = "";
        return Task.CompletedTask;
    }

    [AvaloniaFact]
    public void FreshInstallSeedsFortniteWithoutRecursion()
    {
        MaskOverlayManager.EnsureDefaults();
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), AtomicJsonFile.ReadObject(Paths.CropCoordinatesFile)));
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), AtomicJsonFile.ReadObject(Profile("Fortnite"))));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedFortniteIsBackedUpAndRestored(bool malformedJson)
    {
        Directory.CreateDirectory(MaskOverlayManager.ProfilesDirectory);
        var damaged = CropConfigDefaults.Create();
        damaged["scales"]!["loot"] = "1/0";
        string original = malformedJson ? "{broken" : damaged.ToJsonString();
        File.WriteAllText(Profile("Fortnite"), original);
        MaskOverlayManager.EnsureDefaults();
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), AtomicJsonFile.ReadObject(Profile("Fortnite"))));
        string backup = Assert.Single(Directory.GetFiles(MaskOverlayManager.ProfilesDirectory, "Fortnite.json.invalid.*.bak"));
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [AvaloniaFact]
    public void ExistingFortniteEditsSurviveAndDoNotSeedFromAnotherActiveProfile()
    {
        Paths.EnsureWritableDirectories();
        Directory.CreateDirectory(MaskOverlayManager.ProfilesDirectory);
        var custom = CropConfigDefaults.Create();
        custom["overlays"]!["loot"]!["x"] = 123;
        AtomicJsonFile.WriteObject(Paths.CropCoordinatesFile, custom);
        MaskOverlayManager.EnsureDefaults();
        Assert.True(JsonNode.DeepEquals(CropConfigDefaults.Create(), AtomicJsonFile.ReadObject(Profile("Fortnite"))));
        AtomicJsonFile.WriteObject(Profile("Fortnite"), custom);
        MaskOverlayManager.EnsureDefaults();
        Assert.True(JsonNode.DeepEquals(custom, AtomicJsonFile.ReadObject(Profile("Fortnite"))));
    }

    [AvaloniaFact]
    public void ProfileWriteFailureIsReported()
    {
        MaskOverlayManager.EnsureDefaults();
        SettingsManager.Instance.ActiveMaskOverlay = "Blocked";
        Directory.CreateDirectory(Profile("Blocked"));
        Assert.False(MaskOverlayManager.SyncActiveProfileFromCurrentConfig());
    }

    [AvaloniaFact]
    public void SpectatingStartsOnAndNoMaskRemainsEmpty()
    {
        var vm = new MainViewModel(Paths);
        Assert.True(vm.IsSpectating);
        vm.IsSpectating = false;
        vm.ApplyDefaults();
        Assert.True(vm.IsSpectating);
        vm.ApplyMaskProfile(CropConfigDefaults.NoMaskProfileName);
        Assert.False(vm.IsSpectating);
        Assert.False(vm.IsTeammates);
    }

    [AvaloniaTheory]
    [InlineData("save", ConfirmDialogWindow.SaveChangesChoice.Save)]
    [InlineData("discard", ConfirmDialogWindow.SaveChangesChoice.Discard)]
    [InlineData("back", ConfirmDialogWindow.SaveChangesChoice.BackToEditing)]
    [InlineData("close", ConfirmDialogWindow.SaveChangesChoice.BackToEditing)]
    [InlineData("enter", ConfirmDialogWindow.SaveChangesChoice.BackToEditing)]
    [InlineData("escape", ConfirmDialogWindow.SaveChangesChoice.BackToEditing)]
    public async Task UnsavedPromptHasClearActionsAndSafeKeyboardDefaults(string action, ConfirmDialogWindow.SaveChangesChoice expected)
    {
        var owner = new Window();
        owner.Show();
        var result = ConfirmDialogWindow.AskSaveChangesAsync(owner, "Fortnite", "closing Crop Tools");
        var dialog = Assert.IsType<ConfirmDialogWindow>(Assert.Single(owner.OwnedWindows));
        var save = dialog.FindControl<Button>("YesBtn")!;
        var back = dialog.FindControl<Button>("NoBtn")!;
        var discard = dialog.FindControl<Button>("AltBtn")!;
        Assert.Equal("Save changes", save.Content);
        Assert.Equal("Back to editing", back.Content);
        Assert.Equal("Discard changes", discard.Content);
        Assert.Contains("Success", save.Classes);
        Assert.Contains("Secondary", back.Classes);
        Assert.Contains("Danger", discard.Classes);
        Assert.False(save.IsDefault);
        Assert.True(back.IsDefault);
        Assert.True(back.IsCancel);
        switch (action)
        {
            case "save": save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case "discard": discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case "back": back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case "close": dialog.Close(); break;
            case "enter": dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); break;
            case "escape": dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); break;
        }
        Assert.Equal(expected, await result.WaitAsync(TimeSpan.FromSeconds(3)));
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddingHudRestoresTemporaryZoomAndPreservesManualZoom(bool temporaryZoom)
    {
        var window = new CropToolWindow();
        var source = new FreeVideoStudio.Core.Editing.CropSourceRect(1500, 900, 160, 80);
        Set(window, "_sourceSelection", source);
        string snapshot = Path.Combine(_root, "frame.png");
        using (var bitmap = new SKBitmap(1920, 1080))
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(snapshot)) data.SaveTo(stream);
        Set(window, "_snapshotPath", snapshot);
        Invoke(window, "ApplySnapshotZoomInternal", 2.0, false, true);
        if (temporaryZoom) Set(window, "_preZoomState", Nested("PreZoomState", 0.5, true, false, new Vector()));
        var role = FreeVideoStudio.Core.Editing.CropEditSession.BuiltInRoles[0];
        await (Task)Invoke(window, "AddCurrentSelection", role)!;
        var edit = (FreeVideoStudio.Core.Editing.CropEditSession)Get(window, "_edit")!;
        Assert.True(edit.Dirty);
        Assert.Single(edit.Layers);
        Assert.Equal(role.Key, edit.SelectedRoleKey);
        Assert.Null(Get(window, "_sourceSelection"));
        Assert.Null(Get(window, "_preZoomState"));
        Assert.Equal(temporaryZoom ? 0.5 : 2.0, (double)Invoke(window, "CurrentZoom")!);
        Assert.Equal(temporaryZoom, Get(window, "_snapshotFitMode"));
        Assert.Equal(!temporaryZoom, Get(window, "_userZoomed"));
        edit.Dirty = false;
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void FirstUse_NoPreviousProfile_PickerEmpty_GateLocked()
    {
        SettingsManager.Instance.LastEditedCropProfile = "";
        var window = new CropToolWindow();
        var edit = (CropEditSession)Get(window, "_edit")!;
        var combo = window.FindControl<ComboBox>("CropToolMaskOverlayCombo");
        var stateLabel = (TextBlock?)Get(window, "_profileStateLabel");

        Assert.False(edit.GateUnlocked);
        Assert.Null(edit.ActiveProfile);
        Assert.Null(Get(window, "_verifiedEditingProfile"));
        Assert.Null(combo?.SelectedItem);
        Assert.Equal("No profile selected", stateLabel?.Text);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ReopenSavedValidProfile_GateUnlocked_IdentityVerified()
    {
        MaskOverlayManager.EnsureDefaults();
        SettingsManager.Instance.LastEditedCropProfile = "Fortnite";
        var window = new CropToolWindow();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        var edit = (CropEditSession)Get(window, "_edit")!;
        var combo = window.FindControl<ComboBox>("CropToolMaskOverlayCombo");
        var stateLabel = (TextBlock?)Get(window, "_profileStateLabel");

        Assert.True(edit.GateUnlocked);
        Assert.Equal("Fortnite", edit.ActiveProfile);
        Assert.Equal("Fortnite", (string?)Get(window, "_verifiedEditingProfile"));
        Assert.Equal("Fortnite", combo?.SelectedItem);
        Assert.Equal("EDITING PROFILE: Fortnite", stateLabel?.Text);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData("CorruptProfile", "{malformed json")]
    [InlineData("No Mask", "")]
    [InlineData("NonExistent", "")]
    public void InvalidOrCorruptProfileChoice_DoesNotUnlockOrRelabel(string profileName, string content)
    {
        Directory.CreateDirectory(MaskOverlayManager.ProfilesDirectory);
        if (!string.IsNullOrEmpty(content))
        {
            File.WriteAllText(Profile(profileName), content);
        }
        SettingsManager.Instance.LastEditedCropProfile = profileName;

        var window = new CropToolWindow();
        Dispatcher.UIThread.RunJobs();

        var edit = (CropEditSession)Get(window, "_edit")!;
        var combo = window.FindControl<ComboBox>("CropToolMaskOverlayCombo");
        var stateLabel = (TextBlock?)Get(window, "_profileStateLabel");

        Assert.False(edit.GateUnlocked);
        Assert.Null(edit.ActiveProfile);
        Assert.Null(Get(window, "_verifiedEditingProfile"));
        Assert.Null(combo?.SelectedItem);
        Assert.Equal("No profile selected", stateLabel?.Text);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task FailedProfileLoadAfterValidProfile_PreservesPriorValidStateWithoutRelabeling()
    {
        MaskOverlayManager.EnsureDefaults();
        SettingsManager.Instance.LastEditedCropProfile = "Fortnite";
        var window = new CropToolWindow();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        var edit = (CropEditSession)Get(window, "_edit")!;
        var stateLabel = (TextBlock?)Get(window, "_profileStateLabel");

        Assert.True(edit.GateUnlocked);
        Assert.Equal("Fortnite", edit.ActiveProfile);
        int initialLayerCount = edit.Layers.Count;

        File.WriteAllText(Profile("BrokenProfile"), "{bad json");

        bool result = await (Task<bool>)Invoke(window, "OnProfileChosenAsync", "BrokenProfile")!;
        Assert.False(result);

        Assert.Equal("Fortnite", edit.ActiveProfile);
        Assert.Equal("Fortnite", (string?)Get(window, "_verifiedEditingProfile"));
        Assert.True(edit.GateUnlocked);
        Assert.Equal(initialLayerCount, edit.Layers.Count);
        Assert.Equal("EDITING PROFILE: Fortnite", stateLabel?.Text);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task GlobalActiveMaskOverlayMismatch_SavesOnlyToVerifiedProfile()
    {
        MaskOverlayManager.EnsureDefaults();
        
        var otherConfig = CropConfigDefaults.Create();
        otherConfig["overlays"]!["loot"]!["x"] = 999;
        string otherProfilePath = Profile("OtherProfile");
        AtomicJsonFile.WriteObject(otherProfilePath, otherConfig);
        string originalOtherText = File.ReadAllText(otherProfilePath);

        SettingsManager.Instance.LastEditedCropProfile = "Fortnite";
        var window = new CropToolWindow();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        var edit = (CropEditSession)Get(window, "_edit")!;
        Assert.Equal("Fortnite", edit.ActiveProfile);

        SettingsManager.Instance.ActiveMaskOverlay = "OtherProfile";

        var role = CropEditSession.BuiltInRoles[0];
        edit.AdoptSavedLayer(new CropLayer
        {
            RoleKey = role.Key,
            DisplayName = role.DisplayName,
            SourceRect = new CropSourceRect(100, 100, 200, 200),
            CropImagePath = "",
            X = 50,
            Y = 50,
            Width = 200,
            Height = 200,
            Z = 1,
            GeometryVerified = true
        });
        edit.Dirty = true;
        Assert.True(edit.Dirty);

        bool saved = await (Task<bool>)Invoke(window, "SaveConfigAsync")!;
        Assert.True(saved);
        Assert.False(edit.Dirty);

        string afterOtherText = File.ReadAllText(otherProfilePath);
        Assert.Equal(originalOtherText, afterOtherText);

        var savedFortnite = AtomicJsonFile.ReadObject(Profile("Fortnite"));
        Assert.NotNull(savedFortnite);
        var overlays = savedFortnite!["overlays"];
        Assert.NotNull(overlays);
        Assert.NotNull(overlays![role.Key]);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task PartialWriteFailure_SyncFails_RemainsDirty()
    {
        MaskOverlayManager.EnsureDefaults();
        SettingsManager.Instance.LastEditedCropProfile = "Fortnite";
        var window = new CropToolWindow();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        var edit = (CropEditSession)Get(window, "_edit")!;
        var role = CropEditSession.BuiltInRoles[0];
        edit.AdoptSavedLayer(new CropLayer
        {
            RoleKey = role.Key,
            DisplayName = role.DisplayName,
            SourceRect = new CropSourceRect(100, 100, 200, 200),
            CropImagePath = "",
            X = 50,
            Y = 50,
            Width = 200,
            Height = 200,
            Z = 1,
            GeometryVerified = true
        });
        edit.Dirty = true;
        Assert.True(edit.Dirty);

        File.Delete(Profile("Fortnite"));
        Directory.CreateDirectory(Profile("Fortnite"));

        bool saved = await (Task<bool>)Invoke(window, "SaveConfigAsync")!;
        Assert.False(saved);
        Assert.True(edit.Dirty);

        Directory.Delete(Profile("Fortnite"));
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ConfirmationPanels_ResponsiveLayoutAtNarrowWidth_WarningAndButtonsFitInsidePane()
    {
        var window = new CropToolWindow();
        var deletePanel = window.FindControl<StackPanel>("DeleteConfirmPanel")!;
        var resetPanel = window.FindControl<StackPanel>("ResetConfirmPanel")!;

        Assert.NotNull(deletePanel);
        Assert.NotNull(resetPanel);

        foreach (var panel in new[] { deletePanel, resetPanel })
        {
            panel.IsVisible = true;

            var textBlock = panel.Children.OfType<TextBlock>().First();
            Assert.Equal(TextWrapping.Wrap, textBlock.TextWrapping);

            var wrapPanel = panel.Children.OfType<WrapPanel>().First();
            Assert.NotNull(wrapPanel);

            var cancelBtn = wrapPanel.Children.OfType<Button>().First(b => b.Content?.ToString() == "CANCEL");
            var confirmBtn = wrapPanel.Children.OfType<Button>().First(b => b.Content?.ToString()?.StartsWith("CONFIRM") == true);

            Assert.Contains("Primary", cancelBtn.Classes);
            Assert.Contains("Danger", confirmBtn.Classes);
            Assert.False(confirmBtn.IsDefault);

            double availableWidth = 336 - panel.Margin.Left - panel.Margin.Right;
            panel.Measure(new Size(availableWidth, double.PositiveInfinity));
            Assert.True(panel.DesiredSize.Width <= 336, $"DesiredSize.Width was {panel.DesiredSize.Width}");

            panel.Arrange(new Rect(0, 0, 336, panel.DesiredSize.Height));
            Assert.True(cancelBtn.Bounds.Width > 0);
            Assert.True(confirmBtn.Bounds.Width > 0);
            Assert.True(cancelBtn.Bounds.Right <= 336);
            Assert.True(confirmBtn.Bounds.Right <= 336);
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("No Mask", false)]
    [InlineData("NonExistentProfile_999", false)]
    [InlineData("Invalid:Name", false)]
    [InlineData("Fortnite", true)]
    public void MaskOverlayManager_LoadProfile_ValidatesExplicitly(string? name, bool expectedSuccess)
    {
        MaskOverlayManager.EnsureDefaults();
        var result = MaskOverlayManager.LoadProfile(name);
        Assert.Equal(expectedSuccess, result.Success);
        if (expectedSuccess)
        {
            Assert.Equal(name, result.ProfileName);
            Assert.NotNull(result.Config);
            Assert.Null(result.ErrorMessage);
        }
        else
        {
            Assert.NotNull(result.ErrorMessage);
        }
    }

    private string Profile(string name) => Path.Combine(MaskOverlayManager.ProfilesDirectory, name + ".json");
    private static object? Get(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj);
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static object? Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static object Nested(string name, params object[] args) => Activator.CreateInstance(typeof(CropToolWindow).GetNestedType(name, BindingFlags.NonPublic)!, args)!;

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _previousRoot);
        for (int attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); return; }
            catch (IOException) when (attempt < 5) { await Task.Delay(100); }
        }
    }
}
