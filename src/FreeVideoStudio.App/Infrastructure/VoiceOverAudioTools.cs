using System;
using System.Collections.Generic;
using System.IO;

namespace FreeVideoStudio.App.Infrastructure;

internal static class VoiceOverAudioTools
{
    internal static float[] DecodePeaks(string path, int buckets)
    {
        if (buckets < 1) buckets = 1;
        using var reader = new FreeVideoStudio.Core.Media.WavAudioReader(path);

        long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        if (totalSamples <= 0) return Array.Empty<float>();

        var peaks = new float[buckets];
        var buffer = new float[8192];
        long samplesPerBucket = Math.Max(1, totalSamples / buckets);

        long read = 0;
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                int bucket = (int)Math.Min(buckets - 1, (read + i) / samplesPerBucket);
                float v = buffer[i];
                if (v < 0) v = -v;
                if (v > peaks[bucket]) peaks[bucket] = v;
            }
            read += n;
        }

        return peaks;
    }

    internal static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (!System.IO.File.Exists(path)) return;
                    System.IO.File.Delete(path);
                    return;
                }
                catch (System.IO.IOException) when (attempt < 3)
                {
                    await Task.Delay(60);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    await Task.Delay(60);
                }
                catch (Exception ex)
                {
                    RuntimeLog.WarnThrottled("VoiceOver", $"Could not delete temp take '{System.IO.Path.GetFileName(path)}': {ex.Message}");
                    return;
                }
            }

            RuntimeLog.Debug("VoiceOver", $"Temp take '{System.IO.Path.GetFileName(path)}' is still locked; leaving it for temp cleanup.");
        });
    }

    internal static void RetireGenerationCts(System.Threading.CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { cts.Dispose(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    internal static string FormatClock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    internal static void RememberVoiceProtectionChoices(bool duckGame, bool duckMusic)
    {
        var settings = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance;
        bool dirty = false;

        if (settings.VoiceProtectGameLast != duckGame) dirty = true;
        if (settings.VoiceProtectMusicLast != duckMusic) dirty = true;

        if (!dirty) return;

        try
        {
            bool committed = FreeVideoStudio.App.Infrastructure.SettingsManager.Update(s =>
            {
                s.VoiceProtectGameLast = duckGame;
                s.VoiceProtectMusicLast = duckMusic;
            });
            if (committed)
            {
                RuntimeLog.Info("VoiceOver",
                    $"Voice-protection choices remembered: game={duckGame}, music={duckMusic}.");
            }
            else
            {
                RuntimeLog.Fail("VoiceOver", "Could not save the voice-protection choices: the settings transaction did not commit.");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"Could not save the voice-protection choices: {ex.Message}");
        }
    }
}
