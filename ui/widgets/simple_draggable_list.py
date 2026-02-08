"""
Crash-safe draggable list.
Uses Qt's native InternalMove only (no custom ghost/timer/SIP mutation paths).
"""

from PyQt5.QtCore import Qt, pyqtSignal
from PyQt5.QtWidgets import QListWidget, QAbstractItemView

class SimpleDraggableList(QListWidget):
    drag_started = pyqtSignal(int, str)
    drag_completed = pyqtSignal(int, int, str, str)
    drag_cancelled = pyqtSignal(int, str)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.setDefaultDropAction(Qt.MoveAction)
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDropIndicatorShown(True)
        self.setCursor(Qt.OpenHandCursor)
        self.setSpacing(4)
        self._drag_start_row = -1

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            item = self.itemAt(event.pos())
            self._drag_start_row = self.row(item) if item else -1
        super().mousePressEvent(event)

    def startDrag(self, supportedActions):
        row = self._drag_start_row
        if row >= 0:
            self.drag_started.emit(row, self._item_name(row))
        super().startDrag(supportedActions)

    def dropEvent(self, event):
        start = self._drag_start_row
        before_name = self._item_name(start) if start >= 0 else "Unknown"
        super().dropEvent(event)
        end = self.currentRow()
        after_name = self._item_name(end) if end >= 0 else "Unknown"
        if start >= 0 and end >= 0:
            if start != end:
                self.drag_completed.emit(start, end, before_name, after_name)
            else:
                self.drag_cancelled.emit(start, before_name)
        self._drag_start_row = -1

    def _item_name(self, row: int) -> str:
        if row < 0 or row >= self.count():
            return "Unknown"
        item = self.item(row)
        if not item:
            return "Unknown"
        path = item.data(Qt.UserRole)
        if path:
            import os
            return os.path.basename(path)
        txt = item.text() if hasattr(item, "text") else ""
        return txt or "Unknown"
