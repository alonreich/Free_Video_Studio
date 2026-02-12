import os
import sys
import tempfile
import subprocess
import traceback
import logging
from PyQt5.QtCore import Qt, QTimer, QEvent, pyqtSignal
from PyQt5.QtGui import QPixmap, QColor
from PyQt5.QtWidgets import QDialog, QVBoxLayout, QLabel, QHBoxLayout, QPushButton, QSizePolicy
from utilities.merger_trimmed_slider import MergerTrimmedSlider
try:
    import vlc as _vlc_mod
except Exception:
    _vlc_mod = None
PREVIEW_VISUAL_LEAD_MS = 1100

class MergerMusicOffsetDialog(QDialog):
    _ui_call = pyqtSignal(object)

    def __init__(self, parent, vlc_instance, audio_path: str, initial_offset: float, bin_dir: str, hardware_strategy="CPU"):
        super().__init__(parent)
        self._ui_call.connect(self._run_ui_call)
        self.setWindowTitle("Choose Background Music Start")
        self.setModal(True)
        self.resize(1300, 350)
        self.setMinimumSize(1000, 170)
        self._vlc = vlc_instance
        self._mpath = audio_path
        self._bin = bin_dir
        self.hardware_strategy = hardware_strategy
        self._player = None
        self._timer = QTimer(self)
        self._timer.setInterval(25)
        self._timer.timeout.connect(self._tick)
        self._dragging = False
        self._wave_dragging = False
        self._total_ms = 1
        self._temp_png_path = None
        self._pm_src = None
        self._draw_x0 = 0
        self._draw_w = 1
        self._draw_y0 = 0
        self._draw_h = 1
        self._last_good_vlc_ms = 0
        self.logger = logging.getLogger("Video_Merger")
        v = QVBoxLayout(self)
        self.wave = QLabel("Generating waveform...")
        self.wave.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        self.wave.setAlignment(Qt.AlignCenter)
        self.wave.setScaledContents(False)
        self.wave.setStyleSheet("background: black; color: #999; font-weight: bold;")
        self.wave.installEventFilter(self)
        v.addWidget(self.wave)
        self.slider = MergerTrimmedSlider(self)
        self.slider.enable_trim_overlays(False)
        self.slider.setFixedHeight(50)
        self.slider.setStyleSheet("""
        QSlider { border: none; padding: 0px; background: transparent; }
        QSlider::groove:horizontal { border: none; background: #383838; height: 6px; margin: 0px; border-radius: 3px; }
        QSlider::handle:horizontal { background: #2196F3; border: none; width: 3px; height: 20px; margin: -7px 0; border-radius: 2px; }
        QSlider::add-page:horizontal, QSlider::sub-page:horizontal { background: transparent; }
        """)
        self.slider.valueChanged.connect(self._on_slider_changed)
        try:
            self.slider.sliderPressed.connect(self._on_drag_start)
            self.slider.sliderReleased.connect(self._on_drag_end)
        except Exception:
            pass
        self.slider.installEventFilter(self)
        v.addWidget(self.slider)
        row = QHBoxLayout()
        self.ok_btn = QPushButton("OK")
        self.play_btn = QPushButton("Play")
        self.cancel_btn = QPushButton("Cancel")
        for _b in (self.ok_btn, self.cancel_btn):
            _b.setFixedWidth(100)
            _b.setStyleSheet("""
                QPushButton { background-color: #3aa0d8; color: white; font-size: 13px; padding: 6px 14px; border-radius: 6px; }
                QPushButton:hover { background-color: #52b0e4; }
                QPushButton:pressed { background-color: #2c8fc4; }
            """)
        self.play_btn.setFixedHeight(30)
        self.play_btn.setFixedWidth(100)
        self.play_btn.setStyleSheet("""
            QPushButton { background-color: #59A06D; color: white; font-size: 14px; padding: 6px 14px; border-radius: 6px; }
            QPushButton:hover { background-color: #6fb57f; }
            QPushButton:pressed { background-color: #4a865a; }
        """)
        row.addStretch(1)
        row.addWidget(self.ok_btn)
        row.addSpacing(40)
        row.addWidget(self.play_btn)
        row.addSpacing(40)
        row.addWidget(self.cancel_btn)
        row.addStretch(1)
        v.addLayout(row)
        self.ok_btn.clicked.connect(self.accept)
        self.cancel_btn.clicked.connect(self.reject)
        self.play_btn.clicked.connect(self._toggle_play_pause)
        self._overlay = QLabel(self)
        self._overlay.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._overlay.setStyleSheet("background: transparent;")
        self._overlay.lower()
        self._caret = QLabel(self._overlay)
        self._caret.setStyleSheet("background:#3498db;")
        self._caret.hide()

        import threading
        threading.Thread(target=self._init_assets, args=(float(initial_offset or 0.0),), daemon=True).start()
    @property
    def selected_offset(self):
        return float(self.slider.value()) / 1000.0

    def keyPressEvent(self, event):
        if event.key() == Qt.Key_Space:
            self._toggle_play_pause()
            event.accept()
        elif event.key() in (Qt.Key_Left, Qt.Key_Right):
            self._handle_arrow_seek(event)
            event.accept()
        else:
            super().keyPressEvent(event)

    def _handle_arrow_seek(self, event):
        step = 5000 if (event.modifiers() & Qt.ShiftModifier) else 1000
        if event.key() == Qt.Key_Left:
            self._seek_relative(-step)
        else:
            self._seek_relative(step)

    def _seek_relative(self, delta_ms):
        current = self.slider.value()
        target = max(0, min(self._total_ms, current + delta_ms))
        self.slider.setValue(target)
        if self._player and self._player.is_playing():
            self._seek_player(target)
        self._sync_caret()

    def eventFilter(self, obj, event: QEvent):
        if (obj is self.wave or obj is self.slider) and event.type() == QEvent.KeyPress:
            if event.key() == Qt.Key_Space:
                self._toggle_play_pause()
                return True
            elif event.key() in (Qt.Key_Left, Qt.Key_Right):
                self._handle_arrow_seek(event)
                return True
        if obj is self.wave:
            if event.type() == QEvent.MouseButtonPress and event.button() == Qt.LeftButton:
                try:
                    if self._draw_w <= 1: return True
                    self._wave_dragging = True
                    self._set_time_from_wave_x(int(event.pos().x()), seek_if_playing=True)
                    return True
                except Exception: return True
            if event.type() == QEvent.MouseMove:
                try:
                    if self._wave_dragging and (event.buttons() & Qt.LeftButton):
                        self._set_time_from_wave_x(int(event.pos().x()), seek_if_playing=True)
                        return True
                except Exception: return True
            if event.type() == QEvent.MouseButtonRelease:
                try:
                    if self._wave_dragging:
                        self._wave_dragging = False
                        if self._player and self._player.is_playing():
                            self._seek_player(int(self.slider.value()))
                        return True
                except Exception: return True
            if event.type() == QEvent.Resize:
                QTimer.singleShot(0, self._refresh_wave_scaled)
                QTimer.singleShot(0, self._sync_caret)
        return super().eventFilter(obj, event)

    def resizeEvent(self, e):
        super().resizeEvent(e)
        self._overlay.setGeometry(0, 0, self.width(), self.height())
        QTimer.singleShot(0, self._refresh_wave_scaled)
        QTimer.singleShot(0, self._sync_caret)

    def closeEvent(self, e):
        self._stop_player()
        try:
            if self._temp_png_path and os.path.exists(self._temp_png_path):
                os.remove(self._temp_png_path)
        except Exception: pass
        super().closeEvent(e)

    def accept(self):
        self._stop_player()
        super().accept()

    def reject(self):
        self._stop_player()
        super().reject()

    def _set_time_from_wave_x(self, click_x: int, seek_if_playing: bool):
        rel = (click_x - self._draw_x0) / float(self._draw_w)
        rel = 0.0 if rel < 0.0 else (1.0 if rel > 1.0 else rel)
        target_ms = int(rel * float(self._total_ms))
        target_ms = max(0, min(self._total_ms, target_ms))
        self.slider.setValue(target_ms)
        if seek_if_playing and self._player and self._player.is_playing():
            self._seek_player(target_ms)
        self._sync_caret()

    def _ffmpeg(self):
        exe = "ffmpeg.exe" if sys.platform.startswith("win") else "ffmpeg"
        p = os.path.join(self._bin or "", exe)
        return p if os.path.isfile(p) else exe

    def _ffprobe(self):
        exe = "ffprobe.exe" if sys.platform.startswith("win") else "ffprobe"
        p = os.path.join(self._bin or "", exe)
        return p if os.path.isfile(p) else exe

    def _probe_duration_ms(self) -> int:
        try:
            cmd = [self._ffprobe(), "-v", "error", "-show_entries", "format=duration", "-of", "default=nokey=1:noprint_wrappers=1", self._mpath]
            flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
            out = subprocess.check_output(cmd, creationflags=flags)
            dur = float(out.decode(errors="ignore").strip() or "0")
            return int(max(0.0, dur) * 1000.0 + 0.5)
        except Exception as e:
            self.logger.error(f"FFprobe duration failed: {e}")
            return 0

    def _init_assets(self, initial_offset: float):
        dur_ms = self._probe_duration_ms()
        if dur_ms <= 0: dur_ms = 1
        self._total_ms = dur_ms

        def _apply_init():
            if not self.isVisible(): return
            self.slider.setRange(0, self._total_ms)
            self.slider.set_duration_ms(self._total_ms)
            self.slider.setValue(max(0, min(self._total_ms, int(initial_offset * 1000.0 + 0.5))))
        self._post_ui(_apply_init)
        try:
            if not os.path.exists(self._mpath): raise FileNotFoundError(f"Audio file missing: {self._mpath}")
            ffmpeg_exe = self._ffmpeg()
            tf = tempfile.NamedTemporaryFile(suffix=".png", delete=False)
            self._temp_png_path = tf.name
            tf.close()
            cmd = [ffmpeg_exe, "-y", "-hide_banner", "-loglevel", "error", "-i", os.path.abspath(self._mpath), "-frames:v", "1", "-filter_complex", "volume=5.0,showwavespic=s=1200x300:colors=green", os.path.abspath(self._temp_png_path)]
            flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
            result = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags)
            if result.returncode != 0: raise RuntimeError(f"FFmpeg failed (rc={result.returncode})")

            def _apply_wave():
                pm = QPixmap(self._temp_png_path)
                if not pm.isNull():
                    self._pm_src = pm
                    self._refresh_wave_scaled()
                self._sync_caret()
            self._post_ui(_apply_wave)
        except Exception as e:
            self.logger.error(f"Waveform generation error: {e}")
            self._post_ui(lambda: self.wave.setText(f"Error generating waveform."))
        self._post_ui(self._sync_caret)

    def _refresh_wave_scaled(self):
        try:
            if not self._pm_src or self._pm_src.isNull(): return
            cr = self.wave.contentsRect()
            if cr.width() <= 2 or cr.height() <= 2: return
            scaled = self._pm_src.scaled(cr.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation)
            self.wave.setPixmap(scaled)
            self._draw_w = max(1, scaled.width())
            self._draw_h = max(1, scaled.height())
            self._draw_x0 = int((cr.width() - self._draw_w) / 2)
            self._draw_y0 = int((cr.height() - self._draw_h) / 2)
        except Exception: pass

    def _ensure_player(self):
        if self._player or self._vlc is None: return
        try:
            self._player = self._vlc.media_player_new()
            m = self._vlc.media_new(self._mpath)
            self._player.set_media(m)
            if _vlc_mod is not None:
                try:
                    em = self._player.event_manager()
                    em.event_attach(_vlc_mod.EventType.MediaPlayerEndReached, self._on_vlc_ended)
                except Exception: pass
        except Exception as e:
            self.logger.error(f"VLC Player init failed: {e}")
            self._player = None

    def _stop_player(self):
        try:
            if self._timer.isActive(): self._timer.stop()
            if self._player: self._player.stop()
        except Exception: pass

    def _toggle_play_pause(self):
        self._ensure_player()
        if not self._player: return
        try:
            st = str(self._player.get_state()).lower()
            if st.endswith("playing"):
                self._player.pause()
                self.play_btn.setText("▶ Play")
                self._timer.stop()
            else:
                want_ms = int(self.slider.value())
                self._player.play()

                def _after_start():
                    self._seek_player(want_ms)
                    self._timer.start()
                    self.play_btn.setText("⏸ Pause")
                QTimer.singleShot(90, _after_start)
        except Exception: pass

    def _seek_player(self, ms: int):
        try:
            if self._player:
                self._player.set_time(max(0, int(ms)))
                self._last_good_vlc_ms = ms
        except Exception: pass

    def _on_drag_start(self): self._dragging = True

    def _on_drag_end(self):
        self._dragging = False
        if self._player and self._player.is_playing(): self._seek_player(int(self.slider.value()))

    def _on_slider_changed(self, v):
        if self._player and self._player.is_playing() and self._dragging: self._seek_player(int(v))
        self._sync_caret()

    def _sync_caret(self):
        try:
            if self._total_ms <= 0:
                self._caret.hide()
                return
            frac = float(self.slider.value()) / float(self._total_ms)
            frac = max(0.0, min(1.0, frac))
            wave_pos = self.wave.mapTo(self, self.wave.rect().topLeft())
            x = wave_pos.x() + self._draw_x0 + round(frac * float(self._draw_w)) - 1
            y = wave_pos.y() + self._draw_y0
            self._caret.setGeometry(x, y, 2, self._draw_h)
            self._caret.show()
            self._caret.raise_()
        except Exception: self._caret.hide()

    def _tick(self):
        if not self._player: return
        try:
            st = str(self._player.get_state()).lower()
            if not st.endswith("playing"):
                self._timer.stop()
                self.play_btn.setText("▶ Play")
                return
            vlc_ms = int(self._player.get_time() or 0)
            if vlc_ms <= 0: vlc_ms = self._last_good_vlc_ms
            else: self._last_good_vlc_ms = vlc_ms
            vlc_ms = max(0, min(self._total_ms, int(vlc_ms + PREVIEW_VISUAL_LEAD_MS)))
            if vlc_ms >= self._total_ms - 10:
                self._on_vlc_ended()
                return
            if not self._dragging and not self._wave_dragging:
                self.slider.blockSignals(True)
                self.slider.setValue(vlc_ms)
                self.slider.blockSignals(False)
                self._sync_caret()
        except Exception: pass

    def _on_vlc_ended(self, event=None):
        def _ui():
            try:
                self._timer.stop()
                self.play_btn.setText("▶ Play")
                self.slider.setValue(self._total_ms)
                self._sync_caret()
                if self._player: self._player.stop()
            except Exception: pass
        QTimer.singleShot(0, _ui)

    def _run_ui_call(self, fn):
        try: fn()
        except Exception: pass

    def _post_ui(self, fn):
        try: self._ui_call.emit(fn)
        except Exception: pass
