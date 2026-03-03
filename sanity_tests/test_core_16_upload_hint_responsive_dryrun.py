from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_16_upload_hint_responsive_baseline_contracts_dryrun() -> None:
    """
    End-user UX contract:
    Keep upload hint/arrow relation perfect at 1574x912,
    and scale proportionally when the window resizes.
    """
    src = read_source("ui/main_window.py")
    assert_all_present(
        src,
        [
            "self.REF_WINDOW_W = 1574.0",
            "self.REF_WINDOW_H = 912.0",
            "self.REF_BOX_W, self.REF_BOX_H = 705, 121",
            "self.REF_ARROW_L, self.REF_ARROW_S = 425, 42",
            "self.REF_OFFSET_X = 182",
            "self.REF_GAP = 18",
            "self.REF_RIGHT_SAFETY = 72",
            "sx = curr_w / ref_w",
            "sy = curr_h / ref_h",
            "scale = max(0.45, min(1.85, (sx + sy) / 2.0))",
            "offset_x = int(round(float(getattr(self, 'REF_OFFSET_X', 182)) * sx))",
            "right_safety = max(24, int(round(float(getattr(self, 'REF_RIGHT_SAFETY', 72)) * scale)))",
            "self.hint_centering_layout.setContentsMargins(offset_x, target_y, 0, 0)",
        ],
    )

def test_core_16_upload_hint_reflows_on_live_resize_contracts_dryrun() -> None:
    """
    Regression contract:
    Upload hint must recompute during live resize (same app session),
    not only after close/reopen.
    """
    src = read_source("ui/parts/main_window_events.py")
    assert_all_present(
        src,
        [
            "QTimer.singleShot(0, self._update_upload_hint_responsive)",
            "if hasattr(self, \"_update_upload_hint_responsive\"):",
            "self._update_upload_hint_responsive()",
        ],
    )
    ui_builder_src = read_source("ui/parts/ui_builder_mixin.py")
    assert_all_present(
        ui_builder_src,
        [
            "if obj in (",
            "getattr(self, \"right_panel\", None)",
            "getattr(self, \"upload_button\", None)",
            "self._update_upload_hint_responsive()",
        ],
    )

def test_core_16_upload_hint_small_size_anti_overlap_contracts_dryrun() -> None:
    """
    Regression contract:
    At small window sizes, arrow must not penetrate the text box border.
    """
    src = read_source("ui/parts/main_window_ui_helpers_b.py")
    assert_all_present(
        src,
        [
            "overlay_w = int(overlay.width()) if overlay else 0",
            "max_box_w_for_overlay = max(180, overlay_w - right_safety - max(16, int(round(34 * scale))))",
            "max_offset_x = max(0, overlay_w - box_w - gap - min_arrow_block - right_safety)",
            "available_space = max(0, overlay_w - offset_x - box_w - gap - right_safety)",
            "if arrow_l < min_arrow_l:",
            "self.upload_hint_arrow.setFixedSize(0, 0)",
            "self.upload_hint_arrow.hide()",
        ],
    )
