using System;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>What the Viral Post card shows right now (AIVIRAL_HONEST_01: the badge always tells the truth).</summary>
public sealed record ViralPostView(
    string Badge,
    string? Message,
    bool ShowText,
    bool ShowRegenerate,
    bool ShowSetup,
    bool ShowCancel,
    string RegenerateLabel,
    ViralPostResult? Post);

/// <summary>
/// VIRALVM_01 (UI-LOGIC separation) — the Finished screen's "Write my post" card. Choosing the AI
/// company/model/key, running the generation off the UI thread, cancellation and turning the outcome
/// into an honest card state live here; FinishedDialogWindow only draws <see cref="View"/>. The
/// generator is injectable, so the whole flow is testable without the internet or FFmpeg.
/// </summary>
public sealed class ViralPostViewModel : IDisposable
{
    /// <summary>Writes a post for a video. Returns the outcome and a "(1,234 tokens)" note (may be empty).</summary>
    public delegate Task<(ViralPostOutcome Outcome, string TokenInfo)> PostGenerator(string videoPath, string? avoidTitle, CancellationToken ct);

    private readonly ISettingsStore _store;
    private readonly PostGenerator _generate;
    private readonly CtsSlot _run = new();
    private string? _videoPath;

    public ViralPostViewModel(ISettingsStore store, PostGenerator? generate = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _generate = generate ?? ((videoPath, avoidTitle, ct) => ProductionRunAsync(videoPath, avoidTitle, ct));
        View = Writing();
    }

    /// <summary>The card state to draw.</summary>
    public ViralPostView View { get; private set; }

    /// <summary>Raised (on the caller's thread) whenever <see cref="View"/> changes.</summary>
    public event Action<ViralPostView>? Changed;

    /// <summary>Settings › AI › "Write my post".</summary>
    public bool IsEnabled => _store.Current.AiViralPostHelperEnabled;

    /// <summary>The company the post is written with (for the "connect your AI" wizard).</summary>
    public AiVendor Vendor => _store.Current.GetUsageConfig(AiUsage.ViralPost).Vendor;

    public Task StartAsync(string videoPath)
    {
        _videoPath = videoPath;
        return RequestAsync(avoidTitle: null);
    }

    /// <summary>NEW VERSION / RETRY: asks again, avoiding the current title.</summary>
    public Task RegenerateAsync() => RequestAsync(View.Post?.Title);

    /// <summary>After the AI wizard added a key.</summary>
    public Task RetryAsync() => RequestAsync(avoidTitle: null);

    public void Cancel()
    {
        _run.Cancel();
        Show(new ViralPostView("CANCELLED", "Writing cancelled.", false, true, false, false, "↻ WRITE POST", null));
    }

    private async Task RequestAsync(string? avoidTitle)
    {
        string? videoPath = _videoPath;
        if (string.IsNullOrEmpty(videoPath)) return;

        Show(Writing());
        CancellationToken token = _run.Renew();
        (ViralPostOutcome outcome, string tokenInfo) result;
        try
        {
            result = await _generate(videoPath, avoidTitle, token);
        }
        catch (OperationCanceledException ex)
        {
            RuntimeLog.Swallowed(ex);
            return;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            result = (new ViralPostOutcome(ViralPostStatus.Failed, null, "Something went wrong while writing the post. Try again."), "");
        }
        if (token.IsCancellationRequested) return;
        Show(ToView(result.outcome, result.tokenInfo));
    }

    /// <summary>AIVIRAL_HONEST_01 — the outcome as the card shows it. Pure.</summary>
    public static ViralPostView ToView(ViralPostOutcome outcome, string tokenInfo = "") => outcome.Status switch
    {
        ViralPostStatus.AiGenerated when outcome.Post != null =>
            new ViralPostView($"AI WRITTEN{tokenInfo}", outcome.Message, true, true, false, false, "↻ NEW VERSION", outcome.Post),
        ViralPostStatus.NoKey =>
            new ViralPostView("NO AI KEY", outcome.Message, false, false, true, false, "↻ NEW VERSION", null),
        _ =>
            new ViralPostView("AI FAILED", (outcome.Message ?? "The AI could not write the post.") + AiUsageMeter.ReferenceSuffix, false, true, false, false, "↻ RETRY", null),
    };

    private static ViralPostView Writing(
        string message = "Looking at your video and writing a title, caption and hashtags…",
        string badge = "WRITING…") =>
        new(badge, message, false, false, false, true, "↻ NEW VERSION", null);

    private void Show(ViralPostView view)
    {
        View = view;
        Changed?.Invoke(view);
    }

    /// <summary>The production generator: current AI settings, FFmpeg stills, pre-run cost estimate, and live verbose beats off the UI thread.</summary>
    private async Task<(ViralPostOutcome Outcome, string TokenInfo)> ProductionRunAsync(
        string videoPath,
        string? avoidTitle,
        CancellationToken ct)
    {
        var settings = _store.Current;
        var config = settings.GetUsageConfig(AiUsage.ViralPost);
        string apiKey = settings.GetApiKey(config.Vendor);
        string model = settings.GetEffectiveModel(AiUsage.ViralPost);
        string vendorName = AiVisionProviders.DisplayName(config.Vendor);
        string ffmpegPath = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");

        var est = AiCostEstimator.EstimateViralPost(config.Vendor, model);
        string estBanner = AiCostEstimator.FormatPreRunBanner(est, config.Vendor, model);
        string currentStep = string.IsNullOrWhiteSpace(apiKey)
            ? "Checking AI key…"
            : $"Preparing {vendorName} ({model})…";
        DateTime startedUtc = DateTime.UtcNow;
        bool finished = false;
        object stateLock = new();

        void EmitProgress(string? newStep = null)
        {
            if (ct.IsCancellationRequested) return;
            lock (stateLock)
            {
                if (finished || ct.IsCancellationRequested) return;
                if (!string.IsNullOrWhiteSpace(newStep)) currentStep = newStep;
                int secs = (int)Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);
                string badge = secs >= 1 ? $"WRITING… {secs}s" : "WRITING…";
                string msg = string.IsNullOrWhiteSpace(apiKey) ? currentStep : $"{currentStep} · {estBanner}";
                Show(Writing(msg, badge));
            }
        }

        return await Task.Run(async () =>
        {
            using var usage = AiUsageMeter.BeginTask("Viral Post Helper", config.Vendor);
            using var timer = new System.Threading.Timer(_ => EmitProgress(null), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            EmitProgress(currentStep);
            try
            {
                ViralPostOutcome outcome = await AiViralPostService.GenerateWithStatusAsync(
                    ffmpegPath,
                    videoPath,
                    config.Vendor,
                    apiKey,
                    model,
                    avoidTitle,
                    step => EmitProgress(step),
                    ct).ConfigureAwait(false);

                string tokenInfo = usage.Tally.TotalTokens > 0 ? $" ({usage.Tally.TotalTokens:N0} tokens)" : "";
                if (usage.Tally.TotalTokens > 0)
                {
                    string summary = usage.SummaryWithEstimate(est);
                    outcome = outcome.Status == ViralPostStatus.AiGenerated
                        ? outcome with { Message = summary }
                        : outcome with { Message = string.IsNullOrWhiteSpace(outcome.Message) ? summary : $"{outcome.Message} ({summary})" };
                }
                return (outcome, tokenInfo);
            }
            finally
            {
                lock (stateLock) finished = true;
            }
        }, ct).ConfigureAwait(false);
    }

    public void Dispose() => _run.Dispose();
}
