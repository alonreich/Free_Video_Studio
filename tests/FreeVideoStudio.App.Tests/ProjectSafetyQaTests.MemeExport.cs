using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed partial class ProjectSafetyQaTests
{
    [AvaloniaTheory]
    [InlineData(MemePresentationMode.InlineFullScreen, 5.1)]
    [InlineData(MemePresentationMode.CornerOverlay, 4.1)]
    public async Task MainAddMeme_SpeedEditorChange_SaveReopen_FinalExportContainsEditedPictureAndSound(MemePresentationMode mode, double expectedDuration)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string source = Path.Combine(_root, "gameplay.mp4");
        string library = Path.Combine(_root, "gallery");
        Directory.CreateDirectory(library);
        string meme = Path.Combine(library, "reaction.mp4");
        async Task Ffmpeg(params string[] args)
        {
            var start = new ProcessStartInfo(Path.Combine(RepoRoot.Path, "binaries", "ffmpeg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-n" }.Concat(args)) start.ArgumentList.Add(arg);
            var run = await AsyncProcessRunner.RunAsync(start, cancellationToken: timeout.Token);
            Assert.True(run.ExitCode == 0, run.StandardError);
        }
        Task MakeVideo(string picture, string sound, string path) => Ffmpeg("-f", "lavfi", "-i", picture, "-f", "lavfi", "-i", sound,
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", path);
        await MakeVideo("color=blue:s=160x90:r=60:d=4", "sine=frequency=440:sample_rate=48000:duration=4", source);
        await MakeVideo("color=green:s=160x90:r=60:d=4,drawbox=w=iw:h=ih:color=red:t=fill:enable='gte(t,2)'",
            @"aevalsrc=if(lt(t\,2)\,0.1*sin(2*PI*300*t)\,0.1*sin(2*PI*900*t)):s=48000:d=4", meme);
        string previousFolder = SettingsManager.Instance.MemeDirectoryPath;
        SettingsManager.Update(s => s.MemeDirectoryPath = library);
        var editor = await EditorWithVideoAsync(source, 4);
        static void Click(Button button)
        {
            Assert.True(button.IsEffectivelyEnabled, $"Disabled button: {button.Content}");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        static Task Wait(Func<bool> condition, string action) => LiveCaptureDriver.PumpUntil(condition, action);
        MainWindow? reopened = null;
        static void CloseWindow(MainWindow window)
        {
            typeof(MainWindow).GetField("_isSafeToClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close();
        }
        try
        {
            var main = editor.Window;
            main.Show();
            Call(main, "ApplyMainSpeedPreset", 1.0);
            bool corner = mode == MemePresentationMode.CornerOverlay;
            if (corner) main.FindControl<ToggleSwitch>("PortraitModeCheckbox")!.IsChecked = false;
            Click(main.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Add meme")));
            await Wait(() => main.OwnedWindows.OfType<MemePickerWindow>().Any(), "main Add meme picker");
            var add = main.OwnedWindows.OfType<MemePickerWindow>().Single();
            var gallery = add.FindControl<ListBox>("MemeList")!;
            MemePickerRow? Tile() => gallery.Items.OfType<MemeGalleryTiles>().SelectMany(row => row.Tiles).SingleOrDefault(t => t.Item.FullPath == meme);
            await Wait(() => Tile() != null, "test video in the gallery");
            Tile()!.SelectCommand.Execute(null);
            add.Choice.FullScreenCommand.Execute(null);
            add.Choice.AtStartCommand.Execute(null);
            add.Choice.PartEnd = 1;
            add.Choice.PlaySound = false;
            Click(add.FindControl<Button>("UseBtn")!);
            var placements = editor.Vm.Timeline.MemePlacements;
            await Wait(() => placements.Count == 1, "main placement committed");
            var initial = placements.Single();
            Assert.False(initial.PlaySound);
            Assert.Equal(0, initial.ClipInSec);
            Call(main, "OpenSpeedEditorAtMeme", initial.Id);
            await Wait(() => main.OwnedWindows.OfType<GranularSpeedEditorWindow>().Any(), "Speed Editor");
            var speed = main.OwnedWindows.OfType<GranularSpeedEditorWindow>().Single();
            Button? Change() => speed.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "Change") && b.IsEffectivelyVisible);
            await Wait(() => Change() != null, "selected meme Change action");
            Click(Change()!);
            await Wait(() => speed.OwnedWindows.OfType<MemePickerWindow>().Any(), "Speed Editor change picker");
            var change = speed.OwnedWindows.OfType<MemePickerWindow>().Single();
            if (corner)
            {
                change.Choice.CornerCommand.Execute(null);
                change.Choice.TopLeftCommand.Execute(null);
                change.Choice.SmallCommand.Execute(null);
            }
            change.Choice.PartEnd = 3;
            change.Choice.SetPartStart(2);
            change.Choice.PlaySound = true;
            Click(change.FindControl<Button>("UseBtn")!);
            await Wait(() => speed.ResultMemes.Single().ClipInSec == 2, "updated Speed Editor selection");
            Click(speed.FindControl<Button>("AcceptGranularBtn")!);
            await Wait(() => placements.SingleOrDefault()?.ClipInSec == 2 && !speed.IsVisible, "Speed Editor applied to main project");
            string project = Path.Combine(_root, "edited.fvsproj");
            _picker.NextSave = project;
            Assert.True(await Session(main).SaveAsAsync(), string.Join(" | ", _notifier.Notices));
            reopened = new MainWindow(_ => { });
            Assert.True(await Session(reopened).OpenAsync(project), string.Join(" | ", _notifier.Notices));
            var payload = await (Task<ExportPayload>)Call(reopened, "ComposeExportPayloadAsync", _root, null, 2, null, null)!;
            var restored = Assert.Single(payload.MemePlacements!);
            Assert.Equal(initial.Id, restored.Id);
            Assert.Equal(2, restored.ClipInSec);
            Assert.Equal(1, restored.DurationSec);
            Assert.True(restored.PlaySound);
            Assert.Equal(mode, restored.Mode);
            if (corner) Assert.Equal(MemeOverlayCorner.TopLeft, restored.Corner);
            var result = await new MainMediaController().ExecuteExportAsync(payload, timeout.Token, _ => { }, (_, _, _) => { });
            Assert.True(result.Success, result.ErrorMessage);
            string output = Assert.IsType<string>(result.OutputPath);
            var prober = new MediaProber(Path.Combine(RepoRoot.Path, "binaries", "ffprobe.exe"), output);
            Assert.InRange(await prober.GetDurationAsync(timeout.Token), expectedDuration - 0.04, expectedDuration + 0.04);
            async Task CheckInterval(string seconds, bool duringMeme, string label)
            {
                string rgb = Path.Combine(_root, label + ".rgb");
                await Ffmpeg("-ss", seconds, "-i", output, "-frames:v", "1", "-vf", "scale=160:90", "-pix_fmt", "rgb24", "-f", "rawvideo", rgb);
                byte[] frame = await File.ReadAllBytesAsync(rgb, timeout.Token);
                Assert.Equal(160 * 90 * 3, frame.Length);
                void AssertColor(int x, int y, int channel)
                {
                    int p = (y * 160 + x) * 3;
                    Assert.True(frame[p + channel] > frame[p + (channel + 1) % 3] + 60 && frame[p + channel] > frame[p + (channel + 2) % 3] + 60,
                        $"{label} at ({x},{y}): RGB ({frame[p]}, {frame[p + 1]}, {frame[p + 2]})");
                }
                AssertColor(80, 45, duringMeme && !corner ? 0 : 2);
                if (corner)
                {
                    AssertColor(15, 9, duringMeme ? 0 : 2);
                    AssertColor(145, 9, 2);
                    AssertColor(15, 81, 2);
                    AssertColor(145, 81, 2);
                }
                string pcm = Path.Combine(_root, label + ".s16");
                await Ffmpeg("-ss", seconds, "-i", output, "-t", "0.25", "-vn", "-ar", "48000", "-ac", "1", "-f", "s16le", pcm);
                byte[] bytes = await File.ReadAllBytesAsync(pcm, timeout.Token);
                var samples = new short[bytes.Length / sizeof(short)];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                Assert.InRange(samples.Length, 11998, 12002);
                double ToneAmplitude(int frequency)
                {
                    double real = 0, imaginary = 0;
                    for (int i = 0; i < samples.Length; i++)
                    {
                        double phase = 2 * Math.PI * frequency * i / 48000;
                        real += samples[i] * Math.Cos(phase);
                        imaginary += samples[i] * Math.Sin(phase);
                    }
                    return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
                }
                double gameplay = ToneAmplitude(440), edited = ToneAmplitude(900), discarded = ToneAmplitude(300);
                Assert.True(duringMeme ? edited > 40 : edited < 40, $"{label}: edited 900 Hz amplitude {edited}");
                Assert.True(corner || !duringMeme ? gameplay > 40 : gameplay < 40, $"{label}: gameplay 440 Hz amplitude {gameplay}");
                Assert.True(discarded < 40, $"{label}: discarded 300 Hz amplitude {discarded}");
            }
            await CheckInterval("0.45", true, "edited-meme");
            await CheckInterval("2.0", false, "gameplay");
        }
        finally
        {
            SettingsManager.Update(s => s.MemeDirectoryPath = previousFolder);
            if (reopened != null) CloseWindow(reopened);
            CloseWindow(editor.Window);
            await SettleAsync(editor);
        }
    }
}
