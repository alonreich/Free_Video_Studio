using System;
using System.Globalization;
using System.IO;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

internal static class RescuedOutputPath
{
    private const int MaxIndex = 10000;

    internal const string EmptyReservationTag = "RESCUE_02";

    private static void RemoveEmptyReservation(string candidate)
    {
        try
        {
            var info = new FileInfo(candidate);
            if (info.Exists && info.Length == 0) info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    internal static string? TryRescue(
        string sourcePath,
        string tempDirectory,
        string filenamePrefix,
        string logTag,
        string failureMessage)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;

            Directory.CreateDirectory(tempDirectory);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            for (int idx = 0; idx <= MaxIndex; idx++)
            {
                string candidate = Path.Combine(
                    tempDirectory,
                    idx == 0 ? $"{filenamePrefix}{stamp}.mp4"
                             : $"{filenamePrefix}{stamp}-{idx}.mp4");

                try
                {
                    using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                    }
                }
                catch (IOException swallowed)
                {
                    global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
                    continue;
                }

                try
                {
                    File.Move(sourcePath, candidate, overwrite: true);
                }
                catch
                {
                    RemoveEmptyReservation(candidate);
                    throw;
                }
                return candidate;
            }

            CoreLogger.Fail(logTag,
                $"{failureMessage}: could not reserve a filename in '{tempDirectory}' after {MaxIndex} attempts. " +
                "The folder may be full, read-only, or unavailable.");
            return null;
        }
        catch (Exception ex)
        {
            CoreLogger.Fail(logTag, $"{failureMessage}: {ex.Message}");
            return null;
        }
    }
}
