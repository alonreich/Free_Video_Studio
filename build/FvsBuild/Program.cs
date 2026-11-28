using System.Globalization;

namespace FvsBuild;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBuildFailed = 1;
    private const int ExitPublishIncomplete = 2;

    private static int Main(string[] args)
    {
        if (args.Contains("--dev-runtime") && args.Length != 1)
        {
            Console.Error.WriteLine("--dev-runtime must be used alone; no release pipeline was started.");
            return ExitBuildFailed;
        }
        bool devMode = args.Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
        bool stageOnly = args.Any(a => a.Equals("--stage-only", StringComparison.OrdinalIgnoreCase));
        bool protectRootKey = args.Any(a => a.Equals("--protect-root-key", StringComparison.OrdinalIgnoreCase));
        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("build.cmd      official release: release gate, build, publish to GitHub as latest.");
            Console.WriteLine("               An unfinished earlier release (tag pushed, release missing) is finished first (RELRESUME_02).");
            Console.WriteLine("dev_build.cmd  local build (--dev): sentinels + tests, NativeAOT, installer; git and GitHub untouched.");
            Console.WriteLine("Neither command takes arguments (ONECMD_01). Internal: --stage-only (CI), --dev-runtime (dev.cmd),");
            Console.WriteLine("--protect-root-key (ROOTKEY_01 maintenance).");
            return ExitOk;
        }

        Environment.CurrentDirectory = FindRepoRoot();
        if (args.Length == 1 && args[0] == "--dev-runtime")
        {
            string destination = Path.GetFullPath(".dev-run/app");
            Directory.CreateDirectory(destination);
            using BuildLog runtimeLog = new(Path.GetFullPath(".dev-run/staging.log"));
            if (!Staging.StageDependencies(runtimeLog, destination)) return ExitBuildFailed;
            FreeVideoStudio.Core.Infrastructure.RuntimePayloadManifest.FromFolder(destination).Write(destination);
            return ExitOk;
        }
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

        DateTimeOffset started = DateTimeOffset.Now;
        using BuildLog log = new("build.log");
        if (protectRootKey)
        {
            int protectedExit = CodeSigning.ProtectRootKey(log);
            log.WriteVerdictAndClose(protectedExit, DateTimeOffset.Now - started);
            return protectedExit;
        }
        bool publish = !devMode && !stageOnly;
        if (publish && ReleaseGate.HasUnfinishedRelease(log))
        {
            return RunResume(log, beta: false, assumeYes: true, started);
        }
        int exitCode = RunPipeline(log, publish, devMode, stageOnly, beta: false, assumeYes: true, started);
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
        string buildVersion = devMode ? ReleaseGate.NewDevVersion(DateTime.UtcNow) : ReleaseGate.NewVersion(DateTime.UtcNow);
        string tag = "v" + buildVersion;
        string flavor = devMode ? "Local Dev" : "NativeAOT win-x64";
        SmartUpdateBuilder.IsDevBuild = devMode;
        CodeSigning.DevBuild = devMode || stageOnly;
        log.Info($"Build version: {buildVersion}  (tag {tag})");

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
            if (devMode && !stageOnly)
            {
                log.Info($"[VERSION] Dev build {buildVersion}: ranks below every release, so it is never mistaken for one (DEVVERSION_01).");
                var problems = new List<string>();
                ReleaseGate.RunLocalChecks(problems, log, requireCleanTree: false);
                if (problems.Count > 0)
                {
                    log.Blocked("DEV BUILD BLOCKED - nothing was built", problems);
                    log.WriteVerdictAndClose(ExitBuildFailed, DateTimeOffset.Now - started);
                    return ExitBuildFailed;
                }
            }
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
                    log.Info("[PUBLISH] Skipped: not an official release. GitHub was not touched.");
                }
                log.WriteVerdictAndClose(ExitOk, DateTimeOffset.Now - started);
                return ExitOk;
            }

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
                if (!plan.Beta) ReleaseGate.RecordReleaseNotes(plan, log);
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
            log.Warn("docs/CHANGELOG.md still has its notes under '## Upcoming'. Run build.cmd again: it finishes THIS release first (RELRESUME_02).");
            log.Warn(".\\compiled\\FreeVideoStudio.exe is good and usable.");
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

    private static int RunResume(BuildLog log, bool beta, bool assumeYes, DateTimeOffset started)
    {
        ReleaseGate.ResumePlan? resume = ReleaseGate.PreflightResume(beta, assumeYes, log);
        if (resume is null)
        {
            log.WriteVerdictAndClose(ExitBuildFailed, DateTimeOffset.Now - started);
            return ExitBuildFailed;
        }
        ReleasePlan plan = resume.Plan;
        if (resume.State != ReleaseGate.ResumeState.Published)
        {
            log.Banner(plan.Beta ? "PUBLISHING BETA TO GITHUB (RESUMED)..." : "PUBLISHING RELEASE TO GITHUB (RESUMED)...");
            if (!GitHubReleasePublisher.Publish(Staging.CompiledExePath, plan, log))
            {
                log.Warn($"The release {plan.Tag} is still not published (reason above). Fix it and run build.cmd again.");
                log.WriteVerdictAndClose(ExitPublishIncomplete, DateTimeOffset.Now - started);
                return ExitPublishIncomplete;
            }
            SmartUpdateBuilder.MarkReleased(plan.Version, log);
        }
        if (!plan.Beta && !resume.NotesAlreadyPromoted) ReleaseGate.RecordReleaseNotes(plan, log);
        if (resume.State != ReleaseGate.ResumeState.Published)
            ReleaseChecklist.PrepareAndOpen(plan.Version, plan.Tag, Staging.CompiledExePath, log);
        log.WriteVerdictAndClose(ExitOk, DateTimeOffset.Now - started);
        return ExitOk;
    }

    private static int FailRelease(ReleasePlan? plan, BuildLog log, DateTimeOffset started)
    {
        if (plan is not null) ReleaseGate.Undo(plan, log);
        return FailBuild(log, started);
    }

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
        if (!CodeSigning.SignDependenciesWhenPubliclyTrusted(Staging.StagingDirPath, Staging.StagedProgramPath, log)) return false;
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
