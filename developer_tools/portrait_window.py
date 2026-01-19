import json
import os
from PyQt5.QtWidgets import (
    QApplication, QWidget, QPushButton, QHBoxLayout, QVBoxLayout,
    QLabel, QGraphicsScene, QGraphicsView, QGraphicsPixmapItem,
    QGraphicsItem, QComboBox, QMessageBox, QFrame
)

from PyQt5.QtCore import Qt, QTimer, QRectF
from PyQt5.QtGui import QPainter, QPen, QColor, QFont, QBrush, QPixmap
from utils import PersistentWindowMixin
from graphics_items import ResizablePixmapItem
from config import PORTRAIT_WINDOW_STYLESHEET, HUD_ELEMENT_MAPPINGS

class PortraitView(QGraphicsView):
    def keyPressEvent(self, event):
        selected_items = self.scene().selectedItems()
        if selected_items:
            item = selected_items[0]
            delta = 1
            if event.modifiers() & Qt.ShiftModifier: delta = 10
            key = event.key()
            if key == Qt.Key_Up: item.moveBy(0, -delta); event.accept()
            elif key == Qt.Key_Down: item.moveBy(0, delta); event.accept()
            elif key == Qt.Key_Left: item.moveBy(-delta, 0); event.accept()
            elif key == Qt.Key_Right: item.moveBy(delta, 0); event.accept()
            else: super().keyPressEvent(event)
        else:
            super().keyPressEvent(event)

class PortraitWindow(PersistentWindowMixin, QWidget):
    def __init__(self, original_resolution, config_path, parent=None):
        super(PortraitWindow, self).__init__(parent)
        self.original_resolution = original_resolution
        self.base_title = "Portrait Composer (Auto-Save Active)"
        self.setWindowFlags(Qt.Window | Qt.Tool | Qt.WindowStaysOnTopHint)
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
        self.view.setRenderHint(QPainter.Antialiasing)
        self.view.setRenderHint(QPainter.SmoothPixmapTransform)
        self.view.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.view.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.role_map_rev = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
        self.role_label = QLabel("Role: -")
        self.role_label.setAlignment(Qt.AlignCenter)
        self.has_background = False
        self.background_item = None
        self.status_label = QLabel("Ready")
        self.status_label.setAlignment(Qt.AlignCenter)
        self.status_label.setProperty("class", "status")
        self.delete_button = QPushButton("Delete Selected")
        self.delete_button.clicked.connect(self.delete_selected)
        self.delete_button.setEnabled(False)
        self.delete_button.setProperty("class", "warning")
        self.done_button = QPushButton("✓ Done Organizing")
        self.done_button.clicked.connect(self.on_done_clicked)
        self.done_button.setProperty("class", "success")
        self.instructions_label = QLabel("Arrange HUD elements in safe zone. Use arrow keys for fine adjustment.")
        self.instructions_label.setProperty("class", "italic")
        self.instructions_label.setAlignment(Qt.AlignCenter)
        self.pos_label = QLabel("Pos: -")
        self.pos_label.setProperty("class", "info")
        self.scale_label = QLabel("Size: -")
        self.scale_label.setProperty("class", "info")
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
        ctrl_layout = QVBoxLayout(controls)
        ctrl_layout.addWidget(self.instructions_label)
        row1 = QHBoxLayout()
        row1.addWidget(self.role_label)
        row1.addWidget(self.delete_button)
        ctrl_layout.addLayout(row1)
        row2 = QHBoxLayout()
        row2.addWidget(self.pos_label)
        row2.addStretch()
        row2.addWidget(self.scale_label)
        ctrl_layout.addLayout(row2)
        row3 = QHBoxLayout()
        row3.addStretch()
        row3.addWidget(self.done_button)
        row3.addStretch()
        ctrl_layout.addLayout(row3)
        main_layout.addWidget(controls)
        self.scene.selectionChanged.connect(self.on_selection_changed)

    def showEvent(self, event):
        super().showEvent(event)
        self.view.fitInView(self.scene.sceneRect(), Qt.KeepAspectRatio)

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.view.fitInView(self.scene.sceneRect(), Qt.KeepAspectRatio)

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
        if has_sel:
            item = selected[0]
            if len(selected) > 1:
                for i in selected[1:]: i.setSelected(False)
            self.update_item_info(item)
            if hasattr(item, 'assigned_role') and item.assigned_role:
                self.role_label.setText(f"<b>Role:</b> {item.assigned_role}")
            else:
                self.role_label.setText("<b>Role:</b> -")
        else:
            self.role_label.setText("<b>Role:</b> -")
            self.pos_label.setText("Pos: -")
            self.scale_label.setText("Size: -")

    def cleanup_duplicates(self, role, keep_item):
        for item in self.scene.items():
            if item != keep_item and isinstance(item, ResizablePixmapItem):
                if getattr(item, 'assigned_role', None) == role:
                    self.scene.removeItem(item)

    def add_scissored_item(self, pixmap, crop_rect, background_crop_width):
        item = ResizablePixmapItem(pixmap, crop_rect)
        if background_crop_width > 0:
            visual_scale_factor = 1280.0 / float(background_crop_width)
            item.current_width *= visual_scale_factor
            item.current_height *= visual_scale_factor
            item.update_handle_positions()
        self.scene.addItem(item)
        item.setPos((1280 - item.current_width)/2, (1920 - item.current_height)/2)
        item.setSelected(True)
        item.item_changed.connect(lambda: self.on_item_modified(item))

    def on_item_modified(self, item):
        if item.isSelected():
            self.update_item_info(item)

    def update_item_info(self, item):
        self.pos_label.setText(f"Pos: {int(item.x())}, {int(item.y())}")
        self.scale_label.setText(f"Size: {int(item.current_width)}x{int(item.current_height)}")

    def delete_selected(self):
        for item in self.scene.selectedItems():
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
        self.status_label.setText("✓ All elements organized!")
        self.status_label.setStyleSheet("color: #2ecc71; font-weight: bold; font-size: 14px;")
        QTimer.singleShot(1500, lambda: self.status_label.setText("Ready"))
        QTimer.singleShot(1500, lambda: self.status_label.setStyleSheet("color: #777; font-weight: bold; font-size: 12px;"))
        pass

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
