import os
import json
import logging
import tempfile
import time
from PyQt5.QtWidgets import QApplication, QFileDialog, QMessageBox
from PyQt5.QtGui import QPixmap, QColor
from PyQt5.QtCore import QTimer, QThread, Qt
from utils import cleanup_temp_snapshots, get_snapshot_dir
from config import CROP_APP_STYLESHEET, HUD_ELEMENT_MAPPINGS, UI_BEHAVIOR, get_tech_key_from_role
from enhanced_logger import get_enhanced_logger
from magic_wand import MagicWand, MagicWandWorker
from system.utils import ProcessManager

class CropAppHandlers:
    def _is_wand_thread_running(self):
        """Safely check Magic Wand thread state without touching deleted Qt wrappers."""
        thread = getattr(self, 'wand_thread', None)
        if thread is None:
            return False
        try:
            return bool(thread.isRunning())
        except RuntimeError:
            self.wand_thread = None
            return False

    def _cleanup_magic_wand_runtime(self):
        """[FIX #4] Robust Magic Wand cleanup with thread joining."""
        if hasattr(self, '_magic_wand_timeout_timer') and self._magic_wand_timeout_timer:
            self._magic_wand_timeout_timer.stop()
        if hasattr(self, '_analyzing_timer') and self._analyzing_timer:
            self._analyzing_timer.stop()
        thread = getattr(self, 'wand_thread', None)
        if thread and thread.isRunning():
            thread.quit()
            if not thread.wait(1000):
                thread.terminate()
        self.wand_thread = None
        self.wand_worker = None
        if hasattr(self, 'progress_bar'):
            self.progress_bar.setVisible(False)
        self._magic_wand_active_id = None
        self.magic_wand_button.setText("🪄 MAGIC WAND")
        self.magic_wand_button.setEnabled(True)

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
        if hasattr(self, 'open_image_button'):
            self.open_image_button.clicked.connect(self.open_image_fallback)
        if hasattr(self, 'back_to_video_button'):
            self.back_to_video_button.clicked.connect(self.back_to_video)
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
        if hasattr(self, 'media_processor'):
            self.media_processor.info_retrieved.connect(self._on_video_info_ready)

    def set_style(self):
        from config import UNIFIED_STYLESHEET
        style = CROP_APP_STYLESHEET or UNIFIED_STYLESHEET
        self.setStyleSheet(style)

    def _set_upload_hint_active(self, active):
        """[FIX #17] Centralized hint management."""
        target = getattr(self, 'hint_overlay_widget', None)
        if not target:
            return
        if active:
            if hasattr(self, '_update_upload_hint_responsive'):
                self._update_upload_hint_responsive()
            target.show()
            target.raise_()
            if hasattr(self, '_hint_group'):
                self._hint_group.start()
        else:
            if hasattr(self, '_hint_group'):
                self._hint_group.stop()
            target.hide()

    def back_to_video(self):
        """[FIX #8] Return to video seeker from drawing view."""
        self.show_video_view()

    def open_image_fallback(self):
        """[FIX #1] Direct image upload when VLC fails."""
        file_path, _ = QFileDialog.getOpenFileName(self, "Open Image", self.last_dir or "", "Images (*.png *.jpg *.jpeg)")
        if file_path:
            self.last_dir = os.path.dirname(file_path)
            self.snapshot_path = file_path
            if hasattr(self, 'save_geometry'):
                self.save_geometry()
            self._show_draw_view()

    def update_wizard_step(self, step_num, instruction):
        """[FIX #17, #22] Short, punchy status labels with Goal/Status split."""
        self.current_step = step_num
        if hasattr(self, 'progress_bar') and self.progress_bar and not self._is_wand_thread_running():
            clamped_step = max(1, min(step_num, 5))
            self.progress_bar.setValue(clamped_step * 20)
            self.progress_bar.setVisible(True)
        goal_map = {
            1: "UPLOAD VIDEO",
            2: "FIND HUD FRAME",
            3: "REFINE BOX",
            4: "PORTRAIT COMPOSER",
            5: "CONFIG READY"
        }
        punchy_map = {
            "Open a video file to begin the configuration wizard.": "UPLOAD VIDEO",
            "Finding clear frame with HUD elements...": "ANALYZING...",
            "Video View. Find a frame and take a snapshot.": "SEEK FRAME",
            "Capturing snapshot...": "CAPTURING...",
            "Now, please refine selection.\n(Use the small arrows to adjust the HUD shape)": "REFINE BOX",
            "All elements configured! You can still add more if needed.": "CONFIG READY"
        }
        if hasattr(self, 'goal_label'):
            self.goal_label.setText(f"Goal: {goal_map.get(step_num, 'Configure HUD')}")
        short_instr = punchy_map.get(instruction, instruction)
        if hasattr(self, 'status_label'):
            self.status_label.setText(short_instr)
        self.update_progress_tracker()
        
    def _on_video_info_ready(self, resolution):
        """[FIX #3, #4] Callback for background resolution detection."""
        self.logger.info(f"Background info ready: {resolution}")
        enhanced_logger = self._get_enhanced_logger()
        if enhanced_logger and self.media_processor.input_file_path:
            enhanced_logger.log_video_loaded(self.media_processor.input_file_path, resolution)
        total_ms = self.media_processor.get_length()
        if total_ms > 0:
            self.position_slider.setRange(0, total_ms)
            self.position_slider.setEnabled(True)
            self.snapshot_button.setEnabled(True)
            self.snapshot_button.setText("START CROPPING")
            if hasattr(self, 'status_label'):
                self.status_label.setText("Frame ready")
            if hasattr(self, 'goal_label'):
                self.goal_label.setText("Goal: Find HUD Frame")

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
        process_rect = rect.toRect() if hasattr(rect, 'toRect') else rect
        if role == "Mini Map + Stats":
            process_rect.setLeft(process_rect.left() - 1)
            if hasattr(self.draw_widget, 'pixmap') and not self.draw_widget.pixmap.isNull():
                pix = self.draw_widget.pixmap.copy(process_rect.intersected(self.draw_widget.pixmap.rect()))
        elif role == "Loot Area":
            process_rect.setRight(process_rect.right() + 1)
            if hasattr(self.draw_widget, 'pixmap') and not self.draw_widget.pixmap.isNull():
                pix = self.draw_widget.pixmap.copy(process_rect.intersected(self.draw_widget.pixmap.rect()))
        rect = process_rect
        tech_key = get_tech_key_from_role(role)
        if tech_key == "unknown": return
        if self._get_enhanced_logger():
            w = rect.width() * 1.3
            h = rect.height() * 1.3
            pos = self._default_position_for_role(role, w, h)
            self._get_enhanced_logger().log_hud_crop_details(role, rect, pos, (w, h))
        if hasattr(self, '_magic_wand_preview_timer') and self._magic_wand_preview_timer:
            self._magic_wand_preview_timer.stop()
        item = self.add_scissored_item(pix, rect, self.background_crop_width, role)
        if item and hasattr(self, 'register_undo_action'):
            def undo_add(it=item, r=role):
                if it.scene(): it.scene().removeItem(it)
                if r in self.modified_roles:
                    still_exists = any(i.assigned_role == r for i in self.portrait_scene.items() if isinstance(i, ResizablePixmapItem) and i != it)
                    if not still_exists:
                        self.modified_roles.discard(r)
                self._mark_dirty(); self.on_selection_changed(); return True

            def redo_add(it=item, r=role):
                if hasattr(self, 'portrait_scene'): self.portrait_scene.addItem(it)
                if r: self.modified_roles.add(r)
                self._mark_dirty(); self.on_selection_changed(); return True
            self.register_undo_action(f"Add {role}", undo_add, redo_add)
        self.draw_widget.clear_selection()
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        self.update_wizard_step(3, f"Adjust '{role}' in the Portrait Composer, then click FINISH to save.")

    def on_magic_wand_clicked(self):
        """[FIX #4, #5, #9, #29] Single worker pattern for Magic Wand with progress bar and thread safety."""
        if not self.snapshot_path or not os.path.exists(self.snapshot_path):
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Warning)
            msg.setWindowTitle("Magic Wand")
            msg.setText("Please take a snapshot first.")
            msg.setStandardButtons(QMessageBox.Ok)
            for btn in msg.findChildren(QPushButton):
                btn.setCursor(Qt.PointingHandCursor)
            msg.exec_()
            return
        if self._is_wand_thread_running():
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
        if hasattr(self, 'progress_bar'):
            self.progress_bar.setValue(10)
            self.progress_bar.setVisible(True)
        self._start_magic_wand_timeout(self._magic_wand_active_id)
        self.magic_wand_button.setEnabled(False)
        self.magic_wand_button.setText("Analyzing.")
        if not hasattr(self, '_analyzing_timer'):
            self._analyzing_timer = QTimer(self)
            self._analyzing_timer.timeout.connect(self._update_analyzing_state)
        self._analyzing_dots = 1
        self._analyzing_timer.start(500)
        self.wand_thread = QThread()
        res = self.media_processor.original_resolution or "1920x1080"
        params = {"resolution": res}
        self.wand_worker = MagicWandWorker(MagicWand(self.logger, params), self.snapshot_path, params)
        self.wand_worker.moveToThread(self.wand_thread)
        self.wand_thread.started.connect(self.wand_worker.run)
        self.wand_worker.finished.connect(lambda regions: self._on_magic_wand_finished(regions, self._magic_wand_active_id))
        self.wand_worker.error.connect(self._on_magic_wand_error)
        self.wand_worker.finished.connect(self.wand_thread.quit)
        self.wand_worker.error.connect(self.wand_thread.quit)
        self.wand_thread.finished.connect(self.wand_thread.deleteLater)
        self.wand_worker.destroyed.connect(lambda: self.logger.debug("Wand worker destroyed"))
        self.wand_thread.start()

    def _update_analyzing_state(self):
        """Update the button text and progress bar to show animation."""
        self._analyzing_dots = (self._analyzing_dots % 3) + 1
        self.magic_wand_button.setText(f"Analyzing{'.' * self._analyzing_dots}")
        if hasattr(self, 'progress_bar'):
            cur = self.progress_bar.value()
            if cur < 85:
                self.progress_bar.setValue(cur + 2)

    def _start_magic_wand_timeout(self, magic_id):
        if not hasattr(self, '_magic_wand_timeout_timer'):
            self._magic_wand_timeout_timer = QTimer(self)
            self._magic_wand_timeout_timer.setSingleShot(True)
            self._magic_wand_timeout_timer.timeout.connect(self._on_magic_wand_timeout_current)
        self._magic_wand_timeout_timer.start(UI_BEHAVIOR.MAGIC_WAND_MAX_SECONDS * 1000)

    def _on_magic_wand_timeout_current(self):
        self._on_magic_wand_timeout(getattr(self, '_magic_wand_active_id', None))

    def _on_magic_wand_timeout(self, magic_id):
        if magic_id != getattr(self, '_magic_wand_active_id', None):
            return
        self._magic_wand_cancelled = True
        self.magic_wand_button.setEnabled(True)
        self.magic_wand_button.setText("MAGIC WAND")
        if self._is_wand_thread_running():
            try:
                self.wand_thread.quit()
                self.wand_thread.wait()
            except RuntimeError:
                self.wand_thread = None
        self._cleanup_magic_wand_runtime()
        if hasattr(self, 'status_label'):
            self.status_label.setText("Magic Wand timed out. Try another frame or draw manually.")

    def _on_magic_wand_error(self, err):
        self._cleanup_magic_wand_runtime()
        if hasattr(self, '_magic_wand_preview_timer'):
            self._magic_wand_preview_timer.stop()
        QMessageBox.warning(self, "Magic Wand Error", str(err))

    def _on_magic_wand_finished(self, regions, magic_id):
        if magic_id != getattr(self, '_magic_wand_active_id', None):
            return
        if getattr(self, '_magic_wand_cancelled', False):
            return
        self._cleanup_magic_wand_runtime()
        if regions:
            self._magic_wand_candidates = regions
            self.logger.info(f"Magic Wand found {len(regions)} regions.")
            if hasattr(self, 'draw_widget'):
                self.draw_widget.set_candidates(regions)
                self.update_wizard_step(3, f"Detected {len(regions)} elements! Click one to tag.")
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
        current_pix, current_rect = self.draw_widget.get_selection()
        if current_rect and not getattr(self, '_magic_wand_candidates', None):
            reply = QMessageBox.question(
                self, "Overwrite Selection?",
                "You have an active selection. Replace it with Magic Wand detection?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if reply == QMessageBox.No:
                return
        self.logger.info(f"Applying Magic Wand region: {r}")
        rect_f = QRectF(float(r.x()), float(r.y()), float(r.width()), float(r.height()))
        self.draw_widget.set_crop_rect(rect_f, auto_zoom=False)
        self.draw_widget.update()

    def reset_state(self, force=False):
        """[FIX #9] Robust cleanup with logging."""
        if not force and getattr(self, '_dirty', False):
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Question)
            msg.setWindowTitle("Reset Confirmation")
            msg.setText("Are you sure you want to reset all current progress?\nUnsaved changes will be lost.")
            msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
            msg.setDefaultButton(QMessageBox.No)
            for btn in msg.findChildren(QPushButton):
                btn.setCursor(Qt.PointingHandCursor)
            if msg.exec_() == QMessageBox.No:
                return
        self.logger.info("Performing surgical state reset...")
        try: 
            if hasattr(self, 'snapshot_path') and self.snapshot_path and os.path.exists(self.snapshot_path):
                try: os.unlink(self.snapshot_path)
                except Exception as e: self.logger.warning(f"Failed to delete snapshot: {e}")
            cleanup_temp_snapshots()
            ProcessManager.cleanup_temp_files()
        except Exception as e:
            self.logger.error(f"Error during state reset: {e}")
        for attr in ['_magic_wand_preview_timer', '_magic_wand_timeout_timer', '_analyzing_timer', '_scrubbing_safety_timer']:
            timer = getattr(self, attr, None)
            if timer: 
                try: timer.stop()
                except: pass
        if self._is_wand_thread_running():
            try:
                self.wand_thread.quit()
                self.wand_thread.wait(1000)
            except:
                self.wand_thread = None
        self._cleanup_magic_wand_runtime()
        if hasattr(self, 'state_manager'):
            self.state_manager.clear_undo_stack()
        if hasattr(self, 'portrait_scene'):
            self.portrait_scene.clear()
            self.portrait_scene.setBackgroundBrush(QColor("black"))
            self.placeholders_group = []
            self.background_item = None
            if hasattr(self, 'modified_roles'):
                self.modified_roles.clear()
        if hasattr(self, 'media_processor') and self.media_processor:
            self.media_processor.stop()
            self.media_processor.set_media_to_null()
        if hasattr(self, 'draw_widget') and self.draw_widget:
            self.draw_widget.clear_selection()
            self.draw_widget.setImage(None)
        self._dirty = False
        self.show_video_view()
        self.update_wizard_step(1, "Open a video file to begin the configuration wizard.")
        self._set_upload_hint_active(True)
        self.update_progress_tracker()
        
    def _sync_play_pause_button(self, is_playing=None):
        if is_playing is None:
            is_playing = self.media_processor.is_playing()
        if is_playing:
            self.play_pause_button.setText("⏸ PAUSE")
        else:
            self.play_pause_button.setText("▶ PLAY")
        self.play_pause_button.update()

    def play_pause(self):
        is_playing = self.media_processor.play_pause()
        if is_playing is None:
            is_playing = self.media_processor.is_playing()
        if self._get_enhanced_logger():
            self._get_enhanced_logger().log_button_click("Play/Pause", f"New State: {'Playing' if is_playing else 'Paused'}")
        if is_playing and self.view_stack.currentWidget() != self.draw_scroll_area:
            self.show_video_view()
        self._sync_play_pause_button(is_playing)

    def open_file(self):
        if self._get_enhanced_logger():
            self._get_enhanced_logger().log_button_click("Open Video File")
        self.timer.stop()
        self._set_upload_hint_active(False)
        file_path, _ = QFileDialog.getOpenFileName(self, "Open Video", self.last_dir or "", "Video Files (*.mp4 *.avi *.mkv)")
        if file_path:
            self.load_file(file_path)
        else:
            if not self.media_processor.media:
                self._set_upload_hint_active(True)
            self.timer.start()

    def load_file(self, file_path):
        """[FIX #12] Defer slider and status updates until media length is confirmed."""
        self.last_dir = os.path.dirname(file_path)
        if hasattr(self, 'save_geometry'):
            self.save_geometry()
        loaded_ok = self.media_processor.load_media(file_path, self.video_frame.winId())
        if not loaded_ok:
            QMessageBox.critical(self, "Video Load Error", "Failed to load this video. Please choose another file.")
            self.play_pause_button.setEnabled(False)
            self.snapshot_button.setEnabled(False)
            self._set_upload_hint_active(True)
            self.timer.start()
            return
        self._set_upload_hint_active(False)
        if hasattr(self, 'status_label'):
            self.status_label.setText("Loading video - please wait...")
        self.play_pause_button.setEnabled(True)
        self.snapshot_button.setEnabled(False)
        self.snapshot_button.setText("Loading...")
        self.position_slider.setEnabled(False)
        self.show_video_view()
        self.update_wizard_step(2, "Finding clear frame with HUD elements...")
        QTimer.singleShot(200, self._sync_play_pause_button)
        self.update_progress_tracker()
        self.timer.start()

    def take_snapshot(self):
        if not self.media_processor.original_resolution:
             if hasattr(self, 'status_label'):
                 self.status_label.setText("WAITING FOR VIDEO...")
             QTimer.singleShot(1000, self.take_snapshot)
             return
        if self._get_enhanced_logger():
            self._get_enhanced_logger().log_button_click("START CROPPING", f"Video Position: {self.position_slider.value()}ms")
        try:
            if hasattr(self, '_snapshot_processing') and self._snapshot_processing:
                return
            self._snapshot_processing = True
            self._magic_wand_candidates = None
            self.snapshot_button.setEnabled(False)
            self.snapshot_button.setText("Capturing...")
            if hasattr(self, 'status_label'):
                self.status_label.setText("Capturing snapshot...")
            if hasattr(self, 'slider_container'):
                self.slider_container.setVisible(False)
            was_playing = self.media_processor.is_playing()
            if was_playing:
                self.play_pause()
            self.snapshot_path = os.path.join(get_snapshot_dir(), "last_snapshot.png")
            if os.path.exists(self.snapshot_path):
                try: os.unlink(self.snapshot_path)
                except: pass
            preferred_time = self.position_slider.value() / 1000.0
            QTimer.singleShot(50, lambda: self._execute_snapshot_capture(self.snapshot_path, preferred_time))
        except Exception as e:
            self.logger.critical(f"CRITICAL: Crash in take_snapshot: {e}", exc_info=True)
            self._reset_snapshot_ui()

    def _execute_snapshot_capture(self, path, time_val):
        """[FIX #2] Robust snapshot execution with explicit verification."""
        success, message = self.media_processor.take_snapshot(path, time_val)
        if success: 
            QTimer.singleShot(UI_BEHAVIOR.SNAPSHOT_RETRY_INTERVAL_MS, self._check_and_show_snapshot)
        else: 
            if hasattr(self, 'status_label'):
                self.status_label.setText(f"Snapshot Error: {message}")
            self._reset_snapshot_ui()

    def _reset_snapshot_ui(self):
        self._snapshot_processing = False
        self.snapshot_button.setEnabled(True)
        self.snapshot_button.setText("START CROPPING")
        self.snapshot_button.setVisible(True)
        if hasattr(self, 'slider_container'):
            self.slider_container.setVisible(True)
        if hasattr(self, 'back_to_video_button'):
            self.back_to_video_button.setVisible(False)

    def _check_and_show_snapshot(self, attempts=0, started_at_ms=None):
        try:
            if started_at_ms is None:
                started_at_ms = int(time.time() * 1000)
            if os.path.exists(self.snapshot_path) and os.path.getsize(self.snapshot_path) > 100:
                self._show_draw_view()
            elif attempts < UI_BEHAVIOR.SNAPSHOT_MAX_RETRIES:
                QTimer.singleShot(
                    UI_BEHAVIOR.SNAPSHOT_RETRY_INTERVAL_MS,
                    lambda: self._check_and_show_snapshot(attempts + 1, started_at_ms)
                )
            else:
                self.logger.error("Snapshot verification timed out")
                self._reset_snapshot_ui()
        except Exception as e:
            self.logger.error(f"Error in _check_and_show_snapshot: {e}")
            self._reset_snapshot_ui()

    def _show_draw_view(self):
        try:
            snapshot_pixmap = QPixmap(self.snapshot_path)
            if snapshot_pixmap.isNull():
                self.logger.error("Failed to load snapshot pixmap")
                self._reset_snapshot_ui()
                return
            self.set_background_image(snapshot_pixmap)
            self.view_stack.setCurrentWidget(self.draw_scroll_area)

            from PyQt5.QtWidgets import QApplication
            QApplication.processEvents()
            self.draw_widget.setImage(self.snapshot_path)
            self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
            next_element = self.get_next_element_to_configure()
            self.update_wizard_step(3, f"Draw a box around the {next_element}" if next_element else "Refine your selection")
            self._snapshot_processing = False
            self.snapshot_button.setVisible(False)
            if hasattr(self, 'back_to_video_button'):
                self.back_to_video_button.setVisible(True)
            if hasattr(self, 'magic_wand_button'):
                self.magic_wand_button.setVisible(True)
            self.draw_widget.setFocus()
        except Exception as e:
            self.logger.error(f"Error in _show_draw_view: {e}")
            self._reset_snapshot_ui()

    def show_video_view(self):
        self.view_stack.setCurrentWidget(self.video_frame)
        self.update_wizard_step(2, "Video View. Find a frame and take a snapshot.")
        if hasattr(self, 'snapshot_button'):
            self.snapshot_button.setVisible(True)
            self.snapshot_button.setEnabled(bool(self.media_processor.original_resolution))
        if hasattr(self, 'back_to_video_button'):
            self.back_to_video_button.setVisible(False)
        if hasattr(self, 'slider_container'):
            self.slider_container.setVisible(True)
        if hasattr(self, 'magic_wand_button'):
            self.magic_wand_button.setVisible(False)

    def set_position(self, position_ms):
        if not hasattr(self, 'media_processor') or not self.media_processor or not self.media_processor.media_player:
            return
        total = self.media_processor.media_player.get_length()
        if total > 0:
            self.media_processor.set_position(position_ms / total)
        self.update_time_labels()

    def _on_slider_pressed(self):
        self.is_scrubbing = True
        self._was_playing_before_scrub = self.media_processor.is_playing()
        if self._was_playing_before_scrub:
            self.media_processor.media_player.pause()
            self._sync_play_pause_button(False)
        if not hasattr(self, '_scrubbing_safety_timer'):
            self._scrubbing_safety_timer = QTimer(self)
            self._scrubbing_safety_timer.setSingleShot(True)
            self._scrubbing_safety_timer.timeout.connect(self._on_slider_released)
        self._scrubbing_safety_timer.start(2000)

    def _on_slider_moved(self, position):
        if hasattr(self, '_scrubbing_safety_timer'):
            self._scrubbing_safety_timer.start(2000)
        self.update_time_labels()
        if self.is_scrubbing:
            self.set_position(position)
        
    def _on_slider_released(self):
        if hasattr(self, '_scrubbing_safety_timer'):
            self._scrubbing_safety_timer.stop()
        self.is_scrubbing = False
        self.set_position(self.position_slider.value())
        if getattr(self, '_was_playing_before_scrub', False):
            self.media_processor.media_player.play()
            self._sync_play_pause_button(True)
        self._was_playing_before_scrub = False

    def update_time_labels(self):
        if not hasattr(self, 'current_time_label') or not hasattr(self, 'total_time_label'):
            return
        total_ms = self.media_processor.get_length()
        current_ms = self.media_processor.get_time()
        if self.is_scrubbing:
             current_ms = self.position_slider.value()
        self.current_time_label.setText(self._format_time(current_ms))
        self.total_time_label.setText(self._format_time(total_ms))
        
    def update_ui(self):
        """[FIX #12/32] update UI while respecting scrubbing and ensuring labels are current."""
        if not hasattr(self, 'media_processor') or not self.media_processor.media:
            return
        try:
            if not self.is_scrubbing:
                current_time = self.media_processor.get_time()
                if hasattr(self, 'position_slider') and self.position_slider and self.position_slider.isEnabled():
                    self.position_slider.setValue(current_time)
            self.update_time_labels()
        except Exception as e:
            self.logger.error(f"Error in update_ui: {e}")
           
    def get_title_info(self):
        return self.base_title

    def _format_time(self, millis):
        if millis < 0: millis = 0
        total_seconds = int(millis / 1000)
        minutes = total_seconds // 60
        seconds = total_seconds % 60
        return f"{minutes:02d}:{seconds:02d}"