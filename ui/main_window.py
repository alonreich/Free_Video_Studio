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
try:
    from developer_tools.utils import PersistentWindowMixin
except ImportError:
    import sys
    sys.path.append(os.path.join(os.path.dirname(__file__), '..', 'developer_tools'))

    from utils import PersistentWindowMixin
from ui.parts.main_window_events import MainWindowEventsMixin
from ui.parts.main_window_file_a import MainWindowFileAMixin
from ui.parts.main_window_file_b import MainWindowFileBMixin
from ui.parts.main_window_tools import MainWindowToolsMixin
from ui.parts.main_window_ui_helpers_a import MainWindowUiHelpersAMixin
from ui.parts.main_window_ui_helpers_b import MainWindowUiHelpersBMixin
from ui.parts.main_window_core_a import MainWindowCoreAMixin
from ui.parts.main_window_core_b import MainWindowCoreBMixin
from ui.parts.main_window_core_c import MainWindowCoreCMixin

class _QtLiveLogHandler(logging.Handler):
    def __init__(self, ui_owner):
        super().__init__()
        self.ui = ui_owner

    def emit(self, record):
        if not QCoreApplication.instance(): return
        if getattr(self.ui, "_switching_app", False) or getattr(self.ui, "_in_transition", False): return
        try:
            if not getattr(self, "formatter", None):
                self.setFormatter(logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s'))
            msg = self.format(record)
            if msg and hasattr(self.ui, "live_log_signal"):
                self.ui.live_log_signal.emit(msg)
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
            title_info_provider=lambda: "Main",
            config_manager=self.config_manager
        )
        self.thumbnail_extracted_signal.connect(self._on_thumb_extracted)
        self.playback_rate = 1.1
        self.speed_segments = []
        self.hardware_strategy = hardware_strategy
        try:
            self._video_volume_pct = int(self.config_manager.config.get('video_mix_volume', 100))
            self._music_volume_pct = int(self.config_manager.config.get('music_mix_volume', 80))
        except:
            self._video_volume_pct, self._music_volume_pct = 100, 80
        self.last_dir = self.config_manager.config.get('last_directory', os.path.expanduser('~'))
        self.trim_start_ms, self.trim_end_ms, self.trim_start, self.trim_end = 0, 0, 0.0, 0.0
        self.music_timeline_start_ms, self.music_timeline_end_ms = 0, 0
        self.input_file_path, self.original_duration_ms, self.original_resolution = None, 0, ""
        self.is_playing, self.is_processing, self.wants_to_play = False, False, False
        self._is_seeking_from_end, self._suspend_volume_sync, self._opening_granular_dialog = False, True, False
        self._in_transition = False
        self._ignore_mpv_end_until, self._handling_video_end, self._last_mpv_end_emit = 0.0, False, 0.0
        self.volume_shortcut_target, self._phase_is_processing, self._phase_dots = 'main', False, 1
        self._base_title, self._music_files, self.scan_complete = "Fortnite Video Compressor", [], False
        self.set_style()
        self.setWindowTitle(self._base_title)
        try: StateTransfer.clear_state()
        except Exception as state_err: self.logger.debug("Could not clear startup session state: %s", state_err)
        if self.hardware_strategy == "Scanning...":
            if hasattr(self, 'hardware_status_label'): self.hardware_status_label.setText("🔎 Scanning...")
            else: self.status_bar.showMessage("🔎 Scanning...")
        elif self.hardware_strategy == "CPU":
            self.show_status_warning("⚠️ No compatible GPU detected.")
            self.scan_complete = True
        else:
            self.status_bar.showMessage("Ready.", 5000)
            self.scan_complete = True
        self.live_log_signal.connect(self.log_overlay_sink)
        self.video_ended_signal.connect(self._handle_video_end)
        self.duration_changed_signal.connect(self._safe_handle_duration_changed)
        try:
            self.logger.handlers = [h for h in self.logger.handlers if not isinstance(h, _QtLiveLogHandler)]
            qh = _QtLiveLogHandler(self); qh.setLevel(logging.INFO); self.logger.addHandler(qh)
        except: pass
        self.timer = QTimer(self); self.timer.setInterval(40); self.timer.timeout.connect(self.update_player_state)
        self.setMinimumSize(1000, 600)
        self._scan_mp3_folder()
        self._update_window_size_in_title()

        def _ss(o):
            if getattr(self, "input_file_path", None): self.seek_relative_time(o)
        QShortcut(QKeySequence(Qt.Key_Left), self, lambda: _ss(-250))
        QShortcut(QKeySequence(Qt.Key_Right), self, lambda: _ss(250))
        QShortcut(QKeySequence(Qt.CTRL | Qt.Key_Left), self, lambda: _ss(-5))
        QShortcut(QKeySequence(Qt.CTRL | Qt.Key_Right), self, lambda: _ss(5))
        self._init_upload_hint_blink()
        self._set_upload_hint_active(not file_path)
        if file_path: self.handle_file_selection(file_path)
        QTimer.singleShot(10, self._setup_mpv)


































