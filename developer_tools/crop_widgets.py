from PyQt5.QtWidgets import QWidget, QFrame, QPushButton, QVBoxLayout, QHBoxLayout, QMenu, QAction, QApplication
from PyQt5.QtCore import Qt, QPoint, QRect, pyqtSignal, QRectF, QPointF, QSize, QSizeF, QTimer
from PyQt5.QtGui import QPainter, QPen, QBrush, QColor, QPixmap, QPainterPath, QCursor
from config import UI_BEHAVIOR, UI_LAYOUT, HUD_ELEMENT_MAPPINGS

class RoleToolbar(QFrame):
    """A contextual toolbar for selecting a HUD element role."""
    role_selected = pyqtSignal(str)

    def __init__(self, parent=None):
        super().__init__(parent, Qt.Tool | Qt.FramelessWindowHint | Qt.WindowDoesNotAcceptFocus)
        self.setLayout(QVBoxLayout())
        self.layout().setContentsMargins(6, 6, 6, 6)
        self.layout().setSpacing(8)
        self.setObjectName("roleToolbar")
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setFocusPolicy(Qt.NoFocus)

        from PyQt5.QtWidgets import QGraphicsDropShadowEffect
        self.shadow = QGraphicsDropShadowEffect(self)
        self.shadow.setBlurRadius(15)
        self.shadow.setColor(QColor(0, 0, 0, 180))
        self.shadow.setOffset(0, 0)
        self.setGraphicsEffect(self.shadow)
        self.setStyleSheet("""
            #roleToolbar {
                background-color: rgba(31, 41, 55, 0.98);
                border: 2px solid #4B5563;
                border-radius: 8px;
            }
            QPushButton {
                background-color: #374151;
                color: #F9FAFB;
                border: 1px solid #4B5563;
                border-radius: 4px;
                padding: 4px 12px;
                font-weight: bold;
                font-size: 11px;
                min-height: 34px;
                max-height: 34px;
                text-align: left;
            }
            QPushButton:hover {
                background-color: #4B5563;
                border-color: #60A5FA;
            }
            QPushButton:pressed {
                background-color: #1F2937;
                padding-top: 2px;
                padding-left: 14px;
            }
        """)

    def set_roles(self, roles, configured_roles):
        while self.layout().count():
            child = self.layout().takeAt(0)
            if child.widget():
                child.widget().deleteLater()
        for role in roles:
            display_text = role.upper()
            btn = QPushButton(display_text)
            btn.setFixedHeight(34)
            btn.setCursor(Qt.PointingHandCursor)

            def make_handler(r):
                return lambda: self._on_btn_clicked(r)
            btn.clicked.connect(make_handler(role))
            self.layout().addWidget(btn)
        self.adjustSize()

    def _on_btn_clicked(self, role):
        self.role_selected.emit(role)
        self.hide()

    def set_roles_with_priority(self, roles, configured_roles, primary_role=None):
        if primary_role and primary_role in roles:
            ordered = [primary_role] + [r for r in roles if r != primary_role]
        else:
            ordered = list(roles)
        self.set_roles(ordered, configured_roles)

class DrawWidget(QWidget):
    crop_role_selected = pyqtSignal(object, object, str)

    def __init__(self, parent=None):
        super(DrawWidget, self).__init__(parent)
        self.mode = 'none'
        self.pixmap = QPixmap()
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.StrongFocus)
        self._crop_rect_img = QRectF()
        self._drawing_rect_img = QRectF()
        self._drawing_start_img = QPointF()
        self.scroll_area = None
        self.zoom = 1.0
        self._img_rect = QRect()
        self._cache_pix = None
        self._panning = False
        self._last_mouse_release_pos = QPoint()
        self._crosshair_pos = None
        self._resizing_selection = False
        self._resize_corner = None
        self._moving_selection = False
        self._move_start_offset_img = QPointF()
        self._selection_display_rect = QRectF()
        self._selection_handle_size = 14
        self.role_toolbar = RoleToolbar(self.window())
        self.role_toolbar.role_selected.connect(self.confirm_selection)
        self._role_menu = QMenu(self)
        self._role_menu.triggered.connect(self._handle_role_menu_action)
        self._role_popup_timer = QTimer(self)
        self._role_popup_timer.setSingleShot(True)
        self._role_popup_timer.timeout.connect(self._show_role_toolbar)

    def set_scroll_area(self, scroll_area):
        self.scroll_area = scroll_area

    def set_roles(self, all_roles, configured_roles):
        if not all_roles:
            all_roles = list(HUD_ELEMENT_MAPPINGS.values())
        self._all_roles = list(all_roles)
        self._configured_roles = set(configured_roles)
        self.role_toolbar.set_roles(self._all_roles, self._configured_roles)
        if self._all_roles:
            self._primary_role = next((r for r in self._all_roles if r not in self._configured_roles), self._all_roles[0])

    def clear_selection(self):
        self._crop_rect_img = QRectF()
        self._selection_display_rect = QRectF()
        self._resizing_selection = False
        self._resize_corner = None
        self.role_toolbar.hide()
        self._last_mouse_release_pos = QPoint()
        self._user_zoomed = False
        self.update()
        
    def set_crop_rect(self, rect_f):
        """Public method to apply a crop rectangle from external sources like Magic Wand."""
        self._crop_rect_img = self._clamp_rect_to_image(rect_f)
        center_img = rect_f.center()
        self._last_mouse_release_pos = self._map_rect_to_display(QRectF(center_img.x(), center_img.y(), 0, 0)).topLeft().toPoint()
        self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)
        self._auto_zoom_to_selection()
        self._show_role_toolbar()
        self.update()

    def setImage(self, image_path):
        if image_path:
            self.pixmap = QPixmap(image_path)
            self.zoom = 1.0
            self.setCursor(Qt.CrossCursor)
            self._user_zoomed = False
            QTimer.singleShot(50, self._update_scaled_pixmap)
        else:
            self.pixmap = QPixmap()
            self.setCursor(Qt.ArrowCursor)
        self.clear_selection()
        self.update()

    def _update_scaled_pixmap(self):
        if self.pixmap.isNull() or not self.scroll_area: return
        viewport_size = self.scroll_area.viewport().size()
        if self.zoom == 1.0:
            available_width = viewport_size.width() - 10
            if available_width <= 0: available_width = viewport_size.width()
            available_height = viewport_size.height() - 10
            if available_height <= 0: available_height = viewport_size.height()
            scale_x = available_width / self.pixmap.width()
            scale_y = available_height / self.pixmap.height()
            self.zoom = min(scale_x, scale_y)
        scaled_size = self.pixmap.size() * self.zoom
        self.resize(scaled_size)
        self._img_rect = QRect(QPoint(0, 0), scaled_size)
        self._cache_pix = self.pixmap.scaled(scaled_size, Qt.KeepAspectRatio, Qt.SmoothTransformation)
        if not self._crop_rect_img.isNull():
            self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)

    def wheelEvent(self, event):
        if self.pixmap.isNull(): return
        angle = event.angleDelta().y()
        if angle == 0: return
        viewport_size = self.scroll_area.viewport().size() if self.scroll_area else self.size()
        min_allowed_zoom = 0.1
        if self.pixmap.width() > 0 and self.pixmap.height() > 0:
            scale_w = viewport_size.width() / self.pixmap.width()
            scale_h = viewport_size.height() / self.pixmap.height()
            min_allowed_zoom = min(scale_w, scale_h)
        factor = 1.1 if angle > 0 else 1/1.1
        new_zoom = self.zoom * factor
        new_zoom = max(min_allowed_zoom, min(10.0, new_zoom))
        if new_zoom != self.zoom:
            self.zoom = new_zoom
            self._user_zoomed = True
            self._update_scaled_pixmap()
            self.update()

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
            if self.mode != 'drawing':
                self._selection_display_rect = rect_to_draw
                self._draw_selection_handles(painter, rect_to_draw)
        if self._crosshair_pos and (self.mode == 'drawing' or self._crop_rect_img.isNull()):
            self._draw_crosshair(painter, self._crosshair_pos)

    def _draw_selection_handles(self, painter, rect):
        size = self._selection_handle_size
        half = size / 2
        painter.setBrush(QColor("#c52c2c"))
        painter.setPen(QPen(Qt.white, 1))
        corners = [
            rect.topLeft(),
            rect.topRight(),
            rect.bottomLeft(),
            rect.bottomRight()
        ]
        for corner in corners:
            painter.drawRect(QRectF(corner.x() - half, corner.y() - half, size, size))

    def _draw_crosshair(self, painter, pos):
        pen = QPen(QColor(0, 255, 255, 100), 1, Qt.DashLine)
        painter.setPen(pen)
        painter.drawLine(0, pos.y(), self.width(), pos.y())
        painter.drawLine(pos.x(), 0, pos.x(), self.height())
        painter.setPen(QPen(QColor(0, 255, 255, 180), 1))
        painter.drawLine(pos.x() - 10, pos.y(), pos.x() + 10, pos.y())
        painter.drawLine(pos.x(), pos.y() - 10, pos.x(), pos.y() + 10)

    def mousePressEvent(self, event):
        if self.pixmap.isNull(): return
        if event.button() == Qt.MiddleButton:
            self._panning = True
            self._pan_start_pos = event.pos()
            self.setCursor(Qt.ClosedHandCursor)
            return
        if event.button() == Qt.LeftButton:
            self._role_popup_timer.stop()
            if self.role_toolbar.isVisible() and self.role_toolbar.geometry().contains(self.mapToGlobal(event.pos())):
                return
            corner = None
            if not self._selection_display_rect.isNull():
                corner = self._hit_test_selection_handle(event.pos())
            if corner:
                self._resizing_selection = True
                self._resize_corner = corner
                self._resize_anchor_img = self._crop_rect_img
                self._apply_resize_cursor(corner)
                return
            if not self._selection_display_rect.isNull() and self._selection_display_rect.contains(event.pos()):
                self._moving_selection = True
                click_pos_img = self._map_to_image(event.pos())
                self._move_start_offset_img = QPointF(
                    click_pos_img.x() - self._crop_rect_img.x(),
                    click_pos_img.y() - self._crop_rect_img.y()
                )
                self.setCursor(Qt.ClosedHandCursor)
                return
            top_level = self.window()
            if hasattr(top_level, 'enhanced_logger') and top_level.enhanced_logger:
                top_level.enhanced_logger.log_user_action("Start Rubberband Selection", f"Mouse Pos: ({event.pos().x()}, {event.pos().y()})")
            self.clear_selection()
            self.mode = 'drawing'
            self.grabMouse()
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
        self._crosshair_pos = event.pos()
        if self._moving_selection:
            new_pos_img = self._map_to_image(event.pos())
            new_rect = QRectF(
                new_pos_img.x() - self._move_start_offset_img.x(),
                new_pos_img.y() - self._move_start_offset_img.y(),
                self._crop_rect_img.width(),
                self._crop_rect_img.height()
            )
            self._crop_rect_img = self._clamp_rect_to_image(new_rect)
            self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)
            if self.role_toolbar.isVisible():
                self._show_role_toolbar(move_only=True)
            self.update()
            return
        if not self._resizing_selection and not self._selection_display_rect.isNull():
            corner = self._hit_test_selection_handle(event.pos())
            if corner:
                self._apply_resize_cursor(corner)
            else:
                self.setCursor(Qt.CrossCursor)
        if self._resizing_selection and not self._crop_rect_img.isNull():
            self._resize_selection(event.pos())
            self._apply_resize_cursor(self._resize_corner)
            if self.role_toolbar.isVisible():
                self._show_role_toolbar(move_only=True)
            self.update()
            return
        if self.mode == 'drawing':
            img_pos = self._map_to_image(event.pos())
            self._drawing_rect_img = QRectF(self._drawing_start_img, img_pos).normalized()
            self.update()

    def mouseReleaseEvent(self, event):
        try:
            if self._panning and event.button() == Qt.MiddleButton:
                self._panning = False
                self.setCursor(Qt.ArrowCursor)
                return
            if self._moving_selection:
                self._moving_selection = False
                self.setCursor(Qt.CrossCursor)
                if not self._crop_rect_img.isNull():
                    self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)
                    self._last_mouse_release_pos = event.pos()
                    self._role_popup_timer.start(50)
                self.update()
                return
            if self._resizing_selection:
                self._resizing_selection = False
                self._resize_corner = None
                if not self._crop_rect_img.isNull():
                    self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)
                    self._last_mouse_release_pos = event.pos()
                    self._auto_zoom_to_selection()
                    self._role_popup_timer.start(100)
                self.update()
                return
            if self.mode == 'drawing':
                self.mode = 'none'
                try:
                    self.releaseMouse()
                except Exception:
                    pass
                self._crop_rect_img = self._clamp_rect_to_image(self._drawing_rect_img)
                self._drawing_rect_img = QRectF()
                self._last_mouse_release_pos = event.pos()
                if self._crop_rect_img.width() < UI_BEHAVIOR.SELECTION_MIN_SIZE or self._crop_rect_img.height() < UI_BEHAVIOR.SELECTION_MIN_SIZE:
                    self.clear_selection()
                else:
                    self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)
                    self._auto_zoom_to_selection()
                    self._role_popup_timer.start(150)
            self.update()
        except Exception as e:
            top_level = self.window()
            if hasattr(top_level, 'logger'):
                top_level.logger.error(f"DrawWidget.mouseReleaseEvent error: {e}")
            self.clear_selection()
            self.setCursor(Qt.CrossCursor)
            self.update()

    def _show_role_toolbar(self, move_only=False):
        if self._crop_rect_img.isNull(): return
        if not hasattr(self, '_all_roles') or not self._all_roles:
            all_roles = list(HUD_ELEMENT_MAPPINGS.values())
            self.set_roles(all_roles, getattr(self, '_configured_roles', set()))
        if not self._all_roles:
            return
        if not move_only:
            self.role_toolbar.set_roles(self._all_roles, getattr(self, '_configured_roles', set()))
            self.role_toolbar.adjustSize()
        if self.scroll_area:
            v_rect = self.scroll_area.viewport().rect()
            tl = self.mapFrom(self.scroll_area.viewport(), v_rect.topLeft())
            visible_rect = QRectF(QPointF(tl), QSizeF(v_rect.size()))
        else:
            visible_rect = QRectF(self.rect())
        rect_display = self._selection_display_rect
        if rect_display.isNull():
            rect_display = self._map_rect_to_display(self._crop_rect_img)
        tb_w = self.role_toolbar.width()
        tb_h = self.role_toolbar.height()
        buffer = 30
        margin = 15
        space_left = rect_display.left() - visible_rect.left()
        space_right = visible_rect.right() - rect_display.right()
        space_top = rect_display.top() - visible_rect.top()
        space_bottom = visible_rect.bottom() - rect_display.bottom()
        use_horizontal = True
        if (space_left < tb_w + buffer) and (space_right < tb_w + buffer):
            if (space_top > tb_h + buffer) or (space_bottom > tb_h + buffer):
                use_horizontal = False
        if use_horizontal:
            if space_right >= space_left:
                x = rect_display.right() + buffer
            else:
                x = rect_display.left() - tb_w - buffer
            y = rect_display.center().y() - (tb_h / 2)
        else:
            if space_bottom >= space_top:
                y = rect_display.bottom() + buffer
            else:
                y = rect_display.top() - tb_h - buffer
            x = rect_display.center().x() - (tb_w / 2)
        x = max(visible_rect.left() + margin, min(x, visible_rect.right() - tb_w - margin))
        y = max(visible_rect.top() + margin, min(y, visible_rect.bottom() - tb_h - margin))
        final_rect = QRectF(x, y, tb_w, tb_h)
        if final_rect.intersects(rect_display):
            if space_bottom > space_top:
                y = visible_rect.bottom() - tb_h - margin
            else:
                y = visible_rect.top() + margin
        global_pos = self.mapToGlobal(QPoint(int(x), int(y)))
        self.role_toolbar.move(global_pos)
        self.role_toolbar.show()
        self.role_toolbar.raise_()
        self._apply_role_priority(rect_display.center(), visible_rect.center().x(), visible_rect.center().y())

    def _show_role_menu_fallback(self):
        if self._crop_rect_img.isNull(): return
        roles = list(getattr(self, '_all_roles', [])) or list(HUD_ELEMENT_MAPPINGS.values())
        if not roles: return
        self._role_menu.clear()
        for role in roles:
            action = QAction(role.upper(), self._role_menu)
            action.setData(role)
            self._role_menu.addAction(action)
        popup_pos = self.mapToGlobal(self._last_mouse_release_pos)
        self._role_menu.popup(popup_pos)

    def _handle_role_menu_action(self, action):
        role = action.data() if action else None
        if role:
            self.confirm_selection(role)

    def _force_role_menu_after_release(self):
        if self._crop_rect_img.isNull():
            return
        if self.role_toolbar.isVisible():
            return
        self._show_role_menu_fallback()

    def _apply_role_priority(self, box_center, center_x, center_y):
        if not hasattr(self, '_all_roles'):
            return
        primary = None
        is_right = box_center.x() > center_x
        is_bottom = box_center.y() > center_y
        if not is_right and not is_bottom:
            primary = HUD_ELEMENT_MAPPINGS.get("team")
        elif is_right and not is_bottom:
            primary = HUD_ELEMENT_MAPPINGS.get("stats")
        elif is_right and is_bottom:
            primary = HUD_ELEMENT_MAPPINGS.get("loot")
        elif not is_right and is_bottom:
            primary = HUD_ELEMENT_MAPPINGS.get("normal_hp")
        self.role_toolbar.set_roles_with_priority(self._all_roles, getattr(self, '_configured_roles', set()), primary)

    def _hit_test_selection_handle(self, pos):
        rect = self._selection_display_rect
        if rect.isNull():
            return None
        size = self._selection_handle_size
        half = size / 2
        corners = {
            "tl": rect.topLeft(),
            "tr": rect.topRight(),
            "bl": rect.bottomLeft(),
            "br": rect.bottomRight()
        }
        for key, corner in corners.items():
            handle_rect = QRectF(corner.x() - half, corner.y() - half, size, size)
            if handle_rect.contains(pos):
                return key
        return None

    def _resize_selection(self, pos):
        if not self._resize_corner or self._crop_rect_img.isNull():
            return
        img_pos = self._map_to_image(pos)
        rect = QRectF(self._crop_rect_img)
        if self._resize_corner == "tl":
            rect.setTopLeft(img_pos)
        elif self._resize_corner == "tr":
            rect.setTopRight(img_pos)
        elif self._resize_corner == "bl":
            rect.setBottomLeft(img_pos)
        elif self._resize_corner == "br":
            rect.setBottomRight(img_pos)
        rect = rect.normalized()
        rect = self._clamp_rect_to_image(rect)
        if rect.width() < UI_BEHAVIOR.SELECTION_MIN_SIZE or rect.height() < UI_BEHAVIOR.SELECTION_MIN_SIZE:
            return
        self._crop_rect_img = rect
        self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)

    def _auto_zoom_to_selection(self):
        if self._crop_rect_img.isNull() or not self.scroll_area:
            return
        rect_display = self._map_rect_to_display(self._crop_rect_img)
        viewport = self.scroll_area.viewport().rect()
        if rect_display.width() < 1 or rect_display.height() < 1:
            return
        scale_x = viewport.width() / rect_display.width()
        scale_y = viewport.height() / rect_display.height()
        target_zoom = min(scale_x, scale_y) * 0.3825
        target_zoom = max(self.zoom, min(target_zoom, 10.0))
        if not getattr(self, '_user_zoomed', False) and abs(target_zoom - self.zoom) > 0.01:
            self.zoom = target_zoom
            self._update_scaled_pixmap()
        display_rect = self._map_rect_to_display(self._crop_rect_img)
        h_bar = self.scroll_area.horizontalScrollBar()
        v_bar = self.scroll_area.verticalScrollBar()
        target_x = display_rect.center().x() - (viewport.width() / 2)
        target_y = display_rect.center().y() - (viewport.height() / 2)
        max_x = max(0, self.width() - viewport.width())
        max_y = max(0, self.height() - viewport.height())
        h_bar.setValue(int(max(0, min(target_x, max_x))))
        v_bar.setValue(int(max(0, min(target_y, max_y))))

    def _apply_resize_cursor(self, corner):
        if corner in ("tl", "br"):
            self.setCursor(Qt.SizeFDiagCursor)
        elif corner in ("tr", "bl"):
            self.setCursor(Qt.SizeBDiagCursor)

    def get_selection(self):
        if self.pixmap.isNull() or self._crop_rect_img.isNull() or not self._crop_rect_img.isValid():
            return None, None
        final_rect = self._crop_rect_img.toRect().intersected(self.pixmap.rect())
        if final_rect.width() < UI_BEHAVIOR.SELECTION_MIN_SIZE or final_rect.height() < UI_BEHAVIOR.SELECTION_MIN_SIZE:
            return None, None
        return self.pixmap.copy(final_rect), final_rect

    def confirm_selection(self, role):
        pix, rect = self.get_selection()
        if pix and rect:
            top_level = self.window()
            if hasattr(top_level, 'enhanced_logger') and top_level.enhanced_logger:
                top_level.enhanced_logger.log_hud_element_selection(role, rect)
            self.crop_role_selected.emit(pix, rect, role)
        self.clear_selection()
        self.reset_zoom()

    def reset_zoom(self):
        """Resets the view to fit-to-screen (initial state)."""
        self.zoom = 1.0
        self._user_zoomed = False
        self._update_scaled_pixmap()
        self.update()

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
        move_amount = UI_BEHAVIOR.KEYBOARD_NUDGE_STEP
        key = event.key()
        if key in (Qt.Key_Return, Qt.Key_Enter):
            self._show_role_toolbar()
            event.accept()
            return
        if key == Qt.Key_Delete:
            self.clear_selection()
            event.accept()
            return
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
        self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)
        if not self._selection_display_rect.isNull():
            self._last_mouse_release_pos = self._selection_display_rect.center().toPoint()
        if self.role_toolbar.isVisible():
            self._show_role_toolbar(move_only=True)
        self.update()
        event.accept()

    def keyPressEvent(self, event):
        self.handle_key_press(event)
        if not event.isAccepted():
            super().keyPressEvent(event)
