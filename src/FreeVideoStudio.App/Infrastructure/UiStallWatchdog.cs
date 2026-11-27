using System;
using System.IO;
using System.Threading;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

public static class UiStallWatchdog
{
    public const int TickMs = 1000;
    public const int ReportAfterMs = 2000;
    public const int MaxReportsPerStall = 6;
    public const string FileName = "ui_stalls.log";

    private static Timer? _timer;
    private static long _heartbeat;
    private static int _reports;

    public static void Start()
    {
        if (_timer != null) return;
        Interlocked.Exchange(ref _heartbeat, Environment.TickCount64);
        _timer = new Timer(_ => Check(), null, TickMs, TickMs);
    }

    public static void Stop()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        try { t?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static void Check()
    {
        long since = Environment.TickCount64 - Interlocked.Read(ref _heartbeat);
        if (since > ReportAfterMs)
        {
            if (_reports < MaxReportsPerStall)
            {
                _reports++;
                Report($"UI THREAD STALLED for {since}ms (report {_reports}/{MaxReportsPerStall}). " +
                       $"Blocking calls: {BlockingCallTracker.Describe()} | " +
                       $"preview UI step: {MpvVideoView.LastUiStep} | preview render step: {MpvVideoView.LastRenderStep} | " +
                       $"render lock: {MpvVideoView.LastLockStep}");
            }
        }
        else
        {
            _reports = 0;
        }
        Dispatcher.UIThread.Post(() => Interlocked.Exchange(ref _heartbeat, Environment.TickCount64), DispatcherPriority.Background);
    }

    private static void Report(string detail)
    {
        RuntimeLog.EmergencyWrite("UiWatchdog", detail);
        try
        {
            string dir = ApplicationPaths.CreateDefault().LogsDirectory;
            Directory.CreateDirectory(dir);
            BoundedLogFile.Append(Path.Combine(dir, FileName),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [pid {Environment.ProcessId}] {detail}{Environment.NewLine}");
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
}
