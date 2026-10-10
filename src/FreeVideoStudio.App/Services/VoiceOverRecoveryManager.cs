// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

internal enum TakeRecoveryState
{
    Pending,
    Validating,
    RecoveredInSession,
    Discarded,
    Committed
}

/// <summary>VORECOVERY_01 — why a take is (still) pending. Validation, storage and publication
/// failures are distinct: a rejected UI publication is never reported as corrupt audio.</summary>
internal enum RecoveryErrorKind
{
    None,
    Capture,
    Validation,
    Storage,
    Publication
}

internal sealed class PendingFailedTake
{
    public string Id { get; }
    public VoiceOverWindow.VoiceOverSession Session { get; }
    public CapturedTake? TakeOutcome { get; }
    public CapturedTake? Outcome => TakeOutcome;
    public string FailureReason { get; set; }
    public RecoveryErrorKind ErrorKind { get; set; } = RecoveryErrorKind.Capture;
    public TakeRecoveryState State { get; set; } = TakeRecoveryState.Pending;
    public DateTime TimestampUtc { get; } = DateTime.UtcNow;
    public string? SourceManifestPath { get; set; }

    /// <summary>Legacy alias. NOT a persistence decision — see <see cref="MustRemainDurable"/>.</summary>
    public bool DiscardedByUser
    {
        get => State == TakeRecoveryState.Discarded;
        set { if (value && State == TakeRecoveryState.Pending) State = TakeRecoveryState.Discarded; }
    }

    /// <summary>Predicate 1 — blocks Apply.</summary>
    public bool BlocksApply => State == TakeRecoveryState.Pending || State == TakeRecoveryState.Validating;

    /// <summary>Predicate 2 — must stay in the durable recovery index (discoverable after reopen).</summary>
    public bool MustRemainDurable => State != TakeRecoveryState.Discarded && State != TakeRecoveryState.Committed;

    /// <summary>Predicate 3 — terminal: this take's record may be removed from storage.</summary>
    public bool IsTerminal => !MustRemainDurable;

    public bool IsResolved => !BlocksApply;

    public PendingFailedTake(VoiceOverWindow.VoiceOverSession session, CapturedTake? takeOutcome, string failureReason, string? id = null)
    {
        Id = !string.IsNullOrWhiteSpace(id) ? id : Guid.NewGuid().ToString("N");
        Session = session;
        TakeOutcome = takeOutcome;
        FailureReason = failureReason;
    }
}

internal sealed record RecoveryBatchResult(int RecoveredCount, int FailedCount, string? ErrorMessage)
{
    /// <summary>Non-null when the takes changed state but the durable index could not be updated.</summary>
    public string? StorageError { get; init; }
}

/// <summary>VORECOVERY_01 — truthful outcome of a recovery-index read/write. Never a silent void.</summary>
internal sealed record RecoveryStoreResult(bool Success, string? Error)
{
    public static readonly RecoveryStoreResult Ok = new(true, null);

    /// <summary>VORECOVERY_02 — takes this manager still held as durable that another session had
    /// already committed/discarded. The stale transition was rejected; the manager adopts the
    /// terminal state instead of resurrecting the record.</summary>
    public IReadOnlyDictionary<string, TakeRecoveryState>? ResolvedElsewhere { get; init; }
}

/// <summary>Outcome of validating one recovered WAV off the UI thread.</summary>
internal sealed record RecoveryAudioVerdict(bool IsValid, double DurationSec, string? Error);

/// <summary>
/// VORECOVERY_01 — every file-system touch of the recovery transaction goes through this seam so
/// the manager can run it off the dispatcher and tests can gate/fail it deterministically.
/// </summary>
internal interface IVoiceOverRecoveryStore
{
    IReadOnlyList<string> EnumerateManifests(string directory, string searchPattern);
    bool ManifestExists(string path);
    string ReadManifest(string path);
    /// <summary>Unique temp file + flush-to-disk + atomic replace. On failure the previous file is untouched.</summary>
    void WriteManifestAtomic(string path, byte[] content);
    void DeleteManifest(string path);
    bool AudioExists(string path);
    /// <summary>Deletes the audio and proves it is gone; throws if it is still present.</summary>
    void DeleteAudio(string path);
    RecoveryAudioVerdict ValidateAudio(string path);

    /// <summary>VORECOVERY_02 — cross-PROCESS exclusion for one read-merge-write of the recovery
    /// folder (the static gate only covers this process). Null = the store needs no OS lock.</summary>
    IDisposable? AcquireStoreLock(string directory) => null;
}

internal sealed class FileVoiceOverRecoveryStore : IVoiceOverRecoveryStore
{
    public static readonly FileVoiceOverRecoveryStore Instance = new();

    public IReadOnlyList<string> EnumerateManifests(string directory, string searchPattern)
        => Directory.Exists(directory) ? Directory.GetFiles(directory, searchPattern) : Array.Empty<string>();

    public bool ManifestExists(string path) => File.Exists(path);

    public IDisposable? AcquireStoreLock(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "voiceover_recovery.lock");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1); }
            catch (IOException ex) when (DateTime.UtcNow < deadline)
            {
                RuntimeLog.SwallowedThrottled(ex);   // another process holds the folder lock; retry until the deadline
                System.Threading.Thread.Sleep(25);
            }
        }
    }

    public string ReadManifest(string path) => File.ReadAllText(path);

    public void WriteManifestAtomic(string path, byte[] content)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // VORECOVERY_01 — a per-write temp name: two writers can never share or clobber one ".tmp".
        string tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(content, 0, content.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tmpPath);
            throw;
        }
    }

    private static void TryDeleteTemp(string tmpPath)
    {
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }   // the original write failure is rethrown to the caller
    }

    public void DeleteManifest(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public bool AudioExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public void DeleteAudio(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        if (File.Exists(path)) throw new IOException("Audio file locked by another process");
    }

    public RecoveryAudioVerdict ValidateAudio(string path)
    {
        if (!File.Exists(path)) return new RecoveryAudioVerdict(false, 0, "Audio file missing on disk");
        if (new FileInfo(path).Length < 44) return new RecoveryAudioVerdict(false, 0, "Audio file is incomplete or truncated (< 44 bytes)");
        try
        {
            using var reader = new WavAudioReader(path);
            if (reader.Length <= 0 || reader.TotalTime.TotalSeconds <= 0)
                return new RecoveryAudioVerdict(false, 0, "Audio file contains zero audio samples");
            var buf = new float[256];
            if (reader.Read(buf, 0, buf.Length) <= 0)
                return new RecoveryAudioVerdict(false, 0, "Audio file sample data is unreadable");
            return new RecoveryAudioVerdict(true, reader.TotalTime.TotalSeconds, null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);   // surfaced to the user as the verdict's Error
            return new RecoveryAudioVerdict(false, 0, $"Corrupt audio file: {ex.Message}");
        }
    }
}

/// <summary>
/// VORECOVERY_01 — durable failed-take recovery for Voice Over Studio.
/// <para>Threading: the in-memory take list is owned by the caller's thread (the UI dispatcher in
/// production). Every async method snapshots state on that thread, performs ALL file-system work
/// off-thread through <see cref="IVoiceOverRecoveryStore"/>, and publishes results back on the
/// caller's context. One recovery operation is owned at a time (<see cref="IsOperationInFlight"/>);
/// a caller that stops waiting does not end that ownership.</para>
/// <para>Storage: one manifest per (canonical video, recording session) —
/// <c>voiceover_recovery_{videoKey}_{sessionId}.json</c>. Each take carries a serialized stable id.
/// Writes are process-serialized, merge by take id (records this manager does not hold are
/// preserved), use a unique temp file and atomic replace, and a newer snapshot is never
/// overwritten by an older one.</para>
/// </summary>
internal sealed class VoiceOverRecoveryManager
{
    internal const int SchemaVersion = 2;
    internal const string UnboundVideoKey = "unbound";

    private static readonly object WriteGate = new();
    private static readonly Dictionary<string, long> LastWrittenGeneration = new(StringComparer.OrdinalIgnoreCase);
    private static long _generationCounter;

    private readonly Func<string> _videoPathProvider;
    private readonly Func<string> _baseDirProvider;
    private readonly IVoiceOverRecoveryStore _store;
    private readonly List<PendingFailedTake> _pendingFailedTakes = new();
    private readonly HashSet<string> _trackedManifestFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadErrors = new();
    private Task<RecoveryStoreResult> _lastPersist = Task.FromResult(RecoveryStoreResult.Ok);

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string? TestManifestDirectory { get; set; }
    public IReadOnlyList<PendingFailedTake> PendingFailedTakes => _pendingFailedTakes;
    public bool HasUnresolvedFailedTakes => _pendingFailedTakes.Exists(t => t.BlocksApply);
    public bool HasDurableTakes => _pendingFailedTakes.Exists(t => t.MustRemainDurable);
    /// <summary>True while a recover / discard / discovery operation owns the take list.</summary>
    public bool IsOperationInFlight { get; private set; }
    /// <summary>Manifests that exist but could not be read — preserved on disk, surfaced to the user.</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;
    public RecoveryStoreResult LastStoreResult { get; private set; } = RecoveryStoreResult.Ok;

    public VoiceOverRecoveryManager(Func<string> videoPathProvider, Func<string> baseDirProvider, IVoiceOverRecoveryStore? store = null)
    {
        _videoPathProvider = videoPathProvider ?? throw new ArgumentNullException(nameof(videoPathProvider));
        _baseDirProvider = baseDirProvider ?? throw new ArgumentNullException(nameof(baseDirProvider));
        _store = store ?? FileVoiceOverRecoveryStore.Instance;
    }

    /// <summary>The latest persistence started by this manager (for tests and close-time observers).</summary>
    public Task<RecoveryStoreResult> WhenStoreIdle() => _lastPersist;

    private static string CanonicalVideo(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return "";
        try { return Path.GetFullPath(videoPath.Trim()); }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);   // an unusable path is treated as "no video", which is isolated under the unbound key
            return "";
        }
    }

    /// <summary>VORECOVERY_01 — an empty/invalid identity is NOT "default": it maps to a dedicated
    /// unbound key and only ever matches other unbound manifests.</summary>
    public static string ComputeVideoKey(string? videoPath)
    {
        string canonical = CanonicalVideo(videoPath);
        if (canonical.Length == 0) return UnboundVideoKey;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant()));
        return Convert.ToHexString(hash).Substring(0, 16);
    }

    private string BaseDirectory() => TestManifestDirectory ?? _baseDirProvider();

    public string GetRecoveryManifestPath()
    {
        string path = Path.Combine(BaseDirectory(), $"voiceover_recovery_{ComputeVideoKey(_videoPathProvider())}_{SessionId}.json");
        _trackedManifestFiles.Add(path);
        return path;
    }

    // ───────────────────────────── register ─────────────────────────────

    private PendingFailedTake AddFailedTake(VoiceOverWindow.VoiceOverSession session, CapturedTake? takeOutcome, string failureReason)
    {
        var pending = new PendingFailedTake(session, takeOutcome, failureReason)
        {
            SourceManifestPath = GetRecoveryManifestPath(),
            ErrorKind = RecoveryErrorKind.Capture
        };
        _pendingFailedTakes.Add(pending);
        return pending;
    }

    /// <summary>Synchronous variant (tests / non-UI callers). Returns the truthful storage result.</summary>
    public RecoveryStoreResult RegisterFailedTake(VoiceOverWindow.VoiceOverSession session, CapturedTake? takeOutcome, string failureReason)
    {
        AddFailedTake(session, takeOutcome, failureReason);
        return SaveManifest();
    }

    /// <summary>UI variant: the take is registered in memory at once; the index write runs off-thread.</summary>
    public Task<RecoveryStoreResult> RegisterFailedTakeAsync(VoiceOverWindow.VoiceOverSession session, CapturedTake? takeOutcome, string failureReason)
    {
        AddFailedTake(session, takeOutcome, failureReason);
        return PersistAsync();
    }

    // ───────────────────────────── persistence ─────────────────────────────

    private sealed record TakeRecord(string Id, string WavPath, double StartSec, double EndSec, double TrimLeftSec,
        double TrimRightSec, bool IsMuted, string Reason, RecoveryErrorKind ErrorKind, TakeRecoveryState State);

    private sealed record PathPlan(string Path, bool IsOwn, HashSet<string> HeldIds, List<TakeRecord> Durable);

    private sealed record PersistPlan(long Generation, string SessionId, string CanonicalVideo, string Directory,
        List<PathPlan> Paths, List<(string Id, TakeRecoveryState State)> Terminal);

    // ── VORECOVERY_02: durable terminal resolutions ("tombstones") ──
    // A commit/discard is recorded in voiceover_tombstones_{videoKey}.json BEFORE the take's record
    // leaves its manifest. Every write and every discovery consults it, so a stale snapshot (a
    // manager that loaded the take earlier and still believes it Pending) can never recreate a
    // resolved take — not even after its manifest was deleted. Entries expire after 30 days.
    internal static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(30);

    internal static string TombstonePath(string directory, string canonicalVideo)
        => Path.Combine(directory, $"voiceover_tombstones_{ComputeVideoKey(canonicalVideo)}.json");

    private static Dictionary<string, (TakeRecoveryState State, DateTime Utc)> ReadTombstones(IVoiceOverRecoveryStore store, string path)
    {
        var map = new Dictionary<string, (TakeRecoveryState, DateTime)>(StringComparer.Ordinal);
        if (!store.ManifestExists(path)) return map;
        using var doc = JsonDocument.Parse(store.ReadManifest(path));   // unreadable → throw → caller fails truthfully
        if (doc.RootElement.TryGetProperty("resolved", out var resolved) && resolved.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in resolved.EnumerateObject())
            {
                var state = Enum.TryParse<TakeRecoveryState>(Str(entry.Value, "state"), out var st) && st is TakeRecoveryState.Committed or TakeRecoveryState.Discarded
                    ? st : TakeRecoveryState.Committed;
                var utc = DateTime.TryParse(Str(entry.Value, "utc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var u) ? u : DateTime.UtcNow;
                map[entry.Name] = (state, utc);
            }
        }
        return map;
    }

    private static byte[] SerializeTombstones(Dictionary<string, (TakeRecoveryState State, DateTime Utc)> map)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteStartObject("resolved");
            foreach (var (id, (state, utc)) in map)
            {
                w.WriteStartObject(id);
                w.WriteString("state", state.ToString());
                w.WriteString("utc", utc.ToUniversalTime().ToString("O"));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private PersistPlan BuildPersistPlan()
    {
        string own = GetRecoveryManifestPath();
        var paths = new List<PathPlan>();
        var terminal = new List<(string, TakeRecoveryState)>();
        foreach (var t in _pendingFailedTakes)
            if (t.IsTerminal) terminal.Add((t.Id, t.State));
        foreach (string path in _trackedManifestFiles)
        {
            var held = new HashSet<string>(StringComparer.Ordinal);
            var durable = new List<TakeRecord>();
            foreach (var t in _pendingFailedTakes)
            {
                if (!string.Equals(t.SourceManifestPath ?? own, path, StringComparison.OrdinalIgnoreCase)) continue;
                held.Add(t.Id);
                if (t.MustRemainDurable)
                {
                    var s = t.Session;
                    durable.Add(new TakeRecord(t.Id, s.WavPath, s.StartSec, s.EndSec, s.TrimLeftSec, s.TrimRightSec,
                        s.IsMuted, t.FailureReason, t.ErrorKind, t.State));
                }
            }
            paths.Add(new PathPlan(path, string.Equals(path, own, StringComparison.OrdinalIgnoreCase), held, durable));
        }
        long gen = System.Threading.Interlocked.Increment(ref _generationCounter);
        return new PersistPlan(gen, SessionId, CanonicalVideo(_videoPathProvider()), Path.GetDirectoryName(own) ?? BaseDirectory(), paths, terminal);
    }

    /// <summary>Synchronous persistence (tests / non-UI callers). Production UI uses <see cref="PersistAsync"/>.</summary>
    public RecoveryStoreResult SaveManifest()
    {
        var result = ExecutePlan(BuildPersistPlan(), _store);
        ApplyResolvedElsewhere(result);
        LastStoreResult = result;
        _lastPersist = Task.FromResult(result);
        return result;
    }

    /// <summary>Snapshot on the caller's thread, write off-thread. The returned result is truthful.</summary>
    public Task<RecoveryStoreResult> PersistAsync()
    {
        var task = PersistCoreAsync(BuildPersistPlan());
        _lastPersist = task;
        return task;
    }

    private async Task<RecoveryStoreResult> PersistCoreAsync(PersistPlan plan)
    {
        var store = _store;
        var result = await Task.Run(() => ExecutePlan(plan, store));
        ApplyResolvedElsewhere(result);   // back on the caller's context (UI thread in production)
        LastStoreResult = result;
        return result;
    }

    /// <summary>VORECOVERY_02 — a known commit/discard dominates this manager's stale view.</summary>
    private void ApplyResolvedElsewhere(RecoveryStoreResult result)
    {
        if (result.ResolvedElsewhere is not { Count: > 0 } resolved) return;
        foreach (var take in _pendingFailedTakes)
        {
            if (take.MustRemainDurable && resolved.TryGetValue(take.Id, out var state))
            {
                RuntimeLog.Warn("VoiceOver", $"VORECOVERY_02 take {take.Id} was already {state} by another session; stale state rejected.");
                take.State = state;
                take.FailureReason = $"Already {state.ToString().ToLowerInvariant()} in another Voice Over session";
            }
        }
    }

    private static RecoveryStoreResult ExecutePlan(PersistPlan plan, IVoiceOverRecoveryStore store)
    {
        var errors = new List<string>();
        var resolvedElsewhere = new Dictionary<string, TakeRecoveryState>(StringComparer.Ordinal);
        lock (WriteGate)
        {
            IDisposable? crossProcessLock;
            try { crossProcessLock = store.AcquireStoreLock(plan.Directory); }
            catch (Exception ex)
            {
                RuntimeLog.Fail("VoiceOver", $"VORECOVERY_02 recovery folder is locked by another process; nothing written. {ex.Message}");
                return new RecoveryStoreResult(false, $"Recovery index is busy in another process ({ex.Message})");
            }
            using (crossProcessLock)
            {
                // 1. Terminal resolutions are made durable FIRST. If that fails, no record is removed.
                string tombPath = TombstonePath(plan.Directory, plan.CanonicalVideo);
                Dictionary<string, (TakeRecoveryState State, DateTime Utc)> tombs;
                try
                {
                    tombs = ReadTombstones(store, tombPath);
                    bool changed = false;
                    var cutoff = DateTime.UtcNow - TombstoneRetention;
                    foreach (var id in tombs.Where(kv => kv.Value.Utc < cutoff).Select(kv => kv.Key).ToList()) { tombs.Remove(id); changed = true; }
                    foreach (var (id, state) in plan.Terminal)
                    {
                        if (tombs.ContainsKey(id)) continue;
                        tombs[id] = (state, DateTime.UtcNow);
                        changed = true;
                    }
                    if (changed)
                    {
                        if (tombs.Count == 0) store.DeleteManifest(tombPath);
                        else store.WriteManifestAtomic(tombPath, SerializeTombstones(tombs));
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceOver", $"VORECOVERY_02 resolution record could not be read/updated; no recovery index was changed. {ex.Message}");
                    return new RecoveryStoreResult(false, $"Recovery resolution record could not be saved ({ex.Message})");
                }

                // 2. Manifests: merge by id; resolved ids never survive or come back.
                foreach (var p in plan.Paths)
                {
                    // Ordering guard is PER MANAGER: it only stops this manager's older snapshot from
                    // overwriting its own newer one. Cross-manager conflicts are decided by step 1.
                    string genKey = plan.SessionId + "|" + p.Path;
                    if (LastWrittenGeneration.TryGetValue(genKey, out long written) && written > plan.Generation)
                        continue;
                    try
                    {
                        var preserved = new List<JsonElement>();
                        string sessionId = plan.SessionId;
                        string video = plan.CanonicalVideo;
                        if (store.ManifestExists(p.Path))
                        {
                            string existing = store.ReadManifest(p.Path);
                            using var doc = JsonDocument.Parse(existing);   // unreadable → throw → file left untouched
                            var root = doc.RootElement;
                            if (!p.IsOwn)
                            {
                                if (root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String) sessionId = sid.GetString() ?? sessionId;
                                if (root.TryGetProperty("videoPath", out var vp) && vp.ValueKind == JsonValueKind.String) video = vp.GetString() ?? video;
                            }
                            if (root.TryGetProperty("failedTakes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in arr.EnumerateArray())
                                {
                                    string id = EntryId(item);
                                    if (id.Length > 0 && (p.HeldIds.Contains(id) || tombs.ContainsKey(id))) continue;   // ours, or resolved
                                    preserved.Add(item.Clone());
                                }
                            }
                        }

                        var durable = new List<TakeRecord>(p.Durable.Count);
                        foreach (var d in p.Durable)
                        {
                            if (tombs.TryGetValue(d.Id, out var tomb)) resolvedElsewhere[d.Id] = tomb.State;   // stale: rejected
                            else durable.Add(d);
                        }

                        if (preserved.Count == 0 && durable.Count == 0)
                        {
                            store.DeleteManifest(p.Path);
                        }
                        else
                        {
                            store.WriteManifestAtomic(p.Path, Serialize(sessionId, video, preserved, durable));
                        }
                        LastWrittenGeneration[genKey] = plan.Generation;
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Fail("VoiceOver", $"VORECOVERY_01 recovery index '{Path.GetFileName(p.Path)}' could not be updated; previous file preserved. {ex.Message}");
                        errors.Add($"{Path.GetFileName(p.Path)}: {ex.Message}");
                    }
                }
            }
        }
        var result = errors.Count == 0 ? RecoveryStoreResult.Ok : new RecoveryStoreResult(false, "Recovery index could not be saved (" + string.Join("; ", errors) + ")");
        return resolvedElsewhere.Count == 0 ? result : result with { ResolvedElsewhere = resolvedElsewhere };
    }

    private static byte[] Serialize(string sessionId, string video, List<JsonElement> preserved, List<TakeRecord> takes)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SchemaVersion);
            w.WriteString("sessionId", sessionId);
            w.WriteString("videoPath", video);
            w.WriteString("timestampUtc", DateTime.UtcNow.ToString("O"));
            w.WriteStartArray("failedTakes");
            foreach (var e in preserved) e.WriteTo(w);
            foreach (var t in takes)
            {
                w.WriteStartObject();
                w.WriteString("id", t.Id);
                w.WriteString("wavPath", t.WavPath);
                w.WriteNumber("startSec", Finite(t.StartSec));
                w.WriteNumber("endSec", Finite(t.EndSec));
                w.WriteNumber("trimLeftSec", Finite(t.TrimLeftSec));
                w.WriteNumber("trimRightSec", Finite(t.TrimRightSec));
                w.WriteBoolean("isMuted", t.IsMuted);
                w.WriteString("reason", t.Reason);
                w.WriteString("errorKind", t.ErrorKind.ToString());
                w.WriteString("state", t.State.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0.0;

    // ───────────────────────────── discovery ─────────────────────────────

    private sealed record LoadedManifest(string Path, List<LoadedTake> Takes);
    private sealed record LoadedTake(string Id, string WavPath, double StartSec, double EndSec, double TrimLeftSec,
        double TrimRightSec, bool IsMuted, string Reason, bool AudioPresent, bool TimingValid);
    private sealed record LoadBatch(List<LoadedManifest> Manifests, List<string> Errors);

    private static LoadBatch LoadManifests(IVoiceOverRecoveryStore store, string baseDir, string canonicalCurrent, string key)
    {
        var manifests = new List<LoadedManifest>();
        var errors = new List<string>();
        var candidates = new List<string>();
        try
        {
            candidates.AddRange(store.EnumerateManifests(baseDir, $"voiceover_recovery_{key}_*.json"));
            foreach (string legacy in new[] { Path.Combine(baseDir, $"voiceover_recovery_{key}.json"), Path.Combine(baseDir, "voiceover_recovery_manifest.json") })
            {
                if (store.ManifestExists(legacy) && !candidates.Contains(legacy, StringComparer.OrdinalIgnoreCase)) candidates.Add(legacy);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"VORECOVERY_01 recovery folder could not be listed: {ex.Message}");
            errors.Add($"Recovery folder could not be read: {ex.Message}");
        }

        // VORECOVERY_02 — a take already committed/discarded is never adopted again, even if a
        // manifest still lists it (e.g. its cleanup write failed).
        var tombs = new Dictionary<string, (TakeRecoveryState State, DateTime Utc)>(StringComparer.Ordinal);
        try { tombs = ReadTombstones(store, TombstonePath(baseDir, canonicalCurrent)); }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"VORECOVERY_02 resolution record unreadable: {ex.Message}");
            errors.Add($"The recovery resolution record could not be read ({ex.Message}); already-applied takes may be offered again.");
        }

        foreach (string path in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(store.ReadManifest(path));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("root is not an object");
                if (root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.GetInt32() > SchemaVersion)
                    throw new JsonException($"unsupported schema version {sv.GetInt32()}");

                string manifestVideo = root.TryGetProperty("videoPath", out var vp) && vp.ValueKind == JsonValueKind.String ? vp.GetString() ?? "" : "";
                // Exact identity: both unbound, or the same canonical video. Never cross-adopt.
                if (!string.Equals(canonicalCurrent, CanonicalVideo(manifestVideo), StringComparison.OrdinalIgnoreCase)) continue;

                var takes = new List<LoadedTake>();
                if (root.TryGetProperty("failedTakes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;   // preserved verbatim by the merge-on-write
                        string id = EntryId(item);
                        string wav = Str(item, "wavPath");
                        if (id.Length == 0 || wav.Length == 0) continue;     // unidentifiable entry: preserved verbatim, never adopted
                        if (tombs.ContainsKey(id)) continue;                 // resolved elsewhere: never resurrected
                        double start = Num(item, "startSec"), end = Num(item, "endSec");
                        bool timingValid = double.IsFinite(start) && double.IsFinite(end) && start >= 0;
                        takes.Add(new LoadedTake(id, wav,
                            double.IsFinite(start) ? start : 0, double.IsFinite(end) ? end : 0,
                            Finite(Num(item, "trimLeftSec")), Finite(Num(item, "trimRightSec")),
                            item.TryGetProperty("isMuted", out var m) && m.ValueKind == JsonValueKind.True,
                            Str(item, "reason") is { Length: > 0 } r ? r : "Unfinalized take from previous session",
                            store.AudioExists(wav), timingValid));
                    }
                }
                manifests.Add(new LoadedManifest(path, takes));
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("VoiceOver", $"VORECOVERY_01 recovery index '{Path.GetFileName(path)}' is unreadable and was preserved: {ex.Message}");
                errors.Add($"{Path.GetFileName(path)} could not be read ({ex.Message}); it was left on disk.");
            }
        }
        return new LoadBatch(manifests, errors);
    }

    /// <summary>Stable take identity. Legacy entries written without an id get a deterministic one
    /// derived from their audio path, so adoption and merge-on-write stay idempotent.</summary>
    private static string EntryId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return "";
        string id = Str(item, "id");
        if (id.Length > 0) return id;
        string wav = Str(item, "wavPath");
        if (wav.Length == 0) return "";
        return "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wav.ToUpperInvariant()))).Substring(0, 16);
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0.0;

    private void MergeLoaded(LoadBatch batch)
    {
        _loadErrors.Clear();
        _loadErrors.AddRange(batch.Errors);
        foreach (var manifest in batch.Manifests)
        {
            _trackedManifestFiles.Add(manifest.Path);   // only READABLE manifests become rewritable
            foreach (var t in manifest.Takes)
            {
                if (_pendingFailedTakes.Exists(x => x.Id == t.Id ||
                        string.Equals(x.Session.WavPath, t.WavPath, StringComparison.OrdinalIgnoreCase)))
                    continue;   // idempotent: repeated discovery never duplicates a take
                var session = new VoiceOverWindow.VoiceOverSession
                {
                    WavPath = t.WavPath, StartSec = t.StartSec, EndSec = t.EndSec,
                    TrimLeftSec = t.TrimLeftSec, TrimRightSec = t.TrimRightSec, IsMuted = t.IsMuted
                };
                string reason = t.Reason;
                var kind = RecoveryErrorKind.Capture;
                if (!t.AudioPresent) { reason = "Audio file missing on disk — " + reason; kind = RecoveryErrorKind.Storage; }
                else if (!t.TimingValid) { reason = "Saved timing was invalid — " + reason; kind = RecoveryErrorKind.Validation; }
                _pendingFailedTakes.Add(new PendingFailedTake(session, null, reason, t.Id)
                {
                    SourceManifestPath = manifest.Path,
                    State = TakeRecoveryState.Pending,   // a durable record is uncommitted by definition
                    ErrorKind = kind
                });
            }
        }
    }

    /// <summary>Synchronous discovery (tests). Production uses <see cref="CheckAndOfferReopenRecoveryAsync"/>.</summary>
    public void CheckAndOfferReopenRecovery()
    {
        string canonical = CanonicalVideo(_videoPathProvider());
        MergeLoaded(LoadManifests(_store, BaseDirectory(), canonical, ComputeVideoKey(canonical)));
    }

    public async Task CheckAndOfferReopenRecoveryAsync()
    {
        if (IsOperationInFlight) return;
        IsOperationInFlight = true;
        try
        {
            string canonical = CanonicalVideo(_videoPathProvider());
            string key = ComputeVideoKey(canonical);
            string baseDir = BaseDirectory();
            var store = _store;
            var batch = await Task.Run(() => LoadManifests(store, baseDir, canonical, key));
            MergeLoaded(batch);
        }
        finally { IsOperationInFlight = false; }
    }

    // ───────────────────────────── recover ─────────────────────────────

    private List<PendingFailedTake> BeginValidation()
    {
        var candidates = _pendingFailedTakes.FindAll(t => t.State == TakeRecoveryState.Pending);
        foreach (var t in candidates) t.State = TakeRecoveryState.Validating;
        return candidates;
    }

    private RecoveryBatchResult PublishVerdicts(List<(PendingFailedTake Take, RecoveryAudioVerdict Verdict)> verdicts,
        Func<VoiceOverWindow.VoiceOverSession, bool> publish)
    {
        int recovered = 0, failed = 0;
        string? lastError = null;
        foreach (var (take, verdict) in verdicts)
        {
            if (take.State != TakeRecoveryState.Validating) continue;   // resolved elsewhere meanwhile
            if (!verdict.IsValid)
            {
                take.FailureReason = verdict.Error ?? "Audio could not be validated";
                take.ErrorKind = verdict.Error != null && verdict.Error.StartsWith("Audio file missing", StringComparison.Ordinal)
                    ? RecoveryErrorKind.Storage : RecoveryErrorKind.Validation;
                take.State = TakeRecoveryState.Pending;
                failed++;
                lastError = take.FailureReason;
                continue;
            }
            if (take.Session.EndSec <= take.Session.StartSec)
                take.Session.EndSec = take.Session.StartSec + verdict.DurationSec;
            try
            {
                if (!publish(take.Session))
                    throw new InvalidOperationException("the voiceover session no longer accepts takes");
                take.State = TakeRecoveryState.RecoveredInSession;   // durable until Commit or Discard
                recovered++;
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("VoiceOver", $"VORECOVERY_01 recovered take publication rejected: {ex.Message}");
                take.FailureReason = $"Publication rejected: {ex.Message}";
                take.ErrorKind = RecoveryErrorKind.Publication;   // NOT a corrupt-audio diagnosis
                take.State = TakeRecoveryState.Pending;
                failed++;
                lastError = take.FailureReason;
            }
        }
        string? message = lastError;
        if (recovered > 0 && failed > 0) message = $"{recovered} take(s) recovered; {failed} take(s) remain unrecoverable.";
        return new RecoveryBatchResult(recovered, failed, message);
    }

    private static Func<VoiceOverWindow.VoiceOverSession, bool> AsPublisher(Action<VoiceOverWindow.VoiceOverSession> action)
        => s => { action(s); return true; };

    /// <summary>Synchronous variant (tests / non-UI callers).</summary>
    public RecoveryBatchResult RecoverTakes(IVoiceCaptureSession capture, Action<VoiceOverWindow.VoiceOverSession> onTakeRecovered)
    {
        if (capture.HasUnreleasedDevice)
            return new RecoveryBatchResult(0, 0, "Cannot recover: capture device is still releasing. Please wait a moment.");
        if (IsOperationInFlight)
            return new RecoveryBatchResult(0, 0, "Another recovery operation is still running.");
        var candidates = BeginValidation();
        var verdicts = candidates.ConvertAll(t => (t, _store.ValidateAudio(t.Session.WavPath)));
        var result = PublishVerdicts(verdicts, AsPublisher(onTakeRecovered));
        var store = SaveManifest();
        return result with { StorageError = store.Success ? null : store.Error };
    }

    /// <summary>
    /// UI variant: validation (file I/O) off-thread; publication on the caller's context; index write off-thread.
    /// <paramref name="publish"/> returns false (or throws) to reject — the take then stays Pending and durable.
    /// </summary>
    public async Task<RecoveryBatchResult> RecoverTakesAsync(IVoiceCaptureSession capture, Func<VoiceOverWindow.VoiceOverSession, bool> publish)
    {
        if (capture.HasUnreleasedDevice)
            return new RecoveryBatchResult(0, 0, "Cannot recover: capture device is still releasing. Please wait a moment.");
        if (IsOperationInFlight)
            return new RecoveryBatchResult(0, 0, "Another recovery operation is still running.");
        IsOperationInFlight = true;
        try
        {
            var candidates = BeginValidation();
            var paths = candidates.ConvertAll(t => t.Session.WavPath);
            var store = _store;
            RecoveryAudioVerdict[] verdicts;
            try
            {
                verdicts = await Task.Run(() => paths.Select(store.ValidateAudio).ToArray());
            }
            catch (Exception ex)
            {
                foreach (var t in candidates) if (t.State == TakeRecoveryState.Validating) t.State = TakeRecoveryState.Pending;
                RuntimeLog.Fail("VoiceOver", $"VORECOVERY_01 validation failed: {ex.Message}");
                return new RecoveryBatchResult(0, candidates.Count, $"Recovered audio could not be checked: {ex.Message}");
            }
            var pairs = new List<(PendingFailedTake, RecoveryAudioVerdict)>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++) pairs.Add((candidates[i], verdicts[i]));
            var result = PublishVerdicts(pairs, publish);
            var persisted = await PersistAsync();
            return result with { StorageError = persisted.Success ? null : persisted.Error };
        }
        finally { IsOperationInFlight = false; }
    }

    // ───────────────────────────── discard ─────────────────────────────

    public bool DiscardTakes(IVoiceCaptureSession capture, out string? error)
    {
        if (capture.HasUnreleasedDevice)
        {
            error = "Cannot discard: capture device is still releasing. Please wait a moment.";
            return false;
        }
        if (IsOperationInFlight) { error = "Another recovery operation is still running."; return false; }
        var targets = _pendingFailedTakes.FindAll(t => t.BlocksApply);
        var outcomes = targets.ConvertAll(t => DeleteOne(_store, t.Session.WavPath));
        return FinishDiscard(targets, outcomes, SaveManifestAfterDiscard, out error);
    }

    private RecoveryStoreResult SaveManifestAfterDiscard() => SaveManifest();

    public async Task<(bool Success, string? Error)> DiscardTakesAsync(IVoiceCaptureSession capture)
        => await DiscardAsync(capture, _pendingFailedTakes.FindAll(t => t.BlocksApply));

    private async Task<(bool Success, string? Error)> DiscardAsync(IVoiceCaptureSession capture, List<PendingFailedTake> targets)
    {
        if (capture.HasUnreleasedDevice)
            return (false, "Cannot discard: capture device is still releasing. Please wait a moment.");
        if (IsOperationInFlight) return (false, "Another recovery operation is still running.");
        IsOperationInFlight = true;
        try
        {
            var paths = targets.ConvertAll(t => t.Session.WavPath);
            var store = _store;
            var outcomes = await Task.Run(() => paths.ConvertAll(p => DeleteOne(store, p)));
            RecoveryStoreResult persisted = RecoveryStoreResult.Ok;
            bool ok = FinishDiscard(targets, outcomes, () => RecoveryStoreResult.Ok, out string? error);
            if (targets.Exists(t => t.State == TakeRecoveryState.Discarded)) persisted = await PersistAsync();
            if (!persisted.Success) return (false, persisted.Error);
            return (ok, error);
        }
        finally { IsOperationInFlight = false; }
    }

    private static string? DeleteOne(IVoiceOverRecoveryStore store, string path)
    {
        try
        {
            if (store.AudioExists(path)) store.DeleteAudio(path);
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"VORECOVERY_01 failed to delete {Path.GetFileName(path)}: {ex.Message}");
            return ex.Message;
        }
    }

    private static bool FinishDiscard(List<PendingFailedTake> targets, List<string?> outcomes, Func<RecoveryStoreResult> persist, out string? error)
    {
        string? deleteError = null;
        for (int i = 0; i < targets.Count; i++)
        {
            if (outcomes[i] == null) targets[i].State = TakeRecoveryState.Discarded;   // explicit, proven deletion only
            else deleteError = outcomes[i];
        }
        var stored = persist();
        if (deleteError != null) { error = $"Failed to delete audio file: {deleteError}"; return false; }
        if (!stored.Success) { error = stored.Error; return false; }
        error = null;
        return true;
    }

    public void DiscardPendingTake(IVoiceCaptureSession capture, PendingFailedTake take)
    {
        if (capture.HasUnreleasedDevice || IsOperationInFlight) return;
        FinishDiscard(new List<PendingFailedTake> { take }, new List<string?> { DeleteOne(_store, take.Session.WavPath) }, SaveManifest, out _);
    }

    public Task<(bool Success, string? Error)> DiscardPendingTakeAsync(IVoiceCaptureSession capture, PendingFailedTake take)
        => DiscardAsync(capture, new List<PendingFailedTake> { take });

    // ───────────────────────────── commit ─────────────────────────────

    private void MarkCommitted(IEnumerable<string> committedWavPaths)
    {
        var set = new HashSet<string>(committedWavPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var take in _pendingFailedTakes)
        {
            // Only takes that are actually IN the committed snapshot. Pending takes block Apply and
            // takes of other sessions are not held here, so neither can be swept by a commit.
            if (take.State == TakeRecoveryState.RecoveredInSession && set.Contains(take.Session.WavPath))
                take.State = TakeRecoveryState.Committed;
        }
    }

    public RecoveryStoreResult CommitTakes(IEnumerable<string> committedWavPaths)
    {
        MarkCommitted(committedWavPaths);
        return SaveManifest();
    }

    /// <summary>Apply's commit: removes ONLY the committed take ids from the durable index, off-thread.</summary>
    public Task<RecoveryStoreResult> CommitTakesAsync(IEnumerable<string> committedWavPaths)
    {
        MarkCommitted(committedWavPaths);
        return PersistAsync();
    }

    public RecoveryStoreResult CommitAllRecovered()
    {
        MarkCommitted(_pendingFailedTakes.Where(t => t.State == TakeRecoveryState.RecoveredInSession).Select(t => t.Session.WavPath).ToList());
        return SaveManifest();
    }
}
