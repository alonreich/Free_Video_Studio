namespace FreeVideoStudio.App.Models;

public sealed record EditorPreviewLayers(
    MusicWizardResult? Music,
    VoiceOverWindow.VoiceOverResult? VoiceOver,
    bool ShowTeammates = false,
    bool ShowSpectating = true)
{
    public static readonly EditorPreviewLayers None = new(null, null);

    public bool HasMusic => Music != null && !string.IsNullOrEmpty(Music.MusicFilePath);

    public bool HasVoiceOver => VoiceOver != null &&
        ((VoiceOver.VoiceOverTakes?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(VoiceOver.VoiceOverWavPath));
}
