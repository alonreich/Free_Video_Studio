import os
import sys
import time
import subprocess
import json
import threading
from PyQt5.QtCore import Qt, QTimer, QCoreApplication, QObject, pyqtSignal, QPropertyAnimation, QAbstractAnimation
from PyQt5.QtGui import QIcon, QFont
from PyQt5.QtWidgets import (QStyle, QApplication, QDialog, QVBoxLayout, QLabel,
                             QGridLayout, QPushButton, QMessageBox)

from processing.worker import ProcessThread
from ui.styles import UIStyles

class FfmpegMixin:
    def _quit_application(self, dialog_to_close):
        """Accepts the dialog first, then quits the application, ensuring state is saved."""
        if dialog_to_close:
            dialog_to_close.accept()
        if hasattr(self, "cleanup_and_exit"):
            self.cleanup_and_exit()
        else:
            QCoreApplication.instance().quit()
    
    def _safe_status(self, text: str, color: str = "white"):
        """Use set_status_text_with_color if available; otherwise just log."""
        try:
            self.set_status_text_with_color(text, color)
        except Exception:
            try:
                if hasattr(self, "logger"):
                    self.logger.info("STATUS(%s): %s", color, text)
            except Exception:
                pass
    
    def _safe_set_phase(self, name: str, ok: bool | None = None):
        """Try to update the phase overlay/label; never crash if label is missing."""
        try:
            self.on_phase_update(name)
            return
        except Exception:
            pass
        try:
            if hasattr(self, "logger"):
                self.logger.info("PHASE: %s", name)
        except Exception:
            pass
    
    def _safe_set_duration_text(self, text: str):
        """Set duration/resolution text if a label exists; otherwise log."""
        try:
            self.duration_label.setText(text)
            return
        except Exception:
            pass
        try:
            if hasattr(self, "logger"):
                self.logger.info("DURATION: %s", text)
        except Exception:
            pass
    
    def cancel_processing(self):
        if not self.is_processing:
            return
        self.logger.info("CANCEL: User clicked cancel button. Initiating kill sequence...")
        if hasattr(self, "cancel_button"):
            self.cancel_button.setEnabled(False)
            self.cancel_button.setText("Stopping...")
        QApplication.processEvents()
        if hasattr(self, "process_thread") and self.process_thread and self.process_thread.isRunning():
            self.process_thread.cancel()
            if not self.process_thread.wait(5000):
                self.logger.error("CANCEL: Process thread failed to stop within 5s.")
        self.on_process_finished(False, "Processing was canceled by the user.")
        self._save_app_state_and_config()
    
    def start_processing(self):
        """
        Starts the video processing sequence in a separate thread;
        never let exceptions kill the app.
        """
        try:
            if self.is_processing:
                self.show_message("Info", "A video is already being processed. Please wait.")
                return
            if not self.input_file_path or not os.path.exists(self.input_file_path):
                self.show_message("Error", "Please select a valid video file first.")
                return

            from processing.system_utils import check_disk_space
            out_dir = os.path.join(self.base_dir, "!!!_Output_Video_Files_!!!")
            if not check_disk_space(out_dir, 2.0):
                self.show_message("Disk Space Low", "You have less than 2GB free on the output drive. Please free up space before processing.")
                return
            if self.trim_start_ms > 0 or self.trim_end_ms > 0:
                start_time_ms = self.trim_start_ms
                end_time_ms = self.trim_end_ms
            else:
                start_time_ms = (self.start_minute_input.value() * 60 * 1000) + (self.start_second_input.value() * 1000) + self.start_ms_input.value()
                end_time_ms = (self.end_minute_input.value() * 60 * 1000) + (self.end_second_input.value() * 1000) + self.end_ms_input.value()
            total_duration_ms = self.original_duration_ms
            if total_duration_ms <= 0:
                total_duration_ms = 99999 * 1000
            start_time_ms = max(0, min(start_time_ms, total_duration_ms))
            end_time_ms = max(0, min(end_time_ms, total_duration_ms))
            MIN_DURATION_MS = 500
            if end_time_ms < start_time_ms + MIN_DURATION_MS:
                end_time_ms = min(total_duration_ms, start_time_ms + MIN_DURATION_MS)
                if end_time_ms < start_time_ms + MIN_DURATION_MS:
                    start_time_ms = max(0, end_time_ms - MIN_DURATION_MS)
            self.trim_start_ms = start_time_ms
            self.trim_end_ms = end_time_ms
            is_mobile_format = self.mobile_checkbox.isChecked()
            speed_factor = self.speed_spinbox.value()
            if speed_factor < 0.5 or speed_factor > 3.1:
                self.show_message("Invalid Speed", "Allowed speed range is 0.5x to 3.1x.")
                self.is_processing = False
                self.process_button.setEnabled(True)
                return
            self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
            music_path = None
            music_offset_s = 0.0
            linear_video_vol = self._get_master_eff() / 100.0
            if hasattr(self, "_wizard_tracks") and self._wizard_tracks:
                music_path = self._wizard_tracks[0][0]
                music_offset_s = self._wizard_tracks[0][1]
            music_vol_linear = self._music_eff() / 100.0 if music_path else 0.0
            q_level = int(self.quality_slider.value())
            self.logger.info(
                "PROCESS: clicked at %s | start=%dms end=%dms speed=%sx | mobile=%s teammates=%s boss_hp=%s | quality_level=%d | disable_fades=%s | music=%s vol=%.2f video_vol=%.2f",
                time.strftime("%Y-%m-%d %H:%M:%S"),
                start_time_ms, end_time_ms, speed_factor,
                is_mobile_format, self.teammates_checkbox.isChecked(), self.boss_hp_checkbox.isChecked(),
                q_level,
                self.no_fade_checkbox.isChecked(),
                (music_path or "None"), music_vol_linear, linear_video_vol
            )
            if hasattr(self, 'portrait_mask_overlay'):
                self.portrait_mask_overlay.hide()
            self.is_processing = True
            self._proc_start_ts = time.time()
            self._pulse_phase = 0
            self.process_button.setEnabled(False)
            self.cancel_button.setVisible(True)
            self.cancel_button.setEnabled(True)
            self.progress_bar.setRange(0, 0)
            self.progress_bar.setValue(0)
            self._pulse_timer.start()
            self.process_button.setText("Processing...")
            self.process_button.setIcon(self.style().standardIcon(QStyle.SP_BrowserReload))
            self._safe_set_phase("Processing")
            self._show_processing_overlay()
            self._safe_status("Preparing... (probing/seek)...", "white")
            self.progress_update_signal.emit(0)
            cfg = dict(self.config_manager.config)
            cfg['last_speed'] = float(speed_factor)
            cfg['mobile_checked'] = bool(is_mobile_format)
            cfg['teammates_checked'] = bool(self.teammates_checkbox.isChecked())
            self.config_manager.save_config(cfg)
            music_conf = {
                'ducking_threshold': 0.15,
                'ducking_ratio': 2.5,
                'eq_enabled': True,
                'main_vol': linear_video_vol,
                'music_vol': music_vol_linear if music_path else 1.0,
                'timeline_start_ms': int(getattr(self, 'music_timeline_start_ms', self.trim_start_ms)),
                'timeline_end_ms': int(getattr(self, 'music_timeline_end_ms', self.trim_end_ms))
            }
            music_conf['timeline_start_ms'] = max(self.trim_start_ms, min(music_conf['timeline_start_ms'], self.trim_end_ms))
            music_conf['timeline_end_ms'] = max(music_conf['timeline_start_ms'], min(music_conf['timeline_end_ms'], self.trim_end_ms))
            p_text = None
            if is_mobile_format and hasattr(self, 'portrait_text_input'):
                raw_text = self.portrait_text_input.text().strip()
                if raw_text:
                    p_text = raw_text
            intro_abs_time = getattr(self, 'selected_intro_abs_time', 0.0)
            if intro_abs_time is None:
                intro_abs_time = 0.0
            intro_abs_time_ms = int(intro_abs_time * 1000)
            self.process_thread = ProcessThread(
                self.input_file_path, start_time_ms, end_time_ms, self.original_resolution,
                is_mobile_format, speed_factor, self.script_dir,
                self.progress_update_signal, self.status_update_signal, self.process_finished_signal,
                self.logger,
                is_boss_hp=self.boss_hp_checkbox.isChecked(),
                show_teammates_overlay=(is_mobile_format and self.teammates_checkbox.isChecked()),
                quality_level=q_level,
                bg_music_path=music_path, 
                bg_music_volume=music_vol_linear,
                bg_music_offset_ms=int(music_offset_s * 1000),
                original_total_duration_ms=self.original_duration_ms,
                disable_fades=self.no_fade_checkbox.isChecked(),
                intro_still_sec=0.1,
                intro_from_midpoint=(intro_abs_time_ms <= 0),
                intro_abs_time_ms=intro_abs_time_ms if intro_abs_time_ms > 0 else None,
                portrait_text=p_text,
                music_config=music_conf,
                speed_segments=getattr(self, 'speed_segments', None),
                hardware_strategy=getattr(self, 'hardware_strategy', 'CPU')
            )
            self.process_thread.started.connect(lambda: self.logger.info("ProcessThread: started"))
            self.process_thread.finished.connect(lambda: self.logger.info("ProcessThread: finished"))
            self.process_thread.start()
        except Exception as e:
            self.logger.exception("start_processing crashed: %s", e)
            try:
                self._pulse_timer.stop()
                self._hide_processing_overlay()
                QApplication.restoreOverrideCursor()
                self.is_processing = False
                self.process_button.setEnabled(True)
                self.cancel_button.setVisible(False)
                self.progress_bar.setRange(0, 100)
                self.progress_bar.setValue(0)
                self.on_phase_update("Error")
                self._safe_status(f"Processing failed to start: {e}", "red")
            except Exception:
                pass
    
    def _show_error_with_log(self, message):
        """[FIX #22] Error dialog with log viewer."""
        msg = QMessageBox(self)
        msg.setIcon(QMessageBox.Critical)
        msg.setWindowTitle("Processing Error")
        msg.setText("An error occurred during processing.")
        msg.setInformativeText(message)
        details_btn = msg.addButton("Show Technical Logs", QMessageBox.ActionRole)
        ok_btn = msg.addButton(QMessageBox.Ok)
        msg.exec_()
        if msg.clickedButton() == details_btn:
            log_path = os.path.join(self.base_dir, "logs", "main_app.log")
            log_content = "Log file not found."
            if os.path.exists(log_path):
                try:
                    with open(log_path, "r", encoding="utf-8", errors="ignore") as f:
                        lines = f.readlines()
                        log_content = "".join(lines[-100:])
                except Exception as e:
                    log_content = f"Failed to read log: {e}"
            d = QDialog(self)
            d.setWindowTitle("Technical Logs (Last 100 Lines)")
            d.resize(900, 600)
            l = QVBoxLayout(d)

            from PyQt5.QtWidgets import QTextEdit
            t = QTextEdit()
            t.setFont(QFont("Consolas", 10))
            t.setReadOnly(True)
            t.setPlainText(log_content)
            l.addWidget(t)
            t.verticalScrollBar().setValue(t.verticalScrollBar().maximum())
            d.exec_()

    def share_via_whatsapp(self):
        url = "https://web.whatsapp.com"
        try:
            if sys.platform == 'win32':
                os.startfile(url)
            elif sys.platform == 'darwin':
                subprocess.Popen(['open', url])
            else:
                subprocess.Popen(['xdg-open', url])
        except Exception as e:
            self.show_message("Error", f"Failed to open WhatsApp. Please visit {url} manually. Error: {e}")

    def on_process_finished(self, success, message):
        button_size = (250, 55)
        self.is_processing = False
        self._proc_start_ts = None
        self._phase_is_processing = False
        if hasattr(self, "_pulse_timer"):
            self._pulse_timer.stop()
        self._hide_processing_overlay()
        self.process_button.setEnabled(True)
        self.cancel_button.setVisible(False)
        self.process_button.setText("Process Video")
        self.process_button.setIcon(self.style().standardIcon(QStyle.SP_DialogApplyButton))
        self.progress_bar.setRange(0, 100)
        self.progress_bar.setValue(0)
        self.selected_intro_abs_time = None
        try:
            self.thumb_pick_btn.setText("📸 SET THUMBNAIL 📸")
        except Exception:
            pass
        if success:
            self._safe_set_phase("Done", ok=True)
            if hasattr(self, "cleanup_worker"):
                try:
                    self.cleanup_worker.run()
                except: pass
        else:
            if "canceled by user" in message.lower():
                self._safe_set_phase("Canceled", ok=False)
                self._safe_status("Processing was canceled by the user.", "orange")
            else:
                self._safe_set_phase("Error", ok=False)
        self.status_update_signal.emit("Ready to process another video.")
        try:
            if success:
                orig_size = os.path.getsize(self.input_file_path) if self.input_file_path and os.path.exists(self.input_file_path) else 0
                time.sleep(0.1)
                new_size  = os.path.getsize(message) if message and os.path.exists(message) else 0
                orig_mb = orig_size / (1024.0 * 1024.0)
                new_mb  = new_size  / (1024.0 * 1024.0)
                self.logger.info(
                    f"RESULT: SUCCESS | file='{os.path.basename(self.input_file_path)}' | "
                    f"original_size={orig_mb:.1f} MB | new_size={new_mb:.1f} MB"
                )
            else:
                self.logger.error(
                    f"RESULT: FAILURE | file='{os.path.basename(self.input_file_path) if self.input_file_path else 'N/A'}' | details={message}"
                )
        except Exception:
            pass
        if success:
            output_dir = os.path.dirname(message)

            class FinishedDialog(QDialog):
                def closeEvent(self, e):
                    self.accept()
            dialog = FinishedDialog(self)
            dialog.setWindowTitle("Done! Video Processed Successfully!")
            dialog.setModal(True)
            dialog.resize(int(self.width() * 0.5), 100)
            layout = QVBoxLayout(dialog)
            label = QLabel(f"File saved to:\n{message}")
            layout.addWidget(label)
            grid = QGridLayout()
            grid.setSpacing(40)
            grid.setContentsMargins(30, 50, 30, 50)
            whatsapp_button = QPushButton("✆   SHARE VIA WHATSAPP   ✆")
            whatsapp_button.setFont(QFont("Segoe UI Emoji", 10))
            whatsapp_button.setFixedSize(*button_size)
            whatsapp_button.setStyleSheet(UIStyles.get_3d_style("#328742", font_size=10))
            whatsapp_button.setCursor(Qt.PointingHandCursor)
            whatsapp_button.clicked.connect(lambda: (self.share_via_whatsapp(), self._quit_application(dialog)))
            open_folder_button = QPushButton("OPEN OUTPUT FOLDER")
            open_folder_button.setFixedSize(*button_size)
            open_folder_button.setStyleSheet(UIStyles.get_3d_style("#6c5f9e"))
            open_folder_button.setCursor(Qt.PointingHandCursor)
            open_folder_button.clicked.connect(lambda: (
                dialog.accept(),
                self.open_folder(os.path.dirname(message)),
                self._save_app_state_and_config(),
                QCoreApplication.instance().quit()
            ))
            new_file_button = QPushButton("📂   UPLOAD A NEW FILE   📂")
            new_file_button.setFont(QFont("Segoe UI Emoji", 10))
            new_file_button.setFixedSize(*button_size)
            new_file_button.setStyleSheet(UIStyles.get_3d_style("#6c5f9e", font_size=10))
            new_file_button.setCursor(Qt.PointingHandCursor)
            new_file_button.clicked.connect(dialog.reject)
            grid.addWidget(whatsapp_button, 0, 0, alignment=Qt.AlignCenter)
            grid.addWidget(open_folder_button, 0, 1, alignment=Qt.AlignCenter)
            grid.addWidget(new_file_button, 0, 2, alignment=Qt.AlignCenter)
            done_button = QPushButton("DONE")
            done_button.setFixedSize(*button_size)
            done_button.setStyleSheet(UIStyles.get_3d_style("#821e1e", padding="8px 16px"))
            done_button.setCursor(Qt.PointingHandCursor)
            done_button.clicked.connect(dialog.accept)
            grid.addWidget(done_button, 1, 0, 1, 3, alignment=Qt.AlignCenter)
            finished_button = QPushButton("CLOSE THE APP!\r\n(EXIT)")
            finished_button.setFixedSize(*button_size)
            finished_button.setStyleSheet(UIStyles.get_3d_style("#c90e0e", padding="8px 16px"))
            finished_button.setCursor(Qt.PointingHandCursor)
            finished_button.clicked.connect(lambda: self._quit_application(dialog)) 
            grid.addWidget(finished_button, 2, 0, 1, 3, alignment=Qt.AlignCenter)
            layout.addLayout(grid)
            dialog.setLayout(layout)
            anim = QPropertyAnimation(dialog, b"windowOpacity")
            anim.setDuration(1200)
            anim.setStartValue(1.0)
            anim.setKeyValueAt(0.5, 0.75)
            anim.setEndValue(1.0)
            anim.setLoopCount(-1)
            anim.start()
            dialog.finished.connect(anim.stop)
            result = dialog.exec_()
            anim.stop()
            if hasattr(self, '_update_portrait_mask_overlay_state'):
                self._update_portrait_mask_overlay_state()
            if result == QDialog.Rejected:
                self.handle_new_file()
        else:
            if "canceled by user" not in message.lower():
                self._show_error_with_log(message)
        try:
            for h in getattr(self.logger, "handlers", []):
                stream = getattr(h, "stream", None)
                if stream:
                    stream.write("-----------------------------------------------------------------------------------------------\n")
                    stream.write("-----------------------------------------------------------------------------------------------")
                    stream.write("-----------------------------------------------------------------------------------------------")
                    stream.write("-----------------------------------------------------------------------------------------------")
                    stream.flush()
        except Exception:
            pass
    
    def get_video_info(self):
        if not self.input_file_path or not os.path.exists(self.input_file_path):
            self.show_message("Error", "No valid video file selected.")
            return
        self._safe_status("Analyzing video... (Background)", "orange")
        self.process_button.setEnabled(False)
        self.playPauseButton.setEnabled(False)
        self.start_trim_button.setEnabled(False)
        self.end_trim_button.setEnabled(False)
        path_to_probe = str(self.input_file_path)

        def _bg_worker(p):
            try:
                d, r = self._probe_video_metadata(p)
                return True, d, r
            except Exception as e:
                return False, 0.0, str(e)

        def _on_worker_finished(result):
            success, duration_s, res_or_err = result
            if not success:
                self._safe_status(f"Error analyzing: {res_or_err}", "red")
                if hasattr(self, "logger"):
                    self.logger.error(f"Probe failed: {res_or_err}")
                return
            duration_ms = int(duration_s * 1000)
            if duration_ms <= 0 or not res_or_err:
                self._safe_status("Video analysis failed (invalid metadata).", "red")
                return
            try:
                self.original_duration_ms = duration_ms
                self.original_resolution = res_or_err
                if hasattr(self, 'set_resolution_text'):
                    self.set_resolution_text(self.original_resolution)
                elif hasattr(self, 'resolution_label'):
                    self.resolution_label.setText(self.original_resolution)
                self.positionSlider.setRange(0, duration_ms)
                self.positionSlider.set_duration_ms(duration_ms)
                self._update_trim_inputs()
                if self.original_resolution not in ["1920x1080", "2560x1440", "3440x1440", "3840x2160"]:
                    self._safe_status(f"Note: Odd resolution ({self.original_resolution})", "orange")
                self._safe_set_duration_text(
                    f"Duration: {duration_s:.2f} s | Res: {self.original_resolution}"
                )
                self.trim_start_ms = 0
                self.trim_end_ms = duration_ms
                self._update_trim_widgets_from_trim_times()
                self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
                self._safe_status("Video loaded.", "white")
            except Exception as e:
                self._safe_status(f"UI Update Error: {e}", "red")
                if hasattr(self, "logger"):
                    self.logger.exception("UI update failed in on_worker_finished")
            finally:
                if hasattr(self, "timer") and not self.timer.isActive():
                    self.timer.start(100)
                if hasattr(self, "video_frame"):
                    self.video_frame.setFocus()
                self.activateWindow()
                self.setFocus()

        class _ProbeBridge(QObject):
            done = pyqtSignal(object)
        self._probe_bridge = _ProbeBridge()
        self._probe_bridge.done.connect(_on_worker_finished)

        def _thread_target():
            result = _bg_worker(path_to_probe)
            self._probe_bridge.done.emit(result)
        t = threading.Thread(target=_thread_target, daemon=True)
        t.start()

    def on_progress(self, value: int):
        if self.progress_bar.maximum() == 0:
            self.progress_bar.setRange(0, 100)
        v = int(max(0, min(100, value)))
        self.progress_bar.setValue(v)
    
    def _probe_audio_duration(self, path: str) -> float:
        """Return audio duration in seconds (float) or 0.0 on failure."""
        try:
            ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')
            cmd = [ffprobe_path, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration", "-of", "csv=p=0", path]
            r = subprocess.run(cmd, text=True, capture_output=True, creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0))
            out = r.stdout.strip()
            if not out or "n/a" in out.lower():
                cmd = [ffprobe_path, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]
                r = subprocess.run(cmd, text=True, capture_output=True, creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0))
                out = r.stdout.strip()
            return max(0.0, float(out))
        except Exception:
            return 0.0
    
    def _probe_video_metadata(self, path: str) -> tuple[float, str]:
        """Return (duration_s, resolution_str) or (0.0, '')."""
        try:
            ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')
            cmd = [
                ffprobe_path, "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=duration,width,height", "-of", "json", path
            ]
            r = subprocess.run(cmd, text=True, check=True,
                            stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0))
            data = json.loads(r.stdout.strip())
            stream = data.get('streams', [{}])[0]
            dur = float(stream.get('duration', 0.0) or 0.0)
            res = f"{stream.get('width', 0)}x{stream.get('height', 0)}"
            return max(0.0, dur), res
        except Exception:
            self.logger.exception(f"Failed to probe video metadata for {path}")
            return 0.0, ""