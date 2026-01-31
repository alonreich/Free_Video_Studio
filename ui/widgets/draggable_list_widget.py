from PyQt5.QtCore import Qt, QRect, QTimer, QMimeData, QPoint
from PyQt5.QtGui import QPainter, QColor, QLinearGradient, QDrag, QPixmap
from PyQt5.QtWidgets import QListWidget, QAbstractItemView, QApplication

class DraggableListWidget(QListWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.drop_indicator_rect = None
        self.setCursor(Qt.PointingHandCursor)
        self.auto_scroll_timer = QTimer()
        self.auto_scroll_timer.timeout.connect(self._auto_scroll)
        self.auto_scroll_direction = 0
        self.drag_scroll_margin = 120
        self.scroll_speed = 15
        self._drag_item_index = -1
        self._last_drag_pos = None
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDropIndicatorShown(True)
        self.setSelectionMode(QAbstractItemView.SingleSelection)

    def dragMoveEvent(self, event):
        super().dragMoveEvent(event)
        index = self.indexAt(event.pos())
        pos = self.dropIndicatorPosition()
        if not index.isValid() or pos == QAbstractItemView.OnItem or pos == QAbstractItemView.OnViewport:
            self.drop_indicator_rect = None
        else:
            rect = self.visualRect(index)
            item_height = 40
            if pos == QAbstractItemView.AboveItem:
                self.drop_indicator_rect = QRect(rect.left(), rect.top() - item_height, rect.width(), item_height)
            elif pos == QAbstractItemView.BelowItem:
                self.drop_indicator_rect = QRect(rect.left(), rect.bottom(), rect.width(), item_height)
            else:
                self.drop_indicator_rect = None
        self._handle_auto_scroll_during_drag(event.pos())
        self.viewport().update()

    def _handle_auto_scroll_during_drag(self, pos):
        """Handle auto-scrolling when dragging near top or bottom edges with predictive scrolling."""
        viewport = self.viewport()
        viewport_rect = viewport.rect()
        viewport_height = viewport_rect.height()
        mouse_y = pos.y()
        scroll_factor = 0
        if mouse_y < self.drag_scroll_margin:
            self.auto_scroll_direction = -1
            scroll_factor = 1.0 - (mouse_y / self.drag_scroll_margin)
            self.scroll_speed = int(8 + (scroll_factor * 22))
            if not self.auto_scroll_timer.isActive():
                self.auto_scroll_timer.start(30)
        elif mouse_y > viewport_height - self.drag_scroll_margin:
            self.auto_scroll_direction = 1
            distance_from_edge = viewport_height - mouse_y
            scroll_factor = 1.0 - (distance_from_edge / self.drag_scroll_margin)
            self.scroll_speed = int(8 + (scroll_factor * 22))
            if not self.auto_scroll_timer.isActive():
                self.auto_scroll_timer.start(30)
        else:
            self.auto_scroll_direction = 0
            self.auto_scroll_timer.stop()

    def _auto_scroll(self):
        """Perform auto-scrolling based on direction."""
        if self.auto_scroll_direction == -1:
            self.verticalScrollBar().setValue(
                self.verticalScrollBar().value() - self.scroll_speed
            )
        elif self.auto_scroll_direction == 1:
            self.verticalScrollBar().setValue(
                self.verticalScrollBar().value() + self.scroll_speed
            )
        else:
            self.auto_scroll_timer.stop()

    def dragLeaveEvent(self, event):
        self.drop_indicator_rect = None
        self.auto_scroll_timer.stop()
        self.auto_scroll_direction = 0
        self.viewport().update()
        super().dragLeaveEvent(event)

    def dropEvent(self, event):
        self.drop_indicator_rect = None
        self.auto_scroll_timer.stop()
        self.auto_scroll_direction = 0
        super().dropEvent(event)
        self.viewport().update()

    def paintEvent(self, event):
        super().paintEvent(event)
        if self.drop_indicator_rect:
            painter = QPainter(self.viewport())
            gradient = QLinearGradient(self.drop_indicator_rect.topLeft(), self.drop_indicator_rect.bottomLeft())
            gradient.setColorAt(0, QColor(66, 174, 255, 230))
            gradient.setColorAt(1, QColor(0, 120, 215, 230))
            painter.fillRect(self.drop_indicator_rect, gradient)
            border_color = QColor(255, 255, 255, 255)
            pen = painter.pen()
            pen.setColor(border_color)
            pen.setWidth(2)
            painter.setPen(pen)
            painter.drawRect(self.drop_indicator_rect.adjusted(0, 0, -1, -1))
            highlight_rect = self.drop_indicator_rect.adjusted(1, 1, -2, -2)
            highlight_gradient = QLinearGradient(highlight_rect.topLeft(), highlight_rect.bottomLeft())
            highlight_gradient.setColorAt(0, QColor(255, 255, 255, 100))
            highlight_gradient.setColorAt(1, QColor(255, 255, 255, 30))
            pen.setWidth(1)
            pen.setColor(QColor(255, 255, 255, 150))
            painter.setPen(pen)
            painter.drawRect(highlight_rect)
