import vlc
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
            if getattr(self, "vlc_player", None):
                self.vlc_player.stop()
            if getattr(self, "vlc_music_player", None):
                self.vlc_music_player.stop()
            if getattr(self, "playPauseButton", None):
                self.playPauseButton.setText("PLAY")
            if getattr(self, "positionSlider", None):
                self.positionSlider.setValue(0)
        except Exception:
            pass
    
    def toggle_play_pause(self):
        """Toggles play/pause for video and triggers music sync."""
        if getattr(self, '_is_seeking_from_end', False) or not getattr(self, "input_file_path", None):
            return
        if not getattr(self, "vlc_player", None):
            return
        if self.vlc_player.is_playing():
            if self.timer.isActive():
                self.timer.stop()
            self.vlc_player.pause()
            if getattr(self, "vlc_music_player", None):
                try: self.vlc_music_player.pause()
                except: pass
            self.wants_to_play = False
            self.set_vlc_position(self.vlc_player.get_time(), sync_only=True, force_pause=True)
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        else:
            state = self.vlc_player.get_state()
            if state == vlc.State.Ended:
                self.vlc_player.stop()
                if getattr(self, 'vlc_music_player', None):
                    getattr(self, 'vlc_music_player', None).stop()
                self.set_vlc_position(self.trim_start_ms)
            self.wants_to_play = True
            self.vlc_player.play()
            self.vlc_player.set_rate(self.playback_rate)

            def _apply_audio_final():
                if not getattr(self, "vlc_player", None): return
                self.vlc_player.audio_set_mute(False)
                if hasattr(self, "_vol_eff"):
                    vol = self._vol_eff()
                    self.vlc_player.audio_set_volume(vol)
                m_player = getattr(self, "vlc_music_player", None)
                if m_player:
                    m_player.audio_set_mute(False)
                    if hasattr(self, "_music_eff"):
                        m_vol = self._music_eff()
                        m_player.audio_set_volume(m_vol)
                tracks = self.vlc_player.audio_get_track_description()
                if tracks and len(tracks) > 1:
                    self.vlc_player.audio_set_track(tracks[1][0])
                else:
                    self.vlc_player.audio_set_track(1)
                self.vlc_player.audio_set_mute(False)
            QTimer.singleShot(300, _apply_audio_final)
            if not self.timer.isActive():
                self.timer.start(50)
            self.playPauseButton.setText("PAUSE")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def update_player_state(self):
        """On a timer, updates UI slider and keeps music in sync."""
        player = getattr(self, "vlc_player", None)
        if not player:
            return
        state = player.get_state()
        is_active = state in (vlc.State.Playing, vlc.State.Paused, vlc.State.Opening, vlc.State.Buffering)
        if not is_active:
            return
        slider = getattr(self, "positionSlider", None)
        if slider and slider.isSliderDown():
            return
        current_time = player.get_time()
        if current_time >= 0:
            if slider:
                slider.blockSignals(True)
                slider.setValue(int(current_time))
                slider.blockSignals(False)
                slider.update()
            if getattr(self, 'vlc_music_player', None) and hasattr(self, "_wizard_tracks") and self._wizard_tracks:
                 m_player = getattr(self, 'vlc_music_player')
                 if not m_player.is_playing() and getattr(self, "wants_to_play", False):
                     if self.music_timeline_start_ms <= current_time < self.music_timeline_end_ms:
                        self.set_vlc_position(current_time, sync_only=True)
            if hasattr(self, 'speed_segments') and getattr(self, 'granular_checkbox', None) and self.granular_checkbox.isChecked():
                target_speed = getattr(self, 'speed_spinbox', None).value() if hasattr(self, 'speed_spinbox') else 1.1
                current_segment_index = -1
                if self.speed_segments:
                    for i, seg in enumerate(self.speed_segments):
                        if seg['start'] <= current_time < seg['end']:
                            target_speed = seg['speed']
                            current_segment_index = i
                            break
                if not hasattr(self, '_last_rate_update_main'): self._last_rate_update_main = 0
                now = time.time()
                if abs(player.get_rate() - target_speed) > 0.05:
                    if now - self._last_rate_update_main > 0.1:
                        player.set_rate(target_speed)
                        self._last_rate_update_main = now
            is_currently_vlc_playing = (state == vlc.State.Playing)
            if is_currently_vlc_playing != getattr(self, "is_playing", None):
                self.is_playing = is_currently_vlc_playing
                icon = QStyle.SP_MediaPause if self.is_playing else QStyle.SP_MediaPlay
                label = "PAUSE" if self.is_playing else "PLAY"
                if getattr(self, "playPauseButton", None):
                    self.playPauseButton.setText(label)
                    self.playPauseButton.setIcon(self.style().standardIcon(icon))

    def set_vlc_position(self, position_ms, sync_only=False, force_pause=False):
        """Sets video player position (in ms) AND syncs the music player state."""
        if not hasattr(self, "_scrub_lock") or self._scrub_lock is None:
            self._scrub_lock = threading.RLock()
        with self._scrub_lock:
            try:
                now = time.time()
                if not hasattr(self, "_last_scrub_ts"): self._last_scrub_ts = 0
                if not force_pause and (now - self._last_scrub_ts < 0.05):
                    return
                self._last_scrub_ts = now
                target_ms = int(position_ms)
                music_player = getattr(self, 'vlc_music_player', None)
                if music_player and hasattr(self, "_wizard_tracks") and self._wizard_tracks:
                    if self.music_timeline_start_ms >= 0 and self.music_timeline_end_ms > 0:
                        is_video_playing = not force_pause and getattr(self, 'wants_to_play', False)
                        is_within_music_bounds = self.music_timeline_start_ms <= target_ms < self.music_timeline_end_ms
                        if is_video_playing and is_within_music_bounds:
                            if not music_player.is_playing():
                                music_player.play()
                        else:
                            if music_player.is_playing():
                                music_player.pause()
                        if is_within_music_bounds:
                            music_player.set_rate(1.0)
                            time_since_music_start_project_ms = target_ms - self.music_timeline_start_ms
                            speed = float(getattr(self, 'speed_spinbox', None).value() if hasattr(self, 'speed_spinbox') else 1.1)
                            real_audio_ms = time_since_music_start_project_ms / speed
                            if hasattr(self, 'granular_checkbox') and self.granular_checkbox.isChecked() and hasattr(self, 'speed_segments'):
                                wall_now = self._calculate_wall_clock_time(target_ms, self.speed_segments, speed)
                                wall_start = self._calculate_wall_clock_time(self.music_timeline_start_ms, self.speed_segments, speed)
                                real_audio_ms = (wall_now - wall_start) * 1000.0
                            file_offset_ms = self._get_music_offset_ms() 
                            music_target_in_file_ms = int(real_audio_ms + file_offset_ms)
                            if abs(music_player.get_time() - music_target_in_file_ms) > 50:
                                music_player.set_time(music_target_in_file_ms)
                            if hasattr(self, "_sync_all_volumes"):
                                self._sync_all_volumes()
                            else:
                                music_player.audio_set_mute(False)
                                if hasattr(music_player, "audio_set_volume"):
                                    music_player.audio_set_volume(self._music_eff())
                            music_player.set_rate(1.0)
                            if music_player.get_rate() != 1.0:
                                music_player.set_rate(1.0)
                    else:
                        if music_player.is_playing(): music_player.pause()
                if not sync_only and getattr(self, "vlc_player", None):
                    self.vlc_player.set_time(target_ms)
            except Exception as e:
                if hasattr(self, "logger"):
                    self.logger.error(f"CRITICAL: Seek failed in set_vlc_position: {e}")

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
    
    def _on_vlc_end_reached(self, event=None):
        """
        VLC reached end. (Native VLC thread)
        [FIX #23] Use QTimer to safely hop back to UI thread.
        """
        try:
            QTimer.singleShot(0, self._safe_handle_vlc_end)
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"VLC End Event failed to defer: {e}")

    def _safe_handle_vlc_end(self):
        """Handle end of media safely on the main thread."""
        try:
            if not self.signalsBlocked():
                self.video_ended_signal.emit()
        except Exception:
            pass