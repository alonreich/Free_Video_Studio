import os
import time
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QStyle
from utilities.merger_music_wizard_constants import PREVIEW_VISUAL_LEAD_MS, RECURSIVE_MS_DRIFT_CORRECTION_MS

class MergerMusicWizardPlaybackMixin:
    def _on_video_vol_changed(self, val):
        if self._video_player: self._video_player.audio_set_volume(val)

    def _on_music_vol_changed(self, val):
        if self._player: self._player.audio_set_volume(val)
        self.logger.info(f"WIZARD: Music Volume changed to {val}%")

    def toggle_video_preview(self):
        try:
            if self.stack.currentIndex() == 1:
                st = self._player.get_state()
                if st == 3:
                    self.logger.info("WIZARD: User clicked PAUSE.")
                    self._player.pause()
                    self.btn_play_video.setText("  PLAY")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                    if hasattr(self, '_play_timer'): self._play_timer.stop()
                else:
                    self.logger.info(f"WIZARD: User clicked PLAY at offset {self.offset_slider.value()/1000.0:.1f}s")
                    if st in (0, 5, 6, 7):
                        # Use synced audio if available for perfect Step 2 sync
                        preview_path = getattr(self, "_temp_sync", None) or self.current_track_path
                        m = self.vlc.media_new(preview_path)
                        self._player.set_media(m)
                    self._player.play()

                    def _after_start():
                        self._player.set_time(int(self.offset_slider.value()))
                        if not hasattr(self, '_play_timer'):
                            self._play_timer = QTimer(self); self._play_timer.setInterval(50); self._play_timer.timeout.connect(self._on_play_tick)
                        self._play_timer.start()
                        self.btn_play_video.setText("  PAUSE")
                        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    QTimer.singleShot(90, _after_start)
            elif self.stack.currentIndex() == 2:
                st = self._video_player.get_state()
                if st == 3:
                    self.logger.info("WIZARD: User clicked PAUSE Project.")
                    self._video_player.pause()
                    if self._player: self._player.pause()
                    self.btn_play_video.setText("  PLAY")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                else:
                    self.logger.info("WIZARD: User clicked PLAY Project.")
                    if st in (0, 5, 6, 7):
                        self._sync_all_players_to_time(self.timeline.current_time)
                    self._video_player.play()
                    if self._player: self._player.play()
                    self.btn_play_video.setText("  PAUSE")
                    self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                    if not hasattr(self, '_play_timer') or not self._play_timer.isActive():
                        self._play_timer = QTimer(self); self._play_timer.setInterval(50); self._play_timer.timeout.connect(self._on_play_tick); self._play_timer.start()
        except Exception as e:
            self.logger.error(f"WIZARD: Playback toggle failed: {e}")

    def _on_play_tick(self):
        if self._is_syncing: return
        self._is_syncing = True
        try:
            now = time.time()
            do_heavy = (now - self._last_tick_ts > 0.1)
            if do_heavy: self._last_tick_ts = now
            if self.stack.currentIndex() == 1 and self._player:
                try:
                    st = self._player.get_state()
                    if st == 3:
                        # Sync duration from VLC to avoid probed vs actual drift
                        vlc_len = self._player.get_length()
                        if vlc_len > 0 and abs(vlc_len - self.offset_slider.maximum()) > 50:
                            self.offset_slider.blockSignals(True)
                            self.offset_slider.setRange(0, vlc_len)
                            self.current_track_dur = vlc_len / 1000.0
                            self.offset_slider.blockSignals(False)

                        vlc_ms = int(self._player.get_time() or 0)
                        if vlc_ms <= 0: vlc_ms = self._last_good_vlc_ms
                        else: self._last_good_vlc_ms = vlc_ms
                        
                        # Apply recursive correction: 0ms for first 10s, then Xms every 10s
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
            if self.stack.currentIndex() == 2 and self._video_player:
                try:
                    if now - self._last_seek_ts < 0.5:
                        self._last_clock_ts = now; return
                    st = self._video_player.get_state()
                    if st in (1, 2, 3):
                        v_time = self._video_player.get_time() / 1000.0
                        if v_time < 0: v_time = 0.0
                        clock_delta = now - self._last_clock_ts; self._last_clock_ts = now
                        if do_heavy:
                            curr_media = self._video_player.get_media()
                            if curr_media:
                                curr_mrl = str(curr_media.get_mrl()).lower().replace("%20", " ").replace("file:///", "").replace("/", "\\")
                                temp_elapsed = 0.0; matched_idx = -1
                                for i, seg in enumerate(self.video_segments):
                                    seg_path_norm = seg["path"].lower().replace("/", "\\")
                                    if seg_path_norm in curr_mrl or curr_mrl in seg_path_norm or os.path.basename(seg_path_norm).lower() in curr_mrl:
                                        matched_idx = i; break
                                    temp_elapsed += seg["duration"]
                                if matched_idx != -1:
                                    self._current_elapsed_offset = temp_elapsed
                                    if st == 3 and v_time >= self.video_segments[matched_idx]["duration"] - 0.2:
                                        if matched_idx < len(self.video_segments) - 1:
                                            next_path = self.video_segments[matched_idx + 1]["path"]
                                            m = self.vlc.media_new(next_path); self._video_player.set_media(m); self._video_player.play()
                                        else:
                                            self.toggle_video_preview(); self.timeline.set_current_time(self.total_video_sec); return
                                    if self._player: self._sync_music_only_to_time(self._current_elapsed_offset + v_time)
                        if st == 3: project_time = self._current_elapsed_offset + v_time
                        else: project_time = self.timeline.current_time + clock_delta
                        project_time = min(self.total_video_sec, max(0.0, project_time))
                        self.timeline.set_current_time(project_time); self._sync_caret()
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
        if self._player: self._player.stop()
