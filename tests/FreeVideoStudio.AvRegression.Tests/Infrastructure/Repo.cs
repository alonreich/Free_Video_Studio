using System.Runtime.CompilerServices;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>Repository paths, the isolated app state, and the work folder of this run.</summary>
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

    /// <summary>Everything this suite writes lives under tests/AvRegression/ (git-ignored).</summary>
    public static string OutRoot => System.IO.Path.Combine(Path, "tests", "AvRegression");
    public static string MediaDir => Directory.CreateDirectory(System.IO.Path.Combine(OutRoot, "media")).FullName;
    public static string WorkDir(string scenario) =>
        Directory.CreateDirectory(System.IO.Path.Combine(OutRoot, "work", scenario + "-" + DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6])).FullName;

    /// <summary>
    /// Runs before any test touches the app: the app's settings, logs and caches go to a throw-away
    /// folder, never to the developer's real profile (SettingsManager reads this on first use).
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateAppState()
    {
        string state = System.IO.Path.Combine(OutRoot, "state");
        Directory.CreateDirectory(state);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, state);
    }
}

/// <summary>
/// The pass/fail budget of every health check, in ONE place. A human tunes these, an agent does not
/// loosen them to make a red run green (docs/README.md §0.3a).
/// </summary>
internal static class Budget
{
    /// <summary>Playhead frozen this long while it should be playing = a stall/hang.</summary>
    public const int StallMs = 250;
    /// <summary>After play/seek/load the picture may take this long to start moving.</summary>
    public const int StartGraceMs = 450;
    /// <summary>Playback rate must stay within this fraction of the requested speed.</summary>
    public const double RateTolerance = 0.12;
    /// <summary>Largest audio/video offset the player may report while playing.</summary>
    public const double MaxAvSyncSec = 0.080;
    /// <summary>An offset above MaxAvSyncSec must be gone within this (mpv re-times after a speed change).</summary>
    public const int AvSyncRecoverMs = 400;
    /// <summary>An offset this large is a defect even for one sample.</summary>
    public const double HugeAvSyncSec = 0.250;
    /// <summary>A backwards step larger than this during plain playback is a stutter.</summary>
    public const double MaxBackStepSec = 0.020;
    /// <summary>A click-to-seek must land (position reported, seek finished) within this.</summary>
    public const int SeekLatencyMs = 700;
    /// <summary>A player command that does not answer within this is a hang.</summary>
    public const int CommandTimeoutMs = 3000;
    /// <summary>Export marker tolerance: picture vs model, music vs model, audio vs picture.</summary>
    public const double ExportSyncSec = 0.040;
    /// <summary>Where the music stops, measured through its 1.5 s fade-out (less exact than a beat).</summary>
    public const double MusicEndSec = 0.100;
    /// <summary>A picture marker may land one frame off at 60 fps on top of the sync tolerance.</summary>
    public const double FrameSec = 1.0 / 60;
}
