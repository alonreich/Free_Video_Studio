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
    scale_w = TARGET_W / in_w
    scale_h = TARGET_H / in_h
    scale = max(scale_w, scale_h)
    scaled_w = in_w * scale
    scaled_h = in_h * scale
    crop_x = max(0, (scaled_w - TARGET_W) / 2)
    crop_y = max(0, (scaled_h - TARGET_H) / 2)
    rect_scaled_x = x * scale
    rect_scaled_y = y * scale
    rect_scaled_w = w * scale
    rect_scaled_h = h * scale
    rect_scaled_x -= crop_x
    rect_scaled_y -= crop_y
    rect_scaled_w = max(1.0, min(rect_scaled_w, TARGET_W))
    rect_scaled_h = max(1.0, min(rect_scaled_h, TARGET_H))
    rect_scaled_x = max(0.0, min(rect_scaled_x, TARGET_W - rect_scaled_w))
    rect_scaled_y = max(0.0, min(rect_scaled_y, TARGET_H - rect_scaled_h))
    scale_factor = CONTENT_W / TARGET_W
    final_x = rect_scaled_x * scale_factor
    final_y = rect_scaled_y * scale_factor
    final_w = rect_scaled_w * scale_factor
    final_h = rect_scaled_h * scale_factor
    final_w = max(1.0, min(final_w, CONTENT_W))
    final_h = max(1.0, min(final_h, CONTENT_H))
    final_x = max(0.0, min(final_x, CONTENT_W - final_w))
    final_y = max(0.0, min(final_y, CONTENT_H - final_h))
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

def clamp_overlay_position(x: int, y: int, width: int, height: int) -> Tuple[int, int]:
    """
    Clamp overlay position to screen bounds.
    Args:
        x, y: Proposed overlay position (top-left corner)
        width, height: Scaled overlay dimensions
    Returns:
        (clamped_x, clamped_y) ensuring overlay stays within visible area:
        x in [0, 1080 - width]
        y in [150, 1770 - height]
    """
    clamped_x = max(0, min(x, CONTENT_W - width))
    clamped_y = max(PADDING_TOP, min(y, CONTENT_H + PADDING_TOP - height))
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