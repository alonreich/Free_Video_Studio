using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;
using Xunit;

namespace FreeVideoStudio.App.Tests;

internal sealed class SampleCaptureDevices : IVoiceCaptureDeviceFactory
{
    public bool HasInputDevice => true;
    public int DeviceCount => 1;
    public IReadOnlyList<string> GetDeviceNames() => ["Synthetic PCM"];
    public IMicMonitorDevice CreateMonitor() => new LiveMonitor();
    public SampleInput Input { get; } = new();
    public VoiceRecorder? Recorder { get; private set; }
    public SampleFile? File { get; private set; }
    public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber)
    {
        Recorder = new VoiceRecorder(outputPath, () => Input,
            () => File = new SampleFile(outputPath));
        return new NAudioVoiceCaptureDevices.RecorderAdapter(Recorder);
    }

    internal sealed class SampleInput : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(44100, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public Action? OnStart;
        public Action? OnStop;
        public int Stops, Disposals;
        public void Deliver(byte[] data) => DataAvailable?.Invoke(this, new(data, data.Length));
        public void Disconnect() => RecordingStopped?.Invoke(this, new(new IOException("test disconnected")));
        public void StartRecording() => OnStart?.Invoke();
        public void StopRecording() { Stops++; OnStop?.Invoke(); RecordingStopped?.Invoke(this, new()); }
        public void Dispose() => Disposals++;
    }

    internal sealed class SampleFile(string path) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)
    {
        public bool FailNext;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailNext)
            {
                FailNext = false;
                base.Write(buffer, offset, Math.Min(3, count));
                throw new IOException("test WAV storage failure");
            }
            base.Write(buffer, offset, count);
        }
    }

    public static byte[] ReadPcm(string path)
    {
        using var reader = new WaveFileReader(path);
        var bytes = new byte[checked((int)reader.Length)];
        Assert.Equal(bytes.Length, reader.Read(bytes, 0, bytes.Length));
        return bytes;
    }
}
