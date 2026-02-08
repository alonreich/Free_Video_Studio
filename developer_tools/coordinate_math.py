"""
Centralized coordinate transformation math for Fortnite Video Software.
Ensures consistent scaling between crop tool preview and final render.
"""

import math
from typing import Tuple
TARGET_W = 1280
TARGET_H = 1920
CONTENT_W = 1080
CONTENT_H = 1620
PADDING_TOP = 150
PADDING_BOTTOM = 150
BACKEND_SCALE = 1280.0 / 1080.0

def scale_round(val: float) -> int:
    """Consistent rounding to nearest integer, avoiding banker's rounding."""
    return int(math.floor(val + 0.5))

def outward_round_rect(x: float, y: float, w: float, h: float) -> Tuple[int, int, int, int]:
    """
    Rounds a rectangle outwards to the nearest pixel grid.
    Top-left corner (x, y) is floored.
    Bottom-right corner (x+w, y+h) is ceiled.
    [FIX #30] Ensures the resulting integer rect always contains the original float rect.
    """
    x_min = float(x)
    y_min = float(y)
    x_max = x_min + float(w)
    y_max = y_min + float(h)
    ix = int(math.floor(x_min))
    iy = int(math.floor(y_min))
    iw = int(math.ceil(x_max)) - ix
    ih = int(math.ceil(y_max)) - iy
    return ix, iy, max(1, iw), max(1, ih)

def transform_to_content_area(rect: Tuple[float, float, float, float],
                            original_resolution: str) -> Tuple[float, float, float, float]:
    """
    Transform coordinates from original video to 1080p portrait content area (1080x1620).
    Matches the filter chain in filter_builder.py:
    1. Scale to 1280:1920 (force_original_aspect_ratio=increase)
    2. Crop to 1280:1920 (center crop)
    3. Scale to 1080:1620
    4. Pad to 1080:1920 (black bars top/bottom)
    """
    if not original_resolution:
        return rect
    try:
        in_w, in_h = map(int, original_resolution.split('x'))
        if in_w <= 0 or in_h <= 0:
            return rect
    except (ValueError, AttributeError):
        return rect
    x, y, w, h = rect
    scale_w = 1280.0 / in_w
    scale_h = 1920.0 / in_h
    scale = max(scale_w, scale_h)
    scaled_w = in_w * scale
    scaled_h = in_h * scale
    crop_x = (scaled_w - 1280.0) / 2.0
    crop_y = (scaled_h - 1920.0) / 2.0
    rect_scaled_x = x * scale - crop_x
    rect_scaled_y = y * scale - crop_y
    rect_scaled_w = w * scale
    rect_scaled_h = h * scale
    ui_scale = 1080.0 / 1280.0
    final_x = rect_scaled_x * ui_scale
    final_y = rect_scaled_y * ui_scale
    final_w = rect_scaled_w * ui_scale
    final_h = rect_scaled_h * ui_scale
    final_w = max(0.0, final_w)
    final_h = max(0.0, final_h)
    return (final_x, final_y, final_w, final_h)

def transform_to_content_area_int(rect: Tuple[int, int, int, int],
                                original_resolution: str) -> Tuple[int, int, int, int]:
    """
    Integer version of transform_to_content_area with outward rounding.
    """
    x, y, w, h = rect
    fx, fy, fw, fh = transform_to_content_area((float(x), float(y), float(w), float(h)), original_resolution)
    ix, iy, iw, ih = outward_round_rect(fx, fy, fw, fh)
    return (
        int(ix),
        int(iy),
        int(iw),
        int(ih)
    )

def clamp_overlay_position(x: float, y: float, width: float, height: float, padding_top_ui: int = 150, padding_bottom_ui: int = 150) -> Tuple[float, float]:
    """
    Clamp overlay position to screen bounds in 1280x1920 backend canvas space.
    Args:
        x, y: Proposed overlay position in 1280x1920 space (top-left corner)
        width, height: Scaled overlay dimensions in 1280x1920 space
        padding_top_ui: Padding in UI space (e.g., 150)
    """
    min_y = float(padding_top_ui) * BACKEND_SCALE
    max_y = max(min_y, float(TARGET_H) - height - (float(padding_bottom_ui) * BACKEND_SCALE))
    clamped_x = max(0.0, min(float(x), float(TARGET_W) - width))
    clamped_y = max(min_y, min(float(y), max_y))
    return clamped_x, clamped_y

def validate_crop_rect(rect: Tuple[int, int, int, int],
                        original_resolution: str) -> Tuple[bool, str]:
    x, y, w, h = rect
    if w <= 0 or h <= 0:
        return False, f"Invalid rectangle dimensions: {w}x{h}"
    if not original_resolution:
        return True, ""
    try:
        in_w, in_h = map(int, original_resolution.split('x'))
        if x < 0 or y < 0 or x + w > in_w or y + h > in_h:
            return False, f"Rectangle {rect} exceeds source dimensions {in_w}x{in_h}"
    except (ValueError, AttributeError):
        pass
    return True, ""

def scale_rect(rect: Tuple[float, float, float, float], scale_factor: float) -> Tuple[float, float, float, float]:
    x, y, w, h = rect
    return (x, y, w * scale_factor, h * scale_factor)

def scale_rect_int(rect: Tuple[int, int, int, int], scale_factor: float) -> Tuple[int, int, int, int]:
    x, y, w, h = rect
    scaled_w = int(math.ceil(w * scale_factor))
    scaled_h = int(math.ceil(h * scale_factor))
    return (x, y, scaled_w, scaled_h)

def inverse_transform_from_content_area(rect: Tuple[float, float, float, float],
                                        original_resolution: str) -> Tuple[float, float, float, float]:
    """
    Inverse transformation from 1080p portrait content area back to original video coordinates.
    """
    if not original_resolution:
        return rect
    try:
        in_w, in_h = map(int, original_resolution.split('x'))
        if in_w <= 0 or in_h <= 0:
            return rect
    except (ValueError, AttributeError):
        return rect
    x_content, y_content, w_content, h_content = rect
    x_canvas = x_content * BACKEND_SCALE
    y_canvas = y_content * BACKEND_SCALE
    w_canvas = w_content * BACKEND_SCALE
    h_canvas = h_content * BACKEND_SCALE
    scale_w = 1280.0 / in_w
    scale_h = 1920.0 / in_h
    scale = max(scale_w, scale_h)
    scaled_w = in_w * scale
    scaled_h = in_h * scale
    crop_x = (scaled_w - 1280.0) / 2.0
    crop_y = (scaled_h - 1920.0) / 2.0
    x_scaled = x_canvas + crop_x
    y_scaled = y_canvas + crop_y
    x_original = x_scaled / scale
    y_original = y_scaled / scale
    w_original = w_canvas / scale
    h_original = h_canvas / scale
    w_original = max(1.0, min(w_original, in_w))
    h_original = max(1.0, min(h_original, in_h))
    x_original = max(0.0, min(x_original, in_w - w_original))
    y_original = max(0.0, min(y_original, in_h - h_original))
    return (x_original, y_original, w_original, h_original)

def inverse_transform_from_content_area_int(rect: Tuple[int, int, int, int],
                                            original_resolution: str) -> Tuple[int, int, int, int]:
    x, y, w, h = rect
    fx, fy, fw, fh = inverse_transform_from_content_area(
        (float(x), float(y), float(w), float(h)), original_resolution
    )
    return outward_round_rect(fx, fy, fw, fh)

