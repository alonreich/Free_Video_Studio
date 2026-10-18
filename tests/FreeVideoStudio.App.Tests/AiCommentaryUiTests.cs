using System;
using System.Collections.Generic;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// AICOMMENTARY_UI_01, AIUNDO_01 — unit tests for AI Commentary dialog models,
/// consent persistence, and session undo/redo parity.
/// </summary>
public sealed class AiCommentaryUiTests
{
    [Fact]
    public void AiCommentaryDialogResult_StoresAndPreservesConfiguration()
    {
        var result = new AiCommentaryDialogResult(
            CaptionTone.Tactical,
            CaptionStyle.Karaoke,
            CaptionPosition.BottomContent,
            "English",
            AiGameProfile.FirstPerson,
            "Focus on headshots and sniper calls",
            EntireTimeline: true);

        Assert.Equal(CaptionTone.Tactical, result.Tone);
        Assert.Equal(CaptionStyle.Karaoke, result.Style);
        Assert.Equal(CaptionPosition.BottomContent, result.Position);
        Assert.Equal("English", result.Language);
        Assert.Equal(AiGameProfile.FirstPerson, result.Profile);
        Assert.Equal("Focus on headshots and sniper calls", result.CustomNotes);
        Assert.True(result.EntireTimeline);
    }

    [Fact]
    public void AiCommentaryCloudConsent_TogglePersistsInSettings()
    {
        bool original = SettingsManager.Instance.AiCommentaryCloudConsent;
        try
        {
            SettingsManager.Update(s => s.AiCommentaryCloudConsent = true);
            Assert.True(SettingsManager.Instance.AiCommentaryCloudConsent);

            SettingsManager.Update(s => s.AiCommentaryCloudConsent = false);
            Assert.False(SettingsManager.Instance.AiCommentaryCloudConsent);
        }
        finally
        {
            SettingsManager.Update(s => s.AiCommentaryCloudConsent = original);
        }
    }

    [Fact]
    public void AiUndo01_GranularSession_CapturesAndRestoresCaptionTrackOnUndoRedo()
    {
        var parking = new GranularHistoryParking();
        var session = new GranularEditSession("C:\\test\\clip.mp4", 0, 10000, parking);

        Assert.Null(session.CaptionTrack);

        var originalSnapshot = session.Capture();
        Assert.Null(originalSnapshot.CaptionTrack);

        var candidateTrack = new CaptionTrack(
            CaptionStyle.Karaoke,
            CaptionPosition.BottomContent,
            CaptionTone.Hype,
            "en",
            new List<CaptionLine>
            {
                new(1, 0.5, 2.0, "Let's go!", new[] { "go" }, 0.95),
                new(2, 2.5, 4.0, "Triple elimination!", new[] { "Triple" }, 0.98)
            });

        session.CaptionTrack = candidateTrack;
        Assert.NotNull(session.CaptionTrack);
        Assert.Equal(2, session.CaptionTrack.Lines!.Count);

        session.CaptionTrack = originalSnapshot.CaptionTrack;
        Assert.Null(session.CaptionTrack);
        Assert.Equal(0, session.History.UndoCount);

        session.CaptionTrack = candidateTrack;

        session.CaptionTrack = originalSnapshot.CaptionTrack;
        session.Checkpoint("apply AI commentary captions");
        session.CaptionTrack = candidateTrack;

        Assert.Equal(1, session.History.UndoCount);
        Assert.Equal(2, session.CaptionTrack.Lines!.Count);

        var undoStep = session.Undo();
        Assert.NotNull(undoStep);
        Assert.Null(session.CaptionTrack);
        Assert.Equal(0, session.History.UndoCount);
        Assert.Equal(1, session.History.RedoCount);

        var redoStep = session.Redo();
        Assert.NotNull(redoStep);
        Assert.NotNull(session.CaptionTrack);
        Assert.Equal(2, session.CaptionTrack.Lines!.Count);
        Assert.Equal(1, session.History.UndoCount);
        Assert.Equal(0, session.History.RedoCount);
    }

    [Fact]
    public void GranularEditorSnapshot_EqualityAccountsForCaptionTrack()
    {
        var trackA = new CaptionTrack(
            CaptionStyle.Karaoke,
            CaptionPosition.BottomContent,
            CaptionTone.Hype,
            "en",
            new List<CaptionLine> { new(1, 1.0, 2.0, "Line 1") });

        var trackB = new CaptionTrack(
            CaptionStyle.PopIn,
            CaptionPosition.Top,
            CaptionTone.Chill,
            "es",
            new List<CaptionLine> { new(1, 1.0, 2.0, "Línea 1") });

        var parking = new GranularHistoryParking();
        var session = new GranularEditSession("C:\\test\\clip.mp4", 0, 5000, parking);

        session.CaptionTrack = trackA;
        var snapA = session.Capture();

        session.CaptionTrack = trackB;
        var snapB = session.Capture();

        Assert.False(snapA.Equals(snapB));
        Assert.NotEqual(snapA.GetHashCode(), snapB.GetHashCode());

        session.CaptionTrack = trackA;
        var snapA2 = session.Capture();
        Assert.True(snapA.Equals(snapA2));
        Assert.Equal(snapA.GetHashCode(), snapA2.GetHashCode());
    }
}
