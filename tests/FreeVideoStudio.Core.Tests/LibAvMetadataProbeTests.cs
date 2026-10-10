// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Media.Native;
using Xunit;
using Xunit.Abstractions;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// LIBAVPROBE_01/03 — native libav metadata vs the ffprobe subprocess it replaces.
/// Every assertion compares the two backends on the SAME file through the SAME MediaProber
/// accessors, plus the raw JSON keys those accessors (and OutputSizeEstimator) read.
/// </summary>
public sealed class LibAvMetadataProbeTests : IClassFixture<LibAvMetadataProbeTests.Fixtures>
{
    private readonly Fixtures _fx;
    private readonly ITestOutputHelper _out;

    public LibAvMetadataProbeTests(Fixtures fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    public sealed class Fixtures : IAsyncLifetime
    {
        public string Root { get; private set; } = string.Empty;
        public string FfprobePath => Path.Combine(Root, "binaries", "ffprobe.exe");
        public string FfmpegPath => Path.Combine(Root, "binaries", "ffmpeg.exe");
        public string Work { get; } = Path.Combine(Path.GetTempPath(), $"fvs-libav-{Guid.NewGuid():N}");

        /// <summary>name → path. Generated + representative repository media.</summary>
        public Dictionary<string, string> Valid { get; } = new();
        public Dictionary<string, string> Invalid { get; } = new();
        public List<string> NotCovered { get; } = new();

        public async Task InitializeAsync()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FreeVideoStudio.sln"))) dir = dir.Parent;
            Root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
            Directory.CreateDirectory(Work);

            async Task Gen(string name, params string[] args)
            {
                string path = Path.Combine(Work, name);
                var psi = new ProcessStartInfo(FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string a in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" }.Concat(args).Append(path)) psi.ArgumentList.Add(a);
                var r = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(60));
                if (r.ExitCode == 0 && File.Exists(path)) Valid[name] = path;
                else NotCovered.Add($"{name}: ffmpeg exit {r.ExitCode}: {r.StandardError.Trim()}");
            }

            string[] v320 = { "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30" };
            string[] sine = { "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000" };
            await Gen("av.mp4", [.. v320, .. sine, "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-b:a", "192k"]);
            await Gen("video-only.mp4", [.. v320, "-t", "2", "-c:v", "libx264", "-preset", "ultrafast"]);
            await Gen("audio-only.m4a", [.. sine, "-t", "2", "-c:a", "aac"]);
            await Gen("ntsc-59.94.mp4", ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=60000/1001", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast"]);
            await Gen("bt601-tv.mp4", [.. v320, "-t", "1", "-c:v", "libx264", "-preset", "ultrafast",
                "-color_primaries", "smpte170m", "-color_trc", "smpte170m", "-colorspace", "smpte170m", "-color_range", "tv"]);
            await Gen("timing-tag.mp4", [.. v320, .. sine, "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac",
                "-metadata", "fvs_timing=v=2;fps=30/1;intro=3;fadein=0;fadeout=15", "-metadata", "fvs_intro_sec=0.100",
                "-movflags", "+faststart+use_metadata_tags"]);
            // Two video + two audio streams: the second video is larger, so "first video" vs "best video" would differ.
            await Gen("two-video-two-audio.mkv", ["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=25", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=50",
                .. sine, "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100",
                "-map", "0:v", "-map", "1:v", "-map", "2:a", "-map", "3:a", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac"]);
            // Audio with an attached picture (cover art) — ffprobe lists it as a VIDEO stream.
            await Gen("cover-art.mp3", [.. sine, "-f", "lavfi", "-i", "color=c=red:size=64x64", "-map", "0:a", "-map", "1:v", "-t", "1",
                "-frames:v", "1", "-c:a", "libmp3lame", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic", "-id3v2_version", "3"]);
            await Gen("still.png", ["-f", "lavfi", "-i", "color=c=blue:size=200x100", "-frames:v", "1"]);

            if (Valid.TryGetValue("av.mp4", out string? src))
            {
                // Stream-copy remuxes, so the metadata is carried WITHOUT re-encoding:
                // a display-matrix rotation (side data only; pixels stay 320x180) …
                await Gen("rotated-90.mp4", ["-display_rotation:v:0", "90", "-i", src, "-c", "copy"]);
                // … and an HDR10 + full-range VUI written straight into the H.264 SPS.
                await Gen("hdr-tags-fullrange.mp4", ["-i", src, "-c", "copy", "-bsf:v",
                    "h264_metadata=colour_primaries=9:transfer_characteristics=16:matrix_coefficients=9:video_full_range_flag=1"]);
            }

            if (Valid.TryGetValue("av.mp4", out string? av))
            {
                // Unicode path (UTF-8 → libav → wide on Windows) — Hebrew + symbol.
                string uni = Path.Combine(Work, "קליפ בדיקה ✓.mp4");
                File.Copy(av, uni);
                Valid["unicode-path"] = uni;

                byte[] bytes = await File.ReadAllBytesAsync(av);
                string truncHalf = Path.Combine(Work, "truncated-half.mp4");
                await File.WriteAllBytesAsync(truncHalf, bytes[..(bytes.Length / 2)]);
                Invalid["truncated-half.mp4"] = truncHalf;
                string truncHead = Path.Combine(Work, "truncated-1k.mp4");
                await File.WriteAllBytesAsync(truncHead, bytes[..1024]);
                Invalid["truncated-1k.mp4"] = truncHead;
            }
            if (Valid.TryGetValue("timing-tag.mp4", out string? fast))
            {
                // faststart: moov first, so a truncated tail still opens — a partially readable file.
                byte[] bytes = await File.ReadAllBytesAsync(fast);
                string partial = Path.Combine(Work, "faststart-truncated-tail.mp4");
                await File.WriteAllBytesAsync(partial, bytes[..(bytes.Length * 2 / 3)]);
                Valid["faststart-truncated-tail.mp4"] = partial;
            }
            string garbage = Path.Combine(Work, "garbage.mp4");
            await File.WriteAllTextAsync(garbage, "not a video");
            Invalid["garbage.mp4"] = garbage;
            string empty = Path.Combine(Work, "empty.mp4");
            await File.WriteAllBytesAsync(empty, Array.Empty<byte>());
            Invalid["empty.mp4"] = empty;
            Invalid["missing.mp4"] = Path.Combine(Work, "does-not-exist.mp4");

            // Representative repository media (real-world encoders/containers).
            foreach (string sub in new[] { "meme" })
            {
                string d = Path.Combine(Root, sub);
                if (!Directory.Exists(d)) { NotCovered.Add($"{sub}/ missing"); continue; }
                foreach (string f in Directory.EnumerateFiles(d).Where(f => new FileInfo(f).Length > 1024).OrderBy(f => f, StringComparer.Ordinal))
                    Valid[$"{sub}/{Path.GetFileName(f)}"] = f;
            }
            string mp3 = Path.Combine(Root, "mp3");
            if (Directory.Exists(mp3))
                foreach (string f in Directory.EnumerateFiles(mp3, "*.mp3").Where(f => new FileInfo(f).Length > 1024).OrderBy(f => f, StringComparer.Ordinal).Take(6))
                    Valid[$"mp3/{Path.GetFileName(f)}"] = f;
        }

        public Task DisposeAsync()
        {
            try { if (Directory.Exists(Work)) Directory.Delete(Work, recursive: true); }
            catch (IOException) { /* temp cleanup only; a locked file here is reported by the lock test */ }
            return Task.CompletedTask;
        }
    }

    // ── parity ──────────────────────────────────────────────────────────────────────────────

    private static readonly string[] StreamKeys =
        { "index", "codec_type", "width", "height", "pix_fmt", "color_range", "color_space", "color_transfer", "color_primaries", "avg_frame_rate", "duration", "bit_rate" };

    private static string? Str(JsonNode? n) => n?.ToString();

    private static readonly List<string> KnownDifferences = new();

    private static void CompareJson(string name, JsonObject native, JsonObject ffprobe, List<string> diffs)
    {
        var ns = native["streams"]?.AsArray() ?? new JsonArray();
        var fs = ffprobe["streams"]?.AsArray() ?? new JsonArray();
        if (ns.Count != fs.Count) diffs.Add($"{name}: stream count native {ns.Count} vs ffprobe {fs.Count}");
        for (int i = 0; i < Math.Min(ns.Count, fs.Count); i++)
        {
            foreach (string k in StreamKeys)
            {
                string? a = Str(ns[i]?[k]), b = Str(fs[i]?[k]);
                if (k == "duration" && a != null && b != null && double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out double da)
                    && double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out double db) && Math.Abs(da - db) <= 1e-6) continue;
                if (a != b) diffs.Add($"{name}: streams[{i}].{k} native '{a}' vs ffprobe '{b}'");
            }
        }
        foreach (string k in new[] { "duration", "bit_rate" })
        {
            string? a = Str(native["format"]?[k]), b = Str(ffprobe["format"]?[k]);
            if (a != b) diffs.Add($"{name}: format.{k} native '{a}' vs ffprobe '{b}'");
        }
        var nt = native["format"]?["tags"] as JsonObject;
        var ft = ffprobe["format"]?["tags"] as JsonObject;
        var keys = (nt?.Select(p => p.Key) ?? []).Union(ft?.Select(p => p.Key) ?? []).ToList();
        foreach (string k in keys)
        {
            string? a = Str(nt?[k]), b = Str(ft?[k]);
            if (a == b) continue;
            // KNOWN, PRE-EXISTING: AsyncProcessRunner decodes ffprobe's UTF-8 stdout with the console
            // OEM code page, so NON-ASCII tag text arrives as mojibake on the subprocess path. libav
            // returns the real UTF-8. No consumer reads such a tag (only fvs_timing / fvs_intro_sec,
            // both ASCII), so this is recorded, not failed. ASCII tags must match exactly.
            if (a != null && b != null && a.Any(c => c > 127)) { KnownDifferences.Add($"{name}: tags.{k} non-ASCII (ffprobe stdout code page)"); continue; }
            diffs.Add($"{name}: format.tags.{k} native '{a}' vs ffprobe '{b}'");
        }
    }

    private static async Task<string> Accessors(MediaProber p)
    {
        double dur = await p.GetDurationAsync();
        var (w, h) = await p.GetResolutionAsync();
        var color = await p.GetVideoColorInfoAsync();
        bool audio = await p.HasAudioAsync();
        int abr = await p.GetAudioBitrateAsync();
        double vbr = await p.GetVideoBitrateKbpsAsync();
        var timing = await p.GetExportTimingAsync();
        return $"dur={dur:F6} res={w}x{h} color=[{color}] audio={audio} abr={abr} vbr={vbr:F3} timing={(timing is { } t ? ExportTimingTag.Format(t) : "none")}";
    }

    [WindowsOnlyFact]
    public async Task NativeMatchesFfprobeOnEveryFixture()
    {
        Assert.True(LibAvMediaBackend.TryInitialize(_fx.FfprobePath, out string detail), detail);
        _out.WriteLine(detail);
        Assert.True(_fx.Valid.Count >= 10, $"too few fixtures: {string.Join(", ", _fx.Valid.Keys)}");

        var diffs = new List<string>();
        foreach (var (name, path) in _fx.Valid.Concat(_fx.Invalid))
        {
            var native = new MediaProber(_fx.FfprobePath, path, MediaProbeBackend.Native);
            var ffprobe = new MediaProber(_fx.FfprobePath, path, MediaProbeBackend.Ffprobe);
            string a = await Accessors(native), b = await Accessors(ffprobe);
            _out.WriteLine($"{name}: {a}");
            if (a != b) diffs.Add($"{name}: accessors differ\n  native  {a}\n  ffprobe {b}");
            CompareJson(name, await native.ProbeAsync(), await ffprobe.ProbeAsync(), diffs);

            bool shouldOpen = _fx.Valid.ContainsKey(name);
            if (shouldOpen && native.AnsweredBy != "libav") diffs.Add($"{name}: native backend did not answer");
            if (!shouldOpen && (await native.ProbeAsync()).Count != 0) diffs.Add($"{name}: invalid media produced data");
        }
        foreach (string n in _fx.NotCovered) _out.WriteLine($"NOT COVERED: {n}");
        foreach (string n in KnownDifferences.Distinct()) _out.WriteLine($"KNOWN DIFFERENCE: {n}");
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [WindowsOnlyFact]
    public async Task SpecificSemanticsArePreserved()
    {
        async Task<MediaProber> P(string name) { var p = new MediaProber(_fx.FfprobePath, _fx.Valid[name], MediaProbeBackend.Native); await p.ProbeAsync(); return p; }

        var av = await P("av.mp4");
        Assert.InRange(await av.GetDurationAsync(), 1.9, 2.1);
        Assert.Equal((320, 180), await av.GetResolutionAsync());
        Assert.True(await av.HasAudioAsync());

        Assert.False(await (await P("video-only.mp4")).HasAudioAsync());
        Assert.Equal((0, 0), await (await P("audio-only.m4a")).GetResolutionAsync());

        // First video stream wins (not the largest), exactly as before.
        Assert.Equal((160, 90), await (await P("two-video-two-audio.mkv")).GetResolutionAsync());

        // Attached picture is still a video stream to the existing accessors.
        Assert.Equal((64, 64), await (await P("cover-art.mp3")).GetResolutionAsync());

        var hdr = await (await P("hdr-tags-fullrange.mp4")).GetVideoColorInfoAsync();
        Assert.True(hdr.IsHdr && hdr.IsFullRange, hdr.ToString());
        Assert.Equal("bt2020", hdr.Primaries);
        Assert.Equal("bt2020nc", hdr.Matrix);

        Assert.True((await (await P("bt601-tv.mp4")).GetVideoColorInfoAsync()).IsBt601Matrix);

        var timing = await (await P("timing-tag.mp4")).GetExportTimingAsync();
        Assert.NotNull(timing);
        Assert.Equal(3, timing!.Value.IntroFrames);

        // Rotation is NOT applied to width/height by ffprobe -show_streams, and nothing consumes it.
        // The fixture really carries a display matrix (ffprobe reports it as side data) …
        var rotatedRaw = await new MediaProber(_fx.FfprobePath, _fx.Valid["rotated-90.mp4"], MediaProbeBackend.Ffprobe).ProbeAsync();
        Assert.Contains("\"rotation\"", rotatedRaw["streams"]![0]!["side_data_list"]?.ToJsonString() ?? string.Empty);
        // … and both backends still report the coded size, which is what every caller has always received.
        Assert.Equal((320, 180), await (await P("rotated-90.mp4")).GetResolutionAsync());

        var fps = (await (await P("ntsc-59.94.mp4")).ProbeAsync())["streams"]![0]!["avg_frame_rate"]!.ToString();
        Assert.Equal("60000/1001", fps);
    }

    /// <summary>
    /// LIBAVPROBE_02 — the backend binds by GetExport and must leave FreeVideoStudio.Core's single
    /// DllImportResolver slot free (libmpv's P/Invokes are resolved through it).
    /// </summary>
    [WindowsOnlyFact]
    public async Task NativeBackendLeavesTheAssemblyResolverSlotFree()
    {
        Assert.True((await MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, _fx.Valid["av.mp4"], TimeSpan.FromSeconds(15), default, MediaProbeBackend.Native)).Ok);
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(MediaProber).Assembly, (_, _, _) => IntPtr.Zero);
        Assert.True((await MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, _fx.Valid["av.mp4"], TimeSpan.FromSeconds(15), default, MediaProbeBackend.Native)).Ok);
    }

    // ── cancellation / timeout ──────────────────────────────────────────────────────────────

    [WindowsOnlyFact]
    public async Task PreCancelledTokenIsCancellationNotFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, _fx.Valid["av.mp4"], TimeSpan.FromSeconds(15), cts.Token, MediaProbeBackend.Auto));
    }

    [WindowsOnlyFact]
    public async Task ExpiredDeadlineAbortsThroughTheInterruptCallback()
    {
        // A 1-tick budget has already passed when libav performs its first read, so AVIOInterruptCB
        // must stop avformat_open_input with AVERROR_EXIT and the probe must surface a timeout.
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, _fx.Valid["av.mp4"], TimeSpan.FromTicks(1), CancellationToken.None, MediaProbeBackend.Native));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"timeout took {sw.ElapsedMilliseconds} ms");
    }

    [WindowsOnlyFact]
    public async Task RapidCancellationIsBoundedAndNeverFaults()
    {
        var rng = new Random(1234);
        string clip = _fx.Valid.FirstOrDefault(kv => kv.Key.StartsWith("meme/") && kv.Key.EndsWith(".mp4")).Value ?? _fx.Valid["av.mp4"];
        int cancelled = 0, completed = 0;
        long worst = 0;
        for (int i = 0; i < 200; i++)
        {
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            var task = MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, clip, TimeSpan.FromSeconds(15), cts.Token, MediaProbeBackend.Native);
            if (rng.Next(4) != 0) cts.CancelAfter(TimeSpan.FromTicks(rng.Next(0, 20_000)));
            try
            {
                var r = await task;
                Assert.True(r.Ok, r.Error);
                completed++;
            }
            catch (OperationCanceledException) { cancelled++; }
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
        }
        _out.WriteLine($"rapid cancellation: {completed} completed, {cancelled} cancelled, worst {worst} ms");
        Assert.True(worst < 3000, $"a cancelled probe took {worst} ms");
        AssertNotLocked(clip);
    }

    // ── fallback ────────────────────────────────────────────────────────────────────────────

    private sealed class FakeNative(NativeProbeStatus status, bool throwCancel = false) : INativeMediaBackend
    {
        public int Calls;
        public Task<NativeProbeResult> ProbeAsync(string ffprobePath, string mediaPath, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (throwCancel) throw new OperationCanceledException();
            return Task.FromResult(new NativeProbeResult(status, null, $"fake {status}"));
        }
    }

    [WindowsOnlyFact]
    public async Task UnavailableNativeFallsBackToFfprobeOnce()
    {
        var fake = new FakeNative(NativeProbeStatus.Unavailable);
        MediaMetadataProbe.NativeOverride = fake;
        try
        {
            long before = MediaMetadataProbe.FallbackCount;
            var prober = new MediaProber(_fx.FfprobePath, _fx.Valid["av.mp4"]);
            Assert.Equal((320, 180), await prober.GetResolutionAsync());
            Assert.Equal("ffprobe", prober.AnsweredBy);
            Assert.Equal(1, fake.Calls);
            Assert.True(MediaMetadataProbe.FallbackCount > before);
        }
        finally { MediaMetadataProbe.NativeOverride = null; }
    }

    [WindowsOnlyFact]
    public async Task MediaErrorFallsBackOnceAndFfprobeFailureDoesNotBounce()
    {
        var fake = new FakeNative(NativeProbeStatus.MediaError);
        MediaMetadataProbe.NativeOverride = fake;
        try
        {
            var r = await MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, _fx.Invalid["garbage.mp4"], TimeSpan.FromSeconds(15));
            Assert.False(r.Ok);
            Assert.Equal("ffprobe", r.Backend);
            Assert.NotEqual(0, r.ExitCode);
            Assert.Equal(1, fake.Calls);   // no second native attempt after ffprobe failed
        }
        finally { MediaMetadataProbe.NativeOverride = null; }
    }

    [WindowsOnlyFact]
    public async Task NativeOnlyNeverStartsFfprobeAndCancellationNeverFallsBack()
    {
        MediaMetadataProbe.NativeOverride = new FakeNative(NativeProbeStatus.Unavailable);
        try
        {
            // A non-existent ffprobe proves no subprocess was attempted (it would throw Win32Exception).
            var r = await MediaMetadataProbe.ProbeAsync(Path.Combine(_fx.Work, "no-ffprobe.exe"), _fx.Valid["av.mp4"], TimeSpan.FromSeconds(5), default, MediaProbeBackend.Native);
            Assert.False(r.Ok);
            MediaMetadataProbe.NativeOverride = new FakeNative(NativeProbeStatus.Ok, throwCancel: true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                MediaMetadataProbe.ProbeAsync(Path.Combine(_fx.Work, "no-ffprobe.exe"), _fx.Valid["av.mp4"], TimeSpan.FromSeconds(5)));
        }
        finally { MediaMetadataProbe.NativeOverride = null; }
    }

    [WindowsOnlyFact]
    public async Task EnvironmentOverrideForcesTheSubprocess()
    {
        string? old = Environment.GetEnvironmentVariable(MediaMetadataProbe.BackendEnvironmentVariable);
        var fake = new FakeNative(NativeProbeStatus.Ok);
        MediaMetadataProbe.NativeOverride = fake;
        try
        {
            Environment.SetEnvironmentVariable(MediaMetadataProbe.BackendEnvironmentVariable, "ffprobe");
            var r = await MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, _fx.Valid["av.mp4"], TimeSpan.FromSeconds(15));
            Assert.True(r.Ok);
            Assert.Equal("ffprobe", r.Backend);
            Assert.Equal(0, fake.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MediaMetadataProbe.BackendEnvironmentVariable, old);
            MediaMetadataProbe.NativeOverride = null;
        }
    }

    // ── stress / resources ──────────────────────────────────────────────────────────────────

    private static void AssertNotLocked(string path)
    {
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [WindowsOnlyFact]
    public async Task RepeatedProbesShowNoResourceGrowth()
    {
        string valid = _fx.Valid["av.mp4"];
        string invalid = _fx.Invalid["truncated-half.mp4"];
        async Task Round(int n)
        {
            for (int i = 0; i < n; i++)
            {
                var ok = await MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, valid, TimeSpan.FromSeconds(15), default, MediaProbeBackend.Native);
                Assert.True(ok.Ok, ok.Error);
                var bad = await MediaMetadataProbe.ProbeAsync(_fx.FfprobePath, invalid, TimeSpan.FromSeconds(15), default, MediaProbeBackend.Native);
                Assert.False(bad.Ok);
            }
        }
        (long priv, int handles) Sample()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var me = Process.GetCurrentProcess();
            return (me.PrivateMemorySize64, me.HandleCount);
        }

        await Round(50);   // warm-up: libav one-time tables, JIT, thread pool
        var baseline = Sample();
        var samples = new List<(long, int)>();
        for (int block = 0; block < 5; block++)
        {
            await Round(60);
            samples.Add(Sample());
        }
        var end = samples[^1];
        _out.WriteLine($"baseline priv={baseline.priv / 1024} KB handles={baseline.handles}; " +
                       string.Join("; ", samples.Select((s, i) => $"after {(i + 1) * 60} pairs: priv={s.Item1 / 1024} KB handles={s.Item2}")));
        Assert.True(end.Item1 - baseline.priv < 32L * 1024 * 1024, $"private bytes grew {(end.Item1 - baseline.priv) / 1024} KB over 300 valid + 300 invalid probes");
        Assert.True(end.Item2 - baseline.handles < 50, $"handle count grew {end.Item2 - baseline.handles}");
        AssertNotLocked(valid);
        AssertNotLocked(invalid);
    }
}
