using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public class IpcTests : IDisposable
{
    private readonly string _testDir;
    private readonly ApplicationPaths _paths;

    public IpcTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "FvsIpcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _paths = new ApplicationPaths(_testDir);
        _paths.EnsureWritableDirectories();
    }

    public void Dispose()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }


    [Fact]
    public void IpcProtocol_RoundtripFrame_EncodesAndDecodesFaithfully()
    {
        using var stream = new MemoryStream();
        string json = "{\"MainWindowBounds\":{\"X\":100,\"Y\":200,\"Width\":1280,\"Height\":720}}";
        byte[] payload = Encoding.UTF8.GetBytes(json);

        IpcProtocol.WriteFrame(stream, IpcOpcode.UpdateProperties, payload);
        stream.Position = 0;

        var frame = IpcProtocol.ReadFrame(stream);
        Assert.NotNull(frame);
        Assert.Equal(IpcOpcode.UpdateProperties, frame.Value.Opcode);
        Assert.Equal(payload, frame.Value.Payload);

        string decodedJson = Encoding.UTF8.GetString(frame.Value.Payload);
        Assert.Equal(json, decodedJson);
    }

    [Fact]
    public async Task IpcProtocol_RoundtripFrameAsync_EncodesAndDecodesFaithfully()
    {
        using var stream = new MemoryStream();
        var obj = new JsonObject
        {
            ["returned_from_crop_tool"] = true,
            ["MainVolume"] = 85.5
        };

        await IpcProtocol.WriteJsonFrameAsync(stream, IpcOpcode.Handoff, obj);
        stream.Position = 0;

        var frame = await IpcProtocol.ReadFrameAsync(stream);
        Assert.NotNull(frame);
        Assert.Equal(IpcOpcode.Handoff, frame.Value.Opcode);

        var decodedObj = IpcProtocol.FromUtf8Bytes(frame.Value.Payload);
        Assert.NotNull(decodedObj);
        Assert.True(decodedObj["returned_from_crop_tool"]?.GetValue<bool>());
        Assert.Equal(85.5, decodedObj["MainVolume"]?.GetValue<double>());
    }

    [Fact]
    public void IpcProtocol_CorruptedMagic_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        byte[] badHeader = new byte[IpcProtocol.HeaderSize];
        badHeader[0] = 0xDE;
        badHeader[1] = 0xAD;
        badHeader[2] = 0xBE;
        badHeader[3] = 0xEF;
        stream.Write(badHeader);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => IpcProtocol.ReadFrame(stream));
    }

    [Fact]
    public void IpcProtocol_UnsupportedVersion_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        byte[] badVersionHeader = new byte[IpcProtocol.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badVersionHeader.AsSpan(0, 4), IpcProtocol.Magic);
        badVersionHeader[4] = 99;
        stream.Write(badVersionHeader);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => IpcProtocol.ReadFrame(stream));
    }

    [Fact]
    public void IpcProtocol_ExcessivePayloadLength_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        byte[] header = new byte[IpcProtocol.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), IpcProtocol.Magic);
        header[4] = IpcProtocol.Version;
        header[5] = (byte)IpcOpcode.GetState;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(6, 4), IpcProtocol.MaxPayloadSize + 1024);
        stream.Write(header);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => IpcProtocol.ReadFrame(stream));
    }

    [Fact]
    public void IpcProtocol_TruncatedPayload_ThrowsEndOfStreamException()
    {
        using var stream = new MemoryStream();
        byte[] header = new byte[IpcProtocol.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), IpcProtocol.Magic);
        header[4] = IpcProtocol.Version;
        header[5] = (byte)IpcOpcode.UpdateProperties;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(6, 4), 50);
        stream.Write(header);
        stream.Write(new byte[10]);
        stream.Position = 0;

        Assert.Throws<EndOfStreamException>(() => IpcProtocol.ReadFrame(stream));
    }


    [Fact]
    public async Task IpcServerAndClient_HandoffRoundtrip_CompletesUnder5Milliseconds()
    {
        var initialState = new JsonObject
        {
            ["schema_version"] = 1,
            ["MainVolume"] = 100.0
        };

        using var server = NamedPipeStateServer.TryStart(_paths, initialState);
        Assert.NotNull(server);
        Assert.True(server.IsRunning);

        var handoff = new JsonObject
        {
            ["returned_from_crop_tool"] = true,
            ["UploadVideoDirectory"] = "C:\\Videos\\Clips",
            ["MainVolume"] = 80.0
        };

        var sw = Stopwatch.StartNew();
        bool updated = await NamedPipeStateClient.UpdatePropertiesAsync(handoff, TimeSpan.FromSeconds(2));
        var state = await NamedPipeStateClient.GetStateAsync(TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.True(updated);
        Assert.NotNull(state);
        Assert.True(state["returned_from_crop_tool"]?.GetValue<bool>());
        Assert.Equal("C:\\Videos\\Clips", state["UploadVideoDirectory"]?.GetValue<string>());
        Assert.Equal(80.0, state["MainVolume"]?.GetValue<double>());

        Assert.True(sw.ElapsedMilliseconds <= 100, $"IPC roundtrip took {sw.ElapsedMilliseconds} ms (target was <= 100ms in CI/test runner).");
    }

    [Fact]
    public void IpcServer_InProcessStateAccess_TakesSubMillisecond()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1 });
        Assert.NotNull(server);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            server.UpdateProperties(new JsonObject { ["MainVolume"] = 50.0 + i });
            var state = server.GetState();
            Assert.Equal(50.0 + i, state["MainVolume"]?.GetValue<double>());
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 10, $"50 in-memory operations took {sw.ElapsedMilliseconds} ms.");
    }


    [Fact]
    public async Task IpcStressTest_RapidConcurrentUpdates_ZeroLockExceptions()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1 });
        Assert.NotNull(server);

        int clientCount = 10;
        int operationsPerClient = 10;
        var tasks = new List<Task>();

        for (int c = 0; c < clientCount; c++)
        {
            int clientId = c;
            tasks.Add(Task.Run(async () =>
            {
                var store = new StateTransferStore(_paths);
                for (int i = 0; i < operationsPerClient; i++)
                {
                    await store.UpdatePropertiesAsync(new JsonObject
                    {
                        ["UploadVideoDirectory"] = $"C:\\Path_{clientId}_{i}"
                    });

                    var current = await store.LoadAsync();
                    Assert.NotNull(current);
                }
            }));
        }

        await Task.WhenAll(tasks);

        var finalState = server.GetState();
        Assert.NotNull(finalState);
        Assert.True(finalState.ContainsKey("UploadVideoDirectory"));
    }


    [Fact]
    public async Task StateTransferStore_WhenNoServerRunning_FallsBackToDisk()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();

        var diskPayload = new JsonObject
        {
            ["schema_version"] = 1,
            ["MainVolume"] = 42.0,
            ["UploadVideoDirectory"] = "C:\\Fallback"
        };
        AtomicJsonFile.WriteObject(_paths.SessionStateFile, diskPayload);

        var store = new StateTransferStore(_paths);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(42.0, loaded["MainVolume"]?.GetValue<double>());
        Assert.Equal("C:\\Fallback", loaded["UploadVideoDirectory"]?.GetValue<string>());
    }

    [Fact]
    public async Task StateTransferStore_AsyncFallback_ReadsDiskWithoutError()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();

        var diskPayload = new JsonObject
        {
            ["schema_version"] = 1,
            ["MainVolume"] = 77.0
        };
        AtomicJsonFile.WriteObject(_paths.SessionStateFile, diskPayload);

        var store = new StateTransferStore(_paths);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(77.0, loaded["MainVolume"]?.GetValue<double>());
    }

    [Fact]
    public async Task StateTransferStore_SendHandoffAsync_WorksAcrossProcesses()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1 });
        Assert.NotNull(server);

        var store = new StateTransferStore(_paths);
        bool ok = await store.SendHandoffAsync(new HandoffPayload
        {
            SourceProcess = "CropTool",
            TargetProcess = "MainWindow",
            ReturnedFromCropTool = true,
            SelectedClipPath = "C:\\Clips\\VictoryRoyale.mp4",
            SelectedClipStartMs = 1500,
            SelectedClipEndMs = 9500
        });

        Assert.True(ok);

        var state = await store.LoadAsync();
        Assert.NotNull(state);
        Assert.True(state["returned_from_crop_tool"]?.GetValue<bool>());
    }

    [Fact]
    public async Task NamedPipeStateClient_SendHandoffAsync_DirectPipeTransport()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1 });
        Assert.NotNull(server);

        var handoffPayload = new JsonObject
        {
            ["returned_from_crop_tool"] = true,
            ["source"] = "CropTool",
            ["target"] = "MainWindow"
        };

        bool ok = await NamedPipeStateClient.SendHandoffAsync(handoffPayload, TimeSpan.FromSeconds(2));
        Assert.True(ok);

        var state = server.GetState();
        Assert.True(state["returned_from_crop_tool"]?.GetValue<bool>());
    }

    [Fact]
    public async Task NamedPipeStateClient_SendHandoffAsync_MergerReturn_DirectPipeTransport()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1 });
        Assert.NotNull(server);

        var handoffPayload = new JsonObject
        {
            ["returned_from_crop_tool"] = true,
            ["source"] = "VideoMerger",
            ["target"] = "MainWindow"
        };

        bool ok = await NamedPipeStateClient.SendHandoffAsync(handoffPayload, TimeSpan.FromSeconds(2));
        Assert.True(ok);

        var state = server.GetState();
        Assert.True(state["returned_from_crop_tool"]?.GetValue<bool>());
    }

    [Fact]
    public async Task NamedPipeStateClient_PingAndClear_DirectPipeTransport()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject
        {
            ["schema_version"] = 1,
            ["MainVolume"] = 99.0
        });
        Assert.NotNull(server);

        bool pingOk = await NamedPipeStateClient.PingAsync(TimeSpan.FromSeconds(2));
        Assert.True(pingOk);

        bool clearOk = await NamedPipeStateClient.ClearStateAsync(TimeSpan.FromSeconds(2));
        Assert.True(clearOk);

        var state = server.GetState();
        Assert.False(state.ContainsKey("MainVolume"));
        Assert.Equal(1, state["schema_version"]?.GetValue<int>());
    }

    [Fact]
    public async Task IpcServer_ConcurrentFlushes_DiskEndsAtTheLatestState()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1 });
        Assert.NotNull(server);

        var flushers = new List<Task>();
        for (int i = 1; i <= 200; i++)
        {
            server!.UpdateProperties(new JsonObject { ["MainVolume"] = (double)(i % 100) });
            flushers.Add(Task.Run(server.FlushToDiskSafe));
        }
        await Task.WhenAll(flushers);
        server!.FlushToDiskSafe();

        double expected = server.GetState()["MainVolume"]!.GetValue<double>();
        JsonObject? onDisk = AtomicJsonFile.ReadObject(_paths.SessionStateFile);
        Assert.NotNull(onDisk);
        Assert.Equal(expected, onDisk!["MainVolume"]!.GetValue<double>());
    }


    private static readonly string[] ChildKeys =
        ["UploadVideoDirectory", "MergerUploadDirectory", "MergerOutputDirectory", "CropToolUploadDirectory"];

    private StateTransferStore ForeignStore(TimeSpan budget) =>
        new(_paths) { IgnoreInProcessServerForTests = true, DeliveryBudgetForTests = budget };

    private byte[]? DiskBytes() => File.Exists(_paths.SessionStateFile) ? File.ReadAllBytes(_paths.SessionStateFile) : null;

    [Fact]
    // IPCOWNER_01: a live owner prevents fallback writes behind the server.
    public async Task IpcOwner_DelayedAck_RetriesPipe_NeverWritesDiskBehindLiveServer()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1, ["MainVolume"] = 10.0 });
        Assert.NotNull(server);
        AtomicJsonFile.WriteObject(_paths.SessionStateFile, new JsonObject { ["schema_version"] = 1, ["UploadVideoDirectory"] = "disk-marker" });
        server!.AckDelayForTests = TimeSpan.FromMilliseconds(250);

        bool ok = await ForeignStore(TimeSpan.FromSeconds(5)).UpdatePropertiesAsync(new JsonObject { ["MergerOutputDirectory"] = "X" });

        Assert.True(ok);
        Assert.Equal("X", server.GetState()["MergerOutputDirectory"]?.GetValue<string>());

        JsonObject onDisk = AtomicJsonFile.ReadObject(_paths.SessionStateFile)!;
        Assert.False(onDisk["UploadVideoDirectory"]?.GetValue<string>() == "disk-marker" && onDisk.ContainsKey("MergerOutputDirectory"),
            "A client wrote the session file while the live server held the lease.");

        server.AckDelayForTests = TimeSpan.Zero;
        server.UpdateProperties(new JsonObject { ["MainVolume"] = 11.0 });
        server.FlushToDiskSafe();
        onDisk = AtomicJsonFile.ReadObject(_paths.SessionStateFile)!;
        Assert.Equal("X", onDisk["MergerOutputDirectory"]?.GetValue<string>());
        Assert.Equal(11.0, onDisk["MainVolume"]?.GetValue<double>());
    }

    [Fact]
    public async Task IpcOwner_ServerFlushAfterClientTimeout_HandoffFailsExplicitly_NoFalseSuccess()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1, ["MainVolume"] = 20.0 });
        Assert.NotNull(server);
        server!.FlushToDiskSafe();
        server.UpdateProperties(new JsonObject { ["MainVolume"] = 21.0 });
        server.FlushToDiskSafe();
        byte[]? before = DiskBytes();
        server.ApplyDelayForTests = TimeSpan.FromSeconds(30);

        var store = ForeignStore(TimeSpan.FromMilliseconds(700));
        var sw = Stopwatch.StartNew();
        bool asyncOk = await store.SendHandoffAsync(new HandoffPayload { SourceProcess = "CropTool", TargetProcess = "MainWindow", ReturnedFromCropTool = true });
        bool syncOk = await store.SendHandoffAsync(new HandoffPayload { SourceProcess = "VideoMerger", TargetProcess = "MainWindow", ReturnedFromCropTool = true });
        bool updOk = await store.UpdatePropertiesAsync(new JsonObject { ["MergerUploadDirectory"] = "lost?" });
        sw.Stop();

        Assert.False(asyncOk);
        Assert.False(syncOk);
        Assert.False(updOk);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"Bounded retry took {sw.ElapsedMilliseconds} ms.");
        Assert.Equal(before, DiskBytes());

        server.UpdateProperties(new JsonObject { ["MainVolume"] = 22.0 });
        server.FlushToDiskSafe();
        JsonObject onDisk = AtomicJsonFile.ReadObject(_paths.SessionStateFile)!;
        Assert.Equal(22.0, onDisk["MainVolume"]?.GetValue<double>());
        Assert.False(onDisk.ContainsKey("returned_from_crop_tool"));
        Assert.False(onDisk.ContainsKey("MergerUploadDirectory"));
    }

    [Fact]
    public async Task IpcOwner_LeaseHeldWithoutServer_FailsAndLeavesDiskUntouched_ThenCommitsOnceFree()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();
        AtomicJsonFile.WriteObject(_paths.SessionStateFile, new JsonObject { ["schema_version"] = 1, ["MainVolume"] = 30.0 });
        byte[]? before = DiskBytes();
        var store = new StateTransferStore(_paths) { DeliveryBudgetForTests = TimeSpan.FromMilliseconds(500) };

        using (var foreignLease = new Mutex(false, IpcProtocol.ServerMutexName, out bool createdNew))
        {
            Assert.True(createdNew);
            Assert.False(await store.UpdatePropertiesAsync(new JsonObject { ["MainVolume"] = 31.0 }));
            Assert.False(await store.SaveAsync(new JsonObject { ["MainVolume"] = 32.0 }));
            Assert.False(await store.ClearAsync());
            Assert.False(await store.SendHandoffAsync(new HandoffPayload { ReturnedFromCropTool = true }));
            Assert.Equal(before, DiskBytes());
        }

        Assert.True(await store.UpdatePropertiesAsync(new JsonObject { ["MainVolume"] = 33.0 }));
        Assert.Equal(33.0, AtomicJsonFile.ReadObject(_paths.SessionStateFile)!["MainVolume"]?.GetValue<double>());
        Assert.True(await store.SendHandoffAsync(new HandoffPayload { ReturnedFromCropTool = true }));
        Assert.True(AtomicJsonFile.ReadObject(_paths.SessionStateFile)!["returned_from_crop_tool"]?.GetValue<bool>());
        Assert.True(await store.ClearAsync());
        Assert.False(File.Exists(_paths.SessionStateFile));
    }

    [Fact]
    public async Task IpcOwner_DisposedServer_RefusesWrites_StoreReroutesDurably()
    {
        var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1, ["MainVolume"] = 40.0 });
        Assert.NotNull(server);
        server!.Dispose();

        Assert.False(server.UpdateProperties(new JsonObject { ["MainVolume"] = 41.0 }));
        Assert.False(server.SaveState(new JsonObject { ["MainVolume"] = 41.0 }));
        Assert.False(server.ClearState());

        var store = new StateTransferStore(_paths);
        Assert.True(await store.UpdatePropertiesAsync(new JsonObject { ["MainVolume"] = 42.0 }));
        server.FlushToDiskSafe();
        Assert.Equal(42.0, AtomicJsonFile.ReadObject(_paths.SessionStateFile)!["MainVolume"]?.GetValue<double>());
    }

    private Process StartChild(string key, int count, int budgetMs)
    {
        string dll = typeof(IpcChildProcess).Assembly.Location;
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        var psi = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in new[] { dll, "--ipc-child", "update", _testDir, key, count.ToString(), budgetMs.ToString() })
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static async Task AssertChildrenSucceeded(List<Process> children)
    {
        foreach (Process p in children)
        {
            using (p)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await p.WaitForExitAsync(cts.Token);
                string err = await p.StandardError.ReadToEndAsync();
                Assert.True(p.ExitCode == 0, $"child exit {p.ExitCode}: {err}");
            }
        }
    }

    [Fact]
    public async Task IpcOwner_ConcurrentProcesses_LiveServer_NoLostUpdates()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1 });
        Assert.NotNull(server);
        const int count = 10;

        var children = new List<Process>();
        foreach (string key in ChildKeys) children.Add(StartChild(key, count, 20000));
        for (int i = 0; i < 30; i++) server!.UpdateProperties(new JsonObject { ["MainVolume"] = (double)i });
        await AssertChildrenSucceeded(children);

        server!.FlushToDiskSafe();
        JsonObject mem = server.GetState();
        JsonObject onDisk = AtomicJsonFile.ReadObject(_paths.SessionStateFile)!;
        foreach (string key in ChildKeys)
        {
            Assert.Equal($"{key}_{count - 1}", mem[key]?.GetValue<string>());
            Assert.Equal($"{key}_{count - 1}", onDisk[key]?.GetValue<string>());
        }
        Assert.Equal(29.0, onDisk["MainVolume"]?.GetValue<double>());
    }

    [Fact]
    public async Task IpcOwner_ConcurrentProcesses_ServerStartsMidRun_NoLostUpdates()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();
        const int count = 15;

        var children = new List<Process>();
        foreach (string key in ChildKeys) children.Add(StartChild(key, count, 20000));

        await Task.Delay(300);
        NamedPipeStateServer? server = null;
        var sw = Stopwatch.StartNew();
        while (server == null && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            server = NamedPipeStateServer.TryStart(_paths);
            if (server == null) await Task.Delay(5);
        }
        Assert.NotNull(server);

        using (server)
        {
            await AssertChildrenSucceeded(children);
            server!.FlushToDiskSafe();
            JsonObject mem = server.GetState();
            JsonObject onDisk = AtomicJsonFile.ReadObject(_paths.SessionStateFile)!;
            foreach (string key in ChildKeys)
            {
                Assert.Equal($"{key}_{count - 1}", mem[key]?.GetValue<string>());
                Assert.Equal($"{key}_{count - 1}", onDisk[key]?.GetValue<string>());
            }
        }
    }


    /// <summary>A peer that owns the state pipe, accepts every connection and never answers.</summary>
    private sealed class SilentPeer : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<System.IO.Pipes.NamedPipeServerStream> _held = new();
        private readonly Task _loop;
        public int Accepted;

        public SilentPeer()
        {
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var pipe = new System.IO.Pipes.NamedPipeServerStream(
                        IpcProtocol.PipeName, System.IO.Pipes.PipeDirection.InOut,
                        System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                    lock (_held) _held.Add(pipe);
                    try { await pipe.WaitForConnectionAsync(_cts.Token); Interlocked.Increment(ref Accepted); }
                    catch (OperationCanceledException) { return; }
                    catch (IOException) { }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            lock (_held) foreach (var p in _held) p.Dispose();
            _cts.Dispose();
        }
    }

    [Fact]
    // IPCDEADLINE_01: all exchanges must complete within an end-to-end deadline.
    public async Task IpcDeadline_PeerConnectsButNeverResponds_EveryClientCallReturnsWithinItsDeadline()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();
        using var peer = new SilentPeer();
        await Task.Delay(100);
        TimeSpan deadline = TimeSpan.FromMilliseconds(300);

        var sw = Stopwatch.StartNew();
        JsonObject? state = await NamedPipeStateClient.GetStateAsync(deadline);
        bool upd = await NamedPipeStateClient.UpdatePropertiesAsync(new JsonObject { ["MainVolume"] = 1.0 }, deadline);
        bool save = await NamedPipeStateClient.SaveStateAsync(new JsonObject { ["MainVolume"] = 2.0 }, deadline);
        bool clear = await NamedPipeStateClient.ClearStateAsync(deadline);
        bool hand = await NamedPipeStateClient.SendHandoffAsync(new JsonObject { ["source"] = "x" }, deadline);
        bool ping = await NamedPipeStateClient.PingAsync(deadline);
        sw.Stop();

        Assert.True(peer.Accepted >= 1, "The silent peer never accepted a connection; the test proved nothing.");
        Assert.Null(state);
        Assert.False(upd || save || clear || hand || ping);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"Six bounded exchanges took {sw.ElapsedMilliseconds} ms (deadline {deadline.TotalMilliseconds} ms each).");
    }

    [Fact]
    public async Task IpcDeadline_ExchangeHonoursOneEndToEndDeadline_NotOnlyTheConnect()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();
        using var peer = new SilentPeer();
        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        IpcFrame? frame = await IpcProtocol.ExchangeAsync(
            () => new System.IO.Pipes.NamedPipeClientStream(".", IpcProtocol.PipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous),
            IpcOpcode.GetState, ReadOnlyMemory<byte>.Empty, TimeSpan.FromMilliseconds(250));
        sw.Stop();

        Assert.Null(frame);
        Assert.True(peer.Accepted >= 1);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Read past the deadline: {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task IpcDeadline_HungPeerWithoutLease_StoreCommitIsBoundedAndDurable()
    {
        NamedPipeStateServer.ActiveInstance?.Dispose();
        using var peer = new SilentPeer();
        await Task.Delay(100);

        var store = new StateTransferStore(_paths) { DeliveryBudgetForTests = TimeSpan.FromMilliseconds(600) };
        var sw = Stopwatch.StartNew();
        bool ok = await store.UpdatePropertiesAsync(new JsonObject { ["MergerOutputDirectory"] = "hung-peer" });
        sw.Stop();

        Assert.True(ok, "With no lease holder the commit must fall through to the owner-guarded disk write.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Commit took {sw.ElapsedMilliseconds} ms.");
        Assert.Equal("hung-peer", AtomicJsonFile.ReadObject(_paths.SessionStateFile)!["MergerOutputDirectory"]?.GetValue<string>());
    }

    /// <summary>A commit function that records batches, can be held open, and can be told to fail.</summary>
    private sealed class RecordingCommit
    {
        public readonly List<JsonObject> Batches = new();
        public int Active, MaxConcurrent;
        public volatile bool Fail;
        public TimeSpan Delay = TimeSpan.Zero;
        public TaskCompletionSource? Gate;

        public async Task<bool> Commit(JsonObject batch, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref Active);
            lock (Batches) MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                if (Gate != null) await Gate.Task;
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
                if (Fail) return false;
                lock (Batches) Batches.Add(batch.DeepClone().AsObject());
                return true;
            }
            finally { Interlocked.Decrement(ref Active); }
        }

        public JsonObject Last { get { lock (Batches) return Batches[^1]; } }
    }

    [Fact]
    public async Task SessionWriter_RapidUpdates_NewestValuePersists_CoalescedAndNeverConcurrent()
    {
        var commit = new RecordingCommit { Delay = TimeSpan.FromMilliseconds(15) };
        var writer = new SessionStateWriter(commit.Commit);

        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= 2000; i++)
        {
            writer.Enqueue(new JsonObject { ["MainVolume"] = (double)i, ["UploadVideoDirectory"] = $"dir{i}" });
        }
        sw.Stop();

        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2000.0, commit.Last["MainVolume"]!.GetValue<double>());
        Assert.Equal("dir2000", commit.Last["UploadVideoDirectory"]!.GetValue<string>());
        Assert.Equal(1, commit.MaxConcurrent);
        Assert.True(commit.Batches.Count < 200, $"Expected coalescing, got {commit.Batches.Count} commits for 2000 updates.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Enqueue is supposed to be memory-only; 2000 took {sw.ElapsedMilliseconds} ms.");

        double previous = 0;
        foreach (var batch in commit.Batches)
        {
            double v = batch["MainVolume"]!.GetValue<double>();
            Assert.True(v > previous, "Batches were committed out of order.");
            previous = v;
        }
        Assert.False(writer.HasUnacknowledgedChanges);
    }

    [Fact]
    public async Task SessionWriter_HungOwner_EnqueueAndReadNeverBlock_FlushReportsFailureWithinBudget()
    {
        var commit = new RecordingCommit { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var writer = new SessionStateWriter(commit.Commit);
        writer.Prime(new JsonObject { ["schema_version"] = 1, ["MainVolume"] = 5.0 });

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500; i++) writer.Enqueue(new JsonObject { ["MainVolume"] = (double)i });
        JsonObject snap = writer.ReadSnapshot();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Enqueue/Read blocked behind a hung owner: {sw.ElapsedMilliseconds} ms.");
        Assert.Equal(499.0, snap["MainVolume"]!.GetValue<double>());

        var flushClock = Stopwatch.StartNew();
        bool flushed = await writer.FlushAsync(TimeSpan.FromMilliseconds(300));
        flushClock.Stop();
        Assert.False(flushed);
        Assert.True(flushClock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(writer.HasUnacknowledgedChanges);

        commit.Gate.SetResult();
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(499.0, commit.Last["MainVolume"]!.GetValue<double>());
    }

    [Fact]
    public async Task SessionWriter_UnacknowledgedCommit_IsReported_Retained_AndRetriedByTheNextFlush()
    {
        var commit = new RecordingCommit { Fail = true };
        var writer = new SessionStateWriter(commit.Commit);
        var failures = new List<JsonObject>();
        writer.CommitFailed += b => { lock (failures) failures.Add(b); };

        writer.Enqueue(new JsonObject { ["CustomMusicDirectory"] = "kept" });
        Assert.False(await writer.FlushAsync(TimeSpan.FromSeconds(5)));
        lock (failures) Assert.Single(failures);
        Assert.Equal("kept", writer.ReadSnapshot()["CustomMusicDirectory"]?.GetValue<string>());

        commit.Fail = false;
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("kept", commit.Last["CustomMusicDirectory"]?.GetValue<string>());
        Assert.False(writer.HasUnacknowledgedChanges);
    }

    [Fact]
    public async Task SessionWriter_FailedOlderValue_NeverOverwritesANewerOne()
    {
        var commit = new RecordingCommit { Fail = true };
        var writer = new SessionStateWriter(commit.Commit);

        writer.Enqueue(new JsonObject { ["MainWindowBounds"] = new JsonObject { ["X"] = 1 }, ["MainVolume"] = 1.0 });
        Assert.False(await writer.FlushAsync(TimeSpan.FromSeconds(5)));

        commit.Fail = false;
        writer.Enqueue(new JsonObject { ["MainWindowBounds"] = new JsonObject { ["X"] = 2 } });
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));

        JsonObject last = commit.Last;
        Assert.Equal(2, last["MainWindowBounds"]!["X"]!.GetValue<int>());
        Assert.Equal(1.0, last["MainVolume"]!.GetValue<double>());
    }

    [Fact]
    public async Task SessionWriter_CloseDuringPendingSave_FinalSnapshotIsCommittedLast()
    {
        var commit = new RecordingCommit { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var writer = new SessionStateWriter(commit.Commit);

        writer.Enqueue(new JsonObject { ["MainWindowBounds"] = new JsonObject { ["X"] = 10 } });
        await Task.Delay(50);
        writer.Enqueue(new JsonObject { ["MainWindowBounds"] = new JsonObject { ["X"] = 99 } });

        Task<bool> finalFlush = writer.FlushAsync(TimeSpan.FromSeconds(5));
        commit.Gate.SetResult();

        Assert.True(await finalFlush);
        Assert.Equal(99, commit.Last["MainWindowBounds"]!["X"]!.GetValue<int>());
    }

    [Fact]
    public async Task SessionWriter_RealOwner_SlowAck_EnqueueReturnsImmediately_AndTheOwnerGetsTheLatest()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1 });
        Assert.NotNull(server);
        server!.AckDelayForTests = TimeSpan.FromMilliseconds(400);
        var foreign = ForeignStore(TimeSpan.FromSeconds(5));
        var writer = new SessionStateWriter((b, ct) => foreign.UpdatePropertiesAsync(b, ct));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) writer.Enqueue(new JsonObject { ["MergerUploadDirectory"] = $"d{i}" });
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"Enqueue waited on IPC: {sw.ElapsedMilliseconds} ms.");

        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("d49", server.GetState()["MergerUploadDirectory"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionWriter_For_InProcessOwner_ReadSnapshotIsMemoryAndSeesPendingWrites()
    {
        using var server = NamedPipeStateServer.TryStart(_paths, new JsonObject { ["schema_version"] = 1, ["MainVolume"] = 3.0 });
        Assert.NotNull(server);
        var writer = SessionStateWriter.For(_paths);

        writer.Enqueue(new JsonObject { ["CropToolUploadDirectory"] = "C:\\crop" });
        Assert.Equal("C:\\crop", writer.ReadSnapshot()["CropToolUploadDirectory"]?.GetValue<string>());
        Assert.Equal(3.0, writer.ReadSnapshot()["MainVolume"]?.GetValue<double>());

        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("C:\\crop", server!.GetState()["CropToolUploadDirectory"]?.GetValue<string>());
    }

    [Fact]
    public void UiIpc_NoSynchronousIpcEntryPointExists()
    {
        foreach (var type in new[] { typeof(NamedPipeStateClient), typeof(StateTransferStore) })
        {
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
            {
                Assert.False(m.Name.EndsWith("Sync", StringComparison.Ordinal),
                    $"UIIPC_01 — {type.Name}.{m.Name} is a synchronous IPC entry point; it can block the UI thread on a peer.");
            }
        }
    }
}
