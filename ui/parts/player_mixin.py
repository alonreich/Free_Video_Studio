import vlc
from PyQt5.QtCore import QTimer
from PyQt5.QtMultimedia import QMediaPlayer
from PyQt5.QtWidgets import QStyle

class PlayerMixin:
    def _safe_stop_playback(self):
        try:
            if getattr(self, "player", None):
                self.player.stop()
                self.player.setPosition(0)
            if getattr(self, "play_button", None):
                self.play_button.setChecked(False)
                self.play_button.setText("Play")
            if getattr(self, "position_slider", None):
                self.position_slider.setValue(0)
        except Exception:
            pass
    
    def _finish_seek_from_end(self):
        """Callback to finish the seek operation, re-enabling the UI."""
        self.vlc_player.pause()
        self.playPauseButton.setEnabled(True)
        self._is_seeking_from_end = False
    
    def toggle_play_pause(self):
        """Toggles play/pause, handling restarts from the end and ignoring clicks during seeks."""
        if getattr(self, '_is_seeking_from_end', False):
            return
        if not getattr(self, "input_file_path", None):
            return
        player_state = self.vlc_player.get_state()
        if player_state == vlc.State.Playing:
            self.vlc_player.pause()
            self.playPauseButton.setText("Play")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            if self.timer.isActive():
                self.timer.stop()
        else:
            pos = self.vlc_player.get_position()
            if player_state == vlc.State.Ended or (player_state == vlc.State.Paused and pos >= 0.999):
                self.vlc_player.stop()
                self.vlc_player.play()
            else:
                self.vlc_player.play()
                self.vlc_player.set_rate(self.playback_rate)

            def _wait_for_playing_state():
                if self.vlc_player.is_playing():
                    try:
                        self.vlc_player.set_rate(self.playback_rate)
                        self.vlc_player.audio_set_mute(False)
                        if hasattr(self, 'apply_master_volume'):
                            self.apply_master_volume()
                    except Exception as e:
                        self.logger.error(f"Error applying rate/unmute: {e}")
                else:
                    if not hasattr(self, '_retry_count'): self._retry_count = 0
                    if self._retry_count < 10:
                        self._retry_count += 1
                        QTimer.singleShot(50, _wait_for_playing_state)
                    else:
                        self._retry_count = 0
            self._retry_count = 0
            QTimer.singleShot(10, _wait_for_playing_state)
            self.playPauseButton.setText("Pause")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
            if not self.timer.isActive():
                self.timer.start(50)
    
    def update_player_state(self):
        """Updates the UI slider position based on VLC playback time."""
        if not self.vlc_player:
            return
        slider = getattr(self, "positionSlider", None)
        if slider and (getattr(slider, "_is_pressed", False) or getattr(self, "_dragging_handle", None)):
            return
        current_time = self.vlc_player.get_time()
        if current_time >= 0:
            slider.blockSignals(True)
            slider.setValue(current_time)
            slider.blockSignals(False)
            player_state = self.vlc_player.get_state()
            is_currently_vlc_playing = (player_state == vlc.State.Playing)
            if is_currently_vlc_playing != self.is_playing:
                self.is_playing = is_currently_vlc_playing
                icon = QStyle.SP_MediaPause if self.is_playing else QStyle.SP_MediaPlay
                label = "Pause" if self.is_playing else "Play"
                self.playPauseButton.setText(label)
                self.playPauseButton.setIcon(self.style().standardIcon(icon))
    
    def set_vlc_position(self, position):
        """Sets the player position safely, ensuring no timer-fight occurs."""
        try:
            target_ms = int(position)
            state = self.vlc_player.get_state()
            if state in (vlc.State.Stopped, vlc.State.Ended):
                self._is_seeking_from_end = True
                self.playPauseButton.setEnabled(False)
                self.vlc_player.play()
                QTimer.singleShot(50, lambda: self.vlc_player.set_time(target_ms))
                QTimer.singleShot(250, self._finish_seek_from_end)
            else:
                self.vlc_player.set_time(target_ms)
        except Exception as e:
            self.logger.error(f"CRITICAL: Seek failed in set_vlc_position: {e}")
            if hasattr(self, "show_message"):
                self.show_message("Player Error", f"Could not seek video: {e}")
    
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