namespace FreeVideoStudio.Core.Media;

/// <summary>VOSAMPLE_01: immutable output anchor established immediately before capture starts.
/// Opening buffers spend the same budget as all later buffers; UI delivery never moves the anchor.</summary>
public sealed class VoiceCaptureBudget(double outputEnd, Func<double> readOutputClock)
{
    public double? OutputStart { get; private set; }
    public long MaximumFrames { get; private set; }
    private bool _exhausted;
    public bool Exhausted { get => Volatile.Read(ref _exhausted); internal set => Volatile.Write(ref _exhausted, value); }

    public void Start(int sampleRate)
    {
        if (OutputStart.HasValue) throw new InvalidOperationException("A capture budget belongs to one take.");
        double start = readOutputClock();
        MaximumFrames = FrameCount(outputEnd, start, sampleRate);
        OutputStart = start;
    }

    public static long FrameCount(double outputEnd, double actualOutputStart, int sampleRate)
    {
        if (!double.IsFinite(outputEnd) || !double.IsFinite(actualOutputStart) || sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(actualOutputStart));
        return checked((long)Math.Floor(Math.Max(0, outputEnd - actualOutputStart) * sampleRate));
    }

    public int AcceptBytes(long writtenBytes, int availableBytes, int blockAlign)
    {
        if (!OutputStart.HasValue) throw new InvalidOperationException("Capture must be anchored before writing PCM.");
        if (blockAlign <= 0 || writtenBytes < 0 || availableBytes < 0) throw new ArgumentOutOfRangeException(nameof(blockAlign));
        long remaining = Math.Max(0, MaximumFrames - writtenBytes / blockAlign);
        return checked((int)(Math.Min(remaining, availableBytes / blockAlign) * blockAlign));
    }
}
