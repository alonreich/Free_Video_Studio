// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.Core.Media;

/// <summary>One Magic Wand run's inputs.</summary>
/// <param name="LocalDetect">The offline detector (or its cached answer for this clip). Always runs.</param>
/// <param name="AiFrame">The ONE frozen frame the AI may see, or null when the AI is not to be
/// asked this run (no key, no consent, frame unavailable). Null means zero network traffic.</param>
/// <param name="SourceWidth">Source-frame width every candidate is expressed in.</param>
/// <param name="SourceHeight">Source-frame height every candidate is expressed in.</param>
public sealed record HudDetectionRequest(
    Func<CancellationToken, Task<IReadOnlyList<HudCandidate>>> LocalDetect,
    AiHudFrame? AiFrame,
    int SourceWidth,
    int SourceHeight);

/// <summary>
/// One Magic Wand run's published answer. Pure data: SUGGESTIONS for the user to pick from.
/// Nothing in it — and nothing the coordinator does — commits a crop.
/// </summary>
public sealed record HudDetectionResult(
    long Generation,
    IReadOnlyList<HudCandidate> Candidates,
    IReadOnlyList<HudCandidate> LocalCandidates,
    bool LocalSucceeded,
    bool LocalTimedOut,
    AiHudOutcome? AiOutcome,
    int AiCandidateCount);

/// <summary>
/// AIHUD_03 — runs the offline detector and the optional AI detector side by side, fuses their
/// answers deterministically, and guarantees that a cancelled or superseded run publishes NOTHING.
///
/// <para><b>Staleness.</b> Every <see cref="RunAsync"/> takes a new generation and cancels the
/// previous run's token; <see cref="Cancel"/> does the same without starting anything. A run only
/// returns a result if, after BOTH detectors have finished, its token is still live and its
/// generation is still the current one. Otherwise it returns null — so a slow AI response can
/// never overwrite a newer scan, and a cancelled run cannot surface late.</para>
///
/// <para><b>Fallback.</b> The AI side never takes the local side down with it. Missing key,
/// timeout, rate limit, network or model error, malformed answer, or an outright exception from
/// the provider: the local candidates are returned exactly as the detector produced them. Each
/// AI failure is reported once through <see cref="IFaultSink"/> at <see cref="FaultTier.Degraded"/>
/// — the AI suggestions the user asked for did not happen — with wording that says the local
/// results are still shown. A missing key is not a fault: it is the default, offline state.</para>
///
/// <para><b>Threading.</b> No UI types. Callable from the UI thread; all internal awaits use
/// <c>ConfigureAwait(false)</c>, and the caller re-checks <see cref="IsCurrent"/> on its own
/// thread before drawing.</para>
/// </summary>
public sealed class HudDetectionCoordinator : IDisposable
{
    public const string FaultArea = "CROP";

    private readonly IAiHudDetectionService? _ai;
    private readonly IFaultSink _faults;
    private readonly object _gate = new();
    private long _generation;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public HudDetectionCoordinator(IAiHudDetectionService? ai, IFaultSink faults)
    {
        _ai = ai;
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
    }

    /// <summary>The provider, if any. The UI reads <see cref="IAiHudDetectionService.ProviderName"/> for the privacy notice.</summary>
    public IAiHudDetectionService? AiService => _ai;

    public long CurrentGeneration
    {
        get { lock (_gate) return _generation; }
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate) return !_disposed && generation == _generation;
    }

    /// <summary>Cancels the run in flight (if any) and invalidates everything it might still publish.</summary>
    public void Cancel()
    {
        CancellationTokenSource? old;
        lock (_gate)
        {
            _generation++;
            old = _cts;
            _cts = null;
        }
        CancelQuietly(old);
    }

    public async Task<HudDetectionResult?> RunAsync(
        HudDetectionRequest request,
        IProgress<HudAutoDetector.DetectionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        long generation;
        CancellationToken token;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_generation;
            previous = _cts;
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }
        CancelQuietly(previous);

        // ── Start BOTH before awaiting either, so the AI round-trip overlaps the local scan.
        Task<AiHudDetectionResult>? aiTask = null;
        AiHudOutcome? aiOutcome = null;
        if (request.AiFrame != null)
        {
            if (_ai == null || !_ai.IsConfigured)
                aiOutcome = AiHudOutcome.NotConfigured;
            else
                aiTask = RunAiGuardedAsync(_ai, request.AiFrame, token);
        }

        IReadOnlyList<HudCandidate> local = Array.Empty<HudCandidate>();
        bool localSucceeded = false;
        bool localTimedOut = false;
        try
        {
            local = await request.LocalDetect(token).ConfigureAwait(false) ?? Array.Empty<HudCandidate>();
            localSucceeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await ObserveAsync(aiTask).ConfigureAwait(false);
            return null;   // the user (or a newer run) cancelled: publish nothing.
        }
        catch (OperationCanceledException ex)
        {
            // Not our token: the detector hit its own MaxSeconds ceiling.
            localTimedOut = true;
            _faults.Recoverable(FaultArea, "Local Magic Wand reached its time ceiling.", ex);
        }
        catch (Exception ex)
        {
            _faults.Degraded(FaultArea,
                "The offline Magic Wand could not read this clip. You can still drag a box round a HUD piece yourself.",
                ex);
        }

        if (aiTask != null)
        {
            if (!token.IsCancellationRequested)
                progress?.Report(new HudAutoDetector.DetectionProgress(99, $"Waiting for {_ai!.ProviderName}'s suggestions…"));

            AiHudDetectionResult? aiResult;
            try
            {
                aiResult = await aiTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }

            aiOutcome = aiResult.Outcome;
            if (token.IsCancellationRequested || !IsCurrent(generation)) return null;

            ReportAiOutcome(aiResult);
            IReadOnlyList<HudCandidate> fused = FuseWithAi(local, aiResult, request.SourceWidth, request.SourceHeight, out int aiCount);
            return Publish(generation, token, fused, local, localSucceeded, localTimedOut, aiOutcome, aiCount);
        }

        return Publish(generation, token, local, local, localSucceeded, localTimedOut, aiOutcome, 0);
    }

    private HudDetectionResult? Publish(
        long generation, CancellationToken token, IReadOnlyList<HudCandidate> candidates, IReadOnlyList<HudCandidate> local,
        bool localSucceeded, bool localTimedOut, AiHudOutcome? aiOutcome, int aiCount)
    {
        if (token.IsCancellationRequested || !IsCurrent(generation)) return null;
        return new HudDetectionResult(generation, candidates, local, localSucceeded, localTimedOut, aiOutcome, aiCount);
    }

    private static IReadOnlyList<HudCandidate> FuseWithAi(
        IReadOnlyList<HudCandidate> local, AiHudDetectionResult aiResult, int sourceWidth, int sourceHeight, out int aiCount)
    {
        aiCount = 0;
        if (aiResult.Outcome != AiHudOutcome.Success || aiResult.Candidates.Count == 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return local;

        var converted = new List<HudCandidate>(aiResult.Candidates.Count);
        foreach (AiHudCandidate c in aiResult.Candidates)
        {
            // Defence in depth: a provider that skipped the parser still cannot get geometry through.
            if (!AiHudResponseParser.IsValid(c)) continue;

            (int x, int y, int w, int h) = AiHudGeometry.ToSourcePixels(c, sourceWidth, sourceHeight);
            if (w < MinSourcePixels || h < MinSourcePixels) continue;

            converted.Add(new HudCandidate(x, y, w, h, AiHudLabelMapper.ToRoleKey(c.Label), c.Label, c.Confidence, HudCandidateSource.Ai));
        }

        aiCount = converted.Count;
        return HudCandidateFusion.Fuse(local, converted);
    }

    /// <summary>The Crop Tool's own floor for a usable box (matches its local-candidate filter).</summary>
    public const int MinSourcePixels = 4;

    private void ReportAiOutcome(AiHudDetectionResult result)
    {
        string? message = result.Outcome switch
        {
            AiHudOutcome.Success or AiHudOutcome.NotConfigured => null,
            AiHudOutcome.Timeout => "AI HUD suggestions timed out. The offline Magic Wand results are still shown.",
            AiHudOutcome.RateLimited => "The AI provider is rate-limiting requests right now. The offline Magic Wand results are still shown.",
            AiHudOutcome.NetworkError => "AI HUD suggestions are unavailable (no connection). The offline Magic Wand results are still shown.",
            AiHudOutcome.MalformedResponse => "The AI returned an answer that could not be used. The offline Magic Wand results are still shown.",
            _ => "AI HUD suggestions are unavailable right now. The offline Magic Wand results are still shown.",
        };
        if (message != null)
            _faults.Degraded(FaultArea, message, technicalDetail: $"AI HUD outcome {result.Outcome}: {result.Detail}");
    }

    /// <summary>Turns a contract-breaking provider exception into a ModelError result. Caller cancellation still propagates.</summary>
    private async Task<AiHudDetectionResult> RunAiGuardedAsync(IAiHudDetectionService ai, AiHudFrame frame, CancellationToken token)
    {
        try
        {
            return await ai.DetectAsync(frame, token).ConfigureAwait(false)
                ?? AiHudDetectionResult.Failed(AiHudOutcome.ModelError, "provider returned null");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _faults.Recoverable(FaultArea, "AI HUD provider threw instead of returning an outcome.", ex);
            return AiHudDetectionResult.Failed(AiHudOutcome.ModelError, ex.GetType().Name);
        }
    }

    private async Task ObserveAsync(Task<AiHudDetectionResult>? task)
    {
        if (task == null) return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancel is the user getting what they asked for (FAULTTIER_01): nothing to report.
        }
    }

    private void CancelQuietly(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _faults.Recoverable(FaultArea, "Magic Wand token already disposed.", ex);
        }
        finally
        {
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? old;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            old = _cts;
            _cts = null;
        }
        CancelQuietly(old);
    }
}
