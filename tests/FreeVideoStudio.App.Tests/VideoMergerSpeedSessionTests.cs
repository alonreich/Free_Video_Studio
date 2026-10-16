// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SPEEDRESTORE_01 / MERGESESSION_01 — verifies that VideoMergerWindow initializes at 1.0x (detent 10),
/// that speed presets synchronize the UI slider and properties, and that session restore repairs
/// corrupted legacy 0.1x base speed to 1.0x instead of executing 10x slow motion preview/export.
/// </summary>
public sealed class VideoMergerSpeedSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsMergerSpeedTests_" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _programData;

    public VideoMergerSpeedSessionTests()
    {
        _programData = LiveCaptureDriver.IsolateProgramData(_root);
    }

    public void Dispose()
    {
        _programData.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [AvaloniaFact]
    public void WindowInitialization_MainSpeedSlider_DefaultsTo10_AndBaseSpeedIs1()
    {
        var window = new VideoMergerWindow();
        try
        {
            var speedSlider = window.FindControl<SpinningWheelSlider>("MainSpeedSlider");
            Assert.NotNull(speedSlider);
            Assert.Equal(10, speedSlider.Value);
            Assert.Equal(10.0, window.MainSpeedSliderValue);
            Assert.Equal(1.0, window.BaseSpeedForTesting);

            var qualitySlider = window.FindControl<SpinningWheelSlider>("QualitySlider");
            Assert.NotNull(qualitySlider);
            Assert.Equal(19, qualitySlider.Value);
            Assert.Equal(19.0, window.QualitySliderValue);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ApplySpeedPreset_UpdatesBaseSpeedAndSliderAndStyledProperty()
    {
        var window = new VideoMergerWindow();
        try
        {
            var speedSlider = window.FindControl<SpinningWheelSlider>("MainSpeedSlider");
            Assert.NotNull(speedSlider);

            window.ApplySpeedPresetForTesting(2.0);

            Assert.Equal(2.0, window.BaseSpeedForTesting);
            Assert.Equal(20, speedSlider.Value);
            Assert.Equal(20.0, window.MainSpeedSliderValue);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SessionRestore_Corrupted01BaseSpeed_IsRepairedToDefault10()
    {
        var window = new VideoMergerWindow();
        try
        {
            var dummyClip = new EdlClip
            {
                ClipId = Guid.NewGuid(),
                Path = Path.Combine(_root, "test.mp4"),
                DurationUs = 10_000_000
            };
            var edl = new MergeEdl
            {
                Clips = new[] { dummyClip },
                BaseSpeed = 0.1 // legacy corrupted speed
            };

            var plan = new MergerRestorePlan(edl, Array.Empty<string>(), Array.Empty<string>());
            await window.ApplyRestorePlanForTesting(plan);

            Assert.Equal(1.0, window.BaseSpeedForTesting);
            var speedSlider = window.FindControl<SpinningWheelSlider>("MainSpeedSlider");
            Assert.NotNull(speedSlider);
            Assert.Equal(10, speedSlider.Value);
            Assert.Equal(10.0, window.MainSpeedSliderValue);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SessionRestore_ExplicitValidBaseSpeed_IsPreserved()
    {
        var window = new VideoMergerWindow();
        try
        {
            var dummyClip = new EdlClip
            {
                ClipId = Guid.NewGuid(),
                Path = Path.Combine(_root, "test2.mp4"),
                DurationUs = 10_000_000
            };
            var edl = new MergeEdl
            {
                Clips = new[] { dummyClip },
                BaseSpeed = 1.5 // deliberate 1.5x speed
            };

            var plan = new MergerRestorePlan(edl, Array.Empty<string>(), Array.Empty<string>());
            await window.ApplyRestorePlanForTesting(plan);

            Assert.Equal(1.5, window.BaseSpeedForTesting);
            var speedSlider = window.FindControl<SpinningWheelSlider>("MainSpeedSlider");
            Assert.NotNull(speedSlider);
            Assert.Equal(15, speedSlider.Value);
            Assert.Equal(15.0, window.MainSpeedSliderValue);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }
}
