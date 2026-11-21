namespace FvsBuild;

internal static class Staging
{
    private const string ProjectFile = @"src\FreeVideoStudio.App\FreeVideoStudio.App.csproj";
    private const string ProjectExe = "FreeVideoStudio.exe";
    private const string OutputExe = InnoInstaller.SetupFileName;
    private const string OutputDir = @".\compiled";
    private const string ReleaseAssetsDir = @".\obj\ReleaseAssets";
    private const string StagingDir = @".\obj\StandaloneTemp\Staging";
    private const string InstallerListDir = @".\obj\StandaloneTemp\InstallerList";
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
        "!_Inspirational Music - The Travelling Symphony by SavfkMusic (No CopyRight).mp3",
        "!_Inspirational Music - The Travelling Symphony by SavfkMusic (No CopyRight).txt",
        "!_System Recall - Aftertune (No CopyRights).mp3",
        "!_System Recall - Aftertune (No CopyRights).txt",
    ];

    private static readonly string[] StarterMeme =
    [
        "Countdown - Landscape.mp4",
        "Countdown - Portrait.mp4",
        "Quick Flash - Portrait.mp4",
        "Sunset Gradient.png",
        "Test Card.jpg",
        "oopsie.png",
    ];

    public static string CompiledExePath => Path.Combine(OutputDir, OutputExe);
    public static string CompiledDirPath => OutputDir;
    public static string StagingDirPath => StagingDir;
    public static string InstallerListDirPath => InstallerListDir;
    public static string StagedProgramPath => Path.Combine(StagingDir, ProjectExe);

    public static void TerminateProcesses(BuildLog log)
    {
        string root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    continue;
                }
                if (executable is null || !executable.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    log.Info($"  -> Stopping {Path.GetFileName(executable)} (PID {process.Id}): it runs from this repository.");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    log.Warn($"  -> Could not stop PID {process.Id}: {ex.Message}");
                }
            }
        }
        _ = Cli.RunQuiet("dotnet", ["build-server", "shutdown"]);
    }

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

    public static void PrepareCompiledDir(BuildLog log, bool devMode)
    {
        Directory.CreateDirectory(OutputDir);
        foreach (string entry in Directory.EnumerateFileSystemEntries(OutputDir))
        {
            if (!string.Equals(Path.GetFileName(entry), OutputExe, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(entry);
                TryDeleteFile(entry);
            }
        }
        if (!devMode)
        {
            TryDeleteFile(Path.Combine(OutputDir, OutputExe));
        }
    }

    public static void PurgeTempArtifacts(BuildLog log)
    {
        log.Info("[NativeAOT] 1. Purging old temp folders...");
        TryDeleteDirectory(StagingDir);
        TryDeleteDirectory(InstallerListDir);
        TryDeleteDirectory(@".\obj\StandaloneTemp\NativeAot_final");
        TryDeleteFile(LegacyPayloadZip);
    }

    private static readonly string[] IlcInternalCrashMarkers =
    [
        "ILCompiler.NativeAotFatalErrorException",
        "ILCompiler.CodeGenerationFailedException",
        "error IL1013",
    ];

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

    public static bool StageDependencies(BuildLog log, string? destination = null)
    {
        string stagingRoot = destination ?? StagingDir;
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (destination != null) RejectStagingLinks(stagingRoot);
        bool StageFile(string source, string folder)
        {
            string target = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(source)));
            expected.Add(target);
            if (!File.Exists(source)) return false;
            if (destination != null && File.Exists(target) && SameContent(source, target)) return true;
            return CopyTo(source, folder);
        }
        log.Info(destination == null ? "[NativeAOT] 2.5 Copying binaries to staging..."
            : "[DEV] Verifying native binaries; copying only missing or changed content...");
        foreach (string group in new[] { "backend", "frontend" })
        {
            Directory.CreateDirectory(Path.Combine(stagingRoot, group));
        }

        List<string> missing = [];

        foreach (string file in BackendExact)
        {
            if (!StageFile($@".\binaries\{file}", Path.Combine(stagingRoot, "backend")))
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
                if (!StageFile(match, Path.Combine(stagingRoot, "backend"))) missing.Add(match);
            }
        }

        foreach (string file in FrontendExact)
        {
            if (!StageFile($@".\binaries\{file}", Path.Combine(stagingRoot, "frontend")))
            {
                missing.Add($@"binaries\{file}");
            }
        }

        log.Info(destination == null ? "[NativeAOT] 2.6 Copying starter media to staging..."
            : "[DEV] Verifying starter media; copying only missing or changed content...");
        string starterRoot = Path.Combine(stagingRoot, "starter");
        foreach (string folder in new[] { "mp3", "meme" })
        {
            Directory.CreateDirectory(Path.Combine(starterRoot, folder));
        }
        foreach (string file in StarterMp3)
        {
            if (!StageFile($@".\mp3\{file}", Path.Combine(starterRoot, "mp3")))
            {
                missing.Add($@"mp3\{file}");
            }
        }
        foreach (string file in StarterMeme)
        {
            string src = File.Exists($@".\build\starter_meme\{file}") ? $@".\build\starter_meme\{file}" : $@".\meme\{file}";
            if (!StageFile(src, Path.Combine(starterRoot, "meme")))
            {
                missing.Add($@"build\starter_meme\{file}");
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
        if (destination != null)
        {
            foreach (string group in new[] { "backend", "frontend", "starter" })
                foreach (string file in Directory.EnumerateFiles(Path.Combine(stagingRoot, group), "*", SearchOption.AllDirectories))
                    if (!expected.Contains(Path.GetFullPath(file))) File.Delete(file);
        }
        return true;
    }

    private static bool SameContent(string source, string target)
    {
        if (new FileInfo(source).Length != new FileInfo(target).Length) return false;
        using var input = File.OpenRead(source);
        using var output = File.OpenRead(target);
        return System.Security.Cryptography.SHA256.HashData(input).AsSpan()
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(output));
    }

    private static void RejectStagingLinks(string root)
    {
        for (DirectoryInfo? ancestor = new(Path.GetFullPath(root)); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing linked staging path: " + ancestor.FullName);
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing linked staging entry: " + entry);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }

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
