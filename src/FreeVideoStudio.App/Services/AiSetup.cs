using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Services;

public enum AiMode
{
    NotChosen,
    LocalOnly,
    OnlineOnly,
    Hybrid,
}

internal static class AiSetup
{
    public static bool UsesPc(AiMode mode) => mode is AiMode.LocalOnly or AiMode.Hybrid;

    public static bool UsesOnline(AiMode mode) => mode is AiMode.OnlineOnly or AiMode.Hybrid;

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
        AiMode.LocalOnly => packReady,
        AiMode.OnlineOnly => hasKey,
        AiMode.Hybrid => hasKey && packReady,
        _ => false,
    };

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

    public static bool PcAvailable => FreeVideoStudio.Core.LocalAi.LocalAiPackManifest.IsPublished || LocalAiPackService.IsReady;

    public static AiMode AfterPackRemoved(AiMode saved, bool hasKey) => saved switch
    {
        AiMode.Hybrid => hasKey ? AiMode.OnlineOnly : AiMode.NotChosen,
        AiMode.LocalOnly => AiMode.NotChosen,
        _ => saved,
    };

    public static bool ApplyPackRemoved()
    {
        var s = SettingsManager.Instance;
        AiMode next = AfterPackRemoved(s.AiMode, HasStoredKey(s));
        if (next == s.AiMode) return false;
        RuntimeLog.Info("AiSetup", $"{PackRemovedTag}: the PC AI was removed, so 'How AI works' went from {s.AiMode} to {next}.");
        return SettingsManager.Update(x => x.AiMode = next);
    }

    public static string PackSizeText()
    {
        long bytes = FreeVideoStudio.Core.LocalAi.LocalAiPackManifest.TotalBytes;
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
