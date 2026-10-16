// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md (#UI-MUSICWIZARD)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    /// <summary>
    /// ISSUE_10 — the folder the wizard is currently browsing: the user's saved custom music
    /// folder if set and present, otherwise the shell Music folder.
    /// </summary>
    private string ResolveCurrentMusicDirectory()
    {
        try
        {
            if (File.Exists(_paths.SessionStateFile))
            {
                var state = AtomicJsonFile.ReadObject(_paths.SessionStateFile);
                if (state != null && state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
                {
                    string custom = node.ToString();
                    if (Directory.Exists(custom)) return custom;
                }
            }
        }
        catch (Exception __ex) { RuntimeLog.Swallowed(__ex); }

        return Infrastructure.MemeDirectory.GetMusicRoot();
    }

    private void LoadMusicDirectory()
    {
        string? targetDir = null;
        try
        {
            if (File.Exists(_paths.SessionStateFile))
            {
                var state = AtomicJsonFile.ReadObject(_paths.SessionStateFile);
                if (state != null && state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
                {
                    targetDir = node.ToString();
                }
            }
        }
        catch (Exception __ex) { RuntimeLog.Swallowed(__ex); }

        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            targetDir = Infrastructure.MemeDirectory.GetMusicRoot();
        }

        _ = ScanDirectoryForMusicAsync(targetDir);
    }

    private async Task ScanDirectoryForMusicAsync(string directoryPath)
    {
        CancelMusicScan();
        var cts = new CancellationTokenSource();
        _musicScanCts = cts;
        int scanVersion = _musicScanVersion;
        Vm.MusicFolderText = directoryPath;

        _allTracks.Clear();
        AvailableTracks.Clear();
        ApplyTrackFilterAndSort();

        try
        {
            var tracks = await Task.Run(() =>
            {
                var found = new System.Collections.Generic.List<MusicTrackItem>();
                if (!Directory.Exists(directoryPath))
                    return found;

                var exts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

                foreach (string f in Directory.EnumerateFiles(directoryPath))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!exts.Contains(Path.GetExtension(f)))
                        continue;

                    var fileInfo = new FileInfo(f);
                    found.Add(new MusicTrackItem
                    {
                        Name = Path.GetFileName(f),
                        Title = Path.GetFileNameWithoutExtension(f),
                        FilePath = f,
                        DurationText = "Loading...",
                        SizeText = "",
                        LastModifiedTicks = fileInfo.LastWriteTimeUtc.Ticks,
                        IsRecent = _recentMusicPaths.Contains(f)
                    });
                }

                return found;
            }, cts.Token);

            if (cts.Token.IsCancellationRequested || scanVersion != _musicScanVersion)
                return;

            _allTracks.Clear();
            foreach (var item in tracks)
                _allTracks.Add(item);

            ApplyTrackFilterAndSort();

            foreach (var item in tracks)
                _ = ProbeTrackInfoAsync(item, cts.Token);
        }
        catch (OperationCanceledException swallowed10)
        {
            RuntimeLog.Swallowed(swallowed10);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to scan music directory: {ex.Message}");
            ApplyTrackFilterAndSort();
        }
    }

    private void UpdateMusicEmptyState()
    {
        var emptyText = this.FindControl<TextBlock>("EmptyMusicListText");
        if (emptyText != null)
        {
            emptyText.Text = _allTracks.Count == 0
                ? "No music files found. Change folder or drop an audio file here."
                : "No songs match the current search.";
            emptyText.IsVisible = AvailableTracks.Count == 0;
        }
    }

    /// <summary>
    /// Improvement #6: Probes duration and file size asynchronously for display in the list.
    /// </summary>
    private async Task ProbeTrackInfoAsync(MusicTrackItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            await _trackProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(item.FilePath);
                double sizeMb = fileInfo.Length / (1024.0 * 1024.0);
                string sizeText = sizeMb >= 1.0 ? $"{sizeMb:F1} MB" : $"{fileInfo.Length / 1024.0:F0} KB";

                var ffprobePath = ResolveFfprobePath();
                var prober = new FreeVideoStudio.Core.Media.MediaProber(ffprobePath, item.FilePath);
                double duration = await prober.GetDurationAsync().ConfigureAwait(false);

                string durationText;
                double durationSec = 0.0;
                if (duration > 0)
                {
                    durationSec = duration;
                    var ts = TimeSpan.FromSeconds(duration);
                    durationText = ts.TotalHours >= 1
                        ? ts.ToString(@"h\:mm\:ss")
                        : ts.ToString(@"m\:ss");
                }
                else
                {
                    durationText = "—";
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    item.SizeText = sizeText;
                    item.DurationSec = durationSec;
                    item.DurationText = durationText;

                    if (_musicSortMode == "Shortest" || _musicSortMode == "Longest")
                    {
                        ApplyTrackFilterAndSort();
                    }
                    else
                    {
                        var idx = AvailableTracks.IndexOf(item);

                        if (idx >= 0)
                        {
                            var listbox = MusicListBoxCtl;
                            bool wasSelected = ReferenceEquals(listbox?.SelectedItem, item);
                            var tmp = AvailableTracks[idx];
                            _suppressTrackSelectionSideEffects = true;
                            try
                            {
                                AvailableTracks[idx] = tmp;
                                if (wasSelected && listbox != null) listbox.SelectedItem = tmp;
                            }
                            finally { _suppressTrackSelectionSideEffects = false; }
                        }
                    }

                    OnTrackDurationKnown(item);
                    UpdateCoverageBar();
                    UpdateProblemFlags();
                });
            }
            finally
            {
                _trackProbeGate.Release();
            }
        }
        catch (OperationCanceledException swallowed4)
        {
            RuntimeLog.Swallowed(swallowed4);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MUSIC_WIZARD", $"Failed to probe {item.Name}: {ex.Message}");
            Dispatcher.UIThread.Post(() => item.DurationText = "—");
        }
    }
}
