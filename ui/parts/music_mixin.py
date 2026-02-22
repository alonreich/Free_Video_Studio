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
        except Exception as e:
            self.logger.debug(f"DEBUG: custom_mp3_dir check failed: {e}")
        d = os.path.join(self.base_dir, "mp3")
        try:
            if not os.path.exists(d):
                os.makedirs(d, exist_ok=True)
        except Exception as e:
            self.logger.error(f"Failed to create default mp3 directory: {e}")
        return d
    
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
            except Exception as ex:
                self.logger.error(f"WIZARD: Failed saving custom music folder '{folder}': {ex}")
            wizard.mp3_dir = folder
            wizard.load_tracks(folder)
            self._custom_mp3_dir = folder

    def _ensure_music_player_ready(self):
        """[FIX #1] Initializes a dedicated MPV instance for music preview in the main window."""
        try:
            import mpv
            if not getattr(self, "_music_preview_player", None):
                self._music_preview_player = mpv.MPV(
                    vid='no',
                    osc=False,
                    input_default_bindings=False,
                    hr_seek='yes',
                    hwdec='no',
                    keep_open='yes',
                    loglevel="info"
                )
                self.logger.info("PREVIEW: Music preview player engine initialized.")
            return True
        except Exception as e:
            self.logger.error(f"PREVIEW: Failed to init music player: {e}")
            return False

    def open_music_wizard(self):
        if hasattr(self, 'music_button'):
            self.music_button.setEnabled(False)
        self._pre_wizard_state = {}
        if hasattr(self, "player") and self.player:
            try:
                self._pre_wizard_state['player_playing'] = not getattr(self.player, "pause", True)
                self._pre_wizard_state['player_mute'] = getattr(self.player, "mute", False)
                self.player.pause = True
                if hasattr(self.player, 'pause') and callable(self.player.pause):
                    self.player.pause()
                self.player.mute = True
                if getattr(self, "_music_preview_player", None):
                    self._music_preview_player.pause = True
            except Exception as ex:
                self.logger.debug(f"WIZARD: Failed to capture/prepare main player state: {ex}")
        self.wants_to_play = False
        if hasattr(self, 'playPauseButton'):
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.positionSlider.show()
        self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
        if hasattr(self, 'timer') and self.timer.isActive():
            self.timer.stop()
        QTimer.singleShot(150, self._delayed_wizard_launch)

    def _delayed_wizard_launch(self):
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
            if hasattr(self, 'music_button'): self.music_button.setEnabled(True)
            self._restore_pre_wizard_state()
            return
        mp3_dir = self._mp3_dir()
        wizard = MergerMusicWizard(
            self, self.player, self.bin_dir, mp3_dir, 
            total_project_sec, speed_factor,
            trim_start_ms=t_start, trim_end_ms=t_end,
            speed_segments=speed_segments
        )
        curr_eff = self._get_master_eff()
        wizard.video_vol_slider.setValue(curr_eff)
        if hasattr(self, "_music_volume_pct"): wizard.music_vol_slider.setValue(int(self._music_volume_pct))
        if hasattr(self, "_wizard_tracks") and self._wizard_tracks:
            wizard.selected_tracks = list(self._wizard_tracks)
        res = wizard.exec_()
        if hasattr(self, 'music_button'): self.music_button.setEnabled(True)
        self.wants_to_play = False
        if hasattr(self, "player") and self.player:
            try: self.player.pause = True
            except: pass
        if res == QDialog.Accepted:
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
                self._video_volume_pct = wizard.video_vol_slider.value()
                if self._ensure_music_player_ready():
                    try:
                        self._music_preview_player.command("loadfile", self._current_music_path, "replace")
                        self._music_preview_player.pause = True
                        self._music_preview_player.volume = self._music_volume_pct
                    except Exception as e:
                        self.logger.error(f"PREVIEW: Failed to load music file: {e}")
                try:
                    cfg = dict(self.config_manager.config)
                    cfg['music_mix_volume'] = self._music_volume_pct
                    cfg['video_mix_volume'] = self._video_volume_pct
                    self.config_manager.save_config(cfg)
                except Exception as ex:
                    self.logger.error(f"WIZARD: Failed to persist mix volumes: {ex}")
                if hasattr(self, "positionSlider"):
                    self.positionSlider.set_music_visible(True)
                    self.positionSlider.set_music_times(t_start, t_end)
                    self.music_timeline_start_ms = t_start
                    self.music_timeline_end_ms = t_end
                if hasattr(self, "_on_master_volume_changed"):
                    self._on_master_volume_changed(self.volume_slider.value())
                if getattr(self, "player", None):
                    self.player.pause = True
                    if hasattr(wizard, 'final_timeline_time'):
                        try:
                            source_ms = wizard._project_time_to_source_ms(wizard.final_timeline_time)
                            self.player.seek(source_ms / 1000.0, reference='absolute', precision='exact')
                            self.positionSlider.setValue(int(source_ms))
                            self.logger.info(f"WIZARD: Returning to main at {source_ms}ms (Project: {wizard.final_timeline_time:.2f}s)")
                        except Exception as seek_err:
                            self.logger.debug(f"WIZARD: Seek back to main failed: {seek_err}")
                self.wants_to_play = False
                if hasattr(self, 'playPauseButton'):
                    self.playPauseButton.setText("PLAY")
                    self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                if hasattr(self, 'timer'):
                    self.timer.start(40)
            else:
                self._reset_music_player()
        else:
            self._restore_pre_wizard_state()
        wizard.stop_previews()

    def _restore_pre_wizard_state(self):
        """Restores playback and mute state after music wizard is canceled."""
        if not hasattr(self, '_pre_wizard_state'):
            return
        try:
            if getattr(self, "player", None):
                if 'player_mute' in self._pre_wizard_state:
                    self.player.mute = self._pre_wizard_state['player_mute']
                if 'player_playing' in self._pre_wizard_state and self._pre_wizard_state['player_playing']:
                    self.player.pause = False
                    self.wants_to_play = True
                    if getattr(self, "_music_preview_player", None) and getattr(self, "_wizard_tracks", None):
                        self._music_preview_player.pause = False
                    if hasattr(self, 'timer') and not self.timer.isActive():
                        self.timer.start(40)
                    if hasattr(self, 'playPauseButton'):
                        self.playPauseButton.setText("PAUSE")
                        self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.debug(f"Restore wizard state failed: {e}")
        finally:
            if hasattr(self, '_pre_wizard_state'):
                del self._pre_wizard_state

    def _reset_music_player(self):
        self.music_timeline_start_sec = None
        self.music_timeline_end_sec = None
        self._wizard_tracks = []
        if getattr(self, "_music_preview_player", None):
            try:
                self._music_preview_player.stop()
            except:
                pass
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
