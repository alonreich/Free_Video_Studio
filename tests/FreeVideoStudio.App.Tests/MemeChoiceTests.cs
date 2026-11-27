using System.IO;
using CornerMemeFrames = FreeVideoStudio.App.Infrastructure.CornerMemeFrames;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class MemeChoiceTests
{
    [Fact]
    public void Defaults_AreFullScreen_BottomRight_Medium_WithSound()
    {
        var vm = new MemeChoiceViewModel();
        Assert.True(vm.IsFullScreen);
        Assert.False(vm.IsCorner);
        Assert.True(vm.IsBottomRight);
        Assert.True(vm.IsMedium);
        Assert.True(vm.PlaySound);
        Assert.False(vm.HasMeme);
    }

    [Fact]
    public void FullScreenCard_SaysHowMuchLongerTheVideoGets()
    {
        var vm = new MemeChoiceViewModel { DurationSec = 4.0 };
        Assert.Equal("Video becomes +4.0 sec longer", vm.FullScreenLengthText);
        vm.DurationSec = 2.46;
        Assert.Equal("Video becomes +2.5 sec longer", vm.FullScreenLengthText);
    }

    [Fact]
    public void Commands_SetEveryProperty_AndApplyCarriesThemOntoThePlacement()
    {
        var vm = new MemeChoiceViewModel();
        vm.CornerCommand.Execute(null);
        vm.TopLeftCommand.Execute(null);
        vm.LargeCommand.Execute(null);
        vm.PlaySound = false;
        Assert.True(vm.IsCorner && vm.IsTopLeft && vm.IsLarge);

        var placed = vm.ApplyTo(new MemePlacement("m.mp4", 3, 4, "meme0"));
        Assert.Equal((MemePresentationMode.CornerOverlay, MemeOverlayCorner.TopLeft, MemeOverlaySize.Large, false),
            (placed.Mode, placed.Corner, placed.Size, placed.PlaySound));
        Assert.Equal(0.0, placed.OutputDurationSec);

        var again = new MemeChoiceViewModel();
        again.LoadFrom(placed);
        Assert.True(again.IsCorner && again.IsTopLeft && again.IsLarge && !again.PlaySound);
        Assert.Equal(4.0, again.DurationSec);
    }

    [Fact]
    public void PreviewFrameSplitter_SeparatesConcatenatedPngs()
    {
        byte[] Png(byte marker) => new byte[] { 0x89, (byte)'P', marker, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 1, 2, 3, 4 };
        var stream = new MemoryStream();
        stream.Write(Png(7));
        stream.Write(Png(9));
        var parts = new System.Collections.Generic.List<byte[]>(CornerMemeFrames.SplitPngStream(stream.ToArray()));
        Assert.Equal(2, parts.Count);
        Assert.Equal(7, parts[0][2]);
        Assert.Equal(9, parts[1][2]);
    }

    [Fact]
    public void MEMEPICK_06_MoreOptionsHeader_SummarisesLengthAndEffect()
    {
        var vm = new MemeChoiceViewModel();
        vm.SetMeme("a.png", true, null, false);
        Assert.Equal("More options  ·  4.0 s  ·  Cut", vm.MoreOptionsHeader);
        vm.Transition = MemeTransition.Zoom;
        Assert.Equal("More options  ·  4.0 s  ·  Zoom punch", vm.MoreOptionsHeader);
        vm.Mode = MemePresentationMode.CornerOverlay;
        vm.Animation = MemeOverlayAnimation.Slide;
        Assert.Equal("More options  ·  4.0 s  ·  Slide", vm.MoreOptionsHeader);
    }

    [Fact]
    public void MEMEPICK_06_APictureHasNoPlayButton_SoItNeverStaysPaused()
    {
        var vm = new MemeChoiceViewModel();
        vm.SetMeme("a.mp4", false, 3, false);
        Assert.True(vm.IsVideo);
        vm.IsPlaying = false;
        vm.SetMeme("a.png", true, null, false);
        Assert.False(vm.IsVideo);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public void MEMEGALLERY_01_OneDownloadButton_GetsTheWholeLibrary_NoQuestions()
    {
        var vm = new MemeChoiceViewModel();
        int asked = 0;
        vm.DownloadRequested += () => asked++;
        vm.DownloadCommand.Execute(null);
        Assert.Equal(1, asked);
        string service = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Services", "MemeManagementService.cs"));
        int start = service.IndexOf("DownloadAllMemesAsync(Window parent)", System.StringComparison.Ordinal);
        string body = service.Substring(start, service.IndexOf("public static string DownloadStatus", start, System.StringComparison.Ordinal) - start);
        Assert.Contains("SyncAllMemesAsync", body);
        Assert.DoesNotContain("ConfirmDialogWindow", body);
    }

    private static FreeVideoStudio.App.Controls.MemePickerRow Row(string file, bool image, double seconds = 0) =>
        new(new MemeItem { FileName = file, FullPath = Path.Combine("zz_meme_tests", file), IsImage = image, DurationSec = seconds }, true);

    [Fact]
    public void MEMEGALLERY_01_VideosAndPicturesSitInTheirOwnSections_InRowsOfTiles()
    {
        var rows = new[] { Row("p1.png", true), Row("v1.mp4", false, 3), Row("v2.mp4", false, 5), Row("v3.mp4", false, 7), Row("p2.png", true) };
        var (order, lines) = FreeVideoStudio.App.Controls.MemeGallery.Build(rows, 2);
        Assert.Equal(new[] { "v1.mp4", "v2.mp4", "v3.mp4", "p1.png", "p2.png" }, order.Select(r => r.Item.FileName));
        Assert.IsType<FreeVideoStudio.App.Controls.MemeGalleryHeader>(lines[0]);
        Assert.Contains("VIDEO MEMES  ·  3", ((FreeVideoStudio.App.Controls.MemeGalleryHeader)lines[0]).Text);
        Assert.Equal(2, ((FreeVideoStudio.App.Controls.MemeGalleryTiles)lines[1]).Tiles.Count);
        Assert.Single(((FreeVideoStudio.App.Controls.MemeGalleryTiles)lines[2]).Tiles);
        Assert.Contains("PICTURE MEMES  ·  2", ((FreeVideoStudio.App.Controls.MemeGalleryHeader)lines[3]).Text);
        Assert.Equal(3, ((FreeVideoStudio.App.Controls.MemeGalleryTiles)lines[4]).FirstIndex);
    }

    [Theory]
    [InlineData(612, 3)]
    [InlineData(300, 2)]
    [InlineData(1000, 5)]
    public void MEMEGALLERY_01_TheGalleryFitsAsManyTilesAsTheWidthAllows(double width, int columns)
    {
        Assert.Equal(columns, FreeVideoStudio.App.Controls.MemeGallery.ColumnsFor(width));
    }

    [Fact]
    public void MEMEGALLERY_01_EachTileShowsAReadableTitle_AndItsLengthOrPicture()
    {
        Assert.Equal("funny cat meme", FreeVideoStudio.App.Controls.MemePickerRow.TitleFor("funny_cat-meme.mp4"));
        Assert.Equal("🎬 0:07", Row("v.mp4", false, 7.2).BadgeText);
        Assert.Equal("🎬 1:15", Row("v.mp4", false, 75).BadgeText);
        Assert.Equal("🖼 PICTURE", Row("p.png", true).BadgeText);
    }

    [Fact]
    public void MEMEMUSIC_03_PickerDefaults_PictureKeepsMusic_VideoStopsIt()
    {
        var vm = new MemeChoiceViewModel();
        vm.SetMeme(Path.Combine("zz_meme_tests", "never_used.png"), true, null, false);
        Assert.True(vm.MusicContinues);
        Assert.Equal("🎵 Music continues to play", vm.MusicText);
        vm.SetMeme(Path.Combine("zz_meme_tests", "never_used.mp4"), false, 3, false);
        Assert.False(vm.MusicContinues);
        Assert.Equal("🔇 Music stops!", vm.MusicText);
        vm.MusicContinueCommand.Execute(null);
        Assert.True(vm.ApplyTo(new MemePlacement("x.mp4", 0, 3, "m")).MusicContinues);
    }

    [Fact]
    public void MEMEMUSIC_03_ChangingAPlacedMemeShowsItsOwnMusicChoice_AndCornersHideIt()
    {
        var vm = new MemeChoiceViewModel();
        vm.LoadFrom(new MemePlacement("x.png", 1, 2, "m", MusicContinues: false));
        Assert.False(vm.MusicContinues);
        Assert.True(vm.ShowMusicChoice);
        vm.Mode = MemePresentationMode.CornerOverlay;
        Assert.False(vm.ShowMusicChoice);
    }

    [Fact]
    public void MEMEGALLERY_01_TheMainScreenUsesTheSameGallery_WithStartOrEnd()
    {
        var vm = new MemeChoiceViewModel { Mode = MemePresentationMode.CornerOverlay };
        vm.IsMainScreen = true;
        Assert.True(vm.IsCorner);
        Assert.True(vm.ShowModeCards);
        Assert.True(vm.IsAtHere);
        vm.AtStartCommand.Execute(null);
        Assert.True(vm.IsAtStart);

        var main = new MainViewModel();
        Assert.Equal("🤡 Choose a meme…", main.MemeButtonText);
        main.SelectedMemeItem = new MemeItem { FileName = "big_win.mp4", FullPath = "big_win.mp4" };
        main.MemeAtStart = true;
        Assert.Equal("🤡 big win · at the start  ✎", main.MemeButtonText);
    }

    [Fact]
    public void MEMERECOVER_01_TheMainScreenMemeIsWrittenToTheCrashRecoveryFile()
    {
        string choose = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.axaml.cs"));
        string save = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.Recovery.cs"));
        string restore = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.Restore.cs"));
        Assert.Contains("_memePlacements.Add(placed);", choose);
        Assert.Contains("SaveRecoveryState(label: \"add meme\")", choose);
        Assert.Contains("mainVm.SelectedMemeItem?.FullPath", save);
        Assert.Contains("_viewModel.SelectedMemeItem = _memeItems.FirstOrDefault(", restore);
        Assert.DoesNotContain("MemeComboBox", File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.axaml")));
    }

    [Fact]
    public void MEMEPICK_07_TheButtonSaysAddForANewMeme_AndSaveWhenChangingOne()
    {
        var vm = new MemeChoiceViewModel();
        Assert.Equal("ADD", vm.AddButtonText);
        vm.LoadFrom(new MemePlacement("a.mp4", 1, 2, "x"));
        Assert.Equal("SAVE", vm.AddButtonText);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void MEMEPICK_07_TypingAnywhereGoesToTheSearch_ButNotFromATextBoxOrNumberBox()
    {
        Assert.True(FreeVideoStudio.App.Controls.MemePickerWindow.TypingGoesToSearch(new Avalonia.Controls.ListBox(), "d"));
        Assert.False(FreeVideoStudio.App.Controls.MemePickerWindow.TypingGoesToSearch(new Avalonia.Controls.TextBox(), "d"));
        Assert.False(FreeVideoStudio.App.Controls.MemePickerWindow.TypingGoesToSearch(new Avalonia.Controls.NumericUpDown(), "5"));
        Assert.False(FreeVideoStudio.App.Controls.MemePickerWindow.TypingGoesToSearch(new Avalonia.Controls.ListBox(), "\b"));
        Assert.False(FreeVideoStudio.App.Controls.MemePickerWindow.TypingGoesToSearch(new Avalonia.Controls.ListBox(), ""));
    }

    [Fact]
    public void MEMEPICK_07_CloseIsAnXInTheTitleBar_AndEscStillCloses()
    {
        string xaml = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", "MemePickerWindow.axaml"));
        Assert.Contains("Name=\"CancelBtn\" Content=\"✕\" IsCancel=\"True\"", xaml);
        Assert.DoesNotContain("USE THIS MEME", xaml);
        Assert.Contains("IsDefault=\"True\"", xaml);
    }

    [Theory]
    [InlineData(10, 10, MemeOverlayCorner.TopLeft)]
    [InlineData(90, 10, MemeOverlayCorner.TopRight)]
    [InlineData(10, 190, MemeOverlayCorner.BottomLeft)]
    [InlineData(90, 190, MemeOverlayCorner.BottomRight)]
    public void MEMEPICK_06_ClickingAPreviewCornerPicksThatCorner(double x, double y, MemeOverlayCorner expected)
    {
        Assert.Equal(expected, FreeVideoStudio.App.Controls.MemePlacementPreview.CornerAt(x, y, 100, 200));
    }

    [Fact]
    public void MEMEPICK_06_TheButtonsAreAlwaysOnScreen_At80PercentOf1080p()
    {
        string xaml = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", "MemePickerWindow.axaml"));
        var h = System.Text.RegularExpressions.Regex.Match(xaml, "Width=\"\\d+\" Height=\"(\\d+)\"");
        var minH = System.Text.RegularExpressions.Regex.Match(xaml, "MinHeight=\"(\\d+)\"");
        Assert.True(int.Parse(h.Groups[1].Value) <= 1032 * 0.8, "default height must fit 80% of a 1080p work area, or the whole window scrolls");
        Assert.True(int.Parse(minH.Groups[1].Value) <= 720 * 0.8, "minimum height must fit 80% of a 768p laptop work area");
        Assert.Contains("<ScrollViewer Grid.Column=\"2\"", xaml);
    }
}
