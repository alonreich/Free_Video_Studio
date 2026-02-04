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
        self.wrap_at_px = 1040
        self.safe_max_px = 1000
        self.base_font_size = 80
        self.min_font_size = 36
        self.line_spacing = -45
        self.shadow_pad_px = 14
        self.measure_fudge = 1.12

    def get_mobile_coordinates(self, logger=None):
        conf_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
        default_conf_data = {
            "crops_1080p": {
                "loot": [506, 96, 1422, 1466],
                "stats": [317, 228, 1631, 33],
                "normal_hp": [464, 102, -840, 1473],
                "boss_hp": [0, 0, 0, 0],
                "team": [639, 333, 176, 234]
            },
            "scales": {
                "loot": 1.2931,
                "stats": 1.4886,
                "team": 1.61,
                "normal_hp": 1.1847,
                "boss_hp": 0.0
            },
            "overlays": {
                "loot": {"x": 509, "y": 1406},
                "stats": {"x": 682, "y": 0},
                "team": {"x": 32, "y": 1434},
                "normal_hp": {"x": 31, "y": 1410},
                "boss_hp": {"x": 0, "y": 0}
            },
            "window_geometry": {
                "x": 71, "y": 43, "w": 1600, "h": 880
            },
            "last_directory": "C:/Users/user/AppData/Local/Temp/Highlights/Fortnite",
            "portrait_window_geometry": {
                "x": 595, "y": 90, "w": 700, "h": 880
            }
        }
        if not os.path.exists(conf_path):
            if logger: logger.info(f"Config missing at {conf_path}, creating with defaults.")
            try:
                with open(conf_path, 'w', encoding='utf-8') as f:
                    json.dump(default_conf_data, f, indent=4)
            except Exception as e:
                if logger: logger.error(f"Failed to create default config: {e}")
                return default_conf_data
        try:
            with open(conf_path, 'r', encoding='utf-8') as f:
                loaded_data = json.load(f)
            if logger: logger.info(f"Loaded crop config from {conf_path}")
            return loaded_data
        except Exception as e:
            if logger: logger.error(f"Failed to load crop config: {e}. Returning defaults.")
            return default_conf_data

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