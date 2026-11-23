using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public class AudioFilterChain
{
    private const double CrossfadeOutSec = MusicCrossfade.OutgoingFadeSec;

    private const double EdgeFadeSec = 1.5;

    public static (List<string> chains, string finalLabel) Build(
        JsonObject? musicConfig,
        double videoStartTime,
        double videoEndTime,
        double speedFactor,
        bool disableFades,
        double vfadeInD,
        List<string>? audioFilterCmd,
        int sampleRate = 48000,
        List<MusicTrack>? musicTracks = null,
        int musicStartIndex = 1,
        double? totalProjectDuration = null,
        string mainAudioLabel = "[0:a]",
        bool musicLeadFadeIn = true,
        bool musicTailFadeOut = true,
        string? voiceOverLabel = null,
        string? voiceProtectMusicPulse = null,
        string? freezeMusicDipPulse = null)
    {
        var chain = new List<string>();

        musicConfig ??= new JsonObject();
        int targetSampleRate = sampleRate > 0 ? sampleRate : 48000;

        var rawParts = new List<string>();
        if (audioFilterCmd != null) rawParts.AddRange(audioFilterCmd);
        if (vfadeInD > 0) rawParts.Add($"afade=t=in:st=0:d={vfadeInD.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");

        var cleanedParts = new List<string>();
        foreach (var part in rawParts)
        {
            string s = part.Trim().Trim(',');
            if (!string.IsNullOrEmpty(s)) cleanedParts.Add(s);
        }
        if (cleanedParts.Count == 0) cleanedParts.Add("anull");
        string mainAudioFilter = string.Join(",", cleanedParts);

        double mainDuration = totalProjectDuration ?? 
            (speedFactor > 0 ? (videoEndTime - videoStartTime) / speedFactor : videoEndTime - videoStartTime);

        if (!string.IsNullOrEmpty(mainAudioLabel))
        {
            chain.Add($"{mainAudioLabel}{mainAudioFilter}[a_main_raw]");
        }
        else
        {
            chain.Add($"anullsrc=r={targetSampleRate}:cl=stereo," +
                      $"atrim=duration={Math.Max(0.01, mainDuration).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"asetpts=PTS-STARTPTS[a_main_raw]");
        }

        var tracks = new List<MusicTrack>();
        double fallbackMusicDuration = totalProjectDuration ?? mainDuration;

        if (musicTracks != null && musicTracks.Count > 0)
        {
            tracks.AddRange(musicTracks);
        }
        else if (musicConfig != null && musicConfig["path"]?.ToString() is string path && !string.IsNullOrEmpty(path))
        {
            double offset = (double)(musicConfig["file_offset_sec"]?.GetValue<double>() ?? 0);
            double dur = totalProjectDuration ?? (videoEndTime - videoStartTime) / Math.Max(0.001, speedFactor);
            tracks.Add(new MusicTrack(path, offset, dur));
        }

        double? musicWindowSec = null;
        if (musicConfig != null)
        {
            try
            {
                double mStart = (double)(musicConfig["timeline_start_sec"]?.GetValue<double>() ?? 0);
                double mEnd = (double)(musicConfig["timeline_end_sec"]?.GetValue<double>() ?? 0);
                if (mEnd > mStart) musicWindowSec = mEnd - mStart;
            }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        }

        if (tracks.Count > 0 && musicWindowSec.HasValue)
        {
            double remaining = musicWindowSec.Value;
            var clippedTracks = new List<MusicTrack>();
            foreach (var track in tracks)
            {
                double take = Math.Min(track.Duration, remaining);
                if (take > 0.001)
                {
                    clippedTracks.Add(new MusicTrack(track.Path, track.Offset, take));
                    remaining -= take;
                }
                if (remaining <= 0.001) break;
            }
            tracks = clippedTracks;
        }

        if (tracks.Count == 0)
        {
            double vVol = GetDouble(musicConfig, "main_vol", GetDouble(musicConfig, "video_volume", 1.0));
            chain.Add($"[a_main_raw]volume={vVol.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"aresample={targetSampleRate}:async=1[game_leveled_base]");

            if (!string.IsNullOrEmpty(voiceOverLabel))
            {
                chain.Add($"[game_leveled_base]{voiceOverLabel}amix=inputs=2:duration=first:dropout_transition=2:normalize=0[a_main_prepared]");
            }
            else
            {
                chain.Add("[game_leveled_base]anull[a_main_prepared]");
            }

            return (chain, "[a_main_prepared]");
        }

        double initialDelaySec = 0;
        if (musicConfig != null)
        {
            try { initialDelaySec = Math.Max(0, (double)(musicConfig["timeline_start_sec"]?.GetValue<double>() ?? 0)); }
            catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        }

        var preparedMusicLabels = new List<string>();
        double accumProjectSec = initialDelaySec;

        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            string inputLabel = $"[{musicStartIndex + i}:a]";
            string outLabel = $"[a_mus_{i}]";
            string preLabel = $"[a_mus_{i}_pre]";

            double fileStart = Math.Max(0, track.Offset);
            double extraDelay = track.Offset < 0 ? -track.Offset : 0;

            bool isFirst = i == 0;
            bool isLast = i == tracks.Count - 1;

            double overlap = isFirst
                ? 0.0
                : MusicCrossfade.Overlap(track.Duration, tracks[i - 1].Duration);

            double playDur = Math.Max(0.01, track.Duration + overlap);
            double half = playDur / 2.0;

            var musicFilters = new List<string>
            {
                $"atrim=start={fileStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:duration={playDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}",
                "asetpts=PTS-STARTPTS"
            };

            if (!disableFades && playDur > 0.1)
            {
                double fadeInDur = isFirst
                    ? (musicLeadFadeIn ? Math.Min(EdgeFadeSec, half) : 0.0)
                    : Math.Min(overlap, half);

                if (fadeInDur > 0.001)
                {
                    musicFilters.Add(
                        $"afade=t=in:st=0:d={fadeInDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                double fadeOutDur = isLast
                    ? ((musicTailFadeOut && track.ApplyFadeOut) ? Math.Min(EdgeFadeSec, half) : 0.0)
                    : Math.Min(CrossfadeOutSec, half);

                if (fadeOutDur > 0.001)
                {
                    double fadeOutStart = Math.Max(0, playDur - fadeOutDur);
                    musicFilters.Add(
                        $"afade=t=out:st={fadeOutStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:d={fadeOutDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            double mVol = GetDouble(musicConfig, "music_vol", GetDouble(musicConfig, "volume", 0.8));

            musicFilters.Add($"volume={mVol.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");

            chain.Add($"{inputLabel}{string.Join(",", musicFilters)}{preLabel}");

            double startSec = Math.Max(0.0,
                accumProjectSec - overlap + extraDelay + track.TimelineStartDelay);
            int delayMs = (int)(startSec * 1000);
            if (delayMs > 0)
                chain.Add($"{preLabel}adelay={delayMs}|{delayMs}{outLabel}");
            else
                chain.Add($"{preLabel}anull{outLabel}");

            preparedMusicLabels.Add(outLabel);
            accumProjectSec += track.Duration;
        }

        string bgMusicLabel;
        if (preparedMusicLabels.Count > 1)
        {
            string mixInputs = string.Join("", preparedMusicLabels);
            string weights = string.Join(" ", Enumerable.Repeat("1", preparedMusicLabels.Count));
            chain.Add($"{mixInputs}amix=inputs={preparedMusicLabels.Count}:" +
                      $"duration=longest:dropout_transition=0:weights='{weights}':normalize=0[a_bg_music_raw]");
            bgMusicLabel = "[a_bg_music_raw]";
        }
        else
        {
            bgMusicLabel = preparedMusicLabels[0];
        }

        bool carvingEnabled = ReadBool(musicConfig, "carving_enabled", true);

        double legacyRatio = GetDouble(musicConfig, "ducking_ratio", SidechainCompressNode.TunedRatio);
        bool duckingEnabled = ReadBool(musicConfig, "ducking_enabled",
                                       legacyRatio > SidechainCompressNode.BypassRatio + 0.0001);

        if (!string.IsNullOrEmpty(freezeMusicDipPulse))
        {
            chain.AddRange(FreezeSoundEffect.MusicDipChains(bgMusicLabel, freezeMusicDipPulse, "fzd", "[a_bg_freeze_dip]"));
            bgMusicLabel = "[a_bg_freeze_dip]";
            CoreLogger.Info("Audio", "Freeze effect: music keeps playing, ducked 6 dB, carved at 500 Hz and muffled under each freeze.");
        }

        if (!string.IsNullOrEmpty(voiceProtectMusicPulse))
        {
            chain.AddRange(GatedVoiceProtection(bgMusicLabel, voiceProtectMusicPulse, "vpm", "[a_bg_voice_protected]"));
            bgMusicLabel = "[a_bg_voice_protected]";
            CoreLogger.Info("Audio", "Voice protection: music bed ducked 85% and carved at 2.5 kHz during the voice-over takes only.");
        }

        double vVolGame = GetDouble(musicConfig, "main_vol", GetDouble(musicConfig, "video_volume", 1.0));

        chain.Add($"[a_main_raw]volume={vVolGame.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)},aresample={targetSampleRate}:async=1[game_leveled_base]");

        if (!string.IsNullOrEmpty(voiceOverLabel))
        {
            chain.Add($"[game_leveled_base]{voiceOverLabel}amix=inputs=2:duration=first:dropout_transition=2:normalize=0[game_leveled]");
        }
        else
        {
            chain.Add("[game_leveled_base]anull[game_leveled]");
        }

        string gameForMix;
        if (duckingEnabled || carvingEnabled)
        {
            int duckStrength = (int)Math.Round(GetDouble(musicConfig, "ducking_strength", SidechainCompressNode.DefaultStrength));
            int carveStrength = (int)Math.Round(GetDouble(musicConfig, "carving_strength", SidechainCompressNode.DefaultStrength));
            var compressors = new List<(string band, SidechainCompressNode node)>();
            if (duckingEnabled)
            {
                compressors.Add(("mid", SidechainCompressNode.Duck(duckStrength)));
                compressors.Add(("high", SidechainCompressNode.Duck(duckStrength)));
            }
            if (carvingEnabled)
            {
                compressors.Add(("mid", SidechainCompressNode.Carve(carveStrength)));
            }

            int n = compressors.Count;
            chain.Add($"[game_leveled]asplit=2[game_out_pre][game_trig]");
            string pinFormat = $"aformat=sample_fmts=fltp:sample_rates={targetSampleRate}:channel_layouts=stereo";
            chain.Add("[game_trig]highpass=f=200,lowpass=f=3500," +
                      $"agate=threshold=0.05:attack=5:release=100,{pinFormat}" +
                      (n > 1 ? $",asplit={n}{string.Concat(Enumerable.Range(0, n).Select(k => $"[trig_{k}]"))}" : "[trig_0]"));

            chain.Add($"{bgMusicLabel}{pinFormat},acrossover=split='{CrossoverLowHz} {CrossoverHighHz}'[mus_low][mus_mid][mus_high]");
            var bandLabel = new Dictionary<string, string> { ["mid"] = "mus_mid", ["high"] = "mus_high" };
            for (int k = 0; k < n; k++)
            {
                var (band, node) = compressors[k];
                string outLabel = $"mus_{band}_c{k}";
                chain.Add(new FilterChain()
                    .WithInputs(bandLabel[band], $"trig_{k}")
                    .AddNode(node)
                    .WithOutputs(outLabel)
                    .ToFFmpegString());
                bandLabel[band] = outLabel;
            }

            chain.Add(new FilterChain()
                .WithInputs("mus_low", bandLabel["mid"], bandLabel["high"])
                .AddNode(new AmixNode { Inputs = 3, Weights = "1 1 1", Normalize = 0 })
                .WithOutputs("a_music_reconstructed")
                .ToFFmpegString());

            gameForMix = "[game_out_pre]";
            bgMusicLabel = "[a_music_reconstructed]";
            CoreLogger.Info("Audio",
                $"Music protection: ducking {(duckingEnabled ? $"ON (strength {duckStrength})" : "OFF")}, dynamic speech-band carving {(carvingEnabled ? $"ON (strength {carveStrength})" : "OFF")} " +
                $"(multiband {CrossoverLowHz}/{CrossoverHighHz} Hz, {n} sidechain stage(s)).");
        }
        else
        {
            gameForMix = "[game_leveled]";
        }

        chain.Add($"{gameForMix}{bgMusicLabel}amix=inputs=2:" +
                  $"duration=first:dropout_transition=3:weights='1 1':normalize=0," +
                  $"aresample={targetSampleRate}:async=1[a_music_prepared]");

        return (chain, "[a_music_prepared]");
    }

    public const int CrossoverLowHz = 250;
    public const int CrossoverHighHz = 2900;

    public const double VoiceDuckDepth = 0.85;

    public const double VoiceRampSec = 0.3;

    public static double VoiceProtectionPulseAt(double t, IEnumerable<(double StartSec, double EndSec)> takes)
    {
        double sum = 0;
        foreach (var (s, e) in takes)
        {
            double up = Math.Clamp((t - (s - VoiceRampSec)) / VoiceRampSec, 0, 1);
            double down = Math.Clamp(((e + VoiceRampSec) - t) / VoiceRampSec, 0, 1);
            sum += up * down;
        }
        return Math.Clamp(sum, 0, 1);
    }
    public const string VoiceCarveEq = "equalizer=f=2500:width_type=h:width=2200:g=-3";

    public static IEnumerable<string> GatedVoiceProtection(string inLabel, string pulseExpr, string prefix, string outLabel)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string depth = VoiceDuckDepth.ToString("F2", ci);
        yield return $"{inLabel}asplit=2[{prefix}_dry][{prefix}_wet]";
        yield return $"[{prefix}_wet]{VoiceCarveEq},volume='{pulseExpr}':eval=frame[{prefix}_wet_g]";
        yield return $"[{prefix}_dry]volume='1-({pulseExpr})':eval=frame[{prefix}_dry_g]";
        yield return $"[{prefix}_dry_g][{prefix}_wet_g]amix=inputs=2:weights='1 1':normalize=0," +
                     $"volume='1.0-{depth}*({pulseExpr})':eval=frame{outLabel}";
    }

    private static bool ReadBool(JsonObject? obj, string key, bool defaultValue)
    {
        var node = obj?[key];
        if (node == null) return defaultValue;

        string raw = node.ToString().Trim().Trim('"');
        if (bool.TryParse(raw, out bool parsed)) return parsed;
        if (raw == "1") return true;
        if (raw == "0") return false;
        return defaultValue;
    }

    private static double GetDouble(JsonObject? obj, string key, double defaultValue)
    {
        if (obj == null) return defaultValue;
        try { return obj[key]?.GetValue<double>() ?? defaultValue; }
        catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
            return defaultValue;
        }
    }
}

public record MusicTrack(string Path, double Offset, double Duration, double TimelineStartDelay = 0.0, bool ApplyFadeOut = true);
