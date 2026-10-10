// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

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
/// * Downloads to %TEMP%\FVS_AutoUpdate\&lt;tag&gt;\, verifies the SHA-256 against GitHub's
///   published digest and REFUSES to run anything that does not match.
/// * Launches the downloaded installer, which waits for this app to close (--wait-pid), then the
///   user chooses "Restart &amp; update now" or "Update when I close the app" (UPDATEUX_04). The
///   installer shows its own progress window from the moment the app closes (UPGRADEUX_01).
/// </summary>
internal static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/alonreich/Free_Video_Studio/releases/latest";
    private const string ExpectedAssetName = "FreeVideoStudio.exe";

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
    private const string DownloadFolderRootName = "FVS_AutoUpdate";
    private const string LastCheckFile = "update_last_check_utc.txt";
    private const string SkippedTagFile = "update_skipped_tag.txt";

    /// <summary>UPDATEUX_06 — "when was the last check, and what did it find?" for Settings › About.</summary>
    private const string LastResultFile = "update_last_result.txt";

    /// <summary>UPDATEUX_02 — the "typical home connection" behind the time estimate (~20 Mbit/s).</summary>
    private const double TypicalBytesPerSecond = 2.5 * 1024 * 1024;

    private const string ReleasesPageUrl = "https://github.com/alonreich/Free_Video_Studio/releases/latest";

    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(2);
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
        string? PageUrl = null)
    {
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
        // The master switch. OFF means: no network call, no prompt, no nag — ever.
        if (Environment.GetCommandLineArgs().Contains("--upgrade-health")) return;
        if (!SettingsManager.Instance.AutoUpdateChecks)
        {
            RuntimeLog.Info("UPDATE", "Update checks are disabled in Settings; staying silent.");
            return;
        }

        // dev.cmd runs with FVS_DEV_LOG_DIR set; a developer's machine must never be offered
        // a GitHub release against its own local build. DEVUPDATE_01: when dev.cmd also names a
        // local source (.\compiled), the SAME flow runs against that folder instead.
        if (RuntimeLog.IsDevMode && DevReleaseFolder is null)
        {
            RuntimeLog.Info("UPDATE", "Dev mode detected; update check skipped.");
            return;
        }

        // Let the window settle first — the suggestor must never compete with startup work
        // or recovery prompts for the user's attention.
        try
        {
            await Task.Delay(StartupGracePeriod).ConfigureAwait(false);
        }
        catch (System.Exception swallowed4)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed4);   // FAULTTIER_02 — no failure is silent.
            return;
        }

        bool ownerStillVisible = await Dispatcher.UIThread.InvokeAsync(() => owner.IsVisible);
        if (!ownerStillVisible) return;

        // DEVUPDATE_01 — every dev launch checks .\compiled; a local folder needs no rate limit.
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

            if (!DeploymentLifecycle.TryParseVersion(release.Tag, out Version remote) ||
                !TryGetLocalVersion(out Version local))
            {
                RuntimeLog.Fail("UPDATE", $"Could not compare versions (running build vs tag '{release.Tag}'); no prompt shown.");
                RecordResult($"Could not read the version number of the latest release ('{release.Tag}').");
                return;
            }

            // STRICTLY newer only. Equal ("already have it") and older ("running a newer build")
            // are the two false positives this feature must never produce.
            if (remote.CompareTo(local) <= 0)
            {
                RuntimeLog.Info("UPDATE", $"Already up to date (installed {local}, latest {remote}).");
                RecordResult($"You have the latest version ({local}).");
                return;
            }

            RuntimeLog.Info("UPDATE", $"Newer version found: {remote} (installed {local}). Prompting user.");

            string skipped = UiStateStore.ReadText(SkippedTagFile).Trim();
            if (string.Equals(skipped, release.Tag, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Info("UPDATE", $"Release {release.Tag} was skipped by the user; staying quiet until a newer one appears.");
                RecordResult($"Version {remote} is available — you chose to skip it.");
                return;
            }

            RecordResult($"Version {remote} is available (you have {local}).");
            await AskAndActAsync(owner, release, local, statusCallback: null).ConfigureAwait(false);
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
                    NativeDialog.ShowError(problem + "\r\nPlease check your network connection and try again.", "Update Check Failed");
                });
                return;
            }

            if (!DeploymentLifecycle.TryParseVersion(release.Tag, out Version remote) ||
                !TryGetLocalVersion(out Version local))
            {
                RecordResult($"Could not read the version number of the latest release ('{release.Tag}').");
                statusCallback?.Invoke($"Could not compare versions ({release.Tag}).");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NativeDialog.ShowError($"Could not determine version compatibility (installed build vs release tag '{release.Tag}').", "Update Check Failed");
                });
                return;
            }

            if (remote.CompareTo(local) <= 0)
            {
                RecordResult($"You have the latest version ({local}).");
                statusCallback?.Invoke($"Up to date (v{local}).");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NativeDialog.ShowInfo($"You are already running the latest version of Free Video Studio (v{local}).");
                });
                return;
            }

            RecordResult($"Version {remote} is available (you have {local}).");
            statusCallback?.Invoke($"Update available: {release.Tag}. Preparing the details…");
            await AskAndActAsync(owner, release, local, statusCallback).ConfigureAwait(false);
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

    /// <summary>
    /// Shows the "new version" question with its size, time and self-install ability resolved
    /// first (UPDATEUX_02, UPDATEUX_05), then acts on the answer. Shared by both checks.
    /// </summary>
    private static async Task AskAndActAsync(Window owner, UpdateRelease release, Version local, Action<string>? statusCallback)
    {
        bool canInstallItself = AuthenticodeVerifier.CanVerifyUpdates(Environment.ProcessPath, out string trustDetail);
        string? sizeText = null;
        if (canInstallItself)
        {
            bool small = await RuntimeAlreadyMatchesAsync(release, verifyInstalledBytes: false, CancellationToken.None).ConfigureAwait(false);
            sizeText = DescribeDownload(small ? release.AppOnlySize : release.Size);
        }
        else
        {
            RuntimeLog.Info("UPDATE", "This copy cannot verify updates by itself; offering the download page instead. " + trustDetail);
        }

        // Same UI-thread marshalling pattern MainWindow uses (Post + completion source):
        // DispatcherOperation shapes differ per InvokeAsync overload, so we don't touch them.
        var choiceReady = new TaskCompletionSource<UpdateChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { choiceReady.SetResult(await UpdateAvailableWindow.AskAsync(owner, local, release.Tag, release.ReleaseNotes, sizeText, canInstallItself)); }
            catch (Exception ex)
            {
                choiceReady.SetException(ex);
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
            }
        });
        UpdateChoice choice = await choiceReady.Task.ConfigureAwait(false);

        switch (choice)
        {
            case UpdateChoice.SkipThisVersion:
                UiStateStore.WriteText(SkippedTagFile, release.Tag);
                RuntimeLog.Info("UPDATE", $"User skipped {release.Tag}. Only a strictly newer release will be offered again.");
                RecordResult($"Version {release.Tag.TrimStart('v', 'V')} is available — you chose to skip it.");
                statusCallback?.Invoke($"Skipped {release.Tag}");
                break;

            case UpdateChoice.NeverTellMeAgain:
                SettingsManager.SetAutoUpdateChecks(false);
                RuntimeLog.Info("UPDATE", "User chose 'never tell me about updates again' — Settings checkbox now reflects OFF.");
                statusCallback?.Invoke("Auto updates disabled in Settings.");
                break;

            case UpdateChoice.UpdateNow when !canInstallItself:
                OpenReleasePage(owner, release);
                statusCallback?.Invoke("Opened the download page in your browser.");
                break;

            case UpdateChoice.UpdateNow:
                statusCallback?.Invoke("Downloading the update…");
                await DownloadVerifyLaunchAsync(owner, release).ConfigureAwait(false);
                break;

            default:
                // NotNow ("Remind me later") / Dismissed: nothing is stored; a later start offers it again.
                statusCallback?.Invoke("Update postponed. You will be reminded next time the app starts.");
                break;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // DEVUPDATE_01 — THE DEV UPDATE SOURCE.
    //
    // dev.cmd sets FVS_DEV_UPDATE_SOURCE to the repository root. In dev mode (FVS_DEV_LOG_DIR also
    // set) the updater then treats .\compiled\FreeVideoStudio.exe — what dev_build.cmd produces —
    // as "the latest release": its version, size and SHA-256 stand in for the GitHub release JSON,
    // and everything after that is the production flow unchanged (question, download window, hash,
    // publisher check, Restart & update, installer window, first-launch notice).
    //
    // ⚠️ Both variables are required, and a file:// URL is accepted ONLY while they are. A release
    // JSON from GitHub can never name a local file: production keeps its pinned-host rule.
    // ══════════════════════════════════════════════════════════════════════════════════════════
    internal const string DevUpdateSourceVariable = "FVS_DEV_UPDATE_SOURCE";

    private static readonly string[] DevOnlyEnvironment =
    [
        "FVS_DEV_LOG_DIR", "FVS_PROGRAMDATA_ROOT", DevUpdateSourceVariable, "FVS_ALLOW_UNSIGNED_UPDATE"
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
    private static string? LocalDevSourcePath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !uri.IsFile) return null;
        if (DevReleaseFolder is null)
            throw new InvalidOperationException("A local update file is only accepted in the dev.cmd environment.");
        return uri.LocalPath;
    }

    /// <summary>
    /// DEVUPDATE_01 — reads .\compiled\FreeVideoStudio.exe as if it were the GitHub release. The
    /// SHA-256 is taken now, like GitHub's published digest; if the file is rebuilt before the
    /// download finishes, the check fails exactly as a tampered download would.
    /// </summary>
    internal static async Task<(UpdateRelease? Release, string Problem)> QueryLocalDevReleaseAsync(string repoRoot)
    {
        string exe = Path.Combine(repoRoot, "compiled", ExpectedAssetName);
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
                         + "This update comes from your own folder, not from GitHub. Everything after this question is the production flow.";
            RuntimeLog.Info("UPDATE", $"DEV update source: {exe} (version {version}, {RuntimePayloadManifest.FormatBytes(file.Length)}).");
            return (new UpdateRelease("v" + version, new Uri(exe).AbsoluteUri, sha256, file.Length, notes), string.Empty);
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

    /// <summary>UPDATEUX_05 — opens the release page for a copy that cannot install updates itself.</summary>
    private static void OpenReleasePage(Window owner, UpdateRelease release)
    {
        string url = !string.IsNullOrWhiteSpace(release.PageUrl) && Uri.TryCreate(release.PageUrl, UriKind.Absolute, out Uri? page)
                     && page.Scheme == Uri.UriSchemeHttps && string.Equals(page.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? release.PageUrl!
            : ReleasesPageUrl;
        try
        {
            _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            FloatingNotice.Info(owner, "The download page is opening in your browser.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RuntimeLog.Fail("UPDATE", "Could not open the release page: " + ex.Message);
            Dispatcher.UIThread.Post(() => NativeDialog.ShowError(
                "Your browser could not be opened. Please visit this page to download the new version:\r\n\r\n" + url, "Free Video Studio Update"));
        }
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
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed6);   // FAULTTIER_02 — no failure is silent.
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
            using var cts = new CancellationTokenSource(ProbeTimeout);
            using var response = await Http.GetAsync(LatestReleaseApiUrl, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Info("UPDATE", $"Release probe returned HTTP {(int)response.StatusCode}; staying silent.");
                return (null, $"GitHub did not answer the update check (HTTP {(int)response.StatusCode}).");
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var root = JsonNode.Parse(json)?.AsObject();

            string? tag = root?["tag_name"]?.GetValue<string>();
            string? releaseNotes = root?["body"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tag))
            {
                RuntimeLog.Fail("UPDATE", "Release JSON carried no tag_name; staying silent.");
                return (null, "The latest release on GitHub has no version number.");
            }

            // /releases/latest never returns drafts or prereleases; this is defense in depth.
            if (root?["draft"]?.GetValue<bool>() == true || root?["prerelease"]?.GetValue<bool>() == true)
            {
                RuntimeLog.Info("UPDATE", "Latest release is a draft/prerelease; staying silent.");
                return (null, "No finished release is published yet.");
            }

            JsonNode? asset = null;
            JsonNode? appOnlyAsset = null;
            string? runtimeManifestUrl = null;

            foreach (JsonNode? candidate in root?["assets"]?.AsArray() ?? [])
            {
                string? name = candidate?["name"]?.GetValue<string>();
                if (name is null) continue;

                if (name.Equals(ExpectedAssetName, StringComparison.OrdinalIgnoreCase))
                    asset = candidate;
                else if (name.Equals(AppOnlyAssetName, StringComparison.OrdinalIgnoreCase))
                    appOnlyAsset = candidate;          // SYS-PAYLOADSPLIT
                else if (name.Equals(RuntimeManifestAssetName, StringComparison.OrdinalIgnoreCase))
                    runtimeManifestUrl = candidate?["browser_download_url"]?.GetValue<string>();
            }

            string? url = asset?["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
            {
                RuntimeLog.Fail("UPDATE", $"Release {tag} carries no '{ExpectedAssetName}' asset; staying silent.");
                return (null, $"The latest release ({tag}) has no installer attached yet.");
            }

            // UPDATETRUST_01 — refuse an asset URL that is not HTTPS to a pinned GitHub host.
            if (!IsAllowedAssetUrl(url!))
            {
                RuntimeLog.Fail("UPDATE", $"Release {tag} points its asset at an unexpected location; refusing to download it.");
                return (null, $"The latest release ({tag}) points to an unexpected download location, so it was ignored for safety.");
            }

            // UPDATETRUST_01 — the tag becomes a directory name below. Reject it here, while we can
            // still stay silent, rather than at download time.
            if (!TrySanitizeTagForPath(tag!, out _))
            {
                RuntimeLog.Fail("UPDATE", $"Release tag '{tag}' is not usable as a folder name; staying silent.");
                return (null, $"The latest release has an unusable version name ('{tag}').");
            }

            // digest looks like "sha256:<hex>" — the publisher already trusts this exact value.
            string? digest = asset?["digest"]?.GetValue<string>();
            string? sha256 = null;
            if (!string.IsNullOrEmpty(digest))
            {
                int colon = digest.IndexOf(':');
                sha256 = colon >= 0 ? digest[(colon + 1)..].ToLowerInvariant() : null;
            }

            long size = asset?["size"]?.GetValue<long>() ?? 0;

            // SYS-PAYLOADSPLIT — the small package is optional. A release that does not publish one
            // behaves exactly as before, which is what makes this safe to ship ahead of the build
            // change that starts producing it.
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
                root?["html_url"]?.GetValue<string>()), string.Empty);
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("UPDATE", $"Release probe failed (offline or blocked?): {ex.Message}");
            return (null, "Could not reach GitHub to check for updates (no internet connection, or it is blocked).");
        }
    }

    /// <summary>Reads the current running version using DeploymentLifecycle and Win32 file version.</summary>
    private static bool TryGetLocalVersion(out Version version)
    {
        version = new Version(0, 0);
        try
        {
            // DEVUPDATE_01 — in dev the question is the production one: "is the build in .\compiled
            // newer than the copy INSTALLED on this machine?" The dev app itself is stamped by the
            // same dev_build.cmd run that made .\compiled, so comparing against it would always
            // say "up to date". Nothing installed yet counts as 0.0: everything is newer.
            if (DevReleaseFolder is not null)
            {
                string installed = DeploymentFootprint.InstallPath;
                if (!File.Exists(installed)) return true;
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(installed);
                return DeploymentLifecycle.TryParseVersion((info.ProductVersion ?? info.FileVersion ?? string.Empty).Split('+')[0], out version);
            }

            string current = DeploymentLifecycle.GetCurrentVersion();
            if (DeploymentLifecycle.TryParseVersion(current, out version))
            {
                return true;
            }

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return false;
            return DeploymentLifecycle.TryParseVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion, out version);
        }
        catch (System.Exception swallowed3)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);   // FAULTTIER_02 — no failure is silent.
            return false;
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
        if (!DeploymentFootprint.IsRunningFromInstallPath()) return false;
        if (!release.HasAppOnlyPackage || string.IsNullOrWhiteSpace(release.AppOnlySha256Hex) || string.IsNullOrWhiteSpace(release.RuntimeManifestUrl))
            return false;

        try
        {
            // UPDATEUX_03 — hashing every installed file takes seconds; it now runs while the
            // download window already says "Checking which parts of the update you need…".
            if (verifyInstalledBytes)
                await Task.Run(() => InstallPayload.Verify(AppContext.BaseDirectory), cancel).WaitAsync(cancel).ConfigureAwait(false);
            var installed = FreeVideoStudio.Core.Infrastructure.RuntimePayloadManifest.Read(
                AppContext.BaseDirectory);
            if (installed is null)
            {
                RuntimeLog.Info("UPDATE",
                    "No local runtime manifest, so the installed runtime cannot be proven current; "
                  + "using the full installer.");
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(ProbeTimeout);
            string json = await Http.GetStringAsync(release.RuntimeManifestUrl!, cts.Token).ConfigureAwait(false);

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
            // A cancel is not a fault (FAULTTIER_01) and is not a reason to pick the small package.
            // A USER cancel must still stop the update, so it is re-thrown to the download flow.
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

    private static async Task DownloadVerifyLaunchAsync(Window owner, UpdateRelease release)
    {
        PurgeOldDownloadFolders();

        // ══════════════════════════════════════════════════════════════════════════════════════
        // UPDATETRUST_01 — THE TAG IS UNTRUSTED INPUT AND IT IS ABOUT TO BECOME A DIRECTORY NAME.
        //
        // WHAT WAS WRONG: this was Path.Combine(GetTempPath(), DownloadFolderRootName, release.Tag)
        // with release.Tag straight out of the GitHub JSON. The only gate upstream is
        // DeploymentLifecycle.TryParseVersion, which does TrimStart('v','V') then
        // TakeWhile(IsDigit || '.') — it validates a PREFIX and silently discards the rest. So
        // "9.9.9\..\..\Microsoft\Windows\Start Menu\Programs\Startup" parses happily as 9.9.9 and
        // was then used verbatim as a folder name. Path.Combine does not reject "..", so the .exe
        // landed wherever the tag pointed — a persistence primitive, one JSON field wide.
        //
        // The fix rejects rather than strips (a stripped traversal silently collides with another
        // release's folder) and then ASSERTS containment on the resolved path, so even a sanitiser
        // bug cannot put a file outside the download root.
        // ══════════════════════════════════════════════════════════════════════════════════════
        if (!TrySanitizeTagForPath(release.Tag, out string tagFolderName))
        {
            RuntimeLog.Fail("UPDATE", $"Refusing to download release '{release.Tag}': the tag is not a usable folder name.");
            return;
        }

        string downloadRoot = Path.Combine(Path.GetTempPath(), DownloadFolderRootName);
        string folder = Path.Combine(downloadRoot, tagFolderName);

        string resolvedRoot = Path.GetFullPath(downloadRoot);
        string resolvedFolder = Path.GetFullPath(folder);
        if (!resolvedFolder.StartsWith(
                resolvedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            RuntimeLog.Fail("UPDATE", "Refusing to download: the resolved update folder escapes the download root.");
            return;
        }

        folder = resolvedFolder;
        folder = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        bool appOnly = false;
        string finalPath = Path.Combine(folder, ExpectedAssetName);
        string executablePath = finalPath;
        string partPath = finalPath + ".part";
        Directory.CreateDirectory(folder);

        using var cts = new CancellationTokenSource();
        var vm = new UpdateDownloadViewModel();
        var readyChoice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.CancelRequested += () =>
        {
            vm.Stage("Cancelling…", "Stopping the update. Your current version is not changed.", null, canCancel: false);
            cts.Cancel();
        };
        vm.ReadyChoice += restartNow => readyChoice.TrySetResult(restartNow);
        UpdateDownloadWindow? progressWindow = null;
        Task dialogTask = Task.CompletedTask;
        var dialogShown = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                progressWindow = new UpdateDownloadWindow { DataContext = vm };
                dialogTask = progressWindow.ShowDialog(owner);
                dialogShown.SetResult(null);
            }
            catch (Exception ex)
            {
                dialogShown.SetException(ex);
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
            }
        });
        await dialogShown.Task.ConfigureAwait(false);

        bool handedOff = false;
        try
        {
            // UPDATEUX_03 — the window is already open and says what is happening while the
            // installed files are hashed. This used to run BEFORE the window appeared: seconds of
            // nothing after the user clicked "Yes".
            // SYS-PAYLOADSPLIT — resolved here, once, before anything is fetched, so the size the user
            // is told about below is the size that is actually downloaded.
            appOnly = await RuntimeAlreadyMatchesAsync(release, verifyInstalledBytes: true, cts.Token).ConfigureAwait(false);
            if (appOnly)
            {
                RuntimeLog.Info("UPDATE",
                    $"Update {release.Tag}: app-only package selected "
                  + $"({release.DescribeDownloadSize(true)} rather than {release.DescribeDownloadSize(false)}).");
            }
            finalPath = Path.Combine(folder, appOnly ? AppOnlyAssetName : ExpectedAssetName);
            executablePath = finalPath;
            partPath = finalPath + ".part";
            string downloadUrl = appOnly ? release.AppOnlyUrl! : release.DownloadUrl;
            string? expectedHash = appOnly ? release.AppOnlySha256Hex : release.Sha256Hex;
            string sizeText = release.DescribeDownloadSize(appOnly);
            string? localSource = LocalDevSourcePath(downloadUrl);
            Dispatcher.UIThread.Post(() => vm.Stage("Downloading the new version…",
                localSource != null ? $"DEV: copying from .\\compiled ({sizeText})…" : $"Connecting to GitHub… ({sizeText} to download)",
                0, canCancel: true));

            // DEVUPDATE_01 — the dev source is read from disk with the same loop, the same progress,
            // the same hash check and the same publisher check as a GitHub download.
            using HttpResponseMessage? response = localSource != null ? null
                : await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response?.EnsureSuccessStatusCode();
            long total = localSource != null ? new FileInfo(localSource).Length
                : response!.Content.Headers.ContentLength ?? (appOnly ? release.AppOnlySize : release.Size);

            await using Stream source = localSource != null
                ? new FileStream(localSource, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true)
                : await response!.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            byte[] buffer = new byte[1024 * 1024];
            long copied = 0;
            DateTime lastReport = DateTime.MinValue;
            var meter = new DownloadMeter();

            // Network guard: 45-second per-chunk read stall timeout to prevent hanging indefinitely
            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(45));

                int read;
                try
                {
                    read = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    // UPDATEUX_03 — a stall used to look exactly like the user pressing Cancel: the
                    // window vanished and nothing was said. It is a failure, and it is reported.
                    throw new IOException("The download stopped receiving data for 45 seconds. Please check your internet connection and try again.");
                }
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                copied += read;
                meter.Add(copied);
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 250)
                {
                    lastReport = DateTime.UtcNow;
                    ReportDownloadProgress(vm, copied, total, meter.BytesPerSecond);
                }
            }
            await target.FlushAsync(cts.Token).ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);
            ReportDownloadProgress(vm, copied, total > 0 ? total : copied, meter.BytesPerSecond);

            // UPDATEUX_03 — the safety check takes seconds on a large file. It used to sit at "100%"
            // with an enabled Cancel button that could not act. It is a named stage now.
            Dispatcher.UIThread.Post(() => vm.Stage("Checking the download is safe…",
                "Making sure the file arrived complete, unchanged and signed by the publisher. This takes a few seconds.",
                null, canCancel: false));

            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                throw new InvalidOperationException("The release has no published fingerprint, so the download cannot be verified. Nothing was installed.");
            }

            string actualHash;
            await using (FileStream verifyStream = File.OpenRead(partPath))
            {
                actualHash = Convert.ToHexString(SHA256.HashData(verifyStream)).ToLowerInvariant();
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The downloaded file does not match the fingerprint the publisher uploaded. The file was deleted and nothing was installed.");
            }

            File.Move(partPath, finalPath, overwrite: true);

            // ══════════════════════════════════════════════════════════════════════════════════
            // UPDATETRUST_01 — RE-HASH THE PATH WE ARE ACTUALLY GOING TO EXECUTE.
            //
            // The hash above was computed over partPath; the file then got RENAMED and a DIFFERENT
            // path is launched below. That rename window was a time-of-check/time-of-use gap: the
            // bytes that were verified and the bytes that run were never proven to be the same
            // bytes. Re-hashing finalPath costs one sequential read of a file already in the page
            // cache and closes the gap completely.
            // ══════════════════════════════════════════════════════════════════════════════════
            string finalHash;
            await using (FileStream finalStream = File.OpenRead(finalPath))
            {
                finalHash = Convert.ToHexString(SHA256.HashData(finalStream)).ToLowerInvariant();
            }

            if (!string.Equals(finalHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The installer changed on disk after it was verified. Nothing was installed.");
            }

            // ══════════════════════════════════════════════════════════════════════════════════
            // UPDATETRUST_01 — PROVE THE PUBLISHER, NOT JUST THE BYTES.
            //
            // The SHA-256 above and the URL it validates come out of the SAME JSON document. That
            // pair proves transport integrity and nothing more: whoever can produce that response
            // controls both halves at once. This is the only check that asks "did WE sign this?",
            // and it runs on the exact path about to be executed with elevation.
            //
            // See AuthenticodeVerifier for why the anchor is the running process rather than a
            // hardcoded thumbprint, and why an unsigned running build degrades to hash-only
            // LOUDLY instead of failing closed.
            // ══════════════════════════════════════════════════════════════════════════════════
            if (appOnly)
                executablePath = ExtractCompactInstaller(finalPath, folder);
            var verdict = AuthenticodeVerifier.EvaluateUpdateCandidate(
                executablePath, Environment.ProcessPath, out string trustDetail);

            switch (verdict)
            {
                case AuthenticodeVerifier.TrustVerdict.Rejected:
                    TryDeleteFile(finalPath);
                    throw new InvalidOperationException(
                        "The downloaded installer failed its signature check and was deleted. Nothing was installed." +
                        Environment.NewLine + trustDetail);

                // ══════════════════════════════════════════════════════════════════════════
                // UPDATETRUST_02 — NoAnchor IS A REFUSAL, NOT A WARNING.
                //
                // This branch used to log one line and fall through to Process.Start with
                // --install --auto-update, i.e. it executed the downloaded binary elevated on the
                // strength of a SHA-256 read out of the same GitHub JSON document that supplied
                // the URL. That hash proves transport integrity and nothing else. Anyone able to
                // produce that response body — a compromised repo or CI token, a TLS-terminating
                // proxy, a mis-issued certificate — controls the payload AND the fingerprint that
                // validates it, in one move.
                //
                // AuthenticodeVerifier's own class comment names this as the attack it exists to
                // close. Keeping a fall-through for unsigned builds meant it was never closed in
                // production, because production WAS the unsigned build (SIGNMANDATE_01).
                //
                // Refusing costs the one thing a warning was protecting: in-app auto-update for
                // installs that are themselves unsigned. That is a real regression and it is the
                // correct trade — the user is told exactly what happened and sent to the release
                // page to install the signed build by hand, ONCE. From then on they have an
                // anchor and auto-update works normally and verifiably.
                //
                // FVS_ALLOW_UNSIGNED_UPDATE=1 restores the old behaviour for developers testing
                // the update path against unsigned local builds. It is read from the environment
                // on purpose: it cannot be set by a downloaded payload, a settings file or a
                // server response, so nothing an attacker controls can re-open this door.
                // ══════════════════════════════════════════════════════════════════════════
                case AuthenticodeVerifier.TrustVerdict.NoAnchor:
                    if (!string.Equals(Environment.GetEnvironmentVariable("FVS_ALLOW_UNSIGNED_UPDATE"), "1", StringComparison.Ordinal))
                    {
                        TryDeleteFile(finalPath);
                        RuntimeLog.Fail("UPDATE",
                            "REFUSED — " + trustDetail +
                            " The installer was deleted rather than executed with elevation on an unverifiable fingerprint (UPDATETRUST_02).");
                        throw new InvalidOperationException(
                            "This build is not digitally signed, so the downloaded update could not be checked against a publisher." +
                            Environment.NewLine + Environment.NewLine +
                            "Nothing was installed and the download was deleted. To update safely, download the latest release " +
                            "manually from the project's releases page and run it once — after that, updates will verify and " +
                            "install automatically.");
                    }

                    RuntimeLog.Fail("UPDATE",
                        "SIGNATURE CHECK SKIPPED — " + trustDetail +
                        " FVS_ALLOW_UNSIGNED_UPDATE=1 is set, so the update was accepted on its published fingerprint alone. " +
                        "DEVELOPER OVERRIDE — never set this on an end-user machine.");
                    break;

                default:
                    RuntimeLog.Info("UPDATE", "Publisher check passed: " + trustDetail);
                    break;
            }

            // Honour a Cancel clicked during verification/handoff — never install past a cancel.
            cts.Token.ThrowIfCancellationRequested();

            RuntimeLog.Info("UPDATE", $"Download of {release.Tag} verified (sha256 {finalHash[..12]}…). Handing off to installer with --auto-update.");

            // The installer waits (--wait-pid) until this app closes, then shows its own progress
            // window (UPGRADEUX_01). Windows will still show its own UAC consent once — that is OS
            // security and cannot (and should not) be bypassed by any app.
            var installer = new ProcessStartInfo(executablePath) { UseShellExecute = DevReleaseFolder is null };
            if (DevReleaseFolder is not null)
            {
                // DEVUPDATE_01 — the installer must behave exactly like production: install to
                // Program Files, keep the REAL settings, start the installed app normally. So none
                // of dev.cmd's sandbox variables may leak into it or into the app it starts.
                foreach (string name in DevOnlyEnvironment) installer.Environment.Remove(name);
            }
            installer.ArgumentList.Add("--install");
            installer.ArgumentList.Add("--auto-update");
            installer.ArgumentList.Add("--source-current");
            installer.ArgumentList.Add("--wait-pid");
            installer.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            _ = Process.Start(installer) ?? throw new IOException("Could not start the update.");
            handedOff = true;

            // UPDATEUX_04 — a choice, not "close the app yourself when you are done".
            Dispatcher.UIThread.Post(vm.Ready);
            bool restartNow = await readyChoice.Task.ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => CloseProgressWindow(progressWindow));
            if (restartNow)
            {
                RuntimeLog.Info("UPDATE", "User chose Restart & update now; closing the app normally (unsaved work is asked about first).");
                Dispatcher.UIThread.Post(() => RestartToUpdate(owner));
            }
            else
            {
                RuntimeLog.Info("UPDATE", "User chose to update when the app closes; the installer is waiting.");
                FloatingNotice.Info(owner, "The update will install by itself when you close Free Video Studio.");
            }
        }
        catch (Exception ex) when (handedOff)
        {
            // The installer is already running and waiting for this app to close; the update
            // will still happen. Nothing here may delete the file it is running from.
            RuntimeLog.Fail("UPDATE", $"After hand-off to the installer: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info("UPDATE", "Update download cancelled by the user; current install untouched.");
            FloatingNotice.Info(owner, "Update cancelled — nothing was changed. The app will offer it again on a later start.");
            TryDeleteFile(partPath);
            // UPDATETRUST_01 — a cancel after the rename must not leave a runnable installer behind.
            TryDeleteFile(finalPath);
            if (executablePath != finalPath) TryDeleteFile(executablePath);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Update download/verify failed: {ex.Message}");
            TryDeleteFile(partPath);
            // UPDATETRUST_01 — every rejection path removes the artifact, including one rejected
            // AFTER the rename (bad re-hash, failed signature check).
            TryDeleteFile(finalPath);
            if (executablePath != finalPath) TryDeleteFile(executablePath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                NativeDialog.ShowError(
                    "The update could not be downloaded." + Environment.NewLine + Environment.NewLine +
                    $"Reason: {ex.Message}" + Environment.NewLine + Environment.NewLine +
                    "Your current version was not changed. The app will offer the update again on a later start.",
                    "Update Failed");
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() => CloseProgressWindow(progressWindow));
        }

        await dialogTask.ConfigureAwait(false);
    }

    private static void CloseProgressWindow(UpdateDownloadWindow? window)
    {
        if (window is null) return;
        try
        {
            window.ClosingByUpdater = true;
            if (window.IsVisible) window.Close();
        }
        catch (System.Exception swallowed5)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed5);   // FAULTTIER_02 — no failure is silent.
        }
    }

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

        // While the "save your work?" question is open the main window is not active; once the user
        // answers "keep working" it becomes active again and is still visible.
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

    /// <summary>UPDATEUX_03 — download speed averaged over the last few seconds, for "time left".</summary>
    private sealed class DownloadMeter
    {
        private readonly Queue<(DateTime At, long Bytes)> _samples = new();

        public double BytesPerSecond { get; private set; }

        public void Add(long totalBytes)
        {
            DateTime now = DateTime.UtcNow;
            _samples.Enqueue((now, totalBytes));
            while (_samples.Count > 2 && now - _samples.Peek().At > TimeSpan.FromSeconds(5)) _samples.Dequeue();
            (DateTime firstAt, long firstBytes) = _samples.Peek();
            double seconds = (now - firstAt).TotalSeconds;
            if (seconds >= 0.5) BytesPerSecond = (totalBytes - firstBytes) / seconds;
        }
    }

    internal static string ExtractCompactInstaller(string archivePath, string folder)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != ExpectedAssetName ||
            archive.Entries[0].Length is < 1 or > 512L * 1024 * 1024 ||
            ((archive.Entries[0].ExternalAttributes >> 16) & 0xF000) == 0xA000)
            throw new IOException("The small update package has unexpected contents. Download the full installer.");
        string path = Path.Combine(folder, ExpectedAssetName);
        using var input = archive.Entries[0].Open();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
        return path;
    }

    /// <summary>UPDATEUX_03 — "43%  ·  140 of 322 MB  ·  3.1 MB/s  ·  about 1 min left".</summary>
    private static void ReportDownloadProgress(UpdateDownloadViewModel vm, long copied, long total, double bytesPerSecond)
    {
        double? fraction = total > 0 ? Math.Clamp((double)copied / total, 0, 1) : null;
        string text = DescribeDownloadProgress(copied, total, bytesPerSecond);
        Dispatcher.UIThread.Post(() => vm.Stage("Downloading the new version…", text, fraction, canCancel: true));
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

        // Leading/trailing dots and any ".." run are traversal or Windows-illegal names.
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

        // Reserved Windows device names, with or without an extension.
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

    private static void PurgeOldDownloadFolders()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), DownloadFolderRootName);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("UPDATE", $"Could not purge old update downloads: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
        }
    }
}
