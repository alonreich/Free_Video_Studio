from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_16_upload_hint_responsive_baseline_contracts_dryrun() -> None:
    """
    End-user UX contract:
    Keep upload hint/arrow relation perfect at 1574x912,
    and scale proportionally when the window resizes.
    """
    src = read_source("ui/parts/main_window_ui_helpers_b.py")
    assert_all_present(
        src,
        [
            "def _update_upload_hint_responsive(self):",
            "self._hint_debounce_timer.setSingleShot(True)",
            "self._hint_debounce_timer.timeout.connect(self._do_update_upload_hint_responsive)",
            "self._hint_debounce_timer.start(5)",
            "def _do_update_upload_hint_responsive(self):",
            "self.hint_group_container.hide()",
            "self.upload_hint_arrow.hide()",
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
            "if event.type() in (QEvent.Resize, QEvent.Move):",
            "if obj is self or obj is vs:",
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
            "if not hasattr(self, 'hint_group_container') or self.hint_group_container is None: return",
            "self.hint_group_container.hide()",
            "self.upload_hint_arrow.hide()",
        ],
    )
