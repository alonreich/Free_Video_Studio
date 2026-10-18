using FreeVideoStudio.Core.Media;
using NAudio.Wave;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public class VoiceCaptureBudgetTests
{
    [Fact]
    public void ExactEndpoint_StopsOnceAndPublishesExactlyTheSavedPcm()
    {
        var input = new Input();
        var stream = new MemoryStream();
        using var recorder = new VoiceRecorder("test.wav", () => input, () => stream)
        { Budget = new VoiceCaptureBudget(0.02, () => 0) };
        int stops = 0;
        recorder.StopRequested += (_, _) => stops++;
        using var published = new MemoryStream();
        recorder.PcmAvailable += (_, pcm) => published.Write(pcm.Data, 0, pcm.Count);
        recorder.StartRecording();
        byte[] first = Enumerable.Repeat((byte)0x12, 882).ToArray();
        byte[] last = Enumerable.Repeat((byte)0x34, 882).ToArray();
        input.Deliver(first);
        Assert.Equal(0, stops);
        input.Deliver(last);
        Assert.Equal(1, stops);
        input.Deliver(Enumerable.Repeat((byte)0x71, 882).ToArray());
        recorder.StopRecording();
        Assert.Equal(1764, recorder.BytesCaptured);
        using var reader = new WaveFileReader(new MemoryStream(stream.ToArray()));
        byte[] decoded = new byte[1764];
        Assert.Equal(decoded.Length, reader.Read(decoded, 0, decoded.Length));
        Assert.Equal(first.Concat(last).ToArray(), decoded);
        Assert.Equal(decoded, published.ToArray());
    }
    [Theory]
    [InlineData(1, 0, 44100, 44100)]
    [InlineData(1, 0.75, 48000, 12000)]
    [InlineData(1, 1, 48000, 0)]
    [InlineData(1, 2, 48000, 0)]
    [InlineData(0.00003, 0, 44100, 1)]
    public void WholeFrames(double end, double start, int rate, long expected)
        => Assert.Equal(expected, VoiceCaptureBudget.FrameCount(end, start, rate));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void BudgetUsesBlockAlignment(int channels)
    {
        var budget = new VoiceCaptureBudget(0.25, () => 0);
        budget.Start(48000);
        int align = channels * 3;
        Assert.Equal(12000 * align, budget.AcceptBytes(0, 48000 * align + 1, align));
        Assert.Equal(0, budget.AcceptBytes(12000 * align, align * 10, align));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0.25, 0, 1)]
    [InlineData(0.25, 0, 2)]
    [InlineData(1, 0.75, 1)]
    [InlineData(0.00003, 0, 1)]
    public void RealWriter_CrossingAndDrainBuffersCannotSavePostEndPulse(double end, double start, int channels)
    {
        var input = new Input { WaveFormat = new WaveFormat(44100, 16, channels) };
        var stream = new MemoryStream();
        using var recorder = new VoiceRecorder("test.wav", () => input, () => stream)
        { Budget = new VoiceCaptureBudget(end, () => start) };
        int requested = 0;
        recorder.StopRequested += (_, _) => requested++;
        long published = 0;
        recorder.PcmAvailable += (_, pcm) => published += pcm.Count;
        recorder.StartRecording();
        int expected = (int)VoiceCaptureBudget.FrameCount(end, start, 44100) * input.WaveFormat.BlockAlign;
        var data = new byte[expected + 400];
        Array.Fill(data, (byte)0x71, expected, 400);
        input.Deliver(data);
        input.Deliver(Enumerable.Repeat((byte)0x71, 600).ToArray());
        input.Drain = () => input.Deliver(Enumerable.Repeat((byte)0x71, 800).ToArray());
        recorder.StopRecording();
        Assert.Equal(1, requested);
        Assert.Equal(expected, recorder.BytesCaptured);
        Assert.Equal(expected, published);
        Assert.Null(recorder.CaptureError);
        using var reader = new WaveFileReader(new MemoryStream(stream.ToArray()));
        Assert.Equal(expected, reader.Length);
        var decoded = new byte[expected + 100];
        Assert.Equal(expected, reader.Read(decoded, 0, decoded.Length));
        Assert.All(decoded, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task WriterFailure_PreservesAcknowledgedPrefix_ReportsOnce_StopsAndDisposesIdempotently()
    {
        var input = new Input();
        var stream = new FailingStream();
        using var recorder = new VoiceRecorder("test.wav", () => input, () => stream);
        int failures = 0, stops = 0;
        recorder.Stopped += (_, _) => failures++;
        recorder.StopRequested += (_, _) => stops++;
        recorder.StartRecording();
        byte[] prefix = Enumerable.Repeat((byte)0x23, 882).ToArray();
        input.Deliver(prefix);
        stream.FailNext = true;
        input.Deliver(Enumerable.Repeat((byte)0x71, 882).ToArray());
        input.Deliver(new byte[882]);
        Assert.IsType<IOException>(recorder.CaptureError);
        Assert.Equal(1, failures);
        Assert.Equal(1, stops);
        await Task.WhenAll(Task.Run(recorder.StopRecording), Task.Run(recorder.Dispose), Task.Run(recorder.StopRecording));
        Assert.Equal(1, input.Disposals);
        Assert.Equal(prefix.Length, recorder.BytesCaptured);
        Assert.Null(recorder.FileFinalizationError);
        using var reader = new WaveFileReader(new MemoryStream(stream.ToArray()));
        Assert.Equal(prefix.Length, reader.Length);
        byte[] decoded = new byte[prefix.Length];
        Assert.Equal(decoded.Length, reader.Read(decoded, 0, decoded.Length));
        Assert.Equal(prefix, decoded);
    }

    [Fact]
    public void SubscriberFailure_DoesNotBecomeStorageFault()
    {
        var input = new Input();
        var stream = new MemoryStream();
        using var recorder = new VoiceRecorder("test.wav", () => input, () => stream);
        recorder.VolumeChanged += (_, _) => throw new InvalidOperationException("UI callback");
        long pcm = 0;
        recorder.PcmAvailable += (_, b) => pcm += b.Count;
        recorder.StartRecording();
        input.Deliver(new byte[882]);
        recorder.StopRecording();
        Assert.Null(recorder.CaptureError);
        Assert.Equal(882, pcm);
    }

    private sealed class Input : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(44100, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public Action? Drain;
        public int Disposals;
        public void StartRecording() { }
        public void Deliver(byte[] data) => DataAvailable?.Invoke(this, new(data, data.Length));
        public void StopRecording() { Drain?.Invoke(); RecordingStopped?.Invoke(this, new()); }
        public void Dispose() => Disposals++;
    }

    private sealed class FailingStream : MemoryStream
    {
        public bool FailNext;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailNext)
            {
                FailNext = false;
                base.Write(buffer, offset, Math.Min(3, count));
                throw new IOException("test storage failure after partial write");
            }
            base.Write(buffer, offset, count);
        }
    }
}
