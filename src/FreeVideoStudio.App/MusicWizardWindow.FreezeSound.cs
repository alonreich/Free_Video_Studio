using System;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    private readonly Services.FreezeSoundPreview _freezeSound = new();
    private double _phase3FreezeSoundAt = double.NaN;

    private string? Phase3FreezeLoudnessPath => _isMergerMode ? null : _videoPath;

    private FreeVideoStudio.Core.Media.OutputTimeline Phase3OutputTimeline() =>
        FreeVideoStudio.Core.Media.OutputTimeline.Create(
            GetPhase3SourceDurationSeconds() * 1000.0,
            _phase3SpeedSegments,
            _phase3BaseSpeed,
            _trimStartMs,
            FreeVideoStudio.Core.Media.MemePlacement.ToInsertions(_phase3Memes),
            FreeVideoStudio.Core.Media.CutRange.ToClipRelative(_phase3Cuts, _trimStartMs));

    private void UpdatePhase3FreezeSound(double outputSec)
    {
        var timeline = Phase3OutputTimeline();
        foreach (var range in timeline.FreezeOutputRanges())
        {
            if (outputSec < range.StartOutputSec - 0.001 || outputSec >= range.EndOutputSec) continue;
            if (_phase3FreezeSoundAt.Equals(range.StartOutputSec)) return;
            _phase3FreezeSoundAt = range.StartOutputSec;
            _freezeSound.PlayHold(range.EndOutputSec - range.StartOutputSec, Phase3FreezeLoudnessPath, GetPreviewVideoBalance(),
                Math.Max(0, timeline.TotalOutputSeconds - range.EndOutputSec), Math.Max(0, outputSec - range.StartOutputSec));
            return;
        }
        _phase3FreezeSoundAt = double.NaN;
    }

    private void StopPhase3FreezeSound()
    {
        _phase3FreezeSoundAt = double.NaN;
        _freezeSound.Stop();
    }
}
