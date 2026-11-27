using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    internal const string MergerPreviewTag = "WIZMERGEPREVIEW_01";

    public CompositeTimeline? MergerComposite { get; set; }

    public IReadOnlyList<(double StartSec, double EndSec)>? MergerClipWindows { get; set; }

    private double? MergerWindowDuration(int index)
        => _isMergerMode && MergerClipWindows != null && index >= 0 && index < MergerClipWindows.Count
           && MergerClipWindows[index].EndSec > MergerClipWindows[index].StartSec
            ? MergerClipWindows[index].EndSec - MergerClipWindows[index].StartSec
            : null;

    private double MergerWindowStart(int index)
        => _isMergerMode && MergerClipWindows != null && index >= 0 && index < MergerClipWindows.Count
            ? MergerClipWindows[index].StartSec
            : 0;

    private string Phase3PreviewUrl()
    {
        if (!_isMergerMode || _mergerVideos is not { Count: > 0 } videos) return _videoPath;
        if (MergerClipWindows is { } w && w.Count == videos.Count) return MergerPreviewEdl(videos, w);
        if (MergerComposite is { Clips.Count: > 0 } comp) return MergeEditorSource.Build(comp).MpvUrl;
        return MergerPreviewEdl(videos, null);
    }

    internal static string MergerPreviewEdl(IReadOnlyList<string> videos, IReadOnlyList<(double StartSec, double EndSec)>? windows)
    {
        var ci = CultureInfo.InvariantCulture;
        var url = new StringBuilder("edl://");
        bool first = true;
        for (int i = 0; i < videos.Count; i++)
        {
            string path = videos[i];
            if (string.IsNullOrEmpty(path)) continue;
            bool hasWindow = windows != null && i < windows.Count && windows[i].EndSec > windows[i].StartSec;
            if (windows != null && !hasWindow) continue;
            if (!first) url.Append(';');
            first = false;
            url.Append('%').Append(Encoding.UTF8.GetByteCount(path).ToString(ci)).Append('%').Append(path);
            if (hasWindow)
                url.Append(',').Append(windows![i].StartSec.ToString("0.######", ci))
                   .Append(',').Append((windows[i].EndSec - windows[i].StartSec).ToString("0.######", ci));
        }
        return url.ToString();
    }
}
