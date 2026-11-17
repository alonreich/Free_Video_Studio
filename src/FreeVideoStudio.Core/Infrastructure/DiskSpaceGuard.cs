namespace FreeVideoStudio.Core.Infrastructure;

public static class DiskSpaceGuard
{
    private const long SafetyMarginBytes = 256L * 1024 * 1024;

    public readonly record struct Result(bool Ok, string? Message)
    {
        public static Result Success() => new(true, null);
        public static Result Fail(string message) => new(false, message);
    }

    public static Result Check(string tempDirectory, string outputDirectory, long estimatedOutputBytes)
    {
        if (estimatedOutputBytes <= 0) return Result.Success();

        try
        {
            string? tempRoot = GetVolumeRoot(tempDirectory);
            string? outputRoot = GetVolumeRoot(outputDirectory);

            bool sameVolume = tempRoot != null && outputRoot != null &&
                              string.Equals(tempRoot, outputRoot, StringComparison.OrdinalIgnoreCase);

            if (sameVolume)
            {
                long needed = (estimatedOutputBytes * 2) + SafetyMarginBytes;
                return CheckOne(tempRoot!, needed, "staging and output");
            }

            Result staging = CheckOne(tempRoot, estimatedOutputBytes + SafetyMarginBytes, "staging");
            if (!staging.Ok) return staging;

            return CheckOne(outputRoot, estimatedOutputBytes + SafetyMarginBytes, "output");
        }
        catch (Exception ex)
        {
            CoreLogger.Info("DiskSpace", $"Free-space probe skipped: {ex.Message}");
            return Result.Success();
        }
    }

    private static Result CheckOne(string? volumeRoot, long neededBytes, string role)
    {
        if (string.IsNullOrWhiteSpace(volumeRoot)) return Result.Success();

        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady) return Result.Success();

        long free = drive.AvailableFreeSpace;
        if (free >= neededBytes)
        {
            CoreLogger.Info("DiskSpace",
                $"Free-space check passed for {role} volume: {Mb(free)} MB available, {Mb(neededBytes)} MB required.");
            return Result.Success();
        }

        string message =
            $"Not enough free space on drive {drive.Name.TrimEnd('\\')} for the {role} files. " +
            $"About {Mb(neededBytes)} MB is required but only {Mb(free)} MB is free. " +
            "Free up some space (or pick a different output folder in Settings) and try again.";

        CoreLogger.Fail("DiskSpace", message);
        return Result.Fail(message);
    }

    private static string? GetVolumeRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        try
        {
            string full = Path.GetFullPath(directory);
            string? root = Path.GetPathRoot(full);

            if (string.IsNullOrWhiteSpace(root) || root!.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return root;
        }
        catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
            return null;
        }
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    public static long EstimateOutputBytes(double durationSeconds, int? videoBitrateKbps, double? targetMb)
    {
        if (targetMb is > 0) return (long)(targetMb.Value * 1024 * 1024);
        if (durationSeconds <= 0) return 0;

        int kbps = videoBitrateKbps is > 0 ? videoBitrateKbps!.Value : 15000;
        return (long)((kbps + 192) * 1000.0 / 8.0 * durationSeconds);
    }

    private const int TwoPassMasterKbps = 32000;

    public static long EstimateTwoPassMasterBytes(double durationSeconds)
    {
        if (durationSeconds <= 0) return 0;
        return (long)((TwoPassMasterKbps + 192) * 1000.0 / 8.0 * durationSeconds);
    }

    public static bool HasRoomFor(string tempDirectory, long extraBytes)
    {
        if (extraBytes <= 0) return true;

        try
        {
            string? root = GetVolumeRoot(tempDirectory);
            if (string.IsNullOrWhiteSpace(root)) return true;

            var drive = new DriveInfo(root!);
            if (!drive.IsReady) return true;

            long needed = extraBytes + SafetyMarginBytes;
            bool ok = drive.AvailableFreeSpace >= needed;
            CoreLogger.Info("DiskSpace",
                ok
                    ? $"Two-pass scratch master fits: {Mb(drive.AvailableFreeSpace)} MB free, {Mb(needed)} MB needed."
                    : $"Two-pass scratch master does NOT fit ({Mb(drive.AvailableFreeSpace)} MB free, {Mb(needed)} MB needed) — using the slower two-pass route that needs no extra disk.");
            return ok;
        }
        catch (Exception ex)
        {
            CoreLogger.Info("DiskSpace", $"Two-pass scratch probe skipped: {ex.Message}");
            return true;
        }
    }
}
