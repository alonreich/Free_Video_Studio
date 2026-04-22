import os, sys, time
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowCoreAMixin:
    def open_granular_speed_dialog(self):
        try:
            if not self.input_file_path:
                 self.granular_checkbox.blockSignals(True); self.granular_checkbox.setChecked(False); self.granular_checkbox.blockSignals(False)
                 QMessageBox.warning(self, "No Video", "Please load a video first."); return
            if not self.granular_checkbox.isChecked():
                self.statusBar().showMessage("Granular Speed disabled (segments preserved).", 3000); return
            self.logger.info("UI: Opening Granular Speed Editor..."); self._opening_granular_dialog = True; self._ignore_mpv_end_until = time.time() + 2.0; current_ms = 0
            if self.player:
                if not self._safe_mpv_get("pause", True): self._safe_mpv_set("pause", True)
                current_ms = max(0, int((getattr(self.player, 'time-pos', 0) or 0) * 1000))
            m_p = getattr(self, "_music_preview_player", None)
            if m_p:
                try: self._safe_mpv_set("pause", True, target_player=m_p); m_p.mute = True
                except: pass
            if hasattr(self, "playPauseButton"):
                self.playPauseButton.setText("PLAY"); self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            self.is_playing = False
            if hasattr(self, "timer") and self.timer.isActive(): self.timer.stop()

            from ui.widgets.granular_speed_editor import GranularSpeedEditor
            dlg = GranularSpeedEditor(self.input_file_path, self, self.speed_segments, base_speed=self.speed_spinbox.value(), start_time_ms=current_ms, mpv_instance=self.player, volume=self._vol_eff())
            res = dlg.exec_(); self._opening_granular_dialog = False; self._ignore_mpv_end_until = time.time() + 1.0; QThread.msleep(150); QCoreApplication.processEvents()
            if res == QDialog.Accepted:
                self.logger.info(f"UI: Granular Speed segments updated ({len(dlg.speed_segments)} segments). Recalculating quality..."); self.speed_segments = sorted(dlg.speed_segments, key=lambda x: x['start'])
                self.granular_checkbox.blockSignals(True); self.granular_checkbox.setChecked(True); self.granular_checkbox.blockSignals(False)
                if hasattr(self, "_update_quality_label"): self._update_quality_label()
            else:
                self.logger.info("UI: Granular Speed Editor cancelled.")
                if not self.speed_segments:
                    self.granular_checkbox.blockSignals(True); self.granular_checkbox.setChecked(False); self.granular_checkbox.blockSignals(False)
            if hasattr(self, "positionSlider"):
                self.positionSlider.set_speed_segments(self.speed_segments); self.positionSlider.update()
            if m_p:
                try: m_p.mute = False
                except: pass
            if hasattr(self, "update_player_state"): self.update_player_state()
            self.activateWindow(); self.setFocus()
        except Exception as e:
            self.logger.error(f"UI: Error in granular dialog: {e}"); self._opening_granular_dialog = False

    def _clear_speed_segments(self):
        if not self.speed_segments: return
        if QMessageBox.question(self, "Clear Speed Segments", "Are you sure you want to clear all speed segments?", QMessageBox.Yes | QMessageBox.No) == QMessageBox.Yes:
            self.logger.info("UI: Clearing all speed segments. Recalculating quality..."); self.speed_segments = []
            if hasattr(self, "positionSlider"):
                self.positionSlider.set_speed_segments([]); self.positionSlider.update()
            self.granular_checkbox.blockSignals(True); self.granular_checkbox.setChecked(False); self.granular_checkbox.blockSignals(False)
            if hasattr(self, "_update_quality_label"): self._update_quality_label()
            self.statusBar().showMessage("Speed segments cleared.", 3000)

    def show_priority_message(self, message: str, duration_ms: int = 5000, is_critical: bool = False):
        try:
            if getattr(self, "_in_transition", False): return
            now = time.time()
            if not is_critical and now < float(getattr(self, "_block_status_until", 0.0)): return
            self.statusBar().showMessage(message, duration_ms)
            if is_critical:
                self._block_status_until = now + (duration_ms / 1000.0); QCoreApplication.processEvents()
        except: pass

    def on_hardware_scan_finished(self, mode: str):
        if not hasattr(self, 'status_bar'): return
        self.hardware_strategy = mode; self.scan_complete = True; self.logger.info(f"GPU: Hardware scan finished. Strategy: {mode}")
        try:
            cfg = self.config_manager.config; cfg["last_hardware_strategy"] = mode; self.config_manager.save_config(cfg)
        except: pass
        if hasattr(self, 'hardware_status_label'):
            icon = "🚀" if mode in ["NVIDIA", "AMD", "INTEL"] else "⚠️"
            self.hardware_status_label.setText(f"{icon} {mode} Mode"); self.hardware_status_label.setStyleSheet("color: #43b581; font-weight: bold;" if mode != "CPU" else "color: #ffa500; font-weight: bold;"); self.hardware_status_label.show()
        if mode == "CPU": self.show_status_warning("⚠️ No compatible GPU detected. CPU-only mode.")
        else: self.show_priority_message(f"✅ Hardware Acceleration Enabled ({mode})", 5000, is_critical=True)
        if hasattr(self, "_maybe_enable_process"): self._maybe_enable_process()

    def show_status_warning(self, message: str):
        try:
            if not hasattr(self, 'status_bar_warning_label'):
                self.status_bar_warning_label = QLabel(message); self.status_bar_warning_label.setStyleSheet("color: #f39c12; font-weight: bold; padding-left: 10px;"); self.status_bar.addPermanentWidget(self.status_bar_warning_label)
            self.status_bar_warning_label.setText(message); self.status_bar_warning_label.show(); QTimer.singleShot(10000, self.status_bar_warning_label.hide)
        except: pass
