// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;

namespace VoiceOverNativeSmoke;

internal static class Program
{
    /// <summary>
    /// MICSMOKE_02 — the mode label must be evidence, not a feature switch. A framework-dependent
    /// run (dotnet run / apphost) is launched by hostfxr, which always publishes the FX_DEPS_FILE /
    /// APP_CONTEXT_DEPS_FILES app-context properties; a NativeAOT executable has no host and no
    /// deps file. RuntimeFeature.IsDynamicCodeSupported alone is NOT used: runtimeconfig switches
    /// can set it false under the managed runtime. The exact executable path and SHA-256 are printed
    /// so the run can be tied to the published artifact.
    /// </summary>
    private static void ReportExecutionMode()
    {
        bool hosted = AppContext.GetData("FX_DEPS_FILE") != null || AppContext.GetData("APP_CONTEXT_DEPS_FILES") != null;
        string process = Environment.ProcessPath ?? "(unknown)";
        bool viaDotnet = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        bool native = !hosted && !viaDotnet && !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
        string hash = "(unavailable)";
        try
        {
            using var stream = File.OpenRead(process);
            hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (IOException ex) { hash = $"(unreadable: {ex.Message})"; }
        catch (UnauthorizedAccessException ex) { hash = $"(unreadable: {ex.Message})"; }
        Console.WriteLine(native
            ? "[MODE] NativeAOT (no runtime host, no deps file)"
            : $"[MODE] MANAGED via {(viaDotnet ? "dotnet host" : "apphost")} - does NOT prove the NativeAOT build");
        Console.WriteLine($"[EXE] {process}");
        Console.WriteLine($"[SHA256] {hash}");
    }

    public static int Main(string[] args)
    {
        bool optInRun = args.Any(a => string.Equals(a, "--run", StringComparison.OrdinalIgnoreCase));
        // MICSMOKE_01 — with --require-signal the user must speak: a peak below this fails the run,
        // proving real samples (not just delivered digital zeros) reach the recorder.
        bool requireSignal = args.Any(a => string.Equals(a, "--require-signal", StringComparison.OrdinalIgnoreCase));
        const float SignalPeakThreshold = 0.01f;
        ReportExecutionMode();

        int deviceCount = 0;
        try
        {
            deviceCount = WaveIn.DeviceCount;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STATUS] NOT RUN: WaveIn enumeration failed: {ex.Message}");
            return 0;
        }

        if (!optInRun || deviceCount <= 0)
        {
            Console.WriteLine($"[STATUS] NOT RUN: Physical microphone hardware verification requires --run and an active recording device. (Devices found: {deviceCount}, opt-in: {optInRun})");
            return 0;
        }

        Console.WriteLine($"Found {deviceCount} input device(s):");
        int targetDevice = 0;
        for (int i = 0; i < deviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            Console.WriteLine($"  [{i}] {caps.ProductName}");
            if (caps.ProductName.Contains("Sound Blaster", StringComparison.OrdinalIgnoreCase) ||
                caps.ProductName.Contains("AE-7", StringComparison.OrdinalIgnoreCase))
            {
                targetDevice = i;
            }
        }

        var selectedCaps = WaveIn.GetCapabilities(targetDevice);
        Console.WriteLine($"Selected device [{targetDevice}]: {selectedCaps.ProductName}");

        string tempWavDir = Path.Combine(Path.GetTempPath(), "FvsNativeSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWavDir);

        try
        {
            // ── Phase 1: Test MicLevelMonitor (Idle Monitoring) ────────────────────────
            Console.WriteLine("Phase 1: Testing MicLevelMonitor idle capture...");
            using (var monitor = new MicLevelMonitor())
            {
                int monitorLevelsSeen = 0;
                float monitorPeak = 0;
                monitor.LevelChanged += (_, level) =>
                {
                    Interlocked.Increment(ref monitorLevelsSeen);
                    if (level > monitorPeak) monitorPeak = level;
                };

                monitor.Start(targetDevice);
                Thread.Sleep(1500);
                monitor.Stop();

                if (monitor.LastError != null)
                {
                    Console.Error.WriteLine($"[FAIL] MicLevelMonitor failed with error: {monitor.LastError.Message}");
                    return 1;
                }

                if (monitor.BuffersSeen <= 0)
                {
                    Console.Error.WriteLine($"[FAIL] MicLevelMonitor received 0 buffers during 1.5s monitoring.");
                    return 1;
                }

                Console.WriteLine($"  MicLevelMonitor OK: {monitor.BuffersSeen} buffers, {monitorLevelsSeen} level events, peak: {monitorPeak:0.####}");
            }

            // ── Phase 2: Test VoiceRecorder (Recording & WAV Persistence) ──────────────
            Console.WriteLine("Phase 2: Testing VoiceRecorder capture and WAV output...");
            string wavPath = Path.Combine(tempWavDir, "smoke_take_1.wav");
            if (requireSignal) Console.WriteLine("  >>> SPEAK NOW for 2 seconds <<<");
            using (var recorder = new VoiceRecorder(wavPath, targetDevice))
            {
                recorder.StartRecording();
                Thread.Sleep(2000);
                recorder.StopRecording();

                if (recorder.LastError != null)
                {
                    Console.Error.WriteLine($"[FAIL] VoiceRecorder failed with error: {recorder.LastError.Message}");
                    return 1;
                }

                if (recorder.BuffersSeen <= 0 || recorder.BytesCaptured <= 0)
                {
                    Console.Error.WriteLine($"[FAIL] VoiceRecorder captured 0 buffers or 0 bytes.");
                    return 1;
                }

                if (!File.Exists(wavPath) || new FileInfo(wavPath).Length <= 44)
                {
                    Console.Error.WriteLine($"[FAIL] VoiceRecorder generated missing or empty WAV file.");
                    return 1;
                }

                using (var reader = new WavAudioReader(wavPath))
                {
                    if (reader.TotalTime.TotalSeconds < 0.5)
                    {
                        Console.Error.WriteLine($"[FAIL] WavAudioReader read invalid duration: {reader.TotalTime.TotalSeconds:0.###}s");
                        return 1;
                    }
                    Console.WriteLine($"  VoiceRecorder OK: {recorder.BuffersSeen} buffers, {recorder.BytesCaptured} bytes, duration: {reader.TotalTime.TotalSeconds:0.###}s, peak: {recorder.PeakSeen:0.####}");
                }

                if (requireSignal && recorder.PeakSeen < SignalPeakThreshold)
                {
                    Console.Error.WriteLine($"[FAIL] --require-signal: peak {recorder.PeakSeen:0.####} < {SignalPeakThreshold}. Buffers arrived but carried no voice (muted input or privacy zeros).");
                    return 1;
                }
            }

            // ── Phase 2b: Take preview playback (same WaveOut + WavAudioReader path as VoiceOverPreviewPlayer) ──
            Console.WriteLine("Phase 2b: Playing the recorded take through WaveOut...");
            {
                Exception? playbackError = null;
                using var reader = new WavAudioReader(wavPath);
                using var player = new WaveOut();
                player.PlaybackStopped += (_, e) => { if (e.Exception != null) playbackError = e.Exception; };
                player.Init(reader);
                player.Play();
                Thread.Sleep(1200);
                var state = player.PlaybackState;
                player.Stop();
                if (playbackError != null)
                {
                    Console.Error.WriteLine($"[FAIL] Take preview playback failed: {playbackError.Message}");
                    return 1;
                }
                Console.WriteLine($"  WaveOut OK: state while playing = {state}, position {reader.CurrentTime.TotalSeconds:0.###}s");
            }

            // ── Phase 3: Consecutive Record/Drain Cycles ──────────────────────────────
            Console.WriteLine("Phase 3: Testing 5 consecutive open/record/drain cycles...");
            for (int cycle = 1; cycle <= 5; cycle++)
            {
                string cycleWav = Path.Combine(tempWavDir, $"smoke_cycle_{cycle}.wav");
                using var rec = new VoiceRecorder(cycleWav, targetDevice);
                rec.StartRecording();
                Thread.Sleep(600);
                rec.StopRecording();

                if (rec.LastError != null)
                {
                    Console.Error.WriteLine($"[FAIL] Cycle {cycle} failed with error: {rec.LastError.Message}");
                    return 1;
                }

                if (rec.BuffersSeen <= 0 || rec.BytesCaptured <= 0)
                {
                    Console.Error.WriteLine($"[FAIL] Cycle {cycle} captured 0 buffers or 0 bytes.");
                    return 1;
                }

                using var rdr = new WavAudioReader(cycleWav);
                Console.WriteLine($"  Cycle {cycle}/5 OK: {rec.BuffersSeen} buffers, {rec.BytesCaptured} bytes, {rdr.TotalTime.TotalSeconds:0.###}s");
            }

            // ── Phase 4: Production ordering — idle monitor -> record -> stop -> monitor, repeated ──
            Console.WriteLine("Phase 4: Testing 3 monitor -> record -> monitor hand-offs on the same endpoint...");
            using (var monitor = new MicLevelMonitor())
            {
                for (int round = 1; round <= 3; round++)
                {
                    monitor.Start(targetDevice);
                    Thread.Sleep(400);
                    int monitorBuffers = monitor.BuffersSeen;
                    monitor.Stop();   // VOMON_01 — the monitor releases the endpoint before the recorder opens it
                    if (monitor.LastError != null || monitorBuffers <= 0)
                    {
                        Console.Error.WriteLine($"[FAIL] Round {round} monitor: {monitor.LastError?.Message ?? "0 buffers"}");
                        return 1;
                    }

                    string roundWav = Path.Combine(tempWavDir, $"smoke_round_{round}.wav");
                    using (var rec = new VoiceRecorder(roundWav, targetDevice))
                    {
                        rec.StartRecording();
                        Thread.Sleep(500);
                        rec.StopRecording();
                        if (rec.LastError != null || rec.BuffersSeen <= 0)
                        {
                            Console.Error.WriteLine($"[FAIL] Round {round} recorder: {rec.LastError?.Message ?? "0 buffers"}");
                            return 1;
                        }
                        Console.WriteLine($"  Round {round}/3 OK: monitor {monitorBuffers} buffers, recorder {rec.BuffersSeen} buffers, {rec.BytesCaptured} bytes");
                    }
                }
            }

            Console.WriteLine("[STATUS] PASSED: Real microphone hardware capture verified successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] Native smoke test threw unhandled exception: {ex}");
            return 1;
        }
        finally
        {
            // Microphone samples are never kept as fixtures.
            try { Directory.Delete(tempWavDir, recursive: true); }
            catch (IOException ex) { Console.Error.WriteLine($"[WARN] Could not delete smoke audio at {tempWavDir}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"[WARN] Could not delete smoke audio at {tempWavDir}: {ex.Message}"); }
        }
    }
}
