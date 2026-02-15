import os
import sys
import subprocess
import tempfile
from PyQt5.QtCore import Qt, QTimer, QRect
from PyQt5.QtGui import QPixmap, QPainter, QColor
from PyQt5.QtWidgets import (QStyleOptionSlider, QStyle, QDialog, QVBoxLayout,
                            QLabel, QHBoxLayout, QPushButton, QWidget, QSlider, QApplication, QMessageBox)

from ui.widgets.trimmed_slider import TrimmedSlider

class MusicMixin:
    def _mp3_dir(self):
        """Return the absolute path to the project's central MP3 folder or custom path from config."""
        try:
            custom = self.config_manager.config.get('custom_mp3_dir')
            if custom and os.path.isdir(custom):
                return custom
        except: pass
        d = os.path.join(self.base_dir, "mp3")
        try: os.makedirs(d, exist_ok=True)
        except: pass
        return d
    
    def _on_speed_changed(self, value):
        try:
            speed = float(value)
            self.logger.info(f"OPTION: Speed Multiplier set to {speed:.1f}x")
        except Exception as e:
            self.logger.error("Error handling speed change: %s", e)
    
    def _scan_mp3_folder(self):
        try:
            d = self._mp3_dir()
            files = []
            for name in os.listdir(d):
                if name.lower().endswith(".mp3"):
                    p = os.path.join(d, name)
                    try: mt = os.path.getmtime(p)
                    except: mt = 0
                    files.append((mt, name, p))
            files.sort(key=lambda x: x[0], reverse=True)
            files = files[:50]
            self._music_files = [ (n, p) for _, n, p in files ]
        except: self._music_files = []

    def _on_select_music_folder(self, wizard):
        from PyQt5.QtWidgets import QFileDialog
        folder = QFileDialog.getExistingDirectory(wizard, "Select Music Folder", self._mp3_dir())
        if folder:
            self.logger.info(f"WIZARD: User changed music folder to: {folder}")
            try:
                cfg = dict(self.config_manager.config)
                cfg['custom_mp3_dir'] = folder
                self.config_manager.save_config(cfg)
            except: pass
            wizard.mp3_dir = folder
            wizard.load_tracks(folder)
            self._custom_mp3_dir = folder

    def open_music_wizard(self):
        if getattr(self, "vlc_player", None) and self.vlc_player.is_playing():
            self.vlc_player.pause()
            self.wants_to_play = False
            if hasattr(self, 'playPauseButton'):
                self.playPauseButton.setText("PLAY")
                self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            if hasattr(self, 'timer') and self.timer.isActive():
                self.timer.stop()

        from ui.widgets.music_wizard import MergerMusicWizard
        t_start = getattr(self, "trim_start_ms", 0)
        t_end = getattr(self, "trim_end_ms", 0)
        if t_end <= t_start:
            t_start = 0
            t_end = self.original_duration_ms
        speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
        speed_segments = []
        if getattr(self, 'granular_checkbox', None) and self.granular_checkbox.isChecked():
            speed_segments = list(getattr(self, 'speed_segments', []))
        if speed_segments:
            wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
            wall_end = self._calculate_wall_clock_time(t_end, speed_segments, speed_factor)
            total_project_sec = wall_end - wall_start
        else:
            trimmed_dur_ms = t_end - t_start
            total_project_sec = (trimmed_dur_ms / 1000.0) / speed_factor
        if total_project_sec <= 0:
            QMessageBox.warning(self, "No Video", "Please load a video file first!")
            return
        mp3_dir = self._mp3_dir()
        wizard = MergerMusicWizard(
            self, self.vlc_instance, self.bin_dir, mp3_dir, 
            total_project_sec, speed_factor,
            trim_start_ms=t_start, trim_end_ms=t_end,
            speed_segments=speed_segments
        )
        curr_eff = self._get_master_eff()
        wizard.video_vol_slider.setValue(curr_eff)
        if hasattr(self, "_music_volume_pct"): wizard.music_vol_slider.setValue(int(self._music_volume_pct))
        if hasattr(self, "_wizard_tracks") and self._wizard_tracks:
            wizard.selected_tracks = list(self._wizard_tracks)
        if wizard.exec_() == QDialog.Accepted:
            self._wizard_tracks = list(wizard.selected_tracks)
            if self._wizard_tracks:
                trimmed_ms = t_end - t_start
                if speed_segments:
                    w_s = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
                    w_e = self._calculate_wall_clock_time(t_end, speed_segments, speed_factor)
                    project_total_sec = w_e - w_s
                else:
                    project_total_sec = (trimmed_ms / 1000.0) / speed_factor
                first_track = self._wizard_tracks[0]
                self._current_music_path = first_track[0]
                self._current_music_offset = first_track[1]
                self._music_volume_pct = wizard.music_vol_slider.value()
                if hasattr(self, "positionSlider"):
                    self.positionSlider.set_music_visible(True)
                    self.positionSlider.set_music_times(t_start, t_end)
                    self.music_timeline_start_ms = t_start
                    self.music_timeline_end_ms = t_end
                eff = wizard.video_vol_slider.value()
                raw = self.volume_slider.maximum() + self.volume_slider.minimum() - eff if self.volume_slider.invertedAppearance() else eff
                self.volume_slider.setValue(raw)
                if self.vlc_instance and first_track[0]:
                    if not hasattr(self, 'vlc_music_player') or self.vlc_music_player is None:
                        self.vlc_music_player = self.vlc_instance.media_player_new()
                    m = self.vlc_instance.media_new(first_track[0])
                    self.vlc_music_player.set_media(m)
                    self.vlc_music_player.audio_set_volume(int(self._music_volume_pct))
            else:
                self._reset_music_player()
        wizard.stop_previews()

    def _reset_music_player(self):
        if hasattr(self, 'vlc_music_player') and self.vlc_music_player:
            self.vlc_music_player.stop()
            self.vlc_music_player.release()
            self.vlc_music_player = None
        self.music_timeline_start_sec = None
        self.music_timeline_end_sec = None
        self._wizard_tracks = []
        if hasattr(self, 'positionSlider'):
            self.positionSlider.reset_music_times()

    def _get_master_eff(self):
        val = self.volume_slider.value()
        if self.volume_slider.invertedAppearance():
            return self.volume_slider.maximum() + self.volume_slider.minimum() - val
        return val

    def _music_eff(self, raw=None):
        if not hasattr(self, "_music_volume_pct"):
            try: self._music_volume_pct = int(self.config_manager.config.get('music_volume', 80))
            except: self._music_volume_pct = 80
        return self._music_volume_pct

    def _on_master_volume_changed(self, raw: int):
        try:
            eff = self._get_master_eff()
            self.logger.info(f"USER: Adjusted video track volume to {eff}%")
            player = getattr(self, "vlc_player", None)
            if player:
                player.audio_set_volume(eff)
            try:
                cfg = dict(self.config_manager.config)
                cfg['last_volume'] = eff
                self.config_manager.save_config(cfg)
            except: pass
            if hasattr(self, "_update_volume_badge"): self._update_volume_badge()
        except: pass

    def apply_master_volume(self):
        try:
            player = getattr(self, "vlc_player", None)
            if player:
                eff = int(self.config_manager.config.get('last_volume', 100))
                player.audio_set_volume(eff)
        except: pass

    def _get_music_offset_ms(self):
        if hasattr(self, "_wizard_tracks") and self._wizard_tracks:
            return int(self._wizard_tracks[0][1] * 1000)
        return 0

    def _on_slider_music_trim_changed(self, start_ms, end_ms):
        self.music_timeline_start_ms = start_ms
        self.music_timeline_end_ms = end_ms

    def _probe_audio_duration(self, path: str) -> float:
        try:
            ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')
            cmd = [ffprobe_path, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration", "-of", "csv=p=0", path]
            r = subprocess.run(cmd, text=True, capture_output=True, creationflags=0x08000000)
            return max(0.0, float(r.stdout.strip() or 0.0))
        except: return 0.0
