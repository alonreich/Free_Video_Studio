using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public sealed record AiCommentaryDialogResult(
    CaptionTone Tone,
    CaptionStyle Style,
    CaptionPosition Position,
    string Language,
    AiGameProfile Profile,
    string? CustomNotes,
    bool EntireTimeline,
    CommentaryPersonality? Personality = null,
    CommentaryPreset Preset = CommentaryPreset.HypeStreamer,
    CaptionLook Look = CaptionLook.Outline,
    CaptionSize Size = CaptionSize.Medium)
{
    public CommentaryPersonality EffectivePersonality =>
        (Personality ?? CommentaryPersonality.FromPreset(CommentaryPersonality.PresetForTone(Tone))).Normalized();
}

public partial class AiCommentaryDialogWindow : Window
{
    public AiCommentaryDialogResult? Result { get; private set; }

    public AiCommentaryDialogWindow()
    {
        InitializeComponent();
    }

    private void Attach(AiCommentaryOptionsViewModel vm)
    {
        DataContext = vm;
        vm.CancelRequested += () =>
        {
            Result = null;
            Close();
        };
        vm.GenerateRequested += () =>
        {
            Result = vm.ToResult();
            Close();
        };
    }

    internal static int ProfileToIndex(AiGameProfile p) => AiCommentaryOptionsViewModel.ProfileToIndex(p);

    public static async Task<AiCommentaryDialogResult?> ShowDialogAsync(
        Window owner,
        bool hasSelectedSegment,
        CaptionTone initialTone = CaptionTone.Hype,
        AiGameProfile initialProfile = AiGameProfile.ThirdPerson)
    {
        var dlg = new AiCommentaryDialogWindow();
        dlg.Attach(AiCommentaryOptionsViewModel.FromSettings(
            Infrastructure.SettingsManager.Instance, hasSelectedSegment, initialTone, initialProfile));
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    public static void Remember(AiCommentaryDialogResult r) => AiCommentaryOptionsViewModel.Remember(r);
}
