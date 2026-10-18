namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow
{
    /// <summary>
    /// SCRAPER_05 — "Skip cover pictures" (Settings › Editing › Video Merger). Pending in the
    /// view-model like every other value; the Video Merger's own bottom-left checkbox edits the same flag.
    /// </summary>
    public bool MergerThumbnailScraper { get => Vm.MergerThumbnailScraper; set => Vm.MergerThumbnailScraper = value; }

    /// <summary>CLIPLEVEL_01 — "Even out the volume between clips" (Settings › Editing › Video Merger).</summary>
    public bool MergerMatchClipLoudness { get => Vm.MergerMatchClipLoudness; set => Vm.MergerMatchClipLoudness = value; }
}
