import os
import time
from PyQt5.QtWidgets import QApplication, QFileDialog, QMessageBox, QPushButton, QStyle
from PyQt5.QtGui import QPixmap, QColor
from PyQt5.QtCore import QTimer, QThread, Qt, QObject, pyqtSignal
from utils import cleanup_temp_snapshots, get_snapshot_dir
from config import (
    CROP_APP_STYLESHEET,
    HUD_ELEMENT_MAPPINGS,
    UI_BEHAVIOR,
    WizardState,
    get_stylesheet,
    get_tech_key_from_role,
)

from enhanced_logger import get_enhanced_logger
from magic_wand import MagicWand, MagicWandWorker
from system.utils import ProcessManager
from graphics_items import ResizablePixmapItem

class SnapshotWorker(QObject):
    finished = pyqtSignal(bool, str)

    def __init__(self, media_processor, path, time_val):
        super().__init__()
        self.media_processor = media_processor
        self.path = path
        self.time_val = time_val

    def run(self):
        try:
            success, message = self.media_processor.take_snapshot(self.path, self.time_val)
        except Exception as e:
            success, message = False, str(e)
        self.finished.emit(success, message)

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
        """Cooperative Magic Wand cleanup without force-killing threads."""
        if hasattr(self, '_magic_wand_timeout_timer') and self._magic_wand_timeout_timer:
            self._magic_wand_timeout_timer.stop()
        if hasattr(self, '_analyzing_timer') and self._analyzing_timer:
            self._analyzing_timer.stop()
        thread = getattr(self, 'wand_thread', None)
        if thread:
            if thread.isRunning():
                worker = getattr(self, 'wand_worker', None)
                if worker and hasattr(worker, 'cancel'):
                    try:
                        worker.cancel()
                    except RuntimeError:
                        pass
                thread.quit()
                if not thread.wait(1500):
                    self.logger.error("Magic Wand thread did not stop in time; forcing termination")
                    try:
                        thread.terminate()
                        thread.wait(500)
                    except:
                        pass
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
        style = CROP_APP_STYLESHEET or ""
        theme_qss = get_stylesheet()
        if theme_qss:
            style = f"{style}\n{theme_qss}" if style else theme_qss
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
        """Fallback path for VLC-missing environments: load a local screenshot safely."""
        image_path, _ = QFileDialog.getOpenFileName(
            self,
            "Open Screenshot",
            self.last_dir or "",
            "Image Files (*.png *.jpg *.jpeg *.bmp *.webp)"
        )
        if not image_path:
            return
        if not os.path.isfile(image_path):
            QMessageBox.warning(self, "Invalid Image", "Selected image does not exist.")
            return
        ext = os.path.splitext(image_path)[1].lower()
        if ext not in {'.png', '.jpg', '.jpeg', '.bmp', '.webp'}:
            QMessageBox.warning(self, "Unsupported Image", f"Unsupported image type: {ext or 'unknown'}")
            return
        pix = QPixmap(image_path)
        if pix.isNull():
            QMessageBox.warning(self, "Image Load Error", "Could not decode this image file.")
            return
        self.last_dir = os.path.dirname(image_path)
        self._delete_managed_snapshot()
        fallback_snapshot_path = os.path.join(get_snapshot_dir(), "fallback_snapshot.png")
        try:
            if os.path.exists(fallback_snapshot_path):
                os.unlink(fallback_snapshot_path)
        except OSError as unlink_err:
            self.logger.warning(f"Could not remove previous fallback snapshot: {unlink_err}")
        if not pix.save(fallback_snapshot_path, "PNG"):
            QMessageBox.warning(self, "Image Save Error", "Could not prepare a temporary working copy of this image.")
            return
        self.snapshot_path = fallback_snapshot_path
        self._snapshot_owned_by_app = True
        self.snapshot_resolution = f"{pix.width()}x{pix.height()}"
        try:
            if hasattr(self, 'media_processor') and self.media_processor:
                self.media_processor.stop()
                self.media_processor.set_media_to_null()
        except Exception:
            pass
        self._set_upload_hint_active(False)
        self.set_background_image(pix)
        self.view_stack.setCurrentWidget(self.draw_scroll_area)
        QApplication.processEvents()
        self.draw_widget.setImage(self.snapshot_path)
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        next_element = self.get_next_element_to_configure()
        self.update_wizard_step(3, f"Draw a box around the {next_element}" if next_element else "Refine your selection")
        self.snapshot_button.setVisible(False)
        if hasattr(self, 'back_to_video_button'):
            self.back_to_video_button.setVisible(False)
        if hasattr(self, 'magic_wand_button'):
            self.magic_wand_button.setVisible(True)
        if hasattr(self, 'slider_container'):
            self.slider_container.setVisible(False)
        self.draw_widget.setFocus()

    def update_wizard_step(self, step_num, instruction):
        """[FIX #17, #22, #5] Short, punchy status labels with Goal/Status split."""
        self.current_step = step_num
        if not self._is_wand_thread_running():
            clamped_step = max(1, min(step_num, 5))
            self.progress_bar.setValue(clamped_step * 20)
            self.progress_bar.setVisible(True)
        goal_map = {
            1: WizardState.UPLOAD.value,
            2: WizardState.FIND_HUD.value,
            3: WizardState.REFINE.value,
            4: WizardState.COMPOSER.value,
            5: WizardState.READY.value,
        }
        punchy_map = {
            "Open a video file to begin the configuration wizard.": "UPLOAD VIDEO",
            "Finding clear frame with HUD elements...": "ANALYZING...",
            "Video View. Find a frame and take a snapshot.": "SEEK FRAME",
            "Capturing snapshot...": "CAPTURING...",
            "Now, please refine selection.\n(Use the small arrows to adjust the HUD shape)": "REFINE BOX",
            "All elements configured! You can still add more if needed.": "CONFIG READY"
        }
        self.goal_label.setText(f"Goal: {goal_map.get(step_num, 'Configure HUD')}")
        short_instr = punchy_map.get(instruction, instruction)
        self.status_label.setText(short_instr)
        self.update_progress_tracker()
        
    def _on_video_info_ready(self, resolution):
        """[FIX #2, #3, #4] Callback for background resolution detection."""
        if resolution == "UNKNOWN":
            self.logger.warning("Resolution detection failed, prompting user.")

            from PyQt5.QtWidgets import QInputDialog
            res, ok = QInputDialog.getText(
                self, "Manual Resolution Required",
                "Automated detection failed. Please enter resolution (e.g. 1920x1080):",
                text="1920x1080"
            )
            if ok and "x" in res:
                self.media_processor.original_resolution = res
                resolution = res
            else:
                QMessageBox.critical(self, "Error", "A valid resolution is required to continue.")
                self.reset_state(force=True)
                return
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
            self.status_label.setText("Frame ready")
            self.goal_label.setText("Goal: Find HUD Frame")

    def update_progress_tracker(self):
        """[FIX #5] Clean progress tracker without redundant checks."""
        if not hasattr(self, 'progress_labels'):
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

        from config import HUD_SAFE_PADDING
        tk = get_tech_key_from_role(role)
        padding = HUD_SAFE_PADDING.get(tk, {})
        if "left" in padding:
            process_rect.setLeft(process_rect.left() + padding["left"])
        if "right" in padding:
            process_rect.setRight(process_rect.right() + padding["right"])
        if hasattr(self.draw_widget, 'pixmap') and not self.draw_widget.pixmap.isNull():
            pix = self.draw_widget.pixmap.copy(process_rect.intersected(self.draw_widget.pixmap.rect()))
        rect = process_rect
        tech_key = get_tech_key_from_role(role)
        if tech_key == "unknown":
            self.logger.error(f"Cannot map role '{role}' to a technical key")
            QMessageBox.warning(self, "Unknown HUD Role", f"Cannot save selection for unknown role: {role}")
            return
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
             self.magic_wand_button.setEnabled(False)
             self.magic_wand_button.setToolTip("Feature disabled: Reference templates ('anchors' folder) are missing.")
             QMessageBox.warning(self, "Magic Wand Unavailable", 
                                f"Magic Wand cannot function because the 'anchors' directory is missing.\n\n"
                                f"Required path: {anchor_dir}\n\n"
                                "Please ensure this folder exists with the necessary reference images.")
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
                worker = getattr(self, 'wand_worker', None)
                if worker and hasattr(worker, 'cancel'):
                    worker.cancel()
                self.wand_thread.requestInterruption()
                self.wand_thread.quit()
                if not self.wand_thread.wait(2000):
                    self.logger.error("Magic Wand timeout cleanup: thread did not stop within timeout, forcing.")
                    self.wand_thread.terminate()
                    self.wand_thread.wait(500)
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
        """[FIX #4, #5] Improved feedback on Magic Wand failure."""
        if magic_id != getattr(self, '_magic_wand_active_id', None):
            return
        if getattr(self, '_magic_wand_cancelled', False):
            return
        self._cleanup_magic_wand_runtime()
        if regions:
            self._magic_wand_candidates = regions
            self.logger.info(f"Magic Wand found {len(regions)} regions.")
            self.draw_widget.set_candidates(regions)
            self.update_wizard_step(3, f"Detected {len(regions)} elements! Click one to tag.")
        else:
            self.logger.warning("Magic Wand found no elements.")
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Information)
            msg.setWindowTitle("Magic Wand Result")
            msg.setText("No HUD elements detected automatically.\n\n"
                       "Tips:\n"
                       "- Try a frame with higher contrast\n"
                       "- Ensure HUD is fully visible\n\n"
                       "You can now draw boxes manually.")
            for btn in msg.findChildren(QPushButton):
                btn.setCursor(Qt.PointingHandCursor)
            msg.exec_()
            self._magic_wand_candidates = None
            self.update_wizard_step(3, "Please draw boxes manually around HUD elements.")
            self.draw_widget.setFocus()

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
            self._delete_managed_snapshot()
            cleanup_temp_snapshots()
            ProcessManager.cleanup_temp_files()
        except Exception as e:
            self.logger.error(f"Error during state reset: {e}")
        for attr in ['_magic_wand_preview_timer', '_magic_wand_timeout_timer', '_analyzing_timer', '_scrubbing_safety_timer']:
            timer = getattr(self, attr, None)
            if timer: 
                try:
                    timer.stop()
                except RuntimeError as timer_err:
                    self.logger.debug(f"Timer stop skipped for {attr}: {timer_err}")
        self._cleanup_snapshot_runtime()
        if hasattr(self, '_set_cropping_hint_active'):
            self._set_cropping_hint_active(False)
        if self._is_wand_thread_running():
            try:
                worker = getattr(self, 'wand_worker', None)
                if worker and hasattr(worker, 'cancel'):
                    worker.cancel()
                self.wand_thread.requestInterruption()
                self.wand_thread.quit()
                if not self.wand_thread.wait(1500):
                    self.logger.warning("Magic Wand thread still running during reset, forcing.")
                    self.wand_thread.terminate()
                    self.wand_thread.wait(500)
            except RuntimeError as thread_err:
                self.logger.debug(f"Magic Wand thread cleanup skipped: {thread_err}")
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
            self.play_pause_button.setText("  PAUSE")
            self.play_pause_button.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
        else:
            self.play_pause_button.setText("  PLAY")
            self.play_pause_button.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
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
        if not self.media_processor.vlc_instance:
             if hasattr(self, 'open_image_fallback'):
                 self.open_image_fallback()
             return
        self.timer.stop()
        self._set_upload_hint_active(False)
        file_path, _ = QFileDialog.getOpenFileName(
            self,
            "Open Video",
            self.last_dir or "",
            "Video Files (*.mp4 *.avi *.mkv *.mov *.webm *.m4v)"
        )
        if file_path:
            self.load_file(file_path)
        else:
            if not self.media_processor.media:
                self._set_upload_hint_active(True)
            self.timer.start()

    def _cleanup_snapshot_runtime(self):
        thread = getattr(self, '_snapshot_thread', None)
        if thread and thread.isRunning():
            thread.quit()
            if not thread.wait(1500):
                self.logger.warning("Snapshot thread did not stop in time")
        self._snapshot_worker = None
        self._snapshot_thread = None

    def _is_managed_snapshot_path(self, path):
        if not path:
            return False
        try:
            snapshot_dir = os.path.abspath(get_snapshot_dir())
            abs_path = os.path.abspath(path)
            return os.path.commonpath([snapshot_dir, abs_path]) == snapshot_dir
        except (ValueError, OSError):
            return False

    def _delete_managed_snapshot(self):
        snapshot_path = getattr(self, 'snapshot_path', None)
        if not snapshot_path:
            return
        is_managed = bool(getattr(self, '_snapshot_owned_by_app', False)) or self._is_managed_snapshot_path(snapshot_path)
        if is_managed and os.path.exists(snapshot_path):
            try:
                os.unlink(snapshot_path)
            except OSError as unlink_err:
                self.logger.warning(f"Failed to delete managed snapshot: {unlink_err}")
        self.snapshot_path = None
        self._snapshot_owned_by_app = False

    def load_file(self, file_path):
        """[FIX #12] Defer slider and status updates until media length is confirmed."""
        if not file_path or not os.path.isfile(file_path):
            QMessageBox.warning(self, "Invalid File", "Selected file does not exist or is not a regular file.")
            self._set_upload_hint_active(True)
            self.timer.start()
            return
        allowed_ext = {'.mp4', '.avi', '.mkv', '.mov', '.webm', '.m4v'}
        ext = os.path.splitext(file_path)[1].lower()
        if ext not in allowed_ext:
            QMessageBox.warning(self, "Unsupported Format", f"Unsupported file type: {ext or 'unknown'}. Please select a video file.")
            self._set_upload_hint_active(True)
            self.timer.start()
            return
        self.last_dir = os.path.dirname(file_path)
        if hasattr(self, 'save_geometry'):
            self.save_geometry()
        QApplication.processEvents()
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
        if hasattr(self, 'slider_container') and self.slider_container:
            self.slider_container.show()
        if hasattr(self, 'play_pause_button') and self.play_pause_button:
            self.play_pause_button.show()
        if hasattr(self, 'snapshot_button') and self.snapshot_button:
            self.snapshot_button.show()
            self.snapshot_button.setText("START CROPPING")
            self.snapshot_button.setEnabled(True)
        if hasattr(self, 'reset_state_button') and self.reset_state_button:
            self.reset_state_button.show()
        self.play_pause_button.setEnabled(True)
        self.snapshot_button.setEnabled(False)
        self.snapshot_button.setText("Loading...")
        self.position_slider.setEnabled(False)
        self.show_video_view()
        self.update_wizard_step(2, "Finding clear frame with HUD elements...")
        if hasattr(self, '_set_cropping_hint_active'):
            self._set_cropping_hint_active(True)
        QTimer.singleShot(200, self._sync_play_pause_button)
        self.update_progress_tracker()
        self.timer.start()

    def take_snapshot(self):
        if not self.media_processor.original_resolution and self.media_processor.media_player:
             w, h = self.media_processor.media_player.video_get_size(0)
             if w > 0 and h > 0:
                 self.media_processor.original_resolution = f"{w}x{h}"
        max_resolution_wait_attempts = 12
        if not hasattr(self, '_snapshot_wait_attempts'):
            self._snapshot_wait_attempts = 0
        if not self.media_processor.original_resolution:
             self._snapshot_wait_attempts += 1
             if hasattr(self, 'status_label'):
                 self.status_label.setText("WAITING FOR VIDEO...")
             if self._snapshot_wait_attempts > max_resolution_wait_attempts:
                 self.logger.error("Timed out waiting for video resolution before snapshot")
                 QMessageBox.warning(self, "Snapshot", "Timed out waiting for video metadata. Try reloading the video.")
                 self._snapshot_wait_attempts = 0
                 self._reset_snapshot_ui()
                 return
             QTimer.singleShot(1000, self.take_snapshot)
             return
        self._snapshot_wait_attempts = 0
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
            if hasattr(self, '_set_cropping_hint_active'):
                self._set_cropping_hint_active(False)
            if hasattr(self, 'slider_container'):
                self.slider_container.setVisible(False)
            was_playing = self.media_processor.is_playing()
            if was_playing:
                self.play_pause()
            self._delete_managed_snapshot()
            self.snapshot_path = os.path.join(get_snapshot_dir(), "last_snapshot.png")
            self._snapshot_owned_by_app = True
            preferred_time = self.position_slider.value() / 1000.0
            QTimer.singleShot(50, lambda: self._execute_snapshot_capture(self.snapshot_path, preferred_time))
        except Exception as e:
            self.logger.critical(f"CRITICAL: Crash in take_snapshot: {e}", exc_info=True)
            self._reset_snapshot_ui()

    def _execute_snapshot_capture(self, path, time_val):
        """Run snapshot capture off the GUI thread and return via signal."""
        if getattr(self, '_snapshot_thread', None) and self._snapshot_thread.isRunning():
            return
        self._snapshot_thread = QThread(self)
        self._snapshot_worker = SnapshotWorker(self.media_processor, path, time_val)
        self._snapshot_worker.moveToThread(self._snapshot_thread)
        self._snapshot_thread.started.connect(self._snapshot_worker.run)
        self._snapshot_worker.finished.connect(self._on_snapshot_capture_result)
        self._snapshot_worker.finished.connect(self._snapshot_thread.quit)
        self._snapshot_worker.finished.connect(self._snapshot_worker.deleteLater)
        self._snapshot_thread.finished.connect(self._on_snapshot_thread_finished)
        self._snapshot_thread.finished.connect(self._snapshot_thread.deleteLater)
        self._snapshot_thread.start()

    def _on_snapshot_capture_result(self, success, message):
        """[FIX #7, #5] Direct signal transition for snapshots, removing polling jitter."""
        self._snapshot_worker = None
        if success:
            if os.path.exists(self.snapshot_path) and os.path.getsize(self.snapshot_path) > 100:
                self._show_draw_view()
            else:
                self.logger.error(f"Snapshot file error after success signal: {self.snapshot_path}")
                self.status_label.setText("Snapshot file verification failed.")
                self._reset_snapshot_ui()
            return
        self.status_label.setText(f"Snapshot Error: {message}")
        self._reset_snapshot_ui()

    def _on_snapshot_thread_finished(self):
        self._snapshot_thread = None

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
            self.snapshot_resolution = f"{snapshot_pixmap.width()}x{snapshot_pixmap.height()}"
            self.set_background_image(snapshot_pixmap)
            self.view_stack.setCurrentWidget(self.draw_scroll_area)
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
        if hasattr(self, '_set_cropping_hint_active'):
            self._set_cropping_hint_active(True)

    def set_position(self, position_ms):
        if not hasattr(self, 'media_processor') or not self.media_processor or not self.media_processor.media_player:
            return
        total = self.media_processor.media_player.get_length()
        if total > 0:
            clamped_ms = max(0, min(int(position_ms), int(total)))
            normalized = max(0.0, min(1.0, clamped_ms / float(total)))
            self.media_processor.set_position(normalized)
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
            if hasattr(self, 'hint_overlay_widget') and self.hint_overlay_widget.isVisible():
                self.hint_overlay_widget.raise_()
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
