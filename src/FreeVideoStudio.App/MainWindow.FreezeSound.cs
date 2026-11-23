namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private readonly Services.FreezeSoundPreview _freezeSound = new();

    private double LiveGameBalance() =>
        (_musicWizardResult != null && _isMusicActive ? _musicWizardResult.VideoVolume : 1.0) * PreviewGameDuckGain();

    private void StartLiveFreezeSound()
    {
        if (PreviewMixActive) return;
        _freezeSound.PlayHold(_freezeDurationS, _loadedVideoPath, LiveGameBalance(), LiveFreezeTailRoomSec());
    }

    private double? LiveFreezeTailRoomSec()
    {
        double startMs = _trimStartMs, endMs = PreviewVideoEndMs();
        if (endMs <= startMs) return null;
        var timeline = FreeVideoStudio.Core.Media.PreviewTimeMap.Create(startMs, endMs, _viewModel.Timeline.BuildExportSpeedSegments(), _baseSpeed, _cuts);
        var ranges = timeline.FreezeOutputRanges();
        if (ranges.Count == 0) return null;
        return System.Math.Max(0, timeline.TotalOutputSeconds - ranges[0].EndOutputSec);
    }
}
