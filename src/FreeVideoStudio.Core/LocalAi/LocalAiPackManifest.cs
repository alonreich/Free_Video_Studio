using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.LocalAi;

public sealed record LocalAiPackFile(string Name, string Url, long Length, string Sha256);

public static class LocalAiPackManifest
{
    public const string PackVersion = "1";
    public const string ReleaseTag = "localai-v1";
    public const string ReleaseBaseUrl = "https://github.com/alonreich/Free_Video_Studio/releases/download/" + ReleaseTag + "/";

    public const string OnnxRuntimeFile = "onnxruntime.dll";
    public const string DirectMlFile = "DirectML.dll";
    public const string EncoderFile = "sam2_small_encoder.onnx";
    public const string DecoderFile = "sam2_small_decoder.onnx";

    public const int ModelInputSize = 1024;
    public const int MaskSize = 256;
    public const string EncoderInput = "image";
    public const string EncoderEmbed = "image_embed";
    public const string EncoderHighRes0 = "high_res_feats_0";
    public const string EncoderHighRes1 = "high_res_feats_1";
    public const string DecoderPointCoords = "point_coords";
    public const string DecoderPointLabels = "point_labels";
    public const string DecoderMaskInput = "mask_input";
    public const string DecoderHasMaskInput = "has_mask_input";
    public const string DecoderMasks = "masks";
    public const string DecoderScores = "iou_predictions";
    public const string DecoderObjectScore = "object_scores";

    public static IReadOnlyList<LocalAiPackFile> Files => LocalAiPackFiles.All;

    public static bool IsPublished => LocalAiPackFiles.Released && Files.Count > 0;

    public static long TotalBytes
    {
        get
        {
            long sum = 0;
            foreach (var f in Files) sum += f.Length;
            return sum;
        }
    }

    public static IReadOnlyList<string> RequiredNames { get; } = new[] { OnnxRuntimeFile, EncoderFile, DecoderFile };
}
