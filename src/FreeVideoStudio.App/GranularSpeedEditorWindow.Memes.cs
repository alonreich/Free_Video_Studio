using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Threading;
using System;
using static FreeVideoStudio.App.Infrastructure.GranularEditorVisuals;

namespace FreeVideoStudio.App;

public partial class GranularSpeedEditorWindow
{
    private Infrastructure.CornerMemeOverlayPresenter? _cornerMemes;
    private string _cornerMemeKey = "";
    private readonly Services.EditorTimelineCache _gameplayTimelineCache = new();

    private OutputTimeline GameplayTimeline()
        => _gameplayTimelineCache.Get(Math.Max(0.001, GetDuration()) * 1000, _edit.Segments, _edit.Cuts, Array.Empty<MemePlacement>(),
            speed: EditorBaseSpeed,
            freezeStartMs: _edit.FreezeTimeMs >= 0 ? _edit.FreezeTimeMs - _edit.TrimStartMs : -1,
            freezeDurationSeconds: _edit.FreezeDurationS);

    private async void OnAddMemeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { await AddMemeAsync(); }
        catch (Exception ex) { RuntimeLog.Fail("MEME", $"Adding a meme failed: {ex.Message}"); }
    }

    private WrapPanel? _selectedMemeActions;
    private readonly TextBlock _selectedMemeTitle = new() { MaxWidth = 120, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Image _selectedMemePicture = new() { Width = 32, Height = 24, Stretch = Avalonia.Media.Stretch.Uniform };
    private Infrastructure.OwnedLruCache<Avalonia.Media.Imaging.Bitmap>.Lease? _selectedMemeLease;
    private string? _selectedMemePath;
    private int _selectedMemeGeneration;
    private void InstallSelectedMemeActions()
    {
        if (_removeMemeBtn?.Parent is not StackPanel parent) return;
        var top = new WrapPanel();
        foreach (var child in parent.Children.ToArray())
        {
            parent.Children.Remove(child);
            if (!ReferenceEquals(child, _removeMemeBtn)) top.Children.Add(child);
        }
        parent.Orientation = Avalonia.Layout.Orientation.Vertical;
        parent.Children.Add(top);
        _selectedMemeActions = new WrapPanel { IsVisible = false };
        _selectedMemeActions.Children.Add(_selectedMemePicture);
        _selectedMemeActions.Children.Add(_selectedMemeTitle);
        var preview = new Button { Content = "Preview", Margin = new Thickness(4, 0) };
        preview.Classes.Add("Secondary");
        preview.Click += (_, _) => PreviewSelectedMeme();
        var change = new Button { Content = "Change", Margin = new Thickness(4, 0) };
        change.Classes.Add("Secondary");
        change.Click += (_, _) => { if (_edit.SelectedMemeId is { } id) _ = EditMemeAsync(id); };
        _selectedMemeActions.Children.Add(preview);
        _selectedMemeActions.Children.Add(change);
        _removeMemeBtn.Content = "Remove";
        _removeMemeBtn.MinWidth = 64;
        _selectedMemeActions.Children.Add(_removeMemeBtn);
        parent.Children.Add(_selectedMemeActions);
        Closed += (_, _) =>
        {
            _selectedMemePath = null;
            _selectedMemePicture.Source = null;
            _selectedMemeLease?.Dispose();
            _selectedMemeLease = null;
        };
    }
    private void UpdateSelectedMemeActions()
    {
        var meme = _edit.Memes.FirstOrDefault(m => m.Id == _edit.SelectedMemeId);
        if (_selectedMemeActions != null) _selectedMemeActions.IsVisible = meme != null;
        _selectedMemeTitle.Text = meme == null ? "" : Controls.MemePickerRow.TitleFor(Path.GetFileName(meme.FilePath));
        if (_selectedMemePath == meme?.FilePath) return;
        _selectedMemePath = meme?.FilePath;
        _selectedMemePicture.Source = null;
        _selectedMemeLease?.Dispose();
        _selectedMemeLease = null;
        int generation = ++_selectedMemeGeneration;
        if (meme != null) _ = LoadSelectedMemeThumbnailAsync(meme.FilePath, generation);
    }
    private async Task LoadSelectedMemeThumbnailAsync(string path, int generation)
    {
        try
        {
            var lease = await Task.Run(() => Infrastructure.MemeThumbnailCache.AcquireAsync(path, Infrastructure.CornerMemeFrames.IsStillImage(path), CancellationToken.None));
            if (_selectedMemePath != path || _editorClosing || generation != _selectedMemeGeneration) { lease?.Dispose(); return; }
            _selectedMemeLease = lease;
            _selectedMemePicture.Source = lease?.Value;
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
    private void PreviewSelectedMeme()
    {
        if (_edit.SelectedMemeId is not { } id) return;
        SelectAndShowMeme(id);
        if (_videoHost?.IpcClient?.IsPaused == true) TogglePlayPause();
    }
    private Controls.MemePickerContext CreateMemePickerContext(MemePlacement? existing = null)
    {
        double here = existing?.AtSourceSecRelative ?? Math.Max(0, _playheadMs / 1000.0);
        return new Controls.MemePickerContext(choice =>
        {
            double requested = choice.Placement switch
            {
                ViewModels.MemePlacementPoint.Start => 0,
                ViewModels.MemePlacementPoint.End => choice.IsCorner
                    ? GameplayTimeline().OutputToSourceRelative(Math.Max(0, GameplayTimeline().TotalOutputSeconds - choice.LengthSeconds))
                    : GetDuration(),
                _ => here,
            };
            var ruler = existing == null ? OutTimeline() : _edit.TimelineExcludingMeme(existing.Id, GetDuration() * 1000);
            var ok = _edit.ResolveNewMemeAnchor(ruler, requested, choice.Mode, out double at, existing?.Id);
            if (existing != null && ok.Ok && choice.IsFullScreen && !_edit.MemeSeparationIsSafe(at, existing.Id, out var clash))
                return new Controls.MemeLanding(false, at, 0, clash!);
            if (!ok.Ok) return new Controls.MemeLanding(false, at, 0, ok.Error!);
            string? mergeNote = null;
            if (_mergeSource != null) at = _mergeSource.ResolveMemeAt(at, choice.IsCorner, out _, out mergeNote);
            double output = ruler.SourceToOutput(at);
            if (choice.IsCorner && GameplayTimeline().TotalOutputSeconds - GameplayTimeline().SourceToOutput(at) <= 0.001)
                return new Controls.MemeLanding(false, at, output, "No gameplay remains here for a corner meme. Choose End, or move the video cursor earlier.");
            string note = Math.Abs(at - requested) > 0.01 ? $" · moved {Math.Abs(at - requested):0.0}s to the nearest allowed spot" : "";
            return new Controls.MemeLanding(true, at, output, $"Starts at {output:0.0}s{note}{(mergeNote == null ? "" : " · " + mergeNote)}");
        }, _edit.IsMergeMode ? null : async (candidate, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var segments = _edit.ResultSegments.ToList();
            if (_edit.FreezeTimeMs >= 0) segments.Add(new SpeedSegment(_edit.FreezeTimeMs, _edit.FreezeTimeMs + _edit.FreezeDurationS * 1000, 0));
            var memes = _edit.Memes.Where(m => m.Id != existing?.Id).ToList();
            memes.Add(candidate);
            if (Owner is MainWindow main)
                return await main.ComposeEditorMemePreviewAsync(segments, _edit.ResultCuts.ToList(), memes, _edit.CaptionTrack, _edit.BaseSpeed);
            return new Models.ExportPayload
            {
                InputPath = _edit.VideoPath, TrimStartMs = _edit.TrimStartMs, TrimEndMs = _edit.TrimEndMs,
                LoadedVideoDurationMs = _edit.TrimEndMs, BaseSpeed = _edit.BaseSpeed, IsMobileFormat = _edit.IsMobileFormat,
                SpeedSegments = segments, Cuts = _edit.ResultCuts.ToList(), MemePlacements = memes, QualityLevel = 2,
                CaptionTrack = _edit.CaptionTrack,
            };
        }, _edit.IsMergeMode ? async (candidate, directory, ct, progress) =>
        {
            if (Owner is not VideoMergerWindow merger || _mergeSource == null)
                return new Models.ExportResult { ErrorMessage = "The merged video is not available for preview." };
            var memes = _edit.Memes.Where(m => m.Id != existing?.Id).ToList();
            memes.Add(candidate);
            var state = new MergeEditorState(_edit.Segments.ToList(), _edit.FreezeTimeMs, _edit.FreezeDurationS,
                _edit.Cuts.ToList(), memes, _edit.BaseSpeed, _edit.CaptionTrack);
            return await merger.RenderEditorMemePreviewAsync(_mergeSource, state, _edit.IsMobileFormat, directory, ct, progress);
        } : null);
    }
    private async Task AddMemeAsync()
    {
        if (string.IsNullOrWhiteSpace(_edit.VideoPath)) return;

        if (_videoHost?.IpcClient is { } player) await player.SetPropertyAsync("pause", "yes");
        var pick = await Controls.MemePickerWindow.PickAsync(this, AvailableMemes, _edit.IsMobileFormat,
            latest => AvailableMemes = latest, context: CreateMemePickerContext());
        if (pick == null) return;
        var picked = pick.Item;

        if (!System.IO.File.Exists(picked.FullPath))
        {
            NotifyError("That meme file is no longer on disk.");
            return;
        }

        double rawSourceRelSec = pick.AnchorSec ?? Math.Max(0, _playheadMs / 1000.0);
        bool corner = pick.Mode == MemePresentationMode.CornerOverlay;

        var anchor = _edit.ResolveNewMemeAnchor(OutTimeline(), rawSourceRelSec, pick.Mode, out double at);
        if (!anchor.Ok)
        {
            NotifyError(anchor.Error!);
            return;
        }

        double duration = pick.DurationSec is double d && d > 0.01 ? d : await ResolveMemeDurationAsync(picked);
        if (duration <= 0.01)
        {
            NotifyError("That meme's length could not be read, so it was not added.");
            return;
        }

        var placed = _edit.AddMeme(pick.ToPlacement(at, duration));
        string id = placed.Id;
        string? mergeNote = ApplyMergeMemeRules(id);
        placed = _edit.Memes.First(m => m.Id == id);

        bool moved = Math.Abs(placed.AtSourceSecRelative - rawSourceRelSec) > 0.01;
        RuntimeLog.Info("MEME",
            $"Added '{System.IO.Path.GetFileName(picked.FullPath)}' as {(corner ? $"a {pick.Size} corner overlay ({pick.Corner}, sound {(pick.PlaySound ? "on" : "off")}, {pick.Animation})" : $"a full-screen cutaway ({pick.Transition})")} " +
            $"at {placed.AtSourceSecRelative:0.###}s source-relative ({duration:0.###}s long, from {pick.ClipInSec:0.###}s of the file)" +
            $"{(moved ? $"; slid {placed.AtSourceSecRelative - rawSourceRelSec:0.###}s" : "")}.");

        InvalidateMemeTimelines();
        _gameplayTimelineCache.Clear();
        RedrawTimeline();
        UpdateMemeButtonsState();
        if (!corner) ShowMemeLandingFrame(id);
        await RefreshMemePreviewAsync("Fitting the meme into your video...");

        if (mergeNote != null) Notify(mergeNote);
        else Notify(corner
            ? "Corner meme added — the video length stays the same"
            : moved
                ? $"Meme added — it moved {Math.Abs(placed.AtSourceSecRelative - rawSourceRelSec):0.0}s to the edge of a block it cannot interrupt."
                : $"Meme added — the video is {duration:0.0}s longer");
    }

    private async Task EditMemeAsync(string memeId)
    {
        try
        {
            int idx = _edit.Memes.FindIndex(m => m.Id == memeId);
            if (idx < 0) return;
            var old = _edit.Memes[idx];

            if (_videoHost?.IpcClient is { } player) await player.SetPropertyAsync("pause", "yes");
            var pick = await Controls.MemePickerWindow.PickAsync(this, AvailableMemes, _edit.IsMobileFormat,
                latest => AvailableMemes = latest, existing: old, context: CreateMemePickerContext(old));
            if (pick == null) return;
            idx = _edit.Memes.FindIndex(m => m.Id == memeId);
            if (idx < 0) return;

            double duration = pick.DurationSec is double d && d > 0.01 ? d : old.DurationSec;
            if (!string.Equals(pick.Item.FullPath, old.FilePath, StringComparison.OrdinalIgnoreCase) && !(pick.DurationSec > 0.01))
            {
                duration = await ResolveMemeDurationAsync(pick.Item);
                if (duration <= 0.01) { NotifyError("That meme's length could not be read, so nothing changed."); return; }
            }

            var changed = _edit.UpdateMeme(memeId, pick.ToPlacement(pick.AnchorSec ?? old.AtSourceSecRelative, duration),
                Math.Max(0.001, GetDuration()) * 1000, out var before, out var updated, out double movedBy);
            if (!changed.Ok) { NotifyError(changed.Error!); return; }
            if (updated == null || updated == before) return;
            string? mergeNote = ApplyMergeMemeRules(memeId);

            double delta = updated.OutputDurationSec - old.OutputDurationSec;
            RuntimeLog.Info("MEME",
                $"Changed '{System.IO.Path.GetFileName(old.FilePath)}': {old.Mode}/{old.Corner}/{old.Size}/sound {old.PlaySound} -> " +
                $"{updated.Mode}/{updated.Corner}/{updated.Size}/sound {updated.PlaySound}; video length {delta:+0.000;-0.000;0}s; moved {movedBy:+0.000;-0.000;0}s.");

            InvalidateMemeTimelines();
            _gameplayTimelineCache.Clear();
            RedrawTimeline();
            UpdateMemeButtonsState();
            await RefreshMemePreviewAsync("Re-timing your video...");

            string what = Math.Abs(delta) < 0.001 ? "Meme updated"
                : delta < 0 ? $"Meme now plays in a corner — the video is {-delta:0.0}s shorter"
                : $"Meme now plays full screen — the video is {delta:0.0}s longer";
            if (Math.Abs(movedBy) > 0.01)
                what += $". It moved {Math.Abs(movedBy):0.0}s {(movedBy > 0 ? "later" : "earlier")}, off a speed block it cannot interrupt";
            Notify(mergeNote ?? what);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Changing a meme failed: {ex.Message}");
        }
    }

    private string? ApplyMergeMemeRules(string memeId)
    {
        var src = _mergeSource;
        if (src == null) return null;
        int idx = _edit.Memes.FindIndex(m => m.Id == memeId);
        if (idx < 0) return null;
        var m = _edit.Memes[idx];
        if (m.IsCornerOverlay) return null;

        double at = src.ResolveMemeAt(m.AtSourceSecRelative, corner: false, out _, out string? note);
        if (note == null || Math.Abs(at - m.AtSourceSecRelative) < 0.0005) return null;
        if (!_edit.MemeSeparationIsSafe(at, memeId, out string? clash, BaseTimeline()))
        {
            RuntimeLog.Info("MEME", $"Merge rule wanted to move '{System.IO.Path.GetFileName(m.FilePath)}' but {clash}");
            return $"{note} {clash}";
        }
        _edit.Memes[idx] = m with { AtSourceSecRelative = at };
        InvalidateMemeTimelines();
        RuntimeLog.Info("MEME", $"Merge rule: '{System.IO.Path.GetFileName(m.FilePath)}' {m.AtSourceSecRelative:0.###}s -> {at:0.###}s. {note}");
        return note;
    }

    private void ResnapMemesAfterBlockEdit()
    {
        var moved = _edit.ResnapMemesOffBlocks();
        if (moved.Count == 0) return;
        InvalidateMemeTimelines();
        RuntimeLog.Info("MEME", $"Speed block now covers {moved.Count} full-screen meme(s); moved to the block edge: {string.Join(", ", moved)}.");
        Notify(moved.Count == 1
            ? $"Meme '{moved[0]}' moved to the edge of the speed block — a full-screen meme cannot interrupt a block"
            : $"{moved.Count} memes moved to the edge of a speed block — a full-screen meme cannot interrupt a block");
        _ = RefreshMemePreviewAsync("", stall: false);
    }

    private (double Start, double End, bool CutByClip)? CornerGameplaySpan(MemePlacement m, OutputTimeline game)
    {
        double total = game.TotalOutputSeconds;
        bool cut = false;
        if (_mergeSource != null)
        {
            _mergeSource.ResolveMemeAt(m.AtSourceSecRelative, corner: true, out double clipEndSec, out _);
            double clipEndGame = game.SourceToOutput(clipEndSec);
            double start0 = game.SourceToOutput(m.AtSourceSecRelative);
            if (clipEndGame < start0 + m.DurationSec - 0.001) cut = true;
            total = Math.Min(total, clipEndGame);
        }
        if (MemePlacement.VisibleInterval(game.SourceToOutput(m.AtSourceSecRelative), m.DurationSec, total) is not { } v) return null;
        return (v.StartSec, v.EndSec, cut);
    }

    private List<(double OutStart, double OutEnd)> CornerOutputPieces(double gameStart, double gameEnd, OutputTimeline game)
    {
        var cutaways = new List<(double At, double Dur)>();
        foreach (var fm in MemePlacement.InlineOnly(_edit.Memes))
            cutaways.Add((game.SourceToOutput(fm.AtSourceSecRelative), fm.DurationSec));
        cutaways.Sort((a, b) => a.At.CompareTo(b.At));

        double ToOut(double g, bool includeAtPoint)
        {
            double add = 0;
            foreach (var c in cutaways)
                if (includeAtPoint ? c.At <= g + 1e-6 : c.At < g - 1e-6) add += c.Dur;
            return g + add;
        }

        var pieces = new List<(double, double)>();
        double from = gameStart;
        foreach (var c in cutaways)
        {
            if (c.At <= gameStart + 1e-6 || c.At >= gameEnd - 1e-6) continue;
            pieces.Add((ToOut(from, true), ToOut(c.At, false)));
            from = c.At;
        }
        pieces.Add((ToOut(from, true), ToOut(gameEnd, false)));
        return pieces;
    }

    private void DrawCornerMemeBands(Canvas canvas, double w, double h)
    {
        var corners = MemePlacement.CornerOnly(_edit.Memes);
        if (corners.Count == 0 || w <= 0) return;
        double outDur = OutDurationSec();
        if (outDur <= 0.0001) return;

        var game = GameplayTimeline();
        var colour = Infrastructure.ThemeResources.Colour(this, "AppMemeColor", Avalonia.Media.Color.FromRgb(124, 58, 237));
        var fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(150, colour.R, colour.G, colour.B));
        var fillSelected = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, colour.R, colour.G, colour.B));
        var outline = new Avalonia.Media.SolidColorBrush(colour);
        double stripH = Math.Max(10, h * 0.4);

        foreach (var m in corners)
        {
            if (CornerGameplaySpan(m, game) is not { } span) continue;
            bool selected = _edit.SelectedMemeId == m.Id;
            string id = m.Id;
            string tip =
                $"Corner meme: {System.IO.Path.GetFileName(m.FilePath)} ({m.DurationSec:0.0}s, {(m.HasCustomPosition ? "own position" : m.Corner.ToString())}, {m.Size}, sound {(m.PlaySound ? "on" : "off")})\n" +
                "Plays over the gameplay — the video length stays the same.\n" +
                (span.CutByClip ? $"⚠ Shows only {span.End - span.Start:0.0}s: it stops where its clip ends.\n" : "") +
                "Click to select · drag to move · double-click (or Enter) to change · Delete to remove.";

            foreach (var (o0, o1) in CornerOutputPieces(span.Start, span.End, game))
            {
                double x1 = Math.Clamp(o0 / outDur * w, 0, w);
                double x2 = Math.Clamp(o1 / outDur * w, 0, w);
                double stripW = Math.Max(MemeGrabMinWidthPx, x2 - x1);

                var strip = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = selected ? fillSelected : fill,
                    Stroke = span.CutByClip ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Orange) : outline,
                    StrokeThickness = selected ? 2 : 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                    Width = stripW,
                    Height = stripH,
                    RadiusX = 3,
                    RadiusY = 3,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                };
                ToolTip.SetTip(strip, tip);
                Canvas.SetLeft(strip, Math.Min(x1, Math.Max(0, w - stripW)));
                Canvas.SetTop(strip, 2);
                AttachCornerStripInteractions(strip, id, canvas);
                canvas.Children.Add(strip);
            }
        }
    }

    private void AttachCornerStripInteractions(Control strip, string id, Canvas timelineCanvas)
    {
        strip.DoubleTapped += (_, e) => { e.Handled = true; _ = EditMemeAsync(id); };
        strip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed) return;
            int idx = _edit.Memes.FindIndex(m => m.Id == id);
            if (idx < 0) return;

            _edit.SelectedMemeId = id;
            _edit.SelectedSegmentIndex = -1;
            _isFreezeCameraSelected = false;
            UpdateDeleteButtonVisibility();
            UpdateMemeButtonsState();

            double w = Math.Max(1, timelineCanvas.Bounds.Width);
            double outDur = OutDurationSec();
            double pointerOut = Math.Clamp(e.GetPosition(timelineCanvas).X / w * outDur, 0, outDur);
            double anchorOut = OutTimeline().SourceToOutput(_edit.Memes[idx].AtSourceSecRelative);
            _memeDragGrabOffsetOutSec = pointerOut - anchorOut;
            _memeDragTimeline = OutTimeline();
            _memeDragLastX = -1;
            _draggingMemeId = id;
            _memeDragIsCorner = true;
            if (_memePreview != null) _memePreview.Suspended = true;
            _memeDragWasPlaying = _videoHost?.IpcClient != null && !_videoHost.IpcClient.IsPaused;
            if (_memeDragWasPlaying) _ = _videoHost!.IpcClient!.SetPropertyAsync("pause", "yes");
            EnsureMemeCaptureLostHook(timelineCanvas);
            e.Pointer.Capture(timelineCanvas);
            SetStatus("Moving the corner meme — release to set. It plays over the gameplay, so nothing else moves.");
            RedrawTimeline();
            e.Handled = true;
        };
    }

    private void PumpCornerMemeDrag(Avalonia.Input.PointerEventArgs e, Canvas canvas, double w)
    {
        double px = e.GetPosition(canvas).X;
        if (_memeDragLastX >= 0 && Math.Abs(px - _memeDragLastX) < 0.5) return;
        _memeDragLastX = px;

        var live = _memeDragTimeline ?? OutTimeline();
        double outDur = Math.Max(0.001, live.TotalOutputSeconds);
        double targetOut = Math.Clamp(px / w * outDur - _memeDragGrabOffsetOutSec, 0, outDur);
        double src = live.OutputToSourceRelative(targetOut);
        if (_edit.MoveMemeTo(_draggingMemeId!, src, BaseTimeline(), "meme-drag", out _, out _))
        {
            _gameplayTimelineCache.Clear();
            _cornerMemeKey = "";
            RedrawTimeline();
            ShowMemeLandingFrame(_draggingMemeId!);
        }
    }

    private readonly HashSet<Canvas> _memeCaptureHooked = new();

    private void EnsureMemeCaptureLostHook(Canvas canvas)
    {
        if (!_memeCaptureHooked.Add(canvas)) return;
        canvas.PointerCaptureLost += (_, e) => EndMemeDragCore(e.Pointer);
    }

    private double CornerGameplayClock(double playheadRelMs)
    {
        var game = GameplayTimeline();
        if (_isCurrentlyFrozen && _edit.FreezeTimeMs >= 0)
        {
            double freezeRel = Math.Max(0, (_edit.FreezeTimeMs - _edit.TrimStartMs) / 1000.0);
            double heldFor = Math.Clamp((DateTime.UtcNow - _freezeStartTime).TotalSeconds, 0, _edit.FreezeDurationS);
            return Math.Max(0, game.SourceToOutput(freezeRel) - _edit.FreezeDurationS + heldFor);
        }
        return game.SourceToOutput(playheadRelMs / 1000.0);
    }

    private void UpdateEditorCornerMemes(double playheadRelMs)
    {
        var corners = MemePlacement.CornerOnly(_edit.Memes);
        if (corners.Count == 0 || _videoHost == null) { _cornerMemes?.Hide(); return; }

        _cornerMemes ??= new Infrastructure.CornerMemeOverlayPresenter();
        _cornerMemes.Attach(_videoHost);
        var (rw, rh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (rw > 1 && rh > 1) _cornerMemes.SetFrameSize(rw - rw % 2, rh - rh % 2);

        var game = GameplayTimeline();
        var spans = new List<Infrastructure.CornerMemeSpan>(corners.Count);
        var key = new System.Text.StringBuilder();
        foreach (var m in corners)
        {
            if (CornerGameplaySpan(m, game) is not { } v) continue;
            spans.Add(new Infrastructure.CornerMemeSpan(m, v.Start, v.End));
            key.Append(m).Append('@').Append(v.Start.ToString("F3")).Append('-').Append(v.End.ToString("F3")).Append(';');
        }
        string k = key.ToString();
        if (k != _cornerMemeKey) { _cornerMemeKey = k; _cornerMemes.SetSpans(spans); }

        _cornerMemes.Update(CornerGameplayClock(playheadRelMs));
    }

    private void TeardownEditorCornerMemes()
    {
        _cornerMemes?.Dispose();
        _cornerMemes = null;
        _cornerMemeKey = "";
    }

    private void OpenMemeListMenu(Control anchor)
    {
        var menu = new ContextMenu();
        var live = OutTimeline();
        var ordered = _edit.Memes.OrderBy(m => m.AtSourceSecRelative).ToList();
        if (ordered.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No memes yet — press ADD MEME to drop one at the playhead", IsEnabled = false });
        }
        foreach (var m in ordered)
        {
            double outAt = m.IsCornerOverlay
                ? live.SourceToOutput(m.AtSourceSecRelative)
                : Math.Max(0, live.SourceToOutput(m.AtSourceSecRelative) - m.DurationSec);
            string mode = m.IsCornerOverlay ? "corner" : "full screen";
            string id = m.Id;
            var item = new MenuItem
            {
                Header = $"{FormatMs(outAt * 1000.0)}   {System.IO.Path.GetFileName(m.FilePath)}   ({mode}, {m.DurationSec:0.0}s)",
            };
            var go = new MenuItem { Header = "Go to it" };
            go.Click += (_, _) => SelectAndShowMeme(id);
            var edit = new MenuItem { Header = "Change…" };
            edit.Click += (_, _) => { SelectAndShowMeme(id); _ = EditMemeAsync(id); };
            var remove = new MenuItem { Header = "Remove" };
            remove.Click += (_, _) => { _edit.SelectedMemeId = id; RemoveSelectedMeme(); };
            item.Items.Add(go);
            item.Items.Add(edit);
            item.Items.Add(remove);
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    public void FocusMemeOnOpen(string memeId)
    {
        void Focus()
        {
            if (_editorClosing) return;
            SelectAndShowMeme(memeId);
        }
        Opened += (_, _) =>
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(Focus, TimeSpan.FromMilliseconds(400));
            Avalonia.Threading.DispatcherTimer.RunOnce(Focus, TimeSpan.FromMilliseconds(1500));
        };
    }

    private void SelectAndShowMeme(string id)
    {
        if (_edit.Memes.FindIndex(m => m.Id == id) < 0) return;
        _edit.SelectedMemeId = id;
        _edit.SelectedSegmentIndex = -1;
        _isFreezeCameraSelected = false;
        UpdateDeleteButtonVisibility();
        UpdateMemeButtonsState();
        ShowMemeLandingFrame(id);
        RedrawTimeline();
    }
}
