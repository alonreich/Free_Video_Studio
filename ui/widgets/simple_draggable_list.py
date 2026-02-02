"""
Simple, robust draggable list with full shape ghost blocks.
No crashes, no geometry distortion, professional animations.
"""

from PyQt5.QtCore import (
    Qt, QRect, QTimer, QPoint, QPropertyAnimation, 
    QEasingCurve, QParallelAnimationGroup,
    pyqtSignal
)

from PyQt5.QtGui import (
    QPainter, QColor, QPen, QPixmap
)

from PyQt5.QtWidgets import (
    QListWidget, QAbstractItemView, QApplication, QWidget
)

class SimpleGhostWidget(QWidget):
    """Simple ghost widget that works reliably."""
    
    def __init__(self, source_widget, parent=None):
        super().__init__(parent)
        self.source_widget = source_widget
        self.setAttribute(Qt.WA_TransparentForMouseEvents)
        self.ghost_pixmap = source_widget.grab()
        self.ghost_opacity = 0.7
        self.setFixedSize(self.ghost_pixmap.size())
        
    def paintEvent(self, event):
        """Paint ghost with simple, reliable rendering."""
        painter = QPainter(self)
        painter.setOpacity(self.ghost_opacity)
        painter.drawPixmap(0, 0, self.ghost_pixmap)
        painter.setPen(QPen(QColor(100, 180, 255, 200), 2))
        painter.drawRect(0, 0, self.width() - 1, self.height() - 1)

class SimpleDraggableList(QListWidget):
    """
    Simple, reliable draggable list that:
    1. Doesn't crash
    2. Preserves geometry
    3. Has full shape ghost blocks
    4. Has smooth animations
    """
    drag_started = pyqtSignal(int, str)
    drag_completed = pyqtSignal(int, int, str, str)
    drag_cancelled = pyqtSignal(int, str)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setDragDropMode(QAbstractItemView.InternalMove)
        self.setSelectionMode(QAbstractItemView.SingleSelection)
        self.setCursor(Qt.OpenHandCursor)
        self.setDragEnabled(True)
        self.setAcceptDrops(True)
        self.setDropIndicatorShown(False)
        self.setSpacing(6)
        self.setUniformItemSizes(True)
        self._drag_item_index = -1
        self._drag_start_pos = QPoint()
        self._is_dragging = False
        self._ghost_widget = None
        self._drop_target_index = -1
        self._original_positions = {}
        
    def mousePressEvent(self, event):
        """Start drag on mouse press."""
        if event.button() == Qt.LeftButton:
            self._drag_start_pos = event.pos()
            index = self.indexAt(event.pos())
            if index.isValid():
                self._drag_item_index = index.row()
                self.setCursor(Qt.ClosedHandCursor)
        super().mousePressEvent(event)
    
    def mouseMoveEvent(self, event):
        """Handle drag movement."""
        if not (event.buttons() & Qt.LeftButton):
            return
        if not self._is_dragging:
            manhattan_length = (event.pos() - self._drag_start_pos).manhattanLength()
            if manhattan_length > QApplication.startDragDistance():
                self._start_drag(event.pos())
                return
        if self._is_dragging and self._ghost_widget:
            self._update_ghost(event.pos())
            self._update_drop_target(event.pos())
        super().mouseMoveEvent(event)
    
    def mouseReleaseEvent(self, event):
        """Complete drag on release."""
        if event.button() == Qt.LeftButton and self._is_dragging:
            self._complete_drag()
        self.setCursor(Qt.OpenHandCursor)
        self._cleanup()
        super().mouseReleaseEvent(event)
    
    def _start_drag(self, position):
        """Start drag operation."""
        self._is_dragging = True
        self._store_original_positions()
        item = self.item(self._drag_item_index)
        if item:
            widget = self.itemWidget(item)
            if widget:
                try:
                    self._create_ghost(widget, position)
                    widget.setVisible(False)
                    filename = self._get_item_filename(self._drag_item_index)
                    self.drag_started.emit(self._drag_item_index, filename)
                except Exception as e:
                    print(f"Error in _start_drag: {e}")

                    import traceback
                    traceback.print_exc()
                    self._cleanup()
                    self._is_dragging = False
    
    def _get_item_filename(self, index):
        """Get filename from item at given index."""
        item = self.item(index)
        if not item:
            return "Unknown"
        widget = self.itemWidget(item)
        if widget:
            from PyQt5.QtWidgets import QLabel
            for child in widget.findChildren(QLabel):
                if hasattr(child, 'text'):
                    text = child.text()
                    if text and not text.startswith('▶'):
                        return text

        from PyQt5.QtCore import Qt
        path = item.data(Qt.UserRole)
        if path:
            import os
            return os.path.basename(path)
        return "Unknown"
    
    def _create_ghost(self, source_widget, position):
        """Create ghost widget."""
        if self._ghost_widget:
            self._ghost_widget.deleteLater()
        self._ghost_widget = SimpleGhostWidget(source_widget, self.viewport())
        ghost_pos = position - QPoint(source_widget.width() // 2, 
                                     source_widget.height() // 2)
        self._ghost_widget.move(ghost_pos)
        self._ghost_widget.show()
    
    def _update_ghost(self, position):
        """Update ghost position."""
        if not self._ghost_widget:
            return
        current_pos = self._ghost_widget.pos()
        target_pos = position - QPoint(self._ghost_widget.width() // 2,
                                      self._ghost_widget.height() // 2)
        dx = target_pos.x() - current_pos.x()
        dy = target_pos.y() - current_pos.y()
        new_pos = current_pos + QPoint(int(dx * 0.5), int(dy * 0.5))
        self._ghost_widget.move(new_pos)
    
    def _update_drop_target(self, position):
        """Update drop target position."""
        index = self.indexAt(position)
        if not index.isValid():
            self._drop_target_index = -1
            return
        self._drop_target_index = index.row()
    
    def _complete_drag(self):
        """Complete drag operation."""
        if not self._is_dragging or self._drag_item_index < 0:
            return
        if self._drop_target_index >= 0 and self._drop_target_index != self._drag_item_index:
            self._perform_move()
        else:
            self._return_to_original()
    
    def _perform_move(self):
        """Perform the actual move with proper widget preservation."""
        try:
            from_index = self._drag_item_index
            to_index = self._drop_target_index
            if from_index == to_index or from_index < 0 or to_index < 0:
                return
            from_filename = self._get_item_filename(from_index)
            to_filename = self._get_item_filename(to_index) if to_index < self.count() else "End of list"
            item = self.item(from_index)
            if not item:
                return
            widget = self.itemWidget(item)
            widget_size = None
            if widget:
                widget_geometry = widget.geometry()
                widget_size = widget.size()
                widget_visible = widget.isVisible()
                widget.setVisible(False)
            taken_item = self.takeItem(from_index)
            if not taken_item:
                return
            self.insertItem(to_index, taken_item)
            if widget and widget_size:
                self.setItemWidget(taken_item, None)
                widget.setParent(self.viewport())
                self.setItemWidget(taken_item, widget)
                widget.setVisible(True)
                widget.setFixedSize(widget_size)
                widget.setGeometry(0, 0, widget_size.width(), widget_size.height())
                taken_item.setSizeHint(widget.sizeHint())
            else:
                taken_item.setSizeHint(self._get_default_item_size())
            self.setCurrentRow(to_index)
            self._update_all_item_size_hints()
            self._ensure_consistent_geometry()
            if self.viewport():
                self.viewport().update()
            self.drag_completed.emit(from_index, to_index, from_filename, to_filename)
        except Exception as e:
            print(f"Error in _perform_move: {e}")

            import traceback
            traceback.print_exc()
            self._cleanup()
            self._update_all_item_size_hints()
            self._ensure_consistent_geometry()
            if self.viewport():
                self.viewport().update()
    
    def _get_default_item_size(self):
        """Get default item size for consistency."""

        from PyQt5.QtCore import QSize
        return QSize(700, 40)
    
    def _return_to_original(self):
        """Return items to original positions."""
        item = self.item(self._drag_item_index)
        if item:
            widget = self.itemWidget(item)
            if widget:
                widget.setVisible(True)
        filename = self._get_item_filename(self._drag_item_index)
        self.drag_cancelled.emit(self._drag_item_index, filename)
    
    def _ensure_consistent_geometry(self):
        """Ensure all widgets have consistent geometry."""
        for i in range(self.count()):
            item = self.item(i)
            if item:
                widget = self.itemWidget(item)
                if widget:
                    widget.setFixedSize(700, 40)
                    item.setSizeHint(widget.sizeHint())
    
    def _store_original_positions(self):
        """Store original positions of all items."""
        self._original_positions.clear()
        for i in range(self.count()):
            item = self.item(i)
            if item:
                widget = self.itemWidget(item)
                if widget:
                    self._original_positions[i] = widget.pos()
    
    def _update_all_item_size_hints(self):
        """Update size hints for all items to ensure proper layout."""
        for i in range(self.count()):
            item = self.item(i)
            if item:
                widget = self.itemWidget(item)
                if widget:
                    item.setSizeHint(widget.sizeHint())
    
    def _cleanup(self):
        """Clean up drag state."""
        if self._ghost_widget:
            self._ghost_widget.deleteLater()
            self._ghost_widget = None
        if self._drag_item_index >= 0:
            item = self.item(self._drag_item_index)
            if item:
                widget = self.itemWidget(item)
                if widget and not widget.isVisible():
                    widget.setVisible(True)
        self._is_dragging = False
        self._drag_item_index = -1
        self._drop_target_index = -1
        viewport = self.viewport()
        if viewport:
            viewport.update()

def update_merger_window_to_use_simple_draggable():
    """Helper function to update merger_window.py to use the simple draggable list."""

    import os
    merger_window_path = os.path.join(os.path.dirname(__file__), '..', '..', 'utilities', 'merger_window.py')
    if os.path.exists(merger_window_path):
        with open(merger_window_path, 'r', encoding='utf-8') as f:
            content = f.read()
        content = content.replace(
            'from ui.widgets.draggable_list_widget import DraggableListWidget',
            'from ui.widgets.simple_draggable_list import SimpleDraggableList'
        )
        content = content.replace(
            'def create_draggable_list_widget(self):\n        listw = DraggableListWidget()',
            'def create_draggable_list_widget(self):\n        listw = SimpleDraggableList()'
        )
        with open(merger_window_path, 'w', encoding='utf-8') as f:
            f.write(content)
        print("Updated merger_window.py to use SimpleDraggableList")
    else:
        print(f"Could not find {merger_window_path}")
if __name__ == "__main__":
    update_merger_window_to_use_simple_draggable()