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
from typing import Dict, Any, Optional, List
from PyQt5.QtCore import QRect, QObject, pyqtSignal
from coordinate_math import (
    transform_to_content_area_int,
    validate_crop_rect,
    clamp_overlay_position
)
try:
    from validation_system import ValidationLevel, ValidationFeedback, ValidationRule
    VALIDATION_SYSTEM_AVAILABLE = True
except ImportError:
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
    REQUIRED_SECTIONS = ["crops_1080p", "scales", "overlays"]
    DEFAULT_VALUES = {
        "crops_1080p": {
            "loot": [300, 100, 700, 1400],
            "stats": [800, 30, 100, 50],
            "normal_hp": [40, 150, 20, 1400],
            "boss_hp": [60, 200, 20, 1400],
            "team": [200, 400, 50, 100]
        },
        "scales": {
            "loot": 1.5,
            "stats": 1.2,
            "team": 1.3,
            "normal_hp": 1.4,
            "boss_hp": 1.5
        },
        "overlays": {
            "loot": {"x": 600, "y": 1600},
            "stats": {"x": 100, "y": 50},
            "team": {"x": 50, "y": 100},
            "normal_hp": {"x": 20, "y": 1400},
            "boss_hp": {"x": 20, "y": 1400}
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
        self._last_known_config: Dict[str, Any] = copy.deepcopy(self.DEFAULT_VALUES)
        self._config_version = 0
        self._last_file_mtime = 0
        self._observer = ConfigObserver()
        if VALIDATION_SYSTEM_AVAILABLE:
            try:
                self.validation_feedback = ValidationFeedback()
                self._setup_validation_rules()
            except Exception as e:
                self.logger.error(f"Failed to initialize validation system: {e}")
    
    def get_observer(self) -> ConfigObserver:
        """Get the observer instance for subscribing to config changes."""
        return self._observer
    
    def _setup_validation_rules(self):
        """Setup validation rules for configuration."""
        if not self.validation_feedback:
            return
        self.validation_feedback.add_rule(ValidationRule(
            rule_id="config_file_exists",
            condition=lambda: os.path.exists(self.config_path),
            message="Configuration file does not exist",
            level=ValidationLevel.WARNING
        ))
        self.validation_feedback.add_rule(ValidationRule(
            rule_id="config_valid_json",
            condition=lambda: self._is_valid_json(),
            message="Configuration file contains invalid JSON",
            level=ValidationLevel.ERROR
        ))
        self.validation_feedback.add_rule(ValidationRule(
            rule_id="required_sections_exist",
            condition=lambda: self._has_required_sections(),
            message="Configuration missing required sections",
            level=ValidationLevel.ERROR
        ))
        self.validation_feedback.add_rule(ValidationRule(
            rule_id="section_consistency",
            condition=lambda: self._check_section_consistency(),
            message="Configuration sections are inconsistent",
            level=ValidationLevel.WARNING
        ))
    
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
            for key in tech_keys:
                if (key not in config["crops_1080p"] or 
                    key not in config["scales"] or 
                    key not in config["overlays"]):
                    return False
            return True
        except Exception:
            return False
    
    def _enforce_cross_section_consistency(self, config: Dict[str, Any]) -> None:
        """Ensure all sections have the same keys by adding missing entries with defaults."""
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
                        config[section][key] = [0, 0, 100, 100]
                    elif section == "scales":
                        config[section][key] = 1.0
                    elif section == "overlays":
                        config[section][key] = {"x": 0, "y": 0}
                    self.logger.debug(f"Added missing key '{key}' to section '{section}'")
        
    def _acquire_lock(self, timeout_seconds: int = 5) -> bool:
        """Acquire a file lock with improved deadlock prevention and watchdog timeout."""

        import psutil
        import threading
        import sys
        current_pid = os.getpid()
        start_time = time.time()
        max_attempts = 50
        attempt = 0
        self._cleanup_stale_lock(timeout_seconds)
        while attempt < max_attempts and (time.time() - start_time) < timeout_seconds:
            attempt += 1
            try:
                fd = os.open(self._lock_file_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                with os.fdopen(fd, 'w') as f:
                    f.write(str(current_pid))
                    f.flush()
                    os.fsync(fd)
                try:
                    if sys.platform != 'win32':
                        import fcntl
                        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    else:
                        import msvcrt
                        msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
                except (ImportError, AttributeError, OSError):
                    pass
                self.logger.debug(f"Acquired lock for {self.config_path} (PID: {current_pid})")
                self._start_lock_watchdog()
                return True
            except FileExistsError:
                if not self._check_and_break_stale_lock(timeout_seconds):
                    wait_time = min(0.1 * (1.5 ** attempt), 1.0)
                    time.sleep(wait_time)
                    continue
                else:
                    continue
            except Exception as e:
                self.logger.error(f"Unexpected error acquiring lock: {e}")
                return False
        self.logger.warning(f"Failed to acquire lock for {self.config_path} after {timeout_seconds}s")
        self._notify_lock_timeout()
        return False
    
    def _cleanup_stale_lock(self, timeout_seconds: int):
        """Clean up stale lock file on startup."""
        if os.path.exists(self._lock_file_path):
            lock_age = time.time() - os.path.getmtime(self._lock_file_path)
            if lock_age > timeout_seconds * 2:
                try:
                    os.unlink(self._lock_file_path)
                    self.logger.warning(f"Cleaned up stale lock on startup (age: {lock_age:.1f}s)")
                except OSError:
                    pass
    
    def _check_and_break_stale_lock(self, timeout_seconds: int) -> bool:
        """Check if lock is stale and break it if necessary."""

        import psutil
        try:
            if not os.path.exists(self._lock_file_path):
                return False
            lock_age = time.time() - os.path.getmtime(self._lock_file_path)
            if lock_age > timeout_seconds:
                self.logger.warning(f"Breaking stale lock (age: {lock_age:.1f}s > {timeout_seconds}s)")
                try:
                    os.unlink(self._lock_file_path)
                except OSError:
                    pass
                return True
            try:
                with open(self._lock_file_path, 'r') as f:
                    lock_pid_str = f.read().strip()
                    if lock_pid_str:
                        lock_pid = int(lock_pid_str)
                        if not psutil.pid_exists(lock_pid):
                            self.logger.warning(f"Breaking lock from dead process {lock_pid}")
                            try:
                                os.unlink(self._lock_file_path)
                            except OSError:
                                pass
                            return True
            except (ValueError, OSError):
                self.logger.warning("Breaking corrupted lock file")
                try:
                    os.unlink(self._lock_file_path)
                except OSError:
                    pass
                return True
        except (OSError, psutil.NoSuchProcess, psutil.AccessDenied) as e:
            self.logger.debug(f"Lock check error: {e}")
        return False
    
    def _start_lock_watchdog(self):
        """Start a watchdog timer to automatically release lock if thread dies."""

        def watchdog():
            time.sleep(30)
            if os.path.exists(self._lock_file_path):
                try:
                    with open(self._lock_file_path, 'r') as f:
                        pid = int(f.read().strip())
                        if pid == os.getpid():
                            self.logger.error("Lock watchdog triggered - forcing lock release")
                            self._release_lock()
                except:
                    pass
        
        import threading
        watchdog_thread = threading.Thread(target=watchdog, daemon=True)
        watchdog_thread.start()
    
    def _notify_lock_timeout(self):
        """Notify user about lock timeout (could be extended to show UI message)."""
        self.logger.error(f"LOCK TIMEOUT: Could not acquire lock for {self.config_path}")
    
    def _release_lock(self):
        """Release the file lock."""
        try:
            if os.path.exists(self._lock_file_path):
                os.unlink(self._lock_file_path)
                self.logger.debug(f"Released lock for {self.config_path}")
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
                    config = self.DEFAULT_VALUES.copy()
                    if isinstance(file_config, dict):
                        for section in self.REQUIRED_SECTIONS:
                            if section in file_config:
                                config[section] = file_config[section]
                            else:
                                self.logger.warning(f"Missing section '{section}' in config, using defaults")
                        for key, value in file_config.items():
                            if key not in self.REQUIRED_SECTIONS:
                                config[key] = value
                        self._last_known_config = copy.deepcopy(config)
                        self._last_file_mtime = current_mtime
                        return config
                except Exception as e:
                    self.logger.warning(f"Fallback read failed after lock error: {e}")
            self.logger.warning("Lock unavailable; returning last known config")
            return copy.deepcopy(self._last_known_config)
        config = self.DEFAULT_VALUES.copy()
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
                    for section in self.REQUIRED_SECTIONS:
                        if section in file_config:
                            config[section] = file_config[section]
                        else:
                            self.logger.warning(f"Missing section '{section}' in config, using defaults")
                    for key, value in file_config.items():
                        if key not in self.REQUIRED_SECTIONS:
                            config[key] = value
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
            for section in self.REQUIRED_SECTIONS:
                if section not in config:
                    config[section] = self.DEFAULT_VALUES[section]
                    self.logger.warning(f"Added missing section '{section}' to config")
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
                self.logger.info("=" * 80)
                self.logger.info("CONFIGURATION SAVED SUCCESSFULLY")
                self.logger.info("-" * 40)
                self.logger.info(f"File: {self.config_path}")
                self.logger.info(f"Timestamp: {time.strftime('%Y-%m-%d %H:%M:%S')}")
                self.logger.info(f"Sections: {', '.join(self.REQUIRED_SECTIONS)}")
                self.logger.info(f"Configured elements: {len(config['crops_1080p'])}")
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
                success = True
                old_config = self._last_known_config
                new_keys = set(config["crops_1080p"].keys())
                old_keys = set(old_config.get("crops_1080p", {}).keys())
                for key in new_keys:
                    if key in old_keys:
                        old_crop = old_config.get("crops_1080p", {}).get(key)
                        new_crop = config["crops_1080p"].get(key)
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

    def _transform_coordinates_rational(self, rect: QRect, original_resolution: str) -> List[int]:
        """
        Transform coordinates from original video to 1080p portrait content area (1080x1620).
        Uses centralized coordinate math to ensure consistency with filter_builder.
        Returns [x, y, width, height] in 1080p portrait content area coordinates (y=0..1620).
        Note: The final overlay positions have y offset +150 for padding.
        """
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
            normalized_rect = [w_content, h_content, x_content, y_content]
            config = self.load_config()
            config["crops_1080p"][tech_key] = normalized_rect
            success = self.save_config(config, enforce_consistency=True)
            if success:
                self.logger.info(f"Saved TRANSFORMED crop coordinates for {tech_key}: {normalized_rect} (from original {original_resolution})")
                return True
            else:
                self.logger.error(f"Failed to save crop coordinates for {tech_key}")
                return False
        except Exception as e:
            self.logger.error(f"Error saving crop coordinates for {tech_key}: {e}")
            return False

    def delete_crop_coordinates(self, tech_key: str) -> bool:
        """
        [FIX #2] Zero-outs configuration data instead of deleting keys.
        Deleting keys causes the processing engine to fall back to hardcoded defaults (Ghost Elements).
        """
        config_data = self.get_current_config_data()
        changes_made = False
        if 'crops_1080p' in config_data and tech_key in config_data['crops_1080p']:
            config_data['crops_1080p'][tech_key] = [0, 0, 0, 0]
            changes_made = True
        if 'scales' in config_data and tech_key in config_data['scales']:
            config_data['scales'][tech_key] = 0.0
            changes_made = True
        if 'overlays' in config_data and tech_key in config_data['overlays']:
            config_data['overlays'][tech_key] = {"x": 0, "y": 0}
            changes_made = True
        if changes_made:
            self.logger.info(f"Zeroed-out configuration data for tech_key: {tech_key} (Prevents default fallback)")
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
            BACKEND_SCALE = 1280.0 / 1080.0
            x_scaled = int(round(x * BACKEND_SCALE))
            y_scaled = int(round(y * BACKEND_SCALE))
            clamped_x_scaled, clamped_y_scaled = clamp_overlay_position(x_scaled, y_scaled, scaled_width, scaled_height)
            clamped_x = int(round(clamped_x_scaled / BACKEND_SCALE))
            clamped_y = int(round(clamped_y_scaled / BACKEND_SCALE))
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
        EPSILON = 0.001
        if scale_factor < EPSILON:
            self.logger.warning(f"Scale factor {scale_factor} too small, clamping to {EPSILON}")
            scale_factor = EPSILON
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
        if self.validation_feedback:
            try:
                validation_results = self.validation_feedback.check_all_rules({})
                for rule_id, result in validation_results.items():
                    if not result['valid']:
                        issues.append(f"Validation: {result['message']}")
            except Exception as e:
                self.logger.error(f"Validation system error: {e}")
        return issues
    
    def get_configured_elements(self) -> List[str]:
        """Get list of configured HUD element technical keys."""
        config = self.load_config()
        return list(config["crops_1080p"].keys())
    
    def is_element_configured(self, tech_key: str) -> bool:
        """Check if a HUD element is fully configured."""
        config = self.load_config()
        return (tech_key in config["crops_1080p"] and
                tech_key in config["scales"] and
                tech_key in config["overlays"])

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