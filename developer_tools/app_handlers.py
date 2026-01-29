import os
import json
import logging
import tempfile
from PyQt5.QtWidgets import QApplication, QFileDialog, QMessageBox
from PyQt5.QtGui import QPixmap
from PyQt5.QtCore import QTimer, QThread
from portrait_window import PortraitWindow
from utils import cleanup_temp_snapshots
from config import CROP_APP_STYLESHEET, HUD_ELEMENT_MAPPINGS
from enhanced_logger import get_enhanced_logger
from magic_wand import MagicWand, MagicWandWorker

class CropAppHandlers:
    def _get_enhanced_logger(self):
        """Get enhanced logger instance."""
        if hasattr(self, 'enhanced_logger') and self.enhanced_logger:
            return self.enhanced_logger
        return get_enhanced_logger(self.logger)
    
    def connect_signals(self):
        if hasattr(self, 'return_button'):
            self.return_button.clicked.connect(self._deferred_launch_main_app)
        self.play_pause_button.clicked.connect(self.play_pause)
        self.open_button.clicked.connect(self.open_file)
        self.snapshot_button.clicked.connect(self.take_snapshot)
        if hasattr(self, 'magic_wand_button'):
            self.magic_wand_button.clicked.connect(self.on_magic_wand_clicked)
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
        configured_tech_keys = self.config_manager.get_configured_elements()
        return {display_name for tech_key, display_name in HUD_ELEMENT_MAPPINGS.items() if tech_key in configured_tech_keys}

    def handle_crop_completed(self, pix, rect, role):
        if not pix or not rect: return
        tech_key_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
        tech_key = tech_key_map.get(role, "unknown")
        if tech_key == "unknown": return
        self.launch_portrait_editor(pix, rect, role)
        self.draw_widget.clear_selection()
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        self.update_wizard_step(3, f"Adjust '{role}' in Portrait window, then click FINISH to save.")

    def on_magic_wand_clicked(self):
        """Handler for the Magic Wand button."""
        if not self.snapshot_path or not os.path.exists(self.snapshot_path):
            QMessageBox.warning(self, "Magic Wand", "Please take a snapshot first.")
            return
        script_dir = os.path.dirname(os.path.abspath(__file__))
        anchor_dir = os.path.join(script_dir, 'anchors')
        required_files = ['ref_keybind_1.png', 'ref_keybind_5.png', 'ref_minimap_border.png', 'ref_hp_icon.png']
        missing = []
        if not os.path.isdir(anchor_dir):
             missing.append("Directory: /anchors")
        else:
             for f in required_files:
                 if not os.path.exists(os.path.join(anchor_dir, f)):
                     missing.append(f)
        if missing:
             msg = (
                 "Magic Wand cannot function because reference images are missing.\n\n"
                 f"Location: {anchor_dir}\n"
                 "Missing items:\n" + "\n".join([f" - {m}" for m in missing]) + "\n\n"
                 "Please restore these files to use Auto-Detection."
             )
             QMessageBox.critical(self, "Setup Required", msg)
             return
        if hasattr(self, '_magic_wand_candidates') and self._magic_wand_candidates:
            self._magic_wand_index = (self._magic_wand_index + 1) % len(self._magic_wand_candidates)
            self._apply_magic_wand_region(self._magic_wand_candidates[self._magic_wand_index])
            self.update_wizard_step(3, f"Cycle {self._magic_wand_index+1}/{len(self._magic_wand_candidates)}: Adjust region, then confirm.")
            return

        from PyQt5.QtCore import QThread
        from magic_wand import MagicWand, MagicWandWorker
        self.logger.info("Magic Wand activated.")
        if hasattr(self, 'magic_wand_button'):
            self.magic_wand_button.setEnabled(False)
            self.magic_wand_button.setText("Analyzing...")
        self.wand_thread = QThread()
        self.wand_worker = MagicWandWorker(MagicWand(self.logger), self.snapshot_path, {})
        self.wand_worker.moveToThread(self.wand_thread)
        self.wand_thread.started.connect(self.wand_worker.run)
        self.wand_worker.finished.connect(self._on_magic_wand_finished)
        self.wand_worker.finished.connect(self.wand_thread.quit)
        self.wand_worker.finished.connect(self.wand_worker.deleteLater)
        self.wand_thread.finished.connect(self.wand_thread.deleteLater)
        self.wand_thread.error.connect(lambda err: QMessageBox.warning(self, "Error", str(err)))
        self.wand_thread.start()

    def _on_magic_wand_finished(self, regions):
        if hasattr(self, 'magic_wand_button'):
            self.magic_wand_button.setEnabled(True)
            self.magic_wand_button.setText("MAGIC WAND")
        if regions:
            self._magic_wand_candidates = regions
            self._magic_wand_index = 0
            self.logger.info(f"Magic Wand found {len(regions)} regions.")
            if len(regions) > 1:
                QMessageBox.information(self, "Magic Wand", f"Found {len(regions)} regions! Click 'Magic Wand' again to cycle through them.")
            self._apply_magic_wand_region(regions[0])
            self.update_wizard_step(3, "Magic Wand detected a region! Adjust if needed, then confirm.")
        else:
            QMessageBox.information(self, "Magic Wand", "No clear HUD elements detected automatically. Please draw manually.")
            self._magic_wand_candidates = None

    def _apply_magic_wand_region(self, r):
        """Helper to apply a rect to the draw widget."""

        from PyQt5.QtCore import QRectF
        self.logger.info(f"Applying Magic Wand region: {r}")
        rect_f = QRectF(float(r.x()), float(r.y()), float(r.width()), float(r.height()))
        self.draw_widget._crop_rect_img = rect_f
        self.draw_widget._show_confirm_button()
        self.draw_widget._zoom_to_crop()
        self.draw_widget.update()

    def show_completion_state(self):
        if hasattr(self, 'complete_button'):
            self.complete_button.setVisible(True)
        
    def finish_and_save(self):
        self.update_wizard_step(5, "Configuration complete")
        if self.portrait_window:
            self.portrait_window = None
        self.update_progress_tracker()
        self.close()

    def launch_portrait_editor(self, pix, rect, role):
        if self.background_crop_width <= 0: self.background_crop_width = 1920
        try:
            if self.portrait_window is None:
                self.portrait_window = PortraitWindow(self.media_processor.original_resolution, self.config_path)
                self.portrait_window.destroyed.connect(lambda: setattr(self, 'portrait_window', None))
                self.portrait_window.done_organizing.connect(self.finish_and_save)
            if hasattr(self, 'draw_widget') and not self.draw_widget.pixmap.isNull():
                self.portrait_window.set_background_image(self.draw_widget.pixmap)

            def set_focus_on_portrait():
                if self.portrait_window:
                    self.portrait_window.show()
                    self.portrait_window.raise_()
                    self.portrait_window.activateWindow()
                    self.portrait_window.setFocus()
            QTimer.singleShot(50, set_focus_on_portrait)
            self.portrait_window.add_scissored_item(pix, rect, self.background_crop_width, role)
            items = self.portrait_window.scene.selectedItems()
            if items: items[0].set_role(role)
            self.update_wizard_step(4, f"Adjust '{role}' position and size in the portrait editor")
        except Exception as e:
            self.logger.error(f"CRASH PREVENTED: {e}", exc_info=True)
            QMessageBox.critical(self, "Error", f"Failed to open editor: {e}")

    def reset_state(self, force=False):
        if not force:
            reply = QMessageBox.question(self, 'Reset Confirmation', 
                                         "Are you sure you want to reset all current progress? Unsaved changes will be lost.",
                                         QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
            if reply == QMessageBox.No:
                return
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
        self._magic_wand_candidates = None
        self.update_progress_tracker()

    def play_pause(self):
        """Toggle play/pause and update the button text immediately.
        The previous implementation used misencoded characters and a delayed update via
        QTimer.singleShot which caused flickering and garbled text. This version
        updates the button text synchronously and uses proper Unicode symbols.
        """
        self.media_processor.play_pause()
        if self.media_processor.is_playing():
            self.play_pause_button.setText("⏸ PAUSE")
        else:
            self.play_pause_button.setText("▶ PLAY")

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
            has_res = self.media_processor.original_resolution is not None
            if total_ms > 0 and has_res:
                self.snapshot_button.setEnabled(True)
                if hasattr(self, 'magic_wand_button'):
                     self.magic_wand_button.setEnabled(True)
                self.snapshot_button.setText("START CROPPING")
                self.position_slider.setRange(0, total_ms)
                self.position_slider.setSingleStep(33)
                self.position_slider.setPageStep(1000)
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
        if hasattr(self, 'snapshot_path') and self.snapshot_path and os.path.exists(self.snapshot_path):
            try:
                os.remove(self.snapshot_path)
                self.logger.info(f"Deleted previous snapshot: {self.snapshot_path}")
            except Exception as e:
                self.logger.warning(f"Failed to delete previous snapshot: {e}")
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
            total_ms = self.media_processor.media_player.get_length()
            self.logger.info(f"Slider (ms): {slider_value}, Total (ms): {total_ms}")
            if total_ms > 0:
                preferred_time = slider_value / 1000.0
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

                def check_and_show(attempts=0):
                    if os.path.exists(self.snapshot_path) and os.path.getsize(self.snapshot_path) > 0:
                        file_size = os.path.getsize(self.snapshot_path)
                        self.logger.info(f"Snapshot file exists, size: {file_size} bytes")
                        enhanced_logger = self._get_enhanced_logger()
                        if enhanced_logger:
                            enhanced_logger.log_video_loaded(self.snapshot_path, "snapshot")
                        QApplication.processEvents()
                        QTimer.singleShot(200, self._show_draw_view)
                    elif attempts < 50:
                        self.logger.warning(f"Snapshot file not ready (attempt {attempts}), retrying...")
                        QTimer.singleShot(100, lambda: check_and_show(attempts + 1))
                    else:
                        self.logger.error("Snapshot creation timed out.")
                        QMessageBox.warning(self, "Snapshot Error", "Timed out waiting for snapshot file creation.")
                        self._snapshot_processing = False
                        self.snapshot_button.setEnabled(True)
                QTimer.singleShot(50, lambda: check_and_show(0))
            else: 
                self.logger.error(f"Snapshot failed: {message}")
                QMessageBox.warning(self, "Snapshot Error", message)
                self._snapshot_processing = False
                self.snapshot_button.setEnabled(True)
        except Exception as e:
            self.logger.error(f"Error taking snapshot: {e}", exc_info=True)
            QMessageBox.warning(self, "Snapshot Error", f"Unexpected error: {e}")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)

    def _show_draw_view(self):
        self.logger.info(f"Showing draw view for snapshot: {self.snapshot_path}")
        if not os.path.exists(self.snapshot_path):
            self.logger.error(f"Snapshot file does not exist: {self.snapshot_path}")
            QMessageBox.warning(self, "Snapshot Error", "Snapshot file was not created. Please try again.")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)
            return
        file_size = os.path.getsize(self.snapshot_path)
        self.logger.info(f"Snapshot file size: {file_size} bytes")
        if file_size == 0:
            self.logger.error(f"Snapshot file is empty: {self.snapshot_path}")
            QMessageBox.warning(self, "Snapshot Error", "Snapshot file is empty. Please try again.")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)
            return
        if hasattr(self, 'draw_scroll_area'):
            self.view_stack.setCurrentWidget(self.draw_scroll_area)
            self.logger.info("Switched to draw_scroll_area view")
        else:
            self.view_stack.setCurrentWidget(self.draw_widget)
            self.logger.info("Switched to draw_widget view")
        QApplication.processEvents()
        snapshot_pixmap = QPixmap(self.snapshot_path)
        if snapshot_pixmap.isNull():
            self.logger.error(f"Failed to load snapshot from: {self.snapshot_path}")
            QMessageBox.warning(self, "Snapshot Error", "Failed to load snapshot image. The file may be corrupted.")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)
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
        self.logger.info("Draw view displayed successfully")
        self._snapshot_processing = False
        self.snapshot_button.setEnabled(True)
        self.snapshot_button.setText("RETAKE SNAPSHOT")

    def show_video_view(self):
        self.view_stack.setCurrentWidget(self.video_frame)
        self.update_wizard_step(2, "Video View. Find a frame and take a snapshot.")
        if hasattr(self, 'snapshot_button'):
            self.snapshot_button.setText("TAKE SNAPSHOT")

    def set_position(self, position_ms):
        total = self.media_processor.media_player.get_length()
        if total > 0:
            self.media_processor.set_position(position_ms / total)
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
            self.current_time_label.setText(self._format_time(self.position_slider.value()))
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
        if self.media_processor.media and not getattr(self, 'is_scrubbing', False):
            self.position_slider.setValue(self.media_processor.media_player.get_time())
        self.update_time_labels()
           
    def get_title_info(self):
        return self.base_title

    def _format_time(self, millis):
        total_seconds = int(millis / 1000)
        minutes = total_seconds // 60
        seconds = total_seconds % 60
        return f"{minutes:02d}:{seconds:02d}"
