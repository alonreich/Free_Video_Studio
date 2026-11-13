using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Services;

/// <summary>AIPREFLIGHT_03 (docs/05 SYS-AISETTINGS §7.7) — before a multi-request job prepares any media (Smart Zoom,
/// Commentary, Kill Tracker), one tiny request proves the key works, the model exists and it reads a picture and
/// answers JSON. A wrong key fails in seconds with the Settings › AI fix instead of after the clips are cut.
/// No balance API is assumed. A success is reused for 30 minutes per key + model (<see cref="AiResultCache"/>).
/// Single-request features (Music Auto-Fit, Viral Post, Magic Wand) skip it: their one request IS the check.</summary>
internal static class AiPreflight
{
    private static readonly byte[] TestImage = CreateImage();
    private static byte[] CreateImage()
    {
        using var bitmap = new SkiaSharp.SKBitmap(64, 64);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static Task<bool> EnsureAsync(AiVendor vendor, string key, string model,
        CancellationToken ct, Action<string>? progress = null, IAiVisionProvider? provider = null)
    {
        string effective = AiVisionProviders.EffectiveModel(vendor, model, key);
        string identity = AiResultCache.Key("preflight-image-json-v1", vendor.ToString(), key, effective);
        return AiResultCache.RunAsync(identity, async () =>
        {
            progress?.Invoke("Checking your AI connection…");
            using var usage = AiUsageMeter.BeginTask("Connection check", vendor);
            string result = await (provider ?? AiVisionProviders.Get(vendor)).GenerateJsonAsync(key, effective,
                "Check that the image can be read. Return {\"ok\":true}.",
                new[] { new AiImageInput(TestImage, "image/png") }, null,
                "{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"]}",
                reasoning: AiReasoningPolicy.ConnectionCheck, options: GeminiCallOptions.Quick, userLog: progress, ct: ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(result);
            if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new AiProviderException(vendor, AiProviderErrorKind.MalformedResponse,
                    "This model could not read a test picture. Choose another model in Settings › AI.");
            return true;
        }, success => success, ct, progress);
    }
}
