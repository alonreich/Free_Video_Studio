from PyQt5.QtWidgets import QMainWindow, QFileDialog, QApplication, QListWidget, QLabel, QPushButton, QMessageBox, QWidget
from PyQt5.QtCore import pyqtSignal, Qt, QTimer, QEvent, QProcess, QThread, QStandardPaths, QMutex, QMutexLocker, QMimeData, QUrl
from PyQt5.QtGui import QIcon, QPainter, QPixmap, QFont, QColor, QPen, QBrush, QDragEnterEvent, QDropEvent
from PyQt5.QtCore import QRect
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
from utilities.merger_utils import _get_logger, _human
from utilities.merger_window_logic import MergerWindowLogic
from utilities.merger_ffmpeg import MergerEngine
from utilities.merger_draggable_list import MergerDraggableList
from utilities.merger_phase_overlay_mixin import MergerPhaseOverlayMixin
from utilities.merger_unified_music_widget import UnifiedMusicWidget

class ConfigManagerAdapter:
    def __init__(self, merger_window_instance):
        self.window = merger_window_instance
    @property
    def config(self):
        return self.window._cfg
    
    def save_config(self, cfg_data):
        self.window._cfg.update(cfg_data)

class VideoMergerWindow(QMainWindow, MergerPhaseOverlayMixin):
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
        self.logger = _get_logger()
        self.engine = MergerEngine(self.ffmpeg, self.logger)
        self.engine.progress_updated.connect(self.update_progress)
        self.engine.status_message.connect(self.handle_status_update)
        self.engine.finished.connect(self._on_merge_finished)
        self.engine.log_output.connect(self._append_live_log)
        self.config_manager = ConfigManagerAdapter(self)
        self.ui_handler = MergerUI(self)
        self.event_handler = MergerHandlers(self)
        self.logic_handler = MergerWindowLogic(self)
        self._is_processing = False
        self._pulse_phase = 0
        self._status_lock_until = 0.0
        self.init_ui()
        self.connect_signals()
        self._scan_mp3_folder()
        self.event_handler.update_button_states()
        self.logger.info("OPEN: Video Merger window created")
        self.setAcceptDrops(True)
    @property
    def is_processing(self) -> bool:
        return self._is_processing

    def showEvent(self, event: QEvent):
        if not self._loaded:
            self._loaded = True
            self.logic_handler.load_config()
        super().showEvent(event)

    def resizeEvent(self, event: QEvent):
        super().resizeEvent(event)
        self._resize_overlay()

    def dragEnterEvent(self, event: QDragEnterEvent):
        if event.mimeData().hasUrls():
            urls = event.mimeData().urls()
            valid = any(self.validate_file_path(url.toLocalFile()) for url in urls if url.isLocalFile())
            if valid:
                event.acceptProposedAction()

    def dropEvent(self, event: QDropEvent):
        urls = event.mimeData().urls()
        files = []
        for url in urls:
            if url.isLocalFile():
                path = url.toLocalFile()
                if self.validate_file_path(path):
                    files.append(path)
        if files:
            self._handle_dropped_files(files)
        event.acceptProposedAction()

    def _handle_dropped_files(self, files):
        if self._is_processing:
            return
        current_count = self.listw.count()
        if current_count + len(files) > self.MAX_FILES:
            msg = f"Cannot add {len(files)} files. Limit is {self.MAX_FILES}."
            self.logger.warning(f"USER ACTION BLOCKED: {msg}")
            QMessageBox.warning(self, "Limit reached", msg)
            return
        self.logger.info(f"USER ACTION: Drag-and-drop added {len(files)} files.")
        if hasattr(self.event_handler, "add_videos_from_list"):
            self.event_handler.add_videos_from_list(files)
        else:
            self.event_handler.add_videos_from_list(files)

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
        def safe_disconnect(signal):
            try:
                if signal: signal.disconnect()
            except: pass
        safe_disconnect(getattr(self.listw, 'itemSelectionChanged', None))
        safe_disconnect(getattr(self.btn_add, 'clicked', None) if hasattr(self, 'btn_add') else None)
        self.listw.itemSelectionChanged.connect(self.event_handler.update_button_states)
        self.listw.itemSelectionChanged.connect(self.event_handler.on_selection_changed)
        self.listw.model().rowsInserted.connect(self.event_handler.update_button_states)
        self.listw.model().rowsRemoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsMoved.connect(self.event_handler.update_button_states)
        self.btn_add.clicked.connect(self.add_videos)
        self.btn_remove.clicked.connect(self.remove_selected)
        self.btn_clear.clicked.connect(self.confirm_clear_list)
        if hasattr(self, 'unified_music_widget'):
            self.unified_music_widget.music_toggled.connect(self._on_unified_music_toggled)
            self.unified_music_widget.track_selected.connect(self._on_unified_track_selected)
            self.unified_music_widget.volume_changed.connect(self._on_unified_volume_changed)
            self.unified_music_widget.offset_changed.connect(self._on_unified_offset_changed)
            self.unified_music_widget.advanced_requested.connect(self._on_unified_advanced_requested)
        if hasattr(self, 'add_music_checkbox'):
            self.add_music_checkbox.toggled.connect(self._on_add_music_toggled)

    def confirm_clear_list(self):
        if self.listw.count() > 0:
            self.logger.info(f"USER_ACTION: User cleared the entire list of {self.listw.count()} video(s)")
            self.set_status_message("List cleared", "color: #ff6b6b; font-weight: bold;", 2000)
            if hasattr(self.event_handler, "clear_all"):
                self.event_handler.clear_all()
            else:
                self.listw.clear()

    def closeEvent(self, event):
        self.logic_handler.save_config()
        if self.vlc_instance:
            try: self.vlc_instance.release()
            except: pass
        super().closeEvent(event)

    def handle_status_update(self, msg: str):
        self.set_status_message(msg, "color: #43b581; font-weight: normal;")

    def set_status_message(self, msg: str, style: str | None = None, lock_ms: int = 0):
        if self.is_status_locked() and lock_ms == 0:
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

    def on_merge_clicked(self):
        self.start_merge_processing()

    def start_merge_processing(self):
        n = self.listw.count()
        if n < 2:
            QMessageBox.information(self, "Need more videos", "Please add at least 2 videos to merge.")
            return
        video_files = []
        for i in range(n):
            it = self.listw.item(i)
            video_files.append(it.data(Qt.UserRole))
        default_name = f"Merged_Video_{datetime.now().strftime('%Y%m%d_%H%M%S')}.mp4"
        start_dir = self.logic_handler.get_last_out_dir()
        save_path, _ = QFileDialog.getSaveFileName(self, "Save Merged Video", 
                                                str(Path(start_dir) / default_name), 
                                                "MP4 Video (*.mp4)")
        if not save_path:
            self.logger.info("USER ACTION: Cancelled merge at save dialog.")
            return
        self.logger.info(f"USER ACTION: Confirmed merge destination: '{save_path}'. Processing {n} input files.")
        self.logic_handler.set_last_out_dir(str(Path(save_path).parent))
        self.logic_handler.save_config()
        music_path, music_vol = self._get_selected_music()
        music_config = {}
        if music_path:
            self.logger.info(f"SYSTEM: Configuring background music: '{os.path.basename(music_path)}' at volume {music_vol}%.")
            music_config = {
                'path': music_path,
                'volume': music_vol,
                'offset': self.music_offset_input.value() if hasattr(self, 'music_offset_input') else 0.0
            }
        self._is_processing = True
        self.engine.start_merge(video_files, save_path, music_config)
        self._show_processing_overlay()
        self._pulse_timer.start(250)
        self.btn_merge.hide()
        self.btn_cancel_merge.show()
        self.set_ui_busy(True)
        self.event_handler.update_button_states()

    def _on_merge_finished(self, success, result_msg):
        self._is_processing = False
        self._pulse_timer.stop()
        self._hide_processing_overlay()
        self.btn_merge.setText("Merge Videos")
        self.btn_merge.show()
        self.btn_cancel_merge.hide()
        self.set_ui_busy(False)
        self.event_handler.update_button_states()
        self.update_progress(0, "")
        if success:
            self.logger.info("SYSTEM: Merge operation completed successfully.")
            self.show_success_dialog(result_msg)
        else:
            self.logger.error(f"SYSTEM: Merge operation failed or was cancelled. Details: {result_msg}")
            if "Cancelled" in result_msg:
                self.set_status_message("Merge cancelled.", "color: #ff6b6b;", 3000)
            else:
                QMessageBox.critical(self, "Merge Failed", f"Error: {result_msg}")

    def update_progress(self, percent: int, message: str = ""):
        """Update progress UI elements and status message."""
        try:
            if hasattr(self, "progress_bar"):
                self.progress_bar.setValue(int(percent))
            if message:
                self.set_status_message(message)
        except Exception as ex:
            self.logger.debug(f"Failed to update progress: {ex}")

    def _update_process_button_text(self) -> None:
        if not self._is_processing: return
        self._pulse_phase = (self._pulse_phase + 1) % 4
        self.btn_merge.setText(f"Merging{'.' * (self._pulse_phase + 1)}")

    def cancel_processing(self):
        self.logger.warning("USER ACTION: Requested cancellation of merge process.")
        self.engine.cancel()
        self.set_status_message("Cancelling...", "color: #ff6b6b; font-weight: bold;")

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
        listw = MergerDraggableList(self)
        listw.logger = self.logger
        listw.setAlternatingRowColors(False)
        listw.setSpacing(6)
        listw.setSelectionMode(QListWidget.ExtendedSelection)
        if not getattr(listw, "_use_native_drag", False):
            listw.drag_started.connect(lambda i, f: self.logger.info(f"USER INTERACTION: Drag gesture initiated on item {i+1} ('{os.path.basename(f)}')."))
            listw.drag_completed.connect(lambda s, e, f, t: self.logger.info(f"USER INTERACTION: Drag completed. Item moved from {s+1} to {e+1}."))
            if hasattr(self.event_handler, "on_drag_completed"):
                listw.drag_completed.connect(self.event_handler.on_drag_completed)
            listw.item_moved_signal.connect(self.logic_handler.perform_move)
        else:
            listw.model().rowsMoved.connect(self.event_handler.on_rows_moved)
        return listw

    def set_ui_busy(self, is_busy: bool):
        self.btn_back.setDisabled(is_busy)
        self.listw.setEditTriggers(QListWidget.NoEditTriggers if is_busy else QListWidget.DoubleClicked)

    def show_success_dialog(self, output_path):
        self.status_label.setText(f"Done! Saved to: {os.path.basename(output_path)}")
        self.status_label.setStyleSheet("color: #43b581; font-weight: bold;")
        msg = QMessageBox(self)
        msg.setWindowTitle("Merge Complete")
        msg.setText(f"Video saved successfully!\n\n{os.path.basename(output_path)}")
        msg.setIcon(QMessageBox.Information)
        open_btn = msg.addButton("Open Folder", QMessageBox.ActionRole)
        play_btn = msg.addButton("Play Video", QMessageBox.ActionRole)
        close_btn = msg.addButton("Close", QMessageBox.RejectRole)
        msg.exec_()
        if msg.clickedButton() == open_btn:
            try:
                self.logger.info("USER ACTION: Opened output folder.")
                if sys.platform == 'win32': os.startfile(Path(output_path).parent)
                elif sys.platform == 'darwin': subprocess.run(['open', Path(output_path).parent])
                else: subprocess.run(['xdg-open', Path(output_path).parent])
            except: pass
        elif msg.clickedButton() == play_btn:
            try:
                self.logger.info("USER ACTION: Opened output video.")
                if sys.platform == 'win32': os.startfile(output_path)
                elif sys.platform == 'darwin': subprocess.run(['open', output_path])
                else: subprocess.run(['xdg-open', output_path])
            except: pass

    def make_item_widget(self, path):
        return self.event_handler.make_item_widget(path)

    def can_anim(self, row, new_row):
        return self.logic_handler.can_anim(row, new_row)

    def start_swap_animation(self, row, new_row):
        return self.logic_handler.start_swap_animation(row, new_row)

    def perform_swap(self, row, new_row):
        return self.logic_handler.perform_swap(row, new_row)

    def perform_move(self, from_row, to_row, rebuild_widget: bool = False):
        return self.logic_handler.perform_move(from_row, to_row, rebuild_widget=rebuild_widget)

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
        except: pass
        finally: p.end()

    def setup_progress_visualization(self):
        from PyQt5.QtWidgets import QWidget

    def setup_keyboard_shortcuts(self):
        """Placeholder for keyboard shortcut setup (missing implementation guard)."""
        return

    def setup_standard_tooltips(self):
        """Placeholder for tooltip setup (missing implementation guard)."""
        return

    def _on_unified_music_toggled(self, *args, **kwargs):
        """Fallback handler to avoid missing callback crashes."""
        return

    def _on_unified_track_selected(self, *args, **kwargs):
        """Fallback handler to avoid missing callback crashes."""
        return

    def _on_unified_volume_changed(self, *args, **kwargs):
        """Fallback handler to avoid missing callback crashes."""
        return

    def _on_unified_offset_changed(self, *args, **kwargs):
        """Fallback handler to avoid missing callback crashes."""
        return

    def _on_unified_advanced_requested(self, *args, **kwargs):
        """Fallback handler to avoid missing callback crashes."""
        return

    def _on_add_music_toggled(self, *args, **kwargs):
        """Fallback handler to avoid missing callback crashes."""
        return

    def _scan_mp3_folder(self):
        """Fallback scan to avoid startup crash if music scanning is missing."""
        return

    def _get_selected_music(self):
        """Fallback music selection (no music)."""
        return ("", 0)
