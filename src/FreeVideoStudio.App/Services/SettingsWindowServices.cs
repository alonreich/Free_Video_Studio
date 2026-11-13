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


/// <summary>SETTINGSDI_01 — the settings document: read the frozen snapshot, change it in one transaction.</summary>
public interface ISettingsStore
{
    /// <summary>The published, read-only snapshot (SETTINGSFREEZE_01).</summary>
    AppSettings Current { get; }

    /// <summary>One SETTX_01 transaction. False when it did not commit (nothing changed).</summary>
    bool TryUpdate(Action<AppSettings> patch);
}

/// <summary>Production <see cref="ISettingsStore"/>: <see cref="SettingsManager"/>.</summary>
public sealed class SettingsManagerStore : ISettingsStore
{
    public static readonly SettingsManagerStore Instance = new();
    private SettingsManagerStore() { }
    public AppSettings Current => SettingsManager.Instance;
    public bool TryUpdate(Action<AppSettings> patch) => SettingsManager.Update(patch);
}

/// <summary>SETTINGSDI_01 — the outcome of checking an AI key with its company.</summary>
public sealed record KeyTestResult(bool Success, string Message, bool Cancelled = false, IReadOnlyList<string>? Models = null);

/// <summary>SETTINGSDI_01 — talks to the AI companies about keys and models.</summary>
public interface IAiKeyValidator
{
    /// <summary>Checks <paramref name="key"/> with <paramref name="vendor"/>. Never throws; cancellation is a result.</summary>
    Task<KeyTestResult> TestAsync(AiVendor vendor, string key, string googleModel, CancellationToken ct);

    /// <summary>The models <paramref name="key"/> may use. Empty on failure; never throws.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(AiVendor vendor, string key, CancellationToken ct);
}

/// <summary>Production <see cref="IAiKeyValidator"/>: GeminiClient / AiVisionProviders.</summary>
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
        try
        {
            if (vendor == AiVendor.Google)
            {
                string resolvedModel = GeminiClient.ResolveModel(googleModel);
                progress?.Invoke($"Sending quick test request to {vendorName} ({resolvedModel})…");
                var (success, message) = await GeminiClient.Shared.TestKeyAsync(key, googleModel, ct).ConfigureAwait(false);
                if (!success) return new KeyTestResult(false, message);
                progress?.Invoke($"Fetching available {vendorName} models for your key…");
                IReadOnlyList<string> googleModels = Array.Empty<string>();
                try { googleModels = await AiVisionProviders.Get(vendor).ListModelsAsync(key, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                if (googleModels.Count > 0)
                    progress?.Invoke($"Verified! {vendorName} returned {googleModels.Count} live model(s).");
                return new KeyTestResult(true, message, Models: googleModels);
            }
            progress?.Invoke($"Connecting to {vendorName} and checking available models…");
            var models = await AiVisionProviders.Get(vendor).ListModelsAsync(key, ct).ConfigureAwait(false);
            if (models.Count == 0)
            {
                return new KeyTestResult(false, $"{vendorName} accepted the key, but returned 0 vision models.");
            }
            progress?.Invoke($"Verified! {vendorName} returned {models.Count} model(s).");
            return new KeyTestResult(true, $"Key works. {models.Count} model(s) available.", Models: models);
        }
        catch (OperationCanceledException ex)
        {
            CoreLogger.Swallowed(ex);
            return new KeyTestResult(false, "Not tested yet", Cancelled: true);
        }
        catch (AiProviderException ex)
        {
            RuntimeLog.Info("Settings", $"{vendor} key test failed: {ex.Kind}.");
            return new KeyTestResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("AI", $"{vendor} key test failed: {ex.Message}");
            return new KeyTestResult(false, $"Connection failed: {ex.Message}");
        }
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

/// <summary>SETTINGSDI_01 / SETCHIPS_01 — which hardware video encoders this PC has.</summary>
public interface IEncoderCatalog
{
    /// <summary>FFmpeg encoder names found (e.g. h264_nvenc). Null when detection failed. Never throws.</summary>
    Task<IReadOnlyCollection<string>?> DetectAsync();
}

/// <summary>
/// Production <see cref="IEncoderCatalog"/>: probes FFmpeg ONCE per process, off the UI thread,
/// and caches the answer (the hardware does not change while the app runs).
/// </summary>
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

/// <summary>SETTINGSDI_01 / SETBACKUPIO_01 — reading and writing a settings backup file.</summary>
public interface ISettingsBackupStore
{
    /// <summary>The largest file accepted as a backup.</summary>
    long MaxBytes { get; }

    /// <summary>Validates size BEFORE reading, then reads asynchronously. Null + reason when refused.</summary>
    Task<(string? Json, string? Error)> ReadAsync(string path, CancellationToken ct);

    /// <summary>Writes atomically (temp file + replace), asynchronously.</summary>
    Task WriteAsync(string path, string json, CancellationToken ct);
}

/// <summary>Production <see cref="ISettingsBackupStore"/> on the local file system.</summary>
public sealed class FileSettingsBackupStore : ISettingsBackupStore
{
    public static readonly FileSettingsBackupStore Instance = new();
    private FileSettingsBackupStore() { }

    /// <summary>SETBACKUPIO_01 — shared limit: a real backup is a few kilobytes.</summary>
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

/// <summary>SETTINGSDI_01 — the manual update controls on the About tab.</summary>
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

/// <summary>Production <see cref="IUpdateChecker"/>: <see cref="UpdateService"/>.</summary>
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

/// <summary>SETTINGSDI_01 — the cross-process session-state store (last tab, music folder, merger folder).</summary>
public interface ISessionStateSink
{
    bool IsPrimed { get; }
    JsonObject ReadSnapshot();
    void Enqueue(JsonObject updates);
}

/// <summary>Production <see cref="ISessionStateSink"/>: <see cref="SessionStateWriter"/>.</summary>
public sealed class SessionStateSink : ISessionStateSink
{
    private readonly SessionStateWriter _writer;
    public SessionStateSink(SessionStateWriter writer) => _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    public bool IsPrimed => _writer.IsPrimed;
    public JsonObject ReadSnapshot() => _writer.ReadSnapshot();
    public void Enqueue(JsonObject updates) => _writer.Enqueue(updates);
}

/// <summary>
/// SETTINGSDI_01 — the bundle the composition root hands the Settings window (and its view-model).
/// <see cref="CreateDefault"/> exists for processes/places that have no composition root in reach
/// (the Video Merger, the XAML previewer); it builds the same production adapters.
/// </summary>
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
