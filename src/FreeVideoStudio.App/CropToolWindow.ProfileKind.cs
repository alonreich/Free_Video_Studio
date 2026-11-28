using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class CropToolWindow
{
    internal static readonly IReadOnlyList<(string Label, AiGameProfile? Type)> GameTypeChoices = new (string, AiGameProfile?)[]
    {
        ("Automatic (from the name)", null),
        ("Fortnite", AiGameProfile.Fortnite),
        ("Third-person game", AiGameProfile.ThirdPerson),
        ("First-person game", AiGameProfile.FirstPerson),
        ("Any footage (drone, sport…)", AiGameProfile.Generic),
    };

    private bool _syncingGameType;

    private void BuildProfileKindUi(ComboBox? profileCombo)
    {
        if (this.FindControl<ComboBox>("CropToolGameTypeCombo") is { } gameCombo)
        {
            gameCombo.ItemsSource = GameTypeChoices.Select(c => c.Label).ToList();
            gameCombo.SelectedIndex = 0;
            gameCombo.SelectionChanged += (_, _) => OnGameTypeChosen(gameCombo.SelectedIndex);
        }

        if (this.FindControl<Button>("CreateBlankMaskOverlayBtn") is { } blank)
            blank.Click += async (_, _) => await CreateBlankProfileAsync(profileCombo);
    }

    private void RefreshGameTypeCombo(JsonObject? config, string? profileName)
    {
        if (this.FindControl<ComboBox>("CropToolGameTypeCombo") is not { } gameCombo) return;
        _syncingGameType = true;
        try
        {
            AiGameProfile? stored = HudElements.GameType(config);
            int index = 0;
            for (int i = 0; i < GameTypeChoices.Count; i++)
                if (GameTypeChoices[i].Type == stored) { index = i; break; }
            gameCombo.SelectedIndex = index;
            gameCombo.IsEnabled = !string.IsNullOrWhiteSpace(profileName);
        }
        finally { _syncingGameType = false; }
    }

    private void OnGameTypeChosen(int index)
    {
        if (_syncingGameType || index < 0 || index >= GameTypeChoices.Count) return;
        string? profile = _verifiedEditingProfile;
        if (string.IsNullOrWhiteSpace(profile)) return;
        AiGameProfile? type = GameTypeChoices[index].Type;
        if (!MaskOverlayManager.SetProfileGameType(profile, type))
        {
            SetStatus("Could not save what this profile records. See runtime log.");
            return;
        }
        var reloaded = MaskOverlayManager.LoadProfile(profile);
        _edit.UseFortniteRoles = HudElements.UsesFortniteRoles(reloaded.Success ? reloaded.Config : null, profile)
                                 || _edit.Layers.Any(l => HudElements.IsBuiltIn(l.RoleKey));
        SetStatusSuccess($"\"{profile}\" now records: {GameTypeChoices[index].Label}.");
    }

    private async Task CreateBlankProfileAsync(ComboBox? profileCombo)
    {
        string? newName = NewMaskOverlayName?.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (ValidateNewMaskOverlayName() is { } nameError)
        {
            SetStatus(nameError);
            return;
        }
        if (!await ConfirmUnsavedChangesAsync($"starting the blank profile \"{newName}\"")) return;

        string? created = MaskOverlayManager.CreateBlankProfile(newName);
        if (created == null)
        {
            SetStatus("Could not create that profile. Pick another name.");
            return;
        }

        _changingProfile = true;
        try
        {
            _edit.Dirty = false;
            var updated = MaskOverlayManager.GetAvailableProfiles();
            if (profileCombo != null)
            {
                profileCombo.ItemsSource = updated.Where(p => !MaskOverlayManager.IsNoMask(p)).ToList();
                profileCombo.SelectedItem = created;
            }
            RefreshMaskOverlayNameCache(updated);
            NewMaskOverlayName = string.Empty;
            await OnProfileChosenAsync(created);
        }
        finally { _changingProfile = false; }
        SetStatusSuccess($"Blank profile \"{created}\" is ready. Draw a box around each HUD part you want to keep.");
    }
}
