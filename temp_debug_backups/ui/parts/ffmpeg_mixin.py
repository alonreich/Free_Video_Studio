import os
import sys
import time
import subprocess
import json
import threading
from PyQt5.QtCore import Qt, QTimer, QCoreApplication, QObject, pyqtSignal, QPropertyAnimation, QUrl
from PyQt5.QtGui import QIcon, QFont, QDesktopServices, QPixmap, QPainter
from PyQt5.QtWidgets import (QStyle, QApplication, QDialog, QVBoxLayout, QLabel,
                             QGridLayout, QPushButton, QMessageBox, QSizePolicy)

from processing.worker import ProcessThread
from ui.styles import UIStyles
from system.utils import UIManager, MediaProber

class FfmpegMixin:
    def _quit_application(self, dialog_to_close):
        if dialog_to_close: dialog_to_close.accept()
        if hasattr(self, "cleanup_and_exit"): self.cleanup_and_exit()
        else: QCoreApplication.instance().quit()

    def _safe_status(self, text: str, color: str = "white"):
        try: self.set_status_text_with_color(text, color)
        except: pass

    def _safe_set_phase(self, name: str, ok: bool | None = None):
        try: self.on_phase_update(name)
        except: pass

    def _safe_set_duration_text(self, text: str):
        try: self.duration_label.setText(text)
        except: pass

    def show_message(self, title: str, message: str):
        try:
            if str(title).strip().lower() in {"error", "critical"}: QMessageBox.critical(self, title, message)
            elif str(title).strip().lower() in {"warning", "warn"}: QMessageBox.warning(self, title, message)
            else: QMessageBox.information(self, title, message)
        except: pass

    def cancel_processing(self):
        if not self.is_processing: return
        if hasattr(self, "cancel_button"):
            self.cancel_button.setEnabled(False)
            self.cancel_button.setText("Stopping...")
        QApplication.processEvents()
        if hasattr(self, "process_thread") and self.process_thread and self.process_thread.isRunning():
            self.process_thread.cancel()
            self.process_thread.wait(5000)
        self.on_process_finished(False, "Processing was canceled by the user.")
        self._save_app_state_and_config()

    def start_processing(self):
        try:
            if self.is_processing:
                self.show_message("Info", "A video is already being processed. Please wait.")
                return
            if not self.input_file_path or not os.path.exists(self.input_file_path):
                self.show_message("Error", "Please select a valid video file first.")
                return

            from processing.system_utils import check_disk_space
            out_dir = os.path.join(os.path.expanduser("~"), "Downloads")
            os.makedirs(out_dir, exist_ok=True)
            if not check_disk_space(out_dir, 2.0):
                self.show_message("Disk Space Low", "You have less than 2GB free on the output drive. Please free up space before processing.")
                return
            if self.trim_start_ms > 0 or self.trim_end_ms > 0:
                start_time_ms = self.trim_start_ms; end_time_ms = self.trim_end_ms
            else:
                start_time_ms = (self.start_minute_input.value() * 60 * 1000) + (self.start_second_input.value() * 1000) + self.start_ms_input.value()
                end_time_ms = (self.end_minute_input.value() * 60 * 1000) + (self.end_second_input.value() * 1000) + self.end_ms_input.value()
            total_duration_ms = self.original_duration_ms
            if total_duration_ms <= 0: total_duration_ms = 99999 * 1000
            start_time_ms = max(0, min(start_time_ms, total_duration_ms))
            end_time_ms = max(0, min(end_time_ms, total_duration_ms))
            MIN_DURATION_MS = 500
            if end_time_ms < start_time_ms + MIN_DURATION_MS:
                end_time_ms = min(total_duration_ms, start_time_ms + MIN_DURATION_MS)
                if end_time_ms < start_time_ms + MIN_DURATION_MS: start_time_ms = max(0, end_time_ms - MIN_DURATION_MS)
            self.trim_start_ms = start_time_ms; self.trim_end_ms = end_time_ms
            is_mobile_format = self.mobile_checkbox.isChecked()
            speed_factor = float(self.speed_spinbox.value())
            if speed_factor < 0.5 or speed_factor > 3.1:
                self.show_message("Invalid Speed", "Allowed speed range is 0.5x to 3.1x."); self.is_processing = False
                self.process_button.setEnabled(True); return
            granular_enabled = bool(getattr(self, "granular_checkbox", None) and self.granular_checkbox.isChecked())
            raw_segments = list(getattr(self, "speed_segments", []) or []) if granular_enabled else []
            segments = []; speed_segments_for_worker = []
            for seg in raw_segments:
                try:
                    s_ms = int(seg.get("start_ms", seg.get("start", 0))); e_ms = int(seg.get("end_ms", seg.get("end", 0))); spd = float(seg.get("speed", speed_factor))
                    if e_ms <= s_ms: continue
                    segments.append({"start": s_ms, "end": e_ms, "speed": spd}); speed_segments_for_worker.append({"start_ms": s_ms, "end_ms": e_ms, "speed": spd})
                except: continue
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
            music_path = None; music_offset_s = 0.0; linear_video_vol = self._get_master_eff() / 100.0
            if hasattr(self, "_wizard_tracks") and self._wizard_tracks:
                music_path = self._wizard_tracks[0][0]; music_offset_s = self._wizard_tracks[0][1]
            music_vol_linear = self._music_eff() / 100.0 if music_path else 0.0
            q_level = int(self.quality_slider.value())
            if hasattr(self, 'portrait_mask_overlay'): self.portrait_mask_overlay.hide()
            if hasattr(self, 'set_overlays_force_hidden'): self.set_overlays_force_hidden(True)
            self.is_processing = True; self._proc_start_ts = time.time(); self._pulse_phase = 0
            self.process_button.setEnabled(False); self.cancel_button.setVisible(True); self.cancel_button.setEnabled(True)
            self.progress_bar.setRange(0, 0); self.progress_bar.setValue(0); self._pulse_timer.start(250)
            self.process_button.setText("PROCESSING"); self.process_button.setIcon(self.style().standardIcon(QStyle.SP_BrowserReload))
            self._safe_set_phase("Processing"); self._show_processing_overlay(); self._safe_status("Preparing... (probing/seek)...", "white")
            self.progress_update_signal.emit(0)
            cfg = dict(self.config_manager.config); cfg['last_speed'] = float(speed_factor); cfg['mobile_checked'] = bool(is_mobile_format); cfg['teammates_checked'] = bool(self.teammates_checkbox.isChecked())
            self.config_manager.save_config(cfg)
            m_start_ms = int(getattr(self, 'music_timeline_start_ms', self.trim_start_ms)); m_end_ms = int(getattr(self, 'music_timeline_end_ms', self.trim_end_ms))
            v_wall_start = self._calculate_wall_clock_time(start_time_ms, segments, speed_factor)
            m_wall_start = self._calculate_wall_clock_time(m_start_ms, segments, speed_factor)
            m_wall_end = self._calculate_wall_clock_time(m_end_ms, segments, speed_factor)
            m_proj_start_sec = max(0.0, (m_wall_start - v_wall_start) / 1000.0)
            m_proj_end_sec = max(0.0, (m_wall_end - v_wall_start) / 1000.0)
            music_conf = {'path': music_path, 'ducking_threshold': 0.15, 'ducking_ratio': 2.5, 'eq_enabled': True, 'main_vol': linear_video_vol, 'music_vol': music_vol_linear if music_path else 1.0, 'timeline_start_sec': m_proj_start_sec, 'timeline_end_sec': m_proj_end_sec, 'file_offset_sec': music_offset_s}
            p_text = None
            if is_mobile_format and hasattr(self, 'portrait_text_input'):
                raw_text = self.portrait_text_input.text().strip()
                if raw_text: p_text = raw_text
            intro_abs_time = getattr(self, 'selected_intro_abs_time', 0.0)
            if not intro_abs_time or intro_abs_time <= 0:
                segment_duration = (end_time_ms - start_time_ms) / 1000.0
                intro_abs_time = (start_time_ms / 1000.0) + (segment_duration * 0.66)
            intro_abs_time_ms = int(intro_abs_time * 1000)
            self.process_thread = ProcessThread(input_path=self.input_file_path, start_time_ms=start_time_ms, end_time_ms=end_time_ms, original_resolution=self.original_resolution, is_mobile_format=is_mobile_format, speed_factor=speed_factor, base_dir=self.base_dir, progress_signal=self.progress_update_signal, status_signal=self.status_update_signal, finished_signal=self.process_finished_signal, logger=self.logger, is_boss_hp=self.boss_hp_checkbox.isChecked(), show_teammates_overlay=(is_mobile_format and self.teammates_checkbox.isChecked()), quality_level=q_level, bg_music_path=music_path, bg_music_volume=music_vol_linear, bg_music_offset_ms=int(music_offset_s * 1000), original_total_duration_ms=self.original_duration_ms, disable_fades=self.no_fade_checkbox.isChecked(), intro_still_sec=1, intro_from_midpoint=(intro_abs_time_ms <= 0), intro_abs_time_ms=intro_abs_time_ms if intro_abs_time_ms > 0 else None, portrait_text=p_text, music_config=music_conf, speed_segments=speed_segments_for_worker, hardware_strategy=getattr(self, 'hardware_strategy', 'CPU'), music_tracks=getattr(self, "_wizard_tracks", []))
            self.process_thread.start()
        except:
            self.is_processing = False; self.process_button.setEnabled(True)

    def _show_error_with_log(self, message):
        msg = QMessageBox(self); msg.setIcon(QMessageBox.Critical); msg.setWindowTitle("Processing Error"); msg.setText("An error occurred during processing."); msg.setInformativeText(message)
        UIManager.style_and_size_msg_box(msg, message)
        details_btn = msg.addButton("Show Technical Logs", QMessageBox.ActionRole); ok_btn = msg.addButton(QMessageBox.Ok)
        for btn in msg.buttons(): btn.setCursor(Qt.PointingHandCursor)
        msg.exec_()
        if msg.clickedButton() == details_btn:
            log_path = os.path.join(self.base_dir, "logs", "main_app.log"); log_content = "Log file not found."
            if os.path.exists(log_path):
                try:
                    with open(log_path, "r", encoding="utf-8", errors="ignore") as f:
                        lines = f.readlines(); log_content = "".join(lines[-100:])
                except: pass
            d = QDialog(self); d.setWindowTitle("Technical Logs (Last 100 Lines)"); d.resize(900, 600); l = QVBoxLayout(d)

            from PyQt5.QtWidgets import QTextEdit
            t = QTextEdit(); t.setFont(QFont("Consolas", 10)); t.setReadOnly(True); t.setPlainText(log_content); l.addWidget(t); d.exec_()

    def share_via_whatsapp(self):
        try: QDesktopServices.openUrl(QUrl("https://web.whatsapp.com"))
        except: pass

    def open_output_in_explorer(self, file_path: str):
        full_path = os.path.abspath(file_path)
        if not os.path.exists(full_path): return
        try:
            if os.name == "nt": subprocess.run(['explorer', '/select,', os.path.normpath(full_path)], check=False)
            elif sys.platform == "darwin": subprocess.Popen(["open", "-R", full_path])
            else: subprocess.Popen(["xdg-open", os.path.dirname(full_path)])
        except: pass

    def _dialog_button_style(self, color: str, pressed: str, *, font_size: int = 12) -> str:
        return f"QPushButton {{ background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {color}, stop:1 {pressed}); color: white; font-weight: bold; font-family: Arial; font-size: {font_size}px; border-radius: 8px; border: 1px solid rgba(0,0,0,0.45); padding: 0px; text-align: center; min-width: 180px; max-width: 180px; min-height: 45px; max-height: 45px; }} QPushButton:hover {{ border: 1px solid #7DD3FC; }} QPushButton:pressed {{ background-color: {pressed}; }}"

    def on_process_finished(self, success, message):
        self.is_processing = False; self._proc_start_ts = None; self._phase_is_processing = False
        if hasattr(self, "_pulse_timer"): self._pulse_timer.stop()
        self._hide_processing_overlay(); self.cancel_button.setVisible(False)
        self.process_button.setText("PROCESS"); self.process_button.setIcon(self.style().standardIcon(QStyle.SP_DialogApplyButton))
        self.progress_bar.setRange(0, 100); self.progress_bar.setValue(0); self.selected_intro_abs_time = None
        if hasattr(self, "_maybe_enable_process"): self._maybe_enable_process()
        else: self.process_button.setEnabled(True)
        if success: self._safe_set_phase("Done", ok=True)
        else:
            if "canceled by user" in message.lower(): self._safe_set_phase("Canceled", ok=False)
            else: self._safe_set_phase("Error", ok=False)
        self.status_update_signal.emit("Ready to process another video.")
        if success:
            if hasattr(self, "set_overlays_force_hidden"):
                self.set_overlays_force_hidden(True)
            output_path = message; self._block_portrait_overlay = True

            class FinishedDialog(QDialog):
                def closeEvent(self, e): self.accept()
            dialog = FinishedDialog(self); dialog.setWindowTitle("Done! Video Processed Successfully!"); dialog.setModal(True); dialog.setFixedSize(800, 460)
            layout = QVBoxLayout(dialog); layout.setContentsMargins(30, 30, 30, 30); layout.setSpacing(20)
            label = QLabel(f"File successfully saved to:\n{output_path}"); label.setStyleSheet("font-size: 16px; font-weight: bold; color: #7DD3FC;")
            label.setAlignment(Qt.AlignCenter); layout.addWidget(label)
            grid = QGridLayout(); grid.setHorizontalSpacing(54); grid.setVerticalSpacing(44); grid.setContentsMargins(20, 20, 20, 20)
            whatsapp_button = QPushButton("✆  WHATSAPP SHARE  ✆"); whatsapp_button.setStyleSheet(self._dialog_button_style("#3CA557", "#2B7D40"))
            whatsapp_button.clicked.connect(self.share_via_whatsapp)
            open_folder_button = QPushButton("OPEN FOLDER"); open_folder_button.setStyleSheet(self._dialog_button_style("#2e82a0", "#1e648c"))
            open_folder_button.clicked.connect(lambda: self.open_output_in_explorer(output_path))
            new_file_button = QPushButton("📂  UPLOAD NEW  📂"); new_file_button.setStyleSheet(self._dialog_button_style("#2e82a0", "#1e648c"))
            new_file_button.clicked.connect(lambda: dialog.done(QDialog.Rejected))
            done_button = QPushButton("DONE"); done_button.setStyleSheet(self._dialog_button_style("#1a7a1a", "#0a300a"))
            done_button.clicked.connect(dialog.accept)
            exit_btn = QPushButton("EXIT APP!"); exit_btn.setStyleSheet(self._dialog_button_style("#b00000", "#300000"))
            exit_btn.clicked.connect(lambda: self._quit_application(dialog))
            for b in [whatsapp_button, open_folder_button, new_file_button, done_button, exit_btn]: b.setFixedSize(180, 45); b.setCursor(Qt.PointingHandCursor)
            grid.addWidget(whatsapp_button, 0, 0, alignment=Qt.AlignCenter); grid.addWidget(open_folder_button, 0, 1, alignment=Qt.AlignCenter); grid.addWidget(new_file_button, 0, 2, alignment=Qt.AlignCenter); grid.addWidget(done_button, 1, 0, 1, 3, alignment=Qt.AlignCenter); grid.addWidget(exit_btn, 2, 0, 1, 3, alignment=Qt.AlignCenter); layout.addLayout(grid)
            result = dialog.exec_(); self._block_portrait_overlay = False
            if hasattr(self, "set_overlays_force_hidden"):
                self.set_overlays_force_hidden(False)
            if result == QDialog.Rejected: self.handle_new_file()
        else:
            if "canceled by user" not in message.lower(): self._show_error_with_log(message)

    def get_video_info(self):
        if not self.input_file_path or not os.path.exists(self.input_file_path): return
        self._safe_status("Analyzing video...", "orange")

        def _bg_worker(p):
            try:
                d, r = MediaProber.probe_metadata(self.bin_dir, p)
                return True, d, r
            except Exception as e: return False, 0.0, str(e)

        def _on_worker_finished(result):
            success, duration_s, res_or_err = result
            if not success: return
            duration_ms = int(duration_s * 1000)
            try:
                self.original_duration_ms = duration_ms; self.original_resolution = res_or_err
                if hasattr(self, 'resolution_label'): self.resolution_label.setText(self.original_resolution)
                self.positionSlider.setRange(0, duration_ms); self.positionSlider.set_duration_ms(duration_ms)
                self._update_trim_inputs(); self._safe_set_duration_text(f"Duration: {duration_s:.2f} s | Res: {self.original_resolution}")
                self.trim_start_ms = 0; self.trim_end_ms = duration_ms; self._update_trim_widgets_from_trim_times(); self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms); self._safe_status("Video loaded.", "white")
            except: pass
            finally:
                if hasattr(self, "timer") and not self.timer.isActive(): self.timer.start(100)

        class _ProbeBridge(QObject):
            done = pyqtSignal(object)
        self._probe_bridge = _ProbeBridge(); self._probe_bridge.done.connect(_on_worker_finished)

        def _thread_target():
            result = _bg_worker(str(self.input_file_path)); self._probe_bridge.done.emit(result)
        threading.Thread(target=_thread_target, daemon=True).start()

    def on_progress(self, value: int):
        if self.progress_bar.maximum() == 0: self.progress_bar.setRange(0, 100)
        self.progress_bar.setValue(int(max(0, min(100, value))))

    def _calculate_wall_clock_time(self, video_ms, segments, base_speed):
        accumulated_wall_time = 0.0; current_v = 0.0
        for seg in segments:
            if video_ms <= seg['start']: break
            if seg['start'] > current_v:
                if base_speed < 0.001: accumulated_wall_time += (seg['start'] - current_v)
                else: accumulated_wall_time += (seg['start'] - current_v) / base_speed
            partial_dur = min(video_ms, seg['end']) - seg['start']
            if seg['speed'] < 0.001: accumulated_wall_time += partial_dur
            else: accumulated_wall_time += partial_dur / seg['speed']
            current_v = seg['end']
        if video_ms > current_v:
            if base_speed < 0.001: accumulated_wall_time += (video_ms - current_v)
            else: accumulated_wall_time += (video_ms - current_v) / base_speed
        return accumulated_wall_time
