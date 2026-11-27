using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
namespace FreeVideoStudio.App.Controls;
public partial class MemePickerWindow
{
    private MpvVideoView? _resultView;
    private Button? _resultButton;
    private Panel? _resultPanel;
    private Control? _placementPreview;
    private CancellationTokenSource? _resultCancellation;
    private Task? _resultTask;
    private string? _resultDirectory;
    private string? _resultPath;
    private double _resultStart;
    private bool _resultValid;
    private bool _previewCloseReady;
    private bool _previewClosePending;
    private void ConfigureResultPreview(Button? button)
    {
        _resultButton = button;
        if ((button?.Parent?.Parent as StackPanel)?.Children.OfType<Border>().FirstOrDefault() is { Child: Control preview } frame)
        {
            frame.Child = null;
            _placementPreview = preview;
            _resultPanel = new Panel();
            _resultPanel.Children.Add(preview);
            frame.Child = _resultPanel;
        }
        if (button != null) button.Click += (_, _) =>
        {
            if (_resultTask is { IsCompleted: false }) { _resultCancellation?.Cancel(); return; }
            _resultTask = PreviewResultAsync();
        };
        Choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MemeChoiceViewModel.IsPlaying))
            {
                if (_resultValid && Choice.IsResultPreview && _resultView?.IpcClient is { } player) _ = player.SetPropertyAsync("pause", Choice.IsPlaying ? "no" : "yes");
                return;
            }
            if (e.PropertyName is nameof(MemeChoiceViewModel.HasMeme) or nameof(MemeChoiceViewModel.MemePath) or nameof(MemeChoiceViewModel.Mode)
                or nameof(MemeChoiceViewModel.Placement) or nameof(MemeChoiceViewModel.ClipInSec)
                or nameof(MemeChoiceViewModel.LengthSec) or nameof(MemeChoiceViewModel.Corner)
                or nameof(MemeChoiceViewModel.Size) or nameof(MemeChoiceViewModel.PosX) or nameof(MemeChoiceViewModel.PosY)
                or nameof(MemeChoiceViewModel.PlaySound) or nameof(MemeChoiceViewModel.MusicContinues)
                or nameof(MemeChoiceViewModel.Animation) or nameof(MemeChoiceViewModel.Transition)) InvalidateResultPreview();
        };
        Closing += (_, e) =>
        {
            if (_previewCloseReady || (_resultView == null && _resultTask == null)) return;
            e.Cancel = true;
            if (!_previewClosePending) { _previewClosePending = true; IsEnabled = false; _ = CloseResultPreviewAsync(); }
        };
    }
    private void InvalidateResultPreview()
    {
        _resultValid = false;
        Choice.IsResultPreview = false;
        _resultCancellation?.Cancel();
        if (_resultView != null)
        {
            _resultView.IsVisible = false;
            if (_resultView.IpcClient is { } player) _ = player.SetPropertyAsync("pause", "yes");
        }
        if (_placementPreview != null) _placementPreview.IsVisible = true;
    }
    private async Task PreviewResultAsync()
    {
        if ((_context?.ComposePreview == null && _context?.RenderPreview == null) || _selected == null || !_landingOk || _resultPanel == null)
        {
            ShowStatus("A video must be loaded to preview the result here.");
            return;
        }
        _resultCancellation?.Dispose();
        _resultCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = _resultCancellation.Token;
        if (_resultButton != null) _resultButton.Content = "Cancel preview";
        try
        {
            if (_resultValid && _resultPath != null && File.Exists(_resultPath) && _resultView?.IpcClient is { } ready)
            {
                await ready.LoadFileAsync(_resultPath, _resultStart);
                Choice.IsPlaying = true;
                await ready.SetPropertyAsync("pause", "no");
                ShowStatus("Playing the prepared preview.");
                return;
            }
            if (_resultView?.IpcClient is { } old) await old.SetPropertyAsync("pause", "yes");
            var pick = BuildResultForPreview();
            if (pick == null) return;
            var landing = _context.Locate(Choice);
            var candidate = pick.ToPlacement(landing.SourceSeconds, pick.DurationSec ?? 4) with { Id = "meme-preview" };
            ShowStatus("Preparing your video with this meme, sound and effects...");
            _resultDirectory ??= Path.Combine(ApplicationPaths.CreateDefault().TempDirectory, "meme-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_resultDirectory);
            Action<int> progress = percent => Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) ShowStatus($"Preparing preview: {percent}%"); });
            Models.ExportResult result;
            if (_context.RenderPreview != null) result = await _context.RenderPreview(candidate, _resultDirectory, ct, progress);
            else
            {
                var payload = await _context.ComposePreview!(candidate, ct);
                ct.ThrowIfCancellationRequested();
                payload = payload with { OutputDirectory = _resultDirectory, OutputBaseName = "preview-" + Guid.NewGuid().ToString("N"), TargetMbOverride = null };
                result = await new Services.MainMediaController().ExecuteExportAsync(payload, ct, progress, (_, _, _) => { });
            }
            ct.ThrowIfCancellationRequested();
            if (!result.Success || result.OutputPath == null) { ShowStatus(result.ErrorMessage ?? "The preview could not be prepared. Your edit has not changed."); return; }
            if (_resultView == null)
            {
                _resultView = new MpvVideoView();
                _resultView.PointerPressed += (_, _) =>
                {
                    if (!Choice.IsCorner) return;
                    InvalidateResultPreview();
                    ShowStatus("Adjust the meme in the placement preview, then press Preview result.");
                };
                _resultPanel.Children.Add(_resultView);
                await _resultView.StartMpvProcessAsync(BinaryPathResolver.Resolve("mpv.exe", "backend", "binaries"));
            }
            ct.ThrowIfCancellationRequested();
            if (_resultView.IpcClient is not { } player) throw new InvalidOperationException("The preview player did not start.");
            _resultView.IsVisible = true;
            Choice.IsResultPreview = true;
            if (_placementPreview != null) _placementPreview.IsVisible = false;
            _resultPath = result.OutputPath;
            _resultStart = Math.Max(0, landing.OutputSeconds - 1);
            await player.LoadFileAsync(_resultPath, _resultStart);
            _resultValid = true;
            await player.ApplyPreviewGainAsync();
            Choice.IsPlaying = true;
            await player.SetPropertyAsync("pause", "no");
            ShowStatus("Playing your video with this meme. Change a setting, then press Preview result to hear the new result.");
        }
        catch (OperationCanceledException) { if (!_cts.IsCancellationRequested) ShowStatus("Preview cancelled. Your edit has not changed."); }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Result preview failed: {ex.Message}");
            ShowStatus("The preview could not be played. Your edit has not changed.");
        }
        finally
        {
            if (_resultButton != null) _resultButton.Content = "Preview result";
        }
    }
    private MemePickResult? BuildResultForPreview()
    {
        var old = Result;
        try { Result = _selected?.Item; return BuildResult(); }
        finally { Result = old; }
    }
    private async Task CloseResultPreviewAsync()
    {
        try
        {
            _resultCancellation?.Cancel();
            if (_resultTask != null) await _resultTask;
            if (_resultView != null)
            {
                var shutdown = await _resultView.ShutdownAsync();
                if (!shutdown.Succeeded)
                {
                    ShowStatus(shutdown.Reason ?? "The preview is still closing. Try closing this window again.");
                    return;
                }
            }
            _previewCloseReady = true;
            if (_resultDirectory != null && Directory.Exists(_resultDirectory))
            {
                string full = Path.GetFullPath(_resultDirectory);
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ApplicationPaths.CreateDefault().TempDirectory));
                if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(full).StartsWith("meme-preview-", StringComparison.Ordinal))
                    throw new IOException("The preview folder is outside its temporary workspace.");
                await Task.Run(() => SafeDirectoryCleanup.DeleteTree(full));
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        finally
        {
            if (_previewCloseReady)
            {
                _resultCancellation?.Dispose();
                Close();
            }
            else
            {
                _previewClosePending = false;
                IsEnabled = true;
            }
        }
    }
}
