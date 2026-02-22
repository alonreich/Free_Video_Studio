import os
import sys
import subprocess
from PyQt5.QtCore import Qt, QEvent
from PyQt5.QtWidgets import QWidget, QApplication

class KeyboardMixin(QWidget):
    """Handles global keyboard shortcuts for the main window."""

    def _launch_dev_tool(self):
        """Launches the crop tool using the centralized method."""
        if hasattr(self, 'launch_crop_tool'):
            self.launch_crop_tool()
        else:
            self.logger.error("launch_crop_tool method not found in main window instance.")

    def eventFilter(self, obj, event):
        if event.type() == QEvent.KeyPress:
            if QApplication.activeModalWidget() is not None:
                return super().eventFilter(obj, event)
            key = event.key()
            if key == Qt.Key_F12:
                self._launch_dev_tool()
                return True
            if key in (Qt.Key_Up, Qt.Key_Down, Qt.Key_Plus, Qt.Key_Equal, Qt.Key_Minus):
                if self._handle_volume_keys(key, event.modifiers()):
                    return True
            if key in (Qt.Key_Return, Qt.Key_Enter):
                self._on_process_clicked()
                return True
            if getattr(self, "input_file_path", None):
                if key == Qt.Key_Space:
                    if hasattr(self, "portrait_text_input") and self.portrait_text_input.isVisible() and self.portrait_text_input.hasFocus():
                        return False
                    self.toggle_play_pause()
                    return True
                if key == Qt.Key_BracketLeft:
                    self.set_start_time()
                    return True
                if key == Qt.Key_BracketRight:
                    self.set_end_time()
                    return True
                if event.modifiers() == Qt.ShiftModifier:
                    if key == Qt.Key_Right:
                        self.seek_relative_time(3000)
                        return True
                    if key == Qt.Key_Left:
                        self.seek_relative_time(-3000)
                        return True
        return super().eventFilter(obj, event)

    def _handle_volume_keys(self, key, modifiers) -> bool:
        try:
            if key in (Qt.Key_Plus, Qt.Key_Equal, Qt.Key_Minus):
                vol_step = 15
                if key == Qt.Key_Minus:
                    vol_step = -15
            else:
                vol_step = 15 if modifiers == Qt.ShiftModifier else 1
                if key == Qt.Key_Down:
                    vol_step = -vol_step
            if self.volume_shortcut_target == 'music':
                slider = self.music_volume_slider
                eff_func = self._music_eff
                callback = self._on_music_volume_changed
                log_name = "Music"
            else:
                slider = self.volume_slider
                eff_func = self._vol_eff
                callback = self._on_master_volume_changed
                log_name = "Main"
            current_eff_vol = eff_func()
            new_eff_vol = max(0, min(100, current_eff_vol + vol_step))
            if slider.invertedAppearance():
                new_raw_val = slider.maximum() + slider.minimum() - new_eff_vol
            else:
                new_raw_val = new_eff_vol
            new_raw_val = max(slider.minimum(), min(slider.maximum(), new_raw_val))
            slider.setValue(new_raw_val)
            callback(new_raw_val)
            if hasattr(self, "logger"):
                self.logger.debug(f"KEYBOARD: {log_name} volume set to {new_eff_vol}%")
            return True
        except Exception as e:
            if hasattr(self, "logger"):
                self.logger.error(f"Failed to handle volume key: {e}")
            return False

    def seek_relative_time(self, ms_offset: int):
        """Moves the player's position by a given millisecond offset."""
        if not getattr(self, "player", None):
            return
        current_time = (getattr(self.player, "time-pos", 0) or 0) * 1000
        new_time = max(0, current_time + ms_offset)
        max_time = (getattr(self.player, "duration", 0) or 0) * 1000
        if max_time > 0:
            new_time = min(new_time, max_time)
        self.set_player_position(new_time)
        if getattr(self.player, "pause", True):
            self.positionSlider.setValue(int(new_time))
        if hasattr(self, "logger"):
            self.logger.debug("KEYBOARD: Seeked %dms to %dms", ms_offset, new_time)