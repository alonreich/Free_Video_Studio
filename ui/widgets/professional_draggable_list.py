"""
Professional Draggable List with proper ghost blocks, smooth animations,
and geometric integrity preservation.
"""

from PyQt5.QtCore import (
    Qt, QRect, QTimer, QPoint, QPropertyAnimation, 
    QEasingCurve, QParallelAnimationGroup, QSequentialAnimationGroup,
    pyqtSignal, QSize, QEvent, QMimeData
)

from PyQt5.QtGui import (
    QPainter, QColor, QLinearGradient, QDrag, QPixmap, QBrush, 
    QPen, QPainterPath, QRegion, QTransform
)

from PyQt5.QtWidgets import (
    QListWidget, QAbstractItemView, QApplication, QStyle, 
    QStyleOption, QListView, QWidget, QVBoxLayout, QLabel
)

import math

class ProfessionalGhostWidget(QWidget):
    """Professional ghost widget with full shape and smooth animations."""
    
    def __init__(self, source_widget, parent=None):
        super().__init__(parent)
        self.source_widget = source_widget
        self.setAttribute(Qt.WA_TransparentForMouseEvents)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.ghost_pixmap = source_widget.grab()
        self.ghost_opacity = 0.7
        self.elevation = 10
        self.scale_factor = 1.0
        self.shadow_blur = 15
        self.setFixedSize(self.ghost_pixmap.size())
        self.is_animating = False
        
    def paintEvent(self, event):
        """Paint professional ghost with shadow and highlight."""
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        painter.setRenderHint(QPainter.SmoothPixmapTransform)
        shadow_color = QColor(0, 100, 200, 60)
        shadow_rect = QRect(0, 0, self.width(), self.height())
        shadow_rect.adjust(self.elevation, self.elevation, 
                          self.elevation, self.elevation)
        for i in range(3):
            alpha = 20 - i * 5
            offset = self.elevation - i
            shadow_rect_adjusted = QRect(offset, offset, 
                                        self.width(), self.height())
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor(0, 100, 200, alpha))
            painter.drawRoundedRect(shadow_rect_adjusted, 6, 6)
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
        path = QPainterPath()
        path.addRoundedRect(0, 0, self.width(), self.height(), 6, 6)
        painter.setClipPath(path)
        painter.drawPixmap(0, 0, self.ghost_pixmap)
        painter.restore()
        highlight_color = QColor(100, 180, 255, 180)
        painter.setPen(QPen(highlight_color, 2, Qt.SolidLine))
        painter.setBrush(Qt.NoBrush)
        painter.drawRoundedRect(0, 0, self.width() - 1, self.height() - 1, 6, 6)
        inner_glow = QColor(200, 230, 255, 80)
        painter.setPen(QPen(inner_glow, 1, Qt.SolidLine))
        painter.drawRoundedRect(2, 2, self.width() - 5, self.height() - 5, 4, 4)
        
    def animate_pickup(self):
        """Animate ghost when picked up - scale up with bounce."""
        self.scale_anim = QPropertyAnimation(self, b"scale_factor")
        self.scale_anim.setDuration(200)
        self.scale_anim.setStartValue(1.0)
        self.scale_anim.setEndValue(1.08)
        self.scale_anim.setEasingCurve(QEasingCurve.OutBack)
        self.opacity_anim = QPropertyAnimation(self, b"ghost_opacity")
        self.opacity_anim.setDuration(150)
        self.opacity_anim.setStartValue(0.7)
        self.opacity_anim.setEndValue(0.85)
        self.anim_group = QParallelAnimationGroup()
        self.anim_group.addAnimation(self.scale_anim)
        self.anim_group.addAnimation(self.opacity_anim)
        self.anim_group.start()
        
    def animate_drop(self):
        """Animate ghost when dropped - scale down smoothly."""
        self.scale_anim = QPropertyAnimation(self, b"scale_factor")
        self.scale_anim.setDuration(250)
        self.scale_anim.setStartValue(self.scale_factor)
        self.scale_anim.setEndValue(1.0)
        self.scale_anim.setEasingCurve(QEasingCurve.InOutQuad)
        self.opacity_anim = QPropertyAnimation(self, b"ghost_opacity")
        self.opacity_anim.setDuration(200)
        self.opacity_anim.setStartValue(self.ghost_opacity)
        self.opacity_anim.setEndValue(0.3)
        self.anim_group = QParallelAnimationGroup()
        self.anim_group.addAnimation(self.scale_anim)
        self.anim_group.addAnimation(self.opacity_anim)
        self.anim_group.finished.connect(self.deleteLater)
        self.anim_group.start()

class ProfessionalDraggableList(QListWidget):
    """
    Professional draggable list with:
    - Full shape ghost blocks
    - Smooth animations that preserve geometry
    - No crashes or visual distortion
    - Industry-standard drag behavior
    """
    drag_started = pyqtSignal(int)
    drag_ended = pyqtSignal(int, int)
    items_reordered = pyqtSignal(list)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.setSelectionMode(QAbstractItemView.SingleSelection)
        self.setCursor(Qt.OpenHandCursor)
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDropIndicatorShown(False)
        self.setSpacing(8)
        self.setAlternatingRowColors(False)
        self._drag_item_index = -1
        self._drag_start_pos = QPoint()
        self._is_dragging = False
        self._ghost_widget = None
        self._drop_target_index = -1
        self._item_animations = {}
        self._animation_group = None
        self.drop_indicator_color = QColor(100, 180, 255, 200)
        self.push_animation_color = QColor(255, 200, 100, 60)
        self.auto_scroll_timer = QTimer()
        self.auto_scroll_timer.timeout.connect(self._auto_scroll)
        self.auto_scroll_direction = 0
        self.drag_scroll_margin = 80
        self.scroll_speed = 15
        self._original_positions = {}
        
    def mousePressEvent(self, event):
        """Start drag operation on mouse press."""
        if event.button() == Qt.LeftButton:
            self._drag_start_pos = event.pos()
            index = self.indexAt(event.pos())
            if index.isValid():
                self._drag_item_index = index.row()
                self.setCursor(Qt.ClosedHandCursor)
                self._store_original_positions()
        super().mousePressEvent(event)
    
    def mouseMoveEvent(self, event):
        """Handle mouse movement for drag operation."""
        if not (event.buttons() & Qt.LeftButton):
            return
        if not self._is_dragging:
            manhattan_length = (event.pos() - self._drag_start_pos).manhattanLength()
            if manhattan_length > QApplication.startDragDistance():
                self._start_drag_operation(event.pos())
                return
        if self._is_dragging:
            self._update_ghost_position(event.pos())
            self._update_drop_target(event.pos())
            self._animate_items_for_space(event.pos())
            self._handle_auto_scroll(event.pos())
        super().mouseMoveEvent(event)
    
    def mouseReleaseEvent(self, event):
        """Complete drag operation on mouse release."""
        if event.button() == Qt.LeftButton and self._is_dragging:
            self._complete_drag_operation()
        self.setCursor(Qt.OpenHandCursor)
        self._cleanup_drag_state()
        super().mouseReleaseEvent(event)
    
    def _start_drag_operation(self, position):
        """Start the drag operation with ghost widget."""
        self._is_dragging = True
        item = self.item(self._drag_item_index)
        if item:
            widget = self.itemWidget(item)
            if widget:
                self._create_ghost_widget(widget, position)
                widget.setVisible(False)
        self.drag_started.emit(self._drag_item_index)
    
    def _create_ghost_widget(self, source_widget, position):
        """Create a professional ghost widget."""
        if self._ghost_widget:
            self._ghost_widget.deleteLater()
        self._ghost_widget = ProfessionalGhostWidget(source_widget, self.viewport())
        ghost_pos = position - QPoint(source_widget.width() // 2, 
                                     source_widget.height() // 2)
        self._ghost_widget.move(ghost_pos)
        self._ghost_widget.show()
        self._ghost_widget.animate_pickup()
    
    def _update_ghost_position(self, position):
        """Update ghost position with smooth following."""
        if not self._ghost_widget:
            return
        current_pos = self._ghost_widget.pos()
        target_pos = position - QPoint(self._ghost_widget.width() // 2,
                                      self._ghost_widget.height() // 2)
        dx = target_pos.x() - current_pos.x()
        dy = target_pos.y() - current_pos.y()
        new_pos = current_pos + QPoint(int(dx * 0.4), int(dy * 0.4))
        self._ghost_widget.move(new_pos)
        viewport = self.viewport()
        if viewport:
            viewport_rect = viewport.rect()
            center_y = viewport_rect.center().y()
            distance = abs(position.y() - center_y)
            max_distance = viewport_rect.height() / 2
            opacity = 0.85 - (distance / max_distance) * 0.3
            self._ghost_widget.ghost_opacity = max(0.5, min(0.9, opacity))
            self._ghost_widget.update()
    
    def _update_drop_target(self, position):
        """Update the drop target based on mouse position."""
        index = self.indexAt(position)
        if not index.isValid():
            self._drop_target_index = -1
            return
        rect = self.visualRect(index)
        if rect.isNull():
            self._drop_target_index = -1
            return
        item_center = rect.center().y()
        if position.y() < item_center - rect.height() * 0.25:
            self._drop_target_index = index.row()
        elif position.y() > item_center + rect.height() * 0.25:
            self._drop_target_index = index.row() + 1
        else:
            self._drop_target_index = index.row()
        self.viewport().update()
    
    def _animate_items_for_space(self, position):
        """Animate items to make space for the dragged item."""
        if self._drop_target_index < 0 or self._drag_item_index < 0:
            return
        if self._drag_item_index < self._drop_target_index:
            for idx in range(self._drag_item_index + 1, self._drop_target_index + 1):
                self._animate_item_push(idx, -1)
        elif self._drag_item_index > self._drop_target_index:
            for idx in range(self._drop_target_index, self._drag_item_index):
                self._animate_item_push(idx, 1)
    
    def _animate_item_push(self, idx, direction):
        """Animate an item to push it aside."""
        item = self.item(idx)
        if not item:
            return
        widget = self.itemWidget(item)
        if not widget:
            return
        visual_rect = self.visualRect(item)
        if visual_rect.isNull():
            return
        push_amount = visual_rect.height() * 0.2 * direction
        if idx not in self._item_animations:
            anim = QPropertyAnimation(widget, b"pos")
            anim.setDuration(150)
            anim.setEasingCurve(QEasingCurve.OutCubic)
            self._item_animations[idx] = anim
        anim = self._item_animations[idx]
        target_pos = widget.pos() + QPoint(0, int(push_amount))
        if target_pos != widget.pos():
            anim.setStartValue(widget.pos())
            anim.setEndValue(target_pos)
            anim.start()
    
    def _complete_drag_operation(self):
        """Complete the drag operation with animation."""
        if not self._is_dragging or self._drag_item_index < 0:
            return
        if self._drop_target_index >= 0 and self._drop_target_index != self._drag_item_index:
            self._perform_animated_move()
        else:
            self._return_to_original_positions()
        self._cleanup_drag_state()
    
    def _perform_animated_move(self):
        """Perform animated move of items."""
        if self._animation_group:
            self._animation_group.stop()
            self._animation_group.deleteLater()
        self._animation_group = QParallelAnimationGroup()
        from_index = self._drag_item_index
        to_index = self._drop_target_index
        if from_index < to_index:
            affected = list(range(from_index, to_index + 1))
        else:
            affected = list(range(to_index, from_index + 1))
        for idx in affected:
            item = self.item(idx)
            if not item:
                continue
            widget = self.itemWidget(item)
            if not widget:
                continue
            if idx == from_index:
                target_item = self.item(to_index)
                if target_item:
                    target_widget = self.itemWidget(target_item)
                    if target_widget:
                        target_pos = target_widget.pos()
                    else:
                        continue
                else:
                    continue
            else:
                target_pos = self._original_positions.get(idx, widget.pos())
            anim = QPropertyAnimation(widget, b"pos")
            anim.setDuration(300)
            anim.setStartValue(widget.pos())
            anim.setEndValue(target_pos)
            anim.setEasingCurve(QEasingCurve.OutCubic)
            self._animation_group.addAnimation(anim)
        self._animation_group.finished.connect(self._on_move_complete)
        self._animation_group.start()
    
    def _on_move_complete(self):
        """Handle completion of move animation."""
        if self._drag_item_index >= 0 and self._drop_target_index >= 0:
            item = self.takeItem(self._drag_item_index)
            if item:
                self.insertItem(self._drop_target_index, item)
                widget = self.itemWidget(item)
                if widget:
                    widget.setVisible(True)
        self._item_animations.clear()
        if self._animation_group:
            self._animation_group.deleteLater()
            self._animation_group = None
        if self._drop_target_index >= 0:
            self.setCurrentRow(self._drop_target_index)
        self.d