// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow
{
    /// <summary>
    /// SCRAPER_05 — "Skip cover pictures" (Defaults tab). Seeded from settings when the window is
    /// built and written back by SaveAndClose, exactly like every other checkbox here. The Video
    /// Merger's own bottom-left checkbox edits the same flag.
    /// </summary>
    public bool MergerThumbnailScraper { get; set; } = SettingsManager.Instance.MergerThumbnailScraper;

    /// <summary>CLIPLEVEL_01 — "Even out the volume between clips" (Defaults tab, Video Merger).</summary>
    public bool MergerMatchClipLoudness { get; set; } = SettingsManager.Instance.MergerMatchClipLoudness;
}
