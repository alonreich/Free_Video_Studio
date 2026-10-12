using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

public sealed class ExportViewModel : ViewModelBase
{
    private int _qualitySliderValue = QualityLadder.DefaultIndex;
    private double? _targetMbOverride;
    private string _hardwareMode = "Auto";
    private string _hardwareStatusText = "HW: Detecting…";
    private string _hardwareStatusColor = "#00783C";
    private bool _isRdpWarningVisible;
    private bool _isRdpGpuBlocked;
    private bool _isExporting;
    private int _progressPercentage;
    private int _phaseCurrent;
    private string _phaseTitle = "";
    private int _phaseProgress;
    private string _statusText = "Ready";
    private string _estimatedFileSizeText = "—";
    private string _estimatedFileSizeDescription = "Estimated size of the finished video, including sound.";

    public string EstimatedFileSizeText
    {
        get => _estimatedFileSizeText;
        set => SetProperty(ref _estimatedFileSizeText, value);
    }

    public string EstimatedFileSizeDescription
    {
        get => _estimatedFileSizeDescription;
        set => SetProperty(ref _estimatedFileSizeDescription, value);
    }

    public int QualitySliderValue
    {
        get => _qualitySliderValue;
        set => SetProperty(ref _qualitySliderValue, QualityLadder.ClampIndex(value));
    }

    public double? TargetMbOverride
    {
        get => _targetMbOverride;
        set => SetProperty(ref _targetMbOverride, value);
    }

    public string HardwareMode
    {
        get => _hardwareMode;
        set => SetProperty(ref _hardwareMode, value);
    }

    public string HardwareStatusText
    {
        get => _hardwareStatusText;
        set => SetProperty(ref _hardwareStatusText, value);
    }

    public string HardwareStatusColor
    {
        get => _hardwareStatusColor;
        set => SetProperty(ref _hardwareStatusColor, value);
    }

    public bool IsRdpWarningVisible
    {
        get => _isRdpWarningVisible;
        set => SetProperty(ref _isRdpWarningVisible, value);
    }

    public bool IsRdpGpuBlocked
    {
        get => _isRdpGpuBlocked;
        set => SetProperty(ref _isRdpGpuBlocked, value);
    }

    public bool IsExporting
    {
        get => _isExporting;
        set => SetProperty(ref _isExporting, value);
    }

    public int ProgressPercentage
    {
        get => _progressPercentage;
        set => SetProperty(ref _progressPercentage, value);
    }

    public int PhaseCurrent
    {
        get => _phaseCurrent;
        set => SetProperty(ref _phaseCurrent, value);
    }

    public string PhaseTitle
    {
        get => _phaseTitle;
        set => SetProperty(ref _phaseTitle, value);
    }

    public int PhaseProgress
    {
        get => _phaseProgress;
        set => SetProperty(ref _phaseProgress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }


    public string ResolveHardwareMode()
    {
        return ExportEncoderStrategy.Resolve(
            SettingsManager.Instance.VideoEncoderOverride,
            _hardwareMode,
            ResolveFfmpegPath());
    }

    public static string ResolveFfmpegPath()
    {
        return BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
    }

    public async Task InitializeHardwareScanAsync()
    {
        try
        {
            string ffmpegExe = ResolveFfmpegPath();
            string mode = await HardwareScanner.ScanSharedAsync(ffmpegExe);
            _hardwareMode = mode;

            bool isRdpBlocked = CheckRdpGpuBlocked();
            IsRdpGpuBlocked = isRdpBlocked;
            IsRdpWarningVisible = isRdpBlocked;

            if (isRdpBlocked)
            {
                RuntimeLog.Fail("Hardware", "RDP Session detected with blocked GPU.");
                HardwareStatusText = "RDP: CPU BLOCKED";
                HardwareStatusColor = "#e74c3c";
                return;
            }

            string effective = ResolveHardwareMode();
            if (effective == HardwareScanner.ScanFailed)
            {
                RuntimeLog.Fail("Hardware", "Boot hardware scan did not complete; encoder will be re-probed at export time.");
                HardwareStatusText = "HW: Detecting…";
                HardwareStatusColor = "#daa520";
            }
            else if (effective == "CPU")
            {
                RuntimeLog.Fail("Hardware", "No supported hardware encoder detected; CPU fallback active.");
                HardwareStatusText = "HW: CPU Only";
                HardwareStatusColor = "#808080";
            }
            else
            {
                HardwareStatusText = $"HW: {effective} (Ready)";
                HardwareStatusColor = "#00783C";
            }
        }
        catch (System.Exception swallowed)
        {
            _hardwareMode = HardwareScanner.ScanFailed;
            HardwareStatusText = "HW: Detecting…";
            HardwareStatusColor = "#daa520";
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static bool CheckRdpGpuBlocked()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        bool isRdp = false;
        try { isRdp = GetSystemMetrics(0x1000) != 0; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        if (!isRdp)
        {
            var clientName = Environment.GetEnvironmentVariable("CLIENTNAME");
            if (!string.IsNullOrEmpty(clientName) && !clientName.Equals("Console", StringComparison.OrdinalIgnoreCase))
                isRdp = true;
        }

        if (!isRdp) return false;

        try
        {
            var val = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", "fEnableWddmDriver", null);
            if (val == null || (val is int i && i == 0))
            {
                return true;
            }
        }
        catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
            return true;
        }
        return false;
    }

    /// <summary>NOPOWERSHELL_02 — the one argument that makes this executable apply the RDP GPU policy and exit.</summary>
    public const string ApplyRdpGpuPolicyArgument = "--apply-rdp-gpu-policy";

    private const string RdpPolicyKeyPath = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    /// <summary>
    /// NOPOWERSHELL_02 — the Auto-Fix starts THIS signed executable elevated (Windows asks the user)
    /// with <see cref="ApplyRdpGpuPolicyArgument"/>, and that copy writes the two DWORDs through the
    /// .NET registry API (<see cref="ApplyRdpGpuPolicyElevated"/>). It used to start a hidden
    /// <c>powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden</c> as administrator to edit
    /// machine policy — a combination antivirus heuristics treat as an attack (MITRE T1059.001,
    /// T1112) and one of the reasons new releases were reported as a trojan. Same values, same UAC
    /// prompt, no script host.
    /// </summary>
    public async Task<bool> AutoFixRdpGpuPolicyAsync()
    {
        string? self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !File.Exists(self)) return false;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = self,
                Arguments = ApplyRdpGpuPolicyArgument,
                UseShellExecute = true,
                Verb = "runas"
            });
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    /// <summary>
    /// NOPOWERSHELL_02 — runs in the elevated copy only (dispatched first thing by <c>Program</c>).
    /// Fixed action, no parameters: nothing on the command line can choose a different key or value.
    /// Returns 0 on success, 2 when not elevated, 1 on failure.
    /// </summary>
    public static int ApplyRdpGpuPolicyElevated()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return 1;
        try
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                if (!new System.Security.Principal.WindowsPrincipal(identity)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return 2;
            }

            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = root.CreateSubKey(RdpPolicyKeyPath, writable: true);
            key.SetValue("fEnableWddmDriver", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("fEnableAVC444ModeOnHWEncoder", 1, Microsoft.Win32.RegistryValueKind.DWord);
            return 0;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("RDP policy", ex.ToString());
            return 1;
        }
    }
}
