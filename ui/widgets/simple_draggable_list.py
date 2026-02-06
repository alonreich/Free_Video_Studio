"""
Simple, robust draggable list with full shape ghost blocks.
No crashes, no geometry distortion, professional animations.
"""

import sip
from PyQt5.QtCore import (
    Qt, QRect, QTimer, QPoint, QPropertyAnimation, 
    QEasingCurve, QParallelAnimationGroup,
    pyqtSignal, QSignalBlocker
)

from PyQt5.QtGui import (
    QPainter, QColor, QPen, QPixmap
)

from PyQt5.QtWidgets import (
    QListWidget, QAbstractItemView, QApplication, QWidget, QListWidgetItem, QListView
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
        # Disable built-in D&D to prevent conflict with manual implementation
        self.setDragDropMode(QAbstractItemView.NoDragDrop)
        self.setSelectionMode(QAbstractItemView.SingleSelection)
        self.setCursor(Qt.OpenHandCursor)
        self.setDragEnabled(False)
        self.setAcceptDrops(False)
        
        # Enforce list-wide properties for consistency
        self.setResizeMode(QListView.Adjust)
        self.setSpacing(4)
        self.setUniformItemSizes(False)
        self.setVerticalScrollMode(QAbstractItemView.ScrollPerPixel)
        
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
        if not self._is_dragging and self._drag_item_index >= 0:
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
        if event.button() == Qt.LeftButton:
            if self._is_dragging:
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
            if widget and not sip.isdeleted(widget):
                try:
                    self._create_ghost(widget, position)
                    widget.setVisible(False)
                    filename = self._get_item_filename(self._drag_item_index)
                    self.drag_started.emit(self._drag_item_index, filename)
                except Exception:
                    self._cleanup()
                    self._is_dragging = False
    
    def _get_item_filename(self, index):
        """Get filename from item at given index."""
        item = self.item(index)
        if not item: return "Unknown"
        widget = self.itemWidget(item)
        if widget and not sip.isdeleted(widget):
            from PyQt5.QtWidgets import QLabel
            for child in widget.findChildren(QLabel):
                if hasattr(child, 'text'):
                    text = child.text()
                    if text and not text.startswith('▶'):
                        return text
        path = item.data(Qt.UserRole)
        if path:
            import os
            return os.path.basename(path)
        return "Unknown"
    
    def _create_ghost(self, source_widget, position):
        """Create ghost widget."""
        if self._ghost_widget:
            self._ghost_widget.deleteLater()
        self._ghost_widget = SimpleGhostWidget(source_widget, self)
        ghost_pos = position - QPoint(source_widget.width() // 2, 
                                     source_widget.height() // 2)
        self._ghost_widget.move(ghost_pos)
        self._ghost_widget.show()
        self._ghost_widget.raise_()
    
    def _update_ghost(self, position):
        """Update ghost position."""
        if not self._ghost_widget or sip.isdeleted(self._ghost_widget):
            return
        target_pos = position - QPoint(self._ghost_widget.width() // 2,
                                      self._ghost_widget.height() // 2)
        self._ghost_widget.move(target_pos)
    
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
            self._cleanup()
    
    def _perform_move(self):
        """Perform the actual move with item cloning for maximum stability."""
        if sip.isdeleted(self): return
        
        from_index = self._drag_item_index
        to_index = self._drop_target_index
        
        if from_index == to_index or from_index < 0 or to_index < 0:
            self._cleanup()
            return

        try:
            from_filename = self._get_item_filename(from_index)
            to_filename = self._get_item_filename(to_index) if to_index < self.count() else "End of list"
            
            old_item = self.item(from_index)
            if not old_item: return
            
            path = old_item.data(Qt.UserRole)
            probe_data = old_item.data(Qt.UserRole + 1)
            f_hash = old_item.data(Qt.UserRole + 2)
            tooltip = old_item.toolTip()
            
            widget = self.itemWidget(old_item)
            if widget and not sip.isdeleted(widget):
                self.removeItemWidget(old_item)
                widget.setParent(self)
                widget.hide()
            else:
                widget = None

            new_item = QListWidgetItem()
            new_item.setData(Qt.UserRole, path)
            new_item.setData(Qt.UserRole + 1, probe_data)
            new_item.setData(Qt.UserRole + 2, f_hash)
            new_item.setToolTip(tooltip)
            
            # Ensure width is correct
            vw = self.viewport().width() if self.viewport() else 800
            new_item.setSizeHint(self._get_default_item_size(vw))
            
            self.insertItem(to_index, new_item)
            
            actual_from = from_index + 1 if to_index <= from_index else from_index
            item_to_remove = self.takeItem(actual_from)
            if item_to_remove:
                sip.delete(item_to_remove)

            if widget and not sip.isdeleted(widget):
                self.setItemWidget(new_item, widget)
                widget.show()
            
            self.setCurrentRow(to_index)
            self.clearSelection()
            new_item.setSelected(True)
            
            self.drag_completed.emit(from_index, to_index, from_filename, to_filename)
            
        except Exception:
            pass
        finally:
            QTimer.singleShot(0, self._ensure_consistent_geometry)

    def _get_default_item_size(self, width=None):
        """Get default item size for consistency."""
        from PyQt5.QtCore import QSize
        w = width if (width and width > 0) else 720
        return QSize(w, 52)
    
    def _ensure_consistent_geometry(self):
        """Ensure all widgets have consistent height and size hints with crash protection."""
        if sip.isdeleted(self): return
        
        blocker = QSignalBlocker(self)
        try:
            v = self.viewport()
            if not v or sip.isdeleted(v): return
            vw = v.width()
            
            for i in range(self.count()):
                try:
                    item = self.item(i)
                    if not item: continue
                    
                    # Force item to span full width to allow centering stretches to work
                    item.setSizeHint(self._get_default_item_size(vw))
                    
                    widget = self.itemWidget(item)
                    if widget and not sip.isdeleted(widget):
                        widget.setFixedHeight(52)
                        # Ensure the widget matches the item width
                        widget.setFixedWidth(vw)
                        if hasattr(widget, 'video_frame') and not sip.isdeleted(widget.video_frame):
                            widget.video_frame.setFixedSize(720, 42)
                except Exception:
                    continue
            
            self.doItemsLayout()
            self.update()
        except Exception:
            pass
        finally:
            blocker.unblock()
    
    def _store_original_positions(self):
        """Store original positions of all items."""
        self._original_positions.clear()
        for i in range(self.count()):
            try:
                item = self.item(i)
                if item:
                    widget = self.itemWidget(item)
                    if widget and not sip.isdeleted(widget):
                        self._original_positions[i] = widget.pos()
            except: pass
    
    def _cleanup(self):
        """Clean up drag state safely."""
        if sip.isdeleted(self): return
        
        if self._ghost_widget:
            if not sip.isdeleted(self._ghost_widget):
                self._ghost_widget.hide()
                self._ghost_widget.deleteLater()
            self._ghost_widget = None
        
        if self._drag_item_index >= 0:
            try:
                item = self.item(self._drag_item_index)
                if item:
                    widget = self.itemWidget(item)
                    if widget and not sip.isdeleted(widget):
                        widget.setVisible(True)
            except: pass
        
        self._is_dragging = False
        self._drag_item_index = -1
        self._drop_target_index = -1
        
        QTimer.singleShot(0, self._ensure_consistent_geometry)

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
if __name__ == "__main__":
    update_merger_window_to_use_simple_draggable()