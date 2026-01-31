from PyQt5.QtWidgets import QFileDialog, QMessageBox, QListWidgetItem, QProgressDialog
from PyQt5.QtCore import Qt, QThread, pyqtSignal
from pathlib import Path
from utilities.merger_utils import _human
from utilities.merger_handlers_buttons import MergerHandlersButtonsMixin

class FileLoaderWorker(QThread):
    file_loaded = pyqtSignal(str, int)
    finished = pyqtSignal(int, int)

    def __init__(self, files, current_set, max_limit):
        super().__init__()
        self.files = files
        self.current_set = current_set
        self.max_limit = max_limit

    def run(self):
        added = 0
        duplicates = 0
        room = self.max_limit - len(self.current_set)
        for f in self.files:
            if added >= room:
                break
            if f in self.current_set:
                duplicates += 1
                continue
            try:
                sz = Path(f).stat().st_size
            except Exception as ex:
                sz = 0
            self.file_loaded.emit(f, sz)
            self.current_set.add(f)
            added += 1
        self.finished.emit(added, duplicates)

class MergerHandlersListMixin(MergerHandlersButtonsMixin):
    def on_merge_clicked(self):
        try:
            self.parent.btn_merge.setEnabled(False)
            self.parent.btn_back.setEnabled(False)
            self.parent.listw.setEnabled(False)
            self.parent.ui_handler._show_processing_overlay()
            self.parent.btn_merge.setText("Processing…")
        except Exception as ex:
            self.logger.debug(f"Error in on_merge_clicked: {ex}")
        self.parent.start_merge_processing()

    def add_videos(self):
        start_dir = self.parent._last_dir if self.parent._last_dir and Path(self.parent._last_dir).exists() else str(Path.home() / "Downloads")
        files, _ = QFileDialog.getOpenFileNames(
            self.parent, "Select videos to merge", start_dir,
            "Videos (*.mp4 *.mov *.mkv *.m4v *.ts *.avi *.webm);;All Files (*)"
        )
        if not files:
            return
        current_count = self.parent.listw.count()
        if current_count >= self.parent.MAX_FILES:
             QMessageBox.warning(self.parent, "Limit reached", f"Maximum {self.parent.MAX_FILES} files already added.")
             return
        current_files = {self.parent.listw.item(i).data(Qt.UserRole) for i in range(current_count)}
        self.parent.set_ui_busy(True)
        self.parent.status_label.setText("Loading files...")
        self._loader = FileLoaderWorker(files, current_files, self.parent.MAX_FILES)
        self._loader.file_loaded.connect(self._on_file_loaded)
        self._loader.finished.connect(self._on_loading_finished)
        self._loader.start()
        try:
            self.parent._last_dir = str(Path(files[0]).parent)
            self.parent.logic_handler.save_config()
        except Exception as ex:
            self.logger.debug(f"Failed to save last directory: {ex}")

    def _on_file_loaded(self, path, size):
        self.logger.info("ADD: %s | size=%s", path, _human(size))
        item = QListWidgetItem()
        item.setToolTip(path)
        item.setData(Qt.UserRole, path)
        w = self.make_item_widget(path)
        item.setSizeHint(w.sizeHint())
        self.parent.listw.addItem(item)
        self.parent.listw.setItemWidget(item, w)

    def _on_loading_finished(self, added, duplicates):
        self.parent.set_ui_busy(False)
        status_messages = []
        if added > 0:
            status_messages.append(f"Added {added} files")
        if duplicates > 0:
            status_messages.append(f"{duplicates} duplicates skipped")
        if status_messages:
            status_text = " | ".join(status_messages)
            self.parent.status_label.setText(status_text)
            self.parent.status_label.setStyleSheet("color: #ffa500; font-weight: bold;")

            from PyQt5.QtCore import QTimer
            QTimer.singleShot(3000, lambda: self._clear_status_if_no_processing())
        if self.parent.listw.count() >= self.parent.MAX_FILES:
            self.parent.status_label.setText(f"Maximum {self.parent.MAX_FILES} files reached")
            self.parent.status_label.setStyleSheet("color: #ff6b6b; font-weight: bold;")

            from PyQt5.QtCore import QTimer
            QTimer.singleShot(3000, lambda: self._clear_status_if_no_processing())
        self.parent.event_handler.update_button_states()

    def _clear_status_if_no_processing(self):
        """Clear status label if not currently processing."""
        if not self.parent.is_processing:
            self.parent.status_label.setText("Ready. Add 2 to 100 videos to begin.")
            self.parent.status_label.setStyleSheet("color: #7289da; font-weight: normal;")

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
        viewport = self.parent.listw.viewport()
        visible_rect = viewport.rect()
        viewport_top = visible_rect.top()
        item_h = 50
        try:
             rect = self.parent.listw.visualItemRect(self.parent.listw.item(0))
             if not rect.isNull(): item_h = rect.height()
        except Exception as ex:
            self.logger.debug(f"Failed to get item height: {ex}")
        rows_visible = max(1, visible_rect.height() // item_h)
        if self.parent.can_anim(row, new_row) and self.parent.start_swap_animation(row, new_row):
            return
        self.parent.perform_swap(row, new_row)
        scroll_bar = self.parent.listw.verticalScrollBar()
        scroll_pos = scroll_bar.value()
        first_visible_idx = scroll_pos // item_h
        last_visible_idx = first_visible_idx + rows_visible - 1
        if new_row < first_visible_idx + 4:
            target_idx = max(0, new_row - 3)
            scroll_bar.setValue(target_idx * item_h)
        elif new_row > last_visible_idx - 4:
            target_idx = new_row - rows_visible + 4
            max_scroll = max(0, self.parent.listw.count() * item_h - visible_rect.height())
            scroll_bar.setValue(min(target_idx * item_h, max_scroll))
        else:
            self.parent.listw.scrollToItem(self.parent.listw.item(new_row))

    def on_selection_changed(self):
        sel = self.parent.listw.selectedItems()
        if sel:
            row = self.parent.listw.row(sel[0])
            self._auto_scroll_to_keep_visible(row)
        for i in range(self.parent.listw.count()):
            item = self.parent.listw.item(i)
            widget = self.parent.listw.itemWidget(item)
            if widget:
                frame = getattr(widget, 'video_frame', None)
                if frame:
                    if item.isSelected():
                        frame.setStyleSheet("QFrame#videoItemFrame { background-color:#6a869a; border-radius:6px; border: 2px solid #2e8b57; }")
                    else:
                        frame.setStyleSheet("QFrame#videoItemFrame { background-color:#4a667a; border-radius:6px; border: none; }")
                else:
                    if item.isSelected():
                        widget.setStyleSheet("background-color:#6a869a; border-radius:6px; border: 2px solid #2e8b57;")
                    else:
                        widget.setStyleSheet("background-color:#4a667a; border-radius:6px; border: none;")

    def _auto_scroll_to_keep_visible(self, row):
        self.parent.listw.scrollToItem(self.parent.listw.item(row))

    def on_rows_moved(self, parent, start, end, destination, row):
        try:
            num_moved = end - start + 1
            for i in range(num_moved):
                item_to_update = self.parent.listw.item(row + i)
                if item_to_update and self.parent.listw.itemWidget(item_to_update):
                     item_to_update.setSizeHint(self.parent.listw.itemWidget(item_to_update).sizeHint())
            self.parent.listw.viewport().update()
        except Exception as ex:
            self.logger.debug(f"Error in on_rows_moved: {ex}")
