import os
import math
from .processing_utils import make_even
try:
    from developer_tools.coordinate_math import (
        TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE,
        PADDING_TOP, UI_PADDING_TOP, UI_TO_INTERNAL_SCALE, scale_round, 
        inverse_transform_from_content_area_int, get_resolution_ints,
        PORTRAIT_W, PORTRAIT_H
    )
except ImportError:
    import sys
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    if base_dir not in sys.path:
        sys.path.append(base_dir)

    from developer_tools.coordinate_math import (
        TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE,
        PADDING_TOP, UI_PADDING_TOP, UI_TO_INTERNAL_SCALE, scale_round, 
        inverse_transform_from_content_area_int, get_resolution_ints,
        PORTRAIT_W, PORTRAIT_H
    )

class MobileFilterMixin:
    def build_mobile_filter(self, *args, **kwargs):
        if len(args) >= 2 and isinstance(args[0], dict) and isinstance(args[1], str):
            coords = args[0]
            is_boss_hp = kwargs.get('is_boss_hp', False)
            show_teammates = kwargs.get('show_teammates', False)
            return self.build_mobile_filter_chain("[0:v]", coords, is_boss_hp, show_teammates, None, False)
        return self.build_mobile_filter_chain(*args, **kwargs)

    def build_mobile_filter_chain(self, input_pad, mobile_coords, is_boss_hp, show_teammates, txt_input_label=None, use_cuda=False, original_resolution="1920x1080"):
        coords_data = mobile_coords
        FINAL_W, FINAL_H = PORTRAIT_W, PORTRAIT_H
        CONTENT_AREA_W, CONTENT_AREA_H = CONTENT_W, CONTENT_H
        TARGET_INTERNAL_W, TARGET_INTERNAL_H = TARGET_W, TARGET_H
        CONTENT_OFFSET_Y = UI_PADDING_TOP
        parts = []

        def get_rect(section, key):
            return tuple(coords_data.get(section, {}).get(key, [0,0,0,0]))
        scales = coords_data.get("scales", {})
        overlays = coords_data.get("overlays", {})
        z_orders = coords_data.get("z_orders", {})
        hp_key = "boss_hp" if is_boss_hp else "normal_hp"
        active_layers = []

        def register_layer(name, conf_key, crop_key_1080, ov_key):
            rect_1080 = get_rect("crops_1080p", crop_key_1080)
            sc = float(scales.get(conf_key, 1.0))
            if rect_1080 and rect_1080[0] >= 1:
                active_layers.append({
                    "name": name, "conf_key": conf_key, "ui_rect": rect_1080,
                    "scale": sc, "pos": (overlays.get(ov_key, {"x": 0, "y": 0})),
                    "z": z_orders.get(ov_key, 50)
                })
        register_layer("hp", hp_key, hp_key, hp_key)
        register_layer("loot", "loot", "loot", "loot")
        register_layer("stats", "stats", "stats", "stats")
        register_layer("spec", "spectating", "spectating", "spectating")
        if show_teammates: register_layer("team", "team", "team", "team")
        active_layers.sort(key=lambda x: x["z"], reverse=True)
        if active_layers:
            split_count = 1 + len(active_layers)
            parts.append(f"{input_pad}split={split_count}[v_base_in]" + "".join([f"[v_layer_in_{i}]" for i in range(len(active_layers))]))
            in_w, in_h = get_resolution_ints(original_resolution)
            scale = float(TARGET_INTERNAL_H) / float(in_h)
            scaled_w = math.ceil(in_w * scale)
            if scaled_w % 2 != 0: scaled_w += 1
            cx = int(math.floor((scaled_w - TARGET_INTERNAL_W) / 4.0) * 2.0)
            scaled_h = math.ceil(in_h * scale)
            if scaled_h % 2 != 0: scaled_h += 1
            cy = int(math.floor((scaled_h - TARGET_INTERNAL_H) / 4.0) * 2.0)
            parts.append(f"[v_base_in]scale={scaled_w}:{scaled_h}:flags=lanczos,crop={TARGET_INTERNAL_W}:{TARGET_INTERNAL_H}:{cx}:{cy}[main_base]")
            curr_v = "[main_base]"
            for i, layer in enumerate(active_layers):
                drift_type = None
                ck = layer['conf_key']
                if ck in ["stats", "normal_hp", "boss_hp", "team", "spectating"]:
                    drift_type = "left"
                elif ck == "loot":
                    drift_type = "right"
                r = layer['ui_rect']
                source_rect = inverse_transform_from_content_area_int((r[2], r[3], r[0], r[1]), original_resolution, drift_type)
                sx, sy, sw, sh = source_rect
                rw = max(2, make_even(layer['ui_rect'][0] * layer['scale'] * UI_TO_INTERNAL_SCALE))
                rh = max(2, make_even(layer['ui_rect'][1] * layer['scale'] * UI_TO_INTERNAL_SCALE))
                ui_x_absolute = float(layer['pos']['x'])
                ui_y_absolute = float(layer['pos']['y'])
                lx_raw = ui_x_absolute * BACKEND_SCALE
                ly_raw = (ui_y_absolute - UI_PADDING_TOP) * BACKEND_SCALE
                lx = scale_round(max(0.0, min(lx_raw, float(TARGET_INTERNAL_W) - rw)))
                ly = scale_round(max(0.0, min(ly_raw, float(TARGET_INTERNAL_H) - rh)))
                parts.append(f"[v_layer_in_{i}]crop={sw}:{sh}:{sx}:{sy},scale={rw}:{rh}:flags=lanczos[v_layer_out_{i}]")
                next_v = f"[v_comp_{i}]"
                parts.append(f"{curr_v}[v_layer_out_{i}]overlay=x={lx}:y={ly}:eof_action=pass{next_v}")
                curr_v = next_v
        else:
            in_w, in_h = get_resolution_ints(original_resolution)
            scale = float(TARGET_INTERNAL_H) / float(in_h)
            scaled_w = math.ceil(in_w * scale)
            if scaled_w % 2 != 0: scaled_w += 1
            cx = int(math.floor((scaled_w - TARGET_INTERNAL_W) / 4.0) * 2.0)
            scaled_h = math.ceil(in_h * scale)
            if scaled_h % 2 != 0: scaled_h += 1
            cy = int(math.floor((scaled_h - TARGET_INTERNAL_H) / 4.0) * 2.0)
            parts.append(f"{input_pad}scale={scaled_w}:{scaled_h}:flags=lanczos,crop={TARGET_INTERNAL_W}:{TARGET_INTERNAL_H}:{cx}:{cy}[main_base]")
            curr_v = "[main_base]"
        parts.append(f"{curr_v}scale={CONTENT_AREA_W}:{CONTENT_AREA_H}:flags=lanczos,pad={FINAL_W}:{FINAL_H}:0:{CONTENT_OFFSET_Y}:black,setsar=1[v_padded]")
        curr_v = "[v_padded]"
        if txt_input_label:
            parts.append(f"{curr_v}{txt_input_label}overlay=0:0:shortest=1:eof_action=repeat:format=auto[v_final_raw]")
            curr_v = "[v_final_raw]"
        parts.append(f"{curr_v}format=yuv420p[v_final_yuv]")
        curr_v = "[v_final_yuv]"

        from .filter_builder import FilterResult
        return FilterResult((";".join(parts), curr_v))
