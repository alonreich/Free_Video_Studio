import json
import os
from PyQt5.QtWidgets import (
    QApplication, QWidget, QPushButton, QHBoxLayout, QVBoxLayout,
    QLabel, QGraphicsScene, QGraphicsView, QGraphicsPixmapItem,
    QGraphicsItem, QComboBox, QMessageBox, QFrame
)

from PyQt5.QtCore import Qt, QTimer, QRectF, pyqtSignal, QPointF
from PyQt5.QtGui import QPainter, QPen, QColor, QFont, QBrush, QPixmap, QCursor
from utils import PersistentWindowMixin
from graphics_items import ResizablePixmapItem
from config import PORTRAIT_WINDOW_STYLESHEET, HUD_ELEMENT_MAPPINGS
from state_manager import get_state_manager

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
        if not self.scene():
            return
        view_rect = self.viewport().rect()
        scene_rect = self.scene().sceneRect()
        if scene_rect.width() <= 0 or scene_rect.height() <= 0:
            return
        self.resetTransform()
        scale = min(view_rect.width() / scene_rect.width(), view_rect.height() / scene_rect.height())
        scale = min(scale, 1.0)
        self.zoom = scale
        self.scale(scale, scale)
        self.user_zoomed = False

    def wheelEvent(self, event):
        angle = event.angleDelta().y()
        if angle == 0:
            return
        zoom_factor = 1.1 if angle > 0 else 1 / 1.1
        new_zoom = max(self.min_zoom, min(self.max_zoom, self.zoom * zoom_factor))
        if new_zoom == self.zoom:
            return
        factor = new_zoom / self.zoom
        self.zoom = new_zoom
        self.scale(factor, factor)
        self.user_zoomed = True
        self._clamp_scroll()
        
    def _clamp_scroll(self):
        """Ensure scrollbars do not show void beyond scene edges."""
        hbar = self.horizontalScrollBar()
        vbar = self.verticalScrollBar()
        if not hbar or not vbar:
            return
        bounds = self.scene().sceneRect()
        view_rect = self.viewport().rect()
        top_left = self.mapToScene(view_rect.topLeft())
        bottom_right = self.mapToScene(view_rect.bottomRight())
        view_scene_rect = QRectF(top_left, bottom_right).normalized()
        dx = 0
        if view_scene_rect.left() < bounds.left():
            dx = bounds.left() - view_scene_rect.left()
        elif view_scene_rect.right() > bounds.right():
            dx = bounds.right() - view_scene_rect.right()
        dy = 0
        if view_scene_rect.top() < bounds.top():
            dy = bounds.top() - view_scene_rect.top()
        elif view_scene_rect.bottom() > bounds.bottom():
            dy = bounds.bottom() - view_scene_rect.bottom()
        if dx != 0:
            hbar.setValue(hbar.value() + int(dx * self.zoom))
        if dy != 0:
            vbar.setValue(vbar.value() + int(dy * self.zoom))
        hbar.setValue(max(hbar.minimum(), min(hbar.value(), hbar.maximum())))
        vbar.setValue(max(vbar.minimum(), min(vbar.value(), vbar.maximum())))

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
            delta = 1
            key = event.key()
            if key == Qt.Key_Up: item.moveBy(0, -delta); event.accept()
            elif key == Qt.Key_Down: item.moveBy(0, delta); event.accept()
            elif key == Qt.Key_Left: item.moveBy(-delta, 0); event.accept()
            elif key == Qt.Key_Right: item.moveBy(delta, 0); event.accept()
            else: super().keyPressEvent(event)
        else:
            super().keyPressEvent(event)

class PortraitWindow(PersistentWindowMixin, QWidget):
    done_organizing = pyqtSignal()

    def __init__(self, original_resolution, config_path, parent=None):
        super(PortraitWindow, self).__init__(parent)
        self.original_resolution = original_resolution
        self.base_title = "Portrait Composer (Auto-Save Active)"
        self.setWindowFlags(Qt.Window | Qt.Tool)
        self.setFixedSize(650, 900)
        self.setup_persistence(
            config_path=config_path,
            settings_key='portrait_window_geometry',
            default_geo={'x': 635, 'y': 90, 'w': 650, 'h': 900},
            title_info_provider=self.get_title_info
        )
        self.scene = QGraphicsScene(self)
        self.scene.setSceneRect(0, 0, 1280, 1920)
        self.scene.setBackgroundBrush(QColor("#050505"))
        canvas_item = self.scene.addRect(0, 0, 1280, 1920, QPen(Qt.NoPen), QBrush(QColor("#1e1e1e")))
        canvas_item.setZValue(-100)
        left_danger = self.scene.addRect(0, 0, 100, 1920, QPen(Qt.NoPen), QBrush(QColor(231, 76, 60, 40)))
        left_danger.setZValue(100)
        right_danger = self.scene.addRect(1180, 0, 100, 1920, QPen(Qt.NoPen), QBrush(QColor(231, 76, 60, 40)))
        right_danger.setZValue(100)
        safe_pen = QPen(QColor("#2ecc71"), 3, Qt.DashLine)
        safe_zone = self.scene.addRect(100, 0, 1080, 1920, safe_pen)
        safe_zone.setZValue(101)
        limit_pen = QPen(QColor("#34495e"), 6, Qt.SolidLine)
        border = self.scene.addRect(0, 0, 1280, 1920, limit_pen)
        border.setZValue(102)
        self._add_guide_text("SAFE ZONE (VIDEO CONTENT)", 120, 50, "#2ecc71", 16)
        self._add_guide_text("OBSCURED / DANGER", 15, 960, "#c0392b", 14, rotate=-90)
        self._add_guide_text("OBSCURED / DANGER", 1225, 960, "#c0392b", 14, rotate=-90)
        self.view = PortraitView(self.scene, self)
        self.state_manager = get_state_manager()
        self.has_background = False
        self.background_item = None
        self.status_label = QLabel("Ready")
        self.status_label.setAlignment(Qt.AlignCenter)
        self.status_label.setProperty("class", "status")
        self.delete_button = QPushButton("DELETE SELECTED")
        self.delete_button.clicked.connect(self.delete_selected)
        self.delete_button.setEnabled(False)
        self.delete_button.setProperty("class", "danger")
        self.delete_button.setFixedSize(130, 40)
        self.undo_button = QPushButton("UNDO")
        self.undo_button.clicked.connect(self.undo)
        self.undo_button.setProperty("class", "neutral")
        self.undo_button.setToolTip("Ctrl+Z")
        self.undo_button.setCursor(QCursor(Qt.PointingHandCursor))
        self.undo_button.setFixedSize(120, 40)
        self.redo_button = QPushButton("REDO")
        self.redo_button.clicked.connect(self.redo)
        self.redo_button.setProperty("class", "neutral")
        self.redo_button.setToolTip("Ctrl+Y")
        self.redo_button.setCursor(QCursor(Qt.PointingHandCursor))
        self.redo_button.setFixedSize(120, 40)
        self.done_button = QPushButton("FINISH & SAVE")
        self.done_button.clicked.connect(self.on_done_clicked)
        self.done_button.setProperty("class", "accent")
        self.done_button.setFixedSize(120, 40)
        self.set_style()
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        top_bar = QFrame()
        top_bar.setProperty("class", "header")
        top_layout = QHBoxLayout(top_bar)
        top_layout.addWidget(self.status_label)
        main_layout.addWidget(top_bar)
        main_layout.addWidget(self.view, 1)
        controls = QFrame()
        controls.setProperty("class", "footer")
        ctrl_layout = QHBoxLayout(controls)
        ctrl_layout.setContentsMargins(10, 10, 10, 10)
        ctrl_layout.setSpacing(10)
        ctrl_layout.addStretch()
        ctrl_layout.addWidget(self.delete_button)
        ctrl_layout.addWidget(self.undo_button)
        ctrl_layout.addWidget(self.redo_button)
        ctrl_layout.addStretch()
        ctrl_layout.addWidget(self.done_button)
        main_layout.addWidget(controls)
        self.scene.selectionChanged.connect(self.on_selection_changed)
        self.update_undo_redo_buttons()

    def showEvent(self, event):
        super().showEvent(event)
        if not self.view.user_zoomed:
            self.view.fit_to_scene()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        if not self.view.user_zoomed:
            self.view.fit_to_scene()

    def _add_guide_text(self, text, x, y, color, size=16, rotate=0):
        t = self.scene.addText(text)
        t.setDefaultTextColor(QColor(color))
        t.setPos(x, y)
        if rotate != 0:
            t.setRotation(rotate)
        font = QFont("Segoe UI", size, QFont.Bold)
        t.setFont(font)
        t.setOpacity(0.7)

    def get_title_info(self):
        return self.base_title

    def set_style(self):
        self.setStyleSheet(PORTRAIT_WINDOW_STYLESHEET)

    def on_selection_changed(self):
        selected = self.scene.selectedItems()
        has_sel = bool(selected)
        self.delete_button.setEnabled(has_sel)

    def cleanup_duplicates(self, role, keep_item):
        for item in self.scene.items():
            if item != keep_item and isinstance(item, ResizablePixmapItem):
                if getattr(item, 'assigned_role', None) == role:
                    self.scene.removeItem(item)

    def add_scissored_item(self, pixmap, crop_rect, background_crop_width, role=None):
        item = ResizablePixmapItem(pixmap, crop_rect)
        if background_crop_width > 0:
            visual_scale_factor = 1280.0 / float(background_crop_width)
            item.current_width *= visual_scale_factor
            item.current_height *= visual_scale_factor
            item.update_handle_positions()
        self.scene.addItem(item)
        if role:
            item.setPos(self._default_position_for_role(role, item.current_width, item.current_height))
        else:
            item.setPos((1280 - item.current_width)/2, (1920 - item.current_height)/2)
        item.setSelected(True)
        item.item_changed.connect(lambda: self.on_item_modified(item))
        self.register_undo_action(
            "Add item",
            lambda: self.scene.removeItem(item),
            lambda: self.scene.addItem(item)
        )
        if role:
            item.assigned_role = role

    def _default_position_for_role(self, role, width, height):
        """Return default QPointF for given role."""
        padding = 20
        safe_left = 100
        safe_right = 1180 - width
        safe_top = padding
        safe_bottom = 1920 - height - padding
        role_lower = role.lower()
        if "loot" in role_lower:
            x = safe_right - padding
            y = safe_bottom - padding
        elif "own health" in role_lower or "boss hp" in role_lower:
            x = safe_left + padding
            y = safe_bottom - padding
        elif "mini map" in role_lower or "stats" in role_lower:
            x = safe_right - padding
            y = safe_top + padding
        elif "teammates" in role_lower:
            x = safe_left + padding
            y = safe_top + padding
        else:
            x = (1280 - width) / 2
            y = (1920 - height) / 2
        return QPointF(x, y)

    def on_item_modified(self, item):
        pass

    def delete_selected(self):
        selected_items = self.scene.selectedItems()
        if not selected_items:
            return
        items_to_delete = list(selected_items)
        positions = [(item, item.pos()) for item in items_to_delete]
        self.register_undo_action(
            "Delete selected",
            lambda: [self.scene.removeItem(item) for item in items_to_delete],
            lambda: [self.scene.addItem(item) for item, pos in positions]
        )
        for item in items_to_delete:
            self.scene.removeItem(item)
        self.on_selection_changed()

    def snap_to_default(self, item, role):
        pass

    def save_to_config(self, item, role):
        """Calculates 1:1 coordinates and saves to JSON."""
        try:
            crop_rect = item.crop_rect
            overlay_x = int(round(item.x()))
            overlay_y = int(round(item.y()))
            scale_factor = round(item.current_width / crop_rect.width(), 3)
            base_dir = os.path.dirname(os.path.dirname(os.path.abspath(self.config_path)))
            real_conf_path = os.path.join(base_dir, 'processing', 'crops_coordinations.conf')
            data = {"crops_1080p": {}, "scales": {}, "overlays": {}}
            if os.path.exists(real_conf_path):
                try:
                    with open(real_conf_path, 'r') as f:
                        data = json.load(f)
                except:
                    pass
            if "crops_1080p" not in data: data["crops_1080p"] = {}
            if "scales" not in data: data["scales"] = {}
            if "overlays" not in data: data["overlays"] = {}
            data["crops_1080p"][role] = [crop_rect.x(), crop_rect.y(), crop_rect.width(), crop_rect.height()]
            data["scales"][role] = scale_factor
            data["overlays"][role] = {"x": overlay_x, "y": overlay_y}
            with open(real_conf_path, 'w') as f:
                json.dump(data, f, indent=4)
            self.status_label.setText(f"SAVED: {role.upper()} (Scale: {scale_factor})")
            self.status_label.setStyleSheet("color: #2ecc71; font-weight: bold; font-size: 14px;")
            QTimer.singleShot(2000, lambda: self.status_label.setStyleSheet("color: #777; font-weight: bold; font-size: 12px;"))
            QTimer.singleShot(2000, lambda: self.status_label.setText("Ready"))
        except Exception as e:
            self.status_label.setText(f"SAVE ERROR: {str(e)}")
            self.status_label.setStyleSheet("color: red; font-weight: bold;")

    def on_done_clicked(self):
        """Handle done button click."""
        try:
            from enhanced_logger import get_enhanced_logger
            enhanced_logger = get_enhanced_logger()
            if enhanced_logger:
                config_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(self.config_path))), 'processing', 'crops_coordinations.conf')
                if os.path.exists(config_path):
                    enhanced_logger.log_finished_button_click(config_path, config_path)
        except ImportError:
            pass
        self.status_label.setText("✓ All elements organized!")
        self.status_label.setStyleSheet("color: #2ecc71; font-weight: bold; font-size: 14px;")
        QTimer.singleShot(1500, lambda: self.status_label.setText("Ready"))
        QTimer.singleShot(1500, lambda: self.status_label.setStyleSheet("color: #777; font-weight: bold; font-size: 12px;"))
        self.done_organizing.emit()

    def set_background_image(self, full_pixmap):
        """
        Takes the full 16:9 1080p snapshot and simulates how it looks in the 9:16 
        portrait video (zoomed/cropped center), with a dimming overlay.
        """
        if full_pixmap.isNull(): return
        target_w, target_h = 1280, 1920
        src_w = full_pixmap.width()
        src_h = full_pixmap.height()
        scale = target_h / src_h
        new_w = int(src_w * scale)
        new_h = int(src_h * scale)
        scaled_pix = full_pixmap.scaled(new_w, new_h, Qt.KeepAspectRatio, Qt.SmoothTransformation)
        crop_x = (new_w - target_w) // 2
        final_bg = scaled_pix.copy(crop_x, 0, target_w, target_h)
        dimmed = QPixmap(final_bg.size())
        dimmed.fill(Qt.transparent)
        painter = QPainter(dimmed)
        painter.drawPixmap(0, 0, final_bg)
        painter.fillRect(dimmed.rect(), QColor(0, 0, 0, 80))
        painter.end()
        if self.background_item:
            self.scene.removeItem(self.background_item)
        self.background_item = QGraphicsPixmapItem(dimmed)
        self.background_item.setZValue(-80)
        self.scene.addItem(self.background_item)
        self.has_background = True

    def register_undo_action(self, description, undo_func, redo_func):
        """Register an undoable action with the state manager."""
        self.state_manager.add_undo_action(
            action_type="portrait_edit",
            description=description,
            undo_func=undo_func,
            redo_func=redo_func
        )
        self.update_undo_redo_buttons()

    def undo(self):
        """Perform undo operation."""
        if self.state_manager.undo():
            self.status_label.setText("Undo performed")
            self.status_label.setStyleSheet("color: #3498db; font-weight: bold;")
            QTimer.singleShot(1500, lambda: self.status_label.setText("Ready"))
            QTimer.singleShot(1500, lambda: self.status_label.setStyleSheet("color: #777; font-weight: bold; font-size: 12px;"))
        self.update_undo_redo_buttons()

    def redo(self):
        """Perform redo operation."""
        if self.state_manager.redo():
            self.status_label.setText("Redo performed")
            self.status_label.setStyleSheet("color: #3498db; font-weight: bold;")
            QTimer.singleShot(1500, lambda: self.status_label.setText("Ready"))
            QTimer.singleShot(1500, lambda: self.status_label.setStyleSheet("color: #777; font-weight: bold; font-size: 12px;"))
        self.update_undo_redo_buttons()

    def update_undo_redo_buttons(self):
        """Update undo/redo button states based on state manager."""
        self.undo_button.setEnabled(self.state_manager.can_undo())
        self.redo_button.setEnabled(self.state_manager.can_redo())
        undo_desc = self.state_manager.get_undo_description()
        redo_desc = self.state_manager.get_redo_description()
        if undo_desc:
            self.undo_button.setToolTip(f"Ctrl+Z: {undo_desc}")
        else:
            self.undo_button.setToolTip("Ctrl+Z")
        if redo_desc:
            self.redo_button.setToolTip(f"Ctrl+Y: {redo_desc}")
        else:
            self.redo_button.setToolTip("Ctrl+Y")
