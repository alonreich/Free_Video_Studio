import os
import json

class VideoConfig:
    def __init__(self, base_dir):
        self.base_dir = base_dir
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.default_main_width = 1280
        self.default_main_height = 1920
        self.mobile_main_width = 1080
        self.mobile_main_height = 1920
        self.fade_duration = 1.0
        self.epsilon = 0.01
        self.wrap_at_px = 950
        self.safe_max_px = 900
        self.base_font_size = 80
        self.min_font_size = 36
        self.line_spacing = -45
        self.shadow_pad_px = 14
        self.measure_fudge = 1.12

    def get_mobile_coordinates(self, logger=None):
        defaults = {
            "crops_1080p": {
                "loot": [339, 68, 1548, 976],
                "stats": [215, 153, 1682, 20],
                "normal_hp": [324, 50, 33, 979],
                "boss_hp": [325, 46, 138, 981],
                "team": [174, 161, 14, 801]
            },
            "scales": {
                "loot": 1.953, "stats": 2.112, "team": 1.61, "normal_hp": 1.849, "boss_hp": 1.615
            },
            "overlays": {
                "loot": {"x": 613, "y": 1659}, "stats": {"x": 829, "y": 0}, "team": {"x": 32, "y": 1434},
                "normal_hp": {"x": 5, "y": 1681}, "boss_hp": {"x": 20, "y": 1701}
            }
        }
        conf_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
        final_coords = defaults.copy()
        if os.path.exists(conf_path):
            try:
                with open(conf_path, 'r', encoding='utf-8') as f:
                    external_data = json.load(f)
                    for section in ["crops_1080p", "scales", "overlays"]:
                        if section in external_data:
                            final_coords[section].update(external_data[section])
                if logger: logger.info(f"Loaded crop config from {conf_path}")
            except Exception as e:
                if logger: logger.error(f"Failed to load crop config: {e}")
        else:
             if logger: logger.warning(f"Config missing at {conf_path}, using defaults")
        return final_coords

    def get_quality_settings(self, quality_level, target_mb_override=None):
        try:
            q = int(quality_level)
        except Exception:
            q = 2
        keep_highest_res = False
        target_mb = 52.0 
        if q >= 4:
            keep_highest_res = True
            target_mb = None
        elif q == 3:
            target_mb = 90.0
        elif q == 2:
            target_mb = 45.0
        elif q == 1:
            target_mb = 25.0
        else:
            target_mb = 15.0
        if not keep_highest_res:
             if target_mb_override is not None:
                 target_mb = target_mb_override
        return keep_highest_res, target_mb, q