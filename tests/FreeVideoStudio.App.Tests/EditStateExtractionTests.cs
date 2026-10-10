using System.Reflection;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Editing;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// EDITSTATE_01 — the durable edit state of the Granular editor and the Crop Tool lives in
/// <c>Core/Editing</c>, not in the windows. The behaviour is unit-tested in Core
/// (<c>GranularEditSessionTests</c>, <c>CropEditSessionTests</c>); these pin the WINDOW side, which a
/// compiler is happy to let drift back: a private field holding the segment list or the dirty flag
/// compiles exactly as well as the session does.
/// </summary>
public sealed class EditStateExtractionTests
{
    private static string Src(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", file));

    private static string GranularWindow() => string.Concat(
        Directory.GetFiles(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App"), "GranularSpeedEditorWindow*.cs").Select(File.ReadAllText));

    private static string CropWindow() => string.Concat(
        Directory.GetFiles(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App"), "CropToolWindow*.cs").Select(File.ReadAllText));

    [Fact]
    public void GranularWindowNoLongerDeclaresDurableEditState()
    {
        string code = GranularWindow();
        foreach (string field in new[] { "_segments", "_cuts", "_memes", "_baseSpeed", "_freezeTimeMs", "_freezeDurationS",
                                         "_selectedSegmentIndex", "_selectedMemeId", "_nextMemeIdIndex", "_pendingStartMs",
                                         "_pendingEndMs", "_openingSignature", "_historyBaseline", "_parkedHistory" })
            Assert.DoesNotMatch(@"\b(private|internal)\b[^;=(]*\s" + Regex.Escape(field) + @"\s*(=|;)", code);

        Assert.Contains("GranularEditSession _edit", code);
        Assert.DoesNotMatch(@"\bnew\s+GranularEditHistory\s*\(", code);   // one history, owned by the session
    }

    [Fact]
    public void CropWindowNoLongerDeclaresDurableEditState()
    {
        string code = CropWindow();
        foreach (string field in new[] { "_dirty", "_activeProfile", "_deletedRoleKeys", "_customRoles", "_selectedItem",
                                         "_originalResolution", "_captureResolutionKnown", "_existingMaskOverlayNames" })
            Assert.DoesNotMatch(@"\b(private|internal)\b[^;=(]*\s" + Regex.Escape(field) + @"\s*(=|;)", code);

        Assert.Contains("CropEditSession _edit", code);
        Assert.DoesNotMatch(@"\bnew\s+UndoStack\s*<", code);              // UNDO_27 stack is the session's
    }

    [Fact]
    public void MagicWandStaysOutOfTheEditState()
    {
        // AIHUD_01 / UNDO_27 — the wand's scan, AI request, cancellation and candidates are window
        // state; only a COMMITTED candidate (AddCurrentSelection) reaches the session.
        string wand = Src("CropToolWindow.MagicWand.cs");
        Assert.DoesNotMatch(@"_edit\s*\.\s*(AddLayer|Record|DeleteSelected|ApplySnapshot|MoveSelectedLayer|ResetWorking)\b", wand);

        foreach (FieldInfo f in typeof(CropEditSession).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            string name = f.FieldType.FullName ?? "";
            Assert.DoesNotContain("Candidate", name);
            Assert.DoesNotContain("Gemini", name);
            Assert.DoesNotContain("System.Threading", name);
        }
    }

    [Fact]
    public void SessionsAreConstructedWithExplicitDependencies_NotTheServiceLocator()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.Core", "Editing"), "*.cs"))
            Assert.DoesNotContain("AppServices", File.ReadAllText(file));
    }
}
