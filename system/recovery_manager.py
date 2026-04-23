import os
import sys
import json
import psutil
import tempfile
import threading
import logging
from pathlib import Path
from typing import Any, Dict, Optional, Callable

class RecoveryManager:
    def __init__(self, app_id: str, logger: Optional[logging.Logger] = None):
        self.app_id = app_id
        self.logger = logger or logging.getLogger(f"Recovery_{app_id}")
        self.temp_dir = Path(tempfile.gettempdir())
        self.lock_file = self.temp_dir / f"{app_id}.lock"
        self.state_file = self.temp_dir / f"{app_id}_recovery.json"
        self.safe_mode_file = self.temp_dir / f"{app_id}_safe_mode.sentinel"
        self._lock_handle = None
        self._last_save_time = 0
        self._safe_mode_threshold = 120

    def check_fault(self) -> bool:
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
                return False
            return self.state_file.exists()
        except (ValueError, OSError):
            return True

    def acquire_lock(self):
        try:
            with open(self.lock_file, "w") as f:
                f.write(str(os.getpid()))
        except Exception:
            pass

    def cleanup_lock(self):
        try:
            if self.lock_file.exists():
                self.lock_file.unlink()
            if self.safe_mode_file.exists():
                self.safe_mode_file.unlink()
        except Exception:
            pass

    def is_safe_mode_active(self) -> bool:
        if self.safe_mode_file.exists():
            try:
                mtime = self.safe_mode_file.stat().st_mtime

                import time
                if time.time() - mtime < self._safe_mode_threshold:
                    return True
            except Exception:
                pass
        return False

    def activate_safe_mode(self):
        try:
            self.safe_mode_file.touch()
        except Exception:
            pass

    def save_state_async(self, state: Dict[str, Any]):
        thread = threading.Thread(target=self.save_state, args=(state,), daemon=True)
        thread.start()

    def save_state(self, state: Dict[str, Any]):
        try:
            import time
            state["_recovery_timestamp"] = time.time()
            temp_fd, temp_path = tempfile.mkstemp(dir=self.temp_dir, suffix=".tmp")
            try:
                with os.fdopen(temp_fd, 'w', encoding='utf-8') as f:
                    json.dump(state, f, indent=4, ensure_ascii=False)
                    f.flush()
                    os.fsync(f.fileno())
                os.replace(temp_path, self.state_file)
            except Exception as e:
                if os.path.exists(temp_path):
                    os.remove(temp_path)
                raise e
        except Exception:
            pass

    def load_state(self) -> Optional[Dict[str, Any]]:
        if not self.state_file.exists():
            return None
        try:
            with open(self.state_file, 'r', encoding='utf-8') as f:
                return json.load(f)
        except Exception:
            return None

    def clear_state(self):
        try:
            if self.state_file.exists():
                self.state_file.unlink()
        except Exception:
            pass
RECOVERY_JSON_SCHEMA_MAIN = {
    "app_id": "main_app",
    "version": "1.0.1",
    "assets": {
        "input_file_path": None,
        "wizard_tracks": [],
        "current_music_path": None
    },
    "volatile_settings": {
        "trim_start_ms": 0,
        "trim_end_ms": 0,
        "playback_rate": 1.1,
        "speed_segments": [],
        "video_mix_volume": 100,
        "music_volume_pct": 80,
        "video_volume_pct": 80,
        "quality_slider_index": 7,
        "thumbnail_pos_ms": 0
    },
    "ui_dynamics": {
        "mobile_checked": False,
        "slider_value_ms": 0
    }
}
RECOVERY_JSON_SCHEMA_MERGER = {
    "app_id": "video_merger",
    "version": "1.0.1",
    "assets": {
        "video_files": [],
        "wizard_tracks": []
    },
    "volatile_settings": {
        "video_volume": 100,
        "music_volume": 80,
        "quality_level": 7
    },
    "ui_dynamics": {
        "window_geometry_base64": "",
        "last_dir": ""
    }
}
