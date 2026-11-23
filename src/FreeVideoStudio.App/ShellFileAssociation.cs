using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FreeVideoStudio.App;

[SupportedOSPlatform("windows")]
public static class ShellFileAssociation
{
    private const string ProgId = "FreeVideoStudio.Video";
    private const string AppExeKey = "FreeVideoStudio.exe";
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mov" };

    private const string StampVersion = "1";

    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
            if (!IsInstalledProgram(exePath)) return;

            string command = $"\"{exePath}\" \"%1\"";
            string stamp = $"{StampVersion}|{exePath}";

            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            using (RegistryKey? existing = classes.OpenSubKey(ProgId))
            {
                if (existing?.GetValue("_fvs_stamp") as string == stamp)
                    return;
            }

            using (RegistryKey prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue("", "Free Video Studio Video");
                prog.SetValue("FriendlyTypeName", "Free Video Studio");
                prog.SetValue("_fvs_stamp", stamp);
                using (RegistryKey icon = prog.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{exePath}\",0");
                using (RegistryKey cmd = prog.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", command);
            }

            using (RegistryKey app = classes.CreateSubKey($@"Applications\{AppExeKey}"))
            {
                app.SetValue("FriendlyAppName", "Free Video Studio");
                using (RegistryKey cmd = app.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", command);
                using (RegistryKey supported = app.CreateSubKey("SupportedTypes"))
                    foreach (string ext in VideoExtensions)
                        supported.SetValue(ext, "");
            }

            foreach (string ext in VideoExtensions)
            {
                using RegistryKey extKey = classes.CreateSubKey($@"{ext}\OpenWithProgids");
                extKey.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            RuntimeLog.Info("OPEN WITH", "Registered Explorer 'Open with' association (HKCU) for .mp4/.mkv/.avi/.mov.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("OPEN WITH", $"Association registration skipped: {ex.Message}");
        }
    }

    internal static bool IsInstalledProgram(string exePath)
    {
        try
        {
            return string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(DeploymentFootprint.InstallPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }
}
