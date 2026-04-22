import faulthandler, logging, os, signal, subprocess, sys, time, threading, traceback, weakref
from PyQt5.QtCore import (Qt, QTimer, pyqtSignal, QEvent, QRect, QPoint, QSize, QCoreApplication, QThread)
from PyQt5.QtGui import (QIcon, QPixmap, QColor, QFont, QPainter)
from PyQt5.QtWidgets import (QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QMessageBox, QStyle, QApplication, QLabel, QFrame, QSizePolicy, QProgressBar, QStackedLayout, QPushButton, QCheckBox, QSpinBox, QDoubleSpinBox, QGridLayout)
from system.utils import MPVSafetyManager
from ui.parts.player_mixin import PlayerMixin
from ui.parts.ui_builder_mixin import UiBuilderMixin
from ui.parts.volume_mixin import VolumeMixin
from ui.parts.main_window_core_a import MainWindowCoreAMixin
from ui.parts.main_window_core_b import MainWindowCoreBMixin
from ui.parts.main_window_core_c import MainWindowCoreCMixin
from ui.parts.main_window_events import MainWindowEventsMixin
from ui.parts.main_window_file_a import MainWindowFileAMixin
from ui.parts.main_window_file_b import MainWindowFileBMixin
from ui.parts.main_window_tools import MainWindowToolsMixin
from ui.parts.main_window_ui_helpers_a import MainWindowUiHelpersAMixin
from ui.parts.main_window_ui_helpers_b import MainWindowUiHelpersBMixin
from ui.parts.music_mixin import MusicMixin
from ui.parts.trim_mixin import TrimMixin
from ui.parts.ffmpeg_mixin import FfmpegMixin
from ui.parts.keyboard_mixin import KeyboardMixin
from ui.parts.phase_overlay_mixin import PhaseOverlayMixin
from ui.styles import UIStyles
from system.config import ConfigManager
from system.state_transfer import StateTransfer
from ui.widgets.tooltip_manager import ToolTipManager
from ui.widgets.timeline_overlay import TimelineOverlay

class _QtLiveLogHandler(logging.Handler):
    def __init__(self, target):
        super().__init__()
        self.target = weakref.ref(target)

    def emit(self, record):
        try:
            t = self.target()
            if t and hasattr(t, "log_overlay_sink"):
                msg = self.format(record)
                t.log_overlay_sink(msg)
        except Exception: pass

class FortniteVideoSoftware(QMainWindow, PlayerMixin, UiBuilderMixin, VolumeMixin,
                            MainWindowCoreAMixin, MainWindowCoreBMixin, MainWindowCoreCMixin,
                            MainWindowEventsMixin, MainWindowFileAMixin, MainWindowFileBMixin,
                            MainWindowToolsMixin, MainWindowUiHelpersAMixin, MainWindowUiHelpersBMixin,
                            MusicMixin, TrimMixin, FfmpegMixin, KeyboardMixin, PhaseOverlayMixin):
    progress_update_signal = pyqtSignal(float)
    status_update_signal = pyqtSignal(str)
    process_finished_signal = pyqtSignal(bool, str)
    video_ended_signal = pyqtSignal()
    thumbnail_extracted_signal = pyqtSignal(int, float, bool)
    duration_changed_signal = pyqtSignal(int)

    def __init__(self, file_path=None, hardware_strategy="Scanning...", bin_dir="", config_manager=None, tooltip_manager=None):
        super().__init__()
        self.base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
        self.bin_dir = bin_dir if bin_dir else os.path.join(self.base_dir, 'binaries')
        self.config_manager = config_manager if config_manager else ConfigManager(os.path.join(self.base_dir, 'config', 'main_app', 'main_app.conf'))
        self.tooltip_manager = tooltip_manager if tooltip_manager else ToolTipManager(self)
        self.timeline_overlay = TimelineOverlay(self)
        self.positionSlider = self.timeline_overlay.positionSlider
        PlayerMixin.__init__(self)
        self.logger = logging.getLogger("Main_App"); self._mpv_lock = threading.RLock(); self._is_seeking_active = False; self._last_player_output_bind_ts = 0; self._binding_player_output = False
        self.duration_changed_signal.connect(self._safe_handle_duration_changed)
        self.thumbnail_extracted_signal.connect(self._on_thumb_extracted)
        try: StateTransfer.clear_state()
        except: pass
        self.setWindowTitle("Fortnite Video Software - Pro Edition"); self.setMinimumSize(1200, 800)
        self.central_widget = QWidget(); self.setCentralWidget(self.central_widget)
        self._init_core_logic(file_path, hardware_strategy)
        self.set_style(); self.init_ui(); self._setup_mpv(); self._set_video_controls_enabled(False)
        self.setAcceptDrops(True); self.status_bar = self.statusBar(); self.restore_geometry()
        self.positionSlider.sliderMoved.connect(self.set_player_position)
        self.positionSlider.trim_times_changed.connect(self._on_slider_trim_changed)
        self.positionSlider.music_trim_changed.connect(self._on_slider_music_trim_changed)
        self.positionSlider.rangeChanged.connect(lambda *_: self._maybe_enable_process())
        self.positionSlider.valueChanged.connect(lambda: self._sync_main_timeline_badges())
        QApplication.instance().installEventFilter(self); self.show()
        QTimer.singleShot(100, self._update_overlay_positions)
        QTimer.singleShot(500, self._update_overlay_positions)
        QTimer.singleShot(1500, self._update_overlay_positions)

    def _init_core_logic(self, file_path, hardware_strategy):
        self.input_file_path = None; self.original_duration_ms = 0; self.original_resolution = ""; self.trim_start_ms = 0; self.trim_end_ms = 0
        self.is_playing = False; self.wants_to_play = False; self.playback_rate = 1.1; self.hardware_strategy = hardware_strategy
        self.scan_complete = (hardware_strategy != "Scanning..."); self.speed_segments = []
        self.last_dir = self.config_manager.config.get('last_directory', os.path.expanduser("~"))
        self.timer = QTimer(self); self.timer.timeout.connect(self.update_player_state)
        if file_path and os.path.exists(file_path): QTimer.singleShot(500, lambda: self.handle_file_selection(file_path))

    def _on_mpv_idle_changed(self, is_idle):
        if is_idle and self.wants_to_play: QTimer.singleShot(0, self._safe_handle_mpv_end)

    def _update_overlay_positions(self):
        if hasattr(self, "_update_upload_hint_responsive"): self._update_upload_hint_responsive()
        try:
            if hasattr(self, "_resize_overlay"): self._resize_overlay()
            if hasattr(self, "timeline_overlay"):
                res = getattr(self, "original_resolution", "1920x1080")
                self.timeline_overlay.update_geometry(self.video_surface, res)
            if hasattr(self, "portrait_mask_overlay"): self._update_portrait_mask_overlay_state()
        except: pass

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls(): event.accept()
        else: event.ignore()

    def dropEvent(self, event):
        files = [u.toLocalFile() for u in event.mimeData().urls()]
        if files: self.handle_file_selection(files[0])

    def resizeEvent(self, event):
        self._update_overlay_positions()
        if hasattr(self, "_update_upload_hint_responsive"): self._update_upload_hint_responsive()
        MainWindowEventsMixin.resizeEvent(self, event)

    def mousePressEvent(self, event): MainWindowEventsMixin.mousePressEvent(self, event)

    def closeEvent(self, event): MainWindowEventsMixin.closeEvent(self, event)
VideoCompressorApp = FortniteVideoSoftware
