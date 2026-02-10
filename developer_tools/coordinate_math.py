"""
Centralized coordinate transformation math for Fortnite Video Software.
Ensures perfect mathematical alignment between any source resolution (16:9)
and the target 9:16 portrait output.
"""

import math
import re
from typing import Tuple

def scale_round(val: float) -> int:
    """Consistent rounding to nearest integer using standard round()."""
    return int(round(val))

def outward_round_rect(x: float, y: float, w: float, h: float) -> Tuple[int, int, int, int]:
    """
    Rounds a rectangle strictly outwards:
    Left/Top edges floor, Right/Bottom edges ceil.
    This ensures the selection never shrinks.
    """
    ix = int(math.floor(x))
    iy = int(math.floor(y))
    iw = int(math.ceil(x + w)) - ix
    ih = int(math.ceil(y + h)) - iy
    return ix, iy, max(1, iw), max(1, ih)

def get_resolution_ints(res_str: str) -> Tuple[int, int]:
    """Parses resolution string using robust regex."""
    if not res_str:
        return 1920, 1080
    try:
        match = re.search(r'(\d+)\s*[x:X\s]\s*(\d+)', str(res_str))
        if match:
            return int(match.group(1)), int(match.group(2))
    except (ValueError, IndexError):
        pass
    return 1920, 1080
PORTRAIT_W = 1080
PORTRAIT_H = 1920
INTERNAL_W = 1280
INTERNAL_H = 1920
UI_PADDING_TOP = 150
UI_PADDING_BOTTOM = 150
UI_CONTENT_H = PORTRAIT_H - UI_PADDING_TOP - UI_PADDING_BOTTOM
UI_TO_INTERNAL_SCALE = float(INTERNAL_W) / float(PORTRAIT_W)

def transform_to_content_area(rect: Tuple[float, float, float, float],
                            original_resolution: str) -> Tuple[float, float, float, float]:
    """
    Transforms coordinates from original video pixels to the 1080x1620 UI content area.
    """
    in_w, in_h = get_resolution_ints(original_resolution)
    x, y, w, h = rect
    scale = float(INTERNAL_H) / float(in_h)
    scaled_w = in_w * scale
    crop_x = (scaled_w - INTERNAL_W) / 2.0
    internal_x = (x * scale) - crop_x
    internal_y = (y * scale)
    internal_w = w * scale
    internal_h = h * scale
    ui_scale = 1.0 / UI_TO_INTERNAL_SCALE
    ui_x = internal_x * ui_scale
    ui_y = (internal_y * ui_scale) - UI_PADDING_TOP 
    ui_w = internal_w * ui_scale
    ui_h = internal_h * ui_scale
    return (ui_x, ui_y, ui_w, ui_h)

def inverse_transform_from_content_area(rect: Tuple[float, float, float, float],
                                        original_resolution: str) -> Tuple[float, float, float, float]:
    """
    Transforms logical UI coordinates (1080x1620) back to original video pixels.
    Inverts the centering and scaling logic.
    """
    in_w, in_h = get_resolution_ints(original_resolution)
    ui_x, ui_y, ui_w, ui_h = rect
    full_ui_y = ui_y + UI_PADDING_TOP
    internal_x = ui_x * UI_TO_INTERNAL_SCALE
    internal_y = full_ui_y * UI_TO_INTERNAL_SCALE
    internal_w = ui_w * UI_TO_INTERNAL_SCALE
    internal_h = ui_h * UI_TO_INTERNAL_SCALE
    scale = float(INTERNAL_H) / float(in_h)
    scaled_w = in_w * scale
    crop_x = (scaled_w - INTERNAL_W) / 2.0
    orig_x = (internal_x + crop_x) / scale
    orig_y = internal_y / scale
    orig_w = internal_w / scale
    orig_h = internal_h / scale
    final_x = max(0.0, min(orig_x, float(in_w) - 1.0))
    final_y = max(0.0, min(orig_y, float(in_h) - 1.0))
    final_w = max(1.0, min(orig_w, float(in_w) - final_x))
    final_h = max(1.0, min(orig_h, float(in_h) - final_y))
    return (final_x, final_y, final_w, final_h)

def inverse_transform_from_content_area_int(rect: Tuple[int, int, int, int],
                                            original_resolution: str) -> Tuple[int, int, int, int]:
    fx, fy, fw, fh = inverse_transform_from_content_area(
        (float(rect[0]), float(rect[1]), float(rect[2]), float(rect[3])), original_resolution
    )
    return outward_round_rect(fx, fy, fw, fh)

def transform_to_content_area_int(rect: Tuple[int, int, int, int],
                                original_resolution: str) -> Tuple[int, int, int, int]:
    fx, fy, fw, fh = transform_to_content_area(
        (float(rect[0]), float(rect[1]), float(rect[2]), float(rect[3])), original_resolution
    )
    return outward_round_rect(fx, fy, fw, fh)

def clamp_overlay_position(x: float, y: float, width: float, height: float, padding_top_ui: int = 150, padding_bottom_ui: int = 150) -> Tuple[float, float]:
    """Clamp overlay position to screen bounds in 1280x1920 backend canvas space."""
    backend_scale = float(INTERNAL_W) / float(PORTRAIT_W)
    min_y = float(padding_top_ui) * backend_scale
    max_y = max(min_y, float(INTERNAL_H) - height - (float(padding_bottom_ui) * backend_scale))
    clamped_x = max(0.0, min(float(x), float(INTERNAL_W) - width))
    clamped_y = max(min_y, min(float(y), max_y))
    return clamped_x, clamped_y

def validate_crop_rect(rect: Tuple[int, int, int, int], original_resolution: str) -> Tuple[bool, str]:
    x, y, w, h = rect
    if w <= 0 or h <= 0: return False, f"Invalid rectangle dimensions: {w}x{h}"
    in_w, in_h = get_resolution_ints(original_resolution)
    if x < 0 or y < 0 or x + w > in_w or y + h > in_h:
        return False, f"Rectangle {rect} exceeds source dimensions {in_w}x{in_h}"
    return True, ""

def scale_rect(rect: Tuple[float, float, float, float], scale_factor: float) -> Tuple[float, float, float, float]:
    x, y, w, h = rect
    return (x, y, w * scale_factor, h * scale_factor)

def scale_rect_int(rect: Tuple[int, int, int, int], scale_factor: float) -> Tuple[int, int, int, int]:
    x, y, w, h = rect
    return (x, y, int(math.ceil(w * scale_factor)), int(math.ceil(h * scale_factor)))
TARGET_W = INTERNAL_W
TARGET_H = INTERNAL_H
CONTENT_W = PORTRAIT_W
CONTENT_H = UI_CONTENT_H
PADDING_TOP = UI_PADDING_TOP
BACKEND_SCALE = float(INTERNAL_W) / float(PORTRAIT_W)
