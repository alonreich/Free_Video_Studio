using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public sealed class VideoRenderMode
{
    private static VideoRenderMode? s_instance;

    public static VideoRenderMode Current =>
        s_instance ?? throw new InvalidOperationException("VideoRenderMode not initialized. Call Initialize() at startup.");

    public bool UseHardwareAcceleration { get; }

    public string RendererName { get; }

    public string FailureReason { get; }

    private VideoRenderMode(GpuCapabilityProbe.Result result)
    {
        UseHardwareAcceleration = result.UseHardwareAcceleration;
        RendererName = result.RendererName;
        FailureReason = result.FailureReason;
    }

    public static VideoRenderMode Initialize()
    {
        if (s_instance != null)
            return s_instance;

        var shared = HardwareCapability.TryLoadRenderMode();
        if (shared.HasValue)
        {
            var reused = new GpuCapabilityProbe.Result(
                shared.Value.UseHardwareAcceleration,
                shared.Value.RendererName,
                shared.Value.DriverVersion,
                shared.Value.FailureReason);
            CoreLogger.Info("GPU",
                $"GPU probe skipped — reusing the suite-wide result: {(reused.UseHardwareAcceleration ? "hardware" : "software")} ({reused.RendererName}).");
            s_instance = new VideoRenderMode(reused);
            return s_instance;
        }

        var result = GpuCapabilityProbe.Probe();
        HardwareCapability.SaveRenderMode(result);
        s_instance = new VideoRenderMode(result);
        return s_instance;
    }

    public override string ToString()
    {
        return UseHardwareAcceleration
            ? $"Hardware ({RendererName})"
            : $"Software ({FailureReason})";
    }
}