using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FreeVideoStudio.App.Controls;

internal sealed class HardwareTelemetrySampler : IDisposable
{
    private readonly object _cpuGate = new();

    private ulong _lastIdle;
    private ulong _lastSys;

    private int _lastGpu;

    private int _sampleInFlight;

    private Process? _smiProcess;

    internal int LastGpu => Volatile.Read(ref _lastGpu);

    internal bool TryBeginSample() => Interlocked.CompareExchange(ref _sampleInFlight, 1, 0) == 0;

    internal void EndSample() => Volatile.Write(ref _sampleInFlight, 0);

    internal (int Cpu, int Mem, int Gpu) Sample() => (GetCpuUsage(), GetMemUsage(), LastGpu);

    internal void Start()
    {
        try
        {
            StopSmiProcess();

            _smiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,utilization.encoder --format=csv,noheader,nounits -i 0 -l 1",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            _smiProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var parts = e.Data.Trim().Split(',');
                    if (parts.Length >= 2)
                    {
                        int core = int.TryParse(parts[0].Trim(), out int c) ? c : 0;
                        int enc = int.TryParse(parts[1].Trim(), out int ex) ? ex : 0;
                        Volatile.Write(ref _lastGpu, Math.Max(0, Math.Min(100, Math.Max(core, enc))));
                    }
                }
            };
            _smiProcess.Start();

            try { FreeVideoStudio.Core.Infrastructure.ChildProcessTracker.AddProcess(_smiProcess); }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

            _smiProcess.BeginOutputReadLine();
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    internal void Stop() => StopSmiProcess();

    private void StopSmiProcess()
    {
        var previous = _smiProcess;
        _smiProcess = null;
        if (previous == null) return;

        try
        {
            FreeVideoStudio.Core.Infrastructure.GracefulProcessTerminator.Terminate(
                previous, "Telemetry", attemptQuitCommand: false, cooperativeGraceMs: 0);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        try { previous.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public void Dispose() => StopSmiProcess();

    private int GetCpuUsage()
    {
        lock (_cpuGate)
        {
            if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            {
                ulong sysIdle = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
                ulong sysKernel = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
                ulong sysUser = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;
                ulong sysTime = sysKernel + sysUser;

                if (_lastSys > 0)
                {
                    ulong idlDiff = sysIdle - _lastIdle;
                    ulong sysDiff = sysTime - _lastSys;
                    if (sysDiff > 0)
                    {
                        double dCpu = (sysDiff - idlDiff) * 100.0 / sysDiff;
                        _lastIdle = sysIdle;
                        _lastSys = sysTime;
                        return Math.Max(0, Math.Min(100, (int)dCpu));
                    }
                }
                _lastIdle = sysIdle;
                _lastSys = sysTime;
            }
            return 0;
        }
    }

    private int GetMemUsage()
    {
        MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            return (int)memStatus.dwMemoryLoad;
        }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
