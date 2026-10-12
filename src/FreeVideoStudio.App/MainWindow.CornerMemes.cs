
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

        _cornerMemes.Update(PreviewOutputSeconds(host.IpcClient.CurrentTime));
    }
}
