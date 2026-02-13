import os
from PyQt5.QtWidgets import QMessageBox, QStyle
from PyQt5.QtCore import Qt

class MergerMusicWizardNavigationMixin:

    def _on_page_changed(self, index):
        if not hasattr(self, 'btn_nav_next'): return
        if index in (1, 2):
            self.btn_play_video.setVisible(True)
            self.btn_play_video.setText("  PLAY")
            self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        else:
            self.btn_play_video.setVisible(False)
        if index == 2:
            self.btn_nav_next.setText("✓  DONE")
            if self.width() < 1500:
                self._resize_from_center(1600, 800)
            self._bind_video_output()
            self._prepare_timeline_data()
        elif index in (0, 1):
            self.btn_nav_next.setText("NEXT")
            if self.width() > 1500:
                self._resize_from_center(1300, 650)
        if index == 0:
            self.update_coverage_ui()
        self._sync_caret()
    def _resize_from_center(self, w, h):
        old_center = self.geometry().center()
        self.resize(w, h)
        new_rect = self.geometry()
        new_rect.moveCenter(old_center)
        self.setGeometry(new_rect)
    def _on_nav_next_clicked(self):
        idx = self.stack.currentIndex()
        self.logger.info(f"WIZARD: User clicked NEXT on step {idx + 1}")
        if idx == 0:
            self.go_to_offset_step()
        elif idx == 1:
            self.confirm_current_track()
        elif idx == 2:
            self.logger.info("WIZARD: User clicked DONE on Timeline. Finishing.")
            self.stop_previews()
            self.accept()
    def _on_nav_back_clicked(self):
        idx = self.stack.currentIndex()
        if idx > 0:
            if idx == 1:
                self.btn_back.hide()
            self.stack.setCurrentIndex(idx - 1)
            self.stop_previews()
    def go_to_offset_step(self):
        item = self.track_list.currentItem()
        if not item: 
            QMessageBox.warning(self, "No Selection", "Please click on a song first!")
            return
        self.current_track_path = item.data(Qt.UserRole)
        if not self.current_track_path: return
        self.logger.info(f"WIZARD: User selected song: {os.path.basename(self.current_track_path)}")
        self._last_good_vlc_ms = 0
        self.offset_slider.blockSignals(True)
        self.offset_slider.setValue(0)
        self.offset_slider.blockSignals(False)
        self._sync_caret()
        self.stack.setCurrentIndex(1)
        self.start_waveform_generation()
        self.btn_back.show()
    def confirm_current_track(self):
        """Records the current track's offset selection and checks coverage."""
        if self._player: self._player.stop()
        offset = self.offset_slider.value() / 1000.0
        actual_dur = self.current_track_dur - offset
        self.logger.info(f"WIZARD: User confirmed track '{os.path.basename(self.current_track_path)}' starting at {offset:.1f}s (Covers {actual_dur:.1f}s)")
        self.selected_tracks.append((self.current_track_path, offset, actual_dur))
        self.update_coverage_ui()
        covered = sum(t[2] for t in self.selected_tracks)
        if covered < self.total_video_sec - 0.5:
            self.logger.info(f"WIZARD: More music needed ({covered:.1f}s / {self.total_video_sec:.1f}s). Returning to Step 1.")
            QMessageBox.information(self, "Need more music", 
                f"You've covered {covered:.1f}s of your {self.total_video_sec:.1f}s project.\n\n"
                "Please select another song to fill the remaining time!")
            self.stack.setCurrentIndex(0)
            self.btn_back.hide()
        else:
            self.logger.info("WIZARD: Coverage complete. Moving to Step 3 Timeline.")
            self.stack.setCurrentIndex(2)
