import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowCoreCMixin:
    def _safe_handle_duration_changed(self, duration_ms: int):
        try:
            self.positionSlider.setRange(0, duration_ms)
            self.positionSlider.set_duration_ms(duration_ms)
            self.logger.info(f"UI: Duration updated to {duration_ms}ms via signal.")
        except Exception as e: self.logger.error("Error updating UI duration: %s", e)

    def _save_app_state_and_config(self):
        cfg = self.config_manager.config
        try: cfg['mobile_checked'] = bool(self.mobile_checkbox.isChecked())
        except Exception: pass
        try: cfg['teammates_checked'] = bool(self.teammates_checkbox.isChecked())
        except Exception: pass
        try: cfg['last_directory'] = self.last_dir
        except Exception: pass
        self.config_manager.save_config(cfg)
        self.logger.info("CONFIG: Saved current state to disk.")

    def cleanup_and_exit(self):
        self.logger.info("=== Application shutting down ===")
        self.blockSignals(True)
        if hasattr(self, "timer") and self.timer.isActive(): self.timer.stop()
        if getattr(self, "is_processing", False) and hasattr(self, "process_thread"):
            self.process_thread.cancel()
            if self.process_thread.isRunning(): self.process_thread.wait(3000)
        try:
            if getattr(self, "player", None):
                self.player.terminate()
                self.player = None
        except Exception as e: self.logger.error("Failed to safely stop MPV on close: %s", e)

        from system.utils import ProcessManager
        ProcessManager.cleanup_temp_files()
        self._save_app_state_and_config()
        QCoreApplication.instance().quit()

    def _on_slider_trim_changed(self, start_ms, end_ms):
        """[FIX #1] Ensure music handles follow video trim handles immediately."""
        self.trim_start_ms = start_ms
        self.trim_end_ms = end_ms
        
        # [FIX] Force music handles to jump to video handles if they exist
        has_music = (hasattr(self, "_wizard_tracks") and self._wizard_tracks) or hasattr(self, "music_timeline_start_ms")
        
        if has_music:
            self.music_timeline_start_ms = start_ms
            self.music_timeline_end_ms = end_ms
            if hasattr(self, "positionSlider"):
                # Visually update the pink handles
                self.positionSlider.set_music_times(start_ms, end_ms)
            
            # If we have exactly one track, update its duration to match the new handles
            if hasattr(self, "_wizard_tracks") and len(self._wizard_tracks) == 1:
                path, offset, _old_dur = self._wizard_tracks[0]
                new_dur = (end_ms - start_ms) / 1000.0
                self._wizard_tracks[0] = (path, offset, new_dur)
                
        self._update_trim_widgets_from_trim_times()

































