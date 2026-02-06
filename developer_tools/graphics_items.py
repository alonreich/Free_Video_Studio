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
        self.current_width = pixmap.width()
        self.current_height = pixmap.height()
        self.setFlags(QGraphicsItem.ItemIsMovable | QGraphicsItem.ItemIsSelectable | QGraphicsItem.ItemSendsGeometryChanges)
        self.setAcceptHoverEvents(True)
        self.handle_size = UI_LAYOUT.GRAPHICS_HANDLE_SIZE
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
        return QRectF(-30, -80, self.current_width + 60, self.current_height + 130)

    def paint(self, painter, option, widget):
        painter.drawPixmap(QRectF(0, 0, self.current_width, self.current_height), 
                            self.original_pixmap, QRectF(self.original_pixmap.rect()))
        black_pen = QPen(Qt.black, 2)
        painter.setPen(black_pen)
        painter.setBrush(Qt.NoBrush)
        painter.drawRect(QRectF(-1, -1, self.current_width + 2, self.current_height + 2))
        if self.assigned_role:
            title_text = self.assigned_role.upper()
            font = painter.font()
            font.setBold(True)
            font.setPixelSize(UI_LAYOUT.GRAPHICS_TEXT_FONT_SIZE)
            painter.setFont(font)
            fm = painter.fontMetrics()
            text_w = fm.width(title_text) + UI_LAYOUT.GRAPHICS_TEXT_PADDING
            text_h = fm.height() + UI_LAYOUT.GRAPHICS_TEXT_HEIGHT_PAD
            scene_y = self.scenePos().y()
            center_x = (self.current_width - text_w) / 2
            if scene_y > (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2):
                draw_y = -text_h - 5
            else:
                draw_y = self.current_height + UI_LAYOUT.GRAPHICS_TEXT_OFFSET_Y
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor(0, 0, 0, 180))
            painter.drawRoundedRect(QRectF(center_x, draw_y, text_w, text_h), 6, 6)
            painter.setPen(QColor(UI_COLORS.TEXT_WARNING))
            painter.drawText(QRectF(center_x, draw_y, text_w, text_h), Qt.AlignCenter, title_text)
        if self.isSelected():
            pen = QPen(QColor(UI_COLORS.MARCHING_ANTS), 3, Qt.CustomDashLine)
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
        if self.handle_br.isUnderMouse():
            self.setCursor(Qt.SizeFDiagCursor)
        elif self.handle_tl.isUnderMouse():
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
            self._snap_active = False
            self._snap_repeat_key = None
            self._snap_repeat_count = 0
            self._snap_suppressed_until_release = False
        elif self.handle_tl.isUnderMouse():
            self.is_resizing_tl = True
            self.resize_start_pos = event.pos()
            self.start_width = self.current_width
            self.start_height = self.current_height
            self.start_pos = self.pos()
            self._snap_active = False
            self._snap_repeat_key = None
            self._snap_repeat_count = 0
            self._snap_suppressed_until_release = False
        else:
            self._snap_active = False
            self._snap_repeat_key = None
            self._snap_repeat_count = 0
            self._snap_suppressed_until_release = False
            super(ResizablePixmapItem, self).mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if self.is_resizing_br:
            delta = event.pos() - self.resize_start_pos
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
            delta = event.pos() - self.resize_start_pos
            new_pos_x = self.start_pos.x() + delta.x()
            snapped_left = self.calculate_snapping(QPointF(new_pos_x, self.start_pos.y()), resize_edge='L', proposed_value=new_pos_x)
            if snapped_left is not None:
                new_pos_x = snapped_left
                self._snap_active = True
                self._snap_target_pos = QPointF(new_pos_x, self.start_pos.y())
            if self.scene():
                new_pos_x = max(self.scene().sceneRect().left(), new_pos_x)
            clamped_delta_x = new_pos_x - self.start_pos.x()
            new_width = self.start_width - clamped_delta_x
            aspect = self.original_pixmap.height() / self.original_pixmap.width() if self.original_pixmap.width() > 0 else 0
            new_height = new_width * aspect
            new_pos_y = self.start_pos.y() + (self.start_height - new_height)
            snapped_top = self.calculate_snapping(QPointF(new_pos_x, new_pos_y), resize_edge='T', proposed_value=new_pos_y)
            if snapped_top is not None:
                new_pos_y = snapped_top
                new_height = self.start_pos.y() + self.start_height - new_pos_y
                new_width = new_height / aspect if aspect > 0 else new_width
                new_pos_x = self.start_pos.x() + self.start_width - new_width
                self._snap_active = True
                self._snap_target_pos = QPointF(new_pos_x, new_pos_y)
            if self.scene():
                top_bound = self.scene().sceneRect().top() + UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT
                if new_pos_y < top_bound:
                    new_pos_y = top_bound
                    new_height = self.start_pos.y() - new_pos_y + self.start_height
                    new_width = new_height / aspect if aspect > 0 else 0
                    new_pos_x = self.start_pos.x() + (self.start_width - new_width)
            if new_width > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE and new_height > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE:
                self._apply_smooth_resize(new_width, new_height, new_pos_x, new_pos_y)
        else:
            super(ResizablePixmapItem, self).mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
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

    def calculate_snapping(self, proposed_pos, resize_edge=None, proposed_value=None):
        if not self.scene(): return proposed_pos if resize_edge is None else None
        if len(self.scene().selectedItems()) > 1 and resize_edge is None:
            return proposed_pos
        view = None
        if self.scene().views():
            view = self.scene().views()[0]
        snap_enabled = getattr(view, 'snap_enabled', True)
        default_threshold = getattr(UI_BEHAVIOR, 'SNAP_THRESHOLD', 15)
        min_x_dist = default_threshold
        min_y_dist = default_threshold
        center_threshold = getattr(UI_BEHAVIOR, 'SNAP_CENTER_THRESHOLD', 80)
        guide_threshold = getattr(UI_BEHAVIOR, 'SNAP_GUIDE_THRESHOLD', 95)
        center_lock_threshold = getattr(UI_BEHAVIOR, 'SNAP_CENTER_LOCK_THRESHOLD', 60)
        center_lock_x = False
        center_lock_y = False
        my_w = self.current_width
        my_h = self.current_height
        curr_pos = self.pos()
        my_left = curr_pos.x()
        my_right = my_left + my_w
        my_top = curr_pos.y()
        my_bottom = my_top + my_h
        my_center_x = (my_left + my_right) / 2
        my_center_y = (my_top + my_bottom) / 2
        if resize_edge is None:
            my_left = proposed_pos.x()
            my_right = my_left + my_w
            my_center_x = my_left + (my_w / 2)
            my_top = proposed_pos.y()
            my_bottom = my_top + my_h
            my_center_y = my_top + (my_h / 2)
        else:
            if resize_edge == 'R':
                my_right = proposed_value
                my_center_x = (my_left + my_right) / 2
            elif resize_edge == 'L':
                my_left = proposed_value
                my_center_x = (my_left + my_right) / 2
            elif resize_edge == 'B':
                my_bottom = proposed_value
                my_center_y = (my_top + my_bottom) / 2
            elif resize_edge == 'T':
                my_top = proposed_value
                my_center_y = (my_top + my_bottom) / 2
        targets_x = [
            (0, 'L', "Canvas Left"),
            (UI_LAYOUT.PORTRAIT_BASE_WIDTH / 2.0, 'C', "Canvas Center"),
            (UI_LAYOUT.PORTRAIT_BASE_WIDTH, 'R', "Canvas Right")
        ]
        targets_y = [
            (UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT, 'T', "Content Top"),
            (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2.0, 'C', "Canvas Center"),
            (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING, 'B', "Content Bottom")
        ]
        for item in self.scene().items():
            if isinstance(item, ResizablePixmapItem) and item != self and item.isVisible():
                pos = item.scenePos()
                w = item.current_width
                h = item.current_height
                role = item.assigned_role or "Item"
                targets_x.append((pos.x(), 'L', f"{role} Left"))
                targets_x.append((pos.x() + w, 'R', f"{role} Right"))
                targets_x.append((pos.x() + (w / 2), 'C', f"{role} Center"))
                targets_y.append((pos.y(), 'T', f"{role} Top"))
                targets_y.append((pos.y() + h, 'B', f"{role} Bottom"))
                targets_y.append((pos.y() + (h / 2), 'C', f"{role} Center"))
        final_x_val = my_left if resize_edge != 'R' else my_right
        active_snap_x = None
        guide_snap_x = None
        snapped_coord = None
        if resize_edge in [None, 'L', 'R']:
            for tx, t_type, t_label in targets_x:
                if resize_edge in [None, 'L']:
                    d_l = abs(my_left - tx)
                    if d_l < min_x_dist:
                        min_x_dist = d_l
                        if resize_edge == 'L':
                            snapped_coord = tx
                        else:
                            final_x_val = tx
                        active_snap_x = (tx, f"Align Left -> {t_label}")
                    elif d_l < guide_threshold and guide_snap_x is None:
                        guide_snap_x = (tx, f"Align Left -> {t_label}")
                if resize_edge in [None, 'R']:
                    d_r = abs(my_right - tx)
                    if d_r < min_x_dist:
                        min_x_dist = d_r
                        if resize_edge == 'R':
                            snapped_coord = tx
                        else:
                            final_x_val = tx - my_w
                        active_snap_x = (tx, f"Align Right -> {t_label}")
                    elif d_r < guide_threshold and guide_snap_x is None:
                        guide_snap_x = (tx, f"Align Right -> {t_label}")
                if resize_edge is None:
                    d_c = abs(my_center_x - tx)
                    if d_c < center_threshold:
                        min_x_dist = d_c
                        final_x_val = tx - (my_w / 2)
                        active_snap_x = (tx, f"Center -> {t_label}")
                        if d_c < center_lock_threshold:
                            center_lock_x = True
                    elif d_c < guide_threshold and guide_snap_x is None:
                        guide_snap_x = (tx, f"Center -> {t_label}")
        final_y_val = my_top if resize_edge != 'B' else my_bottom
        active_snap_y = None
        guide_snap_y = None
        if resize_edge in [None, 'T', 'B']:
            for ty, t_type, t_label in targets_y:
                if resize_edge in [None, 'T']:
                    d_t = abs(my_top - ty)
                    if d_t < min_y_dist:
                        min_y_dist = d_t
                        if resize_edge == 'T':
                            snapped_coord = ty
                        else:
                            final_y_val = ty
                        active_snap_y = (ty, f"Align Top -> {t_label}")
                    elif d_t < guide_threshold and guide_snap_y is None:
                        guide_snap_y = (ty, f"Align Top -> {t_label}")
                if resize_edge in [None, 'B']:
                    d_b = abs(my_bottom - ty)
                    if d_b < min_y_dist:
                        min_y_dist = d_b
                        if resize_edge == 'B':
                            snapped_coord = ty
                        else:
                            final_y_val = ty - my_h
                        active_snap_y = (ty, f"Align Bottom -> {t_label}")
                    elif d_b < guide_threshold and guide_snap_y is None:
                        guide_snap_y = (ty, f"Align Bottom -> {t_label}")
                if resize_edge is None:
                    d_c = abs(my_center_y - ty)
                    if d_c < center_threshold:
                        min_y_dist = d_c
                        final_y_val = ty - (my_h / 2)
                        active_snap_y = (ty, f"Center -> {t_label}")
                        if d_c < center_lock_threshold:
                            center_lock_y = True
                    elif d_c < guide_threshold and guide_snap_y is None:
                        guide_snap_y = (ty, f"Center -> {t_label}")
        pen = QPen(QColor("#00FFFF"), 1, Qt.DashLine)
        font = QFont("Arial", 9)
        scene_rect = self.scene().sceneRect() if self.scene() else None
        x_min = scene_rect.left() if scene_rect else -5000
        x_max = scene_rect.right() if scene_rect else 5000
        y_min = scene_rect.top() if scene_rect else -5000
        y_max = scene_rect.bottom() if scene_rect else 5000
        line_snap_x = active_snap_x or guide_snap_x
        line_snap_y = active_snap_y or guide_snap_y
        current_snap_key = (line_snap_x, line_snap_y)
        if current_snap_key != self._last_snap_key:
            self.clear_guides()
            self._last_snap_key = current_snap_key
            if line_snap_x:
                lx, label = line_snap_x
                line = QGraphicsLineItem(lx, y_min, lx, y_max)
                line.setPen(QPen(QColor("#33ffff"), 2, Qt.DashLine))
                line.setZValue(999)
                self.scene().addItem(line)
                self.guide_lines.append(line)
                text = QGraphicsSimpleTextItem(label)
                text.setBrush(QBrush(QColor("#33ffff")))
                text.setFont(font)
                text.setPos(lx + 5, (my_top if resize_edge != 'B' else my_bottom) - 20)
                text.setZValue(1000)
                self.scene().addItem(text)
                self.guide_labels.append(text)
            if line_snap_y:
                ly, label = line_snap_y
                line = QGraphicsLineItem(x_min, ly, x_max, ly)
                line.setPen(QPen(QColor("#33ffff"), 2, Qt.DashLine))
                line.setZValue(999)
                self.scene().addItem(line)
                self.guide_lines.append(line)
                text = QGraphicsSimpleTextItem(label)
                text.setBrush(QBrush(QColor("#33ffff")))
                text.setFont(font)
                text.setPos((my_left if resize_edge != 'R' else my_right) + 5, ly - 20)
                text.setZValue(1000)
                self.scene().addItem(text)
                self.guide_labels.append(text)
        self._center_lock_x = center_lock_x
        self._center_lock_y = center_lock_y
        if not snap_enabled or self._snap_suppressed_until_release:
            return proposed_pos if resize_edge is None else None
        if resize_edge is not None:
            return snapped_coord
        if resize_edge is None:
            snap_key = (active_snap_x, active_snap_y)
            if snap_key == self._snap_repeat_key and snap_key != (None, None):
                self._snap_repeat_count += 1
            else:
                self._snap_repeat_key = snap_key
                self._snap_repeat_count = 1 if snap_key != (None, None) else 0
            repeat_limit = getattr(UI_BEHAVIOR, 'SNAP_REPEAT_SUPPRESS_COUNT', 4)
            if self._snap_repeat_count >= repeat_limit and snap_key != (None, None):
                self._snap_suppressed_until_release = True
                return proposed_pos
        if center_lock_x:
            final_x_val = (active_snap_x[0] - (my_w / 2)) if active_snap_x else final_x_val
        if center_lock_y:
            final_y_val = (active_snap_y[0] - (my_h / 2)) if active_snap_y else final_y_val
        return QPointF(final_x_val, final_y_val)

    def _get_snap_alpha(self):
        alpha = getattr(UI_BEHAVIOR, 'SNAP_SMOOTHING_ALPHA', 0.35)
        try:
            return max(0.05, min(float(alpha), 0.9))
        except Exception:
            return 0.35

    def _get_resize_snap_alpha(self):
        alpha = getattr(UI_BEHAVIOR, 'SNAP_RESIZE_SMOOTHING_ALPHA', self._get_snap_alpha())
        try:
            return max(0.05, min(float(alpha), 0.9))
        except Exception:
            return self._get_snap_alpha()

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
        self.current_width = cur_w
        self.current_height = cur_h
        self.update_handle_positions()
        self.item_changed.emit()

    def _apply_snap_final_state(self):
        if self._snap_target_size:
            target_w, target_h = self._snap_target_size
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
            self.clear_guides()
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