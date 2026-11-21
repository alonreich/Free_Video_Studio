using System.Reflection;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public class UxAuditTests
{
    private sealed class FakeExport(ExportState state) : IExportCoordinator
    {
        public ExportState State => state;
        public event EventHandler<ExportState>? StateChanged { add { } remove { } }
        public event EventHandler<ExportCompletion>? Completed { add { } remove { } }
        public Task<ExportCompletion> StartAsync(ExportRequest request) => throw new NotSupportedException();
        public bool Cancel() => false;
        public Task<bool> ShutdownAsync(TimeSpan timeout) => Task.FromResult(true);
    }

    private static Task<bool> AskStopExport(MainWindow window) =>
        (Task<bool>)typeof(MainWindow).GetMethod("ConfirmStopExportOnCloseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, null)!;

    private static void UseExport(MainWindow window, ExportState state) =>
        typeof(MainWindow).GetField("_exportCoordinator", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, new FakeExport(state));

    [AvaloniaTheory]
    [InlineData(ExportState.Idle, true, true, 0)]
    [InlineData(ExportState.Cancelling, true, true, 0)]
    [InlineData(ExportState.Running, true, true, 1)]
    [InlineData(ExportState.Running, false, false, 1)]
    public async Task ClosingDuringExportAsksFirst(ExportState state, bool answer, bool closes, int prompts)
    {
        string root = Path.Combine(Path.GetTempPath(), "ux-close-" + Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        FreeVideoStudio.App.Infrastructure.AppServices.Initialize(new ApplicationPaths(root));
        var window = new MainWindow();
        UseExport(window, state);
        int asked = 0;
        window.StopExportDecisionSeam = () => { asked++; return Task.FromResult(answer); };
        Assert.Equal(closes, await AskStopExport(window));
        Assert.Equal(prompts, asked);
    }

    [AvaloniaFact]
    public async Task FinishedExport_ExitsWithoutAskingStopTheExport()
    {
        string root = Path.Combine(Path.GetTempPath(), "ux-finish-exit-" + Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        FreeVideoStudio.App.Infrastructure.AppServices.Initialize(new ApplicationPaths(root));
        var window = new MainWindow();
        var coordinator = (IExportCoordinator)typeof(MainWindow).GetField("_exportCoordinator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        int asked = 0;
        window.StopExportDecisionSeam = () => { asked++; return Task.FromResult(false); };

        ExportCompletion done = await coordinator.StartAsync(new ExportRequest("test",
            new DelegateExportRunner(_ => Task.FromResult(ExportOutcome.Succeeded))));

        Assert.Equal(ExportOutcome.Succeeded, done.Outcome);
        Assert.Equal(ExportState.Idle, coordinator.State);
        Assert.True(await AskStopExport(window));
        Assert.Equal(0, asked);

        string exportSrc = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "MainWindow.Export.cs"));
        int coreDecl = exportSrc.IndexOf("ProcessVideoCoreAsync(System.Threading.CancellationToken", StringComparison.Ordinal);
        int runPostDecl = exportSrc.IndexOf("private async Task RunPostExportUiAsync(", StringComparison.Ordinal);
        int finishedDlgIdx = exportSrc.IndexOf("new FreeVideoStudio.App.Controls.FinishedDialogWindow()", StringComparison.Ordinal);
        Assert.True(coreDecl > 0 && runPostDecl > 0 && finishedDlgIdx > runPostDecl && finishedDlgIdx < coreDecl);
    }

    [AvaloniaTheory]
    [InlineData("invalid")]
    [InlineData("lock")]
    [InlineData("io")]
    [InlineData("other")]
    public void CropSaveFailuresAreActionableAndHideRawExceptionText(string kind)
    {
        Exception ex = kind switch
        {
            "invalid" => new InvalidOperationException("Refusing to save the crop configuration: RAWDETAIL."),
            "lock" => new LockException("Timed out waiting for named mutex 'RAWDETAIL'."),
            "io" => new IOException("RAWDETAIL sharing violation"),
            _ => new FormatException("RAWDETAIL")
        };
        string text = CropToolWindow.DescribeSaveFailure(ex, @"C:\logs\app.log");
        Assert.StartsWith("Could not save", text);
        Assert.DoesNotContain("RAWDETAIL", text);
        if (kind == "other") Assert.Contains(@"C:\logs\app.log", text);
    }

    [AvaloniaFact]
    public void SegmentRowIsAnAnnouncedInvokableListItem()
    {
        var row = new GranularSpeedEditorWindow.SegmentRowBorder();
        AutomationProperties.SetName(row, "Segment 1 of 2");
        int invoked = 0;
        row.Invoked += () => invoked++;
        var peer = ControlAutomationPeer.CreatePeerForElement(row);
        Assert.Equal(AutomationControlType.ListItem, peer.GetAutomationControlType());
        Assert.True(peer.IsControlElement());
        Assert.Equal("Segment 1 of 2", peer.GetName());
        Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>(peer).Invoke();
        Assert.Equal(1, invoked);
    }

    [AvaloniaFact]
    public void SegmentRowsFollowTheThemeLive()
    {
        var window = new Window { RequestedThemeVariant = ThemeVariant.Light };
        window.Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary { ["AppZoomCardBrush"] = Brushes.White };
        window.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary { ["AppZoomCardBrush"] = Brushes.Black };
        var row = new GranularSpeedEditorWindow.SegmentRowBorder();
        typeof(GranularSpeedEditorWindow).GetMethod("PaintToken", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { row, Border.BackgroundProperty, "AppZoomCardBrush" });
        window.Content = row;
        window.Show();
        Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
        window.Close();
    }

    [Fact]
    public void SegmentListUsesNoHardcodedColours()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        foreach (string hex in new[] { "#1e293b", "#3d4f63", "#26364a", "#334155", "#e2e8f0" })
            Assert.DoesNotContain(hex, source, StringComparison.OrdinalIgnoreCase);
    }
}
