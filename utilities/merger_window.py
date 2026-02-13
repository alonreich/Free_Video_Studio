from PyQt5.QtWidgets import QMainWindow, QFileDialog, QApplication, QListWidget, QLabel, QPushButton, QMessageBox, QInputDialog, QShortcut
from PyQt5.QtCore import pyqtSignal, Qt, QTimer, QEvent, QProcess, QThread, QStandardPaths, QMutex, QMutexLocker, QRect, QUrl
from PyQt5.QtGui import QIcon, QKeySequence, QDesktopServices
import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
try:
    from PyQt5.QtWinExtras import QWinTaskbarButton, QWinTaskbarProgress
    _HAS_WIN_EXTRAS = True
except ImportError:
    _HAS_WIN_EXTRAS = False

import math
import tempfile
import os
import sys
import subprocess
import time
import json
import shutil
import psutil
import decimal
from datetime import datetime
from pathlib import Path
from utilities.merger_ui import MergerUI
from utilities.merger_handlers_main import MergerHandlers
from utilities.merger_utils import _get_logger, _human, escape_ffmpeg_path, get_disk_free_space, _ffprobe, build_audio_ducking_filters
from utilities.merger_window_logic import MergerWindowLogic
from utilities.workers import ProbeWorker
from utilities.merger_engine import MergerEngine
from utilities.merger_draggable_list import MergerDraggableList
from utilities.merger_phase_overlay_mixin import MergerPhaseOverlayMixin
from utilities.merger_phase_overlay_logic import MergerPhaseOverlayLogic
from utilities.merger_phase_overlay_draw import MergerPhaseOverlayDraw
from utilities.merger_unified_music_widget import UnifiedMusicWidget
from utilities.merger_music_dialog import MusicDialogHandler

class VideoMergerWindow(QMainWindow, MergerPhaseOverlayMixin, MergerPhaseOverlayLogic, MergerPhaseOverlayDraw):
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
        self.original_duration = 0.0
        self.process = None
        self._pulse_phase = 0
        self._cfg = config_manager.config if config_manager else {}
        self.config_manager = config_manager
        self.logger = _get_logger()
        self.logic_handler = MergerWindowLogic(self)
        self.logic_handler.load_config()
        self.ui_handler = MergerUI(self)
        self.event_handler = MergerHandlers(self)
        self.music_dialog_handler = MusicDialogHandler(self)
        self.engine = None
        self._state_mutex = QMutex()
        self._is_processing = False
        self._is_cancelling = False
        self._status_lock_until = 0.0
        self._temp_dir = None
        self._probe_worker = None
        self.taskbar_button = None
        self.taskbar_progress = None
        self._last_gpu_val = 0
        self._iops_prev = None
        self._iops_dyn_max = 1.0
        self._cleanup_stale_temps()
        self.init_ui()
        self.connect_signals()
        self.setAcceptDrops(True)
        QTimer.singleShot(100, self._scan_mp3_folder)
        self._original_merge_btn_style = """
            QPushButton {
                background-color: #1b6d26;
                color: white;
                font-weight: bold;
                font-size: 12px;
                border-radius: 10px;
            }
            QPushButton:hover { background-color: #22822d; }
            QPushButton:disabled { background-color: #7f8c8d; color: #bdc3c7; }
        """
        self.event_handler.update_button_states()
        self.logger.info("OPEN: Video Merger window created")

    def create_draggable_list_widget(self):
        listw = MergerDraggableList(self)
        if hasattr(listw, "drag_started"):
            listw.drag_started.connect(self.event_handler.on_drag_started)
        if hasattr(listw, "drag_completed"):
            listw.drag_completed.connect(self.event_handler.on_drag_completed)
        return listw

    def add_videos(self):
        self.event_handler.add_videos()

    def remove_selected(self):
        self.logger.info("USER: Clicked REMOVE SELECTED")
        self.event_handler.remove_selected()

    def move_item(self, direction):
        self.event_handler.move_item(direction)

    def return_to_main_app(self):
        self.logger.info("USER: Clicked RETURN TO MENU")
        if self.is_processing:
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Information)
            msg.setWindowTitle("Merge in progress")
            msg.setText("Please cancel the merge first, then return to menu.")
            for btn in msg.findChildren(QPushButton): btn.setCursor(Qt.PointingHandCursor)
            msg.exec_()
            return
        if self.listw.count() > 0 and not self.is_processing:
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Question)
            msg.setWindowTitle("Return to menu")
            msg.setText("You still have videos in the list. Return to menu anyway?")
            msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
            msg.setDefaultButton(QMessageBox.No)
            for btn in msg.findChildren(QPushButton): btn.setCursor(Qt.PointingHandCursor)
            reply = msg.exec_()
            if reply != QMessageBox.Yes:
                return
        self.return_to_main.emit()

    def perform_move(self, from_row, to_row, rebuild_widget=False):
        self.logic_handler.perform_move(from_row, to_row, rebuild_widget)

    def make_item_widget(self, path):
        return self.event_handler.make_item_widget(path)

    def set_ui_busy(self, busy: bool):
        self.btn_add.setEnabled(not busy)
        self.btn_add_folder.setEnabled(not busy)
        self.btn_remove.setEnabled(not busy)
        self.btn_clear.setEnabled(not busy)
        self.btn_merge.setEnabled(not busy)
        self.btn_back.setEnabled(not busy)
        self.listw.setEnabled(not busy)
        undo_enabled = False
        redo_enabled = False
        if (not busy) and hasattr(self, 'event_handler') and hasattr(self.event_handler, 'undo_stack'):
            try:
                undo_enabled = self.event_handler.undo_stack.canUndo()
                redo_enabled = self.event_handler.undo_stack.canRedo()
            except RuntimeError:
                undo_enabled = False
                redo_enabled = False
        if hasattr(self, 'btn_undo'):
            self.btn_undo.setEnabled(undo_enabled)
        if hasattr(self, 'btn_redo'):
            self.btn_redo.setEnabled(redo_enabled)

    def _paint_graph_event(self, event):
        from PyQt5.QtGui import QPainter, QColor
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
            self.logger.debug(f"Graph paint skipped: {ex}")
        finally:
            p.end()

    def setup_progress_visualization(self):
        if not hasattr(self, "_progress_samples"):
            self._progress_samples = []

    def _cleanup_stale_temps(self):
        """Clean up old temp directories from previous crashes (Fix #8, #16)."""
        try:
            tmp = Path(tempfile.gettempdir())
            now = time.time()
            for p in tmp.glob("fvs_merger_*"):
                if p.is_dir() and (p / ".fvs_merger_tmp").exists():
                    try:
                        mtime = p.stat().st_mtime
                        if now - mtime > 1800:
                            shutil.rmtree(p, ignore_errors=True)
                            self.logger.info(f"Cleaned stale temp: {p}")
                    except Exception as ex:
                        self.logger.debug(f"Temp cleanup skip for {p}: {ex}")
        except Exception as e:
            self.logger.warning(f"Temp cleanup failed: {e}")

    def _handle_dropped_files(self, files):
        if self.is_processing:
            return
        allowed = {'.mp4', '.mov', '.mkv', '.m4v', '.ts', '.avi', '.webm'}
        supported = [f for f in files if Path(f).suffix.lower() in allowed]
        skipped = len(files) - len(supported)
        if supported:
            self.event_handler.add_videos_from_list(supported)
            if skipped > 0:
                self.set_status_message(
                    f"Added {len(supported)} file(s). Skipped {skipped} unsupported file(s).",
                    "color: #ffa500;",
                    2500,
                    force=True,
                )
            return
        if files and skipped > 0:
            self.set_status_message(
                "No supported video files were dropped. Supported: mp4/mov/mkv/m4v/ts/avi/webm",
                "color: #ff6b6b;",
                3500,
                force=True,
            )
    @property
    def is_processing(self) -> bool:
        with QMutexLocker(self._state_mutex):
            return self._is_processing

    def set_processing_state(self, value: bool) -> bool:
        with QMutexLocker(self._state_mutex):
            if value:
                if self._is_processing or self._is_cancelling:
                    return False
                self._is_processing = True
                return True
            self._is_processing = False
            if not self._is_processing:
                self._is_cancelling = False
            return True

    def request_cancellation(self) -> bool:
        with QMutexLocker(self._state_mutex):
            if not self._is_processing or self._is_cancelling:
                return False
            self._is_cancelling = True
            return True

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls() and not self.is_processing:
            event.acceptProposedAction()
            return
        event.ignore()

    def dropEvent(self, event):
        if self.is_processing:
            event.ignore()
            return
        urls = event.mimeData().urls() if event.mimeData().hasUrls() else []
        files = [u.toLocalFile() for u in urls if u.isLocalFile()]
        self._handle_dropped_files(files)
        if files:
            event.acceptProposedAction()
        else:
            event.ignore()

    def showEvent(self, event: QEvent):
        if not self._loaded:
            self._loaded = True
            self.logic_handler.load_config()
            if _HAS_WIN_EXTRAS and not self.taskbar_button:
                try:
                    self.taskbar_button = QWinTaskbarButton(self)
                    self.taskbar_button.setWindow(self.windowHandle())
                    self.taskbar_progress = self.taskbar_button.progress()
                except Exception as ex:
                    self.logger.debug(f"Taskbar progress unavailable: {ex}")
        super().showEvent(event)

    def resizeEvent(self, event: QEvent):
        super().resizeEvent(event)
        if hasattr(self, "_resize_overlay"):
            self._resize_overlay()

    def init_ui(self):
        self.setWindowTitle("Video Merger")
        self.resize(1000, 700)

        from PyQt5.QtWidgets import QProgressBar
        self.progress_bar = QProgressBar()
        self.ui_handler.setup_ui()
        self.ui_handler.set_style()
        self._original_merge_btn_style = self.btn_merge.styleSheet()
        self.set_icon()
        self._ensure_overlay_widgets()
        if hasattr(self, 'btn_cancel_merge'):
             self.btn_cancel_merge.clicked.connect(self.cancel_processing)
        else:
             self.btn_cancel_merge = QPushButton("Cancel Merge")
             self.btn_cancel_merge.setObjectName("danger-btn")
             self.btn_cancel_merge.clicked.connect(self.cancel_processing)
             self.btn_cancel_merge.hide()
             self.merge_row.addWidget(self.btn_cancel_merge)
        self._pulse_timer = QTimer(self)
        self._pulse_timer.timeout.connect(self._update_process_button_text)
        if hasattr(self, '_graph'):
            self._graph.paintEvent = self._paint_graph_event
        self.setup_progress_visualization()
        self.setup_keyboard_shortcuts()
        
    def setup_keyboard_shortcuts(self):
        self.merge_shortcut = QShortcut(QKeySequence("Ctrl+Return"), self)
        self.merge_shortcut.activated.connect(self._shortcut_merge)
        self.add_shortcut = QShortcut(QKeySequence("Ctrl+O"), self)
        self.add_shortcut.activated.connect(self._shortcut_add)
        self.move_up_shortcut = QShortcut(QKeySequence("Ctrl+Up"), self)
        self.move_up_shortcut.activated.connect(lambda: self._shortcut_move(-1))
        self.move_down_shortcut = QShortcut(QKeySequence("Ctrl+Down"), self)
        self.move_down_shortcut.activated.connect(lambda: self._shortcut_move(1))

    def _is_ui_busy_for_actions(self) -> bool:
        return bool(self.is_processing or getattr(self.event_handler, "_loading_lock", False))

    def _shortcut_merge(self):
        if self._is_ui_busy_for_actions():
            return
        self.on_merge_clicked()

    def _shortcut_add(self):
        if self._is_ui_busy_for_actions():
            return
        self.add_videos()

    def _shortcut_move(self, direction: int):
        if self._is_ui_busy_for_actions():
            return
        self.move_item(direction)

    def set_icon(self):
        try:
            _proj_root_path = Path(self.base_dir) if self.base_dir else Path(__file__).resolve().parents[1]
            icon_path = _proj_root_path / "icons" / "Video_Icon_File.ico"
            if not icon_path.exists():
                icon_path = _proj_root_path / "icons" / "app_icon.ico"
            if icon_path.exists():
                self.setWindowIcon(QIcon(str(icon_path)))
        except Exception:
            pass

    def connect_signals(self):
        """Connect all signals with deduplication."""
        self.event_handler.setup_list_connections()
        self.listw.itemSelectionChanged.connect(self.event_handler.update_button_states)
        self.listw.itemSelectionChanged.connect(self.event_handler.refresh_selection_highlights)
        self.status_updated.connect(self.handle_status_update)
        self.listw.model().rowsInserted.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.on_list_cleared)
        self.listw.model().rowsMoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsMoved.connect(self.event_handler.on_rows_moved)
        self.btn_add.clicked.connect(self.add_videos)
        self.btn_add_folder.clicked.connect(self.event_handler.add_folder)
        self.btn_remove.clicked.connect(self.remove_selected)
        self.btn_clear.clicked.connect(self.confirm_clear_list)
        self.listw.itemSelectionChanged.connect(self.event_handler.on_selection_changed)

    def _open_music_advanced_dialog(self):
        try:
            track = self.unified_music_widget.get_selected_track()
            if not track:
                QMessageBox.information(self, "Select music", "Please select at least one track first.")
                return
            self.music_dialog_handler.show_music_offset_dialog(track)
        except Exception as ex:
            self.logger.error(f"Failed to open advanced music dialog: {ex}")
            self.set_status_message("Could not open music editor.", "color: #ff6b6b;", 2500, force=True)

    def confirm_clear_list(self):
        if self.listw.count() > 0:
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Question)
            msg.setWindowTitle('Confirm Clear')
            msg.setText("Are you sure you want to remove all videos from the list?")
            msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
            msg.setDefaultButton(QMessageBox.No)
            for btn in msg.findChildren(QPushButton): btn.setCursor(Qt.PointingHandCursor)
            reply = msg.exec_()
            if reply == QMessageBox.Yes:
                self.logger.info("USER: Confirmed CLEAR ALL")
                self.event_handler.clear_all()
            else:
                self.logger.info("USER: Cancelled CLEAR ALL")

    def closeEvent(self, event):
        self._stop_all_workers()
        if self.config_manager:
            self.logic_handler.save_config()
        if self.vlc_instance:
             try: 
                 self.vlc_instance.release()
             except Exception as ex:
                 self.logger.debug(f"VLC release skipped: {ex}")
        super().closeEvent(event)

    def _stop_all_workers(self):
        """Safely stops all background threads before exit."""
        if hasattr(self, '_stats_worker') and self._stats_worker:
            try:
                self._stats_worker.stop()
                self._stats_worker = None
            except Exception as ex:
                self.logger.debug(f"Stats worker stop skip: {ex}")
        if self.engine and self.engine.isRunning():
            try:
                self.engine.cancel()
                self.engine.wait(2000)
            except Exception as ex:
                self.logger.debug(f"Engine stop skip: {ex}")
        if self._probe_worker and self._probe_worker.isRunning():
            try:
                self._probe_worker.cancel()
                self._probe_worker.wait(1500)
            except Exception as ex:
                self.logger.debug(f"Probe worker stop skip: {ex}")
        loader = getattr(getattr(self, "event_handler", None), "_loader", None)
        if loader and loader.isRunning():
            try:
                loader.cancel()
                loader.wait(2000)
            except Exception as ex:
                self.logger.debug(f"Loader stop skip: {ex}")

    def handle_status_update(self, msg: str):
        self.set_status_message(f"Processing... {msg}", "color: #43b581; font-weight: normal;", 1500)

    def set_status_message(self, msg: str, style: str | None = None, lock_ms: int = 0, force: bool = False):
        if not force and self.is_status_locked():
            return
        if style:
            self.status_label.setStyleSheet(style)
        self.status_label.setText(msg)
        if lock_ms > 0:
            self._status_lock_until = time.time() + (lock_ms / 1000.0)
        else:
            self._status_lock_until = 0.0

    def is_status_locked(self) -> bool:
        return time.time() < self._status_lock_until

    def on_list_cleared(self):
        if self.listw.count() == 0:
            self._reset_music_player()

    def on_merge_clicked(self):
        self.start_merge_processing()

    def _human_time(self, seconds: float) -> str:
        try:
            total = max(0, int(round(float(seconds))))
        except Exception:
            total = 0
        h = total // 3600
        m = (total % 3600) // 60
        s = total % 60
        return f"{h:02}:{m:02}:{s:02}"

    def estimate_total_duration_seconds(self) -> float:
        total = 0.0
        for i in range(self.listw.count()):
            try:
                it = self.listw.item(i)
                probe_data = it.data(Qt.UserRole + 1) or {}
                dur = float((probe_data.get("format") or {}).get("duration") or 0.0)
                total += max(0.0, dur)
            except Exception:
                continue
        try:
            if hasattr(self, "unified_music_widget"):
                self.unified_music_widget.set_video_total_seconds(total)
        except Exception:
            pass
        return total

    def estimate_total_duration_text(self) -> str:
        total = self.estimate_total_duration_seconds()
        return self._human_time(total) if total > 0 else ""

    def _probe_media_duration(self, path: str) -> float:
        try:
            ffprobe = _ffprobe(self.ffmpeg)
            cmd = [
                ffprobe,
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path,
            ]
            flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
            r = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags, timeout=6)
            if r.returncode == 0 and r.stdout:
                return max(0.0, float(r.stdout.strip()))
        except Exception:
            pass
        return 0.0

    def _estimate_required_output_bytes(self, video_files: list[str]) -> int:
        total_in = 0
        for p in video_files:
            try:
                total_in += os.path.getsize(p)
            except Exception:
                continue
        return max(int(total_in * 1.05), 300 * 1024 * 1024)

    def _collect_preflight_warnings(self) -> list[str]:
        warnings = []
        for i in range(self.listw.count()):
            it = self.listw.item(i)
            p = it.data(Qt.UserRole)
            probe_data = it.data(Qt.UserRole + 1) or {}
            streams = probe_data.get("streams") or []
            has_video = any((s.get("codec_type") == "video") or (s.get("width") and s.get("height")) for s in streams)
            if not has_video:
                warnings.append(f"Row {i+1}: {os.path.basename(str(p))} (video stream not detected)")
        return warnings

    def _get_next_output_path(self):
        r"""
        Get the output path with forced naming convention (Fix #12).
        Folder: ..\!!!_Output_Video_Files_!!!\ (Relative to utilities, so Project Root)
        Name: Merged-Videos-X.mp4
        """
        try:
            base_path = Path(self.base_dir).resolve() if self.base_dir else Path.cwd().parent
            output_dir = base_path / "!!!_Output_Video_Files_!!!"
            output_dir.mkdir(parents=True, exist_ok=True)
            i = 1
            while True:
                name = f"Merged-Videos-{i}.mp4"
                p = output_dir / name
                if not p.exists():
                    return str(p)
                i += 1
        except Exception as e:
            self.logger.error(f"Path generation failed: {e}")
            fallback_dir = Path(self.base_dir).resolve() if self.base_dir else Path.cwd()
            return str((fallback_dir / f"Merged-Videos-{int(time.time())}.mp4").resolve())

    def start_merge_processing(self):
        if not self.set_processing_state(True):
            return
        if getattr(self.event_handler, "_loading_lock", False):
            self.set_processing_state(False)
            QMessageBox.information(self, "Please wait", "Still loading files. Please wait until loading finishes.")
            return
        n = self.listw.count()
        if n < 1:
            QMessageBox.information(self, "Need a video", "Please add at least 1 video to merge.")
            self.set_processing_state(False)
            return
        video_files = []
        for i in range(n):
            it = self.listw.item(i)
            video_files.append(it.data(Qt.UserRole))
        preflight_warnings = self._collect_preflight_warnings()
        if preflight_warnings:
            preview = "\n".join(preflight_warnings[:5])
            if len(preflight_warnings) > 5:
                preview += f"\n...and {len(preflight_warnings)-5} more"
            reply = QMessageBox.question(
                self,
                "Potential file compatibility issues",
                f"Some items may fail during merge:\n\n{preview}\n\nContinue anyway?",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.No,
            )
            if reply != QMessageBox.Yes:
                self.set_processing_state(False)
                return
        music_enabled = bool(self.unified_music_widget.toggle_button.isChecked())
        music_tracks = self.unified_music_widget.get_selected_tracks() if hasattr(self.unified_music_widget, "get_selected_tracks") else []
        music_path = music_tracks[0] if music_tracks else self.unified_music_widget.get_selected_track()
        music_offset = self.unified_music_widget.get_offset()
        if music_enabled and not music_tracks:
            QMessageBox.information(self, "Select music", "Music is enabled, but no track is selected.")
            self.set_processing_state(False)
            return
        if music_tracks:
            total_video = self.estimate_total_duration_seconds()
            try:
                self.unified_music_widget.update_coverage_guidance(total_video, self._probe_media_duration)
            except Exception:
                pass
            music_unique_total = 0.0
            for t in music_tracks:
                music_unique_total += self._probe_media_duration(t)
            if total_video > 0 and music_unique_total < total_video:
                missing = self._human_time(total_video - music_unique_total)
                reply = QMessageBox.question(
                    self,
                    "Music Coverage Warning",
                    f"Your selected music ({self._human_time(music_unique_total)}) is shorter than all videos ({self._human_time(total_video)}).\n\n"
                    f"You need about {missing} more music for full coverage.\n\n"
                    "If you continue, the music will repeat automatically.\n"
                    "Do you want to proceed?",
                    QMessageBox.Yes | QMessageBox.No,
                    QMessageBox.No,
                )
                if reply != QMessageBox.Yes:
                    self.set_processing_state(False)
                    return
            if total_video > 0 and music_unique_total > total_video + 30:
                pass
            mus_dur = self._probe_media_duration(music_path)
            if mus_dur > 0 and music_offset >= max(0.0, mus_dur - 0.1):
                QMessageBox.warning(
                    self,
                    "Music start is too late",
                    "The selected music start offset is at/after the end of the track.\n"
                    "Please reduce the Start value.",
                )
                self.set_processing_state(False)
                return
        self._output_path = self._get_next_output_path()
        free_bytes = get_disk_free_space(os.path.dirname(os.path.abspath(self._output_path)))
        req_bytes = self._estimate_required_output_bytes(video_files)
        if free_bytes < req_bytes:
            if free_bytes < (req_bytes * 0.5):
                QMessageBox.critical(
                    self,
                    "Critically Low Disk Space",
                    f"You only have {_human(free_bytes)} available, but need at least {_human(req_bytes)}.\n"
                    "Please free up space to continue."
                )
                self.set_processing_state(False)
                return
            reply = QMessageBox.question(
                self,
                "Low Disk Space",
                f"Estimated required space: {_human(req_bytes)}\n"
                f"Available space: {_human(free_bytes)}\n\n"
                "Merge might fail if the output is larger than expected. Continue?",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.No,
            )
            if reply != QMessageBox.Yes:
                self.set_processing_state(False)
                return
        est_text = self.estimate_total_duration_text()
        if est_text:
            self.set_status_message(f"Preparing merge. Estimated output length: {est_text}", "color: #43b581;", 2000, force=True)
        self._show_processing_overlay()
        self._pulse_timer.start(250)
        self.btn_merge.hide()
        self.btn_cancel_merge.show()
        self.btn_cancel_merge.setCursor(Qt.PointingHandCursor)
        self.event_handler.update_button_states()
        self.logic_handler.request_save_config()
        self.set_status_message("Analyzing files...", "color: #43b581;", 0, force=True)
        self._probe_worker = ProbeWorker(video_files, self.ffmpeg)
        self._probe_worker.finished.connect(lambda r, t: self._validate_and_finalize(r, t))
        self._probe_worker.error.connect(lambda e: self._merge_finished_cleanup(False, e))
        self._probe_worker.start()

    def _validate_and_finalize(self, results, total_duration):
        """
        Validate resolution consistency and audio presence (Fix #1, #2).
        Calculates peak quality targets to match original media.
        """
        if not self.is_processing: return
        result_by_path = {r.get("path"): r for r in (results or []) if isinstance(r, dict)}
        video_files = []
        first_res = None
        normalize_video = False
        has_audio_input = False
        peak_v_bitrate = 0
        peak_a_bitrate = 0
        peak_a_rate = 44100
        for i in range(self.listw.count()):
            it = self.listw.item(i)
            path = it.data(Qt.UserRole)
            video_files.append(path)
            info = result_by_path.get(path)
            if not info:
                self._merge_finished_cleanup(False, f"Probe data missing for file: {path}")
                return
            peak_v_bitrate = max(peak_v_bitrate, info.get("video_bitrate", 0))
            peak_a_bitrate = max(peak_a_bitrate, info.get("audio_bitrate", 0))
            peak_a_rate = max(peak_a_rate, info.get("audio_rate", 0))
            res = info.get("resolution")
            if not res or len(res) != 2 or not all(isinstance(v, int) and v > 0 for v in res):
                self._merge_finished_cleanup(False, f"Could not determine video resolution for: {path}")
                return
            if first_res is None:
                first_res = tuple(res)
            elif tuple(res) != first_res:
                normalize_video = True
            has_audio_input = has_audio_input or bool(info.get("has_audio"))
        if peak_v_bitrate == 0: peak_v_bitrate = 8000000
        if peak_a_bitrate == 0: peak_a_bitrate = 192000
        if peak_a_rate == 0: peak_a_rate = 48000
        self._finalize_merge_setup(video_files, total_duration, has_audio_input, first_res, normalize_video, peak_v_bitrate, peak_a_bitrate, peak_a_rate)

    def _finalize_merge_setup(self, video_files, total_duration=0.0, has_audio_input=False, target_resolution=None, normalize_video=False, target_v_bitrate=0, target_a_bitrate=0, target_a_rate=48000):
        if not self.is_processing: return
        self._temp_dir = tempfile.TemporaryDirectory(prefix="fvs_merger_")
        try:
            Path(self._temp_dir.name, ".fvs_merger_tmp").write_text("fvs", encoding="utf-8")
        except Exception as e:
            self._merge_finished_cleanup(False, f"Failed to init temp dir: {e}")
            return
        wizard_tracks = []
        if hasattr(self.unified_music_widget, "get_wizard_tracks"):
            wizard_tracks = self.unified_music_widget.get_wizard_tracks()
        music_vol = self.unified_music_widget.get_volume()
        video_vol = self.unified_music_widget.get_video_volume()
        cmd = [self.ffmpeg, "-y"]
        filters = []
        if normalize_video:
            tw, th = int(target_resolution[0]), int(target_resolution[1])
            for i, path in enumerate(video_files):
                cmd.extend(["-i", path])
                filters.append(
                    f"[{i}:v]scale={tw}:{th}:force_original_aspect_ratio=decrease,"
                    f"pad={tw}:{th}:(ow-iw)/2:(oh-ih)/2,setsar=1[v{i}]"
                )
            v_inputs = "".join(f"[v{i}]" for i in range(len(video_files)))
            filters.append(f"{v_inputs}concat=n={len(video_files)}:v=1:a=0[v_out]")
            map_video = "[v_out]"
            if has_audio_input:
                a_inputs = "".join(f"[{i}:a]" for i in range(len(video_files)))
                filters.append(f"{a_inputs}concat=n={len(video_files)}:v=0:a=1,volume={video_vol/100.0}[a_serial]")
                map_audio = "[a_serial]"
            else:
                map_audio = None
        else:
            concat_txt = Path(self._temp_dir.name, "concat_list.txt")
            with concat_txt.open("w", encoding="utf-8") as f:
                f.write("ffconcat version 1.0\n")
                for path in video_files:
                    f.write(f"file '{escape_ffmpeg_path(path)}'\n")
            cmd.extend(["-f", "concat", "-safe", "0", "-i", str(concat_txt)])
            map_video = "0:v"
            if has_audio_input:
                filters.append(f"[0:a]volume={video_vol/100.0}[a_vid_vol]")
                map_audio = "[a_vid_vol]"
            else:
                map_audio = None
        if wizard_tracks:
            music_start_idx = len(video_files) if normalize_video else 1
            fadeout_lead = 7.0
            crossfade_sec = 3.0
            expanded_tracks = []
            covered = 0.0
            for path, offset, dur in wizard_tracks:
                expanded_tracks.append((path, offset, dur))
                if len(expanded_tracks) == 1: covered += dur
                else: covered += max(0.0, dur - crossfade_sec)
            cycle_guard = 0
            while covered < max(0.1, float(total_duration)) and cycle_guard < 32 and len(expanded_tracks) < 96:
                path, _, _ = wizard_tracks[-1]
                dur = self._probe_media_duration(path)
                expanded_tracks.append((path, 0.0, dur))
                covered += max(0.0, dur - crossfade_sec)
                cycle_guard += 1
            for t, _, _ in expanded_tracks:
                cmd.extend(["-i", t])
            music_inputs = []
            for idx, (track_path, start_sec, dur) in enumerate(expanded_tracks):
                in_idx = music_start_idx + idx
                out_label = f"m_{idx}"
                fade_start = max(0.0, dur - fadeout_lead)
                if idx == 0:
                    filters.append(
                        f"[{in_idx}:a]atrim=start={start_sec},asetpts=PTS-STARTPTS,volume={music_vol/100.0},afade=t=in:d=3,{f'afade=t=out:st={fade_start}:d={fadeout_lead}' if dur > fadeout_lead else ''}[{out_label}]"
                    )
                else:
                    filters.append(
                        f"[{in_idx}:a]atrim=start={start_sec},asetpts=PTS-STARTPTS,volume={music_vol/100.0},{f'afade=t=out:st={fade_start}:d={fadeout_lead}' if dur > fadeout_lead else ''}[{out_label}]"
                    )
                music_inputs.append(out_label)
            music_out = music_inputs[0]
            for i in range(1, len(music_inputs)):
                next_label = f"m_xf_{i}"
                filters.append(f"[{music_out}][{music_inputs[i]}]acrossfade=d={crossfade_sec}:c1=tri:c2=tri[{next_label}]")
                music_out = next_label
            filters.append(f"[{music_out}]atrim=duration={max(0.1, float(total_duration))}[mus]")
            ducking_filters = build_audio_ducking_filters(
                video_audio_stream=map_audio or "anullsrc=channel_layout=stereo:sample_rate=48000",
                music_stream="[mus]",
                music_volume=1.0, 
                sample_rate=target_a_rate,
                video_has_audio=has_audio_input
            )
            filters.extend(ducking_filters)
            map_audio = "[a_out]"
        if filters:
            filter_script_path = Path(self._temp_dir.name, "filter_complex.txt")
            with open(filter_script_path, "w", encoding="utf-8") as f:
                f.write(";".join(filters))
            cmd.extend(["-filter_complex_script", str(filter_script_path)])
        cmd.extend(["-map", map_video])
        if map_audio: cmd.extend(["-map", map_audio])
        self.engine = MergerEngine(self.ffmpeg, cmd, self._output_path, total_duration, use_gpu=True, target_v_bitrate=target_v_bitrate, target_a_bitrate=target_a_bitrate, target_a_rate=target_a_rate)
        self.engine.progress.connect(self._update_progress)
        self.engine.log_line.connect(self._append_log)
        self.engine.finished.connect(self._merge_finished_cleanup)
        if self.taskbar_progress:
            self.taskbar_progress.setValue(0); self.taskbar_progress.setVisible(True)
        self.engine.start()

    def _update_progress(self, percent, time_str):
        self.set_status_message(f"Merging: {percent}% ({time_str})", "color: #43b581;", force=True)
        if hasattr(self, '_graph'): 
            self._sample_perf_counters_safe()
        self.setWindowTitle(f"Video Merger - {percent}%")
        if self.taskbar_progress:
            self.taskbar_progress.setValue(percent)

    def _append_log(self, line):
        self._append_live_log(str(line))

    def cancel_processing(self):
        if self.request_cancellation():
            self.logger.info("USER: Clicked CANCEL MERGE")
            self.set_status_message("Cancelling...", "color: #ffa500;", force=True)
            if self.engine and self.engine.isRunning():
                self.engine.cancel()
            if self._probe_worker and self._probe_worker.isRunning():
                 self._probe_worker.cancel()
                 self._probe_worker.wait(1200)
            if self.taskbar_progress:
                self.taskbar_progress.setPaused(True)

    def _merge_finished_cleanup(self, success, result_msg):
        with QMutexLocker(self._state_mutex):
            self._is_processing = False
            self._is_cancelling = False
        self._pulse_timer.stop()
        self._hide_processing_overlay()
        self.setWindowTitle("Video Merger")
        if self.taskbar_progress:
            self.taskbar_progress.setVisible(False)
            self.taskbar_progress.setValue(0)
        if self._temp_dir:
            try:
                self._temp_dir.cleanup()
            except Exception as ex:
                self.logger.debug(f"Temp dir cleanup skip: {ex}")
            self._temp_dir = None
        self.btn_cancel_merge.hide()
        self.btn_merge.show()
        self.event_handler.update_button_states()
        if success:
            self.event_handler.show_success_dialog(result_msg)
            self.set_status_message("Merge Complete!", "color: #43b581; font-weight: bold;", 5000, force=True)
        else:
            if "Cancelled" not in result_msg:
                 friendly = "Merge failed. Please check input files and available disk space."
                 msg = QMessageBox(self)
                 msg.setIcon(QMessageBox.Critical)
                 msg.setWindowTitle("Merge Failed")
                 msg.setText(f"{friendly}\n\nDetails:\n{result_msg}")
                 for btn in msg.findChildren(QPushButton): btn.setCursor(Qt.PointingHandCursor)
                 msg.exec_()
            self.set_status_message(f"Failed: {result_msg}", "color: #ff6b6b;", 5000, force=True)
            
    def _update_process_button_text(self):
        if not self.btn_cancel_merge.isVisible(): return
        dots = "." * (self._pulse_phase % 4)
        self.btn_cancel_merge.setText(f"Cancel Merge{dots}")
        self._pulse_phase += 1
        
    def _scan_mp3_folder(self):
        """Initial music scan."""
        try:
            mp3_dir = os.path.join(self.base_dir, "mp3") if self.base_dir else "mp3"
            self.unified_music_widget.load_tracks(mp3_dir)
        except Exception:
            pass
            
    def _reset_music_player(self):
        try:
            if hasattr(self, "unified_music_widget") and self.unified_music_widget:
                self.unified_music_widget.clear_playlist()
                self.unified_music_widget.toggle_button.setChecked(False)
                self.set_status_message("Music reset because list is empty.", "color: #7289da;", 1200, force=True)
        except Exception as ex:
            self.logger.debug(f"Music reset skipped: {ex}")
