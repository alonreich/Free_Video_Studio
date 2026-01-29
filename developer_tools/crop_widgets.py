from PyQt5.QtWidgets import QWidget, QMenu, QAction
from PyQt5.QtCore import Qt, QPoint, QRect, pyqtSignal, QRectF, QPointF, QSize, QTimer
from PyQt5.QtGui import QPainter, QPen, QBrush, QColor, QPixmap, QPainterPath
from config import HUD_ELEMENT_MAPPINGS
from enhanced_logger import get_enhanced_logger

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
        self.zoom = 1.0
        self.min_zoom = 0.25
        self.max_zoom = 4.0
        self.scroll_area = None
        self._panning = False
        self._pan_start = QPoint()
        self._pan_scroll_start = QPoint()
        self._crop_rect_img = QRectF()
        self._drawing_rect_img = QRectF()
        self._drawing_start_img = QPointF()
        self._has_fit = False
        self.confirm_button_rect = None
        self.confirm_button_hover = False
        self.confirm_button_pressed = False
        self.confirm_blink_on = True
        self.confirm_blink_timer = QTimer(self)
        self.confirm_blink_timer.setInterval(900)
        self.confirm_blink_timer.timeout.connect(self._toggle_confirm_blink)
        self.initial_zoom = 1.0
        self.initial_scroll = QPoint(0, 0)
        self._smart_zoom_start_pos_img = QPointF()
        self._smart_zoom_initial_zoom = 1.0
        self._smart_zoom_direction = None
        self._smart_zoom_phase = 0
        self._smart_zoom_threshold = 50

    def set_scroll_area(self, scroll_area):
        self.scroll_area = scroll_area

    def set_roles(self, all_roles, configured_roles):
        self.all_roles = all_roles
        self.configured_roles = configured_roles

    def clear_selection(self):
        self._crop_rect_img = QRectF()
        self._hide_confirm_button()
        self.update()

    def setImage(self, image_path):
        if image_path is None:
            self.pixmap = QPixmap()
        else:
            self.pixmap = QPixmap(image_path)
        self._fit_to_view()
        self._cache_pix = None
        self._crop_rect_img = QRectF()
        self._drawing_rect_img = QRectF()
        self._hide_confirm_button()
        self.update()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self._update_scrollbars()
    
    def showEvent(self, event):
        """Handle widget being shown - ensure image is properly fitted."""
        super().showEvent(event)
        if not self.pixmap.isNull() and not self._has_fit:
            self._fit_to_view()

    def _fit_to_view(self):
        if not self.scroll_area or self.pixmap.isNull():
            self.zoom = 1.0
            return
        viewport = self.scroll_area.viewport().size()
        if viewport.width() <= 0 or viewport.height() <= 0:
            self.zoom = 1.0
            return
        scale_x = viewport.width() / float(self.pixmap.width())
        scale_y = viewport.height() / float(self.pixmap.height())
        self.zoom = min(scale_x, scale_y, 1.0)
        self._has_fit = True
        self._cache_pix = None
        self._update_scaled_pixmap()
        self.initial_zoom = self.zoom
        if self.scroll_area:
            hbar = self.scroll_area.horizontalScrollBar()
            vbar = self.scroll_area.verticalScrollBar()
            self.initial_scroll = QPoint(hbar.value(), vbar.value())

    def _update_scaled_pixmap(self):
        if self.pixmap.isNull(): return
        scaled_w = max(1, int(self.pixmap.width() * self.zoom))
        scaled_h = max(1, int(self.pixmap.height() * self.zoom))
        target_size = QSize(scaled_w, scaled_h)
        if (self._cache_pix is None or self._cache_size != target_size):
            self._cache_pix = self.pixmap.scaled(
                scaled_w, scaled_h, Qt.IgnoreAspectRatio, Qt.SmoothTransformation
            )
            self._cache_size = target_size
            viewport = self.scroll_area.viewport().size() if self.scroll_area else QSize(0, 0)
            img_w = self._cache_pix.width()
            img_h = self._cache_pix.height()
            if viewport.width() > 0 and img_w <= viewport.width() and viewport.height() > 0 and img_h <= viewport.height():
                x = (viewport.width() - img_w) // 2
                y = (viewport.height() - img_h) // 2
                self.resize(viewport.width(), viewport.height())
                self._img_rect = QRect(x, y, img_w, img_h)
            elif viewport.width() > 0 and img_w <= viewport.width():
                x = (viewport.width() - img_w) // 2
                self.resize(viewport.width(), img_h)
                self._img_rect = QRect(x, 0, img_w, img_h)
            elif viewport.height() > 0 and img_h <= viewport.height():
                y = (viewport.height() - img_h) // 2
                self.resize(img_w, viewport.height())
                self._img_rect = QRect(0, y, img_w, img_h)
            else:
                self._img_rect = QRect(0, 0, img_w, img_h)
                self.resize(self._cache_pix.size())
            self._update_scrollbars()

    def _update_scrollbars(self):
        if not self.scroll_area:
            return
        viewport = self.scroll_area.viewport().size()
        hbar = self.scroll_area.horizontalScrollBar()
        vbar = self.scroll_area.verticalScrollBar()
        max_x = max(0, self.width() - viewport.width())
        max_y = max(0, self.height() - viewport.height())
        hbar.setRange(0, max_x)
        vbar.setRange(0, max_y)
        hbar.setPageStep(viewport.width())
        vbar.setPageStep(viewport.height())
        if max_x == 0:
            hbar.setValue(0)
        else:
            hbar.setValue(max(hbar.minimum(), min(hbar.value(), hbar.maximum())))
        if max_y == 0:
            vbar.setValue(0)
        else:
            vbar.setValue(max(vbar.minimum(), min(vbar.value(), vbar.maximum())))

    def _map_to_image(self, pos):
        """Map a widget position to image coordinates.
        Previously this function ignored the offset of the scaled image within the
        widget, causing the selection rectangle to be offset when the image was
        letterboxed or centered. Subtract the image rect top-left before
        dividing by the zoom level.
        """
        return QPointF(
            (pos.x() - self._img_rect.x()) / self.zoom,
            (pos.y() - self._img_rect.y()) / self.zoom
        )
    
    def _map_rect_to_display(self, rect_img):
        """Map an image-space rectangle to widget coordinates.
        The previous implementation did not account for the offset of the
        displayed image within the widget. We add the top-left offset of
        `_img_rect` so that the display rectangle aligns with the drawn
        pixmap.
        """
        return QRect(
            int(self._img_rect.x() + rect_img.x() * self.zoom),
            int(self._img_rect.y() + rect_img.y() * self.zoom),
            int(rect_img.width() * self.zoom),
            int(rect_img.height() * self.zoom)
        )

    def _clamp_rect_to_image(self, rect_img):
        if self.pixmap.isNull():
            return rect_img
        img_rect = QRectF(0, 0, self.pixmap.width(), self.pixmap.height())
        clamped = rect_img.intersected(img_rect)
        if clamped.isNull():
            return QRectF()
        return clamped

    def get_selection(self):
        if self.pixmap.isNull() or self._crop_rect_img.isNull() or not self._crop_rect_img.isValid():
            return None, None
        try:
            final_rect = self._crop_rect_img.toRect()
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
        if not self._crop_rect_img.isNull():
            path = QPainterPath()
            path.addRect(QRectF(self.rect()))
            path.addRect(QRectF(self._map_rect_to_display(self._crop_rect_img)))
            painter.setBrush(QColor(0, 0, 0, 120))
            painter.setPen(Qt.NoPen)
            painter.drawPath(path)
        rect_to_draw_img = self._drawing_rect_img if self.mode == 'drawing' else self._crop_rect_img
        rect_to_draw = self._map_rect_to_display(rect_to_draw_img) if not rect_to_draw_img.isNull() else QRect()
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
            if self._should_draw_confirm_button():
                self._draw_confirm_button(painter, rect_to_draw)
        if self.mode in ['drawing', 'resizing', 'moving'] and not self.pixmap.isNull():
            self.draw_magnifier(painter)
        elif self._crop_rect_img.isNull() and not self.pixmap.isNull():
            overlay_rect = QRect(0, 0, 820, 260)
            overlay_rect.moveCenter(self.rect().center())
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor("#000000"))
            painter.drawRoundedRect(overlay_rect, 12, 12)
            painter.setPen(QColor("#FBBF24"))
            font = painter.font()
            font.setPointSize(20)
            font.setBold(True)
            painter.setFont(font)
            instruction_text = (
                "🎯\n\n"
                "CLICK & DRAG TO CROP A HUD ELEMENT\n"
                "Right click inside the box to select HUD element\n"
                "Press ESC to return to video"
            )
            painter.drawText(overlay_rect, Qt.AlignCenter | Qt.TextWordWrap, instruction_text)

    def draw_magnifier(self, painter):
        if self._cache_pix is None or self._cache_pix.isNull(): 
            return
        if self._img_rect.width() <= 0 or self._img_rect.height() <= 0:
            return
        mag_size = 180
        zoom = 3.0
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
        if event.button() == Qt.LeftButton and self._hit_confirm_button(event.pos()):
            self.confirm_button_pressed = True
            self.update()
            return
        if event.button() == Qt.RightButton and self._should_open_menu_on_click(event.pos()):
            self.open_identification_menu(event.globalPos())
            return
        if event.button() == Qt.MiddleButton and self.scroll_area:
            self._panning = True
            self._pan_start = event.pos()
            self._pan_scroll_start = QPoint(
                self.scroll_area.horizontalScrollBar().value(),
                self.scroll_area.verticalScrollBar().value()
            )
            self.setCursor(Qt.ClosedHandCursor)
            return
        if event.button() == Qt.LeftButton:
            handle = self.get_handle_at(event.pos())
            if handle:
                self.mode = 'resizing'
                self.resize_edge = handle
            elif not self._crop_rect_img.isNull() and self._map_rect_to_display(self._crop_rect_img).contains(event.pos()):
                self.mode = 'moving'
                self.last_mouse_pos = event.pos()
            else:
                img_pos = self._map_to_image(event.pos())
                self.begin = event.pos()
                self.end = event.pos()
                self._drawing_start_img = img_pos
                self._drawing_rect_img = QRectF(img_pos, img_pos)
                self.mode = 'drawing'
            self.update()

    def mouseMoveEvent(self, event):
        self.cursor_pos = event.pos()
        handle = self.get_handle_at(event.pos())
        if handle:
            self.setCursor(Qt.SizeFDiagCursor)
        elif self._hit_confirm_button(event.pos()):
            self.confirm_button_hover = True
            if not self._panning:
                self.setCursor(Qt.PointingHandCursor)
        else:
            if self.confirm_button_hover:
                self.confirm_button_hover = False
            if not self._panning and not handle:
                self.setCursor(Qt.ArrowCursor)
        if self._panning and self.scroll_area:
            delta = event.pos() - self._pan_start
            self.scroll_area.horizontalScrollBar().setValue(self._pan_scroll_start.x() - delta.x())
            self.scroll_area.verticalScrollBar().setValue(self._pan_scroll_start.y() - delta.y())
            self._clamp_scroll()
            return
        if self.mode == 'drawing': 
            self.end = event.pos()
            img_pos = self._map_to_image(event.pos())
            self._drawing_rect_img = QRectF(self._drawing_start_img, img_pos).normalized()
        elif self.mode == 'moving':
            if not self._crop_rect_img.isNull():
                delta = (event.pos() - self.last_mouse_pos)
                delta_img = QPointF(delta.x() / self.zoom, delta.y() / self.zoom)
                self._crop_rect_img.translate(delta_img)
                self._crop_rect_img = self._clamp_rect_to_image(self._crop_rect_img)
                self.last_mouse_pos = event.pos()
        elif self.mode == 'resizing' and self.resize_edge:
            r, p = self._crop_rect_img, self._map_to_image(event.pos())
            if not r.isNull():
                if 'left' in self.resize_edge: r.setLeft(min(r.right()-10, p.x()))
                if 'right' in self.resize_edge: r.setRight(max(r.left()+10, p.x()))
                if 'top' in self.resize_edge: r.setTop(min(r.bottom()-10, p.y()))
                if 'bottom' in self.resize_edge: r.setBottom(max(r.top()+10, p.y()))
                self._crop_rect_img = self._clamp_rect_to_image(r)
        self.update()

    def mouseReleaseEvent(self, event):
        try:
            if self.confirm_button_pressed and event.button() == Qt.LeftButton:
                self.confirm_button_pressed = False
                if self._hit_confirm_button(event.pos()):
                    self.open_identification_menu(self.mapToGlobal(self.confirm_button_rect.center()))
                self.update()
                return
            if self._panning and event.button() == Qt.MiddleButton:
                self._panning = False
                self.setCursor(Qt.ArrowCursor)
                return
            if self.mode == 'drawing':
                self._crop_rect_img = self._drawing_rect_img.normalized()
                if self._crop_rect_img.width() < 10 or self._crop_rect_img.height() < 10: 
                    self._crop_rect_img = QRectF()
                    self._hide_confirm_button()
                else:
                    self._show_confirm_button()
                    self._zoom_to_crop()
            self.mode = 'none'
            self.resize_edge = None
            self._drawing_rect_img = QRectF()
            self.update()
        except Exception as e:
            print(f"Error in mouseReleaseEvent: {e}")

            import traceback
            traceback.print_exc()
            self.mode = 'none'
            self.resize_edge = None
            self.update()

    def get_handle_at(self, pos):
        r = self._map_rect_to_display(self._crop_rect_img)
        if r.isNull():
            return None
        if (pos - r.topLeft()).manhattanLength() < 15:
            return 'top_left'
        if (pos - r.topRight()).manhattanLength() < 15:
            return 'top_right'
        if (pos - r.bottomLeft()).manhattanLength() < 15:
            return 'bottom_left'
        if (pos - r.bottomRight()).manhattanLength() < 15:
            return 'bottom_right'
        return None

    def keyPressEvent(self, event):
        if event.key() == Qt.Key_Escape:
            parent_window = self.window()
            if hasattr(parent_window, 'show_video_view'):
                parent_window.show_video_view()
            self.clear_selection()
            return
        if self._crop_rect_img.isNull() or not self._crop_rect_img.isValid():
            return super().keyPressEvent(event)
        delta = 1
        if event.key() == Qt.Key_Up:
            self._crop_rect_img.translate(0, -delta)
        elif event.key() == Qt.Key_Down:
            self._crop_rect_img.translate(0, delta)
        elif event.key() == Qt.Key_Left:
            self._crop_rect_img.translate(-delta, 0)
        elif event.key() == Qt.Key_Right:
            self._crop_rect_img.translate(delta, 0)
        else:
            return super().keyPressEvent(event)
        self._crop_rect_img = self._clamp_rect_to_image(self._crop_rect_img)
        self._show_confirm_button()
        self.update()

    def wheelEvent(self, event):
        if self.pixmap.isNull():
            return
        if not self.scroll_area:
            return
        angle = event.angleDelta().y()
        if angle == 0:
            return
        zoom_factor = 1.1 if angle > 0 else 1 / 1.1
        new_zoom = max(self.min_zoom, min(self.max_zoom, self.zoom * zoom_factor))
        if new_zoom == self.zoom:
            return
        cursor_pos = event.pos()
        img_pos = QPointF(cursor_pos.x() / self.zoom, cursor_pos.y() / self.zoom)
        self.zoom = new_zoom
        self._cache_pix = None
        self._update_scaled_pixmap()
        new_cursor_pos = QPointF(img_pos.x() * self.zoom, img_pos.y() * self.zoom)
        hbar = self.scroll_area.horizontalScrollBar()
        vbar = self.scroll_area.verticalScrollBar()
        hbar.setValue(int(new_cursor_pos.x() - cursor_pos.x()))
        vbar.setValue(int(new_cursor_pos.y() - cursor_pos.y()))
        self._clamp_scroll()
        self.update()

    def _toggle_confirm_blink(self):
        self.confirm_blink_on = not self.confirm_blink_on
        if self._should_draw_confirm_button():
            self.update()

    def _show_confirm_button(self):
        if not self.confirm_blink_timer.isActive():
            self.confirm_blink_timer.start()
        self.confirm_blink_on = True

    def _hide_confirm_button(self):
        self.confirm_button_rect = None
        self.confirm_button_hover = False
        self.confirm_button_pressed = False
        self.confirm_blink_timer.stop()

    def _zoom_to_crop(self):
        """Zoom and center view on the crop rectangle - less aggressive."""
        if self._crop_rect_img.isNull() or not self.scroll_area:
            return
        viewport = self.scroll_area.viewport().size()
        if viewport.width() <= 0 or viewport.height() <= 0:
            return
        target_zoom_x = viewport.width() * 0.4 / max(10, self._crop_rect_img.width())
        target_zoom_y = viewport.height() * 0.4 / max(10, self._crop_rect_img.height())
        target_zoom = min(target_zoom_x, target_zoom_y, self.max_zoom)
        current_crop_width_display = self._crop_rect_img.width() * self.zoom
        current_crop_height_display = self._crop_rect_img.height() * self.zoom
        if (current_crop_width_display > viewport.width() * 0.3 and 
            current_crop_height_display > viewport.height() * 0.3):
            self._center_on_crop()
            return
        target_zoom = max(self.min_zoom, min(self.max_zoom, target_zoom))
        if abs(target_zoom - self.zoom) > self.zoom * 0.1:
            self.zoom = target_zoom
            self._cache_pix = None
            self._update_scaled_pixmap()
        self._center_on_crop()
        self.update()

    def _center_on_crop(self):
        """Center scrollbars on the crop rectangle with smart edge handling."""
        if self._crop_rect_img.isNull() or not self.scroll_area:
            return
        rect_display = self._map_rect_to_display(self._crop_rect_img)
        viewport = self.scroll_area.viewport().size()
        hbar = self.scroll_area.horizontalScrollBar()
        vbar = self.scroll_area.verticalScrollBar()
        ideal_x = rect_display.center().x() - viewport.width() / 2.0
        ideal_y = rect_display.center().y() - viewport.height() / 2.0
        max_scroll_x = max(0, self.width() - viewport.width())
        max_scroll_y = max(0, self.height() - viewport.height())
        target_x = int(max(0, min(ideal_x, max_scroll_x)))
        target_y = int(max(0, min(ideal_y, max_scroll_y)))
        hbar.setValue(target_x)
        vbar.setValue(target_y)

    def _clamp_scroll(self):
        """Ensure scrollbars do not show void (empty space) beyond image edges."""
        if not self.scroll_area or self.pixmap.isNull():
            return
        viewport = self.scroll_area.viewport().size()
        hbar = self.scroll_area.horizontalScrollBar()
        vbar = self.scroll_area.verticalScrollBar()
        img_w = self._img_rect.width()
        img_h = self._img_rect.height()
        if img_w <= viewport.width():
            hbar.setValue(0)
        else:
            hbar.setValue(max(hbar.minimum(), min(hbar.value(), hbar.maximum())))
        if img_h <= viewport.height():
            vbar.setValue(0)
        else:
            vbar.setValue(max(vbar.minimum(), min(vbar.value(), vbar.maximum())))

    def _should_draw_confirm_button(self):
        return not self._crop_rect_img.isNull() and self.confirm_blink_on

    def _hit_confirm_button(self, pos):
        return self.confirm_button_rect is not None and self.confirm_button_rect.contains(pos)

    def _should_open_menu_on_click(self, pos):
        if self._crop_rect_img.isNull():
            return False
        return self._map_rect_to_display(self._crop_rect_img).contains(pos)

    def _draw_confirm_button(self, painter, rect_to_draw):
        button_size = 60
        offset = 40
        center = rect_to_draw.center()
        width_mid = self.width() / 2.0
        height_mid = self.height() / 2.0
        if center.x() >= width_mid and center.y() >= height_mid:
            button_x = rect_to_draw.left() - offset - button_size
            button_y = rect_to_draw.top() - offset - button_size
        elif center.x() < width_mid and center.y() >= height_mid:
            button_x = rect_to_draw.right() + offset
            button_y = rect_to_draw.top() - offset - button_size
        elif center.x() < width_mid and center.y() < height_mid:
            button_x = rect_to_draw.right() + offset
            button_y = rect_to_draw.bottom() + offset
        else:
            button_x = rect_to_draw.left() - offset - button_size
            button_y = rect_to_draw.bottom() + offset
        button_x = max(2, min(int(button_x), self.width() - button_size - 2))
        button_y = max(2, min(int(button_y), self.height() - button_size - 2))
        self.confirm_button_rect = QRect(button_x, button_y, button_size, button_size)
        base_color = QColor("#22C55E")
        dark_color = QColor("#15803D")
        light_color = QColor("#4ADE80")
        if self.confirm_button_pressed:
            top_left = dark_color
            bottom_right = light_color
            text_offset = 2
        else:
            top_left = light_color
            bottom_right = dark_color
            text_offset = 0
        painter.setPen(Qt.NoPen)
        painter.setBrush(base_color)
        painter.drawRoundedRect(self.confirm_button_rect, 12, 12)
        painter.setPen(QPen(top_left, 3))
        painter.drawLine(self.confirm_button_rect.topLeft(), self.confirm_button_rect.topRight())
        painter.drawLine(self.confirm_button_rect.topLeft(), self.confirm_button_rect.bottomLeft())
        painter.setPen(QPen(bottom_right, 3))
        painter.drawLine(self.confirm_button_rect.bottomLeft(), self.confirm_button_rect.bottomRight())
        painter.drawLine(self.confirm_button_rect.topRight(), self.confirm_button_rect.bottomRight())
        painter.setPen(QPen(QColor("#F0FDF4"), 5))
        check_rect = self.confirm_button_rect.adjusted(16 + text_offset, 20 + text_offset, -16 + text_offset, -12 + text_offset)
        painter.drawLine(check_rect.left(), check_rect.center().y(), check_rect.center().x() - 4, check_rect.bottom())
        painter.drawLine(check_rect.center().x() - 4, check_rect.bottom(), check_rect.right(), check_rect.top())

    def suggest_role_from_rect(self, rect_img):
        """Suggest a HUD element based on the rectangle's position in the image."""
        if self.pixmap.isNull():
            return None
        img_width = self.pixmap.width()
        img_height = self.pixmap.height()
        center_x = rect_img.center().x() / img_width
        center_y = rect_img.center().y() / img_height
        if center_x > 0.7 and center_y > 0.7:
            return "Loot Area"
        elif center_x < 0.3 and center_y > 0.7:
            return "Normal HP"
        elif center_x > 0.7 and center_y < 0.3:
            return "Minimap"
        elif center_x < 0.3 and center_y < 0.3:
            return "Teammates"
        else:
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
            suggested_role = None
            if not self._crop_rect_img.isNull():
                suggested_role = self.suggest_role_from_rect(self._crop_rect_img)
                enhanced_logger = get_enhanced_logger()
                if enhanced_logger and enhanced_logger.crop_logger:
                    enhanced_logger.crop_logger.info(f"AUTO-DETECTION SUGGESTION: {suggested_role} based on position")
            for role in self.all_roles:
                is_configured = role in self.configured_roles
                text = role
                if is_configured:
                    text += " (Re-configure)"
                if role == suggested_role:
                    text = "★ " + text
                action = QAction(text, self)
                action.triggered.connect(lambda checked, r=role: self.confirm_selection(r))
                menu.addAction(action)
        menu.addSeparator()
        cancel_action = QAction("Cancel", self)
        cancel_action.triggered.connect(self.clear_selection)
        menu.addAction(cancel_action)
        menu.exec_(global_pos)

    def reset_view_to_initial(self):
        """Reset zoom and scroll position to initial state."""
        if not self.scroll_area:
            return
        self.zoom = self.initial_zoom
        self._cache_pix = None
        self._update_scaled_pixmap()
        hbar = self.scroll_area.horizontalScrollBar()
        vbar = self.scroll_area.verticalScrollBar()
        hbar.setValue(self.initial_scroll.x())
        vbar.setValue(self.initial_scroll.y())
        self.update()

    def _smart_zoom_start(self, img_pos):
        """Start smart autozoom on drawing initiation."""
        if not self.scroll_area or self.pixmap.isNull():
            return
        self._smart_zoom_start_pos_img = img_pos
        self._smart_zoom_initial_zoom = self.zoom
        self._smart_zoom_direction = None
        self._smart_zoom_phase = 0
        new_zoom = min(self.max_zoom, self.zoom * 1.2)
        if abs(new_zoom - self.zoom) > 0.01:
            self.zoom = new_zoom
            self._cache_pix = None
            self._update_scaled_pixmap()
            self._center_on_point(img_pos)
            self._smart_zoom_phase = 1
            self.update()

    def _smart_zoom_update(self, current_img_pos):
        """Update smart autozoom based on current drawing position."""
        if self._smart_zoom_phase != 1:
            return
        dx = current_img_pos.x() - self._smart_zoom_start_pos_img.x()
        dy = current_img_pos.y() - self._smart_zoom_start_pos_img.y()
        distance = (dx*dx + dy*dy) ** 0.5
        if distance < self._smart_zoom_threshold:
            return
        if abs(dx) > abs(dy):
            direction = 'right' if dx > 0 else 'left'
        else:
            direction = 'down' if dy > 0 else 'up'
        if self._smart_zoom_direction != direction:
            self._smart_zoom_direction = direction
            self._smart_zoom_apply(direction, current_img_pos)

    def _smart_zoom_apply(self, direction, current_img_pos):
        """Apply second zoom (additional 20%) and adjust scroll towards direction."""
        if self._smart_zoom_phase != 1:
            return
        new_zoom = min(self.max_zoom, self._smart_zoom_initial_zoom * 1.44)
        if abs(new_zoom - self.zoom) > 0.01:
            self.zoom = new_zoom
            self._cache_pix = None
            self._update_scaled_pixmap()
        self._focus_towards_direction(direction, current_img_pos)
        self._smart_zoom_phase = 2
        self.update()

    def _center_on_point(self, img_pos):
        """Center view on a specific image point."""
        if not self.scroll_area:
            return
        display_pos = QPointF(img_pos.x() * self.zoom, img_pos.y() * self.zoom)
        viewport = self.scroll_area.viewport().size()
        hbar = self.scroll_area.horizontalScrollBar()
        vbar = self.scroll_area.verticalScrollBar()
        target_x = display_pos.x() - viewport.width() // 2
        target_y = display_pos.y() - viewport.height() // 2
        hbar.setValue(max(hbar.minimum(), min(target_x, hbar.maximum())))
        vbar.setValue(max(vbar.minimum(), min(target_y, vbar.maximum())))

    def _focus_towards_direction(self, direction, current_img_pos):
        """Adjust scroll to focus from start point towards the drawing direction."""
        if not self.scroll_area:
            return
        viewport = self.scroll_area.viewport().size()
        hbar = self.scroll_area.horizontalScrollBar()
        vbar = self.scroll_area.verticalScrollBar()
        start_display = QPointF(
            self._smart_zoom_start_pos_img.x() * self.zoom,
            self._smart_zoom_start_pos_img.y() * self.zoom
        )
        current_display = QPointF(
            current_img_pos.x() * self.zoom,
            current_img_pos.y() * self.zoom
        )
        if direction == 'right':
            target_x = start_display.x() - viewport.width() * 0.25
        elif direction == 'left':
            target_x = start_display.x() - viewport.width() * 0.75
        else:
            target_x = start_display.x() - viewport.width() // 2
        if direction == 'down':
            target_y = start_display.y() - viewport.height() * 0.25
        elif direction == 'up':
            target_y = start_display.y() - viewport.height() * 0.75
        else:
            target_y = start_display.y() - viewport.height() // 2
        current_x = hbar.value()
        current_y = vbar.value()
        new_x = int(current_x * 0.7 + target_x * 0.3)
        new_y = int(current_y * 0.7 + target_y * 0.3)
        hbar.setValue(max(hbar.minimum(), min(new_x, hbar.maximum())))
        vbar.setValue(max(vbar.minimum(), min(new_y, vbar.maximum())))

    def _smart_zoom_reset(self):
        """Reset smart autozoom state."""
        self._smart_zoom_start_pos_img = QPointF()
        self._smart_zoom_initial_zoom = 1.0
        self._smart_zoom_direction = None
        self._smart_zoom_phase = 0

    def confirm_selection(self, role):
        pix, rect = self.get_selection()
        if pix and rect:
            self.crop_role_selected.emit(pix, rect, role)
            self.reset_view_to_initial()
        else:
            print("Selection invalid during confirmation")
