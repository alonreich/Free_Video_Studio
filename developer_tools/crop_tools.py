import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')

from PyQt5.QtCore import (
    Qt, QTimer, QPoint, QRectF, QPointF, pyqtSignal, QObject, QThread,
    QPropertyAnimation, QEasingCurve, QParallelAnimationGroup, QSequentialAnimationGroup
)

from PyQt5.QtGui import QPainter, QColor, QFont, QBrush, QPixmap, QPen, QPolygon, QKeySequence, QFontMetrics
from PyQt5.QtWidgets import (
    QApplication, QWidget, QGraphicsScene, QGraphicsPixmapItem, QGraphicsRectItem, 
    QGraphicsSimpleTextItem, QDialog, QFrame, QVBoxLayout, QLabel, QHBoxLayout, QMessageBox,
    QProgressDialog, QShortcut, QPushButton, QScrollArea, QGraphicsOpacityEffect, QStyle, QListWidgetItem
)
project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
if project_root not in sys.path:
    sys.path.insert(0, project_root)

from system.utils import ConsoleManager, ProcessManager, LogManager, DependencyDoctor
logger_initial = ConsoleManager.initialize(project_root, "crop_tools.log", "Crop_Tool")

import json
import traceback
import time
import math
import tempfile
import subprocess
from utils import PersistentWindowMixin, cleanup_temp_snapshots, cleanup_old_backups
from Keyboard_Mixing import KeyboardShortcutMixin
from media_processor import MediaProcessor
from ui_setup import Ui_CropApp
from app_handlers import CropAppHandlers
from config import HUD_ELEMENT_MAPPINGS, Z_ORDER_MAP, UI_COLORS, UI_LAYOUT, UI_BEHAVIOR, HUD_SAFE_PADDING, get_tech_key_from_role
from logger_setup import setup_logger
from enhanced_logger import get_enhanced_logger
from config_manager import get_config_manager
from state_manager import get_state_manager
from graphics_items import ResizablePixmapItem
from coordinate_math import (
    transform_to_content_area, transform_to_content_area_int, inverse_transform_from_content_area_int, get_resolution_ints, outward_round_rect, scale_round,
    PORTRAIT_W, PORTRAIT_H, UI_PADDING_TOP, UI_PADDING_BOTTOM, UI_CONTENT_H
)

from resource_manager import get_resource_manager
from system.state_transfer import StateTransfer

class AnimatedCheckmark(QWidget):
    def __init__(self, size=64, parent=None):
        super().__init__(parent)
        self.setFixedSize(size, size)
        self.scale_val = 0.0
        self.opacity_val = 0.0
        self._anim_group = QParallelAnimationGroup(self)
        scale_anim = QPropertyAnimation(self, b"scale_prop")
        scale_anim.setDuration(600)
        scale_anim.setStartValue(0.0)
        scale_anim.setEndValue(1.0)
        scale_anim.setEasingCurve(QEasingCurve.OutBack)
        opacity_anim = QPropertyAnimation(self, b"opacity_prop")
        opacity_anim.setDuration(400)
        opacity_anim.setStartValue(0.0)
        opacity_anim.setEndValue(1.0)
        self._anim_group.addAnimation(scale_anim)
        self._anim_group.addAnimation(opacity_anim)

    def get_scale(self): return self.scale_val

    def set_scale(self, s): self.scale_val = s; self.update()
    scale_prop = property(get_scale, set_scale)

    def get_opacity(self): return self.opacity_val

    def set_opacity(self, o): self.opacity_val = o; self.update()
    opacity_prop = property(get_opacity, set_opacity)

    def start(self):
        self._anim_group.start()

    def paintEvent(self, event):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.setOpacity(self.opacity_val)
        p.translate(self.width()/2, self.height()/2)
        p.scale(self.scale_val, self.scale_val)
        p.translate(-self.width()/2, -self.height()/2)
        p.setPen(Qt.NoPen)
        p.setBrush(QColor(UI_COLORS.SUCCESS))
        p.drawEllipse(2, 2, self.width()-4, self.height()-4)
        pen = QPen(QColor("white"), 4, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin)
        p.setPen(pen)
        w, h = self.width(), self.height()
        path = QPolygon([
            QPoint(int(w * 0.25), int(h * 0.5)),
            QPoint(int(w * 0.45), int(h * 0.73)),
            QPoint(int(w * 0.75), int(h * 0.35))
        ])
        p.drawPolyline(path)

class SummaryToast(QDialog):
    def __init__(self, configured, unchanged, preview_pixmap=None, parent=None):
        super().__init__(parent)
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setFixedSize(650, 850)
        container = QFrame(self)
        container.setObjectName("container")
        container.setStyleSheet(f"""
            #container {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                border: 2px solid {UI_COLORS.PRIMARY};
                border-radius: 20px;
            }}
            QLabel {{ background: transparent; color: {UI_COLORS.TEXT_PRIMARY}; }}
            #mainTitle {{
                font-size: 28px;
                font-weight: 800;
                color: {UI_COLORS.TEXT_PRIMARY};
            }}
        """)
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.addWidget(container)
        content_layout = QVBoxLayout(container)
        content_layout.setContentsMargins(40, 40, 40, 40)
        content_layout.setSpacing(25)
        header_layout = QHBoxLayout()
        self.checkmark = AnimatedCheckmark(80)
        header_layout.addWidget(self.checkmark)
        title_vbox = QVBoxLayout()
        title_label = QLabel("SUCCESS")
        title_label.setObjectName("mainTitle")
        subtitle = QLabel("Configuration deployed safely.")
        subtitle.setStyleSheet(f"color: {UI_COLORS.TEXT_DISABLED}; font-size: 16px;")
        title_vbox.addWidget(title_label)
        title_vbox.addWidget(subtitle)
        header_layout.addLayout(title_vbox)
        header_layout.addStretch()
        content_layout.addLayout(header_layout)
        if preview_pixmap and not preview_pixmap.isNull():
            preview_frame = QFrame()
            preview_frame.setFixedSize(570, 320)
            preview_frame.setStyleSheet(f"background: #000; border: 1px solid {UI_COLORS.BORDER_MEDIUM}; border-radius: 10px;")
            p_layout = QVBoxLayout(preview_frame)
            p_layout.setContentsMargins(6,6,6,6)
            img_label = QLabel()
            img_label.setPixmap(preview_pixmap.scaled(558, 308, Qt.KeepAspectRatio, Qt.SmoothTransformation))
            img_label.setAlignment(Qt.AlignCenter)
            p_layout.addWidget(img_label)
            content_layout.addWidget(preview_frame, 0, Qt.AlignCenter)
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.NoFrame)
        scroll.setStyleSheet("background: transparent;")
        scroll_content = QWidget()
        list_layout = QVBoxLayout(scroll_content)
        list_layout.setContentsMargins(0, 5, 0, 5)
        list_layout.setSpacing(12)
        if configured:
            conf_header = QLabel("  MODIFIED ELEMENTS")
            conf_header.setStyleSheet(f"color: {UI_COLORS.SUCCESS}; font-weight: bold; font-size: 14px; background: rgba(16, 185, 129, 30); padding: 8px; border-radius: 4px;")
            list_layout.addWidget(conf_header)
            for item in sorted(configured):
                lbl = QLabel(f"  ✓  {item}")
                lbl.setStyleSheet(f"color: {UI_COLORS.TEXT_SECONDARY}; font-size: 16px;")
                list_layout.addWidget(lbl)
        if unchanged:
            list_layout.addSpacing(20)
            un_header = QLabel("  UNTOUCHED (DEFAULTS)")
            un_header.setStyleSheet(f"color: {UI_COLORS.TEXT_DISABLED}; font-weight: bold; font-size: 14px; background: rgba(156, 163, 175, 20); padding: 8px; border-radius: 4px;")
            list_layout.addWidget(un_header)
            for item in sorted(unchanged):
                lbl = QLabel(f"  •  {item}")
                lbl.setStyleSheet(f"color: {UI_COLORS.TEXT_DISABLED}; font-size: 15px;")
                list_layout.addWidget(lbl)
        scroll.setWidget(scroll_content)
        content_layout.addWidget(scroll)
        footer = QLabel("Automatically returning to main app...")
        footer.setAlignment(Qt.AlignCenter)
        footer.setStyleSheet(f"color: {UI_COLORS.PRIMARY}; font-size: 14px; font-weight: bold;")
        content_layout.addWidget(footer)
        QTimer.singleShot(200, self.checkmark.start)
        screen = QApplication.primaryScreen().availableGeometry()
        self.move(screen.center() - self.rect().center())

class GuidanceToast(QDialog):
    def __init__(self, message, parent=None):
        super().__init__(parent)
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setFixedWidth(600)
        container = QFrame(self)
        container.setObjectName("container")
        container.setStyleSheet(f"""
            #container {{
                background-color: {UI_COLORS.BACKGROUND_DARK};
                border: 3px solid {UI_COLORS.PRIMARY};
                border-radius: 25px;
            }}
        """)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.addWidget(container)
        content = QVBoxLayout(container)
        content.setContentsMargins(40, 40, 40, 40)
        lbl = QLabel(message)
        lbl.setWordWrap(True)
        lbl.setAlignment(Qt.AlignCenter)
        lbl.setStyleSheet(f"color: {UI_COLORS.TEXT_PRIMARY}; font-size: 18px; font-weight: 800; line-height: 140%;")
        content.addWidget(lbl)
        self.adjustSize()
        screen = QApplication.primaryScreen().availableGeometry()
        self.move(screen.center() - self.rect().center())

class ThinkingToast(QDialog):
    def __init__(self, parent=None, target_widget=None):
        super().__init__(parent)
        self.target_widget = target_widget or parent
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setFixedSize(400, 450)
        self.angle = 0
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.rotate)
        self.timer.start(50)
        layout = QVBoxLayout(self)
        layout.setAlignment(Qt.AlignCenter)
        layout.addSpacing(300)
        self.msg_label = QLabel("Please wait while processing")
        self.msg_label.setAlignment(Qt.AlignCenter)
        self.msg_label.setStyleSheet(f"""
            color: {UI_COLORS.TEXT_PRIMARY};
            font-size: 20px;
            font-weight: bold;
            background-color: rgba(15, 23, 42, 180);
            padding: 15px;
            border-radius: 10px;
        """)
        layout.addWidget(self.msg_label)

    def rotate(self):
        self.angle = (self.angle + 30) % 360
        self.update()

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        painter.setPen(Qt.NoPen)
        painter.setBrush(QColor(15, 23, 42, 200))
        painter.drawRoundedRect(50, 20, 300, 300, 30, 30)
        painter.translate(200, 170)
        painter.rotate(self.angle)
        pen = QPen(QColor(56, 189, 248), 12)
        pen.setCapStyle(Qt.RoundCap)
        painter.setPen(pen)
        for i in range(8):
            painter.rotate(45)
            painter.setOpacity(0.2 + (i * 0.1))
            painter.drawLine(0, -50, 0, -90)

    def showEvent(self, event):
        self.center_on_target()
        super().showEvent(event)

    def center_on_target(self):
        if not self.target_widget:
            return
        geo = self.target_widget.geometry()
        global_pos = self.target_widget.mapToGlobal(QPoint(0, 0))
        center_x = global_pos.x() + (geo.width() // 2)
        center_y = global_pos.y() + (geo.height() // 2)
        self.move(center_x - (self.width() // 2), center_y - (self.height() // 2))

class SaveConfigWorker(QObject):
    finished = pyqtSignal(bool, list, list, str)

    def __init__(self, hud_config_path, item_payload, logger=None):
        super().__init__()
        self.hud_config_path = hud_config_path
        self.item_payload = item_payload or {}
        self.logger = logger

    def _create_rotation_backup(self):
        conf_path = self.hud_config_path
        if not os.path.exists(conf_path):
            return
        try:
            for i in range(4, 0, -1):
                old_b = f"{conf_path}.bak{i}"
                new_b = f"{conf_path}.bak{i+1}"
                if os.path.exists(old_b):
                    import shutil
                    shutil.move(old_b, new_b)

            import shutil
            shutil.copy2(conf_path, f"{conf_path}.bak1")
            if self.logger: self.logger.info(f"Rotation backup created: {conf_path}.bak1")
        except Exception as e:
            if self.logger: self.logger.error(f"Backup rotation failed: {e}")

    def run(self):
        try:
            manager = get_config_manager(self.hud_config_path, self.logger)
            config = manager.load_config()
            existing_before = set(config.get("crops_1080p", {}).keys())
            saved_keys = set()
            configured = []
            for tech_key, payload in self.item_payload.items():
                for section in ["crops_1080p", "scales", "overlays", "z_orders"]:
                    if section not in config or not isinstance(config[section], dict):
                        config[section] = {}
                config["crops_1080p"][tech_key] = payload["crop"]
                config["scales"][tech_key] = payload["scale"]
                config["overlays"][tech_key] = payload["overlay"]
                config["z_orders"][tech_key] = payload["z"]
                configured.append(payload["display"])
                saved_keys.add(tech_key)
            unchanged = [HUD_ELEMENT_MAPPINGS.get(k, k) for k in sorted(existing_before - saved_keys)]
            if manager.save_config(config):
                try: self._create_rotation_backup()
                except Exception as backup_err:
                    if self.logger: self.logger.error(f"Failed to create rotation backup: {backup_err}")
                self.finished.emit(True, configured, unchanged, "")
            else:
                self.finished.emit(False, [], [], "Failed to save config.")
        except Exception as e:
            self.finished.emit(False, [], [], str(e))

class CropApp(KeyboardShortcutMixin, PersistentWindowMixin, QWidget, CropAppHandlers):
    done_organizing = pyqtSignal()

    def __init__(self, logger_instance, enhanced_logger_instance, file_path=None):
        super().__init__()
        self.setAcceptDrops(True)
        self.logger = logger_instance
        self.enhanced_logger = enhanced_logger_instance
        self.base_title = f"Portrait {PORTRAIT_W}x{PORTRAIT_H} (Crop Tool)"
        self.script_dir = os.path.dirname(os.path.abspath(__file__))
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, '..'))
        self.hud_config_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
        self.app_config_path = os.path.join(self.base_dir, 'config', 'crop_tools.conf')
        processing_dir = os.path.dirname(self.hud_config_path)
        if not os.path.exists(processing_dir):
            try:
                os.makedirs(processing_dir, exist_ok=True)
                self.logger.info(f"Created missing processing directory: {processing_dir}")
            except Exception as e:
                self.logger.error(f"Failed to create processing directory: {e}")
                QMessageBox.critical(self, "System Error", f"Cannot create configuration folder at:\n{processing_dir}")
        self.config_manager = get_config_manager(self.hud_config_path, self.logger)
        self.state_manager = get_state_manager(self.logger)
        self.resource_manager = get_resource_manager(self.logger)
        self.resource_manager.setup_cleanup_timer(30000)
        self._check_dependencies()
        self._autosave_file = os.path.join(tempfile.gettempdir(), "fvs_autosave_recovery.json")
        self._autosave_timer = QTimer(self)
        self._autosave_timer.setInterval(5000)
        self._autosave_timer.timeout.connect(self._auto_save_state)
        self.last_dir = None
        self.bin_dir = os.path.abspath(os.path.join(self.base_dir, 'binaries'))
        self.snapshot_path = None
        self.ui = Ui_CropApp()
        try:
            self.ui.setupUi(self)
            self.view_stack = getattr(self, 'view_stack', None)
            self.draw_scroll_area = getattr(self, 'draw_scroll_area', None)
            self.draw_widget = getattr(self, 'draw_widget', None)
            self.position_slider = getattr(self, 'position_slider', None)
            self.play_pause_button = getattr(self, 'play_pause_button', None)
            self.open_button = getattr(self, 'open_button', None)
            self.snapshot_button = getattr(self, 'snapshot_button', None)
            self.magic_wand_button = getattr(self, 'magic_wand_button', None)
            self.reset_state_button = getattr(self, 'reset_state_button', None)
            self.return_button = getattr(self, 'return_button', None)
            self.current_time_label = getattr(self, 'current_time_label', None)
            self.total_time_label = getattr(self, 'total_time_label', None)
            self.goal_label = getattr(self, 'goal_label', None)
            self.status_label = getattr(self, 'status_label', None)
            self.snap_toggle_button = getattr(self, 'snap_toggle_button', None)
            self.show_placeholders_checkbox = getattr(self, 'show_placeholders_checkbox', None)
            self.done_button = getattr(self, 'done_button', None)
            self.delete_button = getattr(self, 'delete_button', None)
            self.undo_button = getattr(self, 'undo_button', None)
            self.redo_button = getattr(self, 'redo_button', None)
            self.transparency_slider = getattr(self, 'transparency_slider', None)
            self.portrait_scene = getattr(self, 'portrait_scene', None)
            self.portrait_view = getattr(self, 'portrait_view', None)
            self.layer_list = getattr(self, 'layer_list', None)
            self.media_processor = MediaProcessor(self.bin_dir, wid=None)
            if hasattr(self, 'video_surface') and self.video_surface:
                self.video_surface.paintEvent = lambda event: None
        except Exception as e:
            logger_initial.critical(f"SetupUI Failed: {e}", exc_info=True)
            raise e
        self.background_crop_width = 0
        self.modified_roles = set()
        self.placeholders_group = []
        self.background_item = None
        self.snapshot_resolution = None
        self.background_dim_alpha = UI_COLORS.OPACITY_DIM_LOW
        self._dirty = False
        self._suppress_undo_registration = False
        self._in_undo_redo = False
        self._item_edit_cache = {}
        self._item_edit_timers = {}
        self._save_thread = None
        self._save_worker = None
        self._save_progress_dialog = None
        self.connect_signals()
        if not self.media_processor.player:
            self.mpv_error_label.setVisible(True)
            if hasattr(self, 'mpv_error_label'): self.mpv_error_label.setText("⚠️ MPV ENGINE MISSING")
            if hasattr(self, 'upload_hint_label'): self.upload_hint_label.setVisible(False)
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Critical)
            msg.setWindowTitle("MPV Player Required")
            msg.setText("Video playback is disabled because MPV Media Player was not found.")
            msg.exec_()
        self.timer = QTimer(self)
        self.timer.setInterval(100)
        self.timer.timeout.connect(self.update_ui)
        self.timer.start()
        self.set_style()
        self.setFocusPolicy(Qt.StrongFocus)
        self.setFocus()
        self.play_pause_button.setFixedWidth(110)
        self.play_pause_button.setFixedHeight(29)
        self.play_pause_button.setCursor(Qt.PointingHandCursor)
        self.setup_persistence(
            config_path=self.app_config_path,
            settings_key='window_geometry',
            default_geo={'w': 1800, 'h': 930},
            title_info_provider=self.get_title_info,
            extra_data_provider=self._get_persistence_extras
        )
        settings = get_config_manager(self.app_config_path, self.logger).load_config()
        if hasattr(self, 'transparency_slider') and 'ghost_transparency' in settings:
            self.transparency_slider.setValue(settings['ghost_transparency'])
        self._setup_portrait_editor()
        if hasattr(self, 'slider_container'): self.slider_container.hide()
        if hasattr(self, 'play_pause_button'): self.play_pause_button.hide()
        if hasattr(self, 'snapshot_button'): self.snapshot_button.hide()
        if hasattr(self, 'reset_state_button'): self.reset_state_button.hide()
        self._init_refine_selection_hint()
        QTimer.singleShot(100, self._update_upload_overlay_geometry)
        try:
            session_data = StateTransfer.load_state()
            if session_data:
                if session_data.get('input_file'):
                    path = session_data['input_file']
                    if os.path.exists(path): file_path = path
                if session_data.get('resolution'): self.media_processor.original_resolution = session_data['resolution']
        except: pass
        if hasattr(self, 'video_surface') and self.video_surface:
            QTimer.singleShot(50, lambda: self.media_processor.attach_wid(self.video_surface.winId()))
        if file_path and os.path.exists(file_path): self.load_file(file_path)

    def connect_signals(self):
        super().connect_signals()
        self.done_button.clicked.connect(self.on_done_clicked)
        self.delete_button.clicked.connect(self.delete_selected)
        self.undo_button.clicked.connect(self.undo)
        self.redo_button.clicked.connect(self.redo)
        self.undo_shortcut = QShortcut(QKeySequence("Ctrl+Z"), self)
        self.undo_shortcut.activated.connect(self.undo)
        self.redo_shortcut_y = QShortcut(QKeySequence("Ctrl+Y"), self)
        self.redo_shortcut_y.activated.connect(self.redo)
        self.redo_shortcut_z = QShortcut(QKeySequence("Ctrl+Shift+Z"), self)
        self.redo_shortcut_z.activated.connect(self.redo)
        if hasattr(self, 'raise_button'): self.raise_button.clicked.connect(self.raise_selected_item)
        if hasattr(self, 'lower_button'): self.lower_button.clicked.connect(self.lower_selected_item)
        if hasattr(self, 'snap_toggle_button'): self.snap_toggle_button.clicked.connect(self._on_snap_toggle)
        self.show_placeholders_checkbox.stateChanged.connect(self.toggle_placeholders)
        self.portrait_scene.selectionChanged.connect(self.on_selection_changed)
        if hasattr(self, 'transparency_slider'): self.transparency_slider.valueChanged.connect(self.update_placeholder_transparency)
        if self.layer_list:
            self.layer_list.model().rowsMoved.connect(self._on_layer_order_changed)
            self.layer_list.itemSelectionChanged.connect(self._on_layer_list_selection_changed)

    def _setup_portrait_editor(self):
        canvas_item = self.portrait_scene.addRect(0, 0, PORTRAIT_W, PORTRAIT_H, QPen(Qt.NoPen), QBrush(QColor("#1e1e1e")))
        canvas_item.setZValue(-100)
        border = self.portrait_scene.addRect(-3, -3, PORTRAIT_W + 6, PORTRAIT_H + 6, QPen(QColor("#34495e"), 12))
        border.setZValue(102)
        top_bar_rect = self.portrait_scene.addRect(0, 0, PORTRAIT_W, UI_PADDING_TOP, QPen(Qt.NoPen), QBrush(QColor("black")))
        top_bar_rect.setZValue(100)
        bottom_bar_rect = self.portrait_scene.addRect(0, PORTRAIT_H - UI_PADDING_BOTTOM, PORTRAIT_W, UI_PADDING_BOTTOM, QPen(Qt.NoPen), QBrush(QColor("black")))
        bottom_bar_rect.setZValue(100)
        self.load_existing_placeholders()
        self.update_undo_redo_buttons()
        self.on_selection_changed()
        self._refresh_done_button()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.portrait_view.fit_to_scene()
        self._position_refine_selection_hint()
        self._update_upload_overlay_geometry()

    def _init_refine_selection_hint(self):
        self._refine_hint_visible = False
        self._refine_hint_blink_timer = QTimer(self)
        self._refine_hint_blink_timer.setInterval(UI_BEHAVIOR.VIDEO_VIEW_GUIDANCE_BLINK_INTERVAL)
        self._refine_hint_blink_timer.timeout.connect(self._toggle_refine_selection_hint)
        self._refine_hint_hide_timer = QTimer(self)
        self._refine_hint_hide_timer.setSingleShot(True)
        self._refine_hint_hide_timer.timeout.connect(self._hide_refine_selection_hint)

    def _position_refine_selection_hint(self):
        if not hasattr(self, 'draw_refine_hint_container'): return
        viewport = self.draw_scroll_area.viewport()
        if viewport is None: return
        self.draw_refine_hint_container.setFixedWidth(min(560, viewport.width() - 24))
        self.draw_refine_hint_container.adjustSize()
        self.draw_refine_hint_container.move((viewport.width() - self.draw_refine_hint_container.width()) // 2, 12)
        self.draw_refine_hint_container.raise_()

    def show_refine_selection_overlay(self):
        if not hasattr(self, 'draw_refine_hint_container'): return
        self._position_refine_selection_hint()
        self._refine_hint_visible = True
        self.draw_refine_hint_container.setVisible(True)
        self._refine_hint_blink_timer.start()
        self._refine_hint_hide_timer.start(3000)

    def _toggle_refine_selection_hint(self):
        if not hasattr(self, 'draw_refine_hint_container'): return
        self._refine_hint_visible = not self._refine_hint_visible
        self.draw_refine_hint_container.setVisible(self._refine_hint_visible)

    def _hide_refine_selection_hint(self):
        if hasattr(self, '_refine_hint_blink_timer'): self._refine_hint_blink_timer.stop()
        if hasattr(self, 'draw_refine_hint_container'): self.draw_refine_hint_container.setVisible(False)

    def keyPressEvent(self, event):
        key = event.key()
        if key == Qt.Key_Right: self.set_position(self.media_processor.get_time() + 33); event.accept(); return
        if key == Qt.Key_Left: self.set_position(self.media_processor.get_time() - 33); event.accept(); return
        if key == Qt.Key_Delete: self.delete_selected(); event.accept(); return
        super().keyPressEvent(event)

    def toggle_placeholders(self):
        is_visible = self.show_placeholders_checkbox.isChecked()
        if hasattr(self, 'transparency_slider'): self.transparency_slider.setVisible(is_visible)
        if is_visible: self.update_placeholder_transparency()
        else:
            for item in self.placeholders_group: item.setVisible(False)
        self._refresh_portrait_controls_enabled()

    def update_placeholder_transparency(self):
        alpha = self.transparency_slider.value()
        for item in self.placeholders_group:
            if self.show_placeholders_checkbox.isChecked():
                item.setVisible(True)
                item.setBrush(QBrush(QColor(0, 255, 0, alpha)))
            else: item.setVisible(False)

    def _on_snap_toggle(self):
        enabled = self.snap_toggle_button.isChecked()
        if hasattr(self.portrait_view, 'set_snap_enabled'): self.portrait_view.set_snap_enabled(enabled)

    def load_existing_placeholders(self):
        config = self.config_manager.load_config()
        configured_keys = self.config_manager.get_configured_elements()
        for tech_key in configured_keys:
            crop = config.get('crops_1080p', {}).get(tech_key)
            overlay = config.get('overlays', {}).get(tech_key)
            if crop and overlay:
                item = QGraphicsRectItem(overlay['x'], overlay['y'], crop[0], crop[1])
                item.setBrush(QBrush(QColor(0, 255, 0, UI_COLORS.OPACITY_PH_ALPHA)))
                item.setZValue(config.get("z_orders", {}).get(tech_key, 10))
                self.portrait_scene.addItem(item)
                self.placeholders_group.append(item)

    def _on_layer_order_changed(self):
        self._sync_z_from_layer_list()
        self._mark_dirty()

    def _sync_z_from_layer_list(self):
        count = self.layer_list.count()
        for i in range(count):
            role = self.layer_list.item(i).data(Qt.UserRole)
            for g_item in self.portrait_scene.items():
                if isinstance(g_item, ResizablePixmapItem) and g_item.assigned_role == role:
                    g_item.setZValue(count - i)
                    break

    def _refresh_layer_list(self):
        self.layer_list.blockSignals(True)
        self.layer_list.clear()
        items = [i for i in self.portrait_scene.items() if isinstance(i, ResizablePixmapItem)]
        items.sort(key=lambda x: x.zValue(), reverse=True)
        for item in items:
            li = QListWidgetItem(item.assigned_role or "Unknown")
            li.setData(Qt.UserRole, item.assigned_role)
            self.layer_list.addItem(li)
        self.layer_list.blockSignals(False)

    def _on_layer_list_selection_changed(self):
        sel = self.layer_list.selectedItems()
        if not sel: return
        role = sel[0].data(Qt.UserRole)
        self.portrait_scene.clearSelection()
        for g_item in self.portrait_scene.items():
            if isinstance(g_item, ResizablePixmapItem) and g_item.assigned_role == role:
                g_item.setSelected(True); break

    def on_selection_changed(self):
        self._refresh_portrait_controls_enabled()
        self.update_undo_redo_buttons()
        if not self.layer_list: return
        self.layer_list.blockSignals(True)
        sel = self.portrait_scene.selectedItems()
        if sel and isinstance(sel[0], ResizablePixmapItem):
            role = sel[0].assigned_role
            for i in range(self.layer_list.count()):
                if self.layer_list.item(i).data(Qt.UserRole) == role:
                    self.layer_list.setCurrentRow(i); break
        else: self.layer_list.clearSelection()
        self.layer_list.blockSignals(False)

    def add_scissored_item(self, pixmap, crop_rect, background_crop_width, role=None):
        for item in self.portrait_scene.items():
            if isinstance(item, ResizablePixmapItem) and item.assigned_role == role:
                self.portrait_scene.removeItem(item)
        item = ResizablePixmapItem(pixmap, crop_rect)
        item.assigned_role = role
        item.setZValue(50)
        self.portrait_scene.addItem(item)
        item.setPos(100, 100)
        item.item_changed.connect(lambda: self._handle_item_changed(item))
        self._mark_dirty()
        self._refresh_layer_list()
        return item

    def _handle_item_changed(self, item):
        if self._in_undo_redo: return
        self._mark_dirty()

    def _get_item_state(self, item):
        p = item.scenePos()
        return {"x": float(p.x()), "y": float(p.y()), "width": float(item.current_width), "height": float(item.current_height), "z": item.zValue()}

    def _apply_item_state(self, item, state):
        item.setPos(state["x"], state["y"])
        item.setZValue(state["z"])
        item.current_width, item.current_height = state["width"], state["height"]
        item.update_handle_positions(); item.update()
        self._refresh_layer_list(); return True

    def delete_selected(self):
        for item in self.portrait_scene.selectedItems():
            if isinstance(item, ResizablePixmapItem):
                self.portrait_scene.removeItem(item)
        self._mark_dirty()
        self._refresh_layer_list()

    def on_done_clicked(self):
        items = [i for i in self.portrait_scene.items() if isinstance(i, ResizablePixmapItem)]
        if not items:
            QMessageBox.information(self, "Nothing To Save", "No HUD elements are currently placed.")
            return
        payload = {}
        original_resolution = getattr(self.media_processor, 'original_resolution', None) or getattr(self, 'snapshot_resolution', None) or "1920x1080"
        for it in items:
            role = it.assigned_role
            tech_key = get_tech_key_from_role(role)
            if tech_key == "unknown":
                continue
            rect_obj = it.crop_rect.toRect() if hasattr(it.crop_rect, "toRect") else it.crop_rect
            source_rect = (int(rect_obj.x()), int(rect_obj.y()), int(rect_obj.width()), int(rect_obj.height()))
            transformed = transform_to_content_area_int(source_rect, original_resolution)
            crop_w = max(2, int(transformed[2]))
            crop_h = max(2, int(transformed[3]))
            scale_x = float(it.current_width) / float(crop_w)
            scale_y = float(it.current_height) / float(crop_h)
            scale_val = max(0.0001, round((scale_x + scale_y) / 2.0, 4))
            payload[tech_key] = {"crop": [crop_w, crop_h, int(transformed[0]), int(transformed[1])], "scale": scale_val, "overlay": {"x": int(round(it.scenePos().x())), "y": int(round(it.scenePos().y()))}, "z": int(round(it.zValue())), "display": role}
        if not payload:
            QMessageBox.warning(self, "Nothing To Save", "No recognized HUD elements are currently placed.")
            return
        self.done_button.setEnabled(False)
        self._save_progress_dialog = QProgressDialog("Saving crop settings...", None, 0, 0, self)
        self._save_progress_dialog.setWindowModality(Qt.ApplicationModal)
        self._save_progress_dialog.setMinimumDuration(0)
        self._save_progress_dialog.show()
        self._save_thread = QThread(self)
        self._save_worker = SaveConfigWorker(self.hud_config_path, payload, self.logger)
        self._save_worker.moveToThread(self._save_thread)
        self._save_thread.started.connect(self._save_worker.run)
        self._save_worker.finished.connect(self._on_save_finished)
        self._save_worker.finished.connect(self._save_thread.quit)
        self._save_worker.finished.connect(self._save_worker.deleteLater)
        self._save_thread.finished.connect(self._save_thread.deleteLater)
        self._save_thread.start()

    def _on_save_finished(self, success, configured, unchanged, error):
        if self._save_progress_dialog:
            self._save_progress_dialog.close()
            self._save_progress_dialog = None
        self._save_thread = None
        self._save_worker = None
        if not success:
            self.done_button.setEnabled(True)
            QMessageBox.critical(self, "Save Failed", error or "The crop configuration could not be saved.")
            return
        self._dirty = False
        self._refresh_done_button()
        try:
            self._summary_toast = SummaryToast(configured, unchanged, self.portrait_view.grab(), self)
            self._summary_toast.show()
        except Exception:
            pass
        QTimer.singleShot(900, self._deferred_launch_main_app)

    def set_background_image(self, pix):
        if pix.isNull(): return
        if self.background_item: self.portrait_scene.removeItem(self.background_item)
        self.background_item = QGraphicsPixmapItem(pix.scaled(PORTRAIT_W, PORTRAIT_H))
        self.background_item.setZValue(-80)
        self.portrait_scene.addItem(self.background_item)

    def register_undo_action(self, desc, uf, rf):
        self.state_manager.add_undo_action("portrait_edit", desc, uf, rf)
        self.update_undo_redo_buttons()

    def _refresh_portrait_controls_enabled(self):
        has_items = any(isinstance(i, ResizablePixmapItem) for i in self.portrait_scene.items())
        self.done_button.setEnabled(self._dirty)
        self.undo_button.setEnabled(self.state_manager.can_undo())
        self.redo_button.setEnabled(self.state_manager.can_redo())

    def _check_dependencies(self):
        if not DependencyDoctor.check_ffmpeg(self.base_dir)[0]: sys.exit(1)

    def _auto_save_state(self): pass

    def _check_restore(self): pass

    def raise_selected_item(self):
        for item in self.portrait_scene.selectedItems(): item.setZValue(item.zValue() + 1)
        self._refresh_layer_list()

    def lower_selected_item(self):
        for item in self.portrait_scene.selectedItems(): item.setZValue(item.zValue() - 1)
        self._refresh_layer_list()

    def update_undo_redo_buttons(self):
        self.undo_button.setEnabled(self.state_manager.can_undo())
        self.redo_button.setEnabled(self.state_manager.can_redo())

    def showEvent(self, event):
        super().showEvent(event)
        self.portrait_view.fit_to_scene()

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls(): event.acceptProposedAction()

    def dropEvent(self, event):
        for url in event.mimeData().urls(): self.load_file(url.toLocalFile())

    def closeEvent(self, event):
        if self._confirm_discard_changes(): super().closeEvent(event)
        else: event.ignore()

    def _deferred_launch_main_app(self):
        subprocess.Popen([sys.executable, os.path.join(self.base_dir, 'app.py')])
        QApplication.quit()

    def _get_persistence_extras(self): return {}

    def _mark_dirty(self, is_dirty=True):
        self._dirty = is_dirty
        self._refresh_done_button()

    def _refresh_done_button(self):
        self.done_button.setEnabled(self._dirty)

    def _confirm_discard_changes(self):
        if not self._dirty: return True
        return QMessageBox.question(self, "Unsaved", "Discard changes?", QMessageBox.Yes | QMessageBox.No) == QMessageBox.Yes

    def get_title_info(self): return self.base_title

    def undo(self):
        self._in_undo_redo = True
        if self.state_manager.undo(): self._mark_dirty(); self._refresh_layer_list()
        self._in_undo_redo = False

    def redo(self):
        self._in_undo_redo = True
        if self.state_manager.redo(): self._mark_dirty(); self._refresh_layer_list()
        self._in_undo_redo = False

def main():
    app = QApplication(sys.argv)

    from system.logger import setup_native_logging
    logger = setup_native_logging("crop_tool")
    ex = CropApp(logger, get_enhanced_logger(logger))
    ex.show()
    sys.exit(app.exec_())
if __name__ == '__main__':
    main()
