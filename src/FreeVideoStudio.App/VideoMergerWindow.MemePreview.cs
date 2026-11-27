using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
namespace FreeVideoStudio.App;
public partial class VideoMergerWindow
{
    internal async Task<ExportResult> RenderEditorMemePreviewAsync(MergeEditorSource source, MergeEditorState state,
        bool portrait, string directory, CancellationToken ct, Action<int> progress)
    {
        var next = source.FromEditor(state, CurrentEdl) with { Captions = state.Captions };
        var composite = CompositeTimeline.Build(next);
        var prefs = Infrastructure.SettingsManager.Instance;
        string encoder = ExportEncoderStrategy.Resolve(prefs.VideoEncoderOverride.ToString(), null, BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries"));
        using var worker = new MergerWorker
        {
            InputFiles = next.Clips.Select(c => c.Path).ToList(), Edl = next,
            ClipTrims = composite.Clips.Select(c => new MergerWorker.ClipTrim(c.KeepInUs / 1_000_000.0, c.KeepOutUs / 1_000_000.0)).ToList(),
            ClipIntroTagSec = _timelineIntroTags.ToList(),
            OutputDirectory = directory, OutputBaseName = "preview-" + Guid.NewGuid().ToString("N"),
            SpeedFactor = next.BaseSpeed, QualityPercent = 50,
            OutputRatio = portrait ? MergerWorker.TargetAspectRatio.Portrait9x16 : MergerWorker.TargetAspectRatio.Landscape16x9,
            AutoSpikeFlattening = prefs.Defaults.AutoSpikeFlattening, MatchClipLoudness = prefs.MergerMatchClipLoudness,
            HardwareStrategy = encoder, CaptionTrack = state.Captions,
        };
        if (portrait)
        {
            var coords = await VideoConfig.GetMobileCoordinatesAsync();
            worker.CaptionHudRects = MobileFilterBuilder.HudCanvasRects(coords, prefs.Defaults.StartShowTeammates, true);
        }
        if (_thumbPath != null)
        {
            worker.ThumbnailClipIndex = next.Clips.ToList().FindIndex(c => SameVideoPath(c.Path, _thumbPath));
            worker.ThumbnailSourceSec = _thumbSourceSec;
        }
        if (_musicResult is { } music)
        {
            var timeline = composite;
            double start = timeline.MergedSecToBodyOutputSec(music.TimelineStartSeconds);
            double end = timeline.MergedSecToBodyOutputSec(music.TimelineEndSeconds);
            var tracks = music.ToBedTracks();
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].KnownSec <= 0)
                {
                    ct.ThrowIfCancellationRequested();
                    double duration = await new MediaProber(BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries"), tracks[i].Path).GetDurationAsync();
                    tracks[i] = tracks[i] with { KnownSec = duration };
                }
            foreach (var segment in MusicBedPlan.Build(tracks, Math.Max(0.01, end - start), music.LoopMusic, 0))
                worker.MusicTracks.Add(new MusicTrack(segment.Path, segment.FileOffsetSec, segment.DurationSec));
            worker.MusicTracksArePlanned = true;
            worker.MusicConfig = new JsonObject
            {
                ["ducking_enabled"] = music.EnableDucking,
                ["ducking_threshold"] = music.EnableDucking ? SidechainCompressNode.TunedThreshold : SidechainCompressNode.BypassThreshold,
                ["ducking_ratio"] = music.EnableDucking ? SidechainCompressNode.TunedRatio : SidechainCompressNode.BypassRatio,
                ["main_vol"] = music.VideoVolume, ["music_vol"] = music.MusicVolume,
                ["carving_enabled"] = music.EnableCarving,
                ["ducking_strength"] = (double)prefs.Defaults.DuckingStrength,
                ["carving_strength"] = (double)prefs.Defaults.CarvingStrength,
                ["timeline_start_sec"] = start, ["timeline_end_sec"] = Math.Max(start + 0.01, end), ["loop_music"] = music.LoopMusic,
            };
        }
        else worker.MusicConfig = new JsonObject { ["main_vol"] = 1.0 };
        ExportResult? result = null;
        worker.ProgressUpdate += progress;
        worker.Finished += (success, message) => result = new ExportResult
        {
            Success = success, Canceled = ct.IsCancellationRequested,
            OutputPath = success ? message : null, ErrorMessage = success ? null : message,
        };
        using var registration = ct.Register(worker.Cancel);
        await worker.RunAsync(ct);
        ct.ThrowIfCancellationRequested();
        return result ?? new ExportResult { ErrorMessage = "The preview stopped without producing a video." };
    }
}
