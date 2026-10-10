using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;

// LIBAVFRAME_01 benchmark child: one 160 px thumbnail frame in a FRESH process ("cold"), timed in-process.
// native = VideoFrameGrabber native only; ffmpeg = the exact MemeThumbnailCache subprocess (PNG on stdout).
if (args.Length == 5 && args[0] == "--libavframe-cold")
{
    var coldFrameWatch = Stopwatch.StartNew();
    bool coldFrameOk;
    if (args[1] == "native")
        coldFrameOk = (await VideoFrameGrabber.DecodeNativeAsync(args[2], args[3], VideoFrameRequest.AtFfmpegSeek(args[4], 160, true), TimeSpan.FromSeconds(30))).Status == NativeFrameStatus.Ok;
    else
        coldFrameOk = (await ThumbPng(args[2], args[3], args[4])).Length > 0;
    coldFrameWatch.Stop();
    Console.WriteLine($"COLD_MS={coldFrameWatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} OK={coldFrameOk}");
    return coldFrameOk ? 0 : 1;
}

// LIBAVPROBE_01 benchmark child: one metadata probe in a FRESH process ("cold"), timed in-process.
if (args.Length == 4 && args[0] == "--libav-cold")
{
    var backend = args[1] == "native" ? MediaProbeBackend.Native : MediaProbeBackend.Ffprobe;
    var coldWatch = Stopwatch.StartNew();
    var coldResult = await MediaMetadataProbe.ProbeAsync(args[2], args[3], TimeSpan.FromSeconds(15), default, backend);
    coldWatch.Stop();
    Console.WriteLine($"COLD_MS={coldWatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} OK={coldResult.Ok} BACKEND={coldResult.Backend}");
    return coldResult.Ok ? 0 : 1;
}

// Run from the workspace root. All generated media and state stay inside this directory.
string root = Directory.GetCurrentDirectory();
string ffmpeg = Path.Combine(root, "binaries", "ffmpeg.exe");
if (!File.Exists(ffmpeg)) throw new InvalidOperationException("Run from the workspace root.");
string work = Path.Combine(root, "tests", "MediaPipelineChecks", "artifacts", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
Directory.CreateDirectory(Path.Combine(work, "temp"));
Environment.SetEnvironmentVariable("TMP", Path.Combine(work, "temp"));
Environment.SetEnvironmentVariable("TEMP", Path.Combine(work, "temp"));
Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, Path.Combine(work, "state"));
Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "binaries") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
var failures = new List<string>();

// ══════════════════════════════════════════════════════════════════════════════════════════════
// LIBAVPROBE_01 — native libav metadata: parity, stress/resources, benchmark (Windows).
// ══════════════════════════════════════════════════════════════════════════════════════════════
string ffprobe = Path.Combine(root, "binaries", "ffprobe.exe");
string libavClip = Path.Combine(work, "libav-av.mp4");
string libavBad = Path.Combine(work, "libav-truncated.mp4");

await Check("LIBAV: native metadata matches the ffprobe subprocess on repository and generated media", async () =>
{
    await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
        "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", libavClip);
    byte[] bytes = await File.ReadAllBytesAsync(libavClip);
    await File.WriteAllBytesAsync(libavBad, bytes[..(bytes.Length / 2)]);
    var files = new List<string> { libavClip, libavBad };
    files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "meme")).Where(f => new FileInfo(f).Length > 1024));
    files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "mp3"), "*.mp3").Where(f => new FileInfo(f).Length > 1024));
    int compared = 0;
    foreach (string f in files)
    {
        var n = new MediaProber(ffprobe, f, MediaProbeBackend.Native);
        var p = new MediaProber(ffprobe, f, MediaProbeBackend.Ffprobe);
        string a = $"{await n.GetDurationAsync():F6}|{await n.GetResolutionAsync()}|{await n.GetVideoColorInfoAsync()}|{await n.HasAudioAsync()}|{await n.GetAudioBitrateAsync()}|{await n.GetVideoBitrateKbpsAsync():F3}";
        string b = $"{await p.GetDurationAsync():F6}|{await p.GetResolutionAsync()}|{await p.GetVideoColorInfoAsync()}|{await p.HasAudioAsync()}|{await p.GetAudioBitrateAsync()}|{await p.GetVideoBitrateKbpsAsync():F3}";
        Require(a == b, $"{Path.GetFileName(f)}: native {a} vs ffprobe {b}");
        compared++;
    }
    Console.WriteLine($"  parity: {compared} files identical through every MediaProber accessor");
});

await Check("LIBAV: stress — repeated open/probe/close, invalid media and rapid cancellation leak nothing", async () =>
{
    (long priv, int handles, int threads) Sample()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var me = Process.GetCurrentProcess();
        return (me.PrivateMemorySize64, me.HandleCount, me.Threads.Count);
    }
    async Task Valid(int n) { for (int i = 0; i < n; i++) Require((await MediaMetadataProbe.ProbeAsync(ffprobe, libavClip, TimeSpan.FromSeconds(15), default, MediaProbeBackend.Native)).Ok, "valid probe failed"); }
    async Task Invalid(int n) { for (int i = 0; i < n; i++) Require(!(await MediaMetadataProbe.ProbeAsync(ffprobe, libavBad, TimeSpan.FromSeconds(15), default, MediaProbeBackend.Native)).Ok, "invalid probe succeeded"); }
    var rng = new Random(42);
    int cancelled = 0;
    long worstCancelMs = 0;
    async Task Cancel(int n)
    {
        for (int i = 0; i < n; i++)
        {
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            var t = MediaMetadataProbe.ProbeAsync(ffprobe, libavClip, TimeSpan.FromSeconds(15), cts.Token, MediaProbeBackend.Native);
            cts.CancelAfter(TimeSpan.FromTicks(rng.Next(0, 30_000)));
            try { await t; } catch (OperationCanceledException) { cancelled++; }
            worstCancelMs = Math.Max(worstCancelMs, sw.ElapsedMilliseconds);
        }
    }

    await Valid(50); await Invalid(20);   // warm-up
    var start = Sample();
    var rows = new List<string> { $"    start            priv {start.priv / 1024,8} KB  handles {start.handles,5}  threads {start.threads,3}" };
    (long priv, int handles, int threads) last = start;
    for (int round = 1; round <= 5; round++)
    {
        await Valid(200); await Invalid(100); await Cancel(60);
        last = Sample();
        rows.Add($"    after round {round}    priv {last.priv / 1024,8} KB  handles {last.handles,5}  threads {last.threads,3}   (cumulative {round * 200} valid, {round * 100} invalid, {round * 60} cancel attempts)");
    }
    Console.WriteLine(string.Join(Environment.NewLine, rows));
    Console.WriteLine($"  cancellation: {cancelled}/300 attempts cancelled, worst completion {worstCancelMs} ms");
    using (new FileStream(libavClip, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    using (new FileStream(libavBad, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    Console.WriteLine("  source media not locked after 1000 valid + 500 invalid + 300 cancel attempts");
    Require(last.priv - start.priv < 32L * 1024 * 1024, $"private bytes grew {(last.priv - start.priv) / 1024} KB");
    Require(last.handles - start.handles < 50, $"handles grew {last.handles - start.handles}");
    Require(worstCancelMs < 3000, $"a cancelled probe took {worstCancelMs} ms");
});

await Check("LIBAV: benchmark — cold and warm metadata probe, native vs ffprobe subprocess", async () =>
{
    static (double median, double p95) Stats(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        double Q(double q) { double i = q * (s.Count - 1); int lo = (int)Math.Floor(i), hi = (int)Math.Ceiling(i); return s[lo] + (s[hi] - s[lo]) * (i - lo); }
        return (Q(0.5), Q(0.95));
    }
    string bigClip = Directory.EnumerateFiles(Path.Combine(root, "meme"), "*.mp4").OrderByDescending(f => new FileInfo(f).Length).First();
    var report = new List<string>();
    void Row(string label, List<double> native, List<double> sub)
    {
        var (nm, np) = Stats(native); var (sm, sp) = Stats(sub);
        report.Add($"  {label,-44} n={native.Count,3}/{sub.Count,3}  native median {nm,8:F3} ms  p95 {np,8:F3} ms | ffprobe median {sm,8:F3} ms  p95 {sp,8:F3} ms | median change {(nm - sm) / sm * 100,7:F1}%  ({sm / nm,6:F1}x)");
    }

    // Cold: a fresh process per sample; time = the first probe inside it (DLL load / process spawn included).
    string self = Environment.ProcessPath!;
    string? selfDll = self.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? Path.Combine(AppContext.BaseDirectory, "MediaPipelineChecks.dll") : null;
    async Task<double> Cold(string backend, string file)
    {
        string[] a = selfDll != null ? [selfDll, "--libav-cold", backend, ffprobe, file] : ["--libav-cold", backend, ffprobe, file];
        string text = await RunText(self, a);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"COLD_MS=([0-9.]+) OK=True");
        Require(m.Success, $"cold child failed: {text}");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
    foreach (var (label, file) in new[] { ("cold, 2 s generated clip", libavClip), ($"cold, {Path.GetFileName(bigClip)}", bigClip) })
    {
        var n = new List<double>(); var p = new List<double>();
        for (int i = 0; i < 15; i++) { n.Add(await Cold("native", file)); p.Add(await Cold("ffprobe", file)); }
        Row(label, n, p);
    }

    // Warm: same process, after warm-up.
    async Task<List<double>> Warm(MediaProbeBackend backend, string file, int count)
    {
        for (int i = 0; i < 5; i++) await MediaMetadataProbe.ProbeAsync(ffprobe, file, TimeSpan.FromSeconds(15), default, backend);
        var list = new List<double>();
        for (int i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await MediaMetadataProbe.ProbeAsync(ffprobe, file, TimeSpan.FromSeconds(15), default, backend);
            sw.Stop();
            Require(r.Ok, r.Error ?? "probe failed");
            list.Add(sw.Elapsed.TotalMilliseconds);
        }
        return list;
    }
    Row("warm, 2 s generated clip", await Warm(MediaProbeBackend.Native, libavClip, 200), await Warm(MediaProbeBackend.Ffprobe, libavClip, 100));
    Row($"warm, {Path.GetFileName(bigClip)}", await Warm(MediaProbeBackend.Native, bigClip, 200), await Warm(MediaProbeBackend.Ffprobe, bigClip, 100));
    Console.WriteLine(string.Join(Environment.NewLine, report));
});

// ══════════════════════════════════════════════════════════════════════════════════════════════
// LIBAVFRAME_01 — native single-frame decode: parity, stress/resources, benchmark (Windows).
// ══════════════════════════════════════════════════════════════════════════════════════════════
string frameClip = Path.Combine(work, "libavframe-counter.mp4");
string frameBad = Path.Combine(work, "libavframe-truncated.mp4");

await Check("LIBAV: frame — native single frame matches the ffmpeg subprocess (selection exact, pixels within tolerance)", async () =>
{
    // Frame number painted into the picture (lossless, B-frames, GOP 30): selection must match EXACTLY.
    await Ffmpeg("-f", "lavfi", "-i", "nullsrc=s=320x240:r=30:d=6", "-vf",
        "format=yuv420p,geq=lum='16+12*if(lt(Y\\,H/2)\\,mod(N\\,16)\\,trunc(N/16))':cb=128:cr=128",
        "-c:v", "libx264", "-preset", "fast", "-qp", "0", "-g", "30", "-bf", "3", "-pix_fmt", "yuv420p", frameClip);
    byte[] bytes = await File.ReadAllBytesAsync(frameClip);
    await File.WriteAllBytesAsync(frameBad, bytes[..(bytes.Length / 2)]);
    static int Num(DecodedVideoFrame f)
    {
        int Lv(int x, int y) => (int)Math.Round(f.Pixels[y * f.Stride + x * 4 + 1] * 219.0 / 255.0 / 12.0);
        return Lv(f.Width / 2, 3 * f.Height / 4) * 16 + Lv(f.Width / 2, f.Height / 4);
    }
    int exact = 0;
    foreach (string t in new[] { "0", "0.001", "0.033", "0.5", "0.517", "1.234", "2.999", "3.000", "5.950", "5.990", "7.000" })
    {
        var n = await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, frameClip, VideoFrameRequest.AtFfmpegSeek(t, 160, true), TimeSpan.FromSeconds(30));
        byte[] png = await ThumbPng(ffmpeg, frameClip, t);
        if (png.Length == 0) { Require(n.Status == NativeFrameStatus.NoFrame, $"counter @ {t}: ffmpeg no picture, native {n.Status}"); continue; }
        Require(n.Status == NativeFrameStatus.Ok, $"counter @ {t}: native {n.Status} {n.Detail}");
        int a = Num(n.Frame!), b = Num(DecodePng(png));
        Require(a == b, $"counter @ {t}: native frame #{a} vs ffmpeg frame #{b}");
        exact++;
    }
    Console.WriteLine($"  selection: {exact} timestamps, native frame number == ffmpeg frame number at every one");

    var rows = new List<string>();
    foreach (string f in Directory.EnumerateFiles(Path.Combine(root, "meme"), "*.mp4").OrderBy(f => f, StringComparer.Ordinal))
    {
        foreach (string t in new[] { "0", "0.5", "1.234" })
        {
            var n = await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, f, VideoFrameRequest.AtFfmpegSeek(t, 160, true), TimeSpan.FromSeconds(30));
            byte[] png = await ThumbPng(ffmpeg, f, t);
            if (png.Length == 0) { Require(n.Status == NativeFrameStatus.NoFrame, $"{Path.GetFileName(f)} @ {t}: ffmpeg none, native {n.Status}"); continue; }
            Require(n.Status == NativeFrameStatus.Ok, $"{Path.GetFileName(f)} @ {t}: native {n.Status} {n.Detail}");
            var p = DecodePng(png);
            Require((n.Frame!.Width, n.Frame.Height) == (p.Width, p.Height), $"{Path.GetFileName(f)} @ {t}: size {n.Frame.Width}x{n.Frame.Height} vs {p.Width}x{p.Height}");
            var (mae, psnr) = ImageDiff(n.Frame, p);
            rows.Add($"    {Path.GetFileName(f),-62} @ {t,-5} {p.Width}x{p.Height}  MAE {mae,6:F3}  PSNR {psnr,6:F2} dB");
            Require(mae <= 2.5 && psnr >= 35, $"{Path.GetFileName(f)} @ {t}: MAE {mae:F3} PSNR {psnr:F2} (tolerance MAE ≤ 2.5, PSNR ≥ 35 dB)");
        }
    }
    Console.WriteLine(string.Join(Environment.NewLine, rows));
});

await Check("LIBAV: frame — stress: decodes, seeks, invalid media and rapid cancellation leak nothing", async () =>
{
    (long priv, int handles, int threads) Sample()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var me = Process.GetCurrentProcess();
        return (me.PrivateMemorySize64, me.HandleCount, me.Threads.Count);
    }
    string meme = Directory.EnumerateFiles(Path.Combine(root, "meme"), "*.mp4").OrderByDescending(f => new FileInfo(f).Length).First();
    var rng = new Random(77);
    async Task Valid(int n)
    {
        for (int i = 0; i < n; i++)
        {
            string file = i % 2 == 0 ? frameClip : meme;
            string t = (rng.NextDouble() * 5.5).ToString("0.000", CultureInfo.InvariantCulture);
            var r = await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, file, VideoFrameRequest.AtFfmpegSeek(t, i % 3 == 0 ? 0 : 160, true), TimeSpan.FromSeconds(30));
            Require(r.Status is NativeFrameStatus.Ok or NativeFrameStatus.NoFrame, $"valid decode {Path.GetFileName(file)} @ {t}: {r.Status} {r.Detail}");
        }
    }
    async Task Invalid(int n)
    {
        for (int i = 0; i < n; i++)
        {
            var r = await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, i % 2 == 0 ? frameBad : Path.Combine(work, "missing.mp4"), VideoFrameRequest.AtFfmpegSeek("2.0", 160, true), TimeSpan.FromSeconds(30));
            Require(r.Status != NativeFrameStatus.Unavailable, $"invalid media disabled the backend: {r.Detail}");
        }
    }
    int cancelled = 0;
    long worstCancelMs = 0;
    async Task Cancel(int n)
    {
        for (int i = 0; i < n; i++)
        {
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            var t = VideoFrameGrabber.DecodeNativeAsync(ffmpeg, meme, VideoFrameRequest.AtFfmpegSeek("3.0", 160, true), TimeSpan.FromSeconds(30), cts.Token);
            cts.CancelAfter(TimeSpan.FromTicks(rng.Next(0, 200_000)));
            try { await t; } catch (OperationCanceledException) { cancelled++; }
            worstCancelMs = Math.Max(worstCancelMs, sw.ElapsedMilliseconds);
        }
    }

    await Valid(40); await Invalid(10);   // warm-up
    var start = Sample();
    var rows = new List<string> { $"    start            priv {start.priv / 1024,8} KB  handles {start.handles,5}  threads {start.threads,3}" };
    var last = start;
    for (int round = 1; round <= 5; round++)
    {
        await Valid(100); await Invalid(40); await Cancel(40);
        last = Sample();
        rows.Add($"    after round {round}    priv {last.priv / 1024,8} KB  handles {last.handles,5}  threads {last.threads,3}   (cumulative {round * 100} decodes/seeks, {round * 40} invalid, {round * 40} cancel attempts)");
    }
    Console.WriteLine(string.Join(Environment.NewLine, rows));
    Console.WriteLine($"  cancellation: {cancelled}/200 attempts cancelled, worst completion {worstCancelMs} ms");
    using (new FileStream(frameClip, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    using (new FileStream(frameBad, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    using (new FileStream(meme, FileMode.Open, FileAccess.Read, FileShare.None)) { }
    Console.WriteLine("  source media not locked after 500 decodes + 200 invalid + 200 cancel attempts");
    Require(last.priv - start.priv < 48L * 1024 * 1024, $"private bytes grew {(last.priv - start.priv) / 1024} KB");
    Require(last.handles - start.handles < 50, $"handles grew {last.handles - start.handles}");
    Require(last.threads - start.threads < 16, $"threads grew {last.threads - start.threads}");
    Require(worstCancelMs < 3000, $"a cancelled decode took {worstCancelMs} ms");
});

await Check("LIBAV: frame — benchmark: cold, warm and random-timestamp single frame, native vs ffmpeg subprocess", async () =>
{
    static (double median, double p95) Stats(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        double Q(double q) { double i = q * (s.Count - 1); int lo = (int)Math.Floor(i), hi = (int)Math.Ceiling(i); return s[lo] + (s[hi] - s[lo]) * (i - lo); }
        return (Q(0.5), Q(0.95));
    }
    var report = new List<string>();
    void Row(string label, List<double> native, List<double> sub)
    {
        var (nm, np) = Stats(native); var (sm, sp) = Stats(sub);
        report.Add($"  {label,-46} n={native.Count,3}/{sub.Count,3}  native median {nm,8:F2} ms  p95 {np,8:F2} | ffmpeg median {sm,8:F2} ms  p95 {sp,8:F2} | median change {(nm - sm) / sm * 100,7:F1}%  ({sm / nm,5:F1}x)");
    }
    string big = Directory.EnumerateFiles(Path.Combine(root, "meme"), "*.mp4").OrderByDescending(f => new FileInfo(f).Length).First();
    string self = Environment.ProcessPath!;
    string? selfDll = self.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? Path.Combine(AppContext.BaseDirectory, "MediaPipelineChecks.dll") : null;
    async Task<double> Cold(string backend, string file)
    {
        string[] a = selfDll != null ? [selfDll, "--libavframe-cold", backend, ffmpeg, file, "0.5"] : ["--libavframe-cold", backend, ffmpeg, file, "0.5"];
        string text = await RunText(self, a);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"COLD_MS=([0-9.]+) OK=True");
        Require(m.Success, $"cold child failed: {text}");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
    foreach (var (label, file) in new[] { ("cold thumb @0.5, 320x240 counter clip", frameClip), ($"cold thumb @0.5, {Path.GetFileName(big)}", big) })
    {
        var n = new List<double>(); var p = new List<double>();
        for (int i = 0; i < 15; i++) { n.Add(await Cold("native", file)); p.Add(await Cold("ffmpeg", file)); }
        Row(label, n, p);
    }
    async Task<List<double>> Warm(bool native, string file, Func<int, string> at, int count, int width)
    {
        for (int i = 0; i < 3; i++) await One(native, file, at(i), width);
        var list = new List<double>();
        for (int i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            await One(native, file, at(i), width);
            list.Add(sw.Elapsed.TotalMilliseconds);
        }
        return list;
    }
    async Task One(bool native, string file, string t, int width)
    {
        if (native)
        {
            var r = await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, file, VideoFrameRequest.AtFfmpegSeek(t, width, width > 0), TimeSpan.FromSeconds(30));
            Require(r.Status is NativeFrameStatus.Ok or NativeFrameStatus.NoFrame, r.Detail);
        }
        else if (width > 0) await ThumbPng(ffmpeg, file, t);
        else
        {
            string jpg = Path.Combine(work, "bench-still.jpg");
            await Run(["-ss", t, "-i", file, "-frames:v", "1", "-q:v", "2", jpg]);
        }
    }
    var rng = new Random(9);
    double dur = await new MediaProber(ffprobe, big).GetDurationAsync();
    string[] randomTs = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() * Math.Max(0.1, dur - 0.2)).ToString("0.000", CultureInfo.InvariantCulture)).ToArray();
    Row($"warm thumb @0.5, {Path.GetFileName(big)}", await Warm(true, big, _ => "0.5", 60, 160), await Warm(false, big, _ => "0.5", 30, 160));
    Row($"warm full-size still @0.5 (Gemini), {Path.GetFileName(big)}", await Warm(true, big, _ => "0.5", 40, 0), await Warm(false, big, _ => "0.5", 20, 0));
    Row($"random-timestamp thumb, {Path.GetFileName(big)}", await Warm(true, big, i => randomTs[i % randomTs.Length], 100, 160), await Warm(false, big, i => randomTs[i % randomTs.Length], 40, 160));
    Console.WriteLine(string.Join(Environment.NewLine, report));
});

// LIBAVFRAME_01 — long soak (opt-in: --libav-soak runs ONLY this). Several thousand native single-frame
// decodes mixed with seeks, invalid media and cancellations; full managed GC before every sample, so a
// native leak cannot hide in (or be faked by) ordinary managed-heap fluctuation.
// LIBAVFRAME_01 — soak DIAGNOSIS (opt-in: --libav-soak-diag runs ONLY this). Isolates each native path
// on ONE input at a time and samples, besides private bytes, the process heaps' IN-USE bytes
// (HeapSummary.cbAllocated): a leak grows in-use bytes; fragmentation/caching only grows committed.
await Check("LIBAV-SOAKDIAG: frame — per-path isolation with heap in-use bytes", async () =>
{
    string clip = Path.Combine(work, "diag-counter.mp4");
    await Ffmpeg("-f", "lavfi", "-i", "nullsrc=s=320x240:r=30:d=6", "-vf", "format=yuv420p", "-c:v", "libx264", "-preset", "fast", "-g", "30", "-bf", "3", clip);
    byte[] bytes = await File.ReadAllBytesAsync(clip);
    string bad = Path.Combine(work, "diag-truncated.mp4");
    await File.WriteAllBytesAsync(bad, bytes[..(bytes.Length / 2)]);
    string meme = Directory.EnumerateFiles(Path.Combine(root, "meme"), "*.mp4").First(f => f.Contains("Landscape", StringComparison.Ordinal));
    var rng = new Random(7);

    (long priv, long gc, long heapAlloc, long heapCommit, int handles, int threads) Sample()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var me = Process.GetCurrentProcess();
        var h = HeapStats.Sample();
        return (me.PrivateMemorySize64, GC.GetGCMemoryInfo().TotalCommittedBytes, h.Allocated, h.Committed, me.HandleCount, me.Threads.Count);
    }
    static double TheilSen(IReadOnlyList<(double x, double y)> p)
    {
        var sl = new List<double>();
        for (int i = 0; i < p.Count; i++) for (int j = i + 1; j < p.Count; j++) if (p[j].x != p[i].x) sl.Add((p[j].y - p[i].y) / (p[j].x - p[i].x));
        sl.Sort();
        return sl.Count == 0 ? 0 : sl[sl.Count / 2];
    }

    var verdicts = new List<string>();
    async Task Phase(string label, int ops, int every, Func<int, Task> op)
    {
        for (int i = 0; i < Math.Min(200, ops / 10); i++) await op(i);   // phase warm-up
        var rows = new List<(int ops, long priv, long gc, long ha, long hc, int handles, int threads)>();
        var s0 = Sample(); rows.Add((0, s0.priv, s0.gc, s0.heapAlloc, s0.heapCommit, s0.handles, s0.threads));
        for (int i = 1; i <= ops; i++)
        {
            await op(i);
            if (i % every == 0) { var s = Sample(); rows.Add((i, s.priv, s.gc, s.heapAlloc, s.heapCommit, s.handles, s.threads)); }
        }
        Console.WriteLine($"  PHASE {label}: {ops} operations");
        Console.WriteLine("       ops   private KB   non-GC KB  heap in-use KB  heap committed KB  handles  threads");
        foreach (var r in rows)
            Console.WriteLine($"    {r.ops,6}  {r.priv / 1024,11}  {(r.priv - r.gc) / 1024,10}  {r.ha / 1024,14}  {r.hc / 1024,17}  {r.handles,7}  {r.threads,7}");
        double tsInUse = TheilSen(rows.Select(r => ((double)r.ops, r.ha / 1024.0)).ToList()) * 1000;
        double tsNonGc = TheilSen(rows.Select(r => ((double)r.ops, (r.priv - r.gc) / 1024.0)).ToList()) * 1000;
        double tsCommit = TheilSen(rows.Select(r => ((double)r.ops, r.hc / 1024.0)).ToList()) * 1000;
        string v = $"  PHASE {label}: Theil–Sen KB per 1000 ops — heap IN-USE {tsInUse:F1}, heap committed {tsCommit:F1}, non-GC private {tsNonGc:F1}; handles {rows[^1].handles - rows[0].handles:+0;-0;0}, threads {rows[^1].threads - rows[0].threads:+0;-0;0}";
        Console.WriteLine(v);
        verdicts.Add(v);
    }

    await Phase("A thumbnail 160 px, one 1080p meme, random seek", 4000, 200, async i =>
        await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, meme, VideoFrameRequest.AtFfmpegSeek((rng.NextDouble() * 8).ToString("0.000", CultureInfo.InvariantCulture), 160, true), TimeSpan.FromSeconds(30)));
    await Phase("B full-size 1080p still, same meme, random seek", 2000, 100, async i =>
        await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, meme, VideoFrameRequest.AtFfmpegSeek((rng.NextDouble() * 8).ToString("0.000", CultureInfo.InvariantCulture)), TimeSpan.FromSeconds(30)));
    await Phase("C thumbnail 160 px, 320x240 clip, past-end NoFrame", 4000, 200, async i =>
        await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, clip, VideoFrameRequest.AtFfmpegSeek("9.000", 160, true), TimeSpan.FromSeconds(30)));
    await Phase("D invalid inputs (truncated / garbage text / missing)", 6000, 300, async i =>
    {
        string f = (i % 3) switch { 0 => bad, 1 => Path.Combine(root, "README.md"), _ => Path.Combine(work, "missing.mp4") };
        await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, f, VideoFrameRequest.AtFfmpegSeek("2.000", 160, true), TimeSpan.FromSeconds(30));
    });
    await Phase("E cancellations at random points, same meme", 3000, 150, async i =>
    {
        using var cts = new CancellationTokenSource();
        var t = VideoFrameGrabber.DecodeNativeAsync(ffmpeg, meme, VideoFrameRequest.AtFfmpegSeek("4.000", 160, true), TimeSpan.FromSeconds(30), cts.Token);
        cts.CancelAfter(TimeSpan.FromTicks(rng.Next(0, 400_000)));
        try { await t; } catch (OperationCanceledException) { }
    });
    await Phase("F metadata probe only (LIBAVPROBE baseline), same meme", 4000, 200, async i =>
        await MediaMetadataProbe.ProbeAsync(ffprobe, meme, TimeSpan.FromSeconds(15), default, MediaProbeBackend.Native));

    Console.WriteLine("  SUMMARY");
    foreach (string v in verdicts) Console.WriteLine(v);
});

await Check("LIBAV-SOAK: frame — several thousand decodes, seeks, invalid media and cancellations stabilise", async () =>
{
    string soakClip = Path.Combine(work, "soak-counter.mp4");
    string soakBad = Path.Combine(work, "soak-truncated.mp4");
    await Ffmpeg("-f", "lavfi", "-i", "nullsrc=s=320x240:r=30:d=6", "-vf",
        "format=yuv420p,geq=lum='16+12*if(lt(Y\\,H/2)\\,mod(N\\,16)\\,trunc(N/16))':cb=128:cr=128",
        "-c:v", "libx264", "-preset", "fast", "-qp", "0", "-g", "30", "-bf", "3", "-pix_fmt", "yuv420p", soakClip);
    byte[] bytes = await File.ReadAllBytesAsync(soakClip);
    await File.WriteAllBytesAsync(soakBad, bytes[..(bytes.Length / 2)]);
    string garbage = Path.Combine(work, "soak-garbage.mp4");
    await File.WriteAllTextAsync(garbage, "not a video");
    var memes = Directory.EnumerateFiles(Path.Combine(root, "meme"), "*.mp4").OrderBy(f => f, StringComparer.Ordinal).ToList();
    var durations = new Dictionary<string, double>();
    foreach (string f in memes.Append(soakClip)) durations[f] = await new MediaProber(ffprobe, f).GetDurationAsync();

    var rng = new Random(20261004);
    long decodes = 0, okFrames = 0, noFrames = 0, invalids = 0, cancelAttempts = 0, cancelled = 0, worstCancelMs = 0;
    async Task Valid(int n)
    {
        for (int i = 0; i < n; i++)
        {
            string file = rng.Next(3) == 0 ? soakClip : memes[rng.Next(memes.Count)];
            double d = durations[file];
            string t = (rng.NextDouble() * (d + 0.5)).ToString("0.000", CultureInfo.InvariantCulture);   // includes past-the-end
            int width = rng.Next(4) == 0 ? 0 : 160;   // 1 in 4 full-size (Gemini), else thumbnail
            var r = await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, file, VideoFrameRequest.AtFfmpegSeek(t, width, width > 0), TimeSpan.FromSeconds(30));
            Require(r.Status is NativeFrameStatus.Ok or NativeFrameStatus.NoFrame, $"{Path.GetFileName(file)} @ {t}: {r.Status} {r.Detail}");
            if (r.Status == NativeFrameStatus.Ok) okFrames++; else noFrames++;
            decodes++;
        }
    }
    async Task Invalid(int n)
    {
        for (int i = 0; i < n; i++)
        {
            string file = (i % 3) switch { 0 => soakBad, 1 => garbage, _ => Path.Combine(work, "soak-missing.mp4") };
            var r = await VideoFrameGrabber.DecodeNativeAsync(ffmpeg, file, VideoFrameRequest.AtFfmpegSeek("2.500", 160, true), TimeSpan.FromSeconds(30));
            Require(r.Status != NativeFrameStatus.Unavailable, $"invalid media disabled the backend: {r.Detail}");
            invalids++;
        }
    }
    async Task Cancel(int n)
    {
        for (int i = 0; i < n; i++)
        {
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            string file = memes[rng.Next(memes.Count)];
            var task = VideoFrameGrabber.DecodeNativeAsync(ffmpeg, file, VideoFrameRequest.AtFfmpegSeek("3.000", 160, true), TimeSpan.FromSeconds(30), cts.Token);
            cts.CancelAfter(TimeSpan.FromTicks(rng.Next(0, 400_000)));
            try { await task; } catch (OperationCanceledException) { cancelled++; }
            worstCancelMs = Math.Max(worstCancelMs, sw.ElapsedMilliseconds);
            cancelAttempts++;
        }
    }
    // gc = bytes the managed GC itself has COMMITTED (its regions, incl. the large-object heap that the
    // full-size BGRA buffers live in). private − gc is everything else: libav, swscale, decoder threads,
    // the CRT heap — the part a NATIVE leak would grow. The first soak run could not separate the two.
    var heapSeries = new List<(long inUse, long committed)>();   // one entry per Sample(), same order as `series`
    (long priv, long ws, int handles, int threads, long gc) Sample()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var me = Process.GetCurrentProcess();
        var hs = HeapStats.Sample();
        heapSeries.Add((hs.Allocated, hs.Committed));
        return (me.PrivateMemorySize64, me.WorkingSet64, me.HandleCount, me.Threads.Count, GC.GetGCMemoryInfo().TotalCommittedBytes);
    }

    // Warm-up: thread pool, decoder thread pools, libav static tables, swscale caches — before the baseline.
    await Valid(300); await Invalid(30); await Cancel(30);
    const int Rounds = 48, PerRound = 200;   // ~9,600 measured decodes (first soak run: 24 rounds was too short to call)
    var series = new List<(int round, long decodes, long priv, long ws, int handles, int threads, long gc)>();
    var b = Sample();
    series.Add((0, decodes, b.priv, b.ws, b.handles, b.threads, b.gc));
    var soakWatch = Stopwatch.StartNew();
    for (int round = 1; round <= Rounds; round++)
    {
        await Valid(PerRound); await Invalid(25); await Cancel(25);
        var s = Sample();
        series.Add((round, decodes, s.priv, s.ws, s.handles, s.threads, s.gc));
    }
    soakWatch.Stop();

    Console.WriteLine($"  soak: {decodes} decodes ({okFrames} frames, {noFrames} past-end NoFrame), {invalids} invalid, {cancelAttempts} cancel attempts ({cancelled} cancelled, worst {worstCancelMs} ms), {soakWatch.Elapsed.TotalSeconds:F0} s after warm-up");
    Console.WriteLine("    round  decodes   heap in-use KB  heap committed KB   private KB  GC committed KB  private-GC KB  working set KB  handles  threads");
    for (int k = 0; k < series.Count; k++)
    {
        var r = series[k];
        Console.WriteLine($"    {r.round,5}  {r.decodes,7}  {heapSeries[k].inUse / 1024,14}  {heapSeries[k].committed / 1024,17}  {r.priv / 1024,11}  {r.gc / 1024,15}  {(r.priv - r.gc) / 1024,13}  {r.ws / 1024,14}  {r.handles,7}  {r.threads,7}");
    }

    // Trend. A native leak raises the FLOOR of private bytes linearly with decodes and can never be
    // given back. The Windows heap, by contrast, keeps freed blocks and occasionally decommits them
    // (the first soak run dropped from 163 MB to 112.8 MB — below its own baseline — at round 19).
    // Least squares is dragged by such a drop, so the decision uses the Theil–Sen estimator (median of
    // pairwise slopes, robust to outliers) over the second half, and the series of per-window floors
    // (minimum of each 8-round window) is printed so a rising floor is visible directly.
    static double TheilSen(IReadOnlyList<(double x, double y)> p)
    {
        var slopes = new List<double>();
        for (int i = 0; i < p.Count; i++)
            for (int j = i + 1; j < p.Count; j++)
                if (p[j].x != p[i].x) slopes.Add((p[j].y - p[i].y) / (p[j].x - p[i].x));
        slopes.Sort();
        return slopes.Count == 0 ? 0 : slopes[slopes.Count / 2];
    }
    static double LeastSquares(IReadOnlyList<(double x, double y)> p)
    {
        double mx = p.Average(q => q.x), my = p.Average(q => q.y);
        double num = p.Sum(q => (q.x - mx) * (q.y - my)), den = p.Sum(q => (q.x - mx) * (q.x - mx));
        return den == 0 ? 0 : num / den;
    }
    var post = series.Skip(1).Select(r => ((double)r.decodes, r.priv / 1024.0)).ToList();          // after the first measured round
    var half = series.Skip(series.Count / 2).Select(r => ((double)r.decodes, r.priv / 1024.0)).ToList();
    var nativeHalf = series.Skip(series.Count / 2).Select(r => ((double)r.decodes, (r.priv - r.gc) / 1024.0)).ToList();
    var gcHalf = series.Skip(series.Count / 2).Select(r => ((double)r.decodes, r.gc / 1024.0)).ToList();
    double lsHalf = LeastSquares(half) * 1000, tsHalf = TheilSen(half) * 1000, tsAll = TheilSen(post) * 1000;
    double tsNativeHalf = TheilSen(nativeHalf) * 1000, tsGcHalf = TheilSen(gcHalf) * 1000;
    var floors = series.Skip(1).Select((r, i) => (r, i)).GroupBy(t => t.i / 8)
        .Select(g => (from: g.First().r.round, to: g.Last().r.round, floor: g.Min(t => t.r.priv) / 1024, ceil: g.Max(t => t.r.priv) / 1024,
                      nfloor: g.Min(t => t.r.priv - t.r.gc) / 1024, nceil: g.Max(t => t.r.priv - t.r.gc) / 1024)).ToList();
    int handleGrowth = series[^1].handles - series[0].handles, threadGrowth = series[^1].threads - series[0].threads;
    Console.WriteLine($"  trend (KB per 1000 decodes): private — Theil–Sen second half {tsHalf:F1}, Theil–Sen rounds 1..{Rounds} {tsAll:F1}, least squares second half {lsHalf:F1}; NON-GC (private − GC committed) Theil–Sen second half {tsNativeHalf:F1}; GC committed Theil–Sen second half {tsGcHalf:F1}; handles {handleGrowth:+0;-0;0}, threads {threadGrowth:+0;-0;0}");
    Console.WriteLine("  per-window private bytes (floor = minimum, ceiling = maximum):");
    foreach (var w in floors) Console.WriteLine($"    rounds {w.from,2}..{w.to,2}  private floor {w.floor,8} KB  ceiling {w.ceil,8} KB | non-GC floor {w.nfloor,8} KB  ceiling {w.nceil,8} KB");

    foreach (string f in new[] { soakClip, soakBad, garbage })
        using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    foreach (string f in memes)
        using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)) { }
    Console.WriteLine($"  source media not locked: {memes.Count + 3} files opened FileShare.None after the soak");

    // A real per-decode native leak of even 1 KB would be ≥ 1000 KB per 1000 decodes.
    // ══ LEAK-SAFETY DECISION — LOCKED 2026-10-04 (user decision, Stage 2 sign-off rule) ═══════════════
    // Pass/fail is judged ONLY on what a leak must grow, never on memory the heaps can give back:
    //   1. native heap bytes IN USE (HeapSummary.cbAllocated over all process heaps — libav's av_malloc
    //      lands there): Theil–Sen slope over rounds 1..N < 256 KB per 1000 decodes (a 1 KB/decode leak
    //      would be ≥ 1000), and the last sample within 16 MB of the baseline;
    //   2. handle count stable (growth < 20);  3. thread count stable (growth < 12);
    //   4. every source file openable FileShare.None afterwards (checked above — throws if locked);
    //   5. cancellation/cleanup: every cancelled decode completes in < 3000 ms, and every decode returns
    //      Ok/NoFrame and every invalid input a non-Unavailable status (checked in the loops above).
    // Private bytes, working set, GC-committed and heap-COMMITTED bytes are REPORTED ONLY: the
    // diagnosis run (--libav-soak-diag) showed them rising through reusable heap free space while
    // in-use bytes stayed flat in every native path. Do NOT change this rule based on a soak result.
    var inUse = series.Select((r, k) => ((double)r.decodes, heapSeries[k].inUse / 1024.0)).Skip(1).ToList();
    double tsInUse = TheilSen(inUse) * 1000;
    long inUseGrowthKb = (heapSeries[^1].inUse - heapSeries[0].inUse) / 1024;
    Console.WriteLine($"  LEAK-SAFETY: heap in-use Theil–Sen {tsInUse:F1} KB per 1000 decodes, last − baseline {inUseGrowthKb:+0;-0;0} KB; handles {handleGrowth:+0;-0;0}; threads {threadGrowth:+0;-0;0}; worst cancel {worstCancelMs} ms; sources unlocked: yes");
    Require(tsInUse < 256, $"native heap in-use bytes rising: Theil–Sen {tsInUse:F1} KB per 1000 decodes");
    Require(inUseGrowthKb < 16 * 1024, $"native heap in-use bytes grew {inUseGrowthKb} KB over the soak");
    Require(handleGrowth < 20, $"handles grew {handleGrowth}");
    Require(threadGrowth < 12, $"threads grew {threadGrowth}");
    Require(worstCancelMs < 3000, $"a cancelled decode took {worstCancelMs} ms");
});

await Check("Crop recovery: newest valid backup, unchanged backups, defaults only as last resort", async () =>
{
    var paths = new ApplicationPaths(Path.Combine(work, "crops"));
    Directory.CreateDirectory(paths.ProgramDataRoot);
    var store = new CropConfigStore(paths);
    var older = CropConfigDefaults.Create();
    older["test_marker"] = "older";
    var newest = CropConfigDefaults.Create();
    newest["test_marker"] = "newest";
    AtomicJsonFile.WriteObject(paths.CropCoordinatesFile + ".bak3", older);
    AtomicJsonFile.WriteObject(paths.CropCoordinatesFile + ".bak2", newest);
    File.WriteAllText(paths.CropCoordinatesFile + ".bak1", "{broken");
    File.WriteAllText(paths.CropCoordinatesFile, "{broken");
    string before = File.ReadAllText(paths.CropCoordinatesFile + ".bak2");
    var restored = await store.LoadAsync();
    Require(restored["test_marker"]?.ToString() == "newest", "Did not choose newest valid backup.");
    Require(File.ReadAllText(paths.CropCoordinatesFile + ".bak2") == before, "Recovery changed backup.");
    Require((await store.LoadAsync())["test_marker"]?.ToString() == "newest", "Recovered file was not persisted.");
    File.Delete(paths.CropCoordinatesFile);
    Require((await store.LoadAsync())["test_marker"]?.ToString() == "newest", "Missing live file did not recover.");
    var emptyPaths = new ApplicationPaths(Path.Combine(work, "empty-crops"));
    Directory.CreateDirectory(emptyPaths.ProgramDataRoot);
    var defaults = await new CropConfigStore(emptyPaths).LoadAsync();
    Require(defaults["coordinate_space"]?.ToString() == CropConfigDefaults.CoordinateSpace, "Defaults invalid.");
});

await Check("FFmpeg filters remain identical across regional settings", async () =>
{
    var original = CultureInfo.CurrentCulture;
    try
    {
        string? baseline = null;
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "ar-SA" })
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var speed = GranularSpeedBuilder.Build(2000, [new(200, 800, 0.5), new(1100, 1100, 0.0)],
                baseSpeed: 1.25, needHudBranch: false, targetFps: "30");
            baseline ??= speed.filterGraph;
            Require(speed.filterGraph == baseline, $"Graph varies under {culture}.");
            foreach (var rate in new[] { 0.1, 0.5, 1.0, 1.25, 4.0 })
            {
                // AVSYNC_01 — 1.0x is a true no-op and returns an empty chain.
                // TEMPO_01 — both engines of the shared policy must parse in the bundled FFmpeg.
                foreach (bool rubberband in new[] { false, true })
                {
                    var tempoChain = AudioTempoFilterBuilder.Build(rate, rubberband);
                    if (tempoChain.Count == 0) continue;
                    await Ffmpeg("-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-af",
                        string.Join(",", tempoChain), "-t", "0.05", "-f", "null", "NUL");
                }
            }
            await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-filter_complex", speed.filterGraph.Replace("[0:a]", "[1:a]"), "-map", speed.videoLabel, "-map", speed.audioLabel,
                "-t", "2", "-f", "null", "NUL");
        }
    }
    finally { CultureInfo.CurrentCulture = original; }
});

await Check("Intel low-quality preset is accepted by the bundled FFmpeg", async () =>
{
    var manager = new EncoderManager("INTEL", ffmpeg);
    var (flags, _) = manager.GetCodecFlags("h264_qsv", null, 1, "30", 1, false);
    string preset = flags[flags.IndexOf("-preset") + 1];
    var result = await Run(["-f", "lavfi", "-i", "color=s=320x180:r=30", "-frames:v", "1", ..flags, "-f", "null", "NUL"]);
    Require(!result.error.Contains("Unable to parse option") && !result.error.Contains("Error setting option preset"), result.error);
    if (result.exit != 0) Console.WriteLine($"  Preset '{preset}' parsed; Intel encode was not verified: {result.error.Trim()}");
});

await Check("Export mix ignores preview master; Wizard levels change each audio channel", async () =>
{
    var mix = new JsonObject { ["main_vol"] = 0.8, ["music_vol"] = 0.4, ["ducking_enabled"] = false, ["carving_enabled"] = false };
    string? baseline = null;
    foreach (int master in new[] { 0, 25, 50, 100 })
    {
        MpvIpcClient.SetGlobalMasterVolume(master);
        var graph = AudioFilterChain.Build(mix, 0, 1, 1, true, 0, null,
            musicTracks: [new("music.wav", 0, 1)], totalProjectDuration: 1);
        string filter = string.Join(";", graph.chains);
        baseline ??= filter;
        Require(filter == baseline, "Preview loudness changed export mix.");
    }
    var samples = await MixSamples(0.8, 0.4);
    var gameMuted = await MixSamples(0, 0.4);
    var musicMuted = await MixSamples(0.8, 0);
    Require(Rms(gameMuted, 0) < 0.00001, "Gameplay mute failed.");
    Require(Rms(musicMuted, 1) < 0.00001, "Music mute failed.");
    Require(Math.Abs(Rms(samples, 0) / Rms(samples, 1) - 2) < 0.01, "80/40 mix ratio was not preserved.");
    Require(Math.Abs(Rms(samples, 1) - Rms(gameMuted, 1)) < 0.00001, "Gameplay control changed music level.");
});

await Check("Actual mpv preview audio matches linear Wizard/export levels", async () =>
{
    string source = Path.Combine(work, "preview-source.wav");
    await Ffmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=0.5", source);
    double referenceRms = 0;
    MpvIpcClient.SetGlobalMuted(false);
    MpvIpcClient.SetGlobalMasterVolume(100);   // VOLCURVE_01 — balances are linear under a 100% master
    foreach (int linear in new[] { 100, 80, 40, 20, 0 })
    {
        string wav = Path.Combine(work, $"preview-{linear}.wav");
        var result = await RunExecutable(Path.Combine(root, "binaries", "mpv.exe"),
            ["--no-config", "--no-terminal", "--vid=no", "--ao=pcm", "--ao-pcm-file=" + wav,
             "--volume=" + MpvIpcClient.PlayerMpvVolume(linear / 100.0).ToString(CultureInfo.InvariantCulture), source]);
        Require(result.exit == 0, result.error);
        string raw = wav + ".raw";
        await Ffmpeg("-i", wav, "-ac", "2", "-f", "f32le", raw);
        byte[] bytes = File.ReadAllBytes(raw);
        float[] samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        double measured = Rms(samples, 0);
        if (linear == 100) referenceRms = measured;
        Require(Math.Abs(measured / referenceRms - linear / 100.0) < 0.005, $"mpv amplitude differs at {linear}%.");
    }
});

await Check("New mpv audio previews inherit the preview master", async () =>
{
    NativeLibrary.SetDllImportResolver(typeof(MpvWrapper).Assembly, (name, _, _) =>
        name == "libmpv-2.dll" ? NativeLibrary.Load(Path.Combine(root, "binaries", name)) : IntPtr.Zero);
    foreach (int master in new[] { 0, 25, 100 })
    {
        MpvIpcClient.SetGlobalMasterVolume(master);
        using var player = new MpvIpcClient();
        await player.StartAudioOnlyAsync("");
        Require(Math.Abs(double.Parse(player.GetPropertyString("volume")!, CultureInfo.InvariantCulture) - MpvIpcClient.PlayerMpvVolume()) < 0.01, "Initial mpv volume differs.");
    }
});

await Check("Complete Main export works with decimal commas and muted preview", async () =>
{
    string input = Path.Combine(work, "source.mp4");
    await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
        "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", input);
    var savedCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        MpvIpcClient.SetGlobalMasterVolume(0);
        using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "main-state")))
        {
            InputPath = input, OutputDirectory = ExportDirectory("main-export"),
            StartTimeMs = 0, EndTimeMs = 1500, SpeedFactor = 1.25, OriginalResolution = "320x180",
            IsMobileFormat = false, HardwareStrategy = "CPU", QualityLevel = 20,
            EnableFades = false, IntroStillSec = 0.1,
            AutoSpikeFlattening = false
        };
        bool success = false;
        string output = "";
        worker.Finished += (ok, message) => { success = ok; output = message; };
        await worker.RunAsync();
        Require(success, worker.FailureDetail ?? output);
        Require(await AudioRms(output) > 0.01, "Preview mute leaked into Main export.");
    }
    finally { CultureInfo.CurrentCulture = savedCulture; }
});

await Check("Complete Merger export honors Wizard gameplay mute without music", async () =>
{
    using var worker = new MergerWorker(new ApplicationPaths(Path.Combine(work, "merger-state")))
    {
        InputFiles = [Path.Combine(work, "source.mp4")], OutputDirectory = ExportDirectory("merge-export"),
        HardwareStrategy = "CPU", QualityPercent = 5, AutoSpikeFlattening = false,
        MusicConfig = new JsonObject { ["main_vol"] = 0.0 }
    };
    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    await worker.RunAsync();
    Require(success, worker.FailureDetail ?? output);
    Require(await AudioRms(output) < 0.00001, "Wizard gameplay mute was ignored in Merger.");
});

await Check("GPU: unsupported effects retain their complete graph and audio stays independent", () =>
{
    foreach (string effect in new[] { "fade=t=in:st=0:d=1", "crop=160:180:0:0", "scale=320:180:force_original_aspect_ratio=decrease", "format=yuva420p" })
    {
        string graph = $"[0:v]{effect}[v];[0:a]volume='if(lt(t,1),0.4,0.8)'[a]";
        var plan = ExportVideoPipeline.Create("h264_nvenc", graph);
        Require(!plan.UsesGpuFrames && plan.FilterGraph == graph && plan.DecodeFlags.Count == 0,
            "Unsupported effect was changed or triggered GPU downloads.");
    }
    var native = ExportVideoPipeline.Create("h264_nvenc", "[0:v]format=yuv420p[v];[0:a]volume='if(lt(t,1),0.4,0.8)'[a]");
    Require(native.UsesGpuFrames && native.FilterGraph.Contains("volume='if(lt(t,1),0.4,0.8)'"), "Audio expression was changed by GPU planning.");
    var cpu = ExportVideoPipeline.Create("libx264", "[0:v]format=yuv420p[v]");
    Require(!cpu.UsesGpuFrames && cpu.DeviceFlags.Count == 0, "CPU fallback inherited a CUDA device.");
    return Task.CompletedTask;
});

await Check("GPU: complete Main export keeps intro and speed changes in VRAM", async () =>
{
    string input = await EnsureGpuSource();
    var result = await MainGpuExport("gpu-main", input, worker =>
    {
        worker.IntroStillSec = 0.1;
        worker.SpeedSegments = [new(200, 800, 0.5)];
    });
    Require(result.gpu, "Main fell back from its resident graph. See gpu-main.log.");
    Require(await AudioRms(result.path) > 0.01, "Main GPU export lost audio.");
});

await Check("GPU: complete Merger scales and joins multiple clips in VRAM", async () =>
{
    string input = await EnsureGpuSource();
    using var worker = new MergerWorker(new ApplicationPaths(Path.Combine(work, "gpu-merge-state")))
    {
        InputFiles = [input, input], OutputDirectory = ExportDirectory("gpu-merge"),
        HardwareStrategy = "NVIDIA", QualityPercent = 45, AutoSpikeFlattening = false,
        MusicConfig = new JsonObject { ["main_vol"] = 0.8 },
        ClipTrims = [new(0.2, 1.2), new(0.5, 1.5)]
    };
    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var previous = CoreLogger.InfoAction;
    CoreLogger.InfoAction = (topic, message) => logs.Enqueue($"{topic}: {message}");
    try { await worker.RunAsync(); }
    finally { CoreLogger.InfoAction = previous; File.WriteAllLines(Path.Combine(work, "gpu-merge.log"), logs); }
    Require(success, worker.FailureDetail ?? output);
    Require(worker.UsedGpuVideoProcessing, "Merger fell back from its resident graph. See gpu-merge.log.");
    Require(await AudioRms(output) > 0.01, "GPU merge lost audio.");
});

await Check("GPU: portrait crop and fades retain their effects with hardware encoding", async () =>
{
    string input = await EnsureGpuSource();
    var result = await MainGpuExport("gpu-effects", input, worker =>
    {
        worker.IsMobileFormat = true;
        worker.StartTimeMs = 500;
        worker.EndTimeMs = 1500;
        worker.EnableFades = true;
    });
    Require(!result.gpu && result.description.Contains("NVENC"), "Effects did not retain GPU encoding.");
    var probe = new MediaProber(Path.Combine(root, "binaries", "ffprobe.exe"), result.path);
    Require(await probe.GetResolutionAsync() == (1080, 1920), "Portrait output size changed.");
});

await Check("GPU: unsupported hardware decoding retries once and retains NVENC", async () =>
{
    string input = Path.Combine(work, "software-decoder.mkv");
    await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
        "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", input);
    var result = await MainGpuExport("gpu-retry", input, _ => { });
    Require(!result.gpu && result.description.Contains("NVENC"), "Decoder failure discarded the hardware encoder.");
    string logs = File.ReadAllText(Path.Combine(work, "gpu-retry.log"));
    Require(logs.Split("Retrying the original effects with the same hardware encoder.").Length == 2,
        "Expected exactly one compatibility retry.");
});

await Check("GPU: size-locked NVENC export probes complexity and lands on target without a blind retry", async () =>
{
    // PROBE_01 — 20 s of 1080p60 content under a 25.0 MB size lock. The export must
    // MEASURE the clip's complexity with a 5 s NVENC CQ-20 middle slice (software
    // decode, null muxer), pull the -b:v ask below the naive budget by the calibrated
    // margin, and land inside the acceptance band on the FIRST encode. The blind
    // size-retry loop (attempt 2) must never be entered; it stays only as the safety
    // net for probe failures and foreign NVENC SDK behaviour.
    string input = Path.Combine(work, "nvenc-size-source.mp4");
    if (!File.Exists(input))
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=20",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=20",
            "-c:v", "h264_nvenc", "-preset", "p6", "-cq", "19",
            "-c:a", "aac", "-b:a", "128k", "-shortest", input);

    var result = await MainGpuExport("gpu-size-probe", input, worker =>
    {
        worker.EndTimeMs = 20000;
        worker.OriginalResolution = "1920x1080";
        worker.TargetMbOverride = 25.0;
    });
    Require(result.gpu, "Size-locked export fell back from its resident NVENC graph. See gpu-size-probe.log.");
    Require(result.worker.ComplexityProbeRan, "The NVENC complexity probe did not run.");
    Require(result.worker.LastComplexityProbeCommandLine != null &&
        !result.worker.LastComplexityProbeCommandLine.Contains("hwaccel"),
        "The probe injected hardware decode flags (zero-copy guardrail violated).");
    Require(result.worker.ComplexityProbeBitsPerSecond > 10_000_000 &&
        result.worker.ComplexityProbeBitsPerSecond < 120_000_000,
        $"Probe appetite {result.worker.ComplexityProbeBitsPerSecond:N0} bps is implausible for 1080p60 content.");
    Require(result.worker.ComplexityProbeScaleFactor is > 0.98 and < 1.0,
        $"Scale factor {result.worker.ComplexityProbeScaleFactor:F4} is not a calibrated sub-unity margin.");
    string log = File.ReadAllText(Path.Combine(work, "gpu-size-probe.log"));
    Require(log.Split("PROBE_01 complexity probe:").Length == 2,
        "The probe must run exactly once per export.");
    // The probe-scaled rate must actually reach the encoder: -b:v and -maxrate carry
    // the same probe-discounted value, below the 10358 kbps naive budget for this
    // exact 25.0 MB / 20 s / 127 kbps-audio configuration.
    var bv = System.Text.RegularExpressions.Regex.Match(log, @"-b:v (\d+)k");
    var mr = System.Text.RegularExpressions.Regex.Match(log, @"-maxrate (\d+)k");
    Require(bv.Success && mr.Success && bv.Groups[1].Value == mr.Groups[1].Value,
        "The probe-scaled rate must be applied to both -b:v and -maxrate.");
    Require(int.TryParse(bv.Groups[1].Value, out int appliedKbps) && appliedKbps > 9000 && appliedKbps <= 10358,
        $"Applied -b:v {bv.Groups[1].Value} does not look like the probe-discounted budget rate.");
    Require(!log.Contains("Export size target not met after retries"),
        "The export missed the acceptance band even with the probe.");
    Require(!log.Contains("Could not preserve the first render before retrying"),
        "The blind size retry was entered.");
    double mb = new FileInfo(result.path).Length / 1048576.0;
    Require(mb is >= 23.75 and <= 26.25,
        $"Landed at {mb:F2} MB — outside the 5% acceptance band around the 25.0 MB target.");
    Require(mb is >= 24.2 and <= 25.2,
        $"Landed at {mb:F2} MB — the probe should land near ~24.8 MB, not merely inside the band.");
});

await Check("Export errors: missing encoder classification", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("Unknown encoder 'h264_nvenc_test'");
    collector.AddStderrLine("Error initializing output stream 0:0 -- Error while opening encoder for output stream #0:0 - maybe incorrect parameters such as bit_rate, rate, width or height");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode", Encoder = "h264_nvenc_test" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);
    
    Require(failure.Category == ExportFailureCategory.MissingEncoder, $"Expected MissingEncoder, got {failure.Category}");
    Require(failure.Summary.Contains("encoder", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
    Require(failure.SpecificCause != null && failure.SpecificCause.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase), $"SpecificCause was {failure.SpecificCause}");
});

await Check("Export errors: corrupt input classification and explicit code", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("[mov,mp4,m4a,3gp,3g2,mj2 @ 000002] moov atom not found");
    collector.AddStderrLine("input.mp4: Invalid data found when processing input");
    collector.AddStderrLine("Task finished with error code: -1094995529 (Invalid data found when processing input)");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.CorruptInput, $"Expected CorruptInput, got {failure.Category}");
    Require(failure.NativeErrorCode == -1094995529, $"Expected -1094995529, got {failure.NativeErrorCode}");
    Require(failure.NativeErrorSource == "FFmpeg", $"Expected FFmpeg source, got {failure.NativeErrorSource}");
    Require(failure.Summary.Contains("corrupt", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: disk full classification", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("av_interleaved_write_frame(): No space left on device");
    collector.AddStderrLine("Task finished with error code: -28 (No space left on device)");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.DiskFull, $"Expected DiskFull, got {failure.Category}");
    Require(failure.NativeErrorCode == -28, $"Expected -28, got {failure.NativeErrorCode}");
    Require(failure.Summary.Contains("free space", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: access denied classification", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("output.mp4: Permission denied");
    collector.AddStderrLine("Task finished with error code: -13 (Permission denied)");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.AccessDenied, $"Expected AccessDenied, got {failure.Category}");
    Require(failure.NativeErrorCode == -13, $"Expected -13, got {failure.NativeErrorCode}");
    Require(failure.Summary.Contains("access", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: startup failure (invalid binary path) classification", async () =>
{
    await Task.CompletedTask;
    Exception? caughtEx = null;
    try
    {
        Process.Start(new ProcessStartInfo("C:\\NonExistent_Directory_12345\\ffmpeg_missing.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
    catch (Exception ex)
    {
        caughtEx = ex;
    }

    Require(caughtEx != null, "Process.Start unexpectedly succeeded with nonexistent path.");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "StartProcess" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: null, processStartException: caughtEx, isTimeout: false, isCancellation: false, collector: null);

    Require(failure.Category == ExportFailureCategory.StartupFailure, $"Expected StartupFailure, got {failure.Category}");
    Require(failure.NativeErrorCode == 2, $"Expected Win32 error 2, got {failure.NativeErrorCode}");
    Require(failure.NativeErrorSource == "Win32", $"Expected Win32 error source, got {failure.NativeErrorSource}");
    Require(failure.Summary.Contains("not found", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: timeout and cancellation classification", async () =>
{
    await Task.CompletedTask;
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var cancelFailure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: null, processStartException: null, isTimeout: false, isCancellation: true, collector: null);
    Require(cancelFailure.Category == ExportFailureCategory.Cancellation, $"Expected Cancellation, got {cancelFailure.Category}");
    Require(cancelFailure.Summary.Contains("cancelled", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {cancelFailure.Summary}");

    var timeoutFailure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: null, processStartException: null, isTimeout: true, isCancellation: false, collector: null);
    Require(timeoutFailure.Category == ExportFailureCategory.Timeout, $"Expected Timeout, got {timeoutFailure.Category}");
    Require(timeoutFailure.Summary.Contains("timed out", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {timeoutFailure.Summary}");
});

await Check("Export errors: early useful error preserved despite 100+ trailing diagnostic lines", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("[h264_qsv @ 000001] Error creating a MFX session: -9.");
    collector.AddStderrLine("[h264_qsv @ 000001] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.");
    for (int i = 0; i < 120; i++)
    {
        collector.AddStderrLine($"[routine_filter @ {i:D6}] Routine log line {i} with uninteresting details");
    }

    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode", Encoder = "h264_qsv" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.MissingEncoder, $"Expected MissingEncoder, got {failure.Category}");
    Require(failure.DiagnosticLines.Any(l => l.Contains("MFX session") || l.Contains("-9")), "Early MFX session error line was evicted from diagnostic lines.");
    Require(failure.SpecificCause != null && failure.SpecificCause.Contains("-9"), $"SpecificCause did not capture MFX error: {failure.SpecificCause}");
});

await Check("Export errors: unfamiliar error wording produces honest Unknown category", async () =>
{
    await Task.CompletedTask;
    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("Something totally bizarre and unique happened in subsystem XYZ-999");
    collector.AddStderrLine("Thread aborted due to unexplained status 9999");

    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(ExportStage.Encoding, attempt, processExitCode: 1, processStartException: null, isTimeout: false, isCancellation: false, collector: collector);

    Require(failure.Category == ExportFailureCategory.Unknown, $"Expected Unknown category, got {failure.Category}");
    Require(failure.NativeErrorCode == null, $"Expected null NativeErrorCode for generic exit code, got {failure.NativeErrorCode}");
    Require(failure.Summary.Contains("unexpected", StringComparison.OrdinalIgnoreCase), $"Unexpected summary: {failure.Summary}");
});

await Check("Export errors: GPU failure followed by successful fallback leaves LastFailure null", async () =>
{
    string input = Path.Combine(work, "software-decoder.mkv");
    if (!File.Exists(input))
    {
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", input);
    }

    using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "gpu-fallback-check-state")))
    {
        InputPath = input, OutputDirectory = ExportDirectory("gpu-fallback-check"),
        StartTimeMs = 0, EndTimeMs = 1800, OriginalResolution = "320x180",
        IsMobileFormat = false, HardwareStrategy = "NVIDIA", QualityLevel = 20,
        EnableFades = false,
        AutoSpikeFlattening = false
    };

    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    await worker.RunAsync();

    Require(success, worker.FailureDetail ?? output);
    Require(worker.LastFailure == null, $"LastFailure should be null on successful export, but was: {worker.LastFailure?.Summary}");
    Require(worker.FailureDetail == null, $"FailureDetail should be null on successful export, but was: {worker.FailureDetail}");
});

await Check("Export errors: old unrelated log entry in shared log cannot become current cause", async () =>
{
    await Task.CompletedTask;
    CoreLogger.Fail("OldJob", "Fatal: No space left on device while writing old video 9999");

    var collector = new FfmpegDiagnosticCollector();
    collector.AddStderrLine("Random pipeline error occurred during filtering");
    var attempt = new ExportAttemptIdentity { AttemptIndex = 1, Operation = "SinglePassEncode" };
    var failure = FfmpegErrorClassifier.Classify(
        ExportStage.Encoding, attempt,
        processExitCode: 1, processStartException: null,
        isTimeout: false, isCancellation: false,
        collector: collector);

    Require(failure.Category != ExportFailureCategory.DiskFull,
        $"Old log falsely caused failure category to become DiskFull.");
    Require(failure.Category == ExportFailureCategory.Unknown,
        $"Expected Unknown category, got {failure.Category}");
    Require(!failure.Summary.Contains("space", StringComparison.OrdinalIgnoreCase),
        $"Summary contained old log content: {failure.Summary}");
    Require(failure.SpecificCause == null || !failure.SpecificCause.Contains("space", StringComparison.OrdinalIgnoreCase),
        $"SpecificCause contained old log content: {failure.SpecificCause}");
    Require(!failure.DiagnosticLines.Any(l => l.Contains("space", StringComparison.OrdinalIgnoreCase)),
        $"DiagnosticLines contained old log content: {string.Join(", ", failure.DiagnosticLines)}");
});

await Check("A/V sync drift: cut and speed seams keep audio packets flush with video", async () =>
{
    const double DriftToleranceSec = 0.005;
    const int SampleRate = 48000;
    const double AacPacketSec = 1024.0 / SampleRate;
    string ffprobe = Path.Combine(root, "binaries", "ffprobe.exe");

    // AVSYNC_01 — the synthetic source. Ten seconds of 30 fps footage with a 1 kHz
    // beep whose ONSET sits exactly on every 1.0 s frame boundary (0.2 s beep,
    // 0.8 s silence). The beeps are drift probes: whatever the export graph does
    // to time, each surviving beep's decoded onset must equal the output second
    // that OutputTimeline predicts for its source second.
    string input = Path.Combine(work, "sync-source.mp4");
    string beep = "0.8*sin(2*PI*1000*t)*lt(mod(t\\,1)\\,0.2)";
    await Ffmpeg(
        "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=10",
        "-f", "lavfi", "-i", $"aevalsrc={beep}|{beep}:s=48000:d=10",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-shortest", input);

    // AVSYNC_01 — the edit under test, expressed exactly as the UI expresses it:
    // CutRange and SpeedSegment in ABSOLUTE source milliseconds. The cut swallows
    // the 2 s beep whole and closes the timeline up; the 0.5x segment doubles
    // 5-6 s. Expected finished video: [0,2) + [3,5) + [5,6)@0.5 + [6,10) =
    // exactly 10.0 s. Normalisation and the peak limiter are switched off on
    // purpose: they are gain stages orthogonal to the seam mathematics under
    // test, and the limiter's lookahead is itself a few milliseconds of filter
    // latency that would pollute the drift measurement.
    using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "sync-state")))
    {
        InputPath = input, OutputDirectory = ExportDirectory("sync-export"),
        StartTimeMs = 0, EndTimeMs = 10000, SpeedFactor = 1.0,
        SpeedSegments = [new SpeedSegment(5000, 6000, 0.5)],
        Cuts = [new CutRange(2000, 3000)],
        OriginalResolution = "320x180", IsMobileFormat = false,
        HardwareStrategy = "CPU", QualityLevel = 20,
        EnableFades = false,
        AutoSpikeFlattening = false
    };
    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    await worker.RunAsync();
    Require(success, worker.FailureDetail ?? output);

    // The timeline model the rest of the application already draws with is the
    // independent oracle: source second -> output second for each beep that
    // survives the cut. The canonical mapping is asserted too, so a change to
    // the model itself can never silently re-bless this check.
    var timeline = OutputTimeline.Create(10000, [new SpeedSegment(5000, 6000, 0.5)], 1.0, 0, null,
        [new OutputTimeline.Cut(2.0, 3.0)]);
    double[] expected = new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9 }.Select(s => timeline.SourceToOutput(s)).ToArray();
    double[] canonical = [0, 1, 2, 3, 4, 6, 7, 8, 9];
    for (int i = 0; i < canonical.Length; i++)
        Require(Math.Abs(expected[i] - canonical[i]) < 1e-9,
            $"OutputTimeline oracle returned {expected[i]:F4}s for beep {i + 1}, expected {canonical[i]:F4}s.");

    // AVSYNC_01 — packet-level truth on the finished file. The mix is AAC at
    // 48 kHz, so every full packet is exactly 1024 samples. If any seam drops,
    // duplicates or offsets audio, a pts gap stops being 1024/48000 long before
    // the drift can reach the 5 ms budget. (1 ms structural budget — far
    // tighter than the 5 ms DoD, and immune to ffprobe's 6-decimal rounding.)
    var audioPackets = JsonNode.Parse(await RunText(ffprobe, "-v", "error", "-select_streams", "a",
        "-show_entries", "packet=pts_time,duration_time", "-of", "json", output))!["packets"]!.AsArray();
    var audioPts = new List<double>();
    double lastDuration = 0;
    foreach (var packet in audioPackets)
    {
        if (double.TryParse(packet!["pts_time"]?.GetValue<string>(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double ptsTime)) audioPts.Add(ptsTime);
        if (double.TryParse(packet["duration_time"]?.GetValue<string>(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double durationTime)) lastDuration = durationTime;
    }
    audioPts.Sort();
    Require(audioPts.Count > 400, $"Only {audioPts.Count} audio packets in the export.");
    double worstGap = 0;
    for (int i = 1; i < audioPts.Count - 1; i++)
        worstGap = Math.Max(worstGap, Math.Abs(audioPts[i] - audioPts[i - 1] - AacPacketSec));
    // AAC's encoder priming is the ONE negative start allowed: the encoder emits
    // a leading frame the edit list discards, so raw packet pts may begin exactly
    // one frame before zero — decoded content still starts at zero, which the
    // beep-onset measurement below proves against the same budget.
    Require(audioPts[0] >= -AacPacketSec - 0.0005 && audioPts[0] <= DriftToleranceSec,
        $"Audio stream starts at {audioPts[0]:F6}s; only one AAC frame of encoder priming (-{AacPacketSec:F6}s) may precede zero.");
    Require(worstGap <= 0.001,
        $"Audio packet spacing drifted up to {worstGap * 1000:F3} ms; every full AAC frame must be exactly {AacPacketSec:F6}s.");
    Require(Math.Abs(audioPts[^1] + lastDuration - 10.0) <= DriftToleranceSec,
        $"Audio spans {audioPts[0]:F4}-{audioPts[^1] + lastDuration:F4}s; the finished edit must be exactly 10.0 s.");
    Console.WriteLine($"  Audio packets: {audioPts.Count}, first={audioPts[0]:F6}s, last={audioPts[^1]:F6}s (+{lastDuration:F6}s), worst gap error {worstGap * 1000:F3} ms.");
    // AVSYNC-PART2 — the video side of the same contract: 600 frames of strict
    // CFR 60 starting at zero, and a keyframe sitting exactly on each seam
    // second (the exporter's 2 s GOP puts an IDR on every concat boundary:
    // 0/2/4/6/8 s). B-frames reorder packets, so pts are sorted before the
    // uniform-spacing assertion.
    var videoPackets = JsonNode.Parse(await RunText(ffprobe, "-v", "error", "-select_streams", "v",
        "-show_entries", "packet=pts_time,flags", "-of", "json", output))!["packets"]!.AsArray();
    var frames = videoPackets
        .Select(p => (Pts: double.Parse(p!["pts_time"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            Keyframe: (p["flags"]?.GetValue<string>() ?? "").Contains('K')))
        .OrderBy(f => f.Pts)
        .ToArray();
    Require(frames.Length == 600, $"Expected 600 CFR frames (10 s at 60 fps), found {frames.Length}.");
    Require(Math.Abs(frames[0].Pts) <= 0.0005, $"Video starts at {frames[0].Pts:F4}s instead of zero.");
    double worstFrameGap = 0;
    for (int i = 1; i < frames.Length; i++)
        worstFrameGap = Math.Max(worstFrameGap, Math.Abs(frames[i].Pts - frames[i - 1].Pts - 1.0 / 60.0));
    Require(worstFrameGap <= 0.001,
        $"Video is not uniformly 60 fps: worst frame gap error {worstFrameGap * 1000:F3} ms.");
    foreach (double seam in new[] { 2.0, 4.0, 6.0 })
    {
        var onSeam = frames.Where(f => Math.Abs(f.Pts - seam) <= 0.001).ToArray();
        Require(onSeam.Length > 0, $"No video frame lands on the {seam}s seam.");
        Require(onSeam.Any(f => f.Keyframe), $"No keyframe lands on the {seam}s seam.");
    }

    // AVSYNC-PART2 — decode the finished mix and measure where each beep
    // actually begins. A 1 ms backward-looking peak envelope feeds a rising-edge
    // gate (on above 30% of the beep plateau, off below 5%); the backward window
    // keeps the detected edge a fraction of a millisecond after the true one, so
    // the whole 5 ms budget stays available for real drift.
    string raw = Path.Combine(work, "sync-output-audio.raw");
    await Ffmpeg("-i", output, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", raw);
    byte[] bytes = File.ReadAllBytes(raw);
    float[] samples = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    Require(samples.Length >= SampleRate * 10 && samples.Length <= SampleRate * 10 + 1024,
        $"Decoded to {samples.Length} samples; the edit-list-trimmed mix must be exactly {SampleRate * 10} (plus at most one padded AAC tail frame).");
    int window = SampleRate / 1000;
    var onsets = new List<double>();
    bool loud = false;
    for (int i = 0; i < samples.Length; i++)
    {
        double windowPeak = 0;
        for (int j = Math.Max(0, i - window + 1); j <= i; j++)
        {
            double a = Math.Abs(samples[j]);
            if (a > windowPeak) windowPeak = a;
        }
        if (!loud && windowPeak > 0.30) { onsets.Add(i / (double)SampleRate); loud = true; }
        else if (loud && windowPeak < 0.05) loud = false;
    }
    string found = string.Join(", ", onsets.Select(o => o.ToString("F4", CultureInfo.InvariantCulture)));
    Require(onsets.Count == canonical.Length,
        $"Expected {canonical.Length} beeps after the cut, found {onsets.Count}: [{found}].");
    // The beep at output 0.0 sits on TWO by-design amplitude ramps: the SPLICE_01
    // de-click fade into its chunk and the AAC decoder's first-frame
    // reconstruction after the edit-list priming trim. Both delay the envelope
    // crossing (~10 ms combined) without moving the content — the packet checks
    // above prove the t=0 boundary exactly (one priming frame, then zero drift).
    const double StartOfStreamBudgetSec = 0.012;
    double worstDrift = 0;
    int worstIndex = 0;
    bool exceeded = false;
    for (int i = 0; i < Math.Min(onsets.Count, canonical.Length); i++)
    {
        double drift = Math.Abs(onsets[i] - canonical[i]);
        if (drift > worstDrift) { worstDrift = drift; worstIndex = i; }
        if (drift > (i == 0 ? StartOfStreamBudgetSec : DriftToleranceSec)) exceeded = true;
    }
    Require(!exceeded,
        $"Beep {worstIndex + 1} landed at {onsets[worstIndex]:F4}s, expected {canonical[worstIndex]:F4}s " +
        $"(drift {(onsets[worstIndex] - canonical[worstIndex]) * 1000:+0.00;-0.00} ms, budget 5 ms). Onsets: [{found}].");
    Console.WriteLine($"  Max A/V drift across the 2 s cut and 0.5x seams: {worstDrift * 1000:F2} ms over {onsets.Count} beeps.");
});

if (failures.Count > 0)
{
    foreach (string failure in failures) Console.Error.WriteLine(failure);
    Console.Error.WriteLine($"Failures retained for inspection at: {work}");
    return 1;
}

if (!args.Contains("--keep-artifacts"))
{
    try
    {
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Could not remove test artifacts: {ex.Message}");
    }
}

Console.WriteLine("All checks passed. Test artifacts cleaned.");
return 0;

static async Task<byte[]> ThumbPng(string ffmpegExe, string file, string seek)
{
    // The exact MemeThumbnailCache subprocess (LIBAVFRAME_04 fallback path).
    var psi = new ProcessStartInfo(ffmpegExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-ss", seek, "-i", file, "-frames:v", "1", "-vf", "scale=160:-2", "-f", "image2pipe", "-c:v", "png", "pipe:1" })
        psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    using var ms = new MemoryStream();
    Task copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
    Task<string> err = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    await copy;
    await err;
    return p.ExitCode == 0 ? ms.ToArray() : Array.Empty<byte>();
}
static DecodedVideoFrame DecodePng(byte[] png)
{
    using var decoded = SkiaSharp.SKBitmap.Decode(png) ?? throw new InvalidDataException("undecodable PNG");
    using var bgra = decoded.Copy(SkiaSharp.SKColorType.Bgra8888)!;
    return new DecodedVideoFrame(bgra.GetPixelSpan().ToArray(), bgra.Width, bgra.Height, bgra.RowBytes, null);
}
static (double mae, double psnr) ImageDiff(DecodedVideoFrame a, DecodedVideoFrame b)
{
    double sum = 0, sq = 0; long n = 0;
    for (int y = 0; y < a.Height; y++)
        for (int x = 0; x < a.Width; x++)
            for (int c = 0; c < 3; c++)
            {
                int d = a.Pixels[y * a.Stride + x * 4 + c] - b.Pixels[y * b.Stride + x * 4 + c];
                sum += Math.Abs(d); sq += d * d; n++;
            }
    double mse = sq / n;
    return (sum / n, mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse));
}
async Task Check(string name, Func<Task> check)
{
    // LIBAV-SOAKDIAG: opt-in only. --libav-soak-diag runs it and nothing else.
    if (args.Contains("--libav-soak-diag") != name.StartsWith("LIBAV-SOAKDIAG:", StringComparison.Ordinal)) return;
    // LIBAV-SOAK: opt-in only (minutes long). --libav-soak runs it and nothing else.
    if (args.Contains("--libav-soak") != name.StartsWith("LIBAV-SOAK:", StringComparison.Ordinal)) return;
    if (args.Contains("--gpu-only") && !name.StartsWith("GPU:", StringComparison.Ordinal)) return;
    if (args.Contains("--libav-only") && !name.StartsWith("LIBAV:", StringComparison.Ordinal)) return;
    if (args.Contains("--skip-libav") && name.StartsWith("LIBAV:", StringComparison.Ordinal)) return;
    try { await check(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failures.Add("FAIL: " + name + " — " + ex.Message); }
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
async Task<(int exit, string error)> Run(string[] arguments)
{
    return await RunExecutable(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", ..arguments]);
}
async Task<(int exit, string error)> RunExecutable(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    using var kill = deadline.Token.Register(() => { try { process.Kill(true); } catch { } });
    await process.WaitForExitAsync(deadline.Token);
    await stdout;
    return (process.ExitCode, await stderr);
}
async Task<string> RunText(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    string stdout = await process.StandardOutput.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    using var kill = deadline.Token.Register(() => { try { process.Kill(true); } catch { } });
    await process.WaitForExitAsync(deadline.Token);
    string error = await stderr;
    Require(process.ExitCode == 0, error);
    return stdout;
}
async Task Ffmpeg(params string[] arguments)
{
    var result = await Run(arguments);
    Require(result.exit == 0, result.error);
}
async Task<float[]> MixSamples(double game, double music)
{
    var config = new JsonObject { ["main_vol"] = game, ["music_vol"] = music, ["ducking_enabled"] = false, ["carving_enabled"] = false };
    var graph = AudioFilterChain.Build(config, 0, 1, 1, true, 0, null,
        musicTracks: [new("music.wav", 0, 1)], totalProjectDuration: 1);
    string path = Path.Combine(work, $"mix-{game.ToString(CultureInfo.InvariantCulture)}-{music.ToString(CultureInfo.InvariantCulture)}.raw");
    await Ffmpeg("-f", "lavfi", "-i", "aevalsrc=0.1*sin(2*PI*440*t)|0:s=48000:d=1",
        "-f", "lavfi", "-i", "aevalsrc=0|0.1*sin(2*PI*880*t):s=48000:d=1",
        "-filter_complex", string.Join(";", graph.chains), "-map", graph.finalLabel, "-t", "1", "-f", "f32le", path);
    byte[] bytes = File.ReadAllBytes(path);
    float[] samples = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    return samples;
}
async Task<double> AudioRms(string media)
{
    string raw = media + ".audio.raw";
    await Ffmpeg("-i", media, "-vn", "-ac", "2", "-f", "f32le", raw);
    byte[] bytes = File.ReadAllBytes(raw);
    Require(bytes.Length > 0, "Export has no decoded audio samples.");
    float[] samples = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    return Rms(samples, 0);
}
static double Rms(float[] samples, int channel)
{
    double sum = 0;
    for (int i = channel; i < samples.Length; i += 2) sum += samples[i] * samples[i];
    return Math.Sqrt(sum / (samples.Length / 2));
}

string ExportDirectory(string name) => Directory.CreateDirectory(Path.Combine(work, name)).FullName;

async Task<string> EnsureGpuSource()
{
    string input = Path.Combine(work, "gpu-source.mp4");
    if (!File.Exists(input))
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", input);
    return input;
}

async Task<(string path, bool gpu, string description, ProcessWorker worker)> MainGpuExport(string name, string input, Action<ProcessWorker> configure)
{
    using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, name + "-state")))
    {
        InputPath = input, OutputDirectory = ExportDirectory(name),
        StartTimeMs = 0, EndTimeMs = 1800, OriginalResolution = "320x180",
        IsMobileFormat = false, HardwareStrategy = "NVIDIA", QualityLevel = 20,
        EnableFades = false,
        AutoSpikeFlattening = false
    };
    configure(worker);
    bool success = false;
    string output = "";
    worker.Finished += (ok, message) => { success = ok; output = message; };
    var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var previousInfo = CoreLogger.InfoAction;
    var previousFail = CoreLogger.FailAction;
    CoreLogger.InfoAction = (topic, message) => logs.Enqueue($"{topic}: {message}");
    CoreLogger.FailAction = (topic, message) => logs.Enqueue($"ERROR {topic}: {message}");
    try { await worker.RunAsync(); }
    finally
    {
        CoreLogger.InfoAction = previousInfo;
        CoreLogger.FailAction = previousFail;
        File.WriteAllLines(Path.Combine(work, name + ".log"), logs);
    }
    Require(success, worker.FailureDetail ?? output);
    return (output, worker.UsedGpuVideoProcessing, worker.LastVideoPipeline, worker);
}
