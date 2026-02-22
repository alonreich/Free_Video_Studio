from PyQt5.QtWidgets import QStyle

class TrimMixin:
    MIN_TRIM_GAP = 1000

    def _update_trim_inputs(self):
        """Update spinbox limits based on the original duration in milliseconds."""
        if not hasattr(self, "start_minute_input"): return
        total_ms = self.original_duration_ms
        total_seconds = total_ms // 1000
        max_m = total_seconds // 60
        max_s = total_seconds % 60
        max_ms = total_ms % 1000
        self.start_minute_input.setRange(0, max_m)
        self.start_second_input.setRange(0, 59)
        self.start_ms_input.setRange(0, 999)
        self.end_minute_input.setRange(0, max_m)
        self.end_second_input.setRange(0, 59)
        self.end_ms_input.setRange(0, 999)
        self.start_minute_input.setValue(0)
        self.start_second_input.setValue(0)
        self.start_ms_input.setValue(0)
        self.end_minute_input.setValue(max_m)
        self.end_second_input.setValue(max_s)
        self.end_ms_input.setValue(max_ms)
        self.trim_start_ms = 0
        self.trim_end_ms = total_ms
        self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
    
    def set_start_time(self):
        pos_ms = int(self.positionSlider.value())
        if self.trim_end_ms > 0:
            if pos_ms > self.trim_end_ms - self.MIN_TRIM_GAP:
                self.trim_end_ms = min(self.original_duration_ms, pos_ms + self.MIN_TRIM_GAP)
                if self.trim_end_ms - pos_ms < self.MIN_TRIM_GAP:
                    pos_ms = max(0, self.trim_end_ms - self.MIN_TRIM_GAP)
        self.trim_start_ms = max(0, pos_ms)
        if hasattr(self, "logger"):
            self.logger.info("TRIM: start set at %d ms from slider", self.trim_start_ms)
        self._update_trim_widgets_from_trim_times()
        self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
    
    def set_end_time(self):
        pos_ms = int(self.positionSlider.value())
        if pos_ms < self.trim_start_ms + self.MIN_TRIM_GAP:
            self.trim_start_ms = max(0, pos_ms - self.MIN_TRIM_GAP)
            if pos_ms - self.trim_start_ms < self.MIN_TRIM_GAP:
                pos_ms = min(self.original_duration_ms, self.trim_start_ms + self.MIN_TRIM_GAP)
        if self.original_duration_ms > 0 and pos_ms > self.original_duration_ms:
            pos_ms = self.original_duration_ms
        self.trim_end_ms = pos_ms
        if hasattr(self, "logger"):
            self.logger.info("TRIM: end set at %d ms from slider", self.trim_end_ms)
        self._update_trim_widgets_from_trim_times()
        self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
        is_playing = self.player and not getattr(self.player, "pause", True)
        if is_playing:
            self.player.pause = True
            self.playPauseButton.setText("Play")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
    
    def _update_trim_widgets_from_trim_times(self):
        """Updates the time spinboxes from the internal millisecond state."""
        for spinbox in (self.start_minute_input, self.start_second_input, self.start_ms_input, 
                        self.end_minute_input, self.end_second_input, self.end_ms_input):
            spinbox.blockSignals(True)
        try:
            start_total_s = self.trim_start_ms // 1000
            start_m = start_total_s // 60
            start_s = start_total_s % 60
            start_ms = self.trim_start_ms % 1000
            self.start_minute_input.setValue(start_m)
            self.start_second_input.setValue(start_s)
            self.start_ms_input.setValue(start_ms)
            end_total_s = self.trim_end_ms // 1000
            end_m = end_total_s // 60
            end_s = end_total_s % 60
            end_ms = self.trim_end_ms % 1000
            self.end_minute_input.setValue(end_m)
            self.end_second_input.setValue(end_s)
            self.end_ms_input.setValue(end_ms)
        finally:
            for spinbox in (self.start_minute_input, self.start_second_input, self.start_ms_input, 
                            self.end_minute_input, self.end_second_input, self.end_ms_input):
                spinbox.blockSignals(False)

    def _on_trim_spin_changed(self):
        """Recalculates the internal millisecond state from all time spinboxes."""
        start_ms = (self.start_minute_input.value() * 60 * 1000) + \
                   (self.start_second_input.value() * 1000) + \
                   self.start_ms_input.value()
        end_ms = (self.end_minute_input.value() * 60 * 1000) + \
                 (self.end_second_input.value() * 1000) + \
                 self.end_ms_input.value()
        if self.original_duration_ms > 0:
            start_ms = max(0, min(start_ms, self.original_duration_ms))
            end_ms = max(0, min(end_ms, self.original_duration_ms))
            if end_ms < start_ms + self.MIN_TRIM_GAP:
                end_ms = min(self.original_duration_ms, start_ms + self.MIN_TRIM_GAP)
                if end_ms < start_ms + self.MIN_TRIM_GAP:
                    start_ms = max(0, end_ms - self.MIN_TRIM_GAP)
        self.trim_start_ms = int(start_ms)
        self.trim_end_ms = int(end_ms)
        self.positionSlider.set_trim_times(self.trim_start_ms, self.trim_end_ms)
