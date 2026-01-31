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
            self.music_volume_badge.setText(f"{self._music_eff(int(s.value()))}%")
            self.music_volume_badge.adjustSize()
            global_top_right = s.mapToGlobal(s.rect().topRight())
            parent_top_right = self.mapFromGlobal(global_top_right)
            x = parent_top_right.x() + 8
            handle_center_global = s.mapToGlobal(handle.center())
            handle_center_parent = self.mapFromGlobal(handle_center_global)
            y = handle_center_parent.y() - (self.music_volume_badge.height() // 2)
            parent_height = self.height()
            y = max(2, min((parent_height - self.music_volume_badge.height() - 2), y))
            self.music_volume_badge.move(x, y)
            self.music_volume_badge.raise_()
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
            self._reset_music_player()
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
            if hasattr(self, 'trim_start_ms'):
                current_trim_start_ms = self.trim_start_ms
            elif hasattr(self, 'trim_start') and self.trim_start is not None:
                current_trim_start_ms = self.trim_start * 1000
            else:
                current_trim_start_ms = 0
            if hasattr(self, 'trim_end_ms') and self.trim_end_ms > 0:
                current_trim_end_ms = self.trim_end_ms
            elif hasattr(self, 'trim_end') and self.trim_end is not None and self.trim_end > 0:
                current_trim_end_ms = self.trim_end * 1000
            elif hasattr(self, 'original_duration_ms'):
                current_trim_end_ms = self.original_duration_ms
            elif hasattr(self, 'original_duration') and self.original_duration > 0:
                current_trim_end_ms = self.original_duration * 1000
            else:
                current_trim_end_ms = 0
            self.positionSlider.set_music_times(current_trim_start_ms, current_trim_end_ms)
            self.music_timeline_start_ms = current_trim_start_ms
            self.music_timeline_end_ms = current_trim_end_ms
            self.music_timeline_start_sec = current_trim_start_ms / 1000.0
            self.music_timeline_end_sec = current_trim_end_ms / 1000.0
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
            self.logger.info("MUSIC: open offset dialog |file='%s' | initial=%.3fs | vol_eff=%d%%",
                             os.path.basename(p), initial, self._music_eff())
            self._open_music_offset_dialog(p)
            if self.vlc_instance:
                if not hasattr(self, 'vlc_music_player') or self.vlc_music_player is None:
                    self.vlc_music_player = self.vlc_instance.media_player_new()
                if self.vlc_music_player:
                    media = self.vlc_instance.media_new(p)
                    self.vlc_music_player.set_media(media)
                    self.vlc_music_player.audio_set_volume(self._music_eff())
            else:
                self.logger.warning("VLC Engine is dead (CPU mode?); skipping background music preview.")
            self.positionSlider.set_music_times(self.music_timeline_start_ms, self.music_timeline_end_ms)
            self.logger.info("MUSIC: selected | file='%s' | visual_start=%.3fs | vol_eff=%d%%",
                             os.path.basename(p), self.music_timeline_start_sec, self._music_eff())
        except Exception as e:
            self.logger.error(f"Error in _on_music_selected: {e}")

    def _open_music_offset_dialog(self, path: str):
        """Uses the dedicated MusicOffsetDialog to handle music offset selection."""
        try:
            current_offset = self._get_music_offset()
            dlg = MusicOffsetDialog(
                self, 
                self.vlc_instance, 
                path, 
                current_offset, 
                self.bin_dir, 
                hardware_strategy=getattr(self, 'hardware_strategy', 'CPU')
            )
            geom_key = 'music_dialog_geom'
            saved_geom = self.config_manager.config.get(geom_key)
            if isinstance(saved_geom, dict):
                w = max(1300, saved_geom.get('w', 1300))
                h = max(350, saved_geom.get('h', 350))
                dlg.resize(w, h)
                if saved_geom.get('x', -1) != -1:
                    dlg.move(saved_geom.get('x', 0), saved_geom.get('y', 0))
            else:
                dlg.resize(1600, 600)
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

    def _get_music_offset_ms(self):
        """Returns the music file start offset in milliseconds."""
        return self._get_music_offset() * 1000

    def _get_music_params(self):
        """Bundles all music-related settings for the rendering engine."""
        if not self.add_music_checkbox.isChecked():
            return None
        path, volume = self._get_selected_music()
        if not path:
            return None
        return {
            "path": path,
            "volume": volume,
            "file_offset_sec": self._get_music_offset(),
            "timeline_start_sec": self.music_timeline_start_sec,
            "timeline_end_sec": self.music_timeline_end_sec,
        }
    
    def _reset_music_player(self):
        """Stops and releases the music player, resetting its state."""
        if hasattr(self, 'vlc_music_player') and self.vlc_music_player:
            self.vlc_music_player.stop()
            self.vlc_music_player.release()
            self.vlc_music_player = None
        self.music_timeline_start_sec = None
        self.music_timeline_end_sec = None
        if hasattr(self, 'music_combo'):
            self.music_combo.setCurrentIndex(-1)
        if hasattr(self, 'music_offset_input'):
            self.music_offset_input.setValue(0.0)
            self.music_offset_input.setVisible(False)
        if hasattr(self, 'positionSlider'):
            self.positionSlider.reset_music_times()

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
            if hasattr(self, 'vlc_music_player') and self.vlc_music_player:
                self.vlc_music_player.audio_set_volume(eff)
            try:
                cfg = dict(self.config_manager.config)
                cfg['music_volume'] = eff
                self.config_manager.save_config(cfg)
            except Exception:
                pass
        except Exception:
            pass