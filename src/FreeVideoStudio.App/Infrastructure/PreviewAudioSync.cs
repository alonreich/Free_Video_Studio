using System;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;

namespace FreeVideoStudio.App.Infrastructure;

internal enum VoiceSyncAction { None, StartAt, Pause, SeekTo }

internal readonly record struct VoiceSyncDecision(VoiceSyncAction Action, double ReaderSec = 0);

internal static class PreviewAudioSync
{
    public const double DriftToleranceSec = BedPreviewFollower.DriftToleranceSec;
    public const int DriftStrikes = BedPreviewFollower.DriftStrikes;
    public const int DriftConfirmMs = BedPreviewFollower.DriftConfirmMs;

    public const int VoiceLatencyMs = 120;
    public const int VoiceBuffers = 3;
    public const double VoiceReaderLeadSec = VoiceLatencyMs / 1000.0;

    public static WaveOutEvent CreateVoicePlayer()
        => new() { BufferMilliseconds = VoiceLatencyMs / VoiceBuffers, NumberOfBuffers = VoiceBuffers };

    public static void DisposeOffUiThread(params IDisposable?[] items)
    {
        if (items.Length == 0 || items.All(i => i == null)) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            using var blocking = FreeVideoStudio.Core.Infrastructure.BlockingCallTracker.Enter("audio output dispose");
            foreach (var item in items)
            {
                try { item?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        });
    }

    public static VoiceSyncDecision Decide(bool playing, double readerSec, double totalSec, bool shouldPlay,
        double voiceTimeSec, ref long overSinceMs, long nowMs)
    {
        if (shouldPlay && !playing)
        {
            overSinceMs = 0;
            return new VoiceSyncDecision(VoiceSyncAction.StartAt,
                voiceTimeSec >= 0 && voiceTimeSec < totalSec ? voiceTimeSec : double.NaN);
        }

        if (!shouldPlay)
        {
            overSinceMs = 0;
            return playing ? new VoiceSyncDecision(VoiceSyncAction.Pause) : default;
        }

        double audible = readerSec - VoiceReaderLeadSec;
        if (Math.Abs(audible - voiceTimeSec) <= DriftToleranceSec)
        {
            overSinceMs = 0;
            return default;
        }

        if (overSinceMs <= 0) { overSinceMs = Math.Max(1, nowMs); return default; }
        if (nowMs - overSinceMs < DriftConfirmMs) return default;
        overSinceMs = 0;
        return new VoiceSyncDecision(VoiceSyncAction.SeekTo,
            Math.Clamp(voiceTimeSec + VoiceReaderLeadSec, 0, Math.Max(0, totalSec)));
    }

    public static void SyncVoiceTake(WavAudioReader reader, WaveOutEvent player, bool shouldPlay, double voiceTimeSec, ref long overSinceMs)
    {
        var decision = Decide(player.PlaybackState == PlaybackState.Playing, reader.CurrentTime.TotalSeconds,
            reader.TotalTime.TotalSeconds, shouldPlay, voiceTimeSec, ref overSinceMs, Environment.TickCount64);
        switch (decision.Action)
        {
            case VoiceSyncAction.StartAt:
                if (double.IsFinite(decision.ReaderSec)) reader.CurrentTime = TimeSpan.FromSeconds(decision.ReaderSec);
                player.Play();
                break;
            case VoiceSyncAction.Pause:
                player.Pause();
                break;
            case VoiceSyncAction.SeekTo:
                reader.CurrentTime = TimeSpan.FromSeconds(decision.ReaderSec);
                break;
        }
    }
}
