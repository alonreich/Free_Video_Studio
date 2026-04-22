import time
import threading
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle
from system.time_sync import TimeSyncEngine
from system.utils import MPVSafetyManager
class PlayerMixin:
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._scrub_lock = threading.RLock()
        self._mpv_lock = threading.RLock()
        self._is_seeking_active = False
        self._in_freeze_segment = False
        self._freeze_start_ts = 0
        self._freeze_seg = None
    def _safe_mpv_set(self, prop, value, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return
        MPVSafetyManager.safe_mpv_set(p, prop, value, lock=self._mpv_lock)
    def _safe_mpv_get(self, prop, default=None, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return default
        return MPVSafetyManager.safe_mpv_get(p, prop, default, lock=self._mpv_lock)
    def _safe_mpv_command(self, *args, target_player=None):
        p = target_player if target_player is not None else getattr(self, "player", None)
        if not p: return False
        return MPVSafetyManager.safe_mpv_command(p, args[0], *args[1:], lock=self._mpv_lock)
    def _safe_stop_playback(self):
        try:
            p = getattr(self, "player", None)
            if p:
                self._safe_mpv_command("stop")
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
                    self._safe_mpv_command("seek", target_m_sec, "absolute", "exact", target_player=music_player)
                self._safe_mpv_set("pause", False, target_player=music_player)
            self._safe_mpv_set("speed", self.playback_rate)
            self._safe_mpv_set("pause", False)
            if not self.timer.isActive():
                self.timer.start(50)
            self.playPauseButton.setText("PAUSE")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
    def update_player_state(self):
        if getattr(self, "_in_transition", False): return
        try:
            if getattr(self, "_in_freeze_segment", False):
                now = time.time()
                elapsed = (now - self._freeze_start_ts) * 1000
                seg = self._freeze_seg
                seg_dur = seg['end'] - seg['start']
                if elapsed >= seg_dur:
                    self._in_freeze_segment = False
                    self._safe_mpv_set("pause", False)
                    self._safe_mpv_command("seek", seg['end'] / 1000.0, "absolute", "exact")
                    if hasattr(self, "positionSlider"): self.positionSlider.setValue(int(seg['end']))
                return
            p = getattr(self, "player", None)
            if not p: return
            idle_active = self._safe_mpv_get("idle-active", True)
            if idle_active:
                if getattr(self, "playPauseButton", None):
                    self.playPauseButton.setText("PLAY")
                    self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                self.is_playing = False; self.wants_to_play = False
                music_player = getattr(self, "_music_preview_player", None)
                if music_player: self._safe_mpv_set("pause", True, target_player=music_player)
                if getattr(self, "timer", None) and self.timer.isActive(): self.timer.stop()
                return
            slider = getattr(self, "positionSlider", None)
            if slider and slider.isSliderDown(): return
            current_time_ms = (self._safe_mpv_get("time-pos", 0) or 0) * 1000
            if current_time_ms >= 0:
                if slider:
                    if slider.maximum() <= 0:
                        dur = self._safe_mpv_get("duration", 0)
                        if dur > 0:
                            slider.setRange(0, int(dur * 1000))
                            if hasattr(slider, 'set_duration_ms'): slider.set_duration_ms(int(dur * 1000))
                    slider.blockSignals(True); slider.setValue(int(current_time_ms)); slider.blockSignals(False); slider.update()
                if hasattr(self, "_sync_music_preview"): self._sync_music_preview()
                self._check_and_update_speed(current_time_ms)
                is_p = not self._safe_mpv_get("pause", True)
                if is_p != getattr(self, "is_playing", None):
                    self.is_playing = is_p; icon = QStyle.SP_MediaPause if self.is_playing else QStyle.SP_MediaPlay; label = "PAUSE" if self.is_playing else "PLAY"
                    if hasattr(self, "playPauseButton"): self.playPauseButton.setText(label); self.playPauseButton.setIcon(self.style().standardIcon(icon))
        except Exception: pass
    def _check_and_update_speed(self, current_ms):
        if not hasattr(self, "speed_segments") or not getattr(self, "granular_checkbox", None) or not self.granular_checkbox.isChecked(): return
        target_speed = self.playback_rate; target_seg = None
        for seg in self.speed_segments:
            if abs(seg["speed"]) < 0.001 and seg["start"] <= current_ms < seg["end"]:
                target_speed = 0.0; target_seg = seg; break
        if abs(target_speed) > 0.001:
            for seg in self.speed_segments:
                if abs(seg["speed"]) >= 0.001 and seg["start"] <= current_ms < seg["end"]:
                    target_speed = seg["speed"]; target_seg = seg; break
        if abs(target_speed) < 0.001:
            if not getattr(self, "_in_freeze_segment", False):
                self._in_freeze_segment = True; self._freeze_start_ts = time.time(); self._freeze_seg = target_seg; self._safe_mpv_set("pause", True)
                music_player = getattr(self, "_music_preview_player", None)
                if music_player: self._safe_mpv_set("pause", True, target_player=music_player)
            return
        now = time.time()
        if not hasattr(self, "_last_speed_update"): self._last_speed_update = 0
        if now - self._last_speed_update < 0.20: return
        current_rate = self._safe_mpv_get("speed", 1.0)
        if abs(current_rate - target_speed) > 0.005:
            if self._safe_mpv_set("speed", target_speed):
                self._last_speed_update = now
    def set_player_position(self, position_ms, sync_only=False, force_pause=False):
        if not hasattr(self, "_scrub_lock") or self._scrub_lock is None:
            self._scrub_lock = threading.RLock()
        target_ms = int(position_ms)
        self._pending_seek_ms = target_ms
        self._pending_force_pause = force_pause
        self._captured_slider_down = False
        if hasattr(self, "positionSlider"):
            self._captured_slider_down = self.positionSlider.isSliderDown()
        self._captured_speed = 1.1
        if hasattr(self, "speed_spinbox"):
            self._captured_speed = self.speed_spinbox.value()
        now = time.time()
        if not hasattr(self, "_last_scrub_ts"): self._last_scrub_ts = 0
        if getattr(self, "_is_seeking_active", False):
            if hasattr(self, "_last_seek_start_ts") and (now - self._last_seek_start_ts > 2.0):
                self._is_seeking_active = False
        if not force_pause and (now - self._last_scrub_ts < 0.05):
            if not hasattr(self, "_seek_timer") or not self._seek_timer.isActive():
                QTimer.singleShot(50, self._execute_throttled_seek)
            return
        self._last_scrub_ts = now
        if not hasattr(self, "_seek_timer"):
            self._seek_timer = QTimer(self)
            self._seek_timer.setSingleShot(True)
            self._seek_timer.timeout.connect(self._execute_throttled_seek)
        interval = 30 if force_pause else 50
        if sync_only:
            self._execute_throttled_seek()
        elif not self._seek_timer.isActive():
            self._seek_timer.start(interval)
    def _execute_throttled_seek(self):
        if not hasattr(self, "_pending_seek_ms") or self._pending_seek_ms is None:
            return
        if getattr(self, "_is_seeking_active", False):
            QTimer.singleShot(30, self._execute_throttled_seek)
            return
        target_ms = self._pending_seek_ms
        force_pause = getattr(self, "_pending_force_pause", False)
        slider_is_down = getattr(self, "_captured_slider_down", False)
        speed_factor = getattr(self, "_captured_speed", 1.1)
        self._pending_seek_ms = None
        self._pending_force_pause = False
        self._is_seeking_active = True
        self._last_seek_start_ts = time.time()
        def _native_seek_task():
            try:
                if getattr(self, "_in_transition", False) or getattr(self, "_shutting_down", False):
                    return
                target_m_sec = -1.0
                precision = "fast"
                if not slider_is_down: precision = "exact"
                music_player = getattr(self, "_music_preview_player", None)
                if music_player and getattr(self, "_wizard_tracks", None):
                    try:
                        t_start = getattr(self, "trim_start_ms", 0)
                        speed_segments = getattr(self, 'speed_segments', [])
                        wall_target = self._calculate_wall_clock_time(target_ms, speed_segments, speed_factor)
                        wall_start = self._calculate_wall_clock_time(t_start, speed_segments, speed_factor)
                        project_pos_sec = (wall_target - wall_start) / 1000.0
                        accum = 0.0
                        for path, offset, dur in self._wizard_tracks:
                            if accum <= project_pos_sec < accum + dur:
                                target_m_sec = offset + (project_pos_sec - accum)
                                break
                            accum += dur
                    except: pass
                p = getattr(self, "player", None)
                if p and not getattr(p, '_core_shutdown', False) and not getattr(p, '_safe_shutdown_initiated', False):
                    if force_pause: self._safe_mpv_set("pause", True)
                    self._safe_mpv_command("seek", target_ms / 1000.0, "absolute", precision)
                    if music_player and target_m_sec >= 0 and not getattr(music_player, '_core_shutdown', False):
                        if force_pause: self._safe_mpv_set("pause", True, target_player=music_player)
                        self._safe_mpv_command("seek", target_m_sec, "absolute", "exact", target_player=music_player)
                        self._safe_mpv_set("speed", 1.0, target_player=music_player)
            finally:
                self._is_seeking_active = False
        threading.Thread(target=_native_seek_task, daemon=True).start()
    def _calculate_wall_clock_time(self, video_ms, segments, base_speed):
        accumulated_wall_time = 0.0
        current_v = 0.0
        for seg in segments:
            if video_ms <= seg['start']: break
            if seg['start'] > current_v:
                if base_speed < 0.001: accumulated_wall_time += (seg['start'] - current_v)
                else: accumulated_wall_time += (seg['start'] - current_v) / base_speed
            partial_dur = min(video_ms, seg['end']) - seg['start']
            if seg['speed'] < 0.001: accumulated_wall_time += partial_dur
            else: accumulated_wall_time += partial_dur / seg['speed']
            current_v = seg['end']
        if video_ms > current_v:
            if base_speed < 0.001: accumulated_wall_time += (video_ms - current_v)
            else: accumulated_wall_time += (video_ms - current_v) / base_speed
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
                    except: pass
                    self.logger.info(f"HARDWARE_SET: Re-binding MPV to Main Surface WID {wid}")
                    self._safe_mpv_set("wid", wid)
            except Exception as e:
                if hasattr(self, 'logger'):
                    self.logger.error(f"Failed to bind MPV output: {e}")
            finally:
                self._binding_player_output = False
        self._binding_player_output = True
        self._last_player_output_bind_ts = now
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
