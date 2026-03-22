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
        try:
            cfg['geometry'] = {
                'x': self.x(),
                'y': self.y(),
                'w': self.width(),
                'h': self.height()
            }

            from PyQt5.QtCore import QByteArray
            cfg['qt_geometry'] = bytes(self.saveGeometry().toBase64()).decode('utf-8')
        except Exception as e:
            self.logger.error(f"Failed to save window geometry: {e}")
        self.config_manager.save_config(cfg)
        self.logger.info("CONFIG: Saved current state to disk.")

    def save_geometry(self):
        self._save_app_state_and_config()

    def restore_geometry(self):
        try:
            cfg = self.config_manager.config
            qt_geom = cfg.get('qt_geometry')
            if qt_geom:
                from PyQt5.QtCore import QByteArray
                if self.restoreGeometry(QByteArray.fromBase64(qt_geom.encode('utf-8'))):
                    self.logger.info("Restored window geometry via Qt.")
                    return
            geom = cfg.get('geometry')
            if geom and isinstance(geom, dict):
                x, y = geom.get('x'), geom.get('y')
                w, h = geom.get('w'), geom.get('h')
                if x is not None and y is not None:
                    from PyQt5.QtWidgets import QApplication
                    from PyQt5.QtCore import QPoint
                    screen = QApplication.screenAt(QPoint(x, y)) or QApplication.primaryScreen()
                    avail = screen.availableGeometry()
                    x = max(avail.x(), min(x, avail.right() - 100))
                    y = max(avail.y(), min(y, avail.bottom() - 100))
                    self.move(x, y)
                if w is not None and h is not None:
                    self.resize(w, h)
                self.logger.info(f"Restored window geometry: {x},{y} {w}x{h}")
        except Exception as e:
            self.logger.error(f"Failed to restore window geometry: {e}")

    def cleanup_and_exit(self):
        self.logger.info("=== Application shutting down ===")
        if getattr(self, "_is_seeking_active", False):
            self.logger.info("EXIT: Waiting for active seek threads to finish...")
            time.sleep(0.3)
        self.blockSignals(True)
        if hasattr(self, "timer") and self.timer.isActive(): self.timer.stop()
        if hasattr(self, "_cleanup_live_logging"):
            self._cleanup_live_logging()
        if hasattr(self, "_hw_worker") and self._hw_worker:
            try: self._hw_worker.abort()
            except: pass
        if getattr(self, "is_processing", False) and hasattr(self, "process_thread"):
            try:
                self.process_thread.cancel()
                if self.process_thread.isRunning(): self.process_thread.wait(3000)
            except: pass
        try:
            from system.utils import MPVSafetyManager
            if getattr(self, "player", None):
                MPVSafetyManager.safe_mpv_shutdown(self.player, lock=getattr(self, "_mpv_lock", None))
                self.player = None
            if getattr(self, "_music_preview_player", None):
                MPVSafetyManager.safe_mpv_shutdown(self._music_preview_player, lock=getattr(self, "_mpv_lock", None))
                self._music_preview_player = None
        except Exception as e: self.logger.error("Failed to safely stop MPV on close: %s", e)

        from system.utils import ProcessManager
        ProcessManager.cleanup_temp_files()
        self._save_app_state_and_config()
        try:
            from ui.main_window import _QtLiveLogHandler
            if hasattr(self, "logger"):
                self.logger.handlers = [h for h in self.logger.handlers if not isinstance(h, _QtLiveLogHandler)]
        except Exception:
            pass
        QCoreApplication.instance().quit()

    def _on_slider_trim_changed(self, start_ms, end_ms):
        self.trim_start_ms = start_ms
        self.trim_end_ms = end_ms
        has_music = (hasattr(self, "_wizard_tracks") and self._wizard_tracks)
        if has_music:
            new_m_start = max(start_ms, self.music_timeline_start_ms)
            new_m_end = min(end_ms, self.music_timeline_end_ms)
            self.music_timeline_start_ms = new_m_start
            self.music_timeline_end_ms = new_m_end
            if hasattr(self, "positionSlider"):
                self.positionSlider.set_music_times(new_m_start, new_m_end)
            if hasattr(self, "_wizard_tracks") and len(self._wizard_tracks) == 1:
                path, offset, _old_dur = self._wizard_tracks[0]
                new_dur = (end_ms - start_ms) / 1000.0
                self._wizard_tracks[0] = (path, offset, new_dur)
        else:
            self.music_timeline_start_ms = 0
            self.music_timeline_end_ms = 0
            if hasattr(self, "positionSlider"):
                self.positionSlider.reset_music_times()
        self._update_trim_widgets_from_trim_times()
        if hasattr(self, "_update_quality_label"):
            self._update_quality_label()
