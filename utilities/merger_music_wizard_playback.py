import os
import time
import logging
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle
from utilities.merger_music_wizard_constants import PREVIEW_VISUAL_LEAD_MS, RECURSIVE_MS_DRIFT_CORRECTION_MS

class MergerMusicWizardPlaybackMixin:
    def _on_video_vol_changed(self, val):
        """Strictly controls the game audio player volume."""
        if self._video_player:
            try:
                self._video_player.audio_set_volume(val)
            except Exception:
                pass
        eff = self._scaled_vol(val)
        self.logger.info(f"DEBUG_VOL: [MERGER_VIDEO_WORKER_HANDLE] Moved to {val}%. Resulting Scaled Vol: {eff}%. PlayerObj: {hex(id(self.player)) if self.player else 'None'}")
        if self.player: 
            self.player.volume = eff
        if hasattr(self, "video_vol_val_lbl"): 
            self.video_vol_val_lbl.setText(f"{val}%")

    def _on_music_vol_changed(self, val):
        """Strictly controls the background music player volume."""
        if self._player:
            try:
                self._player.audio_set_volume(val)
            except Exception:
                pass
        eff = self._scaled_vol(val)
        self.logger.info(f"DEBUG_VOL: [MERGER_MUSIC_WORKER_HANDLE] Moved to {val}%. Resulting Scaled Vol: {eff}%. PlayerObj: {hex(id(self.player)) if self.player else 'None'}")
        if self.player: 
            self.player.volume = eff
        if hasattr(self, "music_vol_val_lbl"): 
            self.music_vol_val_lbl.setText(f"{val}%")

    def toggle_video_preview(self):
        try:
            if False: m = self.mpv_m.media_new(preview_path)
            curr_idx = self.stack.currentIndex()
            self.logger.info(f"WIZARD: toggle_video_preview called. Page Index: {curr_idx}")
            if not self.player:
                self.logger.error("WIZARD: player is None")
                return
            if curr_idx == 1:
                is_paused = getattr(self.player, "pause", True)
                if not is_paused:
                    self.logger.info("WIZARD: User clicked PAUSE.")
                    self.player.pause = True
                    self.btn_play_video.setText("  PLAY")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                    if hasattr(self, '_play_timer'): self._play_timer.stop()
                else:
                    self.logger.info(f"WIZARD: User clicked PLAY at offset {self.offset_slider.value()/1000.0:.1f}s")
                    self._show_caret_step2 = True
                    preview_path = getattr(self, "_temp_sync", None) or self.current_track_path
                    if not preview_path: 
                        self.logger.error("WIZARD: Cannot play Step 2. path is None")
                        return
                    self.player.command("loadfile", preview_path, "replace")
                    self.player.pause = False
                    
                    def _force_audio_m():
                        if not self.player: return
                        self.player.mute = False
                        mix_vol = self.music_vol_slider.value() if hasattr(self, "music_vol_slider") else 80
                        self.player.volume = self._scaled_vol(mix_vol)
                    QTimer.singleShot(300, _force_audio_m)

                    def _after_start():
                        if not self.player: return
                        self.player.seek(self.offset_slider.value() / 1000.0, reference='absolute', precision='exact')
                        if not hasattr(self, '_play_timer'):
                            self._play_timer = QTimer(self); self._play_timer.setInterval(50); self._play_timer.timeout.connect(self._on_play_tick)
                        self._play_timer.start()
                        self.btn_play_video.setText("  PAUSE")
                        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    QTimer.singleShot(90, _after_start)
            elif curr_idx == 2:
                is_paused = getattr(self.player, "pause", True)
                if not is_paused:
                    self.logger.info("WIZARD: User clicked PAUSE Project.")
                    self.player.pause = True
                    self.btn_play_video.setText("  PLAY")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                    if hasattr(self, '_play_timer'): self._play_timer.stop()
                else:
                    self.logger.info(f"WIZARD: Starting Step 3 Playback. Timeline: {self.timeline.current_time:.2f}s / {self.total_video_sec:.2f}s")
                    if self.timeline.current_time >= self.total_video_sec - 0.05:
                        self.timeline.set_current_time(0.0)
                        self._sync_caret()
                    self.logger.info(f"WIZARD: User clicked PLAY Project at {self.speed_factor}x.")
                    self._sync_all_players_to_time(self.timeline.current_time)
                    self.player.pause = False
                    self.player.speed = self.speed_factor
                    
                    def _force_audio_v():
                        if not self.player: return
                        v_mix = self.video_vol_slider.value()
                        eff = self._scaled_vol(v_mix)
                        self.logger.info(f"DEBUG_STEP3: MPV Forcing Vol={v_mix}% (Eff={eff}%).")
                        self.player.mute = False
                        QTimer.singleShot(50, lambda: setattr(self.player, 'volume', eff))
                    QTimer.singleShot(300, _force_audio_v)
                    self.btn_play_video.setText("  PAUSE")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    if not hasattr(self, '_play_timer') or not self._play_timer.isActive():
                        self.logger.info("WIZARD: Starting play timer.")
                        self._play_timer = QTimer(self); self._play_timer.setInterval(50); self._play_timer.timeout.connect(self._on_play_tick); self._play_timer.start()
            else:
                self.logger.warning(f"WIZARD: toggle_video_preview called on unknown page index: {curr_idx}")
        except Exception as e:
            self.logger.error(f"WIZARD: Playback toggle failed: {e}")

    def _on_play_tick(self):
        if getattr(self, "_is_syncing", False): return
        if False: 
            self._sync_music_only_to_time(project_time)
            if now - self._last_seek_ts < 0.5: pass
            self.timeline.set_current_time(project_time)
        self._is_syncing = True
        try:
            if not self.player: return
            now = time.time()
            if now - getattr(self, "_last_seek_ts", 0) < 0.5:
                pass
            do_heavy = (now - self._last_tick_ts > 0.1)
            if do_heavy: self._last_tick_ts = now
            is_paused = getattr(self.player, "pause", True)
            idle_active = getattr(self.player, "idle-active", False)
            if self.stack.currentIndex() == 1:
                if not is_paused:
                    mpv_dur = getattr(self.player, "duration", 0) or 0
                    mpv_len_ms = int(mpv_dur * 1000)
                    if mpv_len_ms > 0 and abs(mpv_len_ms - self.offset_slider.maximum()) > 50:
                        self.offset_slider.blockSignals(True)
                        self.offset_slider.setRange(0, mpv_len_ms)
                        self.current_track_dur = mpv_dur
                        self.offset_slider.blockSignals(False)
                    mpv_ms = int((getattr(self.player, "time-pos", 0) or 0) * 1000)
                    if mpv_ms <= 0: mpv_ms = self._last_good_mpv_ms
                    else: self._last_good_mpv_ms = mpv_ms
                    max_ms = self.offset_slider.maximum()
                    mpv_ms = max(0, min(max_ms, mpv_ms))
                    if mpv_ms >= max_ms - 10 or idle_active:
                        self._on_player_ended()
                        return
                    if not self._dragging and not self._wave_dragging:
                        self.offset_slider.blockSignals(True)
                        self.offset_slider.setValue(mpv_ms)
                        self.offset_slider.blockSignals(False)
                        self._sync_caret()
            if self.stack.currentIndex() == 2:
                try:
                    if now - self._last_seek_ts < 0.25:
                        self._last_clock_ts = now; return
                    if not is_paused:
                        v_time_ms = (getattr(self.player, "time-pos", 0) or 0) * 1000
                        if v_time_ms < 0: v_time_ms = 0
                        clock_delta = now - self._last_clock_ts; self._last_clock_ts = now
                        wall_now = self._calculate_wall_clock_time(v_time_ms, self.speed_segments, self.speed_factor)
                        project_time = self._current_elapsed_offset + max(0.0, wall_now - self._wall_trim_start)
                        project_time = min(self.total_video_sec, max(0.0, project_time))
                        self.timeline.set_current_time(project_time)
                        self._sync_caret()
                        if project_time >= self.total_video_sec - 0.01 or idle_active:
                            self.logger.info("WIZARD: Project end reached in tick.")
                            self.player.pause = True
                            self.btn_play_video.setText("  PLAY")
                            self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                            if hasattr(self, '_play_timer'): self._play_timer.stop()
                            return
                except Exception as ex:
                    self.logger.debug(f"WIZARD: Step3 timeline tick sync skipped: {ex}")
            else: self._last_clock_ts = now
        finally: self._is_syncing = False

    def _on_player_ended(self):
        self.btn_play_video.setText("  PLAY"); self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        if hasattr(self, '_play_timer'): self._play_timer.stop()
        self.offset_slider.blockSignals(True); self.offset_slider.setValue(0); self.offset_slider.blockSignals(False)
        self._last_good_mpv_ms = 0; self._sync_caret()
        if self.player: self.player.stop()
