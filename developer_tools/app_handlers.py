import os
import json
import logging
import tempfile
from PyQt5.QtWidgets import QApplication, QFileDialog, QMessageBox
from PyQt5.QtGui import QPixmap
from PyQt5.QtCore import QTimer
from portrait_window import PortraitWindow
from utils import cleanup_temp_snapshots
from config import CROP_APP_STYLESHEET, HUD_ELEMENT_MAPPINGS
from enhanced_logger import get_enhanced_logger

class CropAppHandlers:
    def _get_enhanced_logger(self):
        """Get enhanced logger instance."""
        if hasattr(self, 'enhanced_logger') and self.enhanced_logger:
            return self.enhanced_logger
        return get_enhanced_logger(self.logger)
    
    def connect_signals(self):
        self.play_pause_button.clicked.connect(self.play_pause)
        self.open_button.clicked.connect(self.open_file)
        self.snapshot_button.clicked.connect(self.take_snapshot)
        self.reset_state_button.clicked.connect(self.reset_state)
        self.position_slider.sliderPressed.connect(self._on_slider_pressed)
        self.position_slider.sliderMoved.connect(self._on_slider_moved)
        self.position_slider.sliderReleased.connect(self._on_slider_released)
        self.position_slider.valueChanged.connect(self.update_time_labels)
        self.is_scrubbing = False
        self._slider_last_value = -1
        if hasattr(self, 'draw_widget'):
            self.draw_widget.crop_role_selected.connect(self.handle_crop_completed)

    def set_style(self):
        self.setStyleSheet(CROP_APP_STYLESHEET)

    def update_wizard_step(self, step_num, instruction):
        self.current_step = step_num
        if hasattr(self, 'progress_bar'):
            clamped_step = max(1, min(step_num, 5))
            self.progress_bar.setValue(clamped_step * 20)
        self.update_progress_tracker()
        
    def update_progress_tracker(self):
        if not hasattr(self, 'progress_labels') or not hasattr(self, 'hud_elements'):
            return
        configured_display_names = self._get_configured_roles()
        for element in self.hud_elements:
            if element not in self.progress_labels:
                continue
            label = self.progress_labels[element]
            is_configured = element in configured_display_names
            label.setProperty("class", "")
            label.style().unpolish(label)
            label.style().polish(label)
            if is_configured:
                label.setProperty("class", "completed")
            elif element == self.get_next_element_to_configure():
                label.setProperty("class", "current")
            label.style().unpolish(label)
            label.style().polish(label)

    def get_next_element_to_configure(self):
        configured_display_names = self._get_configured_roles()
        for element in self.hud_elements:
            if element not in configured_display_names:
                return element
        return None

    def _get_remaining_roles(self):
        configured_display_names = self._get_configured_roles()
        return [role for role in self.hud_elements if role not in configured_display_names]

    def _get_configured_roles(self):
        config_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
        configured_tech_keys = set()
        try:
            if os.path.exists(config_path):
                with open(config_path, 'r') as f:
                    data = json.load(f)
                if "crops_1080p" in data:
                    configured_tech_keys = set(data["crops_1080p"].keys())
        except (IOError, json.JSONDecodeError) as e:
            self.logger.warning(f"Could not read or parse crop config: {e}")
            pass
        return {display_name for tech_key, display_name in HUD_ELEMENT_MAPPINGS.items() if tech_key in configured_tech_keys}

    def handle_crop_completed(self, pix, rect, role):
        if not pix or not rect: return
        tech_key_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
        tech_key = tech_key_map.get(role, "unknown")
        if tech_key == "unknown": return
        self.quick_save_crop(rect, tech_key)
        self.launch_portrait_editor(pix, rect, role)
        self.draw_widget.clear_selection()
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        remaining = self._get_remaining_roles()
        if remaining:
            next_element = self.get_next_element_to_configure()
            if next_element:
                self.update_wizard_step(3, f"✓ '{role}' saved. Next: {next_element}")
            else:
                self.update_wizard_step(3, f"✓ '{role}' saved. Draw another element.")
        else:
            self.update_wizard_step(4, "All HUD elements configured!")
            self.show_completion_state()

    def show_completion_state(self):
        if hasattr(self, 'complete_button'):
            self.complete_button.setVisible(True)
        
    def finish_and_save(self):
        self.update_wizard_step(5, "Configuration complete")
        QMessageBox.information(self, "Configuration Complete", 
            "All HUD elements have been configured and saved!")
        if self.portrait_window:
            self.portrait_window.close()
            self.portrait_window = None
        self.reset_state()

    def quick_save_crop(self, rect, role):
        self.logger.info(f"Saving coordinates for {role}...")
        source_h = 1080
        if self.media_processor.original_resolution:
            try:
                parts = self.media_processor.original_resolution.lower().split('x')
                if len(parts) >= 2: source_h = int(parts[1])
            except: pass
        norm_factor = 1080.0 / float(source_h)
        data = {"crops_1080p": {}, "scales": {}, "overlays": {}}
        config_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
        try:
            if os.path.exists(config_path):
                with open(config_path, 'r') as f: data = json.load(f)
        except: pass
        if "crops_1080p" not in data: data["crops_1080p"] = {}
        enhanced_logger = self._get_enhanced_logger()
        if enhanced_logger:
            enhanced_logger.log_config_changed(config_path, role, "before")
        data["crops_1080p"][role] = [
            int(round(rect.x() * norm_factor)), int(round(rect.y() * norm_factor)),
            int(round(rect.width() * norm_factor)), int(round(rect.height() * norm_factor))
        ]
        with open(config_path, 'w') as f: json.dump(data, f, indent=4)
        if enhanced_logger:
            enhanced_logger.log_config_changed(config_path, role, "after")

    def launch_portrait_editor(self, pix, rect, role):
        if self.background_crop_width <= 0: self.background_crop_width = 1920
        try:
            if self.portrait_window is None:
                self.portrait_window = PortraitWindow(self.media_processor.original_resolution, self.config_path)
                self.portrait_window.destroyed.connect(lambda: setattr(self, 'portrait_window', None))
            if hasattr(self.draw_widget, 'pixmap') and not self.draw_widget.pixmap.isNull():
                self.portrait_window.set_background_image(self.draw_widget.pixmap)
            if not self.portrait_window.isVisible():
                self.portrait_window.show()
                self.portrait_window.raise_()
                self.portrait_window.activateWindow()
                self.portrait_window.setFocus()
            self.portrait_window.add_scissored_item(pix, rect, self.background_crop_width, role)
            items = self.portrait_window.scene.selectedItems()
            if items: items[0].set_role(role)
            self.update_wizard_step(4, f"Adjust '{role}' position and size in the portrait editor")
        except Exception as e:
            self.logger.error(f"CRASH PREVENTED: {e}", exc_info=True)
            QMessageBox.critical(self, "Error", f"Failed to open editor: {e}")

    def reset_state(self):
        if self.portrait_window:
            self.portrait_window.close()
            self.portrait_window = None
        self.media_processor.stop()
        self.media_processor.set_media_to_null()
        self.play_pause_button.setEnabled(False)
        self.snapshot_button.setEnabled(False)
        self.draw_widget.clear_selection()
        self.draw_widget.setImage(None) 
        self.view_stack.setCurrentWidget(self.video_frame)
        self.update_wizard_step(1, "Open a video file to begin the configuration wizard.")
        if hasattr(self, 'complete_button'):
            self.complete_button.setVisible(False)
        self.update_progress_tracker()

    def play_pause(self):
        self.media_processor.play_pause()
        QTimer.singleShot(50, lambda: self.play_pause_button.setText("⏸ PAUSE" if self.media_processor.is_playing() else "▶ PLAY"))

    def open_file(self):
        file_path, _ = QFileDialog.getOpenFileName(self, "Open Video", self.last_dir or "", "Video Files (*.mp4 *.avi *.mkv)")
        if file_path: self.load_file(file_path)

    def load_file(self, file_path):
        self.last_dir = os.path.dirname(file_path)
        self.media_processor.load_media(file_path, self.video_frame.winId())
        enhanced_logger = self._get_enhanced_logger()
        if enhanced_logger:
            resolution = self.media_processor.original_resolution or "unknown"
            enhanced_logger.log_video_loaded(file_path, resolution)
        self.play_pause_button.setEnabled(True)
        self.play_pause_button.setText("⏸ PAUSE")
        self.snapshot_button.setEnabled(False)
        self.snapshot_button.setText("Loading...")
        self.position_slider.setEnabled(True)
        self.show_video_view()
        self.update_wizard_step(2, "Play/Pause to find a clear frame with visible HUD elements.")

        def enable_snapshot_if_ready():
            total_ms = self.media_processor.media_player.get_length()
            if total_ms > 0:
                self.snapshot_button.setEnabled(True)
                self.snapshot_button.setText("START CROPPING")
                self.logger.info(f"Video loaded, length: {total_ms} ms, snapshot enabled")
            else:
                self.logger.info(f"Video not ready yet, length: {total_ms} ms, retrying...")
                QTimer.singleShot(500, enable_snapshot_if_ready)
        QTimer.singleShot(1000, enable_snapshot_if_ready)
        self.update_progress_tracker()

    def take_snapshot(self):
        if hasattr(self, '_snapshot_processing') and self._snapshot_processing:
            self.logger.info("Snapshot already in progress, ignoring click")
            return
        self._snapshot_processing = True
        self.snapshot_button.setEnabled(False)
        self.logger.info("Starting snapshot process")
        try:
            was_playing = self.media_processor.media_player.is_playing()
            self.logger.info(f"Video was playing: {was_playing}")
            if was_playing:
                self.media_processor.play_pause()
                self.play_pause_button.setText("▶ PLAY")
                self.logger.info("Paused video for snapshot")
            slider_value = self.position_slider.value()
            slider_fraction = slider_value / 1000.0
            self.logger.info(f"Slider value: {slider_value}, fraction: {slider_fraction}")
            total_ms = self.media_processor.media_player.get_length()
            self.logger.info(f"Video length: {total_ms} ms")
            if total_ms > 0:
                preferred_time = slider_fraction * (total_ms / 1000.0)
            else:
                current_time = self.media_processor.media_player.get_time()
                preferred_time = current_time / 1000.0
                if preferred_time < 0:
                    preferred_time = 0.1
                self.logger.info(f"Using current time: {current_time} ms, preferred_time: {preferred_time} s")
            preferred_time = max(0.0, preferred_time)
            self.logger.info(f"Final preferred_time: {preferred_time} seconds")

            import time
            timestamp = int(time.time() * 1000)
            unique_snapshot_path = os.path.join(tempfile.gettempdir(), f"snapshot_{timestamp}.png")
            self.logger.info(f"Creating snapshot at: {unique_snapshot_path}")
            success, message = self.media_processor.take_snapshot(unique_snapshot_path, preferred_time)
            self.logger.info(f"Snapshot result: success={success}, message={message}")
            if success: 
                self.snapshot_path = unique_snapshot_path

                def check_and_show():
                    if os.path.exists(self.snapshot_path) and os.path.getsize(self.snapshot_path) > 0:
                        file_size = os.path.getsize(self.snapshot_path)
                        self.logger.info(f"Snapshot file exists, size: {file_size} bytes")
                        enhanced_logger = self._get_enhanced_logger()
                        if enhanced_logger:
                            enhanced_logger.log_video_loaded(self.snapshot_path, "snapshot")
                        QApplication.processEvents()
                        QTimer.singleShot(200, self._show_draw_view)
                    else:
                        self.logger.warning(f"Snapshot file not ready, retrying...")
                        QTimer.singleShot(100, check_and_show)
                QTimer.singleShot(50, check_and_show)
            else: 
                self.logger.error(f"Snapshot failed: {message}")
                QMessageBox.warning(self, "Snapshot Error", message)
        except Exception as e:
            self.logger.error(f"Error taking snapshot: {e}", exc_info=True)
            QMessageBox.warning(self, "Snapshot Error", f"Unexpected error: {e}")
        finally:
            self._snapshot_processing = False
            self.logger.info("Snapshot process completed, re-enabling button")
            QTimer.singleShot(500, lambda: self.snapshot_button.setEnabled(True))

    def _show_draw_view(self):
        self.logger.info(f"Showing draw view for snapshot: {self.snapshot_path}")
        if not os.path.exists(self.snapshot_path):
            self.logger.error(f"Snapshot file does not exist: {self.snapshot_path}")
            QMessageBox.warning(self, "Snapshot Error", "Snapshot file was not created. Please try again.")
            return
        file_size = os.path.getsize(self.snapshot_path)
        self.logger.info(f"Snapshot file size: {file_size} bytes")
        if file_size == 0:
            self.logger.error(f"Snapshot file is empty: {self.snapshot_path}")
            QMessageBox.warning(self, "Snapshot Error", "Snapshot file is empty. Please try again.")
            return
        snapshot_pixmap = QPixmap(self.snapshot_path)
        if snapshot_pixmap.isNull():
            self.logger.error(f"Failed to load snapshot from: {self.snapshot_path}")
            QMessageBox.warning(self, "Snapshot Error", "Failed to load snapshot image. The file may be corrupted.")
            return
        self.logger.info(f"Snapshot loaded successfully: {snapshot_pixmap.width()}x{snapshot_pixmap.height()}")
        try:
            if self.portrait_window is None:
                self.portrait_window = PortraitWindow(self.media_processor.original_resolution, self.config_path)
                self.logger.info("Created portrait window")
        except Exception as e:
            self.logger.error(f"Failed to create portrait window: {e}")
        target_aspect = 1080 / 1920
        img_aspect = snapshot_pixmap.width() / snapshot_pixmap.height()
        w = int(snapshot_pixmap.height() * target_aspect) if img_aspect > target_aspect else snapshot_pixmap.width()
        self.background_crop_width = w
        self.logger.info(f"Background crop width: {w}, image aspect: {img_aspect}")
        self.draw_widget.setImage(self.snapshot_path)
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        next_element = self.get_next_element_to_configure()
        if next_element:
            self.update_wizard_step(3, f"Draw a box around the {next_element}")
        else:
            self.update_wizard_step(3, "All elements configured! You can still add more if needed.")
        self.update_progress_tracker()
        if hasattr(self, 'draw_scroll_area'):
            self.view_stack.setCurrentWidget(self.draw_scroll_area)
            self.logger.info("Switched to draw_scroll_area view")
        else:
            self.view_stack.setCurrentWidget(self.draw_widget)
            self.logger.info("Switched to draw_widget view")
        QApplication.processEvents()
        self.logger.info("Draw view displayed successfully")

    def show_video_view(self):
        self.view_stack.setCurrentWidget(self.video_frame)
        self.update_wizard_step(2, "Video View. Find a frame and take a snapshot.")

    def set_position(self, position):
        self.logger.info(f"Seeking to position: {position} (normalized: {position / 1000.0})")
        self.media_processor.set_position(position / 1000.0)
        self.update_time_labels()

    def _on_slider_pressed(self):
        self.is_scrubbing = True
        self.logger.info(f"Slider pressed at value: {self.position_slider.value()}")

    def _on_slider_moved(self, position):
        self.update_time_labels()
        
    def _on_slider_released(self):
        self.is_scrubbing = False
        self.logger.info(f"Slider released, seeking to: {self.position_slider.value()}")
        self.set_position(self.position_slider.value())

    def update_time_labels(self):
        if not hasattr(self, 'current_time_label') or not hasattr(self, 'total_time_label'):
            return
        if not self.media_processor.media:
            self.current_time_label.setText("00:00")
            self.total_time_label.setText("00:00")
            return
        total_ms = self.media_processor.media_player.get_length()
        current_ms = self.media_processor.media_player.get_time()
        if self.is_scrubbing and total_ms > 0:
            slider_time = int((self.position_slider.value() / 1000.0) * total_ms)
            self.current_time_label.setText(self._format_time(slider_time))
            self.total_time_label.setText(self._format_time(total_ms))
        else:
            if total_ms <= 0:
                self.total_time_label.setText("00:00")
            else:
                self.total_time_label.setText(self._format_time(total_ms))
            if current_ms < 0:
                current_ms = 0
            self.current_time_label.setText(self._format_time(current_ms))

    def update_ui(self):
        if self.media_processor.media:
            if not getattr(self, 'is_scrubbing', False):
                self.position_slider.setValue(int(self.media_processor.get_position() * 1000))
            self.update_time_labels()
           
    def get_title_info(self):
        return self.base_title

    def _format_time(self, millis):
        total_seconds = int(millis / 1000)
        minutes = total_seconds // 60
        seconds = total_seconds % 60
        return f"{minutes:02d}:{seconds:02d}"
