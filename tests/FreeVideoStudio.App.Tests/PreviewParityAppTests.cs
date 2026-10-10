// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>PREVIEWMIX_02 / CORNERPARITY_01 / AUD-MASTERVOL — the App-side halves of preview/export parity.</summary>
public sealed class PreviewParityAppTests
{
    /// <summary>Every file that composes or builds the export (Main App), and the preview-mix path that reuses it.</summary>
    private static readonly string[] ExportPath =
    {
        "src/FreeVideoStudio.App/MainWindow.Export.cs",
        "src/FreeVideoStudio.App/Models/ExportPayload.cs",
        "src/FreeVideoStudio.App/Services/MainMediaController.cs",
        "src/FreeVideoStudio.Core/Media/ProcessWorker.cs",
        "src/FreeVideoStudio.Core/Media/AudioFilterChain.cs",
        "src/FreeVideoStudio.Core/Media/AudioGraphPruner.cs",
        "src/FreeVideoStudio.Core/Media/GranularSpeedBuilder.cs",
        "src/FreeVideoStudio.Core/Media/CornerMemeOverlayGraph.cs",
        "src/FreeVideoStudio.Core/Media/PeakSafety.cs",
        "src/FreeVideoStudio.Core/Media/AudioTempoFilterBuilder.cs",
    };

    [Fact]
    public void PreviewMasterVolume_NeverReachesTheExportPayloadOrGraph()
    {
        var master = new Regex(@"\b(GlobalMasterVolume|GlobalMuted|MasterLinearGain|PerceptualGain|PlayerMpvVolume|ApplyPreviewGainAsync|MasterVolumeUi|VolumeSlider)\b");
        foreach (string rel in ExportPath)
        {
            string path = RepoRoot.SourcePath(rel.Split('/'));
            string code = string.Join("\n", File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));
            Assert.False(master.IsMatch(code), $"{rel} references the preview master volume: {master.Match(code).Value}");
        }
    }

    [Fact]
    public void TheRenderedMix_IsComposedByTheExportsOwnPayloadBuilder()
    {
        string mix = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.PreviewMix.cs"));
        Assert.Contains("ComposeExportPayloadAsync(", mix);
        Assert.Contains("MainMediaController.RenderAudioPreviewAsync(", mix);
        Assert.Contains("_mixGate.TryComplete(", mix);   // PREVIEWMIX_02 — completion goes through the gate
        Assert.DoesNotContain("af=", mix.Replace("\"af\"", ""));   // the mix player carries no filter of its own
    }

    private static CornerMemeSpan Span(string id, double s, double e) =>
        new(new FreeVideoStudio.Core.Media.MemePlacement($"{id}.mp4", 0, e - s, id, MemePresentationMode.CornerOverlay), s, e);

    [Fact]
    public void SimultaneousCornerMemes_AreAllShown_InExportZOrder()
    {
        var spans = new[] { Span("a", 1, 6), Span("b", 3, 8), Span("c", 9, 10) };
        Assert.Equal(new[] { "a", "b" }, CornerMemeOverlayPresenter.ActiveAt(spans, 4.0).Select(s => s.Meme.Id));
        Assert.Equal(new[] { "b" }, CornerMemeOverlayPresenter.ActiveAt(spans, 6.0).Select(s => s.Meme.Id));   // half-open end
        Assert.Empty(CornerMemeOverlayPresenter.ActiveAt(spans, 8.5));
    }
}
