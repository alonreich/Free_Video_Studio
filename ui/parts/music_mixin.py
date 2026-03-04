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
        """[FIX] Initializes a dedicated MPV instance for music preview with correct flags."""
        try:
            if not getattr(self, "_music_preview_player", None):
                from system.utils import MPVSafetyManager
                # [FIX] Added explicit audio output for Windows stability
                kwargs = {
                    'vid': 'no',
                    'vo': 'null',
                    'osc': False,
                    'input_default_bindings': False,
                    'hr_seek': 'yes',
                    'hwdec': 'no',
                    'keep_open': 'yes',
                    'loglevel': "info",
                    'ytdl': False,
                    'demuxer_max_bytes': '300M',
                    'demuxer_max_back_bytes': '60M'
                }
                if sys.platform == 'win32':
                    kwargs['ao'] = 'wasapi'
                
                self._music_preview_player = MPVSafetyManager.create_safe_mpv(**kwargs)
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
            total_project_sec = (wall_end - wall_start) / 1000.0
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
            current_project_sec = max(0.0, (wall_current - wall_start) / 1000.0)
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
        
        QTimer.singleShot(100, lambda: self._complete_wizard_return(res, wizard, t_start, t_end, speed_segments, speed_factor))

    def _complete_wizard_return(self, res, wizard, t_start, t_end, speed_segments, speed_factor):
        try:
            if hasattr(wizard, '_release_player'):
                wizard._release_player()
            QApplication.processEvents()
            self._continue_wizard_return(res, t_start, t_end, speed_segments, speed_factor, wizard)
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"WIZARD: Return completion failed: {e}")
        finally:
            QTimer.singleShot(200, self._set_transition_false)

    def _continue_wizard_return(self, res, t_start, t_end, speed_segments, speed_factor, wizard):
        """Safely handle wizard return with comprehensive error handling."""
        try:
            if hasattr(self, 'music_button'): 
                self.music_button.setEnabled(True)
            self.wants_to_play = False
            self._in_transition = False
            
            self.raise_()
            self.activateWindow()
            if hasattr(self, "video_surface"): self.video_surface.show()

            if hasattr(self, "player") and self.player:
                self._safe_mpv_set("pause", True)
                self._safe_mpv_set("mute", False) 
            
            if hasattr(self, "_bind_main_player_output"):
                self._bind_main_player_output()
            
            if res == QDialog.Accepted:
                self._wizard_tracks = list(getattr(wizard, 'selected_tracks', []))
                self.logger.info(f"WIZARD: Received {len(self._wizard_tracks)} tracks from wizard.")
                
                if self._wizard_tracks:
                    self._music_volume_pct = getattr(wizard, 'music_vol_slider', None).value() if hasattr(wizard, 'music_vol_slider') else 80
                    self._video_volume_pct = getattr(wizard, 'video_vol_slider', None).value() if hasattr(wizard, 'video_vol_slider') else 80
                    
                    if self._ensure_music_player_ready():
                        first_track = self._wizard_tracks[0]
                        self._current_music_path = first_track[0]
                        self._current_music_offset = first_track[1]
                        self.logger.info(f"PREVIEW: Priming music player with {os.path.basename(self._current_music_path)}")
                        self._music_preview_player.command("loadfile", self._current_music_path, "replace")
                        self._safe_mpv_set("pause", True, target_player=self._music_preview_player)
                        
                        self._safe_mpv_set("mute", False, target_player=self._music_preview_player)
                        self._safe_mpv_set("volume", self._music_volume_pct, target_player=self._music_preview_player)

                        if hasattr(self, "_sync_all_volumes"):
                            self._sync_all_volumes()

                    if hasattr(self, "positionSlider"):
                        self.positionSlider.set_music_visible(True)
                        self.positionSlider.set_music_times(t_start, t_end)
                        # [FIX #1] Initialize music timeline bounds to match video trim on first return
                        self.music_timeline_start_ms = t_start
                        self.music_timeline_end_ms = t_end
                    
                    QTimer.singleShot(50, self._sync_music_preview)
                    
                    source_ms = getattr(self, "trim_start_ms", 0)
                    QTimer.singleShot(150, lambda: self._safe_seek_to_start(source_ms))
                    
                    if hasattr(self, 'timer'):
                        self.timer.start(40)
                else:
                    self._reset_music_player()
                
                QTimer.singleShot(500, self._final_unmute_after_wizard)
            else:
                self._restore_pre_wizard_state()
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"WIZARD: Return completion failed: {e}")
        finally:
            self._in_transition = False

    def _final_unmute_after_wizard(self):
        """[FIX] Resolves the silence issue by ensuring player is unmuted after all transitions are clear."""
        self.logger.info("WIZARD: Performing final unmute and volume sync.")
        if hasattr(self, "_sync_all_volumes"):
            self._sync_all_volumes()
        elif getattr(self, "player", None):
            self._safe_mpv_set("mute", False)
            if hasattr(self, "volume_slider"):
                self._safe_mpv_set("volume", self.volume_slider.value())
        
        music_player = getattr(self, "_music_preview_player", None)
        if music_player:
            self._safe_mpv_set("mute", False, target_player=music_player)
            self._safe_mpv_set("volume", self._music_eff(), target_player=music_player)

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
        """[FIX] Restore missing callback for main window music handles."""
        self.music_timeline_start_ms = start_ms
        self.music_timeline_end_ms = end_ms
        # If we have a single track, sync its duration to the visual handles
        if hasattr(self, "_wizard_tracks") and len(self._wizard_tracks) == 1:
            path, offset, _ = self._wizard_tracks[0]
            new_dur = (end_ms - start_ms) / 1000.0
            self._wizard_tracks[0] = (path, offset, new_dur)

    def _sync_music_preview(self):
        """[FIX #2] Sync music playback with video, respecting the pink overlay bounds."""
        if getattr(self, "_in_transition", False): return
        if not hasattr(self, "_wizard_tracks") or not self._wizard_tracks: return
        if not getattr(self, "player", None): return
        
        # Get current project time (wall clock)
        curr_v_ms = (self._safe_mpv_get("time-pos", 0) or 0) * 1000
        
        # [FIX #2] Project zero is now the start of the PINK music handles
        m_start_ms = getattr(self, "music_timeline_start_ms", getattr(self, "trim_start_ms", 0))
        m_end_ms = getattr(self, "music_timeline_end_ms", getattr(self, "trim_end_ms", 0))
        
        speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
        speed_segments = getattr(self, 'speed_segments', [])
        
        # Calculate wall clock position of video vs music start
        wall_now = self._calculate_wall_clock_time(curr_v_ms, speed_segments, speed_factor)
        wall_m_start = self._calculate_wall_clock_time(m_start_ms, speed_segments, speed_factor)
        
        # Time since music was supposed to start
        project_pos_sec = (wall_now - wall_m_start) / 1000.0
        
        music_player = getattr(self, "_music_preview_player", None)
        if not music_player: 
            if not self._ensure_music_player_ready(): return
            music_player = self._music_preview_player

        # [FIX #2] Silence if video is before or after pink music overlay
        if curr_v_ms < m_start_ms - 15 or curr_v_ms > m_end_ms + 15:
            self._safe_mpv_set("pause", True, target_player=music_player)
            self._safe_mpv_set("mute", True, target_player=music_player)
            return

        # Find which track belongs at this project time
        target_track = None
        accum_sec = 0.0
        for path, offset, dur in self._wizard_tracks:
            if accum_sec <= project_pos_sec < accum_sec + dur:
                target_track = (path, offset, project_pos_sec - accum_sec)
                break
            accum_sec += dur
            
        if not target_track:
            self._safe_mpv_set("pause", True, target_player=music_player)
            return
            
        path, offset, pos_in_track = target_track
        
        # Load file if different
        curr_loaded = self._safe_mpv_get("path", "", target_player=music_player)
        if not curr_loaded or os.path.basename(curr_loaded).lower() != os.path.basename(path).lower():
            self.logger.info(f"PREVIEW: Loading music track: {os.path.basename(path)}")
            music_player.command("loadfile", path, "replace")
            self._safe_mpv_set("pause", True, target_player=music_player)
            # Immediately force unmute on new file load
            self._safe_mpv_set("mute", False, target_player=music_player)
            self._safe_mpv_set("volume", self._music_eff(), target_player=music_player)
            return
            
        # Sync position if drifting
        m_pos = self._safe_mpv_get("time-pos", 0, target_player=music_player) or 0
        expected_m_sec = offset + pos_in_track
        
        if abs(m_pos - expected_m_sec) > 0.15: 
            self._safe_mpv_set("speed", 1.0, target_player=music_player)
            if self._mpv_lock.acquire(timeout=0.20):
                try:
                    music_player.seek(expected_m_sec, reference='absolute', precision='exact')
                finally: self._mpv_lock.release()
                
        # Sync pause state
        v_paused = self._safe_mpv_get("pause", True)
        self._safe_mpv_set("pause", v_paused, target_player=music_player)
        
        # Explicitly ensure music player is unmuted and volume set
        m_vol = self._music_eff()
        self._safe_mpv_set("volume", m_vol, target_player=music_player)
        self._safe_mpv_set("mute", False, target_player=music_player)

    def update_player_state(self):
        """[FIX] Overridden by mixin or main window to include music sync."""
        # This usually lives in player_mixin.py, but we ensure it's called
        # We'll update player_mixin to call self._sync_music_preview()
        pass

    def _probe_audio_duration(self, path: str) -> float:
        try:
            ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')
            cmd = [ffprobe_path, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration", "-of", "csv=p=0", path]
            r = subprocess.run(cmd, text=True, capture_output=True, creationflags=0x08000000)
            return max(0.0, float(r.stdout.strip() or 0.0))
        except: return 0.0
