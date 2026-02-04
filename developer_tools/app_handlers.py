import os
import json
import logging
import tempfile
import time
from PyQt5.QtWidgets import QApplication, QFileDialog, QMessageBox
from PyQt5.QtGui import QPixmap
from PyQt5.QtCore import QTimer, QThread, Qt
from utils import cleanup_temp_snapshots
from config import CROP_APP_STYLESHEET, HUD_ELEMENT_MAPPINGS, UI_BEHAVIOR, get_tech_key_from_role
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
        tech_key = get_tech_key_from_role(role)
        if tech_key == "unknown": return
        if self._get_enhanced_logger():
            w = rect.width() * 1.3
            h = rect.height() * 1.3
            pos = self._default_position_for_role(role, w, h)
            self._get_enhanced_logger().log_hud_crop_details(role, rect, pos, (w, h))
        if hasattr(self, '_magic_wand_preview_timer') and self._magic_wand_preview_timer:
            self._magic_wand_preview_timer.stop()
        self.add_scissored_item(pix, rect, self.background_crop_width, role)
        self.draw_widget.clear_selection()
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        self.update_wizard_step(3, f"Adjust '{role}' in the Portrait Composer, then click FINISH to save.")

    def on_magic_wand_clicked(self):
        if not self.snapshot_path or not os.path.exists(self.snapshot_path):
            QMessageBox.warning(self, "Magic Wand", "Please take a snapshot first.")
            return
        if self._get_enhanced_logger():
            self._get_enhanced_logger().log_button_click("MAGIC WAND", "Activated automated HUD detection")
        script_dir = os.path.dirname(os.path.abspath(__file__))
        anchor_dir = os.path.join(script_dir, 'anchors')
        if not os.path.isdir(anchor_dir):
             QMessageBox.critical(self, "Setup Required", f"Magic Wand cannot function because the 'anchors' directory is missing.\n\nLocation: {anchor_dir}\n\nPlease reinstall or create this folder with reference images.")
             return
        if hasattr(self, '_magic_wand_candidates') and self._magic_wand_candidates:
            self._magic_wand_index = (self._magic_wand_index + 1) % len(self._magic_wand_candidates)
            self._apply_magic_wand_region(self._magic_wand_candidates[self._magic_wand_index])
            self.update_wizard_step(3, f"Preview {self._magic_wand_index+1}/{len(self._magic_wand_candidates)}: Pick role to confirm.")
            return
        self.logger.info("Magic Wand activated.")
        self._magic_wand_active_id = int(time.time() * 1000)
        self._magic_wand_cancelled = False
        self._start_magic_wand_timeout(self._magic_wand_active_id)
        self.magic_wand_button.setEnabled(False)
        self.magic_wand_button.setText("Analyzing...")
        self.wand_thread = QThread()
        self.wand_worker = MagicWandWorker(MagicWand(self.logger), self.snapshot_path, {})
        self.wand_worker.moveToThread(self.wand_thread)
        self.wand_thread.started.connect(self.wand_worker.run)
        self.wand_worker.finished.connect(lambda regions: self._on_magic_wand_finished(regions, self._magic_wand_active_id))
        self.wand_worker.finished.connect(self.wand_thread.quit)
        self.wand_worker.finished.connect(self.wand_worker.deleteLater)
        self.wand_thread.finished.connect(self.wand_thread.deleteLater)
        self.wand_worker.error.connect(self._on_magic_wand_error)
        self.wand_thread.start()

    def _start_magic_wand_timeout(self, magic_id):
        if not hasattr(self, '_magic_wand_timeout_timer'):
            self._magic_wand_timeout_timer = QTimer(self)
            self._magic_wand_timeout_timer.setSingleShot(True)
            self._magic_wand_timeout_timer.timeout.connect(lambda: self._on_magic_wand_timeout(magic_id))
        self._magic_wand_timeout_timer.start(UI_BEHAVIOR.MAGIC_WAND_MAX_SECONDS * 1000)

    def _on_magic_wand_timeout(self, magic_id):
        if magic_id != getattr(self, '_magic_wand_active_id', None):
            return
        self._magic_wand_cancelled = True
        self.magic_wand_button.setEnabled(True)
        self.magic_wand_button.setText("MAGIC WAND")
        if hasattr(self, 'wand_thread') and self.wand_thread.isRunning():
            self.wand_thread.quit()
            self.wand_thread.wait()
        if hasattr(self, 'status_label'):
            self.status_label.setText("Magic Wand timed out. Try another frame or draw manually.")

    def _on_magic_wand_error(self, err):
        self.magic_wand_button.setEnabled(True)
        self.magic_wand_button.setText("MAGIC WAND")
        if hasattr(self, 'wand_thread') and self.wand_thread.isRunning():
            self.wand_thread.quit()
            self.wand_thread.wait()
        QMessageBox.warning(self, "Magic Wand Error", str(err))

    def _on_magic_wand_finished(self, regions, magic_id):
        if magic_id != getattr(self, '_magic_wand_active_id', None):
            return
        if getattr(self, '_magic_wand_cancelled', False):
            return
        self.magic_wand_button.setEnabled(True)
        self.magic_wand_button.setText("MAGIC WAND")
        if regions:
            self._magic_wand_candidates = regions
            self._magic_wand_index = 0
            self.logger.info(f"Magic Wand found {len(regions)} regions.")
            if len(regions) > 1:
                QMessageBox.information(self, "Magic Wand", f"Found {len(regions)} regions! Previewing automatically; click a role to confirm.")
                if not hasattr(self, '_magic_wand_preview_timer'):
                    self._magic_wand_preview_timer = QTimer(self)
                    self._magic_wand_preview_timer.timeout.connect(self._cycle_magic_wand_preview)
                self._magic_wand_preview_timer.start(UI_BEHAVIOR.MAGIC_WAND_PREVIEW_DELAY_MS)
            self._apply_magic_wand_region(regions[0])
            self.update_wizard_step(3, "Magic Wand detected a region! Adjust if needed, then confirm.")
        else:
            QMessageBox.information(self, "Magic Wand", "No clear HUD elements detected automatically. Please draw manually.")
            self._magic_wand_candidates = None

    def _cycle_magic_wand_preview(self):
        if not getattr(self, '_magic_wand_candidates', None):
            return
        self._magic_wand_index = (self._magic_wand_index + 1) % len(self._magic_wand_candidates)
        self._apply_magic_wand_region(self._magic_wand_candidates[self._magic_wand_index])

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
        if self._get_enhanced_logger():
            self._get_enhanced_logger().log_button_click("Reset State", "User confirmed complete reset")
        if hasattr(self, '_magic_wand_preview_timer') and self._magic_wand_preview_timer:
            try:
                self._magic_wand_preview_timer.stop()
            except Exception:
                pass
        try:
            if hasattr(self, 'config_manager'):
                default_config = self.config_manager._sanitize_config(self.config_manager.DEFAULT_VALUES)
                self.config_manager.save_config(default_config)
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error(f"Failed to reset HUD config: {e}")
        if hasattr(self, 'portrait_scene'):
            try:
                if hasattr(self, 'placeholders_group'):
                    self.placeholders_group.clear()
                self.background_item = None
                self.portrait_scene.clear()
                self.load_existing_placeholders()
            except Exception as e:
                if hasattr(self, 'logger'):
                    self.logger.error(f"Failed to reset portrait scene: {e}")
        if hasattr(self, 'media_processor') and self.media_processor:
            try:
                self.media_processor.stop()
                self.media_processor.set_media_to_null()
            except Exception as e:
                if hasattr(self, 'logger'):
                    self.logger.error(f"Failed to reset media processor: {e}")
        if hasattr(self, 'play_pause_button'):
            self.play_pause_button.setEnabled(False)
        if hasattr(self, 'snapshot_button'):
            self.snapshot_button.setEnabled(False)
            self.snapshot_button.setVisible(True)
        if hasattr(self, 'slider_container'):
            self.slider_container.setVisible(True)
        if hasattr(self, 'draw_widget') and self.draw_widget:
            try:
                self.draw_widget.clear_selection()
                self.draw_widget.setImage(None)
                if hasattr(self.draw_widget, 'role_toolbar'):
                    self.draw_widget.role_toolbar.hide()
            except Exception as e:
                if hasattr(self, 'logger'):
                    self.logger.error(f"Failed to reset draw widget: {e}")
        if hasattr(self, 'view_stack') and hasattr(self, 'video_frame'):
            try:
                self.view_stack.setCurrentWidget(self.video_frame)
            except Exception as e:
                if hasattr(self, 'logger'):
                    self.logger.error(f"Failed to reset view stack: {e}")
        self.update_wizard_step(1, "Open a video file to begin the configuration wizard.")
        if hasattr(self, '_set_upload_hint_active'):
            self._set_upload_hint_active(True)
        if hasattr(self, 'magic_wand_button'):
            self.magic_wand_button.setVisible(False)
        self._magic_wand_candidates = None
        self.update_progress_tracker()
        if hasattr(self, '_dirty'):
            self._dirty = False
            if hasattr(self, '_refresh_done_button'):
                self._refresh_done_button()
        if hasattr(self, 'status_label'):
            self.status_label.setText("Reset complete")

    def play_pause(self):
        is_playing = self.media_processor.play_pause()
        if is_playing is None:
            is_playing = self.media_processor.is_playing()
        if self._get_enhanced_logger():
            self._get_enhanced_logger().log_button_click("Play/Pause", f"New State: {'Playing' if is_playing else 'Paused'}")
        self._sync_play_pause_button(is_playing)

    def _sync_play_pause_button(self, is_playing=None):
        if is_playing is None:
            is_playing = self.media_processor.is_playing()
        if is_playing:
            self.play_pause_button.setText("⏸ PAUSE")
            self.show_video_view()
        else:
            self.play_pause_button.setText("▶ PLAY")
        self.play_pause_button.repaint()

    def open_file(self):
        if self._get_enhanced_logger():
            self._get_enhanced_logger().log_button_click("Open Video File")
        self.timer.stop()
        if hasattr(self, '_set_upload_hint_active'):
            self._set_upload_hint_active(False)
        file_path, _ = QFileDialog.getOpenFileName(self, "Open Video", self.last_dir or "", "Video Files (*.mp4 *.avi *.mkv)")
        if file_path:
            self.load_file(file_path)
        else:
            if hasattr(self, '_set_upload_hint_active'):
                self._set_upload_hint_active(True)
            self.timer.start()

    def load_file(self, file_path):
        self.last_dir = os.path.dirname(file_path)
        self.media_processor.load_media(file_path, self.video_frame.winId())
        if hasattr(self, '_set_upload_hint_active'):
            self._set_upload_hint_active(False)
        if hasattr(self, 'status_label'):
            self.status_label.setText("Video loaded - prepare snapshot")
        enhanced_logger = self._get_enhanced_logger()
        if enhanced_logger:
            resolution = self.media_processor.original_resolution or "unknown"
            enhanced_logger.log_video_loaded(file_path, resolution)
        self.play_pause_button.setEnabled(True)
        self.snapshot_button.setEnabled(False)
        self.snapshot_button.setText("Loading...")
        self.position_slider.setEnabled(True)
        self.show_video_view()
        self.update_wizard_step(2, "Play/Pause to find a clear frame with visible HUD elements.")
        QTimer.singleShot(200, self._sync_play_pause_button)

        def enable_snapshot_if_ready():
            total_ms = self.media_processor.media_player.get_length()
            has_res = self.media_processor.original_resolution is not None
            if total_ms > 0 and has_res:
                self.snapshot_button.setEnabled(True)
                self.magic_wand_button.setEnabled(False)
                self.snapshot_button.setText("START CROPPING")
                self.position_slider.setRange(0, total_ms)
                if hasattr(self, 'status_label'):
                    self.status_label.setText("Frame ready - click START CROPPING")
            else:
                QTimer.singleShot(500, enable_snapshot_if_ready)
        QTimer.singleShot(1000, enable_snapshot_if_ready)
        self.update_progress_tracker()
        self.timer.start()

    def take_snapshot(self):
        if not self.media_processor.original_resolution:
             QMessageBox.warning(self, "Not Ready", "Video resolution not yet detected. Please play the video for a moment.")
             return
        if self._get_enhanced_logger():
            self._get_enhanced_logger().log_button_click("START CROPPING", f"Video Position: {self.position_slider.value()}ms")
        try:
            if hasattr(self, '_snapshot_processing') and self._snapshot_processing:
                return
            self._snapshot_processing = True
            self._magic_wand_candidates = None
            self.snapshot_button.setEnabled(False)
            self.snapshot_button.setVisible(False)
            if hasattr(self, 'status_label'):
                self.status_label.setText("Capturing snapshot...")
            if hasattr(self, 'slider_container'):
                self.slider_container.setVisible(False)
            was_playing = self.media_processor.is_playing()
            if was_playing:
                self.play_pause()
            self.play_pause_button.setText("▶ PLAY")
            preferred_time = self.position_slider.value() / 1000.0
            timestamp = int(time.time() * 1000)
            unique_snapshot_path = os.path.join(tempfile.gettempdir(), f"snapshot_{timestamp}.png")
            success, message = self.media_processor.take_snapshot(unique_snapshot_path, preferred_time)
            if success: 
                self.snapshot_path = unique_snapshot_path
                if self._get_enhanced_logger():
                    self._get_enhanced_logger().log_snapshot_taken(preferred_time, unique_snapshot_path)
                QTimer.singleShot(UI_BEHAVIOR.SNAPSHOT_RETRY_INTERVAL_MS, self._check_and_show_snapshot)
            else: 
                QMessageBox.warning(self, "Snapshot Error", message)
                self._snapshot_processing = False
                self.snapshot_button.setEnabled(True)
        except Exception as e:
            self.logger.critical(f"CRITICAL: Crash in take_snapshot: {e}", exc_info=True)
            QMessageBox.critical(self, "Crash Error", f"An error occurred while taking snapshot:\n{e}")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)

    def _check_and_show_snapshot(self, attempts=0):
        try:
            if os.path.exists(self.snapshot_path) and os.path.getsize(self.snapshot_path) > 0:
                self._show_draw_view()
            elif attempts < UI_BEHAVIOR.SNAPSHOT_MAX_RETRIES:
                QTimer.singleShot(UI_BEHAVIOR.SNAPSHOT_RETRY_INTERVAL_MS, lambda: self._check_and_show_snapshot(attempts + 1))
            else:
                QMessageBox.warning(self, "Snapshot Error", "Timed out waiting for snapshot file.")
                self._snapshot_processing = False
                self.snapshot_button.setEnabled(True)
        except Exception as e:
            self.logger.error(f"Error in _check_and_show_snapshot: {e}", exc_info=True)
            QMessageBox.critical(self, "Snapshot Error", f"Failed to verify snapshot: {e}")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)

    def _show_draw_view(self):
        try:
            snapshot_pixmap = QPixmap(self.snapshot_path)
            if snapshot_pixmap.isNull():
                QMessageBox.warning(self, "Snapshot Error", "Failed to load snapshot image (invalid format).")
                self._snapshot_processing = False
                self.snapshot_button.setEnabled(True)
                return
            self.set_background_image(snapshot_pixmap)
            self.view_stack.setCurrentWidget(self.draw_scroll_area)
            self.draw_widget.setImage(self.snapshot_path)
            self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
            self.draw_widget.update()
            next_element = self.get_next_element_to_configure()
            if next_element:
                self.update_wizard_step(3, f"Draw a box around the {next_element}")
            else:
                self.update_wizard_step(3, "All elements configured! You can still add more if needed.")
            if hasattr(self, 'status_label'):
                self.status_label.setText("Draw selection on snapshot")
            self.update_progress_tracker()
            self.draw_widget.setFocus(Qt.OtherFocusReason)
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)
            self.snapshot_button.setVisible(False)
            self.magic_wand_button.setEnabled(True)
            self.magic_wand_button.setVisible(True)
            self.draw_widget.role_toolbar.hide()
        except Exception as e:
            self.logger.error(f"Error in _show_draw_view: {e}", exc_info=True)
            QMessageBox.critical(self, "Display Error", f"Failed to display snapshot: {e}")
            self._snapshot_processing = False
            self.snapshot_button.setEnabled(True)

    def show_video_view(self):
        self.view_stack.setCurrentWidget(self.video_frame)
        self.update_wizard_step(2, "Video View. Find a frame and take a snapshot.")
        if hasattr(self, '_set_upload_hint_active'):
            hint_active = not bool(self.media_processor.media)
            self._set_upload_hint_active(hint_active)
        if hasattr(self, 'snapshot_button'):
            self.snapshot_button.setVisible(True)
            self.snapshot_button.setText("START CROPPING")
        if hasattr(self, 'slider_container'):
            self.slider_container.setVisible(True)
        if hasattr(self, 'status_label'):
            self.status_label.setText("Video view")

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
