import os
import sys
import tempfile
import subprocess
import traceback
from PyQt5.QtWidgets import (
    QDialog, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, 
    QListWidget, QListWidgetItem, QStackedWidget, QWidget,
    QSizePolicy, QProgressBar, QMessageBox
)

from PyQt5.QtCore import Qt, pyqtSignal, QTimer, QSize
from PyQt5.QtGui import QPixmap, QColor, QFont
from utilities.merger_trimmed_slider import MergerTrimmedSlider
from utilities.merger_ui_style import MergerUIStyle
try:
    import vlc as _vlc_mod
except Exception:
    _vlc_mod = None

class MergerMusicWizard(QDialog):
    def __init__(self, parent, vlc_instance, bin_dir, mp3_dir, total_video_sec):
        super().__init__(parent)
        self.parent_window = parent
        self.vlc = vlc_instance
        self.bin_dir = bin_dir
        self.mp3_dir = mp3_dir
        self.total_video_sec = total_video_sec
        self.selected_tracks = [] 
        self._player = None
        self._temp_png = None
        self.setWindowTitle("Background Music Selection Wizard")
        self.setModal(True)
        self.resize(1000, 750)
        self.setup_ui()
        self.load_available_mp3s()

    def setup_ui(self):
        self.main_layout = QVBoxLayout(self)
        self.main_layout.setContentsMargins(40, 40, 40, 40)
        self.main_layout.setSpacing(25)
        self.lbl_wizard_title = QLabel("Guided Music Selection")
        self.lbl_wizard_title.setStyleSheet("font-size: 28px; font-weight: bold; color: white;")
        self.lbl_wizard_title.setAlignment(Qt.AlignCenter)
        self.main_layout.addWidget(self.lbl_wizard_title)
        self.stack = QStackedWidget()
        self.setup_step1_select()
        self.setup_step2_offset()
        self.setup_step3_summary()
        self.main_layout.addWidget(self.stack)
        self.nav_layout = QHBoxLayout()
        self.btn_cancel = QPushButton("EXIT WIZARD")
        self.btn_cancel.setFixedSize(180, 45)
        self.btn_cancel.setStyleSheet(MergerUIStyle.BUTTON_TOOL)
        self.btn_cancel.clicked.connect(self.reject)
        self.nav_layout.addWidget(self.btn_cancel)
        self.nav_layout.addStretch(1)
        self.main_layout.addLayout(self.nav_layout)

    def setup_step1_select(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        self.lbl_step1 = QLabel("STEP 1: Pick a song from your folder")
        self.lbl_step1.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC;")
        layout.addWidget(self.lbl_step1)
        self.coverage_progress = QProgressBar()
        self.coverage_progress.setFixedHeight(30)
        self.coverage_progress.setStyleSheet(MergerUIStyle.PROGRESS_BAR)
        self.coverage_progress.setTextVisible(True)
        self.coverage_progress.setFormat("Coverage: %p%")
        layout.addWidget(self.coverage_progress)
        self.lbl_coverage = QLabel("Music coverage: 0.0s / 0.0s")
        self.lbl_coverage.setStyleSheet("font-size: 14px; font-weight: bold;")
        layout.addWidget(self.lbl_coverage)
        self.track_list = QListWidget()
        self.track_list.setStyleSheet("font-size: 18px; padding: 15px; background: #1f3545; border-radius: 10px;")
        self.track_list.itemDoubleClicked.connect(self.go_to_offset_step)
        layout.addWidget(self.track_list)
        self.btn_select = QPushButton("NEXT: SET STARTING POINT")
        self.btn_select.setFixedSize(350, 60)
        self.btn_select.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_select.clicked.connect(self.go_to_offset_step)
        layout.addWidget(self.btn_select, 0, Qt.AlignCenter)
        self.stack.addWidget(page)

    def setup_step2_offset(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        self.lbl_step2 = QLabel("STEP 2: Choose when the music starts")
        self.lbl_step2.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC;")
        layout.addWidget(self.lbl_step2)
        self.wave_preview = QLabel("Visualizing audio...")
        self.wave_preview.setFixedHeight(250)
        self.wave_preview.setStyleSheet("background: #000; border: 3px solid #266b89; border-radius: 12px;")
        self.wave_preview.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.wave_preview)
        self.offset_slider = MergerTrimmedSlider()
        self.offset_slider.setFixedHeight(70)
        self.offset_slider.setStyleSheet("QSlider::handle:horizontal { width: 10px; background: #2196F3; }")
        layout.addWidget(self.offset_slider)
        btn_row = QHBoxLayout()
        self.btn_play_preview = QPushButton("▶ PLAY PREVIEW")
        self.btn_play_preview.setFixedSize(220, 55)
        self.btn_play_preview.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_play_preview.clicked.connect(self.toggle_preview)
        self.btn_confirm_track = QPushButton("✅ ADD TRACK TO VIDEO")
        self.btn_confirm_track.setFixedSize(300, 60)
        self.btn_confirm_track.setStyleSheet(MergerUIStyle.BUTTON_MERGE)
        self.btn_confirm_track.clicked.connect(self.confirm_current_track)
        btn_row.addStretch(1)
        btn_row.addWidget(self.btn_play_preview)
        btn_row.addSpacing(30)
        btn_row.addWidget(self.btn_confirm_track)
        btn_row.addStretch(1)
        layout.addLayout(btn_row)
        self.stack.addWidget(page)

    def setup_step3_summary(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        self.lbl_step3 = QLabel("WIZARD COMPLETE: Everything looks good!")
        self.lbl_step3.setStyleSheet("font-size: 24px; font-weight: bold; color: #2ecc71;")
        layout.addWidget(self.lbl_step3)
        self.summary_list = QListWidget()
        self.summary_list.setStyleSheet("background: #1f3545; padding: 15px; font-size: 16px;")
        layout.addWidget(self.summary_list)
        self.btn_finish = QPushButton("FINISH & RETURN TO MERGER")
        self.btn_finish.setFixedSize(400, 70)
        self.btn_finish.setStyleSheet(MergerUIStyle.BUTTON_MERGE)
        self.btn_finish.clicked.connect(self.accept)
        layout.addWidget(self.btn_finish, 0, Qt.AlignCenter)
        self.stack.addWidget(page)

    def load_available_mp3s(self):
        self.track_list.clear()
        if not os.path.exists(self.mp3_dir): return
        files = sorted([f for f in os.listdir(self.mp3_dir) if f.lower().endswith(".mp3")])
        if not files:
            self.track_list.addItem("No MP3 files found in ./mp3 folder.")
            self.btn_select.setEnabled(False)
            return
        for f in files:
            item = QListWidgetItem(f"♪  {f}")
            item.setData(Qt.UserRole, os.path.join(self.mp3_dir, f))
            self.track_list.addItem(item)
        self.update_coverage_ui()

    def update_coverage_ui(self):
        covered = sum(t[2] for t in self.selected_tracks)
        target = self.total_video_sec
        val = int(min(100, (covered / target * 100))) if target > 0 else 0
        self.coverage_progress.setValue(val)
        self.lbl_coverage.setText(f"Your video is {target:.1f}s long. Selected music covers: {covered:.1f}s")
        if covered >= target:
            self.lbl_coverage.setStyleSheet("color: #2ecc71; font-weight: bold;")
            self.btn_select.setText("DONE: GO TO SUMMARY")
        else:
            self.lbl_coverage.setStyleSheet("color: #ffa500; font-weight: bold;")
            self.btn_select.setText("NEXT: SET STARTING POINT")

    def go_to_offset_step(self):
        item = self.track_list.currentItem()
        if not item: 
            QMessageBox.warning(self, "No Selection", "Please click on a song first!")
            return
        self.current_track_path = item.data(Qt.UserRole)
        if not self.current_track_path: return
        self.stack.setCurrentIndex(1)
        self.start_waveform_generation()

    def start_waveform_generation(self):
        self.wave_preview.setText("Analyzing Audio Waveform...")
        self.wave_preview.setPixmap(QPixmap())

        def _run():
            try:
                ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
                dur_cmd = [ffmpeg_exe, "-i", self.current_track_path, "-f", "null", "-"]
                r = subprocess.run([os.path.join(self.bin_dir, "ffprobe.exe"), "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", self.current_track_path], capture_output=True, text=True, creationflags=0x08000000)
                self.current_track_dur = float(r.stdout.strip() or 0)
                tf = tempfile.NamedTemporaryFile(suffix=".png", delete=False)
                tmp_path = tf.name; tf.close()
                cmd = [ffmpeg_exe, "-y", "-i", self.current_track_path, "-filter_complex", "volume=3.0,showwavespic=s=1000x250:colors=#7DD3FC", "-frames:v", "1", tmp_path]
                subprocess.run(cmd, capture_output=True, creationflags=0x08000000)
                if os.path.exists(tmp_path):
                    self._temp_png = tmp_path
                    QTimer.singleShot(0, lambda: self.wave_preview.setPixmap(QPixmap(tmp_path).scaled(self.wave_preview.size(), Qt.KeepAspectRatio)))
                QTimer.singleShot(0, lambda: self.offset_slider.setRange(0, int(self.current_track_dur * 1000)))
                QTimer.singleShot(0, lambda: self.offset_slider.set_duration_ms(int(self.current_track_dur * 1000)))
            except Exception: pass

        import threading
        threading.Thread(target=_run, daemon=True).start()

    def toggle_preview(self):
        if not self.vlc: return
        if self._player and self._player.is_playing():
            self._player.stop(); self.btn_play_preview.setText("▶ PLAY PREVIEW")
            return
        if not self._player:
            self._player = self.vlc.media_player_new()
        m = self.vlc.media_new(self.current_track_path)
        self._player.set_media(m)
        self._player.play()
        QTimer.singleShot(100, lambda: self._player.set_time(int(self.offset_slider.value())))
        self.btn_play_preview.setText("⏹ STOP PREVIEW")

    def confirm_current_track(self):
        if self._player: self._player.stop()
        offset = self.offset_slider.value() / 1000.0
        actual_dur = self.current_track_dur - offset
        self.selected_tracks.append((self.current_track_path, offset, actual_dur))
        self.update_coverage_ui()
        covered = sum(t[2] for t in self.selected_tracks)
        if covered < self.total_video_sec:
            QMessageBox.information(self, "Need more music", f"You need {self.total_video_sec - covered:.1f}s more music to cover the video. Select another song!")
            self.stack.setCurrentIndex(0)
        else:
            self.show_summary()

    def show_summary(self):
        self.summary_list.clear()
        for p, off, dur in self.selected_tracks:
            self.summary_list.addItem(f"✅ {os.path.basename(p)} | Starts @ {off:.1f}s | Covers {dur:.1f}s")
        self.stack.setCurrentIndex(2)

    def closeEvent(self, e):
        if self._player: self._player.stop()
        if self._temp_png and os.path.exists(self._temp_png):
            try: os.remove(self._temp_png)
            except Exception: pass
        super().closeEvent(e)
