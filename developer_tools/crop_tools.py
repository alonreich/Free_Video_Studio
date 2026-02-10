import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')

import json
import traceback
import time
project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
if project_root not in sys.path:
    sys.path.insert(0, project_root)

import ctypes
import tempfile
import subprocess
import psutil
from PyQt5.QtWidgets import (
    QApplication, QWidget, QGraphicsScene, QGraphicsPixmapItem, QGraphicsRectItem, 
    QGraphicsSimpleTextItem, QDialog, QFrame, QVBoxLayout, QLabel, QHBoxLayout, QMessageBox,
    QProgressDialog, QShortcut, QPushButton
)

from PyQt5.QtCore import Qt, QTimer, QRectF, pyqtSignal, QPointF, QPoint, QPropertyAnimation, QEasingCurve
from PyQt5.QtCore import QParallelAnimationGroup, QSequentialAnimationGroup
from PyQt5.QtGui import QPainter, QColor, QFont, QBrush, QPixmap, QPen, QPolygon, QKeySequence
from utils import PersistentWindowMixin
from Keyboard_Mixing import KeyboardShortcutMixin
from media_processor import MediaProcessor
from ui_setup import Ui_CropApp
from app_handlers import CropAppHandlers
from config import CROP_APP_STYLESHEET
from system.constants import HUD_ELEMENT_MAPPINGS, Z_ORDER_MAP, UI_COLORS, UI_LAYOUT, UI_BEHAVIOR
from system.shared_paths import SharedPaths
from logger_setup import setup_logger
from enhanced_logger import get_enhanced_logger
from config_manager import get_config_manager
from state_manager import get_state_manager
from graphics_items import ResizablePixmapItem
from coordinate_math import (
    transform_to_content_area, inverse_transform_from_content_area_int, get_resolution_ints, outward_round_rect, scale_round,
    PORTRAIT_W, PORTRAIT_H, UI_PADDING_TOP, UI_PADDING_BOTTOM
)

from system.utils import ProcessManager, LogManager, DependencyDoctor
from system.state_transfer import StateTransfer

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
                font-size: 13px;
                padding: 1px;
                background-color: transparent;
            }}
            #title {{
                font-size: 16px;
                font-weight: bold;
                color: {UI_COLORS.PRIMARY};
                padding-bottom: 8px;
            }}
            #sectionTitle {{
                font-weight: bold;
                font-size: 14px;
                margin-top: 5px;
            }}
            #changed {{ color: {UI_COLORS.SUCCESS}; }}
            #unchanged {{ color: {UI_COLORS.DANGER}; }}
        """)
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.addWidget(container)
        
        from PyQt5.QtWidgets import QScrollArea
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.NoFrame)
        scroll.setStyleSheet("background: transparent;")
        scroll_content = QWidget()
        scroll_content.setStyleSheet("background: transparent;")
        content_layout = QVBoxLayout(scroll_content)
        content_layout.setContentsMargins(15, 15, 15, 15)
        content_layout.setSpacing(2)
        title_label = QLabel("Configuration Saved")
        title_label.setObjectName("title")
        title_label.setAlignment(Qt.AlignCenter)
        content_layout.addWidget(title_label)
        if configured:
            changed_title = QLabel("Updated Elements:")
            changed_title.setObjectName("sectionTitle")
            changed_title.setStyleSheet(f"color: {UI_COLORS.SUCCESS};")
            content_layout.addWidget(changed_title)
            for item in configured:
                item_label = QLabel(f"  • {item}")
                content_layout.addWidget(item_label)
        if unchanged:
            if configured:
                content_layout.addSpacing(10)
            safe_note = QLabel(f"({len(unchanged)} other elements remain unchanged/safe)")
            safe_note.setStyleSheet(f"color: {UI_COLORS.TEXT_DISABLED}; font-style: italic; font-size: 11px;")
            safe_note.setAlignment(Qt.AlignCenter)
            content_layout.addWidget(safe_note)
        scroll.setWidget(scroll_content)
        vbox = QVBoxLayout(container)
        vbox.setContentsMargins(0,0,0,0)
        vbox.addWidget(scroll)
        self.setFixedWidth(350)
        self.adjustSize()
        self.setFixedHeight(min(self.sizeHint().height(), 500))
        screen = QApplication.primaryScreen().availableGeometry()
        self.move(screen.center() - self.rect().center())

class CropApp(KeyboardShortcutMixin, PersistentWindowMixin, QWidget, CropAppHandlers):
    done_organizing = pyqtSignal()

    def __init__(self, logger_instance, enhanced_logger_instance, file_path=None):
        super().__init__()
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
        self._check_dependencies()
        self._autosave_file = os.path.join(tempfile.gettempdir(), "fvs_autosave_recovery.json")
        self._autosave_timer = QTimer(self)
        self._autosave_timer.setInterval(5000)
        self._autosave_timer.timeout.connect(self._auto_save_state)
        self.last_dir = None
        self.bin_dir = os.path.abspath(os.path.join(self.base_dir, 'binaries'))
        self.snapshot_path = None
        self.media_processor = MediaProcessor(self.bin_dir)
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
        self.REF_WIDTH = 1513.0
        self.REF_BOX_W, self.REF_BOX_H = 620, 115
        self.REF_FONT_SIZE = 28
        self.REF_ARROW_L, self.REF_ARROW_S = 400, 40
        self.REF_OFFSET_X = 190
        self.REF_GAP = 20
        self.ui = Ui_CropApp()
        try:
            self.ui.setupUi(self)
            self.video_stack = getattr(self, 'video_stack', None)
            self.video_surface = getattr(self, 'video_surface', None)
            self.video_frame = getattr(self, 'video_frame', None)
            self.hint_overlay_widget = getattr(self, 'hint_overlay_widget', None)
            self.hint_group_container = getattr(self, 'hint_group_container', None)
            self.hint_group_layout = getattr(self, 'hint_group_layout', None)
            self.upload_hint_container = getattr(self, 'upload_hint_container', None)
            self.upload_hint_label = getattr(self, 'upload_hint_label', None)
            self.upload_hint_arrow = getattr(self, 'upload_hint_arrow', None)
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
        except Exception as e:
            logger_instance.critical(f"SetupUI Failed: {e}", exc_info=True)
            raise e
        try:
            self.connect_signals()
        except Exception as e:
            logger_instance.critical(f"Connect Signals Failed: {e}", exc_info=True)
            raise e
        if not self.media_processor.vlc_instance:
            if hasattr(self, 'vlc_error_label'):
                self.vlc_error_label.setVisible(True)
            if hasattr(self, 'upload_hint_label'):
                self.upload_hint_label.setVisible(False)
            if hasattr(self, 'open_image_button'):
                self.open_image_button.setVisible(True)
                self.open_image_button.setText("📷 UPLOAD SCREENSHOT (VLC MISSING)")
            msg = QMessageBox(self)
            msg.setIcon(QMessageBox.Critical)
            msg.setWindowTitle("VLC Player Required")
            msg.setText("Video playback is disabled because VLC Media Player (64-bit) was not found.")
            msg.setInformativeText("The app can still crop snapshots, but you cannot play/seek video.\n\nPlease install VLC to fix this.")
            download_btn = msg.addButton("Download VLC", QMessageBox.ActionRole)
            msg.addButton(QMessageBox.Ok)
            msg.exec_()
            if msg.clickedButton() == download_btn:
                import webbrowser
                webbrowser.open("https://www.videolan.org/vlc/")
        self.timer = QTimer(self)
        self.timer.setInterval(100)
        self.timer.timeout.connect(self.update_ui)
        self.timer.start()
        try:
            self.set_style()
        except Exception as e:
            logger_instance.error(f"Set Style Failed: {e}")
        self.setFocusPolicy(Qt.StrongFocus)
        self.setFocus()
        try:
            self.setup_persistence(
                config_path=self.app_config_path,
                settings_key='window_geometry',
                default_geo={'x': 0, 'y': 0, 'w': 1800, 'h': 930},
                title_info_provider=self.get_title_info,
                extra_data_provider=self._get_persistence_extras
            )
        except Exception as e:
            logger_instance.error(f"Persistence Setup Failed: {e}")
        try:
            self._setup_portrait_editor()
        except Exception as e:
            logger_instance.critical(f"Portrait Editor Setup Failed: {e}", exc_info=True)
            raise e
        self._init_upload_hint_blink()
        self._init_refine_selection_hint()
        try:
            session_data = StateTransfer.load_state()
            if session_data:
                if session_data.get('input_file'):
                    path = session_data['input_file']
                    if os.path.exists(path):
                        file_path = path
                if session_data.get('resolution'):
                     self.media_processor.original_resolution = session_data['resolution']
        except Exception as e:
            logger_instance.error(f"Session Load Failed: {e}")
        if file_path and os.path.exists(file_path):
            self._set_upload_hint_active(False)
            try:
                self.load_file(file_path)
            except Exception as e:
                logger_instance.error(f"Initial Load File Failed: {e}")
        else:
            self._set_upload_hint_active(True)

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
        QShortcut(QKeySequence("O"), self).activated.connect(self.open_button.click)
        QShortcut(QKeySequence("I"), self).activated.connect(self.open_image_button.click)
        QShortcut(QKeySequence("C"), self).activated.connect(self.snapshot_button.click)
        QShortcut(QKeySequence("W"), self).activated.connect(self.magic_wand_button.click)
        QShortcut(QKeySequence("R"), self).activated.connect(self.reset_state_button.click)
        QShortcut(QKeySequence("Home"), self).activated.connect(self.return_button.click)
        QShortcut(QKeySequence("F"), self).activated.connect(self.done_button.click)
        QShortcut(QKeySequence("Ctrl+Return"), self).activated.connect(self.done_button.click)
        QShortcut(QKeySequence("M"), self).activated.connect(self.snap_toggle_button.click)
        QShortcut(QKeySequence("E"), self).activated.connect(self.show_placeholders_checkbox.click)
        if hasattr(self, 'raise_button'):
            self.raise_button.clicked.connect(self.raise_selected_item)
            QShortcut(QKeySequence("Alt+Up"), self).activated.connect(self.raise_button.click)
        if hasattr(self, 'lower_button'):
            self.lower_button.clicked.connect(self.lower_selected_item)
            QShortcut(QKeySequence("Alt+Down"), self).activated.connect(self.lower_button.click)
        self.undo_button.setCursor(Qt.PointingHandCursor)
        self.redo_button.setCursor(Qt.PointingHandCursor)
        if hasattr(self, 'snap_toggle_button'):
            self.snap_toggle_button.clicked.connect(self._on_snap_toggle)
        self.show_placeholders_checkbox.stateChanged.connect(self.toggle_placeholders)
        self.portrait_scene.selectionChanged.connect(self.on_selection_changed)
        if hasattr(self, 'transparency_slider'):
            self.transparency_slider.valueChanged.connect(self.update_placeholder_transparency)

    def _setup_portrait_editor(self):
        """Initializes the integrated portrait editor components."""
        canvas_item = self.portrait_scene.addRect(
            0, 0, PORTRAIT_W, PORTRAIT_H,
            QPen(Qt.NoPen), QBrush(QColor("#1e1e1e"))
        )
        canvas_item.setZValue(-100)
        limit_pen = QPen(QColor("#34495e"), 12, Qt.SolidLine)
        border = self.portrait_scene.addRect(-3, -3, PORTRAIT_W + 6, PORTRAIT_H + 6, limit_pen)
        border.setZValue(102)
        top_bar_rect = self.portrait_scene.addRect(
            0, 0, PORTRAIT_W, UI_PADDING_TOP,
            QPen(Qt.NoPen), QBrush(QColor("black"))
        )
        top_bar_rect.setZValue(100)
        bottom_bar_rect = self.portrait_scene.addRect(
            0, PORTRAIT_H - UI_PADDING_BOTTOM, PORTRAIT_W, UI_PADDING_BOTTOM,
            QPen(Qt.NoPen), QBrush(QColor("black"))
        )
        bottom_bar_rect.setZValue(100)
        help_font = QFont("Arial", 10, QFont.Bold)
        top_help = self.portrait_scene.addSimpleText("RESERVED FOR TEXT LAYER", help_font)
        top_help.setBrush(QBrush(QColor("#555555")))
        top_help.setPos((PORTRAIT_W - top_help.boundingRect().width())/2, (UI_PADDING_TOP - top_help.boundingRect().height())/2)
        top_help.setZValue(101)
        bot_help = self.portrait_scene.addSimpleText("RESERVED FOR CAPTIONS", help_font)
        bot_help.setBrush(QBrush(QColor("#555555")))
        bot_help.setPos((PORTRAIT_W - bot_help.boundingRect().width())/2, PORTRAIT_H - UI_PADDING_BOTTOM + (UI_PADDING_BOTTOM - bot_help.boundingRect().height())/2)
        bot_help.setZValue(101)
        self.load_existing_placeholders()
        self.update_undo_redo_buttons()
        self.on_selection_changed()
        self._refresh_done_button()
        self.portrait_view.setToolTip("Mouse wheel to zoom, middle drag to pan, Shift+Arrows to reorder")

    def _init_upload_hint_blink(self):
        """Initializes the robust smooth fading logic for the upload hint group."""
        if not hasattr(self, 'hint_group_container'):
            return

        from PyQt5.QtWidgets import QGraphicsOpacityEffect
        self._hint_opacity_effect = QGraphicsOpacityEffect(self.hint_group_container)
        self.hint_group_container.setGraphicsEffect(self._hint_opacity_effect)
        anim_in = QPropertyAnimation(self._hint_opacity_effect, b"opacity")
        anim_in.setDuration(1200) 
        anim_in.setStartValue(0.3)
        anim_in.setEndValue(1.0)
        anim_in.setEasingCurve(QEasingCurve.InOutSine)
        anim_out = QPropertyAnimation(self._hint_opacity_effect, b"opacity")
        anim_out.setDuration(1200)
        anim_out.setStartValue(1.0)
        anim_out.setEndValue(0.3)
        anim_out.setEasingCurve(QEasingCurve.InOutSine)
        self._hint_group = QSequentialAnimationGroup(self)
        self._hint_group.addAnimation(anim_in)
        self._hint_group.addAnimation(anim_out)
        self._hint_group.setLoopCount(-1)

    def _set_upload_hint_active(self, active):
        target = getattr(self, 'hint_overlay_widget', None)
        if not target or not hasattr(self, '_hint_group'):
            return
        if active:
            self._update_upload_hint_responsive()
            target.show()
            target.raise_()
            self._hint_group.start()
        else:
            self._hint_group.stop()
            target.hide()

    def _update_upload_hint_responsive(self):
        if not hasattr(self, 'upload_hint_container'):
            return

        from PyQt5.QtGui import QPolygon
        scale = (self.width() / self.REF_WIDTH) * 0.9
        box_w, box_h = int(self.REF_BOX_W * scale), int(self.REF_BOX_H * scale)
        font_size = int(self.REF_FONT_SIZE * scale)
        self.upload_hint_container.setFixedSize(box_w, box_h)
        self.upload_hint_container.setStyleSheet(f"background-color: #000000; border: {max(2, int(3*scale))}px solid #7DD3FC; border-radius: {int(14*scale)}px;")
        self.upload_hint_label.setStyleSheet(f"color: #7DD3FC; font-family: Arial; font-size: {font_size}px; font-weight: bold; background: transparent;")
        if self.hint_group_layout.direction() != QHBoxLayout.TopToBottom:
            self.hint_group_layout.setDirection(QHBoxLayout.TopToBottom)
            self.hint_group_layout.setAlignment(Qt.AlignCenter)
        gap = 60
        self.hint_group_layout.setSpacing(gap)
        c_w, c_h = 1200, 600
        self.upload_hint_arrow.setFixedSize(c_w, c_h)
        self.upload_hint_arrow.setContentsMargins(0, 0, 0, 0)
        pix = QPixmap(c_w, c_h)
        pix.fill(Qt.transparent)
        p = QPainter(pix)
        p.setRenderHint(QPainter.Antialiasing)
        p.setBrush(QColor("#7DD3FC"))
        p.setPen(QPen(QColor("#7DD3FC"), max(2, int(10*scale)), Qt.SolidLine, Qt.FlatCap, Qt.MiterJoin))
        draw_shift_left = 565 
        start_pt = QPoint(c_w // 2, 10)
        end_pt = QPoint(c_w // 2 - 390, 10 + 290)
        p.drawLine(start_pt, end_pt + QPoint(12, -10))
        p.setPen(Qt.NoPen)
        h_s = int(42 * scale) 
        head = QPolygon([
            end_pt,
            QPoint(end_pt.x() + h_s, end_pt.y() - int(h_s * 0.35)),
            QPoint(end_pt.x() + int(h_s * 0.35), end_pt.y() - h_s)
        ])
        p.drawPolygon(head)
        p.end()
        self.upload_hint_arrow.setPixmap(pix)
        self._apply_hint_position()

    def _apply_hint_position(self):
        if not hasattr(self, 'hint_group_container'): return
        try:
            scale = (self.width() / self.REF_WIDTH) * 0.9
            box_w = int(self.REF_BOX_W * scale)
            box_h = int(self.REF_BOX_H * scale)
            gap = 60
            c_w, c_h = 1200, 600
            win_w, win_h = self.width(), self.height()
            draw_shift_left = 565 
            target_x = (win_w - box_w) // 2 - draw_shift_left
            target_y = (win_h - (box_h + gap + c_h)) // 2 + 290
            self.hint_group_container.setFixedSize(max(box_w, c_w), box_h + gap + c_h)
            self.hint_group_container.move(target_x, target_y)
        except Exception:
            pass

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.portrait_view.fit_to_scene()
        self._position_refine_selection_hint()
        if hasattr(self, 'hint_overlay_widget'):
            self.hint_overlay_widget.resize(self.size())
            self.hint_overlay_widget.raise_()
        if hasattr(self, '_update_upload_hint_responsive'):
            self._update_upload_hint_responsive()

    def _toggle_upload_hint(self): pass

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
        self.draw_refine_hint_container.setFixedWidth(min(560, max(360, viewport.width() - 80)))
        self.draw_refine_hint_container.adjustSize()
        self.draw_refine_hint_container.move(max(10, (viewport.width() - self.draw_refine_hint_container.width()) // 2), 12)

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
        if self.view_stack.currentWidget() == self.video_frame:
            if key == Qt.Key_Right:
                self.set_position(self.media_processor.get_time() + 33)
                event.accept(); return
            elif key == Qt.Key_Left:
                self.set_position(self.media_processor.get_time() - 33)
                event.accept(); return
        if key == Qt.Key_Escape:
            if self.view_stack.currentWidget() == self.draw_scroll_area:
                if hasattr(self, 'back_to_video_button') and self.back_to_video_button.isVisible():
                    self.back_to_video_button.click()
                    event.accept(); return
        if key == Qt.Key_F12:
            if self._confirm_discard_changes(): self._deferred_launch_main_app()
            event.accept(); return
        if key == Qt.Key_B:
            self.background_dim_alpha = 0 if self.background_dim_alpha > 0 else UI_COLORS.OPACITY_DIM_LOW
            if self.snapshot_path: self.set_background_image(QPixmap(self.snapshot_path))
            event.accept(); return
        if key == Qt.Key_Delete:
            if self.view_stack.currentWidget() == self.draw_scroll_area: self.draw_widget.clear_selection()
            else: self.delete_selected()
            event.accept(); return
        if self.view_stack.currentWidget() == self.draw_scroll_area:
            if getattr(self, '_magic_wand_candidates', None) and key in (Qt.Key_Right, Qt.Key_Tab):
                self._cycle_magic_wand_preview()
                event.accept(); return
            self.draw_widget.handle_key_press(event)
            if event.isAccepted(): return
        if self.portrait_view.hasFocus() or self.portrait_scene.hasFocus():
            self.portrait_view.keyPressEvent(event); return
        super(CropApp, self).keyPressEvent(event)

    def toggle_placeholders(self):
        try:
            if not hasattr(self, 'show_placeholders_checkbox'): return
            is_visible = self.show_placeholders_checkbox.isChecked()
            if hasattr(self, 'transparency_slider'):
                self.transparency_slider.setVisible(is_visible)
            if hasattr(self, 'draw_widget'):
                self.draw_widget.set_ghosts_visible(is_visible)
            if is_visible:
                self.update_placeholder_transparency()
            else:
                for item in self.placeholders_group:
                    try: item.setVisible(False)
                    except: pass
            self._refresh_portrait_controls_enabled()
        except Exception as e:
            self.logger.error(f"Error in toggle_placeholders: {e}")

    def update_placeholder_transparency(self):
        if not hasattr(self, 'transparency_slider'): return
        alpha = self.transparency_slider.value()
        should_be_visible = self.show_placeholders_checkbox.isChecked()
        for item in self.placeholders_group:
            try:
                if should_be_visible:
                    item.setVisible(True)
                    brush = item.brush()
                    color = brush.color()
                    color.setAlpha(alpha)
                    item.setBrush(QBrush(color))
                    pen = item.pen()
                    pen_color = pen.color()
                    pen_color.setAlpha(min(255, alpha + 40))
                    pen.setColor(pen_color)
                    item.setPen(pen)
                else:
                    item.setVisible(False)
            except: pass

    def clear_all_crops(self):
        items = [i for i in self.portrait_scene.items() if isinstance(i, ResizablePixmapItem)]
        if not items: return
        if QMessageBox.question(self, "Clear All", "Remove all HUD elements?", QMessageBox.Yes | QMessageBox.No) == QMessageBox.Yes:
            states = [{'item': i, 'state': self._get_item_state(i), 'scene': self.portrait_scene, 'role': i.assigned_role} for i in items]

            def undo_clear(dl):
                for d in dl:
                    d['scene'].addItem(d['item'])
                    self._apply_item_state(d['item'], d['state'])
                    if d['role']: self.modified_roles.add(d['role'])
                self._mark_dirty(); return True

            def redo_clear(dl):
                for d in dl:
                    d['scene'].removeItem(d['item'])
                    if d['role']: self.modified_roles.add(d['role'])
                self._mark_dirty(); return True
            self.register_undo_action("Clear All", lambda: undo_clear(states), lambda: redo_clear(states))
            for item in items:
                self.portrait_scene.removeItem(item)
                if item.assigned_role: self.modified_roles.add(item.assigned_role)
            self._mark_dirty(); self.on_selection_changed()

    def _on_snap_toggle(self):
        enabled = self.snap_toggle_button.isChecked()
        if hasattr(self.portrait_view, 'set_snap_enabled'): self.portrait_view.set_snap_enabled(enabled)
        self.snap_toggle_button.setText("🧲 SNAP: ON" if enabled else "🧲 SNAP: OFF")

    def load_existing_placeholders(self):
        for item in self.placeholders_group:
            self.portrait_scene.removeItem(item)
        self.placeholders_group.clear()
        ghosts_for_landscape = []
        res_str = self.media_processor.original_resolution or "1920x1080"
        try:
            config = self.config_manager.load_config()
            configured_keys = self.config_manager.get_configured_elements()
            sorted_keys = sorted(configured_keys, key=lambda k: config.get("z_orders", {}).get(k, 0), reverse=True)
            for tech_key in configured_keys:
                scale = config.get('scales', {}).get(tech_key, 1.0)
                overlay = config.get('overlays', {}).get(tech_key)
                crop = config.get('crops_1080p', {}).get(tech_key)
                if overlay and crop and len(crop) >= 4:
                    try:
                        w_cont, h_cont, x_cont, y_cont = crop[0], crop[1], crop[2], crop[3]
                        source_rect_int = inverse_transform_from_content_area_int((x_cont, y_cont, w_cont, h_cont), res_str)
                        label_text = HUD_ELEMENT_MAPPINGS.get(tech_key, tech_key).upper()
                        ghosts_for_landscape.append((QRectF(*source_rect_int), label_text))
                    except Exception as e:
                        self.logger.error(f"Failed to reverse engineer ghost for {tech_key}: {e}")
                    w_ui, h_ui, x_ui, y_ui = crop[0] * scale, crop[1] * scale, overlay.get('x', 0), overlay.get('y', 0)
                    rect_item = QGraphicsRectItem(x_ui, y_ui, w_ui, h_ui)
                    color_map = {
                        "loot": "#fbbf24", "stats": "#60a5fa", "normal_hp": "#22c55e", 
                        "boss_hp": "#ef4444", "team": "#a855f7", "spectating": "#ec4899"
                    }
                    base_color = QColor(color_map.get(tech_key, "#22c55e"))
                    brush_color = QColor(base_color)
                    brush_color.setAlpha(UI_COLORS.OPACITY_PH_ALPHA)
                    rect_item.setBrush(QBrush(brush_color))
                    rect_item.setPen(QPen(base_color, 3, Qt.DashLine))
                    z_val = config.get("z_orders", {}).get(tech_key, Z_ORDER_MAP.get(tech_key, 10))
                    rect_item.setZValue(z_val - 5)
                    rank = 1 + sorted_keys.index(tech_key)
                    label_text = f"{HUD_ELEMENT_MAPPINGS.get(tech_key, tech_key).upper()} (LAYER {rank})"
                    txt = QGraphicsSimpleTextItem(label_text, rect_item)
                    txt.setBrush(QBrush(QColor("white")))
                    txt.setFont(QFont("Arial", 10, QFont.Bold))
                    tr = txt.boundingRect()
                    txt.setPos((w_ui - tr.width())/2, (h_ui - tr.height())/2)
                    self.portrait_scene.addItem(rect_item)
                    self.placeholders_group.append(rect_item)
        except Exception as e:
            self.logger.error(f"Error loading placeholders: {e}")
        if hasattr(self, 'draw_widget'):
            self.draw_widget.set_ghost_rects(ghosts_for_landscape)
        self.toggle_placeholders()

    def on_selection_changed(self):
        self._refresh_portrait_controls_enabled()
        self.update_undo_redo_buttons()

    def add_scissored_item(self, pixmap, crop_rect, background_crop_width, role=None):
        for item in self.portrait_scene.items():
            if isinstance(item, ResizablePixmapItem) and item.assigned_role == role: self.portrait_scene.removeItem(item)
        for ph in list(self.placeholders_group):
            for ch in ph.childItems():
                if isinstance(ch, QGraphicsSimpleTextItem) and ch.text() == (role or "").upper():
                    self.portrait_scene.removeItem(ph); self.placeholders_group.remove(ph); break
        item = ResizablePixmapItem(pixmap, crop_rect)
        res_str = self.media_processor.original_resolution or self.snapshot_resolution or "1920x1080"
        fx, fy, fw, fh = transform_to_content_area((crop_rect.x(), crop_rect.y(), crop_rect.width(), crop_rect.height()), res_str)
        item.current_width, item.current_height = max(20.0, fw), max(20.0, fh)
        item.update_handle_positions(); self.portrait_scene.addItem(item)
        tech_key = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}.get(role, 'unknown')
        item.setZValue(get_config_manager(self.hud_config_path, self.logger).load_config().get("z_orders", {}).get(tech_key, Z_ORDER_MAP.get(tech_key, 50)))
        item.setPos(self._default_position_for_role(role, item.current_width, item.current_height))
        self.portrait_scene.clearSelection(); item.setSelected(True)
        item.item_changed.connect(lambda: self._handle_item_changed(item))
        if role: item.set_role(role); self.modified_roles.add(role)
        self._mark_dirty()
        return item

    def _default_position_for_role(self, role, width, height):
        p, sl, st = 20, 20, 20 + UI_PADDING_TOP
        sr, sb = PORTRAIT_W - width - p, PORTRAIT_H - UI_PADDING_BOTTOM - height - p
        rl = (role or "").lower()
        if "loot" in rl: return QPointF(sr, sb)
        if "hp" in rl or "health" in rl: return QPointF(sl, sb)
        if "map" in rl or "stats" in rl: return QPointF(sr, st)
        if "team" in rl: return QPointF(sl, st)
        return QPointF((PORTRAIT_W - width)/2, (PORTRAIT_H - UI_PADDING_TOP - UI_PADDING_BOTTOM - height)/2 + UI_PADDING_TOP)

    def on_item_modified(self, item):
        if item.assigned_role: self.modified_roles.add(item.assigned_role)
        self._mark_dirty()

    def _handle_item_changed(self, item):
        if self._in_undo_redo: return
        iid = id(item)
        if iid not in self._item_edit_cache: self._item_edit_cache[iid] = self._get_item_state(item)
        if iid not in self._item_edit_timers:
            t = QTimer(self); t.setSingleShot(True); t.timeout.connect(lambda it=item: self._commit_item_change(it))
            self._item_edit_timers[iid] = t
        self._item_edit_timers[iid].start(250); self.on_item_modified(item)

    def _commit_item_change(self, item):
        iid = id(item)
        start, end = self._item_edit_cache.get(iid), self._get_item_state(item)
        if start and start != end:
            self.register_undo_action(f"Move/Resize {item.assigned_role or 'Item'}", lambda it=item, s=start: self._apply_item_state(it, s), lambda it=item, s=end: self._apply_item_state(it, s))
        self._item_edit_cache[iid] = end

    def _get_item_state(self, item):
        p = item.scenePos()
        return {"x": float(p.x()), "y": float(p.y()), "width": float(item.current_width), "height": float(item.current_height), "z": item.zValue()}

    def _apply_item_state(self, item, state):
        if not state: return False
        item.prepareGeometryChange()
        item.current_width, item.current_height = state["width"], state["height"]
        item.update_handle_positions(); item.setPos(state["x"], state["y"])
        if "z" in state: item.setZValue(state["z"])
        item.update(); self._mark_dirty(); return True

    def delete_selected(self):
        items = [i for i in self.portrait_scene.selectedItems() if isinstance(i, ResizablePixmapItem)]
        if not items: return
        data = [{'item': i, 'state': self._get_item_state(i), 'scene': self.portrait_scene, 'role': i.assigned_role} for i in items]

        def undo_del(dl):
            for d in dl:
                if d['item'].scene() != d['scene']: d['scene'].addItem(d['item'])
                self._apply_item_state(d['item'], d['state'])
                if d['role'] in self.modified_roles:
                    still_exists = any(i.assigned_role == d['role'] for i in self.portrait_scene.items() if isinstance(i, ResizablePixmapItem) and i != d['item'])
                    if not still_exists:
                        self.modified_roles.discard(d['role'])
            self._mark_dirty(); self.on_selection_changed(); return True

        def redo_del(dl):
            for d in dl:
                if d['item'].scene(): d['item'].scene().removeItem(d['item'])
                if d['role']: self.modified_roles.add(d['role'])
            self._mark_dirty(); self.on_selection_changed(); return True
        self.register_undo_action(f"Delete {len(data)} item(s)", lambda d=data: undo_del(d), lambda d=data: redo_del(d))
        for item in items:
            if item.assigned_role: self.modified_roles.add(item.assigned_role)
            self.portrait_scene.removeItem(item)
        self._mark_dirty(); self.on_selection_changed()

    def on_done_clicked(self):
        pd = QProgressDialog("Saving Configuration...", None, 0, 0, self)
        pd.setWindowTitle("Saving"); pd.show(); QApplication.processEvents()
        try:
            tk_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
            items = [item for item in self.portrait_scene.items() if isinstance(item, ResizablePixmapItem)]
            if not items: pd.close(); QMessageBox.warning(self, "Save", "No HUD elements to save."); return
            res_str = self.media_processor.original_resolution or "1920x1080"
            config = self.config_manager.load_config()
            configured, unchanged, saved_keys = [], [], set()
            items.sort(key=lambda i: i.zValue())
            for item in items:
                role = item.assigned_role
                if not role: continue
                tk = tk_map.get(role, "unknown")
                if tk == "unknown": continue
                r = item.crop_rect
                fx, fy, fw, fh = transform_to_content_area((float(r.x()), float(r.y()), float(r.width()), float(r.height())), res_str)
                ix, iy, iw, ih = outward_round_rect(fx, fy, fw, fh)
                normalized_rect = [iw, ih, ix, iy]
                scale = max(0.001, round(float(item.current_width) / max(1.0, fw), 4))
                ox, oy, zv = int(scale_round(item.scenePos().x())), int(scale_round(item.scenePos().y())), int(item.zValue())
                config["crops_1080p"][tk] = normalized_rect
                config["scales"][tk] = scale
                config["overlays"][tk] = {"x": ox, "y": oy}
                config["z_orders"][tk] = zv
                configured.append(HUD_ELEMENT_MAPPINGS.get(tk, tk))
                saved_keys.add(tk)
            all_tech_keys = list(config["crops_1080p"].keys())
            for tk in all_tech_keys:
                if tk not in saved_keys:
                    self.logger.info(f"Removing zombie element from config: {tk}")
                    for s in ["crops_1080p", "scales", "overlays", "z_orders"]:
                        if tk in config[s]: del config[s][tk]
            if self.config_manager.save_config(config):
                try:
                    processing_dir = os.path.dirname(self.hud_config_path)
                    backup_names = [
                        "old_crops_coordinations.conf",
                        "old1_crops_coordinations.conf",
                        "old2_crops_coordinations.conf",
                        "old3_crops_coordinations.conf",
                        "old4_crops_coordinations.conf"
                    ]
                    target_backup = None
                    for name in backup_names:
                        path = os.path.join(processing_dir, name)
                        if not os.path.exists(path):
                            target_backup = path
                            break
                    if not target_backup:
                        oldest_time = float('inf')
                        for name in backup_names:
                            path = os.path.join(processing_dir, name)
                            mtime = os.path.getmtime(path)
                            if mtime < oldest_time:
                                oldest_time = mtime
                                target_backup = path
                    if target_backup:
                        import shutil
                        shutil.copy2(self.hud_config_path, target_backup)
                        self.logger.info(f"Rotation backup created at: {target_backup}")
                except Exception as backup_err:
                    self.logger.error(f"Failed to create rotation backup: {backup_err}")
                self._dirty = False
                if os.path.exists(self._autosave_file):
                    try: os.unlink(self._autosave_file)
                    except: pass
                pd.close(); summary = SummaryToast(configured, unchanged, self); summary.show(); self._start_exit_sequence(summary)
            else: pd.close(); QMessageBox.critical(self, "Save", "Failed to save config.")
        except Exception as e: pd.close(); self.logger.exception(f"Save failed: {e}")

    def _start_exit_sequence(self, summary_dialog):
        widgets = [w for w in (self, summary_dialog) if w]
        seq = QSequentialAnimationGroup(self)
        par = QParallelAnimationGroup(seq)
        for w in widgets:
            a = QPropertyAnimation(w, b"windowOpacity")
            a.setStartValue(1.0); a.setEndValue(0.0); a.setDuration(150); a.setEasingCurve(QEasingCurve.InOutQuad)
            par.addAnimation(a)
        seq.addAnimation(par)

        def finalize():
            for w in widgets: w.setWindowOpacity(1.0)
            if summary_dialog: summary_dialog.close()
            self.close(); self._deferred_launch_main_app()
        seq.finished.connect(finalize); seq.start()

    def set_background_image(self, pix):
        if pix.isNull(): return
        try:
            cw, ch = PORTRAIT_W, PORTRAIT_H - UI_PADDING_TOP - UI_PADDING_BOTTOM
            scaled = pix.scaled(cw, ch, Qt.KeepAspectRatioByExpanding, Qt.SmoothTransformation)
            final_bg = scaled.copy((scaled.width() - cw)//2, (scaled.height() - ch)//2, cw, ch)
            dimmed = QPixmap(final_bg.size()); dimmed.fill(Qt.transparent)
            p = QPainter(dimmed); p.drawPixmap(0, 0, final_bg); p.fillRect(dimmed.rect(), QColor(0, 0, 0, self.background_dim_alpha)); p.end()
            if self.background_item and self.background_item.scene(): self.background_item.setPixmap(dimmed)
            else:
                self.background_item = QGraphicsPixmapItem(dimmed); self.background_item.setZValue(-80)
                self.portrait_scene.addItem(self.background_item)
            self.background_item.setPos(0, UI_PADDING_TOP)
        except Exception as e: self.logger.error(f"Error in set_background_image: {e}")

    def register_undo_action(self, desc, uf, rf):
        if not self._suppress_undo_registration:
            self.state_manager.add_undo_action("portrait_edit", desc, uf, rf)
        self.update_undo_redo_buttons(); self._refresh_portrait_controls_enabled()

    def _refresh_portrait_controls_enabled(self):
        has_items = any(isinstance(i, ResizablePixmapItem) for i in self.portrait_scene.items())
        can_undo, can_redo = self.state_manager.can_undo(), self.state_manager.can_redo()
        active = has_items or can_undo or can_redo
        self.done_button.setEnabled(has_items)
        self.snap_toggle_button.setEnabled(active)
        self.show_placeholders_checkbox.setEnabled(True)
        if hasattr(self, 'transparency_slider'):
            self.transparency_slider.setEnabled(True)
            self.transparency_slider.setVisible(self.show_placeholders_checkbox.isChecked())
        self.undo_button.setEnabled(can_undo); self.redo_button.setEnabled(can_redo)
        self.delete_button.setEnabled(has_items and bool(self.portrait_scene.selectedItems()))
        if hasattr(self, 'reset_state_button'):
            file_loaded = bool(self.media_processor.input_file_path or self.snapshot_path)
            self.reset_state_button.setEnabled(file_loaded)

    def _check_dependencies(self):
        is_valid, missing_path, error = DependencyDoctor.check_ffmpeg(self.base_dir)
        if not is_valid:
            QMessageBox.critical(self, "Missing Dependencies", f"FFmpeg is missing or invalid:\n{missing_path}\n\nError: {error}\n\nPlease reinstall.")
            sys.exit(1)

    def _auto_save_state(self):
        if not self._dirty: return
        try:
            items_data = []
            for item in self.portrait_scene.items():
                if isinstance(item, ResizablePixmapItem) and item.assigned_role:
                    item_data = self._get_item_state(item)
                    item_data['role'] = item.assigned_role
                    if self.snapshot_path:
                        item_data['crop_rect'] = [item.crop_rect.x(), item.crop_rect.y(), item.crop_rect.width(), item.crop_rect.height()]
                        items_data.append(item_data)
            if not items_data:
                if os.path.exists(self._autosave_file):
                    try: os.unlink(self._autosave_file)
                    except: pass
                return
            state = {
                'timestamp': time.time(),
                'input_file': self.media_processor.input_file_path,
                'snapshot': self.snapshot_path,
                'items': items_data
            }
            with open(self._autosave_file, 'w') as f:
                json.dump(state, f)
        except Exception as e:
            self.logger.error(f"Auto-save failed: {e}")

    def _check_restore(self):
        if os.path.exists(self._autosave_file):
            try:
                with open(self._autosave_file, 'r') as f:
                    data = json.load(f)
                if not data.get('items'):
                    try: os.unlink(self._autosave_file)
                    except: pass
                    return
                if 'input_file' not in data or 'snapshot' not in data: return
                if not os.path.exists(data['input_file']) or not os.path.exists(data['snapshot']):
                    return
                reply = QMessageBox.question(self, "Restore Session", 
                                           "An unsaved session was found. Restore it?",
                                           QMessageBox.Yes | QMessageBox.No)
                if reply == QMessageBox.Yes:
                    self.load_file(data['input_file'])
                    self.snapshot_path = data['snapshot']
                    if os.path.exists(self.snapshot_path):
                        pixmap = QPixmap(self.snapshot_path)
                        if not pixmap.isNull():
                            self.set_background_image(pixmap)
                            for item_data in data.get('items', []):
                                rect = QRectF(*item_data['crop_rect'])
                                crop_pix = pixmap.copy(rect.toRect())
                                item = ResizablePixmapItem(crop_pix, rect)
                                item.current_width = item_data['width']
                                item.current_height = item_data['height']
                                item.setPos(item_data['x'], item_data['y'])
                                item.setZValue(item_data['z'])
                                item.assigned_role = item_data['role']
                                item.update_handle_positions()
                                self.portrait_scene.addItem(item)
                                item.item_changed.connect(lambda i=item: self._handle_item_changed(i))
                                self.modified_roles.add(item.assigned_role)
                            self._mark_dirty()
                            self.logger.info("Session restored.")
                else:
                    try: os.unlink(self._autosave_file)
                    except: pass
            except Exception as e:
                self.logger.error(f"Restore failed: {e}")

    def _change_z_order(self, delta):
        selected = [i for i in self.portrait_scene.selectedItems() if isinstance(i, ResizablePixmapItem)]
        if not selected: return
        all_items = [i for i in self.portrait_scene.items() if isinstance(i, ResizablePixmapItem)]
        all_items.sort(key=lambda i: i.zValue())
        data = []
        for item in selected:
            current_z = item.zValue()
            new_z = current_z
            try:
                idx = all_items.index(item)
                if delta > 0:
                    if idx < len(all_items) - 1:
                        new_z = all_items[idx + 1].zValue() + 1
                else:
                    if idx > 0:
                        new_z = all_items[idx - 1].zValue() - 1
            except ValueError:
                new_z = current_z + delta
            if new_z != current_z:
                data.append({'item': item, 'old_z': current_z, 'new_z': new_z})
        if not data: return
        desc = "Move Up Layer" if delta > 0 else "Move Down Layer"
        self.register_undo_action(desc, lambda d=data: self._apply_z_order(d, False), lambda d=data: self._apply_z_order(d, True))
        self._apply_z_order(data, True)

    def raise_selected_item(self):
        self._change_z_order(1)

    def lower_selected_item(self):
        self._change_z_order(-1)

    def _apply_z_order(self, dl, use_new):
        """[FIX Duplication] Consolidated Z-order application logic."""
        for d in dl:
            d['item'].setZValue(d['new_z'] if use_new else d['old_z'])
            if d['item'].assigned_role: self.modified_roles.add(d['item'].assigned_role)
        self._mark_dirty()
        if self.portrait_scene:
            self.portrait_scene.update()
        self.on_selection_changed()
        return True

    def update_undo_redo_buttons(self):
        self._refresh_portrait_controls_enabled()
        has_sel = bool(self.portrait_scene.selectedItems())
        if hasattr(self, 'raise_button'): self.raise_button.setEnabled(has_sel)
        if hasattr(self, 'lower_button'): self.lower_button.setEnabled(has_sel)

    def showEvent(self, event):
        super().showEvent(event)
        self.portrait_view.fit_to_scene()
        self._position_refine_selection_hint()
        if hasattr(self, '_update_upload_hint_responsive'):
            self._update_upload_hint_responsive()
        QTimer.singleShot(200, self._check_restore)
        if not self._autosave_timer.isActive():
            self._autosave_timer.start()

    def closeEvent(self, event):
        if self._confirm_discard_changes():
            if hasattr(self, '_autosave_file') and os.path.exists(self._autosave_file):
                try: os.unlink(self._autosave_file)
                except: pass
            try: cleanup_temp_snapshots()
            except: pass
            super().closeEvent(event)
        else:
            event.ignore()

    def _deferred_launch_main_app(self):
        try:
            updates = {}
            if self.media_processor.input_file_path:
                updates["input_file"] = self.media_processor.input_file_path
            if self.media_processor.original_resolution:
                updates["resolution"] = self.media_processor.original_resolution
            StateTransfer.update_state(updates)
            if hasattr(self, '_autosave_file') and os.path.exists(self._autosave_file):
                try: os.unlink(self._autosave_file)
                except: pass
            cleanup_temp_snapshots()
        except Exception as e:
            self.logger.error(f"Error preparing handoff: {e}")
            pass
        try: 
            subprocess.Popen([sys.executable, "-B", os.path.join(self.base_dir, 'app.py')], cwd=self.base_dir)
        except Exception as e:
            self.logger.critical(f"Failed to launch main app: {e}")
            pass
        QApplication.instance().quit()

    def _get_persistence_extras(self): return {}

    def _mark_dirty(self, is_dirty=True):
        self._dirty = is_dirty
        self._refresh_portrait_controls_enabled()
        self._refresh_done_button()

    def _refresh_done_button(self):
        has_items = any(isinstance(i, ResizablePixmapItem) for i in self.portrait_scene.items())
        self.done_button.setEnabled(has_items); self.done_button.setText("FINISH & SAVE *" if self._dirty else "FINISH & SAVE")

    def _confirm_discard_changes(self):
        if not self._dirty:
            return True
        msg = QMessageBox(self)
        msg.setIcon(QMessageBox.Question)
        msg.setWindowTitle("Unsaved Changes")
        msg.setText("Discard changes?")
        msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
        msg.setDefaultButton(QMessageBox.No)
        for btn in msg.findChildren(QPushButton):
            btn.setCursor(Qt.PointingHandCursor)
        return msg.exec_() == QMessageBox.Yes

    def get_title_info(self): return self.base_title

    def _format_time(self, ms):
        ts = int(ms / 1000); return f"{ts // 60:02d}:{ts % 60:02d}"

    def update_ui(self):
        if not self.media_processor.media: return
        if not self.is_scrubbing:
            curr = self.media_processor.get_time()
            if self.position_slider.isEnabled(): self.position_slider.setValue(curr)
        self.update_time_labels()

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
        for timer in self._item_edit_timers.values(): timer.stop()
        self._in_undo_redo = True
        self._suppress_undo_registration = True
        try:
            if self.state_manager.undo():
                self._mark_dirty()
                self.status_label.setText("Undo performed")
        finally:
            QTimer.singleShot(100, self._end_undo_redo)
        self.update_undo_redo_buttons()

    def redo(self):
        if self.enhanced_logger:
            self.enhanced_logger.log_button_click("Redo", self.state_manager.get_redo_description())
        for timer in self._item_edit_timers.values(): timer.stop()
        self._in_undo_redo = True
        self._suppress_undo_registration = True
        try:
            if self.state_manager.redo():
                self._mark_dirty()
                self.status_label.setText("Redo performed")
        finally:
            QTimer.singleShot(100, self._end_undo_redo)
        self.update_undo_redo_buttons()

    def _end_undo_redo(self):
        self._in_undo_redo = False
        self._suppress_undo_registration = False
        self.update_undo_redo_buttons()

def main():
    try:
        QApplication.setAttribute(Qt.AA_EnableHighDpiScaling)
        QApplication.setAttribute(Qt.AA_UseHighDpiPixmaps)
        log_dir = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "logs")
        os.makedirs(log_dir, exist_ok=True)
        enhanced_logger_instance = setup_logger()
        logger = enhanced_logger_instance.base_logger
        logger.info("Application starting...")

        def global_exception_handler(exc_type, exc_value, exc_traceback):
            if issubclass(exc_type, (KeyboardInterrupt, SystemExit)):
                sys.__excepthook__(exc_type, exc_value, exc_traceback)
                return
            error_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
            logger.critical(f"Unhandled Exception: {error_msg}")
            print(error_msg, file=sys.stderr)
            try:
                QMessageBox.critical(None, "Critical Error", f"An unhandled error occurred:\n{exc_value}")
            except:
                pass
        sys.excepthook = global_exception_handler

        import time
        pid_retries = 3
        success = False
        pid_handle = None
        for attempt in range(pid_retries):
            success, pid_handle = ProcessManager.acquire_pid_lock("fortnite_crop_tool")
            if success:
                break
            time.sleep(0.5)
        if not success:
            logger.warning("Another instance of Crop Tool is running.")
            app = QApplication(sys.argv)
            QMessageBox.information(None, "Already Running", "Crop Tool is already running.")
            sys.exit(0)
        app = QApplication(sys.argv)

        from config import UNIFIED_STYLESHEET
        app.setStyleSheet(UNIFIED_STYLESHEET)
        file_path = sys.argv[1] if len(sys.argv) > 1 else None
        player = CropApp(logger, enhanced_logger_instance, file_path=file_path)
        player.show()
        try:
            ret = app.exec_()
        except Exception as event_loop_err:
            logger.critical(f"Crash in event loop: {event_loop_err}", exc_info=True)
            ret = 1
        if pid_handle:
            pid_handle.close()
        sys.exit(ret)
    except Exception as e:
        if 'logger' in locals():
            logger.critical(f"Unhandled exception in main: {e}", exc_info=True)
        else:
            print(f"Caught unhandled exception in main: {e}")
            traceback.print_exc()
    finally:
        print("--- Crop Tools main() finished ---")
if __name__ == '__main__':
    main()


