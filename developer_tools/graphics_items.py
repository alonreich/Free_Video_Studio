from PyQt5.QtWidgets import QGraphicsObject, QGraphicsRectItem, QGraphicsItem, QGraphicsLineItem, QGraphicsSimpleTextItem
from PyQt5.QtCore import Qt, pyqtSignal, QRectF, QTimer, QPointF
from PyQt5.QtGui import QBrush, QColor, QPen, QFont
from config import UI_LAYOUT, UI_BEHAVIOR, UI_COLORS
from coordinate_math import TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE, scale_round

class MagneticSnapper:
    """Helper class to manage professional magnetic snapping with hysteresis."""

    def __init__(self):
        self.snap_x = None
        self.snap_y = None
        self.acquire_threshold = 8
        self.release_threshold = 12
        self.center_acquire = 15
        self.center_release = 20

    def reset(self):
        self.snap_x = None
        self.snap_y = None

    def calculate(self, proposed_pos, item_w, item_h, targets_x, targets_y, is_resizing=False):
        """
        proposed_pos: QPointF of the item's top-left
        returns: (final_pos, active_guides_x, active_lines_y)
        """
        new_x, new_y = proposed_pos.x(), proposed_pos.y()
        if self.snap_x:
            target_val, offset_type, label = self.snap_x
            current_val = self._get_val_by_type(new_x, item_w, offset_type)
            if abs(current_val - target_val) > self.release_threshold:
                self.snap_x = None
            else:
                new_x = self._apply_snap(new_x, item_w, target_val, offset_type)
        if not self.snap_x:
            best_snap = self._find_best_snap(new_x, item_w, targets_x, self.acquire_threshold)
            if best_snap:
                self.snap_x = best_snap
                new_x = self._apply_snap(new_x, item_w, best_snap[0], best_snap[1])
        if self.snap_y:
            target_val, offset_type, label = self.snap_y
            current_val = self._get_val_by_type(new_y, item_h, offset_type)
            if abs(current_val - target_val) > self.release_threshold:
                self.snap_y = None
            else:
                new_y = self._apply_snap(new_y, item_h, target_val, offset_type)
        if not self.snap_y:
            best_snap = self._find_best_snap(new_y, item_h, targets_y, self.acquire_threshold)
            if best_snap:
                self.snap_y = best_snap
                new_y = self._apply_snap(new_y, item_h, best_snap[0], best_snap[1])
        return QPointF(new_x, new_y), self.snap_x, self.snap_y

    def _get_val_by_type(self, pos, dim, offset_type):
        if offset_type == 'start': return pos
        if offset_type == 'center': return pos + dim / 2
        if offset_type == 'end': return pos + dim
        return pos

    def _apply_snap(self, pos, dim, target_val, offset_type):
        if offset_type == 'start': return target_val
        if offset_type == 'center': return target_val - dim / 2
        if offset_type == 'end': return target_val - dim
        return pos

    def _find_best_snap(self, pos, dim, targets, threshold):
        """
        targets: list of (val, label)
        returns: (val, offset_type, label) or None
        """
        my_start = pos
        my_center = pos + dim / 2
        my_end = pos + dim
        best = None
        min_d = threshold + 1
        for t_val, t_lab in targets:
            d = abs(my_start - t_val)
            if d < min_d:
                min_d = d
                best = (t_val, 'start', t_lab)
            d = abs(my_end - t_val)
            if d < min_d:
                min_d = d
                best = (t_val, 'end', t_lab)
            d = abs(my_center - t_val)
            if d < min_d and d < self.center_acquire:
                min_d = d
                best = (t_val, 'center', t_lab)
        return best

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
        self.handle_size = 18.0
        self.handle_br = QGraphicsRectItem(QRectF(0, 0, self.handle_size, self.handle_size), self)
        self.handle_br.setBrush(QBrush(Qt.red))
        self.handle_br.setPen(QPen(Qt.white, 2))
        self.handle_tl = QGraphicsRectItem(QRectF(0, 0, self.handle_size, self.handle_size), self)
        self.handle_tl.setBrush(QBrush(Qt.red))
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
        self._snapper = MagneticSnapper()
        self._snap_active = False

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
            if rank == 1:
                painter.setPen(QColor(UI_COLORS.TEXT_WARNING))
            elif rank <= 3:
                painter.setPen(QColor(UI_COLORS.TEXT_ACCENT))
            else:
                painter.setPen(QColor(UI_COLORS.TEXT_SECONDARY))
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
        """[FIX Explosive Scaling] Use fixed-size handles for consistent UX."""
        self.handle_size = 18.0
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
        self._snapper.reset()
        if self._is_on_handle(event.pos(), self.handle_br):
            self.is_resizing_br = True
            self.resize_start_scene_pos = event.scenePos()
            self.start_width = self.current_width
            self.start_height = self.current_height
        elif self._is_on_handle(event.pos(), self.handle_tl):
            self.is_resizing_tl = True
            self.resize_start_scene_pos = event.scenePos()
            self.start_width = self.current_width
            self.start_height = self.current_height
            self.start_pos = self.pos()
        else:
            self.setCursor(Qt.ClosedHandCursor)
            super(ResizablePixmapItem, self).mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if self.is_resizing_br:
            delta = event.scenePos() - self.resize_start_scene_pos
            new_width = max(UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE, self.start_width + delta.x())
            if self.scene():
                right_bound = self.scene().sceneRect().right()
                max_width = right_bound - self.pos().x()
                new_width = min(new_width, max_width)
            aspect = self.original_pixmap.height() / self.original_pixmap.width() if self.original_pixmap.width() > 0 else 0
            new_height = new_width * aspect
            if self.scene():
                bottom_bound = UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING
                max_height = bottom_bound - self.pos().y()
                if new_height > max_height:
                    new_height = max_height
                    new_width = new_height / aspect if aspect > 0 else 0
            self.prepareGeometryChange()
            self.current_width = new_width
            self.current_height = new_height
            self.update_handle_positions()
            self.update()
            self.item_changed.emit()
        elif self.is_resizing_tl:
            delta = event.scenePos() - self.resize_start_scene_pos
            new_pos_x = self.start_pos.x() + delta.x()
            if self.scene():
                new_pos_x = max(self.scene().sceneRect().left(), new_pos_x)
            start_right = self.start_pos.x() + self.start_width
            max_x = start_right - UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE
            new_pos_x = min(new_pos_x, max_x)
            new_width = start_right - new_pos_x
            aspect = self.original_pixmap.height() / self.original_pixmap.width() if self.original_pixmap.width() > 0 else 0
            new_height = new_width * aspect
            start_bottom = self.start_pos.y() + self.start_height
            new_pos_y = start_bottom - new_height
            if self.scene():
                top_bound = self.scene().sceneRect().top() + UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT
                if new_pos_y < top_bound:
                    new_pos_y = top_bound
                    new_height = start_bottom - new_pos_y
                    new_width = new_height / aspect if aspect > 0 else 0
                    new_pos_x = start_right - new_width
            self.prepareGeometryChange()
            self.setPos(new_pos_x, new_pos_y)
            self.current_width = new_width
            self.current_height = new_height
            self.update_handle_positions()
            self.update()
            self.item_changed.emit()
        else:
            super(ResizablePixmapItem, self).mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
        self.setCursor(Qt.OpenHandCursor)
        self.is_resizing_br = False
        self.is_resizing_tl = False
        self.clear_guides()
        self._snapper.reset()
        super(ResizablePixmapItem, self).mouseReleaseEvent(event)
        self.item_changed.emit()

    def clear_guides(self):
        if self.scene():
            for line in self.guide_lines:
                try: self.scene().removeItem(line)
                except: pass
            for label in self.guide_labels:
                try: self.scene().removeItem(label)
                except: pass
        self.guide_lines.clear()
        self.guide_labels.clear()

    def content_scene_rect(self):
        """Returns the pixmap area in scene coordinates, excluding handles."""
        return QRectF(self.scenePos().x(), self.scenePos().y(), self.current_width, self.current_height)

    def _draw_smart_guides(self, snap_x, snap_y):
        self.clear_guides()
        if not self.scene(): return
        scene_rect = self.scene().sceneRect()
        guide_color = QColor("#7DD3FC")
        font = QFont("Segoe UI", 9, QFont.Bold)
        if snap_x:
            val, off_type, lab = snap_x
            line = QGraphicsLineItem(val, scene_rect.top(), val, scene_rect.bottom())
            line.setPen(QPen(guide_color, 1, Qt.DashLine))
            line.setZValue(1000)
            self.scene().addItem(line)
            self.guide_lines.append(line)
            txt = QGraphicsSimpleTextItem(f" {lab} ")
            txt.setBrush(QBrush(guide_color))
            txt.setFont(font)
            txt.setPos(val + 4, self.scenePos().y() - 25)
            txt.setZValue(1001)
            self.scene().addItem(txt)
            self.guide_labels.append(txt)
        if snap_y:
            val, off_type, lab = snap_y
            line = QGraphicsLineItem(scene_rect.left(), val, scene_rect.right(), val)
            line.setPen(QPen(guide_color, 1, Qt.DashLine))
            line.setZValue(1000)
            self.scene().addItem(line)
            self.guide_lines.append(line)
            txt = QGraphicsSimpleTextItem(f" {lab} ")
            txt.setBrush(QBrush(guide_color))
            txt.setFont(font)
            txt.setPos(self.scenePos().x() + 10, val + 4)
            txt.setZValue(1001)
            self.scene().addItem(txt)
            self.guide_labels.append(txt)

    def itemChange(self, change, value):
        if change == QGraphicsItem.ItemPositionChange and self.scene():
            new_pos = value
            view = self.scene().views()[0] if self.scene().views() else None
            snap_enabled = getattr(view, 'snap_enabled', True) if view else True
            if self.isSelected() and not self.is_resizing_br and not self.is_resizing_tl and snap_enabled:
                targets_x = [(0, "Canvas Left"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH, "Canvas Right")]
                targets_y = [
                    (UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT, "Content Top"), 
                    (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2, "Canvas Center"), 
                    (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING, "Content Bottom")
                ]
                for item in self.scene().items():
                    if isinstance(item, ResizablePixmapItem) and item != self and item.isVisible():
                        r = item.content_scene_rect()
                        role = item.assigned_role or "Item"
                        targets_x.extend([(r.left(), f"{role} Left"), (r.center().x(), f"{role} Center"), (r.right(), f"{role} Right")])
                        targets_y.extend([(r.top(), f"{role} Top"), (r.center().y(), f"{role} Center"), (r.bottom(), f"{role} Bottom")])
                new_pos, snap_x, snap_y = self._snapper.calculate(new_pos, self.current_width, self.current_height, targets_x, targets_y)
                self._draw_smart_guides(snap_x, snap_y)
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
                self.ant_timer.start(100)
            else:
                self.ant_timer.stop()
                self.clear_guides()
                self._snapper.reset()
        return super(ResizablePixmapItem, self).itemChange(change, value)
