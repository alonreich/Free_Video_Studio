using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class FreezeSoundParityTests
{
    private static string Read(params string[] parts) => File.ReadAllText(RepoRoot.SourcePath(new[] { "src" }.Concat(parts).ToArray()));

    [Fact]
    public void EveryScreenThatHoldsAFreeze_PlaysTheFreezeSoundThroughTheOneEntryPoint()
    {
        Assert.Contains("_freezeSound.PlayHold(_edit.FreezeDurationS, FreezeLoudnessPath, SpeedEditorGameBalance, FreezeTailRoomSec());", Read("FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        string main = Read("FreeVideoStudio.App", "MainWindow.axaml.cs");
        Assert.Matches(new Regex(@"freeze-hold-start[\s\S]{0,200}StartLiveFreezeSound\(\);"), main);
        Assert.Contains("_freezeSound.PlayHold(_freezeDurationS, _loadedVideoPath, LiveGameBalance(), LiveFreezeTailRoomSec());", Read("FreeVideoStudio.App", "MainWindow.FreezeSound.cs"));
        Assert.Contains("_freezeSound.PlayHold(_currentFreezeDurationMs / 1000.0, _videoPath, _voPreviewGameGain);", Read("FreeVideoStudio.App", "VoiceOverWindow.axaml.cs"));
        string[] screens = { "GranularSpeedEditorWindow.axaml.cs", "MainWindow.FreezeSound.cs", "VoiceOverWindow.axaml.cs", "VideoMergerWindow.EdlPreview.cs", "MusicWizardWindow.FreezeSound.cs" };
        foreach (var file in screens)
            Assert.DoesNotContain("_freezeSound.Play(", Read("FreeVideoStudio.App", file));
    }

    [Fact]
    public void VideoMergerPreview_PlaysTheSoundOnEveryFreezeHold_AndCutsTheTailAtTheClipEnd()
    {
        string merger = Read("FreeVideoStudio.App", "VideoMergerWindow.EdlPreview.cs");
        Assert.Matches(new Regex(@"if \(step\.Kind == PreviewStepKind\.Freeze\)\s*_freezeSound\.PlayHold\(step\.HoldSec, MergerFreezeLoudnessPath, MergerGameBalance\(\),\s*plan\.ClipBodyOutputEndSec\(step\.ClipIndex\) - \(step\.OutputStartSec \+ step\.HoldSec\)\);"), merger);
        Assert.Contains("if (!resume) _freezeSound.Stop();", merger);
        Assert.Contains("internal const string? MergerFreezeLoudnessPath = null;", merger);
        Assert.Contains("_freezeSound.SetGameBalance(MergerGameBalance());", Read("FreeVideoStudio.App", "VideoMergerWindow.VolumeSync.cs"));
        Assert.Contains("_freezeSound.Dispose();", Read("FreeVideoStudio.App", "VideoMergerWindow.axaml.cs"));
    }

    [Fact]
    public void AddMusicPreview_PlaysTheSoundOnEveryFreeze_FromWhereThePlayheadIs()
    {
        string sound = Read("FreeVideoStudio.App", "MusicWizardWindow.FreezeSound.cs");
        string wizard = Read("FreeVideoStudio.App", "MusicWizardWindow.axaml.cs");
        Assert.Contains("_freezeSound.PlayHold(range.EndOutputSec - range.StartOutputSec, Phase3FreezeLoudnessPath, GetPreviewVideoBalance(),", sound);
        Assert.Contains("Math.Max(0, timeline.TotalOutputSeconds - range.EndOutputSec), Math.Max(0, outputSec - range.StartOutputSec));", sound);
        Assert.Contains("private string? Phase3FreezeLoudnessPath => _isMergerMode ? null : _videoPath;", sound);
        Assert.Matches(new Regex(@"UpdatePhase3FreezeSound\(outputRelativeSec\);\s*if \(speed <= 0\.001\)"), wizard);
        Assert.True(Regex.Matches(wizard, @"StopPhase3FreezeSound\(\);").Count >= 2, "the freeze sound must stop when the preview stops or seeks");
        Assert.True(Regex.Matches(wizard, @"_freezeSound\.SetGameBalance\(GetPreviewVideoBalance\(\)\);").Count >= 2, "the freeze sound must follow the video volume slider and the master volume");
        Assert.Contains("_freezeSound.Dispose();", wizard);
    }

    [Fact]
    public void SpeedEditorAndMainPreview_CutTheTailWhereTheEditedVideoEnds_LikeTheExport()
    {
        Assert.Contains("Math.Max(0, OutDurationSec() - (FreezeHoldStartOutSec() + _edit.FreezeDurationS));", Read("FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        Assert.Contains("timeline.TotalOutputSeconds - ranges[0].EndOutputSec", Read("FreeVideoStudio.App", "MainWindow.FreezeSound.cs"));
        Assert.Contains("FreezeSoundEffect.PreviewSlice(durationSec, sound, gainDb, tailRoomSec, startAtSec)", Read("FreeVideoStudio.App", "Services", "FreezeSoundPreview.cs"));
    }

    [Fact]
    public void MainPreview_NeverPlaysTheSoundTwice_TheRenderedMixAlreadyCarriesIt()
    {
        Assert.Contains("if (PreviewMixActive) return;", Read("FreeVideoStudio.App", "MainWindow.FreezeSound.cs"));
        Assert.Matches(new Regex(@"if \(PreviewMixActive\)\s*\{\s*_freezeSound\.Stop\(\);"), Read("FreeVideoStudio.App", "MainWindow.PreviewMix.cs"));
    }

    [Fact]
    public void FreezeSoundFollowsEachScreensGameVolume_LikeTheVideoItself()
    {
        string main = Read("FreeVideoStudio.App", "MainWindow.axaml.cs");
        Assert.Contains("ApplyPreviewGainAsync(videoBase * PreviewGameDuckGain());\n        _freezeSound.SetGameBalance(videoBase * PreviewGameDuckGain());", main.Replace("\r\n", "\n"));
        Assert.Contains("_freezeSound.SetGameBalance(_voPreviewGameGain);", Read("FreeVideoStudio.App", "VoiceOverWindow.axaml.cs"));
        Assert.Contains("_freezeSound.SetGameBalance(gain);", Read("FreeVideoStudio.App", "VoiceOverWindow.PreviewProtection.cs"));
        Assert.Contains("_freezeSound.SetGameBalance(SpeedEditorGameBalance);", Read("FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
    }

    [Fact]
    public void OutputVolume_IsThePlayersCurveTimesTheGameBalance()
    {
        Assert.Equal(MpvIpcClient.MasterLinearGain * 0.6, Services.FreezeSoundPreview.OutputVolume(0.6), 9);
        Assert.Equal(0, Services.FreezeSoundPreview.OutputVolume(-1));
        Assert.Equal(0, Services.FreezeSoundPreview.OutputVolume(double.NaN));
    }

    [Fact]
    public void PreviewAndExport_ReadTheVideosLoudnessFromTheSamePlace()
    {
        Assert.Contains("FreezeVideoLufs = FreeVideoStudio.Core.Media.VideoLoudnessCache.TryGet(_loadedVideoPath),", Read("FreeVideoStudio.App", "MainWindow.Export.cs"));
        Assert.Contains("worker.FreezeVideoLufs = payload.FreezeVideoLufs;", Read("FreeVideoStudio.App", "Services", "MainMediaController.cs"));
        Assert.Contains("freezeVideoLufs: FreezeVideoLufs);", Read("FreeVideoStudio.Core", "Media", "ProcessWorker.cs"));
        Assert.Contains("VideoLoudnessCache.TryGet(_loadedVideoPath)?.ToString(\"F2\", ci)", Read("FreeVideoStudio.App", "MainWindow.PreviewMix.cs"));
        Assert.Contains("FreezeSoundEffect.GainDbFor(VideoLoudnessCache.TryGet(videoPath), sound)", Read("FreeVideoStudio.App", "Services", "FreezeSoundPreview.cs"));
        Assert.Contains("{ FreezeVideoLufs = freezeLufs }", Read("FreeVideoStudio.App", "VoiceOverWindow.PlaybackClock.cs"));
        Assert.Contains("VideoLoudnessCache.Set(path, reading.IntegratedLufs);", Read("FreeVideoStudio.App", "MainWindow.axaml.cs"));
        Assert.Contains("VideoLoudnessCache.Set(videoPath, _gameplayLoudnessLufs);", Read("FreeVideoStudio.App", "MainWindow.Restore.cs"));
    }

    [Fact]
    public void MergeMode_UsesTheDefaultLevel_LikeTheVideoMergerExport()
    {
        Assert.Contains("private string? FreezeLoudnessPath => IsMergeMode ? null : _edit.VideoPath;", Read("FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        Assert.DoesNotContain("freezeVideoLufs", Read("FreeVideoStudio.Core", "Media", "MergeClipGraph.cs"));
    }

    [Fact]
    public void VoiceOverPreviewCopy_HasTheSameFreezeSoundAsTheExport()
    {
        var segments = new[] { new SpeedSegment(2000, 3000, 0) };
        var plan = new VoiceOverPlaybackPlan(0, 8, segments, 1.0) { FreezeVideoLufs = -18.2 };
        string voice = plan.BuildGraph(hasAudio: true).Graph;
        var export = GranularSpeedBuilder.Build(8000, segments.ToList(), 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false, freezeVideoLufs: -18.2);
        string Level(string g) => Regex.Match(g, @"freeze_[A-Za-z_\-]+_v\d+_\d+_g-?\d+\.wav").Value;
        Assert.NotEqual("", Level(export.filterGraph));
        Assert.Equal(Level(export.filterGraph), Level(voice));
    }

    [Fact]
    public void LevelIs83PercentOfTheVideo()
    {
        Assert.Equal(0.83, FreezeSoundEffect.LevelBelowVideo);
        Assert.Equal(-18.2 + 20 * System.Math.Log10(0.83) - FreezeSoundEffect.ChurchBellLufs,
            FreezeSoundEffect.GainDbFor(-18.2, FreezeSound.Church_Bell), 1);
    }
}
