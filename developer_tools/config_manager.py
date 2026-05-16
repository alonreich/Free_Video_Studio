import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')

import os
import json
import logging
import tempfile
import shutil
import time
import copy
import threading
import uuid
try:
    import psutil
    HAS_PSUTIL = True
except ImportError:
    HAS_PSUTIL = False

from typing import Dict, Any, Optional, List
from PyQt5.QtCore import QRect, QObject, pyqtSignal
try:
    from processing.coordinate_math import (
        transform_to_content_area_int,
        clamp_overlay_position,
        scale_round
    )

    from processing.hud_config import DEFAULT_HUD_CONFIG, HUD_REQUIRED_SECTIONS, HUD_Z_DEFAULTS, sanitize_hud_config, validate_hud_config
except ImportError:
    from processing.coordinate_math import (
        transform_to_content_area_int,
        clamp_overlay_position,
        scale_round
    )

    from processing.hud_config import DEFAULT_HUD_CONFIG, HUD_REQUIRED_SECTIONS, HUD_Z_DEFAULTS, sanitize_hud_config, validate_hud_config
try:
    from .config import UI_BEHAVIOR
    MIN_SCALE_FACTOR = UI_BEHAVIOR.MIN_SCALE_FACTOR
except ImportError:
    try:
        from config import UI_BEHAVIOR
        MIN_SCALE_FACTOR = UI_BEHAVIOR.MIN_SCALE_FACTOR
    except Exception:
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
_config_manager_instances: Dict[str, "ConfigManager"] = {}
_config_manager_instances_lock = threading.Lock()

class ConfigObserver(QObject):
    config_changed = pyqtSignal(str)
    config_deleted = pyqtSignal(str)
    config_loaded = pyqtSignal()
    
    def __init__(self):
        super().__init__()

class ConfigManager:
    REQUIRED_SECTIONS = list(HUD_REQUIRED_SECTIONS)
    DEFAULT_VALUES = copy.deepcopy(DEFAULT_HUD_CONFIG)

    def __init__(self, config_path: str, logger: Optional[logging.Logger] = None):
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
        self._lock_owner_token: Optional[str] = None
        self._validation_rules: Dict[str, Any] = {}
        self._setup_validation_rules()
    
    def get_observer(self) -> ConfigObserver:
        return self._observer
    
    def _setup_validation_rules(self):
        self._validation_rules = {
            "required_sections": list(self.REQUIRED_SECTIONS),
            "min_scale": MIN_SCALE_FACTOR,
            "min_crop_size": 1,
        }
    
    def _is_valid_json(self) -> bool:
        if not os.path.exists(self.config_path):
            return False
        try:
            with open(self.config_path, 'r', encoding='utf-8') as f:
                json.load(f)
            return True
        except (json.JSONDecodeError, OSError):
            return False
    
    def _has_required_sections(self) -> bool:
        try:
            config = self.load_config()
            for section in self.REQUIRED_SECTIONS:
                if section not in config:
                    return False
            return True
        except Exception:
            return False
    
    def _check_section_consistency(self) -> bool:
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
        if not self.is_hud_config:
            return
        if "crops_1080p" not in config:
            config["crops_1080p"] = {}
        master_keys = set(config["crops_1080p"].keys())
        for section in ["scales", "overlays", "z_orders"]:
            if section in config and isinstance(config[section], dict):
                for key in list(config[section].keys()):
                    if key not in master_keys:
                        del config[section][key]
        for key in master_keys:
            for section in ["scales", "overlays", "z_orders"]:
                if section not in config:
                    config[section] = {}
                if key not in config[section]:
                    if section == "scales":
                        config[section][key] = 1.0
                    elif section == "overlays":
                        if key == "spectating":
                            config[section][key] = {"x": 30, "y": 1300}
                        else:
                            config[section][key] = {"x": 0, "y": 0}
                    elif section == "z_orders":
                        config[section][key] = Z_ORDER_MAP.get(key, 10)

    def _filter_hud_config(self, config: Dict[str, Any]) -> Dict[str, Any]:
        if not isinstance(config, dict):
            return copy.deepcopy(self.DEFAULT_VALUES) if self.is_hud_config else {}
        clean = copy.deepcopy(config)
        if self.is_hud_config:
            for section in self.REQUIRED_SECTIONS:
                if section not in clean or not isinstance(clean[section], dict):
                    clean[section] = {}
        return clean

    def _sanitize_config(self, config: Dict[str, Any]) -> Dict[str, Any]:
        if not self.is_hud_config:
            return self._filter_hud_config(config)
        return sanitize_hud_config(config)
        
    def _acquire_lock(self, timeout_seconds: int = 5) -> bool:
        try:
            lock_dir = os.path.dirname(self._lock_file_path)
            if lock_dir and not os.path.exists(lock_dir):
                os.makedirs(lock_dir, exist_ok=True)
        except Exception as e:
            self.logger.error(f"Failed to create directory for lock file: {e}")
            return False
        start_time = time.time()
        while (time.time() - start_time) < timeout_seconds:
            try:
                fd = os.open(self._lock_file_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                lock_token = uuid.uuid4().hex
                lock_payload = {
                    "pid": os.getpid(),
                    "token": lock_token,
                    "created_at": time.time(),
                }
                try:
                    os.write(fd, json.dumps(lock_payload).encode("utf-8"))
                finally:
                    os.close(fd)
                self._lock_owner_token = lock_token
                self.logger.debug(f"Acquired lock for {self.config_path}")
                return True
            except FileExistsError:
                try:
                    lock_mtime = os.path.getmtime(self._lock_file_path)
                    lock_age = time.time() - lock_mtime
                    is_dead_process = False
                    try:
                        with open(self._lock_file_path, 'r', encoding='utf-8') as f:
                            lock_data = json.loads(f.read().strip())
                            pid = int(lock_data.get("pid", 0))
                            if HAS_PSUTIL:
                                import psutil
                                if not psutil.pid_exists(pid):
                                    is_dead_process = True
                    except Exception as lock_read_error:
                        self.logger.debug(f"Lock metadata read failed: {lock_read_error}")
                    if lock_age > timeout_seconds or is_dead_process:
                        self.logger.warning(f"Breaking stale lock (age: {lock_age:.1f}s) for {self.config_path}")
                        try:
                            os.unlink(self._lock_file_path)
                        except OSError as unlink_error:
                            self.logger.warning(f"Failed to remove stale lock {self._lock_file_path}: {unlink_error}")
                        continue
                except Exception as e:
                    self.logger.warning(f"Error checking lock staleness: {e}")
                time.sleep(0.1)
            except Exception as e:
                self.logger.error(f"Lock acquisition error: {e}")
                return False
        return False

    def _release_lock(self):
        try:
            if os.path.exists(self._lock_file_path):
                should_remove = True
                if self._lock_owner_token:
                    try:
                        with open(self._lock_file_path, 'r', encoding='utf-8') as f:
                            lock_data = json.loads(f.read().strip())
                        file_token = lock_data.get("token")
                        if file_token and file_token != self._lock_owner_token:
                            should_remove = False
                            self.logger.warning("Skipping lock release: token mismatch, another writer owns lock")
                    except Exception as read_error:
                        self.logger.debug(f"Lock token verification skipped: {read_error}")
                if should_remove:
                    os.unlink(self._lock_file_path)
            self._lock_owner_token = None
        except Exception as e:
            self.logger.error(f"Error releasing lock: {e}")
    
    def load_config(self) -> Dict[str, Any]:
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
                        self.logger.debug(f"Verified new config; retained rolling backup at {backup_path}")
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
                    except Exception as cleanup_error:
                        self.logger.warning(f"Failed to remove temp config file {temp_path}: {cleanup_error}")
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
        try:
            import glob
            pattern = f"{self.config_path}.backup.*"
            backups = sorted(glob.glob(pattern), key=os.path.getmtime, reverse=True)
            for stale in backups[max_backups:]:
                try:
                    os.unlink(stale)
                except Exception as prune_error:
                    self.logger.warning(f"Failed to remove stale backup {stale}: {prune_error}")
            config_dir = os.path.dirname(self.config_path)
            config_filename = os.path.basename(self.config_path)
            old_backup_path = os.path.join(config_dir, f"old_{config_filename}")
            if os.path.exists(old_backup_path):
                try:
                    max_age_seconds = 60 * 60 * 24 * 3
                    if (time.time() - os.path.getmtime(old_backup_path)) > max_age_seconds:
                        os.unlink(old_backup_path)
                except Exception as old_backup_error:
                    self.logger.warning(f"Failed to remove old backup {old_backup_path}: {old_backup_error}")
        except Exception as e:
            self.logger.debug(f"Backup pruning skipped: {e}")

    def validate_config_data(self, config: Dict[str, Any]) -> List[str]:
        if self.is_hud_config:
            return validate_hud_config(config)
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
    
    def delete_crop_coordinates(self, tech_key: str) -> bool:
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

            from fractions import Fraction
            scaled_width = scale_round(Fraction(str(width)) * Fraction(str(scale)))
            scaled_height = scale_round(Fraction(str(height)) * Fraction(str(scale)))
            clamped_x, clamped_y = clamp_overlay_position(
                x,
                y,
                scaled_width,
                scaled_height,
                padding_top_ui=150,
                padding_bottom_ui=150
            )
            if clamped_x != x or clamped_y != y:
                self.logger.debug(f"Overlay position clamped from ({x},{y}) to ({clamped_x},{clamped_y}) in portrait space")
            config["overlays"][tech_key] = {"x": clamped_x, "y": clamped_y}
            return self.save_config(config)
        except Exception as e:
            self.logger.error(f"Error updating overlay position for {tech_key}: {e}")
            return False
    
    def update_scale_factor(self, tech_key: str, scale_factor: float) -> bool:
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
        config = self.load_config()
        return {
            "crop_1080p": config["crops_1080p"].get(tech_key, [0, 0, 100, 100]),
            "scale": config["scales"].get(tech_key, 1.0),
            "overlay": config["overlays"].get(tech_key, {"x": 0, "y": 0})
        }
    
    def validate_config(self) -> List[str]:
        issues: List[str] = []
        if not self._is_valid_json():
            issues.append("Configuration file is missing or contains invalid JSON")
            return issues
        if not self._has_required_sections():
            issues.append("Configuration is missing one or more required sections")
        if not self._check_section_consistency():
            issues.append("Configuration sections are inconsistent across HUD keys")
        issues.extend(self.validate_config_data(self.load_config()))
        return issues
    
    def get_configured_elements(self) -> List[str]:
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
        config = self.load_config()
        rect = config.get("crops_1080p", {}).get(tech_key, [0, 0, 0, 0])
        scale_val = config.get("scales", {}).get(tech_key, 0.0)
        return bool(rect and len(rect) >= 4 and rect[0] > 1 and rect[1] > 1 and float(scale_val) > 0)

    def get_current_config_data(self) -> Dict[str, Any]:
        return self.load_config()

def get_config_manager(config_path: Optional[str] = None, logger: Optional[logging.Logger] = None) -> ConfigManager:
    if config_path is None:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        base_dir = os.path.abspath(os.path.join(script_dir, '..'))
        config_path = os.path.join(base_dir, 'processing', 'crops_coordinations.conf')
    config_path = os.path.abspath(config_path)
    with _config_manager_instances_lock:
        if config_path not in _config_manager_instances:
            _config_manager_instances[config_path] = ConfigManager(config_path, logger)
        else:
            if logger is not None:
                _config_manager_instances[config_path].logger = logger
        return _config_manager_instances[config_path]
