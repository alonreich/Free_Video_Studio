"""
Centralized coordinate transformation math for Fortnite Video Software.
Ensures consistent scaling between crop tool preview and final render.
"""

from typing import Tuple
TARGET_W = 1280
TARGET_H = 1920
CONTENT_W = 1080
CONTENT_H = 1620
PADDING_TOP = 150

def transform_to_content_area(rect: Tuple[float, float, float, float],
                            original_resolution: str) -> Tuple[float, float, float, float]:
    """
    Transform coordinates from original video to 1080p portrait content area (1080x1620).
    Matches the filter chain in filter_builder.py:
    1. Scale to 1280:1920 (force_original_aspect_ratio=increase)
    2. Crop to 1280:1920 (center crop)
    3. Scale to 1080:1620
    4. Pad to 1080:1920 (black bars top/bottom)
    Args:
        rect: (x, y, width, height) in original resolution coordinates
        original_resolution: "WxH" string (e.g., "1920x1080")
    Returns:
        (x, y, width, height) in 1080p portrait content area coordinates (y=0..1620).
        Note: The final overlay positions have y offset +150 for padding.
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
    final_x = rect_scaled_x * 1080.0 / 1280.0
    final_y = rect_scaled_y * 1080.0 / 1280.0
    final_w = rect_scaled_w * 1080.0 / 1280.0
    final_h = rect_scaled_h * 1080.0 / 1280.0
    final_w = max(0.0, final_w)
    final_h = max(0.0, final_h)
    return (final_x, final_y, final_w, final_h)

def transform_to_content_area_int(rect: Tuple[int, int, int, int],
                                original_resolution: str) -> Tuple[int, int, int, int]:
    """
    Integer version of transform_to_content_area with epsilon rounding.
    Uses epsilon=0.001 to avoid floating-point rounding errors.
    """
    x, y, w, h = rect
    fx, fy, fw, fh = transform_to_content_area((float(x), float(y), float(w), float(h)), original_resolution)
    EPSILON = 0.001
    return (
        int(round(fx + EPSILON)),
        int(round(fy + EPSILON)),
        int(round(fw + EPSILON)),
        int(round(fh + EPSILON))
    )

def clamp_overlay_position(x: int, y: int, width: int, height: int, padding_top: int = 0, padding_bottom: int = 0) -> Tuple[int, int]:
    """
    Clamp overlay position to screen bounds in 1280x1920 space.
    Args:
        x, y: Proposed overlay position in 1280x1920 space (top-left corner)
        width, height: Scaled overlay dimensions in 1280x1920 space
    Returns:
        (clamped_x, clamped_y) ensuring overlay stays within visible area:
        x in [0, 1280 - width] (full width of cropped frame)
        y in [0, 1920 - height] (full height, no text padding in this space)
        Note: The 150 pixel text padding is handled upstream in portrait_window.py
            before coordinates are passed to this function.
    """
    min_y = max(0, padding_top)
    max_y = max(min_y, TARGET_H - height - padding_bottom)
    clamped_x = max(0, min(x, TARGET_W - width))
    clamped_y = max(min_y, min(y, max_y))
    return clamped_x, clamped_y

def validate_crop_rect(rect: Tuple[int, int, int, int],
                        original_resolution: str) -> Tuple[bool, str]:
    """
    Validate crop rectangle for sanity.
    Returns (is_valid, error_message)
    """
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
    """
    Scale a rectangle by a scale factor.
    """
    x, y, w, h = rect
    return (x, y, w * scale_factor, h * scale_factor)

def scale_rect_int(rect: Tuple[int, int, int, int], scale_factor: float) -> Tuple[int, int, int, int]:
    """
    Integer version of scale_rect with epsilon rounding.
    """
    x, y, w, h = rect
    EPSILON = 0.001
    scaled_w = int(round(w * scale_factor + EPSILON))
    scaled_h = int(round(h * scale_factor + EPSILON))
    return (x, y, scaled_w, scaled_h)

def inverse_transform_from_content_area(rect: Tuple[float, float, float, float],
                                        original_resolution: str) -> Tuple[float, float, float, float]:
    """
    Inverse transformation from 1080p portrait content area back to original video coordinates.
    This reverses the steps in transform_to_content_area.
    Args:
        rect: (x, y, width, height) in 1080p portrait content area coordinates (y=0..1620)
        original_resolution: "WxH" string (e.g., "1920x1080")
    Returns:
        (x, y, width, height) in original resolution coordinates
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
    x_scaled = x_content * 1280 / 1080
    y_scaled = y_content * 1280 / 1080
    w_scaled = w_content * 1280 / 1080
    h_scaled = h_content * 1280 / 1080
    scale_w = TARGET_W / in_w
    scale_h = TARGET_H / in_h
    scale = max(scale_w, scale_h)
    scaled_w = in_w * scale
    scaled_h = in_h * scale
    crop_x = max(0, (scaled_w - TARGET_W) / 2)
    crop_y = max(0, (scaled_h - TARGET_H) / 2)
    x_uncropped = x_scaled + crop_x
    y_uncropped = y_scaled + crop_y
    x_original = x_uncropped / scale
    y_original = y_uncropped / scale
    w_original = w_scaled / scale
    h_original = h_scaled / scale
    w_original = max(1.0, min(w_original, in_w))
    h_original = max(1.0, min(h_original, in_h))
    x_original = max(0.0, min(x_original, in_w - w_original))
    y_original = max(0.0, min(y_original, in_h - h_original))
    return (x_original, y_original, w_original, h_original)

def inverse_transform_from_content_area_int(rect: Tuple[int, int, int, int],
                                            original_resolution: str) -> Tuple[int, int, int, int]:
    """
    Integer version of inverse_transform_from_content_area with epsilon rounding.
    """
    x, y, w, h = rect
    fx, fy, fw, fh = inverse_transform_from_content_area(
        (float(x), float(y), float(w), float(h)), original_resolution
    )
    EPSILON = 0.001
    return (
        int(round(fx + EPSILON)),
        int(round(fy + EPSILON)),
        int(round(fw + EPSILON)),
        int(round(fh + EPSILON))
    )
