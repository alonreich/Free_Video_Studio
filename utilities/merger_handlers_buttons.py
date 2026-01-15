from PyQt5.QtWidgets import QMessageBox

class MergerHandlersButtonsMixin:
    def update_button_states(self):
        n = self.parent.listw.count()
        is_processing = self.parent.is_processing
        selected_items = self.parent.listw.selectedItems()
        is_single_selection = len(selected_items) == 1
        self.parent.add_music_checkbox.setEnabled(not is_processing)
        self.parent.music_combo.setEnabled(not is_processing and self.parent.add_music_checkbox.isChecked())
        self.parent.music_offset_input.setEnabled(not is_processing and self.parent.add_music_checkbox.isChecked())
        self.parent.music_volume_slider.setEnabled(not is_processing and self.parent.add_music_checkbox.isChecked())
        self.parent.btn_merge.setEnabled(n >= 2 and not is_processing)
        self.parent.btn_remove.setEnabled(bool(selected_items) and not is_processing)
        self.parent.btn_clear.setEnabled(n > 0 and not is_processing)
        self.parent.btn_add.setEnabled(not is_processing and n < self.parent.MAX_FILES)
        if is_single_selection and not is_processing:
            current_row = self.parent.listw.row(selected_items[0])
            self.parent.btn_up.setEnabled(current_row > 0)
            self.parent.btn_down.setEnabled(current_row < n - 1)
        else:
            self.parent.btn_up.setEnabled(False)
            self.parent.btn_down.setEnabled(False)
        if is_processing:
            self.parent.status_label.setText("Processing merge... Please wait.")
        elif n == 0:
            self.parent.status_label.setText("Ready. Add 2 to 20 videos to begin.")
        elif n < 2:
            self.parent.status_label.setText(f"Waiting for more videos. Currently {n}/20.")
        else:
            self.parent.status_label.setText(f"Ready to merge {n} videos. Order is set.")