import os
import sys
import subprocess
import tempfile
from PyQt5.QtCore import Qt, QTimer, QRect
from PyQt5.QtGui import QPixmap, QPainter, QColor
from PyQt5.QtWidgets import (QStyleOptionSlider, QStyle, QDialog, QVBoxLayout,
                             QLabel, QHBoxLayout, QPushButton, QWidget)
from ui.widgets.trimmed_slider import TrimmedSlider
from ui.widgets.music_offset_dialog import MusicOffsetDialog

class MusicMixin:
    def _mp3_dir(self):
        """Return the absolute path to the project's central MP3 folder.
        The application historically stored MP3 files in ``ui/MP3``.  The
        user has moved this folder to the project root and renamed it
        ``mp3`` (lowercase).  Use ``self.base_dir``—which points one
        level above the ``ui`` directory—to construct the new path and
        ensure it exists.
        """
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
            from PyQt5.QtWidgets import QStyleOptionSlider, QStyle
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
        r"""Scan .\MP3 for .mp3 files, sorted by modified time (newest first). Never raises."""
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
            if not p: return
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
            dlg = MusicOffsetDialog(self, getattr(self, "vlc_instance", None), p, initial, getattr(self, "bin_dir", ""))
            if dlg.exec_() == QDialog.Accepted:
                self.music_offset_input.setValue(dlg.selected_offset)
                music_end = (self.trim_end or self.original_duration)
                if self.music_start_sec is not None and self.music_end_sec is not None:
                    music_end = self.music_end_sec
                self.positionSlider.set_music_times(dlg.selected_offset, music_end)
                self.logger.info("MUSIC: selected | file='%s' | start=%.3fs | vol_eff=%d%%",
                                os.path.basename(p), float(dlg.selected_offset), self._music_eff())
        except Exception as e:
            self.logger.error(f"Error in _on_music_selected: {e}")
    
    def _open_music_offset_dialog(self, path: str):
        """Popup dialog to select music start (seconds) with a waveform image. Robust fallback if ffmpeg fails."""
        try:
            import tempfile
            dur = self._probe_audio_duration(path)
            if dur <= 0:
                dur = 0.0
            png = None
            try:
                ffmpeg_path = os.path.join(self.bin_dir, 'ffmpeg.exe')
                tmp_png = os.path.join(tempfile.gettempdir(), "bg_waveform.png")
                cmd = [
                    ffmpeg_path, "-hide_banner", "-loglevel", "error",
                    "-i", path, "-frames:v", "1",
                    "-filter_complex", "showwavespic=s=1500x400:split_channels=0:colors=0x86a8b4",
                    "-y", tmp_png
                ]
                subprocess.run(cmd, check=True,
                            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0))
                if os.path.isfile(tmp_png):
                    png = tmp_png
            except Exception:
                png = None
            dlg = QDialog(self)
            dlg.setWindowTitle("Choose Background Music Start")
            v = QVBoxLayout(dlg)
            geom = self.config_manager.config.get('music_dialog_geom')
            if isinstance(geom, dict):
                try:
                    dlg.resize(int(geom.get('w', dlg.width())), int(geom.get('h', dlg.height())))
                    dlg.move(int(geom.get('x', dlg.x())), int(geom.get('y', dlg.y())))
                except Exception:
                    pass
    
            class _WaveformWithCaret(QLabel):
                def __init__(self, pix: QPixmap, *args, **kwargs):
                    super().__init__(*args, **kwargs)
                    self._pix = pix
                    self._dpr = float(getattr(pix, "devicePixelRatioF", lambda: 1.0)())
                    self._eff_w = int(round(pix.width()  / self._dpr))
                    self._eff_h = int(round(pix.height() / self._dpr))
                    self._x = 0
                    self.setMinimumHeight(self._eff_h + 6)
    
                def set_frac(self, frac: float):
                    frac = max(0.0, min(1.0, float(frac)))
                    self._x = int(frac * max(1, self._eff_w - 1))
                    self.update()
    
                def paintEvent(self, e):
                    p = QPainter(self)
                    p.setRenderHint(QPainter.Antialiasing)
                    x0 = (self.width()  - self._eff_w) // 2
                    y0 = (self.height() - self._eff_h) // 2
                    p.drawPixmap(QRect(x0, y0, self._eff_w, self._eff_h), self._pix)
                    p.setPen(QColor(255, 255, 255))
                    cx = x0 + self._x
                    p.drawLine(cx, y0 - 2, cx, y0 + self._eff_h + 2)
                    p.end()
            if png:
                _pix = QPixmap(png)
                dpr = float(getattr(_pix, "devicePixelRatioF", lambda: 1.0)())
                eff_w = int(round(_pix.width()  / dpr))
                eff_h = int(round(_pix.height() / dpr))
                lbl = _WaveformWithCaret(_pix)
                lbl.setAlignment(Qt.AlignCenter)
                lbl.setMinimumWidth(eff_w)
                dlg.resize(max(dlg.width(), eff_w + 80), max(dlg.height(), eff_h + 140))
                v.addWidget(lbl)
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
            v.addLayout(h)
            preview_controls = QHBoxLayout()
            play_btn = QPushButton("▶ Play")
            play_btn.setFocusPolicy(Qt.NoFocus)
            preview_timer = QTimer(dlg)
            preview_timer.setInterval(100)
            def _update_slider_from_audio():
                """Update the preview slider and caret based on current audio time."""
                try:
                    if audio_player is not None and audio_player.is_playing():
                        ms = int(audio_player.get_time() or 0)
                        slider.blockSignals(True)
                        slider.setValue(min(slider.maximum(), ms))
                        slider.blockSignals(False)
                        try:
                            if png:
                                lbl.set_frac(ms / max(1, slider.maximum()))
                            val_label.setText(f"{ms/1000.0:.2f} s")
                        except Exception:
                            pass
                except Exception:
                    pass
            preview_timer.timeout.connect(_update_slider_from_audio)
            def toggle_preview():
                if audio_player is None:
                    return
                try:
                    if audio_player.is_playing():
                        audio_player.pause()
                        play_btn.setText("▶ Play")
                        preview_timer.stop()
                    else:
                        audio_player.stop()
                        start_ms = slider.value()
                        audio_player.play()
                        audio_player.set_time(int(start_ms))
                        play_btn.setText("⏸ Pause")
                        preview_timer.start()
                except Exception:
                    pass
            play_btn.clicked.connect(toggle_preview)
            preview_controls.addWidget(play_btn)
            preview_controls.addStretch(1)
            v.addLayout(preview_controls)
            audio_player = None
            try:
                audio_media = self.vlc_instance.media_new_path(path)
                audio_player = self.vlc_instance.media_player_new()
                audio_player.set_media(audio_media)
                init_vol = 50
                try:
                    init_vol = max(0, min(100, self.music_volume_slider.value()))
                except Exception:
                    pass
                audio_player.audio_set_volume(int(init_vol))
                def _seek_audio(ms: int):
                    try:
                        audio_player.set_time(int(ms))
                    except Exception:
                        pass
                slider.valueChanged.connect(_seek_audio)
            except Exception:
                audio_player = None
            def _sync_caret(ms):
                if png:
                    lbl.set_frac(ms / max(1, slider.maximum()))
                val_label.setText(f"{ms/1000.0:.2f} s")
            _sync_caret(slider.value())
            slider.valueChanged.connect(_sync_caret)
            btns = QHBoxLayout()
            ok = QPushButton("OK")
            cancel = QPushButton("Cancel")
            btns.addWidget(ok)
            btns.addWidget(cancel)
            v.addLayout(btns)
            def on_slide(x):
                val_label.setText(f"{x/1000.0:.2f} s")
            slider.valueChanged.connect(on_slide)
            ok.clicked.connect(dlg.accept)
            cancel.clicked.connect(dlg.reject)
    
            def dialog_key_press(event):
                key = event.key()
                step = 0
                if key == Qt.Key_Up:
                    step = 1
                elif key == Qt.Key_Down:
                    step = -1
                elif key == Qt.Key_Plus:
                    step = 5
                elif key == Qt.Key_Minus:
                    step = -5
                if step != 0:
                    slider = self.music_volume_slider
                    eff_callback = self._music_eff
                    eff_vol = eff_callback(slider.value())
                    new_eff_vol = max(0, min(100, eff_vol + step))
                    if slider.invertedAppearance():
                        new_raw_val = slider.maximum() + slider.minimum() - new_eff_vol
                    else:
                        new_raw_val = new_eff_vol
                    new_raw_val = max(slider.minimum(), min(slider.maximum(), new_raw_val))
                    slider.setValue(new_raw_val) 
                    if audio_player is not None:
                        audio_player.audio_set_volume(new_eff_vol)
                    self.logger.debug(f"DIALOG: Music volume set to {new_eff_vol}%")
                    event.accept()
                else:
                    QDialog.keyPressEvent(dlg, event)
            dlg.keyPressEvent = dialog_key_press
            accepted = dlg.exec_()
            try:
                if audio_player is not None:
                    audio_player.stop()
                    audio_player.release()
            except Exception:
                pass
            try:
                preview_timer.stop()
            except Exception:
                pass
            if accepted == QDialog.Accepted:
                try:
                    cfg = dict(self.config_manager.config)
                    cfg['music_dialog_geom'] = {'x': dlg.x(), 'y': dlg.y(), 'w': dlg.width(), 'h': dlg.height()}
                    self.config_manager.save_config(cfg)
                except Exception:
                    pass
                try:
                    self.music_offset_input.setValue(slider.value() / 1000.0)
                except Exception:
                    pass
        except Exception:
            pass
    
    def _get_music_offset(self) -> float:
        try:
            return float(self.music_offset_input.value())
        except Exception:
            return 0.0
    
    def _get_selected_music(self):
        """Return (path, volume_linear) or (None, None) if disabled/invalid."""
        if not self.add_music_checkbox.isChecked():
            return None, None
        if not self._music_files:
            return None, None
        path = self.music_combo.currentData() or ""
        if not path or not os.path.isfile(path):
            return None, None
        vol_pct = self._music_eff()
        return path, (vol_pct / 100.0)
    
    def _music_eff(self, raw: int | None = None) -> int:
        """Map slider value -> 0..100 respecting invertedAppearance."""
        v = int(self.music_volume_slider.value() if raw is None else raw)
        if self.music_volume_slider.invertedAppearance():
            return max(0, min(100, self.music_volume_slider.maximum() + self.music_volume_slider.minimum() - v))
        return max(0, min(100, v))
    
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
                cfg['last_music_volume'] = eff
                self.config_manager.save_config(cfg)
            except Exception:
                pass
        except Exception:
            pass