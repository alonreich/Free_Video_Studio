// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using static FreeVideoStudio.Core.Editing.CropConfigJson;

namespace FreeVideoStudio.Core.Editing;

/// <summary>
/// EDITSTATE_01 — the Crop Tool's layers as the crop-config document (a cross-process,
/// cross-version contract read back by the Main App), and back. Pure: a document and a session in,
/// layers or written sections out. The window keeps the I/O (<see cref="CropConfigStore"/>), the
/// thumbnails and the profile-file sync.
/// </summary>
public static class CropProfileCodec
{
    /// <summary>
    /// IDEA_1 — the saved elements of a (sanitised) config as editable layers, for every element
    /// the session knows about that is neither already placed nor tombstoned. Registers every key
    /// the profile contains first (ROLEPOPUP_01 / A3), so a custom element is not invisible on reopen.
    ///
    /// <para>
    /// THE DRIFT TRAP: the saved <c>crops_1080p</c> rect is content space, and converting it back
    /// and forth both round OUTWARD, growing the box every cycle. So the source rect is persisted
    /// separately (<c>crops_source</c>) and read back verbatim; the inverse transform is only the
    /// one-time migration for a pre-v4 file. RESGUESS_01 — clamped only against a REAL capture size;
    /// otherwise the layer is marked unverified.
    /// </para>
    /// </summary>
    public static List<CropLayer> ReadSavedLayers(JsonObject config, CropEditSession s)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(s);

        JsonObject crops = EnsureObject(config, "crops_1080p");
        JsonObject scales = EnsureObject(config, "scales");
        JsonObject overlays = EnsureObject(config, "overlays");
        JsonObject zOrders = EnsureObject(config, "z_orders");
        JsonObject sourceCrops = EnsureObject(config, CropConfigDefaults.SourceCropsSection);

        s.AdoptRolesFromConfig(crops);
        s.AdoptRolesFromConfig(sourceCrops);

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
                    HudConfig.CropDriftType(role.Key));
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
                FromSavedConfig = true,              // GHOSTKILL_01
                GeometryVerified = geometryVerified, // RESGUESS_01
            });
        }
        return layers;
    }

    /// <summary>
    /// Writes every placed layer and every tombstone into <paramref name="config"/> (MERGED: the
    /// document may hold elements this session never touched). Re-quantises each verified layer's
    /// size first. RESGUESS_01 — an unverified layer writes only its position and z order, never a
    /// guess over its stored crop. DELETESET_01 — a tombstoned key still in the document is zeroed.
    /// Returns how many layers were written position-only.
    /// </summary>
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
                unverifiedCount++;
                CoreLogger.Info("CROP", $"  Save item: {layer.RoleKey} position/z only (no video loaded, stored crop preserved).");
                continue;
            }

            var quantized = s.QuantizeLayerSize(layer.SourceRect, layer.Width, layer.RoleKey);
            layer.Width = quantized.width;
            layer.Height = quantized.height;
            s.NormalizeLayout(layer);   // the layout pass the window always ran here before writing

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
            CoreLogger.Info("CROP", $"  Save item: {layer.RoleKey} crop=[{clampedCrop.w}x{clampedCrop.h}+{clampedCrop.x}+{clampedCrop.y}] scale={scale} overlay=({ox},{oy}) z={layer.Z}");
        }

        foreach (string deletedKey in s.DeletedRoleKeys)
        {
            // KEYCASE_01 — the layers and the tombstone set agree on what "same key" means.
            if (s.FindLayer(deletedKey) != null) continue;
            if (ReadSectionNode(crops, deletedKey) is null) continue;

            WriteSectionNode(crops, deletedKey, new JsonArray(0, 0, 0, 0));
            CoreLogger.Info("CROP", $"  Save item: {deletedKey} removed (crop cleared to 0x0).");
        }

        CoreLogger.Info("CROP", $"Saving {s.Layers.Count} item(s) to config (schema v{CropConfigDefaults.SchemaVersion}).");
        config["schema_version"] = CropConfigDefaults.SchemaVersion;
        config["coordinate_space"] = CropConfigDefaults.CoordinateSpace;
        return unverifiedCount;
    }
}
