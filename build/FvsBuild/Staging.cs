namespace FvsBuild;

/// <summary>
/// Step 1-2 of the pipeline: clean, stage the release (app publish output + backend/frontend
/// native binaries + starter media) and write its manifests. NODROPPER_01: the staged folder is
/// what the Inno Setup installer packs (InnoInstaller) — nothing is zipped into the program's own
/// resources any more. Every filename here is a contract: the installer installs exactly this
/// layout and MemeAssets.StarterFiles lists the starter names.
/// </summary>
internal static class Staging
{
    private const string ProjectFile = @"src\FreeVideoStudio.App\FreeVideoStudio.App.csproj";
    private const string ProjectExe = "FreeVideoStudio.exe";
    /// <summary>NODROPPER_01 — .\compiled holds exactly the Inno Setup installer.</summary>
    private const string OutputExe = InnoInstaller.SetupFileName;
    private const string OutputDir = @".\compiled";
    /// <summary>
    /// RELEASEASSETS_01 — release sidecars (runtime.manifest.json) live HERE, not in .\compiled.
    /// MANDATE #2 says .\compiled holds exactly one file, and ValidateCompiledOutput enforces it; the
    /// manifest used to be written into .\compiled and every build that reached validation failed.
    /// </summary>
    private const string ReleaseAssetsDir = @".\obj\ReleaseAssets";
    private const string StagingDir = @".\obj\StandaloneTemp\Staging";
    /// <summary>NODROPPER_01 — the signed file list packed into the installer beside the staged files.</summary>
    private const string InstallerListDir = @".\obj\StandaloneTemp\InstallerList";
    /// <summary>Left by builds before NODROPPER_01; removed so it can never be embedded again.</summary>
    private const string LegacyPayloadZip = @"src\FreeVideoStudio.App\payload.zip";

    private static readonly string[] BackendExact =
    [
        "ffmpeg.exe",
        "ffprobe.exe",
    ];

    private static readonly string[] BackendWildcards = ["av*.dll", "sw*.dll", "postproc*.dll"];

    private static readonly string[] FrontendExact = ["libmpv-2.dll", "mpv.exe"];

    private static readonly string[] StarterMp3 =
    [
        "Bonnie Tyler - Holding Out For A Hero.mp3",
        "Cool Dance Background Music (No CopyRights).mp3",
    ];

    private static readonly string[] StarterMeme =
    [
        "What the fuck am I doing here (Robert Deniro) - Landscape.mp4",
        "What the fuck am I doing here (Robert Deniro) - Portrait.mp4",
        "Donald Trump - He Died like a Dog.mp4",
        "I will find you and I will kill you.mp4",
        "Terminated.png",
        "What The Fuck.jpg",
        "oopsie.png",
    ];

    public static string CompiledExePath => Path.Combine(OutputDir, OutputExe);
    public static string CompiledDirPath => OutputDir;
    public static string StagingDirPath => StagingDir;
    public static string InstallerListDirPath => InstallerListDir;
    public static string StagedProgramPath => Path.Combine(StagingDir, ProjectExe);

    /// <summary>Kills any running copies of the product and stops dotnet build servers, so locked files cannot survive a clean.</summary>
    public static void TerminateProcesses(BuildLog log)
    {
        _ = Cli.RunQuiet("taskkill.exe", ["/F", "/IM", "FreeVideoStudio.exe", "/T"]);
        _ = Cli.RunQuiet("dotnet", ["build-server", "shutdown"]);
    }

    /// <summary>Wipes bin/obj of both product projects and runs dotnet clean (parity with the old :CLEAN_ALL).</summary>
    public static void CleanAll(BuildLog log)
    {
        foreach (string directory in new[]
                 {
                     @"src\FreeVideoStudio.App\bin",
                     @"src\FreeVideoStudio.App\obj",
                     @"src\FreeVideoStudio.Core\bin",
                     @"src\FreeVideoStudio.Core\obj",
                 })
        {
            TryDeleteDirectory(directory);
        }
        _ = Cli.RunQuiet("dotnet", ["clean", ProjectFile, "-c", "Release", "-r", "win-x64", "--nologo", "-v", "q", "-nodeReuse:false"]);
    }

    /// <summary>Release mode wipes .\compiled entirely; dev mode only ensures it exists (old Build.cmd vs dev_build.cmd).</summary>
    public static void PrepareCompiledDir(BuildLog log, bool devMode)
    {
        if (devMode)
        {
            Directory.CreateDirectory(OutputDir);
            TryDeleteFile(Path.Combine(OutputDir, ProjectExe));
            return;
        }
        TryDeleteDirectory(OutputDir);
        Directory.CreateDirectory(OutputDir);
    }

    /// <summary>Removes stale staging folders and any leftover payload.zip before the publish runs.</summary>
    public static void PurgeTempArtifacts(BuildLog log)
    {
        log.Info("[NativeAOT] 1. Purging old temp folders...");
        TryDeleteDirectory(StagingDir);
        TryDeleteDirectory(InstallerListDir);
        TryDeleteDirectory(@".\obj\StandaloneTemp\NativeAot_final");
        TryDeleteFile(LegacyPayloadZip);
    }

    /// <summary>
    /// ILCCRASH_01 — output that means ILC ITSELF crashed, as opposed to our code failing to compile.
    /// ILC 9.0.x (dotnet/runtime#108743) can die with IL1013 / NullReferenceException inside
    /// XNodeNavigator while its PARALLEL scanner processes a framework assembly's embedded
    /// ILLink.Substitutions.xml. It is a race in the compiler: the identical inputs compile on the
    /// next run. Nothing in FVS code or configuration triggers it, and nothing is suppressed for it.
    /// </summary>
    private static readonly string[] IlcInternalCrashMarkers =
    [
        "ILCompiler.NativeAotFatalErrorException",
        "ILCompiler.CodeGenerationFailedException",
        "error IL1013",
    ];

    /// <summary>Runs the NativeAOT publish of the program into the staging folder (the only publish since NODROPPER_01).</summary>
    public static bool Publish(string outputDir, string buildVersion, BuildLog log, string stepTitle)
    {
        log.Info($"[NativeAOT] {stepTitle}");
        if (RunPublish(outputDir, buildVersion, log, singleThreadedIlc: false, out bool ilcCrashed))
        {
            return true;
        }
        if (!ilcCrashed)
        {
            return false;
        }

        log.Warn("[NativeAOT] ILC (the NativeAOT compiler) crashed internally - known .NET 9 race, dotnet/runtime#108743.");
        log.Warn("[NativeAOT] Retrying this publish once with a single-threaded ILC scanner (slower, same output)...");
        return RunPublish(outputDir, buildVersion, log, singleThreadedIlc: true, out _);
    }

    private static bool RunPublish(string outputDir, string buildVersion, BuildLog log, bool singleThreadedIlc, out bool ilcCrashed)
    {
        bool crashed = false;
        List<string> arguments =
        [
            "publish", ProjectFile,
            "-c", "Release",
            "-r", "win-x64",
            "-p:TreatWarningsAsErrors=true",
            "-p:PublishAot=true",
            "-p:SelfContained=true",
            "-p:AssemblyVersion=" + buildVersion,
            "-p:Version=" + buildVersion,
            "-p:FileVersion=" + buildVersion,
            "-p:InformationalVersion=" + buildVersion,
            "-nodeReuse:false",
            "-p:UseSharedCompilation=false",
            "-o", outputDir,
            "-v", "m",
        ];
        if (singleThreadedIlc)
        {
            arguments.Add("-p:IlcSingleThreaded=true");
        }
        int exit = Cli.RunStreaming("dotnet", arguments, log, line =>
        {
            foreach (string marker in IlcInternalCrashMarkers)
            {
                if (line.Contains(marker, StringComparison.Ordinal)) { crashed = true; break; }
            }
        });
        ilcCrashed = crashed;
        return exit == 0;
    }

    /// <summary>Step 2.5/2.6: stages backend codecs, the mpv frontend and the starter media next to the staged app.</summary>
    public static bool StageDependencies(BuildLog log)
    {
        log.Info("[NativeAOT] 2.5 Copying binaries to staging...");
        foreach (string group in new[] { "backend", "frontend" })
        {
            Directory.CreateDirectory(Path.Combine(StagingDir, group));
        }

        List<string> missing = [];

        foreach (string file in BackendExact)
        {
            if (!CopyTo($@".\binaries\{file}", Path.Combine(StagingDir, "backend")))
            {
                missing.Add($@"binaries\{file}");
            }
        }
        foreach (string wildcard in BackendWildcards)
        {
            string[] matches = Directory.Exists(@".\binaries")
                ? Directory.GetFiles(@".\binaries", wildcard)
                : [];
            if (matches.Length == 0)
            {
                missing.Add($@"binaries\{wildcard} (no match)");
                continue;
            }
            foreach (string match in matches)
            {
                CopyTo(match, Path.Combine(StagingDir, "backend"));
            }
        }

        foreach (string file in FrontendExact)
        {
            if (!CopyTo($@".\binaries\{file}", Path.Combine(StagingDir, "frontend")))
            {
                missing.Add($@"binaries\{file}");
            }
        }

        log.Info("[NativeAOT] 2.6 Copying starter media to staging...");
        string starterRoot = Path.Combine(StagingDir, "starter");
        foreach (string folder in new[] { "mp3", "meme" })
        {
            Directory.CreateDirectory(Path.Combine(starterRoot, folder));
        }
        foreach (string file in StarterMp3)
        {
            if (!CopyTo($@".\mp3\{file}", Path.Combine(starterRoot, "mp3")))
            {
                missing.Add($@"mp3\{file}");
            }
        }
        foreach (string file in StarterMeme)
        {
            if (!CopyTo($@".\meme\{file}", Path.Combine(starterRoot, "meme")))
            {
                missing.Add($@"meme\{file}");
            }
        }

        if (missing.Count > 0)
        {
            foreach (string file in missing)
            {
                log.Error($"ERROR: staging input missing: {file}");
            }
            log.Error("ERROR: the installer would be incomplete. Fix the inputs above and rebuild.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Step 3: writes the release manifests into the staging folder (runtime.manifest.json, then
    /// install.manifest.json over every staged file) and the release copy of runtime.manifest.json.
    /// Debug symbols move to obj\ReleaseAssets\Symbols first, so they are never installed.
    /// NODROPPER_01: nothing is zipped; the staged folder IS what the Inno Setup installer packs.
    /// </summary>
    public static bool WriteManifests(BuildLog log)
    {
        log.Info("[NativeAOT] 3. Writing release manifests...");
        try
        {
            string symbols = Path.Combine(ReleaseAssetsDir, "Symbols");
            foreach (string pdb in Directory.EnumerateFiles(StagingDir, "*.pdb"))
            {
                Directory.CreateDirectory(symbols);
                File.Move(pdb, Path.Combine(symbols, Path.GetFileName(pdb)), overwrite: true);
            }
            var manifest = FreeVideoStudio.Core.Infrastructure.RuntimePayloadManifest.FromFolder(StagingDir);
            manifest.Write(StagingDir);
            FreeVideoStudio.Core.Infrastructure.InstallPayload.WriteManifest(StagingDir);

            Directory.CreateDirectory(ReleaseAssetsDir);
            manifest.Write(ReleaseAssetsDir);
            foreach (string retired in new[] { "FreeVideoStudio.App.update.zip", "full-payload.zip" })
                TryDeleteFile(Path.Combine(ReleaseAssetsDir, retired));
            string staleAlias = Path.Combine(ReleaseAssetsDir,
                FreeVideoStudio.Core.Infrastructure.LegacyProductIdentity.DownloadName);
            if (File.Exists(staleAlias)) File.Delete(staleAlias);

            log.Info($"[NativeAOT] Runtime fingerprint {manifest.Fingerprint} over {manifest.FileCount} binaries "
                   + $"({FreeVideoStudio.Core.Infrastructure.RuntimePayloadManifest.FormatBytes(manifest.TotalBytes)}).");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.Error($"ERROR: Failed to write the release manifests: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Verifies .\compiled contains EXACTLY FreeVideoStudio-Setup.exe (MANDATE #2, NODROPPER_01).
    /// Strays are removed, then reported as a failure - same as the old
    /// :PURGE_COMPILED_EXTRAS + :VALIDATE_COMPILED_OUTPUT pair.
    /// </summary>
    public static bool ValidateCompiledOutput(BuildLog log)
    {
        bool invalid = !File.Exists(CompiledExePath);
        foreach (string entry in Directory.EnumerateFileSystemEntries(OutputDir))
        {
            if (!string.Equals(Path.GetFileName(entry), OutputExe, StringComparison.OrdinalIgnoreCase))
            {
                log.Error($"ERROR: Removing disallowed artifact from compiled: {Path.GetFileName(entry)}");
                TryDeleteDirectory(entry);
                TryDeleteFile(entry);
                invalid = true;
            }
        }
        if (invalid)
        {
            log.Error($"ERROR: {OutputDir} must contain only {OutputExe}.");
            return false;
        }
        log.Success($"Verified {OutputDir} contains exactly {OutputExe}.");
        return true;
    }

    /// <summary>Step 6: removes the staging folders.</summary>
    public static void CleanupTempArtifacts(BuildLog log)
    {
        log.Info("[NativeAOT] 6. Cleaning up temporary artifacts...");
        TryDeleteDirectory(StagingDir);
        TryDeleteDirectory(InstallerListDir);
        TryDeleteFile(LegacyPayloadZip);
    }

    private static bool CopyTo(string source, string destinationFolder)
    {
        if (!File.Exists(source))
        {
            return false;
        }
        File.Copy(source, Path.Combine(destinationFolder, Path.GetFileName(source)), overwrite: true);
        return true;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
