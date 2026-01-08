from PyQt5.QtWidgets import QGraphicsObject, QGraphicsRectItem, QGraphicsItem
from PyQt5.QtCore import Qt, pyqtSignal, QRectF, QTimer
from PyQt5.QtGui import QBrush, QColor, QPen

class ResizablePixmapItem(QGraphicsObject):
    item_changed = pyqtSignal()

    def __init__(self, pixmap, crop_rect, parent=None):
        super(ResizablePixmapItem, self).__init__(parent)
        self.original_pixmap = pixmap 
        self.crop_rect = crop_rect
        self.current_width = pixmap.width()
        self.current_height = pixmap.height()
        self.setFlags(QGraphicsItem.ItemIsMovable | QGraphicsItem.ItemIsSelectable | QGraphicsItem.ItemSendsGeometryChanges)
        self.setAcceptHoverEvents(True)
        self.handle_br = QGraphicsRectItem(QRectF(0, 0, 10, 10), self)
        self.handle_br.setBrush(QBrush(QColor("red")))
        self.handle_tl = QGraphicsRectItem(QRectF(0, 0, 10, 10), self)
        self.handle_tl.setBrush(QBrush(QColor("red")))
        self.update_handle_positions()
        self.is_resizing_br = False
        self.is_resizing_tl = False
        self.ant_timer = QTimer(self)
        self.ant_timer.timeout.connect(self.update_ant_dash)
        self.ant_dash_offset = 0

    def update_ant_dash(self):
        self.ant_dash_offset = (self.ant_dash_offset + 1) % 8
        self.update()

    def boundingRect(self):
        return QRectF(0, 0, self.current_width, self.current_height)

    def paint(self, painter, option, widget):
        painter.drawPixmap(self.boundingRect(), self.original_pixmap, QRectF(self.original_pixmap.rect()))
        if self.isSelected():
            pen = QPen(QColor("white"), 1, Qt.CustomDashLine)
            pen.setDashPattern([4, 4])
            pen.setDashOffset(self.ant_dash_offset)
            painter.setPen(pen)
            painter.setBrush(Qt.NoBrush)
            painter.drawRect(self.boundingRect())

    def update_handle_positions(self):
        self.handle_br.setPos(self.current_width - 10, self.current_height - 10)
        self.handle_tl.setPos(0, 0)

    def hoverMoveEvent(self, event):
        if self.handle_br.isUnderMouse() or self.handle_tl.isUnderMouse():
            self.setCursor(Qt.SizeFDiagCursor)
        else:
            self.setCursor(Qt.ArrowCursor)
        super(ResizablePixmapItem, self).hoverMoveEvent(event)

    def mousePressEvent(self, event):
        if self.handle_br.isUnderMouse():
            self.is_resizing_br = True
            self.resize_start_pos = event.pos()
            self.start_width = self.current_width
            self.start_height = self.current_height
        elif self.handle_tl.isUnderMouse():
            self.is_resizing_tl = True
            self.resize_start_pos = event.pos()
            self.start_width = self.current_width
            self.start_height = self.current_height
            self.start_pos = self.pos()
        else:
            super(ResizablePixmapItem, self).mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if self.is_resizing_br:
            delta = event.pos() - self.resize_start_pos
            new_width = self.start_width + delta.x()
            aspect_ratio = self.original_pixmap.width() / self.original_pixmap.height()
            if aspect_ratio > 0:
                new_height = new_width / aspect_ratio
                if new_width > 10 and new_height > 10:
                    self.prepareGeometryChange()
                    self.current_width = new_width
                    self.current_height = new_height
                    self.update_handle_positions()
                    self.item_changed.emit()
        elif self.is_resizing_tl:
            delta = event.pos() - self.resize_start_pos
            new_width = self.start_width - delta.x()
            aspect_ratio = self.original_pixmap.width() / self.original_pixmap.height()
            if aspect_ratio > 0:
                new_height = new_width / aspect_ratio
                if new_width > 10 and new_height > 10:
                    self.prepareGeometryChange()
                    self.current_width = new_width
                    self.current_height = new_height
                    new_pos_x = self.start_pos.x() + delta.x()
                    new_pos_y = self.start_pos.y() + (self.start_height - new_height)
                    self.setPos(new_pos_x, new_pos_y)
                    self.update_handle_positions()
                    self.item_changed.emit()
        else:
            super(ResizablePixmapItem, self).mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
        self.is_resizing_br = False
        self.is_resizing_tl = False
        super(ResizablePixmapItem, self).mouseReleaseEvent(event)
        self.item_changed.emit()

    def itemChange(self, change, value):
        if change == QGraphicsItem.ItemSelectedHasChanged:
            if value:
                self.ant_timer.start(100)
            else:
                self.ant_timer.stop()
        elif change == QGraphicsItem.ItemPositionHasChanged:
            self.item_changed.emit()
        return super(ResizablePixmapItem, self).itemChange(change, value)
