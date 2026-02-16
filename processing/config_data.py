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
        conf_dir = os.path.join(self.base_dir, 'processing')
        conf_path = os.path.join(conf_dir, 'crops_coordinations.conf')
        default_conf_data = {
            "crops_1080p": {
                "loot": [400, 400, 680, 1220],
                "stats": [350, 350, 730, 0],
                "normal_hp": [450, 150, 30, 1470],
                "boss_hp": [450, 150, 30, 1470],
                "team": [300, 400, 30, 100]
            },
            "scales": {
                "loot": 1.0,
                "stats": 1.0,
                "team": 1.0,
                "normal_hp": 1.0,
                "boss_hp": 1.0
            },
            "overlays": {
                "loot": {"x": 680, "y": 1370},
                "stats": {"x": 730, "y": 150},
                "team": {"x": 30, "y": 250},
                "normal_hp": {"x": 30, "y": 1620},
                "boss_hp": {"x": 30, "y": 1620}
            },
            "window_geometry": {
                "x": 71, "y": 43, "w": 1600, "h": 880
            },
            "last_directory": "C:/",
            "portrait_window_geometry": {
                "x": 595, "y": 90, "w": 700, "h": 880
            }
        }
        if not os.path.exists(conf_path):
            if logger: logger.info(f"Config missing at {conf_path}, creating with defaults.")
            try:
                os.makedirs(conf_dir, exist_ok=True)
                with open(conf_path, 'w', encoding='utf-8') as f:
                    json.dump(default_conf_data, f, indent=4)
            except Exception as e:
                if logger: logger.error(f"Failed to create default config: {e}")
                return default_conf_data
        for _ in range(3):
            try:
                with open(conf_path, 'r', encoding='utf-8') as f:
                    loaded_data = json.load(f)
                if logger: logger.info(f"Loaded crop config from {conf_path}")
                return loaded_data
            except (json.JSONDecodeError, OSError):
                import time
                time.sleep(0.1)
        if logger: logger.error(f"Failed to load crop config after retries. Returning defaults.")
        return default_conf_data

    def get_quality_settings(self, quality_level, target_mb_override=None):
        try:
            q = int(quality_level)
        except Exception:
            q = 2
        keep_highest_res = False
        target_mb = None
        if q >= 4:
            keep_highest_res = True
        else:
            keep_highest_res = False
            if target_mb_override is not None:
                target_mb = float(target_mb_override)
            else:
                if q <= 0:
                    target_mb = 15.0
                elif q == 1:
                    target_mb = 25.0
                elif q == 2:
                    target_mb = 45.0
                elif q == 3:
                    target_mb = 90.0
                else:
                    target_mb = 45.0
        return keep_highest_res, target_mb, q