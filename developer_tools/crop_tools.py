import sys
import os
import traceback
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.devnull
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.dont_write_bytecode = True

import ctypes
import tempfile
import subprocess
import psutil
from PyQt5.QtWidgets import (
    QApplication, QWidget, QGraphicsScene, QGraphicsPixmapItem, QGraphicsRectItem, 
    QGraphicsSimpleTextItem, QDialog, QFrame, QVBoxLayout, QLabel, QHBoxLayout, QMessageBox
)

from PyQt5.QtCore import Qt, QTimer, QRectF, pyqtSignal, QPointF, QPropertyAnimation, QEasingCurve
from PyQt5.QtGui import QPainter, QColor, QFont, QBrush, QPixmap, QPen
from utils import PersistentWindowMixin
from Keyboard_Mixing import KeyboardShortcutMixin
from media_processor import MediaProcessor
from ui_setup import Ui_CropApp
from app_handlers import CropAppHandlers
from config import CROP_APP_STYLESHEET, HUD_ELEMENT_MAPPINGS, Z_ORDER_MAP, UI_COLORS, UI_LAYOUT, UI_BEHAVIOR
from logger_setup import setup_logger
from enhanced_logger import get_enhanced_logger
from config_manager import get_config_manager
from state_manager import get_state_manager
from graphics_items import ResizablePixmapItem

class SummaryToast(QDialog):
    def __init__(self, configured, unchanged, parent=None):
        super().__init__(parent)
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setAttribute(Qt.WA_TranslucentBackground)
        container = QFrame(self)
        container.setObjectName("container")
        self.setStyleSheet(f"""
            #container {{
                background-color: {UI_COLORS.BACKGROUND_MEDIUM};
                border: 2px solid {UI_COLORS.BORDER_MEDIUM};
                border-radius: 15px;
            }}
            QLabel {{
                color: {UI_COLORS.TEXT_SECONDARY};
                font-size: 14px;
                padding: 2px;
                background-color: transparent;
            }}
            #title {{
                font-size: 18px;
                font-weight: bold;
                color: {UI_COLORS.PRIMARY};
                padding-bottom: 10px;
            }}
            #changed {{ color: {UI_COLORS.SUCCESS}; }}
            #unchanged {{ color: {UI_COLORS.DANGER}; }}
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

class CropApp(KeyboardShortcutMixin, PersistentWindowMixin, QWidget, CropAppHandlers):
    done_organizing = pyqtSignal()

    def __init__(self, logger_instance, enhanced_logger_instance, file_path=None):
        super().__init__()
        self.logger = logger_instance
        self.enhanced_logger = enhanced_logger_instance
        self.base_title = "Fortnite Crop Tool"
        self.script_dir = os.path.dirname(os.path.abspath(__file__))
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, '..'))
        self.config_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
        self.config_manager = get_config_manager(self.config_path, self.logger)
        self.state_manager = get_state_manager(self.logger)
        self.last_dir = None
        self.bin_dir = os.path.abspath(os.path.join(self.base_dir, 'binaries'))
        self.snapshot_path = None
        self.media_processor = MediaProcessor(self.bin_dir)
        self.background_crop_width = 0
        self.modified_roles = set()
        self.placeholders_group = []
        self.background_item = None
        self.ui = Ui_CropApp()
        self.ui.setupUi(self)
        self.connect_signals()
        self.timer = QTimer(self)
        self.timer.setInterval(100)
        self.timer.timeout.connect(self.update_ui)
        self.timer.start()
        self.set_style()
        self.setFocusPolicy(Qt.StrongFocus)
        self.setFocus()
        self.setup_persistence(
            config_path=self.config_path,
            settings_key='window_geometry',
            default_geo={'x': 100, 'y': 100, 'w': 1800, 'h': 900},
            title_info_provider=self.get_title_info
        )
        self._setup_portrait_editor()
        if file_path and os.path.exists(file_path):
            self.load_file(file_path)

    def connect_signals(self):
        super().connect_signals()
        self.done_button.clicked.connect(self.on_done_clicked)
        self.delete_button.clicked.connect(self.delete_selected)
        self.undo_button.clicked.connect(self.undo)
        self.redo_button.clicked.connect(self.redo)
        self.show_placeholders_checkbox.stateChanged.connect(self.toggle_placeholders)
        self.portrait_scene.selectionChanged.connect(self.on_selection_changed)

    def _setup_portrait_editor(self):
        """Initializes the integrated portrait editor components."""
        canvas_item = self.portrait_scene.addRect(0, 0, 1080, 1920, QPen(Qt.NoPen), QBrush(QColor("#1e1e1e")))
        canvas_item.setZValue(-100)
        limit_pen = QPen(QColor("#34495e"), 6, Qt.SolidLine)
        border = self.portrait_scene.addRect(0, 0, 1080, 1920, limit_pen)
        border.setZValue(102)
        top_bar_rect = self.portrait_scene.addRect(0, 0, 1080, 150, QPen(Qt.NoPen), QBrush(QColor("black")))
        top_bar_rect.setZValue(100)
        self.load_existing_placeholders()
        self.update_undo_redo_buttons()
        self.on_selection_changed()

    def keyPressEvent(self, event):
        key = event.key()
        if key == Qt.Key_F12:
            self._deferred_launch_main_app()
            event.accept()
            return
        if self.portrait_view.underMouse():
            self.portrait_view.keyPressEvent(event)
        else:
            super(CropApp, self).keyPressEvent(event)

    def toggle_placeholders(self):
        is_visible = self.show_placeholders_checkbox.isChecked()
        for item in self.placeholders_group:
            item.setVisible(is_visible)

    def load_existing_placeholders(self):
        for item in self.placeholders_group:
            self.portrait_scene.removeItem(item)
        self.placeholders_group.clear()
        try:
            config = self.config_manager.load_config()
            configured = self.config_manager.get_configured_elements()
            inv_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
            for tech_key in configured:
                scale = config.get('scales', {}).get(tech_key, 1.0)
                if scale <= 0.001: continue
                overlay = config.get('overlays', {}).get(tech_key)
                crop = config.get('crops_1080p', {}).get(tech_key)
                if overlay and crop and len(crop) == 4:
                    x = overlay.get('x', 0) / (1280.0/1080.0)
                    y = (overlay.get('y', 0) / (1280.0/1080.0)) + 150
                    w = (crop[2] * scale) / (1280.0/1080.0)
                    h = (crop[3] * scale) / (1280.0/1080.0)
                    rect_item = QGraphicsRectItem(x, y, w, h)
                    rect_item.setBrush(QBrush(QColor(UI_COLORS.BACKGROUND_LIGHT)))
                    rect_item.setPen(QPen(QColor(UI_COLORS.BORDER_MEDIUM), 2, Qt.SolidLine))
                    rect_item.setZValue(-10)
                    self.portrait_scene.addItem(rect_item)
                    self.placeholders_group.append(rect_item)
        except Exception as e:
            self.logger.error(f"Error loading placeholders: {e}")
        self.toggle_placeholders()

    def on_selection_changed(self):
        selected = self.portrait_scene.selectedItems()
        self.delete_button.setEnabled(bool(selected))

    def add_scissored_item(self, pixmap, crop_rect, background_crop_width, role=None):
        item = ResizablePixmapItem(pixmap, crop_rect)
        item.current_width *= 1.2
        item.current_height *= 1.2
        item.update_handle_positions()
        self.portrait_scene.addItem(item)
        tech_key = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}.get(role, 'unknown')
        z_val = Z_ORDER_MAP.get(tech_key, 50)
        item.setZValue(z_val)
        item.setPos(self._default_position_for_role(role, item.current_width, item.current_height))
        self.portrait_scene.clearSelection()
        item.setSelected(True)
        item.item_changed.connect(lambda: self.on_item_modified(item))
        if role:
            item.set_role(role)
            self.modified_roles.add(role)

    def _default_position_for_role(self, role, width, height):
        padding = 20
        safe_left = padding
        safe_right = 1080 - width - padding
        safe_top = padding + 150
        safe_bottom = 1770 - height - padding
        role_lower = role.lower()
        if "loot" in role_lower: return QPointF(safe_right, safe_bottom)
        elif "health" in role_lower or "hp" in role_lower: return QPointF(safe_left, safe_bottom)
        elif "map" in role_lower or "stats" in role_lower: return QPointF(safe_right, safe_top)
        elif "teammates" in role_lower: return QPointF(safe_left, safe_top)
        else: return QPointF((1080 - width) / 2, ((1920 - 150) - height) / 2 + 150)

    def on_item_modified(self, item):
        if item.assigned_role: self.modified_roles.add(item.assigned_role)

    def delete_selected(self):
        selected_items = self.portrait_scene.selectedItems()
        if not selected_items: return
        for item in selected_items:
            if hasattr(item, 'assigned_role') and item.assigned_role:
                 self.modified_roles.add(item.assigned_role)
            self.portrait_scene.removeItem(item)
        self.on_selection_changed()

    def on_done_clicked(self):
        tech_key_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
        items_to_save = [item for item in self.portrait_scene.items() if isinstance(item, ResizablePixmapItem)]
        QMessageBox.information(self, "Save", "Configuration saved (simulated).")
        if hasattr(self, 'done_organizing'):
            self.done_organizing.emit()

    def set_background_image(self, full_pixmap):
        if full_pixmap.isNull(): return
        content_w, content_h = 1080, 1620
        scaled_pix = full_pixmap.scaled(content_w, content_h, Qt.KeepAspectRatioByExpanding, Qt.SmoothTransformation)
        crop_x = (scaled_pix.width() - content_w) // 2
        crop_y = (scaled_pix.height() - content_h) // 2
        final_bg = scaled_pix.copy(crop_x, crop_y, content_w, content_h)
        dimmed_pix = QPixmap(final_bg.size())
        dimmed_pix.fill(Qt.transparent)
        painter = QPainter(dimmed_pix)
        painter.drawPixmap(0, 0, final_bg)
        painter.fillRect(dimmed_pix.rect(), QColor(0, 0, 0, 120))
        painter.end()
        if self.background_item:
            self.background_item.setPixmap(dimmed_pix)
        else:
            self.background_item = QGraphicsPixmapItem(dimmed_pix)
            self.background_item.setZValue(-80)
            self.portrait_scene.addItem(self.background_item)
        self.background_item.setPos(0, 150)

    def register_undo_action(self, description, undo_func, redo_func):
        self.state_manager.add_undo_action("portrait_edit", description, undo_func, redo_func)
        self.update_undo_redo_buttons()

    def undo(self):
        if self.state_manager.undo(): self.status_label.setText("Undo performed")
        self.update_undo_redo_buttons()

    def redo(self):
        if self.state_manager.redo(): self.status_label.setText("Redo performed")
        self.update_undo_redo_buttons()
        
    def update_undo_redo_buttons(self):
        self.undo_button.setEnabled(self.state_manager.can_undo())
        self.redo_button.setEnabled(self.state_manager.can_redo())
        undo_desc = self.state_manager.get_undo_description()
        redo_desc = self.state_manager.get_redo_description()
        self.undo_button.setToolTip(f"Undo: {undo_desc}" if undo_desc else "Nothing to undo")
        self.redo_button.setToolTip(f"Redo: {redo_desc}" if redo_desc else "Nothing to redo")

    def showEvent(self, event):
        super().showEvent(event)
        self.portrait_view.fit_to_scene()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.portrait_view.fit_to_scene()

    def closeEvent(self, event):
        super().closeEvent(event)

    def _deferred_launch_main_app(self):
        QApplication.instance().quit()

def main():
    try:
        enhanced_logger_instance = setup_logger()
        logger = enhanced_logger_instance.base_logger
        logger.info("Application starting...")
        app = QApplication(sys.argv)
        file_path = sys.argv[1] if len(sys.argv) > 1 else None
        player = CropApp(logger, enhanced_logger_instance, file_path=file_path)
        player.show()
        sys.exit(app.exec_())
    except Exception as e:
        print(f"Caught unhandled exception in main: {e}")
        traceback.print_exc()
    finally:
        print("--- Crop Tools main() finished ---")
if __name__ == '__main__':
    main()
