from PyQt5.QtWidgets import QGraphicsObject, QGraphicsRectItem, QGraphicsItem, QGraphicsLineItem, QGraphicsSimpleTextItem
from PyQt5.QtCore import Qt, pyqtSignal, QRectF, QTimer, QPointF
from PyQt5.QtGui import QBrush, QColor, QPen, QFont
from config import UI_LAYOUT, UI_BEHAVIOR, UI_COLORS

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
            proposed_right = self.pos().x() + new_width
            snapped_right = self.calculate_snapping(self.pos(), resize_edge='R', proposed_value=proposed_right)
            if snapped_right is not None:
                new_width = snapped_right - self.pos().x()
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
            if self.scene():
                bottom_bound = UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING
                max_height = bottom_bound - self.pos().y()
                if new_height > max_height:
                    new_height = max_height
                    new_width = new_height / aspect if aspect > 0 else 0
            if new_width > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE and new_height > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE:
                self.prepareGeometryChange()
                self.current_width = new_width
                self.current_height = new_height
                self.update_handle_positions()
                self.setPos(self.itemChange(QGraphicsItem.ItemPositionChange, self.pos()))
                self.item_changed.emit()
        elif self.is_resizing_tl:
            delta = event.pos() - self.resize_start_pos
            new_pos_x = self.start_pos.x() + delta.x()
            snapped_left = self.calculate_snapping(QPointF(new_pos_x, self.start_pos.y()), resize_edge='L', proposed_value=new_pos_x)
            if snapped_left is not None:
                new_pos_x = snapped_left
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
            if self.scene():
                top_bound = self.scene().sceneRect().top() + UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT
                if new_pos_y < top_bound:
                    new_pos_y = top_bound
                    new_height = self.start_pos.y() - new_pos_y + self.start_height
                    new_width = new_height / aspect if aspect > 0 else 0
                    new_pos_x = self.start_pos.x() + (self.start_width - new_width)
            if new_width > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE and new_height > UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE:
                self.prepareGeometryChange()
                self.current_width = new_width
                self.current_height = new_height
                self.setPos(new_pos_x, new_pos_y)
                self.update_handle_positions()
                self.item_changed.emit()
        else:
            super(ResizablePixmapItem, self).mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
        self.is_resizing_br = False
        self.is_resizing_tl = False
        self.clear_guides()
        super(ResizablePixmapItem, self).mouseReleaseEvent(event)
        self.item_changed.emit()

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
        min_x_dist = UI_BEHAVIOR.SNAP_THRESHOLD
        min_y_dist = UI_BEHAVIOR.SNAP_THRESHOLD
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
            (UI_LAYOUT.PORTRAIT_BASE_WIDTH / 2, 'C', "Canvas Center"),
            (UI_LAYOUT.PORTRAIT_BASE_WIDTH, 'R', "Canvas Right")
        ]
        targets_y = [
            (UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT, 'T', "Content Top"),
            (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2, 'C', "Canvas Center"),
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
                if resize_edge in [None, 'R']:
                    d_r = abs(my_right - tx)
                    if d_r < min_x_dist:
                        min_x_dist = d_r
                    if resize_edge == 'R':
                        snapped_coord = tx
                    else:
                        final_x_val = tx - my_w
                    active_snap_x = (tx, f"Align Right -> {t_label}")
                if resize_edge is None:
                    d_c = abs(my_center_x - tx)
                    if d_c < min_x_dist:
                        min_x_dist = d_c
                        final_x_val = tx - (my_w / 2)
                        active_snap_x = (tx, f"Center -> {t_label}")
        final_y_val = my_top if resize_edge != 'B' else my_bottom
        active_snap_y = None
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
                if resize_edge in [None, 'B']:
                    d_b = abs(my_bottom - ty)
                    if d_b < min_y_dist:
                        min_y_dist = d_b
                    if resize_edge == 'B':
                        snapped_coord = ty
                    else:
                        final_y_val = ty - my_h
                    active_snap_y = (ty, f"Align Bottom -> {t_label}")
                if resize_edge is None:
                    d_c = abs(my_center_y - ty)
                    if d_c < min_y_dist:
                        min_y_dist = d_c
                        final_y_val = ty - (my_h / 2)
                        active_snap_y = (ty, f"Center -> {t_label}")
        pen = QPen(QColor("#00FFFF"), 1, Qt.DashLine)
        font = QFont("Arial", 9)
        current_snap_key = (active_snap_x, active_snap_y)
        if current_snap_key != self._last_snap_key:
            self.clear_guides()
            self._last_snap_key = current_snap_key
            if active_snap_x:
                lx, label = active_snap_x
                line = QGraphicsLineItem(lx, -5000, lx, 5000)
                line.setPen(pen)
                line.setZValue(999)
                self.scene().addItem(line)
                self.guide_lines.append(line)
                text = QGraphicsSimpleTextItem(label)
                text.setBrush(QBrush(QColor("#00FFFF")))
                text.setFont(font)
                text.setPos(lx + 5, (my_top if resize_edge != 'B' else my_bottom) - 20)
                text.setZValue(1000)
                self.scene().addItem(text)
                self.guide_labels.append(text)
            if active_snap_y:
                ly, label = active_snap_y
                line = QGraphicsLineItem(-5000, ly, 5000, ly)
                line.setPen(pen)
                line.setZValue(999)
                self.scene().addItem(line)
                self.guide_lines.append(line)
                text = QGraphicsSimpleTextItem(label)
                text.setBrush(QBrush(QColor("#00FFFF")))
                text.setFont(font)
                text.setPos((my_left if resize_edge != 'R' else my_right) + 5, ly - 20)
                text.setZValue(1000)
                self.scene().addItem(text)
                self.guide_labels.append(text)
        if not snap_enabled:
            return proposed_pos if resize_edge is None else None
        if resize_edge is not None:
            return snapped_coord
        return QPointF(final_x_val, final_y_val)

    def itemChange(self, change, value):
        if change == QGraphicsItem.ItemPositionChange and self.scene():
            self.clear_guides()
            new_pos = value
            view = self.scene().views()[0] if self.scene().views() else None
            snap_enabled = True
            if view and hasattr(view, 'snap_enabled'):
                snap_enabled = view.snap_enabled
            if self.isSelected() and not self.is_resizing_br and not self.is_resizing_tl:
                new_pos = self.calculate_snapping(value)
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