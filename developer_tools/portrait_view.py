from PyQt5.QtWidgets import (
    QApplication, QWidget, QPushButton, QHBoxLayout, QVBoxLayout,
    QLabel, QGraphicsScene, QGraphicsView, QGraphicsPixmapItem,
    QGraphicsItem, QComboBox, QMessageBox, QFrame, QGraphicsRectItem, QGraphicsSimpleTextItem,
    QDialog
)

from PyQt5.QtCore import Qt, QTimer, QRectF, pyqtSignal, QPointF, QPropertyAnimation, QEasingCurve
from PyQt5.QtGui import QPainter, QPen, QColor, QFont, QBrush, QPixmap, QCursor
from graphics_items import ResizablePixmapItem
from config import HUD_ELEMENT_MAPPINGS, UI_COLORS, UI_LAYOUT, UI_BEHAVIOR

class PortraitView(QGraphicsView):
    def __init__(self, scene, parent=None):
        super().__init__(scene, parent)
        self.zoom = 1.0
        self.min_zoom = 0.2
        self.max_zoom = 4.0
        self.user_zoomed = False
        self._middle_dragging = False
        self.setRenderHint(QPainter.Antialiasing)
        self.setRenderHint(QPainter.SmoothPixmapTransform)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOn)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOn)
        self.setTransformationAnchor(QGraphicsView.AnchorUnderMouse)
        self.setResizeAnchor(QGraphicsView.AnchorUnderMouse)

    def fit_to_scene(self):
        if not self.scene(): return
        view_rect = self.viewport().rect()
        scene_rect = self.scene().sceneRect()
        if scene_rect.width() <= 0 or scene_rect.height() <= 0: return
        self.resetTransform()
        scale = min(view_rect.width() / scene_rect.width(), view_rect.height() / scene_rect.height())
        scale = min(scale, 1.0)
        self.zoom = scale
        self.scale(scale, scale)
        self.user_zoomed = False

    def wheelEvent(self, event):
        angle = event.angleDelta().y()
        if angle == 0: return
        zoom_factor = 1.1 if angle > 0 else 1 / 1.1
        new_zoom = max(self.min_zoom, min(self.max_zoom, self.zoom * zoom_factor))
        if new_zoom == self.zoom: return
        factor = new_zoom / self.zoom
        self.zoom = new_zoom
        self.scale(factor, factor)
        self.user_zoomed = True
        self._clamp_scroll()
        
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
            self.setDragMode(QGraphicsView.ScrollHandDrag)
            self.setCursor(Qt.ClosedHandCursor)
            return super().mousePressEvent(event)
        return super().mousePressEvent(event)

    def mouseReleaseEvent(self, event):
        if self._middle_dragging and event.button() == Qt.MiddleButton:
            self._middle_dragging = False
            self.setDragMode(QGraphicsView.NoDrag)
            self.setCursor(Qt.ArrowCursor)
            self._clamp_scroll()
        return super().mouseReleaseEvent(event)

    def keyPressEvent(self, event):
        if event.modifiers() == Qt.ControlModifier:
            if event.key() == Qt.Key_Z:
                self.parent().undo()
                event.accept()
                return
            elif event.key() == Qt.Key_Y:
                self.parent().redo()
                event.accept()
                return
        selected_items = self.scene().selectedItems()
        if selected_items:
            item = selected_items[0]
            if event.modifiers() == Qt.ShiftModifier:
                if event.key() == Qt.Key_Up:
                    item.setZValue(item.zValue() + 1)
                    event.accept()
                    return
                elif event.key() == Qt.Key_Down:
                    item.setZValue(item.zValue() - 1)
                    event.accept()
                    return
            delta = 1
            key = event.key()
            if key == Qt.Key_Up: item.moveBy(0, -delta)
            elif key == Qt.Key_Down: item.moveBy(0, delta)
            elif key == Qt.Key_Left: item.moveBy(-delta, 0)
            elif key == Qt.Key_Right: item.moveBy(delta, 0)
            else:
                super().keyPressEvent(event)
                return
            if hasattr(self.parent(), 'on_item_modified'):
                self.parent().on_item_modified(item)
            event.accept()
        else:
            super().keyPressEvent(event)
