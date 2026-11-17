namespace FreeVideoStudio.Core.Infrastructure;

public static class LegacyProductIdentity
{
    public static string CompactName => AppDataPaths.LegacyDirectoryNames[0];
    public static string DisplayName => AppDataPaths.LegacyDirectoryNames[1];
    public static string TempName => DisplayName.Replace(' ', '_');
    public static string DownloadName => CompactName + ".exe";
    public static string[] ExecutableNames => [DownloadName, CompactName + ".App.exe"];
}
