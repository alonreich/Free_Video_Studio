from PyQt5.QtWidgets import QWidget
from PyQt5.QtCore import Qt, QPoint, QRect
from PyQt5.QtGui import QPainter, QPen, QBrush, QColor, QPixmap

class DrawWidget(QWidget):
    def __init__(self, parent=None):
        super(DrawWidget, self).__init__(parent)
        self.begin = QPoint()
        self.end = QPoint()
        self.mode = 'none'
        self.resize_edge = None
        self._crop_rect = QRect()
        self.pixmap = QPixmap()
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.StrongFocus)
        self.handle_size = 10

    def clear_selection(self):
        self._crop_rect = QRect()
        self.update()

    def setImage(self, image_path):
        if image_path is None:
            self.pixmap = QPixmap()
        else:
            self.pixmap = QPixmap(image_path)
        self.update()

    def get_selection(self):
        if self.pixmap.isNull() or self._crop_rect.isNull() or not self._crop_rect.isValid():
            return None, None
        widget_rect = self.rect()
        pixmap_rect = self.pixmap.rect()
        scaled_pixmap = self.pixmap.scaled(widget_rect.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation)
        scaled_rect = scaled_pixmap.rect()
        scaled_rect.moveCenter(widget_rect.center())
        x_scale = pixmap_rect.width() / scaled_rect.width()
        y_scale = pixmap_rect.height() / scaled_rect.height()
        selection_relative_to_image = self._crop_rect.translated(-scaled_rect.topLeft())
        final_rect = QRect(
            int(selection_relative_to_image.x() * x_scale),
            int(selection_relative_to_image.y() * y_scale),
            int(selection_relative_to_image.width() * x_scale),
            int(selection_relative_to_image.height() * y_scale)
        )
        final_rect = final_rect.intersected(pixmap_rect)
        if final_rect.width() < 1 or final_rect.height() < 1:
            return None, None
        cropped_pixmap = self.pixmap.copy(final_rect)
        return cropped_pixmap, final_rect

    def get_handle_at(self, pos):
        r = self._crop_rect
        if r.isNull(): return None
        hs = self.handle_size
        if (pos - r.topLeft()).manhattanLength() < hs: return 'top_left'
        if (pos - r.topRight()).manhattanLength() < hs: return 'top_right'
        if (pos - r.bottomLeft()).manhattanLength() < hs: return 'bottom_left'
        if (pos - r.bottomRight()).manhattanLength() < hs: return 'bottom_right'
        if abs(pos.x() - r.left()) < hs and r.top() < pos.y() < r.bottom(): return 'left'
        if abs(pos.x() - r.right()) < hs and r.top() < pos.y() < r.bottom(): return 'right'
        if abs(pos.y() - r.top()) < hs and r.left() < pos.x() < r.right(): return 'top'
        if abs(pos.y() - r.bottom()) < hs and r.left() < pos.x() < r.right(): return 'bottom'
        if r.contains(pos): return 'move'
        return None

    def paintEvent(self, event):
        super().paintEvent(event)
        painter = QPainter(self)
        if not self.pixmap.isNull():
            current_key = self.pixmap.cacheKey()
            if (not hasattr(self, '_cache_pix') or 
                self._cache_size != self.size() or 
                self._cache_key != current_key):
                self._cache_pix = self.pixmap.scaled(self.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation)
                self._cache_rect = self._cache_pix.rect()
                self._cache_rect.moveCenter(self.rect().center())
                self._cache_size = self.size()
                self._cache_key = current_key
            painter.drawPixmap(self._cache_rect, self._cache_pix)
        if self.mode == 'drawing':
            rect_to_draw = QRect(self.begin, self.end).normalized()
        else:
            rect_to_draw = self._crop_rect
        if not rect_to_draw.isNull():
            pen = QPen(QColor(0, 255, 0), 2, Qt.SolidLine)
            painter.setPen(pen)
            brush = QBrush(QColor(0, 255, 0, 50))
            painter.setBrush(brush)
            painter.drawRect(rect_to_draw)
            painter.setBrush(QBrush(QColor(255, 255, 255)))
            painter.setPen(QPen(QColor(0, 0, 0)))
            hs = 6
            r = rect_to_draw
            painter.drawRect(r.left(), r.top(), hs, hs)
            painter.drawRect(r.right()-hs, r.top(), hs, hs)
            painter.drawRect(r.left(), r.bottom()-hs, hs, hs)
            painter.drawRect(r.right()-hs, r.bottom()-hs, hs, hs)
            painter.drawRect(r.left() + r.width()//2 - hs//2, r.top(), hs, hs)
            painter.drawRect(r.left() + r.width()//2 - hs//2, r.bottom()-hs, hs, hs)
            painter.drawRect(r.left(), r.top() + r.height()//2 - hs//2, hs, hs)
            painter.drawRect(r.right()-hs, r.top() + r.height()//2 - hs//2, hs, hs)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            handle = self.get_handle_at(event.pos())
            if handle == 'move':
                self.mode = 'moving'
                self.last_mouse_pos = event.pos()
            elif handle:
                self.mode = 'resizing'
                self.resize_edge = handle
                self.last_mouse_pos = event.pos()
            else:
                self.mode = 'drawing'
                self.begin = event.pos()
                self.end = event.pos()
                self._crop_rect = QRect()
            self.update()

    def mouseMoveEvent(self, event):
        if self.mode == 'none':
            handle = self.get_handle_at(event.pos())
            if handle in ['top_left', 'bottom_right']: self.setCursor(Qt.SizeFDiagCursor)
            elif handle in ['top_right', 'bottom_left']: self.setCursor(Qt.SizeBDiagCursor)
            elif handle in ['left', 'right']: self.setCursor(Qt.SizeHorCursor)
            elif handle in ['top', 'bottom']: self.setCursor(Qt.SizeVerCursor)
            elif handle == 'move': self.setCursor(Qt.SizeAllCursor)
            else: self.setCursor(Qt.CrossCursor)
        if self.mode == 'drawing':
            self.end = event.pos()
            self.update()
        elif self.mode == 'moving':
            delta = event.pos() - self.last_mouse_pos
            self._crop_rect.translate(delta)
            self.last_mouse_pos = event.pos()
            self.update()
        elif self.mode == 'resizing':
            r = self._crop_rect
            p = event.pos() 
            if 'left' in self.resize_edge: r.setLeft(min(r.right()-10, p.x()))
            if 'right' in self.resize_edge: r.setRight(max(r.left()+10, p.x()))
            if 'top' in self.resize_edge: r.setTop(min(r.bottom()-10, p.y()))
            if 'bottom' in self.resize_edge: r.setBottom(max(r.top()+10, p.y()))
            self.update()

    def mouseReleaseEvent(self, event):
        if event.button() == Qt.LeftButton:
            if self.mode == 'drawing':
                self._crop_rect = QRect(self.begin, self.end).normalized()
            self.mode = 'none'
            self.resize_edge = None
            self.update()
