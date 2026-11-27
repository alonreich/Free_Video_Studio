using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Media;
using CoreMeme = FreeVideoStudio.Core.Media.MemePlacement;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    public const string MainScreenMemeId = "mainmeme";

    private string? _legacyMemeDurationPath;
    private double _legacyMemeDurationSec;
    private bool _legacyMemeProbing;

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
        return new CoreMeme(file, atStart ? 0 : clipLenSec, dur, MainScreenMemeId,
            MusicContinues: Infrastructure.MemePickerPrefsStore.MusicContinuesFor(file));
    }

    private Controls.MemePickerContext CreateMainMemePickerContext()
    {
        double start = _trimStartMs;
        double end = _trimEndMs > start ? _trimEndMs : _loadedVideoDurationMs;
        var session = new FreeVideoStudio.Core.Editing.GranularEditSession(_loadedVideoPath, start, end, new FreeVideoStudio.Core.Editing.GranularHistoryParking());
        session.BaseSpeed = _baseSpeed;
        session.SeedSegmentsFromAbsolute(BuildExportSpeedSegments());
        session.SeedCutsFromAbsolute(_cuts);
        session.SeedMemes(EffectiveMemePlacements());
        double here = Math.Clamp(GetCurrentMpvTime() - start / 1000, 0, (end - start) / 1000);
        var cache = new Services.EditorTimelineCache();
        var game = new Services.EditorTimelineCache();
        return new Controls.MemePickerContext(choice =>
        {
            double duration = (end - start) / 1000;
            var ruler = cache.Get(end - start, session.Segments, session.Cuts, session.Memes, _baseSpeed);
            var gameplay = game.Get(end - start, session.Segments, session.Cuts, [], _baseSpeed);
            double requested = choice.Placement switch
            {
                ViewModels.MemePlacementPoint.Start => 0,
                ViewModels.MemePlacementPoint.End => choice.IsCorner ? gameplay.OutputToSourceRelative(Math.Max(0, gameplay.TotalOutputSeconds - choice.LengthSeconds)) : duration,
                _ => here,
            };
            var ok = session.ResolveNewMemeAnchor(ruler, requested, choice.Mode, out double at);
            double output = ruler.SourceToOutput(at);
            if (choice.IsCorner && gameplay.TotalOutputSeconds - gameplay.SourceToOutput(at) <= 0.001)
                return new Controls.MemeLanding(false, at, output, "No gameplay remains here for a corner meme. Choose End, or move the video cursor earlier.");
            string moved = Math.Abs(at - requested) > 0.01 ? $"; moved {Math.Abs(at - requested):0.0}s to the nearest allowed spot" : "";
            return new Controls.MemeLanding(ok.Ok, at, output, ok.Ok ? $"Starts at {output:0.0}s{moved}" : ok.Error!);
        }, async (candidate, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var payload = await ComposeExportPayloadAsync(_paths.TempDirectory, null, 2, null);
            var memes = EffectiveMemePlacements();
            memes.Add(candidate);
            return payload with { MemeFile = null, MemePlacements = memes };
        });
    }
    internal async Task<Models.ExportPayload> ComposeEditorMemePreviewAsync(
        List<SpeedSegment> segments, List<CutRange> cuts, List<CoreMeme> memes, CaptionTrack? captions, double baseSpeed)
    {
        var payload = await ComposeExportPayloadAsync(_paths.TempDirectory, null, 2, null);
        List<MusicTrack>? tracks = null;
        if (_musicWizardResult is { } music)
        {
            var map = PreviewTimeMap.Create(_trimStartMs, PreviewVideoEndMs(), segments, baseSpeed, cuts);
            var bed = music.PreviewBed(_trimStartMs, PreviewVideoEndMs(), source => map.SourceToOutput(source / 1000));
            tracks = bed.Plan.Select(part => new MusicTrack(part.Path, part.FileOffsetSec, part.DurationSec, bed.StartDelaySec, true)).ToList();
        }
        return payload with { MemeFile = null, MemePlacements = memes, SpeedSegments = segments, Cuts = cuts, MusicTracks = tracks, CaptionTrack = captions, BaseSpeed = baseSpeed };
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

    private List<CoreMeme> EffectiveMemePlacements()
    {
        var all = new List<CoreMeme>(_memePlacements);
        if (MainScreenMemePlacement() is { } main) all.Add(main);
        return all;
    }

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
                  "Change or remove this older start/end meme in GRANULAR SPEED."
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

    private string? _openEditorAtMemeId;

    private void OpenSpeedEditorAtMeme(string memeId)
    {
        var btn = GranularButtonCtl;
        if (btn == null || !btn.IsEnabled) return;
        _openEditorAtMemeId = memeId;
        RuntimeLog.Info("MEME", $"Main timeline meme clicked ({memeId}) — opening the Speed Editor on it.");
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    }
}
