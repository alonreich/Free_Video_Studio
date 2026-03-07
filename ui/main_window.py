import faulthandler, logging, os, signal, subprocess, sys, time, threading, traceback
from logging.handlers import RotatingFileHandler
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *
from system.config import ConfigManager
from system.logger import setup_logger
from system.state_transfer import StateTransfer
from processing.system_utils import kill_process_tree
from ui.widgets.tooltip_manager import ToolTipManager
from ui.widgets.custom_file_dialog import CustomFileDialog
from ui.parts.ui_builder_mixin import UiBuilderMixin
from ui.parts.phase_overlay_mixin import PhaseOverlayMixin
from ui.parts.player_mixin import PlayerMixin
from ui.parts.volume_mixin import VolumeMixin
from ui.parts.trim_mixin import TrimMixin
from ui.parts.music_mixin import MusicMixin
from ui.parts.ffmpeg_mixin import FfmpegMixin
from ui.parts.keyboard_mixin import KeyboardMixin

class PersistentWindowMixin:
    def setup_persistence(self, config_path, settings_key, default_geo, title_info_provider, config_manager):
        self._persistence_config_manager = config_manager
        self._persistence_settings_key = settings_key
        try:
            geo = self._persistence_config_manager.config.get(settings_key, default_geo)
            if 'x' in geo and 'y' in geo:
                self.setGeometry(geo['x'], geo['y'], geo.get('w', default_geo['w']), geo.get('h', default_geo['h']))
            else:
                self.resize(geo.get('w', default_geo['w']), geo.get('h', default_geo['h']))
        except Exception:
            pass

    def handle_close_persistence(self):
        try:
            if hasattr(self, '_persistence_config_manager'):
                geo = self.geometry()
                self._persistence_config_manager.config[self._persistence_settings_key] = {
                    'x': geo.x(), 'y': geo.y(), 'w': geo.width(), 'h': geo.height()
                }
                self._persistence_config_manager.save_config(self._persistence_config_manager.config)
        except Exception:
            pass

from ui.parts.main_window_events import MainWindowEventsMixin
from ui.parts.main_window_file_a import MainWindowFileAMixin
from ui.parts.main_window_file_b import MainWindowFileBMixin
from ui.parts.main_window_tools import MainWindowToolsMixin
from ui.parts.main_window_ui_helpers_a import MainWindowUiHelpersAMixin
from ui.parts.main_window_ui_helpers_b import MainWindowUiHelpersBMixin
from ui.parts.main_window_core_a import MainWindowCoreAMixin
from ui.parts.main_window_core_b import MainWindowCoreBMixin
from ui.parts.main_window_core_c import MainWindowCoreCMixin
import weakref

class _QtLiveLogHandler(logging.Handler):
    def __init__(self, ui_owner):
        super().__init__()
        self.ui_ref = weakref.ref(ui_owner)
        self.setFormatter(logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s'))
        self._emit_lock = threading.Lock()

    def emit(self, record):
        if not QCoreApplication.instance(): return
        ui = self.ui_ref()
        if not ui: return
        if not getattr(ui, "_initialized", False): return
        if getattr(ui, "_switching_app", False) or getattr(ui, "_in_transition", False): return
        with self._emit_lock:
            try:
                msg = self.format(record)
                if msg and hasattr(ui, "live_log_signal"):
                    ui.live_log_signal.emit(msg)
            except Exception:
                pass

class VideoCompressorApp(QMainWindow, UiBuilderMixin, PhaseOverlayMixin, PlayerMixin, VolumeMixin, TrimMixin, MusicMixin, FfmpegMixin, KeyboardMixin, MainWindowEventsMixin, MainWindowFileAMixin, MainWindowFileBMixin, MainWindowToolsMixin, MainWindowUiHelpersAMixin, MainWindowUiHelpersBMixin, MainWindowCoreAMixin, MainWindowCoreBMixin, MainWindowCoreCMixin, PersistentWindowMixin):
    progress_update_signal = pyqtSignal(int)
    status_update_signal = pyqtSignal(str)
    process_finished_signal = pyqtSignal(bool, str)
    live_log_signal = pyqtSignal(str)
    video_ended_signal = pyqtSignal()
    duration_changed_signal = pyqtSignal(int)
    thumbnail_extracted_signal = pyqtSignal(int, float, bool)

    def __init__(self, file_path=None, hardware_strategy="CPU"):
        self._initialized = False
        super().__init__()
        self._scrub_lock = threading.RLock()
        self.script_dir = os.path.dirname(sys.executable) if getattr(sys, 'frozen', False) else os.path.dirname(os.path.abspath(__file__))
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, os.pardir))
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.logger = setup_logger(self.base_dir, "main_app.log", "Main_App")
        self.config_manager = ConfigManager(os.path.join(self.base_dir, 'config', 'main_app', 'main_app.conf'))
        self.status_bar = QStatusBar()
        self.setStatusBar(self.status_bar)
        self.central_widget = QWidget()
        self.setCentralWidget(self.central_widget)
        self.tooltip_manager = ToolTipManager(self)
        self._resize_timer = QTimer(self)
        self._resize_timer.setSingleShot(True)
        self._resize_timer.setInterval(50)
        self._resize_timer.timeout.connect(self._delayed_resize_event)
        self.init_ui()
        self.setup_persistence(
            config_path=os.path.join(self.base_dir, 'config', 'main_app', 'main_app.conf'),
            settings_key="window_geometry",
            default_geo={'w': 1200, 'h': 800},
            title_info_provider=None,
            config_manager=self.config_manager
        )
        self._init_core_logic(file_path, hardware_strategy)
        self._setup_live_logging()
        self._initialized = True

    def _init_core_logic(self, file_path, hardware_strategy):
        self.input_file_path = None
        self.original_duration_ms = 0
        self.original_resolution = ""
        self.trim_start_ms = 0
        self.trim_end_ms = 0
        self.is_playing = False
        self.wants_to_play = False
        self.playback_rate = 1.1
        self.hardware_strategy = hardware_strategy
        self.scan_complete = (hardware_strategy != "Scanning...")
        self.speed_segments = []
        self.last_dir = self.config_manager.config.get('last_directory', os.path.expanduser("~"))
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.update_player_state)
        self._setup_mpv()
        if file_path and os.path.exists(file_path):
            QTimer.singleShot(500, lambda: self.handle_file_selection(file_path))

    def _update_overlay_positions(self):
        try:
            if hasattr(self, "_resize_overlay"):
                self._resize_overlay()
            if hasattr(self, "_update_upload_hint_responsive"):
                self._update_upload_hint_responsive()
            if hasattr(self, "portrait_mask_overlay") and self.portrait_mask_overlay and hasattr(self, "video_surface"):
                if self.portrait_mask_overlay.isVisible():
                    r = self.video_surface.rect()
                    top_left = self.video_surface.mapToGlobal(r.topLeft())
                    local_tl = self.mapFromGlobal(top_left)
                    self.portrait_mask_overlay.setGeometry(QRect(local_tl, r.size()))
        except Exception:
            pass

    def _setup_live_logging(self):
        self._live_handler = _QtLiveLogHandler(self)
        logging.getLogger().addHandler(self._live_handler)
        self.live_log_signal.connect(self._on_live_log)

    def _on_live_log(self, msg):
        if hasattr(self, "log_viewer"):
            self.log_viewer.append(msg)

    def _delayed_resize_event(self):
        self._update_overlay_positions()

    def resizeEvent(self, event):
        MainWindowEventsMixin.resizeEvent(self, event)
        super().resizeEvent(event)

    def moveEvent(self, event):
        MainWindowEventsMixin.moveEvent(self, event)
        super().moveEvent(event)

    def keyPressEvent(self, event):
        if self.handle_global_key_press(event):
            event.accept()
            return
        MainWindowEventsMixin.keyPressEvent(self, event)

    def mousePressEvent(self, event):
        MainWindowEventsMixin.mousePressEvent(self, event)

    def closeEvent(self, event):
        self.handle_close_persistence()
        MainWindowEventsMixin.closeEvent(self, event)
