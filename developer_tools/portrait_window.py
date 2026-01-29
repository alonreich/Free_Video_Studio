import json
import os
from PyQt5.QtWidgets import (
    QApplication, QWidget, QPushButton, QHBoxLayout, QVBoxLayout,
    QLabel, QGraphicsScene, QGraphicsView, QGraphicsPixmapItem,
    QGraphicsItem, QComboBox, QMessageBox, QFrame, QGraphicsRectItem, QGraphicsSimpleTextItem,
    QDialog
)

from PyQt5.QtCore import Qt, QTimer, QRectF, pyqtSignal, QPointF, QPropertyAnimation, QEasingCurve
from PyQt5.QtGui import QPainter, QPen, QColor, QFont, QBrush, QPixmap, QCursor
from utils import PersistentWindowMixin
from graphics_items import ResizablePixmapItem
from config import PORTRAIT_WINDOW_STYLESHEET, HUD_ELEMENT_MAPPINGS
from state_manager import get_state_manager
from config_manager import get_config_manager
BACKEND_SCALE = 1280.0 / 1080.0
Z_ORDER_MAP = {
    'main': 0,
    'loot': 10,
    'normal_hp': 20,
    'boss_hp': 20,
    'stats': 30,
    'team': 40
}

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
            delta = 1
            key = event.key()
            if key == Qt.Key_Up: item.moveBy(0, -delta)
            elif key == Qt.Key_Down: item.moveBy(0, delta)
            elif key == Qt.Key_Left: item.moveBy(-delta, 0)
            elif key == Qt.Key_Right: item.moveBy(delta, 0)
            else: super().keyPressEvent(event)
            if hasattr(self.parent(), 'on_item_modified'):
                self.parent().on_item_modified(item)
        else:
            super().keyPressEvent(event)

class SummaryToast(QDialog):
    def __init__(self, configured, unchanged, parent=None):
        super().__init__(parent)
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setAttribute(Qt.WA_TranslucentBackground)
        container = QFrame(self)
        container.setObjectName("container")
        self.setStyleSheet("""
            #container {
                background-color: #2c3e50;
                border: 2px solid #555;
                border-radius: 15px;
            }
            QLabel {
                color: #eee;
                font-size: 14px;
                padding: 2px;
                background-color: transparent;
            }
            #title {
                font-size: 18px;
                font-weight: bold;
                color: #3498db;
                padding-bottom: 10px;
            }
            #changed { color: #2ecc71; }
            #unchanged { color: #e74c3c; }
        """)
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.addWidget(container)
        content_layout = QVBoxLayout(container)
        content_layout.setContentsMargins(20, 20, 20, 20)
        title_label = QLabel("Configuration Saved")
        title_label.setObjectName("title")
        title_label.setAlignment(Qt.AlignCenter)
        content_layout.addWidget(title_label)
        if configured:
            changed_label = QLabel("Updated Elements:")
            changed_label.setObjectName("changed")
            content_layout.addWidget(changed_label)
            for item in configured:
                content_layout.addWidget(QLabel(f"  • {item}"))
        if unchanged:
            unchanged_label = QLabel("Unchanged Elements:")
            unchanged_label.setObjectName("unchanged")
            content_layout.addWidget(unchanged_label)
            for item in unchanged:
                content_layout.addWidget(QLabel(f"  • {item}"))
        QTimer.singleShot(2500, self.accept)

class PortraitWindow(PersistentWindowMixin, QWidget):
    done_organizing = pyqtSignal()

    def __init__(self, original_resolution, config_path, parent=None):
        super(PortraitWindow, self).__init__(parent)
        self.original_resolution = original_resolution
        self.base_title = "Portrait Composer (Auto-Save Active)"
        self.setWindowFlags(Qt.Window)
        self.resize(450, 800)
        self.setMinimumSize(400, 700)
        self.setup_persistence(config_path, 'portrait_window_geometry', {'x': 635, 'y': 90, 'w': 650, 'h': 800}, self.get_title_info)
        self.scene = QGraphicsScene(self)
        self.scene.setSceneRect(0, 0, 1080, 1920)
        self.scene.setBackgroundBrush(QColor("#1e1e1e"))
        canvas_item = self.scene.addRect(0, 0, 1080, 1920, QPen(Qt.NoPen), QBrush(QColor("#1e1e1e")))
        canvas_item.setZValue(-100)
        limit_pen = QPen(QColor("#34495e"), 6, Qt.SolidLine)
        border = self.scene.addRect(0, 0, 1080, 1920, limit_pen)
        border.setZValue(102)
        top_bar_rect = self.scene.addRect(0, 0, 1080, 150, QPen(Qt.NoPen), QBrush(QColor("black")))
        top_bar_rect.setZValue(100)
        self.view = PortraitView(self.scene, self)
        self.config_manager = get_config_manager(config_path)
        self.state_manager = get_state_manager()
        self.has_background = False
        self.background_item = None
        self.modified_roles = set()
        self.placeholders_group = [] 
        self.status_label = QLabel("Ready")
        self.status_label.setAlignment(Qt.AlignCenter)
        self.status_label.setProperty("class", "status")
        self.toggle_ph_btn = QPushButton("Hide Existing")
        self.toggle_ph_btn.setCheckable(True)
        self.toggle_ph_btn.clicked.connect(self.toggle_placeholders)
        self.toggle_ph_btn.setFixedSize(100, 40)
        self.delete_button = QPushButton("DELETE SELECTED")
        self.delete_button.clicked.connect(self.delete_selected)
        self.delete_button.setEnabled(False)
        self.delete_button.setProperty("class", "danger")
        self.delete_button.setFixedSize(130, 40)
        self.undo_button = QPushButton("UNDO")
        self.undo_button.clicked.connect(self.undo)
        self.undo_button.setProperty("class", "neutral")
        self.undo_button.setToolTip("Ctrl+Z")
        self.undo_button.setFixedSize(120, 40)
        self.redo_button = QPushButton("REDO")
        self.redo_button.clicked.connect(self.redo)
        self.redo_button.setProperty("class", "neutral")
        self.redo_button.setToolTip("Ctrl+Y")
        self.redo_button.setFixedSize(120, 40)
        self.done_button = QPushButton("FINISH && SAVE")
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
        top_layout.addStretch()
        top_layout.addWidget(self.toggle_ph_btn)
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
        self.load_existing_placeholders()

    def toggle_placeholders(self, checked):
        """[FIX #8] Toggle visibility of existing placeholder rectangles."""
        visible = not checked
        for item in self.placeholders_group:
            item.setVisible(visible)
        self.toggle_ph_btn.setText("Show Existing" if checked else "Hide Existing")

    def load_existing_placeholders(self):
        """Load already configured elements as visual placeholders."""
        self.placeholders_group = []
        try:
            config = self.config_manager.load_config()
            configured = self.config_manager.get_configured_elements()
            inv_map = {k: v for k, v in HUD_ELEMENT_MAPPINGS.items()}
            for tech_key in configured:
                scale = config.get('scales', {}).get(tech_key, 1.0)
                if scale <= 0.001: continue
                overlay = config.get('overlays', {}).get(tech_key)
                crop = config.get('crops_1080p', {}).get(tech_key)
                if overlay and crop and len(crop) == 4:
                    x = overlay.get('x', 0) / BACKEND_SCALE
                    y = (overlay.get('y', 0) / BACKEND_SCALE) + 150
                    w = (crop[2] * scale) / BACKEND_SCALE
                    h = (crop[3] * scale) / BACKEND_SCALE
                    rect_item = QGraphicsRectItem(x, y, w, h)
                    rect_item.setBrush(QBrush(QColor(100, 100, 100, 100)))
                    rect_item.setPen(QPen(QColor(150, 150, 150), 1, Qt.DashLine))
                    rect_item.setZValue(-10)
                    display_name = inv_map.get(tech_key, tech_key)
                    text = QGraphicsSimpleTextItem(f"Existing: {display_name}", rect_item)
                    text.setBrush(QBrush(QColor("white")))
                    text.setPos(x + 5, y + 5)
                    self.scene.addItem(rect_item)
                    self.placeholders_group.append(rect_item)
        except Exception as e:
            print(f"Error loading placeholders: {e}")

    def showEvent(self, event):
        super().showEvent(event)
        if not self.view.user_zoomed: self.view.fit_to_scene()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        if not self.view.user_zoomed: self.view.fit_to_scene()

    def _add_guide_text(self, text, x, y, color, size=16, rotate=0):
        t = self.scene.addText(text)
        t.setDefaultTextColor(QColor(color))
        t.setPos(x, y)
        if rotate != 0: t.setRotation(rotate)
        font = QFont("Segoe UI", size, QFont.Bold)
        t.setFont(font)
        t.setOpacity(0.7)

    def get_title_info(self): return self.base_title

    def set_style(self): self.setStyleSheet(PORTRAIT_WINDOW_STYLESHEET)

    def on_selection_changed(self):
        selected = self.scene.selectedItems()
        self.delete_button.setEnabled(bool(selected))

    def add_scissored_item(self, pixmap, crop_rect, background_crop_width, role=None):
        item = ResizablePixmapItem(pixmap, crop_rect)
        item.current_width *= 1.2
        item.current_height *= 1.2
        item.update_handle_positions()
        self.scene.addItem(item)
        z_val = 0
        if role:
            tech_key_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
            tech_key = tech_key_map.get(role, 'unknown')
            z_val = Z_ORDER_MAP.get(tech_key, 50)
        item.setZValue(z_val)
        if role:
            item.setPos(self._default_position_for_role(role, item.current_width, item.current_height))
        else:
            item.setPos((1080 - item.current_width)/2, (1920 - item.current_height)/2)
        self.scene.clearSelection()
        item.setSelected(True)
        item.item_changed.connect(lambda: self.on_item_modified(item))
        self.register_undo_action("Add item", lambda: self.scene.removeItem(item), lambda: self.scene.addItem(item))
        if role:
            item.assigned_role = role
            self.modified_roles.add(role)

    def _default_position_for_role(self, role, width, height):
        padding = 20
        safe_left = padding
        safe_right = 1080 - width - padding
        safe_top = padding + 150
        safe_bottom = 1770 - height - padding
        role_lower = role.lower()
        if "loot" in role_lower: return QPointF(safe_right, safe_bottom)
        elif "own health" in role_lower or "boss hp" in role_lower: return QPointF(safe_left, safe_bottom)
        elif "mini map" in role_lower or "stats" in role_lower: return QPointF(safe_right, safe_top)
        elif "teammates" in role_lower: return QPointF(safe_left, safe_top)
        else: return QPointF((1080 - width) / 2, ((1920 - 150) - height) / 2 + 150)

    def on_item_modified(self, item):
        if item.assigned_role: self.modified_roles.add(item.assigned_role)

    def delete_selected(self):
        selected_items = self.scene.selectedItems()
        if not selected_items: return
        items_to_delete = list(selected_items)
        positions = [(item, item.pos()) for item in items_to_delete]
        for item in items_to_delete:
            if hasattr(item, 'assigned_role') and item.assigned_role:
                tech_key = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}.get(item.assigned_role)
                if tech_key: self.config_manager.delete_crop_coordinates(tech_key)
        self.register_undo_action("Delete selected", 
            lambda: [self.scene.removeItem(item) for item in items_to_delete],
            lambda: [self.scene.addItem(item) for item, pos in positions]
        )
        for item in items_to_delete: self.scene.removeItem(item)
        self.on_selection_changed()

    def on_done_clicked(self):
        """
        Handle done button click.
        This version fixes two critical bugs:
        1. It correctly calculates the scale_factor by relating the UI element's final size
           to the standardized 1080p content-aware crop size, ensuring visual consistency.
        2. It correctly calculates the overlay's y-position relative to the full 1920px frame,
           not the content area, which matches the expectation of the filter_builder.
        """
        logger = self.config_manager.logger
        tech_key_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
        items_to_save = []
        for item in self.scene.items():
            if isinstance(item, ResizablePixmapItem) and item.assigned_role in self.modified_roles:
                items_to_save.append(item)
        if not items_to_save:
            self.done_organizing.emit()
            return
        for item in items_to_save:
            tech_key = tech_key_map.get(item.assigned_role)
            if not tech_key: continue
            if tech_key == "boss_hp":
                self.config_manager.delete_crop_coordinates("normal_hp")
                logger.info("Saving Boss HP -> Removed Normal HP config")
            elif tech_key == "normal_hp":
                self.config_manager.delete_crop_coordinates("boss_hp")
                logger.info("Saving Normal HP -> Removed Boss HP config")
        success_count = 0
        configured_items = []
        for item in items_to_save:
            tech_key = tech_key_map.get(item.assigned_role)
            if not tech_key: continue

            from PyQt5.QtCore import QRect
            from developer_tools.coordinate_math import transform_to_content_area_int
            crop_rect = item.crop_rect
            rect = QRect(crop_rect.x(), crop_rect.y(), crop_rect.width(), crop_rect.height())
            success = self.config_manager.save_crop_coordinates(
                tech_key=tech_key,
                rect=rect,
                original_resolution=self.original_resolution
            )
            if success:
                rect_tuple = (rect.x(), rect.y(), rect.width(), rect.height())
                transformed_rect = transform_to_content_area_int(rect_tuple, self.original_resolution)
                crop_1080_width = transformed_rect[2]
                if crop_1080_width > 0:
                    scale_factor = (item.current_width * BACKEND_SCALE) / crop_1080_width
                else:
                    scale_factor = 1.0
                self.config_manager.update_scale_factor(tech_key, round(scale_factor, 4))
                safe_y = max(150.0, item.y())
                portrait_x = int(round(item.x()))
                portrait_y = int(round(safe_y - 150))
                self.config_manager.update_overlay_position(tech_key, portrait_x, portrait_y)
                logger.info(f"Saved {tech_key}: Scale={round(scale_factor, 3)}, PortraitPos=({portrait_x},{portrait_y})")
                success_count += 1
                display_name = HUD_ELEMENT_MAPPINGS.get(tech_key, tech_key)
                configured_items.append(display_name)
            else:
                logger.error(f"Failed to save crop coordinates for {tech_key}")
        all_hud_items = list(HUD_ELEMENT_MAPPINGS.values())
        unchanged_items = [item for item in all_hud_items if item not in configured_items]
        self.show_toast_summary(configured_items, unchanged_items)
    
    def show_toast_summary(self, configured_items, unchanged_items):
        """Show a summary toast, blink it, then fade out the main window."""
        self.done_organizing.emit()
        self.toast = SummaryToast(configured_items, unchanged_items, self)
        parent_geo = self.geometry()
        self.toast.adjustSize()
        toast_x = parent_geo.x() + (parent_geo.width() - self.toast.width()) / 2
        toast_y = parent_geo.y() + (parent_geo.height() - self.toast.height()) / 2
        self.toast.move(int(toast_x), int(toast_y))
        self.toast.show()
        QTimer.singleShot(2000, self.start_blinking)

    def start_blinking(self):
        """Starts the blinking sequence for the summary toast."""
        self._blinks_remaining = 7
        self.blink_timer = QTimer(self)
        self.blink_timer.timeout.connect(self._blink_toast)
        self.blink_timer.start(280)

    def _blink_toast(self):
        """Toggles toast visibility and ends the sequence when done."""
        if not hasattr(self, 'toast') or not self.toast:
            return
        self.toast.setVisible(not self.toast.isVisible())
        self._blinks_remaining -= 1
        if self._blinks_remaining <= 0:
            self.blink_timer.stop()
            if self.toast:
                self.toast.accept()
            self.close_with_fade()

    def close_with_fade(self):
        """Fade the window out before closing."""
        self.animation = QPropertyAnimation(self, b"windowOpacity")
        self.animation.setDuration(300)
        self.animation.setStartValue(self.windowOpacity())
        self.animation.setEndValue(0.0)
        self.animation.setEasingCurve(QEasingCurve.OutCubic)
        self.animation.finished.connect(self.close)
        self.animation.start()

    def set_background_image(self, full_pixmap):
        if full_pixmap.isNull(): return
        content_w, content_h = 1080, 1620
        src_w, src_h = full_pixmap.width(), full_pixmap.height()
        if src_w == 0 or src_h == 0: return
        scale_w = content_w / src_w
        scale_h = content_h / src_h
        scale = max(scale_w, scale_h)
        scaled_w, scaled_h = int(src_w * scale), int(src_h * scale)
        scaled_pix = full_pixmap.scaled(scaled_w, scaled_h, Qt.KeepAspectRatio, Qt.SmoothTransformation)
        crop_x = (scaled_w - content_w) // 2
        crop_y = (scaled_h - content_h) // 2
        final_bg = scaled_pix.copy(crop_x, crop_y, content_w, content_h)
        dimmed_pix = QPixmap(final_bg.size())
        dimmed_pix.fill(Qt.transparent)
        painter = QPainter(dimmed_pix)
        painter.drawPixmap(0, 0, final_bg)
        painter.fillRect(dimmed_pix.rect(), QColor(0, 0, 0, 80))
        painter.end()
        if self.background_item:
            self.background_item.setPixmap(dimmed_pix)
        else:
            self.background_item = QGraphicsPixmapItem(dimmed_pix)
            self.background_item.setZValue(-80)
            self.scene.addItem(self.background_item)
        self.background_item.setPos(0, 150)
        self.has_background = True

    def register_undo_action(self, description, undo_func, redo_func):
        self.state_manager.add_undo_action("portrait_edit", description, undo_func, redo_func)
        self.update_undo_redo_buttons()

    def undo(self):
        if self.state_manager.undo():
            self.status_label.setText("Undo performed")
            QTimer.singleShot(1500, lambda: self.status_label.setText("Ready"))
        self.update_undo_redo_buttons()

    def redo(self):
        if self.state_manager.redo():
            self.status_label.setText("Redo performed")
            QTimer.singleShot(1500, lambda: self.status_label.setText("Ready"))
        self.update_undo_redo_buttons()

    def update_undo_redo_buttons(self):
        self.undo_button.setEnabled(self.state_manager.can_undo())
        self.redo_button.setEnabled(self.state_manager.can_redo())
        undo_desc = self.state_manager.get_undo_description()
        redo_desc = self.state_manager.get_redo_description()
        self.undo_button.setToolTip(f"Ctrl+Z: {undo_desc}" if undo_desc else "Ctrl+Z")
        self.redo_button.setToolTip(f"Ctrl+Y: {redo_desc}" if redo_desc else "Ctrl+Y")