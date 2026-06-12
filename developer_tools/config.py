import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')

from enum import Enum

class WizardState(Enum):
    UPLOAD = "UPLOAD VIDEO"
    FIND_HUD = "FIND HUD FRAME"
    REFINE = "REFINE BOX"
    COMPOSER = "PORTRAIT COMPOSER"
    READY = "CONFIG READY"

class CV_HEURISTICS:
    MINIMAP_X_RATIO_W = 0.65
    MINIMAP_Y_RATIO_H = 0.0
    MINIMAP_W_RATIO_W = 0.35
    MINIMAP_H_RATIO_H = 0.40
    HP_COLOR_X_RATIO_W = 0.0
    HP_COLOR_Y_RATIO_H = 0.70
    HP_COLOR_W_RATIO_W = 0.50
    HP_COLOR_H_RATIO_H = 0.30
    LOOT_BOX_X_RATIO_W = 0.50
    LOOT_BOX_Y_RATIO_H = 0.70
    LOOT_BOX_W_RATIO_W = 0.50
    LOOT_BOX_H_RATIO_H = 0.30
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
CROPPING_HINT_TEXT = 'Hit "START CROPPING" button to begin!'
HUD_SAFE_PADDING = {
    "stats": {"left": -1},
    "loot": {"right": 1},
    "1920x1080": {"top": 12, "bottom": 12, "left": 15, "right": 15}
}

def get_hud_padding(resolution):
    tk = resolution
    padding = HUD_SAFE_PADDING.get(tk, {})
    if not padding:
        padding = HUD_SAFE_PADDING.get(resolution, {"top": 10, "bottom": 10, "left": 10, "right": 10})
    return padding

def get_tech_key_from_role(role: str) -> str:
    if not role:
        return "unknown"
    return HUD_ELEMENT_KEYS_BY_NAME.get(role, "unknown")

class UI_COLORS:
    BACKGROUND_DARK = "#1a252f"
    BACKGROUND_MEDIUM = "#2c3e50"
    BACKGROUND_LIGHT = "#34495e"
    TEXT_PRIMARY = "#ecf0f1"
    TEXT_SECONDARY = "#bdc3c7"
    TEXT_DISABLED = "#7f8c8d"
    TEXT_ACCENT = "#3498db"
    TEXT_WARNING = "#f39c12"
    TEXT_DANGER = "#e74c3c"
    BORDER_DARK = "#1a252f"
    BORDER_MEDIUM = "#34495e"
    BORDER_ACCENT = "#7DD3FC"
    BORDER_DANGER = "#c0392b"
    BUTTON_DEFAULT = "#3a8db0"
    BUTTON_HOVER = "#2d7da1"
    BUTTON_PRESSED = "#1a5276"
    BUTTON_DISABLED = "#4a5a63"
    PRIMARY = "#3a8db0"
    PRIMARY_HOVER = "#2d7da1"
    PRIMARY_PRESSED = "#1a5276"
    PRIMARY_BORDER = "rgba(0, 0, 0, 0.6)"
    SUCCESS = "#2ecc71"
    SUCCESS_HOVER = "#27ae60"
    SUCCESS_PRESSED = "#1b6d26"
    SUCCESS_BORDER = "rgba(0, 0, 0, 0.6)"
    WARNING = "#e67e22"
    WARNING_HOVER = "#d35400"
    WARNING_PRESSED = "#a04000"
    WARNING_BORDER = "rgba(0, 0, 0, 0.6)"
    DANGER = "#e74c3c"
    DANGER_HOVER = "#c0392b"
    DANGER_PRESSED = "#8e2317"
    DANGER_BORDER = "rgba(0, 0, 0, 0.6)"
    ACCENT = "#3498db"
    ACCENT_HOVER = "#2980b9"
    ACCENT_PRESSED = "#1a5276"
    ACCENT_BORDER = "#1f3545"
    TEAL = "#1abc9c"
    TEAL_HOVER = "#16a085"
    TEAL_PRESSED = "#117a65"
    TEAL_BORDER = "rgba(0, 0, 0, 0.6)"
    PANEL_DARK = "#2c3e50"
    OVERLAY_DIM = "rgba(0, 0, 0, 180)"
    TRANSPARENT = "transparent"
    HANDLE_BLUE = "#3498db"
    HANDLE_ORANGE = "#e67e22"
    MARCHING_ANTS = "#FFFFFF"
    SELECTION_GREEN = "#2ecc71"
    OPACITY_DIM_HIGH = 200
    OPACITY_DIM_MED = 150
    OPACITY_DIM_LOW = 120
    OPACITY_PH_ALPHA = 95

class UI_LAYOUT:
    WIZARD_HEADER_HEIGHT = 140
    PROGRESS_BAR_HEIGHT = 24
    TIME_LABEL_WIDTH = 70
    BUTTON_HEIGHT = 26
    PORTRAIT_BUTTON_HEIGHT = 26
    BUTTON_PADDING_H = "2px 6px"
    BUTTON_FONT_SIZE = "9px"
    PORTRAIT_BUTTON_FONT_SIZE = "9px"
    PORTRAIT_CHECKBOX_FONT_SIZE = "10px"
    PORTRAIT_SLIDER_WIDTH = 120
    BTN_WIDTH_SM = 70
    BTN_WIDTH_MD = 90
    BTN_WIDTH_LG = 110
    BUTTON_BORDER_RADIUS = "4px"
    BUTTON_BORDER_BOTTOM_WIDTH = "1px"
    BUTTON_LARGE_PADDING_H = "6px 20px"
    BUTTON_LARGE_FONT_SIZE = "14px"
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
    VIDEO_VIEW_GUIDANCE_BLINK_INTERVAL = 1200
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
def get_stylesheet():
    try:
        import os
        qss_path = os.path.join(os.path.dirname(__file__), "theme.qss")
        if os.path.exists(qss_path):
            with open(qss_path, "r", encoding="utf-8") as f:
                content = f.read()
                for k, v in UI_COLORS.__dict__.items():
                    if not k.startswith("__"):
                        content = content.replace(f"{{UI_COLORS.{k}}}", str(v))
                for k, v in UI_LAYOUT.__dict__.items():
                    if not k.startswith("__"):
                        content = content.replace(f"{{UI_LAYOUT.{k}}}", str(v))
                # Inject absolute path to the developer_tools directory so that
                # url() references in theme.qss (e.g. arrow SVGs) resolve
                # correctly regardless of the application's working directory.
                # Qt requires forward slashes even on Windows.
                theme_dir = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
                content = content.replace("{THEME_DIR}", theme_dir)
                return content
    except Exception as e:
        print(f"Error loading stylesheet: {e}")
    return ""
PORTRAIT_WINDOW_STYLESHEET = ""
UNIFIED_STYLESHEET = get_stylesheet()
CROP_APP_STYLESHEET = UNIFIED_STYLESHEET
