using System;
using System.IO;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// PEAKMATH_01 — scratch-file hygiene lifted out of <c>MusicWizardWindow</c>.
/// </summary>
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
