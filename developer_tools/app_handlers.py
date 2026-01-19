import os
import json
import logging
from PyQt5.QtWidgets import QApplication, QFileDialog, QMessageBox
from PyQt5.QtGui import QPixmap
from PyQt5.QtCore import QTimer
from portrait_window import PortraitWindow
from utils import cleanup_temp_snapshots
from config import CROP_APP_STYLESHEET, HUD_ELEMENT_MAPPINGS
from guidance_text import GUIDANCE_TEXT, HINT_TEXT

class CropAppHandlers:
    def connect_signals(self):
        self.play_pause_button.clicked.connect(self.play_pause)
        self.open_button.clicked.connect(self.open_file)
        self.snapshot_button.clicked.connect(self.take_snapshot)
        self.back_button.clicked.connect(self.show_video_view)
        self.reset_state_button.clicked.connect(self.reset_state)
        self.position_slider.sliderMoved.connect(self.set_position)
        if hasattr(self, 'draw_widget'):
            self.draw_widget.crop_role_selected.connect(self.handle_crop_completed)
        if hasattr(self, 'complete_button'):
            self.complete_button.clicked.connect(self.finish_and_save)

    def set_style(self):
        self.setStyleSheet(CROP_APP_STYLESHEET)

    def update_wizard_step(self, step_num, instruction):
        step_names = {
            1: "STEP 1: LOAD VIDEO",
            2: "STEP 2: TAKE SNAPSHOT",
            3: "STEP 3: CROP HUD ELEMENTS",
            4: "STEP 4: POSITION IN EDITOR",
            5: "STEP 5: COMPLETE"
        }
        self.step_label.setText(step_names.get(step_num, f"STEP {step_num}:"))
        self.instruction_label.setText(instruction)
        self.update_guidance(step_num)
        
    def update_guidance(self, step_num):
        if step_num in GUIDANCE_TEXT:
            guidance = GUIDANCE_TEXT[step_num]
            self.guidance_label.setText(guidance["title"])
            self.next_step_label.setText(guidance["instruction"])
        self.update_progress_tracker()
        if step_num == 3:
            next_element = self.get_next_element_to_configure()
            if next_element:
                QTimer.singleShot(100, lambda: self._show_element_hint(next_element))
                
    def _show_element_hint(self, element):
        if element in HINT_TEXT:
            if hasattr(self, 'hint_label'):
                self.hint_label.setText(HINT_TEXT[element])
                self.hint_label.setVisible(True)
        
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
            self.update_wizard_step(5, "All HUD elements configured!")
            self.show_completion_state()

    def show_completion_state(self):
        if hasattr(self, 'complete_button'):
            self.complete_button.setVisible(True)
        self.guidance_label.setText("All elements configured!")
        self.next_step_label.setText("Review your work in the portrait editor, then click Finish & Save")
        
    def finish_and_save(self):
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
        data["crops_1080p"][role] = [
            int(round(rect.x() * norm_factor)), int(round(rect.y() * norm_factor)),
            int(round(rect.width() * norm_factor)), int(round(rect.height() * norm_factor))
        ]
        with open(config_path, 'w') as f: json.dump(data, f, indent=4)

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
            self.portrait_window.add_scissored_item(pix, rect, self.background_crop_width)
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
        self.back_button.setVisible(False)
        self.draw_widget.clear_selection()
        self.draw_widget.setImage(None) 
        self.view_stack.setCurrentWidget(self.video_frame)
        self.update_wizard_step(1, "Open a video file to begin the configuration wizard.")
        if hasattr(self, 'complete_button'):
            self.complete_button.setVisible(False)
        self.update_progress_tracker()

    def play_pause(self):
        self.media_processor.play_pause()
        QTimer.singleShot(50, lambda: self.play_pause_button.setText("Pause" if self.media_processor.is_playing() else "Play"))

    def open_file(self):
        file_path, _ = QFileDialog.getOpenFileName(self, "Open Video", self.last_dir or "", "Video Files (*.mp4 *.avi *.mkv)")
        if file_path: self.load_file(file_path)

    def load_file(self, file_path):
        self.last_dir = os.path.dirname(file_path)
        self.media_processor.load_media(file_path, self.video_frame.winId())
        self.play_pause_button.setEnabled(True)
        self.snapshot_button.setEnabled(False)
        self.snapshot_button.setText("Loading...")
        self.position_slider.setEnabled(True)
        self.show_video_view()
        self.update_wizard_step(2, "Play/Pause to find a clear frame with visible HUD elements.")
        QTimer.singleShot(1000, lambda: self.snapshot_button.setEnabled(True))
        QTimer.singleShot(1000, lambda: self.snapshot_button.setText("2. TAKE SNAPSHOT"))
        self.update_progress_tracker()

    def take_snapshot(self):
        if self.media_processor.media_player.is_playing():
            self.media_processor.stop()
            self.play_pause_button.setText("Play")
        success, message = self.media_processor.take_snapshot(self.snapshot_path)
        if success: self._show_draw_view()
        else: QMessageBox.warning(self, "Snapshot Error", message)

    def _show_draw_view(self):
        snapshot_pixmap = QPixmap(self.snapshot_path)
        if snapshot_pixmap.isNull(): return
        try:
            if self.portrait_window is None:
                self.portrait_window = PortraitWindow(self.media_processor.original_resolution, self.config_path)
        except: pass
        target_aspect = 1080 / 1920
        img_aspect = snapshot_pixmap.width() / snapshot_pixmap.height()
        w = int(snapshot_pixmap.height() * target_aspect) if img_aspect > target_aspect else snapshot_pixmap.width()
        self.background_crop_width = w
        self.draw_widget.setImage(self.snapshot_path)
        self.draw_widget.set_roles(self.hud_elements, self._get_configured_roles())
        next_element = self.get_next_element_to_configure()
        if next_element:
            self.update_wizard_step(3, f"Draw a box around the {next_element}")
            self.guidance_label.setText(f"Draw box around {next_element}")
            self.next_step_label.setText("Click & drag to select, then choose element type from menu")
        else:
            self.update_wizard_step(3, "All elements configured! You can still add more if needed.")
            self.guidance_label.setText("All elements configured")
            self.next_step_label.setText("You can still draw additional elements if needed")
        self.update_progress_tracker()
        self.view_stack.setCurrentWidget(self.draw_widget)
        self.back_button.setVisible(True)

    def show_video_view(self):
        self.view_stack.setCurrentWidget(self.video_frame)
        self.back_button.setVisible(False)
        self.update_wizard_step(2, "Video View. Find a frame and take a snapshot.")

    def set_position(self, position):
        self.media_processor.set_position(position / 1000.0)

    def update_ui(self):
        if self.media_processor.media:
            self.position_slider.setValue(int(self.media_processor.get_position() * 1000))
           
    def get_title_info(self):
        return self.base_title