using System.Runtime.CompilerServices;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.AvRegression.Tests;

internal static class Repo
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "FreeVideoStudio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("FreeVideoStudio.sln not found above " + AppContext.BaseDirectory);
    });

    public static string Path => Root.Value;
    public static string Ffmpeg => System.IO.Path.Combine(Path, "binaries", "ffmpeg.exe");
    public static string Ffprobe => System.IO.Path.Combine(Path, "binaries", "ffprobe.exe");

    public static string OutRoot => FreeVideoStudio.Testing.TestArtifactDirectory.SuiteWork("AV");
    public static string MediaDir => Directory.CreateDirectory(System.IO.Path.Combine(OutRoot, "media")).FullName;
    public static string WorkDir(string scenario) =>
        Directory.CreateDirectory(System.IO.Path.Combine(OutRoot, "scenarios", scenario + "-" + DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6])).FullName;

    [ModuleInitializer]
    internal static void IsolateAppState()
    {
        string state = System.IO.Path.Combine(OutRoot, "state");
        Directory.CreateDirectory(state);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, state);
    }
}

internal static class Budget
{
    public const int StallMs = 250;
    public const int StartGraceMs = 450;
    public const double RateTolerance = 0.12;
    public const double MaxAvSyncSec = 0.080;
    public const int AvSyncRecoverMs = 400;
    public const double HugeAvSyncSec = 0.250;
    public const double MaxBackStepSec = 0.020;
    public const int SeekLatencyMs = 700;
    public const int CommandTimeoutMs = 3000;
    public const double ExportSyncSec = 0.040;
    public const double MusicEndSec = 0.100;
    public const double FrameSec = 1.0 / 60;
}
