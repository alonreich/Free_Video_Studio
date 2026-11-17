using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace FreeVideoStudio.App;

public static class RuntimeLog
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static bool _exitRegistered;
    private static string _appName = "[MAIN APP]";
    private const long MaxLogSize = 10 * 1024 * 1024;
    private const int MaxOldLogs = 5;
    private const long MaxTotalOldBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan MaxOldAge = TimeSpan.FromDays(14);

    private const int MaxSingleEntryBytes = 64 * 1024;

    private const long MaxBatchBytes = 1L * 1024 * 1024;

    private static string? _cachedLogPath;
    private static bool _deploymentPhase;

    internal static void SetDeploymentPhase(bool active)
    {
        lock (Sync) { _deploymentPhase = active; _cachedLogPath = null; }
    }
    private static Mutex? _globalMutex;

    private static readonly string LogMutexName =
        FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.UserScopedName("FreeVideoStudioLogMutex");
    private static readonly BlockingCollection<string> _logQueue = new(10000);
    private static long _estimatedSize = -1;
    private static long _droppedCount;

    private static long _failedBatchCount;

    private static bool _startupCleanupDone;
    private static readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    public static string SessionId => _sessionId;
    private static Task? _processTask;

    static RuntimeLog()
    {
        _processTask = Task.Factory.StartNew(ProcessLogQueue, TaskCreationOptions.LongRunning);
    }

    public static event Action<string>? LogAppended;

    public static string LogPath
    {
        get
        {
            if (_cachedLogPath != null) return _cachedLogPath;

            lock (Sync)
            {
                if (_cachedLogPath != null) return _cachedLogPath;

                string? devLogDir = Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR");
                if (!string.IsNullOrWhiteSpace(devLogDir))
                {
                    Directory.CreateDirectory(devLogDir);
                    _cachedLogPath = Path.Combine(devLogDir, "FreeVideoStudio_DEV.log");
                    return _cachedLogPath;
                }

                if (!_deploymentPhase && DeploymentFootprint.IsRunningFromInstallPath())
                {
                    var paths = FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault();
                    paths.EnsureWritableDirectories();
                    _cachedLogPath = Path.Combine(paths.LogsDirectory, "FreeVideoStudio.log");
                }
                else
                {
                    _cachedLogPath = DeploymentFootprint.InstallReportPath;
                }
                return _cachedLogPath;
            }
        }
    }

    public static bool IsDevMode => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR"));

    public static string? DevLogDir => Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR");

    public static void InitializeAppName(string[] args)
    {
        if (args.Any(a => a.Equals("--crop-tool", StringComparison.OrdinalIgnoreCase)))
            _appName = "[CROP TOOLS]";
        else if (args.Any(a => a.Equals("--merger", StringComparison.OrdinalIgnoreCase)))
            _appName = "[VIDEO MERGER]";
        else
            _appName = "[MAIN APP]";
    }

    public static void ResetForProcess()
    {
        lock (Sync)
        {
            if (_initialized) return;
            try
            {
                string lp = LogPath;
            }
            catch {  }

            if (!_exitRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                    SafeWrite($"{_appName} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INFO] SHUTDOWN - Process exiting (PID: {Environment.ProcessId}, Session: {_sessionId})." + Environment.NewLine);
                    WriteExitSeparator();
                    try { _logQueue.CompleteAdding(); } catch (System.Exception) {  }
                    _processTask?.Wait(3000);
                    try { _globalMutex?.Dispose(); } catch (System.Exception) {  }
                };
                _exitRegistered = true;
            }
            _initialized = true;
        }

        string version = "Unknown";
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                version = !string.IsNullOrWhiteSpace(fvi.ProductVersion) ? fvi.ProductVersion!
                        : (!string.IsNullOrWhiteSpace(fvi.FileVersion) ? fvi.FileVersion! : "Unknown");
            }
        }
        catch (System.Exception)
        {
        }
        if (version == "Unknown")
        {
            try { version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown"; }
            catch (System.Exception) { }
        }
        string header = Environment.NewLine +
            $"{_appName} v{version} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INFO] BOOTSTRAP - Started (PID: {Environment.ProcessId}, Session: {_sessionId})" + Environment.NewLine;
        SafeWrite(header);
    }

    private static void WriteExitSeparator()
    {
        string separator = Environment.NewLine + 
                           "---------------------------------------------------------------------------------------------------------------------------" + Environment.NewLine + 
                           Environment.NewLine + 
                           "---------------------------------------------------------------------------------------------------------------------------" + Environment.NewLine;
        SafeWrite(separator);
    }

    public static void Info(string step, string detail)
    {
        Write("INFO", step, detail);
    }

    public static void Debug(string step, string detail)
    {
        if (!IsDevMode) return;
        Write("DEBUG", step, detail);
    }

    public static void Success(string step, string detail)
    {
        Write("SUCCESS", step, detail);
    }

    public static void Fail(string step, string detail)
    {
        Write("FAIL", step, detail);
    }

    public static void Swallowed(
        Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        try
        {
            string where = $"{System.IO.Path.GetFileName(file)}:{line} {member}()";

            FreeVideoStudio.Core.Abstractions.Faults.Recoverable(
                "SWALLOWED", $"{where} — {ex.GetType().Name}: {ex.Message}", ex);
        }
        catch (Exception)
        {
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long LastTicks, int Suppressed)> _throttleState = new();
    private static readonly long ThrottleTicks = TimeSpan.FromSeconds(30).Ticks;

    public static void SwallowedThrottled(
        Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        try
        {
            string where = $"{System.IO.Path.GetFileName(file)}:{line} {member}()";
            if (!TryPassThrottle(where, out int suppressed)) return;

            string tail = suppressed > 0 ? $" (+{suppressed} identical in the last 30s)" : string.Empty;

            FreeVideoStudio.Core.Abstractions.Faults.Recoverable(
                "SWALLOWED", $"{where} — {ex.GetType().Name}: {ex.Message}{tail}", ex);
        }
        catch (Exception)
        {
        }
    }

    public static bool TryPassThrottle(string key, out int suppressed)
    {
        long now = DateTime.UtcNow.Ticks;
        bool emit = false;
        int dropped = 0;
        _throttleState.AddOrUpdate(
            key,
            _ => { emit = true; return (now, 0); },
            (_, prev) =>
            {
                if (now - prev.LastTicks >= ThrottleTicks)
                {
                    emit = true;
                    dropped = prev.Suppressed;
                    return (now, 0);
                }
                return (prev.LastTicks, prev.Suppressed + 1);
            });
        suppressed = dropped;
        return emit;
    }

    public static void Warn(string step, string detail) => Write("WARN", step, detail);

    public static void WarnThrottled(string step, string detail)
    {
        try
        {
            string key = "WARN|" + step + "|" + (detail.Length > 48 ? detail[..48] : detail);
            if (!TryPassThrottle(key, out int suppressed)) return;
            Warn(step, suppressed > 0 ? $"{detail} (+{suppressed} similar in the last 30s)" : detail);
        }
        catch (Exception)
        {
        }
    }

    public static void AppendRaw(string line)
    {
        SafeWrite(line + Environment.NewLine);
        try { LogAppended?.Invoke(line); } catch (System.Exception) {  }
    }

    public static void EmergencyWrite(string step, string detail)
    {
        bool acquired = false;
        try
        {
            try
            {
                _globalMutex ??= new Mutex(false, LogMutexName);
                acquired = _globalMutex.WaitOne(500);
            }
            catch (AbandonedMutexException) { acquired = true; }
            catch (System.Exception) {  }

            string? path = _cachedLogPath;
            if (path == null)
            {
                string? devLogDir = Environment.GetEnvironmentVariable("FVS_DEV_LOG_DIR");
                if (!string.IsNullOrWhiteSpace(devLogDir)) path = Path.Combine(devLogDir, "FreeVideoStudio_DEV.log");
                else if (DeploymentFootprint.IsRunningFromInstallPath()) path = Path.Combine(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().LogsDirectory, "FreeVideoStudio.log");
                else path = DeploymentFootprint.InstallReportPath;
            }

            if (!acquired) path += $".crash.{Environment.ProcessId}.log";

            string safeDetail = FreeVideoStudio.Core.Diagnostics.SecretRedactor.RedactSecrets(detail);
            string line = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [FATAL] {step} - {safeDetail}{Environment.NewLine}";
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        catch (System.Exception) {  }
        finally
        {
            if (acquired && _globalMutex != null)
            {
                try { _globalMutex.ReleaseMutex(); } catch (System.Exception) {  }
            }
        }
    }

    public static void Fail(string step, Exception exception)
    {
        Write("FAIL", step, $"{exception.GetType().Name}: {exception.Message}");
        Debug(step, $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string step, string detail)
    {
        if (!_initialized)
        {
            ResetForProcess();
        }

        string safeDetail = FreeVideoStudio.Core.Diagnostics.SecretRedactor.RedactSecrets(detail);
        string line = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {step} - {safeDetail}{Environment.NewLine}";
        SafeWrite(line);
        try { LogAppended?.Invoke(line.TrimEnd()); } catch (System.Exception) {  }
    }

    private static void SafeWrite(string text)
    {
        if (_logQueue.IsAddingCompleted) return;

        try
        {
            if (text.Length > MaxSingleEntryBytes)
            {
                int originalBytes = Encoding.UTF8.GetByteCount(text);
                if (originalBytes > MaxSingleEntryBytes)
                {
                    int keep = Math.Min(text.Length, MaxSingleEntryBytes / 4);
                    text = text[..keep] +
                           $"... [truncated {originalBytes - Encoding.UTF8.GetByteCount(text[..keep])} bytes]" +
                           Environment.NewLine;
                }
            }

            if (!_logQueue.TryAdd(text))
                Interlocked.Increment(ref _droppedCount);
        }
        catch (System.Exception) {  }
    }

    private static void ProcessLogQueue()
    {
        try
        {
            _globalMutex ??= new Mutex(false, LogMutexName);
        }
        catch
        {
            _cachedLogPath = LogPath + $".{Environment.ProcessId}.local.log";
            _globalMutex = new Mutex(false);
        }

        if (!_startupCleanupDone)
        {
            _startupCleanupDone = true;
            CleanupOldLogs();
        }

        foreach (string firstText in _logQueue.GetConsumingEnumerable())
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(firstText);
                long batchBytes = Encoding.UTF8.GetByteCount(firstText);

                while (batchBytes < MaxBatchBytes && _logQueue.TryTake(out string? moreText))
                {
                    sb.Append(moreText);
                    batchBytes += Encoding.UTF8.GetByteCount(moreText);
                }

                string combinedText = sb.ToString();

                long dropped = Interlocked.Exchange(ref _droppedCount, 0);
                if (dropped > 0)
                {
                    combinedText = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WARN] LOGGER - {dropped} log line(s) dropped due to queue saturation.{Environment.NewLine}" + combinedText;
                }

                long failed = Interlocked.Exchange(ref _failedBatchCount, 0);
                if (failed > 0)
                {
                    combinedText = $"{_appName} [s:{_sessionId}] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WARN] LOGGER - {failed} log batch(es) were lost to a file write error; logging has recovered.{Environment.NewLine}" + combinedText;
                }

                bool written = false;
                while (!written)
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = _globalMutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired)
                        {
                            if (_logQueue.IsAddingCompleted) break;
                            Thread.Sleep(500);
                            continue;
                        }

                        var fi = new FileInfo(LogPath);
                        _estimatedSize = fi.Exists ? fi.Length : 0;
                        long incomingBytes = Encoding.UTF8.GetByteCount(combinedText);

                        if (_estimatedSize + incomingBytes >= MaxLogSize)
                        {
                            string oldLog = LogPath + $".{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.{Guid.NewGuid():N}.old";
                            try
                            {
                                if (File.Exists(LogPath))
                                {
                                    File.Move(LogPath, oldLog);
                                    _estimatedSize = 0;
                                    CleanupOldLogs();
                                }
                            }
                            catch (System.Exception) {  }
                        }

                        using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                        sw.Write(combinedText);
                        _estimatedSize += incomingBytes;

                        int burstCount = 0;
                        while (burstCount < 500
                               && _estimatedSize < MaxLogSize
                               && _logQueue.TryTake(out string? followUp))
                        {
                            sw.Write(followUp);
                            _estimatedSize += Encoding.UTF8.GetByteCount(followUp);
                            burstCount++;
                        }
                        sw.Flush();
                        written = true;
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _failedBatchCount);
                        break;
                    }
                    finally
                    {
                        if (acquired) _globalMutex.ReleaseMutex();
                    }
                }
            }
            catch (System.Exception) {  }
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            bool IsRotatedLog(string f) =>
                f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) && f.Contains("FreeVideoStudio");

            bool IsAuxiliaryLog(string f) =>
                (f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && f.Contains("mpv_debug_")) ||
                f.Contains(".crash.", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".local.log", StringComparison.OrdinalIgnoreCase);

            var oldLogs = Directory.GetFiles(dir, "*.*")
                                   .Where(f => IsRotatedLog(f) || IsAuxiliaryLog(f))
                                   .Select(f => new FileInfo(f))
                                   .OrderByDescending(f => f.CreationTimeUtc)
                                   .ToList();

            DateTime cutoff = DateTime.UtcNow - MaxOldAge;
            long runningTotal = 0;
            int rotatedKept = 0;

            for (int i = 0; i < oldLogs.Count; i++)
            {
                FileInfo f = oldLogs[i];
                runningTotal += f.Length;

                bool rotated = IsRotatedLog(f.FullName);
                bool overCount = false;
                if (rotated)
                {
                    overCount = rotatedKept >= MaxOldLogs;
                    if (!overCount) rotatedKept++;
                }

                bool overSize = runningTotal > MaxTotalOldBytes;
                bool tooOld = f.CreationTimeUtc < cutoff;
                if (overCount || overSize || tooOld)
                {
                    try { f.Delete(); } catch (System.Exception) {  }
                }
            }
        }
        catch (System.Exception) {  }
    }
}
