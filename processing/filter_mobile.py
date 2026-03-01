import os
from .processing_utils import make_even
try:
    from developer_tools.coordinate_math import (
        TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE,
        PADDING_TOP, scale_round, inverse_transform_from_content_area_int
    )
except ImportError:
    import sys
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    if base_dir not in sys.path:
        sys.path.append(base_dir)

    from developer_tools.coordinate_math import (
        TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE,
        PADDING_TOP, scale_round, inverse_transform_from_content_area_int
    )

class MobileFilterMixin:
    def build_mobile_filter(self, mobile_coords, original_res_str, is_boss_hp, show_teammates, use_nvidia=False, needs_text_overlay=False, use_hwaccel=False, needs_hw_download=True, target_fps=60, input_pad="[v_stabilized]", txt_input_label=None, speed_factor=1.0):
        coords_data = mobile_coords
        FINAL_W, FINAL_H = 1080, 1920
        CONTENT_AREA_W, CONTENT_AREA_H = 1080, 1620
        INTERNAL_W, INTERNAL_H = 1280, 1920
        CONTENT_OFFSET_Y = 150
        parts = []
        curr_input = input_pad

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
                w_ui, h_ui, x_ui, y_ui = rect_1080
                transformed = inverse_transform_from_content_area_int((x_ui, y_ui, w_ui, h_ui), original_res_str)
                crop_orig = (transformed[2], transformed[3], transformed[0], transformed[1])
                ov = overlays.get(ov_key, {"x": 0, "y": 0})
                z = z_orders.get(ov_key, 50)
                active_layers.append({
                    "name": name, "crop_orig": crop_orig, "ui_wh": (rect_1080[0], rect_1080[1]),
                    "scale": sc, "pos": (ov["x"], ov["y"]), "z": z
                })
        register_layer("healthbar", hp_key, hp_key, hp_key)
        register_layer("lootbar", "loot", "loot", "loot")
        register_layer("stats", "stats", "stats", "stats")
        register_layer("spectating", "spectating", "spectating", "spectating")
        if show_teammates: register_layer("team", "team", "team", "team")
        active_layers.sort(key=lambda x: x["z"])
        split_count = 1 + len(active_layers)
        parts.append(f"{curr_input}split={split_count}[main_base_sw]" + "".join([f"[{l['name']}_in_sw]" for l in active_layers]))
        f_main_sw = f"scale={INTERNAL_W}:{INTERNAL_H}:force_original_aspect_ratio=increase:flags=bicubic,crop={INTERNAL_W}:{INTERNAL_H}:(iw-{INTERNAL_W})/2:(ih-{INTERNAL_H})/2"
        parts.append(f"[main_base_sw]{f_main_sw}[main_base_composed]")
        current_pad = "[main_base_composed]"
        for i, layer in enumerate(active_layers):
            cw, ch, cx, cy = layer['crop_orig']
            rw = max(2, make_even(layer['ui_wh'][0] * layer['scale'] * BACKEND_SCALE))
            rh = max(2, make_even(layer['ui_wh'][1] * layer['scale'] * BACKEND_SCALE))
            lx, ly = make_even(float(layer['pos'][0]) * BACKEND_SCALE), make_even(float(layer['pos'][1]) * BACKEND_SCALE)
            next_pad = f"[t{i}]" if i < len(active_layers) - 1 else "[internal_composed_sw]"
            parts.append(f"[{layer['name']}_in_sw]crop={cw}:{ch}:{cx}:{cy},scale={rw}:{rh}:flags=lanczos[{layer['name']}_out_sw]")
            parts.append(f"{current_pad}[{layer['name']}_out_sw]overlay=x={lx}:y={ly}:eof_action=pass{next_pad}")
            current_pad = next_pad
        if not active_layers:
            parts.append(f"[main_base_composed]null[internal_composed_sw]")
        parts.append(f"[internal_composed_sw]scale={CONTENT_AREA_W}:{CONTENT_AREA_H}:flags=bicubic[content_scaled_sw]")
        parts.append(f"[content_scaled_sw]pad={FINAL_W}:{FINAL_H}:0:{CONTENT_OFFSET_Y}:black,format=nv12[vpreout_sw]")
        last_v_pad = "[vpreout_sw]"
        if txt_input_label:
            parts.append(f"{last_v_pad}{txt_input_label}overlay=x=0:y=0:eof_action=repeat,setsar=1[vfinal_sw]")
            last_v_pad = "[vfinal_sw]"
        else:
            parts.append(f"{last_v_pad}setsar=1[vfinal_sw]")
            last_v_pad = "[vfinal_sw]"
            
        from .filter_builder import FilterResult
        return FilterResult((";".join(parts), last_v_pad))
