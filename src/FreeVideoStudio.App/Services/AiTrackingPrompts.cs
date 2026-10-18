using System.Globalization;
using System.Text;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIGAME_01 — the AI SMART ZOOM prompts, built from the user's game profile (Settings › AI Features)
/// instead of hard-coding one game and one player skin. Pure text; no I/O.
/// </summary>
public static class AiTrackingPrompts
{
    private static string FootageSentence(AiGameProfile profile) => profile switch
    {
        AiGameProfile.Fortnite => "These come from a Fortnite recording (a third-person shooter).",
        AiGameProfile.ThirdPerson => "These come from a third-person video game recording.",
        AiGameProfile.FirstPerson => "These come from a first-person video game recording.",
        _ => "These come from a video recording (often a video game).",
    };

    /// <summary>The "never this" rule for the player's own character, or empty when the profile has none.</summary>
    public static string ExclusionRule(AiGameProfile profile) => profile switch
    {
        AiGameProfile.ThirdPerson or AiGameProfile.Fortnite =>
            "MANDATORY EXCLUSION — THE PLAYER'S OWN CHARACTER:\n" +
            "The game camera sits behind the player's own character, which is shown large in the lower-centre foreground, usually seen from behind. " +
            "NEVER output, label, track or switch to the player's own character.\n",
        AiGameProfile.FirstPerson =>
            "MANDATORY EXCLUSION — THE PLAYER'S OWN HANDS AND WEAPON:\n" +
            "The player's own arms, hands and held weapon fill the lower foreground. They are NOT targets: never output, label, track or switch to them.\n",
        _ => string.Empty,
    };

    private static string ProfileHints(AiGameProfile profile) => profile switch
    {
        AiGameProfile.Fortnite =>
            "Opponents may be distant, small, crouched on builds or roofs, inside translucent bubble-shield domes, or landing from gliders. Scan those places carefully.\n",
        AiGameProfile.ThirdPerson or AiGameProfile.FirstPerson =>
            "Opponents may be distant, small, partly hidden behind cover, or on rooftops and platforms. Scan carefully.\n",
        _ => string.Empty,
    };

    /// <summary>Multi-angle subject discovery over three stills.</summary>
    public static string Discovery(AiGameProfile profile)
    {
        var sb = new StringBuilder();
        sb.Append("You are an expert computer-vision subject detector.\n");
        sb.Append("Analyse these 3 still frames taken from one short video segment. ").Append(FootageSentence(profile)).Append('\n');
        sb.Append(ExclusionRule(profile));
        sb.Append("ONLY people, characters, players, opponents, animals or vehicles that someone is controlling are subjects. ");
        sb.Append("NEVER output buildings, structures, terrain, trees, statues or other inanimate scenery, and never on-screen HUD elements.\n");
        sb.Append(ProfileHints(profile));
        sb.Append("For each distinct subject output:\n");
        sb.Append("- id: integer starting from 1\n");
        sb.Append("- label: a short, descriptive visual label a person can recognise (for example 'Opponent with shotgun', 'Player in red jacket', 'Car on the left')\n");
        sb.Append("- frame_index: 1, 2 or 3 — the frame where the subject is clearest\n");
        sb.Append("- ymin, xmin, ymax, xmax: integer bounding box on a 0-1000 scale of THAT frame (0,0 is top-left)\n");
        sb.Append("If there are no subjects, output an empty array.");
        return sb.ToString();
    }

    /// <summary>Dense tracking over one video chunk.</summary>
    /// <param name="observedAtSec">Seconds into THIS clip where the box was seen (0 = the first frame).</param>
    /// <param name="intervalSec">0.5 when the request asks for 2 fps sampling, otherwise 1.0.</param>
    public static string Tracking(
        AiGameProfile profile, string label, (int ymin, int xmin, int ymax, int xmax) box,
        double observedAtSec, bool hasReferenceImage, double intervalSec, double clipDurationSec)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("You are a high-precision computer-vision subject tracker. ").Append(FootageSentence(profile).Replace("These come", "This video comes", System.StringComparison.Ordinal)).Append('\n');
        sb.Append("TARGET TO TRACK: '").Append(string.IsNullOrWhiteSpace(label) ? "selected subject" : label.Trim()).Append("'.\n");
        sb.Append("At ").Append(observedAtSec.ToString("0.0", ci)).Append("s into this video the target is inside the box [ymin, xmin, ymax, xmax] = [")
          .Append(box.ymin.ToString(ci)).Append(", ").Append(box.xmin.ToString(ci)).Append(", ")
          .Append(box.ymax.ToString(ci)).Append(", ").Append(box.xmax.ToString(ci)).Append("] on a 0-1000 scale.\n");
        if (hasReferenceImage)
            sb.Append("The attached still image is a close-up of exactly this target. Use it to keep the SAME subject through fast motion, weapon or outfit changes and crossings.\n");
        sb.Append(ExclusionRule(profile));
        sb.Append("Never switch to another subject or to visual clutter.\n");
        sb.Append("TRACKING OUTPUT:\n");
        sb.Append("Report the target every ").Append(intervalSec.ToString("0.0", ci)).Append(" seconds from 0.0 to ")
          .Append(clipDurationSec.ToString("0.0", ci)).Append(" seconds (0.0, ").Append(intervalSec.ToString("0.0", ci)).Append(", …).\n");
        sb.Append("For each time output: time (seconds), visible (true/false), cx and cy (centre, 0-1000), w and h (size, 0-1000).\n");
        sb.Append("If the target is hidden, off-screen, too far to see or not yet in view, output visible=false and null for cx, cy, w and h.");
        return sb.ToString();
    }
}
