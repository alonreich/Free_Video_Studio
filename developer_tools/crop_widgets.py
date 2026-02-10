from PyQt5.QtWidgets import QWidget, QFrame, QPushButton, QVBoxLayout, QHBoxLayout, QMenu, QAction, QApplication, QSlider, QStyle, QStyleOptionSlider
from PyQt5.QtCore import Qt, QPoint, QRect, pyqtSignal, QRectF, QPointF, QSize, QSizeF, QTimer
from PyQt5.QtGui import QPainter, QPen, QBrush, QColor, QPixmap, QPainterPath, QCursor, QFont
from config import UI_BEHAVIOR, UI_LAYOUT, HUD_ELEMENT_MAPPINGS, UI_COLORS

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
        self.setStyleSheet("""
            #roleToolbar {
                background-color: rgba(31, 41, 55, 0.98);
                border: 2px solid #60A5FA;
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

    def set_roles(self, roles, configured_roles, primary_role=None):
        while self.layout().count():
            child = self.layout().takeAt(0)
            if child.widget():
                child.widget().deleteLater()
        for role in roles:
            display_text = role.upper()
            btn = QPushButton(display_text)
            tips = {
                "Loot Area": "Bottom-right popup showing items and ammo.",
                "Mini Map + Stats": "Top-right radar and game statistics.",
                "Own Health Bar (HP)": "Bottom-left health and shield bars.",
                "Boss HP (For When You Are The Boss Character)": "Center health bar for special modes.",
                "Teammates health Bars (HP)": "Left-side health indicators for your squad.",
                "Spectating Eye": "The spectating count eye, which represent how many players are currently watching you."
            }
            btn.setToolTip(tips.get(role, ""))
            btn.setFixedHeight(34)
            btn.setCursor(Qt.PointingHandCursor)
            if role == primary_role:
                btn.setStyleSheet("color: #FFFFFF; font-weight: bold; border-color: #7DD3FC;")
            else:
                btn.setStyleSheet("color: #9CA3AF; font-weight: normal;")
                if role in configured_roles:
                    btn.setStyleSheet("color: #6B7280; font-style: italic;")

            def make_handler(r):
                return lambda: self._on_btn_clicked(r)
            btn.clicked.connect(make_handler(role))
            self.layout().addWidget(btn)
        self.adjustSize()

    def _on_btn_clicked(self, role):
        self.role_selected.emit(role)
        self.hide()

    def show_at(self, pos):
        """[FIX #22] Shows the toolbar at pos, clamped to the monitor's screen bounds."""
        self.adjustSize()
        current_screen = QApplication.screenAt(pos) or QApplication.primaryScreen()
        screen = current_screen.availableGeometry()
        w, h = self.width(), self.height()
        x = max(screen.left(), min(pos.x(), screen.right() - w))
        y = max(screen.top(), min(pos.y(), screen.bottom() - h))
        self.move(x, y)
        self.show()
        self.raise_()

    def set_roles_with_priority(self, roles, configured_roles, primary_role=None):
        """Sorts roles: [Primary] + [Remaining] + [Already Configured]."""
        remaining = [r for r in roles if r not in configured_roles and r != primary_role]
        configured = [r for r in roles if r in configured_roles and r != primary_role]
        ordered = []
        if primary_role:
            ordered.append(primary_role)
        ordered.extend(remaining)
        ordered.extend(configured)
        self.set_roles(ordered, configured_roles, primary_role)

class SeekSlider(QSlider):
    """Custom slider that supports instant click-to-seek behavior."""

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            opt = QStyleOptionSlider()
            self.initStyleOption(opt)
            sr = self.style().subControlRect(QStyle.CC_Slider, opt, QStyle.SC_SliderHandle, self)
            if sr.contains(event.pos()):
                super().mousePressEvent(event)
                return
            new_val = self.minimum() + ((self.maximum() - self.minimum()) * event.x()) / self.width()
            self.setValue(int(new_val))
            self.sliderMoved.emit(self.value())
            event.accept()
        super().mousePressEvent(event)

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
        self._candidates_img = [] 
        self._ant_timer = QTimer(self)
        self._ant_timer.timeout.connect(self._update_ant_dash)
        self._ant_dash_offset = 0
        self._role_menu = QMenu(self)
        self._role_menu.triggered.connect(self._handle_role_menu_action)
        self._role_popup_timer = QTimer(self)
        self._role_popup_timer.setSingleShot(True)
        self._role_popup_timer.timeout.connect(self._show_role_toolbar)
        self._ghost_rects_img = []

    def set_ghost_rects(self, rects):
        """
        rects: List of tuples (QRectF, label_text) in image coordinates.
        """
        self._ghost_rects_img = rects
        self.update()

    def set_ghosts_visible(self, visible):
        self._ghosts_visible = visible
        self.update()

    def _update_ant_dash(self):
        self._ant_dash_offset = (self._ant_dash_offset + 1) % 8
        self.update()

    def set_candidates(self, rects):
        self._candidates_img = [QRectF(float(r.x()), float(r.y()), float(r.width()), float(r.height())) for r in rects]
        if self._candidates_img:
            self._ant_timer.start(100)
        else:
            self._ant_timer.stop()
        self.update()

    def set_scroll_area(self, scroll_area):
        self.scroll_area = scroll_area

    def set_roles(self, all_roles, configured_roles, primary_role=None):
        if not all_roles:
            all_roles = list(HUD_ELEMENT_MAPPINGS.values())
        self._all_roles = list(all_roles)
        self._configured_roles = set(configured_roles)
        self.role_toolbar.set_roles(self._all_roles, self._configured_roles, primary_role)

    def clear_selection(self):
        self._crop_rect_img = QRectF()
        self._selection_display_rect = QRectF()
        self._resizing_selection = False
        self._resize_corner = None
        self.role_toolbar.hide()
        self._last_mouse_release_pos = QPoint()
        self._user_zoomed = False
        self.update()
        
    def set_crop_rect(self, rect_f, auto_zoom=True):
        self._crop_rect_img = self._clamp_rect_to_image(rect_f)
        center_img = rect_f.center()
        self._last_mouse_release_pos = self._map_rect_to_display(QRectF(center_img.x(), center_img.y(), 0, 0)).topLeft().toPoint()
        self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)
        if auto_zoom:
            self._auto_zoom_to_selection()
        self._show_role_toolbar()
        self.update()

    def setImage(self, image_path):
        if image_path:
            self.pixmap = QPixmap(image_path)
            self.zoom = 1.0
            self.setCursor(Qt.CrossCursor)
            self._user_zoomed = False
            self._update_scaled_pixmap()
            QTimer.singleShot(100, self._update_scaled_pixmap)
            QTimer.singleShot(500, self._update_scaled_pixmap)
        else:
            self.pixmap = QPixmap()
            self.setCursor(Qt.ArrowCursor)
        self.clear_selection()
        self.update()

    def _update_scaled_pixmap(self):
        if self.pixmap.isNull() or not self.scroll_area: 
            return
        viewport_size = self.scroll_area.viewport().size()
        if viewport_size.width() < 10 or viewport_size.height() < 10:
            return
        ref_w = self.pixmap.width()
        ref_h = int(ref_w * (9/16))
        if self.zoom == 1.0:
            scale_x = (viewport_size.width() - 10) / float(ref_w)
            scale_y = (viewport_size.height() - 10) / float(ref_h)
            self.zoom = min(scale_x, scale_y)
        new_w = max(1, int(ref_w * self.zoom))
        new_h = max(1, int(ref_h * self.zoom))
        scaled_size = QSize(new_w, new_h)
        if self.size() != scaled_size:
            self.setFixedSize(scaled_size)
        self._img_rect = QRect(QPoint(0, 0), scaled_size)
        self._cache_pix = self.pixmap.scaled(scaled_size, Qt.IgnoreAspectRatio, Qt.SmoothTransformation)
        if not self._crop_rect_img.isNull():
            self._selection_display_rect = self._map_rect_to_display(self._crop_rect_img)
        self.update()
        if self.parentWidget():
            self.parentWidget().update()

    def wheelEvent(self, event):
        if self.pixmap.isNull(): return
        angle = event.angleDelta().y()
        if angle == 0: return
        viewport_size = self.scroll_area.viewport().size() if self.scroll_area else self.size()
        min_allowed_zoom = 0.1
        if self.pixmap.width() > 0 and self.pixmap.height() > 0:
            scale_w = (viewport_size.width() - 20) / self.pixmap.width()
            scale_h = (viewport_size.height() - 20) / self.pixmap.height()
            min_allowed_zoom = min(scale_w, scale_h)
        factor = 1.15 if angle > 0 else 1/1.15
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
        if not self.pixmap.isNull() and self._cache_pix:
            painter.drawPixmap(self._img_rect.topLeft(), self._cache_pix)
        if self._crosshair_pos and (self.mode == 'drawing' or self._crop_rect_img.isNull()):
            self._draw_crosshair(painter, self._crosshair_pos)
        if self._candidates_img:
            ant_pen = QPen(QColor("#00FFFF"), 2, Qt.CustomDashLine)
            ant_pen.setDashPattern([4, 4])
            ant_pen.setDashOffset(self._ant_dash_offset)
            painter.setPen(ant_pen)
            painter.setBrush(Qt.NoBrush)
            for cand_img in self._candidates_img:
                cand_display = self._map_rect_to_display(cand_img)
                painter.drawRect(cand_display)
        if self._ghost_rects_img and getattr(self, '_ghosts_visible', True):
            ghost_pen = QPen(QColor("#FFD700"), 2, Qt.DashLine)
            painter.setPen(ghost_pen)
            painter.setBrush(Qt.NoBrush)
            font = QFont("Arial", 10, QFont.Bold)
            painter.setFont(font)
            for rect_f, label in self._ghost_rects_img:
                ghost_display = self._map_rect_to_display(rect_f)
                painter.drawRect(ghost_display)
                if label:
                    txt_rect = painter.fontMetrics().boundingRect(label)
                    label_pos = ghost_display.topLeft() - QPointF(0, 5)
                    if label_pos.y() < 15: label_pos = ghost_display.bottomLeft() + QPointF(0, 15)
                    bg_rect = QRectF(label_pos.x(), label_pos.y() - txt_rect.height(), txt_rect.width() + 4, txt_rect.height() + 2)
                    painter.fillRect(bg_rect, QColor(0, 0, 0, 150))
                    painter.setPen(QColor("#FFD700"))
                    painter.drawText(label_pos, label)
            painter.setPen(Qt.NoPen)
        if not self._crop_rect_img.isNull():
            path = QPainterPath()
            path.addRect(QRectF(self.rect()))
            path.addRect(QRectF(self._map_rect_to_display(self._crop_rect_img)))
            painter.setBrush(QColor(0, 0, 0, UI_COLORS.OPACITY_DIM_MED))
            painter.setPen(Qt.NoPen)
            painter.drawPath(path)
        rect_to_draw_img = self._drawing_rect_img if self.mode == 'drawing' else self._crop_rect_img
        if not rect_to_draw_img.isNull():
            rect_to_draw = self._map_rect_to_display(rect_to_draw_img)
            painter.setBrush(Qt.NoBrush)
            pen = QPen(QColor(UI_COLORS.SELECTION_GREEN), 2, Qt.SolidLine)
            painter.setPen(pen)
            painter.drawRect(rect_to_draw)
            if self.mode != 'drawing':
                self._selection_display_rect = rect_to_draw
                self._draw_selection_handles(painter, rect_to_draw)
        if self.zoom != 1.0:
            zoom_text = f"{int(self.zoom * 100)}%"
            painter.setPen(QPen(QColor(255, 255, 255), 1))
            painter.setFont(QFont("Arial", 10, QFont.Bold))
            painter.drawText(self.rect().adjusted(0, 0, -10, -10), Qt.AlignBottom | Qt.AlignRight, zoom_text)

    def _draw_selection_handles(self, painter, rect):
        size = self._selection_handle_size
        half = size / 2
        painter.setBrush(QColor(UI_COLORS.HANDLE_ORANGE))
        painter.setPen(QPen(Qt.white, 1.5))
        corners = [
            rect.topLeft(),
            rect.topRight(),
            rect.bottomLeft(),
            rect.bottomRight()
        ]
        for corner in corners:
            painter.drawRect(QRectF(corner.x() - half, corner.y() - half, size, size))

    def _draw_crosshair(self, painter, pos):
        painter.setPen(QPen(QColor(0, 0, 0, 150), 3, Qt.SolidLine))
        painter.drawLine(0, pos.y(), self.width(), pos.y())
        painter.drawLine(pos.x(), 0, pos.x(), self.height())
        pen = QPen(QColor(0, 255, 255, 200), 1, Qt.DashLine)
        painter.setPen(pen)
        painter.drawLine(0, pos.y(), self.width(), pos.y())
        painter.drawLine(pos.x(), 0, pos.x(), self.height())
        painter.setPen(QPen(QColor(255, 255, 255, 255), 2))
        painter.drawLine(pos.x() - 10, pos.y(), pos.x() + 10, pos.y())
        painter.drawLine(pos.x(), pos.y() - 10, pos.x(), pos.y() + 10)

    def mousePressEvent(self, event):
        if self.pixmap.isNull(): return
        if event.button() == Qt.MiddleButton:
            self._panning = True
            self._pan_start_pos = event.pos()
            self.setCursor(Qt.ClosedHandCursor)
            event.accept()
            return
        if event.button() == Qt.LeftButton:
            self._role_popup_timer.stop()
            if self.role_toolbar.isVisible() and self.role_toolbar.geometry().contains(self.mapToGlobal(event.pos())):
                return
            if self._candidates_img:
                for cand_img in self._candidates_img:
                    cand_display = self._map_rect_to_display(cand_img)
                    if cand_display.contains(event.pos()):
                        self._crop_rect_img = cand_img
                        self._selection_display_rect = cand_display
                        self._candidates_img = [] 
                        self._ant_timer.stop()
                        self._show_role_toolbar()
                        self.update()
                        return
            if getattr(self, '_ghosts_visible', True) and self._ghost_rects_img and self.mode != 'drawing':
                for rect_f, label in self._ghost_rects_img:
                    display_rect = self._map_rect_to_display(rect_f)
                    if display_rect.contains(event.pos()):
                        self._crop_rect_img = rect_f
                        self._selection_display_rect = display_rect
                        self._auto_zoom_to_selection()
                        self._show_role_toolbar()
                        self.update()
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
        if getattr(self, '_ghosts_visible', True) and self._ghost_rects_img and not self._moving_selection and not self._resizing_selection and self.mode != 'drawing':
            for rect_f, label in self._ghost_rects_img:
                display_rect = self._map_rect_to_display(rect_f)
                if display_rect.contains(event.pos()):
                    self.setCursor(Qt.PointingHandCursor)
                    return
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
            elif self._selection_display_rect.contains(event.pos()):
                self.setCursor(Qt.OpenHandCursor)
            else:
                self.setCursor(Qt.CrossCursor)
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
                if not self._selection_display_rect.isNull() and self._selection_display_rect.contains(event.pos()):
                    self.setCursor(Qt.OpenHandCursor)
                else:
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

    def _get_ideal_popup_rect(self, popup_size, margin=20):
        if self._crop_rect_img.isNull(): return None
        rect_display = self._map_rect_to_display(self._crop_rect_img)
        global_selection = QRect(self.mapToGlobal(rect_display.topLeft().toPoint()), 
                                 self.mapToGlobal(rect_display.bottomRight().toPoint()))
        gap = 60
        forbidden = global_selection.adjusted(-gap, -gap, gap, gap)
        if self.scroll_area:
            viewport_rect = self.scroll_area.viewport().rect()
            global_viewport = QRect(self.scroll_area.viewport().mapToGlobal(viewport_rect.topLeft()),
                                    self.scroll_area.viewport().mapToGlobal(viewport_rect.bottomRight()))
        else:
            global_viewport = QRect(self.mapToGlobal(self.rect().topLeft()), 
                                    self.mapToGlobal(self.rect().bottomRight()))
        pw, ph = popup_size.width(), popup_size.height()

        def get_cand(side):
            local_margin = 30
            if side == "right":
                x = forbidden.right() + local_margin
                y = forbidden.center().y() - (ph // 2)
            elif side == "left":
                x = forbidden.left() - pw - local_margin
                y = forbidden.center().y() - (ph // 2)
            elif side == "bottom":
                y = forbidden.bottom() + local_margin
                x = forbidden.center().x() - (pw // 2)
            else:
                y = forbidden.top() - ph - local_margin
                x = forbidden.center().x() - (pw // 2)
            if side in ("left", "right"):
                y = max(global_viewport.top() + margin, min(y, global_viewport.bottom() - ph - margin))
            else:
                x = max(global_viewport.left() + margin, min(x, global_viewport.right() - pw - margin))
            return QRect(x, y, pw, ph)
        candidates = []
        for side in ("right", "left", "bottom", "top"):
            cand = get_cand(side)
            vis_ratio = 0.0
            if cand.intersects(forbidden):
                score = 0
            else:
                intersection = cand.intersected(global_viewport)
                vis_ratio = (intersection.width() * intersection.height()) / (pw * ph)
                if vis_ratio > 0.90: score = 3
                elif vis_ratio > 0.40: score = 2
                else: score = 1
            candidates.append((score, vis_ratio, cand))
        candidates.sort(key=lambda x: (x[0], x[1]), reverse=True)
        return candidates[0][2]

    def _show_role_toolbar(self, move_only=False):
        if self._crop_rect_img.isNull(): return
        if move_only:
            self.role_toolbar.hide()

        def _place_menu():
            if self._crop_rect_img.isNull(): return
            img_center = self.pixmap.rect().center()
            if not move_only:
                self._apply_role_priority(self._crop_rect_img.center(), img_center.x(), img_center.y())
            popup_size = self.role_toolbar.layout().sizeHint()
            global_target_rect = self._get_ideal_popup_rect(popup_size)
            if global_target_rect:
                self.role_toolbar.show_at(global_target_rect.topLeft())
        QTimer.singleShot(100, _place_menu)

    def _apply_role_priority(self, box_center_img, img_center_x, img_center_y):
        if not hasattr(self, '_all_roles'): return
        primary = None
        is_right = box_center_img.x() > img_center_x
        is_bottom = box_center_img.y() > img_center_y
        if not is_bottom and not is_right: primary = HUD_ELEMENT_MAPPINGS.get("team")
        elif not is_bottom and is_right: primary = HUD_ELEMENT_MAPPINGS.get("stats")
        elif is_bottom and is_right: primary = HUD_ELEMENT_MAPPINGS.get("loot")
        elif is_bottom and not is_right: primary = HUD_ELEMENT_MAPPINGS.get("normal_hp")
        self.role_toolbar.set_roles_with_priority(self._all_roles, getattr(self, '_configured_roles', set()), primary)

    def _show_role_menu_fallback(self):
        if self._crop_rect_img.isNull(): return
        roles = list(getattr(self, '_all_roles', [])) or list(HUD_ELEMENT_MAPPINGS.values())
        if not roles: return
        self._role_menu.clear()
        for role in roles:
            action = QAction(role.upper(), self._role_menu)
            action.setData(role)
            self._role_menu.addAction(action)
        chosen_rect = self._get_ideal_popup_rect(self._role_menu.sizeHint())
        if not chosen_rect: return
        self._role_menu.popup(chosen_rect.topLeft())

    def _handle_role_menu_action(self, action):
        role = action.data() if action else None
        if role:
            self.confirm_selection(role)

    def _force_role_menu_after_release(self):
        if self._crop_rect_img.isNull(): return
        if self.role_toolbar.isVisible(): return
        self._show_role_menu_fallback()

    def _hit_test_selection_handle(self, pos):
        rect = self._map_rect_to_display(self._crop_rect_img)
        if rect.isNull(): return None
        size = self._selection_handle_size
        half = size / 2
        corners = {"tl": rect.topLeft(), "tr": rect.topRight(), "bl": rect.bottomLeft(), "br": rect.bottomRight()}
        for key, corner in corners.items():
            handle_rect = QRectF(corner.x() - half, corner.y() - half, size, size)
            if handle_rect.contains(pos): return key
        return None

    def _resize_selection(self, pos):
        if not self._resize_corner or self._crop_rect_img.isNull(): return
        img_pos = self._map_to_image(pos)
        rect = QRectF(self._crop_rect_img)
        if self._resize_corner == "tl": rect.setTopLeft(img_pos)
        elif self._resize_corner == "tr": rect.setTopRight(img_pos)
        elif self._resize_corner == "bl": rect.setBottomLeft(img_pos)
        elif self._resize_corner == "br": rect.setBottomRight(img_pos)
        rect = rect.normalized()
        rect = self._clamp_rect_to_image(rect)
        if rect.width() < UI_BEHAVIOR.SELECTION_MIN_SIZE or rect.height() < UI_BEHAVIOR.SELECTION_MIN_SIZE: return
        self._crop_rect_img = rect

    def _auto_zoom_to_selection(self):
        if self._crop_rect_img.isNull() or not self.scroll_area: return
        viewport = self.scroll_area.viewport().rect()
        if viewport.width() < 10: return
        sel_w = self._crop_rect_img.width()
        zoom_target = (viewport.width() * 0.7) / max(1, sel_w)
        zoom_target = max(1.0, min(zoom_target, 10.0))
        if not getattr(self, '_user_zoomed', False) and abs(zoom_target - self.zoom) > 0.05:
            self.zoom = zoom_target
            self._update_scaled_pixmap()
            QApplication.processEvents()

        def _perform_centering():
            if self._crop_rect_img.isNull() or not self.scroll_area: return
            display_rect = self._map_rect_to_display(self._crop_rect_img)
            h_bar = self.scroll_area.horizontalScrollBar()
            v_bar = self.scroll_area.verticalScrollBar()
            target_x = int(display_rect.center().x() - (viewport.width() / 2))
            target_y = int(display_rect.center().y() - (viewport.height() / 2))
            h_bar.setValue(target_x)
            v_bar.setValue(target_y)
        QTimer.singleShot(50, _perform_centering)

    def _apply_resize_cursor(self, corner):
        if corner in ("tl", "br"): self.setCursor(Qt.SizeFDiagCursor)
        elif corner in ("tr", "bl"): self.setCursor(Qt.SizeBDiagCursor)

    def get_selection(self):
        if self.pixmap.isNull() or self._crop_rect_img.isNull() or not self._crop_rect_img.isValid(): return None, None
        final_rect = self._crop_rect_img.toAlignedRect().intersected(self.pixmap.rect())
        if final_rect.width() < UI_BEHAVIOR.SELECTION_MIN_SIZE or final_rect.height() < UI_BEHAVIOR.SELECTION_MIN_SIZE: return None, None
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
        self.zoom = 1.0
        self._user_zoomed = False
        self._update_scaled_pixmap()
        self.update()

    def _map_to_image(self, widget_pos):
        if self.zoom == 0 or self.pixmap.isNull(): return QPointF()
        ref_w = self.pixmap.width()
        ref_h = ref_w * (9/16)
        ref_x = (widget_pos.x() - self._img_rect.x()) / self.zoom
        ref_y = (widget_pos.y() - self._img_rect.y()) / self.zoom
        orig_x = ref_x
        orig_y = ref_y * (self.pixmap.height() / float(ref_h))
        return QPointF(orig_x, orig_y)

    def _map_rect_to_display(self, img_rect):
        if self.pixmap.isNull(): return QRectF()
        ref_w = self.pixmap.width()
        ref_h = ref_w * (9/16)
        ref_y = img_rect.y() * (ref_h / float(self.pixmap.height()))
        ref_h_rect = img_rect.height() * (ref_h / float(self.pixmap.height()))
        return QRectF(
            self._img_rect.x() + img_rect.x() * self.zoom,
            self._img_rect.y() + ref_y * self.zoom,
            img_rect.width() * self.zoom,
            ref_h_rect * self.zoom
        )
        
    def _clamp_rect_to_image(self, rect_f):
        if self.pixmap.isNull(): return rect_f
        img_bounds = QRectF(self.pixmap.rect())
        return rect_f.intersected(img_bounds)

    def handle_key_press(self, event):
        if self._crop_rect_img.isNull(): return
        offset = QPointF(0, 0)
        if event.modifiers() & Qt.ShiftModifier:
            move_amount = 5.0
        else:
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
        if key == Qt.Key_Up: offset.setY(-move_amount)
        elif key == Qt.Key_Down: offset.setY(move_amount)
        elif key == Qt.Key_Left: offset.setX(-move_amount)
        elif key == Qt.Key_Right: offset.setX(move_amount)
        else: return
        self._crop_rect_img.translate(offset)
        self._crop_rect_img = self._clamp_rect_to_image(self._crop_rect_img)
        if self.role_toolbar.isVisible(): self._show_role_toolbar(move_only=True)
        self.update()
        event.accept()

    def keyPressEvent(self, event):
        self.handle_key_press(event)
        if not event.isAccepted(): super().keyPressEvent(event)
