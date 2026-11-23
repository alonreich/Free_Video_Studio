using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class UiQaFixTests
{
    private static string MainWindowXaml() => File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.axaml"));

    private static FinishedDialogWindow FinishedDialog(Func<string, Task<bool>> copy)
    {
        SettingsManager.ReplaceInstanceForTests(s => s.AiViralPostHelperEnabled = false);
        string video = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "UiQa", Guid.NewGuid().ToString("N"), "done.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        File.WriteAllBytes(video, new byte[] { 1 });
        var dialog = new FinishedDialogWindow();
        dialog.SetOutputPath(video);
        dialog.ShellLaunch = _ => throw new Win32Exception(2, "No browser is registered.");
        dialog.CopyTextOverride = copy;
        dialog.Show();
        return dialog;
    }

    private static Task Share(FinishedDialogWindow dialog, string site, string url) =>
        (Task)typeof(FinishedDialogWindow).GetMethod("ShareAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(dialog, new object?[] { site, url, false })!;

    [AvaloniaTheory]
    [InlineData(true, "the link is copied")]
    [InlineData(false, "Go to https://studio.youtube.com/ yourself")]
    public async Task FailedShareLaunch_ShowsAMessageWithANextStep(bool clipboardWorks, string nextStep)
    {
        var dialog = FinishedDialog(_ => Task.FromResult(clipboardWorks));
        await Share(dialog, "YouTube", "https://studio.youtube.com/");
        Assert.NotNull(dialog.ShareProblem);
        Assert.Contains("Couldn't open the browser for YouTube", dialog.ShareProblem!);
        Assert.Contains(nextStep, dialog.ShareProblem!);
        var status = dialog.FindControl<TextBlock>("ShareStatusText")!;
        Assert.True(status.IsVisible);
        Assert.True(dialog.IsVisible, "A failed share must leave the dialog open so the user can act on the message.");
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task FailedCopyCredit_ShowsTheCreditToCopyByHand()
    {
        var dialog = FinishedDialog(_ => Task.FromResult(false));
        dialog.SetAttributionText("Song by Someone (CC BY 4.0)");
        await dialog.CopyCreditAsync();
        Assert.Contains("Couldn't copy the music credit", dialog.ShareProblem);
        Assert.Contains("Song by Someone (CC BY 4.0)", dialog.ShareProblem);
        dialog.Close();
    }

    [AvaloniaFact]
    public void Escape_ClosesTheErrorDialog()
    {
        var dialog = new ErrorDialogWindow();
        dialog.Show();
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void Escape_ClosesTheFinishedDialogLikeTheCloseButton()
    {
        var dialog = FinishedDialog(_ => Task.FromResult(true));
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(dialog.IsVisible);
        Assert.Equal(0, dialog.DialogResult);
    }

    [AvaloniaFact]
    public void MainWindowLabels_NeverUseTheTinyNinePixelSize()
    {
        var tiny = Regex.Matches(MainWindowXaml(), @"AppFontSize(?<size>\d+)\}")
            .Select(m => int.Parse(m.Groups["size"].Value))
            .Where(size => size < 10)
            .ToList();
        Assert.Empty(tiny);
    }

    [AvaloniaFact]
    public void MainMenu_ListsEveryCommandOnce()
    {
        XNamespace ns = "https://github.com/avaloniaui";
        var headers = XDocument.Parse(MainWindowXaml()).Descendants(ns + "MenuItem")
            .Select(m => (string?)m.Attribute("Header"))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => Regex.Replace(h!.Replace("_", ""), @"\s{2,}.*$", "").Trim())
            .ToList();
        var duplicates = headers.GroupBy(h => h.Replace("About Free Video Studio", "About"), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
        Assert.Contains("Open Recent", headers);
        Assert.Contains("Open Project...", headers);
    }

    [AvaloniaFact]
    public void LocalFileActions_NeverSayUpload()
    {
        string xaml = MainWindowXaml();
        var labels = Regex.Matches(xaml, @"\b(?:Header|Content|Text)=""(?<label>[^""]*)""")
            .Select(m => m.Groups["label"].Value)
            .Where(label => label.Contains("upload", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(labels);
        Assert.DoesNotContain("upload a video", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPEN VIDEO", xaml);
        Assert.Contains("OPEN VIDEO", SingleInstanceGuard.HandoffFailureMessage(@"C:\clips\a.mp4"));
    }
}
