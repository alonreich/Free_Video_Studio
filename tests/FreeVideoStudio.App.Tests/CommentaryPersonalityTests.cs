using System;
using System.Linq;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// COMMENTARYMIX_01, COMMENTARYUI_02, TACTICAL_01..03, SWEARING_01, REWRITELINE_01, CAPTIONRTL_01 — the AI
/// Commentary "brain" and its options window (04 UI-AICOMMENTARY, 01 TL-CAPTIONS).
/// </summary>
public sealed class CommentaryPersonalityTests
{
    private static string Prompt(CommentaryPersonality mix, string language = "English", System.Collections.Generic.IReadOnlyList<double>? kills = null, string? rewrite = null)
        => AiCommentaryPrompts.BuildPrompt(mix, AiGameProfile.Fortnite, language, canHearAudio: false, killTimesInPart: kills, rewriteOf: rewrite);

    [Fact]
    public void Presets_SetTheFourSliders_AndHypeStreamerIsTheFirstStyle()
    {
        var vm = AiCommentaryOptionsViewModel.FromSettings(new AppSettings(), hasSelectedSegment: false, null, AiGameProfile.ThirdPerson);
        Assert.Equal(CommentaryPreset.HypeStreamer, vm.Preset);
        Assert.Equal(CommentaryPersonality.FromPreset(CommentaryPreset.HypeStreamer), vm.Personality with { Chattiness = CommentaryChattiness.Normal, AllowSwearing = false });
        Assert.False(vm.IsAdvancedOpen);
        Assert.True(vm.IsLower && vm.IsOutlineLook && vm.IsMedium && vm.IsNormalChat && !vm.AllowSwearing);

        vm.IsCoach = true;
        Assert.Equal(4, vm.Personality.Tactical);
        vm.Tactical = 1;
        Assert.Equal(CommentaryPreset.Coach, vm.Preset);
        Assert.Equal(1, vm.Personality.Tactical);
    }

    [Fact]
    public void EveryChoiceIsRemembered_ButAdvancedAlwaysOpensClosed()
    {
        var s = new AppSettings
        {
            AiCommentaryLastPreset = "RoastMe",
            AiCommentaryLastFunny = 4,
            AiCommentaryLastSarcastic = 2,
            AiCommentaryLastEnergy = 0,
            AiCommentaryLastTactical = 3,
            AiCommentaryLastPosition = "Middle",
            AiCommentaryLastLook = "Box",
            AiCommentaryLastSize = "Large",
            AiCommentaryLastChattiness = "Quiet",
            AiCommentaryLastAllowSwearing = true,
            AiCommentaryLastLanguage = "Hebrew",
            AiCommentaryLastStyle = "PopIn",
        };
        var vm = AiCommentaryOptionsViewModel.FromSettings(s, hasSelectedSegment: false, CaptionTone.Hype, AiGameProfile.ThirdPerson);
        Assert.True(vm.IsRoastMe);
        Assert.Equal(new CommentaryPersonality(4, 2, 0, 3, CommentaryChattiness.Quiet, true), vm.Personality);
        Assert.True(vm.IsMiddle && vm.IsBoxLook && vm.IsLarge && vm.IsQuiet && vm.IsPlainLines);
        Assert.Equal("Hebrew", vm.Language);
        Assert.False(vm.IsAdvancedOpen);

        var r = vm.ToResult();
        Assert.Equal(CaptionPosition.Middle, r.Position);
        Assert.Equal(CaptionLook.Box, r.Look);
        Assert.Equal(CaptionSize.Large, r.Size);
        Assert.Equal(CommentaryPreset.RoastMe, r.Preset);
    }

    [Fact]
    public void TheMixReachesThePromptAsWords_NotNumbers()
    {
        string p = Prompt(new CommentaryPersonality(Funny: 3, Sarcastic: 0, Energy: 0, Tactical: 4));
        Assert.Contains("Funny (A lot)", p);
        Assert.Contains("Sarcastic (Off): none at all", p);
        Assert.Contains("Energy (Calm): very calm", p);
        Assert.Contains("Tactical (Max)", p);
        Assert.DoesNotContain("technical", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tactical01_EveryReasonMustBeSeen_AndHighTacticalAllowsLongerLines()
    {
        string tactical = Prompt(new CommentaryPersonality(0, 0, 2, 4));
        Assert.Contains("NEVER invent thoughts, hopes, plans or feelings", tactical);
        Assert.Contains("at most 12 words", tactical);
        Assert.Contains("at least 2.0 seconds", tactical);

        string none = Prompt(new CommentaryPersonality(2, 0, 2, 0));
        Assert.DoesNotContain("NEVER invent thoughts", none);
        Assert.Contains("at most 8 words", none);
    }

    [Fact]
    public void Tactical03_KillMarksAreFacts_ForTheirPartOnly()
    {
        Assert.Null(CommentaryService.KillTimesForPart(null, 0, 8));
        Assert.Equal([1.5, 6.0], CommentaryService.KillTimesForPart([11.5, 16.0, 30.0], 10, 18));
        Assert.Empty(CommentaryService.KillTimesForPart([30.0], 10, 18)!);

        string withKills = Prompt(CommentaryPersonality.FromPreset(CommentaryPreset.Coach), kills: [1.5, 6.0]);
        Assert.Contains("1.5s, 6.0s", withKills);
        Assert.Contains("Do not claim any other elimination", withKills);
        Assert.Contains("found NO eliminations", Prompt(CommentaryPersonality.FromPreset(CommentaryPreset.Coach), kills: []));
        Assert.DoesNotContain("Kill tracker", Prompt(CommentaryPersonality.FromPreset(CommentaryPreset.Coach)));
    }

    [Fact]
    public void Swearing_IsOffByDefault_ChattinessAndRewriteAndHebrewReachThePrompt()
    {
        Assert.Contains("no swearing", Prompt(CommentaryPersonality.FromPreset(CommentaryPreset.RoastMe)));
        Assert.Contains("Casual swearing is allowed", Prompt(CommentaryPersonality.FromPreset(CommentaryPreset.RoastMe) with { AllowSwearing = true }));
        Assert.Contains("Write FEW lines", Prompt(new CommentaryPersonality(1, 1, 1, 1, CommentaryChattiness.Quiet)));
        Assert.Contains("Talk often", Prompt(new CommentaryPersonality(1, 1, 1, 1, CommentaryChattiness.Busy)));
        Assert.Contains("replaces: \"old line\"", Prompt(new CommentaryPersonality(1, 1, 1, 1), rewrite: "old line"));
        Assert.Contains("Hebrew letters", Prompt(new CommentaryPersonality(1, 1, 1, 1), language: "Hebrew"));
    }

    [Fact]
    public void OldToneCallersStillWork_AndTheTrackKeepsADominantTone()
    {
        Assert.Contains("Energy (Max hype)", AiCommentaryPrompts.BuildPrompt(CaptionTone.Hype, AiGameProfile.Generic, "English"));
        Assert.Equal(CaptionTone.Tactical, CommentaryPersonality.FromPreset(CommentaryPreset.Coach).DominantTone);
        Assert.Equal(CaptionTone.Funny, CommentaryPersonality.FromPreset(CommentaryPreset.RoastMe).DominantTone);
        Assert.Equal(CaptionTone.Hype, CommentaryPersonality.FromPreset(CommentaryPreset.HypeStreamer).DominantTone);
        Assert.Equal(CaptionTone.Chill, CommentaryPersonality.FromPreset(CommentaryPreset.ChillFriend).DominantTone);
        Assert.Equal(CaptionTone.Chill, new CommentaryPersonality(0, 0, 0, 0).DominantTone);
    }

    [Fact]
    public void Rewrite_TheCaptionCueHasARightClickMenu()
    {
        string lane = System.IO.File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.CaptionsLane.cs"));
        Assert.Contains("centerBody.ContextMenu = BuildCaptionContextMenu", lane);
        Assert.Contains("Rewrite this line", lane);
        Assert.Contains("RewriteOf: oldText", lane);
    }
}
