using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.LocalAi;

/// <summary>
/// LOCALTRACK_01 (docs/01 TL-LOCALTRACK) — SAM 2.1 used as a frame-to-frame box tracker: every frame is encoded once,
/// then the mask decoder is prompted with the previous box (moved by its motion, widened by a margin) and the best
/// mask becomes the new box. The model is class-agnostic, so it follows whatever the first box held — a player, an
/// animal, a vehicle — and never "prefers" the large foreground character.
/// </summary>
public sealed class Sam2BoxTracker : IDisposable
{
    private readonly OrtSession _encoder;
    private readonly OrtSession _decoder;
    private readonly float[] _image;
    private readonly float[] _maskInput;
    private readonly float[] _hasMask = { 0f };
    private readonly float[] _memory;
    private bool _hasMemory;
    private float[]? _lastMasks;
    private int _lastMaskSize;
    public const float MemoryBackground = -10f;
    private static readonly string[] EncoderOutputs =
    {
        LocalAiPackManifest.EncoderEmbed, LocalAiPackManifest.EncoderHighRes0, LocalAiPackManifest.EncoderHighRes1,
    };
    private static readonly string[] DecoderOutputs = { LocalAiPackManifest.DecoderMasks, LocalAiPackManifest.DecoderScores, LocalAiPackManifest.DecoderObjectScore };

    public bool OnGpu { get; }
    public int InputSize => LocalAiPackManifest.ModelInputSize;

    public Sam2BoxTracker(OrtRuntime runtime, string packFolder, bool preferGpu)
    {
        _encoder = runtime.CreateSession(System.IO.Path.Combine(packFolder, LocalAiPackManifest.EncoderFile), preferGpu, out bool encGpu);
        try
        {
            _decoder = runtime.CreateSession(System.IO.Path.Combine(packFolder, LocalAiPackManifest.DecoderFile), preferGpu, out _);
        }
        catch
        {
            _encoder.Dispose();
            throw;
        }
        OnGpu = encGpu;
        int s = LocalAiPackManifest.ModelInputSize;
        _image = new float[3 * s * s];
        _maskInput = new float[LocalAiPackManifest.MaskSize * LocalAiPackManifest.MaskSize];
        _memory = new float[_maskInput.Length];
    }

    public bool HasMemory => _hasMemory;

    /// <summary>LOCALMEMORY_01 — keeps mask <paramref name="index"/> of the last <see cref="Step"/> as the next frame's mask prompt.</summary>
    public void Remember(int index)
    {
        int m = LocalAiPackManifest.MaskSize;
        int plane = m * m;
        if (_lastMasks == null || _lastMaskSize != m || index < 0 || (index + 1) * plane > _lastMasks.Length)
        {
            _hasMemory = false;
            return;
        }
        Array.Copy(_lastMasks, index * plane, _memory, 0, plane);
        _hasMemory = true;
    }

    /// <summary>Drops the remembered mask (a new direction, or the target has been lost for a while).</summary>
    public void Forget() => _hasMemory = false;

    /// <summary>
    /// Runs one frame (RGB24, model size square) with a box prompt and an expected centre in SOURCE pixels. Returns every
    /// non-empty mask the decoder proposed and the decoder's "object present" logit (above 0 = in the picture; +infinity
    /// when the model has no such output).
    /// </summary>
    public (IReadOnlyList<MaskCandidate> Candidates, double ObjectLogit) Step(ReadOnlySpan<byte> rgb, PixelBox prompt, double centreX, double centreY, int sourceW, int sourceH,
        double memoryShiftX = 0, double memoryShiftY = 0)
    {
        int s = LocalAiPackManifest.ModelInputSize;
        LocalTrackingMath.FillEncoderInput(rgb, s, _image);
        var enc = _encoder.Run(new[] { (LocalAiPackManifest.EncoderInput, _image, new long[] { 1, 3, s, s }) }, EncoderOutputs);

        var (coords, labels) = LocalTrackingMath.BoxPrompt(prompt, centreX, centreY, sourceW, sourceH, s);
        int m = LocalAiPackManifest.MaskSize;
        int points = labels.Length;
        if (_hasMemory)
        {
            int dx = (int)Math.Round(memoryShiftX * m / sourceW), dy = (int)Math.Round(memoryShiftY * m / sourceH);
            LocalTrackingMath.ShiftMask(_memory, _maskInput, m, dx, dy, MemoryBackground);
            _hasMask[0] = 1f;
        }
        else
        {
            Array.Clear(_maskInput);
            _hasMask[0] = 0f;
        }
        var inputs = new List<(string, float[], long[])>(7)
        {
            (LocalAiPackManifest.EncoderEmbed, enc[LocalAiPackManifest.EncoderEmbed].Data, enc[LocalAiPackManifest.EncoderEmbed].Shape),
            (LocalAiPackManifest.EncoderHighRes0, enc[LocalAiPackManifest.EncoderHighRes0].Data, enc[LocalAiPackManifest.EncoderHighRes0].Shape),
            (LocalAiPackManifest.EncoderHighRes1, enc[LocalAiPackManifest.EncoderHighRes1].Data, enc[LocalAiPackManifest.EncoderHighRes1].Shape),
            (LocalAiPackManifest.DecoderPointCoords, coords, new long[] { 1, points, 2 }),
            (LocalAiPackManifest.DecoderPointLabels, labels, new long[] { 1, points }),
            (LocalAiPackManifest.DecoderMaskInput, _maskInput, new long[] { 1, 1, m, m }),
            (LocalAiPackManifest.DecoderHasMaskInput, _hasMask, new long[] { 1 }),
        };
        var dec = _decoder.Run(inputs, DecoderOutputs);
        var (masks, maskShape) = dec[LocalAiPackManifest.DecoderMasks];
        var (scores, _) = dec[LocalAiPackManifest.DecoderScores];
        _lastMasks = masks;
        _lastMaskSize = maskShape.Length >= 2 && maskShape[^1] == maskShape[^2] ? (int)maskShape[^1] : 0;
        double objectLogit = dec.TryGetValue(LocalAiPackManifest.DecoderObjectScore, out var obj) && obj.Data.Length > 0
            ? obj.Data[0]
            : double.PositiveInfinity;
        return (Candidates(masks, maskShape, scores, sourceW, sourceH), objectLogit);
    }

    /// <summary>Every non-empty mask of the decoder output as a box with its predicted IoU.</summary>
    public static IReadOnlyList<MaskCandidate> Candidates(float[] masks, long[] maskShape, float[] scores, int sourceW, int sourceH)
    {
        var list = new List<MaskCandidate>(4);
        if (maskShape.Length < 2) return list;
        int h = (int)maskShape[^2], w = (int)maskShape[^1];
        if (h <= 0 || w != h) return list;
        int plane = h * w;
        int count = Math.Min(scores.Length, masks.Length / plane);
        for (int k = 0; k < count; k++)
        {
            var box = LocalTrackingMath.MaskToBox(masks.AsSpan(k * plane, plane), h, sourceW, sourceH);
            if (box != null) list.Add(new MaskCandidate(box.Value, scores[k], k));
        }
        return list;
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _encoder.Dispose();
    }
}
