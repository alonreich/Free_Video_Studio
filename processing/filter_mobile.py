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
    def build_mobile_filter(self, *args, **kwargs):
        """[ALIAS] For test contracts and legacy compatibility."""
        if len(args) >= 2 and isinstance(args[0], dict) and isinstance(args[1], str):
            coords = args[0]
            is_boss_hp = kwargs.get('is_boss_hp', False)
            show_teammates = kwargs.get('show_teammates', False)
            return self.build_mobile_filter_chain("[0:v]", coords, is_boss_hp, show_teammates, None, False)
        return self.build_mobile_filter_chain(*args, **kwargs)

    def build_mobile_filter_chain(self, input_pad, mobile_coords, is_boss_hp, show_teammates, txt_input_label=None, use_cuda=False):
        """
        [FIX #1 & #5] Builds a high-performance, linear filter chain using optimized software filters.
        This ensures maximum stability across all hardware while keeping GPU encoding active.
        """
        coords_data = mobile_coords
        FINAL_W, FINAL_H = 1080, 1920
        CONTENT_AREA_W, CONTENT_AREA_H = 1080, 1620
        INTERNAL_W, INTERNAL_H = 1280, 1920
        CONTENT_OFFSET_Y = 150
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
                    "name": name, "ui_rect": rect_1080,
                    "scale": sc, "pos": (overlays.get(ov_key, {"x": 0, "y": 0})),
                    "z": z_orders.get(ov_key, 50)
                })
        register_layer("hp", hp_key, hp_key, hp_key)
        register_layer("loot", "loot", "loot", "loot")
        register_layer("stats", "stats", "stats", "stats")
        register_layer("spec", "spectating", "spectating", "spectating")
        if show_teammates: register_layer("team", "team", "team", "team")
        active_layers.sort(key=lambda x: x["z"])
        if active_layers:
            split_count = 1 + len(active_layers)
            parts.append(f"{input_pad}split={split_count}[v_base_in]" + "".join([f"[v_layer_in_{i}]" for i in range(len(active_layers))]))
            parts.append(f"[v_base_in]scale={INTERNAL_W}:{INTERNAL_H}:force_original_aspect_ratio=increase:flags=lanczos,crop={INTERNAL_W}:{INTERNAL_H}:(iw-{INTERNAL_W})/2:(ih-{INTERNAL_H})/2[main_base]")
            curr_v = "[main_base]"
            for i, layer in enumerate(active_layers):
                rw = max(2, make_even(layer['ui_rect'][0] * layer['scale'] * BACKEND_SCALE))
                rh = max(2, make_even(layer['ui_rect'][1] * layer['scale'] * BACKEND_SCALE))
                lx = make_even(float(layer['pos']['x']) * BACKEND_SCALE)
                ly = make_even(float(layer['pos']['y']) * BACKEND_SCALE)
                parts.append(f"[v_layer_in_{i}]REPLACE_ME_CROP_{layer['name']},scale={rw}:{rh}:flags=lanczos[v_layer_out_{i}]")
                next_v = f"[v_comp_{i}]"
                parts.append(f"{curr_v}[v_layer_out_{i}]overlay=x={lx}:y={ly}:eof_action=pass{next_v}")
                curr_v = next_v
        else:
            parts.append(f"{input_pad}scale={INTERNAL_W}:{INTERNAL_H}:force_original_aspect_ratio=increase:flags=lanczos,crop={INTERNAL_W}:{INTERNAL_H}:(iw-{INTERNAL_W})/2:(ih-{INTERNAL_H})/2[main_base]")
            curr_v = "[main_base]"
        parts.append(f"{curr_v}scale={CONTENT_AREA_W}:{CONTENT_AREA_H}:flags=lanczos,pad={FINAL_W}:{FINAL_H}:0:{CONTENT_OFFSET_Y}:black[v_padded]")
        curr_v = "[v_padded]"
        if txt_input_label:
            parts.append(f"{curr_v}{txt_input_label}overlay=x=0:y=0:eof_action=repeat,setsar=1,format=nv12[v_final]")
            curr_v = "[v_final]"
        else:
            parts.append(f"{curr_v}setsar=1,format=nv12[v_final]")
            curr_v = "[v_final]"

        from .filter_builder import FilterResult
        return FilterResult((";".join(parts), curr_v))
