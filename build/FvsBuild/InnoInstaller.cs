namespace FvsBuild;

/// <summary>
/// NODROPPER_01 — compiles build\installer\FreeVideoStudio.iss with Inno Setup into the single
/// download, .\compiled\FreeVideoStudio-Setup.exe. Spec: docs/09_DISTRIBUTION_AND_RELEASE.md #DIST-INSTALLER.
///
/// <para>Replaces the embedded payload.zip: the release is no longer a program that carries compressed
/// copies of other programs in its own resources (the dropper pattern antivirus engines flag), but a
/// standard installer package whose every file antivirus can unpack, name and scan. Inno Setup is free;
/// version 6.3 or newer is required (winget install JRSoftware.InnoSetup). <c>FVS_ISCC</c> may name
/// ISCC.exe explicitly.</para>
/// </summary>
internal static class InnoInstaller
{
    public const string ScriptPath = @"build\installer\FreeVideoStudio.iss";
    public const string SetupFileName = "FreeVideoStudio-Setup.exe";
    private const string IconPath = @"assets\icons\Video_Icon_File.ico";

    public static bool Build(string buildVersion, string stagingDir, string signedListDir, string outputDir, BuildLog log)
    {
        log.Info("[Installer] Building the Inno Setup installer (NODROPPER_01)...");
        string? compiler = FindCompiler();
        if (compiler is null)
        {
            log.Error("ERROR: Inno Setup was not found. It builds the installer and is free:");
            log.Error("       winget install JRSoftware.InnoSetup      (version 6.3 or newer)");
            log.Error("       or set FVS_ISCC to the full path of ISCC.exe.");
            return false;
        }
        foreach (string required in new[] { ScriptPath, IconPath, Path.Combine(stagingDir, "FreeVideoStudio.exe"),
                     Path.Combine(signedListDir, FreeVideoStudio.Core.Infrastructure.SmartUpdateManifest.FileName),
                     Path.Combine(signedListDir, FreeVideoStudio.Core.Infrastructure.SmartUpdateManifest.SignatureFileName) })
        {
            if (!File.Exists(required))
            {
                log.Error($"ERROR: [Installer] input missing: {required}");
                return false;
            }
        }

        Directory.CreateDirectory(outputDir);
        string output = Path.Combine(outputDir, SetupFileName);
        if (File.Exists(output)) File.Delete(output);

        int exit = Cli.RunStreaming(compiler,
        [
            "/Qp",
            "/DAppVersion=" + buildVersion,
            "/DStagingDir=" + Path.GetFullPath(stagingDir),
            "/DSignedListDir=" + Path.GetFullPath(signedListDir),
            "/DIconFile=" + Path.GetFullPath(IconPath),
            "/DOutputDir=" + Path.GetFullPath(outputDir),
            Path.GetFullPath(ScriptPath),
        ], log);
        if (exit != 0 || !File.Exists(output))
        {
            log.Error($"ERROR: Inno Setup failed (exit code {exit}). The installer was not produced.");
            return false;
        }
        log.Success($"[Installer] {SetupFileName}: {new FileInfo(output).Length / (1024 * 1024)} MB.");
        return true;
    }

    private static string? FindCompiler()
    {
        string? configured = Environment.GetEnvironmentVariable("FVS_ISCC");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string? onPath = Cli.FindOnPath("ISCC.exe");
        if (onPath is not null) return onPath;
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string candidate = Path.Combine(Environment.GetFolderPath(folder), "Inno Setup 6", "ISCC.exe");
            if (File.Exists(candidate)) return candidate;
        }
        string perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Inno Setup 6", "ISCC.exe");
        return File.Exists(perUser) ? perUser : null;
    }
}
