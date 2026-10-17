using System.Globalization;

namespace FvsBuild;

/// <summary>
/// FvsBuild - structured replacement for the old monolithic Build.cmd.
/// Pipeline: [release gate: preflight -> version commit + tag (RELGATE_01)] -> stop this repository's
/// processes (BUILDKILL_01) -> clean -> detect MSVC toolchain -> NativeAOT publish to staging -> PE identity
/// (PEIDENTITY_01) -> sign the program -> stage backend/frontend/starter files -> manifests -> smart-update
/// assets + signed file list -> Inno Setup installer into .\compiled (NODROPPER_01) -> sign it -> release
/// update.json -> validate exactly one file -> cleanup -> [push commit + tag -> GitHub release from that tag
/// -> promote the release notes (RELNOTES_01)].
/// Exit codes (consumed by CI and by Build.cmd): 0 = OK, 1 = BUILD FAILED (or the release gate refused),
/// 2 = build OK but the GitHub publish did not complete.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBuildFailed = 1;
    private const int ExitPublishIncomplete = 2;

    private static int Main(string[] args)
    {
        bool noPublish = args.Any(a => a.Equals("--no-publish", StringComparison.OrdinalIgnoreCase));
        bool devMode = args.Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
        bool stageOnly = args.Any(a => a.Equals("--stage-only", StringComparison.OrdinalIgnoreCase));
        bool beta = args.Any(a => a.Equals("--beta", StringComparison.OrdinalIgnoreCase));
        bool assumeYes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Usage: FvsBuild [--no-publish] [--dev] [--beta] [--yes] [--stage-only]");
            Console.WriteLine("  (default)     official STABLE release: release gate, build, publish to GitHub as latest");
            Console.WriteLine("  --beta        official BETA release: published as a pre-release (Beta update channel only)");
            Console.WriteLine("  --yes         skip the 'type PUBLISH' confirmation (required when there is no terminal)");
            Console.WriteLine("  --no-publish  build only; GitHub and the repository's version files are not touched");
            Console.WriteLine("  --dev         local dev build: no GitHub, and .\\compiled is not wiped first");
            Console.WriteLine("  --stage-only  diagnostic: publish, stage dependencies and write the manifests, then stop.");
            Console.WriteLine("                Leaves the staging folder in place for inspection.");
            Console.WriteLine("                GitHub and .\\compiled are untouched.");
            return ExitOk;
        }

        Environment.CurrentDirectory = FindRepoRoot();
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

        DateTimeOffset started = DateTimeOffset.Now;
        using BuildLog log = new("build.log");
        bool publish = !noPublish && !devMode && !stageOnly;
        int exitCode = RunPipeline(log, publish, devMode, stageOnly, beta, assumeYes, started);
        return exitCode;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FreeVideoStudio.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return Environment.CurrentDirectory;
    }

    private static int RunPipeline(BuildLog log, bool publish, bool devMode, bool stageOnly, bool beta, bool assumeYes, DateTimeOffset started)
    {
        string buildVersion = ReleaseGate.NewVersion(DateTime.UtcNow);
        string tag = "v" + buildVersion;
        string flavor = devMode ? "Local Dev" : "NativeAOT win-x64";
        SmartUpdateBuilder.IsDevBuild = devMode;
        log.Info($"Build version: {buildVersion}  (tag {tag})");

        // RELGATE_01 — an official release is checked and committed BEFORE anything is built, so the
        // binary is compiled from the exact commit that gets tagged. Every other mode leaves the
        // repository's tracked files alone (the version reaches the compiler through -p:Version).
        ReleasePlan? plan = null;
        if (publish)
        {
            plan = ReleaseGate.Preflight(buildVersion, beta, assumeYes, log);
            if (plan is null)
            {
                log.WriteVerdictAndClose(ExitBuildFailed, DateTimeOffset.Now - started);
                return ExitBuildFailed;
            }
            if (!ReleaseGate.CommitVersion(plan, log)) return FailBuild(log, started);
        }
        else
        {
            log.Info("[VERSION] Not an official release: the repository's version files are left unchanged.");
        }

        try
        {
            if (stageOnly)
            {
                log.Banner("###########################################################");
                log.Banner("STAGE-ONLY: staging + manifests, then stop. .\\compiled is untouched.");
                log.Banner("###########################################################");
                Staging.PurgeTempArtifacts(log);
                bool staged = Staging.Publish(Staging.StagingDirPath, buildVersion, log, "Publishing raw payload to staging...")
                              && Staging.StageDependencies(log)
                              && Staging.WriteManifests(log);
                if (!staged)
                {
                    return FailBuild(log, started);
                }
                log.Banner("###########################################################");
                log.Success($"SUCCESS: staging diagnostic build completed: {Staging.StagingDirPath}");
                log.Info("Staging folder kept for inspection: .\\obj\\StandaloneTemp\\Staging");
                log.Banner("###########################################################");
                log.WriteVerdictAndClose(ExitOk, DateTimeOffset.Now - started);
                return ExitOk;
            }

            log.Banner("###########################################################");
            log.Banner(devMode ? "PREPARING LOCAL BUILD ENVIRONMENT..." : "PURGING PREVIOUS BUILD ARTIFACTS...");
            log.Banner("###########################################################");
            Staging.TerminateProcesses(log);
            Staging.PrepareCompiledDir(log, devMode);
            Staging.CleanAll(log);

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Banner("CHECKING NATIVE AOT TOOLCHAIN...");
            log.Banner("###########################################################");
            if (!Cli.EnsureMsvcToolchain(log))
            {
                return FailRelease(plan, log, started);
            }

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Banner($"BUILDING Free Video Studio: {flavor}");
            log.Banner("###########################################################");
            if (!RunNativeAotStages(log, buildVersion))
            {
                return FailRelease(plan, log, started);
            }

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Success(devMode
                ? "SUCCESS: Local dev build completed successfully."
                : "SUCCESS: Build completed successfully.");
            log.Banner(string.Empty);
            log.Info($"Native EXE: {Staging.CompiledExePath}");
            log.Info("Log file:  .\\build.log  (first line: OK / WARN / FAIL)");
            log.Banner("###########################################################");

            if (plan is null)
            {
                if (!devMode)
                {
                    log.Info(string.Empty);
                    log.Info("[PUBLISH] Skipped on request (--no-publish). GitHub was not touched.");
                }
                log.WriteVerdictAndClose(ExitOk, DateTimeOffset.Now - started);
                return ExitOk;
            }

            // SIGNLOCAL_01 superseded by SIGNLOCAL_02: disclose the local publisher certificate before publishing.
            if (CodeSigning.SignedWithLocalDevCertificate)
            {
                log.Banner(string.Empty);
                log.Warn("[PUBLISH] Signed with the repository publisher certificate (SIGNLOCAL_02, publishing by user decision).");
                log.Warn("[PUBLISH] In-app updates verify against the built-in publisher root and install for everyone");
                log.Warn("[PUBLISH] (UPDATEPIN_01). New downloaders still see SmartScreen's 'unknown publisher' once.");
            }

            if (!ReleaseGate.TreeStillClean(log)) return FailRelease(plan, log, started);

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Banner(plan.Beta ? "PUBLISHING BETA TO GITHUB..." : "PUBLISHING RELEASE TO GITHUB...");
            log.Banner("###########################################################");
            if (!ReleaseGate.PushRelease(plan, log))
            {
                ReleaseGate.Undo(plan, log);
                log.Warn("BUILD OK - but nothing was published: the release commit and tag could not be pushed.");
                log.WriteVerdictAndClose(ExitPublishIncomplete, DateTimeOffset.Now - started);
                return ExitPublishIncomplete;
            }
            bool published = GitHubReleasePublisher.Publish(Staging.CompiledExePath, plan, log);
            if (published)
            {
                SmartUpdateBuilder.MarkReleased(buildVersion, log);
                // RELNOTES_01: the staged notes become the release's notes in the repository only now.
                if (!plan.Beta) ReleaseGate.RecordReleaseNotes(plan, log);
                // NEWRELEASE_01: prepare the checklist only after a successful public release.
                ReleaseChecklist.PrepareAndOpen(buildVersion, tag, Staging.CompiledExePath, log);
            }
            if (published)
            {
                log.WriteVerdictAndClose(ExitOk, DateTimeOffset.Now - started);
                return ExitOk;
            }
            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Warn("BUILD OK - but the release was NOT published.");
            log.Warn($"The commit and tag {tag} ARE on GitHub; only the release itself is missing (reason above).");
            log.Warn("docs/CHANGELOG.md still has its notes under '## Upcoming', so the next release carries them.");
            log.Warn(".\\compiled\\FreeVideoStudio-Setup.exe is good and usable.");
            log.Banner("###########################################################");
            log.WriteVerdictAndClose(ExitPublishIncomplete, DateTimeOffset.Now - started);
            return ExitPublishIncomplete;
        }
        catch (Exception ex)
        {
            log.Error($"ERROR: unhandled build failure: {ex}");
            return FailRelease(plan, log, started);
        }
    }

    /// <summary>RELGATE_01 — a failed release build takes its version commit and tag back with it.</summary>
    private static int FailRelease(ReleasePlan? plan, BuildLog log, DateTimeOffset started)
    {
        if (plan is not null) ReleaseGate.Undo(plan, log);
        return FailBuild(log, started);
    }

    /// <summary>
    /// The :BUILD_NATIVE ladder. NODROPPER_01: ONE NativeAOT publish (the program); the release is
    /// packed by Inno Setup, not embedded in a second program.
    /// </summary>
    private static bool RunNativeAotStages(BuildLog log, string buildVersion)
    {
        Staging.PurgeTempArtifacts(log);

        if (!Staging.Publish(Staging.StagingDirPath, buildVersion, log, "2. Publishing the program to staging..."))
        {
            return false;
        }
        if (!Staging.StageDependencies(log))
        {
            return false;
        }
        if (!PeIdentity.FixOriginalFilename(Staging.StagedProgramPath, log)) return false;
        if (!CodeSigning.SignIfNeeded(Staging.StagedProgramPath, log)) return false;
        if (!Staging.WriteManifests(log))
        {
            return false;
        }
        if (!SmartUpdateBuilder.Prepare(buildVersion, Staging.StagingDirPath, Staging.InstallerListDirPath, log)) return false;
        if (!InnoInstaller.Build(buildVersion, Staging.StagingDirPath, Staging.InstallerListDirPath, Staging.CompiledDirPath, log))
        {
            return false;
        }
        if (!CodeSigning.SignIfNeeded(Staging.CompiledExePath, log))
        {
            return false;
        }
        if (!SmartUpdateBuilder.Finish(buildVersion, Staging.CompiledExePath, log)) return false;
        if (!Staging.ValidateCompiledOutput(log))
        {
            return false;
        }
        Staging.CleanupTempArtifacts(log);
        return true;
    }

    private static int FailBuild(BuildLog log, DateTimeOffset started)
    {
        log.Banner(string.Empty);
        log.Banner("###########################################################");
        log.Error("BUILD FAILED - nothing was published.");
        log.Error("The existing GitHub release was NOT touched.");
        log.Error("Scroll up for the first ERROR line, or read .\\build.log");
        log.Banner("###########################################################");
        log.WriteVerdictAndClose(ExitBuildFailed, DateTimeOffset.Now - started);
        return ExitBuildFailed;
    }
}
