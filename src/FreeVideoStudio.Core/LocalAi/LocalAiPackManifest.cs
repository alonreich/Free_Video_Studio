using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.LocalAi;

/// <summary>One file of the Local AI pack: where it is published, how big it is and its SHA-256 fingerprint.</summary>
public sealed record LocalAiPackFile(string Name, string Url, long Length, string Sha256);

/// <summary>
/// LOCALAI_01 / LOCALAIPACK_01 (docs/05 SYS-LOCALAI, docs/09 DIST-LOCALAIPACK) — the PINNED contents of the optional
/// Local AI pack. Nothing in the pack ships with the program; the user installs it from Settings › AI. Every file is
/// downloaded from this repository's GitHub release <see cref="ReleaseTag"/>, hashed against the SHA-256 written here
/// before it is moved into place, and hashed again before it is loaded. <see cref="Files"/> comes from <c>LocalAiPackFiles.cs</c>, which
/// <c>developer_tools/local_ai_pack/build_pack.py</c> regenerates; while it is empty or not yet marked <c>Released</c> the pack is "not published yet" and the app
/// says so instead of offering a download.
/// </summary>
public static class LocalAiPackManifest
{
    public const string PackVersion = "1";
    public const string ReleaseTag = "localai-v1";
    public const string ReleaseBaseUrl = "https://github.com/alonreich/Free_Video_Studio/releases/download/" + ReleaseTag + "/";

    public const string OnnxRuntimeFile = "onnxruntime.dll";
    public const string DirectMlFile = "DirectML.dll";
    public const string EncoderFile = "sam2_small_encoder.onnx";
    public const string DecoderFile = "sam2_small_decoder.onnx";

    /// <summary>The model contract the pack builder exports and <see cref="Sam2BoxTracker"/> reads.</summary>
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

    /// <summary>The names every complete pack must contain (DirectML is optional: without it the CPU is used).</summary>
    public static IReadOnlyList<string> RequiredNames { get; } = new[] { OnnxRuntimeFile, EncoderFile, DecoderFile };
}
