using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FreeVideoStudio.App.Controls;
using System.Xml.Linq;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed class UiAccessibilityAuditTests
{
    [AvaloniaFact]
    public void DialExposesNamedSliderAndCommitsAccessibleChangesOnce()
    {
        var dial = new SpinningWheelSlider();
        dial.SetRange(0, 2);
        dial.SetLabels(new[] { "0.5x", "1.0x", "2.0x" });
        AutomationProperties.SetName(dial, "Video speed");
        var peer = ControlAutomationPeer.CreatePeerForElement(dial)!;
        Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
        Assert.Equal("Video speed", peer.GetName());
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer);
        var text = Assert.IsAssignableFrom<IValueProvider>(peer);
        int changes = 0, commits = 0;
        dial.ValueChanged += (_, _) => changes++;
        dial.ValueChangeCompleted += (_, _) => commits++;
        range.SetValue(1);
        Assert.Equal(1, range.Value);
        Assert.Equal("1.0x", text.Value);
        range.SetValue(1);
        Assert.Equal(1, changes);
        Assert.Equal(1, commits);
        text.SetValue("2.0x");
        Assert.Equal(2, dial.Value);
        Assert.Equal(2, commits);
        Assert.Throws<ArgumentOutOfRangeException>(() => range.SetValue(3));
        dial.IsEnabled = false;
        Assert.True(range.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => range.SetValue(0));
        Assert.Equal(2, dial.Value);
    }
    [AvaloniaTheory]
    [InlineData(PhysicalKey.Enter)]
    [InlineData(PhysicalKey.Space)]
    public void TourContainsFocusAndActivatesBackAndSkip(PhysicalKey activate)
    {
        var original = new Button { Content = "Original action" };
        var window = new Window { Width = 800, Height = 600, Content = new Grid { Children = { original } } };
        window.Show();
        original.Focus();
        try
        {
            CoachOverlay.PlayOnce(window, new[] { new CoachStep("First", "First step"), new CoachStep("Second", "Second step"), new CoachStep("Third", "Third step") });
            Button Find(string name) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == name);
            Assert.Same(Find("NEXT"), window.FocusManager!.GetFocusedElement());
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Same(Find("SKIP"), window.FocusManager.GetFocusedElement());
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.Same(Find("NEXT"), window.FocusManager.GetFocusedElement());
            window.KeyPressQwerty(activate, RawInputModifiers.None);
            window.KeyReleaseQwerty(activate, RawInputModifiers.None);
            Assert.True(Find("BACK").IsVisible);
            Find("BACK").Focus();
            window.KeyPressQwerty(activate, RawInputModifiers.None);
            window.KeyReleaseQwerty(activate, RawInputModifiers.None);
            Assert.False(Find("BACK").IsVisible);
            Assert.Same(Find("NEXT"), window.FocusManager.GetFocusedElement());
            Find("SKIP").Focus();
            window.KeyPressQwerty(activate, RawInputModifiers.None);
            window.KeyReleaseQwerty(activate, RawInputModifiers.None);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "NEXT");
            Assert.Same(original, window.FocusManager.GetFocusedElement());
        }
        finally { CoachOverlay.Cancel(window); window.Close(); }
    }
    [AvaloniaFact]
    public void TourEscapeRestoresFocusAndLastStepKeepsFocusVisible()
    {
        var original = new Button { Content = "Original" };
        var window = new Window { Content = new Grid { Children = { original } } };
        window.Show();
        original.Focus();
        try
        {
            CoachOverlay.PlayOnce(window, new[] { new CoachStep("First", "One"), new CoachStep("Last", "Two") });
            var skip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "SKIP");
            skip.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.False(skip.IsVisible);
            Assert.Equal("GOT IT", Assert.IsType<Button>(window.FocusManager!.GetFocusedElement()).Content);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Same(original, window.FocusManager.GetFocusedElement());
        }
        finally { CoachOverlay.Cancel(window); window.Close(); }
    }
    [AvaloniaFact]
    public void CompositeButtonsAndEveryDialHaveMeaningfulNames()
    {
        foreach (string file in new[] { "MainWindow.axaml", "VideoMergerWindow.axaml", "GranularSpeedEditorWindow.axaml" })
        {
            var doc = XDocument.Load(RepoRoot.SourcePath("src", "FreeVideoStudio.App", file));
            foreach (var dial in doc.Descendants().Where(e => e.Name.LocalName == "SpinningWheelSlider"))
                Assert.False(string.IsNullOrWhiteSpace((string?)dial.Attribute("AutomationProperties.Name")));
            if (file != "MainWindow.axaml") continue;
            foreach (var pair in new[] { ("SetThumbnailButton", "Set thumbnail"), ("VoiceOverButton", "Voice over"), ("AddMusicButton", "Add music") })
            {
                var element = doc.Descendants().Single(e => (string?)e.Attribute("Name") == pair.Item1);
                var button = new Button { Content = new StackPanel() };
                AutomationProperties.SetName(button, (string?)element.Attribute("AutomationProperties.Name"));
                Assert.Equal(pair.Item2, ControlAutomationPeer.CreatePeerForElement(button)!.GetName());
            }
        }
    }
}
