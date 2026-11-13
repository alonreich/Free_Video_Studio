using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>SETTINGSDI_01 — how a status line should look (the window maps it to a theme brush).</summary>
public enum StatusTone { Muted, Warning, Success, Danger }

/// <summary>SETTINGSDI_01 — the "Not tested yet / Key works / …" line under one AI key.</summary>
public sealed class KeyTestStatus : ViewModelBase
{
    private string _text = "Not tested yet";
    private StatusTone _tone = StatusTone.Muted;
    private bool _isBusy;

    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    public StatusTone Tone { get => _tone; private set => SetProperty(ref _tone, value); }
    public bool IsBusy { get => _isBusy; internal set => SetProperty(ref _isBusy, value); }

    internal void Show(string text, StatusTone tone) { Text = text; Tone = tone; }
}

/// <summary>
/// SETTINGSDI_01 / UI-LOGIC separation — everything the Settings window used to do itself that is
/// not presentation: testing keys and listing models with the AI companies, detecting video chips,
/// reading and writing backup files, and committing SAVE. All of it goes through the injected
/// <see cref="SettingsWindowServices"/>, so it is testable without a window, the internet, a GPU or
/// real files. The window only shows the results.
/// </summary>
public sealed partial class SettingsViewModel : IDisposable
{
    private SettingsWindowServices? _services;

    /// <summary>The services this view-model talks to (production adapters unless injected).</summary>
    public SettingsWindowServices Services => _services ??= SettingsWindowServices.CreateDefault();

    /// <summary>The music folder the state store held when the window opened; null = unknown (store not primed).</summary>
    private string? _initialMusicFolder;

    /// <summary>The merger save folder when the window opened (ISSUE_04 state-store copy is only rewritten on change).</summary>
    private string _openMergerOutputDir = "";

    /// <summary>
    /// SETTINGSDI_01 — builds the pending state from the injected store and session state. This is
    /// the constructor the window uses; the (source, musicFolder, …) one stays for tests.
    /// </summary>
    public static SettingsViewModel Open(SettingsWindowServices services, IReadOnlyList<string> gameProfiles)
    {
        ArgumentNullException.ThrowIfNull(services);
        string music = "";
        string? initialMusic = null;
        try
        {
            var state = services.State.ReadSnapshot();
            if (state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
            {
                music = node.GetValue<string>() ?? "";
                initialMusic = music;
            }
            else if (services.State.IsPrimed)
            {
                initialMusic = "";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            RuntimeLog.Swallowed(ex);
        }

        var source = services.Store.Current;
        var vm = new SettingsViewModel(source, music, gameProfiles)
        {
            _services = services,
            _initialMusicFolder = initialMusic,
            _openMergerOutputDir = source.MergerOutputDirectory ?? "",
        };
        return vm;
    }


    /// <summary>
    /// SETTX_01 — ONE settings transaction writes every pending value. The state-store copies (music
    /// folder, merger save folder) are written only after that commit succeeded. False = nothing saved.
    /// </summary>
    public bool Commit()
    {
        bool committed = Services.Store.TryUpdate(s =>
        {
            if (s.AutoUpdateChecks != AutoUpdateChecks) s.UpdateConsentAsked = true;
            WriteTo(s);
        });
        if (!committed)
        {
            RuntimeLog.Fail("SETTINGS", "Settings save failed: the settings transaction did not commit.");
            return false;
        }

        try
        {
            if (!string.Equals(_openMergerOutputDir, MergerOutputDirectory, StringComparison.OrdinalIgnoreCase))
                Services.State.Enqueue(new JsonObject { ["MergerOutputDirectory"] = MergerOutputDirectory });
            if (_initialMusicFolder != null || !string.IsNullOrEmpty(MusicFolder))
                Services.State.Enqueue(new JsonObject { ["CustomMusicDirectory"] = MusicFolder });
        }
        catch (InvalidOperationException ex) { RuntimeLog.Swallowed(ex); }
        return true;
    }


    private readonly Dictionary<AiVendor, KeyTestStatus> _keyStatus = new()
    {
        [AiVendor.Google] = new KeyTestStatus(),
        [AiVendor.Anthropic] = new KeyTestStatus(),
        [AiVendor.OpenAI] = new KeyTestStatus(),
    };

    /// <summary>
    /// AIKEYCANCEL_01 — ONE cancellation slot PER COMPANY. Testing the Anthropic key used to cancel a
    /// Google test still in flight (one shared token), leaving Google stuck on "Contacting…". Now a new
    /// test only replaces the earlier test of the SAME company.
    /// </summary>
    private readonly Dictionary<AiVendor, CtsSlot> _keyTests = new()
    {
        [AiVendor.Google] = new CtsSlot(),
        [AiVendor.Anthropic] = new CtsSlot(),
        [AiVendor.OpenAI] = new CtsSlot(),
    };

    private readonly CtsSlot _modelRefresh = new();

    public KeyTestStatus StatusFor(AiVendor vendor) => _keyStatus[vendor];
    public KeyTestStatus GeminiKeyStatus => _keyStatus[AiVendor.Google];
    public KeyTestStatus ClaudeKeyStatus => _keyStatus[AiVendor.Anthropic];

    /// <summary>Clears one company's pending key (deleted on SAVE) and says so.</summary>
    public void RemoveKey(AiVendor vendor)
    {
        switch (vendor)
        {
            case AiVendor.Google: GeminiApiKey = ""; break;
            case AiVendor.Anthropic: ClaudeApiKey = ""; break;
            default: OpenAiApiKey = ""; break;
        }
        _keyStatus[vendor].Show("The key will be deleted when you press SAVE.", StatusTone.Warning);
    }

    /// <summary>Checks the pending key of <paramref name="vendor"/> with that company. Never throws.</summary>
    public async Task TestKeyAsync(AiVendor vendor)
    {
        var status = _keyStatus[vendor];
        string key = PendingKey(vendor);
        if (string.IsNullOrWhiteSpace(key))
        {
            status.Show("Paste a key first.", StatusTone.Warning);
            return;
        }

        string model = GeminiTestModel;
        string vendorName = AiVisionProviders.DisplayName(vendor);
        status.Show(vendor == AiVendor.Google
            ? $"Step 1/2: Contacting {vendorName} ({model})…"
            : $"Step 1/1: Contacting {vendorName} and loading models…", StatusTone.Muted);
        status.IsBusy = true;
        try
        {
            KeyTestResult result = Services.Keys is AiKeyValidator realValidator
                ? await realValidator.TestAsync(
                    vendor,
                    key,
                    model,
                    step => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Show(step, StatusTone.Muted)),
                    _keyTests[vendor].Renew())
                : await Services.Keys.TestAsync(vendor, key, model, _keyTests[vendor].Renew());

            if (result.Cancelled)
            {
                status.Show("Not tested yet", StatusTone.Muted);
            }
            else
            {
                if (result.Success && result.Models is { Count: > 0 })
                    _liveModels[vendor] = result.Models;
                status.Show(result.Message, result.Success ? StatusTone.Success : StatusTone.Danger);
                if (result.Success && result.Models is { Count: > 0 })
                    AiRoutingChanged?.Invoke();
            }
        }
        finally { status.IsBusy = false; }
    }


    /// <summary>Models listed live per company (Refresh), shared by the default row and every override row.</summary>
    private readonly Dictionary<AiVendor, IReadOnlyList<string>> _liveModels = new();

    /// <summary>Last live-model refresh status message for Settings › AI UI feedback.</summary>
    public string LastModelRefreshMessage { get; private set; } = string.Empty;

    /// <summary>The model list for a company: the live list after Refresh, else the built-in list.</summary>
    public IReadOnlyList<string> ModelsFor(AiVendor vendor)
        => _liveModels.TryGetValue(vendor, out var live) && live.Count > 0 ? live : AiVisionProviders.GetKnownModels(vendor);

    /// <summary>Asks every company in use which models its key can use. True when the lists were refreshed.</summary>
    public async Task<bool> RefreshModelsAsync()
    {
        var token = _modelRefresh.Renew();
        AiVendor v = DefaultAi.Vendor;
        string key = PendingKey(v);
        string vendorName = AiVisionProviders.DisplayName(v);
        if (string.IsNullOrWhiteSpace(key))
        {
            LastModelRefreshMessage = $"Add your {vendorName} API key first to load live models.";
            return false;
        }

        LastModelRefreshMessage = $"Asking {vendorName} for available models…";
        var models = await Services.Keys.ListModelsAsync(v, key, token);
        if (token.IsCancellationRequested)
        {
            LastModelRefreshMessage = "Model refresh cancelled.";
            return false;
        }
        if (models.Count > 0)
        {
            _liveModels[v] = models;
            LastModelRefreshMessage = $"✓ Loaded {models.Count} live {vendorName} model(s).";
            return true;
        }
        LastModelRefreshMessage = $"Could not load live models from {vendorName} (check key or internet connection). Showing built-in models.";
        return false;
    }


    /// <summary>The ONE company + model every AI feature uses (SETAIONE_01; read-only view).</summary>
    public AiUsageConfig DefaultAi => _p.AiDefaultUsage ??= new AiUsageConfig(AiVendor.Google, GeminiClient.DefaultModel);

    /// <summary>Raised after a company/model change, so the window can re-show the row and the key list.</summary>
    public event Action? AiRoutingChanged;

    /// <summary>Sets the company/model every AI feature uses (a missing model = the company's default).</summary>
    public void SetDefaultAi(AiVendor vendor, string? model)
    {
        var def = DefaultAi;
        def.Vendor = vendor;
        def.Model = string.IsNullOrWhiteSpace(model) ? AiVisionProviders.GetDefaultModel(vendor) : model!;
        foreach (AiUsage u in AppSettings.AllAiUsages)
        {
            var own = _p.GetOwnUsageConfig(u);
            own.Vendor = def.Vendor;
            own.Model = def.Model;
        }
        RaiseAiReady();
        AiRoutingChanged?.Invoke();
    }

    /// <summary>"Use recommended": Google Gemini with the recommended model.</summary>
    public void UseRecommendedAi() => SetDefaultAi(AiVisionProviders.RecommendedVendor, AiVisionProviders.RecommendedModel);

    /// <summary>Fills a missing model with the company default (instead of the window doing it while drawing a list).</summary>
    public string EffectiveModel(AiUsageConfig cfg)
        => string.IsNullOrWhiteSpace(cfg.Model) ? AiVisionProviders.GetDefaultModel(cfg.Vendor) : cfg.Model;


    /// <summary>Detects the video chips once (off the UI thread, cached per process) and applies the result.</summary>
    public async Task DetectEncodersAsync()
    {
        IReadOnlyCollection<string>? found = await Services.Encoders.DetectAsync();
        if (found == null) MarkEncoderDetectionFailed();
        else SetDetectedEncoders(found);
    }


    /// <summary>Writes the backup to <paramref name="path"/> asynchronously. Returns the line to show.</summary>
    public async Task<string> WriteBackupFileAsync(string path)
    {
        try
        {
            await Services.Backups.WriteAsync(path, ExportBackupJson(), _backupIo.Renew());
            RuntimeLog.Info("Settings", "Settings backup written.");
            return $"Saved: {Path.GetFileName(path)} (AI keys were left out).";
        }
        catch (OperationCanceledException ex) { RuntimeLog.Swallowed(ex); return ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RuntimeLog.Fail("Settings", $"Settings backup failed: {ex.Message}");
            return "⚠ The backup could not be saved. Try a different folder.";
        }
    }

    /// <summary>
    /// SETBACKUPIO_01 — validates the file (size, then format) BEFORE anything changes, reads it
    /// asynchronously and loads it as PENDING values. Returns (true, message) or (false, reason).
    /// </summary>
    public async Task<(bool Ok, string Message)> ImportBackupFileAsync(string path)
    {
        try
        {
            var (json, error) = await Services.Backups.ReadAsync(path, _backupIo.Renew());
            if (json == null) return (false, "⚠ " + (error ?? "That file could not be read."));
            string? reason = ImportBackupJson(json);
            if (reason != null) return (false, "⚠ " + reason);
            return (true, $"Loaded: {Path.GetFileName(path)}. Look it over, then press SAVE to keep it (or CANCEL to forget it).");
        }
        catch (OperationCanceledException ex) { RuntimeLog.Swallowed(ex); return (false, ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RuntimeLog.Fail("Settings", $"Settings import failed: {ex.Message}");
            return (false, "⚠ The backup could not be loaded.");
        }
    }

    private readonly CtsSlot _backupIo = new();

    /// <summary>Cancels every network and file operation still running (the window closed).</summary>
    public void Dispose()
    {
        foreach (var slot in _keyTests.Values) slot.Dispose();
        _modelRefresh.Dispose();
        _backupIo.Dispose();
    }
}
