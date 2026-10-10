using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

// Independent audit regression: no hardware, files, sleeps, or production changes.
public sealed class MicrophoneSpectrumAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstInFlightSpectrum_MustNotPublishAfterStopOrFault(bool fault)
    {
        var devices = new Devices();
        using var session = new VoiceCaptureSession(devices, action => action());
        await session.StartMonitorAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(session.LatestSpectrum);
        int published = 0;
        session.SpectrumAvailable += (_, _) => Interlocked.Increment(ref published);

        // Hold the analysis gate, allowing the real PCM handler to pass admission
        // but preventing its first FFT from completing until the source stops.
        object gate = typeof(VoiceCaptureSession).GetField("_spectrumGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try { devices.Monitor.EmitPcm(); }
            catch (Exception ex) { workerError = ex; }
        }) { IsBackground = true };

        Monitor.Enter(gate);
        try
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(
                () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)), "PCM callback never reached the held analysis gate.");
            devices.Monitor.EmitStop(fault ? new InvalidOperationException("driver failed") : null);
            Assert.Equal(fault ? VoiceCaptureState.Faulted : VoiceCaptureState.Idle, session.State);
        }
        finally { Monitor.Exit(gate); }

        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "PCM callback did not finish.");
        Assert.Null(workerError);
        Assert.True(session.LatestSpectrum == null && published == 0,
            $"Stopped source published a spectrum: state={session.State}, latest={session.LatestSpectrum != null}, events={published}.");
    }

    private sealed class Devices : IVoiceCaptureDeviceFactory
    {
        public TestMonitor Monitor { get; } = new();
        public bool HasInputDevice => true;
        public int DeviceCount => 1;
        public IReadOnlyList<string> GetDeviceNames() => new[] { "Audit fake" };
        public IMicMonitorDevice CreateMonitor() => Monitor;
        public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber)
            => throw new NotSupportedException("No recording in this test.");
    }

    private sealed class TestMonitor : IMicMonitorDevice
    {
        public event EventHandler<float>? LevelChanged { add { } remove { } }
        public event EventHandler<Exception?>? Stopped;
        public event EventHandler<PcmBuffer>? PcmAvailable;
        public bool IsRunning { get; private set; }
        public int BuffersSeen => 0;
        public void Start(int deviceNumber) => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
        public void EmitStop(Exception? error) { IsRunning = false; Stopped?.Invoke(this, error); }
        public void EmitPcm()
        {
            var bytes = new byte[8192];
            PcmAvailable?.Invoke(this, new PcmBuffer(bytes, bytes.Length, PcmCaptureFormat.ProductionMicrophone));
        }
    }
}
