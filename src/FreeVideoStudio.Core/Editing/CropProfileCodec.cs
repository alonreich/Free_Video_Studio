using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using static FreeVideoStudio.Core.Editing.CropConfigJson;

namespace FreeVideoStudio.Core.Editing;

public static class CropProfileCodec
{
    public static List<CropLayer> ReadSavedLayers(JsonObject config, CropEditSession s)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(s);

        JsonObject crops = EnsureObject(config, "crops_1080p");
        JsonObject scales = EnsureObject(config, "scales");
        JsonObject overlays = EnsureObject(config, "overlays");
        JsonObject zOrders = EnsureObject(config, "z_orders");
        JsonObject sourceCrops = EnsureObject(config, CropConfigDefaults.SourceCropsSection);

        s.AdoptRolesFromConfig(crops, config);
        s.AdoptRolesFromConfig(sourceCrops, config);

        var layers = new List<CropLayer>();
        foreach (CropHudRole role in s.AllRoles.ToList())
        {
            if (s.FindLayer(role.Key) != null) continue;
            if (s.DeletedRoleKeys.Contains(role.Key)) continue;
            if (ReadSectionNode(crops, role.Key) is not JsonArray crop || crop.Count < 4) continue;

            int cropW = ReadInt(crop[0], 0);
            int cropH = ReadInt(crop[1], 0);
            if (cropW <= 1 || cropH <= 1) continue;

            CropSourceRect sourceRect;
            if (ReadSectionNode(sourceCrops, role.Key) is JsonArray src && src.Count >= 4)
            {
                sourceRect = new CropSourceRect(
                    ReadInt(src[2], 0), ReadInt(src[3], 0),
                    Math.Max(2, ReadInt(src[0], 2)), Math.Max(2, ReadInt(src[1], 2)));
            }
            else
            {
                var derived = CoordinateMath.InverseTransformFromContentAreaInt(
                    (ReadInt(crop[2], 0), ReadInt(crop[3], 0), cropW, cropH),
                    s.OriginalResolution,
                    HudConfig.CropDriftType(role.Key, config));
                sourceRect = new CropSourceRect(derived.x, derived.y, derived.w, derived.h);
                CoreLogger.Info("CROP", $"Migrated '{role.Key}' to a stored source rect (pre-v4 config).");
            }

            bool geometryVerified = s.CaptureResolutionKnown;
            if (geometryVerified) sourceRect = s.ClampSourceRect(sourceRect);

            Frac scale = ReadFrac(ReadSectionNode(scales, role.Key), Frac.One);
            var (w, h) = CoordinateMath.QuantizeBackendSize(cropW, cropH, scale);

            double ox = role.DefaultX, oy = role.DefaultY;
            if (ReadSectionNode(overlays, role.Key) is JsonObject ov)
            {
                ox = ReadDouble(ov["x"], role.DefaultX);
                oy = ReadDouble(ov["y"], role.DefaultY);
            }

            layers.Add(new CropLayer
            {
                RoleKey = role.Key,
                DisplayName = role.DisplayName,
                SourceRect = sourceRect,
                CropImagePath = string.Empty,
                X = (int)Math.Round(ox),
                Y = (int)Math.Round(oy),
                Width = w,
                Height = h,
                Z = ReadInt(ReadSectionNode(zOrders, role.Key), role.DefaultZ),
                FromSavedConfig = true,
                GeometryVerified = geometryVerified,
            });
        }
        return layers;
    }

    public static int WriteLayers(JsonObject config, CropEditSession s)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(s);

        JsonObject crops = EnsureObject(config, "crops_1080p");
        JsonObject scales = EnsureObject(config, "scales");
        JsonObject overlays = EnsureObject(config, "overlays");
        JsonObject zOrders = EnsureObject(config, "z_orders");
        JsonObject sourceCrops = EnsureObject(config, CropConfigDefaults.SourceCropsSection);

        int unverifiedCount = 0;
        foreach (CropLayer layer in s.Layers)
        {
            if (!layer.GeometryVerified)
            {
                (int uox, int uoy) = CropEditSession.ClampOverlay(layer.X, layer.Y, layer.Width, layer.Height);
                WriteSectionNode(overlays, layer.RoleKey, new JsonObject { ["x"] = uox, ["y"] = uoy });
                WriteSectionNode(zOrders, layer.RoleKey, layer.Z);
                HudElements.WriteElement(config, layer.RoleKey, layer.DisplayName, s.AnchorOf(layer.RoleKey));
                unverifiedCount++;
                CoreLogger.Info("CROP", $"  Save item: {layer.RoleKey} position/z only (no video loaded, stored crop preserved).");
                continue;
            }

            var quantized = s.QuantizeLayerSize(layer.SourceRect, layer.Width, layer.RoleKey);
            layer.Width = quantized.width;
            layer.Height = quantized.height;
            s.NormalizeLayout(layer);

            var transformed = s.ToContent(layer.SourceRect, layer.RoleKey);
            var clampedCrop = CoordinateMath.ClampContentCrop((Math.Max(2, transformed.w), Math.Max(2, transformed.h), transformed.x, transformed.y));
            Frac scale = quantized.scale;
            (int ox, int oy) = CropEditSession.ClampOverlay(layer.X, layer.Y, layer.Width, layer.Height);

            WriteSectionNode(crops, layer.RoleKey, new JsonArray(clampedCrop.w, clampedCrop.h, clampedCrop.x, clampedCrop.y));
            WriteSectionNode(sourceCrops, layer.RoleKey, new JsonArray(
                layer.SourceRect.Width, layer.SourceRect.Height, layer.SourceRect.X, layer.SourceRect.Y));
            WriteSectionNode(scales, layer.RoleKey, scale.ToString());
            WriteSectionNode(overlays, layer.RoleKey, new JsonObject { ["x"] = ox, ["y"] = oy });
            WriteSectionNode(zOrders, layer.RoleKey, layer.Z);
            HudElements.WriteElement(config, layer.RoleKey, layer.DisplayName, s.AnchorOf(layer.RoleKey));
            CoreLogger.Info("CROP", $"  Save item: {layer.RoleKey} crop=[{clampedCrop.w}x{clampedCrop.h}+{clampedCrop.x}+{clampedCrop.y}] scale={scale} overlay=({ox},{oy}) z={layer.Z}");
        }

        foreach (string deletedKey in s.DeletedRoleKeys)
        {
            if (s.FindLayer(deletedKey) != null) continue;
            if (ReadSectionNode(crops, deletedKey) is null) continue;

            WriteSectionNode(crops, deletedKey, new JsonArray(0, 0, 0, 0));
            HudElements.RemoveElement(config, deletedKey);
            CoreLogger.Info("CROP", $"  Save item: {deletedKey} removed (crop cleared to 0x0).");
        }

        CoreLogger.Info("CROP", $"Saving {s.Layers.Count} item(s) to config (schema v{CropConfigDefaults.SchemaVersion}).");
        config["schema_version"] = CropConfigDefaults.SchemaVersion;
        config["coordinate_space"] = CropConfigDefaults.CoordinateSpace;
        return unverifiedCount;
    }
}
