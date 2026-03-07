import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *
from system.utils import MPVSafetyManager

class MainWindowCoreAMixin:
    def open_granular_speed_dialog(self):
        try:
            if not self.input_file_path:
                 self.granular_checkbox.blockSignals(True)
                 self.granular_checkbox.setChecked(False)
                 self.granular_checkbox.blockSignals(False)
                 QMessageBox.warning(self, "No Video", "Please load a video first.")
                 return
            if not self.granular_checkbox.isChecked():
                self.status_bar.showMessage("Granular Speed disabled (segments preserved).", 3000)
                return
            self._opening_granular_dialog = True
            self._ignore_mpv_end_until = time.time() + 2.0
            current_ms = 0
            MPVSafetyManager.log_mpv_diagnostics(self.player, self.logger, "SPEED_EDITOR_OPEN_START")
            if self.player:
                if not getattr(self.player, "pause", True): self.player.pause = True
                current_ms = max(0, int((getattr(self.player, 'time-pos', 0) or 0) * 1000))
            music_player = getattr(self, "_music_preview_player", None)
            if music_player:
                try:
                    music_player.pause = True
                    music_player.mute = True
                except:
                    pass
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            self.is_playing = False
            if self.timer.isActive(): self.timer.stop()

            from ui.widgets.granular_speed_editor import GranularSpeedEditor
            dlg = GranularSpeedEditor(self.input_file_path, self, self.speed_segments, base_speed=self.speed_spinbox.value(), start_time_ms=current_ms, mpv_instance=self.player, volume=self._vol_eff())
            MPVSafetyManager.log_mpv_diagnostics(self.player, self.logger, "SPEED_EDITOR_EXEC_BEFORE")
            res = dlg.exec_()
            MPVSafetyManager.log_mpv_diagnostics(self.player, self.logger, "SPEED_EDITOR_EXEC_AFTER")
            self._opening_granular_dialog = False
            self._ignore_mpv_end_until = time.time() + 1.0
            QThread.msleep(150)
            QCoreApplication.processEvents()
            self._bind_main_player_output()
            self.timer.start()
            if res == QDialog.Accepted:
                self.speed_segments = sorted(dlg.speed_segments, key=lambda x: x['start'])
                if hasattr(self, "positionSlider"):
                    self.positionSlider.speed_segments = self.speed_segments
                    self.positionSlider.update()
                self.granular_checkbox.blockSignals(True)
                self.granular_checkbox.setChecked(bool(self.speed_segments))
                self.granular_checkbox.blockSignals(False)
                if self.speed_segments: self.status_bar.showMessage(f"Granular Speed Active: {len(self.speed_segments)} segments", 5000)
            else:
                self.granular_checkbox.blockSignals(True)
                self.granular_checkbox.setChecked(bool(self.speed_segments))
                self.granular_checkbox.blockSignals(False)
            rt = max(0, dlg.last_position_ms)
            if getattr(self, "player", None): self.player.seek(rt / 1000.0, reference='absolute', precision='exact')
            self.positionSlider.setValue(int(rt))
            self.positionSlider.update()
            MPVSafetyManager.log_mpv_diagnostics(self.player, self.logger, "SPEED_EDITOR_OPEN_END")
        except Exception as e:
            self.logger.critical(f"CRITICAL: Speed Dialog error: {e}")
            QMessageBox.critical(self, "Error", f"An error occurred: {e}")
            self.granular_checkbox.setChecked(False)
            self._opening_granular_dialog = False

    def _clear_speed_segments(self):
        if not self.speed_segments: return
        if QMessageBox.question(self, "Clear Speed Segments", "Are you sure you want to clear all speed segments?", QMessageBox.Yes | QMessageBox.No) == QMessageBox.Yes:
            self.speed_segments = []
            if hasattr(self, "positionSlider"):
                self.positionSlider.speed_segments = []
                self.positionSlider.update()
            self.granular_checkbox.blockSignals(True)
            self.granular_checkbox.setChecked(False)
            self.granular_checkbox.blockSignals(False)
            self.status_bar.showMessage("Speed segments cleared.", 3000)

    def show_priority_message(self, message: str, duration_ms: int = 5000, is_critical: bool = False):
        try:
            if getattr(self, "_in_transition", False): return
            now = time.time()
            if not is_critical and now < float(getattr(self, "_block_status_until", 0.0)): return
            self.status_bar.showMessage(message, duration_ms)
            if is_critical:
                self._block_status_until = now + (duration_ms / 1000.0)
                QCoreApplication.processEvents()
        except Exception: pass

    def on_hardware_scan_finished(self, mode: str):
        if not hasattr(self, 'status_bar'): return
        self.hardware_strategy = mode
        self.scan_complete = True
        try:
            cfg = self.config_manager.config
            cfg["last_hardware_strategy"] = mode
            self.config_manager.save_config(cfg)
        except Exception: pass
        if hasattr(self, 'hardware_status_label'):
            icon = "🚀" if mode in ["NVIDIA", "AMD", "INTEL"] else "⚠️"
            self.hardware_status_label.setText(f"{icon} {mode} Mode")
            self.hardware_status_label.setStyleSheet("color: #43b581; font-weight: bold;" if mode != "CPU" else "color: #ffa500; font-weight: bold;")
            self.hardware_status_label.show()
        if mode == "CPU": self.show_status_warning("⚠️ No compatible GPU detected. CPU-only mode.")
        else: self.show_priority_message(f"✅ Hardware Acceleration Enabled ({mode})", 5000, is_critical=True)
        self._maybe_enable_process()

    def show_status_warning(self, message: str):
        try:
            if not hasattr(self, 'status_bar_warning_label'):
                self.status_bar_warning_label = QLabel(message)
                self.status_bar_warning_label.setStyleSheet("color: #f39c12; font-weight: bold; padding-left: 10px;")
                self.status_bar.addPermanentWidget(self.status_bar_warning_label)
            self.status_bar_warning_label.setText(message)
            self.status_bar_warning_label.show()
            QTimer.singleShot(10000, self.status_bar_warning_label.hide)
        except Exception: pass
