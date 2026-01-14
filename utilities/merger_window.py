import sys
import os
import subprocess
from pathlib import Path
from PyQt5.QtWidgets import QMainWindow, QFileDialog, QApplication, QListWidget, QLabel, QPushButton
from PyQt5.QtCore import pyqtSignal, Qt, QTimer, QEvent
from PyQt5.QtGui import QIcon
from utilities.merger_ui import MergerUI
from utilities.merger_handlers_main import MergerHandlers
from utilities.merger_ffmpeg import FFMpegHandler
from utilities.merger_utils import _get_logger
from utilities.merger_window_logic import MergerWindowLogic
from ui.widgets.draggable_list_widget import DraggableListWidget
from ui.parts.music_mixin import MusicMixin

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

class VideoMergerWindow(QMainWindow, MusicMixin):
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
        self.positionSlider = DummyPositionSlider()
        self.config_manager = ConfigManagerAdapter(self)
        self.logger = _get_logger()
        self.ui_handler = MergerUI(self)
        self.event_handler = MergerHandlers(self)
        self.ffmpeg_handler = FFMpegHandler(self)
        self.logic_handler = MergerWindowLogic(self)
        self.init_ui()
        self.connect_signals()
        self._scan_mp3_folder()
        self.event_handler.update_button_states()
        self.logger.info("OPEN: Video Merger window created")

    def showEvent(self, event: QEvent):
        """Load config only when the window is first shown."""
        if not self._loaded:
            self._loaded = True
            self.logic_handler.load_config()
        super().showEvent(event)

    def init_ui(self):
        self.setWindowTitle("Video Merger")
        self.resize(980, 560)
        self.setMinimumHeight(560)
        self.ui_handler.set_style()
        self.ui_handler.setup_ui()
        self.set_icon()

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
        self.listw.model().rowsMoved.connect(self.event_handler.update_button_states)
        self.listw.model().rowsMoved.connect(self.event_handler.on_rows_moved)
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

    def on_merge_clicked(self):
        self.event_handler.on_merge_clicked()

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
            subprocess.Popen(command, cwd=self.base_dir, creationflags=subprocess.DETACHED_PROCESS)
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