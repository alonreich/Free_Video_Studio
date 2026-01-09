import os
import sys
import subprocess
import tempfile
from PyQt5.QtCore import Qt, QTimer, QRect
from PyQt5.QtGui import QPixmap, QPainter, QColor
from PyQt5.QtWidgets import (QStyleOptionSlider, QStyle, QDialog, QVBoxLayout,
                             QLabel, QHBoxLayout, QPushButton, QWidget, QSlider, QApplication)
from ui.widgets.trimmed_slider import TrimmedSlider
from ui.widgets.music_offset_dialog import MusicOffsetDialog

class MusicMixin:

    def _mp3_dir(self):
        """Return the absolute path to the project's central MP3 folder."""
        d = os.path.join(self.base_dir, "mp3")
        try:
            os.makedirs(d, exist_ok=True)
        except Exception:
            pass
        return d
    
    def _on_speed_changed(self, value):
        """Logs speed change and saves it to config."""
        try:
            speed = float(value)
            self.logger.info(f"OPTION: Speed Multiplier set to {speed:.1f}x")
        except Exception as e:
            self.logger.error("Error handling speed change: %s", e)
    
    def _update_music_badge(self):
        """Position the small % badge next to the music volume handle (not mirrored)."""
        try:
            if not hasattr(self, "music_volume_badge") or not self.music_volume_slider.isVisible():
                if hasattr(self, "music_volume_badge"):
                    self.music_volume_badge.hide()
                return
            s = self.music_volume_slider
            opt = QStyleOptionSlider()
            opt.initFrom(s)
            opt.orientation   = Qt.Vertical
            opt.minimum       = s.minimum()
            opt.maximum       = s.maximum()
            opt.sliderPosition = int(s.value())
            opt.sliderValue    = int(s.value())
            opt.upsideDown    = not s.invertedAppearance()
            opt.rect          = s.rect()
            handle = s.style().subControlRect(QStyle.CC_Slider, opt, QStyle.SC_SliderHandle, s)
            parent = s.parentWidget() or self
            handle_center = handle.center()
            pt = s.mapTo(parent, handle_center)
            self.music_volume_badge.setText(f"{self._music_eff(int(s.value()))}%")
            self.music_volume_badge.adjustSize()
            x = s.x() + s.width() + 8
            y = pt.y() - (self.music_volume_badge.height() // 2)
            y = max(2, min((parent.height() - self.music_volume_badge.height() - 2), y))
            self.music_volume_badge.move(x, y)
            self.music_volume_badge.show()
        except Exception:
            pass
    
    def _scan_mp3_folder(self):
        r"""Scan .\MP3 for .mp3 files, sorted by modified time (newest first)."""
        try:
            d = self._mp3_dir()
            files = []
            for name in os.listdir(d):
                if name.lower().endswith(".mp3"):
                    p = os.path.join(d, name)
                    try:
                        mt = os.path.getmtime(p)
                    except Exception:
                        mt = 0
                    files.append((mt, name, p))
            files.sort(key=lambda x: x[0], reverse=True)
            self._music_files = [ (n, p) for _, n, p in files ]
        except Exception:
            self._music_files = []
        self._populate_music_combo()
    
    def _populate_music_combo(self):
        """Refresh the dropdown safely based on self._music_files."""
        if not hasattr(self, "music_combo"):
            return
        mf = getattr(self, "_music_files", [])
        self.music_combo.blockSignals(True)
        self.music_combo.clear()
        if not mf:
            self.music_combo.addItem("No MP3 files found in ./MP3", "")
            self.music_combo.setEnabled(False)
            self.music_combo.setVisible(False)
            self.add_music_checkbox.setChecked(False)
            self.music_volume_slider.setEnabled(False)
            self.music_volume_slider.setVisible(False)
            if hasattr(self, "music_volume_label"):
                self.music_volume_label.setVisible(False)
            if hasattr(self, "music_offset_input"):
                self.music_offset_input.setEnabled(False)
                self.music_offset_input.setVisible(False)
        else:
            self.music_combo.addItem("— Select an MP3 —", "")
            for name, path in mf:
                self.music_combo.addItem(name, path)
            self.music_combo.setCurrentIndex(0)
            self.music_combo.setEnabled(True)
            self.music_volume_slider.setEnabled(False)
            if hasattr(self, "music_volume_label"):
                self.music_volume_label.setVisible(False)
            if hasattr(self, "music_offset_input"):
                self.music_offset_input.setEnabled(False)
                self.music_offset_input.setVisible(False)
        self.music_combo.blockSignals(False)
    
    def _on_add_music_toggled(self, checked: bool):
        """Show/enable music controls only if files exist and checkbox checked."""
        have_files = bool(self._music_files)
        enable = checked and have_files
        self.positionSlider.set_music_visible(enable)
        if not enable:
            self.positionSlider.reset_music_times()
        self.music_combo.setVisible(enable)
        self.music_combo.setEnabled(enable)
        self.music_volume_slider.setVisible(enable)
        self.music_offset_input.setVisible(enable and self.music_combo.currentIndex() > 0)
        if hasattr(self, "music_volume_label"):
            self.music_volume_label.setVisible(enable)
        if enable:
            self.volume_shortcut_target = 'music'
            self.logger.info("SHORTCUT: Volume keys now control MUSIC volume")
            if self.music_volume_slider.value() < 10:
                self.music_volume_slider.setValue(20)
            self.music_volume_slider.setEnabled(True)
        else:
            self.music_volume_slider.setEnabled(False)
            if self.music_combo.currentIndex() != 0:
                self.music_combo.setCurrentIndex(0)
    
    def _on_music_selected(self, index: int):
        if not self._music_files or index <= 0:
            self.music_offset_input.setVisible(False)
            self.positionSlider.reset_music_times()
            return
        if self.music_volume_slider.value() in (0, 35):
            self.music_volume_slider.setValue(20)
        try:
            p = self.music_combo.currentData()
            if not p:
                return
            self.music_offset_input.setValue(0.0)
            self.positionSlider.set_music_times(self.trim_start or 0.0, self.trim_end or self.original_duration)
            dur = self._probe_audio_duration(p)
            self.music_offset_input.setRange(0.0, max(0.0, dur - 0.01))
            self.volume_shortcut_target = 'music'
            self.logger.info("SHORTCUT: Volume keys now control MUSIC volume")
            self.music_volume_slider.setEnabled(True)
            if hasattr(self, "music_volume_label"):
                self.music_volume_label.setVisible(True)
            self.music_offset_input.setEnabled(True)
            self.music_offset_input.setVisible(True)
            initial = 0.0
            self.logger.info("MUSIC: open offset dialog | file='%s' | initial=%.3fs | vol_eff=%d%%",
                             os.path.basename(p), initial, self._music_eff())
            self._open_music_offset_dialog(p)
            offset = self._get_music_offset()
            music_end = (self.trim_end or self.original_duration)
            self.positionSlider.set_music_times(offset, music_end)
            self.logger.info("MUSIC: selected | file='%s' | start=%.3fs | vol_eff=%d%%",
                             os.path.basename(p), offset, self._music_eff())
        except Exception as e:
            self.logger.error(f"Error in _on_music_selected: {e}")

    def _open_music_offset_dialog(self, path: str):
        """Popup dialog to select music start (seconds). Responsive and saves geometry instantly."""
        try:
            import tempfile
            dur = self._probe_audio_duration(path)
            if dur <= 0:
                dur = 0.0
            png = None
            try:
                ffmpeg_path = os.path.join(self.bin_dir, 'ffmpeg.exe')
                with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tf:
                    tmp_png = tf.name
                cmd = [
                    ffmpeg_path, "-hide_banner", "-loglevel", "error",
                    "-i", path, "-frames:v", "1",
                    "-filter_complex", "showwavespic=s=1920x600:split_channels=0:colors=0x86a8b4",
                    "-y", tmp_png
                ]
                subprocess.run(cmd, check=True,
                               creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0))
                if os.path.isfile(tmp_png):
                    png = tmp_png
            except Exception:
                png = None
            dlg = QDialog(self)
            dlg.setStyleSheet(self.styleSheet())
            dlg.setWindowTitle("Choose Background Music Start")
            dlg.setMinimumSize(1200, 260)
            geom_key = 'music_dialog_geom'
            saved_geom = self.config_manager.config.get(geom_key)
            if isinstance(saved_geom, dict):
                dlg.resize(saved_geom.get('w', 1200), saved_geom.get('h', 260))
                dlg.move(saved_geom.get('x', 0), saved_geom.get('y', 0))
            else:
                screen_geo = QApplication.desktop().availableGeometry()
                w, h = 1200, 260
                x = screen_geo.x() + (screen_geo.width() - w) // 2
                y = screen_geo.y() + (screen_geo.height() - h) // 2
                dlg.setGeometry(x, y, w, h)



            def save_geometry():
                try:
                    cfg = dict(self.config_manager.config)
                    g = dlg.geometry()
                    cfg[geom_key] = {'x': g.x(), 'y': g.y(), 'w': g.width(), 'h': g.height()}
                    self.config_manager.save_config(cfg)
                except Exception:
                    pass



            def resize_event(event):
                QDialog.resizeEvent(dlg, event)
                save_geometry()
            
            def move_event(event):
                QDialog.moveEvent(dlg, event)
                save_geometry()
            dlg.resizeEvent = resize_event
            dlg.moveEvent = move_event
            v = QVBoxLayout(dlg)
            v.setContentsMargins(20, 20, 20, 20)
            v.setSpacing(20)



            class _ResponsiveWaveform(QLabel):

                def __init__(self, pix: QPixmap, *args, **kwargs):
                    super().__init__(*args, **kwargs)
                    self._pix = pix
                    self._frac = 0.0
                    self.setSizePolicy(1|4, 1|4)
                    self.setStyleSheet("""
                        QLabel {
                            background-color: #34495e;
                            border: 2px solid #266b89;
                            border-radius: 12px;
                        }
                    """)

                def set_frac(self, frac: float):
                    self._frac = max(0.0, min(1.0, float(frac)))
                    self.update()

                def paintEvent(self, e):
                    p = QPainter(self)
                    p.setRenderHint(QPainter.Antialiasing)
                    scaled_pix = self._pix.scaled(self.size(), Qt.IgnoreAspectRatio, Qt.SmoothTransformation)
                    x_off = (self.width() - scaled_pix.width()) // 2
                    y_off = (self.height() - scaled_pix.height()) // 2
                    p.drawPixmap(x_off, y_off, scaled_pix)
                    cx = int(self._frac * self.width())
                    p.setPen(QColor(255, 255, 255))
                    p.drawLine(cx, 0, cx, self.height())
                    p.end()
            waveform_layout = QHBoxLayout()
            waveform_layout.setSpacing(15)
            if png:
                _pix = QPixmap(png)
                lbl = _ResponsiveWaveform(_pix)
                lbl.setAlignment(Qt.AlignCenter)
                waveform_layout.addWidget(lbl, stretch=1)
            vol_slider = QSlider(Qt.Vertical)
            vol_slider.setRange(0, 100)
            vol_slider.setSingleStep(1)
            vol_slider.setPageStep(15)
            vol_slider.setTickInterval(10)
            vol_slider.setTickPosition(QSlider.TicksBothSides)
            vol_slider.setTracking(True)
            vol_slider.setInvertedAppearance(True) 
            vol_slider.setFixedWidth(40)
            vol_slider.setStyleSheet("""
                QSlider::groove:vertical {
                    border: 1px solid #1f2a36;
                    background: qlineargradient(x1:0, y1:1, x2:0, y2:0,
                        stop:0   #e64c4c, stop:0.25 #f7a8a8, stop:0.50 #f2f2f2,
                        stop:0.75 #7bcf43, stop:1   #009b00);
                    width: 20px;
                    border-radius: 3px;
                }
                QSlider::handle:vertical {
                    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
                        stop:0 #455A64, stop:0.40 #455A64, stop:0.42 #90A4AE, stop:0.44 #90A4AE,
                        stop:0.46 #455A64, stop:0.48 #455A64, stop:0.50 #90A4AE, stop:0.52 #90A4AE,
                        stop:0.54 #455A64, stop:0.56 #455A64, stop:0.58 #90A4AE, stop:0.60 #90A4AE,
                        stop:0.62 #455A64, stop:1 #455A64);
                    border: 1px solid #1f2a36;
                    width: 22px; height: 40px; margin: 0 -2px; border-radius: 4px;
                }
                QSlider::handle:vertical:hover {
                    border: 1px solid #90A4AE;
                    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
                        stop:0 #546E7A, stop:0.40 #546E7A, stop:0.42 #CFD8DC, stop:0.44 #CFD8DC,
                        stop:0.46 #546E7A, stop:0.48 #546E7A, stop:0.50 #CFD8DC, stop:0.52 #CFD8DC,
                        stop:0.54 #546E7A, stop:0.56 #546E7A, stop:0.58 #CFD8DC, stop:0.60 #CFD8DC,
                        stop:0.62 #546E7A, stop:1 #546E7A);
                }
                QSlider::sub-page:vertical, QSlider::add-page:vertical { background: transparent; }
            """)
            vol_slider.setValue(self.music_volume_slider.value())
            waveform_layout.addWidget(vol_slider, stretch=0)
            v.addLayout(waveform_layout, stretch=1)
            h = QHBoxLayout()
            slider = TrimmedSlider(dlg)
            slider.enable_trim_overlays(False)
            total_ms = int(max(0.0, dur) * 1000)
            slider.setRange(0, total_ms)
            slider.set_duration_ms(total_ms)
            slider.setFixedHeight(50)
            slider.setSingleStep(50)
            current_ms = int(max(0.0, float(getattr(self.music_offset_input, "value", lambda: 0.0)()) * 1000.0))
            slider.setValue(min(slider.maximum(), current_ms))
            val_label = QLabel(f"{slider.value()/1000.0:.2f} s")
            h.addWidget(slider)
            h.addWidget(val_label)
            v.addLayout(h, stretch=0)
            blue_btn_style = "QPushButton { background-color: #266b89; color: #ffffff; border: none; min-width: 100px; padding: 10px; border-radius: 8px; font-weight: bold; } QPushButton:hover { background-color: #2980b9; }"
            red_btn_style = "QPushButton { background-color: #e74c3c; color: #ffffff; border: none; min-width: 100px; padding: 10px; border-radius: 8px; font-weight: bold; } QPushButton:hover { background-color: #c0392b; }"
            ok = QPushButton("OK")
            ok.setStyleSheet(blue_btn_style)
            ok.setFixedHeight(40)
            play_btn = QPushButton("▶ Play")
            play_btn.setStyleSheet(blue_btn_style)
            play_btn.setFixedHeight(40)
            play_btn.setFocusPolicy(Qt.NoFocus)
            cancel = QPushButton("Cancel")
            cancel.setStyleSheet(red_btn_style)
            cancel.setFixedHeight(40)
            preview_timer = QTimer(dlg)
            preview_timer.setInterval(100)

            def _update_slider_from_audio():
                try:
                    if audio_player is not None and audio_player.is_playing():
                        ms = int(audio_player.get_time() or 0)
                        slider.blockSignals(True)
                        slider.setValue(min(slider.maximum(), ms))
                        slider.blockSignals(False)
                        if png: lbl.set_frac(ms / max(1, slider.maximum()))
                        val_label.setText(f"{ms/1000.0:.2f} s")
                except Exception: pass
            preview_timer.timeout.connect(_update_slider_from_audio)

            def toggle_preview():
                if audio_player is None: return
                try:
                    if audio_player.is_playing():
                        audio_player.pause()
                        play_btn.setText("▶ Play")
                        preview_timer.stop()
                    else:
                        audio_player.stop()
                        audio_player.play()
                        audio_player.set_time(int(slider.value()))
                        play_btn.setText("⏸ Pause")
                        preview_timer.start()
                except Exception: pass
            play_btn.clicked.connect(toggle_preview)
            audio_player = None
            try:
                audio_media = self.vlc_instance.media_new_path(path)
                audio_player = self.vlc_instance.media_player_new()
                audio_player.set_media(audio_media)
                eff_vol = self._music_eff(vol_slider.value())
                audio_player.audio_set_volume(eff_vol)
                slider.valueChanged.connect(lambda ms: audio_player.set_time(int(ms)) if audio_player else None)
            except Exception: audio_player = None

            def on_dialog_vol_changed(val):
                eff = self._music_eff(val)
                if audio_player:
                    audio_player.audio_set_volume(eff)
                self.music_volume_slider.blockSignals(True)
                self.music_volume_slider.setValue(val)
                self.music_volume_slider.blockSignals(False)
                self._on_music_volume_changed(val)
            vol_slider.valueChanged.connect(on_dialog_vol_changed)

            def _sync_caret(ms):
                if png: lbl.set_frac(ms / max(1, slider.maximum()))
                val_label.setText(f"{ms/1000.0:.2f} s")
            _sync_caret(slider.value())
            slider.valueChanged.connect(_sync_caret)
            button_row = QHBoxLayout()
            button_row.addStretch(1)
            button_row.addWidget(ok)
            button_row.addSpacing(45)
            button_row.addWidget(play_btn)
            button_row.addSpacing(45)
            button_row.addWidget(cancel)
            button_row.addStretch(1)
            v.addLayout(button_row, stretch=0)
            
            def on_slide(x):
                val_label.setText(f"{x/1000.0:.2f} s")
            slider.valueChanged.connect(on_slide)
            ok.clicked.connect(dlg.accept)
            cancel.clicked.connect(dlg.reject)

            def dialog_key_press(event):
                key = event.key()
                step = 0
                if key == Qt.Key_Up:    step = 1
                elif key == Qt.Key_Down:  step = -1
                elif key == Qt.Key_Plus:  step = 5
                elif key == Qt.Key_Minus: step = -5
                if step != 0:
                    eff_vol = self._music_eff(vol_slider.value())
                    new_eff_vol = max(0, min(100, eff_vol + step))
                    if vol_slider.invertedAppearance():
                        new_raw_val = vol_slider.maximum() + vol_slider.minimum() - new_eff_vol
                    else:
                        new_raw_val = new_eff_vol
                    vol_slider.setValue(new_raw_val)
                    self.logger.debug(f"DIALOG: Music volume set to {new_eff_vol}%")
                    event.accept()
                else:
                    QDialog.keyPressEvent(dlg, event)
            dlg.keyPressEvent = dialog_key_press
            accepted = dlg.exec_()
            if tmp_png and os.path.exists(tmp_png):
                try: os.remove(tmp_png)
                except: pass
            try:
                if audio_player is not None:
                    audio_player.stop()
                    audio_player.release()
            except Exception: pass
            try:
                preview_timer.stop()
            except Exception: pass
            if accepted == QDialog.Accepted:
                try:
                    self.music_offset_input.setValue(slider.value() / 1000.0)
                except Exception: pass
        except Exception:
            pass

    def _get_selected_music(self):
        """Returns (path, volume_linear) for the processor. Required by ffmpeg_mixin."""
        if not self.add_music_checkbox.isChecked():
            return None, None
        if not self._music_files:
            return None, None
        path = self.music_combo.currentData() or ""
        if not path or not os.path.isfile(path):
            return None, None
        vol_pct = self._music_eff()
        return path, (vol_pct / 100.0)

    def _get_music_offset(self):
        """Returns the start time offset in seconds (float). Required by ffmpeg_mixin."""
        if not hasattr(self, 'music_offset_input'):
            return 0.0
        try:
            return float(self.music_offset_input.value())
        except:
            return 0.0
    
    def _music_eff(self, raw=None):
        """Map slider value -> 0..100 respecting invertedAppearance."""
        val = self.music_volume_slider.value() if raw is None else raw
        if self.music_volume_slider.invertedAppearance():
            return max(0, min(100, self.music_volume_slider.maximum() + self.music_volume_slider.minimum() - val))
        return max(0, min(100, val))
    
    def _on_music_volume_changed(self, raw: int):
        """Keep label/badge in effective % while the slider is inverted."""
        try:
            eff = self._music_eff(raw)
            if hasattr(self, "music_volume_label"):
                self.music_volume_label.setText(f"{eff}%")
            if hasattr(self, "_update_music_badge"):
                self._update_music_badge()
            try:
                cfg = dict(self.config_manager.config)
                cfg['music_volume'] = eff
                self.config_manager.save_config(cfg)
            except Exception:
                pass
        except Exception:
            pass