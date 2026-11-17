using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
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
}
