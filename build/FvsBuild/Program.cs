using System.Globalization;

namespace FvsBuild;

/// <summary>
/// FvsBuild - structured replacement for the old monolithic Build.cmd.
/// Pipeline: terminate processes -> clean -> detect MSVC toolchain -> NativeAOT publish
/// to staging -> PE identity (PEIDENTITY_01) -> sign the program -> stage backend/frontend/starter
/// files -> manifests -> smart-update assets + signed file list -> Inno Setup installer into
/// .\compiled (NODROPPER_01) -> sign it -> release update.json -> validate exactly one file ->
/// cleanup -> optional GitHub release replacement.
/// Exit codes (consumed by CI and by Build.cmd): 0 = OK, 1 = BUILD FAILED, 2 = build OK
/// but the GitHub publish did not complete.
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
        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Usage: FvsBuild [--no-publish] [--dev]");
            Console.WriteLine("  (default)   build .\\compiled\\FreeVideoStudio-Setup.exe, then replace the GitHub release");
            Console.WriteLine("  --no-publish  build only; GitHub is not touched");
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
        int exitCode = RunPipeline(log, noPublish, devMode, stageOnly, started);
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

    private static int RunPipeline(BuildLog log, bool noPublish, bool devMode, bool stageOnly, DateTimeOffset started)
    {
        string buildVersion = DateTime.UtcNow.ToString("yyyy.MM.dd.HHmm", CultureInfo.InvariantCulture);
        string tag = "v" + buildVersion;
        string flavor = devMode ? "Local Dev" : "NativeAOT win-x64";
        SmartUpdateBuilder.IsDevBuild = devMode;
        log.Info($"Build version: {buildVersion}  (tag {tag})");
        if (!devMode)
        {
            SynchronizeVersionFiles(buildVersion, log);
        }
        else
        {
            log.Info("[VERSION] Skipping disk synchronization of project files in dev mode to preserve clean working tree.");
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
                return FailBuild(log, started);
            }

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Banner($"BUILDING Free Video Studio: {flavor}");
            log.Banner("###########################################################");
            if (!RunNativeAotStages(log, buildVersion))
            {
                return FailBuild(log, started);
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

            if (noPublish || devMode)
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

            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Banner("PUBLISHING RELEASE TO GITHUB...");
            log.Banner("###########################################################");
            bool published = GitHubReleasePublisher.Publish(Staging.CompiledExePath, tag, log);
            if (published) SmartUpdateBuilder.MarkReleased(buildVersion, log);
            // NEWRELEASE_01: prepare the checklist only after a successful public release.
            if (published) ReleaseChecklist.PrepareAndOpen(buildVersion, tag, Staging.CompiledExePath, log);
            log.WriteVerdictAndClose(published ? ExitOk : ExitPublishIncomplete, DateTimeOffset.Now - started);
            if (published)
            {
                return ExitOk;
            }
            log.Banner(string.Empty);
            log.Banner("###########################################################");
            log.Warn("BUILD OK - but the release was NOT updated.");
            log.Warn(".\\compiled\\FreeVideoStudio-Setup.exe is good and usable.");
            log.Warn("Only the GitHub publish step did not finish - reason above.");
            log.Banner("###########################################################");
            return ExitPublishIncomplete;
        }
        catch (Exception ex)
        {
            log.Error($"ERROR: unhandled build failure: {ex}");
            return FailBuild(log, started);
        }
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

    private static void SynchronizeVersionFiles(string buildVersion, BuildLog log)
    {
        string root = FindRepoRoot();
        log.Info($"[VERSION] Synchronizing version '{buildVersion}' across project files...");

        string versionTxtPath = Path.Combine(root, "version.txt");
        try
        {
            File.WriteAllText(versionTxtPath, buildVersion.Trim() + Environment.NewLine);
            log.Info($"[VERSION] Updated {versionTxtPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"[VERSION] Failed to update version.txt: {ex.Message}");
        }

        string propsPath = Path.Combine(root, "Directory.Build.props");
        string propsContent = $"""
<Project>
  <PropertyGroup>
    <Version>{buildVersion}</Version>
    <AssemblyVersion>{buildVersion}</AssemblyVersion>
    <FileVersion>{buildVersion}</FileVersion>
    <InformationalVersion>{buildVersion}</InformationalVersion>
    <ProductVersion>{buildVersion}</ProductVersion>
  </PropertyGroup>
</Project>

""";
        try
        {
            File.WriteAllText(propsPath, propsContent);
            log.Info($"[VERSION] Updated {propsPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"[VERSION] Failed to update Directory.Build.props: {ex.Message}");
        }

        string appCsproj = Path.Combine(root, "src", "FreeVideoStudio.App", "FreeVideoStudio.App.csproj");
        UpdateCsprojVersion(appCsproj, buildVersion, log);

        string coreCsproj = Path.Combine(root, "src", "FreeVideoStudio.Core", "FreeVideoStudio.Core.csproj");
        UpdateCsprojVersion(coreCsproj, buildVersion, log);

        string manifestPath = Path.Combine(root, "src", "FreeVideoStudio.App", "app.manifest");
        try
        {
            string manifest = File.ReadAllText(manifestPath);
            string updated = System.Text.RegularExpressions.Regex.Replace(manifest,
                @"(<assemblyIdentity\s+version="")[^""]*("")", "${1}" + buildVersion + "${2}");
            if (updated != manifest) File.WriteAllText(manifestPath, updated);
            log.Info($"[VERSION] Updated {manifestPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[VERSION] Failed to update app.manifest: {ex.Message}");
        }
    }

    private static void UpdateCsprojVersion(string csprojPath, string buildVersion, BuildLog log)
    {
        if (!File.Exists(csprojPath))
        {
            log.Warn($"[VERSION] Project file not found: {csprojPath}");
            return;
        }

        try
        {
            string content = File.ReadAllText(csprojPath);
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<Version>.*?</Version>", $"<Version>{buildVersion}</Version>");
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<AssemblyVersion>.*?</AssemblyVersion>", $"<AssemblyVersion>{buildVersion}</AssemblyVersion>");
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<FileVersion>.*?</FileVersion>", $"<FileVersion>{buildVersion}</FileVersion>");
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<InformationalVersion>.*?</InformationalVersion>", $"<InformationalVersion>{buildVersion}</InformationalVersion>");
            File.WriteAllText(csprojPath, content);
            log.Info($"[VERSION] Updated {csprojPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"[VERSION] Failed to update {csprojPath}: {ex.Message}");
        }
    }
}

