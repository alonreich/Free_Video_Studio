using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.AvRegression.Tests;

public enum Screen { MainApp, SpeedEditor, VoiceOverStudio, AddMusic }

internal static class Screens
{
    public static readonly Screen[] All = Enum.GetValues<Screen>();

    public static GranularEditSession EditorSession(MainEdit e)
    {
        var s = new GranularEditSession(e.Source.Path, e.TrimStartMs, e.EndMs, new GranularHistoryParking());
        s.SeedMemes(e.Memes);
        s.SeedCutsFromAbsolute(e.Cuts);
        s.AdoptClipDuration(e.Source.DurationSec, fromProbe: true);
        s.BaseSpeed = e.BaseSpeed;
        s.FreezeTimeMs = e.FreezeTimeMs;
        s.FreezeDurationS = e.FreezeDurationS;
        s.SeedSegmentsFromAbsolute(e.Segments);
        return s;
    }

    public static VoiceOverPlaybackPlan VoicePlan(MainEdit e) =>
        new(e.TrimStartMs / 1000, e.EndMs / 1000, e.Timeline().BuildExportSpeedSegments(), e.BaseSpeed, e.Cuts);

    public static Func<double, double> SourceToOutput(Screen screen, MainEdit e)
    {
        switch (screen)
        {
            case Screen.MainApp:
            {
                var t = e.Timeline();
                return abs => t.PreviewSourceToOutputSeconds(abs * 1000);
            }
            case Screen.SpeedEditor:
            {
                var session = EditorSession(e);
                var timeline = GranularSpeedEditorWindow.EditorPreviewTimeline(session, (e.EndMs - e.TrimStartMs) / 1000, new EditorTimelineCache());
                return GranularSpeedEditorWindow.EditorPreviewTimeMapper(timeline, e.TrimStartMs);
            }
            case Screen.VoiceOverStudio:
            {
                var plan = VoicePlan(e);
                return abs => Math.Max(0, plan.OutputAt(abs));
            }
            default:
                return MusicWizardWindow.WizardVoiceTimeMapper(e.TrimStartMs, e.EndMs, e.Timeline().BuildExportSpeedSegments(), e.BaseSpeed, e.Cuts);
        }
    }

    public static Func<double, double> Export(MainEdit e)
    {
        var t = e.Timeline();
        return abs => t.SourceMsToOutputSeconds(abs * 1000);
    }

    public static List<(double AnchorOutputSec, double DurationSec)> InlineMemes(MainEdit e, Func<double, double> map) =>
        MemePlacement.InlineOnly(e.Memes).Select(m => (map(e.TrimStartMs / 1000 + m.AtSourceSecRelative), m.DurationSec)).ToList();
}

internal sealed class SimulatedVoiceTake
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _anchorReaderSec, _anchorWallSec;
    private long _overSinceMs;

    public SimulatedVoiceTake(double totalSec) => TotalSec = totalSec;

    public double TotalSec { get; }
    public bool Playing { get; private set; }
    public int Starts { get; private set; }
    public int Seeks { get; private set; }
    public int Pauses { get; private set; }
    public List<string> Log { get; } = new();

    public double ReaderSec => Playing ? _anchorReaderSec + (_clock.Elapsed.TotalSeconds - _anchorWallSec) : _anchorReaderSec;

    public double AudibleSec => ReaderSec - FreeVideoStudio.App.Infrastructure.PreviewAudioSync.VoiceReaderLeadSec;

    public void Tick(bool shouldPlay, double voiceTimeSec)
    {
        double reader = ReaderSec;
        var d = FreeVideoStudio.App.Infrastructure.PreviewAudioSync.Decide(Playing, reader, TotalSec, shouldPlay, voiceTimeSec, ref _overSinceMs, Environment.TickCount64);
        double now = _clock.Elapsed.TotalSeconds;
        switch (d.Action)
        {
            case FreeVideoStudio.App.Infrastructure.VoiceSyncAction.StartAt:
                _anchorReaderSec = (double.IsFinite(d.ReaderSec) ? d.ReaderSec : reader) + FreeVideoStudio.App.Infrastructure.PreviewAudioSync.VoiceReaderLeadSec;
                _anchorWallSec = now; Playing = true; Starts++;
                Log.Add($"start at voice {voiceTimeSec:0.000}s");
                break;
            case FreeVideoStudio.App.Infrastructure.VoiceSyncAction.Pause:
                _anchorReaderSec = reader; _anchorWallSec = now; Playing = false; Pauses++;
                Log.Add($"pause at voice {voiceTimeSec:0.000}s");
                break;
            case FreeVideoStudio.App.Infrastructure.VoiceSyncAction.SeekTo:
                Log.Add($"RE-SEEK from {reader - FreeVideoStudio.App.Infrastructure.PreviewAudioSync.VoiceReaderLeadSec:0.000}s to {voiceTimeSec:0.000}s");
                _anchorReaderSec = d.ReaderSec; _anchorWallSec = now; Seeks++;
                break;
        }
    }
}
