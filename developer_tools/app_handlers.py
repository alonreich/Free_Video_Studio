import os
import json
import logging
import tempfile
from PyQt5.QtWidgets import QApplication, QFileDialog, QMessageBox
from PyQt5.QtGui import QPixmap
from PyQt5.QtCore import QTimer, QThread
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
        """
        Handles the signal from the draw widget. Instead of launching a new window,
        it now calls the integrated method to add the item to the portrait scene.
        """
        if not pix or not rect: return
        tech_key_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
        tech_key = tech_key_map.get(role, "unknown")
        if tech_key == "unknown": return
        self.add_scissored_item(pix, rect, self.background_crop_width, role)
        self.draw_widget.clear_selection()
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        self.update_wizard_step(3, f"Adjust '{role}' in the Portrait Composer, then click FINISH to save.")

    def on_magic_wand_clicked(self):
        if not self.snapshot_path or not os.path.exists(self.snapshot_path):
            QMessageBox.warning(self, "Magic Wand", "Please take a snapshot first.")
            return
        script_dir = os.path.dirname(os.path.abspath(__file__))
        anchor_dir = os.path.join(script_dir, 'anchors')
        if not os.path.isdir(anchor_dir):
             QMessageBox.critical(self, "Setup Required", f"Magic Wand cannot function because the 'anchors' directory is missing.\n\nLocation: {anchor_dir}")
             return
        if hasattr(self, '_magic_wand_candidates') and self._magic_wand_candidates:
            self._magic_wand_index = (self._magic_wand_index + 1) % len(self._magic_wand_candidates)
            self._apply_magic_wand_region(self._magic_wand_candidates[self._magic_wand_index])
            self.update_wizard_step(3, f"Cycle {self._magic_wand_index+1}/{len(self._magic_wand_candidates)}: Adjust region, then confirm.")
            return
        self.logger.info("Magic Wand activated.")
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
        self.wand_worker.error.connect(lambda err: QMessageBox.warning(self, "Magic Wand Error", str(err)))
        self.wand_thread.start()

    def _on_magic_wand_finished(self, regions):
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
        from PyQt5.QtCore import QRectF
        self.logger.info(f"Applying Magic Wand region: {r}")
        rect_f = QRectF(float(r.x()), float(r.y()), float(r.width()), float(r.height()))
        self.draw_widget.set_crop_rect(rect_f)
        self.draw_widget.update()

    def finish_and_save(self):
        self.update_wizard_step(5, "Configuration complete")
        self.update_progress_tracker()
        self.close()

    def reset_state(self, force=False):
        if not force:
            reply = QMessageBox.question(self, 'Reset Confirmation', 
                                         "Are you sure you want to reset all current progress? Unsaved changes will be lost.",
                                         QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
            if reply == QMessageBox.No:
                return
        if hasattr(self, 'portrait_scene'):
            self.portrait_scene.clear()
            self.load_existing_placeholders()
        self.media_processor.stop()
        self.media_processor.set_media_to_null()
        self.play_pause_button.setEnabled(False)
        self.snapshot_button.setEnabled(False)
        self.draw_widget.clear_selection()
        self.draw_widget.setImage(None) 
        self.view_stack.setCurrentWidget(self.video_frame)
        self.update_wizard_step(1, "Open a video file to begin the configuration wizard.")
        if hasattr(self, 'magic_wand_button'):
            self.magic_wand_button.setVisible(False)
        self._magic_wand_candidates = None
        self.update_progress_tracker()

    def play_pause(self):
        self.media_processor.play_pause()
        if self.media_processor.is_playing():
            self.play_pause_button.setText("⏸ PAUSE")
        else:
            self.play_pause_button.setText("▶ PLAY")
        self.play_pause_button.repaint()

    def open_file(self):
        self.timer.stop()
        file_path, _ = QFileDialog.getOpenFileName(self, "Open Video", self.last_dir or "", "Video Files (*.mp4 *.avi *.mkv)")
        if file_path:
            self.load_file(file_path)
        else:
            self.timer.start()

    def load_file(self, file_path):
        self.last_dir = os.path.dirname(file_path)
        self.media_processor.load_media(file_path, self.video_frame.winId())
        enhanced_logger = self._get_enhanced_logger()
        if enhanced_logger:
            resolution = self.media_processor.original_resolution or "unknown"
            enhanced_logger.log_video_loaded(file_path, resolution)
        self.play_pause_button.setEnabled(True)
        self.play_pause_button.setText("▶ PLAY")
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
                self.magic_wand_button.setEnabled(False)
                self.snapshot_button.setText("START CROPPING")
                self.position_slider.setRange(0, total_ms)
            else:
                QTimer.singleShot(500, enable_snapshot_if_ready)
        QTimer.singleShot(1000, enable_snapshot_if_ready)
        self.update_progress_tracker()
        self.timer.start()

    def take_snapshot(self):
        if hasattr(self, '_snapshot_processing') and self._snapshot_processing:
            return
        self._snapshot_processing = True
        self.snapshot_button.setEnabled(False)
        was_playing = self.media_processor.is_playing()
        if was_playing:
            self.play_pause()
        preferred_time = self.position_slider.value() / 1000.0
        timestamp = int(tempfile.time.time() * 1000)
        unique_snapshot_path = os.path.join(tempfile.gettempdir(), f"snapshot_{timestamp}.png")
        success, message = self.media_processor.take_snapshot(unique_snapshot_path, preferred_time)
        if success: 
            self.snapshot_path = unique_snapshot_path
            QTimer.singleShot(100, self._check_and_show_snapshot)
        else: 
            QMessageBox.warning(self, "Snapshot Error", message)
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)

    def _check_and_show_snapshot(self, attempts=0):
        if os.path.exists(self.snapshot_path) and os.path.getsize(self.snapshot_path) > 0:
            self._show_draw_view()
        elif attempts < 20:
            QTimer.singleShot(100, lambda: self._check_and_show_snapshot(attempts + 1))
        else:
            QMessageBox.warning(self, "Snapshot Error", "Timed out waiting for snapshot file.")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)

    def _show_draw_view(self):
        snapshot_pixmap = QPixmap(self.snapshot_path)
        if snapshot_pixmap.isNull():
            QMessageBox.warning(self, "Snapshot Error", "Failed to load snapshot image.")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)
            return
        self.set_background_image(snapshot_pixmap)
        self.view_stack.setCurrentWidget(self.draw_scroll_area)
        self.draw_widget.setImage(self.snapshot_path)
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        next_element = self.get_next_element_to_configure()
        if next_element:
            self.update_wizard_step(3, f"Draw a box around the {next_element}")
        else:
            self.update_wizard_step(3, "All elements configured! You can still add more if needed.")
        self.update_progress_tracker()
        self._snapshot_processing = False
        self.snapshot_button.setEnabled(True)
        self.snapshot_button.setText("RETAKE SNAPSHOT")
        self.magic_wand_button.setEnabled(True)
        self.magic_wand_button.setVisible(True)

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

    def _on_slider_moved(self, position):
        self.update_time_labels()
        
    def _on_slider_released(self):
        self.is_scrubbing = False
        self.set_position(self.position_slider.value())

    def update_time_labels(self):
        if not hasattr(self, 'current_time_label') or not hasattr(self, 'total_time_label'):
            return
        total_ms = self.media_processor.media_player.get_length()
        current_ms = self.media_processor.media_player.get_time()
        if self.is_scrubbing:
             current_ms = self.position_slider.value()
        self.current_time_label.setText(self._format_time(current_ms))
        self.total_time_label.setText(self._format_time(total_ms))
        
    def update_ui(self):
        if self.media_processor.media and not self.is_scrubbing:
            self.position_slider.setValue(self.media_processor.media_player.get_time())
        self.update_time_labels()
           
    def get_title_info(self):
        return self.base_title

    def _format_time(self, millis):
        if millis < 0: millis = 0
        total_seconds = int(millis / 1000)
        minutes = total_seconds // 60
        seconds = total_seconds % 60
        return f"{minutes:02d}:{seconds:02d}"
