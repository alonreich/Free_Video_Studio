from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_13_coordinate_space_content_vs_overlay() -> None:
    """Coordinate math must keep crop in content area logic and overlay in full portrait space."""
    src = read_source("developer_tools/coordinate_math.py")
    assert_all_present(
        src,
        [
            "UI_CONTENT_H = PORTRAIT_H - UI_PADDING_TOP - UI_PADDING_BOTTOM",
            "ui_y = (internal_y * ui_scale) - UI_PADDING_TOP",
            "full_ui_y = ui_y + UI_PADDING_TOP",
            "clamp_overlay_position",
        ],
    )
