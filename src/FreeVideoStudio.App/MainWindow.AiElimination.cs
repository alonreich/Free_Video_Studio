// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (#TL-AITRACKING), docs/05_SYSTEM_LIFECYCLE_STORAGE.md (#SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private readonly List<double> _aiEliminationTimestamps = new();
    private CancellationTokenSource? _aiEliminationScanCts;

    public IReadOnlyList<double> AiEliminationTimestamps => _aiEliminationTimestamps;

    public void SetAiEliminations(IEnumerable<double>? timestamps)
    {
        _aiEliminationTimestamps.Clear();
        if (timestamps != null)
        {
            _aiEliminationTimestamps.AddRange(timestamps);
        }
        QueueTimelineRedrawPass(DispatcherPriority.Background);
    }

    private async Task RunAiEliminationScanAsync(string videoPath)
    {
        try
        {
            _aiEliminationScanCts?.Cancel();
            _aiEliminationScanCts?.Dispose();
            var cts = new CancellationTokenSource();
            _aiEliminationScanCts = cts;

            var settings = SettingsManager.Instance;
            if (!settings.AiEliminationTrackerEnabled) return;

            var config = settings.GetUsageConfig(AiUsage.EliminationTracker);
            string apiKey = settings.GetApiKey(config.Vendor);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                RuntimeLog.Info("AIElimination", "AI Elimination Tracker enabled but no API key configured. Skipping scan.");
                return;
            }

            string ffmpegPath = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            double durationSec = (_loadedVideoDurationMs > 0 ? _loadedVideoDurationMs : 60000.0) / 1000.0;

            var markers = await Task.Run(() => AiEliminationTrackerService.ScanVideoForEliminationsAsync(
                ffmpegPath,
                videoPath,
                durationSec,
                config.Vendor,
                apiKey,
                config.Model,
                status => RuntimeLog.Info("AIElimination", status),
                cts.Token), cts.Token).ConfigureAwait(false);

            if (markers != null && markers.Count > 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested || _loadedVideoPath != videoPath) return;
                    _aiEliminationTimestamps.Clear();
                    foreach (var m in markers)
                    {
                        _aiEliminationTimestamps.Add(m.TimestampSec);
                    }
                    QueueTimelineRedrawPass(DispatcherPriority.Background);
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }
}
