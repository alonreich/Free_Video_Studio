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

    /// <summary>AIPROFILE_ONE_01 — the combo order: index ↔ profile (the single place the mapping lives).</summary>
    internal static int ProfileToIndex(AiGameProfile p) => p switch
    {
        AiGameProfile.FirstPerson => 1,
        AiGameProfile.Fortnite => 2,
        AiGameProfile.Generic => 3,
        _ => 0,
    };

    public static async Task<AiCommentaryDialogResult?> ShowDialogAsync(
        Window owner,
        bool hasSelectedSegment,
        CaptionTone initialTone = CaptionTone.Hype,
        AiGameProfile initialProfile = AiGameProfile.ThirdPerson)
    {
        var dlg = new AiCommentaryDialogWindow();

        // AIPROFILE_ONE_01 — start on the game type chosen in Settings › AI Features ("What are you recording?");
        // the editor writes the choice back, so there is ONE game-type setting for every AI feature.
        if (dlg.ResolveControl<ComboBox>("ProfileCombo") is { } profileCombo)
            profileCombo.SelectedIndex = ProfileToIndex(initialProfile);

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

        dlg.ApplyRemembered(hasSelectedSegment);

        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    /// <summary>
    /// AICOMMENTARY_REMEMBER_01 — opens on the options the user chose last time (tone, caption style, placement,
    /// language, scope, notes). Anything never chosen keeps the window's default; "Selected block" falls back to
    /// "Entire timeline" when no block is selected now.
    /// </summary>
    private void ApplyRemembered(bool hasSelectedSegment)
    {
        var s = Infrastructure.SettingsManager.Instance;
        void Check(string name) { if (ResolveControl<RadioButton>(name) is { } r) r.IsChecked = true; }

        if (Enum.TryParse(s.AiCommentaryLastTone, true, out CaptionTone tone))
            Check(tone switch
            {
                CaptionTone.Chill => "ToneChillRadio",
                CaptionTone.Funny => "ToneFunnyRadio",
                CaptionTone.Tactical => "ToneTacticalRadio",
                _ => "ToneHypeRadio",
            });
        if (Enum.TryParse(s.AiCommentaryLastStyle, true, out CaptionStyle style))
            Check(style == CaptionStyle.PopIn ? "StyleCleanRadio" : "StyleKaraokeRadio");
        if (Enum.TryParse(s.AiCommentaryLastPosition, true, out CaptionPosition pos))
            Check(pos == CaptionPosition.Top ? "PositionTopRadio" : "PositionBottomRadio");
        if (!string.IsNullOrWhiteSpace(s.AiCommentaryLastLanguage) && ResolveControl<ComboBox>("LanguageCombo") is { } lang)
        {
            for (int i = 0; i < lang.ItemCount; i++)
                if (lang.Items[i] is ComboBoxItem { Content: string c } && string.Equals(c, s.AiCommentaryLastLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    lang.SelectedIndex = i;
                    break;
                }
        }
        if (s.AiCommentaryLastEntireTimeline is bool entire)
            Check(entire || !hasSelectedSegment ? "ScopeAllRadio" : "ScopeSegmentRadio");
        if (!string.IsNullOrWhiteSpace(s.AiCommentaryLastNotes) && ResolveControl<TextBox>("CustomNotesText") is { } notes)
            notes.Text = s.AiCommentaryLastNotes;
    }

    /// <summary>AICOMMENTARY_REMEMBER_01 — stores what the user just chose, for the next time the window opens.</summary>
    public static void Remember(AiCommentaryDialogResult r)
    {
        Infrastructure.SettingsManager.Update(s =>
        {
            s.AiCommentaryLastTone = r.Tone.ToString();
            s.AiCommentaryLastStyle = r.Style.ToString();
            s.AiCommentaryLastPosition = r.Position.ToString();
            s.AiCommentaryLastLanguage = r.Language;
            s.AiCommentaryLastEntireTimeline = r.EntireTimeline;
            s.AiCommentaryLastNotes = r.CustomNotes ?? "";
            s.AiTrackingGameProfile = r.Profile.ToString();
        });
    }
}
