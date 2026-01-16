import vlc
from PyQt5.QtCore import QTimer
from PyQt5.QtMultimedia import QMediaPlayer
from PyQt5.QtWidgets import QStyle

class PlayerMixin:
    def _safe_stop_playback(self):
        try:
            if getattr(self, "vlc_player", None):
                self.vlc_player.stop()
            if getattr(self, "vlc_music_player", None):
                self.vlc_music_player.stop()
            if getattr(self, "playPauseButton", None):
                self.playPauseButton.setText("Play")
            if getattr(self, "positionSlider", None):
                self.positionSlider.setValue(0)
        except Exception:
            pass
    
    def _finish_seek_from_end(self):
        """Callback to finish the seek operation, re-enabling the UI."""
        self.vlc_player.pause()
        self.playPauseButton.setEnabled(True)
        self._is_seeking_from_end = False
    
    def toggle_play_pause(self):
        """Toggles play/pause for video and triggers music sync."""
        if getattr(self, '_is_seeking_from_end', False) or not getattr(self, "input_file_path", None):
            return
        if self.vlc_player.is_playing():
            if self.timer.isActive():
                self.timer.stop()
            self.vlc_player.pause()
            self.set_vlc_position(self.vlc_player.get_time(), sync_only=True, force_pause=True)
            self.playPauseButton.setText("Play")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        else:
            pos = self.vlc_player.get_position()
            state = self.vlc_player.get_state()
            if state == vlc.State.Ended or (state == vlc.State.Paused and pos >= 0.999):
                self.vlc_player.stop()
                if getattr(self, 'vlc_music_player', None):
                    getattr(self, 'vlc_music_player', None).stop()
                self.set_vlc_position(self.trim_start * 1000 if self.trim_start is not None else 0)
            self.vlc_player.play()
            self.vlc_player.set_rate(self.playback_rate)
            if not self.timer.isActive():
                self.timer.start(50)
            self.playPauseButton.setText("Pause")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def update_player_state(self):
        """On a timer, updates UI slider and keeps music in sync."""
        if not getattr(self, "vlc_player", None):
            return

        import time
        if (time.time() - getattr(self, "_last_seek_request_time", 0)) < 0.8:
            return
        slider = getattr(self, "positionSlider", None)
        if slider and (getattr(slider, "_is_pressed", False) or 
                       getattr(self, "_dragging_handle", None) or 
                       slider.isSliderDown()):
            return
        current_time = self.vlc_player.get_time()
        if current_time >= 0:
            slider.blockSignals(True)
            slider.setValue(current_time)
            slider.blockSignals(False)
            if getattr(self, 'vlc_music_player', None) and self.add_music_checkbox.isChecked():
                self.set_vlc_position(current_time, sync_only=True)
            is_currently_vlc_playing = self.vlc_player.get_state() == vlc.State.Playing
            if is_currently_vlc_playing != getattr(self, "is_playing", None):
                self.is_playing = is_currently_vlc_playing
                icon = QStyle.SP_MediaPause if self.is_playing else QStyle.SP_MediaPlay
                label = "Pause" if self.is_playing else "Play"
                if getattr(self, "playPauseButton", None):
                    self.playPauseButton.setText(label)
                    self.playPauseButton.setIcon(self.style().standardIcon(icon))

    def set_vlc_position(self, position, sync_only=False, force_pause=False):
        """Sets video player position AND syncs the music player state."""
        try:
            target_ms = int(position)
            music_player = getattr(self, 'vlc_music_player', None)
            if music_player and self.add_music_checkbox.isChecked():
                if self.music_timeline_start_sec is not None and self.music_timeline_end_sec is not None:
                    is_video_playing = not force_pause and (self.vlc_player.get_state() == vlc.State.Playing)
                    music_start_sec = self.music_timeline_start_sec
                    music_end_sec = self.music_timeline_end_sec
                    is_within_music_bounds = (music_start_sec <= (target_ms / 1000.0) < music_end_sec)
                    if is_within_music_bounds:
                        time_into_music_clip_sec = (target_ms / 1000.0) - music_start_sec
                        file_offset_sec = self._get_music_offset()
                        music_target_in_file_ms = int((time_into_music_clip_sec + file_offset_sec) * 1000)
                        if abs(music_player.get_time() - music_target_in_file_ms) > 400:
                            music_player.set_time(music_target_in_file_ms)
                    if is_video_playing and is_within_music_bounds:
                        if not music_player.is_playing(): music_player.play()
                    else:
                        if music_player.is_playing(): music_player.pause()
                else:
                    if music_player.is_playing(): music_player.pause()
            if sync_only:
                return

            import time
            self._last_seek_request_time = time.time()
            self.vlc_player.set_time(target_ms)
        except Exception as e:
            if hasattr(self, "logger"):
                self.logger.error(f"CRITICAL: Seek failed in set_vlc_position: {e}")
    
    def _on_vlc_end_reached(self, event=None):
        """
        VLC reached end.
        Ensures thread-safe handling by using QTimer to push the execution 
        to the main Qt event loop, preventing cross-thread deadlocks.
        """
        try:
            QTimer.singleShot(0, lambda: self.video_ended_signal.emit())
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"VLC End Event failed to defer: {e}")