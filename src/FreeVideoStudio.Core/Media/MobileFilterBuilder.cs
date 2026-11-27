using System.Text;
using System.Text.Json.Nodes;

using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public class MobileFilterBuilder
{
    public static (string filterChain, string outputLabel) Build(
        string inputMainPad,
        string inputHudPad,
        JsonObject mobileCoords,
        bool showTeammates,
        bool showSpectating = true,
        string? txtInputLabel = null,
        bool useCuda = false,
        string originalResolution = "1920x1080")
    {
        var parts = new List<string>();
        var scales = mobileCoords["scales"]?.AsObject() ?? new JsonObject();
        var overlays = mobileCoords["overlays"]?.AsObject() ?? new JsonObject();
        var zOrders = mobileCoords["z_orders"]?.AsObject() ?? new JsonObject();

        var activeLayers = new List<LayerSpec>();

        var crops1080p = mobileCoords["crops_1080p"]?.AsObject();
        if (crops1080p != null)
        {
            foreach (var kvp in crops1080p)
            {
                string key = kvp.Key;

                if (HudConfig.IsRetiredRole(key)) continue;
                if (key == "spectating" && !showSpectating) continue;
                else if (key == "team" && !showTeammates) continue;

                activeLayers.RegisterLayer(mobileCoords, key, key, key, key);
            }
        }

        activeLayers = activeLayers
            .OrderBy(l => l.Z)
            .ThenBy(l => l.ConfKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string currV;

        if (activeLayers.Count > 0)
        {
            if (activeLayers.Count == 1)
            {
                parts.Add($"{inputHudPad}null[v_layer_in_0]");
            }
            else
            {
                var splitLabels = new StringBuilder();
                for (int i = 0; i < activeLayers.Count; i++)
                    splitLabels.Append($"[v_layer_in_{i}]");
                parts.Add($"{inputHudPad}split={activeLayers.Count}{splitLabels}");
            }

            var plan = CoordinateMath.ScalePlan(originalResolution);
            parts.Add($"{inputMainPad}scale={plan.scaledW}:{plan.scaledH}:flags=lanczos," +
                      $"crop={CoordinateConstants.TargetW}:{CoordinateConstants.TargetH}:{plan.cropX}:{plan.cropY}[main_base]");
            currV = "[main_base]";

            for (int i = 0; i < activeLayers.Count; i++)
            {
                var layer = activeLayers[i];

                var sourceRect = CoordinateMath.InverseTransformFromContentAreaInt(
                    (layer.UiRect[2], layer.UiRect[3], layer.UiRect[0], layer.UiRect[1]),
                    originalResolution,
                    HudConfig.CropDriftType(layer.ConfKey));

                int sx = sourceRect.x, sy = sourceRect.y, sw = sourceRect.w, sh = sourceRect.h;

                Frac backendScale = CoordinateConstants.BackendScale;
                Frac scaleFrac = Frac.FromDouble(layer.Scale);
                var (rw, rh) = CoordinateMath.QuantizeBackendSizeInternal(
                    layer.UiRect[0], layer.UiRect[1], scaleFrac);

                var pos = layer.Pos;
                Frac lxRaw = Frac.FromDouble(pos.x) * backendScale;
                Frac lyRaw = (Frac.FromDouble(pos.y) - new Frac(CoordinateConstants.UIPaddingTop, 1)) * backendScale;

                Frac maxLx = new(CoordinateConstants.TargetW - rw, 1);
                Frac maxLy = new(CoordinateConstants.TargetH - rh, 1);

                int lx = CoordinateMath.ScaleRound(
                    Frac.Zero > lxRaw ? Frac.Zero :
                    (lxRaw > maxLx ? maxLx : lxRaw));
                int ly = CoordinateMath.ScaleRound(
                    Frac.Zero > lyRaw ? Frac.Zero :
                    (lyRaw > maxLy ? maxLy : lyRaw));

                parts.Add($"[v_layer_in_{i}]crop=w={sw}:h={sh}:x={sx}:y={sy}," +
                          $"scale=w={rw}:h={rh}:flags=lanczos,format=yuva420p," +
                          $"pad=w=iw+4:h=ih+4:x=2:y=2:color=black@0[v_layer_out_{i}]");

                string nextV = $"[v_comp_{i}]";
                parts.Add($"{currV}[v_layer_out_{i}]overlay=x={lx - 2}:y={ly - 2}:eof_action=pass{nextV}");
                currV = nextV;
            }
        }
        else
        {
            var plan = CoordinateMath.ScalePlan(originalResolution);
            parts.Add($"{inputMainPad}scale={plan.scaledW}:{plan.scaledH}:flags=lanczos," +
                      $"crop={CoordinateConstants.TargetW}:{CoordinateConstants.TargetH}:{plan.cropX}:{plan.cropY}[main_base]");
            currV = "[main_base]";

            if (!string.IsNullOrEmpty(inputHudPad) && inputHudPad != inputMainPad)
            {
                parts.Add($"{inputHudPad}nullsink");
            }
        }

        if (!string.IsNullOrEmpty(txtInputLabel))
        {
            parts.Add($"{currV}scale={CoordinateConstants.ContentW}:{CoordinateConstants.ContentH}:" +
                      $"flags=lanczos,setsar=1[v_scaled_content]");
            parts.Add($"[v_scaled_content]split=2[v_sc_base][v_sc_video]");
            parts.Add($"[v_sc_base]pad={CoordinateConstants.PortraitW}:{CoordinateConstants.PortraitH}:0:0:black," +
                      $"drawbox=x=0:y=0:w={CoordinateConstants.PortraitW}:h={CoordinateConstants.PortraitH}:color=black:t=fill[v_bg_canvas]");
            parts.Add($"[v_bg_canvas][v_sc_video]overlay=x=0:y='if(lt(t,0.11),320,{CoordinateConstants.PaddingTop})':shortest=1[v_padded]");
            currV = "[v_padded]";
            parts.Add($"{currV}{txtInputLabel}overlay=x=0:y='if(lt(t,0.11),170,0)':shortest=1:eof_action=repeat:format=auto[v_final_raw]");
            currV = "[v_final_raw]";
        }
        else
        {
            parts.Add($"{currV}scale={CoordinateConstants.ContentW}:{CoordinateConstants.ContentH}:" +
                      $"flags=lanczos," +
                      $"pad={CoordinateConstants.PortraitW}:{CoordinateConstants.PortraitH}:" +
                      $"0:{CoordinateConstants.PaddingTop}:black,setsar=1[v_padded]");
            currV = "[v_padded]";
        }

        parts.Add($"{currV}format=yuv420p[v_final]");

        return (string.Join(";", parts), "[v_final]");
    }

    public static IReadOnlyList<(double X, double Y, double W, double H)> HudCanvasRects(JsonObject? mobileCoords, bool showTeammates, bool showSpectating)
        => HudCanvasLayers(mobileCoords, showTeammates, showSpectating).Select(l => (l.X, l.Y, l.W, l.H)).ToList();

    public static IReadOnlyList<HudCanvasLayer> HudCanvasLayers(JsonObject? mobileCoords, bool showTeammates, bool showSpectating)
    {
        var result = new List<HudCanvasLayer>();
        if (mobileCoords?["crops_1080p"] is not JsonObject crops) return result;
        var layers = new List<LayerSpec>();
        foreach (var kvp in crops)
        {
            string key = kvp.Key;
            if (HudConfig.IsRetiredRole(key)) continue;
            if (key == "spectating" && !showSpectating) continue;
            if (key == "team" && !showTeammates) continue;
            layers.RegisterLayer(mobileCoords, key, key, key, key);
        }
        foreach (var l in layers.OrderBy(l => l.Z).ThenBy(l => l.ConfKey, StringComparer.OrdinalIgnoreCase))
        {
            double w = l.UiRect[0] * l.Scale, h = l.UiRect[1] * l.Scale;
            if (w > 1 && h > 1) result.Add(new HudCanvasLayer(l.ConfKey, l.Pos.x, l.Pos.y, w, h, l.Z));
        }
        return result;
    }

    public sealed record HudCanvasLayer(string Key, double X, double Y, double W, double H, int Z);

    public record LayerSpec(
        string Name, string ConfKey,
        int[] UiRect,
        double Scale,
        (double x, double y) Pos,
        int Z);
}

internal static class MobileFilterBuilderExtensions
{
    internal static void RegisterLayer(
        this List<MobileFilterBuilder.LayerSpec> list,
        JsonObject coords,
        string name, string confKey, string cropKey1080, string ovKey)
    {
        int[] rect = GetRectHelper(coords, "crops_1080p", cropKey1080);
        var scalesObj = coords["scales"]?.AsObject();
        double scale = 1.0;
        JsonNode? scaleNode = scalesObj != null ? LookupHelper(scalesObj, confKey) : null;
        if (scaleNode != null)
        {
            double parsed;
            try { parsed = (double)scaleNode!; }
            catch (System.Exception swallowed)
            {
                try { parsed = Frac.FromString(scaleNode!.ToString()).ToDouble(); }
                catch (System.Exception swallowed2)
                {
                parsed = double.NaN;
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
                }
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
            }

            if (double.IsFinite(parsed) && parsed > 0.0)
            {
                scale = parsed;
            }
            else
            {
                CoreLogger.Info("EXPORT", $"Layer '{confKey}' has a non-positive or unreadable scale ('{scaleNode}'); using 1/1.");
            }
        }

        var overlaysObj = coords["overlays"]?.AsObject();
        double posX = 0, posY = CoordinateConstants.UIPaddingTop;
        if (overlaysObj != null && LookupHelper(overlaysObj, ovKey) is JsonObject ov)
        {
            try { posX = (double)ov["x"]!; } catch { try { posX = double.Parse(ov["x"]!.ToString()); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }
            try { posY = (double)ov["y"]!; } catch { try { posY = double.Parse(ov["y"]!.ToString()); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); } }
        }

        var zOrdersObj = coords["z_orders"]?.AsObject();
        int z = 50;
        JsonNode? zNode = zOrdersObj != null ? LookupHelper(zOrdersObj, ovKey) : null;
        if (zNode != null)
        {
            try { z = zNode.GetValue<int>(); }
            catch
            {
                try { z = (int)System.Math.Round(double.Parse(zNode.ToString(), System.Globalization.CultureInfo.InvariantCulture)); }
                catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            }
        }

        if (rect.Length >= 4 && rect[0] >= 1 && rect[1] >= 1)
        {
            list.Add(new MobileFilterBuilder.LayerSpec(name, confKey, rect, scale, (posX, posY), z));
        }
    }

    private static JsonNode? LookupHelper(JsonObject section, string key)
    {
        if (section.TryGetPropertyValue(key, out JsonNode? exact)) return exact;

        foreach (var pair in section)
        {
            if (string.Equals(pair.Key, key, System.StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }

        return null;
    }

    private static int[] GetRectHelper(JsonObject coords, string section, string key)
    {
        var sectionObj = coords[section]?.AsObject();
        if (sectionObj == null) return [0, 0, 0, 0];
        var node = LookupHelper(sectionObj, key);
        if (node is JsonArray arr && arr.Count >= 4)
            return [arr[0]!.GetValue<int>(), arr[1]!.GetValue<int>(), arr[2]!.GetValue<int>(), arr[3]!.GetValue<int>()];
        return [0, 0, 0, 0];
    }
}
