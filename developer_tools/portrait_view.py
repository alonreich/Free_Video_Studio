from PyQt5.QtWidgets import QGraphicsView, QGraphicsItem
from PyQt5.QtCore import Qt, QRectF
from PyQt5.QtGui import QPainter
from graphics_items import ResizablePixmapItem
from config import UI_BEHAVIOR

class PortraitView(QGraphicsView):
    def __init__(self, scene, parent=None):
        super().__init__(scene, parent)
        self.zoom = 1.0
        self.min_zoom = 0.2
        self.max_zoom = 4.0
        self.user_zoomed = False
        self._middle_dragging = False
        self._left_panning = False
        self.snap_enabled = True
        self.setRenderHint(QPainter.Antialiasing)
        self.setRenderHint(QPainter.SmoothPixmapTransform)
        self.setViewportUpdateMode(QGraphicsView.FullViewportUpdate)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        self.setTransformationAnchor(QGraphicsView.AnchorUnderMouse)
        self.setResizeAnchor(QGraphicsView.AnchorUnderMouse)

    def set_snap_enabled(self, enabled: bool):
        self.snap_enabled = bool(enabled)

    def fit_to_scene(self):
        if not self.scene(): return
        if self.user_zoomed:
            self._clamp_scroll()
            return
        view_rect = self.viewport().rect()
        scene_rect = self.scene().sceneRect()
        if scene_rect.width() <= 0 or scene_rect.height() <= 0: return
        self.resetTransform()
        content_top = 150
        content_height = scene_rect.height() - content_top
        scale_w = view_rect.width() / scene_rect.width()
        scale_h = view_rect.height() / content_height
        scale = min(scale_w, scale_h)
        scale = min(scale, 1.0)
        self.zoom = scale
        self.scale(scale, scale)
        center_x = scene_rect.center().x()
        center_y = content_top + (content_height / 2)
        self.centerOn(center_x, center_y)
        self.user_zoomed = False

    def wheelEvent(self, event):
        angle = event.angleDelta().y()
        if angle == 0: return
        view_rect = self.viewport().rect()
        scene_rect = self.scene().sceneRect()
        min_allowed_zoom = 0.1
        if scene_rect.width() > 0 and scene_rect.height() > 0:
            scale_w = view_rect.width() / scene_rect.width()
            scale_h = view_rect.height() / scene_rect.height()
            min_allowed_zoom = min(scale_w, scale_h)
        zoom_factor = 1.15 if angle > 0 else 1 / 1.15
        new_zoom = self.zoom * zoom_factor
        new_zoom = max(min_allowed_zoom, min(self.max_zoom, new_zoom))
        if abs(new_zoom - self.zoom) < 0.0001: 
            return
        factor = new_zoom / self.zoom
        self.zoom = new_zoom
        self.scale(factor, factor)
        self.user_zoomed = True
        self._clamp_scroll()
        for item in self.scene().items():
            if hasattr(item, 'update_handle_positions'):
                item.update_handle_positions()
        
    def _clamp_scroll(self):
        hbar = self.horizontalScrollBar()
        vbar = self.verticalScrollBar()
        if not hbar or not vbar: return
        bounds = self.scene().sceneRect()
        view_rect = self.viewport().rect()
        top_left = self.mapToScene(view_rect.topLeft())
        bottom_right = self.mapToScene(view_rect.bottomRight())
        view_scene_rect = QRectF(top_left, bottom_right).normalized()
        dx = 0
        if view_scene_rect.left() < bounds.left(): dx = bounds.left() - view_scene_rect.left()
        elif view_scene_rect.right() > bounds.right(): dx = bounds.right() - view_scene_rect.right()
        dy = 0
        if view_scene_rect.top() < bounds.top(): dy = bounds.top() - view_scene_rect.top()
        elif view_scene_rect.bottom() > bounds.bottom(): dy = bounds.bottom() - view_scene_rect.bottom()
        if dx != 0: hbar.setValue(hbar.value() + int(dx * self.zoom))
        if dy != 0: vbar.setValue(vbar.value() + int(dy * self.zoom))

    def mousePressEvent(self, event):
        if event.button() == Qt.MiddleButton:
            self._middle_dragging = True
            self._last_pan_pos = event.pos()
            self.viewport().setCursor(Qt.ClosedHandCursor)
            return
        if event.button() == Qt.LeftButton:
            clicked_items = self.items(event.pos())
            is_over_hud = any(isinstance(i, ResizablePixmapItem) for i in clicked_items)
            if not is_over_hud:
                if self._can_scroll():
                    self._left_panning = True
                    self._last_pan_pos = event.pos()
                    self.viewport().setCursor(Qt.ClosedHandCursor)
                    return
        return super().mousePressEvent(event)

    def _can_scroll(self):
        """Checks if there is any active scrollable area."""
        hbar = self.horizontalScrollBar()
        vbar = self.verticalScrollBar()
        return (hbar and hbar.maximum() > hbar.minimum()) or (vbar and vbar.maximum() > vbar.minimum())

    def mouseMoveEvent(self, event):
        if self._left_panning or self._middle_dragging:
            delta = event.pos() - self._last_pan_pos
            self._last_pan_pos = event.pos()
            hbar = self.horizontalScrollBar()
            vbar = self.verticalScrollBar()
            if hbar: hbar.setValue(hbar.value() - delta.x())
            if vbar: vbar.setValue(vbar.value() - delta.y())
            return
        item = self.itemAt(event.pos())
        if not item or not (item.flags() & QGraphicsItem.ItemIsMovable):
            if self._can_scroll():
                self.viewport().setCursor(Qt.OpenHandCursor)
            else:
                self.viewport().setCursor(Qt.ArrowCursor)
        else:
            self.viewport().setCursor(Qt.OpenHandCursor)
        super().mouseMoveEvent(event)

    def contextMenuEvent(self, event):
        """[FIX #10, #18] Context menu for layering and autospacing."""
        selected_items = [item for item in self.scene().selectedItems() if isinstance(item, ResizablePixmapItem)]
        if not selected_items:
            return
            
        from PyQt5.QtWidgets import QMenu, QAction
        menu = QMenu(self)
        top_action = QAction("Bring Forward", self)
        top_action.triggered.connect(lambda: self.window().raise_selected_item())
        menu.addAction(top_action)
        bottom_action = QAction("Send Backward", self)
        bottom_action.triggered.connect(lambda: self.window().lower_selected_item())
        menu.addAction(bottom_action)
        menu.addSeparator()
        autospace_action = QAction("Autospace Overlaps", self)
        autospace_action.triggered.connect(lambda: self._autospace_items(selected_items))
        menu.addAction(autospace_action)
        menu.exec_(event.globalPos())

    def _autospace_items(self, items):
        """Simple algorithm to push overlapping items apart with coalesced undo."""
        if len(items) < 2: return
        app = self.window()
        if not hasattr(app, '_get_item_state'): return
        states_before = {item: app._get_item_state(item) for item in items}
        modified_items = []
        items.sort(key=lambda i: i.scenePos().y())
        was_in_undo = getattr(app, '_in_undo_redo', False)
        app._in_undo_redo = True
        try:
            for i in range(len(items)):
                for j in range(i + 1, len(items)):
                    item_a = items[i]
                    item_b = items[j]
                    rect_a = item_a.sceneBoundingRect()
                    rect_b = item_b.sceneBoundingRect()
                    if rect_a.intersects(rect_b):
                        overlap_h = rect_a.bottom() - rect_b.top()
                        item_b.moveBy(0, overlap_h + 10)
                        if item_b not in modified_items:
                            modified_items.append(item_b)
        finally:
            app._in_undo_redo = was_in_undo
        if modified_items and hasattr(app, 'register_undo_action'):
            states_after = {item: app._get_item_state(item) for item in items}
            
            def undo_autospace(states=states_before):
                 for item, state in states.items():
                     app._apply_item_state(item, state)
                 app._mark_dirty()
                 return True
            
            def redo_autospace(states=states_after):
                 for item, state in states.items():
                     app._apply_item_state(item, state)
                 app._mark_dirty()
                 return True
            app.register_undo_action("Autospace Overlaps", undo_autospace, redo_autospace)
            for item in modified_items:
                if item.assigned_role:
                    app.modified_roles.add(item.assigned_role)
            app._mark_dirty()
            self.scene().update()

    def mouseReleaseEvent(self, event):
        if (self._middle_dragging and event.button() == Qt.MiddleButton) or \
           (self._left_panning and event.button() == Qt.LeftButton):
            self._middle_dragging = False
            self._left_panning = False
            if self._can_scroll():
                self.viewport().setCursor(Qt.OpenHandCursor)
            else:
                self.viewport().setCursor(Qt.ArrowCursor)
            self._clamp_scroll()
            return
        super().mouseReleaseEvent(event)
        self.mouseMoveEvent(event)

    def keyPressEvent(self, event):
        if event.key() == Qt.Key_Delete:
            if hasattr(self.window(), 'delete_selected'):
                self.window().delete_selected()
                event.accept()
                return
        selected_items = self.scene().selectedItems()
        if selected_items:
            for item in selected_items:
                if hasattr(item, '_snap_active'):
                    item._snap_active = False
                if hasattr(item, '_snap_target_pos'):
                    item._snap_target_pos = None
                if hasattr(item, '_snap_anim_pos'):
                    item._snap_anim_pos = None
            items_to_move = selected_items
            if event.modifiers() & Qt.ShiftModifier:
                delta = 5.0
            else:
                delta = UI_BEHAVIOR.KEYBOARD_NUDGE_STEP
            if event.modifiers() & Qt.AltModifier:
                if event.key() == Qt.Key_Up:
                    if hasattr(self.window(), 'raise_selected_item'):
                        self.window().raise_selected_item()
                    event.accept()
                    return
                elif event.key() == Qt.Key_Down:
                    if hasattr(self.window(), 'lower_selected_item'):
                        self.window().lower_selected_item()
                    event.accept()
                    return
            key = event.key()
            if key == Qt.Key_Up:
                for item in items_to_move:
                    item.moveBy(0, -delta)
            elif key == Qt.Key_Down:
                for item in items_to_move:
                    item.moveBy(0, delta)
            elif key == Qt.Key_Left:
                for item in items_to_move:
                    item.moveBy(-delta, 0)
            elif key == Qt.Key_Right:
                for item in items_to_move:
                    item.moveBy(delta, 0)
            else:
                super().keyPressEvent(event)
                return
            for item in items_to_move:
                if hasattr(item, 'trigger_nudge_feedback'):
                    item.trigger_nudge_feedback()
            if hasattr(self.window(), 'on_item_modified'):
                for item in items_to_move:
                    self.window().on_item_modified(item)
            event.accept()
        else:
            super().keyPressEvent(event)
