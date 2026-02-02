from PyQt5.QtWidgets import QListWidget, QAbstractItemView, QWidget, QApplication
from PyQt5.QtCore import Qt, pyqtSignal, QPoint, QMimeData
from PyQt5.QtGui import QDrag, QPixmap, QPainter, QColor, QPen, QCursor

class MergerDraggableList(QListWidget):
    """
    Standardized draggable list using QListWidget's built-in InternalMove.
    Fixes performance issues (#7) and visual glitches (#10).
    """
    # Signals kept for compatibility if needed, though InternalMove handles the model
    item_moved_signal = pyqtSignal(int, int) # from, to
    drag_completed = pyqtSignal(int, int, str, str) # start, end, path, type
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDragDropMode(QAbstractItemView.DragDrop)
        self.setDefaultDropAction(Qt.MoveAction)
        self.setSpacing(4)
        self.setUniformItemSizes(True)
        self.setVerticalScrollMode(QAbstractItemView.ScrollPerPixel)

    def dropEvent(self, event):
        if event.source() == self:
            # Internal Reorder
            if event.mimeData().hasFormat("application/x-qabstractitemmodeldatalist"):
                event.setDropAction(Qt.MoveAction)
                super().dropEvent(event)
                event.accept()
        else:
            # External File Drop
            if event.mimeData().hasUrls():
                event.setDropAction(Qt.CopyAction)
                event.accept()
                # Let main window handle the drop logic via parent call or signal
                # The original code called parent()._handle_dropped_files
                if self.parent() and hasattr(self.parent(), '_handle_dropped_files'):
                     self.parent()._handle_dropped_files([url.toLocalFile() for url in event.mimeData().urls()])
            else:
                event.ignore()

    def dragEnterEvent(self, event):
        if event.source() == self:
            event.setDropAction(Qt.MoveAction)
            event.accept()
        elif event.mimeData().hasUrls():
            event.accept()
        else:
            event.ignore()

    def dragMoveEvent(self, event):
        if event.source() == self:
            event.setDropAction(Qt.MoveAction)
            event.accept()
        elif event.mimeData().hasUrls():
            event.setDropAction(Qt.CopyAction)
            event.accept()
        else:
            event.ignore()