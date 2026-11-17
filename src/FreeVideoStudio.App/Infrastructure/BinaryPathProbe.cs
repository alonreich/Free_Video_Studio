using System;
using System.IO;
using System.Linq;
using IOPath = System.IO.Path;

namespace FreeVideoStudio.App.Infrastructure;

internal static class BinaryPathProbe
{
    internal static string ResolveForCropTool(string fileName, string preferredSubdirectory)
    {
        string processDir = IOPath.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = IOPath.GetFullPath(IOPath.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));

        string[] candidates =
        [
            IOPath.Combine(processDir, preferredSubdirectory, fileName),
            IOPath.Combine(processDir, "backend", fileName),
            IOPath.Combine(processDir, "frontend", fileName),
            IOPath.Combine(processDir, fileName),
            sourceRootCandidate,
            IOPath.Combine(Environment.CurrentDirectory, "binaries", fileName),
            IOPath.Combine(Environment.CurrentDirectory, preferredSubdirectory, fileName),
            fileName
        ];

        return candidates.FirstOrDefault(File.Exists) ?? fileName;
    }

    internal static string ResolveForVoiceOver(string fileName, string preferredSubdirectory)
    {
        string processDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string baseDir = AppContext.BaseDirectory;
        string sourceRootCandidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", fileName));

        string preferredPath = System.IO.Path.Combine(baseDir, preferredSubdirectory, fileName);
        if (System.IO.File.Exists(preferredPath)) return preferredPath;

        string rootPath = System.IO.Path.Combine(baseDir, fileName);
        if (System.IO.File.Exists(rootPath)) return rootPath;

        string debugPath = System.IO.Path.Combine(processDir, fileName);
        if (System.IO.File.Exists(debugPath)) return debugPath;

        if (System.IO.File.Exists(sourceRootCandidate)) return sourceRootCandidate;

        return fileName;
    }
}
