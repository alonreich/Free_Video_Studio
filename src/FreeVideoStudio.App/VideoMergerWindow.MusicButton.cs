using System;
using System.Linq;
using System.Threading.Tasks;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private async Task OnAddMusicClickedAsync()
    {
        RuntimeLog.Info("UI", "User clicked Add Music in Video Merger.");
        if (VideoQueue.Count < 1)
        {
            UpdateQueueState();
            SetQueueStatus("Add at least one video before adding music.", true);
            return;
        }

        MusicWizardResult? resume = null;
        if (_musicResult != null && !_musicIsStale)
        {
            var choice = await ConfirmDialogWindow.AskEditOrRemoveAsync(
                this,
                "Edit Background Music?",
                "Your merged video already has background music.\n\n" +
                "EDIT MUSIC reopens the music screen with your songs, where each one plays, and your volumes still there.\n" +
                "REMOVE takes the music off the video.\n" +
                "CANCEL changes nothing.",
                editText: "EDIT MUSIC");

            if (choice == ConfirmDialogWindow.EditOrRemoveChoice.Cancelled) return;
            if (choice == ConfirmDialogWindow.EditOrRemoveChoice.Remove)
            {
                RemoveMergerMusic();
                return;
            }
            resume = _musicResult;
        }
        else if (_musicResult != null && _musicIsStale)
        {
            resume = _musicResult;
        }

        await EnsureTimelineReadyAsync();
        var wizard = new MusicWizardWindow(VideoQueue.ToList(), MusicTimelineTotalSec(), _baseSpeed)
        {
            MergerClipWindows = MusicClipWindows(),
            MergerComposite = EdlDescribesQueue() ? FreeVideoStudio.Core.Media.CompositeTimeline.Build(CurrentEdl) : null,
            InitialState = resume,
        };
        await wizard.ShowDialog(this);

        if (wizard.Result == null) return;

        _musicResult = wizard.Result;
        OnMusicPlaced();
        _musicQueueSignature = string.Join("|", VideoQueue);
        _musicIsStale = false;
        UpdateQueueState();
        UpdateEstimatedSize();
        RuntimeLog.Info("MERGER", $"Music set: {DescribeMusicForButton(_musicResult)}.");
    }

    private void RemoveMergerMusic()
    {
        _musicResult = null;
        _musicTimeline = null;
        _musicIsStale = false;
        _musicQueueSignature = "";
        _ = StopMergerMusicPreview();
        InvalidateMergedTimelineDrawing();
        NoteEdlChanged();
        UpdateQueueState();
        UpdateEstimatedSize();
        Controls.FloatingNotice.Show(this, "Music removed");
        RuntimeLog.Info("UI", "User removed the merger's background music via the EDIT/REMOVE prompt.");
    }

    private static string DescribeMusicForButton(MusicWizardResult r)
    {
        int count = r.OrderedPaths().Count;
        return count <= 1 ? "1 SONG" : $"{count} SONGS";
    }

    private static string MusicButtonTooltip(MusicWizardResult r)
    {
        var names = r.OrderedPaths().Select(System.IO.Path.GetFileNameWithoutExtension).ToList();
        string list = string.Join(Environment.NewLine, names.Select((n, i) => $"{i + 1}. {n}"));
        return "Music on this video:" + Environment.NewLine + list + Environment.NewLine + Environment.NewLine +
               "Click to change the songs or where they play, or to remove the music.";
    }
}

public partial class VideoMergerWindow
{
    private void DrawMergedMusicSongs(Avalonia.Controls.Canvas markers, double w, double h, double total, MusicWizardResult music)
    {
        double speed = _baseSpeed > 0.01 ? _baseSpeed : 1.0;
        var plan = GetMergerMusicPlan(speed);
        double mergedStart = Math.Clamp(music.TimelineStartSeconds, 0, total);
        double mergedEnd = Math.Clamp(music.TimelineEndSeconds > music.TimelineStartSeconds ? music.TimelineEndSeconds : total, mergedStart, total);
        if (plan.Count == 0 || mergedEnd - mergedStart < 0.01) return;

        double outStart = plan[0].OutputStartSec;
        double bedSec = (_plan?.OutputSecAt(music.TimelineEndSeconds) ?? music.TimelineEndSeconds / speed) - outStart;
        if (bedSec <= 0.01) bedSec = Math.Max(0.01, FreeVideoStudio.Core.Media.MusicBedPlan.EndSec(plan, outStart) - outStart);
        double ToX(double outputSec)
        {
            double frac = Math.Clamp((outputSec - outStart) / bedSec, 0, 1);
            return (mergedStart + frac * (mergedEnd - mergedStart)) / total * w;
        }

        const double bandH = 14;
        double top = Math.Max(0, h - bandH);
        var paths = music.OrderedPaths();
        var brushes = new[]
        {
            Infrastructure.ThemeResources.Brush(markers, "AppAccentBrush", Avalonia.Media.Brushes.MediumPurple),
            Infrastructure.ThemeResources.Brush(markers, "AppInfoBrush", Avalonia.Media.Brushes.DeepSkyBlue),
        };

        for (int i = 0; i < plan.Count; i++)
        {
            var seg = plan[i];
            double x0 = ToX(seg.OutputStartSec);
            double x1 = ToX(seg.OutputEndSec);
            if (x1 - x0 < 1) continue;
            int songIndex = Math.Max(0, paths.FindIndex(p => string.Equals(p, seg.Path, StringComparison.OrdinalIgnoreCase)));
            var block = new Avalonia.Controls.Border
            {
                Width = Math.Max(2, x1 - x0 - 1),
                Height = bandH,
                Background = brushes[songIndex % brushes.Length],
                Opacity = 0.9,
                CornerRadius = new Avalonia.CornerRadius(3),
                IsHitTestVisible = false,
                ClipToBounds = true,
                Child = x1 - x0 > 40
                    ? new Avalonia.Controls.TextBlock
                    {
                        Text = $"♪ {songIndex + 1}. {System.IO.Path.GetFileNameWithoutExtension(seg.Path)}",
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(8),
                        Foreground = Infrastructure.ThemeResources.Brush(markers, "AppOnAccentTextBrush", Avalonia.Media.Brushes.White),
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        Margin = new Avalonia.Thickness(4, 0),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    }
                    : null,
            };
            Avalonia.Controls.Canvas.SetLeft(block, x0);
            Avalonia.Controls.Canvas.SetTop(block, top);
            markers.Children.Add(block);
        }

        double coveredX = ToX(FreeVideoStudio.Core.Media.MusicBedPlan.EndSec(plan, outStart));
        double endX = mergedEnd / total * w;
        if (endX - coveredX > 2)
        {
            var gap = new Avalonia.Controls.Border
            {
                Width = endX - coveredX,
                Height = bandH,
                Background = Infrastructure.ThemeResources.Brush(markers, "AppDangerBrush", Avalonia.Media.Brushes.IndianRed),
                Opacity = 0.55,
                CornerRadius = new Avalonia.CornerRadius(3),
                IsHitTestVisible = false,
                Child = endX - coveredX > 60
                    ? new Avalonia.Controls.TextBlock
                    {
                        Text = "no music",
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(8),
                        Foreground = Avalonia.Media.Brushes.White,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    }
                    : null,
            };
            Avalonia.Controls.Canvas.SetLeft(gap, coveredX);
            Avalonia.Controls.Canvas.SetTop(gap, top);
            markers.Children.Add(gap);
        }
    }
}
