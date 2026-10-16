// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS), docs/04_UI_UX_AVALONIA_SPEC.md (UI-AICOMMENTARY)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// Result from the <see cref="AiCommentaryDialogWindow"/> modal.
/// </summary>
public sealed record AiCommentaryDialogResult(
    CaptionTone Tone,
    CaptionStyle Style,
    CaptionPosition Position,
    string Language,
    AiGameProfile Profile,
    string? CustomNotes,
    bool EntireTimeline);

/// <summary>
/// AICOMMENTARY_UI_01 — configuration dialog for AI Live Commentary Captions generation.
/// </summary>
public partial class AiCommentaryDialogWindow : Window
{
    public AiCommentaryDialogResult? Result { get; private set; }

    public AiCommentaryDialogWindow()
    {
        InitializeComponent();

        if (ResolveControl<Button>("CancelBtn") is { } cancelBtn)
        {
            cancelBtn.Click += (_, _) =>
            {
                Result = null;
                Close();
            };
        }

        if (ResolveControl<Button>("GenerateBtn") is { } genBtn)
        {
            genBtn.Click += (_, _) =>
            {
                Result = CollectResult();
                Close();
            };
        }
    }

    private T? ResolveControl<T>(string name) where T : class => this.FindNameScope()?.Find(name) as T;

    private AiCommentaryDialogResult CollectResult()
    {
        CaptionTone tone = CaptionTone.Hype;
        if (ResolveControl<RadioButton>("ToneChillRadio")?.IsChecked == true) tone = CaptionTone.Chill;
        else if (ResolveControl<RadioButton>("ToneFunnyRadio")?.IsChecked == true) tone = CaptionTone.Funny;
        else if (ResolveControl<RadioButton>("ToneTacticalRadio")?.IsChecked == true) tone = CaptionTone.Tactical;

        CaptionStyle style = ResolveControl<RadioButton>("StyleCleanRadio")?.IsChecked == true
            ? CaptionStyle.PopIn
            : CaptionStyle.Karaoke;

        CaptionPosition pos = ResolveControl<RadioButton>("PositionTopRadio")?.IsChecked == true
            ? CaptionPosition.Top
            : CaptionPosition.BottomContent;

        string lang = "English";
        if (ResolveControl<ComboBox>("LanguageCombo") is { SelectedItem: ComboBoxItem item } && item.Content is string s)
        {
            lang = s;
        }

        AiGameProfile profile = AiGameProfile.ThirdPerson;
        if (ResolveControl<ComboBox>("ProfileCombo")?.SelectedIndex is { } pIdx)
        {
            profile = pIdx switch
            {
                1 => AiGameProfile.FirstPerson,
                2 => AiGameProfile.Fortnite,
                3 => AiGameProfile.Generic,
                _ => AiGameProfile.ThirdPerson
            };
        }

        bool entireTimeline = ResolveControl<RadioButton>("ScopeAllRadio")?.IsChecked != false;
        string? notes = ResolveControl<TextBox>("CustomNotesText")?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(notes)) notes = null;

        return new AiCommentaryDialogResult(tone, style, pos, lang, profile, notes, entireTimeline);
    }

    public static async Task<AiCommentaryDialogResult?> ShowDialogAsync(
        Window owner,
        bool hasSelectedSegment,
        CaptionTone initialTone = CaptionTone.Hype)
    {
        var dlg = new AiCommentaryDialogWindow();

        if (dlg.ResolveControl<RadioButton>("ScopeSegmentRadio") is { } segRadio)
        {
            segRadio.IsEnabled = hasSelectedSegment;
            if (!hasSelectedSegment)
            {
                ToolTip.SetTip(segRadio, "No speed block currently selected on the timeline.");
            }
        }

        switch (initialTone)
        {
            case CaptionTone.Chill:
                if (dlg.ResolveControl<RadioButton>("ToneChillRadio") is { } chill) chill.IsChecked = true;
                break;
            case CaptionTone.Funny:
                if (dlg.ResolveControl<RadioButton>("ToneFunnyRadio") is { } funny) funny.IsChecked = true;
                break;
            case CaptionTone.Tactical:
                if (dlg.ResolveControl<RadioButton>("ToneTacticalRadio") is { } tac) tac.IsChecked = true;
                break;
            default:
                if (dlg.ResolveControl<RadioButton>("ToneHypeRadio") is { } hype) hype.IsChecked = true;
                break;
        }

        await dlg.ShowDialog(owner);
        return dlg.Result;
    }
}
