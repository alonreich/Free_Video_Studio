using System;
using System.Collections.Generic;
using System.IO;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.LocalAi;

public readonly record struct DetectedLocalObject(PixelBox Box, int ClassId, string Category, double Confidence);

public sealed class YoloOnnxDetector : IDisposable
{
    public const string DetectorTag = "LOCALDETECT_01";
    public const string InputName = "images";
    public const string OutputName = "output0";
    public const double DefaultConfidenceThreshold = 0.28;
    public const double DefaultNmsIou = 0.45;

    private static readonly string[] RelevantCocoClasses =
    {
        "Character", "Bicycle", "Vehicle", "Motorcycle", "Aircraft", "Bus", "Train", "Truck", "Boat",
        "", "", "", "", "", "Bird", "Cat", "Dog", "Horse", "Sheep", "Cow", "Elephant", "Bear", "Zebra", "Giraffe",
        "", "", "", "", "", "", "", "", "Sports Target", "Kite", "", "", "Skateboard", "Surfboard", ""
    };

    private readonly OrtSession _session;
    private readonly float[] _inputBuffer;

    public bool OnGpu { get; }
    public int InputSize => LocalAiPackManifest.DetectorInputSize;

    public YoloOnnxDetector(OrtRuntime runtime, string packFolder, bool preferGpu = true)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        string path = Path.Combine(packFolder, LocalAiPackManifest.DetectorFile);
        _session = runtime.CreateSession(path, preferGpu, out bool onGpu);
        OnGpu = onGpu;
        int s = LocalAiPackManifest.DetectorInputSize;
        _inputBuffer = new float[3 * s * s];
    }

    public static bool ExistsInPack(string? packFolder)
        => !string.IsNullOrWhiteSpace(packFolder) && File.Exists(Path.Combine(packFolder, LocalAiPackManifest.DetectorFile));

    public IReadOnlyList<DetectedLocalObject> Detect(ReadOnlySpan<byte> rgb, int rgbWidth, int rgbHeight, int sourceW, int sourceH, AiGameProfile profile)
    {
        int s = InputSize;
        FillNormalizedInput(rgb, rgbWidth, rgbHeight, s, _inputBuffer);
        var outputs = _session.Run(
            new[] { (InputName, _inputBuffer, new long[] { 1, 3, s, s }) },
            new[] { OutputName });
        if (!outputs.TryGetValue(OutputName, out var tensor)) return Array.Empty<DetectedLocalObject>();
        return ParseYoloOutput(tensor.Data, tensor.Shape, s, sourceW, sourceH, profile);
    }

    public static void FillNormalizedInput(ReadOnlySpan<byte> rgb, int srcW, int srcH, int dstSize, float[] dstChw)
    {
        int plane = dstSize * dstSize;
        if (srcW <= 0 || srcH <= 0 || rgb.Length < srcW * srcH * 3)
            throw new ArgumentException("RGB buffer is smaller than source dimensions.", nameof(rgb));
        if (dstChw.Length < plane * 3)
            throw new ArgumentException("Destination tensor is too small.", nameof(dstChw));
        for (int y = 0; y < dstSize; y++)
        {
            int sy = Math.Clamp(y * srcH / dstSize, 0, srcH - 1);
            int dstRow = y * dstSize;
            for (int x = 0; x < dstSize; x++)
            {
                int sx = Math.Clamp(x * srcW / dstSize, 0, srcW - 1);
                int srcIdx = (sy * srcW + sx) * 3;
                int p = dstRow + x;
                dstChw[p] = rgb[srcIdx] / 255f;
                dstChw[plane + p] = rgb[srcIdx + 1] / 255f;
                dstChw[2 * plane + p] = rgb[srcIdx + 2] / 255f;
            }
        }
    }

    public static IReadOnlyList<DetectedLocalObject> ParseYoloOutput(
        ReadOnlySpan<float> data,
        long[] shape,
        int modelSize,
        int sourceW,
        int sourceH,
        AiGameProfile profile,
        double minConfidence = DefaultConfidenceThreshold,
        double nmsIou = DefaultNmsIou)
    {
        var raw = new List<DetectedLocalObject>();
        if (shape.Length < 3 || modelSize <= 0 || sourceW <= 0 || sourceH <= 0) return raw;
        int channels = (int)shape[1];
        int anchors = (int)shape[2];
        if (channels < 5 || anchors <= 0 || data.Length < channels * anchors) return raw;
        int numClasses = Math.Min(channels - 4, RelevantCocoClasses.Length);
        double scaleX = sourceW / (double)modelSize;
        double scaleY = sourceH / (double)modelSize;

        for (int a = 0; a < anchors; a++)
        {
            int bestClass = -1;
            float bestScore = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                if (string.IsNullOrEmpty(RelevantCocoClasses[c])) continue;
                float score = data[(4 + c) * anchors + a];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }
            if (bestClass < 0 || bestScore < minConfidence) continue;
            float cx = data[a];
            float cy = data[anchors + a];
            float w = data[2 * anchors + a];
            float h = data[3 * anchors + a];
            if (w <= 2f || h <= 2f) continue;

            var box = new PixelBox(
                (cx - w * 0.5) * scaleX,
                (cy - h * 0.5) * scaleY,
                (cx + w * 0.5) * scaleX,
                (cy + h * 0.5) * scaleY).Clamp(sourceW, sourceH);
            if (box.Width < sourceW * 0.015 || box.Height < sourceH * 0.025) continue;
            if (box.Width > sourceW * 0.92 && box.Height > sourceH * 0.92) continue;
            if (LocalTrackingMath.LooksLikeOwnBody(profile, box, sourceW, sourceH)) continue;

            string category = profile != AiGameProfile.Generic && bestClass == 0
                ? "Character"
                : (bestClass == 0 ? "Person" : RelevantCocoClasses[bestClass]);
            raw.Add(new DetectedLocalObject(box, bestClass, category, bestScore));
        }

        raw.Sort((x, y) => y.Confidence.CompareTo(x.Confidence));
        var kept = new List<DetectedLocalObject>();
        foreach (var cand in raw)
        {
            bool overlaps = false;
            foreach (var existing in kept)
            {
                if (LocalTrackingMath.Iou(cand.Box, existing.Box) >= nmsIou)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps)
            {
                kept.Add(cand);
                if (kept.Count >= 6) break;
            }
        }
        return kept;
    }

    public void Dispose() => _session.Dispose();
}
