using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public class VoiceOverGainUiTests
{
    [AvaloniaFact]
    public async Task ApplySavesGainWithoutChangingRecordedSamples()
    {
        string root = Path.Combine(RepoRoot.Path, "TestResults", "voice-gain-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        using var capture = new VoiceCaptureSession(new SampleCaptureDevices(), a => a());
        var window = new VoiceOverWindow(capture);
        string path = Path.Combine(root, "take.wav");
        byte[] samples = Enumerable.Repeat((byte)0x21, 88200).ToArray();
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(44100, 16, 1)))
            writer.Write(samples, 0, samples.Length);
        window.TriggerCompleteTake(new VoiceOverWindow.VoiceOverSession { WavPath = path, StartSec = 0 }, true, samples.Length, 1, 0.2f);
        window.FindControl<VoiceOverGainSlider>("VoiceGainSlider")!.Value = 150;
        await window.ApplyAndCloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var saved = Assert.Single(window.Result!.VoiceOverTakes);
        Assert.Equal(1.5, saved.Gain);
        Assert.Equal(samples, SampleCaptureDevices.ReadPcm(saved.Path));
    }

    [Fact]
    public void MagnetHoldsUnityAndCanBeEscapedInBothDirections()
    {
        bool held = false;
        Assert.Equal(100, VoiceOverGainSlider.Snap(98, ref held));
        Assert.Equal(100, VoiceOverGainSlider.Snap(105, ref held));
        Assert.Equal(108, VoiceOverGainSlider.Snap(108, ref held));
        Assert.Equal(100, VoiceOverGainSlider.Snap(102, ref held));
        Assert.Equal(100, VoiceOverGainSlider.Snap(95, ref held));
        Assert.Equal(92, VoiceOverGainSlider.Snap(92, ref held));
        Assert.Equal(0, VoiceOverGainSlider.Snap(-10, ref held));
        Assert.Equal(150, VoiceOverGainSlider.Snap(170, ref held));
    }

    [AvaloniaFact]
    public void PointerCanLeaveUnityAndKeyboardCanFineTune()
    {
        var slider = new VoiceOverGainSlider { Height = 120 };
        var host = new Window { Width = 160, Height = 180, Content = slider };
        host.Show();
        try
        {
            var at = slider.TranslatePoint(new Point(slider.Bounds.Width / 2, 22), host)!.Value;
            host.MouseDown(at, MouseButton.Left);
            Assert.Equal(150, slider.Value);
            host.MouseMove(at.WithY(at.Y + 76 * (50.0 / 150)));
            Assert.Equal(100, slider.Value);
            host.MouseMove(at.WithY(at.Y + 76));
            Assert.Equal(0, slider.Value);
            host.MouseUp(at.WithY(at.Y + 76), MouseButton.Left);
            slider.Value = 100;
            slider.Focus();
            host.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Assert.Equal(101, slider.Value);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public void StudioDefaultsToUnityRestoresGainAndScalesRail()
    {
        string root = Path.Combine(RepoRoot.Path, "TestResults", "voice-gain-ui-" + Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        using var capture = new VoiceCaptureSession(new SampleCaptureDevices(), a => a());
        var window = new VoiceOverWindow(capture) { Width = 980, Height = 640 };
        var slider = window.FindControl<VoiceOverGainSlider>("VoiceGainSlider")!;
        Assert.Equal(100, slider.Value);
        window.InitialState = new() { VoiceOverTakes = [new("unused.wav", 0, Gain: 0.5)] };
        window.Show();
        try
        {
            Assert.Equal(50, slider.Value);
            Assert.Equal("50%", window.FindControl<TextBlock>("VoiceGainReadout")!.Text);
            var rail = window.FindControl<Grid>("VoiceGainRail")!;
            window.Height = 640;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            double small = rail.Height;
            window.Height = 960;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(rail.Height > small, $"Rail did not grow: {small} -> {rail.Height}, window {window.Bounds.Height}");
            var reset = window.FindControl<Button>("VoiceGainReset")!;
            var point = reset.TranslatePoint(new Point(reset.Bounds.Width / 2, reset.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(100, slider.Value);
            Assert.Equal(1, window.VoiceVolume.Gain);
            window.VoiceVolume.CanEdit = false;
            Assert.False(slider.IsEnabled);
            Assert.False(reset.IsEnabled);
        }
        finally { window.IsSafeToClose = true; window.Close(); }
    }
}
