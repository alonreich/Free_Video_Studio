using System;
using System.IO;

namespace FreeVideoStudio.App;

public static class OpenWithLaunch
{
    public static string? PendingVideoPath { get; set; }

    public static bool IsVideoFilePath(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return false;
        try
        {
            if (!File.Exists(arg)) return false;
            string ext = Path.GetExtension(arg).ToLowerInvariant();
            return ext is ".mp4" or ".mkv" or ".avi" or ".mov";
        }
        catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
            return false;
        }
    }
}
