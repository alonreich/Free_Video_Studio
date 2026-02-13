from PyQt5.QtWidgets import QFileDialog, QMessageBox, QListWidgetItem, QMenu, QAction, QUndoStack, QUndoCommand
from PyQt5.QtCore import Qt, QPoint, QUrl
from PyQt5.QtGui import QDesktopServices
from pathlib import Path
import os
import uuid
from utilities.workers import FastFileLoaderWorker, FolderScanWorker

def _natural_key(text: str):
    import re
    import locale
    return [int(c) if c.isdigit() else locale.strxfrm(c.lower()) for c in re.split(r"(\d+)", text)]

def _human_time(seconds: float) -> str:
    total = max(0, int(round(float(seconds or 0.0))))
    return f"{total//3600:02}:{(total%3600)//60:02}:{total%60:02}"

class ReorderCommand(QUndoCommand):
    def __init__(self, parent, before_entries, after_entries):
        super().__init__("Reorder videos")
        self.parent = parent
        self.before_entries = list(before_entries)
        self.after_entries = list(after_entries)

    def _apply_order(self, entries):
        listw = self.parent.listw
        selected_ids = {
            listw.item(i).data(Qt.UserRole + 3)
            for i in range(listw.count())
            if listw.item(i).isSelected()
        }
        data_by_id = {}
        for i in range(listw.count()):
            it = listw.item(i)
            clip_id = it.data(Qt.UserRole + 3)
            p = it.data(Qt.UserRole)
            data_by_id[clip_id] = {
                "clip_id": clip_id,
                "path": p,
                "probe_data": it.data(Qt.UserRole + 1),
                "f_hash": it.data(Qt.UserRole + 2),
            }
        self.parent.event_handler._is_replaying_undo = True
        model = listw.model()
        model.blockSignals(True)
        try:
            listw.clear()
            for row, snap in enumerate(entries):
                entry = data_by_id.get(snap.get("clip_id"))
                if not entry:
                    continue
                self.parent.event_handler._add_single_item_internal(
                    entry.get("path"),
                    row=row,
                    probe_data=entry.get("probe_data"),
                    f_hash=entry.get("f_hash"),
                    clip_id=entry.get("clip_id"),
                    refresh=False
                )
            self.parent.event_handler.refresh_ranks()
            if selected_ids:
                listw.clearSelection()
                first_selected = None
                for i in range(listw.count()):
                    it = listw.item(i)
                    if it.data(Qt.UserRole + 3) in selected_ids:
                        it.setSelected(True)
                        if first_selected is None:
                            first_selected = it
                if first_selected is not None:
                    listw.setCurrentItem(first_selected)
        finally:
            model.blockSignals(False)
            self.parent.event_handler._is_replaying_undo = False
            try:
                self.parent.event_handler.update_button_states()
            except RuntimeError:
                pass

    def redo(self):
        self._apply_order(self.after_entries)

    def undo(self):
        self._apply_order(self.before_entries)

class AddCommand(QUndoCommand):
    def __init__(self, parent, items_data, list_widget):
        super().__init__(f"Add {len(items_data)} videos")
        self.parent = parent
        self.items_data = items_data
        self.list_widget = list_widget

    def redo(self):
        for entry in self.items_data:
            path = entry["path"]
            self.parent.event_handler._add_single_item_internal(
                path,
                row=entry.get("row"),
                probe_data=entry.get("probe_data"),
                f_hash=entry.get("f_hash"),
                clip_id=entry.get("clip_id"),
                refresh=False
            )
            self.parent.logger.info(f"LIST: Added file '{os.path.basename(path)}' at row {entry.get('row')}")
        self.parent.event_handler.refresh_ranks()

    def undo(self):
        for entry in self.items_data:
            clip_id = entry.get("clip_id")
            for i in range(self.list_widget.count()):
                it = self.list_widget.item(i)
                if it.data(Qt.UserRole + 3) == clip_id:
                    self.list_widget.takeItem(i)
                    self.parent.logger.info(f"UNDO: Removed file '{os.path.basename(entry.get('path'))}'")
                    break
        self.parent.event_handler.refresh_ranks()

class RemoveCommand(QUndoCommand):
    def __init__(self, parent, items, list_widget):
        super().__init__(f"Remove {len(items)} videos")
        self.parent = parent
        self.items_data = []
        self.list_widget = list_widget
        self.items_data = sorted(
            [(list_widget.row(it), it.data(Qt.UserRole), it.data(Qt.UserRole + 1), it.data(Qt.UserRole + 2), it.data(Qt.UserRole + 3)) for it in items], 
            key=lambda x: x[0], reverse=True
        )

    def redo(self):
        for row, path, _, _, clip_id in self.items_data:
            for i in range(self.list_widget.count()):
                it = self.list_widget.item(i)
                if it.data(Qt.UserRole + 3) == clip_id:
                    self.list_widget.takeItem(i)
                    self.parent.logger.info(f"LIST: Removed file '{os.path.basename(path)}' from row {row}")
                    break
        self.parent.event_handler.refresh_ranks()

    def undo(self):
        for row, path, probe_data, f_hash, clip_id in reversed(self.items_data):
            self.parent.event_handler._add_single_item_internal(path, row, probe_data, f_hash, clip_id=clip_id, refresh=False)
            self.parent.logger.info(f"UNDO: Restored file '{os.path.basename(path)}' to row {row}")
        self.parent.event_handler.refresh_ranks()

class BatchMoveCommand(QUndoCommand):
    def __init__(self, parent, before_entries, after_entries):
        super().__init__("Move selected videos")
        self.parent = parent
        self.before_entries = list(before_entries)
        self.after_entries = list(after_entries)

    def _apply(self, entries):
        listw = self.parent.listw
        selected_ids = {
            listw.item(i).data(Qt.UserRole + 3)
            for i in range(listw.count())
            if listw.item(i).isSelected()
        }
        model = listw.model()
        model.blockSignals(True)
        self.parent.event_handler._is_replaying_undo = True
        try:
            by_id = {}
            for i in range(listw.count()):
                it = listw.item(i)
                cid = it.data(Qt.UserRole + 3)
                by_id[cid] = {
                    "clip_id": cid,
                    "path": it.data(Qt.UserRole),
                    "probe_data": it.data(Qt.UserRole + 1),
                    "f_hash": it.data(Qt.UserRole + 2),
                }
            listw.clear()
            for row, snap in enumerate(entries):
                entry = by_id.get(snap.get("clip_id"))
                if not entry:
                    continue
                self.parent.event_handler._add_single_item_internal(
                    entry["path"],
                    row=row,
                    probe_data=entry.get("probe_data"),
                    f_hash=entry.get("f_hash"),
                    clip_id=entry.get("clip_id"),
                    refresh=False
                )
            self.parent.event_handler.refresh_ranks()
            if selected_ids:
                listw.clearSelection()
                first_selected = None
                for i in range(listw.count()):
                    it = listw.item(i)
                    if it.data(Qt.UserRole + 3) in selected_ids:
                        it.setSelected(True)
                        if first_selected is None:
                            first_selected = it
                if first_selected is not None:
                    listw.setCurrentItem(first_selected)
        finally:
            model.blockSignals(False)
            self.parent.event_handler._is_replaying_undo = False
            self.parent.event_handler.update_button_states()

    def redo(self):
        self._apply(self.after_entries)

    def undo(self):
        self._apply(self.before_entries)

class ClearCommand(QUndoCommand):
    def __init__(self, parent, items_data, list_widget):
        super().__init__(f"Clear {len(items_data)} videos")
        self.parent = parent
        self.items_data = items_data
        self.list_widget = list_widget

    def redo(self):
        self.list_widget.clear()
        self.parent.logger.info(f"LIST: Cleared all {len(self.items_data)} items from list")

    def undo(self):
        for entry in self.items_data:
            path = entry["path"]
            self.parent.event_handler._add_single_item_internal(
                path,
                row=entry.get("row"),
                probe_data=entry.get("probe_data"),
                f_hash=entry.get("f_hash"),
                clip_id=entry.get("clip_id"),
                refresh=False
            )
        self.parent.logger.info(f"UNDO: Restored {len(self.items_data)} items to list")
        self.parent.event_handler.refresh_ranks()

class MergerHandlersListMixin:
    def __init__(self):
        self.undo_stack = QUndoStack(self.parent)
        self.undo_stack.setUndoLimit(200)
        self._loading_lock = False
        self._order_before_drag = None
        self._is_replaying_undo = False
        self._loading_progress_total = 0

    def _safe_update_button_states(self):
        """Safely update button states, catching RuntimeError if Qt objects are deleted."""
        try:
            self.parent.event_handler.update_button_states()
        except RuntimeError:
            pass

    def _snapshot_order(self):
        """Create a complete snapshot of all item data for undo/redo operations."""
        out = []
        for i in range(self.parent.listw.count()):
            it = self.parent.listw.item(i)
            out.append({
                "clip_id": it.data(Qt.UserRole + 3),
                "path": it.data(Qt.UserRole),
                "probe_data": it.data(Qt.UserRole + 1),
                "f_hash": it.data(Qt.UserRole + 2),
            })
        return out

    def setup_list_connections(self):
        self.parent.listw.setContextMenuPolicy(Qt.CustomContextMenu)
        self.parent.listw.customContextMenuRequested.connect(self.show_context_menu)

        from PyQt5.QtWidgets import QShortcut
        from PyQt5.QtGui import QKeySequence
        self.undo_shortcut = QShortcut(QKeySequence("Ctrl+Z"), self.parent)
        self.undo_shortcut.activated.connect(self.undo_stack.undo)
        self.redo_shortcut = QShortcut(QKeySequence("Ctrl+Y"), self.parent)
        self.redo_shortcut.activated.connect(self.undo_stack.redo)
        if hasattr(self.parent, "btn_undo"):
            self.parent.btn_undo.clicked.connect(self.undo_stack.undo)
        if hasattr(self.parent, "btn_redo"):
            self.parent.btn_redo.clicked.connect(self.undo_stack.redo)
        self.undo_stack.indexChanged.connect(lambda *_: self._safe_update_button_states())
        self.del_shortcut = QShortcut(QKeySequence("Delete"), self.parent)
        self.del_shortcut.activated.connect(self.remove_selected)
        self.sel_all_shortcut = QShortcut(QKeySequence("Ctrl+A"), self.parent)
        self.sel_all_shortcut.activated.connect(self.parent.listw.selectAll)
        self.esc_shortcut = QShortcut(QKeySequence("Esc"), self.parent)
        self.esc_shortcut.activated.connect(self.parent.listw.clearSelection)

    def show_context_menu(self, pos: QPoint):
        item = self.parent.listw.itemAt(pos)
        menu = QMenu(self.parent)
        selected = self.parent.listw.selectedItems()
        add_action = QAction("Add Videos...", self.parent)
        add_action.triggered.connect(self.add_videos)
        menu.addAction(add_action)
        add_folder_action = QAction("Smart Add Folder (Recursive)", self.parent)
        add_folder_action.triggered.connect(self.add_folder)
        menu.addAction(add_folder_action)
        quick_smart_add = QAction("Smart Add & Auto-Merge Prep", self.parent)
        quick_smart_add.triggered.connect(self.smart_add_and_prepare)
        menu.addAction(quick_smart_add)
        if item:
            if not item.isSelected():
                self.parent.listw.setCurrentItem(item)
            path = item.data(Qt.UserRole)
            open_action = QAction("Open in Default Player", self.parent)
            open_action.triggered.connect(lambda: QDesktopServices.openUrl(QUrl.fromLocalFile(path)))
            menu.addAction(open_action)
            dup_action = QAction("Duplicate Item", self.parent)
            dup_action.triggered.connect(lambda: self.duplicate_item(item))
            menu.addAction(dup_action)
            remove_action = QAction("Remove Selected", self.parent)
            remove_action.triggered.connect(self.remove_selected)
            menu.addAction(remove_action)
            move_up_action = QAction("Move Up", self.parent)
            move_up_action.triggered.connect(lambda: self.move_item(-1))
            menu.addAction(move_up_action)
            move_down_action = QAction("Move Down", self.parent)
            move_down_action.triggered.connect(lambda: self.move_item(1))
            menu.addAction(move_down_action)
        menu.addSeparator()
        merge_now_action = QAction("Merge Now", self.parent)
        merge_now_action.setEnabled(self.parent.listw.count() >= 1 and not self.parent.is_processing)
        merge_now_action.triggered.connect(self.parent.on_merge_clicked)
        menu.addAction(merge_now_action)
        if selected:
            remove_quick = QAction(f"Quick Remove ({len(selected)})", self.parent)
            remove_quick.triggered.connect(self.remove_selected)
            menu.addAction(remove_quick)
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
        entry = {
            "path": path,
            "row": row,
            "probe_data": probe_data,
            "f_hash": f_hash,
            "clip_id": uuid.uuid4().hex,
        }
        self.undo_stack.push(AddCommand(self.parent, [entry], self.parent.listw))

    def smart_add_and_prepare(self):
        """One-shot action: add videos and proactively prepare merge prerequisites."""
        if self._loading_lock:
            return
        self.add_videos()
        if self.parent.listw.count() > 0:
            self.parent.estimate_total_duration_seconds()
            self.parent.set_status_message("Smart action complete: files queued and merge prerequisites precomputed.", "color: #43b581;", 2000, force=True)

    def on_merge_clicked(self):
        self.parent.logger.info("USER: Clicked MERGE VIDEOS")
        self.parent.start_merge_processing()

    def add_videos(self):
        if self.parent.is_processing:
            return
        if self._loading_lock: return
        self.parent.logger.info("USER: Clicked ADD VIDEOS")
        start_dir = self.parent.logic_handler.get_last_dir()
        files, _ = QFileDialog.getOpenFileNames(
            self.parent, "Select videos to merge", start_dir,
            "Videos (*.mp4 *.mov *.mkv *.m4v *.ts *.avi *.webm);;All Files (*)"
        )
        if not files:
            self.parent.logger.info("USER: Cancelled file selection")
            return
        self.parent.logger.info(f"USER: Selected {len(files)} files to add")
        self._start_file_loader(files)

    def add_folder(self):
        if self.parent.is_processing:
            return
        if self._loading_lock: return
        self.parent.logger.info("USER: Clicked ADD FOLDER")
        start_dir = self.parent.logic_handler.get_last_dir()
        folder = QFileDialog.getExistingDirectory(self.parent, "Select Folder of Videos", start_dir)
        if not folder:
            self.parent.logger.info("USER: Cancelled folder selection")
            return
        self.parent.logger.info(f"USER: Selected folder '{folder}'")
        exts = {'.mp4', '.mov', '.mkv', '.m4v', '.ts', '.avi', '.webm'}
        self.parent.set_status_message("Scanning folder for videos...", "color: #7289da;", force=True)
        self._folder_scan_worker = FolderScanWorker(folder, exts)
        self._folder_scan_worker.finished.connect(self._on_folder_scan_finished)
        self._folder_scan_worker.start()

    def _on_folder_scan_finished(self, files, err):
        if err:
            self.parent.logger.error(f"ERROR: Failed to read folder: {err}")
            QMessageBox.warning(self.parent, "Scan Error", "Could not scan folder contents.")
            self.parent.set_status_message("Folder scan failed.", "color: #ff6b6b;", 2500, force=True)
            return
        if not files:
            self.parent.logger.info("USER: No valid videos found in folder")
            QMessageBox.information(self.parent, "No Videos", "No video files found in that folder.")
            self.parent.set_status_message("No videos found in selected folder.", "color: #ffa500;", 1800, force=True)
            return
        files = sorted(files, key=_natural_key)
        self.parent.logger.info(f"USER: Found {len(files)} videos in folder (recursive smart add)")
        self._start_file_loader(files)

    def add_videos_from_list(self, files):
        if self.parent.is_processing:
            return
        if self._loading_lock: return
        if not files: return
        self._start_file_loader(files)

    def _start_file_loader(self, files):
        current_count = self.parent.listw.count()
        current_files = set()
        if current_count > 0:
             for i in range(current_count):
                it = self.parent.listw.item(i)
                current_files.add(it.data(Qt.UserRole))
        room = max(0, int(self.parent.MAX_FILES - current_count))
        unique_candidates = []
        seen = set(current_files)
        for f in files:
            if f in seen:
                continue
            seen.add(f)
            unique_candidates.append(f)
        if not unique_candidates:
            self.parent.set_status_message("All selected files are already in the list.", "color: #ffa500;", 2200, force=True)
            return
        if len(unique_candidates) > room:
             kept = unique_candidates[:room]
             skipped = len(unique_candidates) - len(kept)
             QMessageBox.information(
                 self.parent,
                 "List limit reached",
                 f"Only {len(kept)} more file(s) can be added (limit {self.parent.MAX_FILES}).\n"
                 f"Skipped {skipped} extra file(s).",
             )
             files = kept
        else:
             files = unique_candidates
        self._pending_undo_items = []
        self._loading_progress_total = max(1, len(files))
        self._loading_lock = True
        self.parent.set_ui_busy(True)
        self.parent.set_status_message("Loading files...", "color: #ffa500;", force=True)
        existing_hashes = set()
        if current_count > 0:
             for i in range(current_count):
                it = self.parent.listw.item(i)
                h = it.data(Qt.UserRole + 2)
                if h: existing_hashes.add(h)
        self._loader = FastFileLoaderWorker(files, current_files, existing_hashes, self.parent.MAX_FILES, self.parent.ffmpeg)
        self._loader.file_loaded.connect(self._on_file_loaded)
        self._loader.progress.connect(self._on_loader_progress)
        self._loader.finished.connect(self._on_loading_finished)
        self._loader.start()
        if files:
            self.parent.logic_handler.set_last_dir(str(Path(files[0]).parent))
            self.parent.logic_handler.request_save_config()

    def _on_loader_progress(self, current, total):
        self._loading_progress_total = max(1, int(total or 1))
        pct = int(round((float(current) / float(self._loading_progress_total)) * 100.0))
        pct = max(0, min(100, pct))
        self.parent.set_status_message(f"Loading files... {current}/{self._loading_progress_total} ({pct}%)", "color: #ffa500;", force=True)

    def _on_file_loaded(self, path, size, probe_data, f_hash):
        current_idx = self.parent.listw.count() + len(self._pending_undo_items)
        self._pending_undo_items.append({
            "path": path,
            "row": current_idx,
            "probe_data": probe_data,
            "f_hash": f_hash,
            "clip_id": uuid.uuid4().hex,
        })

    def refresh_ranks(self):
        """Update all ranking labels (#1, #2, etc.) based on current list order."""
        for i in range(self.parent.listw.count()):
            item = self.parent.listw.item(i)
            w = self.parent.listw.itemWidget(item)
            if w and hasattr(w, 'rank_label'):
                w.rank_label.setText(f"#{i + 1}")

    def refresh_selection_highlights(self):
        """Sync the visual 'marked' state of custom widgets with their list selection state."""
        listw = self.parent.listw
        for i in range(listw.count()):
            item = listw.item(i)
            w = listw.itemWidget(item)
            if w and hasattr(w, 'set_marked'):
                w.set_marked(item.isSelected())

    def _add_single_item_internal(self, path, row=None, probe_data=None, f_hash=None, clip_id=None, refresh=True):
        item = QListWidgetItem()
        item.setToolTip(path)
        item.setData(Qt.UserRole, path)
        item.setData(Qt.UserRole + 3, clip_id or uuid.uuid4().hex)
        if probe_data:
            item.setData(Qt.UserRole + 1, probe_data)
        if f_hash:
            item.setData(Qt.UserRole + 2, f_hash)
        current_count = self.parent.listw.count()
        rank = (row + 1) if row is not None else (current_count + 1)
        w = self.make_item_widget(path, rank=rank)
        if probe_data:
            try:
                dur = float(probe_data.get('format', {}).get('duration', 0))
                if dur > 0:
                    w.set_duration_label(_human_time(dur))
                streams = probe_data.get('streams', [])
                vid = next((s for s in streams if s.get('width')), None)
                if vid:
                    w.set_resolution_label(f"{vid['width']}x{vid['height']}")
            except Exception as ex:
                self.parent.logger.debug(f"Item metadata paint skipped: {ex}")

        from PyQt5.QtCore import QSize
        item.setSizeHint(QSize(w.width(), 50))
        if row is not None:
            self.parent.listw.insertItem(row, item)
            inserted_row = row
        else:
            self.parent.listw.addItem(item)
            inserted_row = self.parent.listw.count() - 1
        self.parent.listw.setItemWidget(item, w)
        if refresh:
            self.refresh_ranks()
        self.refresh_selection_highlights()
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
        self._loading_progress_total = 0
        self.parent.event_handler.update_button_states()
        self.parent.logic_handler.request_save_config()
        if added:
            self.parent.set_status_message(f"Loaded {added} videos. Ready for merge.", "color: #43b581;", 2500, force=True)

    def remove_selected(self):
        if self.parent.is_processing:
            return
        selected = self.parent.listw.selectedItems()
        if not selected: return
        self.parent.logger.info(f"USER: Clicked REMOVE SELECTED ({len(selected)} items selected)")
        cmd = RemoveCommand(self.parent, selected, self.parent.listw)
        self.undo_stack.push(cmd)
        self.parent.event_handler.update_button_states()
        self.parent.set_status_message(f"Removed {len(selected)}", "color: #e74c3c;", 2000, force=True)
        self.parent.logic_handler.request_save_config()

    def clear_all(self):
        if self.parent.is_processing:
            return
        if self.parent.listw.count() == 0: return
        self.parent.logger.info("USER: Clicked CLEAR ALL")
        items_data = []
        for i in range(self.parent.listw.count()):
            it = self.parent.listw.item(i)
            items_data.append({
                "row": i,
                "path": it.data(Qt.UserRole),
                "probe_data": it.data(Qt.UserRole + 1),
                "f_hash": it.data(Qt.UserRole + 2),
                "clip_id": it.data(Qt.UserRole + 3),
            })
        self.undo_stack.push(ClearCommand(self.parent, items_data, self.parent.listw))
        self.parent.set_status_message("List cleared", "color: #e74c3c;", 2000, force=True)
        self.parent.logic_handler.request_save_config()

    def move_item(self, direction: int):
        if self.parent.is_processing:
            return
        sel = self.parent.listw.selectedItems()
        if not sel: return
        dir_name = "UP" if direction < 0 else "DOWN"
        self.parent.logger.info(f"USER: Clicked MOVE {dir_name} ({len(sel)} items selected)")
        rows = sorted([self.parent.listw.row(i) for i in sel])
        if not rows: return
        before = self._snapshot_order()
        if direction < 0 and rows[0] == 0:
            return
        if direction > 0 and rows[-1] == self.parent.listw.count() - 1:
            return
        selected_set = set(rows)
        order = list(range(self.parent.listw.count()))
        if direction < 0:
            for r in rows:
                order[r - 1], order[r] = order[r], order[r - 1]
        else:
            for r in reversed(rows):
                order[r + 1], order[r] = order[r], order[r + 1]
        by_index = {i: before[i] for i in range(len(before))}
        after = [by_index[idx] for idx in order]
        self.undo_stack.push(BatchMoveCommand(self.parent, before, after))
        self.parent.set_status_message(f"Moved {len(selected_set)} item(s)", "color: #7289da;", 1200, force=True)

    def on_selection_changed(self):
        self.parent.event_handler.update_button_states()
        
    def on_drag_completed(self, start_row, end_row, path, tag):
        if start_row == end_row: return
        self.parent.logger.info(f"USER: Drag reordered '{os.path.basename(path)}' from row {start_row} to {end_row}")
        if hasattr(self.parent.logic_handler, "ensure_item_widgets_consistent"):
            self.parent.logic_handler.ensure_item_widgets_consistent()
        self.parent.logic_handler.request_save_config()
        self.parent.event_handler.update_button_states()
        self.parent.set_status_message("Order updated", "color: #7289da;", 1200, force=True)

    def on_drag_started(self, *_):
        self._order_before_drag = self._snapshot_order()

    def on_rows_moved(self, sourceParent, sourceStart, sourceEnd, destinationParent, destinationRow):
        """Capture drag-reorder as a single undoable transaction."""
        if self._is_replaying_undo:
            return
        if hasattr(self.parent.logic_handler, "ensure_item_widgets_consistent"):
            self.parent.logic_handler.ensure_item_widgets_consistent()
        before = self._order_before_drag or []
        after = self._snapshot_order()
        if before and before != after:
            self.undo_stack.push(ReorderCommand(self.parent, before, after))
        self._order_before_drag = None
        self.parent.logic_handler.request_save_config()
        self.parent.event_handler.update_button_states()
