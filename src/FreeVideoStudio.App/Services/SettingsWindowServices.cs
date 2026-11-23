using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

public interface ISettingsStore
{
    AppSettings Current { get; }

    bool TryUpdate(Action<AppSettings> patch);
}

public sealed class SettingsManagerStore : ISettingsStore
{
    public static readonly SettingsManagerStore Instance = new();
    private SettingsManagerStore() { }
    public AppSettings Current => SettingsManager.Instance;
    public bool TryUpdate(Action<AppSettings> patch) => SettingsManager.Update(patch);
}

public sealed record KeyTestResult(bool Success, string Message, bool Cancelled = false, IReadOnlyList<string>? Models = null, bool Unverified = false);

public interface IAiKeyValidator
{
    Task<KeyTestResult> TestAsync(AiVendor vendor, string key, string googleModel, CancellationToken ct);

    Task<IReadOnlyList<string>> ListModelsAsync(AiVendor vendor, string key, CancellationToken ct);
}

public sealed class AiKeyValidator : IAiKeyValidator
{
    public static readonly AiKeyValidator Instance = new();
    private AiKeyValidator() { }

    public Task<KeyTestResult> TestAsync(AiVendor vendor, string key, string googleModel, CancellationToken ct)
        => TestAsync(vendor, key, googleModel, null, ct);

    public async Task<KeyTestResult> TestAsync(AiVendor vendor, string key, string googleModel, Action<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return new KeyTestResult(false, "Paste a key first.");
        string vendorName = AiVisionProviders.DisplayName(vendor);
        AiKeyHealth.Clear(vendor, key);
        string model = ModelToCheck(vendor, googleModel);
        IReadOnlyList<string> models = Array.Empty<string>();
        try
        {
            progress?.Invoke($"Checking the key with {vendorName}…");
            models = await AiVisionProviders.Get(vendor).ListModelsAsync(key, ct).ConfigureAwait(false);
            if (vendor != AiVendor.Google && models.Count == 0)
                return new KeyTestResult(false, $"{vendorName} accepted the key, but this key cannot use any AI model that looks at pictures. Check your plan or billing with {vendorName}.");
            progress?.Invoke($"Checking that {model} can read a picture…");
            await AiPreflight.EnsureAsync(vendor, key, model, ct).ConfigureAwait(false);
            progress?.Invoke("The key works.");
            return new KeyTestResult(true, $"Key works with {model}.", Models: models);
        }
        catch (OperationCanceledException ex)
        {
            CoreLogger.Swallowed(ex);
            return new KeyTestResult(false, "Not tested yet", Cancelled: true);
        }
        catch (AiProviderException ex) when (ex.IsTransient)
        {
            RuntimeLog.Info("Settings", $"{vendor} key test could not finish: {ex.Kind}.");
            return new KeyTestResult(false, $"Could not check the key right now: {ex.Message}", Models: models, Unverified: true);
        }
        catch (AiProviderException ex)
        {
            RuntimeLog.Info("Settings", $"{vendor} key test failed: {ex.Kind}.");
            return new KeyTestResult(false, ex.Message, Models: models);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or System.Text.Json.JsonException)
        {
            CoreLogger.Warn("AI", $"{vendor} key test failed: {ex.Message}");
            return new KeyTestResult(false, $"Could not reach {vendorName}. Check your internet and try again.", Models: models, Unverified: ex is not System.Text.Json.JsonException);
        }
    }

    internal const string KeyCheckTag = "AIKEYCHECK_01";

    internal static string ModelToCheck(AiVendor vendor, string? model)
    {
        string m = (model ?? string.Empty).Trim();
        bool belongs = m.Length > 0 && vendor switch
        {
            AiVendor.Google => m.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) || m.StartsWith("models/", StringComparison.OrdinalIgnoreCase),
            AiVendor.Anthropic => m.StartsWith("claude", StringComparison.OrdinalIgnoreCase),
            _ => !m.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) && !m.StartsWith("claude", StringComparison.OrdinalIgnoreCase) && !m.StartsWith("models/", StringComparison.OrdinalIgnoreCase),
        };
        string chosen = belongs ? AiVisionProviders.ReplaceRetired(vendor, m) : AiVisionProviders.GetDefaultModel(vendor);
        return vendor == AiVendor.Google ? GeminiClient.ResolveModel(chosen) : chosen;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(AiVendor vendor, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<string>();
        try
        {
            return await AiVisionProviders.Get(vendor).ListModelsAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            CoreLogger.Swallowed(ex);
            return Array.Empty<string>();
        }
        catch (AiProviderException ex)
        {
            CoreLogger.Warn("AI", $"Failed to refresh models for {vendor}: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}

public interface IEncoderCatalog
{
    Task<IReadOnlyCollection<string>?> DetectAsync();
}

public sealed class FfmpegEncoderCatalog : IEncoderCatalog
{
    public static readonly FfmpegEncoderCatalog Instance = new();
    private readonly Lazy<Task<IReadOnlyCollection<string>?>> _detected;

    private FfmpegEncoderCatalog()
    {
        _detected = new Lazy<Task<IReadOnlyCollection<string>?>>(() => Task.Run<IReadOnlyCollection<string>?>(() =>
        {
            try { return new EncoderManager(null, null).AvailableEncoders.ToArray(); }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
                return null;
            }
        }));
    }

    public Task<IReadOnlyCollection<string>?> DetectAsync() => _detected.Value;
}

public interface ISettingsBackupStore
{
    long MaxBytes { get; }

    Task<(string? Json, string? Error)> ReadAsync(string path, CancellationToken ct);

    Task WriteAsync(string path, string json, CancellationToken ct);
}

public sealed class FileSettingsBackupStore : ISettingsBackupStore
{
    public static readonly FileSettingsBackupStore Instance = new();
    private FileSettingsBackupStore() { }

    public const long MaxBackupBytes = 2_000_000;

    public long MaxBytes => MaxBackupBytes;

    public async Task<(string? Json, string? Error)> ReadAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return (null, "That file no longer exists.");
        if (info.Length == 0) return (null, "That file is empty.");
        if (info.Length > MaxBytes) return (null, "That file is far too big to be a settings backup.");
        string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return (json, null);
    }

    public async Task WriteAsync(string path, string json, CancellationToken ct)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException ex) { RuntimeLog.Swallowed(ex); }
            catch (UnauthorizedAccessException ex) { RuntimeLog.Swallowed(ex); }
        }
    }
}

public interface IUpdateChecker
{
    string CurrentVersion { get; }
    string DescribeLastCheck();
    string? PendingVersion { get; }
    void SkipPendingVersion();
    string GetSkippedVersion();
    void ClearSkippedVersion();
    Task CheckNowAsync(Window owner, Action<string> status);
    string ReleaseNotesUrl { get; }
    string ReleasesUrl { get; }
}

public sealed class UpdateServiceChecker : IUpdateChecker
{
    public static readonly UpdateServiceChecker Instance = new();
    private UpdateServiceChecker() { }
    public string CurrentVersion => DeploymentLifecycle.GetCurrentVersion();
    public string DescribeLastCheck() => UpdateService.DescribeLastCheck();
    public string? PendingVersion => UpdateService.PendingVersion;
    public void SkipPendingVersion() => UpdateService.SkipPendingVersion();
    public string GetSkippedVersion() => UpdateService.GetSkippedVersion();
    public void ClearSkippedVersion() => UpdateService.ClearSkippedVersion();
    public Task CheckNowAsync(Window owner, Action<string> status) => UpdateService.CheckManualAsync(owner, status);
    public string ReleaseNotesUrl => UpdateService.ReleaseNotesPageUrl();
    public string ReleasesUrl => UpdateService.ReleasesPageUrl;
}

public interface ISessionStateSink
{
    bool IsPrimed { get; }
    JsonObject ReadSnapshot();
    void Enqueue(JsonObject updates);
}

public sealed class SessionStateSink : ISessionStateSink
{
    private readonly SessionStateWriter _writer;
    public SessionStateSink(SessionStateWriter writer) => _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    public bool IsPrimed => _writer.IsPrimed;
    public JsonObject ReadSnapshot() => _writer.ReadSnapshot();
    public void Enqueue(JsonObject updates) => _writer.Enqueue(updates);
}

public sealed record SettingsWindowServices(
    ApplicationPaths Paths,
    ISettingsStore Store,
    IAiKeyValidator Keys,
    IEncoderCatalog Encoders,
    ISettingsBackupStore Backups,
    IUpdateChecker Updates,
    ISessionStateSink State)
{
    public static SettingsWindowServices CreateDefault(ApplicationPaths? paths = null)
    {
        var p = paths ?? ApplicationPaths.CreateDefault();
        return new SettingsWindowServices(
            p,
            SettingsManagerStore.Instance,
            AiKeyValidator.Instance,
            FfmpegEncoderCatalog.Instance,
            FileSettingsBackupStore.Instance,
            UpdateServiceChecker.Instance,
            new SessionStateSink(SessionStateWriter.For(p)));
    }
}
