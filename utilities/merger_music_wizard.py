import os
import sys
import time
import tempfile
import subprocess
import traceback
import logging
import shutil
import re
from PyQt5.QtWidgets import (
    QDialog, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, 
    QListWidget, QListWidgetItem, QStackedWidget, QWidget,
    QSizePolicy, QProgressBar, QMessageBox, QStyle, QSlider, QLineEdit, QApplication
)

from PyQt5 import QtCore
from PyQt5.QtCore import Qt, pyqtSignal, QTimer, QSize, QEvent, QPoint
from PyQt5.QtGui import QPixmap, QColor, QFont
from utilities.merger_trimmed_slider import MergerTrimmedSlider
from utilities.merger_ui_style import MergerUIStyle
from utilities.merger_timeline_widget import MergerTimelineWidget
try:
    import vlc as _vlc_mod
except Exception:
    _vlc_mod = None
PREVIEW_VISUAL_LEAD_MS = 1100

class VideoFilmstripWorker(QtCore.QThread):
    asset_ready = pyqtSignal(int, list)
    finished = pyqtSignal()

    def __init__(self, video_segments_info, bin_dir):
        super().__init__(None)
        self.video_segments_info = video_segments_info 
        self.bin_dir = bin_dir

    def run(self):
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        logger = logging.getLogger("Video_Merger")
        logger.info("GPU_WORKER: Initializing Parallel Filmstrip Extraction.")
        for idx, (path, duration) in enumerate(self.video_segments_info):
            try:
                tmp_pattern_dir = tempfile.mkdtemp(prefix="fvs_thumbs_")
                out_pattern = os.path.join(tmp_pattern_dir, "thumb_%04d.jpg")
                logger.debug(f"GPU_WORKER: Segment {idx} -> {os.path.basename(path)} ({duration:.1f}s)")
                logger.debug(f"GPU_WORKER: Density: 4fps | Pattern: {out_pattern}")
                cmd = [
                    ffmpeg_exe, "-y", "-hide_banner", "-loglevel", "error",
                    "-hwaccel", "auto",
                    "-i", path,
                    "-vf", "fps=4,scale=320:180",
                    "-q:v", "5",
                    out_pattern
                ]
                logger.info(f"GPU_WORKER: Executing FFmpeg: {' '.join(cmd)}")
                start_t = time.time()
                subprocess.run(cmd, capture_output=True, creationflags=flags)
                elapsed = time.time() - start_t
                thumbs = []
                if os.path.exists(tmp_pattern_dir):
                    files = sorted([f for f in os.listdir(tmp_pattern_dir) if f.endswith(".jpg")])
                    logger.debug(f"GPU_WORKER: Found {len(files)} frames in {elapsed:.2f}s")
                    for f in files:
                        full_p = os.path.join(tmp_pattern_dir, f)
                        pm = QPixmap(full_p)
                        if not pm.isNull():
                            thumbs.append(pm)
                        try: os.remove(full_p)
                        except: pass
                    try: os.rmdir(tmp_pattern_dir)
                    except: pass
                if thumbs:
                    logger.info(f"GPU_WORKER: Segment {idx} Complete. Mapped {len(thumbs)} frames.")
                    self.asset_ready.emit(idx, thumbs)
                else:
                    logger.error(f"GPU_WORKER: Segment {idx} Failed - No frames found.")
            except Exception as e:
                logger.error(f"GPU_WORKER: Segment {idx} Critical Error: {e}")
        self.finished.emit()

class MusicWaveformWorker(QtCore.QThread):
    asset_ready = pyqtSignal(int, QPixmap)
    finished = pyqtSignal()

    def __init__(self, music_segments_info, bin_dir):
        super().__init__(None)
        self.music_segments_info = music_segments_info
        self.bin_dir = bin_dir

    def run(self):
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        logger = logging.getLogger("Video_Merger")
        logger.info("CPU_WORKER: Initializing Music Waveform Generation (Parallel).")
        for i, (path, offset, dur) in enumerate(self.music_segments_info):
            try:
                tf = tempfile.NamedTemporaryFile(suffix=".png", delete=False)
                tmp_path = tf.name; tf.close()
                logger.debug(f"CPU_WORKER: Rendering Waveform {i} -> {os.path.basename(path)}")
                logger.debug(f"CPU_WORKER: Offset: {offset:.2f}s | Duration: {dur:.2f}s")
                cmd = [ffmpeg_exe, "-y", "-ss", f"{offset:.3f}", "-t", f"{dur:.3f}", "-i", path, 
                       "-filter_complex", "compand=attacks=0:points=-80/-80|-20/-20|-15/-10|0/-3,showwavespic=s=6000x400:colors=0x2ecc71", "-frames:v", "1", tmp_path]
                logger.info(f"CPU_WORKER: Executing FFmpeg (CPU-Bound): {' '.join(cmd)}")
                start_t = time.time()
                subprocess.run(cmd, capture_output=True, creationflags=flags)
                elapsed = time.time() - start_t
                if os.path.exists(tmp_path):
                    logger.info(f"CPU_WORKER: Waveform {i} Complete. Resolution: 6000x400. Elapsed: {elapsed:.2f}s")
                    pm = QPixmap(tmp_path)
                    os.remove(tmp_path)
                    if not pm.isNull(): self.asset_ready.emit(i, pm)
                else:
                    logger.error(f"CPU_WORKER: Waveform {i} Failed - File missing.")
            except Exception as e:
                logger.error(f"CPU_WORKER: Critical error during waveform {i}: {e}")
        self.finished.emit()

class SearchableListWidget(QListWidget):
    """A QListWidget that ignores decorative elements during keyboard search."""

    def keyPressEvent(self, event):
        if event.text() and len(event.text()) == 1 and event.modifiers() == Qt.NoModifier:
            search_char = event.text().lower()
            for i in range(self.count()):
                item = self.item(i)
                if not item.isHidden():
                    w = self.itemWidget(item)
                    if w and hasattr(w, 'name_lbl'):
                        clean_text = w.name_lbl.text().lower()
                        if clean_text.startswith(search_char):
                            self.setCurrentItem(item)
                            self.scrollToItem(item)
                            return
        super().keyPressEvent(event)

class MusicItemWidget(QWidget):
    """Custom widget for song list items with specific font requirements."""

    def __init__(self, filename, parent=None):
        super().__init__(parent)
        layout = QHBoxLayout(self)
        layout.setContentsMargins(15, 2, 10, 2)
        layout.setSpacing(10)
        self.note_lbl = QLabel("♪")
        self.note_lbl.setStyleSheet("font-size: 18px; color: #7DD3FC; font-weight: bold;")
        self.name_lbl = QLabel(filename)
        self.name_lbl.setStyleSheet("font-size: 14px; color: #ecf0f1;")
        layout.addWidget(self.note_lbl)
        layout.addWidget(self.name_lbl, 1)
        self.setLayout(layout)

class MergerMusicWizard(QDialog):
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
        self._temp_png = None
        self._pm_src = None
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
        self.main_layout.setContentsMargins(20, 20, 20, 20)
        self.main_layout.setSpacing(15)
        self.stack = QStackedWidget()
        self.setup_step1_select()
        self.setup_step2_offset()
        self.setup_step3_timeline()
        self.main_layout.addWidget(self.stack)
        nav_layout = QHBoxLayout()
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
        nav_layout.addWidget(self.btn_back)
        nav_layout.addStretch()
        nav_layout.addWidget(self.btn_play_video)
        nav_layout.addStretch()
        nav_layout.addWidget(self.btn_nav_next)
        self.main_layout.addLayout(nav_layout)
        self._player = self.vlc.media_player_new() if self.vlc else None
        self._video_player = self.vlc.media_player_new() if self.vlc else None
        self._restore_geometry()
        self.stack.currentChanged.connect(self._on_page_changed)
        self._search_timer = QTimer(self)
        self._search_timer.setSingleShot(True)
        self._search_timer.timeout.connect(self._do_search)
        self.update_coverage_ui()
        if self.mp3_dir:
            self.load_tracks(self.mp3_dir)

    def load_tracks(self, folder_path):
        """Scans the folder for MP3 files and populates the list."""
        if not os.path.isdir(folder_path):
            self.logger.warning(f"WIZARD: MP3 folder not found: {folder_path}")
            return
        self.track_list.clear()
        files = [f for f in os.listdir(folder_path) if f.lower().endswith(".mp3")]
        files.sort()
        for filename in files:
            full_path = os.path.join(folder_path, filename)
            item = QListWidgetItem(self.track_list)
            custom_widget = MusicItemWidget(filename)
            item.setSizeHint(custom_widget.sizeHint())
            item.setData(Qt.UserRole, full_path)
            self.track_list.addItem(item)
            self.track_list.setItemWidget(item, custom_widget)
        self.logger.info(f"WIZARD: Loaded {len(files)} tracks from {folder_path}")

    def setup_step1_select(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setSpacing(0)
        layout.setContentsMargins(0, 0, 0, 0)
        self.lbl_step1 = QLabel("STEP 1: Pick a song from your folder")
        self.lbl_step1.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC;")
        self.lbl_step1.setAlignment(Qt.AlignCenter)
        self.lbl_step1.setFixedHeight(30)
        layout.addWidget(self.lbl_step1)
        layout.addSpacing(10)
        self.coverage_progress = QProgressBar()
        self.coverage_progress.setFixedHeight(25)
        self.coverage_progress.setStyleSheet("""
            QProgressBar {
                background: #1f3545;
                border: 1px solid #34495e;
                border-radius: 6px;
                text-align: center;
                color: white;
                font-weight: bold;
            }
            QProgressBar::chunk {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #3498db, stop:1 #2ecc71);
                border-radius: 5px;
            }
        """)
        layout.addWidget(self.coverage_progress)
        layout.addSpacing(15)
        search_layout = QHBoxLayout()
        search_layout.setSpacing(10)
        search_icon = QLabel("🔍")
        search_icon.setStyleSheet("font-size: 18px;")
        self.search_input = QLineEdit()
        self.search_input.setPlaceholderText("Search songs by name...")
        self.search_input.setStyleSheet("""
            QLineEdit {
                background: #0b141d;
                border: 2px solid #1f3545;
                border-radius: 8px;
                padding: 8px 12px;
                color: #ecf0f1;
                font-size: 14px;
            }
            QLineEdit:focus { border-color: #3498db; }
        """)
        self.search_input.textChanged.connect(self._on_search_changed)
        search_layout.addWidget(search_icon)
        search_layout.addWidget(self.search_input)
        layout.addLayout(search_layout)
        layout.addSpacing(10)
        self.track_list = SearchableListWidget()
        self.track_list.setStyleSheet("""
            QListWidget {
                background: #0b141d;
                border: 2px solid #1f3545;
                border-radius: 12px;
                outline: none;
            }
            QListWidget::item {
                background: transparent;
                border: none;
                padding: 0px;
                margin: 0px;
            }
            QListWidget::item:selected {
                background: #3498db;
                border-radius: 4px;
            }
            QScrollBar:vertical { width: 22px; }
        """)
        self.track_list.itemDoubleClicked.connect(self.go_to_offset_step)
        layout.addWidget(self.track_list)
        self.stack.addWidget(page)

    def setup_step2_offset(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setSpacing(0)
        layout.setContentsMargins(0, 0, 0, 0)
        self.lbl_step2 = QLabel("STEP 2: Choose music custom starting point in seconds")
        self.lbl_step2.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC;")
        self.lbl_step2.setAlignment(Qt.AlignCenter)
        self.lbl_step2.setFixedHeight(30)
        layout.addWidget(self.lbl_step2)
        layout.addSpacing(10)
        self.wave_preview = QLabel("Visualizing audio...")
        self.wave_preview.setFixedHeight(250)
        self.wave_preview.setStyleSheet("background: #000; border: 3px solid #266b89; border-radius: 12px;")
        self.wave_preview.setAlignment(Qt.AlignCenter)
        self.wave_preview.installEventFilter(self)
        self._wave_caret = QLabel(self)
        self._wave_caret.setStyleSheet("background: #3498db;")
        self._wave_caret.setFixedWidth(2)
        self._wave_caret.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._wave_caret.hide()
        layout.addWidget(self.wave_preview)
        layout.addSpacing(10)
        self.offset_slider = MergerTrimmedSlider()
        self.offset_slider.setFixedHeight(70)
        self.offset_slider.setStyleSheet("QSlider::handle:horizontal { width: 10px; background: #2196F3; }")
        self.offset_slider.valueChanged.connect(self._on_slider_seek)
        try:
            self.offset_slider.sliderPressed.connect(self._on_drag_start)
            self.offset_slider.sliderReleased.connect(self._on_drag_end)
        except Exception: pass
        layout.addWidget(self.offset_slider)
        layout.addSpacing(10)
        self.stack.addWidget(page)

    def setup_step3_timeline(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setSpacing(0)
        layout.setContentsMargins(0, 0, 0, 0)
        self.lbl_step3 = QLabel("STEP 3: Preview Project Timeline")
        self.lbl_step3.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC;")
        self.lbl_step3.setAlignment(Qt.AlignCenter)
        self.lbl_step3.setFixedHeight(30)
        layout.addWidget(self.lbl_step3)
        layout.addSpacing(10)
        player_volume_row = QHBoxLayout()
        player_volume_row.setSpacing(15)
        self.video_container = QWidget()
        self.video_container.setMinimumHeight(350)
        self.video_container.setStyleSheet("background: #000; border: 2px solid #34495e;")
        self.video_container.setAttribute(Qt.WA_OpaquePaintEvent)
        self.video_container.setAttribute(Qt.WA_NoSystemBackground)
        player_volume_row.addWidget(self.video_container, 1)
        self.video_container.winId()
        vol_col = QVBoxLayout()
        vol_col.setSpacing(10)
        vol_labels = QHBoxLayout()
        v_l = QLabel("VID"); v_l.setStyleSheet("font-size: 9px; font-weight: bold;"); v_l.setAlignment(Qt.AlignCenter)
        m_l = QLabel("MUS"); m_l.setStyleSheet("font-size: 9px; font-weight: bold;"); m_l.setAlignment(Qt.AlignCenter)
        vol_labels.addWidget(v_l); vol_labels.addWidget(m_l)
        vol_col.addLayout(vol_labels)
        vol_sliders = QHBoxLayout()
        self.video_vol_slider = QSlider(Qt.Vertical)
        self.video_vol_slider.setRange(0, 100); self.video_vol_slider.setValue(100); self.video_vol_slider.setFixedWidth(40)
        self.video_vol_slider.setStyleSheet(MergerUIStyle.SLIDER_VOLUME_VERTICAL_METALLIC)
        self.video_vol_slider.setCursor(Qt.PointingHandCursor)
        self.video_vol_slider.valueChanged.connect(self._on_video_vol_changed)
        self.music_vol_slider = QSlider(Qt.Vertical)
        self.music_vol_slider.setRange(0, 100); self.music_vol_slider.setValue(80); self.music_vol_slider.setFixedWidth(40)
        self.music_vol_slider.setStyleSheet(MergerUIStyle.SLIDER_MUSIC_VERTICAL_METALLIC)
        self.music_vol_slider.setCursor(Qt.PointingHandCursor)
        self.music_vol_slider.valueChanged.connect(self._on_music_vol_changed)
        vol_sliders.addWidget(self.video_vol_slider); vol_sliders.addWidget(self.music_vol_slider)
        vol_col.addLayout(vol_sliders)
        player_volume_row.addLayout(vol_col)
        layout.addLayout(player_volume_row, 1)
        self.timeline_container = QWidget()
        self.timeline_container.setFixedHeight(100)
        container_layout = QVBoxLayout(self.timeline_container)
        container_layout.setContentsMargins(0,0,0,0)
        self.timeline = MergerTimelineWidget(self)
        self.timeline.clicked_pos.connect(self._on_timeline_seek)
        container_layout.addWidget(self.timeline)
        self.splash_overlay = QWidget(self.timeline_container)
        self.splash_overlay.setStyleSheet("background: rgba(15, 25, 35, 240); border-radius: 8px;")
        splash_layout = QVBoxLayout(self.splash_overlay)
        self.splash_lbl = QLabel("♪  PREPARING HIGH-DENSITY TIMELINE...  ♪")
        self.splash_lbl.setStyleSheet("color: #7DD3FC; font-size: 18px; font-weight: bold;")
        self.splash_lbl.setAlignment(Qt.AlignCenter)
        splash_layout.addWidget(self.splash_lbl)
        self.splash_overlay.hide()
        layout.addWidget(self.timeline_container)
        self.stack.addWidget(page)

    def _on_page_changed(self, index):
        if not hasattr(self, 'btn_nav_next'): return
        if index in (1, 2):
            self.btn_play_video.setVisible(True)
            self.btn_play_video.setText("  PLAY")
            self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        else:
            self.btn_play_video.setVisible(False)
        if index == 2:
            self.btn_nav_next.setText("✓  DONE")
            if self.width() < 1500:
                self._resize_from_center(1600, 800)
            self._prepare_timeline_data()
        elif index in (0, 1):
            self.btn_nav_next.setText("NEXT")
            if self.width() > 1500:
                self._resize_from_center(1300, 650)
        if index == 0:
            self.update_coverage_ui()
        self._sync_caret()

    def _resize_from_center(self, w, h):
        old_center = self.geometry().center()
        self.resize(w, h)
        new_rect = self.geometry()
        new_rect.moveCenter(old_center)
        self.setGeometry(new_rect)

    def _on_nav_next_clicked(self):
        idx = self.stack.currentIndex()
        self.logger.info(f"WIZARD: User clicked NEXT on step {idx + 1}")
        if idx == 0:
            self.go_to_offset_step()
        elif idx == 1:
            self.confirm_current_track()
        elif idx == 2:
            self.logger.info("WIZARD: User clicked DONE on Timeline. Finishing.")
            self.stop_previews()
            self.accept()

    def _on_nav_back_clicked(self):
        idx = self.stack.currentIndex()
        if idx > 0:
            if idx == 1:
                self.btn_back.hide()
            self.stack.setCurrentIndex(idx - 1)
            self.stop_previews()

    def go_to_offset_step(self):
        item = self.track_list.currentItem()
        if not item: 
            QMessageBox.warning(self, "No Selection", "Please click on a song first!")
            return
        self.current_track_path = item.data(Qt.UserRole)
        if not self.current_track_path: return
        self.logger.info(f"WIZARD: User selected song: {os.path.basename(self.current_track_path)}")
        self._last_good_vlc_ms = 0
        self.offset_slider.blockSignals(True)
        self.offset_slider.setValue(0)
        self.offset_slider.blockSignals(False)
        self._sync_caret()
        self.stack.setCurrentIndex(1)
        self.start_waveform_generation()
        self.btn_back.show()

    def start_waveform_generation(self):
        self.wave_preview.setText("Visualizing audio...")
        self._pm_src = None
        if not self.current_track_path: return
        self.logger.info(f"WIZARD_STEP2: Initializing Waveform Generation for {os.path.basename(self.current_track_path)}")
        self.current_track_dur = self._probe_media_duration(self.current_track_path)
        self.offset_slider.setRange(0, int(self.current_track_dur * 1000))
        self.offset_slider.setValue(0)
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        tf = tempfile.NamedTemporaryFile(suffix=".png", delete=False)
        self._temp_png = tf.name; tf.close()
        self.logger.debug("WIZARD_STEP2: Process Phase 1 - Constructing Filter Chain (Compand -> ShowWavesPic)")
        self.logger.debug("WIZARD_STEP2: Process Phase 2 - Dynamic Range Normalization (Attacks: 0, Peak Cap: -3dB)")
        cmd = [ffmpeg_exe, "-y", "-hide_banner", "-loglevel", "error", "-i", self.current_track_path, "-frames:v", "1", 
               "-filter_complex", "compand=attacks=0:points=-80/-80|-20/-20|-15/-10|0/-3,showwavespic=s=1200x300:colors=0x7DD3FC", self._temp_png]
        self.logger.info(f"WIZARD_STEP2: Executing FFmpeg (CPU-Bound Rendering): {' '.join(cmd)}")
        try:
            start_t = time.time()
            proc = subprocess.Popen(cmd, creationflags=0x08000000)
            proc.wait(15)
            elapsed = time.time() - start_t
            if os.path.exists(self._temp_png):
                self.logger.info(f"WIZARD_STEP2: Render Complete. Size: {os.path.getsize(self._temp_png)} bytes. Elapsed: {elapsed:.2f}s")
                self._pm_src = QPixmap(self._temp_png)
                self._refresh_wave_scaled()
            else:
                self.logger.error("WIZARD_STEP2: Render Failed - Output file not found.")
        except Exception as e:
            self.logger.error(f"WIZARD_STEP2: Critical Execution Error: {e}")
            self.wave_preview.setText(f"Waveform failed: {e}")

    def _on_slider_seek(self, val_ms):
        if self._dragging or self._wave_dragging: return
        if self._player: self._player.set_time(val_ms)
        self._sync_caret()

    def _on_drag_start(self): self._dragging = True

    def _on_drag_end(self):
        self._dragging = False
        if self._player: self._player.set_time(self.offset_slider.value())
        self._sync_caret()

    def _on_video_vol_changed(self, val):
        if self._video_player: self._video_player.audio_set_volume(val)

    def _on_music_vol_changed(self, val):
        if self._player: self._player.audio_set_volume(val)
        self.logger.info(f"WIZARD: Music Volume changed to {val}%")

    def toggle_video_preview(self):
        try:
            if self.stack.currentIndex() == 1:
                st = self._player.get_state()
                if st == 3:
                    self.logger.info("WIZARD: User clicked PAUSE.")
                    self._player.pause()
                    self.btn_play_video.setText("  PLAY")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                    if hasattr(self, '_play_timer'): self._play_timer.stop()
                else:
                    self.logger.info(f"WIZARD: User clicked PLAY at offset {self.offset_slider.value()/1000.0:.1f}s")
                    if st in (0, 5, 6, 7):
                        m = self.vlc.media_new(self.current_track_path)
                        self._player.set_media(m)
                    self._player.play()

                    def _after_start():
                        self._player.set_time(int(self.offset_slider.value()))
                        if not hasattr(self, '_play_timer'):
                            self._play_timer = QTimer(self); self._play_timer.setInterval(50); self._play_timer.timeout.connect(self._on_play_tick)
                        self._play_timer.start()
                        self.btn_play_video.setText("  PAUSE")
                        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    QTimer.singleShot(90, _after_start)
            elif self.stack.currentIndex() == 2:
                st = self._video_player.get_state()
                if st == 3:
                    self.logger.info("WIZARD: User clicked PAUSE Project.")
                    self._video_player.pause()
                    if self._player: self._player.pause()
                    self.btn_play_video.setText("  PLAY")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                else:
                    self.logger.info("WIZARD: User clicked PLAY Project.")
                    if st in (0, 5, 6, 7):
                        self._sync_all_players_to_time(self.timeline.current_time)
                    self._video_player.play()
                    if self._player: self._player.play()
                    self.btn_play_video.setText("  PAUSE")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    if not hasattr(self, '_play_timer') or not self._play_timer.isActive():
                        self._play_timer = QTimer(self); self._play_timer.setInterval(50); self._play_timer.timeout.connect(self._on_play_tick); self._play_timer.start()
        except Exception as e:
            self.logger.error(f"WIZARD: Playback toggle failed: {e}")

    def _on_play_tick(self):
        if self._is_syncing: return
        self._is_syncing = True
        try:
            now = time.time()
            do_heavy = (now - self._last_tick_ts > 0.1)
            if do_heavy: self._last_tick_ts = now
            if self.stack.currentIndex() == 1 and self._player:
                try:
                    st = self._player.get_state()
                    if st == 3:
                        vlc_ms = int(self._player.get_time() or 0)
                        if vlc_ms <= 0: vlc_ms = self._last_good_vlc_ms
                        else: self._last_good_vlc_ms = vlc_ms
                        vlc_ms = int(vlc_ms + PREVIEW_VISUAL_LEAD_MS)
                        max_ms = self.offset_slider.maximum()
                        vlc_ms = max(0, min(max_ms, vlc_ms))
                        if vlc_ms >= max_ms - 10:
                            self._on_vlc_ended()
                            return
                        if not self._dragging and not self._wave_dragging:
                            self.offset_slider.blockSignals(True)
                            self.offset_slider.setValue(vlc_ms)
                            self.offset_slider.blockSignals(False)
                            self._sync_caret()
                    elif st == 6: self._on_vlc_ended()
                except: pass
            if self.stack.currentIndex() == 2 and self._video_player:
                try:
                    if now - self._last_seek_ts < 0.5:
                        self._last_clock_ts = now; return
                    st = self._video_player.get_state()
                    if st in (1, 2, 3):
                        v_time = self._video_player.get_time() / 1000.0
                        if v_time < 0: v_time = 0.0
                        clock_delta = now - self._last_clock_ts; self._last_clock_ts = now
                        if do_heavy:
                            curr_media = self._video_player.get_media()
                            if curr_media:
                                curr_mrl = str(curr_media.get_mrl()).lower().replace("%20", " ").replace("file:///", "").replace("/", "\\")
                                temp_elapsed = 0.0; matched_idx = -1
                                for i, seg in enumerate(self.video_segments):
                                    seg_path_norm = seg["path"].lower().replace("/", "\\")
                                    if seg_path_norm in curr_mrl or curr_mrl in seg_path_norm or os.path.basename(seg_path_norm).lower() in curr_mrl:
                                        matched_idx = i; break
                                    temp_elapsed += seg["duration"]
                                if matched_idx != -1:
                                    self._current_elapsed_offset = temp_elapsed
                                    if st == 3 and v_time >= self.video_segments[matched_idx]["duration"] - 0.2:
                                        if matched_idx < len(self.video_segments) - 1:
                                            next_path = self.video_segments[matched_idx + 1]["path"]
                                            m = self.vlc.media_new(next_path); self._video_player.set_media(m); self._video_player.play()
                                        else:
                                            self.toggle_video_preview(); self.timeline.set_current_time(self.total_video_sec); return
                                    if self._player: self._sync_music_only_to_time(self._current_elapsed_offset + v_time)
                        if st == 3: project_time = self._current_elapsed_offset + v_time
                        else: project_time = self.timeline.current_time + clock_delta
                        project_time = min(self.total_video_sec, max(0.0, project_time))
                        self.timeline.set_current_time(project_time); self._sync_caret()
                    elif st == 6:
                        self.logger.info("WIZARD: Video reached end of project. Stopping music.")
                        if self._player: self._player.pause()
                        self.btn_play_video.setText("  PLAY")
                        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                        if hasattr(self, '_play_timer'): self._play_timer.stop()
                        self.timeline.set_current_time(self.total_video_sec)
                        self._sync_caret()
                    else: self._last_clock_ts = now
                except: pass
            else: self._last_clock_ts = now
        finally: self._is_syncing = False

    def _sync_music_only_to_time(self, project_time):
        if not self._video_player: return
        v_state = self._video_player.get_state()
        if v_state != 3:
            if self._player: self._player.pause()
            return
        elapsed = 0.0; target_idx = -1; music_offset = 0.0
        for i, (path, start_off, dur) in enumerate(self.selected_tracks):
            if elapsed + dur > project_time:
                target_idx = i; music_offset = (project_time - elapsed) + start_off; break
            elapsed += dur
        if target_idx != -1:
            target_path = self.selected_tracks[target_idx][0]
            if target_path != self._last_m_mrl:
                m = self.vlc.media_new(target_path); self._player.set_media(m); self._player.play(); self._player.set_time(int(music_offset * 1000)); self._last_m_mrl = target_path
            else:
                try:
                    curr_audio_time = self._player.get_time() / 1000.0
                    if abs(curr_audio_time - music_offset) > 0.5: self._player.set_time(int(music_offset * 1000))
                    if self._player.get_state() != 3: self._player.play()
                except: pass
        else:
            self._player.stop(); self._last_m_mrl = ""

    def _on_timeline_seek(self, pct):
        self._last_seek_ts = time.time(); target_sec = pct * self.total_video_sec
        is_playing = False
        if self._video_player: is_playing = (self._video_player.get_state() == 3)
        self.timeline.set_current_time(target_sec)
        if self.stack.currentIndex() == 2 and self._video_player:
            target_vid_idx = len(self.video_segments) - 1; video_offset = 0.0; current_count_elapsed = 0.0
            for i, seg in enumerate(self.video_segments):
                if current_count_elapsed + seg["duration"] > target_sec + 0.001:
                    target_vid_idx = i; video_offset = target_sec - current_count_elapsed; break
                current_count_elapsed += seg["duration"]
            final_elapsed = 0.0
            for i in range(target_vid_idx): final_elapsed += self.video_segments[i]["duration"]
            self._current_elapsed_offset = final_elapsed
            target_path = self.video_segments[target_vid_idx]["path"]
            curr_media = self._video_player.get_media()
            curr_mrl = str(curr_media.get_mrl()).replace("%20", " ") if curr_media else ""
            if target_path.replace("\\", "/").lower() not in curr_mrl.lower():
                m = self.vlc.media_new(target_path); self._video_player.set_media(m)
                if is_playing: self._video_player.play()
            self._video_player.set_time(int(video_offset * 1000))
            if not is_playing: self._video_player.set_pause(True)
        self._sync_all_players_to_time(target_sec)
        if not is_playing:
            if self._video_player: self._video_player.set_pause(True)
            if self._player: self._player.set_pause(True)
        self._sync_caret()

    def _sync_all_players_to_time(self, timeline_sec):
        elapsed = 0.0; target_video_idx = 0; video_offset = 0.0
        for i, seg in enumerate(self.video_segments):
            if elapsed + seg["duration"] > timeline_sec:
                target_video_idx = i; video_offset = timeline_sec - elapsed; break
            elapsed += seg["duration"]
        if self._video_player:
            target_path = self.video_segments[target_video_idx]["path"]; curr_media = self._video_player.get_media()
            if not curr_media or target_path.replace("\\", "/") not in str(curr_media.get_mrl()).replace("%20", " "):
                m = self.vlc.media_new(target_path); self._video_player.set_media(m); self._video_player.play()
            self._video_player.set_time(int(video_offset * 1000))
        elapsed = 0.0; target_music_idx = -1; music_offset = 0.0
        for i, (path, start_off, dur) in enumerate(self.selected_tracks):
            if elapsed + dur > timeline_sec:
                target_music_idx = i; music_offset = (timeline_sec - elapsed) + start_off; break
            elapsed += dur
        if self._player:
            if target_music_idx != -1:
                target_path = self.selected_tracks[target_music_idx][0]
                if target_path != self._last_m_mrl:
                    m = self.vlc.media_new(target_path); self._player.set_media(m); self._player.play(); self._last_m_mrl = target_path
                self._player.set_time(int(music_offset * 1000))
            else: self._player.stop()

    def _on_vlc_ended(self):
        self.btn_play_video.setText("  PLAY"); self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        if hasattr(self, '_play_timer'): self._play_timer.stop()
        self.offset_slider.blockSignals(True); self.offset_slider.setValue(0); self.offset_slider.blockSignals(False)
        self._last_good_vlc_ms = 0; self._sync_caret()
        if self._player: self._player.stop()

    def _prepare_timeline_data(self):
        videos = []; video_info_list = []
        for i in range(self.parent_window.listw.count()):
            it = self.parent_window.listw.item(i); p = it.data(Qt.UserRole); probe_data = it.data(Qt.UserRole + 1) or {}
            dur = float(probe_data.get("format", {}).get("duration", 0.0))
            videos.append({"path": p, "duration": dur, "thumbs": []}); video_info_list.append((p, dur))
        self.video_segments = videos
        music = []; music_segments_info = []
        if self.selected_tracks:
            covered = 0.0
            for p, offset, dur in self.selected_tracks:
                music.append({"path": p, "duration": dur, "offset": offset, "wave": QPixmap()}); music_segments_info.append((p, offset, dur)); covered += dur
            cycle_limit = 0
            while covered < self.total_video_sec - 0.1 and cycle_limit < 20:
                p, _, _ = self.selected_tracks[-1]; full_dur = self._probe_media_duration(p)
                music.append({"path": p, "duration": full_dur, "offset": 0.0, "wave": QPixmap()}); music_segments_info.append((p, 0.0, full_dur)); covered += full_dur; cycle_limit += 1
        self.music_segments = music; self.timeline.set_data(self.total_video_sec, self.video_segments, self.music_segments)
        self._workers_running = 0
        if hasattr(self, '_video_worker') and self._video_worker.isRunning(): self._video_worker.terminate()
        if hasattr(self, '_music_worker') and self._music_worker.isRunning(): self._music_worker.terminate()
        self.splash_overlay.setGeometry(self.timeline.rect()); self.splash_overlay.show(); self.splash_overlay.raise_()

        def _check_finished():
            self._workers_running -= 1
            if self._workers_running <= 0: self.splash_overlay.hide()
        self._video_worker = VideoFilmstripWorker(video_info_list, self.bin_dir)
        self._video_worker.setPriority(QtCore.QThread.LowPriority); self._video_worker.asset_ready.connect(self._on_video_asset_ready); self._video_worker.finished.connect(_check_finished)
        self._music_worker = MusicWaveformWorker(music_segments_info, self.bin_dir)
        self._music_worker.setPriority(QtCore.QThread.LowPriority); self._music_worker.asset_ready.connect(self._on_music_asset_ready); self._music_worker.finished.connect(_check_finished)
        self._workers_running = 2; self._video_worker.start(); self._music_worker.start()

    def _on_video_asset_ready(self, idx, thumbs):
        if 0 <= idx < len(self.video_segments): self.video_segments[idx]["thumbs"] = thumbs; self.timeline.update()

    def _on_music_asset_ready(self, idx, pixmap):
        if 0 <= idx < len(self.music_segments): self.music_segments[idx]["wave"] = pixmap; self.timeline.update()

    def confirm_current_track(self):
        """Records the current track's offset selection and checks coverage."""
        if self._player: self._player.stop()
        offset = self.offset_slider.value() / 1000.0
        actual_dur = self.current_track_dur - offset
        self.logger.info(f"WIZARD: User confirmed track '{os.path.basename(self.current_track_path)}' starting at {offset:.1f}s (Covers {actual_dur:.1f}s)")
        self.selected_tracks.append((self.current_track_path, offset, actual_dur))
        self.update_coverage_ui()
        covered = sum(t[2] for t in self.selected_tracks)
        if covered < self.total_video_sec - 0.5:
            self.logger.info(f"WIZARD: More music needed ({covered:.1f}s / {self.total_video_sec:.1f}s). Returning to Step 1.")
            QMessageBox.information(self, "Need more music", 
                f"You've covered {covered:.1f}s of your {self.total_video_sec:.1f}s project.\n\n"
                "Please select another song to fill the remaining time!")
            self.stack.setCurrentIndex(0)
            self.btn_back.hide()
        else:
            self.logger.info("WIZARD: Coverage complete. Moving to Step 3 Timeline.")
            self.stack.setCurrentIndex(2)

    def _sync_caret(self):
        try:
            curr_idx = self.stack.currentIndex()
            if curr_idx == 1:
                if not self.wave_preview.isVisible(): self._wave_caret.hide(); return
                max_ms = self.offset_slider.maximum()
                if max_ms <= 0: self._wave_caret.hide(); return
                frac = self.offset_slider.value() / float(max_ms)
                label_pos = self.wave_preview.mapTo(self, QPoint(0, 0))
                x = label_pos.x() + self._draw_x0 + int(frac * self._draw_w) - 1; y = label_pos.y() + self._draw_y0
                self._wave_caret.setGeometry(x, y, 2, self._draw_h); self._wave_caret.show(); self._wave_caret.raise_()
            elif curr_idx == 2:
                if not self.timeline.isVisible(): self._wave_caret.hide(); return
                frac = self.timeline.current_time / self.total_video_sec
                tl_pos = self.timeline.mapTo(self, QPoint(0, 0))
                x = tl_pos.x() + int(frac * self.timeline.width()) - 1; y = tl_pos.y(); h = self.timeline.height()
                self._wave_caret.setGeometry(x, y, 2, h); self._wave_caret.show(); self._wave_caret.raise_()
            else: self._wave_caret.hide()
        except: self._wave_caret.hide()

    def _probe_media_duration(self, path):
        try:
            ffprobe = os.path.join(self.bin_dir, "ffprobe.exe")
            cmd = [ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path]
            r = subprocess.run(cmd, capture_output=True, text=True, creationflags=0x08000000, timeout=5)
            return float(r.stdout.strip()) if r.returncode == 0 else 0.0
        except: return 0.0

    def _restore_geometry(self):
        try:
            if not hasattr(self.parent_window, "config_manager") or not self.parent_window.config_manager:
                self._center_on_primary()
                return
            cfg = self.parent_window.config_manager.config
            geom = cfg.get("music_wizard_geometry")
            if geom:
                from PyQt5.QtCore import QByteArray
                self.restoreGeometry(QByteArray.fromBase64(geom.encode()))
            else:
                self._center_on_primary()
        except Exception:
            self._center_on_primary()

    def _center_on_primary(self, w=1300, h=650):
        screen = QApplication.primaryScreen().availableGeometry()
        x = screen.x() + (screen.width() - w) // 2
        y = screen.y() + (screen.height() - h) // 2
        self.setGeometry(x, y, w, h)

    def _save_geometry(self):
        try:
            if not hasattr(self.parent_window, "config_manager") or not self.parent_window.config_manager:
                return
            cfg = dict(self.parent_window.config_manager.config)
            cfg["music_wizard_geometry"] = self.saveGeometry().toBase64().data().decode()
            self.parent_window.config_manager.save_config(cfg)
        except Exception:
            pass

    def stop_previews(self):
        if self._player: self._player.stop()
        if self._video_player: self._video_player.stop()
        if hasattr(self, '_play_timer'): self._play_timer.stop()

    def closeEvent(self, e):
        self._save_geometry()
        self.stop_previews()
        super().closeEvent(e)

    def _on_search_changed(self, text): self._search_timer.start(300)

    def _do_search(self):
        txt = self.search_input.text().lower()
        for i in range(self.track_list.count()):
            it = self.track_list.item(i); w = self.track_list.itemWidget(it)
            it.setHidden(txt not in w.name_lbl.text().lower() if w else False)

    def update_coverage_ui(self):
        covered = sum(t[2] for t in self.selected_tracks)
        pct = int((covered / self.total_video_sec) * 100) if self.total_video_sec > 0 else 0
        self.coverage_progress.setValue(min(100, pct))
        self.coverage_progress.setFormat(f"Music Coverage: {covered:.1f}s / {self.total_video_sec:.1f}s (%p%)")

    def _refresh_wave_scaled(self):
        if not self._pm_src: return
        cr = self.wave_preview.contentsRect()
        scaled = self._pm_src.scaled(cr.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation)
        self.wave_preview.setPixmap(scaled)
        self._draw_w = scaled.width(); self._draw_h = scaled.height()
        self._draw_x0 = (cr.width() - self._draw_w) // 2; self._draw_y0 = (cr.height() - self._draw_h) // 2
        self._sync_caret()

    def eventFilter(self, obj, event):
        if obj is self.wave_preview:
            if event.type() == QtCore.QEvent.MouseButtonPress and event.button() == Qt.LeftButton:
                try:
                    if self._draw_w <= 1: return True
                    self._wave_dragging = True
                    self._set_time_from_wave_x(event.pos().x())
                    return True
                except Exception: return True
            if event.type() == QtCore.QEvent.MouseMove and self._wave_dragging:
                try:
                    self._set_time_from_wave_x(event.pos().x())
                    return True
                except Exception: return True
            if event.type() == QtCore.QEvent.MouseButtonRelease:
                self._wave_dragging = False
                return True
        return super().eventFilter(obj, event)

    def _set_time_from_wave_x(self, x):
        if self._draw_w <= 1: return
        rel = (x - self._draw_x0) / float(self._draw_w)
        rel = max(0.0, min(1.0, rel))
        target_ms = int(rel * self.offset_slider.maximum())
        self.offset_slider.setValue(target_ms)
        if self._player:
            self._player.set_time(target_ms)
            self._last_good_vlc_ms = target_ms
        self._sync_caret()
