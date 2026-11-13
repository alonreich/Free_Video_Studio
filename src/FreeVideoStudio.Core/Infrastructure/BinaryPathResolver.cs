using System;
using System.IO;

namespace FreeVideoStudio.Core.Infrastructure;

public static class BinaryPathResolver
{
    public static string Resolve(string filename, params string[] fallbackDirs)
    {
        string[] searchRoots = [AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty];

        foreach (string root in searchRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;

            foreach (string dir in fallbackDirs)
            {
                string path1 = Path.Combine(root, dir, filename);
                if (File.Exists(path1)) return path1;
            }

            var current = new DirectoryInfo(root);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "FreeVideoStudio.sln")))
                {
                    foreach (string dir in fallbackDirs)
                    {
                        string candidate = Path.Combine(current.FullName, dir, filename);
                        if (File.Exists(candidate)) return candidate;
                    }
                    break;
                }
                current = current.Parent;
            }
        }

        return filename;
    }
}
