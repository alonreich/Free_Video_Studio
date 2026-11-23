using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public static class HardwareCapability
{
    private const string FileName = "hardware_capability.json";

    private const int SchemaVersion = 1;

    private const int MaxAgeDays = 7;

    public readonly record struct EncoderCapability(string EncoderMode, DateTime ProbedUtc);

    public readonly record struct RenderCapability(
        bool UseHardwareAcceleration, string RendererName, string DriverVersion, string FailureReason);

    public static EncoderCapability? TryLoadEncoder(string ffmpegPath)
    {
        try
        {
        var root = TryLoadRoot();
        if (root == null) return null;

        string mode = root["encoderMode"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(mode)) return null;

        if (mode == HardwareScanner.ScanFailed) return null;

        string recordedFingerprint = root["ffmpegFingerprint"]?.ToString() ?? "";
        string currentFingerprint = FingerprintFfmpeg(ffmpegPath);
        if (currentFingerprint.Length == 0 || recordedFingerprint != currentFingerprint)
        {
            CoreLogger.Debug("Hardware", "Shared capability: ffmpeg fingerprint changed — re-probing the encoder.");
            return null;
        }

        if (!TryReadUtc(root, "encoderProbedUtc", out DateTime probedUtc)) return null;
        if (DateTime.UtcNow - probedUtc > TimeSpan.FromDays(MaxAgeDays))
        {
            CoreLogger.Debug("Hardware", "Shared capability: encoder result is stale — re-probing.");
            return null;
        }

        return new EncoderCapability(mode, probedUtc);
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("Hardware", $"Shared capability (encoder half) unreadable: {ex.Message} — probing instead.");
            return null;
        }
    }

    public static RenderCapability? TryLoadRenderMode()
    {
        try
        {
            var root = TryLoadRoot();
            if (root == null) return null;

            if (!TryReadBool(root, "renderRecorded", false)) return null;

            string recordedSession = root["sessionKey"]?.ToString() ?? "";
            if (recordedSession.Length == 0 || recordedSession != CurrentSessionKey())
            {
                CoreLogger.Debug("Hardware", "Shared capability: different Windows session (RDP guard) — re-probing the GPU.");
                return null;
            }

            if (!TryReadUtc(root, "renderProbedUtc", out DateTime probedUtc)) return null;
            if (DateTime.UtcNow - probedUtc > TimeSpan.FromDays(MaxAgeDays)) return null;

            return new RenderCapability(
                TryReadBool(root, "useHardwareAcceleration", false),
                root["rendererName"]?.ToString() ?? "Unknown",
                root["driverVersion"]?.ToString() ?? "",
                root["failureReason"]?.ToString() ?? "");
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("Hardware", $"Shared capability (render half) unreadable: {ex.Message} — probing instead.");
            return null;
        }
    }

    private static bool TryReadBool(JsonObject root, string key, bool fallback)
    {
        try
        {
            var node = root[key];
            if (node == null) return fallback;
            if (node.GetValueKind() == System.Text.Json.JsonValueKind.True) return true;
            if (node.GetValueKind() == System.Text.Json.JsonValueKind.False) return false;
            return bool.TryParse(node.ToString(), out bool parsed) ? parsed : fallback;
        }
        catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
            return fallback;
        }
    }

    public static void SaveEncoder(string encoderMode, string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(encoderMode) || encoderMode == HardwareScanner.ScanFailed) return;

        var root = TryLoadRoot() ?? new JsonObject();
        root["schemaVersion"] = SchemaVersion;
        root["encoderMode"] = encoderMode;
        root["ffmpegFingerprint"] = FingerprintFfmpeg(ffmpegPath);
        root["encoderProbedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Write(root);
        CoreLogger.Info("Hardware", $"Shared capability published for the whole suite: encoder={encoderMode}.");
    }

    public static void SaveRenderMode(GpuCapabilityProbe.Result result)
    {
        var root = TryLoadRoot() ?? new JsonObject();
        root["schemaVersion"] = SchemaVersion;
        root["renderRecorded"] = true;
        root["useHardwareAcceleration"] = result.UseHardwareAcceleration;
        root["rendererName"] = result.RendererName;
        root["driverVersion"] = result.DriverVersion;
        root["failureReason"] = result.FailureReason;
        root["sessionKey"] = CurrentSessionKey();
        root["renderProbedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Write(root);
    }

    private static JsonObject? TryLoadRoot()
    {
        try
        {
            string raw = UiStateStore.ReadText(FileName);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (JsonNode.Parse(raw) is not JsonObject root) return null;
            if ((root["schemaVersion"]?.GetValue<int>() ?? 0) != SchemaVersion) return null;
            return root;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("Hardware", $"Shared capability unreadable ({ex.Message}) — every caller will probe for itself.");
            return null;
        }
    }

    private static void Write(JsonObject root)
    {
        try { UiStateStore.WriteText(FileName, root.ToJsonString()); }
        catch (Exception ex) { CoreLogger.Warn("Hardware", $"Shared capability not written: {ex.Message}"); }
    }

    private static bool TryReadUtc(JsonObject root, string key, out DateTime value)
    {
        value = default;
        try
        {
            string? raw = root[key]?.ToString();
            return !string.IsNullOrWhiteSpace(raw)
                   && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                                        DateTimeStyles.RoundtripKind, out value);
        }
        catch (Exception ex)
        {
            CoreLogger.Debug("Hardware", $"Shared capability timestamp '{key}' unreadable: {ex.Message}");
            return false;
        }
    }

    private static string FingerprintFfmpeg(string ffmpegPath)
    {
        try
        {
            var info = new FileInfo(ffmpegPath);
            if (!info.Exists) return "";
            return string.Create(CultureInfo.InvariantCulture,
                $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }
        catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
            return "";
        }
    }

    private static string CurrentSessionKey()
    {
        try
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            string sessionName = Environment.GetEnvironmentVariable("SESSIONNAME") ?? "";
            bool remote = sessionName.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase);
            return string.Create(CultureInfo.InvariantCulture, $"{sessionId}|{(remote ? "remote" : "local")}");
        }
        catch (System.Exception swallowed3)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
            return "";
        }
    }
}
