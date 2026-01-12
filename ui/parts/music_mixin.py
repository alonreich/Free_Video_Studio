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
            files = files[:50]
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
            for name, path in mf:
                self.music_combo.addItem(name, path)
            self.music_combo.setCurrentIndex(-1)
            self.music_combo.setEditable(True)
            self.music_combo.setInsertPolicy(self.music_combo.NoInsert)
            self.music_combo.completer().setFilterMode(Qt.MatchContains)
            self.music_combo.completer().setCompletionMode(self.music_combo.completer().PopupCompletion)
            self.music_combo.lineEdit().setPlaceholderText("-          Select an MP3          -")
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
        self.music_offset_input.setVisible(enable and self.music_combo.currentIndex() >= 0)
        if hasattr(self, "music_volume_label"):
            self.music_volume_label.setVisible(enable)
        if enable:
            self.volume_shortcut_target = 'music'
            self.logger.info("SHORTCUT: Volume keys now control MUSIC volume")
            if self.music_volume_slider.value() > 0:
                self.music_volume_slider.setValue(0)
            self.music_volume_slider.setEnabled(True)
        else:
            self.music_volume_slider.setEnabled(False)
            if self.music_combo.currentIndex() != -1:
                self.music_combo.setCurrentIndex(-1)
    
    def _on_music_selected(self, index: int):
        if not self._music_files or index < 0:
            self.music_offset_input.setVisible(False)
            self.positionSlider.reset_music_times()
            return
        if self.music_volume_slider.value() != 0:
            self.music_volume_slider.setValue(0)
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
        """Uses the dedicated MusicOffsetDialog to handle music offset selection."""
        try:
            current_offset = self._get_music_offset()
            dlg = MusicOffsetDialog(self, self.vlc_instance, path, current_offset, self.bin_dir)
            geom_key = 'music_dialog_geom'
            saved_geom = self.config_manager.config.get(geom_key)
            if isinstance(saved_geom, dict):
                dlg.resize(max(900, saved_geom.get('w', 1200)), max(260, saved_geom.get('h', 350)))
                if saved_geom.get('x', -1) != -1:
                    dlg.move(saved_geom.get('x', 0), saved_geom.get('y', 0))
            if dlg.exec_() == QDialog.Accepted:
                self.music_offset_input.setValue(dlg.selected_offset)
            g = dlg.geometry()
            cfg = dict(self.config_manager.config)
            cfg[geom_key] = {'x': g.x(), 'y': g.y(), 'w': g.width(), 'h': g.height()}
            self.config_manager.save_config(cfg)
        except Exception as e:
            self.logger.error(f"Failed to open music offset dialog: {e}")

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