from PyQt5.QtWidgets import QGraphicsObject, QGraphicsRectItem, QGraphicsItem, QGraphicsLineItem, QGraphicsSimpleTextItem
from PyQt5.QtCore import Qt, pyqtSignal, QRectF, QTimer, QPointF
from PyQt5.QtGui import QBrush, QColor, QPen, QFont
from config import UI_LAYOUT, UI_BEHAVIOR, UI_COLORS
from coordinate_math import TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE, scale_round
import time

class MagneticSnapper:
    """Helper class to manage professional magnetic snapping with velocity gating and Tired Magnet logic."""

    def __init__(self):
        self.snap_x = None
        self.snap_y = None
        self.base_acquire = 8
        self.base_center = 15
        self.release_threshold = 12
        self.pull_away_count_x = 0
        self.last_pull_dir_x = 0
        self.tired_x_until = 0
        self.pull_away_count_y = 0
        self.last_pull_dir_y = 0
        self.tired_y_until = 0

    def reset(self):
        self.snap_x = None
        self.snap_y = None
        self.pull_away_count_x = 0
        self.pull_away_count_y = 0

    def calculate(self, proposed_pos, current_pos, item_w, item_h, targets_x, targets_y, is_resizing=False):
        now = time.time()
        new_x, new_y = proposed_pos.x(), proposed_pos.y()
        velocity = (proposed_pos - current_pos).manhattanLength()
        if velocity > 45 and not is_resizing:
            self.snap_x = None
            self.snap_y = None
            return proposed_pos, None, None
        thresh_x = 2 if now < self.tired_x_until else self.base_acquire
        center_x = 3 if now < self.tired_x_until else self.base_center
        thresh_y = 2 if now < self.tired_y_until else self.base_acquire
        center_y = 3 if now < self.tired_y_until else self.base_center
        if is_resizing:
            thresh_x, center_x = 10, 10
            thresh_y, center_y = 10, 10
        if self.snap_x:
            target_val, offset_type, label = self.snap_x
            curr_val = self._get_val_by_type(new_x, item_w, offset_type)
            diff = curr_val - target_val
            if abs(diff) > self.release_threshold and not is_resizing:
                pull_dir = 1 if diff > 0 else -1
                if pull_dir == self.last_pull_dir_x:
                    self.pull_away_count_x += 1
                else:
                    self.pull_away_count_x = 1
                    self.last_pull_dir_x = pull_dir
                if self.pull_away_count_x >= 2:
                    self.tired_x_until = now + 5.0
                self.snap_x = None
            else:
                new_x = self._apply_snap(new_x, item_w, target_val, offset_type)
        if not self.snap_x:
            best_snap = self._find_best_snap(new_x, item_w, targets_x, thresh_x, center_x)
            if best_snap:
                self.snap_x = best_snap
                new_x = self._apply_snap(new_x, item_w, best_snap[0], best_snap[1])
        if self.snap_y:
            target_val, offset_type, label = self.snap_y
            curr_val = self._get_val_by_type(new_y, item_h, offset_type)
            diff = curr_val - target_val
            if abs(diff) > self.release_threshold and not is_resizing:
                pull_dir = 1 if diff > 0 else -1
                if pull_dir == self.last_pull_dir_y:
                    self.pull_away_count_y += 1
                else:
                    self.pull_away_count_y = 1
                    self.last_pull_dir_y = pull_dir
                if self.pull_away_count_y >= 2:
                    self.tired_y_until = now + 5.0
                self.snap_y = None
            else:
                new_y = self._apply_snap(new_y, item_h, target_val, offset_type)
        if not self.snap_y:
            best_snap = self._find_best_snap(new_y, item_h, targets_y, thresh_y, center_y)
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

    def _find_best_snap(self, pos, dim, targets, threshold, center_thresh):
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
            if d < min_d and d < center_thresh:
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
        self._cached_scaled_pix = None
        self._update_render_cache()
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
        self._is_nudging = False
        self._is_dragging = False
        self._targets_cache_x = None
        self._targets_cache_y = None
        self._nudge_timer = QTimer(self)
        self._nudge_timer.setSingleShot(True)
        self._nudge_timer.timeout.connect(self._clear_nudge_feedback)
        self._nudge_flash_active = False

    def _update_render_cache(self):
        """Pre-renders the item at current size to ensure buttery-smooth dragging."""
        if self.current_width > 0 and self.current_height > 0:
            self._cached_scaled_pix = self.original_pixmap.scaled(
                int(self.current_width), 
                int(self.current_height), 
                Qt.IgnoreAspectRatio, 
                Qt.SmoothTransformation
            )

    def trigger_nudge_feedback(self):
        """Flashes the item cyan to provide professional confirmation of a 1px movement."""
        self._nudge_flash_active = True
        self.update()
        self._nudge_timer.start(150)

    def _clear_nudge_feedback(self):
        self._nudge_flash_active = False
        self.update()

    def update_ant_dash(self):
        self.ant_dash_offset = (self.ant_dash_offset + 1) % 8
        self.update()

    def boundingRect(self):
        h = self.handle_size
        return QRectF(-h, -80, self.current_width + h*2, self.current_height + 160)

    def paint(self, painter, option, widget):
        if self._nudge_flash_active:
            painter.setBrush(QColor(0, 255, 255, 100))
            painter.setPen(QPen(QColor(0, 255, 255), 2))
            painter.drawRect(QRectF(0, 0, self.current_width, self.current_height))
        elif getattr(self, '_is_nudging', False):
            painter.setBrush(QColor(0, 255, 255, 40))
            painter.setPen(QPen(QColor(0, 255, 255), 1))
            painter.drawRect(QRectF(0, 0, self.current_width, self.current_height))
        if self.is_resizing_br or self.is_resizing_tl or self._is_dragging:
            painter.drawPixmap(QRectF(0, 0, self.current_width, self.current_height), 
                                self.original_pixmap, QRectF(self.original_pixmap.rect()))
        elif self._cached_scaled_pix:
            if self._cached_scaled_pix.width() == int(self.current_width) and \
               self._cached_scaled_pix.height() == int(self.current_height):
                painter.drawPixmap(0, 0, self._cached_scaled_pix)
            else:
                painter.drawPixmap(QRectF(0, 0, self.current_width, self.current_height), 
                                    self.original_pixmap, QRectF(self.original_pixmap.rect()))
        else:
            painter.drawPixmap(QRectF(0, 0, self.current_width, self.current_height), 
                                self.original_pixmap, QRectF(self.original_pixmap.rect()))        
        black_pen = QPen(Qt.black, 3)
        painter.setPen(black_pen)
        painter.setBrush(Qt.NoBrush)
        painter.drawRect(QRectF(-1.0, -1.0, self.current_width + 2, self.current_height + 2))
        if self.assigned_role:
            rank = 1
            scene = self.scene()
            if scene:
                if getattr(scene, '_hud_cache_dirty', True):
                    hud_items = [i for i in scene.items() if isinstance(i, ResizablePixmapItem)]
                    hud_items.sort(key=lambda i: i.zValue(), reverse=True)
                    scene._hud_items_cache = hud_items
                    scene._hud_cache_dirty = False
                try:
                    rank = scene._hud_items_cache.index(self) + 1
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
            self._is_dragging = True
            self._targets_cache_x = [(0, "Canvas Left"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH, "Canvas Right")]
            self._targets_cache_y = [
                (UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT, "Content Top"), 
                (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2, "Canvas Center"), 
                (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING, "Content Bottom")
            ]
            if self.scene():
                for item in self.scene().items():
                    if isinstance(item, ResizablePixmapItem) and item != self and item.isVisible():
                        r = item.content_scene_rect()
                        role = item.assigned_role or "Item"
                        self._targets_cache_x.extend([(r.left(), f"{role} Left"), (r.center().x(), f"{role} Center"), (r.right(), f"{role} Right")])
                        self._targets_cache_y.extend([(r.top(), f"{role} Top"), (r.center().y(), f"{role} Center"), (r.bottom(), f"{role} Bottom")])
            super(ResizablePixmapItem, self).mousePressEvent(event)

    def mouseMoveEvent(self, event):
        view = self.scene().views()[0] if self.scene() and self.scene().views() else None
        snap_enabled = getattr(view, 'snap_enabled', True) if view else True
        if self.is_resizing_br:
            delta = event.scenePos() - self.resize_start_scene_pos
            new_width = max(UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE, self.start_width + delta.x())
            proposed_right = self.pos().x() + new_width
            proposed_bottom = self.pos().y() + (new_width * (self.original_pixmap.height() / self.original_pixmap.width()))
            targets_x = [(0, "Canvas Left"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH, "Canvas Right")]
            targets_y = [(UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT, "Content Top"), (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING, "Content Bottom")]
            for item in self.scene().items():
                if isinstance(item, ResizablePixmapItem) and item != self and item.isVisible():
                    r = item.content_scene_rect(); role = item.assigned_role or "Item"
                    targets_x.extend([(r.left(), f"{role} Left"), (r.center().x(), f"{role} Center"), (r.right(), f"{role} Right")])
                    targets_y.extend([(r.top(), f"{role} Top"), (r.center().y(), f"{role} Center"), (r.bottom(), f"{role} Bottom")])
            active_x, active_y = None, None
            for t_val, t_lab in targets_x:
                if abs(proposed_right - t_val) < 10:
                    if snap_enabled: new_width = t_val - self.pos().x()
                    active_x = (t_val, "end", t_lab); break
            aspect = self.original_pixmap.height() / self.original_pixmap.width() if self.original_pixmap.width() > 0 else 0
            new_height = new_width * aspect
            proposed_bottom = self.pos().y() + new_height
            for t_val, t_lab in targets_y:
                if abs(proposed_bottom - t_val) < 10:
                    if snap_enabled: 
                        new_height = t_val - self.pos().y()
                        new_width = new_height / aspect if aspect > 0 else 0
                    active_y = (t_val, "end", t_lab); break
            if active_x or active_y: self._draw_smart_guides(active_x, active_y, self.pos())
            else: self.clear_guides()
            if self.scene():
                right_bound = self.scene().sceneRect().right()
                max_width = right_bound - self.pos().x()
                new_width = min(new_width, max_width)
            self.prepareGeometryChange()
            self.current_width = new_width
            self.current_height = new_height
            self.update_handle_positions()
            self.update(); self.item_changed.emit()
        elif self.is_resizing_tl:
            delta = event.scenePos() - self.resize_start_scene_pos
            start_right = self.start_pos.x() + self.start_width
            start_bottom = self.start_pos.y() + self.start_height
            new_pos_x = self.start_pos.x() + delta.x()
            targets_x = [(0, "Canvas Left"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH, "Canvas Right")]
            targets_y = [(UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT, "Content Top"), (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING, "Content Bottom")]
            for item in self.scene().items():
                if isinstance(item, ResizablePixmapItem) and item != self and item.isVisible():
                    r = item.content_scene_rect(); role = item.assigned_role or "Item"
                    targets_x.extend([(r.left(), f"{role} Left"), (r.center().x(), f"{role} Center"), (r.right(), f"{role} Right")])
                    targets_y.extend([(r.top(), f"{role} Top"), (r.center().y(), f"{role} Center"), (r.bottom(), f"{role} Bottom")])
            active_x, active_y = None, None
            for t_val, t_lab in targets_x:
                if abs(new_pos_x - t_val) < 10:
                    if snap_enabled: new_pos_x = t_val
                    active_x = (t_val, "start", t_lab); break
            new_width = max(UI_LAYOUT.GRAPHICS_ITEM_MIN_SIZE, start_right - new_pos_x)
            aspect = self.original_pixmap.height() / self.original_pixmap.width() if self.original_pixmap.width() > 0 else 0
            new_height = new_width * aspect
            new_pos_y = start_bottom - new_height
            for t_val, t_lab in targets_y:
                if abs(new_pos_y - t_val) < 10:
                    if snap_enabled:
                        new_pos_y = t_val
                        new_height = start_bottom - new_pos_y
                        new_width = new_height / aspect if aspect > 0 else 0
                        new_pos_x = start_right - new_width
                    active_y = (t_val, "start", t_lab); break
            if active_x or active_y: self._draw_smart_guides(active_x, active_y, QPointF(new_pos_x, new_pos_y))
            else: self.clear_guides()
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
            self.update(); self.item_changed.emit()
        else:
            super(ResizablePixmapItem, self).mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
        self.setCursor(Qt.OpenHandCursor)
        self.is_resizing_br = False
        self.is_resizing_tl = False
        self._is_dragging = False
        self._targets_cache_x = None
        self._targets_cache_y = None
        self._update_render_cache()
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

    def _draw_smart_guides(self, snap_x, snap_y, new_pos=None):
        self.clear_guides()
        if not self.scene(): return
        display_pos = new_pos if new_pos is not None else self.scenePos()
        scene_rect = self.scene().sceneRect()
        guide_color = QColor("#7DD3FC")
        font = QFont("Segoe UI", 9, QFont.Bold)
        if snap_x:
            val, off_type, target_lab = snap_x
            my_edge = {"start": "LEFT", "center": "CENTER", "end": "RIGHT"}.get(off_type, "EDGE")
            full_lab = f" [MY {my_edge}] ↔ [{target_lab.upper()}] "
            line = QGraphicsLineItem(val, scene_rect.top(), val, scene_rect.bottom())
            line.setPen(QPen(guide_color, 1, Qt.DashLine))
            line.setZValue(1000)
            self.scene().addItem(line)
            self.guide_lines.append(line)
            txt = QGraphicsSimpleTextItem(full_lab)
            txt.setBrush(QBrush(guide_color))
            txt.setFont(font)
            txt.setPos(val + 4, display_pos.y() - 30)
            txt.setZValue(1001)
            self.scene().addItem(txt)
            self.guide_labels.append(txt)
        if snap_y:
            val, off_type, target_lab = snap_y
            my_edge = {"start": "TOP", "center": "CENTER", "end": "BOTTOM"}.get(off_type, "EDGE")
            full_lab = f" [MY {my_edge}] ↔ [{target_lab.upper()}] "
            line = QGraphicsLineItem(scene_rect.left(), val, scene_rect.right(), val)
            line.setPen(QPen(guide_color, 1, Qt.DashLine))
            line.setZValue(1000)
            self.scene().addItem(line)
            self.guide_lines.append(line)
            txt = QGraphicsSimpleTextItem(full_lab)
            txt.setBrush(QBrush(guide_color))
            txt.setFont(font)
            txt.setPos(display_pos.x() + 15, val + 4)
            txt.setZValue(1001)
            self.scene().addItem(txt)
            self.guide_labels.append(txt)

    def setZValue(self, z):
        super(ResizablePixmapItem, self).setZValue(z)
        if self.scene():
            self.scene()._hud_cache_dirty = True

    def itemChange(self, change, value):
        if change == QGraphicsItem.ItemSceneHasChanged:
            if self.scene():
                self.scene()._hud_cache_dirty = True
        elif change == QGraphicsItem.ItemPositionChange and self.scene():
            new_pos = value
            view = self.scene().views()[0] if self.scene().views() else None
            snap_enabled = getattr(view, 'snap_enabled', True) if view else True
            if self.isSelected() and not self.is_resizing_br and not self.is_resizing_tl:
                if self._is_dragging:
                    tx, ty = self._targets_cache_x, self._targets_cache_y
                else:
                    tx = [(0, "Canvas Left"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH / 2, "Canvas Center"), (UI_LAYOUT.PORTRAIT_BASE_WIDTH, "Canvas Right")]
                    ty = [
                        (UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT, "Content Top"), 
                        (UI_LAYOUT.PORTRAIT_BASE_HEIGHT / 2, "Canvas Center"), 
                        (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING, "Content Bottom")
                    ]
                    for item in self.scene().items():
                        if isinstance(item, ResizablePixmapItem) and item != self and item.isVisible():
                            r = item.content_scene_rect()
                            role = item.assigned_role or "Item"
                            tx.extend([(r.left(), f"{role} Left"), (r.center().x(), f"{role} Center"), (r.right(), f"{role} Right")])
                            ty.extend([(r.top(), f"{role} Top"), (r.center().y(), f"{role} Center"), (r.bottom(), f"{role} Bottom")])
                if tx and ty:
                    res_pos, snap_x, snap_y = self._snapper.calculate(new_pos, self.pos(), self.current_width, self.current_height, tx, ty)
                    if snap_enabled and not self._is_nudging:
                        new_pos = res_pos
                    self._draw_smart_guides(snap_x, snap_y, new_pos)
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
