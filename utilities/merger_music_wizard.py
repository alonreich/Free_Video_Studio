import time
import os
from PyQt5.QtWidgets import QDialog, QVBoxLayout, QHBoxLayout, QPushButton, QStackedWidget, QStyle
from PyQt5.QtCore import Qt, pyqtSignal, QTimer
from utilities.merger_ui_style import MergerUIStyle
from utilities.merger_music_wizard_constants import PREVIEW_VISUAL_LEAD_MS
from utilities.merger_music_wizard_workers import VideoFilmstripWorker, MusicWaveformWorker
from utilities.merger_music_wizard_widgets import SearchableListWidget, MusicItemWidget
from utilities.merger_music_wizard_step_pages import MergerMusicWizardStepPagesMixin
from utilities.merger_music_wizard_page3 import MergerMusicWizardStep3PageMixin
from utilities.merger_music_wizard_navigation import MergerMusicWizardNavigationMixin
from utilities.merger_music_wizard_waveform import MergerMusicWizardWaveformMixin
from utilities.merger_music_wizard_playback import MergerMusicWizardPlaybackMixin
from utilities.merger_music_wizard_timeline import MergerMusicWizardTimelineMixin
from utilities.merger_music_wizard_misc import MergerMusicWizardMiscMixin
try:
    import vlc as _vlc_mod
except Exception:
    _vlc_mod = None

class MergerMusicWizard(
    MergerMusicWizardStepPagesMixin,
    MergerMusicWizardStep3PageMixin,
    MergerMusicWizardNavigationMixin,
    MergerMusicWizardWaveformMixin,
    MergerMusicWizardPlaybackMixin,
    MergerMusicWizardTimelineMixin,
    MergerMusicWizardMiscMixin,
    QDialog,
):
    _ui_call = pyqtSignal(object)

    def __init__(self, parent, vlc_instance, bin_dir, mp3_dir, total_video_sec):
        super().__init__(parent)
        self.parent_window = parent
        self.vlc = vlc_instance
        self.bin_dir = bin_dir
        self.mp3_dir = mp3_dir
        self.total_video_sec = total_video_sec
        self.logger = parent.logger
        self.setWindowTitle("Background Music Selection Wizard")
        self.setModal(True)
        self.current_track_path = None
        self.current_track_dur = 0.0
        self.selected_tracks = []
        self._editing_track_index = -1
        self._pending_offset_ms = 0
        self._temp_png = None
        self._pm_src = None
        self._waveform_worker = None
        self._wave_target_path = ""
        self._draw_w = 0
        self._draw_h = 0
        self._draw_x0 = 0
        self._draw_y0 = 0
        self._dragging = False
        self._wave_dragging = False
        self._last_tick_ts = 0.0 
        self._is_syncing = False 
        self._current_elapsed_offset = 0.0 
        self._last_seek_ts = 0.0 
        self._last_clock_ts = time.time()
        self._vlc_state_playing = 3 
        self._last_good_vlc_ms = 0
        self._last_v_mrl = ""
        self._last_m_mrl = ""
        self.main_layout = QVBoxLayout(self)
        self.main_layout.setContentsMargins(20, 10, 20, 20)
        self.main_layout.setSpacing(15)
        self.stack = QStackedWidget()
        self.setup_step1_select()
        self.setup_step2_offset()
        self.setup_step3_timeline()
        self.main_layout.addWidget(self.stack)
        nav_layout = QHBoxLayout()
        self.btn_cancel_wizard = QPushButton("CANCEL")
        self.btn_cancel_wizard.setFixedWidth(140)
        self.btn_cancel_wizard.setStyleSheet(MergerUIStyle.BUTTON_DANGER)
        self.btn_cancel_wizard.setCursor(Qt.PointingHandCursor)
        self.btn_cancel_wizard.clicked.connect(self._on_nav_cancel_clicked)
        self.btn_back = QPushButton("  BACK")
        self.btn_back.setFixedWidth(135)
        self.btn_back.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_back.setCursor(Qt.PointingHandCursor)
        self.btn_back.clicked.connect(self._on_nav_back_clicked)
        self.btn_back.hide()
        self.btn_play_video = QPushButton("  PLAY")
        self.btn_play_video.setFixedWidth(150)
        self.btn_play_video.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_play_video.setCursor(Qt.PointingHandCursor)
        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.btn_play_video.clicked.connect(self.toggle_video_preview)
        self.btn_play_video.hide()
        self.btn_nav_next = QPushButton("NEXT")
        self.btn_nav_next.setFixedWidth(135)
        self.btn_nav_next.setStyleSheet(MergerUIStyle.BUTTON_MERGE)
        self.btn_nav_next.setCursor(Qt.PointingHandCursor)
        self.btn_nav_next.clicked.connect(self._on_nav_next_clicked)
        nav_layout.addWidget(self.btn_cancel_wizard)
        nav_layout.addWidget(self.btn_back)
        nav_layout.addStretch()
        nav_layout.addWidget(self.btn_play_video)
        nav_layout.addSpacing(80)
        nav_layout.addStretch()
        nav_layout.addWidget(self.btn_nav_next)
        self.main_layout.addLayout(nav_layout)
        self._player = self.vlc.media_player_new() if self.vlc else None
        self._video_player = self.vlc.media_player_new() if self.vlc else None
        if self._video_player:
            self._bind_video_output()
            QTimer.singleShot(0, self._bind_video_output)
        self._restore_geometry()
        self.stack.currentChanged.connect(self._on_page_changed)
        self._search_timer = QTimer(self)
        self._search_timer.setSingleShot(True)
        self._search_timer.timeout.connect(self._do_search)
        self.update_coverage_ui()
        if self.mp3_dir:
            self.load_tracks(self.mp3_dir)

    def reject(self):
        """Ensure threads stop when ESC or CANCEL is clicked."""
        self.stop_previews()
        super().reject()

    def closeEvent(self, event):
        """Ensure threads stop when window is closed."""
        self.stop_previews()
        super().closeEvent(event)

    def stop_previews(self):
        """Utility to stop all preview-related background activity."""
        if hasattr(self, '_stop_waveform_worker'):
            self._stop_waveform_worker()
        if hasattr(self, '_temp_sync') and self._temp_sync and os.path.exists(self._temp_sync):
            try:
                os.remove(self._temp_sync)
                self.logger.info(f"WIZARD: Cleaned up synced audio: {self._temp_sync}")
            except Exception: pass
        self._temp_sync = None
        if hasattr(self, '_player') and self._player:
            self._player.stop()
        if hasattr(self, '_video_player') and self._video_player:
            self._video_player.stop()
        if hasattr(self, '_play_timer'):
            self._play_timer.stop()
        if hasattr(self, '_filmstrip_worker') and self._filmstrip_worker:
            try:
                if self._filmstrip_worker.isRunning():
                    self._filmstrip_worker.stop()
                    self._filmstrip_worker.wait(1000)
            except Exception: pass
        if hasattr(self, '_wave_worker') and self._wave_worker:
            try:
                if self._wave_worker.isRunning():
                    self._wave_worker.stop()
                    self._wave_worker.wait(1000)
            except Exception: pass
__all__ = [
    "PREVIEW_VISUAL_LEAD_MS",
    "VideoFilmstripWorker",
    "MusicWaveformWorker",
    "SearchableListWidget",
    "MusicItemWidget",
    "MergerMusicWizard",
]
