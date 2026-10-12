
using System.Collections.Generic;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private Infrastructure.CornerMemeOverlayPresenter? _cornerMemes;
    private object? _cornerMemePlan;

    /// <summary>
    /// MEMEMODE_01 — corner memes in the Merger preview, on the plan's GAMEPLAY clock
    /// (<see cref="Core.Media.MergerPreviewPlan.GameplaySecAt"/>). Hidden while a full-screen cutaway
    /// owns the player; never pauses, seeks or swaps it.
    /// </summary>
    private void UpdateMergerCornerMemes(bool cutawayActive)
    {
        var plan = _plan;
        if (plan == null || plan.CornerOverlays.Count == 0 || cutawayActive || _videoHost?.IpcClient == null || _edlUrl == null)
        {
            _cornerMemes?.Hide();
            return;
        }

        _cornerMemes ??= new Infrastructure.CornerMemeOverlayPresenter();
        _cornerMemes.Attach(_videoHost);
        int fw = _videoHost.IpcClient.VideoWidth, fh = _videoHost.IpcClient.VideoHeight;
        if (fw > 1 && fh > 1) _cornerMemes.SetFrameSize(fw - fw % 2, fh - fh % 2);
        if (!ReferenceEquals(_cornerMemePlan, plan))
        {
            _cornerMemePlan = plan;
            var spans = new List<Infrastructure.CornerMemeSpan>(plan.CornerOverlays.Count);
            foreach (var c in plan.CornerOverlays) spans.Add(new Infrastructure.CornerMemeSpan(c.Meme, c.GameStartSec, c.GameEndSec));
            _cornerMemes.SetSpans(spans);
        }
        _cornerMemes.Update(plan.GameplaySecAt(_videoHost.IpcClient.CurrentTime));
    }
}
