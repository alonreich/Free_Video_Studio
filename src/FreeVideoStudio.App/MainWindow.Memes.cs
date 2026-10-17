// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Media;
using CoreMeme = FreeVideoStudio.Core.Media.MemePlacement;

namespace FreeVideoStudio.App;

/// <summary>
/// MEMEAUDIT_01 — the main screen's side of the meme audit:
/// <list type="bullet">
/// <item>MEMEMAIN_02 — the panel's "Add MEME" (START / END) meme is a meme like any other: it plays
/// TOGETHER with the Speed Editor's memes (it used to be dropped from the export without a word the
/// moment the editor held one), is drawn on the timeline and is played by the preview.</item>
/// <item>MEMEMARK_02 — the markers: a full-screen meme is one clown head at the gameplay moment it
/// interrupts; a corner meme is a strip over the gameplay it plays across (it used to wear the
/// full-screen head and the "gameplay pauses here" text). Clicking either opens the Speed Editor on it.</item>
/// <item>MEMEMUSIC_02 — "keep the music under the meme?" is asked for the editor's memes too.</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    /// <summary>Id of the main-screen START/END meme when it travels with the editor's memes.</summary>
    public const string MainScreenMemeId = "mainmeme";

    private string? _legacyMemeDurationPath;
    private double _legacyMemeDurationSec;
    private bool _legacyMemeProbing;

    /// <summary>
    /// MEMEMAIN_02 — the panel's START/END meme as a placement (full screen, at the clip start or end),
    /// or null. Its length is the picture's 4 s or the video's probed length (0 until probed; the
    /// export probes it itself, the preview waits for it).
    /// </summary>
    private CoreMeme? MainScreenMemePlacement()
    {
        string? file = SelectedLegacyMemeFile();
        if (file == null) return null;
        bool atStart = Infrastructure.MemePlacementStore.Get(file) == Infrastructure.MemePlacement.Start;
        double startMs = _trimStartSet ? _trimStartMs : 0;
        double endMs = _trimEndMs > startMs ? _trimEndMs : _loadedVideoDurationMs;
        double clipLenSec = Math.Max(0, (endMs - startMs) / 1000.0);
        double dur = Infrastructure.CornerMemeFrames.IsStillImage(file) ? CoreMeme.StillImageDurationSec : 0;
        if (dur <= 0)
        {
            if (string.Equals(_legacyMemeDurationPath, file, StringComparison.OrdinalIgnoreCase)) dur = _legacyMemeDurationSec;
            else ProbeMainScreenMemeLength(file);
        }
        return new CoreMeme(file, atStart ? 0 : clipLenSec, dur, MainScreenMemeId);
    }

    private void ProbeMainScreenMemeLength(string file)
    {
        if (_legacyMemeProbing) return;
        _legacyMemeProbing = true;
        _ = Task.Run(async () =>
        {
            double d = 0;
            try
            {
                string ffprobe = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
                d = await new MediaProber(ffprobe, file).GetDurationAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _legacyMemeProbing = false;
                _legacyMemeDurationPath = file;
                _legacyMemeDurationSec = d > 0.01 ? d : 0;
                UpdateTimelineMarkers();
            });
        });
    }

    /// <summary>MEMEMAIN_02 — every meme the export will play: the editor's plus the panel's START/END meme.</summary>
    private List<CoreMeme> EffectiveMemePlacements()
    {
        var all = new List<CoreMeme>(_memePlacements);
        if (MainScreenMemePlacement() is { } main) all.Add(main);
        return all;
    }

    /// <summary>MEMEMARK_02 — draws every meme on the main screen's SOURCE ruler.</summary>
    private void DrawMainMemeMarkers(Canvas canvas, double duration, double canvasWidth, double laneTop, double laneHeight)
    {
        var memes = EffectiveMemePlacements();
        if (memes.Count == 0 || duration <= 0 || canvasWidth <= 0) return;
        var colour = Infrastructure.ThemeResources.Colour(this, "AppMemeColor", Avalonia.Media.Color.FromRgb(124, 58, 237));
        var stripFill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(140, colour.R, colour.G, colour.B));
        var stripEdge = new Avalonia.Media.SolidColorBrush(colour);
        var tl = _viewModel.Timeline;
        double startMs = _trimStartSet ? _trimStartMs : 0;
        double endMs = _trimEndMs > startMs ? _trimEndMs : _loadedVideoDurationMs;
        double gameTotal = tl.PreviewSourceToOutputSeconds(endMs);

        foreach (var meme in memes)
        {
            double memeAbsSec = startMs / 1000.0 + meme.AtSourceSecRelative;
            double memeX = memeAbsSec / duration * canvasWidth;
            string name = System.IO.Path.GetFileName(meme.FilePath);
            bool isMain = meme.Id == MainScreenMemeId;
            string id = meme.Id;

            if (meme.IsCornerOverlay)
            {
                double anchorOut = tl.PreviewSourceToOutputSeconds(memeAbsSec * 1000.0);
                double endOut = Math.Min(gameTotal, anchorOut + meme.DurationSec);
                double endAbsMs = tl.PreviewOutputToSourceMs(endOut);
                double x2 = Math.Clamp(endAbsMs / 1000.0 / duration * canvasWidth, memeX, canvasWidth);
                var strip = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = Math.Max(6, x2 - memeX),
                    Height = Math.Max(6, laneHeight * 0.25),
                    Fill = stripFill,
                    Stroke = stripEdge,
                    StrokeThickness = 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                    RadiusX = 3,
                    RadiusY = 3,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                ToolTip.SetTip(strip,
                    $"Corner meme: {name} ({meme.DurationSec:0.0}s)\n" +
                    "Plays in a corner over the running gameplay — nothing pauses and the video length stays the same.\n" +
                    "Click to open it in GRANULAR SPEED (move, change or remove it there).");
                Canvas.SetLeft(strip, Math.Min(memeX, Math.Max(0, canvasWidth - strip.Width)));
                Canvas.SetTop(strip, laneTop + 2);
                strip.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    OpenSpeedEditorAtMeme(id);
                };
                canvas.Children.Add(strip);
                continue;
            }

            var marker = CreateMemeTimelineCameraIcon();
            marker.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            ToolTip.SetTip(marker, isMain
                ? $"Meme: {name}" + (meme.DurationSec > 0 ? $" ({meme.DurationSec:0.0}s)" : "") + "\n" +
                  $"Chosen in the panel on the left: it plays full screen at the {(meme.AtSourceSecRelative <= 0.0005 ? "START, before" : "END, after")} your gameplay.\n" +
                  "Change or remove it in the panel (Add MEME)."
                : $"Meme: {name} ({meme.DurationSec:0.0}s)\n" +
                  "Full screen: your gameplay pauses here, the meme plays, then the gameplay carries on from this exact frame.\n" +
                  "Click to open it in GRANULAR SPEED (move, change or remove it there).");
            Canvas.SetTop(marker, -79);
            Canvas.SetLeft(marker, ClampTimelineCameraLeft(memeX, canvasWidth));
            if (!isMain)
            {
                marker.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    OpenSpeedEditorAtMeme(id);
                };
            }
            canvas.Children.Add(marker);
        }
    }

    /// <summary>MEMEMARK_02 — the meme the Speed Editor should open on (consumed by the GRANULAR SPEED handler).</summary>
    private string? _openEditorAtMemeId;

    /// <summary>MEMEMARK_02 — opens the Speed Editor straight on a meme (no EDIT / REMOVE question).</summary>
    private void OpenSpeedEditorAtMeme(string memeId)
    {
        var btn = GranularButtonCtl;
        if (btn == null || !btn.IsEnabled) return;
        _openEditorAtMemeId = memeId;
        RuntimeLog.Info("MEME", $"Main timeline meme clicked ({memeId}) — opening the Speed Editor on it.");
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    }

    /// <summary>
    /// MEMEMUSIC_02 — the "keep the music under the meme?" question, asked once a FULL SCREEN meme and
    /// music are both present and it was never answered — for the editor's memes as well as the
    /// panel's (it used to be asked for the panel's meme only, so the editor's silently paused the
    /// music). Finishing the Music Wizard asks again, which is how the answer is changed.
    /// </summary>
    private async Task AskKeepMusicIfNeededAsync(bool force = false)
    {
        bool hasMusic = _musicWizardResult != null && !string.IsNullOrEmpty(_musicWizardResult.MusicFilePath);
        bool hasFullScreen = EffectiveMemePlacements().Any(m => !m.IsCornerOverlay);
        if (!hasMusic || !hasFullScreen) return;
        if (!force && _keepMusicDuringMeme != null) return;
        await AskKeepMusicDuringMemeAsync();
    }
}
