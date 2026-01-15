from PyQt5.QtWidgets import QFileDialog, QMessageBox, QListWidgetItem
from PyQt5.QtCore import Qt
from pathlib import Path
from utilities.merger_utils import _human
from utilities.merger_handlers_buttons import MergerHandlersButtonsMixin

class MergerHandlersListMixin(MergerHandlersButtonsMixin):
    def on_merge_clicked(self):
        try:
            self.parent.btn_merge.setEnabled(False)
            self.parent.btn_back.setEnabled(False)
            self.parent.listw.setEnabled(False)
            self.parent.ui_handler._show_processing_overlay()
            self.parent.btn_merge.setText("Processing…")
        except Exception:
            pass
        self.parent.start_merge_processing()

    def add_videos(self):
        start_dir = self.parent._last_dir if self.parent._last_dir and Path(self.parent._last_dir).exists() else str(Path.home() / "Downloads")
        files, _ = QFileDialog.getOpenFileNames(
            self.parent, "Select videos to merge", start_dir,
            "Videos (*.mp4 *.mov *.mkv *.m4v *.ts *.avi *.webm);;All Files (*)"
        )
        if not files:
            return
        current = self.parent.listw.count()
        room = max(0, self.parent.MAX_FILES - current)
        if room <= 0:
            QMessageBox.warning(self.parent, "Limit reached", f"Maximum {self.parent.MAX_FILES} files already added.")
            return
        current_files = {self.parent.listw.item(i).data(Qt.UserRole) for i in range(current)}
        new_files = [f for f in files if f not in current_files]
        if new_files:
            try:
                self.parent._last_dir = str(Path(new_files[0]).parent)
                self.parent.logic_handler.save_config()
            except Exception:
                pass
        for f in new_files[:room]:
            try:
                sz = Path(f).stat().st_size
                self.logger.info("ADD: %s | size=%s | dir=%s", f, _human(sz), Path(f).parent)
            except Exception:
                pass
            item = QListWidgetItem()
            item.setToolTip(f)
            item.setData(Qt.UserRole, f)
            w = self.make_item_widget(f)
            item.setSizeHint(w.sizeHint())
            self.parent.listw.addItem(item)
            self.parent.listw.setItemWidget(item, w)
            item.setSizeHint(w.sizeHint())
        if len(files) > len(new_files):
            QMessageBox.warning(self.parent, "Duplicates", "Some selected files were already in the list and were ignored.")
        if len(new_files) > room:
            QMessageBox.information(self.parent, "Limit", f"Only {room} unique file(s) were added (max {self.parent.MAX_FILES} total).")

    def remove_selected(self):
        for it in self.parent.listw.selectedItems():
            self.parent.listw.takeItem(self.parent.listw.row(it))

    def move_item(self, direction: int):
        sel = self.parent.listw.selectedItems()
        if not sel:
            return
        row = self.parent.listw.row(sel[0])
        new_row = row + direction
        if new_row < 0 or new_row >= self.parent.listw.count():
            return
        if self.parent.can_anim(row, new_row) and self.parent.start_swap_animation(row, new_row):
            return
        self.parent.perform_swap(row, new_row)

    def on_selection_changed(self):
        for i in range(self.parent.listw.count()):
            item = self.parent.listw.item(i)
            widget = self.parent.listw.itemWidget(item)
            if widget:
                if item.isSelected():
                    widget.setStyleSheet("background-color:#6a869a; border-radius:6px;")
                else:
                    widget.setStyleSheet("background-color:#4a667a; border-radius:6px;")

    def on_rows_moved(self, parent, start, end, destination, row):
        try:
            num_moved = end - start + 1
            for i in range(num_moved):
                item_to_update = self.parent.listw.item(row + i)
                if item_to_update is None:
                    continue
                widget = self.parent.listw.itemWidget(item_to_update)
                if widget is None:
                    continue
                item_to_update.setSizeHint(widget.sizeHint())
            self.parent.listw.viewport().update()
        except Exception as e:
            self.logger.error("LISTW: Failed to re-apply sizeHint after move: %s", e)