using System.Collections.Generic;
using System.Linq;

namespace FreeVideoStudio.Core.Infrastructure;

public static class ProcessArgs
{
    public static string FormatForLog(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(a =>
            a.Length == 0 || a.Contains(' ') || a.Contains('"')
                ? "\"" + a.Replace("\"", "\\\"") + "\""
                : a));
    }
}
