import time
import threading
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle

class PlayerMixin:
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._scrub_lock = threading.RLock()
        self._mpv_lock = threading.RLock()
        self._is_seeking_active = False

    def _safe_mpv_set(self, prop, value, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return

        import mpv
        if not self._mpv_lock.acquire(timeout=0.05):
            return
        try:
            if getattr(p, '_core_shutdown', False): return
            if prop == "pause": p.pause = value
            elif prop == "speed": p.speed = value
            elif prop == "volume": p.volume = value
            elif prop == "mute": p.mute = value
            else: p.set_property(prop, value)
        except (AttributeError, mpv.ShutdownError):
            pass
        except Exception:
            pass
        finally:
            self._mpv_lock.release()

    def _safe_mpv_get(self, prop, default=None, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return default

        import mpv
        if not self._mpv_lock.acquire(timeout=0.02):
            return default
        try:
            if getattr(p, '_core_shutdown', False): return default
            return getattr(p, prop, default)
        except (AttributeError, mpv.ShutdownError):
            return default
        except Exception:
            return default
        finally:
            self._mpv_lock.release()

    def _safe_mpv_command(self, *args, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return False

        import mpv
        if not self._mpv_lock.acquire(timeout=0.05):
            return False
        try:
            if getattr(p, '_core_shutdown', False): return False
            p.command(*args)
            return True
        except (AttributeError, mpv.ShutdownError, SystemError):
            return False
        except Exception:
            return False
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
                self._safe_mpv_command("seek", restart_ms / 1000.0, "absolute", "exact")
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
                project_pos_sec = (wall_now - wall_start) / 1000.0
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
                self.timer.start(50)
            self.playPauseButton.setText("PAUSE")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def update_player_state(self):
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
                if hasattr(self, "_sync_music_preview"):
                    self._sync_music_preview()
                elif getattr(self, "is_playing", False) and getattr(self, "_wizard_tracks", None):
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
                        if isinstance(segments, list):
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
        if not hasattr(self, "_scrub_lock") or self._scrub_lock is None:
            self._scrub_lock = threading.RLock()
        target_ms = int(position_ms)
        if not hasattr(self, "_pending_seek_ms"): self._pending_seek_ms = None
        self._pending_seek_ms = target_ms
        if not hasattr(self, "_seek_timer"):
            from PyQt5.QtCore import QTimer
            self._seek_timer = QTimer(self)
            self._seek_timer.setSingleShot(True)
            self._seek_timer.timeout.connect(self._execute_throttled_seek)
        interval = 20 if force_pause else 80
        if not self._seek_timer.isActive():
            self._seek_timer.start(interval)

    def _execute_throttled_seek(self):
        if not hasattr(self, "_pending_seek_ms") or self._pending_seek_ms is None:
            return
        if getattr(self, "_is_seeking_active", False):
            if hasattr(self, "_seek_timer"):
                self._seek_timer.start(120)
            return
        target_ms = self._pending_seek_ms
        self._pending_seek_ms = None
        self._is_seeking_active = True
        if not self._mpv_lock.acquire(timeout=0.02):
            self._is_seeking_active = False
            if hasattr(self, "_seek_timer"):
                self._seek_timer.start(50)
            return
        try:
            p = getattr(self, "player", None)
            if not p or getattr(p, '_core_shutdown', False):
                return
            p_dur = getattr(p, "duration", 0) or 0
            max_ms = int(p_dur * 1000)
            if max_ms > 0:
                target_ms = max(0, min(target_ms, max_ms - 1))
            is_dragging = False
            if hasattr(self, "positionSlider"):
                is_dragging = self.positionSlider.isSliderDown()
            seek_mode = "absolute-percent" if target_ms < 0 else "absolute"
            precision = "fast" if is_dragging else "exact"
            p.command("seek", target_ms / 1000.0, seek_mode, precision)
            music_player = getattr(self, "_music_preview_player", None)
            if music_player and not getattr(music_player, '_core_shutdown', False) and getattr(self, "_wizard_tracks", None):
                t_start = getattr(self, "trim_start_ms", 0)
                speed_factor = self.speed_spinbox.value() if hasattr(self, 'speed_spinbox') else 1.1
                speed_segments = getattr(self, 'speed_segments', [])
                try:
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
                    music_player.command("seek", target_m_sec, "absolute", precision)
                except Exception as wall_err:
                    if hasattr(self, "logger"):
                        self.logger.debug(f"Seek wallclock error: {wall_err}")
        except Exception as e:
            if hasattr(self, "logger"):
                self.logger.error(f"STABILITY: Throttled seek failed: {e}")
        finally:
            self._mpv_lock.release()
            self._is_seeking_active = False

    def _calculate_wall_clock_time(self, video_ms, segments, base_speed):
        base_speed = max(0.01, float(base_speed))
        if not segments or not isinstance(segments, list):
            return float(video_ms) / base_speed
        if video_ms < segments[0].get('start', 0):
             return float(video_ms) / base_speed
        current_video_time = 0.0
        accumulated_wall_time = 0.0
        target = float(video_ms)
        for seg in segments:
            start = float(seg.get('start', 0))
            end = float(seg.get('end', 0))
            speed = max(0.01, float(seg.get('speed', base_speed)))
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
        try:
            QTimer.singleShot(0, self._safe_handle_mpv_end)
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"MPV End Event failed to defer: {e}")

    def _bind_main_player_output(self):
        if not getattr(self, "player", None):
            return
        if getattr(self, "_binding_player_output", False):
            return
        now = time.time()
        last_ts = float(getattr(self, "_last_player_output_bind_ts", 0.0) or 0.0)
        if (now - last_ts) < 0.8:
            return
        self._binding_player_output = True
        self._last_player_output_bind_ts = now

        def _perform_bind():
            try:
                wid = None
                surf = getattr(self, 'video_surface', None)
                if surf:
                    try:
                        wid = int(surf.winId())
                    except: pass
                if wid is not None and wid > 0:
                    try:
                        current_wid = self._safe_mpv_get("wid")
                        if current_wid == wid:
                            return
                    except:
                        pass
                    self.logger.info(f"HARDWARE_SET: Re-binding MPV to Main Surface WID {wid}")
                    try:
                        if self._mpv_lock.acquire(timeout=0.20):
                            try:
                                self.player.wid = wid
                            finally: self._mpv_lock.release()
                    except:
                        self._safe_mpv_set("wid", wid)
            except Exception as e:
                if hasattr(self, 'logger'):
                    self.logger.error(f"Failed to bind MPV output: {e}")
            finally:
                self._binding_player_output = False
        _perform_bind()

        def _delayed_bind():
            now2 = time.time()
            last2 = float(getattr(self, "_last_player_output_bind_ts", 0.0) or 0.0)
            if (now2 - last2) < 0.25:
                return
            self._last_player_output_bind_ts = now2
            _perform_bind()
        QTimer.singleShot(300, _delayed_bind)

    def _safe_handle_mpv_end(self):
        try:
            if not self.signalsBlocked():
                self.video_ended_signal.emit()
        except Exception:
            pass
