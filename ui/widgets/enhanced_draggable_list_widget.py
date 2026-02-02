"""
Enhanced Draggable List Widget with professional ghost blocks, smooth animations,
and industry-standard drag-and-drop UX patterns.
"""

from PyQt5.QtCore import (
    Qt, QRect, QTimer, QPoint, QPropertyAnimation, 
    QEasingCurve, QParallelAnimationGroup,
    pyqtSignal
)

from PyQt5.QtGui import (
    QPainter, QColor, QPen, QTransform
)

from PyQt5.QtWidgets import (
    QListWidget, QAbstractItemView, QApplication, QWidget
)

import math
import time

class GhostItemWidget(QWidget):
    """Professional ghost item widget with smooth animations."""
    
    def __init__(self, source_widget, parent=None):
        super().__init__(parent)
        self.source_widget = source_widget
        self.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.ghost_pixmap = source_widget.grab()
        self.ghost_opacity = 0.7
        self.elevation = 8
        self.scale_factor = 1.0
        self.setFixedSize(self.ghost_pixmap.size())
        
    def paintEvent(self, event):
        """Paint the ghost widget with professional effects."""
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing, True)
        painter.setRenderHint(QPainter.SmoothPixmapTransform, True)
        shadow_color = QColor(0, 100, 200, 80)
        shadow_rect = QRect(0, 0, self.width(), self.height())
        shadow_rect.adjust(self.elevation, self.elevation, 
                          self.elevation, self.elevation)
        painter.save()
        painter.setOpacity(0.3)
        painter.fillRect(shadow_rect, shadow_color)
        painter.restore()
        painter.save()
        painter.setOpacity(self.ghost_opacity)
        if self.scale_factor != 1.0:
            transform = QTransform()
            center_x = self.width() / 2
            center_y = self.height() / 2
            transform.translate(center_x, center_y)
            transform.scale(self.scale_factor, self.scale_factor)
            transform.translate(-center_x, -center_y)
            painter.setTransform(transform)
        painter.drawPixmap(0, 0, self.ghost_pixmap)
        painter.restore()
        highlight_color = QColor(100, 180, 255, 150)
        painter.setPen(QPen(highlight_color, 2, Qt.SolidLine))
        painter.setBrush(Qt.NoBrush)
        painter.drawRect(0, 0, self.width() - 1, self.height() - 1)
        
    def animate_pickup(self):
        """Animate the ghost item when picked up."""
        self.scale_anim = QPropertyAnimation(self, b"scale_factor")
        self.scale_anim.setDuration(150)
        self.scale_anim.setStartValue(1.0)
        self.scale_anim.setEndValue(1.05)
        self.scale_anim.setEasingCurve(QEasingCurve.OutBack)
        self.scale_anim.start()
        
    def animate_drop(self):
        """Animate the ghost item when dropped."""
        self.scale_anim = QPropertyAnimation(self, b"scale_factor")
        self.scale_anim.setDuration(200)
        self.scale_anim.setStartValue(self.scale_factor)
        self.scale_anim.setEndValue(1.0)
        self.scale_anim.setEasingCurve(QEasingCurve.InOutQuad)
        self.scale_anim.start()

class EnhancedDraggableListWidget(QListWidget):
    """
    Professional drag-and-drop list widget with:
    - Ghost blocks that push other items aside
    - Smooth animations maintaining geometric integrity
    - Predictive drop zones
    - Visual feedback for all interactions
    """
    drag_started = pyqtSignal(int)
    drag_ended = pyqtSignal(int, int)
    items_reordered = pyqtSignal(list)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self._drag_item_index = -1
        self._drag_start_pos = QPoint()
        self._is_dragging = False
        self._ghost_widget = None
        self._drop_target_index = -1
        self._drop_position = QAbstractItemView.OnViewport
        self._animation_group = None
        self._item_animations = {}
        self.drop_indicator_color = QColor(100, 180, 255, 200)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.setSelectionMode(QAbstractItemView.SingleSelection)
        self.setCursor(Qt.OpenHandCursor)
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDropIndicatorShown(False)
        self.auto_scroll_timer = QTimer()
        self.auto_scroll_timer.timeout.connect(self._auto_scroll)
        self.auto_scroll_direction = 0
        self.drag_scroll_margin = 100
        self.scroll_speed = 20
        self._original_positions = {}
        self._undo_stack = []
        self._redo_stack = []
        
    def mousePressEvent(self, event):
        """Handle mouse press to start drag operation."""
        if event.button() == Qt.LeftButton:
            self._drag_start_pos = event.pos()
            index = self.indexAt(event.pos())
            if index.isValid():
                self._drag_item_index = index.row()
                self.setCursor(Qt.ClosedHandCursor)
                item = self.item(self._drag_item_index)
                if item:
                    widget = self.itemWidget(item)
                    if widget:
                        self._create_ghost_widget(widget, event.pos())
        super().mousePressEvent(event)
    
    def mouseMoveEvent(self, event):
        """Handle mouse move to initiate drag operation."""
        if not (event.buttons() & Qt.LeftButton):
            return
        if not self._is_dragging:
            manhattan_length = (event.pos() - self._drag_start_pos).manhattanLength()
            if manhattan_length > QApplication.startDragDistance():
                self._start_drag_operation(event.pos())
                return
        if self._is_dragging and self._ghost_widget:
            self._update_ghost_position(event.pos())
            self._update_drop_target(event.pos())
            self._handle_auto_scroll_during_drag(event.pos())
        super().mouseMoveEvent(event)
    
    def mouseReleaseEvent(self, event):
        """Handle mouse release to complete drag operation."""
        if event.button() == Qt.LeftButton and self._is_dragging:
            self._complete_drag_operation()
        self.setCursor(Qt.OpenHandCursor)
        self._cleanup_drag_state()
        super().mouseReleaseEvent(event)
    
    def _create_ghost_widget(self, source_widget, position):
        """Create a professional ghost widget for dragging."""
        if self._ghost_widget:
            self._ghost_widget.deleteLater()
        self._ghost_widget = GhostItemWidget(source_widget, self.viewport())
        self._ghost_widget.move(position - QPoint(source_widget.width() // 2, 
                                                 source_widget.height() // 2))
        self._ghost_widget.show()
        self._ghost_widget.animate_pickup()
    
    def _update_ghost_position(self, position):
        """Update ghost widget position with smooth following."""
        if not self._ghost_widget:
            return
        current_pos = self._ghost_widget.pos()
        target_pos = position - QPoint(self._ghost_widget.width() // 2,
                                      self._ghost_widget.height() // 2)
        new_pos = current_pos + (target_pos - current_pos) * 0.3
        self._ghost_widget.move(new_pos)
        viewport_rect = self.viewport().rect()
        center = viewport_rect.center()
        distance = math.sqrt((position.x() - center.x()) ** 2 + 
                           (position.y() - center.y()) ** 2)
        max_distance = math.sqrt(viewport_rect.width() ** 2 + 
                               viewport_rect.height() ** 2) / 2
        opacity = 0.7 - (distance / max_distance) * 0.2
        self._ghost_widget.ghost_opacity = max(0.5, min(0.9, opacity))
        self._ghost_widget.update()
    
    def _start_drag_operation(self, position):
        """Start the drag operation."""
        self._is_dragging = True
        if self._drag_item_index >= 0:
            item = self.item(self._drag_item_index)
            if item:
                widget = self.itemWidget(item)
                if widget:
                    widget.setVisible(False)
        self._save_original_positions()
        self.drag_started.emit(self._drag_item_index)
    
    def _complete_drag_operation(self):
        """Complete the drag operation with animation."""
        if not self._is_dragging or self._drag_item_index < 0:
            return
        if self._drop_target_index >= 0 and self._drop_target_index != self._drag_item_index:
            self._perform_animated_move(self._drag_item_index, self._drop_target_index)
            self.drag_ended.emit(self._drag_item_index, self._drop_target_index)
            self._add_to_undo_stack(self._drag_item_index, self._drop_target_index)
        else:
            self._return_to_original_position()
        self._cleanup_drag_state()
    
    def _perform_animated_move(self, from_index, to_index):
        """Perform animated move of items."""
        if self._animation_group:
            self._animation_group.stop()
            self._animation_group.deleteLater()
        self._animation_group = QParallelAnimationGroup()
        affected_indices = self._get_affected_indices(from_index, to_index)
        for idx in affected_indices:
            item = self.item(idx)
            if not item:
                continue
            widget = self.itemWidget(item)
            if not widget:
                continue
            target_pos = self._calculate_target_position(idx, from_index, to_index)
            anim = QPropertyAnimation(widget, b"pos")
            anim.setDuration(300)
            anim.setStartValue(widget.pos())
            anim.setEndValue(target_pos)
            anim.setEasingCurve(QEasingCurve.OutCubic)
            self._animation_group.addAnimation(anim)
            self._item_animations[idx] = anim
        self._animation_group.finished.connect(self._on_move_animation_finished)
        self._animation_group.start()
    
    def _get_affected_indices(self, from_index, to_index):
        """Get indices of items affected by the move."""
        affected = []
        if from_index < to_index:
            affected = list(range(from_index, to_index + 1))
        else:
            affected = list(range(to_index, from_index + 1))
        return affected
    
    def _calculate_target_position(self, idx, from_index, to_index):
        """Calculate target position for an item during move animation."""
        item = self.item(idx)
        if not item:
            return QPoint(0, 0)
        visual_rect = self.visualRect(item)
        if visual_rect.isNull():
            return QPoint(0, 0)
        if from_index < to_index:
            if idx == from_index:
                target_item = self.item(to_index)
                if target_item:
                    return self.visualRect(target_item).topLeft()
            elif from_index < idx <= to_index:
                prev_item = self.item(idx - 1)
                if prev_item:
                    return self.visualRect(prev_item).topLeft()
        else:
            if idx == from_index:
                target_item = self.item(to_index)
                if target_item:
                    return self.visualRect(target_item).topLeft()
            elif to_index <= idx < from_index:
                next_item = self.item(idx + 1)
                if next_item:
                    return self.visualRect(next_item).topLeft()
        return visual_rect.topLeft()
    
    def _on_move_animation_finished(self):
        """Handle completion of move animation."""
        if self._drag_item_index >= 0 and self._drop_target_index >= 0:
            item = self.takeItem(self._drag_item_index)
            if item:
                self.insertItem(self._drop_target_index, item)
                widget = self.itemWidget(item)
                if widget:
                    widget.setVisible(True)
                if hasattr(self.parent(), 'event_handler'):
                    self.parent().event_handler.update_button_states()
        self._item_animations.clear()
        if self._animation_group:
            self._animation_group.deleteLater()
            self._animation_group = None
        if self._drop_target_index >= 0:
            self.setCurrentRow(self._drop_target_index)
    
    def _return_to_original_position(self):
        """Animate items back to their original positions."""
        if not self._animation_group:
            self._animation_group = QParallelAnimationGroup()
        for idx, original_pos in self._original_positions.items():
            item = self.item(idx)
            if not item:
                continue
            widget = self.itemWidget(item)
            if not widget:
                continue
            anim = QPropertyAnimation(widget, b"pos")
            anim.setDuration(200)
            anim.setStartValue(widget.pos())
            anim.setEndValue(original_pos)
            anim.setEasingCurve(QEasingCurve.OutCubic)
            self._animation_group.addAnimation(anim)
        self._animation_group.start()
    
    def _save_original_positions(self):
        """Save original positions of all items."""
        self._original_positions.clear()
        for i in range(self.count()):
            item = self.item(i)
            if item:
                widget = self.itemWidget(item)
                if widget:
                    self._original_positions[i] = widget.pos()
    
    def _update_drop_target(self, position):
        """Update the drop target based on current mouse position."""
        index = self.indexAt(position)
        if not index.isValid():
            self._drop_target_index = -1
            self._drop_position = QAbstractItemView.OnViewport
            if self.viewport():
                self.viewport().update()
            return
        rect = self.visualRect(index)
        if rect.isNull():
            self._drop_target_index = -1
            self._drop_position = QAbstractItemView.OnViewport
            return
        if position.y() < rect.top() + rect.height() * 0.3:
            self._drop_position = QAbstractItemView.AboveItem
            self._drop_target_index = index.row()
        elif position.y() > rect.bottom() - rect.height() * 0.3:
            self._drop_position = QAbstractItemView.BelowItem
            self._drop_target_index = index.row() + 1
        else:
            self._drop_position = QAbstractItemView.OnItem
            self._drop_target_index = index.row()
        if self.viewport():
            self.viewport().update()
    
    def _handle_auto_scroll_during_drag(self, pos):
        """Handle auto-scrolling when dragging near edges."""
        viewport = self.viewport()
        if not viewport:
            return
        viewport_rect = viewport.rect()
        viewport_height = viewport_rect.height()
        mouse_y = pos.y()
        if mouse_y < self.drag_scroll_margin:
            self.auto_scroll_direction = -1
            scroll_factor = 1.0 - (mouse_y / self.drag_scroll_margin)
            self.scroll_speed = int(10 + (scroll_factor * 30))
            if not self.auto_scroll_timer.isActive():
                self.auto_scroll_timer.start(30)
        elif mouse_y > viewport_height - self.drag_scroll_margin:
            self.auto_scroll_direction = 1
            distance_from_edge = viewport_height - mouse_y
            scroll_factor = 1.0 - (distance_from_edge / self.drag_scroll_margin)
            self.scroll_speed = int(10 + (scroll_factor * 30))
            if not self.auto_scroll_timer.isActive():
                self.auto_scroll_timer.start(30)
        else:
            self.auto_scroll_direction = 0
            self.auto_scroll_timer.stop()
