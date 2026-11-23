using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.LocalAi;

namespace FreeVideoStudio.App.Services;

public enum AiMode
{
    NotChosen,
    LocalOnly,
    OnlineOnly,
    Hybrid,
}

public enum LocalTrackerKind
{
    None,
    BuiltInBasic,
    NeuralSam2,
}

public sealed record AiCapabilities(
    AiMode Mode,
    bool HasOnlineKey,
    bool BuiltInLocalAvailable,
    bool NeuralPackAvailable,
    bool NeuralPackReady,
    LocalDetectorStatus DetectorStatus,
    bool IsModeComplete,
    LocalTrackerKind LocalTracker,
    bool CanUseCloudDiscovery,
    bool CanUseCloudTracking,
    bool CanUseCloudCommentary);

internal static class AiSetup
{
    public const string PolicyTag = "AICAPABILITY_01";

    public static bool UsesPc(AiMode mode) => mode is AiMode.LocalOnly or AiMode.Hybrid;

    public static bool UsesOnline(AiMode mode) => mode is AiMode.OnlineOnly or AiMode.Hybrid;

    public const string PcTrackingTag = "AIPCTRACK_01";

    public static bool TracksOnPc(AiMode mode) => true;

    public static bool UsesLocalTracking(AiMode mode) => TracksOnPc(mode);

    public static bool CanCallCloud(AiMode mode) => UsesOnline(mode);

    public static bool HasStoredKey(AppSettings s)
        => s.HasAiCredential(AiVendor.Google) || s.HasAiCredential(AiVendor.Anthropic) || s.HasAiCredential(AiVendor.OpenAI);

    public static AiMode Effective(AiMode saved, bool hasKey, bool packReady)
    {
        if (saved != AiMode.NotChosen) return saved;
        if (!hasKey) return AiMode.NotChosen;
        return packReady ? AiMode.Hybrid : AiMode.OnlineOnly;
    }

    public static bool IsComplete(AiMode mode, bool hasKey, bool packReady) => mode switch
    {
        AiMode.LocalOnly => true,
        AiMode.OnlineOnly => hasKey,
        AiMode.Hybrid => hasKey,
        _ => false,
    };

    public static LocalTrackerKind LocalTrackerFor(AiMode mode, bool packReady)
        => !TracksOnPc(mode) ? LocalTrackerKind.None : (packReady ? LocalTrackerKind.NeuralSam2 : LocalTrackerKind.BuiltInBasic);

    public static AiCapabilities Capabilities()
    {
        var s = SettingsManager.Instance;
        bool hasKey = HasStoredKey(s);
        bool packReady = LocalAiPackService.IsReady;
        LocalDetectorStatus detector = LocalAiPack.ResolveDetector(ApplicationPaths.CreateDefault());
        return Describe(Effective(s.AiMode, hasKey, packReady), hasKey, packReady, detector);
    }

    public static AiCapabilities Describe(AiMode mode, bool hasKey, bool packReady, LocalDetectorStatus? detector = null)
    {
        detector ??= LocalAiPack.ResolveDetector(ApplicationPaths.CreateDefault());
        bool complete = IsComplete(mode, hasKey, packReady);
        bool cloudOk = UsesOnline(mode) && hasKey;
        LocalTrackerKind localKind = LocalTrackerFor(mode, packReady);
        return new AiCapabilities(
            mode,
            hasKey,
            BuiltInLocalAvailable: true,
            NeuralPackAvailable: NeuralPackAvailable,
            NeuralPackReady: packReady,
            DetectorStatus: detector,
            IsModeComplete: complete,
            LocalTracker: localKind,
            CanUseCloudDiscovery: cloudOk,
            CanUseCloudTracking: !TracksOnPc(mode) && mode == AiMode.OnlineOnly && hasKey,
            CanUseCloudCommentary: cloudOk);
    }

    public static AiMode Current()
    {
        var s = SettingsManager.Instance;
        return Effective(s.AiMode, HasStoredKey(s), LocalAiPackService.IsReady);
    }

    public static bool CurrentIsComplete()
    {
        var s = SettingsManager.Instance;
        bool hasKey = HasStoredKey(s);
        bool pack = LocalAiPackService.IsReady;
        return IsComplete(Effective(s.AiMode, hasKey, pack), hasKey, pack);
    }

    public const string PackRemovedTag = "AIMODEHEAL_01";

    public static bool PcAvailable => true;

    public static bool NeuralPackAvailable => LocalAiPackManifest.IsPublished || LocalAiPackService.IsReady;

    public static AiMode AfterPackRemoved(AiMode saved, bool hasKey) => saved switch
    {
        AiMode.Hybrid => AiMode.Hybrid,
        AiMode.LocalOnly => AiMode.LocalOnly,
        _ => saved,
    };

    public static bool ApplyPackRemoved()
    {
        var s = SettingsManager.Instance;
        AiMode next = AfterPackRemoved(s.AiMode, HasStoredKey(s));
        if (next == s.AiMode) return false;
        RuntimeLog.Info("AiSetup", $"{PackRemovedTag}: the optional neural PC AI pack was removed, so 'How AI works' went from {s.AiMode} to {next}.");
        return SettingsManager.Update(x => x.AiMode = next);
    }

    public static string PackSizeText()
    {
        long bytes = LocalAiPackManifest.TotalBytes;
        return bytes <= 0 ? string.Empty : $"about {(bytes / 1048576.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} MB";
    }

    public static string DisplayName(AiMode mode) => mode switch
    {
        AiMode.LocalOnly => "On this PC only",
        AiMode.OnlineOnly => "Online only",
        AiMode.Hybrid => "Both (PC + online)",
        _ => "Not set up yet",
    };
}
