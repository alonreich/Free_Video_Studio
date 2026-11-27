using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Testing;
using Xunit;
using MemePlacement = FreeVideoStudio.Core.Media.MemePlacement;
namespace FreeVideoStudio.App.Tests;
public sealed class MemeWorkflowTests
{
    [Fact]
    public void DraggingStartKeepsTheEnd_AndDraggingEndKeepsTheStart()
    {
        var vm = new MemeChoiceViewModel();
        vm.SetMeme("funny.mp4", false, 12, false);
        vm.SetPartStart(4);
        Assert.Equal(12, vm.PartEnd);
        vm.PartEnd = 7;
        Assert.Equal(4, vm.ClipInSeconds);
        Assert.Equal(3, vm.LengthSeconds);
        vm.SetPartStart(20);
        Assert.True(vm.LengthSeconds >= (double)vm.MinLengthSec);
        Assert.Equal(7, vm.PartEnd);
        vm.PartEnd = 30;
        Assert.Equal(12, vm.PartEnd);
    }
    [Fact]
    public void PictureLengthAndVideoPartStartWithSensibleDefaults()
    {
        var vm = new MemeChoiceViewModel();
        vm.SetMeme("picture.png", true, null, false);
        Assert.Equal(4, vm.LengthSeconds);
        Assert.Equal(0, vm.ClipInSeconds);
        vm.SetMeme("video.mp4", false, 12, false);
        Assert.Equal(12, vm.LengthSeconds);
        Assert.Equal(0, vm.ClipInSeconds);
    }
    [Fact]
    public void FullScreenAndCornerRememberTheirOwnChoices_AndSurviveReload()
    {
        string root = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "MemeWorkflow", Guid.NewGuid().ToString("N"));
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        var full = new MemePickerPrefsStore.LastChoice(MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true, MemeOverlayAnimation.None, MemeTransition.Fade);
        var corner = new MemePickerPrefsStore.LastChoice(MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopLeft, MemeOverlaySize.Small, false, MemeOverlayAnimation.Slide, MemeTransition.Cut);
        MemePickerPrefsStore.RecordUse("full.mp4", full);
        MemePickerPrefsStore.RecordUse("corner.mp4", corner);
        Assert.Equal(full, MemePickerPrefsStore.ForMode(MemePresentationMode.InlineFullScreen));
        Assert.Equal(corner, MemePickerPrefsStore.ForMode(MemePresentationMode.CornerOverlay));
        var vm = new MemeChoiceViewModel();
        vm.LoadLast(MemePickerPrefsStore.Last);
        Assert.True(vm.IsCorner && vm.IsTopLeft && vm.IsSmall && !vm.PlaySound);
        vm.FullScreenCommand.Execute(null);
        Assert.True(vm.PlaySound);
        Assert.Equal(MemeTransition.Fade, vm.Transition);
        vm.CornerCommand.Execute(null);
        Assert.True(vm.IsTopLeft && vm.IsSmall && !vm.PlaySound);
        string store = Path.Combine(ApplicationPaths.CreateDefault().UiStateDirectory, "meme_picker.json");
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(store))!["modes"]!.AsObject().Count);
        using (LiveCaptureDriver.IsolateProgramData(Path.Combine(root, "another"))) Assert.Null(MemePickerPrefsStore.Last);
        Assert.Equal(full, MemePickerPrefsStore.ForMode(MemePresentationMode.InlineFullScreen));
        Assert.Equal(corner, MemePickerPrefsStore.ForMode(MemePresentationMode.CornerOverlay));
    }
    [Fact]
    public void LandingPreviewAndConfirmedEditUseTheSameSpot_AndUndoRestoresBothTimeAndSound()
    {
        var edit = new GranularEditSession("clip.mp4", 0, 60000, new GranularHistoryParking());
        edit.Segments.Add(new SpeedSegment(10000, 40000, 0.5));
        var old = edit.AddMeme(new MemePlacement("funny.mp4", 5, 3, ""));
        edit.EndGesture();
        var ruler = edit.TimelineExcludingMeme(old.Id, 60000);
        var outcome = edit.ResolveNewMemeAnchor(ruler, 38, MemePresentationMode.InlineFullScreen, out double landing, old.Id);
        Assert.True(outcome.Ok);
        Assert.Equal(40, landing);
        Assert.True(edit.UpdateMeme(old.Id, old with { AtSourceSecRelative = landing, PlaySound = false }, 60000, out _, out var changed).Ok);
        Assert.Equal(landing, changed!.AtSourceSecRelative);
        Assert.False(changed.PlaySound);
        edit.Undo();
        Assert.Equal(old, edit.Memes.Single());
        edit.Redo();
        Assert.Equal(changed, edit.Memes.Single());
    }
    [AvaloniaFact]
    public async Task ClosingPickerDuringRenderCancelsCandidateAndCleansOnlyItsDirectory()
    {
        var root = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "MemePreviewCancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var isolated = LiveCaptureDriver.IsolateProgramData(root);
        var item = new MemeItem { FileName = "picture.png", FullPath = Path.Combine(root, "picture.png"), IsImage = true };
        File.WriteAllBytes(item.FullPath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC"));
        var original = new MemePlacement(item.FullPath, 3, 4, "existing");
        var started = new TaskCompletionSource<(MemePlacement Candidate, string Folder, CancellationToken Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new MemePickerContext(_ => new(true, 7, 8, "Starts at 8s"), RenderPreview: async (candidate, directory, ct, _) =>
        {
            File.WriteAllText(Path.Combine(directory, "partial.mp4"), "partial");
            started.SetResult((candidate, directory, ct));
            await release.Task;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The popup closed without cancelling the renderer.");
        });
        var owner = new Window();
        owner.Show();
        try
        {
            var picking = MemePickerWindow.PickAsync(owner, [item], existing: original, context: context);
            var dialog = Assert.IsType<MemePickerWindow>(Assert.Single(owner.OwnedWindows));
            dialog.Choice.CornerCommand.Execute(null);
            dialog.FindControl<Button>("OpenWithSoundBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var render = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(render.Candidate.IsCornerOverlay);
            Assert.Equal(7, render.Candidate.AtSourceSecRelative);
            string sibling = Path.Combine(Path.GetDirectoryName(render.Folder)!, "other-preview.txt");
            File.WriteAllText(sibling, "keep");
            dialog.Close();
            Assert.True(render.Token.IsCancellationRequested);
            Assert.False(picking.IsCompleted);
            Assert.True(File.Exists(Path.Combine(render.Folder, "partial.mp4")));
            release.SetResult();
            Assert.Null(await picking.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(Directory.Exists(render.Folder));
            Assert.True(File.Exists(sibling));
            Assert.True(File.Exists(item.FullPath));
            Assert.Null(MemePickerPrefsStore.Last);
        }
        finally
        {
            release.TrySetResult();
            owner.Close();
        }
    }
    [Fact]
    public void EditingAMemeDoesNotClashWithItself()
    {
        var edit = new GranularEditSession("clip.mp4", 0, 60000, new GranularHistoryParking());
        var old = edit.AddMeme(new MemePlacement("funny.mp4", 5, 3, ""));
        var ruler = edit.TimelineExcludingMeme(old.Id, 60000);
        Assert.True(edit.ResolveNewMemeAnchor(ruler, 5, old.Mode, out double landing, old.Id).Ok);
        Assert.Equal(5, landing);
    }
    [Fact]
    public void AChosenPartPastThirtySecondsUsesItsOwnFrames()
    {
        Assert.Equal(16, CornerMemeFrames.WindowIndexForTime(40));
        Assert.Equal(0, CornerMemeFrames.WindowIndexForTime(double.NaN));
    }
    [Fact]
    public void ARenderedCornerClearlyOffersAPathBackToPositioning()
    {
        var vm = new MemeChoiceViewModel { Mode = MemePresentationMode.CornerOverlay, IsResultPreview = true };
        Assert.Equal("Click the preview to adjust the meme's position.", vm.PositionText);
        vm.IsResultPreview = false;
        Assert.Contains("drag it anywhere", vm.PositionText);
    }
    [AvaloniaFact]
    public void PlacementChoicesAndPartHandlesDoNotHijackTextInput()
    {
        var vm = new MemeChoiceViewModel { IsMainScreen = true };
        Assert.True(vm.ShowModeCards && vm.IsAtHere);
        vm.AtStartCommand.Execute(null);
        Assert.True(vm.IsAtStart && !vm.IsAtEnd && !vm.IsAtHere);
        vm.AtEndCommand.Execute(null);
        Assert.True(vm.IsAtEnd && !vm.IsAtStart && !vm.IsAtHere);
        vm.AtHereCommand.Execute(null);
        Assert.True(vm.IsAtHere);
        Assert.False(MemePickerWindow.TypingGoesToSearch(new MemePartRange(), " "));
        Assert.False(MemePickerWindow.TypingGoesToSearch(new NumericUpDown(), "5"));
    }
}
