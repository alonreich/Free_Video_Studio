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
        self.wrap_at_px = 1040
        self.safe_max_px = 1000
        self.base_font_size = 80
        self.min_font_size = 36
        self.line_spacing = -45
        self.measure_fudge = 1.12
        self.shadow_pad_px = 5

    def get_mobile_coordinates(self, logger=None):
        conf_dir = os.path.join(self.base_dir, 'processing')
        conf_path = os.path.join(conf_dir, 'crops_coordinations.conf')
        default_conf_data = {
            "crops_1080p": {
                "loot": [511, 103, 1420, 1612],
                "stats": [326, 233, 1620, 180],
                "normal_hp": [465, 71, -839, 1620],
                "boss_hp": [450, 150, 30, 1470],
                "team": [270, 181, -881, 1406],
                "spectating": [54, 22, -842, 1705]
            },
            "scales": {
                "loot": 1.0227,
                "stats": 1.2694,
                "team": 1.1253,
                "normal_hp": 1.1107,
                "boss_hp": 1.0,
                "spectating": 1.2059
            },
            "overlays": {
                "loot": {"x": 539, "y": 1406},
                "stats": {"x": 666, "y": 150},
                "team": {"x": 0, "y": 150},
                "normal_hp": {"x": 9, "y": 1419},
                "boss_hp": {"x": 30, "y": 1620},
                "spectating": {"x": 18, "y": 1524}
            },
            "z_orders": {
                "loot": 10,
                "normal_hp": 20,
                "boss_hp": 20,
                "stats": 30,
                "team": 40,
                "spectating": 100
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
                is_valid = True
                required_elements = ["loot", "stats", "team", "normal_hp", "boss_hp", "spectating"]
                for req in required_elements:
                    if req not in loaded_data.get("crops_1080p", {}) or req not in loaded_data.get("overlays", {}):
                        is_valid = False
                        break
                if not is_valid:
                    if logger: logger.warning(f"Config validation failed at {conf_path}. Overwriting with defaults.")
                    with open(conf_path, 'w', encoding='utf-8') as f:
                        json.dump(default_conf_data, f, indent=4)
                    return default_conf_data
                if logger: logger.info(f"Loaded valid crop config from {conf_path}")
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