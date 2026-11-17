using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Layout;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private ListBox? _cVideoList;
    private ListBox? VideoListCtl => _cVideoList ??= this.FindControl<ListBox>("VideoList");
    private FreeVideoStudio.App.Controls.PhaseOverlayControl? _cOverlayLayer;
    private FreeVideoStudio.App.Controls.PhaseOverlayControl? OverlayLayerCtl => _cOverlayLayer ??= this.FindControl<FreeVideoStudio.App.Controls.PhaseOverlayControl>("OverlayLayer");
    private MenuItem? _cMenuOutputFolder;
    private MenuItem? MenuOutputFolderCtl => _cMenuOutputFolder ??= this.FindControl<MenuItem>("MenuOutputFolder");
    private Button? _cAddMusicButton;
    private Button? AddMusicButtonCtl => _cAddMusicButton ??= this.FindControl<Button>("AddMusicButton");
    private Slider? _cVolumeSlider;
    private Slider? VolumeSliderCtl => _cVolumeSlider ??= this.FindControl<Slider>("VolumeSlider");
    private CheckBox? _cScraperCheckBox;
    private CheckBox? ScraperCheckBoxCtl => _cScraperCheckBox ??= this.FindControl<CheckBox>("ThumbnailScraperCheckBox");
    private Canvas? _cMarkersCanvas;
    private Canvas? MarkersCanvasCtl => _cMarkersCanvas ??= this.FindControl<Canvas>("TimelineMarkersCanvas");
    private Canvas? _cFilmLane;
    private Canvas? FilmLaneCtl => _cFilmLane ??= this.FindControl<Canvas>("MergerFilmstripLane");
    private Canvas? _cWaveLane;
    private Canvas? WaveLaneCtl => _cWaveLane ??= this.FindControl<Canvas>("MergerWaveformLane");
    private Canvas? _cBlocksLane;
    private Canvas? BlocksLaneCtl => _cBlocksLane ??= this.FindControl<Canvas>("MergerClipBlocksLane");
    private Canvas? _cScaleCanvas;
    private Canvas? ScaleCanvasCtl => _cScaleCanvas ??= this.FindControl<Canvas>("TimelineScaleCanvas");

    private static readonly Avalonia.Input.DataFormat<string> VideoItemFormat = Avalonia.Input.DataFormat.CreateStringApplicationFormat("fvs.merger.video-item");
}
