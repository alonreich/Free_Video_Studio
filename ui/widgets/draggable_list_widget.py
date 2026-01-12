from PyQt5.QtCore import Qt, QRect
from PyQt5.QtGui import QPainter, QColor
from PyQt5.QtWidgets import QListWidget, QAbstractItemView

class DraggableListWidget(QListWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.drop_indicator_rect = None

    def dragMoveEvent(self, event):
        super().dragMoveEvent(event)
        index = self.indexAt(event.pos())
        pos = self.dropIndicatorPosition()
        if not index.isValid() or pos == QAbstractItemView.OnItem or pos == QAbstractItemView.OnViewport:
            self.drop_indicator_rect = None
        else:
            rect = self.visualRect(index)
            if pos == QAbstractItemView.AboveItem:
                self.drop_indicator_rect = QRect(rect.left(), rect.top() - 4, rect.width(), 8)
            elif pos == QAbstractItemView.BelowItem:
                self.drop_indicator_rect = QRect(rect.left(), rect.bottom() - 4, rect.width(), 8)
            else:
                self.drop_indicator_rect = None
        self.viewport().update()

    def dragLeaveEvent(self, event):
        self.drop_indicator_rect = None
        self.viewport().update()
        super().dragLeaveEvent(event)

    def dropEvent(self, event):
        self.drop_indicator_rect = None
        super().dropEvent(event)
        self.viewport().update()

    def paintEvent(self, event):
        super().paintEvent(event)
        if self.drop_indicator_rect:
            painter = QPainter(self.viewport())
            fill_color = QColor(66, 174, 255, 200)
            painter.fillRect(self.drop_indicator_rect, fill_color)
            border_color = QColor(255, 255, 255, 220)
            pen = painter.pen()
            pen.setColor(border_color)
            pen.setWidth(1)
            painter.setPen(pen)
            painter.drawRect(self.drop_indicator_rect.adjusted(0, 0, -1, -1))
