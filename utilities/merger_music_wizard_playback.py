import os
import time
import logging
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle
from utilities.merger_music_wizard_constants import PREVIEW_VISUAL_LEAD_MS, RECURSIVE_MS_DRIFT_CORRECTION_MS

class MergerMusicWizardPlaybackMixin:
    def _on_video_vol_changed(self, val):
        """Strictly controls the game audio player volume."""
        eff = self._scaled_vol(val)
        self.logger.info(f"DEBUG_VOL: [MERGER_VIDEO_WORKER_HANDLE] Moved to {val}%. Resulting Scaled Vol: {eff}%. PlayerObj: {hex(id(self._video_player))}")
        if getattr(self, "_video_player", None): 
            self._video_player.audio_set_volume(eff)
        if hasattr(self, "video_vol_val_lbl"): 
            self.video_vol_val_lbl.setText(f"{val}%")

    def _on_music_vol_changed(self, val):
        """Strictly controls the background music player volume."""
        eff = self._scaled_vol(val)
        self.logger.info(f"DEBUG_VOL: [MERGER_MUSIC_WORKER_HANDLE] Moved to {val}%. Resulting Scaled Vol: {eff}%. PlayerObj: {hex(id(self._player))}")
        if getattr(self, "_player", None): 
            self._player.audio_set_volume(eff)
        if hasattr(self, "music_vol_val_lbl"): 
            self.music_vol_val_lbl.setText(f"{val}%")

    def toggle_video_preview(self):
        try:
            curr_idx = self.stack.currentIndex()
            self.logger.info(f"WIZARD: toggle_video_preview called. Page Index: {curr_idx}")
            if curr_idx == 1:
                if not getattr(self, "_player", None): 
                    self.logger.error("WIZARD: _player is None in Step 2")
                    return
                st = self._player.get_state()
                self.logger.info(f"WIZARD: Step 2 player state: {st}")
                if st == 3:
                    self.logger.info("WIZARD: User clicked PAUSE.")
                    self._player.pause()
                    self.btn_play_video.setText("  PLAY")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                    if hasattr(self, '_play_timer'): self._play_timer.stop()
                else:
                    self.logger.info(f"WIZARD: User clicked PLAY at offset {self.offset_slider.value()/1000.0:.1f}s")
                    self._show_caret_step2 = True
                    if st in (0, 5, 6, 7):
                        preview_path = getattr(self, "_temp_sync", None) or self.current_track_path
                        if not getattr(self, "vlc_m", None) or not preview_path: 
                            self.logger.error(f"WIZARD: Cannot play Step 2. vlc_m={bool(getattr(self, 'vlc_m', None))}, path={preview_path}")
                            return
                        m = self.vlc_m.media_new(preview_path)
                        self._player.set_media(m)
                    self._player.play()
                    
                    def _force_audio_m():
                        if not getattr(self, "_player", None): return
                        self._player.audio_set_mute(False)
                        mix_vol = self.music_vol_slider.value() if hasattr(self, "music_vol_slider") else 80
                        self._player.audio_set_volume(self._scaled_vol(mix_vol))
                    QTimer.singleShot(300, _force_audio_m)

                    def _after_start():
                        if not getattr(self, "_player", None): return
                        self._player.set_time(int(self.offset_slider.value()))
                        if not hasattr(self, '_play_timer'):
                            self._play_timer = QTimer(self); self._play_timer.setInterval(50); self._play_timer.timeout.connect(self._on_play_tick)
                        self._play_timer.start()
                        self.btn_play_video.setText("  PAUSE")
                        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    QTimer.singleShot(90, _after_start)
            elif curr_idx == 2:
                if not getattr(self, "_video_player", None): 
                    self.logger.error("WIZARD: _video_player is None in Step 3")
                    return
                st = self._video_player.get_state()
                self.logger.info(f"WIZARD: Step 3 video_player state: {st}")
                if st == 3:
                    self.logger.info("WIZARD: User clicked PAUSE Project.")
                    self._video_player.pause()
                    if self._player: self._player.pause()
                    self.btn_play_video.setText("  PLAY")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                    if hasattr(self, '_play_timer'): self._play_timer.stop()
                else:
                    self.logger.info(f"WIZARD: Starting Step 3 Playback. Timeline: {self.timeline.current_time:.2f}s / {self.total_video_sec:.2f}s")
                    if self.timeline.current_time >= self.total_video_sec - 0.05:
                        self.timeline.set_current_time(0.0)
                        self._sync_caret()
                    self.logger.info(f"WIZARD: User clicked PLAY Project at {self.speed_factor}x.")
                    if st in (0, 5, 6, 7) or self.timeline.current_time < 0.1:
                        self.logger.info("WIZARD: Syncing all players before play.")
                        self._sync_all_players_to_time(self.timeline.current_time)
                    self.logger.info("WIZARD: Calling _video_player.play()")
                    self._video_player.play()
                    self._video_player.set_rate(self.speed_factor)
                    
                    def _force_audio_v():
                        if not getattr(self, "_video_player", None): return
                        v_mix = self.video_vol_slider.value()
                        eff = self._scaled_vol(v_mix)
                        self.logger.info(f"DEBUG_STEP3: Merger Forcing Video Vol={v_mix}% (Eff={eff}%). Player={hex(id(self._video_player))}")
                        self._video_player.audio_set_mute(False)
                        QTimer.singleShot(50, lambda: self._video_player.audio_set_volume(eff))
                        v_tracks = self._video_player.audio_get_track_description()
                        if v_tracks and len(v_tracks) > 1:
                            self._video_player.audio_set_track(v_tracks[1][0])
                        else:
                            self._video_player.audio_set_track(1)
                    QTimer.singleShot(300, _force_audio_v)
                    if getattr(self, "_player", None): 
                        self.logger.info("WIZARD: Calling _player.play() for music")
                        self._player.play()
                        self._player.set_rate(1.0)
                        
                        def _force_audio_m_tl():
                            if not getattr(self, "_player", None): return
                            m_mix = self.music_vol_slider.value()
                            eff = self._scaled_vol(m_mix)
                            self.logger.info(f"DEBUG_STEP3: Merger Forcing Music Vol={m_mix}% (Eff={eff}%). Player={hex(id(self._player))}")
                            self._player.audio_set_mute(False)
                            QTimer.singleShot(50, lambda: self._player.audio_set_volume(eff))
                        QTimer.singleShot(350, _force_audio_m_tl)
                    self.btn_play_video.setText("  PAUSE")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    
                    def _final_vol_safety():
                        if getattr(self, "_video_player", None):
                            self._video_player.audio_set_volume(self.video_vol_slider.value())
                        if getattr(self, "_player", None):
                            self._player.audio_set_volume(self.music_vol_slider.value())
                        self.logger.info("WIZARD: Final volume safety re-enforcement complete.")
                    QTimer.singleShot(1000, _final_vol_safety)
                    if not hasattr(self, '_play_timer') or not self._play_timer.isActive():
                        self.logger.info("WIZARD: Starting play timer.")
                        self._play_timer = QTimer(self); self._play_timer.setInterval(50); self._play_timer.timeout.connect(self._on_play_tick); self._play_timer.start()
            else:
                self.logger.warning(f"WIZARD: toggle_video_preview called on unknown page index: {curr_idx}")
        except Exception as e:
            self.logger.error(f"WIZARD: Playback toggle failed: {e}")

    def _on_play_tick(self):
        if getattr(self, "_is_syncing", False): return
        self._is_syncing = True
        try:
            now = time.time()
            do_heavy = (now - self._last_tick_ts > 0.1)
            if do_heavy: self._last_tick_ts = now
            if self.stack.currentIndex() == 1 and getattr(self, "_player", None):
                try:
                    st = self._player.get_state()
                    if st == 3:
                        vlc_len = self._player.get_length()
                        if vlc_len > 0 and abs(vlc_len - self.offset_slider.maximum()) > 50:
                            self.offset_slider.blockSignals(True)
                            self.offset_slider.setRange(0, vlc_len)
                            self.current_track_dur = vlc_len / 1000.0
                            self.offset_slider.blockSignals(False)
                        vlc_ms = int(self._player.get_time() or 0)
                        if vlc_ms <= 0: vlc_ms = self._last_good_vlc_ms
                        else: self._last_good_vlc_ms = vlc_ms
                        if vlc_ms > 10000:
                            increments = int((vlc_ms - 10000) / 10000)
                            vlc_ms -= (increments * RECURSIVE_MS_DRIFT_CORRECTION_MS)
                        vlc_ms = int(vlc_ms + PREVIEW_VISUAL_LEAD_MS)
                        max_ms = self.offset_slider.maximum()
                        vlc_ms = max(0, min(max_ms, vlc_ms))
                        if vlc_ms >= max_ms - 10:
                            self._on_vlc_ended()
                            return
                        if not self._dragging and not self._wave_dragging:
                            self.offset_slider.blockSignals(True)
                            self.offset_slider.setValue(vlc_ms)
                            self.offset_slider.blockSignals(False)
                            self._sync_caret()
                    elif st == 6: self._on_vlc_ended()
                except Exception as ex:
                    self.logger.debug(f"WIZARD: Step2 play tick sync skipped: {ex}")
            if self.stack.currentIndex() == 2 and getattr(self, "_video_player", None):
                try:
                    if now - self._last_seek_ts < 0.5:
                        self._last_clock_ts = now; return
                    st = self._video_player.get_state()
                    if st in (1, 2, 3):
                        v_time_ms = self._video_player.get_time()
                        if v_time_ms < 0: v_time_ms = 0
                        clock_delta = now - self._last_clock_ts; self._last_clock_ts = now
                        wall_now = self._calculate_wall_clock_time(v_time_ms, self.speed_segments, self.speed_factor)
                        project_time = self._current_elapsed_offset + max(0.0, wall_now - self._wall_trim_start)
                        project_time = min(self.total_video_sec, max(0.0, project_time))
                        self.timeline.set_current_time(project_time)
                        self._sync_caret()
                        if st == 3 and do_heavy and getattr(self, "_player", None):
                            self._sync_music_only_to_time(project_time)
                        if project_time >= self.total_video_sec - 0.01:
                            self.logger.info("WIZARD: Project end reached in tick.")
                            if st == 3: self._video_player.pause()
                            if self._player: self._player.pause()
                            self.btn_play_video.setText("  PLAY")
                            self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                            if hasattr(self, '_play_timer'): self._play_timer.stop()
                            return
                    elif st == 6:
                        self.logger.info("WIZARD: Video reached end of project. Stopping music.")
                        if self._player: self._player.pause()
                        self.btn_play_video.setText("  PLAY")
                        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                        if hasattr(self, '_play_timer'): self._play_timer.stop()
                        self.timeline.set_current_time(self.total_video_sec)
                        self._sync_caret()
                    else: self._last_clock_ts = now
                except Exception as ex:
                    self.logger.debug(f"WIZARD: Step3 timeline tick sync skipped: {ex}")
            else: self._last_clock_ts = now
        finally: self._is_syncing = False

    def _on_vlc_ended(self):
        self.btn_play_video.setText("  PLAY"); self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        if hasattr(self, '_play_timer'): self._play_timer.stop()
        self.offset_slider.blockSignals(True); self.offset_slider.setValue(0); self.offset_slider.blockSignals(False)
        self._last_good_vlc_ms = 0; self._sync_caret()
        if getattr(self, "_player", None): self._player.stop()
