using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.LocalAi;

/// <summary>A failure reported by ONNX Runtime, or by loading it.</summary>
public sealed class OrtException : Exception
{
    public OrtException(string message) : base(message) { }
}

/// <summary>
/// LOCALAI_01 (docs/05 SYS-LOCALAI) — ONNX Runtime through its C API: the DLLs are loaded by FULL PATH from the
/// verified Local AI pack folder (never from the program folder or the search path), and every call goes through
/// the OrtApi function table with unmanaged function pointers, so NativeAOT needs no marshalling metadata.
/// Table slots are the ORT_API_VERSION 22 order (append-only in ONNX Runtime, so a newer DLL keeps them).
/// </summary>
public sealed unsafe class OrtRuntime : IDisposable
{
    public const uint ApiVersion = 22;

    private const int SlotGetErrorMessage = 2;
    private const int SlotCreateEnv = 3;
    private const int SlotCreateSession = 7;
    private const int SlotRun = 9;
    private const int SlotCreateSessionOptions = 10;
    private const int SlotSetSessionExecutionMode = 13;
    private const int SlotDisableMemPattern = 17;
    private const int SlotSetSessionGraphOptimizationLevel = 23;
    private const int SlotSetIntraOpNumThreads = 24;
    private const int SlotCreateTensorWithDataAsOrtValue = 49;
    private const int SlotGetTensorMutableData = 51;
    private const int SlotGetDimensionsCount = 61;
    private const int SlotGetDimensions = 62;
    private const int SlotGetTensorTypeAndShape = 65;
    private const int SlotCreateCpuMemoryInfo = 69;
    private const int SlotReleaseEnv = 92;
    private const int SlotReleaseStatus = 93;
    private const int SlotReleaseMemoryInfo = 94;
    private const int SlotReleaseSession = 95;
    private const int SlotReleaseValue = 96;
    private const int SlotReleaseTensorTypeAndShapeInfo = 99;
    private const int SlotReleaseSessionOptions = 100;
    private const int SlotGetExecutionProviderApi = 195;

    private const int LoggingLevelWarning = 2;
    private const int GraphOptimizationAll = 99;
    private const int ExecutionModeSequential = 0;
    private const int ArenaAllocator = 1;
    private const int MemTypeDefault = 0;
    private const int ElementTypeFloat = 1;

    private readonly void** _api;
    private nint _env;
    private nint _cpuMemory;
    private bool _disposed;

    public bool DirectMlAvailable { get; }
    public string Folder { get; }

    private OrtRuntime(string folder, nint ortHandle, nint dmlHandle, void** api)
    {
        Folder = folder;
        _api = api;
        DirectMlAvailable = dmlHandle != 0;

        nint env;
        byte[] logId = Encoding.ASCII.GetBytes("FreeVideoStudio\0");
        fixed (byte* id = logId)
            Check(((delegate* unmanaged<int, byte*, nint*, nint>)_api[SlotCreateEnv])(LoggingLevelWarning, id, &env));
        _env = env;

        nint mem;
        Check(((delegate* unmanaged<int, int, nint*, nint>)_api[SlotCreateCpuMemoryInfo])(ArenaAllocator, MemTypeDefault, &mem));
        _cpuMemory = mem;
    }

    /// <summary>
    /// Loads <c>DirectML.dll</c> (optional) and <c>onnxruntime.dll</c> from <paramref name="folder"/> by full path.
    /// DirectML is loaded first so onnxruntime binds to THAT copy instead of searching for one.
    /// </summary>
    public static OrtRuntime Load(string folder)
    {
        string ortPath = Path.Combine(folder, LocalAiPackManifest.OnnxRuntimeFile);
        string dmlPath = Path.Combine(folder, LocalAiPackManifest.DirectMlFile);
        if (!File.Exists(ortPath)) throw new OrtException($"ONNX Runtime is missing from the Local AI pack ({ortPath}).");

        nint dml = 0;
        if (File.Exists(dmlPath) && !NativeLibrary.TryLoad(dmlPath, out dml)) dml = 0;
        if (!NativeLibrary.TryLoad(ortPath, out nint ort))
        {
            if (dml != 0) NativeLibrary.Free(dml);
            throw new OrtException("ONNX Runtime could not be loaded. Reinstall the Local AI pack from Settings › AI.");
        }

        try
        {
            if (!NativeLibrary.TryGetExport(ort, "OrtGetApiBase", out nint getBase))
                throw new OrtException("The ONNX Runtime library in the Local AI pack is not usable (no OrtGetApiBase).");
            void** apiBase = ((delegate* unmanaged<void**>)getBase)();
            if (apiBase == null) throw new OrtException("ONNX Runtime returned no API table.");
            void** api = ((delegate* unmanaged<uint, void**>)apiBase[0])(ApiVersion);
            if (api == null) throw new OrtException($"The ONNX Runtime in the Local AI pack does not support API version {ApiVersion}.");
            return new OrtRuntime(folder, ort, dml, api);
        }
        catch
        {
            NativeLibrary.Free(ort);
            if (dml != 0) NativeLibrary.Free(dml);
            throw;
        }
    }

    /// <summary>Opens a model. DirectML (any DirectX 12 GPU) when available and asked for, otherwise the CPU.</summary>
    public OrtSession CreateSession(string modelPath, bool preferGpu, out bool onGpu)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(modelPath)) throw new OrtException($"Model file is missing: {Path.GetFileName(modelPath)}");

        nint options;
        Check(((delegate* unmanaged<nint*, nint>)_api[SlotCreateSessionOptions])(&options));
        try
        {
            Check(((delegate* unmanaged<nint, int, nint>)_api[SlotSetSessionGraphOptimizationLevel])(options, GraphOptimizationAll));
            onGpu = false;
            if (preferGpu && DirectMlAvailable)
            {
                Check(((delegate* unmanaged<nint, nint>)_api[SlotDisableMemPattern])(options));
                Check(((delegate* unmanaged<nint, int, nint>)_api[SlotSetSessionExecutionMode])(options, ExecutionModeSequential));
                void* dmlApi = null;
                byte[] name = Encoding.ASCII.GetBytes("DML\0");
                nint status;
                fixed (byte* n = name)
                    status = ((delegate* unmanaged<byte*, uint, void**, nint>)_api[SlotGetExecutionProviderApi])(n, ApiVersion, &dmlApi);
                if (status == 0 && dmlApi != null)
                {
                    status = ((delegate* unmanaged<nint, int, nint>)((void**)dmlApi)[0])(options, 0);
                    onGpu = status == 0;
                }
                if (status != 0)
                {
                    string why = TakeMessage(status);
                    CoreLogger.Info("LocalAi", $"DirectML unavailable, using the CPU: {why}");
                }
            }
            else
            {
                Check(((delegate* unmanaged<nint, int, nint>)_api[SlotSetIntraOpNumThreads])(options, Math.Max(1, Environment.ProcessorCount / 2)));
            }

            nint session;
            fixed (char* path = modelPath)
                Check(((delegate* unmanaged<nint, char*, nint, nint*, nint>)_api[SlotCreateSession])(_env, path, options, &session));
            return new OrtSession(this, session);
        }
        finally
        {
            ((delegate* unmanaged<nint, void>)_api[SlotReleaseSessionOptions])(options);
        }
    }

    internal nint CreateFloatTensor(float[] data, GCHandle pin, ReadOnlySpan<long> shape)
    {
        nint value;
        fixed (long* s = shape)
            Check(((delegate* unmanaged<nint, void*, nuint, long*, nuint, int, nint*, nint>)_api[SlotCreateTensorWithDataAsOrtValue])(
                _cpuMemory, (void*)pin.AddrOfPinnedObject(), (nuint)(data.Length * sizeof(float)), s, (nuint)shape.Length, ElementTypeFloat, &value));
        return value;
    }

    internal void Run(nint session, nint[] inputNames, nint[] inputs, nint[] outputNames, nint[] outputs)
    {
        fixed (nint* inN = inputNames)
        fixed (nint* inV = inputs)
        fixed (nint* outN = outputNames)
        fixed (nint* outV = outputs)
            Check(((delegate* unmanaged<nint, nint, nint*, nint*, nuint, nint*, nuint, nint*, nint>)_api[SlotRun])(
                session, 0, inN, inV, (nuint)inputs.Length, outN, (nuint)outputNames.Length, outV));
    }

    internal float[] ReadFloatTensor(nint value, out long[] shape)
    {
        nint info;
        Check(((delegate* unmanaged<nint, nint*, nint>)_api[SlotGetTensorTypeAndShape])(value, &info));
        try
        {
            nuint rank;
            Check(((delegate* unmanaged<nint, nuint*, nint>)_api[SlotGetDimensionsCount])(info, &rank));
            shape = new long[(int)rank];
            fixed (long* d = shape)
                Check(((delegate* unmanaged<nint, long*, nuint, nint>)_api[SlotGetDimensions])(info, d, rank));
        }
        finally
        {
            ((delegate* unmanaged<nint, void>)_api[SlotReleaseTensorTypeAndShapeInfo])(info);
        }

        long count = 1;
        foreach (long d in shape) count *= Math.Max(0, d);
        void* data;
        Check(((delegate* unmanaged<nint, void**, nint>)_api[SlotGetTensorMutableData])(value, &data));
        var result = new float[count];
        new ReadOnlySpan<float>(data, (int)count).CopyTo(result);
        return result;
    }

    internal void ReleaseValue(nint value)
    {
        if (value != 0) ((delegate* unmanaged<nint, void>)_api[SlotReleaseValue])(value);
    }

    internal void ReleaseSession(nint session)
    {
        if (session != 0 && !_disposed) ((delegate* unmanaged<nint, void>)_api[SlotReleaseSession])(session);
    }

    private void Check(nint status)
    {
        if (status == 0) return;
        throw new OrtException(TakeMessage(status));
    }

    private string TakeMessage(nint status)
    {
        byte* msg = ((delegate* unmanaged<nint, byte*>)_api[SlotGetErrorMessage])(status);
        string text = msg == null ? "ONNX Runtime error" : Marshal.PtrToStringUTF8((nint)msg) ?? "ONNX Runtime error";
        ((delegate* unmanaged<nint, void>)_api[SlotReleaseStatus])(status);
        return text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_cpuMemory != 0) ((delegate* unmanaged<nint, void>)_api[SlotReleaseMemoryInfo])(_cpuMemory);
        if (_env != 0) ((delegate* unmanaged<nint, void>)_api[SlotReleaseEnv])(_env);
        _cpuMemory = 0;
        _env = 0;
        _disposed = true;
    }
}

/// <summary>One loaded model. Inputs and outputs are float32 tensors addressed by name.</summary>
public sealed class OrtSession : IDisposable
{
    private readonly OrtRuntime _runtime;
    private nint _handle;

    internal OrtSession(OrtRuntime runtime, nint handle)
    {
        _runtime = runtime;
        _handle = handle;
    }

    /// <summary>Runs the model once. Every input array is pinned only for the duration of the call.</summary>
    public Dictionary<string, (float[] Data, long[] Shape)> Run(
        IReadOnlyList<(string Name, float[] Data, long[] Shape)> inputs, IReadOnlyList<string> outputNames)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var pins = new GCHandle[inputs.Count];
        var values = new nint[inputs.Count];
        var inNames = new nint[inputs.Count];
        var outNames = new nint[outputNames.Count];
        var outValues = new nint[outputNames.Count];
        try
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                pins[i] = GCHandle.Alloc(inputs[i].Data, GCHandleType.Pinned);
                values[i] = _runtime.CreateFloatTensor(inputs[i].Data, pins[i], inputs[i].Shape);
                inNames[i] = Marshal.StringToCoTaskMemUTF8(inputs[i].Name);
            }
            for (int i = 0; i < outputNames.Count; i++) outNames[i] = Marshal.StringToCoTaskMemUTF8(outputNames[i]);

            _runtime.Run(_handle, inNames, values, outNames, outValues);

            var result = new Dictionary<string, (float[], long[])>(outputNames.Count, StringComparer.Ordinal);
            for (int i = 0; i < outputNames.Count; i++)
                result[outputNames[i]] = (_runtime.ReadFloatTensor(outValues[i], out long[] shape), shape);
            return result;
        }
        finally
        {
            foreach (nint v in outValues) _runtime.ReleaseValue(v);
            foreach (nint v in values) _runtime.ReleaseValue(v);
            foreach (nint p in inNames) if (p != 0) Marshal.FreeCoTaskMem(p);
            foreach (nint p in outNames) if (p != 0) Marshal.FreeCoTaskMem(p);
            foreach (GCHandle h in pins) if (h.IsAllocated) h.Free();
        }
    }

    public void Dispose()
    {
        _runtime.ReleaseSession(_handle);
        _handle = 0;
    }
}
