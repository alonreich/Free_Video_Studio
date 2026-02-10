r"""
Unified configuration manager for Fortnite Video Software.
Ensures consistent configuration structure between crop tool and processing module.
"""

import os
import json
import logging
import tempfile
import shutil
import time
import copy
try:
    import psutil
    HAS_PSUTIL = True
except ImportError:
    HAS_PSUTIL = False

from typing import Dict, Any, Optional, List
from PyQt5.QtCore import QRect, QObject, pyqtSignal
try:
    from .coordinate_math import (
        transform_to_content_area_int,
        clamp_overlay_position,
        BACKEND_SCALE
    )
except ImportError:
    from coordinate_math import (
        transform_to_content_area_int,
        clamp_overlay_position,
        BACKEND_SCALE
    )
try:
    from .config import UI_BEHAVIOR
    MIN_SCALE_FACTOR = UI_BEHAVIOR.MIN_SCALE_FACTOR
except ImportError:
    try:
        from config import UI_BEHAVIOR
        MIN_SCALE_FACTOR = UI_BEHAVIOR.MIN_SCALE_FACTOR
    except:
        MIN_SCALE_FACTOR = 0.0001
try:
    import sys
    sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

    from system.constants import Z_ORDER_MAP
except ImportError:
    Z_ORDER_MAP = {
        'loot': 10, 'normal_hp': 20, 'boss_hp': 20, 
        'stats': 30, 'team': 40, 'spectating': 100
    }
VALIDATION_SYSTEM_AVAILABLE = False

class ConfigObserver(QObject):
    """Observer pattern for config changes using Qt signals."""
    config_changed = pyqtSignal(str)
    config_deleted = pyqtSignal(str)
    config_loaded = pyqtSignal()
    
    def __init__(self):
        super().__init__()

class ConfigManager:
    """Manages configuration files with validation, consistency checks, and atomic operations."""
    REQUIRED_SECTIONS = ["crops_1080p", "scales", "overlays", "z_orders"]
    DEFAULT_VALUES = {
        "crops_1080p": {
            "loot": [0, 0, 0, 0],
            "stats": [0, 0, 0, 0],
            "normal_hp": [0, 0, 0, 0],
            "boss_hp": [0, 0, 0, 0],
            "team": [0, 0, 0, 0],
            "spectating": [0, 0, 0, 0]
        },
        "scales": {
            "loot": 1.0,
            "stats": 1.0,
            "team": 1.0,
            "normal_hp": 1.0,
            "boss_hp": 1.0,
            "spectating": 1.0
        },
        "overlays": {
            "loot": {"x": 680, "y": 1370},
            "stats": {"x": 730, "y": 150},
            "team": {"x": 30, "y": 250},
            "normal_hp": {"x": 30, "y": 1620},
            "boss_hp": {"x": 30, "y": 1620},
            "spectating": {"x": 30, "y": 1300}
        },
        "z_orders": {
            "loot": Z_ORDER_MAP.get("loot", 10),
            "normal_hp": Z_ORDER_MAP.get("normal_hp", 20),
            "boss_hp": Z_ORDER_MAP.get("boss_hp", 20),
            "stats": Z_ORDER_MAP.get("stats", 30),
            "team": Z_ORDER_MAP.get("team", 40),
            "spectating": Z_ORDER_MAP.get("spectating", 100)
        }
    }
    
    def __init__(self, config_path: str, logger: Optional[logging.Logger] = None):
        """
        Initialize configuration manager.
        Args:
            config_path: Path to the configuration file
            logger: Optional logger instance
        """
        self.config_path = config_path
        self.logger = logger or logging.getLogger(__name__)
        self.script_dir = os.path.dirname(os.path.abspath(__file__))
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, '..'))
        self._lock_file_path = f"{config_path}.lock"
        self.validation_feedback: Optional[Any] = None
        self.is_hud_config = "crops_coordinations.conf" in os.path.basename(config_path).lower()
        if self.is_hud_config:
            self._last_known_config = copy.deepcopy(self.DEFAULT_VALUES)
        else:
            self._last_known_config = {}
        self._config_version = 0
        self._last_file_mtime = 0
        self._observer = ConfigObserver()
    
    def get_observer(self) -> ConfigObserver:
        """Get the observer instance for subscribing to config changes."""
        return self._observer
    
    def _setup_validation_rules(self):
        """Setup validation rules for configuration."""
        pass
    
    def _is_valid_json(self) -> bool:
        """Check if config file contains valid JSON."""
        if not os.path.exists(self.config_path):
            return False
        try:
            with open(self.config_path, 'r', encoding='utf-8') as f:
                json.load(f)
            return True
        except (json.JSONDecodeError, OSError):
            return False
    
    def _has_required_sections(self) -> bool:
        """Check if config has all required sections."""
        try:
            config = self.load_config()
            for section in self.REQUIRED_SECTIONS:
                if section not in config:
                    return False
            return True
        except Exception:
            return False
    
    def _check_section_consistency(self) -> bool:
        """Check consistency between sections."""
        try:
            config = self.load_config()
            tech_keys = set()
            tech_keys.update(config["crops_1080p"].keys())
            tech_keys.update(config["scales"].keys())
            tech_keys.update(config["overlays"].keys())
            tech_keys.update(config["z_orders"].keys())
            for key in tech_keys:
                if (key not in config["crops_1080p"] or 
                    key not in config["scales"] or 
                    key not in config["overlays"] or
                    key not in config["z_orders"]):
                    return False
            return True
        except Exception:
            return False
    
    def _enforce_cross_section_consistency(self, config: Dict[str, Any]) -> None:
        """Ensure all sections have the same keys by adding missing entries with defaults."""
        if not self.is_hud_config:
            return
        all_keys = set()
        for section in self.REQUIRED_SECTIONS:
            if section in config and isinstance(config[section], dict):
                all_keys.update(config[section].keys())
        for key in all_keys:
            for section in self.REQUIRED_SECTIONS:
                if section not in config:
                    config[section] = {}
                if key not in config[section]:
                    if section == "crops_1080p":
                        if key == "spectating":
                            config[section][key] = [150, 100, 30, 1300]
                        else:
                            config[section][key] = [0, 0, 100, 100]
                    elif section == "scales":
                        config[section][key] = 1.0
                    elif section == "overlays":
                        if key == "spectating":
                            config[section][key] = {"x": 30, "y": 1300}
                        else:
                            config[section][key] = {"x": 0, "y": 0}
                    elif section == "z_orders":
                        config[section][key] = Z_ORDER_MAP.get(key, 10)
                    self.logger.debug(f"Added missing key '{key}' to section '{section}'")

    def _filter_hud_config(self, config: Dict[str, Any]) -> Dict[str, Any]:
        """Preserves HUD sections while allowing other metadata to persist."""
        if not isinstance(config, dict):
            return copy.deepcopy(self.DEFAULT_VALUES) if self.is_hud_config else {}
        clean = copy.deepcopy(config)
        if self.is_hud_config:
            for section in self.REQUIRED_SECTIONS:
                if section not in clean or not isinstance(clean[section], dict):
                    clean[section] = {}
        return clean

    def _sanitize_config(self, config: Dict[str, Any]) -> Dict[str, Any]:
        """Sanitizes HUD sections specifically while preserving other top-level keys."""
        clean = self._filter_hud_config(config)
        if not self.is_hud_config:
            return clean
        if "crops_1080p" in clean:
            for key, rect in list(clean["crops_1080p"].items()):
                if not isinstance(rect, list) or len(rect) < 4:
                    clean["crops_1080p"][key] = [0, 0, 0, 0]
                    continue
                try:
                    w = max(0, min(900, int(rect[0])))
                    h = max(0, min(600, int(rect[1])))
                    clean["crops_1080p"][key] = [w, h, int(rect[2]), int(rect[3])]
                except Exception:
                    clean["crops_1080p"][key] = [0, 0, 0, 0]
        if "scales" in clean:
            for key, scale in list(clean["scales"].items()):
                try:
                    scale_val = float(scale)
                    if scale_val < MIN_SCALE_FACTOR:
                        scale_val = MIN_SCALE_FACTOR
                    clean["scales"][key] = round(scale_val, 4)
                except Exception:
                    clean["scales"][key] = 1.0
        if "overlays" in clean:
            for key, overlay in list(clean["overlays"].items()):
                if not isinstance(overlay, dict):
                    clean["overlays"][key] = {"x": 0, "y": 0}
                    continue
                try:
                    x_val = max(0, min(1080, int(overlay.get("x", 0))))
                    y_val = max(0, min(1920, int(overlay.get("y", 0))))
                    clean["overlays"][key] = {"x": x_val, "y": y_val}
                except Exception:
                    clean["overlays"][key] = {"x": 0, "y": 0}
        if "z_orders" in clean:
            for key, z_val in list(clean["z_orders"].items()):
                try:
                    clean["z_orders"][key] = int(z_val)
                except Exception:
                    clean["z_orders"][key] = 10
        self._enforce_cross_section_consistency(clean)
        return clean
        
    def _acquire_lock(self, timeout_seconds: int = 5) -> bool:
        """Acquire a file-based atomic lock."""
        try:
            os.makedirs(os.path.dirname(self._lock_file_path), exist_ok=True)
        except Exception as e:
            self.logger.error(f"Failed to create directory for lock file: {e}")
            return False
        start_time = time.time()
        attempt = 0
        while (time.time() - start_time) < timeout_seconds:
            attempt += 1
            try:
                fd = os.open(self._lock_file_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                try:
                    os.write(fd, str(os.getpid()).encode())
                finally:
                    os.close(fd)
                self.logger.debug(f"Acquired lock for {self.config_path}")
                return True
            except FileExistsError:
                try:
                    with open(self._lock_file_path, 'r') as f:
                        pid = int(f.read().strip())
                    if HAS_PSUTIL:
                        if not psutil.pid_exists(pid):
                            self.logger.warning(f"Breaking stale lock from dead PID {pid}")
                            try: os.unlink(self._lock_file_path)
                            except: pass
                            continue
                    mtime = os.path.getmtime(self._lock_file_path)
                    if (time.time() - mtime) > timeout_seconds:
                        self.logger.warning(f"Breaking stale lock (timed out) for {self.config_path}")
                        try: os.unlink(self._lock_file_path)
                        except: pass
                        continue
                except:
                    pass
                time.sleep(0.1)
            except Exception as e:
                self.logger.error(f"Lock acquisition error: {e}")
                return False
        return False

    def _release_lock(self):
        """Release the file lock."""
        try:
            if os.path.exists(self._lock_file_path):
                os.unlink(self._lock_file_path)
        except Exception as e:
            self.logger.error(f"Error releasing lock: {e}")
    
    def load_config(self) -> Dict[str, Any]:
        """Load configuration from file with locking, ensuring all required sections exist."""
        current_mtime = 0
        if os.path.exists(self.config_path):
            current_mtime = os.path.getmtime(self.config_path)
        if not self._acquire_lock():
            self.logger.error(f"Could not acquire lock for {self.config_path}")
            if os.path.exists(self.config_path):
                try:
                    with open(self.config_path, 'r', encoding='utf-8') as f:
                        file_config = json.load(f)
                    if isinstance(file_config, dict):
                        config = self._sanitize_config(file_config)
                        self._last_known_config = copy.deepcopy(config)
                        self._last_file_mtime = current_mtime
                        return config
                except Exception as e:
                    self.logger.warning(f"Fallback read failed after lock error: {e}")
            self.logger.warning("Lock unavailable; returning last known config")
            return copy.deepcopy(self._last_known_config)
        config = copy.deepcopy(self.DEFAULT_VALUES)
        try:
            if os.path.exists(self.config_path):
                file_size = os.path.getsize(self.config_path)
                if file_size == 0:
                    self.logger.warning(f"Config file {self.config_path} is empty, using defaults")
                    backup_pattern = f"{self.config_path}.backup.*"

                    import glob
                    backups = sorted(glob.glob(backup_pattern), key=os.path.getmtime, reverse=True)
                    if backups:
                        latest_backup = backups[0]
                        self.logger.info(f"Restoring config from backup {latest_backup}")
                        try:
                            shutil.copy2(latest_backup, self.config_path)
                            with open(self.config_path, 'r', encoding='utf-8') as f:
                                file_config = json.load(f)
                        except Exception as e:
                            self.logger.error(f"Failed to restore from backup: {e}")
                            return config
                    else:
                        return config
                try:
                    with open(self.config_path, 'r', encoding='utf-8') as f:
                        file_config = json.load(f)
                    if not isinstance(file_config, dict):
                        self.logger.error(f"Config file {self.config_path} does not contain a JSON object, using defaults")
                        return config
                    config = self._sanitize_config(file_config)
                except (json.JSONDecodeError, OSError) as e:
                    self.logger.error(f"Failed to load config from {self.config_path}: {e}")
            else:
                self.logger.info(f"Config file not found at {self.config_path}, using defaults")
        finally:
            self._release_lock()
        self._last_known_config = copy.deepcopy(config)
        self._last_file_mtime = current_mtime
        return config
    
    def save_config(self, config: Dict[str, Any], enforce_consistency: bool = True) -> bool:
        """Save configuration to file atomically with validation and locking."""
        transaction = None
        state_manager = None
        try:
            from state_manager import get_state_manager, OperationType
            state_manager = get_state_manager(self.logger)
            transaction = state_manager.begin_transaction(
                OperationType.CONFIG_SAVE,
                f"Save config {self.config_path}"
            )
            transaction.add_file_backup(self.config_path)
        except Exception as e:
            self.logger.warning(f"StateManager unavailable for config save: {e}")
        if not self._acquire_lock():
            self.logger.error(f"Could not acquire lock for saving {self.config_path}")
            return False
        backup_path = None
        success = False
        try:
            config = self._sanitize_config(config)
            if enforce_consistency:
                self._enforce_cross_section_consistency(config)
            os.makedirs(os.path.dirname(self.config_path), exist_ok=True)
            temp_fd, temp_path = tempfile.mkstemp(
                suffix='.tmp',
                prefix=os.path.basename(self.config_path) + '.',
                dir=os.path.dirname(self.config_path)
            )
            try:
                with os.fdopen(temp_fd, 'w', encoding='utf-8') as f:
                    json.dump(config, f, indent=4)
                if os.path.exists(self.config_path):
                    backup_path = f"{self.config_path}.backup.{int(time.time())}"
                    shutil.copy2(self.config_path, backup_path)
                    self.logger.debug(f"Created backup at {backup_path}")
                os.replace(temp_path, self.config_path)
                if self.is_hud_config:
                    self.logger.info("=" * 80)
                    self.logger.info("CONFIGURATION SAVED SUCCESSFULLY")
                    self.logger.info("-" * 40)
                    self.logger.info(f"File: {self.config_path}")
                    self.logger.info(f"Timestamp: {time.strftime('%Y-%m-%d %H:%M:%S')}")
                    self.logger.info(f"Sections: {', '.join(self.REQUIRED_SECTIONS)}")
                    self.logger.info(f"Configured elements: {len(config.get('crops_1080p', {}))}")
                    self.logger.info("FULL JSON CONFIGURATION FILE CONTENTS:")
                    self.logger.info("-" * 40)
                    self.logger.info(json.dumps(config, indent=2))
                    self.logger.info("-" * 40)
                    self.logger.info("=" * 80)
                if backup_path and os.path.exists(backup_path):
                    try:
                        with open(self.config_path, 'r', encoding='utf-8') as f:
                            json.load(f)
                        os.unlink(backup_path)
                    except Exception as verify_error:
                        self.logger.warning(f"Backup retained due to verification failure: {verify_error}")
                self._prune_backup_files(max_backups=5)
                success = True
                if self.is_hud_config:
                    old_config = self._last_known_config
                    new_keys = set(config.get("crops_1080p", {}).keys())
                    old_keys = set(old_config.get("crops_1080p", {}).keys())
                    for key in new_keys:
                        if key in old_keys:
                            old_crop = old_config.get("crops_1080p", {}).get(key)
                            new_crop = config.get("crops_1080p", {}).get(key)
                            if old_crop != new_crop:
                                self._observer.config_changed.emit(key)
                        else:
                            self._observer.config_changed.emit(key)
                    for key in old_keys - new_keys:
                        self._observer.config_deleted.emit(key)
                self._observer.config_loaded.emit()
                return True
            except Exception as e:
                self.logger.error(f"Failed during atomic save to {self.config_path}: {e}")
                if os.path.exists(temp_path):
                    try:
                        os.unlink(temp_path)
                    except:
                        pass
                if backup_path and os.path.exists(backup_path):
                    try:
                        shutil.copy2(backup_path, self.config_path)
                        self.logger.info(f"Restored config from backup {backup_path}")
                    except Exception as restore_error:
                        self.logger.error(f"Failed to restore from backup: {restore_error}")
                return False
        except Exception as e:
            self.logger.error(f"Failed to save config to {self.config_path}: {e}")
            return False
        finally:
            self._release_lock()
            if transaction and state_manager:
                if success:
                    state_manager.commit_transaction(transaction)
                else:
                    state_manager.rollback_transaction(transaction)
            if success:
                self._last_known_config = copy.deepcopy(config)

    def _prune_backup_files(self, max_backups: int = 5) -> None:
        """Keep backup file count bounded to avoid unbounded disk growth."""
        try:
            import glob
            pattern = f"{self.config_path}.backup.*"
            backups = sorted(glob.glob(pattern), key=os.path.getmtime, reverse=True)
            for stale in backups[max_backups:]:
                try:
                    os.unlink(stale)
                except Exception:
                    pass
            config_dir = os.path.dirname(self.config_path)
            config_filename = os.path.basename(self.config_path)
            old_backup_path = os.path.join(config_dir, f"old_{config_filename}")
            if os.path.exists(old_backup_path):
                try:
                    max_age_seconds = 60 * 60 * 24 * 3
                    if (time.time() - os.path.getmtime(old_backup_path)) > max_age_seconds:
                        os.unlink(old_backup_path)
                except Exception:
                    pass
        except Exception as e:
            self.logger.debug(f"Backup pruning skipped: {e}")

    def validate_config_data(self, config: Dict[str, Any]) -> List[str]:
        """Validate a provided config object (without reloading from disk)."""
        issues: List[str] = []
        if not isinstance(config, dict):
            return ["Configuration must be a JSON object"]
        clean = self._sanitize_config(config)
        for section in self.REQUIRED_SECTIONS:
            if section not in clean:
                issues.append(f"Missing required section: {section}")
            elif not isinstance(clean[section], dict):
                issues.append(f"Section {section} should be a dictionary")
        tech_keys: set[str] = set()
        tech_keys.update(clean["crops_1080p"].keys())
        tech_keys.update(clean["scales"].keys())
        tech_keys.update(clean["overlays"].keys())
        tech_keys.update(clean["z_orders"].keys())
        for key in tech_keys:
            rect = clean["crops_1080p"].get(key)
            if not isinstance(rect, list) or len(rect) < 4:
                issues.append(f"Invalid crop data for '{key}'")
            scale_val = clean["scales"].get(key)
            if not isinstance(scale_val, (int, float)):
                issues.append(f"Invalid scale value for '{key}'")
            overlay_val = clean["overlays"].get(key)
            if not isinstance(overlay_val, dict) or "x" not in overlay_val or "y" not in overlay_val:
                issues.append(f"Invalid overlay data for '{key}'")
            z_val = clean["z_orders"].get(key)
            if not isinstance(z_val, int):
                issues.append(f"Invalid z-order value for '{key}'")
        return issues

    def transform_crop_rect(self, rect: QRect, original_resolution: str) -> List[int]:
        rect_tuple = (rect.x(), rect.y(), rect.width(), rect.height())
        transformed = transform_to_content_area_int(rect_tuple, original_resolution)
        return list(transformed)
    
    def save_crop_coordinates(self, tech_key: str, rect: QRect, original_resolution: str) -> bool:
        """
        Saves crop coordinates using the same transformation as filter_builder.py.
        Transforms coordinates from original resolution to 1080p portrait content area,
        then converts to [width, height, x, y] format expected by filter_builder.
        """
        try:
            if rect.width() <= 0 or rect.height() <= 0:
                 self.logger.error(f"Invalid crop rectangle for {tech_key}: {rect}")
                 return False
            rect_tuple = (rect.x(), rect.y(), rect.width(), rect.height())
            transformed = transform_to_content_area_int(rect_tuple, original_resolution)
            x_content, y_content, w_content, h_content = transformed
            normalized_rect = [int(w_content), int(h_content), int(x_content), int(y_content)]
            config = self.load_config()
            config["crops_1080p"][tech_key] = normalized_rect
            success = self.save_config(config, enforce_consistency=True)
            if success:
                self.logger.info(
                    f"Saved OUTWARD-ROUNDED crop coordinates for {tech_key}: {normalized_rect} "
                    f"(from original {original_resolution})"
                )
                return True
            else:
                self.logger.error(f"Failed to save crop coordinates for {tech_key}")
                return False
        except Exception as e:
            self.logger.error(f"Error saving crop coordinates for {tech_key}: {e}")
            return False

    def delete_crop_coordinates(self, tech_key: str) -> bool:
        """
        Physically removes configuration data for a technical key.
        [FIX #9] Deleting keys instead of zeroing them ensures the config remains clean.
        """
        config_data = self.get_current_config_data()
        changes_made = False
        if 'crops_1080p' in config_data and tech_key in config_data['crops_1080p']:
            del config_data['crops_1080p'][tech_key]
            changes_made = True
        if 'scales' in config_data and tech_key in config_data['scales']:
            del config_data['scales'][tech_key]
            changes_made = True
        if 'overlays' in config_data and tech_key in config_data['overlays']:
            del config_data['overlays'][tech_key]
            changes_made = True
        if 'z_orders' in config_data and tech_key in config_data['z_orders']:
            del config_data['z_orders'][tech_key]
            changes_made = True
        if changes_made:
            self.logger.info(f"Physically removed configuration data for tech_key: {tech_key}")
            return self.save_config(config_data, enforce_consistency=True)
        return True
    
    def update_overlay_position(self, tech_key: str, x: int, y: int) -> bool:
        """
        Update overlay position for a HUD element with clamping to screen bounds.
        Args:
            tech_key: Technical key for the HUD element
            x: X coordinate in 1080x1920 portrait space (as shown in portrait window)
            y: Y coordinate in 1080x1920 portrait space (0-1920, where 0-150 is text area)
        Returns:
            True if successful, False otherwise
        """
        try:
            config = self.load_config()
            scaled_rect = config.get("crops_1080p", {}).get(tech_key)
            if not scaled_rect or len(scaled_rect) < 4:
                self.logger.warning(f"Overlay update ignored: missing crop_1080p for {tech_key}")
                return False
            width, height = scaled_rect[0], scaled_rect[1]
            if width <= 1 or height <= 1:
                self.logger.warning(f"Overlay update ignored: invalid crop size for {tech_key}")
                return False
            scale = config.get("scales", {}).get(tech_key, 1.0)
            scaled_width = int(round(width * scale))
            scaled_height = int(round(height * scale))
            x_scaled = x * BACKEND_SCALE
            y_scaled = y * BACKEND_SCALE
            try:
                from .coordinate_math import scale_round
            except ImportError:
                from coordinate_math import scale_round
            clamped_x_scaled, clamped_y_scaled = clamp_overlay_position(
                scale_round(x_scaled),
                scale_round(y_scaled),
                scaled_width,
                scaled_height,
                padding_top_ui=150,
                padding_bottom_ui=0
            )
            clamped_x = scale_round(clamped_x_scaled / BACKEND_SCALE)
            clamped_y = scale_round(clamped_y_scaled / BACKEND_SCALE)
            if clamped_x != x or clamped_y != y:
                self.logger.debug(f"Overlay position clamped from ({x},{y}) to ({clamped_x},{clamped_y}) in portrait space")
            config["overlays"][tech_key] = {"x": clamped_x, "y": clamped_y}
            return self.save_config(config)
        except Exception as e:
            self.logger.error(f"Error updating overlay position for {tech_key}: {e}")
            return False
    
    def update_scale_factor(self, tech_key: str, scale_factor: float) -> bool:
        """
        Update scale factor for a HUD element.
        Args:
            tech_key: Technical key for the HUD element
            scale_factor: Scale factor (e.g., 1.0 for original size)
        Returns:
            True if successful, False otherwise
        """
        if scale_factor < MIN_SCALE_FACTOR:
            self.logger.warning(f"Scale factor {scale_factor} too small, clamping to {MIN_SCALE_FACTOR}")
            scale_factor = MIN_SCALE_FACTOR
        try:
            config = self.load_config()
            config["scales"][tech_key] = round(scale_factor, 4)
            return self.save_config(config)
        except Exception as e:
            self.logger.error(f"Error updating scale factor for {tech_key}: {e}")
            return False
    
    def get_element_config(self, tech_key: str) -> Dict[str, Any]:
        """
        Get complete configuration for a HUD element.
        Args:
            tech_key: Technical key for the HUD element
        Returns:
            Dictionary with crop, scale, and overlay data
        """
        config = self.load_config()
        return {
            "crop_1080p": config["crops_1080p"].get(tech_key, [0, 0, 100, 100]),
            "scale": config["scales"].get(tech_key, 1.0),
            "overlay": config["overlays"].get(tech_key, {"x": 0, "y": 0})
        }
    
    def validate_config(self) -> List[str]:
        """
        Validate configuration and return list of issues.
        Returns:
            List of validation error messages
        """
        issues: List[str] = []
        config = self.load_config()
        for section in self.REQUIRED_SECTIONS:
            if section not in config:
                issues.append(f"Missing required section: {section}")
            elif not isinstance(config[section], dict):
                issues.append(f"Section {section} should be a dictionary")
        tech_keys: set[str] = set()
        tech_keys.update(config["crops_1080p"].keys())
        tech_keys.update(config["scales"].keys())
        tech_keys.update(config["overlays"].keys())
        for key in tech_keys:
            if key not in config["crops_1080p"]:
                issues.append(f"Key '{key}' missing in 'crops_1080p' section")
            if key not in config["scales"]:
                issues.append(f"Key '{key}' missing in 'scales' section")
            if key not in config["overlays"]:
                issues.append(f"Key '{key}' missing in 'overlays' section")
            if key not in config["z_orders"]:
                issues.append(f"Key '{key}' missing in 'z_orders' section")
            rect = config["crops_1080p"].get(key)
            if not isinstance(rect, list) or len(rect) < 4:
                issues.append(f"Invalid crop data for '{key}'")
            scale_val = config["scales"].get(key)
            if not isinstance(scale_val, (int, float)):
                issues.append(f"Invalid scale value for '{key}'")
            overlay_val = config["overlays"].get(key)
            if not isinstance(overlay_val, dict) or "x" not in overlay_val or "y" not in overlay_val:
                issues.append(f"Invalid overlay data for '{key}'")
            z_val = config["z_orders"].get(key)
            if not isinstance(z_val, int):
                issues.append(f"Invalid z-order value for '{key}'")
        return issues
    
    def get_configured_elements(self) -> List[str]:
        """Get list of configured HUD element technical keys (non-zero data only)."""
        config = self.load_config()
        configured = []
        for key, rect in config.get("crops_1080p", {}).items():
            if not isinstance(rect, list) or len(rect) < 4:
                continue
            width, height = rect[0], rect[1]
            scale_val = config.get("scales", {}).get(key, 0.0)
            if width > 1 and height > 1 and float(scale_val) > 0:
                configured.append(key)
        return configured
    
    def is_element_configured(self, tech_key: str) -> bool:
        """Check if a HUD element is fully configured."""
        config = self.load_config()
        rect = config.get("crops_1080p", {}).get(tech_key, [0, 0, 0, 0])
        scale_val = config.get("scales", {}).get(tech_key, 0.0)
        return bool(rect and len(rect) >= 4 and rect[0] > 1 and rect[1] > 1 and float(scale_val) > 0)

    def get_current_config_data(self) -> Dict[str, Any]:
        """Returns the entire current configuration data."""
        return self.load_config()
_config_manager_instances: Dict[str, ConfigManager] = {}

def get_config_manager(config_path: Optional[str] = None, logger: Optional[logging.Logger] = None) -> ConfigManager:
    """Get or create configuration manager instance scoped by config path."""
    global _config_manager_instances
    if config_path is None:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        base_dir = os.path.abspath(os.path.join(script_dir, '..'))
        config_path = os.path.join(base_dir, 'processing', 'crops_coordinations.conf')
    config_path = os.path.abspath(config_path)
    if config_path not in _config_manager_instances:
        _config_manager_instances[config_path] = ConfigManager(config_path, logger)
    else:
        if logger is not None:
            _config_manager_instances[config_path].logger = logger
    return _config_manager_instances[config_path]