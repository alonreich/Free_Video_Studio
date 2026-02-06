import sys
import os
import json
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
from PyQt5.QtCore import QParallelAnimationGroup, QSequentialAnimationGroup
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
from coordinate_math import transform_to_content_area

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
                item_label = QLabel(f"  • {item}")
                item_label.setStyleSheet(f"color: {UI_COLORS.SUCCESS};")
                content_layout.addWidget(item_label)
        if configured and unchanged:
            content_layout.addSpacing(60)
        if unchanged:
            unchanged_label = QLabel("Unchanged Elements:")
            unchanged_label.setObjectName("unchanged")
            content_layout.addWidget(unchanged_label)
            for item in unchanged:
                item_label = QLabel(f"  • {item}")
                item_label.setStyleSheet(f"color: {UI_COLORS.DANGER};")
                content_layout.addWidget(item_label)

class CropApp(KeyboardShortcutMixin, PersistentWindowMixin, QWidget, CropAppHandlers):
    done_organizing = pyqtSignal()

    def __init__(self, logger_instance, enhanced_logger_instance, file_path=None):
        super().__init__()
        self.logger = logger_instance
        self.enhanced_logger = enhanced_logger_instance
        self.base_title = "Fortnite Crop Tool"
        self.script_dir = os.path.dirname(os.path.abspath(__file__))
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, '..'))
        self.hud_config_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
        self.app_config_path = os.path.join(self.base_dir, 'config', 'crop_tools.conf')
        self.config_manager = get_config_manager(self.hud_config_path, self.logger)
        self.state_manager = get_state_manager(self.logger)
        self.last_dir = None
        self.bin_dir = os.path.abspath(os.path.join(self.base_dir, 'binaries'))
        self.snapshot_path = None
        self.media_processor = MediaProcessor(self.bin_dir)
        self.background_crop_width = 0
        self.modified_roles = set()
        self.placeholders_group = []
        self.background_item = None
        self.snapshot_resolution = None
        self.background_dim_alpha = 90
        self._dirty = False
        self._loaded_file_path = None
        self._item_edit_cache = {}
        self._item_edit_timers = {}
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
            config_path=self.app_config_path,
            settings_key='window_geometry',
            default_geo={'x': 50, 'y': 50, 'w': 1820, 'h': 900},
            title_info_provider=self.get_title_info,
            extra_data_provider=self._get_persistence_extras
        )
        self._setup_portrait_editor()
        self._init_upload_hint_blink()
        if file_path and os.path.exists(file_path):
            self.load_file(file_path)

    def connect_signals(self):
        super().connect_signals()
        self.done_button.clicked.connect(self.on_done_clicked)
        self.delete_button.clicked.connect(self.delete_selected)
        self.undo_button.clicked.connect(self.undo)
        self.redo_button.clicked.connect(self.redo)
        if hasattr(self, 'snap_toggle_button'):
            self.snap_toggle_button.clicked.connect(self._on_snap_toggle)
        self.show_placeholders_checkbox.stateChanged.connect(self.toggle_placeholders)
        self.portrait_scene.selectionChanged.connect(self.on_selection_changed)
        self.done_button.setToolTip(
            "Save HUD positions (adds * when unsaved). Crops are saved with outward rounding."
        )
        self.delete_button.setToolTip("Delete selected item(s) (Del)")
        self.undo_button.setToolTip("Undo last change (Ctrl+Z)")
        self.redo_button.setToolTip("Redo last undone change (Ctrl+Y)")
        self.show_placeholders_checkbox.setToolTip("Toggle previously saved HUD placeholders")
        if hasattr(self, 'snap_toggle_button'):
            self.snap_toggle_button.setToolTip("Toggle snapping to guides")

    def _setup_portrait_editor(self):
        """Initializes the integrated portrait editor components."""
        canvas_item = self.portrait_scene.addRect(
            0,
            0,
            UI_LAYOUT.PORTRAIT_BASE_WIDTH,
            UI_LAYOUT.PORTRAIT_BASE_HEIGHT,
            QPen(Qt.NoPen),
            QBrush(QColor("#1e1e1e"))
        )
        canvas_item.setZValue(-100)
        limit_pen = QPen(QColor("#34495e"), 6, Qt.SolidLine)
        border = self.portrait_scene.addRect(0, 0, UI_LAYOUT.PORTRAIT_BASE_WIDTH, UI_LAYOUT.PORTRAIT_BASE_HEIGHT, limit_pen)
        border.setZValue(102)
        top_bar_rect = self.portrait_scene.addRect(
            0,
            0,
            UI_LAYOUT.PORTRAIT_BASE_WIDTH,
            UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT,
            QPen(Qt.NoPen),
            QBrush(QColor("black"))
        )
        top_bar_rect.setZValue(100)
        bottom_bar_rect = self.portrait_scene.addRect(
            0,
            UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING,
            UI_LAYOUT.PORTRAIT_BASE_WIDTH,
            UI_LAYOUT.PORTRAIT_BOTTOM_PADDING,
            QPen(Qt.NoPen),
            QBrush(QColor("black"))
        )
        bottom_bar_rect.setZValue(100)
        self.load_existing_placeholders()
        self.update_undo_redo_buttons()
        self.on_selection_changed()
        self._refresh_done_button()
        self.portrait_view.setToolTip("Mouse wheel to zoom, middle drag to pan, Shift+Arrows to reorder")

    def _init_upload_hint_blink(self):
        if not hasattr(self, 'upload_hint_container'):
            return
        self.upload_hint_container.setVisible(True)
        self._upload_hint_visible = True
        self._upload_hint_timer = QTimer(self)
        self._upload_hint_timer.setInterval(UI_BEHAVIOR.VIDEO_VIEW_GUIDANCE_BLINK_INTERVAL)
        self._upload_hint_timer.timeout.connect(self._toggle_upload_hint)
        self._upload_hint_timer.start()

    def _toggle_upload_hint(self):
        if not hasattr(self, 'upload_hint_container'):
            return
        self._upload_hint_visible = not getattr(self, '_upload_hint_visible', True)
        self.upload_hint_container.setVisible(self._upload_hint_visible)

    def _set_upload_hint_active(self, active):
        if not hasattr(self, 'upload_hint_container'):
            return
        self.upload_hint_container.setVisible(active)
        if active:
            if hasattr(self, '_upload_hint_timer'):
                self._upload_hint_timer.start()
        else:
            if hasattr(self, '_upload_hint_timer'):
                self._upload_hint_timer.stop()

    def keyPressEvent(self, event):
        key = event.key()
        if key == Qt.Key_F12:
            if self._confirm_discard_changes():
                self._deferred_launch_main_app()
            event.accept()
            return
        if key == Qt.Key_B:
            self.background_dim_alpha = 0 if self.background_dim_alpha > 0 else 90
            if self.snapshot_path:
                self.set_background_image(QPixmap(self.snapshot_path))
            event.accept()
            return
        if key == Qt.Key_Delete:
            if self.view_stack.currentWidget() == self.draw_scroll_area:
                self.draw_widget.clear_selection()
                self.status_label.setText("Selection cleared")
            else:
                self.delete_selected()
            event.accept()
            return
        if self.view_stack.currentWidget() == self.draw_scroll_area:
            self.draw_widget.handle_key_press(event)
            if event.isAccepted():
                self.status_label.setText("Selection nudged")
                return
        if self.view_stack.currentWidget() == self.portrait_frame:
            self.portrait_view.keyPressEvent(event)
            return
        super(CropApp, self).keyPressEvent(event)

    def toggle_placeholders(self):
        is_visible = self.show_placeholders_checkbox.isChecked()
        if self.enhanced_logger:
            self.enhanced_logger.log_user_action("Toggle Placeholders", f"Visible: {is_visible}")
        for item in self.placeholders_group:
            item.setVisible(is_visible)
        config = self.config_manager.load_config()
        configured = self.config_manager.get_configured_elements()
        hp_status = ""
        if "boss_hp" in configured:
            hp_status = " | Active HP: BOSS"
        elif "normal_hp" in configured:
            hp_status = " | Active HP: NORMAL"
        else:
            hp_status = " | Active HP: NONE"
        self.status_label.setText(f"{'Showing existing crops' if is_visible else 'Existing crops hidden'}{hp_status}")

    def _on_snap_toggle(self):
        if not hasattr(self, 'portrait_view'):
            return
        enabled = self.snap_toggle_button.isChecked()
        if self.enhanced_logger:
            self.enhanced_logger.log_button_click("Snap Toggle", f"Enabled: {enabled}")
        if hasattr(self.portrait_view, 'set_snap_enabled'):
            self.portrait_view.set_snap_enabled(enabled)
        self.status_label.setText("Snapping enabled" if enabled else "Snapping disabled (guides only)")

    def load_existing_placeholders(self):
        for item in self.placeholders_group:
            self.portrait_scene.removeItem(item)
        self.placeholders_group.clear()
        try:
            config = self.config_manager.load_config()
            configured = self.config_manager.get_configured_elements()
            for tech_key in configured:
                scale = config.get('scales', {}).get(tech_key, 1.0)
                if scale <= 0.001: continue
                overlay = config.get('overlays', {}).get(tech_key)
                crop = config.get('crops_1080p', {}).get(tech_key)
                if overlay and crop and len(crop) >= 2:
                    w_ui = crop[0] * scale
                    h_ui = crop[1] * scale
                    x_ui = overlay.get('x', 0)
                    y_ui = overlay.get('y', 0)
                    rect_item = QGraphicsRectItem(x_ui, y_ui, w_ui, h_ui)
                    brush_color = QColor("#22c55e")
                    brush_color.setAlpha(80)
                    rect_item.setBrush(QBrush(brush_color))
                    rect_item.setPen(QPen(QColor("#4ade80"), 2, Qt.DashLine))
                    z_val = config.get("z_orders", {}).get(tech_key, Z_ORDER_MAP.get(tech_key, 10))
                    rect_item.setZValue(z_val - 5)
                    display_name = HUD_ELEMENT_MAPPINGS.get(tech_key, tech_key).upper()
                    text_item = QGraphicsSimpleTextItem(display_name, rect_item)
                    text_item.setBrush(QBrush(QColor("white")))
                    font = QFont("Arial", 9, QFont.Bold)
                    text_item.setFont(font)
                    t_rect = text_item.boundingRect()
                    text_item.setPos((w_ui - t_rect.width())/2, (h_ui - t_rect.height())/2)
                    self.portrait_scene.addItem(rect_item)
                    self.placeholders_group.append(rect_item)
        except Exception as e:
            self.logger.error(f"Error loading placeholders: {e}")
        self.toggle_placeholders()

    def on_selection_changed(self):
        selected = self.portrait_scene.selectedItems()
        self.delete_button.setEnabled(bool(selected))
        if selected:
            self.status_label.setText(f"{len(selected)} item(s) selected")

    def add_scissored_item(self, pixmap, crop_rect, background_crop_width, role=None):
        for existing_item in self.portrait_scene.items():
            if isinstance(existing_item, ResizablePixmapItem) and existing_item.assigned_role == role:
                self.portrait_scene.removeItem(existing_item)
        for ph in list(self.placeholders_group):
            for child in ph.childItems():
                if isinstance(child, QGraphicsSimpleTextItem) and child.text() == role.upper():
                    self.portrait_scene.removeItem(ph)
                    self.placeholders_group.remove(ph)
                    break
        item = ResizablePixmapItem(pixmap, crop_rect)
        original_resolution = (
            self.media_processor.original_resolution
            or self.snapshot_resolution
            or "1920x1080"
        )
        fx, fy, fw, fh = transform_to_content_area(
            (crop_rect.x(), crop_rect.y(), crop_rect.width(), crop_rect.height()),
            original_resolution
        )
        w_ui = max(20.0, fw)
        h_ui = max(20.0, fh)
        item.current_width = w_ui
        item.current_height = h_ui
        item.update_handle_positions()
        self.portrait_scene.addItem(item)
        tech_key = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}.get(role, 'unknown')
        config = self.config_manager.load_config()
        z_val = config.get("z_orders", {}).get(tech_key, Z_ORDER_MAP.get(tech_key, 50))
        item.setZValue(z_val)
        item.setPos(self._default_position_for_role(role, item.current_width, item.current_height))
        self.portrait_scene.clearSelection()
        item.setSelected(True)
        item.item_changed.connect(lambda: self._handle_item_changed(item))
        item.setOpacity(1.0)
        item.setToolTip(role or "HUD element")
        if role:
            item.set_role(role)
            self.modified_roles.add(role)
        if self.enhanced_logger:
            scale_factor = item.current_width / max(1.0, w_ui)
            self.enhanced_logger.log_item_added(role or "Unknown", item.scenePos().toPoint(), (item.current_width, item.current_height), original_resolution, scale_factor)
        self._mark_dirty()

    def _default_position_for_role(self, role, width, height):
        padding = 20
        safe_left = padding
        safe_right = UI_LAYOUT.PORTRAIT_BASE_WIDTH - width - padding
        safe_top = padding + UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT
        safe_bottom = (UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT) - height - padding
        role_lower = role.lower()
        if "loot" in role_lower: return QPointF(safe_right, safe_bottom)
        elif "health" in role_lower or "hp" in role_lower: return QPointF(safe_left, safe_bottom)
        elif "map" in role_lower or "stats" in role_lower: return QPointF(safe_right, safe_top)
        elif "teammates" in role_lower: return QPointF(safe_left, safe_top)
        else:
            return QPointF(
                (UI_LAYOUT.PORTRAIT_BASE_WIDTH - width) / 2,
                ((UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT) - height) / 2 + UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT
            )

    def on_item_modified(self, item):
        if item.assigned_role:
            self.modified_roles.add(item.assigned_role)
        if self.enhanced_logger and item.assigned_role:
            placement = self.enhanced_logger.get_corner_placement(item.sceneBoundingRect(), self.portrait_scene.sceneRect())
            self.enhanced_logger.log_portrait_placement(item.assigned_role, item.scenePos().toPoint(), (item.current_width, item.current_height), placement)
        self._mark_dirty()

    def _handle_item_changed(self, item):
        item_id = id(item)
        if item_id not in self._item_edit_cache:
            self._item_edit_cache[item_id] = self._get_item_state(item)
        if item_id not in self._item_edit_timers:
            timer = QTimer(self)
            timer.setSingleShot(True)
            timer.timeout.connect(lambda it=item: self._commit_item_change(it))
            self._item_edit_timers[item_id] = timer
        self._item_edit_timers[item_id].start(250)
        self.on_item_modified(item)

    def _commit_item_change(self, item):
        item_id = id(item)
        start_state = self._item_edit_cache.get(item_id)
        end_state = self._get_item_state(item)
        if start_state and start_state != end_state:
            description = f"Move/Resize {item.assigned_role or 'Item'}"
            self.register_undo_action(
                description,
                undo_func=lambda it=item, state=start_state: self._apply_item_state(it, state),
                redo_func=lambda it=item, state=end_state: self._apply_item_state(it, state)
            )
        self._item_edit_cache[item_id] = end_state

    def _get_item_state(self, item):
        pos = item.scenePos()
        return {
            "x": float(pos.x()),
            "y": float(pos.y()),
            "width": float(item.current_width),
            "height": float(item.current_height),
            "z": item.zValue()
        }

    def _apply_item_state(self, item, state):
        if not state:
            return False
        item.prepareGeometryChange()
        item.current_width = state["width"]
        item.current_height = state["height"]
        item.update_handle_positions()
        item.setPos(state["x"], state["y"])
        if "z" in state:
            item.setZValue(state["z"])
        item.update()
        self._mark_dirty()
        return True

    def delete_selected(self):
        selected_items = self.portrait_scene.selectedItems()
        if not selected_items: return
        if self.enhanced_logger:
            self.enhanced_logger.log_button_click("Delete Selected", f"Count: {len(selected_items)}")
        items_data = []
        for item in selected_items:
            if isinstance(item, ResizablePixmapItem):
                state = self._get_item_state(item)
                items_data.append({
                    'item': item,
                    'state': state,
                    'scene': self.portrait_scene,
                    'role': item.assigned_role
                })
        
        def undo_delete(data_list):
            if self.enhanced_logger:
                self.enhanced_logger.log_user_action("Undo Delete", f"Restoring {len(data_list)} items")
            for data in data_list:
                item = data['item']
                if item.scene() != data['scene']:
                    data['scene'].addItem(item)
                self._apply_item_state(item, data['state'])
                if data['role']:
                    self.modified_roles.add(data['role'])
            self._mark_dirty()
            return True

        def redo_delete(data_list):
            if self.enhanced_logger:
                self.enhanced_logger.log_user_action("Redo Delete", f"Removing {len(data_list)} items")
            for data in data_list:
                item = data['item']
                if item.scene():
                    item.scene().removeItem(item)
                if data['role']:
                    self.modified_roles.add(data['role'])
            self._mark_dirty()
            return True
        self.register_undo_action(
            f"Delete {len(items_data)} item(s)",
            undo_func=lambda d=items_data: undo_delete(d),
            redo_func=lambda d=items_data: redo_delete(d)
        )
        for item in selected_items:
            if hasattr(item, 'assigned_role') and item.assigned_role:
                 self.modified_roles.add(item.assigned_role)
            self.portrait_scene.removeItem(item)
        self._mark_dirty()
        self.on_selection_changed()

    def on_done_clicked(self):
        if self.enhanced_logger:
            self.enhanced_logger.log_button_click("FINISH & SAVE")
        tech_key_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
        items_to_save = [item for item in self.portrait_scene.items() if isinstance(item, ResizablePixmapItem)]
        if not items_to_save:
            QMessageBox.warning(self, "Save", "No HUD elements found to save.")
            return
        original_resolution = self.media_processor.original_resolution or "1920x1080"
        config = self.config_manager.load_config()
        if self.enhanced_logger:
            self.enhanced_logger.log_user_action("Save Configuration - Starting Update")
        configured = []
        unchanged = []
        overlap_pairs = self._detect_overlaps(items_to_save)
        if overlap_pairs:
            QMessageBox.warning(self, "Overlap Warning", "Some HUD elements overlap. Consider adjusting them for clarity.")
        saved_keys = set()
        items_to_save.sort(key=lambda i: i.zValue())
        for item in items_to_save:
            role = item.assigned_role
            if not role:
                continue
            tech_key = tech_key_map.get(role, "unknown")
            if tech_key == "unknown":
                continue
            rect = item.crop_rect
            transformed = self.config_manager.transform_crop_rect(rect, original_resolution)
            x_content, y_content, w_content, h_content = transformed
            normalized_rect = [w_content, h_content, x_content, y_content]
            scale = max(0.001, round(item.current_width / max(1.0, w_content), 4))
            overlay_x = int(round(item.scenePos().x()))
            overlay_y = int(round(item.scenePos().y()))
            z_val = int(item.zValue())
            updated = False
            if config["crops_1080p"].get(tech_key) != normalized_rect:
                config["crops_1080p"][tech_key] = normalized_rect
                updated = True
            if config["scales"].get(tech_key) != scale:
                config["scales"][tech_key] = scale
                updated = True
            if config["overlays"].get(tech_key) != {"x": overlay_x, "y": overlay_y}:
                config["overlays"][tech_key] = {"x": overlay_x, "y": overlay_y}
                updated = True
            if config["z_orders"].get(tech_key) != z_val:
                config["z_orders"][tech_key] = z_val
                updated = True
            if updated:
                configured.append(HUD_ELEMENT_MAPPINGS.get(tech_key, tech_key))
            else:
                unchanged.append(HUD_ELEMENT_MAPPINGS.get(tech_key, tech_key))
            saved_keys.add(tech_key)
        for role in self.modified_roles:
            tech_key = tech_key_map.get(role)
            if tech_key and tech_key not in saved_keys:
                self.config_manager.delete_crop_coordinates(tech_key)
        issues = self.config_manager.validate_config()
        if issues:
            QMessageBox.warning(self, "Config Validation", "\n".join(issues[:8]))
        temp_file_before = os.path.join(tempfile.gettempdir(), "config_before.json")
        temp_file_after = os.path.join(tempfile.gettempdir(), "config_after.json")
        try:
            with open(temp_file_before, 'w') as f:
                json.dump(self.config_manager.load_config(), f)
            success = self.config_manager.save_config(config)
            if success and self.enhanced_logger:
                with open(temp_file_after, 'w') as f:
                    json.dump(config, f)
                self.enhanced_logger.log_finished_button_click(temp_file_before, temp_file_after)
        except Exception as log_err:
            self.logger.error(f"Logging error during save: {log_err}")
            success = self.config_manager.save_config(config)
        if not success:
            QMessageBox.critical(self, "Save", "Failed to save configuration. Please check logs.")
            return
        summary_toast = SummaryToast(configured, unchanged, self)
        summary_toast.show()
        summary_toast.raise_()
        summary_toast.activateWindow()
        self._start_exit_sequence(summary_toast)
        self._dirty = False
        self._refresh_done_button()
        self.status_label.setText("Configuration saved (outward-rounded crops)")
        self.modified_roles.clear()
        if hasattr(self, 'done_organizing'):
            self.done_organizing.emit()

    def _build_opacity_anim(self, widget, start, end, duration_ms):
        anim = QPropertyAnimation(widget, b"windowOpacity")
        anim.setStartValue(start)
        anim.setEndValue(end)
        anim.setDuration(duration_ms)
        anim.setEasingCurve(QEasingCurve.InOutQuad)
        return anim

    def _start_exit_sequence(self, summary_dialog):
        widgets = [w for w in (self, summary_dialog) if w]
        if hasattr(self, '_exit_animation') and self._exit_animation:
            self._exit_animation.stop()
        blink_cycle_ms = 250
        blink_cycles = 4
        sequence = QSequentialAnimationGroup(self)
        for _ in range(blink_cycles):
            fade_out = QParallelAnimationGroup(sequence)
            fade_in = QParallelAnimationGroup(sequence)
            for widget in widgets:
                fade_out.addAnimation(self._build_opacity_anim(widget, 1.0, 0.3, blink_cycle_ms))
                fade_in.addAnimation(self._build_opacity_anim(widget, 0.3, 1.0, blink_cycle_ms))
            sequence.addAnimation(fade_out)
            sequence.addAnimation(fade_in)
        fade_out_all = QParallelAnimationGroup(sequence)
        for widget in widgets:
            fade_out_all.addAnimation(self._build_opacity_anim(widget, 1.0, 0.0, 800))
        sequence.addAnimation(fade_out_all)

        def finalize_exit():
            for widget in widgets:
                widget.setWindowOpacity(1.0)
            if summary_dialog:
                summary_dialog.close()
            self.close()
            QApplication.instance().quit()
        sequence.finished.connect(finalize_exit)
        self._exit_animation = sequence
        sequence.start()

    def set_background_image(self, full_pixmap):
        if full_pixmap.isNull(): return
        content_w = UI_LAYOUT.PORTRAIT_BASE_WIDTH
        content_h = UI_LAYOUT.PORTRAIT_BASE_HEIGHT - UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT - UI_LAYOUT.PORTRAIT_BOTTOM_PADDING
        scaled_pix = full_pixmap.scaled(content_w, content_h, Qt.KeepAspectRatioByExpanding, Qt.SmoothTransformation)
        crop_x = (scaled_pix.width() - content_w) // 2
        crop_y = (scaled_pix.height() - content_h) // 2
        final_bg = scaled_pix.copy(crop_x, crop_y, content_w, content_h)
        dimmed_pix = QPixmap(final_bg.size())
        dimmed_pix.fill(Qt.transparent)
        painter = QPainter(dimmed_pix)
        painter.drawPixmap(0, 0, final_bg)
        painter.fillRect(dimmed_pix.rect(), QColor(0, 0, 0, self.background_dim_alpha))
        painter.end()
        try:
            if self.background_item and self.background_item.scene():
                self.background_item.setPixmap(dimmed_pix)
            else:
                self.background_item = QGraphicsPixmapItem(dimmed_pix)
                self.background_item.setZValue(-80)
                self.portrait_scene.addItem(self.background_item)
        except (RuntimeError, AttributeError):
            self.background_item = QGraphicsPixmapItem(dimmed_pix)
            self.background_item.setZValue(-80)
            self.portrait_scene.addItem(self.background_item)
        self.background_item.setPos(0, UI_LAYOUT.PORTRAIT_TOP_BAR_HEIGHT)

    def register_undo_action(self, description, undo_func, redo_func):
        self.state_manager.add_undo_action("portrait_edit", description, undo_func, redo_func)
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
        if self._confirm_discard_changes():
            super().closeEvent(event)
        else:
            event.ignore()

    def _deferred_launch_main_app(self):
        QApplication.instance().quit()

    def _get_persistence_extras(self):
        if self.last_dir:
            return {"last_directory": self.last_dir}
        return {}

    def _mark_dirty(self):
        self._dirty = True
        self._refresh_done_button()

    def _refresh_done_button(self):
        has_items = any(isinstance(item, ResizablePixmapItem) for item in self.portrait_scene.items())
        self.done_button.setEnabled(has_items)
        if self._dirty:
            self.done_button.setText("FINISH & SAVE *")
        else:
            self.done_button.setText("FINISH & SAVE")

    def _confirm_discard_changes(self):
        if not self._dirty:
            return True
        reply = QMessageBox.question(
            self,
            "Unsaved Changes",
            "You have unsaved changes. Do you want to discard them?",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No
        )
        return reply == QMessageBox.Yes

    def _detect_overlaps(self, items):
        overlaps = []
        for i, item in enumerate(items):
            rect_i = item.mapToScene(QRectF(0, 0, item.current_width, item.current_height)).boundingRect()
            for other in items[i + 1:]:
                rect_other = other.mapToScene(QRectF(0, 0, other.current_width, other.current_height)).boundingRect()
                if rect_i.intersects(rect_other):
                    overlaps.append((item, other))
        return overlaps

    def undo(self):
        if self.enhanced_logger:
            self.enhanced_logger.log_button_click("Undo", self.state_manager.get_undo_description())
        if self.state_manager.undo():
            self._mark_dirty()
            self.status_label.setText("Undo performed")
        self.update_undo_redo_buttons()

    def redo(self):
        if self.enhanced_logger:
            self.enhanced_logger.log_button_click("Redo", self.state_manager.get_redo_description())
        if self.state_manager.redo():
            self._mark_dirty()
            self.status_label.setText("Redo performed")
        self.update_undo_redo_buttons()

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
