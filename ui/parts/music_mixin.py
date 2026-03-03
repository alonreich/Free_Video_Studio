import os
import sys
import subprocess
import tempfile
import time
from PyQt5.QtCore import Qt, QTimer, QRect
from PyQt5.QtGui import QPixmap, QPainter, QColor
from PyQt5.QtWidgets import (QStyleOptionSlider, QStyle, QDialog, QVBoxLayout,
                            QLabel, QHBoxLayout, QPushButton, QWidget, QSlider, QApplication, QMessageBox)

from ui.widgets.trimmed_slider import TrimmedSlider
from system.utils import MPVSafetyManager

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
            if not getattr(self, "_music_preview_player", None):
                from system.utils import MPVSafetyManager
                self._music_preview_player = MPVSafetyManager.create_safe_mpv(
                    vid='no',
                    osc=False,
                    input_default_bindings=False,
                    hr_seek='yes',
                    hwdec='no',
                    keep_open='yes',
                    loglevel="info"
                )
                if self._music_preview_player:
                    self.logger.info("PREVIEW: Music preview player engine initialized.")
                else:
                    self.logger.error("PREVIEW: Failed to create music preview player")
                    return False
            return True
        except Exception as e:
            self.logger.error(f"PREVIEW: Failed to init music player: {e}")
            return False

    def open_music_wizard(self):
        self._in_transition = True
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
        current_source_ms = self.positionSlider.value()
        if speed_segments:
            wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
            wall_current = self._calculate_wall_clock_time(current_source_ms, speed_segments, speed_factor)
            current_project_sec = max(0.0, wall_current - wall_start)
        else:
            current_project_sec = max(0.0, ((current_source_ms - t_start) / 1000.0) / speed_factor)
        wizard = MergerMusicWizard(
            self, self.player, self.bin_dir, mp3_dir, 
            total_project_sec, speed_factor,
            trim_start_ms=t_start, trim_end_ms=t_end,
            speed_segments=speed_segments,
            initial_project_sec=current_project_sec
        )
        curr_eff = self._get_master_eff()
        wizard.video_vol_slider.blockSignals(True)
        wizard.video_vol_slider.setValue(curr_eff)
        wizard.video_vol_slider.blockSignals(False)
        if hasattr(self, "_music_volume_pct"): 
            wizard.music_vol_slider.blockSignals(True)
            wizard.music_vol_slider.setValue(int(self._music_volume_pct))
            wizard.music_vol_slider.blockSignals(False)
        if hasattr(self, "_wizard_tracks") and self._wizard_tracks:
            wizard.selected_tracks = list(self._wizard_tracks)
        res = wizard.exec_()
        self._in_transition = True
        if hasattr(wizard, 'stop_previews'):
            wizard.stop_previews()
        if hasattr(wizard, '_release_player'):
            wizard._release_player()
        QTimer.singleShot(400, lambda: self._complete_wizard_return(res, wizard, t_start, t_end, speed_segments, speed_factor))

    def _complete_wizard_return(self, res, wizard, t_start, t_end, speed_segments, speed_factor):
        try:
            if hasattr(wizard, '_release_player'):
                wizard._release_player()
            QApplication.processEvents()
            QTimer.singleShot(50, lambda: self._continue_wizard_return(res, t_start, t_end, speed_segments, speed_factor, wizard))
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"WIZARD: Return completion failed: {e}")
        finally:
            QTimer.singleShot(500, self._set_transition_false)

    def _continue_wizard_return(self, res, t_start, t_end, speed_segments, speed_factor, wizard):
        """Safely handle wizard return with comprehensive error handling."""
        try:
            if hasattr(self, 'music_button'): 
                self.music_button.setEnabled(True)
            self.wants_to_play = False
            QApplication.processEvents()
            main_player_ok = False
            if hasattr(self, "player") and self.player:
                try:
                    if hasattr(self, "_safe_mpv_set"):
                        self._safe_mpv_set("pause", True)
                    else:
                        try:
                            self.player.pause = True
                        except Exception as ex:
                            self.logger.debug(f"WIZARD: Direct pause failed, trying command: {ex}")
                            try:
                                self.player.command("set_property", "pause", True)
                            except:
                                pass
                    main_player_ok = True
                except Exception as ex:
                    self.logger.warning(f"WIZARD: Failed to pause main player: {ex}")
            if hasattr(self, "_music_preview_player") and self._music_preview_player:
                try:
                    self._music_preview_player.pause = True
                except Exception as ex:
                    self.logger.debug(f"WIZARD: Failed to pause music preview: {ex}")
            if not main_player_ok:
                self.logger.warning("WIZARD: Main player state uncertain, but NOT attempting recovery to avoid crashes")
            if hasattr(self, "_bind_main_player_output"):
                try:
                    self._bind_main_player_output()
                except Exception as ex:
                    self.logger.debug(f"WIZARD: Failed to re-bind player output: {ex}")
            if res == QDialog.Accepted:
                self._wizard_tracks = list(getattr(wizard, 'selected_tracks', []))
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
                    self._music_volume_pct = getattr(wizard, 'music_vol_slider', None).value() if hasattr(wizard, 'music_vol_slider') else 80
                    self._video_volume_pct = getattr(wizard, 'video_vol_slider', None).value() if hasattr(wizard, 'video_vol_slider') else 80
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
                        try:
                            self.positionSlider.set_music_visible(True)
                            self.positionSlider.set_music_times(t_start, t_end)
                            self.music_timeline_start_ms = t_start
                            self.music_timeline_end_ms = t_end
                        except Exception as ex:
                            self.logger.error(f"WIZARD: Failed to update position slider: {ex}")
                    if hasattr(self, "_on_master_volume_changed"):
                        try:
                            self._on_master_volume_changed(self.volume_slider.value())
                        except Exception as ex:
                            self.logger.debug(f"WIZARD: Failed to update volume display: {ex}")
                    if getattr(self, "player", None):
                        source_ms = getattr(self, "trim_start_ms", 0)
                        QTimer.singleShot(300, lambda: self._safe_seek_to_start(source_ms))
                    self.wants_to_play = False
                    if hasattr(self, 'playPauseButton'):
                        try:
                            self.playPauseButton.setText("PLAY")
                            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                        except Exception as ex:
                            self.logger.debug(f"WIZARD: Failed to update play button: {ex}")
                    if hasattr(self, 'timer'):
                        try:
                            self.timer.start(40)
                        except Exception as ex:
                            self.logger.debug(f"WIZARD: Failed to start timer: {ex}")
                else:
                    self._reset_music_player()
            else:
                self._restore_pre_wizard_state()
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"WIZARD: Return completion failed: {e}")
            self._in_transition = False
        finally:
            QTimer.singleShot(800, self._set_transition_false)

    def _safe_mpv_set(self, property_name, value, target_player=None):
        """Safely set an MPV property using MPVSafetyManager."""
        player = target_player if target_player is not None else getattr(self, "player", None)
        if player:
            return MPVSafetyManager.safe_mpv_set(player, property_name, value)
        return False

    def _safe_mpv_command(self, command, *args, target_player=None):
        """Safely execute an MPV command using MPVSafetyManager."""
        player = target_player if target_player is not None else getattr(self, "player", None)
        if player:
            return MPVSafetyManager.safe_mpv_command(player, command, *args)
        return False

    def _safe_seek_to_start(self, source_ms):
        """Safely seek to start position with error handling."""
        try:
            if getattr(self, "player", None):
                self.set_player_position(int(source_ms), sync_only=False, force_pause=True)
                self.logger.info(f"WIZARD: Returning to main at start position ({source_ms}ms)")
        except Exception as seek_err:
            self.logger.debug(f"WIZARD: Seek back to main failed: {seek_err}")

    def _set_transition_false(self):
        self._in_transition = False

    def _restore_pre_wizard_state(self):
        """Restores playback and mute state after music wizard is canceled."""
        if not hasattr(self, '_pre_wizard_state'):
            return
        try:
            if getattr(self, "player", None):
                if 'player_mute' in self._pre_wizard_state:
                    self._safe_mpv_set("mute", self._pre_wizard_state['player_mute'])
                if 'player_playing' in self._pre_wizard_state and self._pre_wizard_state['player_playing']:
                    self._safe_mpv_set("pause", False)
                    self.wants_to_play = True
                    if getattr(self, "_music_preview_player", None) and getattr(self, "_wizard_tracks", None):
                        self._safe_mpv_set("pause", False, target_player=self._music_preview_player)
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

    def _attempt_player_recovery(self):
        """Attempt to recover the main MPV player if it's corrupted."""
        try:
            self.logger.info("WIZARD: Attempting player recovery...")
            if hasattr(self, "player") and self.player:
                try:
                    self.player.stop()
                except:
                    pass
                self.player = None
                self.mpv_instance = None
            QApplication.processEvents()
            QTimer.singleShot(100, self._actually_reinit_player)
        except Exception as e:
            self.logger.error(f"WIZARD: Player recovery setup failed: {e}")

    def _actually_reinit_player(self):
        """Actually reinitialize the player after cleanup."""
        try:
            if hasattr(self, "_setup_mpv"):
                self._setup_mpv()
                self.logger.info("WIZARD: Player recovery completed.")
                if hasattr(self, "current_video_path") and self.current_video_path:
                    QTimer.singleShot(200, lambda: self._restore_video_after_recovery())
        except Exception as e:
            self.logger.error(f"WIZARD: Player reinitialization failed: {e}")

    def _restore_video_after_recovery(self):
        """Restore video playback after player recovery."""
        try:
            if hasattr(self, "current_video_path") and self.current_video_path and self.player:
                self.player.command("loadfile", self.current_video_path, "replace")
                self.player.pause = True
                if hasattr(self, "trim_start_ms"):
                    self.set_player_position(self.trim_start_ms, sync_only=False, force_pause=True)
        except Exception as e:
            self.logger.debug(f"WIZARD: Failed to restore video after recovery: {e}")

    def _reset_music_player(self):
        self.music_timeline_start_ms = 0
        self.music_timeline_end_ms = 0
        self.music_timeline_start_sec = None
        self.music_timeline_end_sec = None
        self._wizard_tracks = []
        if getattr(self, "_music_preview_player", None):
            try:
                self._music_preview_player.pause = True
            except:
                pass
        if hasattr(self, 'positionSlider'):
            self.positionSlider.reset_music_times()

    def _get_master_eff(self):
        val = self.volume_slider.value()
        if self.volume_slider.invertedAppearance():
            return self.volume_slider.maximum() + self.volume_slider.minimum() - val
        return val

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
