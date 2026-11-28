using System;
using System.Text.Json.Nodes;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App.Services;

public sealed class ProjectRecoveryService
{
    private readonly RecoveryManager _recovery;

    public ProjectRecoveryService(ApplicationPaths? paths = null)
    {
        _recovery = new RecoveryManager(paths ?? ApplicationPaths.CreateDefault());
    }

    public void SaveState(ProjectDocument document, JsonObject? transientMetadata, bool sync = false)
    {
        var state = RecoveryEnvelope.Write(document, DateTimeOffset.UtcNow, transientMetadata);
        if (sync)
            _recovery.SaveState(state);
        else
            _recovery.SaveStateAsync(state);
    }

    public RecoverySnapshot? LoadSnapshot()
    {
        JsonObject? raw = _recovery.LoadState();
        if (raw is null) return null;

        if (RecoveryEnvelope.IsEnvelope(raw))
        {
            if (!RecoveryEnvelope.TryRead(raw, out ProjectDocument? document, out JsonObject? transient, out string? error))
            {
                CoreLogger.Fail("Recovery", $"Recovery envelope rejected: {error}");
                return null;
            }
            return new RecoverySnapshot(document, transient, raw, IsLegacy: false, LegacyRaw: null);
        }

        return new RecoverySnapshot(null, null, raw, IsLegacy: true, LegacyRaw: raw);
    }

    public void ClearState() => _recovery.ClearState();

    public void ReleaseLockOnly() => _recovery.ReleaseLockOnly();

    public bool HasUnsavedWork(MainViewModel mainVm, TimelineViewModel timelineVm, ExportViewModel exportVm)
    {
        if (mainVm.ExportedCleanSinceLastEdit) return false;

        if (string.IsNullOrWhiteSpace(mainVm.LoadedVideoPath)) return false;

        var defaults = SettingsManager.Instance.Defaults;

        bool memeSelected = mainVm.IsAddMeme || mainVm.SelectedMemeItem != null;

        bool noMask = MaskOverlayManager.IsNoMask(SettingsManager.Instance.ActiveMaskOverlay);
        bool hudToggled = mainVm.HudToggles.Count > 0
            ? mainVm.HudToggles.Any(t => t.IsOn != defaults.StartHudElement(mainVm.HudProfileName ?? string.Empty, t.Key))
            : mainVm.IsTeammates != (!noMask && defaults.StartShowTeammates) || mainVm.IsSpectating != !noMask;

        bool exportTogglesChanged = mainVm.IsPortraitMode != defaults.StartPortrait
                                 || mainVm.IsEnableFade != defaults.StartEnableFade;

        bool speedChanged = Math.Abs(timelineVm.BaseSpeed - defaults.StartSpeed) > 0.01;

        return timelineVm.IsTrimStartSet || timelineVm.IsTrimEndSet || timelineVm.IsThumbnailSet ||
               mainVm.IsGranularSpeedActive || mainVm.IsMusicActive || mainVm.VoiceOverResult != null ||
               mainVm.MusicWizardResult != null ||
               mainVm.CaptionTrack is { Lines.Count: > 0 } ||
               timelineVm.Cuts.Count > 0 ||
               timelineVm.MemePlacements.Count > 0 ||
               timelineVm.SpeedSegments.Count > 0 || timelineVm.FreezeTimeMs >= 0 ||
               speedChanged ||
               memeSelected || hudToggled || exportTogglesChanged ||
               !string.IsNullOrWhiteSpace(mainVm.OverlayText);
    }
}

public sealed record RecoverySnapshot(
    ProjectDocument? Document,
    JsonObject? TransientMetadata,
    JsonObject Raw,
    bool IsLegacy,
    JsonObject? LegacyRaw);
