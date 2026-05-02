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
        self.wrap_at_px = 1100
        self.safe_max_px = 1200
        self.base_font_size = 110
        self.min_font_size = 36
        self.line_spacing = -10
        self.measure_fudge = 1.12
        self.shadow_pad_px = 5

    def _rotate_backups(self, conf_path):
        try:
            for i in range(4, 0, -1):
                old_b = f"{conf_path}.bak{i}"
                new_b = f"{conf_path}.bak{i+1}"
                if os.path.exists(old_b):
                    try:
                        if os.path.exists(new_b): os.remove(new_b)
                        os.rename(old_b, new_b)
                    except: pass
            if os.path.exists(conf_path):
                try:
                    target = f"{conf_path}.bak1"
                    if os.path.exists(target): os.remove(target)
                    os.rename(conf_path, target)
                except: pass
        except Exception:
            pass

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
        
        def _try_load(path):
            try:
                with open(path, 'r', encoding='utf-8') as f:
                    loaded_data = json.load(f)
                if not isinstance(loaded_data, dict):
                    return None
                merged = json.loads(json.dumps(default_conf_data))
                for section in ("crops_1080p", "scales", "overlays", "z_orders"):
                    incoming = loaded_data.get(section, {})
                    if isinstance(incoming, dict):
                        merged[section].update(incoming)
                for key, value in loaded_data.items():
                    if key not in merged:
                        merged[key] = value
                return merged
            except:
                return None
        if not os.path.exists(conf_path):
            for i in range(1, 6):
                bak = f"{conf_path}.bak{i}"
                if os.path.exists(bak):
                    data = _try_load(bak)
                    if data:
                        if logger: logger.info(f"Recovered config from backup {bak}")
                        with open(conf_path, 'w', encoding='utf-8') as f:
                            json.dump(data, f, indent=4)
                        return data
            if logger: logger.info(f"Config missing at {conf_path}, creating with defaults.")
            try:
                os.makedirs(conf_dir, exist_ok=True)
                with open(conf_path, 'w', encoding='utf-8') as f:
                    json.dump(default_conf_data, f, indent=4)
            except Exception as e:
                if logger: logger.error(f"Failed to create default config: {e}")
                return default_conf_data
        for _ in range(3):
            loaded_data = _try_load(conf_path)
            if loaded_data:
                return loaded_data

            import time
            time.sleep(0.1)
        if logger: logger.warning(f"Config validation failed at {conf_path}. Overwriting with defaults.")
        self._rotate_backups(conf_path)
        with open(conf_path, 'w', encoding='utf-8') as f:
            json.dump(default_conf_data, f, indent=4)
        return default_conf_data

    def get_quality_settings(self, quality_level, target_mb_override=None):
        try:
            q = int(quality_level)
        except Exception:
            q = 7
        keep_highest_res = False
        target_mb = None
        if q >= 20:
            keep_highest_res = True
        else:
            keep_highest_res = False
            if target_mb_override is not None:
                target_mb = float(target_mb_override)
            else:
                q = max(0, min(19, q))
                target_mb = float(5 + q * 5)
        return keep_highest_res, target_mb, q
