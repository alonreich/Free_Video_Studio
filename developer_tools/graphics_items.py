from PyQt5.QtWidgets import QGraphicsObject, QGraphicsRectItem, QGraphicsItem
from PyQt5.QtCore import Qt, pyqtSignal, QRectF, QTimer
from PyQt5.QtGui import QBrush, QColor, QPen

class ResizablePixmapItem(QGraphicsObject):
    item_changed = pyqtSignal()

    def __init__(self, pixmap, crop_rect, parent=None):
        super(ResizablePixmapItem, self).__init__(parent)
        self.original_pixmap = pixmap 
        self.crop_rect = crop_rect
        self.assigned_role = None 
        self.current_width = pixmap.width()
        self.current_height = pixmap.height()
        self.setFlags(QGraphicsItem.ItemIsMovable | QGraphicsItem.ItemIsSelectable | QGraphicsItem.ItemSendsGeometryChanges)
        self.setAcceptHoverEvents(True)
        self.handle_size = 25
        self.handle_br = QGraphicsRectItem(QRectF(0, 0, self.handle_size, self.handle_size), self)
        self.handle_br.setBrush(QBrush(QColor("#3498db")))
        self.handle_br.setPen(QPen(Qt.white, 2))
        self.handle_tl = QGraphicsRectItem(QRectF(0, 0, self.handle_size, self.handle_size), self)
        self.handle_tl.setBrush(QBrush(QColor("#e67e22")))
        self.handle_tl.setPen(QPen(Qt.white, 2))
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
        return QRectF(-30, -80, self.current_width + 60, self.current_height + 130)

    def paint(self, painter, option, widget):
        painter.drawPixmap(QRectF(0, 0, self.current_width, self.current_height), 
                            self.original_pixmap, QRectF(self.original_pixmap.rect()))
        border_pen = QPen(QColor("#000000"), 2)
        painter.setPen(border_pen)
        painter.setBrush(Qt.NoBrush)
        painter.drawRect(0, 0, int(self.current_width), int(self.current_height))
        if self.assigned_role:
            title_text = self.assigned_role.upper()
            font = painter.font()
            font.setBold(True)
            font.setPixelSize(28)
            painter.setFont(font)
            fm = painter.fontMetrics()
            text_w = fm.width(title_text) + 40
            text_h = fm.height() + 10
            scene_y = self.scenePos().y()
            center_x = (self.current_width - text_w) / 2
            if scene_y > 960: 
                draw_y = -text_h - 5
            else:
                draw_y = self.current_height + 15
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor(0, 0, 0, 180))
            painter.drawRoundedRect(QRectF(center_x, draw_y, text_w, text_h), 6, 6)
            painter.setPen(QColor("#f1c40f"))
            painter.drawText(QRectF(center_x, draw_y, text_w, text_h), Qt.AlignCenter, title_text)
        if self.isSelected():
            pen = QPen(QColor("white"), 3, Qt.CustomDashLine)
            pen.setDashPattern([6, 6])
            pen.setDashOffset(self.ant_dash_offset)
            painter.setPen(pen)
            painter.setBrush(Qt.NoBrush)
            painter.drawRect(0, 0, int(self.current_width), int(self.current_height))

    def set_role(self, role):
        if role == "-- Select Role --":
            self.assigned_role = None
        else:
            self.assigned_role = role
        self.update()

    def update_handle_positions(self):
        offset = self.handle_size / 2
        self.handle_br.setPos(self.current_width - offset, self.current_height - offset)
        self.handle_tl.setPos(-offset, -offset)

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
            aspect = self.original_pixmap.width() / self.original_pixmap.height()
            if aspect > 0:
                new_height = new_width / aspect
            if new_width > 20 and new_height > 20:
                self.prepareGeometryChange()
                self.current_width = new_width
                self.current_height = new_height
                self.update_handle_positions()
                self.item_changed.emit()
        elif self.is_resizing_tl:
            delta = event.pos() - self.resize_start_pos
            new_width = self.start_width - delta.x()
            aspect = self.original_pixmap.width() / self.original_pixmap.height()
            if aspect > 0:
                new_height = new_width / aspect
                if new_width > 20 and new_height > 20:
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
            if value: self.ant_timer.start(100)
            else: self.ant_timer.stop()
        elif change == QGraphicsItem.ItemPositionHasChanged:
            self.item_changed.emit()
        return super(ResizablePixmapItem, self).itemChange(change, value)