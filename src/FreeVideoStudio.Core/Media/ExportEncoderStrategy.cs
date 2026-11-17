namespace FreeVideoStudio.Core.Media;

using FreeVideoStudio.Core.Infrastructure;

public static class ExportEncoderStrategy
{
    public static string Resolve(string? userOverride, string? bootScanResult, string ffmpegPath)
    {
        if (!string.IsNullOrWhiteSpace(userOverride) &&
            !userOverride.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            CoreLogger.Info("Hardware", $"Encoder override active from Settings: {userOverride}.");
            return userOverride;
        }

        if (!string.IsNullOrWhiteSpace(bootScanResult) && bootScanResult != HardwareScanner.ScanFailed)
        {
            return bootScanResult;
        }

        var shared = HardwareCapability.TryLoadEncoder(ffmpegPath);
        if (shared.HasValue)
        {
            CoreLogger.Info("Hardware",
                $"Using the suite-wide encoder result published by the Main App: {shared.Value.EncoderMode} (no re-scan needed).");
            return shared.Value.EncoderMode;
        }

        return HardwareScanner.ScanFailed;
    }
}
