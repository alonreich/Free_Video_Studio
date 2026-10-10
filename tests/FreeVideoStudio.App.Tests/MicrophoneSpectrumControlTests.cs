// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SPECTRUM_03 / SPECTRUM_04 — the production spectrum control and its wiring, headless. Geometry
/// and brush selection are asserted on the control's own layout model (a screenshot cannot prove
/// that bar 7 is 150 Hz); the spectra fed in are real analyzer output from generated PCM.
/// </summary>
public sealed class MicrophoneSpectrumControlTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "FvsSpectrumTest_" + Guid.NewGuid().ToString("N"));

    public MicrophoneSpectrumControlTests()
    {
        Directory.CreateDirectory(_tempDir);
        VoiceOverWindow.RecoveryDirectorySeam = () => _tempDir;
    }

    public void Dispose()
    {
        VoiceOverWindow.RecoveryDirectorySeam = null;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Distinct per-theme colours so the test can tell which dictionary answered.
    private static readonly Dictionary<string, (Color Dark, Color Light)> Tokens = new()
    {
        [MicrophoneSpectrumControl.BackgroundBrushKey] = (Color.FromRgb(1, 1, 1), Color.FromRgb(2, 2, 2)),
        [MicrophoneSpectrumControl.UnlitBrushKey] = (Color.FromRgb(3, 3, 3), Color.FromRgb(4, 4, 4)),
        [MicrophoneSpectrumControl.NormalBrushKey] = (Color.FromRgb(0, 200, 0), Color.FromRgb(0, 150, 0)),
        [MicrophoneSpectrumControl.WarningBrushKey] = (Color.FromRgb(250, 160, 0), Color.FromRgb(240, 150, 0)),
        [MicrophoneSpectrumControl.PeakBrushKey] = (Color.FromRgb(240, 0, 0), Color.FromRgb(220, 0, 0)),
        [MicrophoneSpectrumControl.LabelBrushKey] = (Color.FromRgb(150, 150, 150), Color.FromRgb(200, 200, 200)),
    };

    private static (Window Window, MicrophoneSpectrumControl Meter) Host(double width = 420, double height = 68)
    {
        var dark = new ResourceDictionary();
        var light = new ResourceDictionary();
        foreach (var (key, (d, l)) in Tokens)
        {
            dark[key] = new SolidColorBrush(d);
            light[key] = new SolidColorBrush(l);
        }
        var meter = new MicrophoneSpectrumControl();
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = meter,
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        window.Resources.ThemeDictionaries[ThemeVariant.Dark] = dark;
        window.Resources.ThemeDictionaries[ThemeVariant.Light] = light;
        window.Show();
        window.UpdateLayout();
        return (window, meter);
    }

    private static MicrophoneSpectrumSnapshot ToneSnapshot(double hz, double amp)
    {
        int frames = 8192;
        var bytes = new byte[frames * 2];
        for (int n = 0; n < frames; n++)
        {
            short s = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * hz * n / 44100.0));
            bytes[2 * n] = (byte)(s & 0xFF);
            bytes[2 * n + 1] = (byte)((s >> 8) & 0xFF);
        }
        var a = new MicrophoneSpectrumAnalyzer();
        a.Process(bytes);
        return a.Latest!;
    }

    private static MicrophoneSpectrumSnapshot SilenceSnapshot()
    {
        var a = new MicrophoneSpectrumAnalyzer();
        a.Process(new byte[8192 * 2]);
        return a.Latest!;
    }

    private static void Settle(MicrophoneSpectrumControl meter, MicrophoneSpectrumSnapshot? s, ref double t, double seconds = 0.2)
    {
        for (double end = t + seconds; t < end; t += 0.05) meter.Advance(s, t);
    }

    // ── layout ───────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Meter_HasANonZeroDrawingArea()
    {
        var (window, meter) = Host();
        try
        {
            Assert.True(meter.Bounds.Width > 100 && meter.Bounds.Height >= 40, $"{meter.Bounds}");
            Rect area = MicrophoneSpectrumControl.GetBarArea(meter.Bounds.Size);
            Assert.True(area.Width > 0 && area.Height > 20, $"{area}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Bars_AreOrderedGappedAndBottomAligned()
    {
        var size = new Size(420, 68);
        var levels = Enumerable.Range(0, 40).Select(i => i / 39.0).ToArray();
        var bars = MicrophoneSpectrumControl.ComputeBars(size, levels);
        Assert.Equal(40, bars.Count);

        double width = bars[0].Slot.Width;
        double gap = bars[1].Slot.X - bars[0].Slot.Right;
        Assert.True(gap > 0, "bars must have gaps");
        for (int i = 0; i < bars.Count; i++)
        {
            Assert.Equal(i, bars[i].BandIndex);
            Assert.Equal(width, bars[i].Slot.Width, 6);                        // narrow, equal bars
            Assert.Equal(bars[0].Slot.Bottom, bars[i].Slot.Bottom, 6);         // common baseline
            if (i > 0) Assert.Equal(gap, bars[i].Slot.X - bars[i - 1].Slot.Right, 6);
            if (bars[i].Segments.Count > 0)
            {
                Assert.Equal(bars[i].Slot.Bottom, bars[i].Segments[0].Rect.Bottom, 6);   // grows from the bottom
                for (int k = 1; k < bars[i].Segments.Count; k++)
                    Assert.Equal(bars[i].Segments[k - 1].Rect.Top, bars[i].Segments[k].Rect.Bottom, 6);
                Assert.Equal(bars[i].Slot.Bottom - levels[i] * bars[i].Slot.Height, bars[i].LitTop, 6);
            }
        }
        Assert.True(bars[^1].Slot.Right <= size.Width - MicrophoneSpectrumControl.HorizontalPadding + 1e-6);
        Assert.True(bars[0].Slot.Height > bars[0].Slot.Width * 3, "bars are tall, not squat");
    }

    [AvaloniaFact]
    public void LevelZones_SelectGreenThenWarningThenRed()
    {
        var size = new Size(420, 68);
        var bars = MicrophoneSpectrumControl.ComputeBars(size, new[] { 0.0, 0.5, 0.8, 1.0 });

        Assert.Empty(bars[0].Segments);
        Assert.Equal(new[] { SpectrumZone.Normal }, bars[1].Segments.Select(s => s.Zone));
        Assert.Equal(new[] { SpectrumZone.Normal, SpectrumZone.Warning }, bars[2].Segments.Select(s => s.Zone));
        Assert.Equal(new[] { SpectrumZone.Normal, SpectrumZone.Warning, SpectrumZone.Peak }, bars[3].Segments.Select(s => s.Zone));

        // Thresholds are dBFS: warning from -18, red from -6 on the -60..0 scale.
        Assert.Equal(0.7, MicrophoneSpectrumControl.WarningLevel, 9);
        Assert.Equal(0.9, MicrophoneSpectrumControl.PeakLevel, 9);
        var full = bars[3];
        double h = full.Slot.Height;
        Assert.Equal(full.Slot.Bottom - 0.7 * h, full.Segments[0].Rect.Top, 6);
        Assert.Equal(full.Slot.Bottom - 0.9 * h, full.Segments[1].Rect.Top, 6);
        Assert.Equal(full.Slot.Top, full.Segments[2].Rect.Top, 6);
    }

    [AvaloniaFact]
    public void Silence_DrawsEmptyBars()
    {
        var (window, meter) = Host();
        try
        {
            double t = 0;
            Settle(meter, SilenceSnapshot(), ref t, 1.0);
            Assert.All(meter.DisplayedLevels, l => Assert.Equal(0.0, l));
            Assert.All(MicrophoneSpectrumControl.ComputeBars(meter.Bounds.Size, meter.DisplayedLevels), b => Assert.Empty(b.Segments));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LowAndHighTones_IlluminateDifferentHorizontalPositions()
    {
        var (window, meter) = Host();
        try
        {
            double t = 0;
            Settle(meter, ToneSnapshot(150, 0.05), ref t);
            var low = Lit(meter);
            meter.Clear();
            Settle(meter, ToneSnapshot(6000, 0.05), ref t);
            var high = Lit(meter);

            Assert.NotEmpty(low);
            Assert.NotEmpty(high);
            Assert.True(low.Max(b => b.Slot.Right) < high.Min(b => b.Slot.Left),
                $"low lit {low.Min(b => b.BandIndex)}..{low.Max(b => b.BandIndex)}, high lit {high.Min(b => b.BandIndex)}..{high.Max(b => b.BandIndex)}");
        }
        finally { window.Close(); }

        static List<SpectrumBarGeometry> Lit(MicrophoneSpectrumControl m)
            => MicrophoneSpectrumControl.ComputeBars(m.Bounds.Size, m.DisplayedLevels).Where(b => b.Level > 0.2).ToList();
    }

    [AvaloniaFact]
    public void Resizing_ReflowsBarsInsideTheNewBounds()
    {
        var (window, meter) = Host(width: 420);
        try
        {
            double t = 0;
            Settle(meter, ToneSnapshot(1000, 0.5), ref t);
            var wide = MicrophoneSpectrumControl.ComputeBars(meter.Bounds.Size, meter.DisplayedLevels);

            meter.Width = 140;    // the transport row's MinWidth=140 track at the 980px window floor
            window.UpdateLayout();
            Assert.Equal(140, meter.Bounds.Width, 3);
            var narrow = MicrophoneSpectrumControl.ComputeBars(meter.Bounds.Size, meter.DisplayedLevels);

            Assert.Equal(wide.Count, narrow.Count);
            Assert.True(narrow[0].Slot.Width < wide[0].Slot.Width);
            Assert.True(narrow[0].Slot.Width >= 1.0, "bars stay visible at the minimum width");
            Assert.All(narrow, b => Assert.True(b.Slot.Right <= meter.Bounds.Width + 1e-6 && b.Slot.Left >= 0));
            Assert.Equal(wide.FindIndexOf(b => b.Level == wide.Max(x => x.Level)),
                         narrow.FindIndexOf(b => b.Level == narrow.Max(x => x.Level)));
        }
        finally { window.Close(); }
    }

    // ── theme ────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Brushes_ResolveInTheControlsOwnThemeContext()
    {
        var (window, meter) = Host();
        try
        {
            foreach (var (key, (dark, _)) in Tokens)
                Assert.Equal(dark, Assert.IsAssignableFrom<ISolidColorBrush>(meter.ResolveBrush(key)).Color);

            window.RequestedThemeVariant = ThemeVariant.Light;
            window.UpdateLayout();
            foreach (var (key, (_, light)) in Tokens)
                Assert.Equal(light, Assert.IsAssignableFrom<ISolidColorBrush>(meter.ResolveBrush(key)).Color);

            Assert.Equal(MicrophoneSpectrumControl.PeakBrushKey, MicrophoneSpectrumControl.ZoneBrushKey(SpectrumZone.Peak));
            Assert.Equal(MicrophoneSpectrumControl.WarningBrushKey, MicrophoneSpectrumControl.ZoneBrushKey(SpectrumZone.Warning));
            Assert.Equal(MicrophoneSpectrumControl.NormalBrushKey, MicrophoneSpectrumControl.ZoneBrushKey(SpectrumZone.Normal));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ProductionThemes_DefineEveryMeterTokenInBothVariants()
    {
        string path = Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "AvaloniaApp.axaml");
        var doc = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string variant in new[] { "Dark", "Light" })
        {
            var dict = doc.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == variant);
            var keys = dict.Elements().Select(e => (string?)e.Attribute(x + "Key")).ToHashSet();
            foreach (string key in Tokens.Keys)
                Assert.True(keys.Contains(key), $"{variant} theme is missing {key}");
        }
    }

    [Fact]
    public void RangeLabels_ComeFromTheRealBandEdges()
    {
        var bands = new MicrophoneSpectrumAnalyzer().Bands;
        var (low, mid, high) = MicrophoneSpectrumControl.FormatRangeLabels(bands);
        Assert.Equal("LOW · 60 Hz", low);
        Assert.Equal("1 kHz", mid);
        Assert.Equal("HIGH · 16 kHz", high);
    }

    [Fact]
    public void NoMeterAmplitudeComesFromARandomNumber()
    {
        var files = new[]
        {
            Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Controls", "MicrophoneSpectrumControl.cs"),
            Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.Core", "Media", "MicrophoneSpectrumAnalyzer.cs"),
        }.Concat(Directory.GetFiles(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App"), "VoiceOverWindow*.cs"));
        var pattern = new System.Text.RegularExpressions.Regex(@"\bnew\s+(System\.)?Random\b|\bRandom\.Shared\b|\.NextDouble\s*\(|_eqRandom");
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Assert.False(pattern.IsMatch(text), $"{Path.GetFileName(file)} draws meter amplitudes from a random number (SPECTRUM_04)");
        }
    }

    // ── motion only from fresh input ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void StaleSnapshot_DecaysToEmpty_AndStopsAnimating()
    {
        var (window, meter) = Host();
        try
        {
            var tone = ToneSnapshot(800, 0.5);
            double t = 0;
            Settle(meter, tone, ref t);
            Assert.True(meter.DisplayedLevels.Max() > 0.6);

            // The same snapshot keeps being offered (callbacks stopped): bars must fall, then stop.
            Settle(meter, tone, ref t, 3.0);
            Assert.All(meter.DisplayedLevels, l => Assert.Equal(0.0, l));
            Assert.True(meter.IsSettled);

            int repaints = meter.InvalidationCount;
            Settle(meter, tone, ref t, 2.0);
            Settle(meter, null, ref t, 2.0);
            Assert.Equal(repaints, meter.InvalidationCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FailureInput_DecaysMonotonically_WithoutMotion()
    {
        var (window, meter) = Host();
        try
        {
            double t = 0;
            Settle(meter, ToneSnapshot(2500, 0.5), ref t);
            double previous = meter.DisplayedLevels.Max();
            Assert.True(previous > 0.6);
            for (int i = 0; i < 60; i++)
            {
                meter.Advance(null, t += 0.05);   // the window passes null once the source faults
                Assert.True(meter.DisplayedLevels.Max() <= previous + 1e-12);
                previous = meter.DisplayedLevels.Max();
            }
            Assert.Equal(0.0, previous);
        }
        finally { window.Close(); }
    }

    // ── the studio wiring ────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task VoiceOverWindow_FeedsTheMeterFromTheSession_AndDropsItOnFault()
    {
        var devices = new SpectrumDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        try
        {
            var meter = window.SpectrumMeterControl;
            Assert.NotNull(meter);

            await session.StartMonitorAsync(0).WaitAsync(TimeSpan.FromSeconds(10));
            window.TriggerUpdateSpectrumMeter();
            Assert.Null(meter!.LastInput);                    // open but nothing heard: no fake motion

            devices.Monitor!.RaisePcm(ToneSnapshotPcm(700));
            window.TriggerUpdateSpectrumMeter();
            Assert.Same(session.LatestSpectrum, meter.LastInput);
            Assert.NotNull(meter.LastInput);

            devices.Monitor.RaiseStopped(new InvalidOperationException("unplugged"));
            window.TriggerUpdateSpectrumMeter();
            Assert.Null(meter.LastInput);
        }
        finally { window.Close(); }
    }

    private static byte[] ToneSnapshotPcm(double hz)
    {
        var bytes = new byte[8192 * 2];
        for (int n = 0; n < 8192; n++)
        {
            short s = (short)Math.Round(0.4 * 32767 * Math.Sin(2 * Math.PI * hz * n / 44100.0));
            bytes[2 * n] = (byte)(s & 0xFF);
            bytes[2 * n + 1] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }

    private sealed class SpectrumDevices : IVoiceCaptureDeviceFactory
    {
        public SpectrumMonitor? Monitor { get; private set; }
        public bool HasInputDevice => true;
        public int DeviceCount => 1;
        public IReadOnlyList<string> GetDeviceNames() => new[] { "Fake Microphone" };
        public IMicMonitorDevice CreateMonitor() => Monitor = new SpectrumMonitor();
        public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber)
            => throw new InvalidOperationException("not used");
    }

    private sealed class SpectrumMonitor : IMicMonitorDevice
    {
        public event EventHandler<float>? LevelChanged;
        public event EventHandler<Exception?>? Stopped;
        public event EventHandler<PcmBuffer>? PcmAvailable;
        public bool IsRunning { get; private set; }
        public int BuffersSeen { get; private set; }
        public void Start(int deviceNumber) => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
        public void RaiseStopped(Exception ex) { IsRunning = false; Stopped?.Invoke(this, ex); }
        public void RaisePcm(byte[] data)
        {
            BuffersSeen++;
            LevelChanged?.Invoke(this, 0.4f);
            PcmAvailable?.Invoke(this, new PcmBuffer(data, data.Length, PcmCaptureFormat.ProductionMicrophone));
        }
    }
}

internal static class SpectrumTestListExtensions
{
    public static int FindIndexOf<T>(this IReadOnlyList<T> list, Func<T, bool> match)
    {
        for (int i = 0; i < list.Count; i++) if (match(list[i])) return i;
        return -1;
    }
}
