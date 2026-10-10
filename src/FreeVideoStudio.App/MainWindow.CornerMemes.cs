// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private Infrastructure.CornerMemeOverlayPresenter? _cornerMemes;
    private string _cornerMemeKey = "";

    /// <summary>
    /// MEMEMODE_01 — corner memes in the main preview. Runs on the playback tick AFTER the cutaway
    /// director, on the GAMEPLAY clock (<see cref="ViewModels.TimelineViewModel.PreviewSourceToOutputSeconds"/>,
    /// which never counts memes — 01 TL-OUTPUTTIMELINE double-counting guard). It never pauses,
    /// seeks or swaps the player.
    /// </summary>
    private void UpdateCornerMemeOverlay()
    {
        var corners = MemePlacement.CornerOnly(_memePlacements);
        var host = ActiveVideoHost;
        if (corners.Count == 0 || host?.IpcClient == null)
        {
            _cornerMemes?.Hide();
            return;
        }

        _cornerMemes ??= new Infrastructure.CornerMemeOverlayPresenter();
        _cornerMemes.Attach(host);
        bool portrait = PortraitModeCheckboxCtl?.IsChecked ?? true;
        if (portrait) _cornerMemes.SetFrameSize(CoordinateConstants.PortraitW, CoordinateConstants.PortraitH);
        else
        {
            // CORNERPARITY_01 — the export overlays onto the EVEN SOURCE size (ProcessWorker), and the box,
            // the margin and the even rounding are pixel quantities of THAT frame. A 16x9 stand-in made the
            // margin round to 0 and the box to whole ninths of the height (measured on 1920x1080, Medium:
            // preview box 4/9 = 480 px with no margin vs. export 454 px inset 32 px).
            int w = host.IpcClient.VideoWidth, h = host.IpcClient.VideoHeight;
            if (w > 1 && h > 1) _cornerMemes.SetFrameSize(w - w % 2, h - h % 2);
            else _cornerMemes.SetFrameSize(1920, 1080);
        }

        var tl = _viewModel.Timeline;
        double startMs = tl.IsTrimStartSet ? tl.TrimStartMs : 0.0;
        double endMs = tl.IsTrimEndSet && tl.TrimEndMs > startMs ? tl.TrimEndMs : tl.LoadedVideoDurationMs;
        double total = tl.PreviewSourceToOutputSeconds(endMs);

        var spans = new List<Infrastructure.CornerMemeSpan>(corners.Count);
        var key = new System.Text.StringBuilder();
        foreach (var m in corners)
        {
            double anchor = tl.PreviewSourceToOutputSeconds(startMs + m.AtSourceSecRelative * 1000.0);
            if (MemePlacement.VisibleInterval(anchor, m.DurationSec, total) is not { } v) continue;
            spans.Add(new Infrastructure.CornerMemeSpan(m, v.StartSec, v.EndSec));
            key.Append(m).Append('@').Append(v.StartSec.ToString("F3")).Append(';');
        }
        string k = key.ToString();
        if (k != _cornerMemeKey) { _cornerMemeKey = k; _cornerMemes.SetSpans(spans); }

        // CORNERPARITY_01 — the FREEZE-AWARE gameplay clock: during a held freeze the export's corner meme
        // keeps playing over the held frame (it is overlaid on the rendered stream), so its clock must keep
        // running here too instead of stopping with the paused player.
        _cornerMemes.Update(PreviewOutputSeconds(host.IpcClient.CurrentTime));
    }
}
