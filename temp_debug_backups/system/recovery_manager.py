import os
import sys
import json
import psutil
import tempfile
import threading
import logging
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

class RecoveryManager:
    """
    High-Integrity Crash Recovery Protocol (CRP) for Fortnite Video Software.
    Handles fault detection, transactional state logging, and session restoration.
    """

    def __init__(self, app_id: str, logger: Optional[logging.Logger] = None):
        self.app_id = app_id
        self.logger = logger or logging.getLogger(f"Recovery_{app_id}")
        self.temp_dir = Path(tempfile.gettempdir())
        self.lock_file = self.temp_dir / f"{self.app_id}_session.lock"
        self.state_file = self.temp_dir / f"{self.app_id}_recovery_v2.json"
        self.safe_mode_file = self.temp_dir / f"{self.app_id}_safe_mode.sentinel"
        self._safe_mode_threshold = 120 

    def check_fault(self) -> bool:
        """
        Determines if the previous session ended unexpectedly.
        Returns True if a crash is detected and restoration is possible.
        """
        if not self.lock_file.exists():
            return False
        try:
            with open(self.lock_file, "r") as f:
                content = f.read().strip()
                if not content:
                    return True
                old_pid = int(content)
            if psutil.pid_exists(old_pid):
                try:
                    proc = psutil.Process(old_pid)
                    if proc.is_running() and proc.status() != psutil.STATUS_ZOMBIE:
                        return False 
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    pass
            if self.is_safe_mode_active():
                self.logger.warning(f"CRP: Safe Mode active for {self.app_id}. Bypassing recovery to prevent loop.")
                return False
            return self.state_file.exists()
        except (ValueError, OSError) as e:
            self.logger.error(f"CRP: Error checking fault state: {e}")
            return True

    def is_safe_mode_active(self) -> bool:
        """Checks if a recovery attempt crashed within the threshold period."""
        if self.safe_mode_file.exists():
            try:
                mtime = self.safe_mode_file.stat().st_mtime
                if time.time() - mtime < self._safe_mode_threshold:
                    return True
                else:
                    self.safe_mode_file.unlink()
            except Exception:
                pass
        return False

    def activate_safe_mode(self):
        """Creates a sentinel to detect rapid consecutive crashes during recovery."""
        try:
            self.safe_mode_file.touch()
            self.logger.info(f"CRP: Safe Mode sentinel created for {self.app_id}.")
        except Exception as e:
            self.logger.error(f"CRP: Failed to create safe mode sentinel: {e}")

    def acquire_lock(self):
        """Locks the application instance and records the current PID."""
        try:
            with open(self.lock_file, "w") as f:
                f.write(str(os.getpid()))
        except Exception as e:
            self.logger.error(f"CRP: Failed to acquire lock: {e}")

    def cleanup_lock(self):
        """Removes session locks upon clean exit."""
        try:
            if self.lock_file.exists():
                self.lock_file.unlink()
            if self.safe_mode_file.exists():
                self.safe_mode_file.unlink()
            self.logger.info(f"CRP: Clean exit for {self.app_id}, locks removed.")
        except Exception as e:
            self.logger.error(f"CRP: Cleanup error: {e}")

    def save_state_async(self, state: Dict[str, Any]):
        """Serializes session state on a background thread to prevent UI stutter."""
        thread = threading.Thread(target=self.save_state, args=(state,), daemon=True)
        thread.start()

    def save_state(self, state: Dict[str, Any]):
        """
        Performs an atomic 'write-then-replace' state serialization.
        Ensures the recovery file is never corrupted mid-crash.
        """
        try:
            state["_recovery_meta"] = {
                "timestamp": time.time(),
                "app_id": self.app_id,
                "pid": os.getpid()
            }
            fd, temp_path = tempfile.mkstemp(dir=str(self.temp_dir), prefix="rec_", suffix=".tmp")
            try:
                with os.fdopen(fd, 'w', encoding='utf-8') as f:
                    json.dump(state, f, indent=4, ensure_ascii=False)
                    f.flush()
                    os.fsync(f.fileno())
                os.replace(temp_path, str(self.state_file))
            except Exception as e:
                if os.path.exists(temp_path):
                    os.remove(temp_path)
                raise e
        except Exception as e:
            self.logger.error(f"CRP: State save failed: {e}")

    def load_state(self) -> Optional[Dict[str, Any]]:
        """Reads the recovery JSON if it exists."""
        if not self.state_file.exists():
            return None
        try:
            with open(self.state_file, 'r', encoding='utf-8') as f:
                return json.load(f)
        except Exception as e:
            self.logger.error(f"CRP: Failed to load recovery state: {e}")
            return None

    def validate_assets(self, state: Dict[str, Any]) -> Tuple[bool, List[str]]:
        """
        Verifies that all file pointers in the state still exist on disk.
        Returns (all_present, missing_files_list).
        """
        missing = []
        assets = state.get("assets", {})
        input_path = assets.get("input_file_path")
        if input_path and not os.path.exists(input_path):
            missing.append(input_path)
        for track in assets.get("wizard_tracks", []):
            p = track.get("path")
            if p and not os.path.exists(p):
                missing.append(p)
        for video_entry in assets.get("video_files", []):
            p = video_entry.get("path") if isinstance(video_entry, dict) else video_entry
            if p and not os.path.exists(p):
                missing.append(p)
        return (len(missing) == 0, missing)

    def clear_state(self):
        """Manually wipes the recovery state (e.g., after user declines restore)."""
        try:
            if self.state_file.exists():
                self.state_file.unlink()
        except Exception as e:
            self.logger.error(f"CRP: State clear failed: {e}")
RECOVERY_JSON_SCHEMA_MAIN = {
    "app_id": "main_app",
    "version": "2.0.0",
    "assets": {
        "input_file_path": "C:\\Videos\\VictoryRoyale.mp4",
        "wizard_tracks": [
            {"path": "C:\\Music\\Background_Lobby.mp3", "volume": 0.75, "start_ms": 1000}
        ],
        "current_music_path": "C:\\Music\\Background_Lobby.mp3"
    },
    "volatile_settings": {
        "trim_start_ms": 2500,
        "trim_end_ms": 12000,
        "playback_rate": 1.5,
        "speed_segments": [
            {"start": 0, "end": 2000, "multiplier": 1.0},
            {"start": 2000, "end": 5000, "multiplier": 4.0}
        ],
        "video_mix_volume": 100,
        "music_volume_pct": 80,
        "video_volume_pct": 80,
        "quality_slider_index": 7,
        "thumbnail_pos_ms": 3000,
        "freeze_durations": {"10.5": 2.0},
        "intro_generator_active": True,
        "whatsapp_thumbnail_status": "generated",
        "whatsapp_thumbnail_sec": 0.1
    },
    "ui_dynamics": {
        "show_teammates": True,
        "portrait_9x16_mode": True,
        "active_tab_index": 1,
        "button_states": {
            "add_music": {"color": "#2ecc71", "text": "Music Added"},
            "process": {"enabled": True}
        },
        "checkboxes": {
            "show_teammates": True,
            "portrait_9x16": True
        },
        "slider_value_ms": 3000
    }
}
RECOVERY_JSON_SCHEMA_MERGER = {
    "app_id": "video_merger",
    "version": "2.0.0",
    "assets": {
        "video_files": [
            {"path": "C:\\Videos\\Clip1.mp4", "id": "uuid-1"},
            {"path": "C:\\Videos\\Clip2.mp4", "id": "uuid-2"}
        ],
        "wizard_tracks": [],
        "merger_sequence": [0, 1]
    },
    "volatile_settings": {
        "video_volume": 100,
        "music_volume": 80,
        "quality_level": 7,
        "transition_type": "crossfade",
        "transition_duration": 1.5
    },
    "ui_dynamics": {
        "window_geometry_base64": "AdnQpwAA...",
        "last_dir": "C:\\Videos",
        "active_selection_index": 0,
        "button_colors": {"merge": "#1b6d26"}
    }
}
