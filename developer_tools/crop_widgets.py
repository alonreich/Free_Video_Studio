from PyQt5.QtWidgets import QWidget, QFrame, QPushButton, QHBoxLayout
from PyQt5.QtCore import Qt, QPoint, QRect, pyqtSignal, QRectF, QPointF, QSize
from PyQt5.QtGui import QPainter, QPen, QBrush, QColor, QPixmap, QPainterPath

class RoleToolbar(QFrame):
    """A contextual toolbar for selecting a HUD element role."""
    role_selected = pyqtSignal(str)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setLayout(QHBoxLayout())
        self.layout().setContentsMargins(5, 5, 5, 5)
        self.layout().setSpacing(5)
        self.setObjectName("roleToolbar")
        self.setStyleSheet("""
            #roleToolbar {
                background-color: rgba(31, 41, 55, 0.95);
                border: 1px solid #4B5563;
                border-radius: 8px;
            }
            QPushButton {
                background-color: #374151;
                color: #F9FAFB;
                border: 1px solid #4B5563;
                border-radius: 4px;
                padding: 6px 10px;
                font-weight: bold;
                font-size: 11px;
            }
            QPushButton:hover {
                background-color: #4B5563;
            }
            QPushButton:pressed {
                background-color: #1F2937;
            }
        """)

    def set_roles(self, roles, configured_roles):
        while self.layout().count():
            child = self.layout().takeAt(0)
            if child.widget():
                child.widget().deleteLater()
        for role in roles:
            btn = QPushButton(role.upper())
            if role in configured_roles:
                btn.setText(f"{role.upper()} (RE-CROP)")
            btn.clicked.connect(lambda _, r=role: self.role_selected.emit(r))
            self.layout().addWidget(btn)
        self.adjustSize()

class DrawWidget(QWidget):
    crop_role_selected = pyqtSignal(object, object, str)

    def __init__(self, parent=None):
        super(DrawWidget, self).__init__(parent)
        self.mode = 'none'
        self.pixmap = QPixmap()
        self.setMouseTracking(True)
        self._crop_rect_img = QRectF()
        self._drawing_rect_img = QRectF()
        self._drawing_start_img = QPointF()
        self.scroll_area = None
        self.zoom = 1.0
        self._img_rect = QRect()
        self._cache_pix = None
        self._panning = False
        self.role_toolbar = RoleToolbar(self)
        self.role_toolbar.role_selected.connect(self.confirm_selection)
        self.role_toolbar.hide()

    def set_scroll_area(self, scroll_area):
        self.scroll_area = scroll_area

    def set_roles(self, all_roles, configured_roles):
        self.role_toolbar.set_roles(all_roles, configured_roles)

    def clear_selection(self):
        self._crop_rect_img = QRectF()
        self.role_toolbar.hide()
        self.update()
        
    def set_crop_rect(self, rect_f):
        """Public method to apply a crop rectangle from external sources like Magic Wand."""
        self._crop_rect_img = self._clamp_rect_to_image(rect_f)
        self._show_role_toolbar()
        self.update()

    def setImage(self, image_path):
        if image_path:
            self.pixmap = QPixmap(image_path)
            self.zoom = 1.0
            self._update_scaled_pixmap()
        else:
            self.pixmap = QPixmap()
        self.clear_selection()
        self.update()

    def _update_scaled_pixmap(self):
        if self.pixmap.isNull() or not self.scroll_area: return
        viewport_size = self.scroll_area.viewport().size()
        if self.zoom == 1.0:
            scale_x = viewport_size.width() / self.pixmap.width()
            scale_y = viewport_size.height() / self.pixmap.height()
            self.zoom = min(scale_x, scale_y)
        scaled_size = self.pixmap.size() * self.zoom
        self.resize(scaled_size)
        if scaled_size.width() < viewport_size.width():
            x = (viewport_size.width() - scaled_size.width()) // 2
        else:
            x = 0
        if scaled_size.height() < viewport_size.height():
            y = (viewport_size.height() - scaled_size.height()) // 2
        else:
            y = 0
        self._img_rect = QRect(QPoint(x,y), scaled_size)
        self._cache_pix = self.pixmap.scaled(scaled_size, Qt.KeepAspectRatio, Qt.SmoothTransformation)

    def paintEvent(self, event):
        super().paintEvent(event)
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        if not self.pixmap.isNull():
            painter.drawPixmap(self._img_rect.topLeft(), self._cache_pix)
        if not self._crop_rect_img.isNull():
            path = QPainterPath()
            path.addRect(QRectF(self.rect()))
            path.addRect(QRectF(self._map_rect_to_display(self._crop_rect_img)))
            painter.setBrush(QColor(0, 0, 0, 120))
            painter.setPen(Qt.NoPen)
            painter.drawPath(path)
        rect_to_draw_img = self._drawing_rect_img if self.mode == 'drawing' else self._crop_rect_img
        if not rect_to_draw_img.isNull():
            rect_to_draw = self._map_rect_to_display(rect_to_draw_img)
            painter.setBrush(Qt.NoBrush)
            pen = QPen(QColor("#10B981"), 2, Qt.SolidLine)
            painter.setPen(pen)
            painter.drawRect(rect_to_draw)

    def mousePressEvent(self, event):
        if self.pixmap.isNull(): return
        if event.button() == Qt.MiddleButton:
            self._panning = True
            self._pan_start_pos = event.pos()
            self.setCursor(Qt.ClosedHandCursor)
            return
        if event.button() == Qt.LeftButton:
            if self.role_toolbar.isVisible() and self.role_toolbar.geometry().contains(event.pos()):
                return
            self.clear_selection()
            self.mode = 'drawing'
            self._drawing_start_img = self._map_to_image(event.pos())
            self._drawing_rect_img = QRectF(self._drawing_start_img, self._drawing_start_img)
            self.update()

    def mouseMoveEvent(self, event):
        if self._panning:
            delta = event.pos() - self._pan_start_pos
            h_bar = self.scroll_area.horizontalScrollBar()
            v_bar = self.scroll_area.verticalScrollBar()
            h_bar.setValue(h_bar.value() - delta.x())
            v_bar.setValue(v_bar.value() - delta.y())
            self._pan_start_pos = event.pos()
            return
        if self.mode == 'drawing':
            img_pos = self._map_to_image(event.pos())
            self._drawing_rect_img = QRectF(self._drawing_start_img, img_pos).normalized()
            self.update()

    def mouseReleaseEvent(self, event):
        if self._panning and event.button() == Qt.MiddleButton:
            self._panning = False
            self.setCursor(Qt.ArrowCursor)
            return
        if self.mode == 'drawing':
            self.mode = 'none'
            self._crop_rect_img = self._clamp_rect_to_image(self._drawing_rect_img)
            self._drawing_rect_img = QRectF()
            if self._crop_rect_img.width() < 10 or self._crop_rect_img.height() < 10:
                self.clear_selection()
            else:
                self._show_role_toolbar()
        self.update()

    def _show_role_toolbar(self):
        if self._crop_rect_img.isNull(): return
        self.role_toolbar.show()
        crop_display_rect = self._map_rect_to_display(self._crop_rect_img)
        x = crop_display_rect.x()
        y = crop_display_rect.bottom() + 5
        if x + self.role_toolbar.width() > self.width():
            x = self.width() - self.role_toolbar.width()
        if y + self.role_toolbar.height() > self.height():
            y = crop_display_rect.top() - self.role_toolbar.height() - 5
        self.role_toolbar.move(max(0, x), max(0, y))

    def get_selection(self):
        if self.pixmap.isNull() or self._crop_rect_img.isNull() or not self._crop_rect_img.isValid():
            return None, None
        final_rect = self._crop_rect_img.toRect().intersected(self.pixmap.rect())
        if final_rect.width() < 5 or final_rect.height() < 5:
            return None, None
        return self.pixmap.copy(final_rect), final_rect

    def confirm_selection(self, role):
        pix, rect = self.get_selection()
        if pix and rect:
            self.crop_role_selected.emit(pix, rect, role)
        self.clear_selection()

    def _map_to_image(self, widget_pos):
        if self.zoom == 0: return QPointF()
        return QPointF(
            (widget_pos.x() - self._img_rect.x()) / self.zoom,
            (widget_pos.y() - self._img_rect.y()) / self.zoom
        )

    def _map_rect_to_display(self, img_rect):
        return QRectF(
            self._img_rect.x() + img_rect.x() * self.zoom,
            self._img_rect.y() + img_rect.y() * self.zoom,
            img_rect.width() * self.zoom,
            img_rect.height() * self.zoom
        )
        
    def _clamp_rect_to_image(self, rect_f):
        if self.pixmap.isNull(): return rect_f
        img_bounds = QRectF(self.pixmap.rect())
        return rect_f.intersected(img_bounds)

    def handle_key_press(self, event):
        if self._crop_rect_img.isNull():
            return
        offset = QPointF(0, 0)
        move_amount = 1.0
        key = event.key()
        if key == Qt.Key_Up:
            offset.setY(-move_amount)
        elif key == Qt.Key_Down:
            offset.setY(move_amount)
        elif key == Qt.Key_Left:
            offset.setX(-move_amount)
        elif key == Qt.Key_Right:
            offset.setX(move_amount)
        else:
            return
        self._crop_rect_img.translate(offset)
        self._crop_rect_img = self._clamp_rect_to_image(self._crop_rect_img)
        self._show_role_toolbar()
        self.update()
        event.accept()
