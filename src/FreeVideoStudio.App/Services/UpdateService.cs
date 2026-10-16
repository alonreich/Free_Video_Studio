
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

/// <summary>
/// AUTO-UPDATE SUGGESTOR — the whole feature behind the "Automatically check for updates"
/// checkbox in Settings.
///
/// HOW IT PROBES (and how it avoids false positives):
/// * FvsBuild publishes exactly ONE GitHub release, always named "latest", always carrying a
///   single FreeVideoStudio.exe whose SHA-256 digest GitHub serves in the release JSON
///   (GitHubReleasePublisher verifies the upload with that same digest). The probe therefore
///   reads the releases/latest API endpoint — the "static location" that always describes the
///   newest build — and never guesses from file dates or sizes.
/// * Drafts and prereleases are excluded by the /latest endpoint itself and re-checked here as
///   defense in depth.
/// * Versions are compared as parsed Version objects, STRICTLY greater. An equal tag ("same
///   version already installed") and an older tag (user runs a newer build than is published)
///   both stay perfectly silent.
/// * Any surprise — unparsable tag, missing asset, HTTP error, timeout, offline machine — logs
///   one line and stays silent. The suggestor must never interrupt a user over its own problems.
///
/// HOW IT NAGS (and how it stops):
/// * At most ONE startup probe per 15 minutes (UiStateStore timestamp inside the preserved
///   ProgramData root). The result of every check is recorded for Settings › About (UPDATEUX_06).
/// * "Remind me later" (UPDATEUX_01) = not now; the same release is offered again on a later
///   start (the user said not-now, not never).
/// * "Skip this version" remembers THE TAG in UiStateStore; that exact release is never offered
///   again, any strictly newer one is.
/// * "Never tell me about updates again" writes AutoUpdateChecks=false into settings.json, so
///   the Settings checkbox always tells the truth about the feature's state.
///
/// HOW IT INSTALLS:
/// * Downloads to %LOCALAPPDATA%\FreeVideoStudioUpdates\Downloads\&lt;tag&gt;\ (NOTEMPEXEC_01), verifies the SHA-256 against GitHub's
///   published digest and REFUSES to run anything that does not match.
/// * Launches the downloaded installer, which waits for this app to close (--wait-pid), then the
///   user chooses "Restart &amp; update now" or "Update when I close the app" (UPDATEUX_04). The
///   installer shows its own progress window from the moment the app closes (UPGRADEUX_01).
/// </summary>
internal static partial class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/alonreich/Free_Video_Studio/releases/latest";
    private const string ReleasesApiUrl = "https://api.github.com/repos/alonreich/Free_Video_Studio/releases";

    [System.Runtime.InteropServices.DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    private static bool TryGetWindowsNetworkCost(out uint cost)
    {
        cost = 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var clsid = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
            var iid = new Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B");
            int hr = CoCreateInstance(in clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, in iid, out IntPtr pManager);
            if (hr != 0 || pManager == IntPtr.Zero) return false;
            try
            {
                unsafe
                {
                    void** vtable = *(void***)pManager;
                    delegate* unmanaged[Stdcall]<IntPtr, uint*, void*, int> getCost =
                        (delegate* unmanaged[Stdcall]<IntPtr, uint*, void*, int>)vtable[3];
                    uint c = 0;
                    if (getCost(pManager, &c, null) == 0)
                    {
                        cost = c;
                        return true;
                    }
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(pManager);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
        return false;
    }

    internal static bool IsMeteredOrConstrainedConnection()
    {
        try
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                return false;

            const uint MeteredMask = 0x2 | 0x4 | 0x10000 | 0x20000 | 0x40000 | 0x80000;
            if (TryGetWindowsNetworkCost(out uint cost))
            {
                if ((cost & MeteredMask) != 0) return true;
                if ((cost & 0x1) != 0) return false;
            }

            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wwanpp ||
                     ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wwanpp2))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
        return false;
    }

    /// <summary>
    /// NODROPPER_01 — the full installer is an Inno Setup package. Releases also carry the same bytes
    /// as <see cref="LegacyFullAssetName"/> so copies released before the change still find an installer;
    /// this build prefers the setup name and accepts the old one.
    /// </summary>
    internal const string SetupAssetName = "FreeVideoStudio-Setup.exe";
    private const string LegacyFullAssetName = "FreeVideoStudio.exe";
    private const string ExpectedAssetName = SetupAssetName;

    /// <summary>
    /// SYS-PAYLOADSPLIT — the app-only update package, when a release publishes one.
    ///
    /// <para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// <b>WHY A SECOND ASSET EXISTS.</b> <see cref="ExpectedAssetName"/> is a 322 MB NativeAOT
    /// installer carrying FFmpeg and libmpv as an embedded payload. Downloading it to deliver a
    /// one-line fix costs every user 322 MB, over a 30-minute timeout, to reinstall codec DLLs that
    /// did not change. On a metered connection that is a reason to turn updates off — which turns
    /// every shipped fix into a fix most users never get.
    /// </para>
    ///
    /// <para>
    /// This asset carries the application only. It is used when, and only when, the runtime already
    /// installed on this machine is the one the release expects — proven by comparing
    /// <c>RuntimePayloadManifest</c> fingerprints. Anything else, including "I could not read the
    /// manifest", falls back to the full installer: a bigger download is always safe, a smaller one
    /// is not.
    /// </para>
    ///
    /// <para>⚠️ The size decision is the ONLY thing the fingerprint controls. Whatever is
    /// downloaded is still SHA-256 verified and still Authenticode-checked before it is run
    /// (UPDATETRUST_02). A fingerprint is a hint about what to fetch, never a reason to trust it.
    /// </para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private const string AppOnlyAssetName = "FreeVideoStudio.App.update.zip";

    /// <summary>
    /// SYS-PAYLOADSPLIT — the release's runtime fingerprint, published as a tiny sidecar asset so
    /// the updater can read it without downloading either package.
    /// </summary>
    private const string RuntimeManifestAssetName = "runtime.manifest.json";

    /// <summary>
    /// UPDATETRUST_01 — the ONLY hosts an update asset may be fetched from.
    ///
    /// <para>The asset URL used to be taken from the release JSON verbatim and handed straight to
    /// <c>HttpClient</c>. A response body that named any other host would have been fetched without
    /// comment, and since the SHA-256 that "verifies" the download comes out of that SAME document,
    /// nothing downstream would have objected either. Pinning the host removes the easiest half of
    /// that pairing: an attacker now has to be GitHub, not merely be believed by us.</para>
    ///
    /// <para>⚠️ This gates the URL AS PUBLISHED IN THE JSON. HttpClient still follows GitHub's
    /// redirect to its asset CDN without re-checking the hop, which is why the CDN hosts are listed
    /// too and why this is a defence-in-depth control rather than the primary one — the re-hash and
    /// the Authenticode check before <c>Process.Start</c> are what actually decide.</para>
    /// </summary>
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

    /// <summary>UPDATEUX_06 — "when was the last check, and what did it find?" for Settings › About.</summary>
    private const string LastResultFile = "update_last_result.txt";

    /// <summary>UPDATEUX_02 — the "typical home connection" behind the time estimate (~20 Mbit/s).</summary>
    private const double TypicalBytesPerSecond = 2.5 * 1024 * 1024;


    /// <summary>
    /// UPDATECONSENT_01 — nothing touches the network at startup. The first automatic check waits five
    /// minutes after the window opens (and only after the user agreed to automatic checks): an unknown
    /// program that phones home and downloads executables the moment it starts is what downloader malware
    /// does, and antivirus behaviour engines score it that way.
    /// </summary>
    internal static readonly TimeSpan StartupGracePeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumIntervalBetweenChecks = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Http = CreateHttpClient();
    private static int _checkInProgress;

    internal sealed record UpdateRelease(
        string Tag,
        string DownloadUrl,
        string? Sha256Hex,
        long Size,
        string? ReleaseNotes,
        string? AppOnlyUrl = null,
        string? AppOnlySha256Hex = null,
        long AppOnlySize = 0,
        string? RuntimeManifestUrl = null,
        string? PageUrl = null,
        IReadOnlyDictionary<string, string>? Assets = null)
    {
        /// <summary>DIST-SMART — the URL of a named release asset, when the release published it.</summary>
        public bool TryGetAsset(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? url)
        {
            url = null;
            return Assets is not null && Assets.TryGetValue(name, out url) && !string.IsNullOrWhiteSpace(url);
        }

        /// <summary>DIST-SMART — the URL of a named asset; a missing one means the release is incomplete.</summary>
        public string AssetUrl(string name) => TryGetAsset(name, out string? url) ? url
            : throw new IOException($"The release is missing the file {name}.");

        /// <summary>SYS-PAYLOADSPLIT — true when this release published a small app-only package.</summary>
        public bool HasAppOnlyPackage => !string.IsNullOrWhiteSpace(AppOnlyUrl) && AppOnlySize > 0;

        /// <summary>What the user is about to spend, in the units they think in.</summary>
        public string DescribeDownloadSize(bool appOnly)
            => FreeVideoStudio.Core.Infrastructure.RuntimePayloadManifest.FormatBytes(
                   appOnly ? AppOnlySize : Size);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Entry point, hooked from MainWindow.Opened. Fire-and-forget by design; every failure is
    /// swallowed into RuntimeLog so a broken suggestor can never break the app itself. It never
    /// interrupts the user over its own problems — but every outcome is recorded and shown in
    /// Settings › About (UPDATEUX_06), so a silent check is no longer an invisible one.
    /// </summary>
    public static async Task RunStartupCheckAsync(Window owner)
    {
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

        bool ownerStillVisible = await Dispatcher.UIThread.InvokeAsync(() => owner.IsVisible);
        if (!ownerStillVisible) return;

        if (DevReleaseFolder is null && !ThrottlePermitsCheck()) return;

        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
        {
            RuntimeLog.Info("UPDATE", "Another update check is already in progress; skipping startup check.");
            return;
        }

        try
        {
            RuntimeLog.Info("UPDATE", "Running startup update probe against GitHub releases...");
            (UpdateRelease? release, string problem) = await QueryLatestReleaseAsync().ConfigureAwait(false);
            if (release is null)
            {
                RecordResult(problem + " The app will try again the next time it starts.");
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

            string skipped = UiStateStore.ReadText(SkippedTagFile).Trim();
            if (string.Equals(skipped, release.Tag, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Info("UPDATE", $"Release {release.Tag} was skipped by the user; staying quiet until a newer one appears.");
                RecordResult($"Version {remote} is available — you chose to skip it.");
                return;
            }

            bool isMetered = SettingsManager.Instance.AvoidMeteredNetworkUpdates && IsMeteredOrConstrainedConnection();
            bool autoDownload = SettingsManager.Instance.AutoDownloadUpdates && !isMetered;

            if (!autoDownload)
            {
                string reason = isMetered ? "a metered connection was detected" : "automatic background downloads are turned off in Settings";
                RuntimeLog.Info("UPDATE", $"Newer version {remote} is available; automatic background download skipped because {reason}.");
                RecordResult($"Version {remote} is available. Download skipped because {reason}. You can check and download manually via Settings › About.");
                Dispatcher.UIThread.Post(() =>
                {
                    FloatingNotice.Info(owner, $"Free Video Studio version {remote} is available. Go to Settings › About to download.");
                });
                return;
            }

            RecordResult($"Version {remote} is available (you have {local}). Downloading it in the background…");
            StartBackgroundUpdate(owner, release);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Startup update check failed (staying silent): {ex.Message}");
            RecordResult("The update check failed: " + ex.Message);
        }
        finally
        {
            _ = Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    /// <summary>
    /// UPDATECONSENT_01 — asks ONCE, on a launch after installation, whether Free Video Studio may check
    /// GitHub for updates by itself. Until it is answered nothing is sent. The answer is stored in
    /// settings (UpdateConsentAsked + AutoUpdateChecks) and can be changed in Settings › About; Help ›
    /// Check for Updates always works. Returns true when automatic checks are allowed now. Closing the
    /// question without answering counts as "not now" and it is asked again on the next start.
    /// </summary>
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
                    + "You can change this any time in Settings › About.",
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

    /// <summary>
    /// Explicit manual check triggered on user demand (e.g. from the About tab or Help menu).
    /// Bypasses the startup throttle and auto-update toggle since the user explicitly requested it.
    /// </summary>
    public static async Task CheckManualAsync(Window owner, Action<string>? statusCallback = null)
    {
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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    string message = problem.Contains("limit reached", StringComparison.OrdinalIgnoreCase)
                        ? problem
                        : problem + "\r\nPlease check your network connection and try again.";
                    NativeDialog.ShowError(message, "Update Check Failed");
                });
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
            statusCallback?.Invoke($"Version {release.Tag} found — downloading in the background. Watch the corner of the main window.");
            StartBackgroundUpdate(owner, release);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Manual update check failed: {ex.Message}");
            RecordResult("The update check failed: " + ex.Message);
            statusCallback?.Invoke($"Check failed: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                NativeDialog.ShowError($"Update check failed: {ex.Message}", "Update Check Failed");
            });
        }
        finally
        {
            _ = Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    // ⚠️ Both variables are required, and a file:// URL is accepted ONLY while they are. A release
    internal const string DevUpdateSourceVariable = "FVS_DEV_UPDATE_SOURCE";

    private static readonly string[] DevOnlyEnvironment =
    [
        "FVS_DEV_LOG_DIR", DevUpdateSourceVariable, "FVS_ALLOW_UNSIGNED_UPDATE"
    ];

    /// <summary>The repository root to read .\compiled from, or null outside dev.cmd.</summary>
    internal static string? DevReleaseFolder
    {
        get
        {
            if (!RuntimeLog.IsDevMode) return null;
            string? root = Environment.GetEnvironmentVariable(DevUpdateSourceVariable);
            return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : root;
        }
    }

    /// <summary>The local file behind a dev download URL, or null for a normal (GitHub) URL.</summary>
    internal static string? LocalDevSourcePath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !uri.IsFile) return null;
        if (DevReleaseFolder is null)
            throw new InvalidOperationException("A local update file is only accepted in the dev.cmd environment.");
        return uri.LocalPath;
    }

    /// <summary>
    /// DEVUPDATE_01 — reads .\compiled\FreeVideoStudio-Setup.exe as if it were the GitHub release. The
    /// SHA-256 is taken now, like GitHub's published digest; if the file is rebuilt before the
    /// download finishes, the check fails exactly as a tampered download would.
    /// </summary>
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
            foreach (string name in new[] { AppOnlyAssetName, RuntimeManifestAssetName, SmartUpdateManifest.FileName, SmartUpdateManifest.SignatureFileName })
            {
                string path = Path.Combine(releaseAssets, name);
                if (File.Exists(path)) assets[name] = new Uri(path).AbsoluteUri;
            }
            string smartAssets = Path.Combine(releaseAssets, "smart");
            if (Directory.Exists(smartAssets))
                foreach (string path in Directory.GetFiles(smartAssets)) assets[Path.GetFileName(path)] = new Uri(path).AbsoluteUri;

            string? appOnlyUrl = null, appOnlySha = null;
            long appOnlySize = 0;
            string appOnlyPath = Path.Combine(releaseAssets, AppOnlyAssetName);
            if (assets.ContainsKey(AppOnlyAssetName) && File.GetLastWriteTimeUtc(appOnlyPath) >= file.LastWriteTimeUtc.AddMinutes(-30))
            {
                appOnlyUrl = assets[AppOnlyAssetName];
                appOnlySize = new FileInfo(appOnlyPath).Length;
                appOnlySha = await Task.Run(() => SmartUpdateManifest.Sha256Hex(appOnlyPath)).ConfigureAwait(false);
            }
            assets.TryGetValue(RuntimeManifestAssetName, out string? runtimeUrl);

            RuntimeLog.Info("UPDATE", $"DEV update source: {exe} (version {version}, {RuntimePayloadManifest.FormatBytes(file.Length)}, {assets.Count} assets).");
            return (new UpdateRelease("v" + version, new Uri(exe).AbsoluteUri, sha256, file.Length, notes,
                appOnlyUrl, appOnlySha, appOnlySize, runtimeUrl, null, assets), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Info("UPDATE", "DEV update source unreadable: " + ex.Message);
            return (null, "DEV: could not read the local build: " + ex.Message);
        }
    }

    /// <summary>UPDATEUX_02 — "Download size: 322 MB — about 2 minutes on a typical home connection."</summary>
    internal static string? DescribeDownload(long bytes)
    {
        if (bytes <= 0) return null;
        double seconds = bytes / TypicalBytesPerSecond;
        string time = seconds < 60 ? "less than a minute" : $"about {Math.Ceiling(seconds / 60):0} minute{(seconds > 60 ? "s" : "")}";
        return $"Download size: {RuntimePayloadManifest.FormatBytes(bytes)} — {time} on a typical home connection.";
    }

    /// <summary>UPDATEUX_06 — remembers when the last check ran and what it found.</summary>
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

    /// <summary>
    /// UPDATEUX_06 — "Last checked 5 minutes ago: You have the latest version (2026.10.9.2104)."
    /// Shown in Settings › About so the user can always see that checks run and what they found.
    /// </summary>
    public static string DescribeLastCheck()
    {
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

    /// <summary>At most one probe per 15m, so rapid restarts do not hammer GitHub rate limits.</summary>
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

    /// <summary>UPDATEUX_06 — the release, or null plus one plain-English sentence saying why not.</summary>
    private static async Task<(UpdateRelease? Release, string Problem)> QueryLatestReleaseAsync()
    {
        if (DevReleaseFolder is { } devFolder) return await QueryLocalDevReleaseAsync(devFolder).ConfigureAwait(false);
        try
        {
            bool isBeta = string.Equals(SettingsManager.Instance.UpdateChannel, "Beta", StringComparison.OrdinalIgnoreCase);
            string targetUrl = isBeta ? ReleasesApiUrl + "?per_page=10" : LatestReleaseApiUrl;

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
            JsonObject? root = null;
            if (isBeta)
            {
                var array = JsonNode.Parse(json)?.AsArray();
                if (array != null)
                {
                    foreach (var item in array)
                    {
                        var obj = item?.AsObject();
                        if (obj != null && obj["draft"]?.GetValue<bool>() != true)
                        {
                            root = obj;
                            break;
                        }
                    }
                }
            }
            else
            {
                root = JsonNode.Parse(json)?.AsObject();
                if (root?["draft"]?.GetValue<bool>() == true || root?["prerelease"]?.GetValue<bool>() == true)
                {
                    RuntimeLog.Info("UPDATE", "Latest release is a draft/prerelease; staying silent.");
                    return (null, "No finished release is published yet.");
                }
            }

            if (root is null)
            {
                return (null, isBeta ? "No releases found on Beta channel." : "No finished release is published yet.");
            }

            string? tag = root["tag_name"]?.GetValue<string>();
            string? releaseNotes = root["body"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tag))
            {
                RuntimeLog.Fail("UPDATE", "Release JSON carried no tag_name; staying silent.");
                return (null, "The latest release on GitHub has no version number.");
            }

            JsonNode? asset = null;
            JsonNode? appOnlyAsset = null;
            string? runtimeManifestUrl = null;
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
                else if (name.Equals(AppOnlyAssetName, StringComparison.OrdinalIgnoreCase))
                    appOnlyAsset = candidate;
                else if (name.Equals(RuntimeManifestAssetName, StringComparison.OrdinalIgnoreCase))
                    runtimeManifestUrl = candidate?["browser_download_url"]?.GetValue<string>();
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

            string? appOnlyUrl = appOnlyAsset?["browser_download_url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(appOnlyUrl) && !IsAllowedAssetUrl(appOnlyUrl!))
            {
                RuntimeLog.Fail("UPDATE",
                    $"Release {tag} points its app-only package at an unexpected location; ignoring it "
                  + "and using the full installer.");
                appOnlyUrl = null;
            }

            if (!string.IsNullOrWhiteSpace(runtimeManifestUrl) && !IsAllowedAssetUrl(runtimeManifestUrl!))
                runtimeManifestUrl = null;

            string? appOnlyDigest = appOnlyAsset?["digest"]?.GetValue<string>();
            string? appOnlySha = null;
            if (!string.IsNullOrEmpty(appOnlyDigest))
            {
                int c2 = appOnlyDigest.IndexOf(':');
                appOnlySha = c2 >= 0 ? appOnlyDigest[(c2 + 1)..].ToLowerInvariant() : null;
            }

            return (new UpdateRelease(
                tag, url, sha256, size, releaseNotes,
                appOnlyUrl,
                appOnlySha,
                appOnlyAsset?["size"]?.GetValue<long>() ?? 0,
                runtimeManifestUrl,
                root?["html_url"]?.GetValue<string>(),
                assets), string.Empty);
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("UPDATE", $"Release probe failed (offline or blocked?): {ex.Message}");
            return (null, "Could not reach GitHub to check for updates (no internet connection, or it is blocked).");
        }
    }

    /// <summary>
    /// SYS-PAYLOADSPLIT — DECIDES WHETHER THIS MACHINE NEEDS THE 322 MB INSTALLER OR JUST THE APP.
    ///
    /// <para>
    /// Returns true only when the release published an app-only package AND advertised a runtime
    /// fingerprint AND that fingerprint matches what is installed here. Every other path — no small
    /// package, no advertised fingerprint, an unreadable local manifest, a network failure reading
    /// the sidecar, or a genuine mismatch — returns false and the user gets the full installer.
    /// </para>
    ///
    /// <para>⚠️ THE ASYMMETRY IS THE WHOLE DESIGN. Wrongly choosing the big download costs
    /// bandwidth. Wrongly choosing the small one installs an application against codec binaries it
    /// was not built for, which fails at export time, on the user's machine, after they have done
    /// the work. So every uncertainty resolves to the installer.</para>
    /// </summary>
    /// <param name="verifyInstalledBytes">UPDATEUX_02 — false only for the size shown in the
    /// question, which must not hash hundreds of MB just to print a number. The download itself
    /// always passes true, so the size estimate can never pick the package that is fetched.</param>
    private static async Task<bool> RuntimeAlreadyMatchesAsync(UpdateRelease release, bool verifyInstalledBytes, CancellationToken cancel)
    {
        string installedFolder = DevReleaseFolder is not null ? DeploymentFootprint.InstallFolder : AppContext.BaseDirectory;
        if (DevReleaseFolder is null && !DeploymentFootprint.IsRunningFromInstallPath()) return false;
        if (!File.Exists(Path.Combine(installedFolder, InstallPayload.ManifestName))) return false;
        if (!release.HasAppOnlyPackage || string.IsNullOrWhiteSpace(release.AppOnlySha256Hex) || string.IsNullOrWhiteSpace(release.RuntimeManifestUrl))
            return false;

        try
        {
            if (verifyInstalledBytes)
                await Task.Run(() => InstallPayload.Verify(installedFolder), cancel).WaitAsync(cancel).ConfigureAwait(false);
            var installed = FreeVideoStudio.Core.Infrastructure.RuntimePayloadManifest.Read(installedFolder);
            if (installed is null)
            {
                RuntimeLog.Info("UPDATE",
                    "No local runtime manifest, so the installed runtime cannot be proven current; "
                  + "using the full installer.");
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(ProbeTimeout);
            string json = LocalDevSourcePath(release.RuntimeManifestUrl!) is { } localManifest
                ? await File.ReadAllTextAsync(localManifest, cts.Token).ConfigureAwait(false)
                : await Http.GetStringAsync(release.RuntimeManifestUrl!, cts.Token).ConfigureAwait(false);

            var advertised = FreeVideoStudio.Core.Infrastructure.RuntimePayloadManifest.FromJson(
                JsonNode.Parse(json)?.AsObject());
            if (advertised is null) return false;

            bool match = string.Equals(installed.Fingerprint, advertised.Fingerprint, StringComparison.Ordinal);

            RuntimeLog.Info("UPDATE", match
                ? $"Runtime already matches release {release.Tag} ({installed.Fingerprint}); downloading the "
                + $"app only ({release.DescribeDownloadSize(appOnly: true)} instead of "
                + $"{release.DescribeDownloadSize(appOnly: false)})."
                : $"Runtime differs from release {release.Tag} (local {installed.Fingerprint}, release "
                + $"{advertised.Fingerprint}); the full installer is required.");

            return match;
        }
        catch (OperationCanceledException)
        {
            cancel.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("UPDATE",
                $"Could not compare runtime fingerprints ({ex.GetType().Name}); using the full installer.");
            return false;
        }
    }

    /// <summary>D1 / SYS-PAYLOADSPLIT — the app-only package decision, with the installed bytes verified.</summary>
    internal static Task<bool> RuntimeMatchesForCompactAsync(UpdateRelease release, CancellationToken cancel)
        => RuntimeAlreadyMatchesAsync(release, verifyInstalledBytes: true, cancel);

    /// <summary>
    /// UPDATEUX_04 — "Restart &amp; update now". Closes the app through its NORMAL close path, so a
    /// project with unsaved work still asks first (05 SYS-UPGRADE: no editing process is killed).
    /// The waiting installer starts the moment the process exits. If the user chooses to keep
    /// working instead, they are told the update is still waiting.
    /// </summary>
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

    internal static string ExtractCompactInstaller(string archivePath, string folder)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != InstallPayload.ExecutableName ||
            archive.Entries[0].Length is < 1 or > 512L * 1024 * 1024 ||
            ((archive.Entries[0].ExternalAttributes >> 16) & 0xF000) == 0xA000)
            throw new IOException("The small update package has unexpected contents. Download the full installer.");
        string path = Path.Combine(folder, InstallPayload.ExecutableName);
        using var input = archive.Entries[0].Open();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
        return path;
    }

    /// <summary>UPDATEUX_03 — "43%  ·  140 of 322 MB  ·  1.0 MB/s  ·  about 4 min left".</summary>
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

    /// <summary>
    /// Removes download leftovers from previous updates. Best-effort: a folder still locked by a
    /// running installer is left for the next attempt.
    /// </summary>
    /// <summary>
    /// UPDATETRUST_01 — true only for an HTTPS URL whose host is one of
    /// <see cref="AllowedAssetHosts"/> (exact match or a subdomain of one).
    /// </summary>
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

    /// <summary>
    /// UPDATETRUST_01 — projects an untrusted release tag onto a filesystem-safe folder name, or
    /// REFUSES.
    ///
    /// <para><b>It rejects; it does not strip.</b> Silently removing the offending characters would
    /// map two different tags onto one folder, which is its own (quieter) correctness bug — and it
    /// is exactly the "validate a prefix, discard the rest" mistake in
    /// <c>DeploymentLifecycle.TryParseVersion</c> that let a traversal through in the first place.</para>
    ///
    /// <para>⚠️ The RAW tag stays authoritative everywhere else. <c>SkippedTagFile</c> persists and
    /// compares the raw string, so a sanitised value must never be written there or a release the
    /// user skipped would be offered again on the next start.</para>
    /// </summary>
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
