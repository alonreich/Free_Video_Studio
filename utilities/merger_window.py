from PyQt5.QtWidgets import QMainWindow, QFileDialog, QApplication, QListWidget, QLabel, QPushButton, QMessageBox
from PyQt5.QtCore import pyqtSignal, Qt, QTimer, QEvent, QProcess
from PyQt5.QtGui import QIcon, QPainter, QPixmap, QFont, QColor, QPen, QBrush
from PyQt5.QtCore import QRect
import math
import tempfile
import os
import sys
import subprocess
import time
from pathlib import Path
from utilities.merger_ui import MergerUI
from utilities.merger_handlers_main import MergerHandlers
from utilities.merger_utils import _get_logger, _human
from utilities.merger_window_logic import MergerWindowLogic
from ui.widgets.draggable_list_widget import DraggableListWidget
from processing.filter_builder import FilterBuilder
from processing.encoders import EncoderManager
from ui.parts.music_mixin import MusicMixin
from utilities.merger_phase_overlay_mixin import MergerPhaseOverlayMixin

class DummyPositionSlider:
    """A mock object to satisfy MusicMixin's dependency on positionSlider."""

    def set_music_visible(self, visible): pass

    def reset_music_times(self): pass

    def set_music_times(self, start, end): pass

class ConfigManagerAdapter:
    def __init__(self, merger_window_instance):
        self.window = merger_window_instance
    @property
    def config(self):
        return self.window._cfg
    
    def save_config(self, cfg_data):
        self.window._cfg.update(cfg_data)

class VideoMergerWindow(QMainWindow, MusicMixin, MergerPhaseOverlayMixin):
    MAX_FILES = 20
    status_updated = pyqtSignal(str)
    return_to_main = pyqtSignal()

    def __init__(self, ffmpeg_path: str | None = None, parent: QMainWindow | None = None, vlc_instance=None, bin_dir: str = '', config_manager=None, base_dir: str = ''):
        super().__init__(parent)
        self._loaded = False
        self.base_dir = base_dir
        self.ffmpeg = ffmpeg_path or "ffmpeg"
        self.vlc_instance = vlc_instance
        self.bin_dir = bin_dir
        self.trim_start = None
        self.trim_end = None
        self.original_duration = 0.0
        self.process = None
        self.is_processing = False
        self._pulse_phase = 0
        self.positionSlider = DummyPositionSlider()
        self.config_manager = ConfigManagerAdapter(self)
        self.logger = _get_logger()
        self.ui_handler = MergerUI(self)
        self.event_handler = MergerHandlers(self)
        self.logic_handler = MergerWindowLogic(self)
        self.init_ui()
        self.connect_signals()
        self._scan_mp3_folder()
        self.event_handler.update_button_states()
        self._reset_music_player()
        self.logger.info("OPEN: Video Merger window created")

    def _probe_audio_duration(self, path: str) -> float:
        """Return audio duration in seconds (float) or 0.0 on failure."""
        try:
            ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')
            cmd = [ffprobe_path, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]
            r = subprocess.run(cmd, text=True, check=True,
                            stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0))
            return max(0.0, float(r.stdout.strip()))
        except Exception:
            return 0.0

    def showEvent(self, event: QEvent):
        """Load config only when the window is first shown."""
        if not self._loaded:
            self._loaded = True
            self.logic_handler.load_config()
        super().showEvent(event)

    def resizeEvent(self, event: QEvent):
        super().resizeEvent(event)
        self._resize_overlay()

    def init_ui(self):
        self.setWindowTitle("Video Merger")
        self.resize(980, 560)
        self.setMinimumHeight(560)
        self.ui_handler.set_style()
        self.ui_handler.setup_ui()
        self.set_icon()
        self._ensure_overlay_widgets()
        self.btn_cancel_merge = QPushButton("Cancel Merge")
        self.btn_cancel_merge.setObjectName("danger-btn")
        self.btn_cancel_merge.setFixedSize(221, 41)
        self.btn_cancel_merge.clicked.connect(self.cancel_processing)
        self.btn_cancel_merge.hide()
        self.merge_row.insertWidget(1, self.btn_cancel_merge)
        self._pulse_timer = QTimer(self)
        self._pulse_timer.setSingleShot(False)
        self._pulse_timer.timeout.connect(self._update_process_button_text)
        self._pulse_timer.start(750)
        self._color_pulse_timer = QTimer(self)
        self._color_pulse_timer.setInterval(100)
        self._color_pulse_timer.timeout.connect(self._pulse_button_color)
        if hasattr(self, '_graph'):
            self._graph.paintEvent = self._paint_graph_event

    def set_icon(self):
        try:
            _proj_root_path = Path(__file__).resolve().parents[1]
            preferred = str(_proj_root_path / "icons" / "Video_Icon_File.ico")
            fallback  = str(_proj_root_path / "icons" / "app_icon.ico")
            icon_path = preferred if os.path.exists(preferred) else fallback
            if os.path.exists(icon_path):
                self.setWindowIcon(QIcon(icon_path))
        except Exception as e:
            self.logger.error("Failed to set window icon: %s", e)

    def connect_signals(self):
        self.listw.itemSelectionChanged.connect(self.event_handler.update_button_states)
        self.status_updated.connect(self.handle_status_update)
        self.listw.model().rowsInserted.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.on_list_cleared)
        self.listw.model().rowsMoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsMoved.connect(self.event_handler.on_rows_moved)
        self.btn_add.clicked.connect(self.add_videos)
        self.btn_remove.clicked.connect(self.remove_selected)
        self.btn_clear.clicked.connect(self.listw.clear)
        self.add_music_checkbox.toggled.connect(self._on_add_music_toggled)
        self.music_combo.currentIndexChanged.connect(self._on_music_selected)
        self.music_volume_slider.valueChanged.connect(self._on_music_volume_changed)
        self.music_volume_slider.valueChanged.connect(self._update_music_badge)
        self.listw.itemSelectionChanged.connect(self.event_handler.on_selection_changed)

    def closeEvent(self, e):
        self.logic_handler.save_config()
        super().closeEvent(e)

    def handle_status_update(self, msg: str):
        self.status_label.setStyleSheet("color: #43b581; font-weight: normal;")
        self.status_label.setText(f"Processing merge... {msg}")

    def on_list_cleared(self):
        if self.listw.count() == 0:
            self._reset_music_player()

    def on_merge_clicked(self):
        self.start_merge_processing()

    def start_merge_processing(self):
        if self.is_processing:
            return
        n = self.listw.count()
        if n < 2:
            QMessageBox.information(self, "Need more videos", "Please add at least 2 videos to merge.")
            return
        last_out_dir = self._last_out_dir if self._last_out_dir and Path(self._last_out_dir).exists() else str(Path.home() / "Downloads")
        default_path = str(Path(last_out_dir) / "merged_video.mp4")
        out_path, _ = self.open_save_dialog(default_path)
        if not out_path:
            self.logger.info("MERGE: User cancelled file save dialog.")
            return
        self.is_processing = True
        self._show_processing_overlay()
        self._pulse_timer.start(250)
        self.btn_merge.hide()
        self.btn_cancel_merge.show()
        self.btn_cancel_merge.setCursor(Qt.PointingHandCursor)
        self.event_handler.update_button_states()
        self._last_out_dir = str(Path(out_path).parent)
        self.logic_handler.save_config()
        self._temp_dir = tempfile.TemporaryDirectory()
        concat_txt = Path(self._temp_dir.name, "concat_list.txt")
        video_files = []
        for i in range(n):
            it = self.listw.item(i)
            video_files.append(it.data(Qt.UserRole))
        with concat_txt.open("w", encoding="utf-8") as f:
            for path in video_files:
                escaped_path = path.replace("'", "'\'\''")
                f.write(f"file '{escaped_path}'\n")
        self._output_path = out_path
        music_path, music_vol = self._get_selected_music()
        music_offset = self.music_offset_input.value()
        base_cmd = [self.ffmpeg, "-y", "-f", "concat", "-safe", "0", "-i", str(concat_txt)]
        if music_path:
            self.logger.info("MUSIC: Adding background music. Audio and video will be re-encoded.")
            total_duration = 0.0
            for video_path in video_files:
                try:
                    duration = self._probe_audio_duration(video_path)
                    total_duration += duration
                except Exception as e:
                    self.logger.error(f"Failed to probe duration for {video_path}: {e}")
                    QMessageBox.critical(self, "Error", f"Could not get duration of {os.path.basename(video_path)}.")
                    self._merge_finished()
                    return
            filter_builder = FilterBuilder(self.logger)
            encoder_mgr = EncoderManager(self.logger)
            music_cfg = {
                'path': music_path,
                'volume': music_vol,
                'file_offset_sec': music_offset,
                'timeline_start_sec': 0.0,
                'timeline_end_sec': total_duration
            }
            audio_chains = filter_builder.build_audio_chain(
                music_config=music_cfg,
                video_start_time=0.0,
                video_end_time=total_duration,
                speed_factor=1.0,
                disable_fades=False, 
                vfade_in_d=0.0,
                audio_filter_cmd=""
            )
            filter_complex_str = ";".join(audio_chains)
            self.logger.info("Video Mode: Stream Copy (Lossless Video, Re-encode Audio)")
            base_cmd.extend(["-i", music_path])
            self._cmd = base_cmd + [
                "-filter_complex", filter_complex_str,
                "-map", "0:v",
                "-map", "[acore]",
                "-c:v", "copy",
                "-c:a", "aac", "-b:a", "192k",
                "-shortest",
                str(out_path)
            ]
        else:
            self.logger.info("MUSIC: No background music. Using fast stream copy.")
            self._cmd = base_cmd + [
                "-c", "copy",
                str(out_path)
            ]
        inputs = []
        total_in = 0
        for f in video_files:
            try:
                sz = Path(f).stat().st_size
                total_in += sz
                inputs.append({"path": f, "size": _human(sz)})
            except Exception:
                inputs.append({"path": f, "size": "?"})
        self.logger.info("MERGE_START: outputs='%s'", self._output_path)
        self.logger.info("MERGE_INPUTS: %s", inputs)
        self.logger.info("MERGE_CMD: %s", " ".join(self._cmd))
        self.logger.info("MERGE_TOTAL_INPUT_SIZE: %s", _human(total_in))
        self.process = QProcess(self)
        self.process.finished.connect(self._merge_finished)
        self.process.readyReadStandardError.connect(self._process_ffmpeg_output)
        self._merge_started_at = time.time()
        self.logger.info("MERGE_EXEC: %s", " ".join(self._cmd))
        self.process.start(self.ffmpeg, self._cmd[1:])

    def _update_process_button_text(self) -> None:
        """Updates the process button text AND spinner icon."""
        try:
            self._pulse_phase = (getattr(self, "_pulse_phase", 0) + 1) % 8
            if getattr(self, "is_processing", False):
                dots = "." * (1 + (self._pulse_phase // 2))
                text = f"Merging{dots}"
                spinner = "◐◓◑◒"
                glyph = spinner[(self._pulse_phase // 2) % len(spinner)]
                px = 26
                pm = QPixmap(px, px)
                pm.fill(Qt.transparent)
                p = QPainter(pm)
                f = QFont(self.font())
                f.setPointSize(px)
                p.setFont(f)
                p.setPen(Qt.black)
                p.drawText(pm.rect(), Qt.AlignCenter, glyph)
                p.end()
                self.btn_merge.setText(text)
                self.btn_merge.setIcon(QIcon(pm))
                self.btn_merge.setIconSize(pm.size())
            else:
                self.btn_merge.setText("Merge Videos")
                self.btn_merge.setIcon(QIcon())
        except Exception as e:
            self.logger.error(f"Error updating button text: {e}")

    def cancel_processing(self):
        if self.process and self.process.state() == QProcess.Running:
            self.logger.info("MERGE: User cancelled processing.")
            self.process.kill()

    def add_videos(self):
        self.event_handler.add_videos()

    def remove_selected(self):
        self.event_handler.remove_selected()

    def move_item(self, direction: int):
        self.event_handler.move_item(direction)

    def return_to_main_app(self):
        self.logger.info("ACTION: Return to Main App clicked.")
        try:
            app_py_path = os.path.join(self.base_dir, 'app.py')
            if not os.path.exists(app_py_path):
                self.logger.critical(f"ERROR: Main app script not found at {app_py_path}")
                self.close()
                return
            command = [sys.executable, app_py_path]
            creation_flags = 0x08000000 if sys.platform == "win32" else 0
            subprocess.Popen(command, cwd=self.base_dir, creationflags=creation_flags)
            self.close()
        except Exception as e:
            self.logger.critical(f"ERROR: Failed to launch Main App. Error: {e}")
            self.close()

    def create_draggable_list_widget(self):
        listw = DraggableListWidget()
        listw.setAlternatingRowColors(False)
        listw.setSpacing(6)
        listw.setSelectionMode(QListWidget.SingleSelection)
        listw.setUniformItemSizes(False)
        return listw

    def set_ui_busy(self, is_busy: bool):
        if self.is_processing:
            return
        if is_busy:
            self.setWindowTitle("Video Merger (Processing...)")
            self.btn_back.setDisabled(True)
            self.listw.setDisabled(True)
            self.btn_up.setDisabled(True)
            self.btn_down.setDisabled(True)
        else:
            self.setWindowTitle("Video Merger")
            self.btn_back.setDisabled(False)
            self.listw.setDisabled(False)
        self.event_handler.update_button_states()

    def open_save_dialog(self, default_path):
        return QFileDialog.getSaveFileName(
            self, "Save merged video as…",
            default_path,
            "MP4 (*.mp4);;MOV (*.mov);;MKV (*.mkv);;All Files (*)"
        )

    def show_success_dialog(self, output_path):
        self.event_handler.show_success_dialog(output_path)

    def make_item_widget(self, path):
        return self.event_handler.make_item_widget(path)

    def can_anim(self, row, new_row):
        return self.logic_handler.can_anim(row, new_row)

    def start_swap_animation(self, row, new_row):
        return self.logic_handler.start_swap_animation(row, new_row)

    def perform_swap(self, row, new_row):
        return self.logic_handler.perform_swap(row, new_row)

    def _process_ffmpeg_output(self):
        raw_bytes = self.process.readAllStandardError()
        text = raw_bytes.data().decode('utf-8').strip()
        if text:
            self.logger.info("FFMPEG: %s", text)
            self._append_live_log(text)

    def _merge_finished(self):
        exit_code = self.process.exitCode()
        self.is_processing = False
        self._hide_processing_overlay()
        self._pulse_timer.start(750)
        self.btn_merge.show()
        self.btn_cancel_merge.unsetCursor()
        self.btn_cancel_merge.hide()
        self.btn_merge.setStyleSheet(self._merge_btn_base_css)
        self._update_process_button_text()
        self.logger.info("MERGE_FINISH: exit_code=%s", exit_code)
        self.set_ui_busy(False)
        self.event_handler.update_button_states()
        if hasattr(self, '_temp_dir') and self._temp_dir:
            try:
                self._temp_dir.cleanup()
                self.logger.info("Cleaned up temporary directory: %s", self._temp_dir.name)
            except Exception as e:
                self.logger.error("Failed to clean up temp dir: %s", e)
        if exit_code == 0:
            if hasattr(self, '_output_path') and self._output_path:
                sz = Path(self._output_path).stat().st_size
                self.logger.info("MERGE_DONE: output='%s' | size=%s", self._output_path, _human(sz))
                self.show_success_dialog(self._output_path)
        else:
            if self.process.exitStatus() == QProcess.CrashExit:
                self.logger.error("MERGE_FAIL: FFmpeg process crashed.")
                QMessageBox.critical(self, "Merge Failed", "FFmpeg process crashed. See logs for details.")
            else:
                self.logger.error("MERGE_FAIL: FFmpeg process failed with exit code %s.", exit_code)
                if exit_code != -1:
                    QMessageBox.critical(self, "Merge Failed", f"FFmpeg process failed with exit code {exit_code}. See logs for details.")
        self.process = None

    def _paint_graph_event(self, event):
        p = QPainter(self._graph)
        try:
            p.setRenderHint(QPainter.Antialiasing)
            panel = self._graph.rect()
            f = QFont("Consolas", 10, QFont.Bold); p.setFont(f)
            left, top = panel.left() + 110, panel.top() + 10
            right, bottom = panel.right() - 20, panel.bottom() - 10
            w, h = max(1, right - left), max(1, bottom - top)
            gap_y, bands = 20, 4
            band_h = (h - gap_y * (bands - 1)) // bands
            total_seconds = len(getattr(self, "_cpu_hist", [])) * 2
            time_str = f"{total_seconds//60:02d}:{total_seconds%60:02d}"

            def plot_band(hist, label, color, row):
                y0 = top + row * (band_h + gap_y)
                r_lane = QRect(left, y0, w, band_h)
                p.setPen(QPen(QColor(255, 255, 255, 60), 2))
                p.drawLine(left - 15, y0, left - 15, y0 + band_h + 10)
                text_block_h = 50 
                text_start_y = y0 + (band_h - text_block_h) // 2
                last_val = int(list(hist)[-1]) if hist else 0
                p.setPen(QColor(200, 200, 200))
                p.drawText(panel.left() + 5, text_start_y + 10, label)
                f_big = QFont("Consolas", 14, QFont.Bold); p.setFont(f_big)
                p.setPen(color)
                p.drawText(panel.left() + 5, text_start_y + 32, f"{last_val}%")
                p.setFont(f)
                p.setPen(QColor(100, 100, 100))
                p.drawText(panel.left() + 5, text_start_y + 50, f"T: {time_str}")
                p.setPen(QPen(QColor(255, 255, 255, 20), 1))
                p.drawLine(left, y0 + band_h, right, y0 + band_h)
                if row < 3:
                    sep_y = y0 + band_h + (gap_y // 2)
                    p.setPen(QPen(QColor(60, 70, 80), 1))
                    p.drawLine(panel.left(), sep_y, panel.right(), sep_y)
                vals = list(hist) if hist else []
                if not vals: return
                bar_gap = 9 
                max_bars = w // (15 + bar_gap) 
                visible_vals = vals[-max_bars:]
                actual_bar_w = max(15, min(25, (w // len(visible_vals)) - bar_gap)) if visible_vals else 15
                for i, v in enumerate(visible_vals):
                    bx = left + i * (actual_bar_w + bar_gap)
                    bh = int((max(2, v) / 100.0) * band_h)
                    p.setPen(QPen(color.darker(150), 1))
                    p.setBrush(color)
                    p.drawRect(bx, r_lane.bottom() - bh, actual_bar_w, bh)
            plot_band(self._cpu_hist,  "SYSTEM CPU", QColor(0, 230, 255), 0)
            plot_band(self._gpu_hist,  "NVIDIA GPU", QColor(0, 255, 130), 1)
            plot_band(self._mem_hist,  "MEMORY USE", QColor(255, 180, 0),  2)
            plot_band(self._iops_hist, "DISK I/O",   QColor(255, 80, 80),  3)
        finally:
            p.end()

    def _pulse_button_color(self):
        try:
            if not getattr(self, "is_processing", False):
                if getattr(self, "_color_pulse_timer", None):
                    self._color_pulse_timer.stop()
                return
            self._pulse_phase = (getattr(self, "_pulse_phase", 0) + 1) % 20
            t = self._pulse_phase / 20.0
            k = (math.sin(4 * math.pi * t) + 1) / 2
            g1 = (72, 235, 90)
            g2 = (10,  80, 16)
            r = int(g1[0] * k + g2[0] * (1 - k))
            g = int(g1[1] * k + g2[1] * (1 - k))
            b = int(g1[2] * k + g2[2] * (1 - k))
            current_text = self.btn_merge.text()
            current_icon = self.btn_merge.icon()
            self.btn_merge.setStyleSheet(f"""
                QPushButton {{
                    background-color: rgb({r},{g},{b});
                    color: black;
                    font-weight: bold;
                    font-size: 16px;
                    border-radius: 15px;
                    margin-bottom: 6px;
                }}
                QPushButton:hover {{ background-color: #c8f7c5; }}
            """)
            self.btn_merge.setText(current_text)
            self.btn_merge.setIcon(current_icon)
        except Exception:
            pass            

    def eventFilter(self, obj, event):
        return super().eventFilter(obj, event)