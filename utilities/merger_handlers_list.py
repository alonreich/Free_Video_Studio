from PyQt5.QtWidgets import QFileDialog, QMessageBox, QListWidgetItem, QMenu, QAction, QUndoStack, QUndoCommand, QApplication
from PyQt5.QtCore import Qt, QPoint, QUrl
from PyQt5.QtGui import QDesktopServices
from pathlib import Path
import os
import shutil
# Fix #22: Removed duplicate ProbeTask, use workers.py
from utilities.merger_utils import _human
from utilities.workers import FastFileLoaderWorker

class AddCommand(QUndoCommand):
    def __init__(self, parent, items_data, list_widget):
        super().__init__(f"Add {len(items_data)} videos")
        self.parent = parent
        self.items_data = items_data
        self.list_widget = list_widget

    def redo(self):
        for entry in self.items_data:
            self.parent.event_handler._add_single_item_internal(
                entry["path"],
                row=entry.get("row"),
                probe_data=entry.get("probe_data"),
                f_hash=entry.get("f_hash")
            )

    def undo(self):
        for entry in self.items_data:
            path = entry.get("path")
            for i in range(self.list_widget.count()):
                it = self.list_widget.item(i)
                if it.data(Qt.UserRole) == path:
                    self.list_widget.takeItem(i)
                    break

class RemoveCommand(QUndoCommand):
    def __init__(self, parent, items, list_widget):
        super().__init__(f"Remove {len(items)} videos")
        self.parent = parent
        self.items_data = []
        self.list_widget = list_widget
        # Sort by row descending to avoid index shift issues
        self.items_data = sorted(
            [(list_widget.row(it), it.data(Qt.UserRole), it.data(Qt.UserRole + 1), it.data(Qt.UserRole + 2)) for it in items], 
            key=lambda x: x[0], reverse=True
        )

    def redo(self):
        for row, path, _, _ in self.items_data:
            for i in range(self.list_widget.count()):
                it = self.list_widget.item(i)
                if it.data(Qt.UserRole) == path:
                    self.list_widget.takeItem(i)
                    break

    def undo(self):
        # Re-insert in reverse order (ascending row)
        for row, path, probe_data, f_hash in reversed(self.items_data):
            self.parent.event_handler._add_single_item_internal(path, row, probe_data, f_hash)

class MoveCommand(QUndoCommand):
    def __init__(self, parent, from_row, to_row):
        super().__init__(f"Move video {from_row + 1} -> {to_row + 1}")
        self.parent = parent
        self.from_row = from_row
        self.to_row = to_row

    def redo(self):
        self.parent.perform_move(self.from_row, self.to_row)

    def undo(self):
        self.parent.perform_move(self.to_row, self.from_row)

class ClearCommand(QUndoCommand):
    def __init__(self, parent, items_data, list_widget):
        super().__init__(f"Clear {len(items_data)} videos")
        self.parent = parent
        self.items_data = items_data
        self.list_widget = list_widget

    def redo(self):
        self.list_widget.clear()

    def undo(self):
        for entry in self.items_data:
            self.parent.event_handler._add_single_item_internal(
                entry["path"],
                row=entry.get("row"),
                probe_data=entry.get("probe_data"),
                f_hash=entry.get("f_hash")
            )

class MergerHandlersListMixin:
    def __init__(self):
        self.undo_stack = QUndoStack(self.parent)
        self._loading_lock = False

    def setup_list_connections(self):
        self.parent.listw.setContextMenuPolicy(Qt.CustomContextMenu)
        self.parent.listw.customContextMenuRequested.connect(self.show_context_menu)
        
        # Shortcuts setup
        from PyQt5.QtWidgets import QShortcut
        from PyQt5.QtGui import QKeySequence
        
        # Fix #29: Keyboard Shortcuts
        self.undo_shortcut = QShortcut(QKeySequence("Ctrl+Z"), self.parent)
        self.undo_shortcut.activated.connect(self.undo_stack.undo)
        
        self.redo_shortcut = QShortcut(QKeySequence("Ctrl+Y"), self.parent)
        self.redo_shortcut.activated.connect(self.undo_stack.redo)
        
        self.del_shortcut = QShortcut(QKeySequence("Delete"), self.parent)
        self.del_shortcut.activated.connect(self.remove_selected)
        
        self.sel_all_shortcut = QShortcut(QKeySequence("Ctrl+A"), self.parent)
        self.sel_all_shortcut.activated.connect(self.parent.listw.selectAll)
        
        self.esc_shortcut = QShortcut(QKeySequence("Esc"), self.parent)
        self.esc_shortcut.activated.connect(self.parent.listw.clearSelection)

    def show_context_menu(self, pos: QPoint):
        item = self.parent.listw.itemAt(pos)
        menu = QMenu(self.parent)
        
        add_action = QAction("Add Videos...", self.parent)
        add_action.triggered.connect(self.add_videos)
        menu.addAction(add_action)
        
        if item:
            if not item.isSelected():
                self.parent.listw.setCurrentItem(item)
                
            path = item.data(Qt.UserRole)
            
            # Fix #69: Open in external player
            open_action = QAction("Open in Default Player", self.parent)
            open_action.triggered.connect(lambda: QDesktopServices.openUrl(QUrl.fromLocalFile(path)))
            menu.addAction(open_action)
            
            # Fix #88: Duplicate Item
            dup_action = QAction("Duplicate Item", self.parent)
            dup_action.triggered.connect(lambda: self.duplicate_item(item))
            menu.addAction(dup_action)
            
            remove_action = QAction("Remove Selected", self.parent)
            remove_action.triggered.connect(self.remove_selected)
            menu.addAction(remove_action)
            
        menu.addSeparator()
        
        undo_action = self.undo_stack.createUndoAction(self.parent)
        redo_action = self.undo_stack.createRedoAction(self.parent)
        menu.addAction(undo_action)
        menu.addAction(redo_action)
        
        menu.exec_(self.parent.listw.mapToGlobal(pos))

    def duplicate_item(self, item):
        path = item.data(Qt.UserRole)
        probe_data = item.data(Qt.UserRole + 1)
        f_hash = item.data(Qt.UserRole + 2)
        row = self.parent.listw.row(item) + 1
        
        new_row = self._add_single_item_internal(path, row, probe_data, f_hash)
        
        # Add to undo stack
        entry = {
            "path": path,
            "row": new_row,
            "probe_data": probe_data,
            "f_hash": f_hash
        }
        self.undo_stack.push(AddCommand(self.parent, [entry], self.parent.listw))

    def on_merge_clicked(self):
        self.parent.start_merge_processing()

    def add_videos(self):
        if self._loading_lock: return
        
        start_dir = self.parent.logic_handler.get_last_dir()
        files, _ = QFileDialog.getOpenFileNames(
            self.parent, "Select videos to merge", start_dir,
            "Videos (*.mp4 *.mov *.mkv *.m4v *.ts *.avi *.webm);;All Files (*)"
        )
        if not files: return
        self._start_file_loader(files)

    # Fix #14: Add Folder
    def add_folder(self):
        if self._loading_lock: return
        start_dir = self.parent.logic_handler.get_last_dir()
        folder = QFileDialog.getExistingDirectory(self.parent, "Select Folder of Videos", start_dir)
        if not folder: return
        
        exts = {'.mp4', '.mov', '.mkv', '.m4v', '.ts', '.avi', '.webm'}
        files = []
        try:
            for p in Path(folder).iterdir():
                if p.is_file() and p.suffix.lower() in exts:
                    files.append(str(p))
        except Exception:
            pass
            
        if not files:
            QMessageBox.information(self.parent, "No Videos", "No video files found in that folder.")
            return
            
        self._start_file_loader(sorted(files))

    def add_videos_from_list(self, files):
        if self._loading_lock: return
        if not files: return
        self._start_file_loader(files)

    def _start_file_loader(self, files):
        current_count = self.parent.listw.count()
        if current_count + len(files) > self.parent.MAX_FILES:
             QMessageBox.warning(self.parent, "Limit reached", f"Cannot add {len(files)} files. Limit is {self.parent.MAX_FILES}.")
             return
             
        self._pending_undo_items = []
        self._loading_lock = True
        self.parent.set_ui_busy(True)
        self.parent.set_status_message("Loading files...", "color: #ffa500;", force=True)
        
        current_files = set()
        existing_hashes = set()
        
        # Optimization: Don't read all items if list is empty
        if current_count > 0:
             for i in range(current_count):
                it = self.parent.listw.item(i)
                current_files.add(it.data(Qt.UserRole))
                h = it.data(Qt.UserRole + 2)
                if h: existing_hashes.add(h)
                
        self._loader = FastFileLoaderWorker(files, current_files, existing_hashes, self.parent.MAX_FILES, self.parent.ffmpeg)
        self._loader.file_loaded.connect(self._on_file_loaded)
        self._loader.finished.connect(self._on_loading_finished)
        self._loader.start()
        
        if files:
            self.parent.logic_handler.set_last_dir(str(Path(files[0]).parent))
            # Fix #17: Immediate Config Save
            self.parent.logic_handler.save_config()

    def _on_file_loaded(self, path, size, probe_data, f_hash):
        row = self._add_single_item_internal(path, probe_data=probe_data, f_hash=f_hash)
        self._pending_undo_items.append({
            "path": path,
            "row": row,
            "probe_data": probe_data,
            "f_hash": f_hash
        })

    def _add_single_item_internal(self, path, row=None, probe_data=None, f_hash=None):
        item = QListWidgetItem()
        item.setToolTip(path)
        item.setData(Qt.UserRole, path)
        if probe_data:
            item.setData(Qt.UserRole + 1, probe_data)
        if f_hash:
            item.setData(Qt.UserRole + 2, f_hash)
            
        w = self.make_item_widget(path)
        # Apply metadata labels
        if probe_data:
            try:
                from utilities.merger_handlers_preview import _human_time
                dur = float(probe_data.get('format', {}).get('duration', 0))
                if dur > 0:
                    w.set_duration_label(_human_time(dur))
                streams = probe_data.get('streams', [])
                vid = next((s for s in streams if s.get('width')), None)
                if vid:
                    w.set_resolution_label(f"{vid['width']}x{vid['height']}")
            except: pass
            
        item.setSizeHint(w.sizeHint())
        
        if row is not None:
            self.parent.listw.insertItem(row, item)
            inserted_row = row
        else:
            self.parent.listw.addItem(item)
            inserted_row = self.parent.listw.count() - 1
            
        self.parent.listw.setItemWidget(item, w)
        return inserted_row

    def _on_loading_finished(self, added, duplicates):
        self._loading_lock = False
        self.parent.set_ui_busy(False)
        
        msg = []
        if added: msg.append(f"Added {added}")
        if duplicates: msg.append(f"Skipped {duplicates} dupe(s)")
        self.parent.set_status_message(" | ".join(msg) if msg else "Done", "color: #ffa500;", 3000, force=True)
        
        if getattr(self, "_pending_undo_items", None):
            self.undo_stack.push(AddCommand(self.parent, list(self._pending_undo_items), self.parent.listw))
            self._pending_undo_items = []
            
        self.parent.event_handler.update_button_states()
        self.parent.logic_handler.save_config() # Fix #17 again

    def remove_selected(self):
        selected = self.parent.listw.selectedItems()
        if not selected: return
        
        cmd = RemoveCommand(self.parent, selected, self.parent.listw)
        self.undo_stack.push(cmd)
        
        self.parent.event_handler.update_button_states()
        self.parent.set_status_message(f"Removed {len(selected)}", "color: #e74c3c;", 2000, force=True)
        self.parent.logic_handler.save_config()

    def clear_all(self):
        if self.parent.listw.count() == 0: return
        
        items_data = []
        for i in range(self.parent.listw.count()):
            it = self.parent.listw.item(i)
            items_data.append({
                "row": i,
                "path": it.data(Qt.UserRole),
                "probe_data": it.data(Qt.UserRole + 1),
                "f_hash": it.data(Qt.UserRole + 2)
            })
            
        self.undo_stack.push(ClearCommand(self.parent, items_data, self.parent.listw))
        self.parent.set_status_message("List cleared", "color: #e74c3c;", 2000, force=True)
        self.parent.logic_handler.save_config()

    def move_item(self, direction: int):
        sel = self.parent.listw.selectedItems()
        if not sel: return
        
        # Handle multi-selection move block
        rows = sorted([self.parent.listw.row(i) for i in sel])
        if not rows: return
        
        if direction < 0: # Up
            if rows[0] == 0: return
            for r in rows:
                self.undo_stack.push(MoveCommand(self.parent, r, r - 1))
        else: # Down
            if rows[-1] == self.parent.listw.count() - 1: return
            for r in reversed(rows):
                self.undo_stack.push(MoveCommand(self.parent, r, r + 1))

    def on_selection_changed(self):
        # Fix #18: Optimized selection update
        self.parent.event_handler.update_button_states()
        
    def on_drag_completed(self, start_row, end_row, path, tag):
        if start_row == end_row: return
        self.undo_stack.push(MoveCommand(self.parent, start_row, end_row))

    def on_rows_moved(self, sourceParent, sourceStart, sourceEnd, destinationParent, destinationRow):
        """
        Handle standard Qt model move signal.
        Since the move already happened in the view/model, pushing a standard MoveCommand
        (which tries to do the move again) would be complex to synchronize.
        For safety and stability (Fix #7), we clear the undo stack on manual drag reordering.
        """
        self.undo_stack.clear()
        self.parent.logic_handler.save_config()
        self.parent.event_handler.update_button_states()
