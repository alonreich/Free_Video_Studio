import time
import os
from PyQt5.QtWidgets import QDialog, QVBoxLayout, QHBoxLayout, QPushButton, QStackedWidget, QStyle
from PyQt5.QtCore import Qt, pyqtSignal, QTimer
from ui.widgets.music_wizard_style import MergerUIStyle
from ui.widgets.music_wizard_constants import PREVIEW_VISUAL_LEAD_MS
from ui.widgets.music_wizard_workers import VideoFilmstripWorker, MusicWaveformWorker
from ui.widgets.music_wizard_widgets import SearchableListWidget, MusicItemWidget
from ui.widgets.music_wizard_step_pages import MergerMusicWizardStepPagesMixin
from ui.widgets.music_wizard_page3 import MergerMusicWizardStep3PageMixin
from ui.widgets.music_wizard_navigation import MergerMusicWizardNavigationMixin
from ui.widgets.music_wizard_waveform import MergerMusicWizardWaveformMixin
from ui.widgets.music_wizard_playback import MergerMusicWizardPlaybackMixin
from ui.widgets.music_wizard_timeline import MergerMusicWizardTimelineMixin
from ui.widgets.music_wizard_misc import MergerMusicWizardMiscMixin
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

    def __init__(self, parent, vlc_instance, bin_dir, mp3_dir, total_project_sec, speed_factor=1.1, trim_start_ms=0, trim_end_ms=0, speed_segments=None):
        super().__init__(parent)
        self.parent_window = parent
        self.bin_dir = bin_dir
        self.mp3_dir = mp3_dir
        self.total_video_sec = total_project_sec
        self.speed_factor = speed_factor
        self.trim_start_ms = trim_start_ms
        self.trim_end_ms = trim_end_ms
        self.speed_segments = speed_segments or []
        self.logger = parent.logger
        self._cache_wall_times()
        self.setWindowTitle("Background Music Selection Wizard")
        self.setModal(True)
        vlc_args_v = [
            "--no-osd",
            "--aout=waveout",
            "--avcodec-hw=any",
            "--vout=direct3d11",
            "--ignore-config",
        ]
        vlc_args_m = [
            "--no-osd",
            "--aout=waveout",
            "--avcodec-hw=any",
            "--vout=direct3d11",
            "--ignore-config",
        ]
        os.environ["VLC_PLUGIN_PATH"] = os.path.join(self.bin_dir, "plugins")
        self.vlc_v = _vlc_mod.Instance(vlc_args_v) if _vlc_mod else None
        self.vlc_m = _vlc_mod.Instance(vlc_args_m) if _vlc_mod else None
        self.vlc = self.vlc_v
        self.setStyleSheet('''
            QDialog { background-color: #2c3e50; color: #ecf0f1; }
            QWidget { background-color: #2c3e50; color: #ecf0f1; font-family: "Helvetica Neue", Arial, sans-serif; }
            QLabel { background: transparent; }
            QLineEdit { background: #0b141d; border: 2px solid #1f3545; border-radius: 8px; padding: 8px 12px; color: #ecf0f1; }
            QListWidget { background-color: #0b141d; border: 2px solid #1f3545; border-radius: 12px; outline: none; padding: 2px; color: white; }
            QListWidget::item:selected { background: #1a5276; border-radius: 4px; }
            QScrollBar:vertical { width: 22px; background: #0b141d; border: 1px solid #1f3545; border-radius: 10px; margin: 2px; }
            QScrollBar::handle:vertical { min-height: 34px; border-radius: 9px; border: 1px solid #b8c0c8; background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #c9d0d6, stop:0.5 #e1e6eb, stop:1 #b6bec6); }
        ''')
        self.current_track_path = None
        self.current_track_dur = 0.0
        self.selected_tracks = []
        self._editing_track_index = -1
        self._pending_offset_ms = 0
        self._show_caret_step2 = False
        self._geometry_restored = False
        self._startup_complete = False
        self._temp_png = None
        self._pm_src = None
        self._waveform_worker = None
        self._wave_target_path = ""
        self._draw_w = 0; self._draw_h = 0
        self._draw_x0 = 0; self._draw_y0 = 0
        self._dragging = False; self._wave_dragging = False
        self._last_tick_ts = 0.0; self._is_syncing = False 
        self._current_elapsed_offset = 0.0; self._last_seek_ts = 0.0 
        self._last_clock_ts = time.time()
        self._vlc_state_playing = 3; self._last_good_vlc_ms = 0
        self._last_v_mrl = ""; self._last_m_mrl = ""
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
        self.btn_cancel_wizard.setFixedWidth(140); self.btn_cancel_wizard.setFixedHeight(42)
        self.btn_cancel_wizard.setStyleSheet(MergerUIStyle.BUTTON_DANGER)
        self.btn_cancel_wizard.clicked.connect(self._on_nav_cancel_clicked)
        self.btn_back = QPushButton("  BACK")
        self.btn_back.setFixedWidth(135); self.btn_back.setFixedHeight(42)
        self.btn_back.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_back.clicked.connect(self._on_nav_back_clicked)
        self.btn_back.hide()
        self.btn_play_video = QPushButton("  PLAY")
        self.btn_play_video.setFixedWidth(150); self.btn_play_video.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.btn_play_video.clicked.connect(self.toggle_video_preview)
        self.btn_play_video.hide()
        self.btn_nav_next = QPushButton("NEXT")
        self.btn_nav_next.setFixedWidth(135); self.btn_nav_next.setFixedHeight(42)
        self.btn_nav_next.setStyleSheet(MergerUIStyle.BUTTON_MERGE)
        self.btn_nav_next.setCursor(Qt.PointingHandCursor)
        self.btn_nav_next.clicked.connect(self._on_nav_next_clicked)
        nav_layout.addWidget(self.btn_cancel_wizard); nav_layout.addWidget(self.btn_back)
        nav_layout.addStretch(); nav_layout.addWidget(self.btn_play_video)
        nav_layout.addSpacing(80); nav_layout.addStretch(); nav_layout.addWidget(self.btn_nav_next)
        self.main_layout.addLayout(nav_layout)
        self._player = self.vlc_m.media_player_new() if self.vlc_m else None
        self._video_player = self.vlc_v.media_player_new() if self.vlc_v else None
        if self._player:
            self._player.audio_set_mute(False)
        if self._video_player:
            self._video_player.audio_set_mute(False)
        if self._video_player:
            self._bind_video_output()
            QTimer.singleShot(0, self._bind_video_output)
        self._apply_step_geometry(0)
        self._startup_complete = True
        self.stack.currentChanged.connect(self._on_page_changed)
        self._search_timer = QTimer(self); self._search_timer.setSingleShot(True)
        self._search_timer.timeout.connect(self._do_search)
        self.update_coverage_ui()
        if self.mp3_dir: self.load_tracks(self.mp3_dir)

    def showEvent(self, event):
        super().showEvent(event)

    def reject(self):
        try:
            if hasattr(self, "_save_step_geometry"):
                self._save_step_geometry()
        except Exception:
            pass
        self.stop_previews()
        self._release_vlc()
        super().reject()

    def closeEvent(self, event):
        try:
            if hasattr(self, "_save_step_geometry"):
                self._save_step_geometry()
        except Exception:
            pass
        self.stop_previews()
        self._release_vlc()
        super().closeEvent(event)

    def _release_vlc(self):
        """Safely release VLC players and instances."""
        try:
            if hasattr(self, "_player") and self._player:
                self._player.stop()
                self._player.release()
                self._player = None
            if hasattr(self, "_video_player") and self._video_player:
                self._video_player.stop()
                self._video_player.release()
                self._video_player = None
            if hasattr(self, "vlc_v") and self.vlc_v:
                self.vlc_v.release()
                self.vlc_v = None
            if hasattr(self, "vlc_m") and self.vlc_m:
                self.vlc_m.release()
                self.vlc_m = None
        except Exception:
            pass

    def stop_previews(self):
        if hasattr(self, '_stop_waveform_worker'): self._stop_waveform_worker()
        if hasattr(self, '_temp_sync') and self._temp_sync and os.path.exists(self._temp_sync):
            try: os.remove(self._temp_sync)
            except: pass
        self._temp_sync = None
        if hasattr(self, '_player') and self._player: self._player.stop()
        if hasattr(self, '_video_player') and self._video_player: self._video_player.stop()
        if hasattr(self, '_play_timer'): self._play_timer.stop()
        if hasattr(self, '_filmstrip_worker') and self._filmstrip_worker:
            try:
                if self._filmstrip_worker.isRunning(): self._filmstrip_worker.stop(); self._filmstrip_worker.wait(1000)
            except: pass
        if hasattr(self, '_wave_worker') and self._wave_worker:
            try:
                if self._wave_worker.isRunning(): self._wave_worker.stop(); self._wave_worker.wait(1000)
            except: pass
__all__ = ["PREVIEW_VISUAL_LEAD_MS", "VideoFilmstripWorker", "MusicWaveformWorker", "SearchableListWidget", "MusicItemWidget", "MergerMusicWizard"]
