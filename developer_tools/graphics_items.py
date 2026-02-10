from PyQt5.QtWidgets import QGraphicsObject, QGraphicsRectItem, QGraphicsItem, QGraphicsLineItem, QGraphicsSimpleTextItem
from PyQt5.QtCore import Qt, pyqtSignal, QRectF, QTimer, QPointF
from PyQt5.QtGui import QBrush, QColor, QPen, QFont
from config import UI_LAYOUT, UI_BEHAVIOR, UI_COLORS
from coordinate_math import TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE, scale_round

class ResizablePixmapItem(QGraphicsObject):
    item_changed = pyqtSignal()

    def __init__(self, pixmap, crop_rect, parent=None):
        super(ResizablePixmapItem, self).__init__(parent)
        self.original_pixmap = pixmap 
        self.crop_rect = crop_rect
        self.assigned_role = None 
        self.current_width = float(pixmap.width())
        self.current_height = float(pixmap.height())
        self.setFlags(QGraphicsItem.ItemIsMovable | QGraphicsItem.ItemIsSelectable | QGraphicsItem.ItemSendsGeometryChanges)
        self.setAcceptHoverEvents(True)
        self.handle_size = UI_LAYOUT.GRAPHICS_HANDLE_SIZE
        self.handle_br = QGraphicsRectItem(QRectF(0, 0, self.handle_size, self.handle_size), self)
        self.handle_br.setBrush(QBrush(QColor("#3498db")))
        self.handle_br.setPen(QPen(Qt.white, 2))
        self.handle_tl = QGraphicsRectItem(QRectF(0, 0, self.handle_size, self.handle_size), self)
        self.handle_tl.setBrush(QBrush(QColor("#e67e22")))
        self.handle_tl.setPen(QPen(Qt.white, 2))
        self.handle_br.setZValue(1)
        self.handle_tl.setZValue(1)
        self.setZValue(50)
        self.update_handle_positions()
        self.is_resizing_br = False
        self.is_resizing_tl = False
        self.ant_timer = QTimer(self)
        self.ant_timer.timeout.connect(self.update_ant_dash)
        self.ant_dash_offset = 0
        self.guide_lines = []
        self.guide_labels = []
        self._last_snap_key = None
        self._snap_anim_pos = None
        self._snap_anim_size = None
        self._snap_active = False
        self._snap_target_pos = None
        self._snap_target_size = None
        self._snap_repeat_key = None
        self._snap_repeat_count = 0
        self._snap_suppressed_until_release = False
        self._center_lock_x = False
        self._center_lock_y = False

    def update_ant_dash(self):
        self.ant_dash_offset = (self.ant_dash_offset + 1) % 8
        self.update()

    def boundingRect(self):
        h_size = self.handle_size
        label_buffer = 70 
        min_x = -h_size
        max_x = self.current_width + h_size
        min_y = -label_buffer
        max_y = self.current_height + label_buffer
        return QRectF(min_x, min_y, max_x - min_x, max_y - min_y)

    def paint(self, painter, option, widget):
        if getattr(self, '_is_nudging', False):
            painter.setBrush(QColor(0, 255, 255, 60))
            painter.setPen(QPen(QColor(0, 255, 255), 2))
            painter.drawRect(QRectF(0, 0, self.current_width, self.current_height))
        painter.drawPixmap(QRectF(0, 0, self.current_width, self.current_height), 
                            self.original_pixmap, QRectF(self.original_pixmap.rect()))
        black_pen = QPen(Qt.black, 3)
        painter.setPen(black_pen)
        painter.setBrush(Qt.NoBrush)
        painter.drawRect(QRectF(-1.0, -1.0, self.current_width + 2, self.current_height + 2))
        if self.assigned_role:
            rank = 1
            if self.scene():
                all_hud_items = [i for i in self.scene().items() if isinstance(i, ResizablePixmapItem)]
                all_hud_items.sort(key=lambda i: i.zValue(), reverse=True)
                try:
                    rank = all_hud_items.index(self) + 1
                except ValueError:
                    rank = 1
            title_text = f"{self.assigned_role.upper()} (LAYER {rank})"
            font = painter.font()
            font.setBold(True)
            target_font_size = max(14, int(self.current_height * 0.20))
            target_font_size = min(target_font_size, 36)
            font.setPixelSize(target_font_size)
            painter.setFont(font)
            fm = painter.fontMetrics()
            text_w = fm.horizontalAdvance(title_text) + (target_font_size * 2.0)
            text_h = fm.height() + (target_font_size * 0.6)
            scene_pos = self.scenePos()
            if scene_pos.y() > (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2):
                draw_y = -text_h - 10
            else:
                draw_y = self.current_height + 10
            center_x = int((self.current_width - text_w) / 2)
            draw_y = int(draw_y)
            text_w = int(text_w)
            text_h = int(text_h)
            label_rect = QRectF(center_x, draw_y, text_w, text_h)
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor(0, 0, 0, 210))
            painter.drawRoundedRect(label_rect, 6, 6)
            painter.setPen(QColor(UI_COLORS.TEXT_WARNING))
            painter.drawText(label_rect, Qt.AlignCenter, title_text)
        if self.isSelected():
            pattern_str = getattr(UI_BEHAVIOR, 'ANT_DASH_PATTERN', "4, 4")
            try:
                pattern = [int(x.strip()) for x in pattern_str.split(',')]
            except:
                pattern = [4, 4]
            pen = QPen(QColor(UI_COLORS.MARCHING_ANTS), 2, Qt.CustomDashLine)
            pen.setDashPattern(pattern)
            pen.setDashOffset(self.ant_dash_offset)
            painter.setPen(pen)
            painter.setBrush(Qt.NoBrush)
            painter.drawRect(QRectF(-2, -2, self.current_width + 4, self.current_height + 4))

    def set_role(self, role):
        if role == "-- Select Role --":
            self.assigned_role = None
        else:
            self.assigned_role = role

            from config import HUD_ELEMENT_MAPPINGS, Z_ORDER_MAP
            role_key = "unknown"
            for k, v in HUD_ELEMENT_MAPPINGS.items():
                if v == role:
                    role_key = k
                    break
            self.setZValue(Z_ORDER_MAP.get(role_key, 50))
        self.update()

    def update_handle_positions(self):
        """[FIX #25] Responsive handle sizing with enlarged hit areas."""
        zoom = 1.0
        if self.scene() and self.scene().views():
            view = self.scene().views()[0]
            zoom = view.transform().m11()
        target_screen_size = 16 
        dynamic_size = target_screen_size / max(0.01, zoom)
        self.handle_size = max(10.0, dynamic_size)
        min_dim = min(self.current_width, self.current_height)
        if self.handle_size > min_dim / 1.5:
            self.handle_size = max(4.0, min_dim / 2.0)
        self.hit_size = self.handle_size * 1.5
        offset = self.handle_size / 2
        self.handle_br.setRect(0, 0, self.handle_size, self.handle_size)
        self.handle_tl.setRect(0, 0, self.handle_size, self.handle_size)
        self.handle_br.setPos(self.current_width - offset, self.current_height - offset)
        self.handle_tl.setPos(-offset, -offset)

    def _is_on_handle(self, pos, handle):
        """Helper to detect if a position is within the enlarged hit area of a handle."""
        handle_center = handle.pos() + QPointF(self.handle_size/2, self.handle_size/2)
        dist = (pos - handle_center).manhattanLength()
        return dist < (self.hit_size)

    def hoverMoveEvent(self, event):
        if self._is_on_handle(event.pos(), self.handle_br) or self._is_on_handle(event.pos(), self.handle_tl):
            self.setCursor(Qt.SizeFDiagCursor)
        else:
            self.setCursor(Qt.OpenHandCursor)
        super(ResizablePixmapItem, self).hoverMoveEvent(event)

    def mousePressEvent(self, event):
        if self._is_on_handle(event.pos(), self.handle_br):
            self.is_resizing_br = True
            self.resize_start_scene_pos = event.scenePos()
            self.start_width = self.current_width
            self.start_height = self.current_height
            self._snap_active = False
            self._snap_repeat_key = None
            self._snap_repeat_count = 0
            self._snap_suppressed_until_release = False
        elif self._is_on_handle(event.pos(), self.handle_tl):
            self.is_resizing_tl = True
            self.resize_start_scene_pos = event.scenePos()
            self.start_width = self.current_width
            self.start_height = self.current_height
            self.start_pos = self.pos()
            self._snap_active = False
            self._snap_repeat_key = None
            self._snap_repeat_count = 0
            self._snap_suppressed_until_release = False
        else:
            self.setCursor(Qt.ClosedHandCursor)
            self._snap_active = False
            self._snap_repeat_key = None
            self._snap_repeat_count = 0
            self._snap_suppressed_until_release = False
            super(ResizablePixmapItem, self).mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if self.is_resizing_br:
            delta = event.scenePos() - self.resize_start_scene_pos
            new_width = self.start_width + delta.x()
            proposed_right = self.pos().x() + new_width
            snapped_right = self.calculate_snapping(self.pos(), resize_edge='R', proposed_value=proposed_right)
            if snapped_right is not None:
                new_width = snapped_right - self.pos().x()
                self._snap_active = True
                self._snap_target_size = (new_width, None)
            if self.scene():
                right_bound = self.scene().sceneRect().right()
                max_width = right_bound - self.pos().x()
                new_width = min(new_width, max_width)
            aspect = self.original_pixmap.height() / self.original_pixmap.width() if self.original_pixmap.width() > 0 else 0
            new_height = new_width * aspect
            proposed_bottom = self.pos().y() + new_height
            snapped_bottom = self.calculate_snapping(self.pos(), resize_edge='B', proposed_value=proposed_bottom)
            if snapped_bottom is not None:
                new_height = snapped_bottom - self.pos().y()
                new_width = new_height / aspect if aspect > 0 else new_width
                self._snap_active = True
                self._snap_target_size = (new_width, new_height)
            if self.scene():
                bottom_bound = UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING
                max_height = bottom_bound - self.pos().y()
                if new_height > max_height:
                    new_height = max_height
                    new_width = new_height / aspect if aspect > 0 else 0
            if new_width > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE and new_height > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE:
                self._apply_smooth_resize(new_width, new_height)
        elif self.is_resizing_tl:
            delta = event.scenePos() - self.resize_start_scene_pos
            proposed_new_pos_x = self.start_pos.x() + delta.x()
            snapped_left = self.calculate_snapping(QPointF(proposed_new_pos_x, self.start_pos.y()), resize_edge='L', proposed_value=proposed_new_pos_x)
            if snapped_left is not None:
                proposed_new_pos_x = snapped_left
                self._snap_active = True
                self._snap_target_pos = QPointF(proposed_new_pos_x, self.start_pos.y())
            if self.scene():
                proposed_new_pos_x = max(self.scene().sceneRect().left(), proposed_new_pos_x)
            start_right = self.start_pos.x() + self.start_width
            max_x = start_right - UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE
            new_pos_x = min(proposed_new_pos_x, max_x)
            new_width = start_right - new_pos_x
            aspect = self.original_pixmap.height() / self.original_pixmap.width() if self.original_pixmap.width() > 0 else 0
            new_height = new_width * aspect
            start_bottom = self.start_pos.y() + self.start_height
            new_pos_y = start_bottom - new_height
            snapped_top = self.calculate_snapping(QPointF(new_pos_x, new_pos_y), resize_edge='T', proposed_value=new_pos_y)
            if snapped_top is not None:
                new_pos_y = snapped_top
                new_height = start_bottom - new_pos_y
                new_width = new_height / aspect if aspect > 0 else new_width
                new_pos_x = start_right - new_width
                self._snap_active = True
                self._snap_target_pos = QPointF(new_pos_x, new_pos_y)
            if self.scene():
                top_bound = self.scene().sceneRect().top() + UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT
                if new_pos_y < top_bound:
                    new_pos_y = top_bound
                    new_height = start_bottom - new_pos_y
                    new_width = new_height / aspect if aspect > 0 else 0
                    new_pos_x = start_right - new_width
            if new_width > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE and new_height > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE:
                self._apply_smooth_resize(new_width, new_height, new_pos_x, new_pos_y)
        else:
            super(ResizablePixmapItem, self).mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
        self.setCursor(Qt.OpenHandCursor)
        self.is_resizing_br = False
        self.is_resizing_tl = False
        self.clear_guides()
        if self._snap_active:
            self._apply_snap_final_state()
        super(ResizablePixmapItem, self).mouseReleaseEvent(event)
        self.item_changed.emit()
        self._snap_active = False
        self._snap_target_pos = None
        self._snap_target_size = None
        self._snap_repeat_key = None
        self._snap_repeat_count = 0
        self._snap_suppressed_until_release = False

    def clear_guides(self):
        if self.scene():
            for line in self.guide_lines:
                self.scene().removeItem(line)
            for label in self.guide_labels:
                self.scene().removeItem(label)
        self.guide_lines.clear()
        self.guide_labels.clear()

    def content_scene_rect(self):
        """Returns the pixmap area in scene coordinates, excluding handles."""
        return QRectF(self.scenePos().x(), self.scenePos().y(), self.current_width, self.current_height)

    def calculate_snapping(self, proposed_pos, resize_edge=None, proposed_value=None):
        if not self.scene(): return proposed_pos if resize_edge is None else None
        if len(self.scene().selectedItems()) > 1 and resize_edge is None:
            return proposed_pos
        view = None
        if self.scene().views():
            view = self.scene().views()[0]
        snap_enabled = getattr(view, 'snap_enabled', True)
        is_keyboard = False

        import traceback
        for frame in traceback.extract_stack():
            if 'keyPressEvent' in frame.name:
                is_keyboard = True
                break
        pull_threshold = 12 if not is_keyboard else 0
        visual_threshold = 20
        center_pull = 40 if not is_keyboard else 0
        center_visual = 60
        my_w = self.current_width
        my_h = self.current_height
        if resize_edge is None:
            my_l, my_t = proposed_pos.x(), proposed_pos.y()
        else:
            curr = self.pos()
            my_l, my_t = curr.x(), curr.y()
            if resize_edge == 'L': my_l = proposed_value
            elif resize_edge == 'T': my_t = proposed_value
        my_r = my_l + my_w if resize_edge != 'R' else proposed_value
        my_b = my_t + my_h if resize_edge != 'B' else proposed_value
        my_cx = (my_l + my_r) / 2
        my_cy = (my_t + my_b) / 2
        targets_x = [(0, "Canvas Left"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH, "Canvas Right")]
        targets_y = [(UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT, "Content Top"), (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING, "Content Bottom")]
        for item in self.scene().items():
            if isinstance(item, ResizablePixmapItem) and item != self and item.isVisible():
                r = item.content_scene_rect()
                label = item.assigned_role or "Item"
                targets_x.extend([(r.left(), f"{label} Left"), (r.center().x(), f"{label} Center"), (r.right(), f"{label} Right")])
                targets_y.extend([(r.top(), f"{label} Top"), (r.center().y(), f"{label} Center"), (r.bottom(), f"{label} Bottom")])
        best_x, best_y = None, None
        min_dx, min_dy = visual_threshold, visual_threshold
        active_line_x, active_line_y = None, None
        if resize_edge in [None, 'L', 'R']:
            for tx, t_lab in targets_x:
                if resize_edge in [None, 'L']:
                    d = abs(my_l - tx)
                    if d < min_dx:
                        active_line_x = (tx, f"Align Left -> {t_lab}")
                        if d < pull_threshold: best_x = tx
                if resize_edge in [None, 'R']:
                    d = abs(my_r - tx)
                    if d < min_dx:
                        active_line_x = (tx, f"Align Right -> {t_lab}")
                        if d < pull_threshold: best_x = tx - my_w if resize_edge is None else tx
                if resize_edge is None:
                    d = abs(my_cx - tx)
                    if d < center_visual:
                        if d < min_dx or active_line_x is None:
                            active_line_x = (tx, f"Center -> {t_lab}")
                            if d < center_pull: best_x = tx - (my_w / 2)
        if resize_edge in [None, 'T', 'B']:
            for ty, t_lab in targets_y:
                if resize_edge in [None, 'T']:
                    d = abs(my_t - ty)
                    if d < min_dy:
                        active_line_y = (ty, f"Align Top -> {t_lab}")
                        if d < pull_threshold: best_y = ty
                if resize_edge in [None, 'B']:
                    d = abs(my_b - ty)
                    if d < min_dy:
                        active_line_y = (ty, f"Align Bottom -> {t_lab}")
                        if d < pull_threshold: best_y = ty - my_h if resize_edge is None else ty
                if resize_edge is None:
                    d = abs(my_cy - ty)
                    if d < center_visual:
                        if d < min_dy or active_line_y is None:
                            active_line_y = (ty, f"Center -> {t_lab}")
                            if d < center_pull: best_y = ty - (my_h / 2)
        current_key = (active_line_x, active_line_y)
        if current_key != self._last_snap_key:
            self.clear_guides()
            self._last_snap_key = current_key
            scene_rect = self.scene().sceneRect()
            font = QFont("Segoe UI", 9, QFont.Bold)
            if active_line_x:
                lx, lab = active_line_x
                line = QGraphicsLineItem(lx, scene_rect.top(), lx, scene_rect.bottom())
                line.setPen(QPen(QColor("#00FFFF"), 2, Qt.DashLine))
                line.setZValue(1000)
                self.scene().addItem(line)
                self.guide_lines.append(line)
                txt = QGraphicsSimpleTextItem(lab)
                txt.setBrush(QBrush(QColor("#00FFFF")))
                txt.setFont(font)
                txt.setPos(lx + 5, my_t - 25)
                txt.setZValue(1001)
                self.scene().addItem(txt)
                self.guide_labels.append(txt)
            if active_line_y:
                ly, lab = active_line_y
                line = QGraphicsLineItem(scene_rect.left(), ly, scene_rect.right(), ly)
                line.setPen(QPen(QColor("#00FFFF"), 2, Qt.DashLine))
                line.setZValue(1000)
                self.scene().addItem(line)
                self.guide_lines.append(line)
                txt = QGraphicsSimpleTextItem(lab)
                txt.setBrush(QBrush(QColor("#00FFFF")))
                txt.setFont(font)
                txt.setPos(my_l + 5, ly - 25)
                txt.setZValue(1001)
                self.scene().addItem(txt)
                self.guide_labels.append(txt)
        if is_keyboard or not snap_enabled:
            if resize_edge is None: return proposed_pos
            return proposed_value
        if resize_edge is None:
            fx = best_x if best_x is not None else proposed_pos.x()
            fy = best_y if best_y is not None else proposed_pos.y()
            return QPointF(fx, fy)
        else:
            if resize_edge in ['L', 'R']: return best_x if best_x is not None else proposed_value
            if resize_edge in ['T', 'B']: return best_y if best_y is not None else proposed_value
        return proposed_pos if resize_edge is None else proposed_value

    def _get_snap_alpha(self):
        """[FIX #30] Soft snapping: return a pull factor based on distance to snap point."""
        if not self._snap_active or not self._snap_target_pos:
            return 1.0
        curr_pos = self.pos()
        dx = abs(curr_pos.x() - self._snap_target_pos.x())
        dy = abs(curr_pos.y() - self._snap_target_pos.y())
        dist = (dx**2 + dy**2)**0.5
        if dist < 2: return 1.0
        if dist > 25: return 0.0
        return (1.0 - (dist / 25.0)) ** 2

    def _get_resize_snap_alpha(self):
        """[FIX #30] Soft snapping for resizing."""
        return 1.0

    def _smooth_value(self, current, target):
        alpha = self._get_snap_alpha()
        return (current * (1 - alpha)) + (target * alpha)

    def _smooth_resize_value(self, current, target):
        alpha = self._get_resize_snap_alpha()
        return (current * (1 - alpha)) + (target * alpha)

    def _apply_smooth_resize(self, new_width, new_height, new_pos_x=None, new_pos_y=None):
        self.prepareGeometryChange()
        if self._snap_anim_size is None:
            self._snap_anim_size = (self.current_width, self.current_height)
        cur_w, cur_h = self._snap_anim_size
        if self._snap_active:
            target_w = new_width
            target_h = new_height
            cur_w = self._smooth_resize_value(cur_w, target_w)
            cur_h = self._smooth_resize_value(cur_h, target_h)
            self._snap_anim_size = (cur_w, cur_h)
            if new_pos_x is not None and new_pos_y is not None:
                if self._snap_anim_pos is None:
                    self._snap_anim_pos = QPointF(self.pos())
                cur_x = self._smooth_resize_value(self._snap_anim_pos.x(), new_pos_x)
                cur_y = self._smooth_resize_value(self._snap_anim_pos.y(), new_pos_y)
                self._snap_anim_pos = QPointF(cur_x, cur_y)
                self.setPos(self._snap_anim_pos)
        else:
            cur_w = new_width
            cur_h = new_height
            self._snap_anim_size = (cur_w, cur_h)
            if new_pos_x is not None and new_pos_y is not None:
                self.setPos(new_pos_x, new_pos_y)
        self.prepareGeometryChange()
        self.current_width = cur_w
        self.current_height = cur_h
        self.update_handle_positions()
        self.update()
        self.item_changed.emit()

    def _apply_snap_final_state(self):
        if self._snap_target_size:
            target_w, target_h = self._snap_target_size
            self.prepareGeometryChange()
            if target_w is not None:
                self.current_width = target_w
            if target_h is not None:
                self.current_height = target_h
        if self._snap_target_pos:
            self.setPos(self._snap_target_pos)
        self.update_handle_positions()
        self._snap_anim_pos = None
        self._snap_anim_size = None

    def itemChange(self, change, value):
        if change == QGraphicsItem.ItemPositionChange and self.scene():
            new_pos = value
            view = self.scene().views()[0] if self.scene().views() else None
            snap_enabled = True
            if view and hasattr(view, 'snap_enabled'):
                snap_enabled = view.snap_enabled
            if (self.isSelected()
                    and not self.is_resizing_br
                    and not self.is_resizing_tl
                    and not self._snap_suppressed_until_release):
                snap_target = self.calculate_snapping(value)
                if snap_target != value:
                    self._snap_active = True
                    self._snap_target_pos = snap_target
                    if self._snap_anim_pos is None:
                        self._snap_anim_pos = QPointF(self.pos())
                    new_x = snap_target.x() if self._center_lock_x else self._smooth_value(self._snap_anim_pos.x(), snap_target.x())
                    new_y = snap_target.y() if self._center_lock_y else self._smooth_value(self._snap_anim_pos.y(), snap_target.y())
                    new_pos = QPointF(new_x, new_y)
                    self._snap_anim_pos = new_pos
                else:
                    self._snap_active = False
                    self._snap_target_pos = None
                    self._snap_anim_pos = None
                    self._center_lock_x = False
                    self._center_lock_y = False
            scene_rect = self.scene().sceneRect()
            left_bound = scene_rect.left()
            right_bound = scene_rect.right() - self.current_width
            top_bound = scene_rect.top() + UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT
            bottom_bound = (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING) - self.current_height
            final_x = max(left_bound, min(new_pos.x(), right_bound))
            final_y = max(top_bound, min(new_pos.y(), bottom_bound))
            corrected_pos = QPointF(final_x, final_y)
            if self.pos() != corrected_pos:
                self.item_changed.emit()
            return corrected_pos
        elif change == QGraphicsItem.ItemSelectedHasChanged:
            if value and self.isVisible():
                if self.scene() and self.scene().views():
                    parent_view = self.scene().views()[0]
                    if hasattr(parent_view.parent(), 'enhanced_logger') and parent_view.parent().enhanced_logger:
                        parent_view.parent().enhanced_logger.log_user_action("Item Selected in Portrait", f"Role: {self.assigned_role or 'Unknown'}")
                self.ant_timer.start(100)
            else:
                self.ant_timer.stop()
                self.clear_guides()
        elif change == QGraphicsItem.ItemVisibleHasChanged:
            if not value:
                self.ant_timer.stop()
            elif self.isSelected():
                self.ant_timer.start(100)
        return super(ResizablePixmapItem, self).itemChange(change, value)