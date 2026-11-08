using System.Runtime.CompilerServices;
using FreeVideoStudio.Core.Media.Native;

namespace FreeVideoStudio.App.Tests;

internal static class NativeTestBootstrap
{
    // TESTNATIVE_01: fake media paths must not poison the process-wide DLL discovery latch.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows())
            LibAvMediaBackend.TryInitializeFrameTier(Path.Combine(RepoRoot.Path, "binaries", "ffmpeg.exe"), out _);
    }
}
