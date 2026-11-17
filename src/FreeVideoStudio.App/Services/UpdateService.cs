using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

internal static partial class UpdateService
{
    private const string DevelopmentUpdateMessage = "Development copy: close the app and run dev.cmd to rebuild the current source.";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/alonreich/Free_Video_Studio/releases/latest";

    internal const string ReleasesPageUrl = "https://github.com/alonreich/Free_Video_Studio/releases";

    internal const string SetupAssetName = "FreeVideoStudio.exe";
    private const string LegacyFullAssetName = "FreeVideoStudio.exe";
    private const string ExpectedAssetName = SetupAssetName;

    private static readonly string[] AllowedAssetHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };
    private const string UserAgent = "FreeVideoStudio-Updater";
    private const string LastCheckFile = "update_last_check_utc.txt";
    private const string SkippedTagFile = "update_skipped_tag.txt";

    private const string LastResultFile = "update_last_result.txt";

    private const double TypicalBytesPerSecond = 2.5 * 1024 * 1024;

    internal static readonly TimeSpan StartupGracePeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumIntervalBetweenChecks = TimeSpan.FromMinutes(15);

    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Http = CreateHttpClient();
    private static int _checkInProgress;

    internal sealed record UpdateRelease(
        string Tag,
        string DownloadUrl,
        string? Sha256Hex,
        long Size,
        string? ReleaseNotes,
        string? PageUrl = null,
        IReadOnlyDictionary<string, string>? Assets = null)
    {
        public bool TryGetAsset(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? url)
        {
            url = null;
            return Assets is not null && Assets.TryGetValue(name, out url) && !string.IsNullOrWhiteSpace(url);
        }

        public string AssetUrl(string name) => TryGetAsset(name, out string? url) ? url
            : throw new IOException($"The release is missing the file {name}.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task RunStartupCheckAsync(Window owner)
    {
        if (FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.IsActive) return;
        if (Environment.GetCommandLineArgs().Contains("--upgrade-health")) return;

        if (RuntimeLog.IsDevMode && DevReleaseFolder is null)
        {
            RuntimeLog.Info("UPDATE", "Dev mode detected; update check skipped.");
            return;
        }

        if (!SettingsManager.Instance.UpdateConsentAsked && !await AskUpdateConsentAsync(owner).ConfigureAwait(false))
            return;

        if (!SettingsManager.Instance.AutoUpdateChecks)
        {
            RuntimeLog.Info("UPDATE", "Update checks are disabled in Settings; staying silent.");
            return;
        }

        try
        {
            await Task.Delay(DevReleaseFolder is null ? StartupGracePeriod : TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (System.Exception swallowed4)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed4);
            return;
        }

        await RunAutomaticCheckAsync(owner).ConfigureAwait(false);

        while (true)
        {
            try
            {
                await Task.Delay(DevReleaseFolder is null ? RecheckInterval : TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            }
            catch (System.Exception swallowed5)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed5);
                return;
            }
            if (!await Dispatcher.UIThread.InvokeAsync(() => owner.IsVisible)) return;
            if (!SettingsManager.Instance.AutoUpdateChecks) continue;
            if (PendingVersion is not null) continue;
            await RunAutomaticCheckAsync(owner).ConfigureAwait(false);
        }
    }

    private static async Task RunAutomaticCheckAsync(Window owner)
    {
        bool ownerStillVisible = await Dispatcher.UIThread.InvokeAsync(() => owner.IsVisible);
        if (!ownerStillVisible) return;

        if (DevReleaseFolder is null && !ThrottlePermitsCheck()) return;

        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
        {
            RuntimeLog.Info("UPDATE", "Another update check is already in progress; skipping the automatic check.");
            return;
        }

        try
        {
            RuntimeLog.Info("UPDATE", "Running automatic update probe against GitHub releases...");
            (UpdateRelease? release, string problem) = await QueryLatestReleaseAsync().ConfigureAwait(false);
            if (release is null)
            {
                RecordResult(problem + " The app will try again later.");
                return;
            }

            string currentVer = DeploymentLifecycle.GetCurrentVersion();
            string remote = release.Tag.TrimStart('v', 'V');
            string local = currentVer.TrimStart('v', 'V');
            int cmp = DeploymentLifecycle.CompareVersions(release.Tag, currentVer);
            if (cmp <= 0)
            {
                RuntimeLog.Info("UPDATE", $"Already up to date (installed {currentVer}, latest {release.Tag}).");
                RecordResult($"You have the latest version ({local}).");
                return;
            }

            RuntimeLog.Info("UPDATE", $"Newer version found: {release.Tag} (installed {currentVer}). Prompting user.");

            if (IsPaused(release.Tag))
            {
                RecordResult($"Version {remote} is available — the download is paused. Choose Continue on the update card, or Check for updates now.");
                return;
            }

            string skipped = UiStateStore.ReadText(SkippedTagFile).Trim();
            if (string.Equals(skipped, release.Tag, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Info("UPDATE", $"Release {release.Tag} was skipped by the user; staying quiet until a newer one appears.");
                RecordResult($"Version {remote} is available — you chose to skip it.");
                return;
            }

            if (!SettingsManager.Instance.AutoDownloadUpdates)
            {
                const string reason = "automatic background downloads are turned off in Settings";
                RuntimeLog.Info("UPDATE", $"Newer version {remote} is available; automatic background download skipped because {reason}.");
                RecordResult($"Version {remote} is available. Download skipped because {reason}. You can check and download manually via Settings › About.");
                Dispatcher.UIThread.Post(() =>
                {
                    FloatingNotice.Info(owner, $"Free Video Studio version {remote} is available. Go to Settings › About to download.");
                });
                return;
            }

            RecordResult($"Version {remote} is available (you have {local}). Downloading it in the background…");
            _ = StartBackgroundUpdate(owner, release);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Automatic update check failed (staying silent): {ex.Message}");
            RecordResult("The update check failed: " + ex.Message);
        }
        finally
        {
            _ = Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    internal const string PrivacyPolicyUrl = "https://github.com/alonreich/Free_Video_Studio/blob/main/PRIVACY.md";

    private static async Task<bool> AskUpdateConsentAsync(Window owner)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            bool? allowed = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!owner.IsVisible) return (bool?)null;
                bool yes = await ConfirmDialogWindow.AskAsync(owner,
                    "Free Video Studio can check GitHub for a newer version by itself, a few minutes after it starts. "
                    + "When one exists it is downloaded in the background and you choose when it is installed."
                    + Environment.NewLine + Environment.NewLine
                    + "You can change this any time in Settings › General. Nothing else is sent anywhere unless you ask for it "
                    + "(AI features use only the company whose key you enter). Privacy policy: " + PrivacyPolicyUrl,
                    "Check for updates automatically?", "Yes, check for updates", "No, I'll check myself");
                return (bool?)yes;
            });
            if (allowed is null) return false;
            SettingsManager.SetUpdateConsent(allowed.Value);
            RuntimeLog.Info("UPDATE", allowed.Value
                ? "UPDATECONSENT_01: the user allowed automatic update checks."
                : "UPDATECONSENT_01: the user declined automatic update checks.");
            return allowed.Value;
        }
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    public static async Task CheckManualAsync(Window owner, Action<string>? statusCallback = null)
    {
        if (FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.IsActive)
        {
            statusCallback?.Invoke(DevelopmentUpdateMessage);
            return;
        }
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
        {
            RuntimeLog.Info("UPDATE", "Manual update check requested while an update check is already in progress.");
            FloatingNotice.Warn(owner, "An update check is already in progress...");
            statusCallback?.Invoke("An update check is already in progress...");
            return;
        }

        try
        {
            FloatingNotice.Info(owner, "Checking GitHub for updates...");
            statusCallback?.Invoke("Checking GitHub for updates...");
            RuntimeLog.Info("UPDATE", "Manual update check initiated by user.");

            (UpdateRelease? release, string problem) = await QueryLatestReleaseAsync().ConfigureAwait(false);
            if (release is null)
            {
                RecordResult(problem);
                statusCallback?.Invoke(problem);
                string message = problem.Contains("limit reached", StringComparison.OrdinalIgnoreCase)
                    ? problem
                    : problem + " Please check your network connection and try again.";
                await ErrorReporter.ShowAsync(owner, "Update Check Failed", message);
                return;
            }

            string currentVer = DeploymentLifecycle.GetCurrentVersion();
            int cmp = DeploymentLifecycle.CompareVersions(release.Tag, currentVer);
            if (cmp <= 0)
            {
                RecordResult($"You have the latest version ({currentVer}).");
                statusCallback?.Invoke($"Up to date (v{currentVer}).");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NativeDialog.ShowInfo($"You are already running the latest version of Free Video Studio (v{currentVer}).");
                });
                return;
            }

            RecordResult($"Version {release.Tag} is available (you have {currentVer}). Downloading it in the background…");
            if (string.Equals(GetSkippedVersion(), release.Tag, StringComparison.OrdinalIgnoreCase)) ClearSkippedVersion();
            if (!StartBackgroundUpdate(owner, release))
            {
                statusCallback?.Invoke(CannotDownloadMessage);
                await ErrorReporter.ShowAsync(owner, "Update Can't Download", $"Version {release.Tag} is available. {CannotDownloadMessage}");
                return;
            }
            statusCallback?.Invoke($"Version {release.Tag} found — downloading in the background. Watch the corner of the main window.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Manual update check failed: {ex.Message}");
            string friendly = FreeVideoStudio.App.Infrastructure.UserFacingError.Describe(ex);
            RecordResult("The update check failed. " + friendly);
            statusCallback?.Invoke("Check failed. " + friendly);
            await ErrorReporter.ShowAsync(owner, "Update Check Failed", "The update check failed. " + friendly, ex.ToString());
        }
        finally
        {
            _ = Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    internal const string DevUpdateSourceVariable = "FVS_DEV_UPDATE_SOURCE";

    private static readonly string[] DevOnlyEnvironment =
    [
        "FVS_DEV_LOG_DIR", DevUpdateSourceVariable, "FVS_ALLOW_UNSIGNED_UPDATE"
    ];

    internal static string? DevReleaseFolder
    {
        get
        {
            if (!RuntimeLog.IsDevMode) return null;
            string? root = Environment.GetEnvironmentVariable(DevUpdateSourceVariable);
            return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : root;
        }
    }

    internal static string? LocalDevSourcePath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !uri.IsFile) return null;
        if (DevReleaseFolder is null)
            throw new InvalidOperationException("A local update file is only accepted in the dev.cmd environment.");
        return uri.LocalPath;
    }

    internal static async Task<(UpdateRelease? Release, string Problem)> QueryLocalDevReleaseAsync(string repoRoot)
    {
        string exe = Path.Combine(repoRoot, "compiled", SetupAssetName);
        if (!File.Exists(exe))
            return (null, "DEV: no local build in .\\compiled yet. Run dev_build.cmd to make one.");
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
            string version = (info.ProductVersion ?? info.FileVersion ?? string.Empty).Split('+')[0].Trim().TrimStart('v', 'V');
            if (!DeploymentLifecycle.TryParseVersion(version, out _))
                return (null, $"DEV: could not read the version of .\\compiled\\{ExpectedAssetName}.");

            var file = new FileInfo(exe);
            string sha256 = await Task.Run(() =>
            {
                using FileStream stream = File.OpenRead(exe);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }).ConfigureAwait(false);

            string notes = $"DEV MODE — local build from .\\compiled, built {file.LastWriteTime:yyyy-MM-dd HH:mm}.\n"
                         + "This update comes from your own folder, not from GitHub. Everything after this is the production flow.";

            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ExpectedAssetName] = new Uri(exe).AbsoluteUri };
            string releaseAssets = Path.Combine(repoRoot, "obj", "ReleaseAssets");
            foreach (string name in new[] { SmartUpdateManifest.FileName, SmartUpdateManifest.SignatureFileName })
            {
                string path = Path.Combine(releaseAssets, name);
                if (File.Exists(path)) assets[name] = new Uri(path).AbsoluteUri;
            }
            string smartAssets = Path.Combine(releaseAssets, "smart");
            if (Directory.Exists(smartAssets))
                foreach (string path in Directory.GetFiles(smartAssets)) assets[Path.GetFileName(path)] = new Uri(path).AbsoluteUri;

            RuntimeLog.Info("UPDATE", $"DEV update source: {exe} (version {version}, {RuntimePayloadManifest.FormatBytes(file.Length)}, {assets.Count} assets).");
            return (new UpdateRelease("v" + version, new Uri(exe).AbsoluteUri, sha256, file.Length, notes, null, assets), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Info("UPDATE", "DEV update source unreadable: " + ex.Message);
            return (null, "DEV: could not read the local build: " + ex.Message);
        }
    }

    internal static string? DescribeDownload(long bytes)
    {
        if (bytes <= 0) return null;
        double seconds = bytes / TypicalBytesPerSecond;
        string time = seconds < 60 ? "less than a minute" : $"about {Math.Ceiling(seconds / 60):0} minute{(seconds > 60 ? "s" : "")}";
        return $"Download size: {RuntimePayloadManifest.FormatBytes(bytes)} — {time} on a typical home connection.";
    }

    private static void RecordResult(string message)
    {
        try
        {
            UiStateStore.WriteText(LastResultFile,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n" + message.Replace('\n', ' ').Trim());
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    public static string DescribeLastCheck()
    {
        if (FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.IsActive) return DevelopmentUpdateMessage;
        string text;
        try { text = UiStateStore.ReadText(LastResultFile); }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return "Last check: unknown.";
        }

        int split = text.IndexOf('\n');
        if (split <= 0 || !DateTime.TryParse(text[..split], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when))
            return SettingsManager.Instance.AutoUpdateChecks
                ? "Not checked yet. The app checks automatically when it starts."
                : "Not checked yet. Automatic checks are off — use the button to check now.";

        return $"Last checked {DescribeAgo(DateTime.UtcNow - when.ToUniversalTime(), when)}: {text[(split + 1)..].Trim()}";
    }

    internal static string DescribeAgo(TimeSpan ago, DateTime when)
    {
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} minute{((int)ago.TotalMinutes == 1 ? "" : "s")} ago";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} hour{((int)ago.TotalHours == 1 ? "" : "s")} ago";
        return "on " + when.ToLocalTime().ToString("MMM d, yyyy 'at' HH:mm", CultureInfo.CurrentCulture);
    }

    public static string GetSkippedVersion()
    {
        try { return UiStateStore.ReadText(SkippedTagFile).Trim(); }
        catch (System.Exception swallowed6)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed6);
            return string.Empty;
        }
    }

    public static void ClearSkippedVersion()
    {
        try
        {
            UiStateStore.WriteText(SkippedTagFile, string.Empty);
            RuntimeLog.Info("UPDATE", "Skipped version cleared by user.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Could not clear skipped version: {ex.Message}");
        }
    }

    private static bool ThrottlePermitsCheck()
    {
        string last = UiStateStore.ReadText(LastCheckFile);
        if (DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime when) &&
            DateTime.UtcNow - when < MinimumIntervalBetweenChecks)
        {
            RuntimeLog.Info("UPDATE", $"Last update check was at {when:u} (throttled for {MinimumIntervalBetweenChecks.TotalMinutes:0}m); skipping startup check.");
            return false;
        }

        UiStateStore.WriteText(LastCheckFile, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return true;
    }

    private static async Task<(UpdateRelease? Release, string Problem)> QueryLatestReleaseAsync()
    {
        if (DevReleaseFolder is { } devFolder) return await QueryLocalDevReleaseAsync(devFolder).ConfigureAwait(false);
        try
        {
            string targetUrl = LatestReleaseApiUrl;

            using var cts = new CancellationTokenSource(ProbeTimeout);
            using var response = await Http.GetAsync(targetUrl, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                RuntimeLog.Info("UPDATE", $"Release probe returned HTTP {code}; staying silent.");
                if (code == 403 || code == 429)
                {
                    return (null, "Update check limit reached; please try again later.");
                }
                return (null, $"GitHub did not answer the update check (HTTP {code}).");
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            JsonObject? root = JsonNode.Parse(json)?.AsObject();
            if (root?["draft"]?.GetValue<bool>() == true || root?["prerelease"]?.GetValue<bool>() == true)
            {
                RuntimeLog.Info("UPDATE", "Latest release is a draft/prerelease; staying silent.");
                return (null, "No finished release is published yet.");
            }

            if (root is null)
            {
                return (null, "No finished release is published yet.");
            }

            string? tag = root["tag_name"]?.GetValue<string>();
            string? releaseNotes = root["body"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tag))
            {
                RuntimeLog.Fail("UPDATE", "Release JSON carried no tag_name; staying silent.");
                return (null, "The latest release on GitHub has no version number.");
            }

            JsonNode? asset = null;
            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonNode? candidate in root?["assets"]?.AsArray() ?? [])
            {
                string? name = candidate?["name"]?.GetValue<string>();
                if (name is null) continue;
                string? assetUrl = candidate?["browser_download_url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(assetUrl) && IsAllowedAssetUrl(assetUrl)) assets[name] = assetUrl;

                if (name.Equals(SetupAssetName, StringComparison.OrdinalIgnoreCase))
                    asset = candidate;
                else if (name.Equals(LegacyFullAssetName, StringComparison.OrdinalIgnoreCase))
                    asset ??= candidate;
            }

            string? url = asset?["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
            {
                RuntimeLog.Fail("UPDATE", $"Release {tag} carries no '{ExpectedAssetName}' asset; staying silent.");
                return (null, $"The latest release ({tag}) has no installer attached yet.");
            }

            if (!IsAllowedAssetUrl(url!))
            {
                RuntimeLog.Fail("UPDATE", $"Release {tag} points its asset at an unexpected location; refusing to download it.");
                return (null, $"The latest release ({tag}) points to an unexpected download location, so it was ignored for safety.");
            }

            if (!TrySanitizeTagForPath(tag!, out _))
            {
                RuntimeLog.Fail("UPDATE", $"Release tag '{tag}' is not usable as a folder name; staying silent.");
                return (null, $"The latest release has an unusable version name ('{tag}').");
            }

            string? digest = asset?["digest"]?.GetValue<string>();
            string? sha256 = null;
            if (!string.IsNullOrEmpty(digest))
            {
                int colon = digest.IndexOf(':');
                sha256 = colon >= 0 ? digest[(colon + 1)..].ToLowerInvariant() : null;
            }

            long size = asset?["size"]?.GetValue<long>() ?? 0;

            return (new UpdateRelease(
                tag, url, sha256, size, releaseNotes,
                root?["html_url"]?.GetValue<string>(),
                assets), string.Empty);
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("UPDATE", $"Release probe failed (offline or blocked?): {ex.Message}");
            return (null, "Could not reach GitHub to check for updates (no internet connection, or it is blocked).");
        }
    }

    private static void RestartToUpdate(Window owner)
    {
        Window main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow ?? owner;
        try
        {
            if (!ReferenceEquals(owner, main) && owner.IsVisible) owner.Close();
            main.Close();
        }
        catch (InvalidOperationException ex)
        {
            RuntimeLog.Fail("UPDATE", "Could not close the app for the update: " + ex.Message);
            FloatingNotice.Warn(owner, "Please close Free Video Studio to finish the update.");
            return;
        }

        DateTime giveUp = DateTime.UtcNow.AddMinutes(2);
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        poll.Tick += (_, _) =>
        {
            if (!main.IsVisible || DateTime.UtcNow > giveUp) { poll.Stop(); return; }
            if (!main.IsActive) return;
            poll.Stop();
            FloatingNotice.Info(main, "Update postponed — it will install as soon as you close Free Video Studio.");
        };
        poll.Start();
    }

    internal static string DescribeDownloadProgress(long copied, long total, double bytesPerSecond)
    {
        const double Mb = 1024 * 1024;
        var parts = new List<string>();
        if (total > 0)
        {
            parts.Add($"{Math.Round(Math.Clamp((double)copied / total, 0, 1) * 100):0}%");
            parts.Add($"{copied / Mb:0} of {total / Mb:0} MB");
        }
        else
        {
            parts.Add($"{copied / Mb:0} MB downloaded");
        }
        if (bytesPerSecond > 1024)
        {
            parts.Add(bytesPerSecond >= Mb ? $"{bytesPerSecond / Mb:0.0} MB/s" : $"{bytesPerSecond / 1024:0} KB/s");
            if (total > copied)
            {
                double seconds = (total - copied) / bytesPerSecond;
                parts.Add(seconds < 10 ? "a few seconds left"
                        : seconds < 60 ? $"about {Math.Ceiling(seconds / 5) * 5:0} seconds left"
                        : $"about {Math.Ceiling(seconds / 60):0} min left");
            }
        }
        else if (copied < total)
        {
            parts.Add("waiting for data…");
        }
        return string.Join("  ·  ", parts);
    }

    private static bool IsAllowedAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        string host = uri.Host;
        foreach (string allowed in AllowedAssetHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool TrySanitizeTagForPath(string? tag, out string folderName)
    {
        folderName = string.Empty;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        string candidate = tag!.Trim();
        if (candidate.Length == 0 || candidate.Length > 64) return false;

        if (candidate.StartsWith('.') || candidate.EndsWith('.')) return false;
        if (candidate.Contains("..", StringComparison.Ordinal)) return false;

        foreach (char c in candidate)
        {
            bool ok = (c >= 'a' && c <= 'z')
                   || (c >= 'A' && c <= 'Z')
                   || (c >= '0' && c <= '9')
                   || c == '.' || c == '-' || c == '_';
            if (!ok) return false;
        }

        string stem = candidate;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        foreach (string reserved in ReservedDeviceNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return false;
        }

        folderName = candidate;
        return true;
    }

    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
}
