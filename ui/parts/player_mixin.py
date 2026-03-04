import time
import threading
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle

class PlayerMixin:
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._scrub_lock = threading.RLock()
        self._mpv_lock = threading.Lock()

    def _safe_mpv_set(self, prop, value, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return
        if not self._mpv_lock.acquire(timeout=0.20):
            return
        try:
            if prop == "pause": p.pause = value
            elif prop == "speed": p.speed = value
            elif prop == "volume": p.volume = value
            elif prop == "mute": p.mute = value
            else: p.set_property(prop, value)
        except: pass
        finally:
            self._mpv_lock.release()

    def _safe_mpv_get(self, prop, default=None, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return default
        if not self._mpv_lock.acquire(timeout=0.20):
            return default
        try: return getattr(p, prop, default)
        except: return default
        finally:
            self._mpv_lock.release()

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
        if getattr(self, "_in_transition", False):
            return
        if not getattr(self, "input_file_path", None):
            return
        if not getattr(self, "player", None):
            return
        is_paused = self._safe_mpv_get("pause", True)
        if not is_paused:
            if self.timer.isActive():
                self.timer.stop()
            self._safe_mpv_set("pause", True)
            music_player = getattr(self, "_music_preview_player", None)
            if music_player:
                self._safe_mpv_set("pause", True, target_player=music_player)
            self.wants_to_play = False
            curr_pos = self._safe_mpv_get("time-pos", 0) or 0
            self.set_player_position(curr_pos * 1000, sync_only=True, force_pause=True)
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        else:
            idle_active = self._safe_mpv_get("idle-active", False)
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
            music_player = getattr(self, "_music_preview_player", None)
            if music_player and getattr(self, "_wizard_tracks", None):
                curr_v_ms = (self._safe_mpv_get("time-pos", 0) or 0) * 1000
                t_start = getattr(self, "trim_start_ms", 0)
                speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
                speed_segments = getattr(self, 'speed_segments', [])
                wall_now = self._calculate_wall_clock_time(curr_v_ms, speed_segments, speed_factor)
                wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
                # [FIX] wall times are in ms, so project_pos_sec is correct without extra multiplication
                project_pos_sec = (wall_now - wall_start) / 1000.0
                
                # Check which track is active
                target_m_sec = 0.0
                accum = 0.0
                for path, offset, dur in self._wizard_tracks:
                    if accum <= project_pos_sec < accum + dur:
                        target_m_sec = offset + (project_pos_sec - accum)
                        break
                    accum += dur
                
                self._safe_mpv_set("speed", 1.0, target_player=music_player)
                curr_m_pos = self._safe_mpv_get("time-pos", 0, target_player=music_player) or 0
                if abs(curr_m_pos - target_m_sec) > 0.15:
                    try:
                        if self._mpv_lock.acquire(timeout=0.20):
                            try: music_player.seek(target_m_sec, reference='absolute', precision='exact')
                            finally: self._mpv_lock.release()
                    except: pass
                self._safe_mpv_set("pause", False, target_player=music_player)
            self._safe_mpv_set("speed", self.playback_rate)
            self._safe_mpv_set("pause", False)
            if not self.timer.isActive():
                self.timer.start(40)
            self.playPauseButton.setText("PAUSE")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def update_player_state(self):
        """On a timer, updates UI slider and keeps music in sync."""
        if getattr(self, "_in_transition", False):
            return
        try:
            p = getattr(self, "player", None)
            if not p:
                return
            idle_active = self._safe_mpv_get("idle-active", True)
            if idle_active:
                if getattr(self, "playPauseButton", None):
                    self.playPauseButton.setText("PLAY")
                    self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                self.is_playing = False
                self.wants_to_play = False
                music_player = getattr(self, "_music_preview_player", None)
                if music_player:
                    self._safe_mpv_set("pause", True, target_player=music_player)
                if getattr(self, "timer", None) and self.timer.isActive():
                    self.timer.stop()
                return
            slider = getattr(self, "positionSlider", None)
            if slider and slider.isSliderDown():
                return
            current_time_ms = (self._safe_mpv_get("time-pos", 0) or 0) * 1000
            if current_time_ms >= 0:
                if slider:
                    if slider.maximum() <= 0:
                        dur = self._safe_mpv_get("duration", 0)
                        if dur > 0:
                            slider.setRange(0, int(dur * 1000))
                            if hasattr(slider, 'set_duration_ms'):
                                slider.set_duration_ms(int(dur * 1000))
                    slider.blockSignals(True)
                    slider.setValue(int(current_time_ms))
                    slider.blockSignals(False)
                    slider.update()
                
                # [FIX] Comprehensive Music Sync (Supports Multiple Tracks)
                if hasattr(self, "_sync_music_preview"):
                    self._sync_music_preview()
                elif getattr(self, "is_playing", False) and getattr(self, "_wizard_tracks", None):
                    # Fallback for simple single-track sync if Mixin method is missing
                    try:
                        music_player = getattr(self, "_music_preview_player", None)
                        if music_player:
                            m_pos = self._safe_mpv_get("time-pos", 0, target_player=music_player) or 0
                            t_start = getattr(self, "trim_start_ms", 0)
                            speed = float(getattr(self, 'speed_spinbox', None).value() if hasattr(self, 'speed_spinbox') else 1.1)
                            speed_segments = getattr(self, 'speed_segments', [])
                            wall_now = self._calculate_wall_clock_time(current_time_ms, speed_segments, speed)
                            wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed)
                            project_pos_sec = (wall_now - wall_start) / 1000.0
                            
                            first_track = self._wizard_tracks[0]
                            expected_m_sec = first_track[1] + project_pos_sec
                            if abs(m_pos - expected_m_sec) > 0.15:
                                if self._mpv_lock.acquire(timeout=0.20):
                                    try: music_player.seek(expected_m_sec, reference='absolute', precision='exact')
                                    finally: self._mpv_lock.release()
                            v_paused = self._safe_mpv_get("pause", True)
                            self._safe_mpv_set("pause", v_paused, target_player=music_player)
                    except: pass
                if hasattr(self, 'speed_segments') and getattr(self, 'granular_checkbox', None) and self.granular_checkbox.isChecked():
                    try:
                        target_speed = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
                        segments = getattr(self, 'speed_segments', [])
                        if segments:
                            for seg in segments:
                                if seg['start'] <= current_time_ms < seg['end']:
                                    target_speed = seg['speed']
                                    break
                        if not hasattr(self, '_last_rate_update_main'): self._last_rate_update_main = 0
                        now = time.time()
                        curr_rate = self._safe_mpv_get("speed", 1.0)
                        if abs(curr_rate - target_speed) > 0.01:
                            if getattr(self, "_is_test", False) or (now - self._last_rate_update_main > 0.1):
                                self._safe_mpv_set("speed", target_speed)
                                self._last_rate_update_main = now
                    except: pass
                try:
                    is_currently_paused = self._safe_mpv_get("pause", True)
                    is_playing = not is_currently_paused
                    if is_playing != getattr(self, "is_playing", None):
                        self.is_playing = is_playing
                        icon = QStyle.SP_MediaPause if self.is_playing else QStyle.SP_MediaPlay
                        label = "PAUSE" if self.is_playing else "PLAY"
                        if getattr(self, "playPauseButton", None):
                            self.playPauseButton.setText(label)
                            self.playPauseButton.setIcon(self.style().standardIcon(icon))
                except: pass
        except Exception:
            pass

    def set_player_position(self, position_ms, sync_only=False, force_pause=False):
        """Sets video player position (in ms) with enhanced throttling for stability."""
        if not hasattr(self, "_scrub_lock") or self._scrub_lock is None:
            self._scrub_lock = threading.RLock()
        if not hasattr(self, "_last_seek_ts"): self._last_seek_ts = 0.0
        if not hasattr(self, "_last_scrub_ts"): self._last_scrub_ts = 0.0
        with self._scrub_lock:
            try:
                now = time.time()
                self._last_seek_ts = now
                if not force_pause and (now - self._last_scrub_ts < 0.1):
                    return
                self._last_scrub_ts = now
                target_ms = int(position_ms)
                p_dur = self._safe_mpv_get("duration", 0) or 0
                max_ms = int(p_dur * 1000)
                if max_ms > 0:
                    target_ms = max(0, min(target_ms, max_ms - 1))
                if not sync_only and getattr(self, "player", None):
                    if self._mpv_lock.acquire(timeout=0.20):
                        try:
                            time.sleep(0.01)
                            self.player.seek(target_ms / 1000.0, reference='absolute', precision='exact')
                        finally: self._mpv_lock.release()
                music_player = getattr(self, "_music_preview_player", None)
                if music_player and getattr(self, "_wizard_tracks", None):
                    if force_pause:
                        self._safe_mpv_set("pause", True, target_player=music_player)
                    self._safe_mpv_set("speed", 1.0, target_player=music_player)
                    t_start = getattr(self, "trim_start_ms", 0)
                    speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
                    speed_segments = getattr(self, 'speed_segments', [])
                    wall_target = self._calculate_wall_clock_time(target_ms, speed_segments, speed_factor)
                    wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
                    project_pos_sec = (wall_target - wall_start) / 1000.0
                    
                    target_m_sec = 0.0
                    accum = 0.0
                    for path, offset, dur in self._wizard_tracks:
                        if accum <= project_pos_sec < accum + dur:
                            target_m_sec = offset + (project_pos_sec - accum)
                            break
                        accum += dur

                    try:
                        if self._mpv_lock.acquire(timeout=0.20):
                            try:
                                time.sleep(0.005)
                                music_player.seek(target_m_sec, reference='absolute', precision='exact')
                            finally: self._mpv_lock.release()
                    except: pass
            except Exception as e:
                if hasattr(self, "logger"):
                    self.logger.error(f"CRITICAL: Seek failed in set_player_position: {e}")

    def _calculate_wall_clock_time(self, video_ms, segments, base_speed):
        """
        [FIX #10] Calculates the real wall-clock time required to reach 'video_ms'.
        Optimized to avoid stuttering during preview.
        Returns milliseconds.
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

    def _bind_main_player_output(self):
        """[FIX] Forcefully re-binds MPV output and triggers a redraw to resolve black frames."""
        if not getattr(self, "player", None):
            return
        if getattr(self, "_binding_player_output", False):
            return
        self._binding_player_output = True
        
        def _perform_bind():
            try:
                wid = None
                surf = getattr(self, 'video_surface', None)
                if surf:
                    try:
                        wid = int(surf.winId())
                    except: pass
                
                if wid is not None:
                    self.logger.info(f"HARDWARE_SET: Re-binding MPV to Main Surface WID {wid}")
                    
                    self._safe_mpv_set("wid", 0)
                    time.sleep(0.01)
                    
                    try:
                        if self._mpv_lock.acquire(timeout=0.20):
                            try:
                                self.player.wid = wid
                                if hasattr(self.player, 'command'):
                                    self.player.command("video-reconfig")
                                    self.player.command("frame-step")
                                    self.player.command("frame-back-step")
                            finally: self._mpv_lock.release()
                    except:
                        self._safe_mpv_set("wid", wid)
                
            except Exception as e:
                if hasattr(self, 'logger'):
                    self.logger.error(f"Failed to bind MPV output: {e}")
            finally:
                self._binding_player_output = False
        
        _perform_bind()
        QTimer.singleShot(200, _perform_bind)

    def _safe_handle_mpv_end(self):
        """Handle end of media safely on the main thread."""
        try:
            if not self.signalsBlocked():
                self.video_ended_signal.emit()
        except Exception:
            pass
