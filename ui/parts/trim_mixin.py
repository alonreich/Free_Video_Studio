from PyQt5.QtWidgets import QStyle

class TrimMixin:

    def _update_trim_inputs(self):
        """Update spinbox limits based on the original duration."""
        if not hasattr(self, "start_minute_input"): return
        total_seconds = int(self.original_duration)
        max_m = total_seconds // 60
        max_s = total_seconds % 60
        self.start_minute_input.setRange(0, max_m)
        self.start_second_input.setRange(0, 59)
        self.end_minute_input.setRange(0, max_m)
        self.end_second_input.setRange(0, 59)
        self.start_minute_input.setValue(0)
        self.start_second_input.setValue(0)
        self.end_minute_input.setValue(max_m)
        self.end_second_input.setValue(max_s)
        self.trim_start = 0.0
        self.trim_end = self.original_duration
        self.positionSlider.set_trim_times(self.trim_start, self.trim_end)
    
    def set_start_time(self):
        pos_ms = self.positionSlider.value()
        pos_s = pos_ms / 1000.0
        if self.trim_end is not None and pos_s > self.trim_end:
            self.trim_end = self.original_duration
        if pos_s < 0:
            pos_s = 0
        self.trim_start = pos_s
        if hasattr(self, "logger"):
            self.logger.info("TRIM: start set at %.3fs from slider", self.trim_start)
        self._update_trim_widgets_from_trim_times()
        self.positionSlider.set_trim_times(self.trim_start, self.trim_end)
    
    def set_end_time(self):
        pos_ms = self.positionSlider.value()
        pos_s = pos_ms / 1000.0
        if self.trim_start is not None and pos_s < self.trim_start:
            pos_s = self.trim_start
        if self.original_duration > 0 and pos_s > self.original_duration:
            pos_s = self.original_duration
        self.trim_end = pos_s
        if hasattr(self, "logger"):
            self.logger.info("TRIM: end set at %.3fs from slider", self.trim_end)
        self._update_trim_widgets_from_trim_times()
        self.positionSlider.set_trim_times(self.trim_start, self.trim_end)
        if self.vlc_player.is_playing():
            self.vlc_player.pause()
            self.playPauseButton.setText("Play")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
    
    def _update_trim_widgets_from_trim_times(self):
        self.start_minute_input.blockSignals(True)
        self.start_second_input.blockSignals(True)
        self.end_minute_input.blockSignals(True)
        self.end_second_input.blockSignals(True)
        try:
            if self.trim_start is not None:
                sm = int(self.trim_start // 60)
                ss = self.trim_start % 60
                self.start_minute_input.setValue(sm)
                self.start_second_input.setValue(ss)
            if self.trim_end is not None:
                em = int(self.trim_end // 60)
                es = self.trim_end % 60
                self.end_minute_input.setValue(em)
                self.end_second_input.setValue(es)
        finally:
            self.start_minute_input.blockSignals(False)
            self.start_second_input.blockSignals(False)
            self.end_minute_input.blockSignals(False)
            self.end_second_input.blockSignals(False)
