import time
import threading
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle

class PlayerMixin:
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._scrub_lock = threading.RLock()

    def _safe_stop_playback(self):
        try:
            if getattr(self, "player", None):
                self.player.stop()
            if getattr(self, "playPauseButton", None):
                self.playPauseButton.setText("PLAY")
            if getattr(self, "positionSlider", None):
                self.positionSlider.setValue(0)
        except Exception:
            pass
    
    def toggle_play_pause(self):
        """Toggles play/pause for video and triggers music sync."""
        if not getattr(self, "input_file_path", None):
            return
        if not getattr(self, "player", None):
            return
        is_paused = getattr(self.player, "pause", True)
        if not is_paused:
            if self.timer.isActive():
                self.timer.stop()
            self.player.pause = True
            if getattr(self, "_music_preview_player", None):
                self._music_preview_player.pause = True
            self.wants_to_play = False
            self.set_player_position(getattr(self.player, 'time-pos', 0) * 1000, sync_only=True, force_pause=True)
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        else:
            idle_active = getattr(self.player, "idle-active", False)
            if idle_active:
                restart_ms = int(getattr(self, "trim_start_ms", 0) or 0)
                try:
                    self.player.command("seek", restart_ms / 1000.0, "absolute", "exact")
                except Exception:
                    self.player.seek(restart_ms / 1000.0, reference='absolute', precision='exact')
                if getattr(self, "positionSlider", None):
                    self.positionSlider.blockSignals(True)
                    self.positionSlider.setValue(restart_ms)
                    self.positionSlider.blockSignals(False)
            self.wants_to_play = True
            if getattr(self, "_music_preview_player", None) and getattr(self, "_wizard_tracks", None):
                curr_v_ms = getattr(self.player, "time-pos", 0) * 1000
                t_start = getattr(self, "trim_start_ms", 0)
                speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
                speed_segments = getattr(self, 'speed_segments', [])
                wall_now = self._calculate_wall_clock_time(curr_v_ms, speed_segments, speed_factor)
                wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
                real_audio_ms = (wall_now - wall_start) * 1000.0
                music_offset_sec = getattr(self, "_current_music_offset", 0.0)
                target_m_sec = music_offset_sec + (real_audio_ms / 1000.0)
                try:
                    self._music_preview_player.speed = 1.0
                    if hasattr(self._music_preview_player, 'set_rate'): 
                        self._music_preview_player.set_rate(1.0)
                    if abs((getattr(self._music_preview_player, "time-pos", 0) * 1000.0) - (target_m_sec * 1000.0)) > 50:
                        self._music_preview_player.seek(target_m_sec, reference='absolute', precision='exact')
                    self._music_preview_player.pause = False
                except: pass
            self.player.pause = False
            self.player.speed = self.playback_rate

            def _apply_audio_final():
                if not getattr(self, "player", None): return
                self.player.mute = False
                if hasattr(self, "_vol_eff"):
                    vol = self._vol_eff()
                    self.player.volume = vol
                self.player.mute = False
                if getattr(self, "_music_preview_player", None):
                    self._music_preview_player.volume = getattr(self, "_music_volume_pct", 80)
            QTimer.singleShot(300, _apply_audio_final)
            if not self.timer.isActive():
                self.timer.start(50)
            self.playPauseButton.setText("PAUSE")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def update_player_state(self):
        """On a timer, updates UI slider and keeps music in sync."""
        if not getattr(self, "player", None):
            return
        idle_active = getattr(self.player, "idle-active", False)
        if idle_active:
            if getattr(self, "playPauseButton", None):
                self.playPauseButton.setText("PLAY")
                self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            self.is_playing = False
            self.wants_to_play = False
            if getattr(self, "_music_preview_player", None):
                self._music_preview_player.pause = True
            if getattr(self, "timer", None) and self.timer.isActive():
                self.timer.stop()
            return
        slider = getattr(self, "positionSlider", None)
        if slider and slider.isSliderDown():
            return
        current_time_ms = (getattr(self.player, "time-pos", 0) or 0) * 1000
        if current_time_ms >= 0:
            if slider:
                slider.blockSignals(True)
                slider.setValue(int(current_time_ms))
                slider.blockSignals(False)
                slider.update()
            if getattr(self, "is_playing", False) and getattr(self, "_music_preview_player", None) and getattr(self, "_wizard_tracks", None):
                m_pos = getattr(self._music_preview_player, "time-pos", 0)
                t_start = getattr(self, "trim_start_ms", 0)
                speed = float(getattr(self, 'speed_spinbox', None).value() if hasattr(self, 'speed_spinbox') else 1.1)
                self.playback_rate = speed
                speed_segments = getattr(self, 'speed_segments', [])
                wall_now = self._calculate_wall_clock_time(current_time_ms, speed_segments, speed)
                wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed)
                time_since_music_start_project_ms = (wall_now - wall_start) * 1000.0
                real_audio_ms = time_since_music_start_project_ms 
                expected_m_sec = getattr(self, "_current_music_offset", 0.0) + (real_audio_ms / 1000.0)
                if abs(m_pos - expected_m_sec) > 0.15:
                    try: 
                        if hasattr(self._music_preview_player, 'set_rate'):
                            self._music_preview_player.set_rate(1.0)
                        music_player = self._music_preview_player
                        music_target_in_file_ms = expected_m_sec * 1000.0
                        if hasattr(music_player, 'get_time') and hasattr(music_player, 'time_pos'):
                            if abs((getattr(music_player, 'time_pos', 0) or 0)*1000.0 - music_target_in_file_ms) > 50:
                                music_player.time_pos = music_target_in_file_ms / 1000.0
                        else:
                            self._music_preview_player.seek(expected_m_sec, reference='absolute', precision='exact')
                    except: pass
            if hasattr(self, 'speed_segments') and getattr(self, 'granular_checkbox', None) and self.granular_checkbox.isChecked():
                target_speed = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
                segments = getattr(self, 'speed_segments', [])
                if segments:
                    for seg in segments:
                        if seg['start'] <= current_time_ms < seg['end']:
                            target_speed = seg['speed']
                            break
                if not hasattr(self, '_last_rate_update_main'): self._last_rate_update_main = 0
                now = time.time()
                curr_rate = getattr(self.player, "speed", 1.0)
                if abs(curr_rate - target_speed) > 0.01:
                    if getattr(self, "_is_test", False) or (now - self._last_rate_update_main > 0.1):
                        self.player.speed = target_speed
                        self._last_rate_update_main = now
            is_currently_paused = getattr(self.player, "pause", True)
            is_playing = not is_currently_paused
            if is_playing != getattr(self, "is_playing", None):
                self.is_playing = is_playing
                icon = QStyle.SP_MediaPause if self.is_playing else QStyle.SP_MediaPlay
                label = "PAUSE" if self.is_playing else "PLAY"
                if getattr(self, "playPauseButton", None):
                    self.playPauseButton.setText(label)
                    self.playPauseButton.setIcon(self.style().standardIcon(icon))

    def set_player_position(self, position_ms, sync_only=False, force_pause=False):
        """Sets video player position (in ms)."""
        if not hasattr(self, "_scrub_lock") or self._scrub_lock is None:
            self._scrub_lock = threading.RLock()
        if not hasattr(self, "_last_seek_ts"): self._last_seek_ts = 0.0
        if not hasattr(self, "_last_scrub_ts"): self._last_scrub_ts = 0.0
        with self._scrub_lock:
            try:
                now = time.time()
                self._last_seek_ts = now
                if not force_pause and (now - self._last_scrub_ts < 0.05):
                    return
                self._last_scrub_ts = now
                target_ms = int(position_ms)
                max_ms = int((getattr(self.player, "duration", 0) or 0) * 1000)
                if max_ms > 0:
                    target_ms = max(0, min(target_ms, max_ms - 1))
                if not sync_only and getattr(self, "player", None):
                    self.player.seek(target_ms / 1000.0, reference='absolute', precision='exact')
                if getattr(self, "_music_preview_player", None) and getattr(self, "_wizard_tracks", None):
                    if force_pause:
                        self._music_preview_player.pause = True
                    music_player = self._music_preview_player
                    if hasattr(music_player, 'set_rate'):
                        music_player.set_rate(1.0)
                    elif hasattr(music_player, 'speed'):
                        music_player.speed = 1.0
                    t_start = getattr(self, "trim_start_ms", 0)
                    speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
                    speed_segments = getattr(self, 'speed_segments', [])
                    wall_target = self._calculate_wall_clock_time(target_ms, speed_segments, speed_factor)
                    wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
                    real_audio_ms = (wall_target - wall_start) * 1000.0
                    target_m_sec = getattr(self, "_current_music_offset", 0.0) + (real_audio_ms / 1000.0)
                    try:
                        music_player = self._music_preview_player
                        if hasattr(music_player, 'set_rate'):
                            music_player.set_rate(1.0)
                        elif hasattr(music_player, 'speed'):
                            music_player.speed = 1.0
                        music_target_in_file_ms = target_m_sec * 1000.0
                        if hasattr(music_player, 'get_time') and hasattr(music_player, 'time_pos'):
                            if abs((getattr(music_player, 'time_pos', 0) or 0)*1000.0 - music_target_in_file_ms) > 50:
                                music_player.time_pos = music_target_in_file_ms / 1000.0
                        elif hasattr(music_player, 'time_pos'):
                             music_player.time_pos = music_target_in_file_ms / 1000.0
                        else:
                            self._music_preview_player.seek(target_m_sec, reference='absolute', precision='exact')
                    except: pass
            except Exception as e:
                if hasattr(self, "logger"):
                    self.logger.error(f"CRITICAL: Seek failed in set_player_position: {e}")

    def _calculate_wall_clock_time(self, video_ms, segments, base_speed):
        """
        [FIX #10] Calculates the real wall-clock time required to reach 'video_ms'.
        Optimized to avoid stuttering during preview.
        """
        if not segments:
            return float(video_ms) / base_speed
        if not segments or video_ms < segments[0]['start']:
             return float(video_ms) / base_speed
        current_video_time = 0.0
        accumulated_wall_time = 0.0
        target = float(video_ms)
        for seg in segments:
            start = seg['start']
            end = seg['end']
            speed = seg['speed']
            if start >= target:
                break
            if start > current_video_time:
                gap_dur = start - current_video_time
                accumulated_wall_time += gap_dur / base_speed
                current_video_time = start
            if target < end:
                partial_dur = target - start
                accumulated_wall_time += partial_dur / speed
                current_video_time = target
                break
            else:
                seg_dur = end - start
                accumulated_wall_time += seg_dur / speed
                current_video_time = end
        if current_video_time < target:
            remaining = target - current_video_time
            accumulated_wall_time += remaining / base_speed
        return accumulated_wall_time
    
    def _on_mpv_end_reached(self, event=None):
        """
        MPV reached end. (Native MPV thread)
        [FIX #23] Use QTimer to safely hop back to UI thread.
        """
        try:
            QTimer.singleShot(0, self._safe_handle_mpv_end)
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"MPV End Event failed to defer: {e}")

    def _safe_handle_mpv_end(self):
        """Handle end of media safely on the main thread."""
        try:
            if not self.signalsBlocked():
                self.video_ended_signal.emit()
        except Exception:
            pass