using System.Diagnostics;
using System.Threading.Channels;
using System.Globalization;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public class MPVSafetyManager : IDisposable
{
    private const int SeekDebounceMs = 50;

    private const double StuckSeekSeconds = 2.5;

    private const int WatchdogPollMs = 500;

    private static readonly TimeSpan TeardownJoinCeiling = TimeSpan.FromSeconds(2);

    private nint _mpvHandle;
    private readonly Channel<double> _seekChannel;
    private readonly CancellationTokenSource _cts;
    private readonly Thread _watchdogThread;
    private readonly Thread _seekThread;

    private volatile bool _handleValid;

    private volatile bool _watchdogExited;
    private volatile bool _seekLoopExited;

    private readonly ManualResetEventSlim _stopSignal = new(false);

    private int _disposed;

    private readonly object _stateLock = new();
    private bool _isSeeking;
    private DateTime _seekStartTime;
    private double _seekTargetPercent;

    public MPVSafetyManager(nint mpvHandle)
    {
        _mpvHandle = mpvHandle;
        _handleValid = mpvHandle != nint.Zero;

        _seekChannel = Channel.CreateBounded<double>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        _cts = new CancellationTokenSource();

        _seekThread = new Thread(SeekProcessorThreadBody)
        {
            IsBackground = true,
            Name = "MPV_Seek_Processor"
        };

        _watchdogThread = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "MPV_Safety_Watchdog"
        };

        _seekThread.Start();
        _watchdogThread.Start();
    }

    public void RequestSeek(double timeSeconds)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _seekChannel.Writer.TryWrite(timeSeconds);
    }

    private void SeekProcessorThreadBody()
    {
        try
        {
            SeekProcessorLoopAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException swallowed3)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
        }
        catch (ObjectDisposedException swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
        }
        catch (ChannelClosedException swallowed4)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("MPV", $"The seek processor stopped unexpectedly: {ex.GetType().Name}: {ex.Message}");
            CoreLogger.Debug("MPV", ex.ToString());
        }
        finally
        {
            lock (_stateLock) { _isSeeking = false; }
            _seekLoopExited = true;
        }
    }

    private async Task SeekProcessorLoopAsync()
    {
        CancellationToken token = _cts.Token;
        Stopwatch debounceTimer = Stopwatch.StartNew();

        await foreach (double time in _seekChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (token.IsCancellationRequested) break;

            long elapsedMs = debounceTimer.ElapsedMilliseconds;
            if (elapsedMs < SeekDebounceMs)
            {
                await Task.Delay(SeekDebounceMs - (int)elapsedMs, token).ConfigureAwait(false);
            }

            debounceTimer.Restart();
            if (token.IsCancellationRequested) break;

            lock (_stateLock)
            {
                _isSeeking = true;
                _seekStartTime = DateTime.UtcNow;
                _seekTargetPercent = time;
            }

            try
            {
                IssueSeek(time);
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
            }
            finally
            {
                lock (_stateLock) { _isSeeking = false; }
            }
        }
    }

    private void IssueSeek(double percent)
    {
        if (!_handleValid) return;

        nint handle = _mpvHandle;
        if (handle == nint.Zero) return;

        string pct = percent.ToString("F1", CultureInfo.InvariantCulture);
        MpvWrapper.mpv_command_string(handle, $"seek {pct} absolute-percent");
    }

    private void WatchdogLoop()
    {
        try
        {
            while (!_stopSignal.Wait(WatchdogPollMs))
            {
                bool stuck = false;
                double target = 0;

                lock (_stateLock)
                {
                    if (_isSeeking && (DateTime.UtcNow - _seekStartTime).TotalSeconds > StuckSeekSeconds)
                    {
                        stuck = true;
                        target = _seekTargetPercent;
                        _isSeeking = false;
                    }
                }

                if (stuck)
                {
                    CoreLogger.Fail("MPV",
                        $"Seek watchdog tripped: a seek to {target.ToString("F1", CultureInfo.InvariantCulture)}% " +
                        $"did not complete within {StuckSeekSeconds.ToString("F1", CultureInfo.InvariantCulture)}s. Seek state reset.");
                }
            }
        }
        catch (ObjectDisposedException swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
        finally
        {
            _watchdogExited = true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _handleValid = false;

        try { _seekChannel.Writer.TryComplete(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        try { _stopSignal.Set(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { _cts.Cancel(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        bool seekStopped = JoinBounded(_seekThread, "MPV_Seek_Processor");
        bool watchdogStopped = JoinBounded(_watchdogThread, "MPV_Safety_Watchdog");

        bool seekAccountedFor = seekStopped || _seekLoopExited;
        bool watchdogAccountedFor = watchdogStopped || _watchdogExited;

        if (!seekAccountedFor || !watchdogAccountedFor)
        {
            CoreLogger.Fail("MPV",
                "An MPVSafetyManager consumer did not stop in time — abandoning its synchronisation " +
                "objects instead of freeing them underneath a live thread.");
            _mpvHandle = nint.Zero;
            return;
        }

        try { _cts.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { _stopSignal.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        _mpvHandle = nint.Zero;
    }

    private static bool JoinBounded(Thread thread, string name)
    {
        try
        {
            if (thread.Join(TeardownJoinCeiling)) return true;
            CoreLogger.Warn("MPV", $"{name} did not stop within {TeardownJoinCeiling.TotalSeconds:F0}s; continuing teardown.");
            return false;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
    }
}
