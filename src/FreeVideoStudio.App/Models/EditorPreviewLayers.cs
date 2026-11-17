namespace FreeVideoStudio.App.Models;

public sealed record EditorPreviewLayers(
    MusicWizardResult? Music,
    bool KeepMusicDuringMeme,
    VoiceOverWindow.VoiceOverResult? VoiceOver)
{
    public static readonly EditorPreviewLayers None = new(null, false, null);

    public bool HasMusic => Music != null && !string.IsNullOrEmpty(Music.MusicFilePath);

    public bool HasVoiceOver => VoiceOver != null &&
        ((VoiceOver.VoiceOverTakes?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(VoiceOver.VoiceOverWavPath));
}
