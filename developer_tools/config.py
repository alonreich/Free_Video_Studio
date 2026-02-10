from enum import Enum

class WizardState(Enum):
    UPLOAD = "UPLOAD VIDEO"
    FIND_HUD = "FIND HUD FRAME"
    REFINE = "REFINE BOX"
    COMPOSER = "PORTRAIT COMPOSER"
    READY = "CONFIG READY"

class CV_HEURISTICS:
    MINIMAP_X_RATIO_W = 0.70
    MINIMAP_Y_RATIO_H = 0.0
    MINIMAP_W_RATIO_W = 0.30
    MINIMAP_H_RATIO_H = 0.40
    HP_COLOR_X_RATIO_W = 0.0
    HP_COLOR_Y_RATIO_H = 0.60
    HP_COLOR_W_RATIO_W = 0.55
    HP_COLOR_H_RATIO_H = 0.40
    LOOT_BOX_X_RATIO_W = 0.40
    LOOT_BOX_Y_RATIO_H = 0.60
    LOOT_BOX_W_RATIO_W = 0.60
    LOOT_BOX_H_RATIO_H = 0.40
    MATCH_THRESHOLD = 0.50
    SHRINK_WRAP_PADDING = 8
HUD_ELEMENT_MAPPINGS = {
    "loot": "Loot Area",
    "stats": "Mini Map + Stats",
    "normal_hp": "Own Health Bar (HP)",
    "boss_hp": "Boss HP (For When You Are The Boss Character)",
    "team": "Teammates health Bars (HP)",
    "spectating": "Spectating Eye"
}
HUD_ELEMENT_KEYS_BY_NAME = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}

def get_tech_key_from_role(role: str) -> str:
    if not role:
        return "unknown"
    return HUD_ELEMENT_KEYS_BY_NAME.get(role, "unknown")

class UI_COLORS:
    BACKGROUND_DARK = "#111827"
    BACKGROUND_MEDIUM = "#1F2937"
    BACKGROUND_LIGHT = "#374151"
    TEXT_PRIMARY = "#F9FAFB"
    TEXT_SECONDARY = "#E5E7EB"
    TEXT_DISABLED = "#9CA3AF"
    TEXT_ACCENT = "#2563EB"
    TEXT_WARNING = "#FBBF24"
    TEXT_DANGER = "#FEE2E2"
    BORDER_DARK = "#374151"
    BORDER_MEDIUM = "#4B5563"
    BORDER_ACCENT = "#2563EB"
    BORDER_DANGER = "#991B1B"
    BUTTON_DEFAULT = "#374151"
    BUTTON_HOVER = "#4B5563"
    BUTTON_PRESSED = "#1F2937"
    BUTTON_DISABLED = "#6B7280"
    PRIMARY = "#266B89"
    PRIMARY_HOVER = "#2F82A8"
    PRIMARY_PRESSED = "#1D526B"
    PRIMARY_BORDER = "#164157"
    SUCCESS = "#10B981"
    SUCCESS_HOVER = "#34D399"
    SUCCESS_PRESSED = "#059669"
    SUCCESS_BORDER = "#047857"
    WARNING = "#F59E0B"
    WARNING_HOVER = "#FBBF24"
    WARNING_PRESSED = "#D97706"
    WARNING_BORDER = "#92400E"
    DANGER = "#7F1D1D"
    DANGER_HOVER = "#991B1B"
    DANGER_PRESSED = "#5B0F0F"
    DANGER_BORDER = "#4C0519"
    ACCENT = "#0D9488"
    ACCENT_HOVER = "#14B8A6"
    ACCENT_PRESSED = "#0F766E"
    ACCENT_BORDER = "#0F766E"
    TEAL = "#318181"
    TEAL_HOVER = "#3B9BA8"
    TEAL_PRESSED = "#256161"
    TEAL_BORDER = "#1D4D4D"
    OVERLAY_DIM = "rgba(0, 0, 0, 120)"
    TRANSPARENT = "transparent"
    HANDLE_BLUE = "#3498db"
    HANDLE_ORANGE = "#e67e22"
    MARCHING_ANTS = "white"
    SELECTION_GREEN = "#10B981"
    OPACITY_DIM_HIGH = 180
    OPACITY_DIM_MED = 120
    OPACITY_DIM_LOW = 90
    OPACITY_PH_ALPHA = 80

class UI_LAYOUT:
    WIZARD_HEADER_HEIGHT = 130
    PROGRESS_BAR_HEIGHT = 25
    TIME_LABEL_WIDTH = 60
    BUTTON_HEIGHT = 29
    PORTRAIT_BUTTON_HEIGHT = 29
    BUTTON_PADDING_H = "1px 10px"
    BUTTON_FONT_SIZE = "9px"
    PORTRAIT_BUTTON_FONT_SIZE = "9px"
    PORTRAIT_CHECKBOX_FONT_SIZE = "10px"
    PORTRAIT_SLIDER_WIDTH = 110
    BUTTON_BORDER_RADIUS = "3px"
    BUTTON_BORDER_BOTTOM_WIDTH = "3px"
    BUTTON_LARGE_PADDING_H = "2px 12px"
    BUTTON_LARGE_FONT_SIZE = "12px"
    SCROLLBAR_SIZE = "31px"
    SCROLLBAR_HANDLE_MIN_LENGTH = "40px"
    SCROLLBAR_BORDER_RADIUS = "8px"
    SCROLLBAR_HANDLE_BORDER = "2px"
    PORTRAIT_TOOLBAR_MARGINS = "5px"
    PORTRAIT_TOOLBAR_SPACING = "5px"
    PORTRAIT_TOOLBAR_BORDER_RADIUS = "8px"
    GRAPHICS_HANDLE_SIZE = 25
    GRAPHICS_TEXT_PADDING = 40
    GRAPHICS_TEXT_HEIGHT_PAD = 10
    GRAPHICS_TEXT_FONT_SIZE = 28
    GRAPHICS_TEXT_OFFSET_Y = 15
    GRAPHICS_ITEM_MIN_SIZE = 20
    PORTRAIT_TOP_BAR_HEIGHT = 150
    PORTRAIT_BASE_WIDTH = 1080
    PORTRAIT_BASE_HEIGHT = 1920
    PORTRAIT_BOTTOM_PADDING = 150
    PORTRAIT_CONTENT_HEIGHT = 1620
    ROLE_TOOLBAR_PADDING = 16
    ROLE_TOOLBAR_OFFSET = 12
    ROLE_TOOLBAR_EDGE_MARGIN = 8

class UI_BEHAVIOR:
    SNAP_THRESHOLD = 15
    SNAP_SMOOTHING_ALPHA = 0.8
    SNAP_RESIZE_SMOOTHING_ALPHA = 0.45
    SNAP_CENTER_THRESHOLD = 80
    SNAP_GUIDE_THRESHOLD = 95
    SNAP_CENTER_LOCK_THRESHOLD = 60
    SNAP_REPEAT_SUPPRESS_COUNT = 8
    ANT_DASH_PATTERN = "6, 6"
    ANT_DASH_INTERVAL = 100
    MAGNIFIER_SIZE = 180
    MAGNIFIER_ZOOM = 3.0
    MAGNIFIER_OFFSET = 60
    MAGNIFIER_CURSOR_CROSS_SIZE = 10
    MAGNIFIER_BORDER_WIDTH = 3
    MAGNIFIER_BORDER_COLOR = "#00FF00"
    MAGNIFIER_CROSS_COLOR = "red"
    SNAPSHOT_RETRY_INTERVAL_MS = 100
    SNAPSHOT_MAX_RETRIES = 20
    SLIDER_UPDATE_INTERVAL_MS = 100
    VIDEO_VIEW_GUIDANCE_BLINK_INTERVAL = 700
    MAGIC_WAND_MAX_SECONDS = 8
    MAGIC_WAND_PREVIEW_DELAY_MS = 450
    UNDO_COALESCE_WINDOW_MS = 400
    SELECTION_MIN_SIZE = 10
    KEYBOARD_NUDGE_STEP = 0.4
    MIN_SCALE_FACTOR = 0.0001
Z_ORDER_MAP = {
    'main': 0,
    'loot': 10,
    'normal_hp': 20,
    'boss_hp': 20,
    'stats': 30,
    'team': 40,
    'spectating': 100
}
UNIFIED_STYLESHEET = f"""
QWidget {{
    background-color: {UI_COLORS.BACKGROUND_DARK};
    color: {UI_COLORS.TEXT_PRIMARY};
    font-family: 'Segoe UI', 'Roboto', 'Inter', -apple-system, sans-serif;
    font-size: 14px;
    border: none;
}}
QLabel {{ 
    color: {UI_COLORS.TEXT_SECONDARY};
    font-weight: 500;
    padding: 4px;
}}
QLabel.title {{
    color: {UI_COLORS.TEXT_PRIMARY};
    font-weight: 700;
    font-size: 16px;
}}
QLabel.status {{ color: {UI_COLORS.TEXT_DISABLED}; font-weight: 600; }}
QLabel.info {{ color: {UI_COLORS.TEXT_DISABLED}; }}
QLabel.italic {{ font-style: italic; color: {UI_COLORS.TEXT_ACCENT}; }}
QSlider::groove:horizontal {{ 
    border: 1px solid {UI_COLORS.BORDER_DARK}; 
    height: 16px; 
    background: {UI_COLORS.BACKGROUND_MEDIUM}; 
    margin: 2px 0; 
    border-radius: 4px;
}}
QSlider::handle:horizontal {{ 
    background: {UI_COLORS.PRIMARY};
    border: 2px solid {UI_COLORS.TEXT_PRIMARY}; 
    width: 22px; 
    height: 40px;
    margin: -12px 0; 
    border-radius: 11px;
}}
QSlider::handle:horizontal:hover {{ 
    background: {UI_COLORS.PRIMARY_HOVER}; 
    border: 2px solid {UI_COLORS.TEXT_PRIMARY};
}}
QProgressBar {{
    border: 1px solid {UI_COLORS.BORDER_DARK};
    border-radius: 6px;
    background-color: {UI_COLORS.BACKGROUND_DARK};
    text-align: center;
    color: {UI_COLORS.TEXT_SECONDARY};
    height: {UI_LAYOUT.PROGRESS_BAR_HEIGHT}px;
}}
QProgressBar::chunk {{
    background-color: {UI_COLORS.ACCENT};
    border-radius: 6px;
}}
QAbstractButton {{
    qproperty-cursor: "PointingHandCursor";
}}
QPushButton {{ 
    background-color: {UI_COLORS.BUTTON_DEFAULT}; 
    color: {UI_COLORS.TEXT_PRIMARY};
    border: 1px solid {UI_COLORS.BORDER_MEDIUM};
    padding: {UI_LAYOUT.BUTTON_PADDING_H};
    border-radius: {UI_LAYOUT.BUTTON_BORDER_RADIUS};
    font-weight: 600; 
    font-size: {UI_LAYOUT.BUTTON_FONT_SIZE};
    min-height: {UI_LAYOUT.BUTTON_HEIGHT}px;
    max-height: {UI_LAYOUT.BUTTON_HEIGHT}px;
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.BACKGROUND_DARK};
    qproperty-cursor: "PointingHandCursor";
}}
/* Force hand cursor on all interactive elements and specifically for message boxes */
QPushButton:hover, QAbstractButton:hover {{
    qproperty-cursor: "PointingHandCursor";
}}
QMessageBox QPushButton {{
    qproperty-cursor: "PointingHandCursor";
    min-width: 100px;
}}
/* Deep selector for buttons inside any widget to force the cursor */
QWidget QPushButton, QWidget QAbstractButton {{
    qproperty-cursor: "PointingHandCursor";
}}
QPushButton:hover {{ 
    background-color: {UI_COLORS.BUTTON_HOVER}; 
    border: 1px solid {UI_COLORS.BORDER_MEDIUM};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.BACKGROUND_DARK};
}}
QPushButton:pressed {{ 
    background-color: {UI_COLORS.BUTTON_PRESSED};
    border: 1px solid {UI_COLORS.BORDER_MEDIUM};
    border-bottom: 1px solid {UI_COLORS.BORDER_MEDIUM};
    padding-top: 2px;
    padding-left: 12px;
}}
QPushButton:disabled {{
    background-color: {UI_COLORS.BUTTON_DISABLED};
    color: {UI_COLORS.TEXT_DISABLED};
}}
QPushButton:checked {{
    background-color: {UI_COLORS.ACCENT};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.ACCENT_BORDER};
}}
QPushButton.primary {{
    background-color: {UI_COLORS.PRIMARY}; color: {UI_COLORS.TEXT_PRIMARY};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.PRIMARY_BORDER};
}}
QPushButton.primary:hover {{
    background-color: {UI_COLORS.PRIMARY_HOVER};
}}
QPushButton.primary:pressed {{
    background-color: {UI_COLORS.PRIMARY_PRESSED};
    border-bottom: 1px solid {UI_COLORS.PRIMARY_PRESSED};
}}
QPushButton.primary:disabled {{
    background-color: {UI_COLORS.BUTTON_DISABLED};
    color: {UI_COLORS.TEXT_DISABLED};
    border-bottom: none;
}}
QPushButton.success {{
    background-color: {UI_COLORS.SUCCESS}; color: {UI_COLORS.TEXT_PRIMARY};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.SUCCESS_BORDER};
}}
QPushButton.success:hover {{
    background-color: {UI_COLORS.SUCCESS_HOVER};
}}
QPushButton.success:pressed {{
    background-color: {UI_COLORS.SUCCESS_PRESSED};
    border-bottom: 1px solid {UI_COLORS.SUCCESS_PRESSED};
}}
QPushButton.success:disabled {{
    background-color: {UI_COLORS.BUTTON_DISABLED};
    color: {UI_COLORS.TEXT_DISABLED};
    border-bottom: none;
}}
QPushButton.warning {{
    background-color: {UI_COLORS.WARNING}; color: {UI_COLORS.TEXT_PRIMARY};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.WARNING_BORDER};
}}
QPushButton.warning:hover {{
    background-color: {UI_COLORS.WARNING_HOVER};
}}
QPushButton.warning:pressed {{
    background-color: {UI_COLORS.WARNING_PRESSED};
    border-bottom: 1px solid {UI_COLORS.WARNING_PRESSED};
}}
QPushButton.warning:disabled {{
    background-color: {UI_COLORS.BUTTON_DISABLED};
    color: {UI_COLORS.TEXT_DISABLED};
    border-bottom: none;
}}
QPushButton.danger {{
    background-color: {UI_COLORS.DANGER}; color: {UI_COLORS.TEXT_DANGER};
    border: 1px solid {UI_COLORS.DANGER_BORDER};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.DANGER_BORDER};
}}
QPushButton.danger:hover {{
    background-color: {UI_COLORS.DANGER_HOVER};
    border: 1px solid {UI_COLORS.DANGER_BORDER};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.DANGER_BORDER};
}}
QPushButton.danger:pressed {{
    background-color: {UI_COLORS.DANGER_PRESSED};
    border: 1px solid {UI_COLORS.DANGER_PRESSED};
    border-bottom: 1px solid {UI_COLORS.DANGER_PRESSED};
}}
QPushButton.danger:disabled {{
    background-color: {UI_COLORS.BUTTON_DISABLED};
    color: {UI_COLORS.TEXT_DISABLED};
    border-bottom: none;
}}
QPushButton.accent {{
    background-color: {UI_COLORS.ACCENT}; color: {UI_COLORS.TEXT_PRIMARY};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.ACCENT_BORDER};
}}
QPushButton.accent:hover {{
    background-color: {UI_COLORS.ACCENT_HOVER};
}}
QPushButton.accent:pressed {{
    background-color: {UI_COLORS.ACCENT_PRESSED};
    border-bottom: 1px solid {UI_COLORS.ACCENT_PRESSED};
}}
QPushButton.accent:disabled {{
    background-color: {UI_COLORS.BUTTON_DISABLED};
    color: {UI_COLORS.TEXT_DISABLED};
    border-bottom: none;
}}
QPushButton.portrait-btn {{
    font-size: {UI_LAYOUT.PORTRAIT_BUTTON_FONT_SIZE};
    min-height: {UI_LAYOUT.PORTRAIT_BUTTON_HEIGHT}px;
    max-height: {UI_LAYOUT.PORTRAIT_BUTTON_HEIGHT}px;
}}
QPushButton.teal-btn {{
    background-color: {UI_COLORS.TEAL}; color: {UI_COLORS.TEXT_PRIMARY};
    border-bottom: {UI_LAYOUT.BUTTON_BORDER_BOTTOM_WIDTH} solid {UI_COLORS.TEAL_BORDER};
}}
QPushButton.teal-btn:hover {{
    background-color: {UI_COLORS.TEAL_HOVER};
}}
QPushButton.teal-btn:pressed {{
    background-color: {UI_COLORS.TEAL_PRESSED};
    border-bottom: 1px solid {UI_COLORS.TEAL_PRESSED};
}}
QMessageBox, QDialog {{
    background-color: {UI_COLORS.BACKGROUND_DARK};
    min-width: 400px;
}}
QMessageBox QLabel {{
    qproperty-alignment: 'AlignCenter';
    color: {UI_COLORS.TEXT_PRIMARY};
    font-size: 14px;
    padding: 20px;
}}
QDialog QPushButton, QMessageBox QPushButton {{
    min-width: 100px;
    margin: 5px;
}}
/* This targets the button container in standard message boxes */
QMessageBox {{
    dialogbuttonbox-buttons-have-icons: 0;
}}
QDialogButtonBox {{
    background: transparent;
}}
QCheckBox.portrait-chk {{
    font-size: {UI_LAYOUT.PORTRAIT_CHECKBOX_FONT_SIZE};
}}
QSlider.portrait-sld {{
    min-width: {UI_LAYOUT.PORTRAIT_SLIDER_WIDTH}px;
    max-width: {UI_LAYOUT.PORTRAIT_SLIDER_WIDTH}px;
    height: 25px;
    background: transparent;
}}
QSlider.portrait-sld::groove:horizontal {{
    border: 1px solid #111827;
    height: 4px;
    background: #374151;
    margin: 0px;
    border-radius: 2px;
}}
QSlider.portrait-sld::handle:horizontal {{
    background: #3B82F6;
    border: 1px solid #1E3A8A;
    width: 14px;
    height: 14px;
    margin: -5px 0;
    border-radius: 7px;
}}
QSlider.portrait-sld::handle:horizontal:hover {{
    background: #60A5FA;
    border-color: #FFFFFF;
}}
QCheckBox {{
    color: {UI_COLORS.TEXT_PRIMARY};
    spacing: 10px;
    font-weight: bold;
    background: transparent;
}}
QCheckBox::indicator {{
    width: 18px;
    height: 18px;
    border: 1px solid #4B5563;
    border-radius: 2px;
    background-color: #111827;
}}
QCheckBox::indicator:hover {{
    border: 1px solid #7DD3FC;
}}
QCheckBox::indicator:checked {{
    background-color: #266B89;
    border: 1px solid #7DD3FC;
    image: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path fill='white' d='M20.285 2l-11.285 11.567-5.286-5.011-3.714 3.716 9 8.728 15-15.285z'/></svg>");
}}
QCheckBox::indicator:checked:hover {{
    background-color: #2F82A8;
}}
QScrollBar:vertical {{
    background: {UI_COLORS.BACKGROUND_DARK};
    width: {UI_LAYOUT.SCROLLBAR_SIZE};
    border-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
    border: 1px solid {UI_COLORS.BORDER_DARK};
    margin: 22px 0 22px 0; /* Room for arrows */
}}
QScrollBar::handle:vertical {{
    background: #D1D5DB; /* Much lighter gray for high contrast */
    border-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
    min-height: {UI_LAYOUT.SCROLLBAR_HANDLE_MIN_LENGTH};
    border: 4px solid {UI_COLORS.BACKGROUND_DARK};
}}
QScrollBar::handle:vertical:hover {{ background: #FFFFFF; /* Pure white on hover */ }}
QScrollBar::handle:vertical:pressed {{ background: {UI_COLORS.PRIMARY}; }}
QScrollBar::add-line:vertical {{
    background: {UI_COLORS.BACKGROUND_MEDIUM};
    height: 22px;
    subcontrol-position: bottom;
    subcontrol-origin: margin;
    border: 1px solid {UI_COLORS.BORDER_DARK};
    border-bottom-left-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
    border-bottom-right-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
}}
QScrollBar::sub-line:vertical {{
    background: {UI_COLORS.BACKGROUND_MEDIUM};
    height: 22px;
    subcontrol-position: top;
    subcontrol-origin: margin;
    border: 1px solid {UI_COLORS.BORDER_DARK};
    border-top-left-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
    border-top-right-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
}}
QScrollBar::up-arrow:vertical {{
    image: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='white'><path d='M7 14l5-5 5 5z'/></svg>");
    width: 12px;
    height: 12px;
}}
QScrollBar::down-arrow:vertical {{
    image: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='white'><path d='M7 10l5 5 5-5z'/></svg>");
    width: 12px;
    height: 12px;
}}
QScrollBar:horizontal {{
    background: {UI_COLORS.BACKGROUND_DARK};
    height: {UI_LAYOUT.SCROLLBAR_SIZE};
    border-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
    border: 1px solid {UI_COLORS.BORDER_DARK};
    margin: 0 22px 0 22px; /* Room for arrows */
}}
QScrollBar::handle:horizontal {{
    background: #D1D5DB; /* Much lighter gray */
    border-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
    min-width: {UI_LAYOUT.SCROLLBAR_HANDLE_MIN_LENGTH};
    border: 4px solid {UI_COLORS.BACKGROUND_DARK};
}}
QScrollBar::handle:horizontal:hover {{ background: #FFFFFF; }}
QScrollBar::handle:horizontal:pressed {{ background: {UI_COLORS.PRIMARY}; }}
QScrollBar::add-line:horizontal {{
    background: {UI_COLORS.BACKGROUND_MEDIUM};
    width: 22px;
    subcontrol-position: right;
    subcontrol-origin: margin;
    border: 1px solid {UI_COLORS.BORDER_DARK};
    border-top-right-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
    border-bottom-right-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
}}
QScrollBar::sub-line:horizontal {{
    background: {UI_COLORS.BACKGROUND_MEDIUM};
    width: 22px;
    subcontrol-position: left;
    subcontrol-origin: margin;
    border: 1px solid {UI_COLORS.BORDER_DARK};
    border-top-left-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
    border-bottom-left-radius: {UI_LAYOUT.SCROLLBAR_BORDER_RADIUS};
}}
QScrollBar::left-arrow:horizontal {{
    image: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='white'><path d='M14 7l-5 5 5 5z'/></svg>");
    width: 12px;
    height: 12px;
}}
QScrollBar::right-arrow:horizontal {{
    image: url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='white'><path d='M10 17l5-5-5-5z'/></svg>");
    width: 12px;
    height: 12px;
}}
QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical,
QScrollBar::add-page:horizontal, QScrollBar::sub-page:horizontal {{
    background: none;
}}
#wizardFrame {{
    background-color: {UI_COLORS.BACKGROUND_MEDIUM};
    border-bottom: 2px solid {UI_COLORS.BORDER_DARK};
}}
#progressLabel {{
    background-color: {UI_COLORS.BACKGROUND_LIGHT};
    color: {UI_COLORS.TEXT_SECONDARY};
    padding: 2px 8px;
    border-radius: 4px;
    font-weight: 700;
    font-size: 10px;
    border: 1px solid {UI_COLORS.BORDER_MEDIUM};
    min-width: 120px;
}}
#progressLabel.completed {{
    background-color: {UI_COLORS.SUCCESS_PRESSED};
    color: {UI_COLORS.TEXT_PRIMARY};
    border-color: {UI_COLORS.SUCCESS_BORDER};
}}
#progressLabel.current {{
    background-color: {UI_COLORS.PRIMARY_PRESSED};
    color: {UI_COLORS.TEXT_PRIMARY};
    border-color: {UI_COLORS.PRIMARY};
}}
#controlsFrame {{
    background-color: {UI_COLORS.BACKGROUND_MEDIUM};
    padding: 4px;
    border-top: 1px solid {UI_COLORS.BORDER_DARK};
}}
#portraitFooter {{
    background-color: #203b4b;
    padding: 4px;
    border-top: 1px solid {UI_COLORS.BORDER_DARK};
}}
#portraitHeader {{
    background-color: #203b4b;
    padding: 4px;
    border-bottom: 1px solid {UI_COLORS.BORDER_DARK};
}}
#rightPane {{
    background-color: #203b4b;
}}
#centralDivider {{
    min-width: 18px;
    max-width: 18px;
    background-color: qlineargradient(x1:0, y1:0, x2:1, y2:0, 
        stop:0 #000000, 
        stop:0.1 #000000, 
        stop:0.4 #1F2937, 
        stop:0.6 #1F2937, 
        stop:0.9 #4B5563, 
        stop:1 #4B5563);
    border-left: 1px solid #000000;
    border-right: 1px solid #374151;
}}
#roleToolbar {{
    background-color: {UI_COLORS.OVERLAY_DIM};
    border: 1px solid {UI_COLORS.BORDER_MEDIUM};
    border-radius: {UI_LAYOUT.PORTRAIT_TOOLBAR_BORDER_RADIUS};
}}
#uploadHintContainer {{
    background-color: #000000;
    border: 2px solid #7DD3FC;
    border-radius: 10px;
}}
#uploadHintLabel {{
    color: #7DD3FC;
    font-family: Arial;
    font-size: 20px;
    font-weight: bold;
    padding: 6px 18px;
}}
#roleToolbar QPushButton {{
    background-color: {UI_COLORS.BUTTON_DEFAULT};
    color: {UI_COLORS.TEXT_PRIMARY};
    border: 1px solid {UI_COLORS.BORDER_MEDIUM};
    border-radius: 4px;
    padding: 6px 10px;
    font-weight: bold;
    font-size: 11px;
    min-height: {UI_LAYOUT.BUTTON_HEIGHT}px;
    max-height: {UI_LAYOUT.BUTTON_HEIGHT}px;
}}
#roleToolbar QPushButton:hover {{
    background-color: {UI_COLORS.BUTTON_HOVER};
}}
#roleToolbar QPushButton:pressed {{
    background-color: {UI_COLORS.BUTTON_PRESSED};
}}
"""

def get_stylesheet():
    try:
        import os
        qss_path = os.path.join(os.path.dirname(__file__), "theme.qss")
        if os.path.exists(qss_path):
            with open(qss_path, "r", encoding="utf-8") as f:
                return f.read()
    except:
        pass
    return ""
PORTRAIT_WINDOW_STYLESHEET = ""
CROP_APP_STYLESHEET = UNIFIED_STYLESHEET































