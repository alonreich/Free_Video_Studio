// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS), docs/04_UI_UX_AVALONIA_SPEC.md (UI-AICOMMENTARY)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// Result from the <see cref="AiCommentaryDialogWindow"/> modal. <see cref="Personality"/> (COMMENTARYMIX_01) is
/// the slider mix; when it is null the <see cref="Tone"/>'s one-click style is used.
/// </summary>
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

/// <summary>
/// AICOMMENTARY_UI_01 / COMMENTARYUI_02 — configuration dialog for AI Live Commentary Captions generation. All state is
/// in <see cref="AiCommentaryOptionsViewModel"/> (bound, MVVM_01); this file only opens and closes the window.
/// </summary>
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

    /// <summary>AIPROFILE_ONE_01 — the game-type list order (kept here for existing callers).</summary>
    internal static int ProfileToIndex(AiGameProfile p) => AiCommentaryOptionsViewModel.ProfileToIndex(p);

    public static async Task<AiCommentaryDialogResult?> ShowDialogAsync(
        Window owner,
        bool hasSelectedSegment,
        CaptionTone initialTone = CaptionTone.Hype,
        AiGameProfile initialProfile = AiGameProfile.ThirdPerson)
    {
        var dlg = new AiCommentaryDialogWindow();
        // AIPROFILE_ONE_01 — start on the game type chosen in Settings › AI Features ("What are you recording?").
        // AICOMMENTARY_REMEMBER_01 — every other choice opens as it was left last time; Advanced is always closed.
        dlg.Attach(AiCommentaryOptionsViewModel.FromSettings(
            Infrastructure.SettingsManager.Instance, hasSelectedSegment, initialTone, initialProfile));
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    /// <summary>AICOMMENTARY_REMEMBER_01 — stores what the user just chose, for the next time the window opens.</summary>
    public static void Remember(AiCommentaryDialogResult r) => AiCommentaryOptionsViewModel.Remember(r);
}
