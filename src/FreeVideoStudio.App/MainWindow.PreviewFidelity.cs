// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Linq;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// PREVIEWFIDELITY_01 — the Main App preview says when it is not the export (<see cref="PreviewFidelity"/>).
/// Re-evaluated on the rendered-mix scheduler tick (250 ms), so it costs nothing per frame; the marker
/// changes only when the set of differences changes, and every change is logged once.
/// </summary>
public partial class MainWindow
{
    private Infrastructure.PreviewFidelityBadge? _fidelityBadge;
    private string? _fidelityColorPath;
    private VideoColorInfo? _fidelityColor;

    private void UpdatePreviewFidelity()
    {
        var host = ActiveVideoHost;
        if (host == null || string.IsNullOrEmpty(_loadedVideoPath))
        {
            _fidelityBadge?.Hide();
            return;
        }

        EnsureFidelityColorProbe(_loadedVideoPath!);

        bool portrait = PortraitModeCheckboxCtl?.IsChecked == true;
        var segments = _speedSegments;
        bool hasZoom = segments.Any(s => s.ZoomW.HasValue && s.ZoomH.HasValue && !string.IsNullOrEmpty(s.ZoomOrigRes));
        var corners = MemePlacement.CornerOnly(_memePlacements);

        var issues = PreviewFidelity.Evaluate(new PreviewFidelityInputs
        {
            GpuCropPreview = VideoRenderMode.Current.UseHardwareAcceleration,
            HasZoom = hasZoom,
            Portrait = portrait,
            ZoomEdgePadding = hasZoom && ZoomPreviewSimulator.AnyEdgePadding(segments, portrait),
            SourceColor = _fidelityColor,
            RenderedMixFailed = _mixGate.CurrentSignature != null && _mixGate.CurrentSignature == _mixGate.FailedSignature,
            CornerMemeBeyondPreviewFrames = corners.Any(c => !IsStillImage(c.FilePath) && c.DurationSec > Infrastructure.CornerMemeFrames.MaxSeconds),
            // The rendered mix carries corner-meme sound in this window (PREVIEWMIX_01); a failed mix is reported above.
            CornerMemeSoundNotPreviewed = false,
        });

        _fidelityBadge ??= new Infrastructure.PreviewFidelityBadge();
        _fidelityBadge.Attach(host);
        if (_fidelityBadge.Show(issues))
        {
            RuntimeLog.Info("PreviewFidelity", issues.Count == 0
                ? "Preview matches the export."
                : "Preview differs from the export: " + string.Join(", ", issues.Select(i => i.Code)));
        }
    }

    private static bool IsStillImage(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg";
    }

    /// <summary>Reads the source's colour tags once per loaded file, off the UI thread (one ffprobe).</summary>
    private void EnsureFidelityColorProbe(string path)
    {
        if (string.Equals(_fidelityColorPath, path, StringComparison.OrdinalIgnoreCase)) return;
        _fidelityColorPath = path;
        _fidelityColor = null;
        _ = Task.Run(async () =>
        {
            try
            {
                string ffprobe = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
                var info = await new MediaProber(ffprobe, path).GetVideoColorInfoAsync().ConfigureAwait(false);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (string.Equals(_fidelityColorPath, path, StringComparison.OrdinalIgnoreCase)) _fidelityColor = info;
                });
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }
}
