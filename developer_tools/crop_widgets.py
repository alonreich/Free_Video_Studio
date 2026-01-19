from PyQt5.QtWidgets import QWidget, QMenu, QAction
from PyQt5.QtCore import Qt, QPoint, QRect, pyqtSignal, QRectF
from PyQt5.QtGui import QPainter, QPen, QBrush, QColor, QPixmap, QPainterPath

class DrawWidget(QWidget):
    crop_role_selected = pyqtSignal(object, object, str)

    def __init__(self, parent=None):
        super(DrawWidget, self).__init__(parent)
        self.begin = QPoint()
        self.end = QPoint()
        self.mode = 'none'
        self.resize_edge = None
        self._crop_rect = QRect()
        self.pixmap = QPixmap()
        self._cache_pix = None
        self._cache_size = None
        self._img_rect = QRect()
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.StrongFocus)
        self.handle_size = 10
        self.cursor_pos = QPoint()
        self.all_roles = []
        self.configured_roles = set()

    def set_roles(self, all_roles, configured_roles):
        self.all_roles = all_roles
        self.configured_roles = configured_roles

    def clear_selection(self):
        self._crop_rect = QRect()
        self.update()

    def setImage(self, image_path):
        if image_path is None:
            self.pixmap = QPixmap()
        else:
            self.pixmap = QPixmap(image_path)
        self._cache_pix = None
        self.update()

    def _update_scaled_pixmap(self):
        if self.pixmap.isNull(): return
        if (self._cache_pix is None or self._cache_size != self.size()):
            self._cache_pix = self.pixmap.scaled(
                self.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation
            )
            self._cache_size = self.size()
            x = (self.width() - self._cache_pix.width()) // 2
            y = (self.height() - self._cache_pix.height()) // 2
            self._img_rect = QRect(x, y, self._cache_pix.width(), self._cache_pix.height())

    def get_selection(self):
        if self.pixmap.isNull() or self._crop_rect.isNull() or not self._crop_rect.isValid():
            return None, None
        try:
            self._update_scaled_pixmap()
            x_scale = self.pixmap.width() / float(self._img_rect.width())
            y_scale = self.pixmap.height() / float(self._img_rect.height())
            selection_relative_to_image = self._crop_rect.translated(-self._img_rect.topLeft())
            final_rect = QRect(
                int(round(selection_relative_to_image.x() * x_scale)),
                int(round(selection_relative_to_image.y() * y_scale)),
                int(round(selection_relative_to_image.width() * x_scale)),
                int(round(selection_relative_to_image.height() * y_scale))
            )
            final_rect = final_rect.intersected(self.pixmap.rect())
            if final_rect.width() < 5 or final_rect.height() < 5:
                return None, None
            return self.pixmap.copy(final_rect), final_rect
        except Exception as e:
            print(f"Error extracting selection: {e}")
            return None, None

    def paintEvent(self, event):
        super().paintEvent(event)
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        if not self.pixmap.isNull():
            self._update_scaled_pixmap()
            painter.drawPixmap(self._img_rect.topLeft(), self._cache_pix)
        if not self._crop_rect.isNull():
            path = QPainterPath()
            path.addRect(QRectF(self.rect()))
            path.addRect(QRectF(self._crop_rect))
            painter.setBrush(QColor(0, 0, 0, 120))
            painter.setPen(Qt.NoPen)
            painter.drawPath(path)
        rect_to_draw = QRect(self.begin, self.end).normalized() if self.mode == 'drawing' else self._crop_rect
        if not rect_to_draw.isNull():
            painter.setBrush(Qt.NoBrush)
            pen = QPen(QColor("#10B981"), 2, Qt.SolidLine)
            pen.setJoinStyle(Qt.RoundJoin)
            painter.setPen(pen)
            painter.drawRect(rect_to_draw)
            painter.setBrush(QBrush(QColor(255, 255, 255, 220)))
            painter.setPen(QPen(QColor(0, 0, 0, 180), 1))
            hs = self.handle_size
            for pt in [rect_to_draw.topLeft(), rect_to_draw.topRight(), rect_to_draw.bottomLeft(), rect_to_draw.bottomRight()]:
                painter.drawRect(pt.x() - hs//2, pt.y() - hs//2, hs, hs)
        if self.mode in ['drawing', 'resizing', 'moving'] and not self.pixmap.isNull():
            self.draw_magnifier(painter)
        elif self._crop_rect.isNull() and not self.pixmap.isNull():
            painter.setPen(QColor("#FBBF24"))
            font = painter.font()
            font.setPointSize(24)
            font.setBold(True)
            painter.setFont(font)
            painter.drawText(self.rect(), Qt.AlignCenter, "🎯\n\nCLICK & DRAG TO CROP A HUD ELEMENT")
            font.setPointSize(12)
            font.setBold(False)
            painter.setFont(font)
            painter.setPen(QColor("#9CA3AF"))
            painter.drawText(self.rect().adjusted(0, 80, 0, 0), Qt.AlignCenter, "Draw a box, then select the element type from the pop-up menu.")

    def draw_magnifier(self, painter):
        if self._cache_pix is None or self._cache_pix.isNull(): 
            return
        if self._img_rect.width() <= 0 or self._img_rect.height() <= 0:
            return
        mag_size = 180
        zoom = 2.5
        offset = 60
        cursor_pos = self.cursor_pos
        if not self._img_rect.contains(cursor_pos):
            return
        mag_x, mag_y = cursor_pos.x() + offset, cursor_pos.y() + offset
        if mag_x + mag_size > self.width(): 
            mag_x = cursor_pos.x() - offset - mag_size
        if mag_y + mag_size > self.height(): 
            mag_y = cursor_pos.y() - offset - mag_size
        target_rect = QRect(mag_x, mag_y, mag_size, mag_size)
        painter.save()
        painter.setBrush(QColor("black"))
        painter.setPen(QPen(QColor("#00FF00"), 3))
        painter.drawEllipse(target_rect.center(), mag_size//2, mag_size//2)
        path = QPainterPath()
        path.addEllipse(target_rect.center(), mag_size//2, mag_size//2)
        painter.setClipPath(path)
        rel_x = cursor_pos.x() - self._img_rect.x()
        rel_y = cursor_pos.y() - self._img_rect.y()
        scale_x = self.pixmap.width() / float(self._img_rect.width())
        scale_y = self.pixmap.height() / float(self._img_rect.height())
        src_w = int((mag_size / zoom) / scale_x)
        src_h = int((mag_size / zoom) / scale_y)
        src_center_x = int(rel_x * scale_x)
        src_center_y = int(rel_y * scale_y)
        src_rect = QRect(src_center_x - src_w // 2, src_center_y - src_h // 2, src_w, src_h)
        src_rect = src_rect.intersected(self.pixmap.rect())
        if src_rect.width() > 0 and src_rect.height() > 0:
            painter.drawPixmap(target_rect, self.pixmap, src_rect)
        painter.setClipping(False)
        cursor_in_mag_x = target_rect.center().x()
        cursor_in_mag_y = target_rect.center().y()
        painter.setPen(QPen(QColor("red"), 2))
        painter.drawLine(cursor_in_mag_x-10, cursor_in_mag_y, cursor_in_mag_x+10, cursor_in_mag_y)
        painter.drawLine(cursor_in_mag_x, cursor_in_mag_y-10, cursor_in_mag_x, cursor_in_mag_y+10)
        painter.restore()

    def mousePressEvent(self, event):
        if self.pixmap.isNull(): 
            return
        if event.button() == Qt.LeftButton:
            handle = self.get_handle_at(event.pos())
            if handle:
                self.mode = 'resizing'
                self.resize_edge = handle
            elif self._crop_rect.contains(event.pos()):
                self.mode = 'moving'
                self.last_mouse_pos = event.pos()
            else:
                self.begin = event.pos()
                self.end = event.pos()
                self.mode = 'drawing'
            self.update()

    def mouseMoveEvent(self, event):
        self.cursor_pos = event.pos()
        if self.mode == 'drawing': 
            self.end = event.pos()
        elif self.mode == 'moving':
            if not self._crop_rect.isNull():
                self._crop_rect.translate(event.pos() - self.last_mouse_pos)
                self.last_mouse_pos = event.pos()
        elif self.mode == 'resizing' and self.resize_edge:
            r, p = self._crop_rect, event.pos()
            if not r.isNull():
                if 'left' in self.resize_edge: r.setLeft(min(r.right()-10, p.x()))
                if 'right' in self.resize_edge: r.setRight(max(r.left()+10, p.x()))
                if 'top' in self.resize_edge: r.setTop(min(r.bottom()-10, p.y()))
                if 'bottom' in self.resize_edge: r.setBottom(max(r.top()+10, p.y()))
        self.update()

    def mouseReleaseEvent(self, event):
        try:
            if self.mode == 'drawing':
                self._crop_rect = QRect(self.begin, self.end).normalized()
                if self._crop_rect.width() < 10 or self._crop_rect.height() < 10: 
                    self._crop_rect = QRect()
                else:
                    self.open_identification_menu(event.globalPos())
            self.mode = 'none'
            self.resize_edge = None
            self.update()
        except Exception as e:
            print(f"Error in mouseReleaseEvent: {e}")

            import traceback
            traceback.print_exc()
            self.mode = 'none'
            self.resize_edge = None
            self.update()

    def get_handle_at(self, pos):
        r = self._crop_rect
        if r.isNull(): return None
        if (pos - r.topLeft()).manhattanLength() < 15: return 'top_left'
        if (pos - r.bottomRight()).manhattanLength() < 15: return 'bottom_right'
        return None

    def open_identification_menu(self, global_pos):
        menu = QMenu(self)
        menu.setStyleSheet("""
            QMenu { 
                background-color: #1F2937; 
                color: #F9FAFB; 
                border: 2px solid #2563EB;
                border-radius: 6px;
                padding: 6px;
                font-family: 'Segoe UI', sans-serif;
                font-size: 13px;
                min-width: 220px;
                max-width: 280px;
            }
            QMenu::item { 
                padding: 8px 20px; 
                border-radius: 4px;
                margin: 2px;
                font-weight: 500;
            }
            QMenu::item:selected { 
                background-color: #2563EB; 
                color: white;
                font-weight: 600;
            }
            QMenu::item:disabled {
                color: #9CA3AF;
                background-color: transparent;
                font-weight: normal;
            }
            QMenu::separator {
                height: 1px;
                background-color: #374151;
                margin: 6px 8px;
            }
        """)
        header = QAction("SELECT HUD ELEMENT", self)
        header.setEnabled(False)
        menu.addAction(header)
        menu.addSeparator()

        if not self.all_roles:
            action = QAction("No roles defined.", self)
            action.setEnabled(False)
            menu.addAction(action)
        else:
            for role in self.all_roles:
                is_configured = role in self.configured_roles
                text = role
                if is_configured:
                    text += " (Re-configure)"
                
                action = QAction(text, self)
                action.triggered.connect(lambda checked, r=role: self.confirm_selection(r))
                menu.addAction(action)

        menu.addSeparator()
        cancel_action = QAction("Cancel", self)
        cancel_action.triggered.connect(self.clear_selection)
        menu.addAction(cancel_action)
        menu.exec_(global_pos)

    def confirm_selection(self, role):
        pix, rect = self.get_selection()
        if pix and rect:
            self.crop_role_selected.emit(pix, rect, role)
        else:
            print("Selection invalid during confirmation")
