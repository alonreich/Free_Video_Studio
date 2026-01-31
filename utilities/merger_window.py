from PyQt5.QtWidgets import QMainWindow, QFileDialog, QApplication, QListWidget, QLabel, QPushButton, QMessageBox
from PyQt5.QtCore import pyqtSignal, Qt, QTimer, QEvent, QProcess, QThread, QStandardPaths, QMutex, QMutexLocker
from PyQt5.QtGui import QIcon, QPainter, QPixmap, QFont, QColor, QPen, QBrush
from PyQt5.QtCore import QRect
import math
import tempfile
import os
import sys
import subprocess
import time
import psutil
import decimal
from datetime import datetime
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
from utilities.merger_unified_music_widget import UnifiedMusicWidget

class MusicSliderMock:
    """Mock object to satisfy MusicMixin dependency safely."""

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

class ProbeWorker(QThread):
    finished = pyqtSignal(list, float)
    error = pyqtSignal(str)

    def __init__(self, video_files, bin_dir, logger=None):
        super().__init__()
        self.video_files = video_files
        self.bin_dir = bin_dir
        self.ffprobe = os.path.join(bin_dir, 'ffprobe.exe') if sys.platform == 'win32' else 'ffprobe'
        self._cancelled = False
        self._logger = logger
        self._mutex = QMutex()

    def run(self):
        total = decimal.Decimal('0.0')
        durations = []
        try:
            for path in self.video_files:
                with QMutexLocker(self._mutex):
                    if self._cancelled:
                        return
                try:
                    cmd = [self.ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]
                    r = subprocess.run(cmd, capture_output=True, text=True,
                                    creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0))
                    duration_str = r.stdout.strip() or '0'
                    d = decimal.Decimal(duration_str) if duration_str else decimal.Decimal('0')
                    if d < 0:
                        d = decimal.Decimal('0')
                    total += d
                    durations.append((path, d))
                except Exception as ex:
                    if self._logger:
                        self._logger.debug(f"Failed to probe {path}: {ex}")
                    durations.append((path, decimal.Decimal('0.0')))
            with QMutexLocker(self._mutex):
                if not self._cancelled:
                    float_durations = [(path, float(d)) for path, d in durations]
                    total_float = float(total.quantize(decimal.Decimal('0.001'), rounding=decimal.ROUND_HALF_UP))
                    self.finished.emit(float_durations, total_float)
        except Exception as ex:
            if self._logger:
                self._logger.error(f"ProbeWorker crashed: {ex}")
            self.error.emit(str(ex))

    def cancel(self):
        """Thread-safe cancellation."""
        with QMutexLocker(self._mutex):
            self._cancelled = True
        if self.isRunning():
            self.quit()
            if not self.wait(1000):
                self.terminate()
                self.wait()

    def cleanup(self):
        """Explicit cleanup method."""
        self.cancel()
        if self.isRunning():
            self.wait(2000)

class VideoMergerWindow(QMainWindow, MusicMixin, MergerPhaseOverlayMixin):
    MAX_FILES = 100
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
        self._pulse_phase = 0
        self.positionSlider = MusicSliderMock()
        self.config_manager = ConfigManagerAdapter(self)
        self.logger = _get_logger()
        self.ui_handler = MergerUI(self)
        self.event_handler = MergerHandlers(self)
        self.logic_handler = MergerWindowLogic(self)
        self._probe_worker = None
        self._state_mutex = QMutex()
        self._is_processing = False
        self._is_cancelling = False
        self.init_ui()
        self.connect_signals()
        self._scan_mp3_folder()
        self.event_handler.update_button_states()
        self._reset_music_player()
        self.logger.info("OPEN: Video Merger window created")
    @property
    def is_processing(self) -> bool:
        """Thread-safe access to processing state."""
        with QMutexLocker(self._state_mutex):
            return self._is_processing
    @is_processing.setter
    def is_processing(self, value: bool) -> None:
        """Thread-safe modification of processing state."""
        with QMutexLocker(self._state_mutex):
            self._is_processing = value
    
    def set_processing_state(self, value: bool) -> bool:
        """
        Thread-safe state transition with cancellation check.
        Returns True if state was successfully changed, False if cancelled.
        """
        with QMutexLocker(self._state_mutex):
            if self._is_cancelling:
                return False
            self._is_processing = value
            return True
    
    def request_cancellation(self) -> bool:
        """
        Request cancellation of current operation.
        Returns True if cancellation was initiated, False if already cancelling.
        """
        with QMutexLocker(self._state_mutex):
            if self._is_cancelling:
                return False
            self._is_cancelling = True
            return True
    
    def clear_cancellation(self) -> None:
        """Clear cancellation flag after operation completes."""
        with QMutexLocker(self._state_mutex):
            self._is_cancelling = False

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
        self.resize(1000, 700)
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
        self._pulse_timer.timeout.connect(self._update_process_button_text)
        if hasattr(self, '_graph'):
            self._graph.paintEvent = self._paint_graph_event
        self.setup_progress_visualization()
        self.setup_keyboard_shortcuts()
        self.setup_standard_tooltips()

    def set_icon(self):
        try:
            _proj_root_path = Path(__file__).resolve().parents[1]
            preferred = str(_proj_root_path / "icons" / "Video_Icon_File.ico")
            fallback  = str(_proj_root_path / "icons" / "app_icon.ico")
            icon_path = preferred if os.path.exists(preferred) else fallback
            if os.path.exists(icon_path):
                self.setWindowIcon(QIcon(icon_path))
        except Exception as ex:
            self.logger.debug(f"Failed to set window icon: {ex}")

    def connect_signals(self):
        """Connect all signals with deduplication."""

        def safe_disconnect(signal):
            try:
                if signal:
                    signal.disconnect()
            except (TypeError, RuntimeError):
                pass
        safe_disconnect(getattr(self.listw, 'itemSelectionChanged', None))
        safe_disconnect(getattr(self, 'status_updated', None))
        safe_disconnect(getattr(self.btn_add, 'clicked', None) if hasattr(self, 'btn_add') else None)
        safe_disconnect(getattr(self.btn_remove, 'clicked', None) if hasattr(self, 'btn_remove') else None)
        safe_disconnect(getattr(self.btn_clear, 'clicked', None) if hasattr(self, 'btn_clear') else None)
        safe_disconnect(getattr(self.add_music_checkbox, 'toggled', None) if hasattr(self, 'add_music_checkbox') else None)
        safe_disconnect(getattr(self.music_combo, 'currentIndexChanged', None) if hasattr(self, 'music_combo') else None)
        safe_disconnect(getattr(self.music_volume_slider, 'valueChanged', None) if hasattr(self, 'music_volume_slider') else None)
        if hasattr(self, 'unified_music_widget'):
            safe_disconnect(getattr(self.unified_music_widget.music_toggled, 'connect', None))
            safe_disconnect(getattr(self.unified_music_widget.track_selected, 'connect', None))
            safe_disconnect(getattr(self.unified_music_widget.volume_changed, 'connect', None))
            safe_disconnect(getattr(self.unified_music_widget.offset_changed, 'connect', None))
            safe_disconnect(getattr(self.unified_music_widget.advanced_requested, 'connect', None))
            self.unified_music_widget.music_toggled.connect(self._on_unified_music_toggled)
            self.unified_music_widget.track_selected.connect(self._on_unified_track_selected)
            self.unified_music_widget.volume_changed.connect(self._on_unified_volume_changed)
            self.unified_music_widget.offset_changed.connect(self._on_unified_offset_changed)
            self.unified_music_widget.advanced_requested.connect(self._on_unified_advanced_requested)
        self.listw.itemSelectionChanged.connect(self.event_handler.update_button_states)
        self.status_updated.connect(self.handle_status_update)
        self.listw.model().rowsInserted.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.on_list_cleared)
        self.listw.model().rowsMoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsMoved.connect(self.event_handler.on_rows_moved)
        self.btn_add.clicked.connect(self.add_videos)
        self.btn_remove.clicked.connect(self.remove_selected)
        self.btn_clear.clicked.connect(self.confirm_clear_list)
        self.add_music_checkbox.toggled.connect(self._on_add_music_toggled)
        self.music_combo.currentIndexChanged.connect(self._on_music_selected)
        self.music_volume_slider.valueChanged.connect(self._on_music_volume_changed)
        self.music_volume_slider.valueChanged.connect(self._update_music_badge)
        self.listw.itemSelectionChanged.connect(self.event_handler.on_selection_changed)

    def confirm_clear_list(self):
        if self.listw.count() > 0:
            self.status_label.setText("List cleared")
            self.status_label.setStyleSheet("color: #ff6b6b; font-weight: bold;")
            self.listw.clear()
            QTimer.singleShot(2000, lambda: self.status_label.setText("Ready. Add 2 to 100 videos to begin."))

    def closeEvent(self, e):
        self.logic_handler.save_config()
        if self.vlc_instance:
             try: 
                 self.vlc_instance.release()
             except Exception as ex:
                 self.logger.debug(f"Failed to release VLC instance: {ex}")
        super().closeEvent(e)

    def handle_status_update(self, msg: str):
        self.status_label.setStyleSheet("color: #43b581; font-weight: normal;")
        self.status_label.setText(f"Processing... {msg}")

    def on_list_cleared(self):
        if self.listw.count() == 0:
            self._reset_music_player()

    def on_merge_clicked(self):
        self.start_merge_processing()

    def _get_next_output_path(self):
        """Get the next available output path in !!!_Output_Video_Files_!!! folder."""
        output_dir = Path(self.base_dir) / "!!!_Output_Video_Files_!!!"
        output_dir.mkdir(exist_ok=True)
        index = 1
        while True:
            candidate = output_dir / f"Merged-Video-{index}.mp4"
            if not candidate.exists():
                return str(candidate)
            index += 1

    def start_merge_processing(self):
        if not self.set_processing_state(True):
            return
        n = self.listw.count()
        if n < 2:
            QMessageBox.information(self, "Need more videos", "Please add at least 2 videos to merge.")
            self.set_processing_state(False)
            return
        out_path = self._get_next_output_path()
        video_files = []
        for i in range(n):
            it = self.listw.item(i)
            video_files.append(it.data(Qt.UserRole))
        music_path, _ = self._get_selected_music()
        needs_probe = bool(music_path)
        self._output_path = out_path
        self._show_processing_overlay()
        self._pulse_timer.start(250)
        self.btn_merge.hide()
        self.btn_cancel_merge.show()
        self.btn_cancel_merge.setCursor(Qt.PointingHandCursor)
        self.event_handler.update_button_states()
        self.logic_handler.save_config()
        self._temp_dir = tempfile.TemporaryDirectory()
        if needs_probe:
            self.logger.info("MERGE: Music detected, starting optimized background probe...")
            self.status_updated.emit("Probing audio duration...")
            self._probe_worker = ProbeWorker(video_files, self.bin_dir, self.logger)
            self._probe_worker.finished.connect(self._finalize_merge_setup)
            self._probe_worker.start()
        else:
            self.logger.info("MERGE: No music, skipping probe.")
            self._finalize_merge_setup(video_files, 0.0)

    def _finalize_merge_setup(self, video_files, total_duration=0.0):
        if not self.is_processing: return
        concat_txt = Path(self._temp_dir.name, "concat_list.txt")
        try:
            with concat_txt.open("w", encoding="utf-8") as f:
                for path in video_files:
                    escaped = path.replace("'", "'\'\''")
                    f.write(f"file '{escaped}'\n")
        except Exception as e:
            QMessageBox.critical(self, "Error", f"Failed to create list file: {e}")
            self._merge_finished_cleanup(-1, QProcess.CrashExit)
            return
        music_path, music_vol = self._get_selected_music()
        music_offset = self.music_offset_input.value()
        base_cmd = [self.ffmpeg, "-y", "-f", "concat", "-safe", "0", "-i", str(concat_txt)]
        encoder_manager = EncoderManager(self.logger)
        encoder_flags, encoder_label = encoder_manager.get_codec_flags(
            encoder_manager.get_initial_encoder(),
            video_bitrate_kbps=None,
            effective_duration_sec=total_duration
        )
        self.logger.info(f"Using encoder: {encoder_label}")
        if music_path:
            filter_builder = FilterBuilder(self.logger)
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
            filter_str = ";".join(audio_chains)
            base_cmd.extend(["-i", music_path])
            self._cmd = base_cmd + [
                "-filter_complex", filter_str,
                "-map", "0:v", "-map", "[acore]",
                *encoder_flags,
                "-c:a", "aac", "-b:a", "192k",
                "-shortest",
                str(self._output_path)
            ]
        else:
            self._cmd = base_cmd + ["-c", "copy", str(self._output_path)]
        self.logger.info(f"MERGE_CMD: {self._cmd}")
        self.process = QProcess(self)
        self.process.finished.connect(self._merge_finished_cleanup)
        self.process.readyReadStandardError.connect(self._process_ffmpeg_output)
        self._merge_started_at = time.time()
        self.process.start(self.ffmpeg, self._cmd[1:])

    def _process_ffmpeg_output(self):
        try:
            raw = self.process.readAllStandardError()
            text = raw.data().decode('utf-8', errors='ignore').strip()
            if text:
                last = text.splitlines()[-1]
                if "frame=" in last or "time=" in last:
                    self.status_updated.emit(last)
                self._append_live_log(text)
        except Exception as ex:
            self.logger.debug(f"Error processing FFmpeg output: {ex}")

    def _update_process_button_text(self) -> None:
        """Lightweight button text update."""
        if not self.is_processing: return
        self._pulse_phase = (self._pulse_phase + 1) % 4
        self.btn_merge.setText(f"Merging{'.' * (self._pulse_phase + 1)}")

    def _kill_ffmpeg_process_tree(self):
        """Kill FFmpeg process and all its child processes to prevent zombies."""
        try:
            if not self.process:
                return
            pid = self.process.processId()
            if not pid or pid <= 0:
                return
            if self.process.state() == QProcess.Running:
                self.process.terminate()
                if not self.process.waitForFinished(2000):
                    self.process.kill()
                    self.process.waitForFinished(1000)
            try:
                parent = psutil.Process(pid)
                children = parent.children(recursive=True)
                for child in reversed(children):
                    try:
                        child.terminate()
                    except (psutil.NoSuchProcess, psutil.AccessDenied):
                        pass
                gone, alive = psutil.wait_procs(children, timeout=2)
                for child in alive:
                    try:
                        child.kill()
                    except (psutil.NoSuchProcess, psutil.AccessDenied):
                        pass
                try:
                    parent.terminate()
                    parent.wait(timeout=2)
                except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.TimeoutExpired):
                    try:
                        parent.kill()
                    except (psutil.NoSuchProcess, psutil.AccessDenied):
                        pass
                gone, alive = psutil.wait_procs([parent] + children, timeout=1)
                if alive:
                    self.logger.warning(f"Some FFmpeg processes still alive after kill: {alive}")
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        except Exception as ex:
            self.logger.error(f"Error killing FFmpeg process tree: {ex}")
            if self.process and self.process.state() == QProcess.Running:
                self.process.kill()
                self.process.waitForFinished(1000)

    def cancel_processing(self):
        if not self.request_cancellation():
            return
        self.logger.info("MERGE: User cancellation requested.")
        if self.process and self.process.state() == QProcess.Running:
            self._kill_ffmpeg_process_tree()
        if self._probe_worker and self._probe_worker.isRunning():
            self._probe_worker.cancel()
        self.status_label.setText("Cancelling...")
        self.status_label.setStyleSheet("color: #ff6b6b; font-weight: bold;")

    def add_videos(self):
        self.event_handler.add_videos()

    def remove_selected(self):
        self.event_handler.remove_selected()

    def move_item(self, direction: int):
        self.event_handler.move_item(direction)

    def return_to_main_app(self):
        try:
            app_path = os.path.join(self.base_dir, 'app.py')
            subprocess.Popen([sys.executable, app_path], cwd=self.base_dir)
            self.close()
        except Exception as ex:
            self.logger.warning(f"Failed to launch main app: {ex}")
            self.close()

    def create_draggable_list_widget(self):
        listw = DraggableListWidget()
        listw.setAlternatingRowColors(False)
        listw.setSpacing(6)
        listw.setSelectionMode(QListWidget.SingleSelection)
        return listw

    def set_ui_busy(self, is_busy: bool):
        if self.is_processing: return
        self.btn_back.setDisabled(is_busy)
        self.listw.setDisabled(is_busy)

    def open_save_dialog(self, default_path):
        return QFileDialog.getSaveFileName(self, "Save Video", default_path, "MP4 (*.mp4)")

    def show_success_dialog(self, output_path):
        self.status_label.setText(f"Done! Saved to: {os.path.basename(output_path)}")
        self.status_label.setStyleSheet("color: #43b581; font-weight: bold;")
        try:
            if sys.platform == 'win32':
                os.startfile(Path(output_path).parent)
            elif sys.platform == 'darwin':
                subprocess.run(['open', Path(output_path).parent])
            else:
                subprocess.run(['xdg-open', Path(output_path).parent])
        except Exception as ex:
            self.logger.debug(f"Failed to open output folder: {ex}")

    def make_item_widget(self, path):
        return self.event_handler.make_item_widget(path)

    def can_anim(self, row, new_row):
        return self.logic_handler.can_anim(row, new_row)

    def start_swap_animation(self, row, new_row):
        return self.logic_handler.start_swap_animation(row, new_row)

    def perform_swap(self, row, new_row):
        return self.logic_handler.perform_swap(row, new_row)

    def _merge_finished_cleanup(self, exit_code, exit_status):
        self.set_processing_state(False)
        self.clear_cancellation()
        self._hide_processing_overlay()
        self._pulse_timer.stop()
        self.btn_merge.setText("Merge Videos")
        self.btn_merge.show()
        self.btn_cancel_merge.hide()
        self.set_ui_busy(False)
        self.event_handler.update_button_states()
        if hasattr(self, '_temp_dir') and self._temp_dir:
            try: 
                self._temp_dir.cleanup()
                self._temp_dir = None
            except Exception as ex:
                self.logger.warning(f"Failed to cleanup temp directory: {ex}")
        if self._probe_worker:
            if self._probe_worker.isRunning():
                self._probe_worker.cancel()
            self._probe_worker = None
        if exit_code == 0 and exit_status == QProcess.NormalExit:
            self.logger.info("MERGE_SUCCESS")
            self.show_success_dialog(self._output_path)
        else:
            self.logger.error(f"MERGE_FAIL: {exit_code}")
            QMessageBox.critical(self, "Merge Failed", "The merge process failed. Check logs.")
        self.process = None

    def _paint_graph_event(self, event):
        p = QPainter(self._graph)
        try:
            p.setRenderHint(QPainter.Antialiasing)
            rect = self._graph.rect()
            p.fillRect(rect, QColor(20, 20, 20, 200))
            if hasattr(self, '_cpu_hist') and self._cpu_hist:
                 path = self._cpu_hist
                 if len(path) > 1:
                     w = rect.width()
                     h = rect.height()
                     step = w / 50.0
                     p.setPen(QColor(0, 255, 255))
                     for i in range(min(len(path), 50) - 1):
                         x1 = w - (i * step)
                         y1 = h - (path[-(i+1)] / 100.0 * h)
                         x2 = w - ((i+1) * step)
                         y2 = h - (path[-(i+2)] / 100.0 * h)
                         p.drawLine(int(x1), int(y1), int(x2), int(y2))
        except Exception as ex:
            self.logger.debug(f"Error painting graph: {ex}")
        finally:
            p.end()

    def setup_progress_visualization(self):
        """Setup enhanced progress visualization for merge operations."""

        from PyQt5.QtWidgets import QProgressBar, QVBoxLayout, QWidget
        self.progress_widget = QWidget(self)
        self.progress_widget.setObjectName("progressWidget")
        self.progress_widget.setFixedHeight(40)
        self.progress_widget.hide()
        layout = QVBoxLayout(self.progress_widget)
        layout.setContentsMargins(20, 5, 20, 5)
        self.progress_bar = QProgressBar()
        self.progress_bar.setRange(0, 100)
        self.progress_bar.setTextVisible(True)
        self.progress_bar.setFormat("Processing: %p%")
        self.progress_bar.setStyleSheet("""
            QProgressBar {
                border: 2px solid #7289da;
                border-radius: 5px;
                text-align: center;
                background-color: #2c2f33;
            }
            QProgressBar::chunk {
                background-color: #43b581;
                border-radius: 3px;
            }
        """)
        layout.addWidget(self.progress_bar)
        if hasattr(self, 'centralWidget'):
            central_widget = self.centralWidget()
            if central_widget and hasattr(central_widget, 'layout'):
                main_layout = central_widget.layout()
                if main_layout:
                    main_layout.insertWidget(main_layout.count() - 1, self.progress_widget)

    def update_progress(self, value: int, text: str = ""):
        """Update progress bar with value and optional text."""
        if hasattr(self, 'progress_bar'):
            self.progress_bar.setValue(value)
            if text:
                self.progress_bar.setFormat(f"{text}: %p%")
            if value > 0 and value < 100:
                if hasattr(self, 'progress_widget') and not self.progress_widget.isVisible():
                    self.progress_widget.show()
            elif value >= 100 or value <= 0:
                if hasattr(self, 'progress_widget') and self.progress_widget.isVisible():
                    self.progress_widget.hide()

    def setup_keyboard_shortcuts(self):
        """Setup keyboard shortcuts for common operations."""

        from PyQt5.QtWidgets import QShortcut
        from PyQt5.QtGui import QKeySequence
        self.shortcut_add = QShortcut(QKeySequence("Ctrl+O"), self)
        self.shortcut_add.activated.connect(self.add_videos)
        self.shortcut_remove = QShortcut(QKeySequence("Delete"), self)
        self.shortcut_remove.activated.connect(self.remove_selected)
        self.shortcut_up = QShortcut(QKeySequence("Ctrl+Up"), self)
        self.shortcut_up.activated.connect(lambda: self.move_item(-1))
        self.shortcut_down = QShortcut(QKeySequence("Ctrl+Down"), self)
        self.shortcut_down.activated.connect(lambda: self.move_item(1))
        self.shortcut_clear = QShortcut(QKeySequence("Ctrl+Shift+C"), self)
        self.shortcut_clear.activated.connect(self.confirm_clear_list)
        self.shortcut_merge = QShortcut(QKeySequence("Ctrl+M"), self)
        self.shortcut_merge.activated.connect(self.on_merge_clicked)
        self.shortcut_cancel = QShortcut(QKeySequence("Esc"), self)
        self.shortcut_cancel.activated.connect(self.cancel_processing)

    def validate_file_path(self, path: str) -> bool:
        """Validate that a file path exists and is accessible."""
        try:
            if not path or not isinstance(path, str):
                return False
            if not os.path.exists(path):
                self.logger.warning(f"File does not exist: {path}")
                return False
            if not os.access(path, os.R_OK):
                self.logger.warning(f"File not readable: {path}")
                return False
            video_extensions = {'.mp4', '.avi', '.mov', '.mkv', '.wmv', '.flv', '.webm'}
            ext = os.path.splitext(path)[1].lower()
            if ext not in video_extensions:
                self.logger.warning(f"Not a video file: {path}")
                return False
            return True
        except Exception as e:
            self.logger.error(f"Error validating file {path}: {e}")
            return False

    def validate_music_volume(self, volume: int) -> bool:
        """Validate music volume is within acceptable range."""
        return 0 <= volume <= 100

    def validate_music_offset(self, offset: float) -> bool:
        """Validate music offset is within reasonable range."""
        return 0.0 <= offset <= 3600.0

    def validate_video_count(self, count: int) -> bool:
        """Validate video count is within acceptable range."""
        return 2 <= count <= self.MAX_FILES

    def batch_add_videos(self, file_paths: list[str]) -> int:
        """Batch add multiple video files with validation."""
        added_count = 0
        invalid_files = []
        for file_path in file_paths:
            if self.listw.count() >= self.MAX_FILES:
                QMessageBox.warning(self, "Limit reached", f"Maximum {self.MAX_FILES} files already added.")
                break
            if not self.validate_file_path(file_path):
                invalid_files.append(file_path)
                continue
            duplicate = False
            for i in range(self.listw.count()):
                item = self.listw.item(i)
                if item and item.data(Qt.UserRole) == file_path:
                    duplicate = True
                    break
            if not duplicate:
                self.event_handler.add_single_video(file_path)
                added_count += 1
        if invalid_files:
            invalid_list = "\n".join([os.path.basename(f) for f in invalid_files[:5]])
            if len(invalid_files) > 5:
                invalid_list += f"\n...and {len(invalid_files) - 5} more"
            QMessageBox.warning(self, "Invalid Files", 
                              f"{len(invalid_files)} files were invalid and skipped:\n{invalid_list}")
        if added_count > 0:
            self.event_handler.update_button_states()
            self.status_label.setText(f"Added {added_count} video(s)")
            self.status_label.setStyleSheet("color: #43b581; font-weight: bold;")
            QTimer.singleShot(2000, lambda: self.status_label.setText("Ready. Add 2 to 100 videos to begin."))
        return added_count

    def batch_remove_selected(self) -> int:
        """Batch remove all selected items."""
        selected_items = self.listw.selectedItems()
        if not selected_items:
            return 0
        removed_count = len(selected_items)
        for item in selected_items:
            row = self.listw.row(item)
            self.listw.takeItem(row)
        self.event_handler.update_button_states()
        self.status_label.setText(f"Removed {removed_count} video(s)")
        self.status_label.setStyleSheet("color: #ff6b6b; font-weight: bold;")
        QTimer.singleShot(2000, lambda: self.status_label.setText("Ready. Add 2 to 100 videos to begin."))
        return removed_count

    def setup_standard_tooltips(self):
        """Setup standardized tooltips for all UI elements."""
        if hasattr(self, 'btn_add'):
            self.btn_add.setToolTip("Add video files (Ctrl+O)")
        if hasattr(self, 'btn_remove'):
            self.btn_remove.setToolTip("Remove selected video (Delete)")
        if hasattr(self, 'btn_clear'):
            self.btn_clear.setToolTip("Clear all videos (Ctrl+Shift+C)")
        if hasattr(self, 'btn_up'):
            self.btn_up.setToolTip("Move selected video up (Ctrl+Up)")
        if hasattr(self, 'btn_down'):
            self.btn_down.setToolTip("Move selected video down (Ctrl+Down)")
        if hasattr(self, 'btn_merge'):
            self.btn_merge.setToolTip("Merge videos (Ctrl+M)")
        if hasattr(self, 'btn_cancel_merge'):
            self.btn_cancel_merge.setToolTip("Cancel merge process (Esc)")
        if hasattr(self, 'btn_back'):
            self.btn_back.setToolTip("Return to main application")
        if hasattr(self, 'add_music_checkbox'):
            self.add_music_checkbox.setToolTip("Add background music to merged video")
        if hasattr(self, 'music_combo'):
            self.music_combo.setToolTip("Select background music track")
        if hasattr(self, 'music_volume_slider'):
            self.music_volume_slider.setToolTip("Adjust music volume (0-100%)")
        if hasattr(self, 'music_offset_input'):
            self.music_offset_input.setToolTip("Music start offset in seconds (0-3600)")
        if hasattr(self, 'listw'):
            self.listw.setToolTip("Drag and drop to reorder videos. Select items for batch operations.")

    def handle_error(self, context: str, error: Exception, show_user: bool = True):
        """Unified error handling with logging and optional user notification."""
        error_msg = f"{context}: {str(error)}"
        self.logger.error(error_msg)
        if show_user:
            user_msg = f"An error occurred: {context}"
            if isinstance(error, (FileNotFoundError, PermissionError)):
                user_msg = f"File error: {context}"
            elif isinstance(error, (ValueError, TypeError)):
                user_msg = f"Invalid data: {context}"
            QMessageBox.warning(self, "Error", f"{user_msg}\n\nDetails: {str(error)[:100]}...")
        return False

    def eventFilter(self, obj, event):
        return super().eventFilter(obj, event)

    def _scan_mp3_folder(self):
        """Scan MP3 folder and load tracks into unified music widget."""
        if hasattr(self, 'unified_music_widget'):
            mp3_folder = os.path.join(self.base_dir, 'mp3')
            self.unified_music_widget.load_tracks(mp3_folder)
    
    def _get_selected_music(self):
        """Get selected music track and volume from unified widget."""
        if hasattr(self, 'unified_music_widget'):
            track_path = self.unified_music_widget.get_selected_track()
            volume = self.unified_music_widget.get_volume()
            return track_path, volume
        return None, 0
    
    def _on_add_music_toggled(self, checked):
        """Handle music toggle state change."""
        pass
    
    def _on_music_selected(self, index):
        """Handle music track selection."""
        pass
    
    def _on_music_volume_changed(self, value):
        """Handle music volume change."""
        pass
    
    def _update_music_badge(self, value):
        """Update music volume badge."""
        pass
    
    def _reset_music_player(self):
        """Reset music player state."""
        pass
    
    def _music_eff(self):
        """Get music volume effect value."""
        if hasattr(self, 'unified_music_widget'):
            return self.unified_music_widget.get_volume() / 100.0
        return 0.25
    
    def preview_music_track(self, track_path):
        """Preview a music track (stub for future implementation)."""
        self.logger.info(f"Would preview track: {track_path}")
        QMessageBox.information(self, "Preview", f"Would play preview of: {os.path.basename(track_path)}")

    def _on_unified_music_toggled(self, enabled):
        """Handle unified music widget toggle signal."""
        self.logger.info(f"Unified music toggled: {enabled}")
        if hasattr(self, 'add_music_checkbox'):
            self.add_music_checkbox.setChecked(enabled)
    
    def _on_unified_track_selected(self, track_path):
        """Handle unified music widget track selection signal."""
        self.logger.info(f"Unified track selected: {track_path}")
        if hasattr(self, 'music_combo'):
            if track_path:
                for i in range(self.music_combo.count()):
                    if self.music_combo.itemData(i) == track_path:
                        self.music_combo.setCurrentIndex(i)
                        break
    
    def _on_unified_volume_changed(self, volume):
        """Handle unified music widget volume change signal."""
        self.logger.info(f"Unified volume changed: {volume}")
        if hasattr(self, 'music_volume_slider'):
            self.music_volume_slider.setValue(volume)
    
    def _on_unified_offset_changed(self, offset):
        """Handle unified music widget offset change signal."""
        self.logger.info(f"Unified offset changed: {offset}")
        if hasattr(self, 'music_offset_input'):
            self.music_offset_input.setValue(offset)
    
    def _on_unified_advanced_requested(self):
        """Handle unified music widget advanced dialog request."""
        self.logger.info("Unified advanced dialog requested")

        from utilities.merger_music_dialog import MusicDialog
        dialog = MusicDialog(self)
        if dialog.exec_():
            if hasattr(self, 'unified_music_widget'):
                selected_track = dialog.get_selected_track()
                volume = dialog.get_volume()
                offset = dialog.get_offset()
                if selected_track:
                    self.unified_music_widget.set_selected_track(selected_track)
                    self.unified_music_widget.set_volume(volume)
                    self.unified_music_widget.set_offset(offset)
