"""
Developer tools package.

This package intentionally avoids side effects at import time.
No temp cleanup, no cwd mutation, and no wildcard runtime imports.
"""

from .config import HUD_ELEMENT_MAPPINGS, UI_COLORS, UI_LAYOUT, UI_BEHAVIOR
from .coordinate_math import (
    transform_to_content_area,
    transform_to_content_area_int,
    inverse_transform_from_content_area,
    inverse_transform_from_content_area_int,
    clamp_overlay_position,
    outward_round_rect,
    scale_round,
)

__all__ = [
    "HUD_ELEMENT_MAPPINGS",
    "UI_COLORS",
    "UI_LAYOUT",
    "UI_BEHAVIOR",
    "transform_to_content_area",
    "transform_to_content_area_int",
    "inverse_transform_from_content_area",
    "inverse_transform_from_content_area_int",
    "clamp_overlay_position",
    "outward_round_rect",
    "scale_round",
]
