using System;
using System.IO;

namespace FreeVideoStudio.App.Infrastructure;

internal static class AudioPeakMath
{
    internal static void DeleteTempFile(ref string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch (System.Exception __ex) { RuntimeLog.Swallowed(__ex); }
        }
        path = null;
    }
}
