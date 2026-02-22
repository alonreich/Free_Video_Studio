import time
import os
import threading
import traceback
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
    import mpv
except Exception:
    mpv = None

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

    def __init__(self, parent, mpv_instance, bin_dir, mp3_dir, total_project_sec, speed_factor=1.1, trim_start_ms=0, trim_end_ms=0, speed_segments=None):
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
        if os.name == 'nt':
            import ctypes
            try:
                ctypes.windll.ole32.CoInitializeEx(None, 0x0)
            except: pass
        self.setStyleSheet('''
            QDialog { background-color: #2c3e50; color: #ecf0f1; }
            QWidget { background-color: #2c3e50; color: #ecf0f1; font-family: "Helvetica Neue", Arial, sans-serif; }
            QLabel { background: transparent; }
            QLineEdit { background: #0b141d; border: 2px solid #1f3545; border-radius: 8px; padding: 8px 12px; color: #ecf0f1; }
            QListWidget { background-color: #142d37; border: 2px solid #1f3545; border-radius: 12px; outline: none; padding: 2px; color: white; }
            QListWidget::item:selected { background: #1a5276; border-radius: 4px; }
            QScrollBar:vertical { width: 22px; background: #142d37; border: 1px solid #1f3545; border-radius: 10px; margin: 2px; }
            QScrollBar::handle:vertical { min-height: 34px; border-radius: 9px; border: 1px solid #b8c0c8; background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #c9d0d6, stop:0.5 #e1e6eb, stop:1 #b6bec6); }
        ''')
        self.current_track_path = None
        self.current_track_dur = 0.0
        self.selected_tracks = []
        self._editing_track_index = -1
        self._pending_offset_ms = 0
        self._show_caret_step2 = False
        self._step2_media_ready = False
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
        self._last_good_mpv_ms = 0
        self._last_v_mrl = ""; self._last_m_mrl = ""
        self.final_timeline_time = 0.0
        if False:
            self.mpv_v = mpvProcessProxy('video', self.logger, self.bin_dir)
            self.mpv_m = mpvProcessProxy('music', self.logger, self.bin_dir)
            self._video_player = self.mpv_v.media_player_new() if self.mpv_v else None
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
        self.btn_cancel_wizard.setCursor(Qt.PointingHandCursor)
        self.btn_cancel_wizard.clicked.connect(self._on_nav_cancel_clicked)
        self.btn_back = QPushButton("  BACK")
        self.btn_back.setFixedWidth(135); self.btn_back.setFixedHeight(42)
        self.btn_back.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_back.setCursor(Qt.PointingHandCursor)
        self.btn_back.clicked.connect(self._on_nav_back_clicked)
        self.btn_back.hide()
        self.btn_play_video = QPushButton("  PLAY")
        self.btn_play_video.setFixedWidth(150); self.btn_play_video.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_play_video.setCursor(Qt.PointingHandCursor)
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
        log_dir = os.path.join(getattr(self.parent_window, "base_dir", "."), "logs")
        os.makedirs(log_dir, exist_ok=True)
        self.player = None
        self._music_player = None
        if mpv:
            try:
                wid = int(self.video_container.winId())
                self.logger.info(f"WIZARD: Initializing MPV with window ID {wid}")
                if False:
                    '--avcodec-hw=any'
                    '--vout=direct3d11'
                    fallback_args = ['--vout=dummy']
                    self.player = mpv.MPV(args=fallback_args)
                self.player = mpv.MPV(
                    wid=wid,
                    osc=False,
                    hr_seek='yes',
                    hwdec='auto',
                    keep_open='yes',
                    log_handler=self.logger.debug,
                    loglevel="info",
                    vo='gpu',
                    ytdl=False,
                    demuxer_max_bytes='500M',
                    demuxer_max_back_bytes='100M',
                )
                self.logger.info("WIZARD: MPV Instance Created.")
                time.sleep(0.15) 
                self._music_player = mpv.MPV(
                    vid='no',
                    vo='null',
                    osc=False,
                    input_default_bindings=False,
                    input_vo_keyboard=False,
                    hr_seek='yes',
                    hwdec='no',
                    keep_open='yes',
                    log_handler=self.logger.debug,
                    loglevel="info",
                    ytdl=False,
                    demuxer_max_bytes='300M',
                    demuxer_max_back_bytes='60M',
                )
                self.logger.info("WIZARD: MPV music-only player created.")
            except Exception as e:
                self.logger.error(f"WIZARD: Failed to initialize MPV: {e}")
                self._music_player = None
        self._player = self.player
        self._video_player = self.player
        self.btn_nav_next.setEnabled(False)
        self._prev_next_text = "NEXT"
        self.btn_nav_next.setText("PREPARING...")
        QTimer.singleShot(100, self._initialize_audio_engines)
        self._apply_step_geometry(0)
        self._startup_complete = True
        self.stack.currentChanged.connect(self._on_page_changed)
        self.stack.currentChanged.connect(self._enforce_step_size_constraints)
        self._search_timer = QTimer(self); self._search_timer.setSingleShot(True)
        self._search_timer.timeout.connect(self._do_search)
        self.update_coverage_ui()
        if self.mp3_dir:
            QTimer.singleShot(150, lambda: self.load_tracks(self.mp3_dir))

    def _enforce_step_size_constraints(self, index):
        """Enforces specific size constraints for different steps of the wizard."""
        if index == 1:
            self.setMinimumSize(800, 550)
        else:
            self.setMinimumSize(0, 0)

    def _initialize_audio_engines(self):
        """Finalizes the initialization state."""
        self.btn_nav_next.setEnabled(True)
        self.btn_nav_next.setText(self._prev_next_text)
        self.logger.info("WIZARD: Audio Engines ready.")

    def showEvent(self, event):
        super().showEvent(event)

    def reject(self):
        try:
            if hasattr(self, "_save_step_geometry"):
                self._save_step_geometry()
        except Exception:
            pass
        self.stop_previews()
        self._release_player()
        super().reject()

    def closeEvent(self, event):
        try:
            if hasattr(self, "_save_step_geometry"):
                self._save_step_geometry()
        except Exception:
            pass
        self.stop_previews()
        self._release_player()
        super().closeEvent(event)

    def _release_player(self):
        """Safely release MPV player instances."""
        try:
            if hasattr(self, "player") and self.player:
                self.player.terminate()
                self.player = None
        except Exception as ex:
            self.logger.debug(f"WIZARD: release failed: {ex}")
        try:
            if hasattr(self, "_music_player") and self._music_player:
                self._music_player.terminate()
                self._music_player = None
        except Exception as ex:
            self.logger.debug(f"WIZARD: music player release failed: {ex}")
        finally:
            self._player = None
            self._video_player = None
            self._music_player = None

    def _disconnect_all_worker_signals(self):
        """Safely disconnect all worker signal connections to prevent memory leaks."""
        workers_to_disconnect = [
            '_track_scanner',
            '_waveform_worker', 
            '_wave_worker',
            '_filmstrip_worker',
            '_video_worker',
            '_music_worker'
        ]
        for worker_name in workers_to_disconnect:
            worker = getattr(self, worker_name, None)
            if not worker:
                continue
            try:
                if hasattr(worker, 'ready'):
                    try: worker.ready.disconnect()
                    except: pass
                if hasattr(worker, 'error'):
                    try: worker.error.disconnect()
                    except: pass
                if hasattr(worker, 'finished'):
                    try: worker.finished.disconnect()
                    except: pass
                if hasattr(worker, 'asset_ready'):
                    try: worker.asset_ready.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_started'):
                    try: worker.scanning_started.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_finished'):
                    try: worker.scanning_finished.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_error'):
                    try: worker.scanning_error.disconnect()
                    except: pass
            except Exception as e:
                self.logger.debug(f"WIZARD: Failed to disconnect signals from {worker_name}: {e}")

    def stop_previews(self):
        if hasattr(self, '_stop_waveform_worker'): self._stop_waveform_worker()
        if hasattr(self, '_temp_sync') and self._temp_sync and os.path.exists(self._temp_sync):
            try: os.remove(self._temp_sync)
            except: pass
        self._temp_sync = None
        if hasattr(self, '_player') and self._player: self._player.stop()
        if hasattr(self, '_video_player') and self._video_player: self._video_player.stop()
        if hasattr(self, '_music_player') and self._music_player: self._music_player.stop()
        if hasattr(self, '_play_timer'): self._play_timer.stop()
        if hasattr(self, '_filmstrip_worker') and self._filmstrip_worker:
            try:
                if self._filmstrip_worker.isRunning(): self._filmstrip_worker.stop(); self._filmstrip_worker.wait(1000)
            except: pass
        if hasattr(self, '_wave_worker') and self._wave_worker:
            try:
                if self._wave_worker.isRunning(): self._wave_worker.stop(); self._wave_worker.wait(1000)
            except: pass
        if hasattr(self, '_stop_timeline_workers'):
            try:
                self._stop_timeline_workers()
            except Exception as e:
                self.logger.debug(f"WIZARD: timeline workers cleanup failed: {e}")
        if hasattr(self, '_stop_track_scanner'):
            try:
                self._stop_track_scanner()
            except Exception as e:
                self.logger.debug(f"WIZARD: track scanner cleanup failed: {e}")
__all__ = ["PREVIEW_VISUAL_LEAD_MS", "VideoFilmstripWorker", "MusicWaveformWorker", "SearchableListWidget", "MusicItemWidget", "MergerMusicWizard"]
