using System.Globalization;

namespace FreeVideoStudio.Core.Media;

public static class VoiceOverLevel
{
    public static double Clamp(double gain) => double.IsFinite(gain) ? Math.Clamp(gain, 0, 1.5) : 1;

    public static float PreviewGain(double gain, double master) => (float)(Clamp(gain) * master);

    public static string Filter(double gain) =>
        "pan=stereo|FL=FL+FC|FR=FR+FC,volume=" + Clamp(gain).ToString("0.########", CultureInfo.InvariantCulture);
}
